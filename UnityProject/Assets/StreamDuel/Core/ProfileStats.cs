using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace StreamDuel.Core
{
    public enum ProfileBattleOutcome { Win, Loss, Draw }

    public sealed class StreamerUsageStats
    {
        public string PackId { get; }
        public string StreamerId { get; }
        public string Name { get; }
        public long Games { get; }
        public long Wins { get; }
        public double WinRate { get { return Games == 0 ? 0 : (double)Wins / Games; } }
        public bool CountsFromProfile { get; }
        public long SampleGames { get; }
        public long DamageDealt { get; }
        public long Kos { get; }
        public double AverageDamage { get { return SampleGames == 0 ? 0 : (double)DamageDealt / SampleGames; } }

        internal StreamerUsageStats(string pack, string id, string name, long games, long wins,
            bool fromProfile, long samples, long damage, long kos)
        {
            PackId = pack; StreamerId = id; Name = name; Games = games; Wins = wins;
            CountsFromProfile = fromProfile; SampleGames = samples; DamageDealt = damage; Kos = kos;
        }
    }

    public sealed class TeamUsageStats
    {
        public string PackId { get; }
        public IReadOnlyList<string> StreamerIds { get; }
        public IReadOnlyList<string> Names { get; }
        public long Games { get; }
        public long Wins { get; }
        public long Losses { get; }
        public long Draws { get; }
        public double WinRate { get { return Games == 0 ? 0 : (double)Wins / Games; } }

        internal TeamUsageStats(string pack, IEnumerable<string> ids, IEnumerable<string> names,
            long wins, long losses, long draws)
        {
            PackId = pack;
            StreamerIds = ids.ToList().AsReadOnly();
            Names = names.ToList().AsReadOnly();
            Wins = wins; Losses = losses; Draws = draws;
            Games = checked(wins + losses + draws);
        }
    }

    public sealed class AiOpponentStats
    {
        public AiLevel Level { get; }
        public long Games { get; }
        public long Wins { get; }
        public long Losses { get; }
        public long Draws { get; }
        public double WinRate { get { return Games == 0 ? 0 : (double)Wins / Games; } }

        internal AiOpponentStats(AiLevel level, long wins, long losses, long draws)
        {
            Level = level; Wins = wins; Losses = losses; Draws = draws;
            Games = checked(wins + losses + draws);
        }
    }

    public sealed class RecentBattleStats
    {
        public string Id { get; }
        public string PackId { get; }
        public DateTimeOffset Date { get; }
        public IReadOnlyList<string> Names { get; }
        public int? PlayerSide { get; }
        public ProfileBattleOutcome? Outcome { get; }
        public AiLevel? OpponentAiLevel { get; }
        public int Turns { get; }

        internal RecentBattleStats(ReplayEntry entry, ProfileBattleOutcome? outcome)
        {
            Id = entry.Id; PackId = entry.PackId; Date = entry.Date; Turns = entry.Turns;
            Names = (entry.Names ?? Array.Empty<string>()).ToList().AsReadOnly();
            PlayerSide = entry.Stats == null ? (int?)null : entry.Stats.PlayerSide;
            OpponentAiLevel = entry.Stats == null ? null : entry.Stats.OpponentAiLevel;
            Outcome = outcome;
        }
    }

    public sealed class StreakStats
    {
        public long CurrentWins { get; }
        public long CurrentLosses { get; }
        public long LongestWins { get; }

        internal StreakStats(long wins, long losses, long longest)
        {
            CurrentWins = wins; CurrentLosses = losses; LongestWins = longest;
        }
    }

    public sealed class ProfileStats
    {
        sealed class StreamerCount
        {
            public long Games, Wins, Samples, SampleWins, Damage, Kos;
            public bool FromProfile;
        }

        sealed class Results
        {
            public long Wins, Losses, Draws;
            public void Add(ProfileBattleOutcome outcome)
            {
                if (outcome == ProfileBattleOutcome.Win) Wins = checked(Wins + 1);
                else if (outcome == ProfileBattleOutcome.Loss) Losses = checked(Losses + 1);
                else Draws = checked(Draws + 1);
            }
        }

        sealed class TeamCount
        {
            public string Pack;
            public string[] Ids;
            public readonly Results Results = new Results();
        }

        readonly IReadOnlyList<StreamerUsageStats> streamers;
        readonly IReadOnlyList<TeamUsageStats> teams;
        readonly IReadOnlyList<AiOpponentStats> ai;
        readonly IReadOnlyList<RecentBattleStats> recent;
        readonly StreakStats streaks;

        /// <summary>复制已读取的本机数据后一次聚合，不读取文件、不重放战斗；缺省输入视为空数据。</summary>
        public ProfileStats(Profile profile, IEnumerable<ReplayEntry> entries, IEnumerable<ContentDb> contents = null,
            PackPolicy policy = null)
        {
            var disabledPacks = new HashSet<string>(StringComparer.Ordinal);
            var disabledStreamers = new HashSet<string>(StringComparer.Ordinal);
            void IncludePolicy(PackPolicy source)
            {
                if (source == null) return;
                disabledPacks.UnionWith(source.DisabledPacks);
                disabledStreamers.UnionWith(source.DisabledStreamers);
            }
            // 整包下架后不会载入定义，仍须从调用方的政策标记历史名字；空参数不能触发磁盘读取。
            IncludePolicy(policy);
            var names = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
            foreach (var db in contents ?? Array.Empty<ContentDb>())
            {
                if (db == null) continue;
                IncludePolicy(db.Policy);
                var packNames = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var fighter in db.Streamers) packNames[fighter.Id] = fighter.Name;
                names[db.PackId] = packNames;
            }
            string Name(string pack, string id)
            {
                if (disabledPacks.Contains(pack) || disabledStreamers.Contains(id)) return "已下架";
                if (!names.TryGetValue(pack, out var packNames)) return id + "（未知）";
                return packNames.TryGetValue(id, out var value) ? value : "已下架";
            }

            var counts = new Dictionary<(string Pack, string Id), StreamerCount>();
            if (profile != null)
            {
                foreach (var pack in profile.Packs)
                    foreach (var pick in pack.Value.Picks)
                    {
                        pack.Value.PickWins.TryGetValue(pick.Key, out int wins);
                        if (pick.Value < 0 || wins < 0 || wins > pick.Value)
                            throw new ArgumentException("档案中的选用次数或胜场无效。", nameof(profile));
                        counts[(pack.Key, pick.Key)] = new StreamerCount
                        {
                            Games = pick.Value, Wins = wins, FromProfile = true
                        };
                    }
            }

            var teamCounts = new Dictionary<string, TeamCount>(StringComparer.Ordinal);
            var aiCounts = new Dictionary<AiLevel, Results>();
            var games = new List<RecentBattleStats>();
            foreach (var entry in entries ?? Array.Empty<ReplayEntry>())
            {
                if (entry == null || entry.IsCorrupt) continue;
                var snapshot = entry.Stats;
                ProfileBattleOutcome? outcome = null;
                if (snapshot != null)
                {
                    ValidateSnapshot(entry);
                    outcome = entry.Winner == -1 ? ProfileBattleOutcome.Draw
                        : entry.Winner == snapshot.PlayerSide ? ProfileBattleOutcome.Win : ProfileBattleOutcome.Loss;
                    foreach (var fighter in snapshot.Fighters)
                    {
                        var key = (entry.PackId, fighter.StreamerId);
                        if (!counts.TryGetValue(key, out var count)) counts[key] = count = new StreamerCount();
                        count.Samples = checked(count.Samples + 1);
                        if (outcome == ProfileBattleOutcome.Win) count.SampleWins = checked(count.SampleWins + 1);
                        count.Damage = checked(count.Damage + fighter.DamageDealt);
                        count.Kos = checked(count.Kos + fighter.Kos);
                    }
                    // 阵容按实际参赛名单归组，长度前缀避免不同包或含分隔符的编号意外合并。
                    var ids = snapshot.Fighters.Select(fighter => fighter.StreamerId).OrderBy(id => id, StringComparer.Ordinal).ToArray();
                    string teamKey = Part(entry.PackId) + string.Concat(ids.Select(Part));
                    if (!teamCounts.TryGetValue(teamKey, out var team))
                        teamCounts[teamKey] = team = new TeamCount { Pack = entry.PackId, Ids = ids };
                    team.Results.Add(outcome.Value);
                    if (snapshot.OpponentAiLevel.HasValue)
                    {
                        AiLevel level = snapshot.OpponentAiLevel.Value;
                        if (!aiCounts.TryGetValue(level, out var result)) aiCounts[level] = result = new Results();
                        result.Add(outcome.Value);
                    }
                }
                games.Add(new RecentBattleStats(entry, outcome));
            }

            // 档案和录像通常覆盖同一批对局，不能把累计选用次数再与样本数相加。
            streamers = counts.Select(pair => new StreamerUsageStats(pair.Key.Pack, pair.Key.Id,
                Name(pair.Key.Pack, pair.Key.Id), pair.Value.FromProfile ? pair.Value.Games : pair.Value.Samples,
                pair.Value.FromProfile ? pair.Value.Wins : pair.Value.SampleWins, pair.Value.FromProfile,
                pair.Value.Samples, pair.Value.Damage, pair.Value.Kos))
                .OrderByDescending(row => row.Games).ThenBy(row => row.PackId, StringComparer.Ordinal)
                .ThenBy(row => row.StreamerId, StringComparer.Ordinal).ToList().AsReadOnly();
            teams = teamCounts.OrderByDescending(pair => pair.Value.Results.Wins + pair.Value.Results.Losses + pair.Value.Results.Draws)
                .ThenBy(pair => pair.Value.Pack, StringComparer.Ordinal).ThenBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new TeamUsageStats(pair.Value.Pack, pair.Value.Ids,
                    pair.Value.Ids.Select(id => Name(pair.Value.Pack, id)), pair.Value.Results.Wins,
                    pair.Value.Results.Losses, pair.Value.Results.Draws)).ToList().AsReadOnly();
            ai = aiCounts.OrderBy(pair => pair.Key).Select(pair => new AiOpponentStats(pair.Key,
                pair.Value.Wins, pair.Value.Losses, pair.Value.Draws)).ToList().AsReadOnly();
            recent = games.OrderByDescending(game => game.Date).ThenByDescending(game => game.Id, StringComparer.Ordinal)
                .ThenBy(game => game.PackId, StringComparer.Ordinal).ToList().AsReadOnly();
            long longest = 0, run = 0;
            foreach (var game in recent)
            {
                run = game.Outcome == ProfileBattleOutcome.Win ? checked(run + 1) : 0;
                longest = Math.Max(longest, run);
            }
            streaks = new StreakStats(recent.TakeWhile(game => game.Outcome == ProfileBattleOutcome.Win).LongCount(),
                recent.TakeWhile(game => game.Outcome == ProfileBattleOutcome.Loss).LongCount(), longest);
        }

        public IReadOnlyList<StreamerUsageStats> ByStreamer() { return streamers; }
        public IReadOnlyList<TeamUsageStats> TopTeams(int n) { return teams.Take(Math.Max(0, n)).ToList().AsReadOnly(); }
        public IReadOnlyList<AiOpponentStats> VsAi() { return ai; }
        public IReadOnlyList<RecentBattleStats> Recent(int n) { return recent.Take(Math.Max(0, n)).ToList().AsReadOnly(); }
        public StreakStats Streaks() { return streaks; }

        static string Part(string value) { return value.Length.ToString(CultureInfo.InvariantCulture) + ":" + value; }

        static void ValidateSnapshot(ReplayEntry entry)
        {
            var snapshot = entry.Stats;
            if (entry.PackId == null || entry.Winner < -1 || entry.Winner > 1 || snapshot.PlayerSide < 0 || snapshot.PlayerSide > 1
                || snapshot.Fighters == null || snapshot.Fighters.Count == 0
                || snapshot.OpponentAiLevel.HasValue && !Enum.IsDefined(typeof(AiLevel), snapshot.OpponentAiLevel.Value))
                throw new ArgumentException("录像统计快照的对战方、结果或名单无效。", nameof(entry));
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var fighter in snapshot.Fighters)
                if (fighter == null || string.IsNullOrEmpty(fighter.StreamerId) || !seen.Add(fighter.StreamerId)
                    || fighter.DamageDealt < 0 || fighter.Kos < 0)
                    throw new ArgumentException("录像统计快照含重复主播或无效计数。", nameof(entry));
        }
    }
}
