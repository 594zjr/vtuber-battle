using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using StreamDuel.Core;

namespace StreamDuel.Cli
{
    internal static partial class Program
    {
        private static int PlayNetworkRuleset(ContentDb db, IList<string> team, Ruleset ruleset, CliOptions options)
        {
            var rating = ProfileStore.LoadRating(options.SaveDir);
            bool host = options.HostPort.HasValue;
            Console.WriteLine("开播对决 · 联网赛制对战（仅用于可信局域网，无加密、无账号）");
            Console.WriteLine("内容包：" + db.PackName);
            Console.WriteLine(db.PackNotice);
            Console.WriteLine("赛制：" + ruleset.Name + "（" + ruleset.Id + "）；最多 " + ruleset.BestOf + " 局；双方须选择相同赛制。");
            Console.WriteLine("整场双方确认并正常收尾后保存各局，排位仅按整场胜负结算一次；中断或不同步不计分。");
            if (ruleset.Id == "casual") Console.WriteLine("休闲单局使用旧协议兼容模式，可连接未指定赛制的旧端。");
            TcpTransport connection;
            if (host)
            {
                connection = WaitForNetworkGuest(db, options, ruleset: ruleset);
                if (connection == null) return Quit();
            }
            else
            {
                Console.WriteLine("正在连接房主……");
                connection = TcpTransport.Connect(options.JoinHost, options.JoinPort, 10000);
            }

            using (connection)
            {
                var session = new NetSession(db, connection, host, team, host ? "房主" : "客机", host ? options.Seed : (uint?)null,
                    enableTermination: true, rating: rating, ruleset: ruleset, legacyCasual: ruleset.Id == "casual");
                CheckNetwork(session);
                Console.WriteLine("等待对手确认完整赛制……");
                var handshake = Stopwatch.StartNew();
                while (session.Battle == null || session.State == NetState.Handshake)
                {
                    session.Poll();
                    CheckNetwork(session);
                    if (session.Battle != null && session.State != NetState.Handshake) break;
                    if (handshake.ElapsedMilliseconds >= 10000) throw new IOException("等待对手确认赛制超时，整场未保存。");
                    Thread.Sleep(10);
                }
                ValidateRandomNetworkTeams(db, session.Battle, options);
                Console.WriteLine("连接成功；本方：" + session.DisplayNames[session.MySide] + "；房主种子：" + session.Battle.Seed);
                Console.WriteLine("本方自报排位分：" + rating.Rating + "；对手自报排位分：" + session.OpponentRating.Rating
                    + "（仅用于本机显示，无防作弊）");
                Console.WriteLine("赛制指纹：" + session.RulesetHash);
                PrintTerminationCommands(network: true);
                var input = new ConsoleInput();
                var pump = new NetworkPump(session);
                var completed = new List<Battle>();
                Battle battle = null;
                int gameNumber = 0, logIndex = 0, hashEventCount = -1, awaitingDrawResponse = -1;
                bool waitingPrinted = false, drawNotified = false, terminalCommand = false;
                ChoiceStamp? inputStage = null;
                Action poll = () =>
                {
                    pump.Pump();
                    ShowNetworkDraw(session, options.Auto, ref drawNotified);
                };
                Func<string, bool> command = line => HandleNetworkCommand(session, line,
                    () => terminalCommand = true, () => awaitingDrawResponse = session.Battle.Record.Events.Count);

                while (true)
                {
                    poll();
                    if (session.GameNumber != gameNumber || session.State == NetState.Over || session.Battle.Phase == Phase.Over)
                    {
                        // 协议可能在同一次 Poll 内确认上一局并开启下一局，按冻结结果补齐尾部日志。
                        var results = session.GameResults;
                        for (int index = completed.Count; index < results.Count; index++)
                        {
                            var result = results[index];
                            var finished = result.Battle;
                            int finishedLog = result.Number == gameNumber ? logIndex : 0;
                            PrintNewLog(finished, ref finishedLog);
                            if (result.Number == gameNumber) logIndex = finishedLog;
                            completed.Add(finished);
                            Console.WriteLine("第 " + result.Number + " 局最终状态哈希：" + result.StateHash.ToString("x16", CultureInfo.InvariantCulture));
                            Console.WriteLine("已确认第 " + result.Number + " 局：" + (result.Winner < 0 ? "平局"
                                : session.DisplayNames[result.Winner] + " 获胜") + "；种子：" + result.Seed);
                        }
                    }
                    if (session.State == NetState.Over) break;
                    if (!ReferenceEquals(battle, session.Battle))
                    {
                        battle = session.Battle;
                        gameNumber = session.GameNumber;
                        logIndex = 0;
                        hashEventCount = -1;
                        awaitingDrawResponse = -1;
                        waitingPrinted = drawNotified = terminalCommand = false;
                        inputStage = null;
                        input.DiscardCompleted();
                        Console.WriteLine();
                        Console.WriteLine("===== 联网赛制第 " + gameNumber + " 局 · 种子 " + session.GameSeed + " =====");
                        Console.WriteLine("赛制比分：" + session.DisplayNames[0] + " " + session.Wins[0] + " : " + session.Wins[1]
                            + " " + session.DisplayNames[1] + "；平局 " + session.Draws);
                        if (session.Clock != null)
                            Console.WriteLine("计时已按赛制重置：每阶段 " + ruleset.TurnSeconds + " 秒；每方总时间 " + ruleset.BankSeconds
                                + " 秒；禁用和首发不计时。");
                        PrintTeams(TextInput.ForDisplay(battle));
                        PrintMixedNotices(db, battle.Sides.SelectMany(side => side.Team).Select(fighter => fighter.Def.Id));
                        if (options.Advice)
                            PrintTeamAdvice(db, battle.Sides[session.MySide].Team.Select(item => item.Def.Id).ToArray(),
                                battle.Sides[1 - session.MySide].Team.Select(item => item.Def.Id).ToArray());
                    }
                    if (awaitingDrawResponse >= 0 && battle.Record.Events.Skip(awaitingDrawResponse)
                        .Any(entry => entry.Type == BattleRecordEventType.DrawResponse && entry.Side == 1 - session.MySide))
                        awaitingDrawResponse = -1;
                    if (!options.Auto)
                    {
                        if (!terminalCommand && battle.Phase != Phase.Over)
                        {
                            if (!inputStage.HasValue || !inputStage.Value.Matches(battle))
                            {
                                input.DiscardCompleted(preserveCommands: true);
                                inputStage = new ChoiceStamp(battle);
                            }
                            input.Start();
                        }
                        if (input.EndRequested && (!input.EndOfInput || !terminalCommand && awaitingDrawResponse < 0
                            && !session.DrawOfferPending && battle.Phase != Phase.Over))
                            return QuitNetworkMatch(completed.Count);
                    }
                    PrintNewLog(battle, ref logIndex);
                    if (options.Hash && session.State != NetState.Waiting && battle.Record.Events.Count != hashEventCount)
                    {
                        Console.WriteLine("同步状态哈希（第 " + gameNumber + " 局 · 第 " + battle.Turn + " 回合）："
                            + battle.StateHash().ToString("x16", CultureInfo.InvariantCulture));
                        hashEventCount = battle.Record.Events.Count;
                    }
                    if (session.State == NetState.Waiting)
                    {
                        if (!waitingPrinted) Console.WriteLine("等待对手或本局哈希确认……");
                        waitingPrinted = true;
                        if (!options.Auto && !terminalCommand && input.TryTake(out string line)
                            && line != null && !line.Trim().Equals("q", StringComparison.OrdinalIgnoreCase) && !command(line))
                            Console.WriteLine("已提交选择，等待对手；仍可输入 /surrender 或 /draw 指令。");
                        Thread.Sleep(10);
                        continue;
                    }
                    if (!options.Auto && input.EndOfInput && (awaitingDrawResponse >= 0 || session.DrawOfferPending))
                    {
                        Thread.Sleep(10);
                        continue;
                    }

                    waitingPrinted = false;
                    var stamp = new ChoiceStamp(battle);
                    NetState choosing = session.State;
                    Func<bool> expired = () => !ReferenceEquals(battle, session.Battle) || !stamp.Matches(battle)
                        || session.HasSubmitted || session.State != choosing;
                    Func<string, int?> readNumber = prompt => ReadPumpedNumber(input, prompt, poll, expired, command);
                    try
                    {
                        if (choosing == NetState.Ban || choosing == NetState.Lead)
                        {
                            bool ban = choosing == NetState.Ban;
                            int? selected = options.Auto ? (ban ? Ai.ChooseBan(battle, session.MySide) : Ai.ChooseLead(battle, session.MySide))
                                : ReadNetworkOpening(battle, session.MySide, ban, readNumber);
                            if (!selected.HasValue) return QuitNetworkMatch(completed.Count);
                            poll();
                            if (!expired())
                            {
                                if (ban) session.SubmitBan(selected.Value);
                                else session.SubmitLead(selected.Value);
                            }
                        }
                        else if (choosing == NetState.Choose)
                        {
                            Console.WriteLine("===== 第 " + battle.Turn + " 回合 =====");
                            var display = TextInput.ForDisplay(battle);
                            PrintFighter(display, 1 - session.MySide);
                            PrintFighter(display, session.MySide);
                            BattleAction? action = options.Auto ? Ai.Choose(battle, session.MySide, options.Level)
                                : ReadAction(battle, session.MySide, readNumber);
                            if (!action.HasValue) return QuitNetworkMatch(completed.Count);
                            poll();
                            if (!expired()) session.Submit(action.Value);
                        }
                        else if (choosing == NetState.Replace)
                        {
                            int? to = options.Auto ? Ai.ChooseReplacement(battle, session.MySide, options.Level)
                                : ReadReplacement(battle, session.MySide, readNumber);
                            if (!to.HasValue) return QuitNetworkMatch(completed.Count);
                            poll();
                            if (!expired()) session.SubmitReplace(to.Value);
                        }
                    }
                    catch (ChoiceExpiredException) { }
                    finally { input.DiscardCompleted(preserveCommands: true); }
                    CheckNetwork(session);
                }
                FinishNetwork(session, connection, null);
                if (!session.MatchConfirmed) throw new IOException("整场尚未完成双方确认，不能保存排位。");
                return SaveNetworkMatch(session, ruleset, completed, options);
            }
        }

