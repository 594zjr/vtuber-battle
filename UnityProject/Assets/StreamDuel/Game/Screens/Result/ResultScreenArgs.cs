using System;
using StreamDuel.Core;

namespace StreamDuel.Game
{
    public sealed class ResultScreenArgs
    {
        public Battle Battle;
        public int PlayerSide;
        public ISaveStore SaveStore;
        public ITextClipboard Clipboard;
        public Action Rematch;
        public string RematchLabel;
        public Action ReturnToTeam;
        public Func<string, ContentDb> ResolvePack;
    }
}
