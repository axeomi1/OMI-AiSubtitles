using System.Collections.Generic;

// OpenAI 兼容接口的 DTO。属性名故意使用小写，以便无论 Emby 的 JSON 序列化器
// 采用何种命名策略，都能和接口字段一一对应。
#pragma warning disable IDE1006 // 命名样式

namespace Emby.AiSubtitles.Models
{
    public class TranscriptionResponse
    {
        public string text { get; set; }
        public string language { get; set; }
        public double? duration { get; set; }
        public List<TranscriptionSegment> segments { get; set; }
    }

    public class TranscriptionSegment
    {
        public double start { get; set; }
        public double end { get; set; }
        public string text { get; set; }
    }

    public class ChatRequest
    {
        public string model { get; set; }
        public List<ChatMessage> messages { get; set; }
        public double temperature { get; set; }
    }

    public class ChatMessage
    {
        public string role { get; set; }
        public string content { get; set; }
    }

    public class ChatResponse
    {
        public List<ChatChoice> choices { get; set; }
    }

    public class ChatChoice
    {
        public ChatMessage message { get; set; }
    }

    /// <summary>插件内部使用的字幕条目。</summary>
    public class SubtitleCue
    {
        public double Start { get; set; }
        public double End { get; set; }
        public string Text { get; set; }
        public string Translation { get; set; }
    }
}
