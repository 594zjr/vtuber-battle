using System;
using System.Globalization;
using System.Linq;
using StreamDuel.Core;

namespace StreamDuel.Cli
{
    internal static partial class Program
    {
        private static bool PlayLocalBattle(Battle battle, CliOptions options, TurnClock clock, ConsoleInput input)
        {
            PrintTeams(battle);
            PrintMixedNotices(battle.Db, battle.Sides.SelectMany(side => side.Team).Select(fighter => fighter.Def.Id));
            if (!options.Auto) PrintTerminationCommands();
            // 只读取原始公开六人阵容，必须早于任何禁用/首发确认，也不借用实战随机流。
            if (options.Advice) PrintTeamAdvice(battle.Db, battle.Sides[0].Team.Select(fighter => fighter.Def.Id).ToArray(),
                battle.Sides[1].Team.Select(fighter => fighter.Def.Id).ToArray());
            int logIndex = 0;
            PrintNewLog(battle, ref logIndex);
            Func<string, int?> readNumber = prompt => ReadPumpedNumber(input, prompt, () => { }, () => false,
                line => HandleLocalCommand(battle, 0, line));

            try
            {
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
                                var choice = ReadBan(battle, readNumber);
                                if (!choice.HasValue) return false;
                                to = choice.Value;
                            }
                            else to = Ai.ChooseBan(battle, side, options.Level);
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
                                var choice = ReadLead(battle, readNumber);
                                if (!choice.HasValue) return false;
                                to = choice.Value;
                            }
                            else to = Ai.ChooseLead(battle, side, options.Level);
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
                        if (clock != null)
                        {
                            if (!ReadTimedReplacements(battle, options, clock, input)) return false;
                            PrintNewLog(battle, ref logIndex);
                            continue;
                        }
                        for (int side = 0; side < 2 && battle.Phase == Phase.Replace; side++)
                        {
                            if (!battle.Need[side]) continue;
                            int to;
                            if (side == 0 && !options.Auto)
                            {
                                var choice = ReadReplacement(battle, readNumber: readNumber);
                                if (!choice.HasValue) return false;
                                to = choice.Value;
                            }
                            else to = Ai.ChooseReplacement(battle, side, options.Level);
                            battle.Replace(side, to);
                            PrintNewLog(battle, ref logIndex);
                        }
                        continue;
                    }

                    BattleAction action;
                    BattleAction foeAction;
                    if (clock != null)
                    {
                        if (!ReadTimedActions(battle, options, clock, input, out action, out foeAction)) return false;
                    }
                    else if (options.Auto)
                    {
                        action = Ai.Choose(battle, 0, options.Level);
                        foeAction = Ai.Choose(battle, 1, options.Level);
                    }
                    else
                    {
                        var choice = ReadAction(battle, readNumber: readNumber);
                        if (!choice.HasValue) return false;
                        action = choice.Value;
                        foeAction = Ai.Choose(battle, 1, options.Level);
                    }
                    int resolvedTurn = battle.Turn;
                    battle.ResolveTurn(action, foeAction);
                    PrintNewLog(battle, ref logIndex);
                    if (options.Hash)
                        Console.WriteLine("第 " + resolvedTurn + " 回合状态哈希：" + battle.StateHash().ToString("x16", CultureInfo.InvariantCulture));
                }
            }
            catch (BattleFinishedException) { }
            PrintNewLog(battle, ref logIndex);
            return true;
        }
    }
}
