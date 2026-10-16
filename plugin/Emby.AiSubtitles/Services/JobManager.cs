using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Emby.AiSubtitles.Configuration;
using Emby.AiSubtitles.Models;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;

namespace Emby.AiSubtitles.Services
{
    public enum JobState
    {
        Queued,
        ExtractingAudio,
        Transcribing,
        Translating,
        Completed,
        Failed
    }

    public class JobRequest
    {
        public string Key { get; set; }
        public string MediaPath { get; set; }
        /// <summary>Emby 使用的三位语言代码，如 chi、eng。</summary>
        public string Language { get; set; }
        /// <summary>给大模型看的目标语言名称，如“简体中文”。</summary>
        public string LanguageName { get; set; }
        public string TwoLetterLanguage { get; set; }
        public string Title { get; set; }
        public string Format { get; set; }
    }

    public class SubtitleJob
    {
        public JobRequest Request { get; set; }
        public JobState State { get; set; }
        public double Progress { get; set; }
        public int SegmentCount { get; set; }
        public string DetectedLanguage { get; set; }
        public string Error { get; set; }
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
        public DateTime? StartedUtc { get; set; }
        public DateTime? FinishedUtc { get; set; }
    }

    /// <summary>
    /// 全局任务队列：负责 抽音频 → 语音识别 → 翻译 → 写入字幕 的完整流程。
    /// Emby 的字幕搜索接口是同步的，而 CPU 识别一部电影可能要几十分钟，所以任务在后台运行，
    /// 结果缓存在插件数据目录，并（可选）直接写到影片目录。
    /// </summary>
    public class JobManager
    {
        public const string PlaceholderMarker = "AI 字幕生成中";

        private static readonly object InitLock = new object();
        private static JobManager _instance;

        private readonly ConcurrentDictionary<string, SubtitleJob> _jobs = new ConcurrentDictionary<string, SubtitleJob>();
        private readonly ConcurrentDictionary<string, JobRequest> _pending = new ConcurrentDictionary<string, JobRequest>();
        private readonly SemaphoreSlim _slots;

        private readonly ILogger _logger;
        private readonly IFfmpegManager _ffmpegManager;
        private readonly ILibraryManager _libraryManager;
        private readonly IProviderManager _providerManager;
        private readonly IFileSystem _fileSystem;
        private readonly AudioExtractor _audio;
        private readonly WhisperClient _whisper;
        private readonly Translator _translator;

        private JobManager(ILogger logger, IJsonSerializer json, IFfmpegManager ffmpegManager,
            ILibraryManager libraryManager, IProviderManager providerManager, IFileSystem fileSystem)
        {
            _logger = logger;
            _ffmpegManager = ffmpegManager;
            _libraryManager = libraryManager;
            _providerManager = providerManager;
            _fileSystem = fileSystem;
            _audio = new AudioExtractor(logger);
            _whisper = new WhisperClient(json);
            _translator = new Translator(json, logger);
            _slots = new SemaphoreSlim(Math.Max(1, Config.MaxConcurrentJobs));
        }

        public static JobManager Instance => _instance;

        public static JobManager GetOrCreate(ILogger logger, IJsonSerializer json, IFfmpegManager ffmpegManager,
            ILibraryManager libraryManager, IProviderManager providerManager, IFileSystem fileSystem)
        {
            lock (InitLock)
            {
                return _instance ?? (_instance = new JobManager(logger, json, ffmpegManager, libraryManager, providerManager, fileSystem));
            }
        }

        private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

