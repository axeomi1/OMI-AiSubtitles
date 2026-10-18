using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Emby.AiSubtitles.Services;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Providers;
using MediaBrowser.Model.Serialization;

namespace Emby.AiSubtitles
{
    /// <summary>
    /// 作为 Emby 的字幕提供程序出现在“搜索字幕”对话框中。
    /// 搜索结果只有一条，根据当前状态显示为：点击开始生成 / 生成中 / 已完成 / 失败重试。
    /// </summary>
    public class AiSubtitleProvider : ISubtitleProvider
    {
        private const string IdPrefix = "aisub_";

        private readonly ILogger _logger;
        private readonly JobManager _jobs;
        private readonly OnlineSubtitleSearcher _onlineSearcher;

        public AiSubtitleProvider(ILogManager logManager, IJsonSerializer json, IFfmpegManager ffmpegManager,
            ILibraryManager libraryManager, IProviderManager providerManager, IFileSystem fileSystem)
        {
            _logger = logManager.GetLogger("AiSubtitles");
            _jobs = JobManager.GetOrCreate(_logger, json, ffmpegManager, libraryManager, providerManager, fileSystem);
            _onlineSearcher = new OnlineSubtitleSearcher(_logger, json);
        }

        public string Name => Plugin.PluginName;

        public IEnumerable<VideoContentType> SupportedMediaTypes => new[] { VideoContentType.Movie, VideoContentType.Episode };

