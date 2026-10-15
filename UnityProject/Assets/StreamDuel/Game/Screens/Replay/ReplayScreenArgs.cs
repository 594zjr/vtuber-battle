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
        public Action Return;
    }
}
