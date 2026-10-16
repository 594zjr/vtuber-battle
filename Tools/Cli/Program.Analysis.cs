using System;
using System.Globalization;
using System.Linq;
using StreamDuel.Core;

namespace StreamDuel.Cli
{
    internal static partial class Program
    {
        private static int PrintAnalysis(ContentDb db, CliOptions options)
        {
            if (options.CalcAttacker != null)
            {
                var preview = DamageCalc.Range(db, options.CalcAttacker, options.CalcSkillIndex, options.CalcDefender);
                var attacker = db.Streamer(options.CalcAttacker);
                var defender = db.Streamer(options.CalcDefender);
                string skillId = options.CalcSkillIndex < attacker.Skills.Count
                    ? attacker.Skills[options.CalcSkillIndex] : attacker.Fifth;
                Console.WriteLine("开播对决 · 伤害计算器");
                PrintAnalysisPack(db);
                Console.WriteLine("攻击方：" + attacker.Name + "（" + attacker.Id + "）");
                Console.WriteLine("技能：" + (options.CalcSkillIndex + 1) + " · " + db.Skills[skillId].Name
                    + (options.CalcSkillIndex == 4 ? "〔第五技能〕" : ""));
                Console.WriteLine("防守方：" + defender.Name + "（" + defender.Id + "）");
                Console.WriteLine("预览伤害：" + preview.Min + "～" + preview.Max);
                Console.WriteLine("赛道倍率：×" + preview.Eff.ToString("0.##", CultureInfo.InvariantCulture));
                foreach (string note in preview.Notes) Console.WriteLine("  " + note);
                Console.WriteLine("双方各一人首发，不含后排支援；按攻击方先手且命中预览，不含暴击。");
                Console.WriteLine("状态技能预览为 0，不表示其效果无效。");
                return 0;
            }

            var matrix = DamageCalc.Matchup(db, options.MatchupTeamA, options.MatchupTeamB);
            Console.WriteLine("开播对决 · 阵容对位表");
            PrintAnalysisPack(db);
            Console.WriteLine("A 队（行，攻击方）：" + string.Join("；", options.MatchupTeamA.Select((id, index) =>
                "A" + (index + 1) + " " + db.Streamer(id).Name + "（" + id + "）")));
            Console.WriteLine("B 队（列，防守方）：" + string.Join("；", options.MatchupTeamB.Select((id, index) =>
                "B" + (index + 1) + " " + db.Streamer(id).Name + "（" + id + "）")));
            Console.WriteLine("数值为各对位最高技能平均伤害占防守方最大人气的百分比，四舍五入，可超过 100%。");
            Console.WriteLine("每格重选双方首发、保留其余后排，按攻击方先手且命中时的可用技能预览，不含暴击；不代表整局胜率。");
            int width = 5;
            foreach (int value in matrix) width = Math.Max(width, value.ToString(CultureInfo.InvariantCulture).Length + 2);
            Console.Write("A/B".PadRight(5));
            for (int column = 0; column < matrix.GetLength(1); column++)
                Console.Write(("B" + (column + 1)).PadLeft(width));
            Console.WriteLine();
            for (int row = 0; row < matrix.GetLength(0); row++)
            {
                Console.Write(("A" + (row + 1)).PadRight(5));
                for (int column = 0; column < matrix.GetLength(1); column++)
                    Console.Write((matrix[row, column].ToString(CultureInfo.InvariantCulture) + "%").PadLeft(width));
                Console.WriteLine();
            }
            return 0;
        }

        private static void PrintAnalysisPack(ContentDb db)
        {
            Console.WriteLine("内容包：" + db.PackName);
            Console.WriteLine(db.PackNotice);
        }
    }
}
