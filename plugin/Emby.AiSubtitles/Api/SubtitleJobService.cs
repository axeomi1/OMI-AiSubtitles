using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Emby.AiSubtitles.Services;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Services;

namespace Emby.AiSubtitles.Api
{
    [Route("/AiSubtitles/Jobs", "GET", Summary = "获取所有 AI 字幕生成任务的实时进度")]
    public class GetSubtitleJobs : IReturn<SubtitleJobsResponse>
    {
    }

    [Route("/AiSubtitles/Jobs/Clear", "POST", Summary = "清理已完成或失败的字幕任务记录")]
    public class ClearSubtitleJobs : IReturn<ClearJobsResponse>
    {
    }

    public class SubtitleJobsResponse
    {
        public List<SubtitleJobDto> Jobs { get; set; } = new List<SubtitleJobDto>();
    }

    public class ClearJobsResponse
    {
        public int Removed { get; set; }
    }

    public class SubtitleJobDto
    {
        public string Key { get; set; }
        public string Title { get; set; }
        public string FileName { get; set; }
        public string Language { get; set; }
        public string Format { get; set; }
        public string State { get; set; }
        public string StateText { get; set; }
        public int Percent { get; set; }
        public int SegmentCount { get; set; }
        public string DetectedLanguage { get; set; }
        public string Error { get; set; }
        public int ElapsedSeconds { get; set; }
        public string CreatedTime { get; set; }
    }

    public class SubtitleJobService : IService
    {
        private readonly JobManager _jobs;

        public SubtitleJobService(ILogManager logManager, IJsonSerializer json, IFfmpegManager ffmpegManager,
            ILibraryManager libraryManager, IProviderManager providerManager, IFileSystem fileSystem)
        {
            var logger = logManager.GetLogger("AiSubtitles");
            _jobs = JobManager.GetOrCreate(logger, json, ffmpegManager, libraryManager, providerManager, fileSystem);
        }

        public object Get(GetSubtitleJobs request)
        {
            var list = _jobs.GetAllJobs().Select(ToDto).ToList();
            return new SubtitleJobsResponse { Jobs = list };
        }

        public object Post(ClearSubtitleJobs request)
        {
            return new ClearJobsResponse { Removed = _jobs.ClearFinishedJobs() };
        }

        private static SubtitleJobDto ToDto(SubtitleJob job)
        {
            var req = job.Request;
            var end = job.FinishedUtc ?? DateTime.UtcNow;
            var start = job.StartedUtc ?? job.CreatedUtc;
            var elapsed = Math.Max(0, (int)(end - start).TotalSeconds);

            int percent;
            switch (job.State)
            {
                case JobState.Queued:
                    percent = 2;
                    break;
                case JobState.ExtractingAudio:
                    percent = 10;
                    break;
                case JobState.Transcribing:
                    percent = 15 + (int)Math.Round(job.Progress * 35);
                    break;
                case JobState.Translating:
                    percent = 50 + (int)Math.Round(job.Progress * 48);
                    break;
                case JobState.Completed:
                    percent = 100;
                    break;
                default:
                    percent = 100;
                    break;
            }

            return new SubtitleJobDto
            {
                Key = req?.Key ?? string.Empty,
                Title = !string.IsNullOrWhiteSpace(req?.Title) ? req.Title : Path.GetFileNameWithoutExtension(req?.MediaPath ?? string.Empty),
                FileName = Path.GetFileName(req?.MediaPath ?? string.Empty),
                Language = req?.LanguageName ?? req?.Language ?? "chi",
                Format = (req?.Format ?? "ass").ToUpperInvariant(),
                State = job.State.ToString(),
                StateText = JobManager.DescribeState(job),
                Percent = percent,
                SegmentCount = job.SegmentCount,
                DetectedLanguage = job.DetectedLanguage ?? string.Empty,
                Error = job.Error ?? string.Empty,
                ElapsedSeconds = elapsed,
                CreatedTime = job.CreatedUtc.ToLocalTime().ToString("HH:mm:ss")
            };
        }
    }
}
