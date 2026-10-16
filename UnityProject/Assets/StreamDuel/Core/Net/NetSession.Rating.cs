using System;
using System.Collections.Generic;
using System.Globalization;

namespace StreamDuel.Core
{
    public sealed partial class NetSession
    {
        private PlayerRating opponentRating;

        public PlayerRating OpponentRating
        {
            get
            {
                return opponentRating == null ? null : new PlayerRating
                {
                    Rating = opponentRating.Rating,
                    Games = opponentRating.Games
                };
            }
        }

        private static string WriteRatingHello(PlayerRating rating)
        {
            if (rating == null) return "";
            if (rating.Games < 0) throw new ArgumentOutOfRangeException(nameof(rating), "排位场数不能为负数。");
            // 分数由双方自报，仅用于本机显示，不防作弊，也不参与规则、随机流或状态哈希。
            // 历史、峰值和本机赛季不属于握手元数据，保持原协议的两字段投影。
            return ",\"rating\":{\"games\":" + rating.Games.ToString(CultureInfo.InvariantCulture)
                + ",\"rating\":" + rating.Rating.ToString(CultureInfo.InvariantCulture) + "}";
        }

        private static PlayerRating ReadRatingHello(Dictionary<string, object> hello)
        {
            // 兼容同一协议内未发送可选评级的客户端，按新档案的初始分数结算。
            if (!hello.TryGetValue("rating", out var value)) return new PlayerRating();
            if (!(value is Dictionary<string, object> source)) throw new FormatException("握手排位信息必须是对象。");
            return new PlayerRating
            {
                Rating = (int)NetJson.Integer(source, "rating", int.MinValue, int.MaxValue),
                Games = (int)NetJson.Integer(source, "games", 0, int.MaxValue)
            };
        }
    }
}
