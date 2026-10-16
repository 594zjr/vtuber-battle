using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace StreamDuel.Core
{
    public sealed class RatingSeasonHistory
    {
        public int Season { get; }
        public int FinalRating { get; }
        public int PeakRating { get; }
        public string Tier { get; }
        public int Games { get; }

        internal RatingSeasonHistory(int season, int finalRating, int peakRating, string tier, int games)
        {
            Season = season;
            FinalRating = finalRating;
            PeakRating = peakRating;
            Tier = tier;
            Games = games;
        }

        internal RatingSeasonHistory Clone()
        {
            return new RatingSeasonHistory(Season, FinalRating, PeakRating, Tier, Games);
        }
    }

    public sealed partial class PlayerRating
    {
        int? peakRating;
        bool placed;
        IReadOnlyList<RatingSeasonHistory> history = Array.AsReadOnly(Array.Empty<RatingSeasonHistory>());

        public int Season = 1;
        public int PeakRating { get { return peakRating ?? Rating; } }
        public bool IsPlaced { get { return placed || Games >= 10; } }
        public IReadOnlyList<RatingSeasonHistory> History { get { return history; } }

        public PlayerRating Clone()
        {
            ValidateSeasonState();
            return new PlayerRating
            {
                Rating = Rating,
                Games = Games,
                Season = Season,
                peakRating = peakRating,
                placed = placed,
                history = Array.AsReadOnly(history.Select(item => item.Clone()).ToArray())
            };
        }

        internal void ValidateSeasonState()
        {
            if (Games < 0) throw new FormatException("排位对局数必须是非负整数。");
            if (Season < 1) throw new FormatException("排位赛季必须从 1 开始。");
            if (PeakRating < Rating) throw new FormatException("本赛季最高分不能低于当前分数。");
            if (history == null || history.Count != (long)Season - 1)
                throw new FormatException("排位历史必须完整覆盖第 1 赛季至上一赛季。");
            bool qualified = Games >= 10;
            for (int i = 0; i < history.Count; i++)
            {
                var item = history[i];
                if (item == null || item.Season != i + 1)
                    throw new FormatException("排位历史赛季必须连续且不能重复或乱序。");
                if (item.Games < 0) throw new FormatException("历史排位场数必须是非负整数。");
                if (item.PeakRating < item.FinalRating)
                    throw new FormatException("历史最高分不能低于该赛季最终分数。");
                if (item.Tier != RatingSystem.TierOf(item.FinalRating).Name)
                    throw new FormatException("历史段位必须与该赛季最终分数一致。");
                qualified |= item.Games >= 10;
            }
            if (IsPlaced != qualified) throw new FormatException("已定级资格必须与当前或历史赛季的已完成场数一致。");
        }

        internal void CommitRating(int rating, int games, int peak, bool isPlaced)
        {
            Rating = rating;
            Games = games;
            peakRating = peak;
            placed = isPlaced;
        }

        internal void CommitSeason(int season, int rating, bool isPlaced, IReadOnlyList<RatingSeasonHistory> previous)
        {
            Season = season;
            Rating = rating;
            Games = 0;
            peakRating = rating;
            placed = isPlaced;
            history = previous;
        }

        string WriteSeasonJson()
        {
            var text = new StringBuilder("{\"games\":").Append(Games.ToString(CultureInfo.InvariantCulture))
                .Append(",\"rating\":").Append(Rating.ToString(CultureInfo.InvariantCulture));
            if (Season != 1) text.Append(",\"season\":").Append(Season.ToString(CultureInfo.InvariantCulture));
            if (PeakRating != Rating) text.Append(",\"peakRating\":").Append(PeakRating.ToString(CultureInfo.InvariantCulture));
            if (IsPlaced != (Games >= 10)) text.Append(",\"isPlaced\":").Append(IsPlaced ? "true" : "false");
            if (history.Count > 0)
            {
                text.Append(",\"history\":[");
                for (int i = 0; i < history.Count; i++)
                {
                    if (i > 0) text.Append(',');
                    var item = history[i];
                    text.Append("{\"season\":").Append(item.Season.ToString(CultureInfo.InvariantCulture))
                        .Append(",\"finalRating\":").Append(item.FinalRating.ToString(CultureInfo.InvariantCulture))
                        .Append(",\"peakRating\":").Append(item.PeakRating.ToString(CultureInfo.InvariantCulture))
                        .Append(",\"tier\":");
                    TeamPresetJson.WriteString(text, item.Tier);
                    text.Append(",\"games\":").Append(item.Games.ToString(CultureInfo.InvariantCulture)).Append('}');
                }
                text.Append(']');
            }
            return text.Append('}').ToString();
        }

        void ReadSeasonMetadata(Dictionary<string, object> source)
        {
            if (source.TryGetValue("season", out var season)) Season = ReadInteger(season, "season", 1);
            if (source.TryGetValue("peakRating", out var peak)) peakRating = ReadInteger(peak, "peakRating", int.MinValue);
            if (source.TryGetValue("history", out var previous))
            {
                if (!(previous is List<object> values)) throw new FormatException("排位历史必须是数组。");
                var entries = new List<RatingSeasonHistory>();
                foreach (object value in values)
                {
                    if (!(value is Dictionary<string, object> item)) throw new FormatException("历史赛季记录必须是对象。");
                    string[] keys = { "season", "finalRating", "peakRating", "tier", "games" };
                    if (item.Count != keys.Length || keys.Any(key => !item.ContainsKey(key)))
                        throw new FormatException("历史赛季字段缺失或包含不支持的字段。");
                    if (!(item["tier"] is string tier)) throw new FormatException("历史段位必须是文字。");
                    entries.Add(new RatingSeasonHistory(ReadInteger(item["season"], "season", 1),
                        ReadInteger(item["finalRating"], "finalRating", int.MinValue),
                        ReadInteger(item["peakRating"], "peakRating", int.MinValue), tier,
                        ReadInteger(item["games"], "games", 0)));
                }
                history = Array.AsReadOnly(entries.ToArray());
            }
            bool qualified = Games >= 10 || history.Any(item => item.Games >= 10);
            if (source.TryGetValue("isPlaced", out var placement))
            {
                if (!(placement is bool flag)) throw new FormatException("已定级标记必须是布尔值。");
                if (flag != qualified) throw new FormatException("已定级标记与当前或历史场数不一致。");
                placed = flag;
            }
            else placed = qualified;
        }
    }

    public static partial class RatingSystem
    {
        public static void NewSeason(PlayerRating rating)
        {
            if (rating == null) throw new ArgumentNullException(nameof(rating));
            int season = checked(rating.Season + 1);
            rating.ValidateSeasonState();
            // 对完整公式舍入，不能先除整数差值或先舍入变化量，尤其会改变低分和负分边界。
            int next = checked((int)Math.Round(1500.0 + ((double)rating.Rating - 1500.0) / 2.0,
                MidpointRounding.AwayFromZero));
            bool placed = rating.IsPlaced;
            var history = rating.History.Select(item => item.Clone()).ToList();
            history.Add(new RatingSeasonHistory(rating.Season, rating.Rating, rating.PeakRating,
                TierOf(rating.Rating).Name, rating.Games));
            var snapshot = Array.AsReadOnly(history.ToArray());
            // 校验、计算和分配全部先完成，任何失败都不能留下半个已切换赛季。
            rating.CommitSeason(season, next, placed, snapshot);
        }
    }
}
