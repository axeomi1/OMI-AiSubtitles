using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Emby.AiSubtitles.Models;

namespace Emby.AiSubtitles.Services
{
    public static class SrtFormatter
    {
        public static string Build(IList<SubtitleCue> cues, bool bilingual)
        {
            var sb = new StringBuilder();
            var index = 1;
            foreach (var cue in cues)
            {
                var original = Clean(cue.Text);
                var translated = Clean(cue.Translation);
                string body;
                if (string.IsNullOrEmpty(translated))
                {
                    body = original;
                }
                else if (bilingual && !string.Equals(original, translated, StringComparison.Ordinal))
                {
                    body = translated + "\n" + original;
                }
                else
                {
                    body = translated;
                }

                if (string.IsNullOrWhiteSpace(body))
                {
                    continue;
                }

                sb.Append(index++).Append('\n');
                sb.Append(FormatTime(cue.Start)).Append(" --> ").Append(FormatTime(Math.Max(cue.End, cue.Start + 0.3))).Append('\n');
                sb.Append(body).Append("\n\n");
            }

            return sb.ToString();
        }

        /// <summary>字幕生成期间先交给 Emby 保存的占位字幕。</summary>
        public static string Placeholder(string message)
        {
            return "1\n00:00:01,000 --> 00:00:15,000\n" + message + "\n\n";
        }

        private static string Clean(string text)
        {
            return string.IsNullOrWhiteSpace(text) ? string.Empty : text.Replace("\r", string.Empty).Trim();
        }

        private static string FormatTime(double seconds)
        {
            if (seconds < 0)
            {
                seconds = 0;
            }

            var t = TimeSpan.FromMilliseconds(Math.Round(seconds * 1000));
            return string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}:{2:00},{3:000}",
                (int)t.TotalHours, t.Minutes, t.Seconds, t.Milliseconds);
        }
    }
}
