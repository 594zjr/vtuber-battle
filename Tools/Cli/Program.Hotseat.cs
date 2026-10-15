using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using StreamDuel.Core;

namespace StreamDuel.Cli
{
    internal static partial class Program
    {
        private static int PlayHotseat(ContentDb db, TeamPreset preset, CliOptions options)
        {
            var teamA = preset.Ids.ToArray();
            var teamB = options.TeamB ?? db.Streamers.Select(streamer => streamer.Id)
                .Where(id => !teamA.Contains(id)).Concat(teamA).Take(6).ToArray();
            var errors = TeamCheck.Validate(db, null, teamB);
            if (errors.Count > 0) throw new ArgumentException("B 方队伍无效：" + string.Join("；", errors));
            var session = new HotseatSession(db, teamA, teamB, options.NameA, options.NameB,
                new BattleOptions { Seed = options.Seed, BanOne = options.Ban, LeadPick = options.Lead });
            if (options.ExportTeam) Console.WriteLine("本方队伍码：" + TeamCode.Encode(preset));
            Console.WriteLine("开播对决 · 同屏双人对战");
            Console.WriteLine("内容包：" + db.PackName);
            Console.WriteLine(db.PackNotice);
            Console.WriteLine("种子：" + options.Seed + "；A 方：" + options.NameA + "；B 方：" + options.NameB);
            Console.WriteLine("轮流私密选招，输入不回显；交接后按回车确认，q 或输入结束退出。");
            Console.WriteLine("本机档案按 A 方累计一次。");
            Console.WriteLine("对战指令：/surrender 投降并结算；同屏模式暂不支持 /draw 求和。");
            int logIndex = 0;
            try
            {
                while (session.Phase != Phase.Over)
                {
                    if (session.NeedsHandover && !ConfirmHotseatHandover(session, options)) return Quit();
                    int side = session.WhoseTurn;
                    var view = session.ViewFor(side);
                    PrintHotseatLog(session.Log, ref logIndex);
                    Console.WriteLine();
                    Console.WriteLine("===== 第 " + view.Turn + " 回合 · " + view.Sides[side].Name + " 操作 =====");
                    PrintHotseatTeams(view);
                    if (view.Phase == Phase.Ban)
                    {
                        int? choice = ReadHotseatMember(view.Sides[1 - side], "禁用对手", null, session);
                        if (!choice.HasValue) return Quit();
                        session.SubmitBan(choice.Value);
                    }
                    else if (view.Phase == Phase.Lead)
                    {
                        int? choice = ReadHotseatMember(view.Sides[side], "选择首发", null, session);
                        if (!choice.HasValue) return Quit();
                        session.SubmitLead(choice.Value);
                    }
                    else if (view.Phase == Phase.Replace)
                    {
                        int? choice = ReadHotseatMember(view.Sides[side], "选择补位", view.SwitchTargets, session);
                        if (!choice.HasValue) return Quit();
                        session.SubmitReplace(choice.Value);
                    }
                    else
                    {
                        PrintHotseatFighter(view, 1 - side, db);
                        PrintHotseatFighter(view, side, db);
                        BattleAction? choice = ReadHotseatAction(view, side, session);
                        if (!choice.HasValue) return Quit();
                        session.Submit(choice.Value);
                    }
                }
            }
            catch (BattleFinishedException) { }
            PrintHotseatLog(session.Log, ref logIndex);
            return SaveCompletedBattle(session.CloneCompletedBattle(), options, 0);
        }

        private static bool ConfirmHotseatHandover(HotseatSession session, CliOptions options)
        {
            if (!Console.IsOutputRedirected)
            {
                try { Console.Clear(); }
                catch (IOException) { }
            }
            // 同时清理可见内容和支持 ANSI 的终端滚动记录，输入本身始终不回显。
            Console.Write("\u001b[2J\u001b[H\u001b[3J");
            string nextName = session.WhoseTurn == 0 ? options.NameA : options.NameB;
            Console.WriteLine("请把电脑交给「" + nextName + "」。");
            while (true)
            {
                Console.Write("接手后按回车确认（q 退出）> ");
                string line = ReadHotseatLine();
                if (line == null || line.Trim().Equals("q", StringComparison.OrdinalIgnoreCase)) return false;
                if (line.Trim().Length == 0)
                {
                    session.ConfirmHandover();
                    return true;
                }
                Console.WriteLine("请交给下一位玩家，再直接按回车确认。");
            }
        }

        private static string ReadHotseatLine()
        {
            Console.Out.Flush();
            string result;
            if (Console.IsInputRedirected) result = Console.ReadLine();
            else
            {
                var text = new StringBuilder();
                while (true)
                {
                    var key = Console.ReadKey(intercept: true);
                    if (key.Key == ConsoleKey.Enter) break;
                    if ((key.Modifiers & ConsoleModifiers.Control) != 0
                        && (key.Key == ConsoleKey.D || key.Key == ConsoleKey.Z))
                    {
                        Console.WriteLine();
                        return null;
                    }
                    if (key.Key == ConsoleKey.Backspace)
                    {
                        if (text.Length > 0) text.Length--;
                    }
                    else if (!char.IsControl(key.KeyChar)) text.Append(key.KeyChar);
                }
                result = text.ToString();
            }
            Console.WriteLine();
            return result;
        }

