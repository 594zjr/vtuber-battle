using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using StreamDuel.Core;

namespace StreamDuel.Cli
{
    internal static partial class Program
    {
        private sealed class MatchSpectatorHost : IDisposable
        {
            private readonly TcpHost listener;
            private readonly List<TcpTransport> viewers = new List<TcpTransport>();
            private readonly SpectatorMatchFeed feed;

            public MatchSpectatorHost(Ruleset ruleset, int port, bool chatEnabled)
            {
                feed = new SpectatorMatchFeed(ruleset, 2, chatEnabled);
                listener = TcpTransport.Listen(port, out int actualPort);
                Console.WriteLine("观战端口：" + actualPort);
                Console.WriteLine("观战延迟：2 回合；同一连接连续观看整场比赛。");
                Console.Out.Flush();
            }

            public void StartGame(Battle battle)
            {
                feed.StartGame(battle);
                Pump();
            }

            public void Pump()
            {
                for (int accepted = 0; accepted < 32; accepted++)
                {
                    var viewer = listener.Accept(0);
                    if (viewer == null) break;
                    viewers.Add(viewer);
                    feed.AddViewer(viewer);
                    Console.WriteLine("观众已连接（累计 " + viewers.Count + " 位）。");
                    Console.Out.Flush();
                }
                feed.Pump();
            }

            public void FinishGame()
            {
                Pump();
                feed.FinishGame();
            }

            public void Finish()
            {
                Pump();
                feed.Finish();
            }

            public void Dispose()
            {
                listener.Dispose();
                foreach (var viewer in viewers) viewer.Dispose();
            }
        }

        private sealed class MatchSpectatorDisplay
        {
            private bool announced;
            private int gameNumber, completedIndex, logIndex, hashEventCount = -1;
            private Battle provisionalBattle;
            private int provisionalLogIndex;

            public void Render(SpectatorClient viewer, CliOptions options, Battle previousBattle, int previousLogIndex)
            {
                if (!announced)
                {
                    announced = true;
                    provisionalBattle = previousBattle;
                    provisionalLogIndex = previousLogIndex;
                    Console.WriteLine("观战赛制：" + TextInput.Clean(viewer.MatchName, TextInputKind.Note).Text
                        + "；最多 " + viewer.BestOf + " 局。");
                    if (previousBattle == null) PrintSpectatorChatHelp(viewer);
                }
                // 一次 Poll 可以收到多局；逐个终态渲染，不能只显示最后一个 Battle。
                while (completedIndex < viewer.CompletedGames.Count)
                {
                    var result = viewer.CompletedGames[completedIndex++];
                    var battle = result.Battle;
                    if (gameNumber != result.GameNumber)
                    {
                        int[] beforeWins = result.Wins;
                        if (result.Winner >= 0) beforeWins[result.Winner]--;
                        BeginGame(battle, result.GameNumber, beforeWins);
                    }
                    PrintNewLog(battle, ref logIndex);
                    var display = TextInput.ForDisplay(battle);
                    Console.WriteLine("第 " + result.GameNumber + " 局结束："
                        + (result.Winner < 0 ? "平局" : display.Sides[result.Winner].Name + " 获胜")
                        + "；比分 " + result.Wins[0] + ":" + result.Wins[1] + "；平局 " + result.Draws);
                    Console.WriteLine("第 " + result.GameNumber + " 局最终状态哈希："
                        + battle.StateHash().ToString("x16", CultureInfo.InvariantCulture));
                    PrintTeams(display);
                    Console.WriteLine(BattleSummary.From(display).ToText());
                }
                var current = viewer.Battle;
                if (current != null && gameNumber != viewer.GameNumber)
                    BeginGame(current, viewer.GameNumber, viewer.ReleasedWins);
                if (current != null && current.Phase != Phase.Over)
                {
                    PrintNewLog(current, ref logIndex);
                    if (options.Hash && current.Record.Events.Count != hashEventCount)
                    {
                        hashEventCount = current.Record.Events.Count;
                        Console.WriteLine("第 " + viewer.GameNumber + " 局观战状态哈希（第 " + current.Turn + " 回合）："
                            + current.StateHash().ToString("x16", CultureInfo.InvariantCulture));
                    }
                }
                Console.Out.Flush();
            }

            private void BeginGame(Battle battle, int number, int[] wins)
            {
                gameNumber = number;
                logIndex = 0;
                hashEventCount = -1;
                Console.WriteLine("第 " + number + " 局 比分 " + wins[0] + ":" + wins[1]);
                // 能力登记可能晚于旧开局；接管已展示的同局，不重复介绍或已读日志。
                if (provisionalBattle != null && provisionalBattle.Seed == battle.Seed)
                {
                    logIndex = provisionalLogIndex;
                    hashEventCount = provisionalBattle.Record.Events.Count;
                    provisionalBattle = null;
                    return;
                }
                provisionalBattle = null;
                Console.WriteLine("观战开始；种子：" + battle.Seed);
                PrintTeams(TextInput.ForDisplay(battle));
                PrintMixedNotices(battle.Db, battle.Sides.SelectMany(side => side.Team).Select(fighter => fighter.Def.Id));
            }
        }

        private static void PrintSpectatorChatHelp(SpectatorClient viewer)
        {
            Console.WriteLine("观众昵称：" + viewer.DisplayName + "；快捷语只发给其他观众，房主可关闭转发。");
            Console.WriteLine("输入 /say 编号；每个可见回合最多 3 条。q 退出；输入结束后仍继续观战。");
            for (int index = 0; index < QuickChat.Count; index++)
                Console.WriteLine("  /say " + index + "：" + QuickChat.Text(index));
        }
    }
}
