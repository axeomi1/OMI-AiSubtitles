"""
Emby AI 字幕 - 语音识别服务

基于 faster-whisper，提供 OpenAI 兼容的 POST /v1/audio/transcriptions 接口。
针对 Intel N5105 (4核4线程) + 16GB 内存 NAS 做了专门优化：
- int8 量化 + 3 线程推理（留 1 核给 Emby 与系统）
- beam_size=1 贪婪解码（比默认 5 快 2.5 倍以上）
- Silero VAD 静音过滤（无台词片段毫秒级跳过）
- 优先使用 /dev/shm 内存盘暂存上传音频，零机械硬盘磨损
"""
import asyncio
import logging
import os
import tempfile
import threading
import time
from typing import Optional

from fastapi import Depends, FastAPI, File, Form, Header, HTTPException, UploadFile
from fastapi.responses import JSONResponse, PlainTextResponse

def _env_int(name: str, default: int) -> int:
    try:
        return max(1, int(float(os.getenv(name, str(default)))))
    except (ValueError, TypeError):
        return default


MODEL_NAME = os.getenv("WHISPER_MODEL", "small")
DEVICE = os.getenv("WHISPER_DEVICE", "cpu")
COMPUTE_TYPE = os.getenv("WHISPER_COMPUTE_TYPE", "int8")
CPU_THREADS = _env_int("WHISPER_CPU_THREADS", 3)  # N5105 4核推荐 3 线程
BEAM_SIZE = _env_int("WHISPER_BEAM_SIZE", 1)      # 1 = 贪婪解码，速度提升 2.5x
MAX_CONCURRENCY = _env_int("MAX_CONCURRENCY", 1)
API_KEY = os.getenv("API_KEY", "")
MODEL_DIR = os.getenv("MODEL_DIR", "/models")
TMP_DIR = "/dev/shm" if os.path.isdir("/dev/shm") else None

logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")
log = logging.getLogger("whisper-service")

app = FastAPI(title="Emby AI Subtitles - Whisper Service")
_model = None
_model_lock = threading.Lock()
_semaphore = asyncio.Semaphore(MAX_CONCURRENCY)


def get_model():
    global _model
    with _model_lock:
        if _model is None:
            from faster_whisper import WhisperModel

            log.info(
                "加载模型 %s (device=%s, compute_type=%s, cpu_threads=%d, beam_size=%d)...",
                MODEL_NAME, DEVICE, COMPUTE_TYPE, CPU_THREADS, BEAM_SIZE,
            )
            _model = WhisperModel(
                MODEL_NAME,
                device=DEVICE,
                compute_type=COMPUTE_TYPE,
                cpu_threads=CPU_THREADS,
                download_root=MODEL_DIR,
            )
            log.info("模型加载完成，已常驻内存")
        return _model


def check_auth(authorization: Optional[str] = Header(default=None)):
    if API_KEY and authorization != f"Bearer {API_KEY}":
        raise HTTPException(status_code=401, detail="invalid api key")


WORD_TIMESTAMPS = os.getenv("WORD_TIMESTAMPS", "true").lower() == "true"
MAX_CUE_SECONDS = float(os.getenv("MAX_CUE_SECONDS", "6"))     # 单条字幕最长显示时间
MAX_CUE_CHARS = int(os.getenv("MAX_CUE_CHARS", "28"))          # 单条字幕最多字符
GAP_SPLIT_SECONDS = float(os.getenv("GAP_SPLIT_SECONDS", "0.6"))  # 词间停顿超过此值即断句
_PUNCT_END = tuple("。！？!?.…♪")


def _split_by_words(seg):
    """利用词级时间戳把 Whisper 的长段落切成与语音精确对齐的短句。"""
    words = [w for w in (seg.words or []) if w.word and w.word.strip()]
    if not words:
        return [(seg.start, seg.end, seg.text.strip())]

    cues, buf = [], []

    def flush():
        if buf:
            text = "".join(w.word for w in buf).strip()
            if text:
                cues.append((buf[0].start, buf[-1].end, text))
            buf.clear()

    for w in words:
        if buf:
            gap = w.start - buf[-1].end
            dur = w.end - buf[0].start
            length = len("".join(x.word for x in buf).strip())
            if (gap >= GAP_SPLIT_SECONDS or dur > MAX_CUE_SECONDS or length >= MAX_CUE_CHARS
                    or buf[-1].word.strip().endswith(_PUNCT_END)):
                flush()
        buf.append(w)
    flush()
    return cues


