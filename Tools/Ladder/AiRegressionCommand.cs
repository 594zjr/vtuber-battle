using System;
using System.Globalization;
using System.IO;
using System.Text;
using StreamDuel.Core;

namespace StreamDuel.Ladder
{
    internal static class AiRegressionCommand
    {
        private static readonly AiLevel[] Levels = { AiLevel.Easy, AiLevel.Normal, AiLevel.Hard, AiLevel.Expert };

        public static int Run(ContentDb db, Options options)
        {
            AiRegressionBaseline baseline = null;
            if (options.WriteBaseline == null)
            {
                string path = options.Baseline ?? Path.Combine(AppContext.BaseDirectory, "ai-baseline.json");
                baseline = AiRegressionBaseline.FromJson(File.ReadAllText(path));
                if (baseline.Result.PackId != db.PackId || baseline.Result.GamesPerPair != options.Games || baseline.Result.Seed != options.Seed
                    || baseline.Result.MaxTurns != Battle.TurnCap || baseline.Result.SamplingVersion != AiRegression.SamplingVersion)
                    throw new ArgumentException("内容包、每对局数、种子、回合上限或采样方案与基准不一致；请使用对应参数及基准文件，或用 --write-baseline 生成候选基准。");
            }

            AiLadderResult result = AiRegression.Run(db, options.Games, options.Seed);
            Print(result);
            if (baseline == null)
            {
                baseline = AiRegressionBaseline.FromResult(result);
                string path = Path.GetFullPath(options.WriteBaseline);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, baseline.ToJson(), new UTF8Encoding(false));
                Console.WriteLine("已写入实际测得的候选基准；允许下降 " + Percentage(baseline.AllowedDrop) + " 个百分点，须审查后再启用。");
            }
            else if (baseline.Result.ContentHash != result.ContentHash)
            {
                Console.WriteLine("内容指纹已变化，仍按相同方案比较。");
            }

            var failures = baseline.Check(result);
            foreach (string failure in failures) Console.Error.WriteLine("失败：" + failure);
            if (failures.Count > 0)
            {
                Console.Error.WriteLine("电脑强度回归未通过，共 " + failures.Count + " 项。");
                return 1;
            }

            Console.WriteLine("电脑强度回归通过。");
            return 0;
        }

        private static void Print(AiLadderResult result)
        {
            Console.WriteLine("电脑强度回归：" + result.PackId + "；每对 " + result.GamesPerPair + " 局；种子 " + result.Seed
                + "；每局最多 " + result.MaxTurns + " 回合；内容指纹 " + result.ContentHash);
            Console.WriteLine("胜率 = 胜场 / 总局数，平局及未完成对局不计胜场。行对列；同档未测试，以 — 表示。");
            Console.WriteLine("| 难度 | 简单 | 普通 | 困难 | 专家 |");
            Console.WriteLine("|---|---:|---:|---:|---:|");
            foreach (AiLevel row in Levels)
            {
                var line = new StringBuilder("| " + Name(row) + " |");
                foreach (AiLevel opponent in Levels)
                    line.Append(" ").Append(row == opponent ? "—" : Percentage(result.WinRate(row, opponent)) + "%").Append(" |");
                Console.WriteLine(line);
            }

            Console.WriteLine("| 高难度 / 低难度 | 高难度胜 | 低难度胜 | 平局 | 未完成 | 高难度胜率 |");
            Console.WriteLine("|---|---:|---:|---:|---:|---:|");
            foreach (AiMatchupResult matchup in result.Matchups)
                Console.WriteLine("| " + Name(matchup.Higher) + " / " + Name(matchup.Lower) + " | " + matchup.HigherWins
                    + " | " + matchup.LowerWins + " | " + matchup.Draws + " | " + matchup.Truncated + " | " + Percentage(matchup.HigherWinRate) + "% |");
            Console.WriteLine("未完成数量包含在平局中，且任何未完成对局都会使回归检查失败；正常达到规则回合上限的平局不算未完成。");
        }

        private static string Percentage(double value)
        {
            return (value * 100).ToString("F1", CultureInfo.InvariantCulture);
        }

        private static string Name(AiLevel level)
        {
            switch (level)
            {
                case AiLevel.Easy: return "简单";
                case AiLevel.Normal: return "普通";
                case AiLevel.Hard: return "困难";
                case AiLevel.Expert: return "专家";
                default: throw new ArgumentOutOfRangeException(nameof(level));
            }
        }
    }
}
