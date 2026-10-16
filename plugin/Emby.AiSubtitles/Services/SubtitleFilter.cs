using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Emby.AiSubtitles.Models;

namespace Emby.AiSubtitles.Services
{
    /// <summary>
    /// 专门清洗和过滤影视中大量出现的无意义语气词、呻吟拟声词（特别是日语动作片中的「あっ、あぁ、んっ、うーん」等）
    /// 以及 Whisper 模型的复读幻觉，避免白白浪费大量大模型 Token 和生成满屏杂音字幕。
    /// </summary>
    public static class SubtitleFilter
    {
        // 匹配日文纯元音/假名呻吟喘息词（如「あっ、あぁ、んっ、ふぅ、はぁ、うっ、イッ」等）
        private static readonly Regex JapaneseMoanRegex = new Regex(
            @"^[\s.,…~～!！?？()（）—あアぁァいイぃィうウぅゥえエぇェおオぉォんンはハふフへヘほホゃャゅュょョーッっ\-]+$",
            RegexOptions.Compiled);

        // 匹配中文语气/呻吟词（如「啊～、嗯…、哈啊、唔、呃」等纯拟声）
        private static readonly Regex ChineseMoanRegex = new Regex(
            @"^[\s.,…~～!！?？()（）—啊哈嗯唔额呃呜哦哎嗷\-]+$",
            RegexOptions.Compiled);

        // 匹配英文喘息、拟声词（如「ah, oh, uh, um, mm, groan, sigh, gasp」等）
        private static readonly Regex EnglishMoanRegex = new Regex(
            @"^(?:a+h+|o+h+|u+h+|u+m+|m+h*m*|huh+|phew|sigh|gasp|groan|moan|shh+)[\s.,!?~…()\-]*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // 匹配括号内的声音/音乐描述标记，如 [Music], (喘息), （歓声）
        private static readonly Regex SoundBracketRegex = new Regex(
            @"^[\[(（【][\w\s\u4e00-\u9fa5\u3040-\u30ff]+[\])）】]$",
            RegexOptions.Compiled);

        // 标点剥离正则
        private static readonly Regex PunctuationRegex = new Regex(
            @"[\s.,…~～!！?？()（）—_\[\]【】\-]",
            RegexOptions.Compiled);

        /// <summary>
        /// 过滤列表中的无意义语气词并去除连续重复的复读幻觉。
        /// </summary>
        public static List<SubtitleCue> FilterCues(IList<SubtitleCue> input, bool enableFilter)
        {
            if (input == null || input.Count == 0)
            {
                return new List<SubtitleCue>();
            }

            try
            {
                var result = new List<SubtitleCue>();
                string lastText = null;
                double lastEnd = -100;

                foreach (var cue in input)
                {
                    var text = cue.Text?.Trim();
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        continue;
                    }

                    if (enableFilter && IsFillerOrMoan(text))
                    {
                        continue;
                    }

                    // 防 Whisper 复读幻觉：仅当紧挨着（间隔 < 3.5 秒）连续出现完全相同的短句时才去重，避免误删相隔较远的正常重复台词
                    if (string.Equals(text, lastText, StringComparison.OrdinalIgnoreCase)
                        && text.Length <= 10
                        && (cue.Start - lastEnd) < 3.5)
                    {
                        lastEnd = cue.End;
                        continue;
                    }

                    lastText = text;
                    lastEnd = cue.End;
                    result.Add(cue);
                }

                return result;
            }
            catch
            {
                return new List<SubtitleCue>(input);
            }
        }

        /// <summary>
        /// 时间轴校正：单条最长 7 秒；不与下一句重叠（下一句开始前 50ms 结束）；过短的条目延长到至少 0.8 秒（不越过下一句）。
        /// 解决「人物已开始说下一句，字幕还停在上一句」的问题。
        /// </summary>
        public static void NormalizeTiming(List<SubtitleCue> cues, double maxSeconds = 7.0, double minSeconds = 0.8)
        {
            if (cues == null || cues.Count == 0)
            {
                return;
            }

            cues.Sort((a, b) => a.Start.CompareTo(b.Start));
            for (int i = 0; i < cues.Count; i++)
            {
                var c = cues[i];
                double nextStart = i + 1 < cues.Count ? cues[i + 1].Start : double.MaxValue;
                double end = Math.Min(c.End, c.Start + maxSeconds);
                if (end - c.Start < minSeconds)
                {
                    end = c.Start + minSeconds;
                }
                end = Math.Min(end, nextStart - 0.05);
                if (end <= c.Start)
                {
                    end = Math.Min(c.Start + 0.3, Math.Max(c.Start + 0.05, nextStart));
                }
                c.End = end;
            }
        }

        private static readonly HashSet<string> DialogueWhitelist = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "はい", "ハイ", "いいえ", "いえ", "いい", "うん", "ううん",
            "おい", "おーい", "おう", "ええ", "え", "えっ", "えー", "へえ", "へぇ", "ああ",
            "yes", "no", "ok", "okay", "yeah", "hey", "hi", "what", "wait",
            "好的", "是的", "对", "好", "行", "喂", "哎"
        };

        public static bool IsFillerOrMoan(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return true;
            }

            try
            {
                text = text.Trim();

                // 纯标点符号
                var stripped = PunctuationRegex.Replace(text, "");
                if (string.IsNullOrEmpty(stripped))
                {
                    return true;
                }

                // 对话白名单：绝对不能把正常的简短应答（如「はい」「いい」「うん」「おい」「えっ」「yes」等）误杀
                if (DialogueWhitelist.Contains(stripped))
                {
                    return false;
                }

                // [Music] 等括号音效描述
                if (SoundBracketRegex.IsMatch(text))
                {
                    return true;
                }

                // 英文拟声词
                if (EnglishMoanRegex.IsMatch(text))
                {
                    return true;
                }

                // 中文纯拟声呻吟词
                if (stripped.Length <= 6 && ChineseMoanRegex.IsMatch(text))
                {
                    return true;
                }

                // 日文拟声词/呻吟词
                if (JapaneseMoanRegex.IsMatch(text))
                {
                    return true;
                }

                return false;
            }
            catch
            {
                return false;
            }
        }
    }
}
