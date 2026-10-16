using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using StreamDuel.Core;

namespace StreamDuel.Cli
{
    internal static partial class Program
    {
        private static int PlayRuleset(ContentDb db, CliOptions options, TeamPreset imported)
        {
            var ruleset = Rulesets.All.FirstOrDefault(item => string.Equals(item.Id, options.RulesetId, StringComparison.Ordinal));
            if (ruleset == null)
                throw new ArgumentException("未知赛制：" + options.RulesetId + "；可用赛制：" + string.Join("、", Rulesets.All.Select(item => item.Id)) + "。");
            ruleset.Validate(db);
            if (options.Advice && !ruleset.BanOne && !ruleset.LeadPick)
                throw new ArgumentException(Loc.Current.T("advice.cli.phase.required"));
            var preset = PrepareTeam(db, options, imported, ruleset.Season);
            var player = preset.Ids.ToArray();
            var opponent = PrepareOpponent(db, player, options);
            // 保留旧的对手选择顺序；赛季限制使该队无效时再交给引擎重新组队。
            if (ruleset.CheckTeam(db, opponent).Count > 0)
            {
                if (options.TeamB != null)
                    throw new ArgumentException("对方队伍不符合赛制：" + string.Join("；", ruleset.CheckTeam(db, opponent)));
                opponent = TeamBuilder.Suggest(db, ruleset.Season, new string[0], unchecked(options.Seed + 1)).ToArray();
            }
            var match = new Match(db, ruleset, new IList<string>[] { player, opponent }, new[] { "你", "电脑" }, options.Seed);
            var completed = new List<Battle>();
            // 超时阶段可能仍有未完成的 ReadLine；整场复用读取器，下一局不能另开读者争抢输入。
            var input = new ConsoleInput();
            int exitCode = 0;

            if (options.ExportTeam) Console.WriteLine("本方队伍码：" + TeamCode.Encode(preset));
            Console.WriteLine("开播对决 · 赛制对战");
            Console.WriteLine("内容包：" + db.PackName);
            Console.WriteLine(db.PackNotice);
            Console.WriteLine("赛制：" + ruleset.Name + "（" + ruleset.Id + "）；最多 " + ruleset.BestOf + " 局，先赢 " + (ruleset.BestOf / 2 + 1) + " 局结束");
            Console.WriteLine("种子：" + options.Seed + "；电脑难度：" + LevelName(options.Level)
                + "；模式：" + (options.Auto ? "双方自动" : "手动"));
            if (ruleset.Season != null) Console.WriteLine("赛季：" + ruleset.Season);
            if (ruleset.TurnSeconds > 0 || ruleset.BankSeconds > 0)
                Console.WriteLine("计时配置：每阶段基本 " + ruleset.TurnSeconds + " 秒，每方总时间 " + ruleset.BankSeconds
                    + " 秒；禁用与首发不计时，每局重置预算。");

            while (!match.Over)
            {
                var battle = match.StartNextGame();
                Console.WriteLine();
                Console.WriteLine("===== 赛制第 " + (completed.Count + 1) + " 局 · 种子 " + battle.Seed + " =====");
                input.DiscardCompleted(preserveCommands: true);
                var clock = ruleset.TurnSeconds > 0 || ruleset.BankSeconds > 0
                    ? new TurnClock(ruleset.TurnSeconds, ruleset.BankSeconds) : null;
                if (!PlayLocalBattle(battle, options, clock, input))
                {
                    Quit();
                    Console.WriteLine("赛制未完成；已完成 " + completed.Count + " 局的回放与战绩保留，未完成局不计入比分。");
                    return exitCode;
                }
                match.ReportResult(battle);
                completed.Add(battle);
                try
                {
                    if (SaveCompletedBattle(battle, options, 0, exportReport: false) != 0) exitCode = 1;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is FormatException)
                {
                    // 单局档案失败不能中断整场比赛，也不能阻止最终战报保存。
                    Console.Error.WriteLine("错误：第 " + completed.Count + " 局档案保存失败：" + ex.Message);
                    exitCode = 1;
                }
                Console.WriteLine("赛制比分：你 " + match.Wins[0] + " : " + match.Wins[1] + " 电脑；平局 " + match.Draws);
            }

            Console.WriteLine("赛制结束：共 " + completed.Count + " 局；你 " + match.Wins[0] + " : " + match.Wins[1] + " 电脑；平局 " + match.Draws);
            SaveRulesetReport(ruleset, match, completed, options);
            return exitCode;
        }

        private static void SaveRulesetReport(Ruleset ruleset, Match match, IList<Battle> completed, CliOptions options)
        {
            string path = ValidateReportPath(options);
            if (path == null) return;
            var markdown = new StringBuilder();
            markdown.Append("# 赛制战报\n\n赛制：").Append(ruleset.Name).Append("（").Append(ruleset.Id).Append("）\n\n")
                .Append("种子：").Append(options.Seed).Append("；完成局数：").Append(completed.Count).Append('\n')
                .Append("\n比分：你 ").Append(match.Wins[0]).Append(" : ").Append(match.Wins[1]).Append(" 电脑；平局 ").Append(match.Draws).Append("\n");
            for (int index = 0; index < completed.Count; index++)
                markdown.Append("\n---\n\n## 第 ").Append(index + 1).Append(" 局\n\n")
                    .Append(BattleReport.Markdown(TextInput.ForDisplay(completed[index])));
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, markdown.ToString(), new UTF8Encoding(false));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                throw new IOException("赛制战报保存失败：" + ex.Message, ex);
            }
            Console.WriteLine("赛制战报已保存：" + options.ReportFile);
        }
    }
}
