using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using StreamDuel.Core;

namespace StreamDuel.Cli
{
    internal static partial class Program
    {
        static int WatchBattle(ContentDb db, CliOptions options)
        {
            Console.WriteLine("开播对决 · 只读观战（仅用于可信局域网）");
            Console.WriteLine("内容包：" + db.PackName);
            Console.WriteLine(db.PackNotice);
            using (var connection = TcpTransport.Connect(options.WatchHost, options.WatchPort, 10000))
            {
                var viewer = new SpectatorClient(db, connection, enableChat: true, displayName: options.WatchName, enableMatch: true);
                Console.WriteLine("已连接观战，等待房主发送延迟数据……（Ctrl+C 退出）");
                Console.Out.Flush();
                int logIndex = 0, hashEventCount = -1, chatIndex = 0;
                bool started = false;
                Battle provisionalBattle = null;
                var input = new ConsoleInput();
                var matchDisplay = new MatchSpectatorDisplay();
                while (true)
                {
                    viewer.Poll();
                    if (viewer.Error != null) throw new IOException("观战失败：" + viewer.Error);
                    var battle = viewer.Battle;
                    if (viewer.HasMatch) matchDisplay.Render(viewer, options, provisionalBattle, logIndex);
                    else if (battle != null)
                    {
                        if (!started)
                        {
                            started = true;
                            Console.WriteLine("观战开始；种子：" + battle.Seed);
                            PrintTeams(TextInput.ForDisplay(battle));
                            PrintMixedNotices(db, battle.Sides.SelectMany(side => side.Team).Select(fighter => fighter.Def.Id));
                            PrintSpectatorChatHelp(viewer);
                        }
                        PrintNewLog(battle, ref logIndex);
                        provisionalBattle = battle;
                        if (options.Hash && !viewer.IsFinished && battle.Record.Events.Count != hashEventCount)
                        {
                            hashEventCount = battle.Record.Events.Count;
                            Console.WriteLine("观战状态哈希（第 " + battle.Turn + " 回合）："
                                + battle.StateHash().ToString("x16", CultureInfo.InvariantCulture));
                        }
                    }
                    while (chatIndex < viewer.ReceivedChats.Count)
                    {
                        var chat = viewer.ReceivedChats[chatIndex++];
                        Console.WriteLine("观众「" + chat.SenderName + "」：" + QuickChat.Text(chat.Index));
                    }
                    if (viewer.IsFinished)
                    {
                        if (viewer.HasMatch)
                        {
                            if (!viewer.IsMatchFinished) throw new IOException("观战比赛缺少整场结束标记。");
                            Console.WriteLine("观战比赛结束：最终比分 " + viewer.ReleasedWins[0] + ":" + viewer.ReleasedWins[1]
                                + "；平局 " + viewer.MatchDraws);
                            return 0;
                        }
                        if (battle == null || battle.Phase != Phase.Over)
                            throw new IOException("观战结束标记缺少完整的终局。");
                        var display = TextInput.ForDisplay(battle);
                        Console.WriteLine("观战结束：" + (display.Winner < 0 ? "平局" : display.Sides[display.Winner].Name + " 获胜"));
                        Console.WriteLine("最终状态哈希：" + battle.StateHash().ToString("x16", CultureInfo.InvariantCulture));
                        PrintTeams(display);
                        Console.WriteLine(BattleSummary.From(display).ToText());
                        return 0;
                    }
                    if (battle != null)
                    {
                        input.Start();
                        if (input.TryTake(out string line) && line != null)
                        {
                            if (line.Trim().Equals("q", StringComparison.OrdinalIgnoreCase))
                            {
                                Console.WriteLine("已退出观战。");
                                return 0;
                            }
                            SendSpectatorChat(viewer, line);
                        }
                    }
                    Thread.Sleep(10);
                }
            }
        }

        static void SendSpectatorChat(SpectatorClient viewer, string line)
        {
            var parts = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2 || !parts[0].Equals("/say", StringComparison.OrdinalIgnoreCase)
                || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int index)
                || index < 0 || index >= QuickChat.Count)
            {
                Console.WriteLine("观战只支持 /say 0～7 的固定短句，或 q 退出。");
                return;
            }
            Console.WriteLine(viewer.SendQuickChat(index)
                ? "观众快捷语已提交：" + QuickChat.Text(index)
                : "观众快捷语未发送：本回合已达上限、观战已结束或连接不可用。");
            Console.Out.Flush();
        }

        sealed class SpectatorHost : IDisposable
        {
            readonly TcpHost listener;
            readonly List<TcpTransport> viewers = new List<TcpTransport>();
            readonly bool chatEnabled;
            SpectatorFeed feed;

            internal SpectatorHost(int port, bool chatEnabled)
            {
                this.chatEnabled = chatEnabled;
                listener = TcpTransport.Listen(port, out int actualPort);
                Console.WriteLine("观战端口：" + actualPort);
                Console.WriteLine("观战延迟：2 回合");
                Console.Out.Flush();
            }

            internal void Start(Battle battle)
            {
                feed = new SpectatorFeed(battle, 2, chatEnabled);
                foreach (var viewer in viewers) feed.AddViewer(viewer);
                Pump();
            }

            internal void Pump()
            {
                // 等选手连接和等待人工输入期间也接收观众，早到的连接保留至握手完成。
                for (int accepted = 0; accepted < 32; accepted++)
                {
                    var viewer = listener.Accept(0);
                    if (viewer == null) break;
                    viewers.Add(viewer);
                    if (feed != null) feed.AddViewer(viewer);
                    Console.WriteLine("观众已连接（累计 " + viewers.Count + " 位）。");
                    Console.Out.Flush();
                }
                feed?.Pump();
            }

            internal void Finish()
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
    }
}
