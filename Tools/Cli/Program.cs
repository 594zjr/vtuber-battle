using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using StreamDuel.Core;

namespace StreamDuel.Cli
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            Console.Out.NewLine = "\n";
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
            try
            {
                var options = CliOptions.Parse(args);
                if (options.Help)
                {
                    PrintHelp();
                    return 0;
                }

                string content = Path.Combine(AppContext.BaseDirectory, "Content");
                var db = ContentDb.FromJson(
                    File.ReadAllText(Path.Combine(content, "StreamDuelRules.json")),
                    File.ReadAllText(Path.Combine(content, "Packs", options.Pack + ".json")));
                var player = options.Team ?? db.Streamers.Take(6).Select(s => s.Id).ToArray();
                var errors = TeamCheck.Validate(db, null, player);
                if (errors.Count > 0) throw new ArgumentException(string.Join("；", errors));
                // 固定名单顺序无需额外随机源，小内容包先选未入队者再补齐。
                var opponent = db.Streamers.Select(s => s.Id).Where(id => !player.Contains(id))
                    .Concat(player).Take(6).ToArray();
                var battle = new Battle(db, new IList<string>[] { player, opponent }, new[] { "你", "电脑" },
                    new BattleOptions { Seed = options.Seed, BanOne = options.Ban });

                Console.WriteLine("开播对决 · 命令行对战");
                Console.WriteLine("内容包：" + db.PackName);
                Console.WriteLine(db.PackNotice);
                Console.WriteLine("种子：" + options.Seed + "；电脑难度：" + (options.Level == AiLevel.Easy ? "简单" : "普通")
                    + "；模式：" + (options.Auto ? "双方自动" : "手动"));
                PrintTeams(battle);
                int logIndex = 0;
                PrintNewLog(battle, ref logIndex);

                while (battle.Phase != Phase.Over)
                {
                    if (battle.Phase == Phase.Ban)
                    {
                        Console.WriteLine();
                        Console.WriteLine("===== 开局禁用 =====");
                        for (int side = 0; side < 2 && battle.Phase == Phase.Ban; side++)
                        {
                            if (battle.HasChosenBan(side)) continue;
                            int to;
                            if (side == 0 && !options.Auto)
                            {
                                var choice = ReadBan(battle);
                                if (!choice.HasValue) return Quit();
                                to = choice.Value;
                            }
                            else to = Ai.ChooseBan(battle, side);
                            battle.ChooseBan(side, to);
                            PrintNewLog(battle, ref logIndex);
                        }
                        PrintTeams(battle);
                        continue;
                    }

                    Console.WriteLine();
                    Console.WriteLine("===== 第 " + battle.Turn + " 回合 =====");
                    PrintFighter(battle, 1);
                    PrintFighter(battle, 0);
                    if (battle.Phase == Phase.Replace)
                    {
                        for (int side = 0; side < 2 && battle.Phase == Phase.Replace; side++)
                        {
                            if (!battle.Need[side]) continue;
                            int to;
                            if (side == 0 && !options.Auto)
                            {
                                var choice = ReadReplacement(battle);
                                if (!choice.HasValue) return Quit();
                                to = choice.Value;
                            }
                            else to = Ai.ChooseReplacement(battle, side);
                            battle.Replace(side, to);
                            PrintNewLog(battle, ref logIndex);
                        }
                        continue;
                    }

                    BattleAction action;
                    if (options.Auto) action = Ai.Choose(battle, 0, options.Level);
                    else
                    {
                        var choice = ReadAction(battle);
                        if (!choice.HasValue) return Quit();
                        action = choice.Value;
                    }
                    var foeAction = Ai.Choose(battle, 1, options.Level);
                    int resolvedTurn = battle.Turn;
                    battle.ResolveTurn(action, foeAction);
                    PrintNewLog(battle, ref logIndex);
                    if (options.Hash)
                        Console.WriteLine("第 " + resolvedTurn + " 回合状态哈希：" + battle.StateHash().ToString("x16", CultureInfo.InvariantCulture));
                }

                Console.WriteLine();
                Console.WriteLine("对局结束：" + (battle.Winner < 0 ? "平局" : battle.Sides[battle.Winner].Name + " 获胜"));
                PrintTeams(battle);
                Console.WriteLine(BattleSummary.From(battle).ToText());
                var profile = ProfileStore.Record(battle, options.SaveDir);
                var stats = profile.Packs[db.PackId];
                Console.WriteLine("本包战绩：" + stats.Wins + " 胜 / " + stats.Losses + " 负 / " + stats.Draws + " 平");
                Console.WriteLine("档案已保存：" + Path.Combine(options.SaveDir, "profile.json"));
                return 0;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is IOException
                || ex is UnauthorizedAccessException || ex is KeyError || ex is OverflowException)
            {
                Console.Error.WriteLine("错误：" + ex.Message);
                return 1;
            }
        }

        private static void PrintHelp()
        {
            Console.WriteLine("用法：dotnet run --project Tools/Cli -- --pack fan_asoul_vr --level normal [--seed 1] [--team id1,id2,id3,id4,id5,id6] [--auto] [--ban] [--hash] [--save-dir 目录]");
            Console.WriteLine("默认：原创包 original、普通难度 normal、种子 1；--level 支持 easy / normal。");
            Console.WriteLine("未指定 --team 时，按内容包顺序选择前六位；电脑先选剩余主播，再按我方顺序补足六位。");
            Console.WriteLine("手动模式：1～5 使用技能；6～11 对应六个号位的换人选项；补位时输入号位 1～6。");
            Console.WriteLine("0 仅在受控时跳过，或技能全部无 PP 时使用硬撑；q 或输入结束退出，未完成对局不判胜负。");
            Console.WriteLine("--auto 让双方按所选难度操作并输出整局；相同内容、参数和种子的战斗日志完全一致。");
            Console.WriteLine("--ban 开局双方各禁用对方一位主播，剩余五位对战；手动输入对手号位 1～6，--auto 时由电脑代选。");
            Console.WriteLine("每局结束累计你这一方的战绩；--save-dir 默认 ./saves，档案为该目录中的 profile.json，仅保存在本机。");
            Console.WriteLine("--hash 无需参数值：每回合结算后、补位前输出一次 64 位状态哈希（16 位十六进制），包括对局结束的回合；补位不重复输出。");
        }

        private static void PrintTeams(Battle battle)
        {
            for (int side = 0; side < 2; side++)
            {
                Console.WriteLine(battle.Sides[side].Name + "的队伍：" + string.Join("；", battle.Sides[side].Team.Select(
                    f => f.Slot + "号 " + f.Name + "（" + f.Def.Id + "，" + f.Hp + "/" + f.MaxHp + (f.Fainted ? "，已下播" : "") + "）")));
            }
        }

        private static void PrintFighter(Battle battle, int side)
        {
            var fighter = battle.Active(side);
            Console.WriteLine(battle.Sides[side].Name + "场上：" + fighter.Name + " · " + Describe.Tracks(battle.Db, fighter.Def)
                + " · 人气 " + fighter.Hp + "/" + fighter.MaxHp + (fighter.Fainted ? "（已下播）" : ""));
            var persona = battle.PersonaOf(fighter);
            Console.WriteLine("  人设：" + persona.Name + "〔" + persona.Node + "；不可消除〕" + persona.Desc);
            var stages = fighter.Stages.Where(kv => kv.Value != 0)
                .Select(kv => battle.Db.StatName(kv.Key) + (kv.Value > 0 ? "+" : "") + kv.Value).ToArray();
            Console.WriteLine("  能力等级：" + (stages.Length == 0 ? "无变化" : string.Join("、", stages)));
            Console.WriteLine("  状态：" + (fighter.Statuses.Count == 0 ? "无" : ""));
            foreach (var status in fighter.Statuses)
            {
                var def = battle.Db.Statuses[status.Id];
                Console.WriteLine("    " + def.Name + "〔剩余 " + status.Left + (def.IsControl ? " 次行动" : " 回合")
                    + "；" + (def.Cleanse ? "可净化" : "不可净化") + "〕" + def.Desc);
            }
            Console.WriteLine("  直播间效果：" + (fighter.Fields.Count == 0 ? "无" : ""));
            foreach (var field in fighter.Fields)
            {
                var def = battle.Db.Fields[field.Id];
                Console.WriteLine("    " + def.Name + "〔" + Describe.FieldKind(def) + "；"
                    + (field.Turns < 0 ? "直到下场" : "剩余 " + field.Turns + " 回合")
                    + (field.Stacks > 0 ? "；" + field.Stacks + " 层" : "")
                    + "；" + (def.Dispel ? "可被消回合" : "消回合清不掉") + "〕" + def.Desc);
            }
        }

        private static int? ReadBan(Battle battle)
        {
            var foes = battle.Sides[1].Team;
            Console.WriteLine("对手完整阵容（选择一位禁用）：");
            for (int index = 0; index < foes.Count; index++)
            {
                var fighter = foes[index];
                var persona = battle.PersonaOf(fighter);
                Console.WriteLine("  [" + (index + 1) + "] " + fighter.Name + "（" + fighter.Def.Id + "） · "
                    + Describe.Tracks(battle.Db, fighter.Def) + " · 人设：" + persona.Name);
                Console.WriteLine("      技能：" + string.Join("、", fighter.Skills.Select(slot =>
                    battle.Db.Skills[slot.Id].Name + (slot.Fifth ? "〔第五技能〕" : ""))));
            }
            while (true)
            {
                var number = ReadNumber("请选择要禁用的对手号位（q 退出）> ");
                if (!number.HasValue) return null;
                if (number.Value >= 1 && number.Value <= foes.Count) return number.Value - 1;
                Console.WriteLine("该号位不存在，请选择列表中的对手主播。");
            }
        }

        private static BattleAction? ReadAction(Battle battle)
        {
            var legal = battle.LegalFor(0);
            Console.WriteLine("技能：");
            foreach (var option in legal.Skills)
            {
                var skill = battle.Db.Skills[option.Id];
                Console.WriteLine("  [" + (option.Index + 1) + "] " + skill.Name + (option.Fifth ? "〔第五技能〕" : "")
                    + " · PP " + option.Pp + "/" + option.Max + " · 先制 " + option.Prio.Value
                    + (option.Usable ? (legal.FreePp ? "〔人设免扣 PP〕" : "") : "〔不可用〕"));
                foreach (var effect in skill.Effects)
                {
                    var description = Describe.Effect(battle.Db, effect);
                    Console.WriteLine("      〔" + description.At + "〕" + description.Text
                        + (description.Dispel == null ? "" : "〔" + description.Dispel + "〕"));
                }
            }
            PrintSwitches(battle, legal.Switches, false);
            if (legal.Controlled != null) Console.WriteLine("  [0] 受「" + legal.Controlled + "」控制，跳过本次行动"
                + (legal.Switches.Count > 0 ? "（仍可换人）" : ""));
            else if (legal.Struggle) Console.WriteLine("  [0] 所有技能无 PP，使用「" + battle.Db.Struggle.Name + "」");

            while (true)
            {
                var number = ReadNumber("选择行动（q 退出）> ");
                if (!number.HasValue) return null;
                var option = legal.Skills.FirstOrDefault(s => s.Index + 1 == number.Value && s.Usable);
                if (option != null) return BattleAction.Skill(option.Index);
                int to = number.Value - 6;
                if (legal.Switches.Contains(to)) return BattleAction.SwitchTo(to);
                if (number.Value == 0 && legal.Controlled != null) return BattleAction.Pass();
                if (number.Value == 0 && legal.Struggle) return new BattleAction { Type = ActionType.Struggle };
                Console.WriteLine("该选项当前不可用，请输入列表中的可用数字。");
            }
        }

        private static int? ReadReplacement(Battle battle)
        {
            // 补位不受主动换人的锁麦限制，最终选择仍交给 Core.Replace 校验。
            var side = battle.Sides[0];
            var choices = Enumerable.Range(0, side.Team.Count)
                .Where(index => index != side.Active && !side.Team[index].Fainted).ToList();
            PrintSwitches(battle, choices, true);
            while (true)
            {
                var number = ReadNumber("请选择补位号位（q 退出）> ");
                if (!number.HasValue) return null;
                if (choices.Contains(number.Value - 1)) return number.Value - 1;
                Console.WriteLine("该号位不能补位，请选择列表中的主播。");
            }
        }

        private static void PrintSwitches(Battle battle, IList<int> choices, bool replacement)
        {
            Console.WriteLine(replacement ? "可补位队友：" : "可换队友：");
            if (choices.Count == 0) Console.WriteLine("  无");
            foreach (int index in choices)
            {
                var fighter = battle.Sides[0].Team[index];
                Console.WriteLine("  [" + (index + (replacement ? 1 : 6)) + "] " + fighter.Slot + "号 " + fighter.Name
                    + " · 人气 " + fighter.Hp + "/" + fighter.MaxHp);
            }
        }

        private static int? ReadNumber(string prompt)
        {
            while (true)
            {
                Console.Write(prompt);
                string line = Console.ReadLine();
                if (line == null || line.Trim().Equals("q", StringComparison.OrdinalIgnoreCase)) return null;
                int value;
                if (int.TryParse(line.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out value)) return value;
                Console.WriteLine("请输入数字，或输入 q 退出。");
            }
        }

        private static int Quit()
        {
            Console.WriteLine();
            Console.WriteLine("已退出，对局未完成。");
            return 0;
        }

        private static void PrintNewLog(Battle battle, ref int index)
        {
            string node = null;
            int turn = -1;
            while (index < battle.Log.Count)
            {
                var entry = battle.Log[index++];
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
