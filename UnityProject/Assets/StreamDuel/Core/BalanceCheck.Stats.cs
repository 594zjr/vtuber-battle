using System;
using System.Collections.Generic;
using System.Linq;

namespace StreamDuel.Core
{
    public sealed class BalanceRunStats
    {
        public long Battles { get; }
        public IReadOnlyList<BalanceStreamerStats> Streamers { get; }

        internal BalanceRunStats(long battles, IEnumerable<BalanceStreamerStats> streamers)
        {
            Battles = battles;
            Streamers = Array.AsReadOnly(streamers.ToArray());
        }
    }

    public sealed class BalanceStreamerStats
    {
        public string Id { get; }
        public long Games { get; }
        public long Wins { get; }
        public long Draws { get; }
        public double WinRate { get; }

        internal BalanceStreamerStats(string id, long games, long wins, long draws, double winRate)
        {
            Id = id;
            Games = games;
            Wins = wins;
            Draws = draws;
            WinRate = winRate;
        }
    }
}