        private static string CacheDir
        {
            get
            {
                var dir = Path.Combine(Plugin.Instance.DataFolderPath, "cache");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        private static string TempDir
        {
            get
            {
                var dir = Path.Combine(Plugin.Instance.DataFolderPath, "tmp");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        public static string BuildKey(string mediaPath, string language)
        {
            var c = Config;
            var format = SrtFormatter.NormalizeFormat(c.SubtitleFormat);
            var raw = string.Join("|", mediaPath, language, c.EnableTranslation, c.SubtitleMode, format, c.PrimaryTextColor, c.SecondaryTextColor);
            using (var sha = SHA1.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(raw));
                return string.Concat(hash.Take(10).Select(b => b.ToString("x2")));
            }
        }

        public string GetCachePath(string key, string format = null)
        {
            var ext = SrtFormatter.NormalizeFormat(format ?? Config.SubtitleFormat);
            return Path.Combine(CacheDir, key + "." + ext);
        }

        public bool TryGetCached(string key, out string path, out string format)
        {
            var preferred = SrtFormatter.NormalizeFormat(Config.SubtitleFormat);
            var p1 = Path.Combine(CacheDir, key + "." + preferred);
            if (File.Exists(p1))
            {
                path = p1;
                format = preferred;
                return true;
            }

            var other = preferred == "ass" ? "srt" : "ass";
            var p2 = Path.Combine(CacheDir, key + "." + other);
            if (File.Exists(p2))
            {
                path = p2;
                format = other;
                return true;
            }

            path = p1;
            format = preferred;
            return false;
        }

        public SubtitleJob GetJob(string key)
        {
            _jobs.TryGetValue(key, out var job);
            return job;
        }

        public IList<SubtitleJob> GetAllJobs()
        {
            return _jobs.Values
                .OrderByDescending(j => j.CreatedUtc)
                .ToList();
        }

        public int ClearFinishedJobs()
        {
            var count = 0;
            foreach (var kv in _jobs.ToArray())
            {
                if (kv.Value.State == JobState.Completed || kv.Value.State == JobState.Failed)
                {
                    if (_jobs.TryRemove(kv.Key, out _))
                    {
                        count++;
                    }
                }
            }

            return count;
        }

        public void RememberRequest(JobRequest request) => _pending[request.Key] = request;

        public bool TryGetRequest(string key, out JobRequest request) => _pending.TryGetValue(key, out request);

        /// <summary>启动任务；如果已在排队或运行中则直接返回现有任务。</summary>
        public SubtitleJob Enqueue(JobRequest request)
        {
            SubtitleJob job;
            lock (_jobs)
            {
                if (_jobs.TryGetValue(request.Key, out job)
                    && job.State != JobState.Failed
                    && !(job.State == JobState.Completed && !TryGetCached(request.Key, out _, out _)))
                {
                    return job;
                }

                job = new SubtitleJob { Request = request, State = JobState.Queued };
                _jobs[request.Key] = job;
            }

            Task.Run(() => RunAsync(job));
            return job;
        }

        private async Task RunAsync(SubtitleJob job)
        {
            var req = job.Request;
            string audioPath = null;
            await _slots.WaitAsync().ConfigureAwait(false);
            try
            {
                job.StartedUtc = DateTime.UtcNow;
                var config = Config;
                var format = SrtFormatter.NormalizeFormat(req.Format ?? config.SubtitleFormat);
                req.Format = format;
                var ct = CancellationToken.None;
                _logger.Info("AI 字幕: 开始生成 {0} [{1}, {2}]", req.MediaPath, req.Language, format);

                // 1. 抽取音频
                job.State = JobState.ExtractingAudio;
                var ffmpeg = !string.IsNullOrWhiteSpace(config.FfmpegPath) ? config.FfmpegPath.Trim() : _ffmpegManager.FfmpegConfiguration?.EncoderPath;
                if (string.IsNullOrWhiteSpace(ffmpeg))
                {
                    ffmpeg = "ffmpeg";
                }

                audioPath = await _audio.ExtractAsync(ffmpeg, req.MediaPath, Path.Combine(TempDir, req.Key), ct).ConfigureAwait(false);

                // 2. 语音识别
                job.State = JobState.Transcribing;
                var transcription = await _whisper.TranscribeAsync(audioPath, config, ct).ConfigureAwait(false);
                var cues = transcription.segments
                    .Select(s => new SubtitleCue { Start = s.start, End = s.end, Text = s.text.Trim() })
                    .ToList();
                if (cues.Count == 0)
                {
                    throw new InvalidOperationException("没有识别到任何语音内容。");
                }

                job.SegmentCount = cues.Count;
                job.DetectedLanguage = transcription.language;
                _logger.Info("AI 字幕: 识别完成，{0} 条，检测语言 {1}", cues.Count, transcription.language);

                // 3. 翻译（原语言与目标语言相同则跳过）
                var needTranslate = config.EnableTranslation && !SameLanguage(transcription.language, req);
                if (needTranslate)
                {
                    if (string.IsNullOrWhiteSpace(config.LlmApiUrl) || string.IsNullOrWhiteSpace(config.LlmModel))
                    {
                        throw new InvalidOperationException("已开启翻译，但没有配置大模型接口地址或模型名。");
                    }

                    job.State = JobState.Translating;
                    job.Progress = 0;
                    var progress = new Progress<double>(p => job.Progress = p);
                    await _translator.TranslateAsync(cues, req.LanguageName, req.Title, config, progress, ct).ConfigureAwait(false);
                }

                // 4. 输出
                var bilingual = needTranslate && string.Equals(config.SubtitleMode, "Bilingual", StringComparison.OrdinalIgnoreCase);
                var content = SrtFormatter.Build(cues, config, bilingual);
                var cachePath = GetCachePath(req.Key, format);
                File.WriteAllText(cachePath, content, new UTF8Encoding(false));

                if (config.SaveNextToMedia)
                {
                    SaveNextToMedia(req, content, format);
                }

                job.State = JobState.Completed;
                job.Progress = 1;
                _logger.Info("AI 字幕: 完成 {0}", req.MediaPath);
            }
            catch (Exception ex)
            {
                job.State = JobState.Failed;
                job.Error = WhisperClient.FlattenException(ex);
                _logger.ErrorException("AI 字幕: 生成失败 {0}", ex, req.MediaPath);
            }
            finally
            {
                job.FinishedUtc = DateTime.UtcNow;
                AudioExtractor.TryDelete(audioPath);
                _slots.Release();
            }
        }

        private static bool SameLanguage(string detected, JobRequest req)
        {
            if (string.IsNullOrWhiteSpace(detected))
            {
                return false;
            }

            detected = detected.Trim().ToLowerInvariant();
            if (detected == "chinese")
            {
                detected = "zh";
            }
            else if (detected == "english")
            {
                detected = "en";
            }

            return string.Equals(detected, req.TwoLetterLanguage, StringComparison.OrdinalIgnoreCase)
                   || string.Equals(detected, req.Language, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 把字幕写到影片旁边：优先覆盖之前由 Emby 保存的占位字幕，否则新建 {文件名}.{语言}.{ass|srt}。
        /// 然后让 Emby 刷新该条目以识别新字幕。
        /// </summary>
        private void SaveNextToMedia(JobRequest req, string content, string format)
        {
            try
            {
                var dir = Path.GetDirectoryName(req.MediaPath);
                var baseName = Path.GetFileNameWithoutExtension(req.MediaPath);
                var targets = new List<string>();

                foreach (var ext in new[] { "ass", "srt" })
                {
                    foreach (var file in Directory.EnumerateFiles(dir, baseName + "*." + ext))
                    {
                        try
                        {
                            if (new FileInfo(file).Length < 8192 && File.ReadAllText(file).Contains(PlaceholderMarker))
                            {
                                if (string.Equals(ext, format, StringComparison.OrdinalIgnoreCase))
                                {
                                    targets.Add(file);
                                }
                                else
                                {
                                    File.Delete(file);
                                }
                            }
                        }
                        catch
                        {
                            // ignore unreadable file
                        }
                    }
                }

                if (targets.Count == 0)
                {
                    targets.Add(Path.Combine(dir, baseName + "." + req.Language + "." + format));
                }

                foreach (var target in targets)
                {
                    File.WriteAllText(target, content, new UTF8Encoding(false));
                    _logger.Info("AI 字幕: 已写入 {0}", target);
                }

                RefreshItem(req.MediaPath);
            }
            catch (Exception ex)
            {
                // 媒体目录只读等情况：缓存里仍然有字幕，用户可以再次搜索下载
                _logger.Warn("AI 字幕: 无法写入影片目录（{0}），可在“搜索字幕”中下载已完成的字幕。", ex.Message);
            }
        }

        private void RefreshItem(string mediaPath)
        {
            try
            {
                var item = _libraryManager.FindByPath(mediaPath, false);
                if (item == null)
                {
                    return;
                }

                var options = new MetadataRefreshOptions(new DirectoryService(_logger, _fileSystem))
                {
                    MetadataRefreshMode = MetadataRefreshMode.Default,
                    ReplaceAllMetadata = false,
                    EnableSubtitleDownloading = false
                };
                _providerManager.QueueRefresh(item.InternalId, options, RefreshPriority.High);
            }
            catch (Exception ex)
            {
                _logger.Warn("AI 字幕: 刷新条目失败: {0}", ex.Message);
            }
        }

        public static string DescribeState(SubtitleJob job)
        {
            var elapsedBase = job.StartedUtc ?? job.CreatedUtc;
            var minutes = Math.Max(0, (int)(DateTime.UtcNow - elapsedBase).TotalMinutes);
            var seconds = Math.Max(0, (int)(DateTime.UtcNow - elapsedBase).TotalSeconds);
            switch (job.State)
            {
                case JobState.Queued:
                    return "排队等待中";
                case JobState.ExtractingAudio:
                    return string.Format("正在抽取音轨 ({0}s)", seconds);
                case JobState.Transcribing:
                    return minutes > 0
                        ? string.Format("正在语音识别 (已用时 {0} 分钟)", minutes)
                        : string.Format("正在语音识别 ({0}s)", seconds);
                case JobState.Translating:
                    return string.Format("正在大模型翻译 {0}% ({1} 句)", (int)Math.Round(job.Progress * 100), job.SegmentCount);
                case JobState.Completed:
                    return string.Format("已完成 ({0} 句)", job.SegmentCount);
                default:
                    return "生成失败";
            }
        }
    }
}
