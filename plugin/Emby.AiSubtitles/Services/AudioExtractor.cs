using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Logging;

namespace Emby.AiSubtitles.Services
{
    public class AudioChunk
    {
        public string Path { get; set; }
        public double StartOffsetSeconds { get; set; }
    }

    /// <summary>
    /// 调用 ffmpeg 从视频（或 .strm 指向的直链/路径）中抽取低码率单声道音轨，
    /// 并按固定时长（默认 600 秒 = 10 分钟，每片仅 ~1.2MB）自动切片，彻底避免云端 API（如 Groq）
    /// 因单文件过大（15MB+）触发 Connection reset by peer。
    /// </summary>
    public class AudioExtractor
    {
        public const int ChunkSeconds = 600;

        private readonly ILogger _logger;

        public AudioExtractor(ILogger logger)
        {
            _logger = logger;
        }

        /// <returns>按时间顺序排列的音频分片列表。</returns>
        public async Task<List<AudioChunk>> ExtractChunksAsync(string ffmpegPath, string mediaPath, string tempDir, string jobKey, CancellationToken ct)
        {
            var inputSource = ResolveInputSource(mediaPath);
            var isHttp = inputSource.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                         || inputSource.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

            var inputFlags = isHttp
                ? "-reconnect 1 -reconnect_streamed 1 -reconnect_delay_max 5 -rw_timeout 30000000 "
                : string.Empty;

            // 优先使用 CBR 24k MP3（每 10 分钟分片约 1.7MB）：
            // MP3 头部包含精确时长，可避免 Groq 对无时长头的 .ogg 预扣 3000 秒额度导致提前触发 429。
            var attempts = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>(".mp3", "-c:a libmp3lame -b:a 24k"),
                new KeyValuePair<string, string>(".ogg", "-c:a libopus -b:a 16k -application voip")
            };

            string lastError = null;
            foreach (var attempt in attempts)
            {
                var ext = attempt.Key;
                CleanupChunks(tempDir, jobKey);

                var chunkPattern = Path.Combine(tempDir, jobKey + "_%03d" + ext);
                var listFile = Path.Combine(tempDir, jobKey + "_segments.csv");

                // 使用 ffmpeg segment muxer 直接边抽边按 600 秒切片，并输出每片精确起止时间到 csv
                var args = string.Format(
                    "-hide_banner -nostdin -y {0}-i \"{1}\" -map 0:a:0 -vn -sn -dn -ac 1 -ar 16000 {2} -f segment -segment_time {3} -segment_list \"{4}\" -segment_list_type csv \"{5}\"",
                    inputFlags, inputSource, attempt.Value, ChunkSeconds, listFile, chunkPattern);

                var (exitCode, stderr) = await RunAsync(ffmpegPath, args, ct).ConfigureAwait(false);
                var chunks = CollectChunks(tempDir, jobKey, ext, listFile);
                TryDelete(listFile);

                if (exitCode == 0 && chunks.Count > 0)
                {
                    _logger.Info("AI 字幕: 音轨抽取并切片完成，共 {0} 片 ({1})", chunks.Count, ext);
                    return chunks;
                }

                lastError = Tail(stderr, 800);
                _logger.Warn("AI 字幕: ffmpeg 使用 {0} 抽取音频失败 (exit {1}): {2}", ext, exitCode, lastError);
                CleanupChunks(tempDir, jobKey);
            }

            throw new InvalidOperationException("ffmpeg 抽取音频失败: " + lastError);
        }