        private static int? ReadHotseatNumber(string prompt, HotseatSession session)
        {
            while (true)
            {
                Console.Write(prompt);
                string line = ReadHotseatLine();
                if (line == null || line.Trim().Equals("q", StringComparison.OrdinalIgnoreCase)) return null;
                if (HandleHotseatCommand(session, line)) continue;
                int number;
                if (int.TryParse(line.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out number)) return number;
                Console.WriteLine("请输入列表中的数字，或输入 q 退出。");
            }
        }

        private static int? ReadHotseatMember(SideView team, string purpose, IList<int> targets, HotseatSession session)
        {
            Console.WriteLine(purpose + "（当前队伍号位）：");
            var choices = targets ?? Enumerable.Range(0, team.Team.Count).ToList();
            foreach (int index in choices)
            {
                var fighter = team.Team[index];
                Console.WriteLine("  [" + (index + 1) + "] " + fighter.Name + "（" + fighter.Id + "） · "
                    + fighter.Tracks + " · 人气 " + fighter.Hp + "/" + fighter.MaxHp + " · 人设：" + fighter.PersonaName);
            }
            while (true)
            {
                var number = ReadHotseatNumber(purpose + "（私密输入，q 退出）> ", session);
                if (!number.HasValue) return null;
                if (choices.Contains(number.Value - 1)) return number.Value - 1;
                Console.WriteLine("该号位当前不可选，请选择列表中的主播。");
            }
        }

        private static BattleAction? ReadHotseatAction(BattleViewModel view, int side, HotseatSession session)
        {
            Console.WriteLine("技能：");
            foreach (var skill in view.Skills)
            {
                Console.WriteLine("  [" + (skill.Index + 1) + "] " + skill.Name + (skill.Fifth ? "〔第五技能〕" : "")
                    + " · PP " + skill.Pp + "/" + skill.MaxPp + " · 先制 " + skill.Priority
                    + (skill.Usable ? (view.FreePp ? "〔人设免扣 PP〕" : "") : "〔不可用〕"));
                foreach (var effect in skill.Effects)
                    Console.WriteLine("      〔" + effect.Timing + "〕" + effect.Text
                        + (effect.Dispel == null ? "" : "〔" + effect.Dispel + "〕"));
            }
            Console.WriteLine("可换队友：");
            if (view.SwitchTargets.Count == 0) Console.WriteLine("  无");
            foreach (int index in view.SwitchTargets)
            {
                var fighter = view.Sides[side].Team[index];
                Console.WriteLine("  [" + (index + 6) + "] " + (index + 1) + "号 " + fighter.Name
                    + " · 人气 " + fighter.Hp + "/" + fighter.MaxHp);
            }
            if (view.ControlledReason != null) Console.WriteLine("  [0] 受「" + view.ControlledReason + "」控制，跳过本次行动");
            else if (view.Struggle) Console.WriteLine("  [0] 所有技能无 PP，使用「" + view.StruggleName + "」");
            while (true)
            {
                var number = ReadHotseatNumber("选择行动（私密输入，q 退出）> ", session);
                if (!number.HasValue) return null;
                var skill = view.Skills.FirstOrDefault(option => option.Index + 1 == number.Value && option.Usable);
                if (skill != null) return BattleAction.Skill(skill.Index);
                if (view.SwitchTargets.Contains(number.Value - 6)) return BattleAction.SwitchTo(number.Value - 6);
                if (number.Value == 0 && view.ControlledReason != null) return BattleAction.Pass();
                if (number.Value == 0 && view.Struggle) return new BattleAction { Type = ActionType.Struggle };
                Console.WriteLine("该选项当前不可用，请输入列表中的可用数字。");
            }
        }

        private static void PrintHotseatTeams(BattleViewModel view)
        {
            foreach (var side in view.Sides)
                Console.WriteLine(side.Name + "的队伍：" + string.Join("；", side.Team.Select((fighter, index) =>
                    (index + 1) + "号 " + fighter.Name + "（" + fighter.Id + "，" + fighter.Hp + "/" + fighter.MaxHp
                    + (fighter.Fainted ? "，已下播" : "") + "）")));
        }

        private static void PrintHotseatFighter(BattleViewModel view, int side, ContentDb db)
        {
            var team = view.Sides[side];
            var fighter = team.Team[team.Active];
            Console.WriteLine(team.Name + "场上：" + fighter.Name + " · " + fighter.Tracks + " · 人气 " + fighter.Hp + "/" + fighter.MaxHp);
            Console.WriteLine("  人设：" + fighter.PersonaName + "；" + fighter.PersonaDescription);
            var stages = fighter.Stages.Where(pair => pair.Value != 0)
                .Select(pair => db.StatName(pair.Key) + (pair.Value > 0 ? "+" : "") + pair.Value).ToArray();
            Console.WriteLine("  能力等级：" + (stages.Length == 0 ? "无变化" : string.Join("、", stages)));
            Console.WriteLine("  状态与直播间效果：" + (fighter.Statuses.Count == 0 ? "无" : ""));
            foreach (var chip in fighter.Statuses) Console.WriteLine("    " + chip.Tooltip);
        }

        private static void PrintHotseatLog(IReadOnlyList<LogEntry> log, ref int index)
        {
            string node = null;
            int turn = -1;
            while (index < log.Count)
            {
                var entry = log[index++];
                if (entry.Node != node || entry.Turn != turn)
                {
                    Console.WriteLine("【第 " + entry.Turn + " 回合 · " + entry.Node + "】");
                    node = entry.Node;
                    turn = entry.Turn;
                }
                Console.WriteLine("  " + entry.Text);
            }
        }
    }
}
