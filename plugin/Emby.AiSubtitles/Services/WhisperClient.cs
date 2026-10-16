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

            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            using (var stream = File.OpenRead(audioPath))
            using (var content = new MultipartFormDataContent())
            using (var request = new HttpRequestMessage(HttpMethod.Post, url))
            {
                timeout.CancelAfter(TimeSpan.FromMinutes(Math.Max(5, config.RequestTimeoutMinutes)));

                var file = new StreamContent(stream);
                file.Headers.ContentType = new MediaTypeHeaderValue(
                    audioPath.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase) ? "audio/ogg" : "audio/mpeg");
                content.Add(file, "file", Path.GetFileName(audioPath));
                content.Add(new StringContent(string.IsNullOrWhiteSpace(config.WhisperModel) ? "whisper-1" : config.WhisperModel.Trim()), "model");
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

                using (var response = await Http.SendAsync(request, timeout.Token).ConfigureAwait(false))
                {
                    var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new HttpRequestException(string.Format("语音识别接口返回 {0}: {1}", (int)response.StatusCode, Truncate(body)));
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
