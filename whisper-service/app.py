"""
Emby AI 字幕 - 语音识别服务

基于 faster-whisper，提供 OpenAI 兼容的 POST /v1/audio/transcriptions 接口。
针对纯 CPU 环境（群晖 / NAS）做了默认优化：int8 量化 + VAD 过滤静音 + 单任务串行。
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

MODEL_NAME = os.getenv("WHISPER_MODEL", "small")
DEVICE = os.getenv("WHISPER_DEVICE", "cpu")
COMPUTE_TYPE = os.getenv("WHISPER_COMPUTE_TYPE", "int8")
CPU_THREADS = int(os.getenv("WHISPER_CPU_THREADS", "0"))  # 0 = 自动
BEAM_SIZE = int(os.getenv("WHISPER_BEAM_SIZE", "5"))
MAX_CONCURRENCY = int(os.getenv("MAX_CONCURRENCY", "1"))
API_KEY = os.getenv("API_KEY", "")
MODEL_DIR = os.getenv("MODEL_DIR", "/models")

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

            log.info("加载模型 %s (device=%s, compute_type=%s)...", MODEL_NAME, DEVICE, COMPUTE_TYPE)
            _model = WhisperModel(
                MODEL_NAME,
                device=DEVICE,
                compute_type=COMPUTE_TYPE,
                cpu_threads=CPU_THREADS,
                download_root=MODEL_DIR,
            )
            log.info("模型加载完成")
        return _model


def check_auth(authorization: Optional[str] = Header(default=None)):
    if API_KEY and authorization != f"Bearer {API_KEY}":
        raise HTTPException(status_code=401, detail="invalid api key")


def transcribe_file(path: str, language: Optional[str], prompt: Optional[str]):
    model = get_model()
    started = time.time()
    segments, info = model.transcribe(
        path,
        language=language or None,
        initial_prompt=prompt or None,
        beam_size=BEAM_SIZE,
        vad_filter=True,
        vad_parameters={"min_silence_duration_ms": 500},
        condition_on_previous_text=False,  # 降低长视频“复读”幻觉
    )
    result = []
    for i, seg in enumerate(segments):
        text = seg.text.strip()
        if not text:
            continue
        result.append({"id": i, "start": round(seg.start, 3), "end": round(seg.end, 3), "text": text})
        if i % 50 == 0:
            log.info("进度 %.0f / %.0f 秒", seg.end, info.duration)
    log.info(
        "识别完成：%d 段，语言 %s (%.2f)，音频 %.0f 秒，耗时 %.0f 秒",
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
    return {"status": "ok", "model": MODEL_NAME, "device": DEVICE, "loaded": _model is not None}


@app.post("/v1/audio/transcriptions", dependencies=[Depends(check_auth)])
async def transcriptions(
    file: UploadFile = File(...),
    model: str = Form(default="whisper-1"),  # 兼容字段，实际以 WHISPER_MODEL 为准
    language: Optional[str] = Form(default=None),
    prompt: Optional[str] = Form(default=None),
    response_format: str = Form(default="json"),
):
    suffix = os.path.splitext(file.filename or "")[1] or ".audio"
    with tempfile.NamedTemporaryFile(delete=False, suffix=suffix) as tmp:
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
