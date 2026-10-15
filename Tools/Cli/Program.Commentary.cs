using System;
using System.Runtime.CompilerServices;
using StreamDuel.Core;

namespace StreamDuel.Cli
{
    internal static partial class Program
    {
        sealed class CommentaryOutput
        {
            public int NextEvent;
        }

        static readonly ConditionalWeakTable<Battle, CommentaryOutput> commentaryOutput
            = new ConditionalWeakTable<Battle, CommentaryOutput>();

        static void PrintCommentary(Battle battle, int throughTurn = int.MaxValue)
        {
            var output = commentaryOutput.GetValue(battle, _ => new CommentaryOutput());
            int completedTurn = battle.Phase == Phase.Over ? battle.Turn : battle.Turn - 1;
            completedTurn = Math.Min(completedTurn, throughTurn);
            int end = output.NextEvent;
            // 同回合可能分多次输出日志，等结算完成后才能确定全回合优先级最高的两条。
            while (end < battle.Events.Count && battle.Events[end].Turn <= completedTurn) end++;
            if (end == output.NextEvent) return;
            foreach (var line in Commentary.For(battle, output.NextEvent, end))
            {
                int turn = battle.Events[line.EventIndex].Turn;
                Console.WriteLine("【解说 · 第 " + turn + " 回合】" + line.Text);
            }
            output.NextEvent = end;
        }
    }
}
