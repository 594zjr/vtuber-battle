using System;
using System.Collections.Generic;
using System.Globalization;

namespace StreamDuel.Core
{
    public sealed class PlayerRating
    {
        public int Rating = 1500;
        public int Games;

        public string ToJson()
        {
            if (Games < 0) throw new FormatException("排位对局数必须是非负整数。");
            return "{\"games\":" + Games.ToString(CultureInfo.InvariantCulture)
                + ",\"rating\":" + Rating.ToString(CultureInfo.InvariantCulture) + "}";
        }

        public static PlayerRating FromJson(string json)
        {
            return ReadValue(MiniJson.Parse(json));
        }

        internal static PlayerRating ReadValue(object value)
        {
            if (!(value is Dictionary<string, object> source)) throw new FormatException("排位分记录必须是对象。");
            return new PlayerRating
            {
                Rating = source.TryGetValue("rating", out var rating) ? ReadInteger(rating, "rating", int.MinValue) : 1500,
                Games = source.TryGetValue("games", out var games) ? ReadInteger(games, "games", 0) : 0
            };
        }

        private static int ReadInteger(object value, string name, int minimum)
        {
            if (!(value is double number) || double.IsNaN(number) || double.IsInfinity(number)
                || number < minimum || number > int.MaxValue || number != Math.Truncate(number))
                throw new FormatException("排位分字段「" + name + "」必须是范围内的整数。");
            return (int)number;
        }
    }

    public sealed class RatingTier
    {
        public string Name { get; }
        public int MinRating { get; }

        internal RatingTier(string name, int minRating)
        {
            Name = name;
            MinRating = minRating;
        }
    }

    public static class RatingSystem
    {
        public static IReadOnlyList<RatingTier> Tiers { get; } = Array.AsReadOnly(new[]
        {
            new RatingTier("初声开播", 0),
            new RatingTier("暖场新星", 1200),
            new RatingTier("人气聚光", 1500),
            new RatingTier("全场焦点", 1800),
            new RatingTier("巅峰主场", 2100)
        });

        public static RatingTier TierOf(int rating)
        {
            var tier = Tiers[0];
            for (int i = 1; i < Tiers.Count && rating >= Tiers[i].MinRating; i++) tier = Tiers[i];
            return tier;
        }

        public static void Apply(PlayerRating a, PlayerRating b, double scoreA)
        {
            if (a == null) throw new ArgumentNullException(nameof(a));
            if (b == null) throw new ArgumentNullException(nameof(b));
            if (ReferenceEquals(a, b)) throw new ArgumentException("双方排位分不能引用同一个对象。");
            if (scoreA != 0 && scoreA != 0.5 && scoreA != 1)
                throw new ArgumentOutOfRangeException(nameof(scoreA), "比赛结果只能是 0、0.5 或 1。");
            if (a.Games < 0) throw new ArgumentOutOfRangeException(nameof(a), "本方排位对局数不能为负。");
            if (b.Games < 0) throw new ArgumentOutOfRangeException(nameof(b), "对方排位对局数不能为负。");

            double expectedA = 1 / (1 + Math.Pow(10, ((double)b.Rating - a.Rating) / 400));
            double difference = scoreA - expectedA;
            int kA = a.Games < 10 ? 40 : 24;
            int kB = b.Games < 10 ? 40 : 24;
            int deltaA = (int)Math.Round(kA * difference, MidpointRounding.AwayFromZero);
            // 共用赛前期望值；同 K 直接取反，避免两个浮点表达式在舍入边界破坏总分守恒。
            int deltaB = kA == kB ? -deltaA : (int)Math.Round(-kB * difference, MidpointRounding.AwayFromZero);
            int ratingA = checked(a.Rating + deltaA);
            int ratingB = checked(b.Rating + deltaB);
            int gamesA = checked(a.Games + 1);
            int gamesB = checked(b.Games + 1);
            // 不截断负分，且所有溢出检查先于写入，失败不会只更新其中一方。
            a.Rating = ratingA;
            b.Rating = ratingB;
            a.Games = gamesA;
            b.Games = gamesB;
        }
    }
}
