using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Emby.AiSubtitles.Configuration;
using Emby.AiSubtitles.Models;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;

namespace Emby.AiSubtitles.Services
{
    /// <summary>通过 OpenAI 兼容的 /chat/completions 接口（DeepSeek、Ollama、OpenAI 等）批量翻译字幕。</summary>
    public class Translator
    {
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };

        private readonly IJsonSerializer _json;
        private readonly ILogger _logger;

        public Translator(IJsonSerializer json, ILogger logger)
        {
            _json = json;
            _logger = logger;
        }

        public async Task TranslateAsync(IList<SubtitleCue> cues, string targetLanguageName, string mediaTitle,
            PluginConfiguration config, IProgress<double> progress, CancellationToken ct)
        {
            var batchSize = Math.Max(1, Math.Min(100, config.TranslationBatchSize));
            for (var offset = 0; offset < cues.Count; offset += batchSize)
            {
                ct.ThrowIfCancellationRequested();
                var batch = cues.Skip(offset).Take(batchSize).ToList();
                var context = cues.Skip(Math.Max(0, offset - 3)).Take(Math.Min(3, offset)).Select(c => c.Text).ToList();
                await TranslateBatchAsync(batch, context, targetLanguageName, mediaTitle, config, ct).ConfigureAwait(false);
                progress?.Report(Math.Min(1.0, (offset + batch.Count) / (double)cues.Count));
            }
        }

        /// <summary>翻译一批；如果模型返回的行数对不上，就对半拆分重试，直到单行。</summary>
        private async Task TranslateBatchAsync(List<SubtitleCue> batch, List<string> context, string target,
            string title, PluginConfiguration config, CancellationToken ct)
        {
            string[] translated = null;
            for (var attempt = 0; attempt < 2 && translated == null; attempt++)
            {
                try
                {
                    translated = await RequestAsync(batch.Select(c => c.Text.Trim()).ToList(), context, target, title, config, ct).ConfigureAwait(false);
                    if (translated != null && translated.Length != batch.Count)
                    {
                        _logger.Debug("AI 字幕: 翻译返回 {0} 行，期望 {1} 行", translated.Length, batch.Count);
                        translated = null;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.Warn("AI 字幕: 翻译请求失败（第 {0} 次）: {1}", attempt + 1, ex.Message);
                    if (attempt == 1)
                    {
                        // 接口本身出错（网络、Key、额度等）直接让任务失败，避免逐行重试浪费时间
                        throw;
                    }

                    await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
                }
            }

            if (translated != null)
            {
                var emptyItems = new List<SubtitleCue>();
                for (var i = 0; i < batch.Count; i++)
                {
                    batch[i].Translation = translated[i]?.Trim();
                    if (string.IsNullOrWhiteSpace(batch[i].Translation) && batch.Count > 1)
                    {
                        emptyItems.Add(batch[i]);
                    }
                }

                // 若批量翻译中有个别行被大模型漏翻为空，单独补翻一次
                if (emptyItems.Count > 0 && emptyItems.Count < batch.Count)
                {
                    foreach (var item in emptyItems)
                    {
                        try
                        {
                            var single = await RequestAsync(new List<string> { item.Text.Trim() }, new List<string>(), target, title, config, ct).ConfigureAwait(false);
                            if (single != null && single.Length == 1 && !string.IsNullOrWhiteSpace(single[0]))
                            {
                                item.Translation = single[0].Trim();
                            }
                        }
                        catch
                        {
                            // 忽略单条补翻异常，保留原文兜底
                        }
                    }
                }

                return;
            }

            if (batch.Count == 1)
            {
                // 单行也失败：保留原文
                batch[0].Translation = null;
                return;
            }

            var half = batch.Count / 2;
            await TranslateBatchAsync(batch.Take(half).ToList(), context, target, title, config, ct).ConfigureAwait(false);
            await TranslateBatchAsync(batch.Skip(half).ToList(), batch.Take(half).Select(c => c.Text).ToList(), target, title, config, ct).ConfigureAwait(false);
        }

        private async Task<string[]> RequestAsync(List<string> lines, List<string> context, string target,
            string title, PluginConfiguration config, CancellationToken ct)
        {
            var system = new StringBuilder();
            system.Append("你是专业的影视字幕本地化翻译专家。以下是虚构影视剧的对白台词，请把用户给出的字幕逐条忠实、自然地翻译成").Append(target).Append("。\n");
            system.Append("要求：\n");
            system.Append("1. 输入是一个 JSON 字符串数组，每个元素是一条待翻译的台词；\n");
            system.Append("2. 只输出一个 JSON 字符串数组，元素个数必须与输入完全相同、顺序严格一一对应，绝不能合并、拆分或遗漏任何条目；\n");
            system.Append("3. 每一条都必须给出对应的中文译文，严禁输出空字符串 \"\"，也不要照搬原文不译（包括简短口语、感叹或亲密对白也必须自然译出）；\n");
            system.Append("4. 译文口语化、简洁自然，符合影视字幕习惯；不要输出任何解释、注释或 Markdown 代码块。");

            var user = new StringBuilder();
            if (context.Count > 0)
            {
                user.Append("（上文参考，无需翻译：").Append(_json.SerializeToString(context)).Append("）\n");
            }

            user.Append(_json.SerializeToString(lines));

            var payload = new ChatRequest
            {
                model = config.LlmModel,
                temperature = 0.3,
                messages = new List<ChatMessage>
                {
                    new ChatMessage { role = "system", content = system.ToString() },
                    new ChatMessage { role = "user", content = user.ToString() }
                }
            };

            var rawUrl = (config.LlmApiUrl ?? string.Empty).Trim();
            if (!rawUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !rawUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                rawUrl = "http://" + rawUrl;
            }

            var url = rawUrl.TrimEnd('/') + "/chat/completions";
            using (var request = new HttpRequestMessage(HttpMethod.Post, url))
            {
                request.Headers.ExpectContinue = false;
                request.Content = new StringContent(_json.SerializeToString(payload), Encoding.UTF8, "application/json");
                if (!string.IsNullOrWhiteSpace(config.LlmApiKey))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.LlmApiKey.Trim());
                }

                using (var response = await Http.SendAsync(request, ct).ConfigureAwait(false))
                {
                    var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new HttpRequestException(string.Format("翻译接口返回 {0}: {1}", (int)response.StatusCode, WhisperClient.Truncate(body)));
                    }

                    var chat = _json.DeserializeFromString<ChatResponse>(body);
                    var text = chat?.choices?.FirstOrDefault()?.message?.content;
                    return ParseArray(text);
                }
            }
        }

        private string[] ParseArray(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            // 兼容 <think> 推理内容和 ```json 代码块
            var thinkEnd = text.LastIndexOf("</think>", StringComparison.OrdinalIgnoreCase);
            if (thinkEnd >= 0)
            {
                text = text.Substring(thinkEnd + "</think>".Length);
            }

            var start = text.IndexOf('[');
            var end = text.LastIndexOf(']');
            if (start < 0 || end <= start)
            {
                return null;
            }

            try
            {
                return _json.DeserializeFromString<string[]>(text.Substring(start, end - start + 1));
            }
            catch (Exception ex)
            {
                _logger.Debug("AI 字幕: 无法解析翻译结果: {0}", ex.Message);
                return null;
            }
        }
    }
}
