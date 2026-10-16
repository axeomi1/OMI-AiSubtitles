using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Emby.AiSubtitles.Configuration;
using Emby.AiSubtitles.Models;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;

namespace Emby.AiSubtitles.Services
{
    /// <summary>调用 OpenAI 兼容的 /audio/transcriptions 接口（自建 faster-whisper 服务或云端）。</summary>
    public class WhisperClient
    {
        private static readonly HttpClient Http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        private static readonly Regex RetryAfterRegex = new Regex(
            @"try again in\s*(?:(\d+)m)?\s*(\d+(?:\.\d+)?)s",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly IJsonSerializer _json;
        private readonly ILogger _logger;

        public WhisperClient(IJsonSerializer json, ILogger logger = null)
        {
            _json = json;
            _logger = logger;
        }

        /// <summary>
        /// 按顺序对所有音频分片调用语音识别，并将每片的时间轴加上精确偏移后合并。
        /// 支持：
        /// 1. Groq 双模型自动故障转移（whisper-large-v3-turbo 触发 429 时自动切到 whisper-large-v3，免费额度翻倍）
        /// 2. 自动解析 "Please try again in XmYs" 冷却时间，原位等待恢复后继续跑剩余分片，绝不丢弃已识别分片
        /// </summary>
        public async Task<TranscriptionResponse> TranscribeChunksAsync(
            IList<AudioChunk> chunks,
            PluginConfiguration config,
            IProgress<double> progress,
            Action<string> statusCallback,
            CancellationToken ct)
        {
            var mergedSegments = new List<TranscriptionSegment>();
            string detectedLanguage = null;

            for (var i = 0; i < chunks.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var chunk = chunks[i];
                var offset = chunk.StartOffsetSeconds;
                statusCallback?.Invoke(string.Format("正在语音识别 {0}/{1} 片 ({2}%)",
                    i + 1, chunks.Count, (int)Math.Round(i * 100.0 / chunks.Count)));

                var resp = await TranscribeSingleAsync(
                    chunk.Path, i + 1, chunks.Count, detectedLanguage, config, statusCallback, ct).ConfigureAwait(false);

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
            Action<string> statusCallback,
            CancellationToken ct)
        {
            var url = config.WhisperApiUrl.TrimEnd('/') + "/audio/transcriptions";
            var audioBytes = File.ReadAllBytes(audioPath);
            var sizeMb = audioBytes.Length / (1024.0 * 1024.0);
            var fileName = Path.GetFileName(audioPath);
            var mime = audioPath.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase) ? "audio/ogg" : "audio/mpeg";
            var primaryModel = string.IsNullOrWhiteSpace(config.WhisperModel) ? "whisper-1" : config.WhisperModel.Trim();
            var currentModel = primaryModel;
            var lang = !string.IsNullOrWhiteSpace(config.SourceLanguage)
                ? config.SourceLanguage.Trim()
                : NormalizeIsoLanguage(lockedLanguage);

            Exception lastError = null;
            for (var attempt = 1; attempt <= 6; attempt++)
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
                        content.Add(new StringContent(currentModel), "model");
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
                            if ((int)response.StatusCode == 429 && attempt < 6)
                            {
                                // 1. 如果是 Groq 且当前模型触发了每小时额度限制，先自动切换到另一个 Groq Whisper 模型（额度独立！）
                                var altModel = GetAlternateGroqModel(url, currentModel);
                                if (altModel != null && !string.Equals(altModel, currentModel, StringComparison.OrdinalIgnoreCase) && attempt <= 2)
                                {
                                    _logger?.Info("AI 字幕: 模型 {0} 触发 429 限流，自动切换至备用模型 {1} 继续识别分片 {2}/{3}",
                                        currentModel, altModel, chunkIndex, totalChunks);
                                    currentModel = altModel;
                                    statusCallback?.Invoke(string.Format("切换备用模型 {0} 识别 {1}/{2} 片", currentModel, chunkIndex, totalChunks));
                                    await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
                                    continue;
                                }

                                // 2. 解析服务端返回的精确冷却秒数（如 Please try again in 10m35.5s）并倒计时等待
                                var waitSeconds = ParseRetryAfterSeconds(response, body, attempt);
                                _logger?.Warn("AI 字幕: 分片 {0}/{1} 触发 429 速率限制，等待 {2} 秒后自动继续...", chunkIndex, totalChunks, waitSeconds);
                                await CountdownWaitAsync(waitSeconds, chunkIndex, totalChunks, statusCallback, ct).ConfigureAwait(false);
                                continue;
                            }

                            if ((int)response.StatusCode == 401 || (int)response.StatusCode == 403)
                            {
                                throw new InvalidOperationException(string.Format(
                                    "Whisper API Key 无效或未填写 (HTTP {0}, URL: {1})。若使用 Groq 请填写 gsk_ 开头的 Key；若使用本地容器请将接口地址改为 http://NAS_IP:8000/v1。服务端返回: {2}",
                                    (int)response.StatusCode, url, Truncate(body)));
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
                catch (InvalidOperationException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    if (attempt < 6)
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

        private static string GetAlternateGroqModel(string url, string currentModel)
        {
            if (url.IndexOf("groq.com", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return null;
            }

            if (string.Equals(currentModel, "whisper-large-v3-turbo", StringComparison.OrdinalIgnoreCase))
            {
                return "whisper-large-v3";
            }

            if (string.Equals(currentModel, "whisper-large-v3", StringComparison.OrdinalIgnoreCase))
            {
                return "whisper-large-v3-turbo";
            }

            return null;
        }

        private static int ParseRetryAfterSeconds(HttpResponseMessage response, string body, int attempt)
        {
            if (response.Headers.RetryAfter?.Delta.HasValue == true)
            {
                var delta = (int)Math.Ceiling(response.Headers.RetryAfter.Delta.Value.TotalSeconds);
                if (delta > 0 && delta <= 1800)
                {
                    return delta + 2;
                }
            }

            if (!string.IsNullOrEmpty(body))
            {
                var m = RetryAfterRegex.Match(body);
                if (m.Success)
                {
                    var mins = 0;
                    if (m.Groups[1].Success)
                    {
                        int.TryParse(m.Groups[1].Value, out mins);
                    }

                    double.TryParse(m.Groups[2].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var secs);
                    var total = (int)Math.Ceiling(mins * 60 + secs) + 3;
                    return Math.Min(1800, Math.Max(5, total));
                }
            }

            return Math.Min(120, 10 * attempt);
        }

        private static async Task CountdownWaitAsync(int totalSeconds, int chunkIndex, int totalChunks,
            Action<string> statusCallback, CancellationToken ct)
        {
            var remaining = totalSeconds;
            while (remaining > 0)
            {
                ct.ThrowIfCancellationRequested();
                var m = remaining / 60;
                var s = remaining % 60;
                var waitText = m > 0 ? string.Format("{0}分{1}秒", m, s) : string.Format("{0}秒", s);
                statusCallback?.Invoke(string.Format("云端额度冷却中，{0}后继续第 {1}/{2} 片", waitText, chunkIndex, totalChunks));
                var step = Math.Min(5, remaining);
                await Task.Delay(TimeSpan.FromSeconds(step), ct).ConfigureAwait(false);
                remaining -= step;
            }
        }

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