        public async Task<IEnumerable<RemoteSubtitleInfo>> Search(SubtitleSearchRequest request, CancellationToken cancellationToken)
        {
            var results = new List<RemoteSubtitleInfo>();
            var config = Plugin.Instance.Configuration;

            if (string.IsNullOrEmpty(request.MediaPath) || !File.Exists(request.MediaPath))
            {
                return results;
            }

            var isAutomatic = request.IsPerfectMatch && !request.SearchAllProviders;
            var language = string.IsNullOrWhiteSpace(request.Language) ? "chi" : request.Language.ToLowerInvariant();
            var format = SrtFormatter.NormalizeFormat(config.SubtitleFormat);
            var title = BuildTitle(request);

            // 1. 优先搜索迅雷字幕库 + SubtitleCat 番号现成字幕（1秒直下）
            if (config.EnableOnlineSubtitleSearch)
            {
                try
                {
                    var onlineResults = await _onlineSearcher.SearchAllAsync(
                        request.MediaPath, title, language, format, cancellationToken).ConfigureAwait(false);
                    if (onlineResults != null && onlineResults.Count > 0)
                    {
                        results.AddRange(onlineResults);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warn("AI 字幕: 在线字幕搜索异常: {0}", ex.Message);
                }
            }

            // 媒体库自动下载字幕时默认不触发耗时的 AI 语音识别，只返回上面的在线现成字幕
            if (isAutomatic && !config.AllowAutomaticDownload)
            {
                return results;
            }

            // 2. AI 语音识别 + 大模型翻译兜底项
            var key = JobManager.BuildKey(request.MediaPath, language);

            _jobs.RememberRequest(new JobRequest
            {
                Key = key,
                MediaPath = request.MediaPath,
                Language = language,
                TwoLetterLanguage = request.LanguageInfo?.TwoLetterISOLanguageName ?? language,
                LanguageName = GetLanguageName(language, request),
                Title = title,
                Format = format
            });

            var info = new RemoteSubtitleInfo
            {
                Id = IdPrefix + key,
                ProviderName = Name,
                Language = language,
                Format = format,
                Author = "AI 语音识别",
                IsHashMatch = false,
                IsForced = false
            };

            var job = _jobs.GetJob(key);
            if (_jobs.TryGetCached(key, out var cachePath, out var cachedFormat))
            {
                info.Format = cachedFormat;
                info.Name = "🤖 AI 字幕 · 已完成 (" + cachedFormat.ToUpperInvariant() + ")";
                info.Comment = "已生成完毕，点击下载";
                info.DateCreated = File.GetLastWriteTimeUtc(cachePath);
            }
            else if (job != null && job.State != JobState.Failed && job.State != JobState.Completed)
            {
                info.Name = "🤖 AI 字幕 · " + JobManager.DescribeState(job);
                info.Comment = "正在后台生成，可在插件设置页实时查看所有任务进度";
            }
            else if (job != null && job.State == JobState.Failed)
            {
                info.Name = "🤖 AI 字幕 · 生成失败，点击重试";
                info.Comment = job.Error;
            }
            else
            {
                info.Name = "🤖 AI 字幕 · 点击开始生成 (" + format.ToUpperInvariant() + ")";
                info.Comment = config.EnableTranslation
                    ? "无现成字幕时使用：语音识别 + 大模型翻译（底部双色双语），可在插件设置页查看进度"
                    : "无现成字幕时使用：语音识别生成原语言字幕，可在插件设置页查看进度";
            }

            results.Add(info);
            return results;
        }

        public async Task<SubtitleResponse> GetSubtitles(string id, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(id))
            {
                throw new ArgumentException("无效的字幕 ID");
            }

            // 1. 迅雷字幕库 / SubtitleCat 现成字幕秒级直下 + 自动转底部 ASS
            if (id.StartsWith(OnlineSubtitleSearcher.XunleiPrefix, StringComparison.Ordinal) ||
                id.StartsWith(OnlineSubtitleSearcher.SubtitleCatPrefix, StringComparison.Ordinal))
            {
                var (lang, fmt, content) = await _onlineSearcher.DownloadAndFormatAsync(
                    id, Plugin.Instance.Configuration, cancellationToken).ConfigureAwait(false);
                return Response(lang, fmt, content);
            }

            if (!id.StartsWith(IdPrefix, StringComparison.Ordinal))
            {
                throw new ArgumentException("无效的字幕 ID: " + id);
            }

            var key = id.Substring(IdPrefix.Length);
            _jobs.TryGetRequest(key, out var request);
            var language = request?.Language ?? "chi";
            var format = SrtFormatter.NormalizeFormat(request?.Format ?? Plugin.Instance.Configuration.SubtitleFormat);

            // 已完成：直接返回缓存的字幕
            if (_jobs.TryGetCached(key, out var cachePath, out var cachedFormat))
            {
                return Response(language, cachedFormat, File.ReadAllText(cachePath));
            }

            var job = _jobs.GetJob(key);
            if (job == null || job.State == JobState.Failed)
            {
                if (request == null)
                {
                    throw new InvalidOperationException("找不到字幕任务信息，请重新搜索字幕后再试。");
                }

                job = _jobs.Enqueue(request);
                _logger.Info("AI 字幕: 已加入队列 {0}", request.MediaPath);
            }

            // 未完成：先返回一条占位字幕，Emby 会把它保存到影片旁边；
            // 任务完成后插件会用正式字幕覆盖这个文件并刷新条目。
            var message = JobManager.PlaceholderMarker + "（" + JobManager.DescribeState(job) + "），完成后会自动替换为正式字幕";
            return Response(language, format, SrtFormatter.Placeholder(message, format));
        }

        private static SubtitleResponse Response(string language, string format, string content)
        {
            return new SubtitleResponse
            {
                Format = format,
                Language = language,
                IsForced = false,
                Stream = new MemoryStream(new UTF8Encoding(false).GetBytes(content))
            };
        }

        private static string GetLanguageName(string language, SubtitleSearchRequest request)
        {
            switch (language)
            {
                case "chi":
                case "zho":
                case "zh":
                case "zh-cn":
                    return "简体中文";
                case "zh-tw":
                case "zh-hk":
                    return "繁體中文";
            }

            return request.LanguageInfo?.DisplayName ?? language;
        }

        private static string BuildTitle(SubtitleSearchRequest request)
        {
            if (request.ContentType == VideoContentType.Episode && !string.IsNullOrEmpty(request.SeriesName))
            {
                return string.Format("{0} S{1:00}E{2:00} {3}", request.SeriesName,
                    request.ParentIndexNumber ?? 0, request.IndexNumber ?? 0, request.Name).Trim();
            }

            return request.ProductionYear.HasValue
                ? string.Format("{0} ({1})", request.Name, request.ProductionYear.Value)
                : request.Name;
        }
    }
}
