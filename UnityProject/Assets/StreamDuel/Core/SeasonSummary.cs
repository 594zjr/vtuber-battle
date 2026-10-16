using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace StreamDuel.Core
{
    public sealed class SeasonStreamerSummary
    {
        public string Id { get; }
        public int Games { get; }
        public int Wins { get; }
        public double WinRate { get { return Games == 0 ? 0 : (double)Wins / Games; } }

        internal SeasonStreamerSummary(string id, int games, int wins)
        {
            Id = id; Games = games; Wins = wins;
        }
    }

    public sealed class OpponentStreamerSummary
    {
        public string Id { get; }
        public int Games { get; }

        internal OpponentStreamerSummary(string id, int games)
        {
            Id = id; Games = games;
        }
    }

    public sealed class SeasonSummary
    {
        public string Format { get; }
        public int Season { get; }
        public int Games { get; }
        public int RecordedGames { get; }
        public int Wins { get; }
        public int Losses { get; }
        public int Draws { get; }
        public double WinRate { get { return RecordedGames == 0 ? 0 : (double)Wins / RecordedGames; } }
        public int PeakRating { get; }
        public int FinalRating { get; }
        public int StartRating { get; }
        public string StartTier { get; }
        public string FinalTier { get; }
        public int LongestWinStreak { get; }
        public int MissingGames { get; }
        public IReadOnlyList<SeasonStreamerSummary> TopStreamers { get; }
        public IReadOnlyList<OpponentStreamerSummary> OpponentStreamers { get; }
        public IReadOnlyList<string> Warnings { get; }

        SeasonSummary(string format, int season, int games, int peak, int final, int start,
            IReadOnlyList<RankedReplaySnapshot> matches, List<string> warnings)
        {
            Format = format;
            Season = season;
            Games = games;
            PeakRating = peak;
            FinalRating = final;
            StartRating = start;
            StartTier = RatingSystem.TierOf(start).Name;
            FinalTier = RatingSystem.TierOf(final).Name;
            RecordedGames = matches.Count;
            MissingGames = Games - RecordedGames;
            int wins = 0, losses = 0, draws = 0, streak = 0, longest = 0, previous = 0;
            var picks = new Dictionary<string, int>(StringComparer.Ordinal);
            var pickWins = new Dictionary<string, int>(StringComparer.Ordinal);
            var opponents = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var match in matches)
            {
                var tag = match.Tag;
                bool won = match.Entry.Winner == tag.PlayerSide;
                if (won) wins++;
                else if (match.Entry.Winner == -1) draws++;
                else losses++;
                // 不跨缺失局或重复编号猜测连胜，即使两边可见录像都是胜局。
                if (tag.GameNumber != (long)previous + 1) streak = 0;
                streak = won ? streak + 1 : 0;
                longest = Math.Max(longest, streak);
                previous = tag.GameNumber;
                foreach (string id in match.Record.Teams[tag.PlayerSide].Distinct(StringComparer.Ordinal))
                {
                    Increment(picks, id);
                    if (won) Increment(pickWins, id);
                }
                foreach (string id in match.Record.Teams[1 - tag.PlayerSide].Distinct(StringComparer.Ordinal)) Increment(opponents, id);
            }
            Wins = wins; Losses = losses; Draws = draws; LongestWinStreak = longest;
            TopStreamers = Array.AsReadOnly(picks.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal)
                .Take(3).Select(pair => new SeasonStreamerSummary(pair.Key, pair.Value,
                    pickWins.TryGetValue(pair.Key, out var count) ? count : 0)).ToArray());
            OpponentStreamers = Array.AsReadOnly(opponents.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new OpponentStreamerSummary(pair.Key, pair.Value)).ToArray());
            if (MissingGames > 0) warnings.Add("有 " + Number(MissingGames) + " 局录像缺失，部分统计不完整");
            Warnings = Array.AsReadOnly(warnings.Distinct(StringComparer.Ordinal).ToArray());
        }

        public static SeasonSummary Create(Profile profile, ReplayLibrary library, int? season = null,
            string format = Profile.DefaultRatingFormat)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            if (library == null) throw new ArgumentNullException(nameof(library));
            if (string.IsNullOrWhiteSpace(format)) throw new ArgumentException("排位赛制名称不能为空。", nameof(format));
            if (profile.Ratings == null) throw new FormatException("档案排位记录不能为空。");
            PlayerRating rating;
            if (profile.Ratings.TryGetValue(format, out var source))
            {
                if (source == null) throw new FormatException("档案排位记录不能为空。");
                rating = source.Clone();
            }
            else rating = new PlayerRating();
            int selected = season ?? rating.Season;
            if (selected < 1 || selected > rating.Season)
                throw new ArgumentOutOfRangeException(nameof(season), "没有所选排位赛季的档案记录。");
            int games, peak, final;
            if (selected == rating.Season)
            {
                games = rating.Games; peak = rating.PeakRating; final = rating.Rating;
            }
            else
            {
                var history = rating.History[selected - 1];
                games = history.Games; peak = history.PeakRating; final = history.FinalRating;
            }
            int start = selected == 1 ? 1500 : checked((int)Math.Round(1500.0
                + ((double)rating.History[selected - 2].FinalRating - 1500.0) / 2.0, MidpointRounding.AwayFromZero));
            var warnings = new List<string>();
            var candidates = library.ReadRankedReplays(format, selected, warnings);
            var matches = new List<RankedReplaySnapshot>();
            foreach (var group in candidates.GroupBy(item => item.Tag.GameNumber).OrderBy(item => item.Key))
            {
                if (group.Key > games)
                {
                    AddWarning(warnings, "有排位录像的局序超出档案场数，已跳过。");
                    continue;
                }
                if (group.Count() != 1)
                {
                    AddWarning(warnings, "有排位录像局序重复，重复编号的所有录像均未计入统计。");
                    continue;
                }
                var item = group.Single();
                if (item.Error != null)
                {
                    AddWarning(warnings, item.Error);
                    continue;
                }
                if (item.Record == null || item.Entry == null) continue;
                matches.Add(item);
            }
            return new SeasonSummary(format, selected, games, peak, final, start, matches.AsReadOnly(), warnings);
        }

        public string ToText()
        {
            var text = new StringBuilder("排位赛季总结 · 第 ").Append(Number(Season)).Append(" 赛季（")
                .Append(TextInput.Clean(Format, TextInputKind.TeamName).Text).AppendLine("）")
                .Append("档案场数：").Append(Number(Games)).Append("；可统计录像：").AppendLine(Number(RecordedGames))
                .Append("胜 / 负 / 平：").Append(Number(Wins)).Append(" / ").Append(Number(Losses)).Append(" / ").AppendLine(Number(Draws))
                .Append("胜率（可统计录像）：").AppendLine(Percent(WinRate))
                .Append("起始 / 最高 / 最终分：").Append(Number(StartRating)).Append(" / ").Append(Number(PeakRating))
                .Append(" / ").AppendLine(Number(FinalRating))
                .Append("段位变化：").Append(StartTier).Append(" → ").AppendLine(FinalTier)
                .Append("最长连胜（已确认连续录像）：").AppendLine(Number(LongestWinStreak))
                .AppendLine("最常用主播（原始阵容）：");
            if (TopStreamers.Count == 0) text.AppendLine("  暂无可统计录像。");
            foreach (var streamer in TopStreamers)
                text.Append("  ").Append(TextInput.Clean(streamer.Id, TextInputKind.Note).Text).Append("：")
                    .Append(Number(streamer.Games)).Append(" 局，胜率 ").AppendLine(Percent(streamer.WinRate));
            text.AppendLine("最常遇到的对手主播（原始阵容）：");
            if (OpponentStreamers.Count == 0) text.AppendLine("  暂无可统计录像。");
            foreach (var streamer in OpponentStreamers)
                text.Append("  ").Append(TextInput.Clean(streamer.Id, TextInputKind.Note).Text).Append("：")
                    .Append(Number(streamer.Games)).AppendLine(" 局");
            foreach (string warning in Warnings) text.Append("提示：").AppendLine(warning);
            return text.ToString().TrimEnd();
        }

        static void Increment(Dictionary<string, int> values, string key)
        {
            values.TryGetValue(key, out int value);
            values[key] = checked(value + 1);
        }

        static void AddWarning(List<string> warnings, string value)
        {
            if (!warnings.Contains(value)) warnings.Add(value);
        }

        static string Number(int value) { return value.ToString(CultureInfo.InvariantCulture); }
        static string Percent(double value) { return (value * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%"; }
    }
}