        private static int? ReadNetworkOpening(Battle battle, int side, bool ban, Func<string, int?> readNumber)
        {
            var team = battle.Sides[ban ? 1 - side : side].Team;
            Console.WriteLine(ban ? "对手完整阵容（选择一位禁用）：" : "选择我方首发（按当前剩余队伍号位）：");
            for (int index = 0; index < team.Count; index++)
            {
                var fighter = team[index];
                Console.WriteLine("  [" + (index + 1) + "] " + fighter.Name + "（" + fighter.Def.Id + "） · "
                    + Describe.Tracks(battle.Db, fighter.Def) + " · 人设：" + battle.PersonaOf(fighter).Name);
                if (ban) Console.WriteLine("      技能：" + string.Join("、", fighter.Skills.Select(slot => battle.Db.Skills[slot.Id].Name)));
            }
            while (true)
            {
                int? number = readNumber(ban ? "请选择要禁用的对手号位（q 退出整场）> " : "请选择首发号位（q 退出整场）> ");
                if (!number.HasValue) return null;
                if (number.Value >= 1 && number.Value <= team.Count) return number.Value - 1;
                Console.WriteLine("该号位不存在，请选择列表中的主播。");
            }
        }

        private static int QuitNetworkMatch(int games)
        {
            Console.WriteLine("已退出未完成的联网赛制；已确认 " + games + " 局，本场未保存、不计战绩与排位。");
            return 0;
        }

