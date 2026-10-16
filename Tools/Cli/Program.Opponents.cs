using System;
using System.Linq;
using StreamDuel.Core;

namespace StreamDuel.Cli
{
    internal static partial class Program
    {
        private static AiOpponent SelectOpponent(ContentDb db, CliOptions options)
        {
            if (options.OpponentId == null) return null;
            var opponent = AiOpponents.Find(db, options.OpponentId);
            if (opponent == null)
                throw new ArgumentException("当前内容包「" + db.PackId + "」没有对手「" + options.OpponentId
                    + "」；使用 --pack " + db.PackId + " --vs list 查看名单。");
            if (options.LevelSpecified && options.Level != opponent.Level)
                throw new ArgumentException("对手「" + TextInput.Clean(opponent.Name, TextInputKind.PlayerName).Text
                    + "」的固定难度为" + LevelName(opponent.Level)
                    + "，与 --level 指定的" + LevelName(options.Level) + "不一致；请省略 --level 或选择相同难度。");
            options.Level = opponent.Level;
            return opponent;
        }

        private static void PrintOpponents(ContentDb db)
        {
            var opponents = AiOpponents.For(db);
            Console.WriteLine("内容包：" + db.PackName + "（" + db.PackId + "）");
            Console.WriteLine(db.PackNotice);
            if (opponents.Count == 0)
            {
                Console.WriteLine("当前内容包没有预设电脑对手。");
                return;
            }
            foreach (var opponent in opponents)
            {
                Console.WriteLine(opponent.Id + "：" + TextInput.Clean(opponent.Name, TextInputKind.PlayerName).Text
                    + "〔" + LevelName(opponent.Level) + "；" + OpponentStyleName(opponent.Style) + "〕");
                Console.WriteLine("  简介：" + TextInput.Clean(opponent.Blurb, TextInputKind.Note).Text);
                Console.WriteLine("  阵容：" + string.Join("；", opponent.Team.Select(id =>
                    db.Streamers.First(streamer => streamer.Id == id).Name + "（" + id + "）")));
            }
            Console.WriteLine("使用 --pack " + db.PackId + " --vs <对手id> 开始对战。");
        }

        private static void PrintOpponent(AiOpponent opponent)
        {
            Console.WriteLine("陪练对手：" + TextInput.Clean(opponent.Name, TextInputKind.PlayerName).Text
                + "（" + opponent.Id + "）");
            Console.WriteLine("出招风格：" + OpponentStyleName(opponent.Style));
            Console.WriteLine("简介：" + TextInput.Clean(opponent.Blurb, TextInputKind.Note).Text);
        }

        private static string OpponentStyleName(string style)
        {
            switch (style)
            {
                case "aggressive": return "猛攻";
                case "defensive": return "稳守";
                case "tricky": return "花招";
                case null: return "沿用主播风格";
                default: return "专属风格（" + style + "）";
            }
        }
    }
}
