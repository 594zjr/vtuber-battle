using System;
using System.Globalization;
using StreamDuel.Core;

namespace StreamDuel.Cli
{
    internal static partial class Program
    {
        private static bool PlayLocalBattle(Battle battle, CliOptions options)
        {
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
                            if (!choice.HasValue) return false;
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
                            if (!choice.HasValue) return false;
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
                if (options.Auto) action = Ai.Choose(battle, 0, options.Level);
                else
                {
                    var choice = ReadAction(battle);
                    if (!choice.HasValue) return false;
                    action = choice.Value;
                }
                var foeAction = Ai.Choose(battle, 1, options.Level);
                int resolvedTurn = battle.Turn;
                battle.ResolveTurn(action, foeAction);
                PrintNewLog(battle, ref logIndex);
                if (options.Hash)
                    Console.WriteLine("第 " + resolvedTurn + " 回合状态哈希：" + battle.StateHash().ToString("x16", CultureInfo.InvariantCulture));
            }
            return true;
        }
    }
}
