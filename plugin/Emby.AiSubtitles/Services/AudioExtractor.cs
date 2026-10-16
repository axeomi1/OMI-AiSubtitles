using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Logging;

namespace Emby.AiSubtitles.Services
{
    /// <summary>调用 ffmpeg 从视频中抽取低码率单声道音轨，供语音识别使用。</summary>
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
            // 优先 Opus（2 小时约 15MB，也能满足 OpenAI 云端 25MB 的限制），失败则回退 MP3。
            var attempts = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>(".ogg", "-c:a libopus -b:a 24k"),
                new KeyValuePair<string, string>(".mp3", "-c:a libmp3lame -b:a 32k")
            };

            string lastError = null;
            foreach (var attempt in attempts)
            {
                var output = outputPathWithoutExt + attempt.Key;
                var args = string.Format(
                    "-hide_banner -nostdin -y -i \"{0}\" -map 0:a:0 -vn -sn -dn -ac 1 -ar 16000 {1} \"{2}\"",
                    mediaPath, attempt.Value, output);

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
