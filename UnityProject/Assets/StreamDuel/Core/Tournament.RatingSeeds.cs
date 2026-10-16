using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace StreamDuel.Core
{
    public enum TournamentSeedMode
    {
        Default,
        ByRating
    }

    public sealed class TournamentRatingSnapshot
    {
        public int Player { get; }
        public int Rating { get; }
        public int Games { get; }
        public int Season { get; }
        public AiLevel? ComputerLevel { get; }

        internal TournamentRatingSnapshot(int player, int rating, int games, int season, AiLevel? computerLevel)
        {
            Player = player;
            Rating = rating;
            Games = games;
            Season = season;
            ComputerLevel = computerLevel;
        }
    }

    public sealed partial class Tournament
    {
        readonly List<TournamentRatingSnapshot> ratingSeeds = new List<TournamentRatingSnapshot>();

        public TournamentSeedMode SeedMode { get; private set; }
        public IReadOnlyList<TournamentRatingSnapshot> RatingSeeds { get { return ratingSeeds.AsReadOnly(); } }
        public IReadOnlyList<int> EffectiveSeedOrder
        {
            get
            {
                return Array.AsReadOnly(SeedMode == TournamentSeedMode.ByRating ? RatingSeedOrder()
                    : IsElimination ? EffectiveEliminationSeeds(Round > 0) : Enumerable.Range(0, players.Count).ToArray());
            }
        }

        void InitializeRatingSeeding(TournamentOptions options)
        {
            SeedMode = options == null ? TournamentSeedMode.Default : options.SeedMode;
            if (SeedMode != TournamentSeedMode.Default && SeedMode != TournamentSeedMode.ByRating)
                throw new ArgumentException("赛事种子模式无效。", nameof(options));
            if (SeedMode == TournamentSeedMode.ByRating && (options.Seed.HasValue || options.SeedOrder != null))
                throw new ArgumentException("排位种子不能与随机种子或指定种子顺序同时使用。", nameof(options));
        }

        int[] RatingSeedOrder()
        {
            return ratingSeeds.OrderByDescending(item => item.Rating).ThenByDescending(item => item.Games)
                .ThenBy(item => item.Player).Select(item => item.Player).ToArray();
        }

        TournamentRatingSnapshot CaptureRating(Profile profile, AiLevel? computerLevel)
        {
            if (computerLevel.HasValue && computerLevel != AiLevel.Normal && computerLevel != AiLevel.Hard
                && computerLevel != AiLevel.Expert)
                throw new ArgumentException("赛事电脑难度只支持普通、困难或专家。", nameof(computerLevel));
            if (SeedMode != TournamentSeedMode.ByRating) return null;

            var rating = new PlayerRating();
            if (profile != null)
            {
                if (profile.Ratings == null) throw new FormatException("档案的排位数据不能为空。");
                if (profile.Ratings.TryGetValue(Profile.DefaultRatingFormat, out var saved))
                {
                    if (saved == null) throw new FormatException("档案的默认排位数据不能为空。");
                    // 报名之后的赛季切换或排位更新不能改变本届赛事的种子。
                    rating = saved.Clone();
                }
            }
            else if (computerLevel.HasValue)
            {
                rating.Rating = computerLevel == AiLevel.Normal ? 1400 : computerLevel == AiLevel.Hard ? 1600 : 1800;
            }
            return new TournamentRatingSnapshot(players.Count, rating.Rating, rating.Games, rating.Season, computerLevel);
        }

        void WriteRatingConfiguration(StringBuilder text)
        {
            text.Append(",\"seedBy\":\"rating\",\"ratingSeeds\":[");
            for (int i = 0; i < ratingSeeds.Count; i++)
            {
                if (i > 0) text.Append(',');
                var item = ratingSeeds[i];
                text.Append("{\"player\":").Append(Number(item.Player)).Append(",\"rating\":").Append(Number(item.Rating))
                    .Append(",\"games\":").Append(Number(item.Games)).Append(",\"season\":").Append(Number(item.Season))
                    .Append(",\"computerLevel\":");
                if (!item.ComputerLevel.HasValue) text.Append("null");
                else TeamPresetJson.WriteString(text, item.ComputerLevel == AiLevel.Normal ? "normal"
                    : item.ComputerLevel == AiLevel.Hard ? "hard" : "expert");
                text.Append('}');
            }
            text.Append(']');
        }

        void RestoreRatingSeeds(Dictionary<string, object> root)
        {
            var values = TournamentJson.Array(root, "ratingSeeds");
            if (values.Count != players.Count) throw new FormatException("排位种子快照必须覆盖全部报名选手。");
            var snapshots = new List<TournamentRatingSnapshot>();
            foreach (object value in values)
            {
                var item = TournamentJson.Object(value);
                TournamentJson.Keys(item, "player", "rating", "games", "season", "computerLevel");
                int player = TournamentJson.Integer(item, "player", 0, int.MaxValue);
                if (player != snapshots.Count) throw new FormatException("排位种子快照必须按报名编号保存，不能重复或乱序。");
                AiLevel? computerLevel;
                switch (item["computerLevel"])
                {
                    case null: computerLevel = null; break;
                    case "normal": computerLevel = AiLevel.Normal; break;
                    case "hard": computerLevel = AiLevel.Hard; break;
                    case "expert": computerLevel = AiLevel.Expert; break;
                    default: throw new FormatException("排位种子电脑难度必须为 normal、hard、expert 或 null。");
                }
                snapshots.Add(new TournamentRatingSnapshot(player,
                    TournamentJson.Integer(item, "rating", int.MinValue, int.MaxValue),
                    TournamentJson.Integer(item, "games", 0, int.MaxValue),
                    TournamentJson.Integer(item, "season", 1, int.MaxValue), computerLevel));
            }
            // 只恢复存档中的报名时快照，排轮过程不读取后来发生变化的玩家档案。
            ratingSeeds.Clear();
            ratingSeeds.AddRange(snapshots);
        }
    }
}
