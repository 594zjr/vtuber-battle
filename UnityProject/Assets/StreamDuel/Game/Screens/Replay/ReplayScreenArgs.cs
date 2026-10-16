using System;
using StreamDuel.Core;

namespace StreamDuel.Game
{
    public sealed class ReplayScreenArgs
    {
        public ContentDb Content;
        public BattleRecord Record;
        public ISaveStore SaveStore;
        public Func<string, ContentDb> ResolvePack;
        public IBattleAnimator Animator;
        public IPortraitProvider Portraits;
        public ITextClipboard Clipboard;
        public GameSettings Settings;
        // 注入的客户端只借用；显式提供连接时由页面关闭，方便宿主与验收控制所有权。
        public SpectatorClient Spectator;
        public IDisposable SpectatorConnection;
        public Action Return;
    }
}
