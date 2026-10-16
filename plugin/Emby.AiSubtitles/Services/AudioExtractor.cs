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
    /// <summary>调用 ffmpeg 从视频（或 .strm 指向的直链/路径）中抽取低码率单声道音轨，供语音识别使用。</summary>
    public class AudioExtractor
    {
        private readonly ILogger _logger;

        public AudioExtractor(ILogger logger)
        {
            _logger = logger;
        }

        /// <returns>生成的音频文件路径。</returns>
        public async Task<string> ExtractAsync(string ffmpegPath, string mediaPath, string outputPathWithoutExt, CancellationToken ct)
        {
            var inputSource = ResolveInputSource(mediaPath);
            var isHttp = inputSource.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                         || inputSource.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

            // 如果是 HTTP/HTTPS 网络流（如 .strm 指向的 Alist / 115 / WebDAV 直链），加上自动重连参数
            var inputFlags = isHttp
                ? "-reconnect 1 -reconnect_streamed 1 -reconnect_delay_max 5 -rw_timeout 30000000 "
                : string.Empty;

            // 优先 Opus 16k（语音识别足够清晰，2 小时仅约 11MB，3 小时约 17MB，轻松通过云端/网关 25MB 限制），失败则回退 MP3 24k。
            var attempts = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>(".ogg", "-c:a libopus -b:a 16k -application voip"),
                new KeyValuePair<string, string>(".mp3", "-c:a libmp3lame -b:a 24k")
            };

            string lastError = null;
            foreach (var attempt in attempts)
            {
                var output = outputPathWithoutExt + attempt.Key;
                var args = string.Format(
                    "-hide_banner -nostdin -y {0}-i \"{1}\" -map 0:a:0 -vn -sn -dn -ac 1 -ar 16000 {2} \"{3}\"",
                    inputFlags, inputSource, attempt.Value, output);

                var (exitCode, stderr) = await RunAsync(ffmpegPath, args, ct).ConfigureAwait(false);
                if (exitCode == 0 && File.Exists(output) && new FileInfo(output).Length > 0)
                {
                    return output;
                }

                lastError = Tail(stderr, 800);
                _logger.Warn("AI 字幕: ffmpeg 使用 {0} 抽取音频失败 (exit {1}): {2}", attempt.Key, exitCode, lastError);
                TryDelete(output);
            }

            throw new InvalidOperationException("ffmpeg 抽取音频失败: " + lastError);
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

            // 如果不是 URL 且是相对路径，则相对于 .strm 所在目录解析
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
