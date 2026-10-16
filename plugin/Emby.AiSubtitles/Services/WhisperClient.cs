using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Emby.AiSubtitles.Configuration;
using Emby.AiSubtitles.Models;
using MediaBrowser.Model.Serialization;

namespace Emby.AiSubtitles.Services
{
    /// <summary>调用 OpenAI 兼容的 /audio/transcriptions 接口（自建 faster-whisper 服务或云端）。</summary>
    public class WhisperClient
    {
        private static readonly HttpClient Http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

        private readonly IJsonSerializer _json;

        public WhisperClient(IJsonSerializer json)
        {
            _json = json;
        }

        /// <summary>
        /// 按顺序对所有音频分片调用语音识别，并将每片的时间轴加上精确偏移后合并。
        /// 每片只有 ~1.2MB，彻底解决 Groq / OpenAI 免费层或反向代理对 15MB+ 大文件直接 Connection reset by peer 的问题。
        /// </summary>
        public async Task<TranscriptionResponse> TranscribeChunksAsync(
            IList<AudioChunk> chunks,
            PluginConfiguration config,
            IProgress<double> progress,
            CancellationToken ct)
        {
            var mergedSegments = new List<TranscriptionSegment>();
            string detectedLanguage = null;

            for (var i = 0; i < chunks.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var chunk = chunks[i];
                var offset = chunk.StartOffsetSeconds;

                var resp = await TranscribeSingleAsync(chunk.Path, i + 1, chunks.Count, detectedLanguage, config, ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(detectedLanguage) && !string.IsNullOrWhiteSpace(resp.language))
                {
                    detectedLanguage = resp.language;
                }

                if (resp.segments != null)
                {
                    foreach (var seg in resp.segments)
                    {
                        if (string.IsNullOrWhiteSpace(seg.text))
                        {
                            continue;
                        }

                        mergedSegments.Add(new TranscriptionSegment
                        {
                            start = seg.start + offset,
                            end = seg.end + offset,
                            text = seg.text.Trim()
                        });
                    }
                }

                progress?.Report((i + 1) / (double)chunks.Count);
            }

            return new TranscriptionResponse
            {
                language = detectedLanguage ?? config.SourceLanguage ?? "unknown",
                segments = mergedSegments
            };
        }

        public async Task<TranscriptionResponse> TranscribeSingleAsync(
            string audioPath,
            int chunkIndex,
            int totalChunks,
            string lockedLanguage,
            PluginConfiguration config,
            CancellationToken ct)
        {
            var url = config.WhisperApiUrl.TrimEnd('/') + "/audio/transcriptions";
            var audioBytes = File.ReadAllBytes(audioPath);
            var sizeMb = audioBytes.Length / (1024.0 * 1024.0);
            var fileName = Path.GetFileName(audioPath);
            var mime = audioPath.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase) ? "audio/ogg" : "audio/mpeg";
            var modelName = string.IsNullOrWhiteSpace(config.WhisperModel) ? "whisper-1" : config.WhisperModel.Trim();
            var lang = !string.IsNullOrWhiteSpace(config.SourceLanguage)
                ? config.SourceLanguage.Trim()
                : NormalizeIsoLanguage(lockedLanguage);

            Exception lastError = null;
            for (var attempt = 1; attempt <= 4; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
                    using (var content = new MultipartFormDataContent())
                    using (var request = new HttpRequestMessage(HttpMethod.Post, url))
                    {
                        timeout.CancelAfter(TimeSpan.FromMinutes(Math.Max(5, config.RequestTimeoutMinutes)));

                        request.Headers.ExpectContinue = false;
                        request.Headers.ConnectionClose = true;

                        var fileContent = new ByteArrayContent(audioBytes);
                        fileContent.Headers.ContentType = new MediaTypeHeaderValue(mime);
                        content.Add(fileContent, "file", fileName);
                        content.Add(new StringContent(modelName), "model");
                        content.Add(new StringContent("verbose_json"), "response_format");
                        content.Add(new StringContent("segment"), "timestamp_granularities[]");
                        if (!string.IsNullOrWhiteSpace(lang))
                        {
                            content.Add(new StringContent(lang), "language");
                        }

                        request.Content = content;
                        if (!string.IsNullOrWhiteSpace(config.WhisperApiKey))
                        {
                            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.WhisperApiKey.Trim());
                        }

                        using (var response = await Http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token).ConfigureAwait(false))
                        {
                            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                            if ((int)response.StatusCode == 429 && attempt < 4)
                            {
                                // Groq 触发速率限制时等待后自动重试
                                await Task.Delay(TimeSpan.FromSeconds(8 * attempt), ct).ConfigureAwait(false);
                                continue;
                            }

                            if (!response.IsSuccessStatusCode)
                            {
                                throw new HttpRequestException(string.Format(
                                    "语音识别接口返回 HTTP {0} (分片 {1}/{2}, {3:F1}MB): {4}",
                                    (int)response.StatusCode, chunkIndex, totalChunks, sizeMb, Truncate(body)));
                            }

                            var result = _json.DeserializeFromString<TranscriptionResponse>(body);
                            if (result == null || result.segments == null)
                            {
                                throw new InvalidOperationException("语音识别接口没有返回 segments，请确认它支持 response_format=verbose_json。");
                            }

                            return result;
                        }
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    if (attempt < 4)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(3 * attempt), ct).ConfigureAwait(false);
                    }
                }
            }

            throw new InvalidOperationException(
                string.Format("上传音频分片 {0}/{1} 至语音识别接口失败 ({2:F1}MB, URL: {3}): {4}",
                    chunkIndex, totalChunks, sizeMb, url, FlattenException(lastError)),
                lastError);
        }

        /// <summary>Whisper verbose_json 返回的 language 可能是全称（如 japanese / english），转成 ISO-639-1 两位代码供后续分片固定语言。</summary>
        private static string NormalizeIsoLanguage(string lang)
        {
            if (string.IsNullOrWhiteSpace(lang))
            {
                return null;
            }

            lang = lang.Trim().ToLowerInvariant();
            if (lang.Length == 2)
            {
                return lang;
            }

            switch (lang)
            {
                case "japanese": return "ja";
                case "english": return "en";
                case "chinese": return "zh";
                case "korean": return "ko";
                case "french": return "fr";
                case "german": return "de";
                case "spanish": return "es";
                case "russian": return "ru";
                default: return null;
            }
        }

        public static string FlattenException(Exception ex)
        {
            if (ex == null)
            {
                return string.Empty;
            }

            var messages = new List<string>();
            var curr = ex;
            while (curr != null)
            {
                var msg = curr.Message?.Trim();
                if (!string.IsNullOrEmpty(msg) && !messages.Contains(msg))
                {
                    messages.Add(msg);
                }

                curr = curr.InnerException;
            }

            return string.Join(" -> ", messages);
        }

        internal static string Truncate(string s, int max = 500)
        {
            if (string.IsNullOrEmpty(s))
            {
                return string.Empty;
            }

            return s.Length <= max ? s : s.Substring(0, max) + "...";
        }
    }
}