        private static int SaveNetworkMatch(NetSession session, Ruleset ruleset, IList<Battle> completed, CliOptions options)
        {
            int exitCode = 0;
            foreach (var battle in completed)
                if (SaveCompletedBattle(battle, options, session.MySide, exportReport: false, recordProfile: false) != 0) exitCode = 1;
            Console.WriteLine("联网赛制结束：共 " + completed.Count + " 局；" + session.DisplayNames[0] + " "
                + session.Wins[0] + " : " + session.Wins[1] + " " + session.DisplayNames[1] + "；平局 " + session.Draws);
            Console.WriteLine("整场结果：" + (session.MatchWinner < 0 ? "平局" : session.DisplayNames[session.MatchWinner] + " 获胜"));
            try { SaveNetworkMatchReport(session, ruleset, completed, options); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
            {
                Console.Error.WriteLine("错误：联网赛制战报保存失败：" + ex.Message);
                exitCode = 1;
            }
            try
            {
                var profile = new FileSaveStore(options.SaveDir).RecordMatchProfile(session);
                var stats = profile.Packs[completed[0].Db.PackId];
                var rating = profile.Ratings[Profile.DefaultRatingFormat];
                Console.WriteLine("本包战绩：" + stats.Wins + " 胜 / " + stats.Losses + " 负 / " + stats.Draws + " 平");
                Console.WriteLine("整场排位已结算一次：" + rating.Rating + " 分 · " + RatingSystem.TierOf(rating.Rating).Name
                    + " · " + rating.Games + " 场");
                Console.WriteLine("档案已保存：" + Path.Combine(options.SaveDir, "profile.json"));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is FormatException
                || ex is InvalidOperationException || ex is OverflowException)
            {
                Console.Error.WriteLine("错误：整场档案保存失败：" + ex.Message);
                exitCode = 1;
            }
            return exitCode;
        }

        private static void SaveNetworkMatchReport(NetSession session, Ruleset ruleset, IList<Battle> completed, CliOptions options)
        {
            string path = ValidateReportPath(options);
            if (path == null) return;
            var text = new StringBuilder("# 联网赛制战报\n\n赛制：").Append(ruleset.Name).Append("（").Append(ruleset.Id)
                .Append("）\n\n初始种子：").Append(completed[0].Seed).Append("；完成局数：").Append(completed.Count)
                .Append("\n\n比分：房主 ").Append(session.Wins[0]).Append(" : ").Append(session.Wins[1]).Append(" 客机；平局 ")
                .Append(session.Draws).Append("\n\n排位以整场结果结算一次。\n");
            for (int index = 0; index < completed.Count; index++)
                text.Append("\n---\n\n## 第 ").Append(index + 1).Append(" 局\n\n")
                    .Append(BattleReport.Markdown(TextInput.ForDisplay(completed[index]), session.MySide));
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, text.ToString(), new UTF8Encoding(false));
            Console.WriteLine("联网赛制战报已保存：" + options.ReportFile);
        }
    }
}
