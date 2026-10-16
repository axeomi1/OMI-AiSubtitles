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

        public async Task<TranscriptionResponse> TranscribeAsync(string audioPath, PluginConfiguration config, CancellationToken ct)
        {
            var url = config.WhisperApiUrl.TrimEnd('/') + "/audio/transcriptions";
            var audioBytes = File.ReadAllBytes(audioPath);
            var sizeMb = audioBytes.Length / (1024.0 * 1024.0);
            var fileName = Path.GetFileName(audioPath);
            var mime = audioPath.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase) ? "audio/ogg" : "audio/mpeg";
            var modelName = string.IsNullOrWhiteSpace(config.WhisperModel) ? "whisper-1" : config.WhisperModel.Trim();

            Exception lastError = null;
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
                    using (var content = new MultipartFormDataContent())
                    using (var request = new HttpRequestMessage(HttpMethod.Post, url))
                    {
                        timeout.CancelAfter(TimeSpan.FromMinutes(Math.Max(5, config.RequestTimeoutMinutes)));

                        // 禁用 Expect: 100-continue 并避免复用已半关闭的空闲连接，防止上传大音频时出现
                        // "Error while copying content to a stream"
                        request.Headers.ExpectContinue = false;
                        request.Headers.ConnectionClose = true;

                        var fileContent = new ByteArrayContent(audioBytes);
                        fileContent.Headers.ContentType = new MediaTypeHeaderValue(mime);
                        content.Add(fileContent, "file", fileName);
                        content.Add(new StringContent(modelName), "model");
                        content.Add(new StringContent("verbose_json"), "response_format");
                        content.Add(new StringContent("segment"), "timestamp_granularities[]");
                        if (!string.IsNullOrWhiteSpace(config.SourceLanguage))
                        {
                            content.Add(new StringContent(config.SourceLanguage.Trim()), "language");
                        }

                        request.Content = content;
                        if (!string.IsNullOrWhiteSpace(config.WhisperApiKey))
                        {
                            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.WhisperApiKey.Trim());
                        }

                        using (var response = await Http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token).ConfigureAwait(false))
                        {
                            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                            if (!response.IsSuccessStatusCode)
                            {
                                throw new HttpRequestException(string.Format(
                                    "语音识别接口返回 HTTP {0} (音频 {1:F1}MB): {2}",
                                    (int)response.StatusCode, sizeMb, Truncate(body)));
                            }

                            var result = _json.DeserializeFromString<TranscriptionResponse>(body);
                            if (result == null || result.segments == null)
                            {
                                throw new InvalidOperationException("语音识别接口没有返回 segments，请确认它支持 response_format=verbose_json。");
                            }

                            result.segments = result.segments
                                .Where(s => !string.IsNullOrWhiteSpace(s.text))
                                .ToList();
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
                    if (attempt < 3)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(3 * attempt), ct).ConfigureAwait(false);
                    }
                }
            }

            throw new InvalidOperationException(
                string.Format("上传音频至语音识别接口失败 (音频 {0:F1}MB, URL: {1}): {2}",
                    sizeMb, url, FlattenException(lastError)),
                lastError);
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
