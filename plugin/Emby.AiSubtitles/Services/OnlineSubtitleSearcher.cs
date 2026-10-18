using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Emby.AiSubtitles.Configuration;
using Emby.AiSubtitles.Models;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Providers;
using MediaBrowser.Model.Serialization;

namespace Emby.AiSubtitles.Services
{
    /// <summary>
    /// 在线现成字幕搜索与秒级下载器（聚合：迅雷官方字幕库 + SubtitleCat 番号字幕库）。
    /// 支持自动从文件名/标题提取番号（如 NIMA-007），下载后自动清洗并转换为用户配置的底部对齐 ASS/SRT 样式。
    /// </summary>
    public class OnlineSubtitleSearcher
    {
        public const string XunleiPrefix = "aisub_xl_";
        public const string SubtitleCatPrefix = "aisub_sc_";

        private static readonly HttpClient Http = CreateClient();

        // 匹配标准日本番号，如 NIMA-007, SSIS-123, IPX-888, FC2-PPV-1234567
        private static readonly Regex FanhaoRegex = new Regex(
            @"\b(FC2[\s\-_]*PPV[\s\-_]*\d{5,8}|[A-Za-z]{2,7}[\s\-_]+\d{2,6})\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // 排除误匹配为番号的常见视频编码词
        private static readonly HashSet<string> NonFanhaoPrefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "WEB", "UTF", "AAC", "AC3", "DTS", "H264", "H265", "X264", "X265",
            "HEVC", "AVC", "HDR", "SDR", "FPS", "bit", "CHS", "CHT", "ENG", "JPN", "MP4", "MKV"
        };

