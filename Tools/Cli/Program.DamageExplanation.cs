using System;
using System.Collections.Generic;
using StreamDuel.Core;

namespace StreamDuel.Cli
{
    internal static partial class Program
    {
        private static void PrintDamageExplanation(Battle battle, int logIndex, bool enabled)
        {
            if (enabled) PrintDamageExplanation(BattleViewModel.DamageExplanationForLog(battle, logIndex));
        }

        private static void PrintDamageExplanation(IReadOnlyList<string> lines)
        {
            // 拆解只读取对应事件的快照；倍率、取整和实际伤害均由 Core 提供。
            foreach (string line in lines) Console.WriteLine("    伤害拆解：" + line);
        }
    }
}
