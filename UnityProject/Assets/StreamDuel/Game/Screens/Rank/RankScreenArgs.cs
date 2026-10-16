using System;
using StreamDuel.Core;

namespace StreamDuel.Game
{
    public sealed class RankScreenArgs
    {
        public Func<Profile> LoadProfile;
        public Func<Profile> StartNewSeason;
        public string ReturnScreenId = ScreenIds.Menu;
        public object ReturnArgs;
    }
}