        private static List<AudioChunk> CollectChunks(string tempDir, string jobKey, string ext, string listFile)
        {
            var result = new List<AudioChunk>();

            // 优先从 ffmpeg 生成的 csv 读取每片精确的起止秒数：filename,start_time,end_time
            if (File.Exists(listFile))
            {
                foreach (var line in File.ReadAllLines(listFile))
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    var parts = line.Split(',');
                    if (parts.Length >= 2)
                    {
                        var filePath = Path.Combine(tempDir, parts[0].Trim());
                        if (File.Exists(filePath) && new FileInfo(filePath).Length > 0)
                        {
                            double.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Any,
                                System.Globalization.CultureInfo.InvariantCulture, out var startSec);
                            result.Add(new AudioChunk { Path = filePath, StartOffsetSeconds = startSec });
                        }
                    }
                }
            }

            if (result.Count > 0)
            {
                return result;
            }

            // 兜底：按文件名顺序推算偏移
            var files = Directory.GetFiles(tempDir, jobKey + "_*" + ext)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .Where(f => new FileInfo(f).Length > 0)
                .ToList();

            for (var i = 0; i < files.Count; i++)
            {
                result.Add(new AudioChunk
                {
                    Path = files[i],
                    StartOffsetSeconds = i * ChunkSeconds
                });
            }

            return result;
        }

        public static void CleanupChunks(string tempDir, string jobKey)
        {
            try
            {
                if (!Directory.Exists(tempDir))
                {
                    return;
                }

                foreach (var f in Directory.GetFiles(tempDir, jobKey + "*"))
                {
                    TryDelete(f);
                }
            }
            catch
            {
                // ignore
            }
        }

        /// <summary>
        /// 如果媒体文件是 .strm，读取里面的第一行有效内容（HTTP URL 或本地/挂载路径）。
        /// 支持相对路径解析。
        /// </summary>
        private string ResolveInputSource(string mediaPath)
        {
            if (!mediaPath.EndsWith(".strm", StringComparison.OrdinalIgnoreCase))
            {
                return mediaPath;
            }

            var target = File.ReadLines(mediaPath, Encoding.UTF8)
                .Select(l => l.Trim())
                .FirstOrDefault(l => !string.IsNullOrEmpty(l) && !l.StartsWith("#", StringComparison.Ordinal));

            if (string.IsNullOrEmpty(target))
            {
                throw new InvalidOperationException(".strm 文件内容为空: " + mediaPath);
            }

            if (!target.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                && !target.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                && !target.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase)
                && !target.StartsWith("rtmp://", StringComparison.OrdinalIgnoreCase)
                && !Path.IsPathRooted(target))
            {
                var dir = Path.GetDirectoryName(mediaPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    target = Path.GetFullPath(Path.Combine(dir, target));
                }
            }

            _logger.Info("AI 字幕: 解析 .strm 文件 {0} -> {1}", mediaPath, target);
            return target;
        }

        private static async Task<(int, string)> RunAsync(string fileName, string arguments, CancellationToken ct)
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                StandardErrorEncoding = Encoding.UTF8,
                StandardOutputEncoding = Encoding.UTF8
            };

            using (var process = new Process { StartInfo = psi, EnableRaisingEvents = true })
            {
                var stderr = new StringBuilder();
                var exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                process.ErrorDataReceived += (s, e) =>
                {
                    if (e.Data != null)
                    {
                        lock (stderr)
                        {
                            stderr.AppendLine(e.Data);
                            if (stderr.Length > 64 * 1024)
                            {
                                stderr.Remove(0, stderr.Length - 32 * 1024);
                            }
                        }
                    }
                };
                process.OutputDataReceived += (s, e) => { };
                process.Exited += (s, e) => exited.TrySetResult(true);

                process.Start();
                process.BeginErrorReadLine();
                process.BeginOutputReadLine();

                using (ct.Register(() =>
                {
                    try
                    {
                        if (!process.HasExited)
                        {
                            process.Kill();
                        }
                    }
                    catch
                    {
                        // ignore
                    }
                }))
                {
                    await exited.Task.ConfigureAwait(false);
                }

                process.WaitForExit();
                ct.ThrowIfCancellationRequested();
                lock (stderr)
                {
                    return (process.ExitCode, stderr.ToString());
                }
            }
        }

        private static string Tail(string s, int max)
        {
            if (string.IsNullOrEmpty(s))
            {
                return string.Empty;
            }

            return s.Length <= max ? s : s.Substring(s.Length - max);
        }

        public static void TryDelete(string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // ignore
            }
        }
    }
}