        // 匹配 40 位纯 SHA1 哈希文件名（过滤迅雷无意义乱码名）
        private static readonly Regex HexHashNameRegex = new Regex(
            @"^[0-9A-Fa-f]{32,42}\.(srt|ass|ssa)$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // SubtitleCat 搜索列表行匹配
        private static readonly Regex ScRowRegex = new Regex(
            @"<tr>[\s\S]*?<a\s+href=""(subs/[^""]+\.html)""[^>]*>([^<]+)</a>\s*(\([^<]*\))?([\s\S]*?)</tr>",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private readonly ILogger _logger;
        private readonly IJsonSerializer _json;

        static OnlineSubtitleSearcher()
        {
            // 尝试通过反射注册 CodePagesEncodingProvider（用于解码迅雷部分 GBK/GB18030 编码的字幕）
            try
            {
                var providerType = Type.GetType("System.Text.CodePagesEncodingProvider, System.Text.Encoding.CodePages");
                var instanceProp = providerType?.GetProperty("Instance");
                var instance = instanceProp?.GetValue(null) as EncodingProvider;
                if (instance != null)
                {
                    Encoding.RegisterProvider(instance);
                }
            }
            catch
            {
                // 忽略
            }
        }

        public OnlineSubtitleSearcher(ILogger logger, IJsonSerializer json)
        {
            _logger = logger;
            _json = json;
        }

        private static HttpClient CreateClient()
        {
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            };
            var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(12)
            };
            client.DefaultRequestHeaders.TryAddWithoutValidation(
                "User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
            return client;
        }

        /// <summary>
        /// 从媒体路径和标题中提取最佳搜索关键词（优先提取番号，如 NIMA-007）。
        /// </summary>
        public static string ExtractSearchQuery(string mediaPath, string title)
        {
            var fileName = !string.IsNullOrEmpty(mediaPath) ? Path.GetFileNameWithoutExtension(mediaPath) : string.Empty;
            var dirName = !string.IsNullOrEmpty(mediaPath) ? Path.GetFileName(Path.GetDirectoryName(mediaPath) ?? string.Empty) : string.Empty;

            foreach (var candidate in new[] { fileName, dirName, title })
            {
                if (string.IsNullOrWhiteSpace(candidate))
                {
                    continue;
                }

                foreach (Match m in FanhaoRegex.Matches(candidate))
                {
                    var raw = m.Groups[1].Value.Trim();
                    var parts = Regex.Split(raw, @"[\s\-_]+");
                    if (parts.Length >= 2 && !NonFanhaoPrefixes.Contains(parts[0]) && !Regex.IsMatch(parts[0], @"^S\d+$", RegexOptions.IgnoreCase))
                    {
                        return string.Join("-", parts).ToUpperInvariant();
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(fileName))
            {
                return fileName.Trim();
            }

            return (title ?? string.Empty).Trim();
        }

        /// <summary>
        /// 并发搜索迅雷字幕库和 SubtitleCat，返回立即可下载的字幕列表。
        /// </summary>
        public async Task<List<RemoteSubtitleInfo>> SearchAllAsync(
            string mediaPath,
            string title,
            string language,
            string targetFormat,
            CancellationToken ct)
        {
            var query = ExtractSearchQuery(mediaPath, title);
            if (string.IsNullOrWhiteSpace(query))
            {
                return new List<RemoteSubtitleInfo>();
            }

            _logger.Info("AI 字幕: 开始在线搜索现成字幕，关键词: {0}", query);

            using (var searchCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                searchCts.CancelAfter(TimeSpan.FromSeconds(8));
                var xlTask = SearchXunleiAsync(query, language, targetFormat, searchCts.Token);
                var scTask = SearchSubtitleCatAsync(query, language, targetFormat, searchCts.Token);

                await Task.WhenAll(xlTask, scTask).ConfigureAwait(false);

                var results = new List<RemoteSubtitleInfo>();
                results.AddRange(xlTask.Result);
                results.AddRange(scTask.Result);
                _logger.Info("AI 字幕: 在线搜索「{0}」完成，共找到 {1} 条现成字幕（迅雷 {2} 条，SubtitleCat {3} 条）",
                    query, results.Count, xlTask.Result.Count, scTask.Result.Count);
                return results;
            }
        }

        private async Task<List<RemoteSubtitleInfo>> SearchXunleiAsync(
            string query,
            string language,
            string targetFormat,
            CancellationToken ct)
        {
            var list = new List<RemoteSubtitleInfo>();
            try
            {
                var url = "https://api-shoulei-ssl.xunlei.com/oracle/subtitle?name=" + Uri.EscapeDataString(query);
                using (var resp = await Http.GetAsync(url, ct).ConfigureAwait(false))
                {
                    if (!resp.IsSuccessStatusCode)
                    {
                        return list;
                    }

                    var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    var data = _json.DeserializeFromString<XunleiResponse>(body);
                    if (data?.data == null || data.data.Count == 0)
                    {
                        return list;
                    }

                    var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var items = data.data
                        .Where(x => !string.IsNullOrWhiteSpace(x.url) && !string.IsNullOrWhiteSpace(x.name))
                        .Where(x => seenUrls.Add(x.url))
                        .ToList();

                    // 如果有带正常名字的字幕，就过滤掉纯哈希乱码名和 "01.srt"
                    var namedItems = items
                        .Where(x => !HexHashNameRegex.IsMatch(x.name) && !x.name.Equals("01.srt", StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    if (namedItems.Count > 0)
                    {
                        items = namedItems;
                    }

                    // 优先排序：包含 zh-CN/chs/zh/中文 > 含查询词 > 有片长信息
                    items = items
                        .OrderByDescending(x => ScoreXunleiItem(x, query))
                        .Take(6)
                        .ToList();

                    foreach (var item in items)
                    {
                        var token = EncodeToken(item.url);
                        var durText = item.duration > 0
                            ? " · 片长 " + FormatDurationMs(item.duration)
                            : string.Empty;

                        list.Add(new RemoteSubtitleInfo
                        {
                            Id = XunleiPrefix + language + "_" + token,
                            ProviderName = Plugin.PluginName,
                            Name = string.Format("⚡ [迅雷秒下] {0}{1} ({2})", item.name, durText, targetFormat.ToUpperInvariant()),
                            Comment = "现成字幕 · 1秒下载并自动转为屏幕底部排版",
                            Author = "迅雷字幕库",
                            Language = language,
                            Format = targetFormat,
                            IsHashMatch = false,
                            IsForced = false
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Debug("AI 字幕: 迅雷字幕搜索失败: {0}", ex.Message);
            }

            return list;
        }

        private static int ScoreXunleiItem(XunleiItem item, string query)
        {
            var score = 0;
            var name = item.name ?? string.Empty;
            if (name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                score += 50;
            }
            if (name.IndexOf("zh-cn", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("chs", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("简体", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                score += 30;
            }
            else if (name.IndexOf("zh", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     name.IndexOf("chi", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                score += 20;
            }
            if (item.duration > 0)
            {
                score += 10;
            }
            return score;
        }

        private async Task<List<RemoteSubtitleInfo>> SearchSubtitleCatAsync(
            string query,
            string language,
            string targetFormat,
            CancellationToken ct)
        {
            var list = new List<RemoteSubtitleInfo>();
            try
            {
                var url = "https://www.subtitlecat.com/index.php?search=" + Uri.EscapeDataString(query);
                using (var resp = await Http.GetAsync(url, ct).ConfigureAwait(false))
                {
                    if (!resp.IsSuccessStatusCode)
                    {
                        return list;
                    }

                    var html = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    var matches = ScRowRegex.Matches(html);
                    var candidates = new List<(string href, string name, string origin, bool thumbsUp, bool thumbsDown, int score)>();

                    foreach (Match m in matches)
                    {
                        var href = m.Groups[1].Value.Trim();
                        var title = WebUtility.HtmlDecode(m.Groups[2].Value).Trim();
                        var origin = WebUtility.HtmlDecode(m.Groups[3].Value).Trim();
                        var rest = m.Groups[4].Value;

                        // 优先确保标题与番号相关
                        var compactQuery = query.Replace("-", "").Replace(" ", "");
                        var compactTitle = title.Replace("-", "").Replace(" ", "");
                        bool matchesQuery = title.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                            compactTitle.IndexOf(compactQuery, StringComparison.OrdinalIgnoreCase) >= 0;
                        if (!matchesQuery)
                        {
                            continue;
                        }

                        bool thumbsUp = rest.Contains("👍") || rest.IndexOf("Rated good", StringComparison.OrdinalIgnoreCase) >= 0;
                        bool thumbsDown = rest.Contains("👎") || rest.IndexOf("Rated bad", StringComparison.OrdinalIgnoreCase) >= 0;

                        int score = 0;
                        if (origin.IndexOf("Chinese", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            score += 40;
                        }
                        else if (origin.IndexOf("Japanese", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            score += 25;
                        }
                        if (thumbsUp)
                        {
                            score += 20;
                        }
                        if (thumbsDown)
                        {
                            score -= 30;
                        }

                        candidates.Add((href, title, origin, thumbsUp, thumbsDown, score));
                    }

                    foreach (var c in candidates.OrderByDescending(x => x.score).Take(4))
                    {
                        var token = EncodeToken(c.href);
                        var badge = c.thumbsUp ? " 👍好评" : string.Empty;
                        var originNote = !string.IsNullOrEmpty(c.origin) && c.origin.IndexOf("Chinese", StringComparison.OrdinalIgnoreCase) >= 0
                            ? " · 中文源"
                            : string.Empty;

                        list.Add(new RemoteSubtitleInfo
                        {
                            Id = SubtitleCatPrefix + language + "_" + token,
                            ProviderName = Plugin.PluginName,
                            Name = string.Format("⚡ [SubtitleCat秒下] {0}{1}{2} ({3})", c.name, originNote, badge, targetFormat.ToUpperInvariant()),
                            Comment = "SubtitleCat 番号字幕库 · 1秒下载并自动转为屏幕底部排版",
                            Author = "SubtitleCat",
                            Language = language,
                            Format = targetFormat,
                            IsHashMatch = false,
                            IsForced = false
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Debug("AI 字幕: SubtitleCat 搜索失败: {0}", ex.Message);
            }

            return list;
        }

        /// <summary>
        /// 下载迅雷或 SubtitleCat 字幕，并转换为用户配置的格式（如底部对齐 ASS）。
        /// </summary>
        public async Task<(string language, string format, string content)> DownloadAndFormatAsync(
            string id,
            PluginConfiguration config,
            CancellationToken ct)
        {
            bool isXunlei = id.StartsWith(XunleiPrefix, StringComparison.Ordinal);
            var prefix = isXunlei ? XunleiPrefix : SubtitleCatPrefix;
            var payload = id.Substring(prefix.Length);

            var sep = payload.IndexOf('_');
            var language = sep > 0 ? payload.Substring(0, sep) : "chi";
            var token = sep > 0 ? payload.Substring(sep + 1) : payload;
            var targetUrlOrPath = DecodeToken(token);

            string downloadUrl;
            if (isXunlei)
            {
                downloadUrl = targetUrlOrPath;
            }
            else
            {
                downloadUrl = await ResolveSubtitleCatDownloadUrlAsync(targetUrlOrPath, language, ct).ConfigureAwait(false);
            }

            _logger.Info("AI 字幕: 正在下载现成字幕 {0}", downloadUrl);
            byte[] bytes;
            using (var resp = await Http.GetAsync(downloadUrl, ct).ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();
                bytes = await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            }

            var rawText = DecodeSubtitleBytes(bytes);
            var targetFormat = SrtFormatter.NormalizeFormat(config.SubtitleFormat);

            // 解析下载的 SRT 字幕并转换为用户设定的底部对齐 ASS/SRT 样式
            var cues = SrtFormatter.ParseSrt(rawText, config.FilterFillerSounds);
            if (cues.Count > 0)
            {
                SubtitleFilter.NormalizeTiming(cues);
                var formatted = SrtFormatter.Build(cues, config, config.SubtitleMode == "Bilingual");
                _logger.Info("AI 字幕: 现成字幕下载并转换完成，共 {0} 条字幕 ({1})", cues.Count, targetFormat.ToUpperInvariant());
                return (language, targetFormat, formatted);
            }

            // 若原本就是 ASS 或非常规格式无法解析为 SRT，则原样返回
            var rawExt = downloadUrl.EndsWith(".ass", StringComparison.OrdinalIgnoreCase) ? "ass" : "srt";
            return (language, rawExt, rawText);
        }

        private async Task<string> ResolveSubtitleCatDownloadUrlAsync(string relativePagePath, string language, CancellationToken ct)
        {
            var baseUri = new Uri("https://www.subtitlecat.com/");
            var pageUri = new Uri(baseUri, relativePagePath.TrimStart('/'));

            using (var resp = await Http.GetAsync(pageUri, ct).ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();
                var html = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

                bool wantTraditional = language.Equals("zh-tw", StringComparison.OrdinalIgnoreCase) ||
                                       language.Equals("zh-hk", StringComparison.OrdinalIgnoreCase) ||
                                       language.Equals("cht", StringComparison.OrdinalIgnoreCase);

                var langIds = wantTraditional
                    ? new[] { "download_zh-TW", "download_zh-CN", "download_zh" }
                    : new[] { "download_zh-CN", "download_zh", "download_zh-TW" };

                foreach (var langId in langIds)
                {
                    var m = Regex.Match(
                        html,
                        @"<a\s+id=""" + Regex.Escape(langId) + @"""[^>]*href=""([^""]+\.srt)""",
                        RegexOptions.IgnoreCase);
                    if (m.Success)
                    {
                        var href = WebUtility.HtmlDecode(m.Groups[1].Value).Trim();
                        return new Uri(baseUri, href).AbsoluteUri;
                    }
                }

                // 兜底查找任意带 -zh-CN.srt 或 -orig.srt 的链接
                var fallback = Regex.Match(html, @"href=""(/subs/[^""]+(?:-zh-CN|-zh-TW|-orig)\.srt)""", RegexOptions.IgnoreCase);
                if (fallback.Success)
                {
                    return new Uri(baseUri, WebUtility.HtmlDecode(fallback.Groups[1].Value).Trim()).AbsoluteUri;
                }

                throw new InvalidOperationException("在 SubtitleCat 页面中未找到可用的中文字幕下载链接。");
            }
        }

        private static string DecodeSubtitleBytes(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
            {
                return string.Empty;
            }

            // 1. 检查 BOM
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            }
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            {
                return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            }
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            {
                return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
            }

            // 2. 尝试严格 UTF-8 解码
            try
            {
                var utf8Strict = new UTF8Encoding(false, true);
                return utf8Strict.GetString(bytes);
            }
            catch
            {
                // 非 UTF-8，继续尝试 GB18030 / GBK
            }

            // 3. 尝试 GB18030 / GBK
            try
            {
                return Encoding.GetEncoding("GB18030").GetString(bytes);
            }
            catch
            {
                return Encoding.UTF8.GetString(bytes);
            }
        }

        private static string FormatDurationMs(long ms)
        {
            var t = TimeSpan.FromMilliseconds(ms);
            return string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}:{2:00}", (int)t.TotalHours, t.Minutes, t.Seconds);
        }

        private static string EncodeToken(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        }

        private static string DecodeToken(string token)
        {
            var base64 = token.Replace('-', '+').Replace('_', '/');
            switch (base64.Length % 4)
            {
                case 2: base64 += "=="; break;
                case 3: base64 += "="; break;
            }
            return Encoding.UTF8.GetString(Convert.FromBase64String(base64));
        }

        public class XunleiResponse
        {
            public int code { get; set; }
            public List<XunleiItem> data { get; set; }
        }

        public class XunleiItem
        {
            public string gcid { get; set; }
            public string cid { get; set; }
            public string url { get; set; }
            public string ext { get; set; }
            public string name { get; set; }
            public long duration { get; set; }
        }
    }
}
