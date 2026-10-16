using System;
using System.Collections.Generic;
using Emby.AiSubtitles.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Emby.AiSubtitles
{
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
    {
        public const string PluginName = "AI 字幕";
        public static readonly Guid PluginGuid = new Guid("473f36ab-1339-419d-983f-54b264390fc3");

        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
        }

        public static Plugin Instance { get; private set; }

        public override string Name => PluginName;

        public override string Description => "使用 Whisper 语音识别 + 大模型翻译，为没有字幕的电影和剧集生成字幕。";

        public override Guid Id => PluginGuid;

        public IEnumerable<PluginPageInfo> GetPages()
        {
            return new[]
            {
                new PluginPageInfo
                {
                    Name = "aisubtitles",
                    EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html",
                    IsMainConfigPage = true
                },
                new PluginPageInfo
                {
                    Name = "aisubtitlesjs",
                    EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.js"
                }
            };
        }
    }
}
