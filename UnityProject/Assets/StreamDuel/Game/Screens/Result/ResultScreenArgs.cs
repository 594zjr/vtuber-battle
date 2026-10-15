using System;
using StreamDuel.Core;

namespace StreamDuel.Game
{
    public sealed class ResultScreenArgs
    {
        public Battle Battle;
        public int PlayerSide;
        public ISaveStore SaveStore;
        public Action Rematch;
        public Action ReturnToTeam;
        public Func<string, ContentDb> ResolvePack;
    }
}
