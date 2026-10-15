using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using StreamDuel.Core;

namespace StreamDuel.Cli
{
    internal static partial class Program
    {
        static void PrintPickMode(Battle battle)
        {
            if (battle.PickCount != 0)
                Console.WriteLine("选人模式：公开 6 人，各选 " + battle.PickCount + " 人；按输入顺序出战，首位为首发。");
        }

        static List<int> ReadPick(Battle battle, int side, Func<string, string> readLine = null)
        {
            if (readLine == null) readLine = prompt => { Console.Write(prompt); return Console.ReadLine(); };
            var available = battle.AvailablePicks(side);
            Console.WriteLine("我方原始阵容（已禁用的号位不可选择）：");
            for (int index = 0; index < battle.Record.Teams[side].Count; index++)
            {
                var definition = battle.Db.Streamer(battle.Record.Teams[side][index]);
                Console.WriteLine("  [" + (index + 1) + "] " + definition.Name + " · " + Describe.Tracks(battle.Db, definition)
                    + (available.Contains(index) ? "" : "〔已禁用〕"));
            }
            while (true)
            {
                string line = readLine("请选择 " + battle.PickCount + " 个原始号位，按出战顺序用逗号或空格分隔，首位为首发（q 退出）> ");
                if (line == null || line.Trim().Equals("q", StringComparison.OrdinalIgnoreCase)) return null;
                var parts = line.Trim().Split(new[] { ',', ' ', '\t', '，' }, StringSplitOptions.RemoveEmptyEntries);
                var selected = new List<int>();
                foreach (string part in parts)
                {
                    if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out int number)
                        || !available.Contains(number - 1) || selected.Contains(number - 1)) break;
                    selected.Add(number - 1);
                }
                if (selected.Count == battle.PickCount && parts.Length == battle.PickCount) return selected;
                Console.WriteLine("选择无效：必须恰好选择 " + battle.PickCount + " 个不同的可选原始号位，不能选择已禁用成员。");
            }
        }

        static string ReadPumpedLine(ConsoleInput input, string prompt, Action pump, Func<bool> expired)
        {
            Console.Write(prompt);
            input.Start();
            while (true)
            {
                if (input.EndRequested) return null;
                pump();
                if (expired()) throw new ChoiceExpiredException();
                if (input.TryTake(out string line)) return line;
                Thread.Sleep(10);
            }
        }
    }
}