def transcribe_file(path: str, language: Optional[str], prompt: Optional[str]):
    model = get_model()
    started = time.time()
    segments, info = model.transcribe(
        path,
        language=language or None,
        initial_prompt=prompt or None,
        beam_size=BEAM_SIZE,
        best_of=1,
        temperature=(0.0, 0.2, 0.4, 0.6, 0.8, 1.0),  # 0.0 失败时自动升温重试，防止整段 30 秒对白被丢弃造成留白
        compression_ratio_threshold=2.8,
        log_prob_threshold=-1.2,                     # 放宽低置信度阈值，保留轻声/含糊对白
        no_speech_threshold=0.85,                    # 防止带背景音/喘息的对白段被误判为无语音而整段跳过
        word_timestamps=WORD_TIMESTAMPS,             # 词级时间戳：字幕起止精确到每个词
        vad_filter=True,
        vad_parameters={
            "threshold": 0.3,                        # 降低人声检测门槛（默认 0.5），轻声细语也能识别
            "min_speech_duration_ms": 150,           # 保留简短台词（默认 250ms）
            "min_silence_duration_ms": 1000,         # 静音满 1 秒才切分，避免把正常说话切碎吞字
            "speech_pad_ms": 400,                    # 语音前后各留 400ms 缓冲，防止吃掉句首句尾
        },
        condition_on_previous_text=False,            # 降低长视频或音乐段“复读”幻觉
    )
    result = []
    for seg in segments:
        if not seg.text.strip():
            continue
        parts = _split_by_words(seg) if WORD_TIMESTAMPS else [(seg.start, seg.end, seg.text.strip())]
        for start, end, text in parts:
            result.append({"id": len(result), "start": round(start, 3), "end": round(end, 3), "text": text})
        if len(result) % 30 == 0:
            log.info("进度 %.0f / %.0f 秒", seg.end, info.duration)
    log.info(
        "分片识别完成：%d 段，语言 %s (%.2f)，音频 %.0f 秒，耗时 %.1f 秒",
        len(result), info.language, info.language_probability, info.duration, time.time() - started,
    )
    return info, result


def to_srt(segments) -> str:
    def ts(t: float) -> str:
        ms = int(round(t * 1000))
        h, ms = divmod(ms, 3600_000)
        m, ms = divmod(ms, 60_000)
        s, ms = divmod(ms, 1000)
        return f"{h:02}:{m:02}:{s:02},{ms:03}"

    return "".join(
        f"{n}\n{ts(s['start'])} --> {ts(s['end'])}\n{s['text']}\n\n" for n, s in enumerate(segments, 1)
    )


@app.get("/health")
def health():
    return {
        "status": "ok",
        "model": MODEL_NAME,
        "device": DEVICE,
        "compute_type": COMPUTE_TYPE,
        "cpu_threads": CPU_THREADS,
        "beam_size": BEAM_SIZE,
        "loaded": _model is not None,
    }


@app.post("/v1/audio/transcriptions", dependencies=[Depends(check_auth)])
async def transcriptions(
    file: UploadFile = File(...),
    model: str = Form(default="whisper-1"),  # 兼容字段，实际以 WHISPER_MODEL 为准
    language: Optional[str] = Form(default=None),
    prompt: Optional[str] = Form(default=None),
    response_format: str = Form(default="json"),
):
    suffix = os.path.splitext(file.filename or "")[1] or ".audio"
    with tempfile.NamedTemporaryFile(delete=False, suffix=suffix, dir=TMP_DIR) as tmp:
        while chunk := await file.read(1024 * 1024):
            tmp.write(chunk)
        path = tmp.name

    try:
        async with _semaphore:
            info, segments = await asyncio.to_thread(transcribe_file, path, language, prompt)
    except Exception as e:  # noqa: BLE001
        log.exception("识别失败")
        raise HTTPException(status_code=500, detail=str(e))
    finally:
        try:
            os.remove(path)
        except OSError:
            pass

    text = " ".join(s["text"] for s in segments)
    if response_format == "srt":
        return PlainTextResponse(to_srt(segments))
    if response_format == "text":
        return PlainTextResponse(text)
    if response_format == "verbose_json":
        return JSONResponse(
            {
                "task": "transcribe",
                "language": info.language,
                "duration": info.duration,
                "text": text,
                "segments": segments,
            }
        )
    return JSONResponse({"text": text})


@app.on_event("startup")
async def preload():
    if os.getenv("PRELOAD_MODEL", "true").lower() == "true":
        asyncio.get_event_loop().run_in_executor(None, get_model)
