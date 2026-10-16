using MediaBrowser.Model.Plugins;

namespace Emby.AiSubtitles.Configuration
{
    public class PluginConfiguration : BasePluginConfiguration
    {
        // ---------- 语音识别（OpenAI 兼容的 /audio/transcriptions 接口） ----------

        /// <summary>Whisper 服务地址（以 /v1 结尾），默认指向 docker-compose 里的 whisper 容器。</summary>
        public string WhisperApiUrl { get; set; } = "http://emby-ai-whisper:8000/v1";

        public string WhisperApiKey { get; set; } = "";

        /// <summary>传给接口的模型名。自建服务会忽略它（以容器环境变量为准）；OpenAI 云端填 whisper-1。</summary>
        public string WhisperModel { get; set; } = "small";

        /// <summary>影片原语言（两位代码，如 en、ja）。留空则自动识别。</summary>
        public string SourceLanguage { get; set; } = "";

        // ---------- 翻译（OpenAI 兼容的 /chat/completions 接口） ----------

        public bool EnableTranslation { get; set; } = true;

        public string LlmApiUrl { get; set; } = "https://api.deepseek.com/v1";

        public string LlmApiKey { get; set; } = "";

        public string LlmModel { get; set; } = "deepseek-chat";

        /// <summary>每次请求翻译的字幕行数。</summary>
        public int TranslationBatchSize { get; set; } = 30;

        /// <summary>Bilingual = 译文+原文双语；Translated = 只要译文。</summary>
        public string SubtitleMode { get; set; } = "Bilingual";

        /// <summary>字幕文件格式：ass（推荐，支持精准底部对齐和独立双色字号）或 srt（带 font color 标签）。</summary>
        public string SubtitleFormat { get; set; } = "ass";

        /// <summary>主字幕（中文/译文）颜色，十六进制 RGB，默认白色 #FFFFFF。</summary>
        public string PrimaryTextColor { get; set; } = "#FFFFFF";

        /// <summary>副字幕（英文/原文）颜色，十六进制 RGB，默认黄色 #FFD700。</summary>
        public string SecondaryTextColor { get; set; } = "#FFD700";

        /// <summary>是否自动过滤无意义的呻吟、喘息、拟声词（如「あっ、あぁ、んっ、ah、uh」）及 Whisper 复读幻觉，大幅节省大模型 Token 并净化画面。</summary>
        public bool FilterFillerSounds { get; set; } = true;

        // ---------- 任务与输出 ----------

        /// <summary>生成完成后把 .srt 直接写到影片所在目录并刷新条目。</summary>
        public bool SaveNextToMedia { get; set; } = true;

        /// <summary>同时运行的生成任务数。纯 CPU 建议保持 1。</summary>
        public int MaxConcurrentJobs { get; set; } = 1;

        /// <summary>
        /// 是否允许 Emby 在扫描媒体库“自动下载字幕”时触发 AI 生成。
        /// 关闭时只有在“搜索字幕”对话框里手动点选才会生成。
        /// </summary>
        public bool AllowAutomaticDownload { get; set; } = false;

        /// <summary>自定义 ffmpeg 路径，留空使用 Emby 自带的 ffmpeg。</summary>
        public string FfmpegPath { get; set; } = "";

        /// <summary>单次识别请求超时时间（分钟）。CPU 识别长片可能需要很久。</summary>
        public int RequestTimeoutMinutes { get; set; } = 360;
    }
}
