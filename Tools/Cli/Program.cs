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

                if (!options.ListPacks) ValidateReportPath(options);
                if (options.ReplayFile != null && !options.ListPacks) return PlayReplay(options);
                string content = Path.Combine(AppContext.BaseDirectory, "Content");
                var db = LoadPack(content, options);
                if (options.ListPacks) return 0;
                if (options.WatchHost != null) return WatchBattle(db, options);
                var player = options.Team ?? TeamBuilder.Suggest(db, null, new string[0], options.Seed).ToArray();
                var errors = TeamCheck.Validate(db, null, player);
                if (errors.Count > 0) throw new ArgumentException(string.Join("；", errors));
                if (options.Network) return PlayNetwork(db, player, options);
                // 固定名单顺序无需额外随机源，小内容包先选未入队者再补齐。
                var opponent = db.Streamers.Select(s => s.Id).Where(id => !player.Contains(id))
                    .Concat(player).Take(6).ToArray();
                var battle = new Battle(db, new IList<string>[] { player, opponent }, new[] { "你", "电脑" },
                    new BattleOptions { Seed = options.Seed, BanOne = options.Ban, LeadPick = options.Lead });

                Console.WriteLine("开播对决 · 命令行对战");
                Console.WriteLine("内容包：" + db.PackName);
                Console.WriteLine(db.PackNotice);
                Console.WriteLine("种子：" + options.Seed + "；电脑难度：" + LevelName(options.Level)
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

                    if (battle.Phase == Phase.Lead)
                    {
                        Console.WriteLine();
                        Console.WriteLine("===== 选择首发 =====");
                        for (int side = 0; side < 2 && battle.Phase == Phase.Lead; side++)
                        {
                            if (battle.HasChosenLead(side)) continue;
                            int to;
                            if (side == 0 && !options.Auto)
                            {
                                var choice = ReadLead(battle);
                                if (!choice.HasValue) return Quit();
                                to = choice.Value;
                            }
                            else to = Ai.ChooseLead(battle, side);
                            battle.ChooseLead(side, to);
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
                            else to = Ai.ChooseReplacement(battle, side, options.Level);
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

                return SaveCompletedBattle(battle, options, 0);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is IOException
                || ex is UnauthorizedAccessException || ex is KeyError || ex is OverflowException
                || ex is FormatException || ex is InvalidOperationException || ex is TimeoutException)
            {
                Console.Error.WriteLine("错误：" + ex.Message);
                return 1;
            }
        }

        private static void PrintHelp()
        {
            Console.WriteLine("用法：dotnet run --project Tools/Cli -- [参数]");
            Console.WriteLine("  --pack 编号       内容包 id，兼容内置文件名；默认原创包 original。");
            Console.WriteLine("  --pack-dir 目录   从指定目录第一层加载外部 JSON；id 冲突时内置优先并提示。");
            Console.WriteLine("  --list-packs      列出可用包的 id、名称和来源后退出，不开始对战或回放。");
            Console.WriteLine("  --level 难度      easy（简单）/ normal（普通）/ hard（困难）/ expert（专家）；默认 normal。");
            Console.WriteLine("  --seed 整数       自动组队与对战的种子 0..4294967295；默认 1。");
            Console.WriteLine("  --team 六个编号   逗号分隔我方六位主播；未指定时自动建议，电脑先选剩余再补齐。");
            Console.WriteLine("  --auto            双方自动操作；回放模式下连续播放全部回合。");
            Console.WriteLine("  --ban             开局各禁用对方一位，变为五对五；手动输入对手号位。");
            Console.WriteLine("  --lead            双方选择首发；与 --ban 同用时先禁用，再从剩余队伍中选首发。");
            Console.WriteLine("  --hash            每回合结算后、补位前输出 64 位状态哈希，补位不重复输出。");
            Console.WriteLine("  --host 端口       等待一位局域网对手，0 自动分配并显示实际端口；种子由房主决定。");
            Console.WriteLine("  --join 地址:端口  加入房主，IPv6 地址用 [地址]:端口；双方各用自己的 --team。");
            Console.WriteLine("  --spectate-port 端口  仅房主启用独立观众端口，0 自动分配；固定延迟 2 回合，结束后补齐。");
            Console.WriteLine("  --watch 地址:端口  只读观看，使用匹配的 --pack/--pack-dir；收到结束标记后退出，不写存档。");
            Console.WriteLine("  --save-dir 目录    本机存档目录，默认 ./saves；profile.json 累计战绩，replays 保留最近 50 局。");
            Console.WriteLine("  --replay 文件     按记录中的内容包回看；回车下一回合，q 或输入结束退出；不累计战绩或保存新回放。");
            Console.WriteLine("  --report 文件     对局结束或回放全部播放后导出 UTF-8 Markdown 战报；提前退出不导出，不能覆盖回放输入或新对局档案。");
            Console.WriteLine("  --help / -h       显示本帮助。");
            Console.WriteLine("手动模式：1～5 使用技能；6～11 对应六个号位的换人选项；补位时输入号位 1～6。");
            Console.WriteLine("0 仅在受控时跳过，或技能全部无 PP 时使用硬撑；q 或输入结束退出，未完成对局不判胜负。");
            Console.WriteLine("相同内容、参数和种子的战斗日志一致；完整输出还取决于初始档案。连续对局会正常累计战绩。");
            Console.WriteLine("回放支持 --auto、--pack-dir 和 --report；不能与 --hash 同用，其他对战/存档参数不改变记录，也不写存档。");
            Console.WriteLine("联网仅用于可信局域网，无加密、无账号；不能与 --ban、--lead、--replay、--list-packs 同用。--auto 仅替本方选招，--hash 显示已同步阶段及最终哈希。");
            Console.WriteLine("观战不能与选手、AI、组队、回放或保存参数同用；可用 --hash 显示收到更新后的局面哈希，最终哈希始终显示。--list-packs 只列包，不连接观战。");
        }

        private static string LevelName(AiLevel level)
        {
            switch (level)
            {
                case AiLevel.Easy: return "简单";
                case AiLevel.Hard: return "困难";
                case AiLevel.Expert: return "专家";
                default: return "普通";
            }
        }

        private static ContentDb LoadPack(string content, CliOptions options)
        {
            string rules = File.ReadAllText(Path.Combine(content, "StreamDuelRules.json"));
            var builtIn = PackLoader.LoadDirectory(rules, Path.Combine(content, "Packs"));
            var packs = new List<PackLoadResult>();
            foreach (var result in builtIn)
            {
                if (result.Db == null) Console.Error.WriteLine("[错误] 内置内容包 " + result.FileName + "：" + result.Error);
                else packs.Add(result);
            }
            var builtInIds = new HashSet<string>(packs.Select(result => result.Db.PackId), StringComparer.Ordinal);
            if (options.PackDirectory != null)
            {
                foreach (var result in PackLoader.LoadDirectory(rules, options.PackDirectory))
                {
                    if (result.Db == null)
                        Console.Error.WriteLine("[错误] 外部内容包 " + result.FileName + "：" + result.Error);
                    else if (builtInIds.Contains(result.Db.PackId))
                        Console.Error.WriteLine("[提醒] 外部内容包 " + result.FileName + " 的 id「" + result.Db.PackId + "」与内置包重复，已使用内置包。");
                    else packs.Add(result);
                }
            }
            if (options.ListPacks)
            {
                foreach (var result in packs)
                    Console.WriteLine(result.Db.PackId + "：" + result.Db.PackName + "〔" + (builtIn.Contains(result) ? "内置" : "外部") + "〕（" + result.FileName + "）");
                return null;
            }

            var selected = packs.FirstOrDefault(result => result.Db.PackId == options.Pack);
            if (selected == null)
                selected = builtIn.FirstOrDefault(result => result.Db != null &&
                    string.Equals(Path.GetFileNameWithoutExtension(result.FileName), options.Pack, StringComparison.OrdinalIgnoreCase));
            if (selected == null) throw new ArgumentException("找不到内容包「" + options.Pack + "」；使用 --list-packs 查看可用内容包。");
            return selected.Db;
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

        private static int? ReadLead(Battle battle)
        {
            var team = battle.Sides[0].Team;
            Console.WriteLine("选择我方首发（按当前剩余队伍号位）：");
            for (int index = 0; index < team.Count; index++)
            {
                var fighter = team[index];
                Console.WriteLine("  [" + (index + 1) + "] " + fighter.Name + "（" + fighter.Def.Id + "） · "
                    + Describe.Tracks(battle.Db, fighter.Def) + " · 人设：" + battle.PersonaOf(fighter).Name);
            }
            while (true)
            {
                var number = ReadNumber("请选择首发号位（q 退出）> ");
                if (!number.HasValue) return null;
                if (number.Value >= 1 && number.Value <= team.Count) return number.Value - 1;
                Console.WriteLine("该号位不存在，请选择列表中的队友。");
            }
        }

        private static BattleAction? ReadAction(Battle battle, int side = 0, Func<string, int?> readNumber = null)
        {
            if (readNumber == null) readNumber = ReadNumber;
            var legal = battle.LegalFor(side);
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
            PrintSwitches(battle, legal.Switches, false, side);
            if (legal.Controlled != null) Console.WriteLine("  [0] 受「" + legal.Controlled + "」控制，跳过本次行动"
                + (legal.Switches.Count > 0 ? "（仍可换人）" : ""));
            else if (legal.Struggle) Console.WriteLine("  [0] 所有技能无 PP，使用「" + battle.Db.Struggle.Name + "」");

            while (true)
            {
                var number = readNumber("选择行动（q 退出）> ");
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

        private static int? ReadReplacement(Battle battle, int mySide = 0, Func<string, int?> readNumber = null)
        {
            if (readNumber == null) readNumber = ReadNumber;
            // 补位不受主动换人的锁麦限制，最终选择仍交给 Core.Replace 校验。
            var side = battle.Sides[mySide];
            var choices = Enumerable.Range(0, side.Team.Count)
                .Where(index => index != side.Active && !side.Team[index].Fainted).ToList();
            PrintSwitches(battle, choices, true, mySide);
            while (true)
            {
                var number = readNumber("请选择补位号位（q 退出）> ");
                if (!number.HasValue) return null;
                if (choices.Contains(number.Value - 1)) return number.Value - 1;
                Console.WriteLine("该号位不能补位，请选择列表中的主播。");
            }
        }

        private static void PrintSwitches(Battle battle, IList<int> choices, bool replacement, int side = 0)
        {
            Console.WriteLine(replacement ? "可补位队友：" : "可换队友：");
            if (choices.Count == 0) Console.WriteLine("  无");
            foreach (int index in choices)
            {
                var fighter = battle.Sides[side].Team[index];
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
