using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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
                var viewer = new SpectatorClient(db, connection);
                Console.WriteLine("已连接观战，等待房主发送延迟数据……（Ctrl+C 退出）");
                Console.Out.Flush();
                int logIndex = 0, hashEventCount = -1;
                bool started = false;
                while (true)
                {
                    viewer.Poll();
                    if (viewer.Error != null) throw new IOException("观战失败：" + viewer.Error);
                    var battle = viewer.Battle;
                    if (battle != null)
                    {
                        if (!started)
                        {
                            started = true;
                            Console.WriteLine("观战开始；种子：" + battle.Seed);
                            PrintTeams(battle);
                        }
                        PrintNewLog(battle, ref logIndex);
                        if (options.Hash && !viewer.IsFinished && battle.Record.Events.Count != hashEventCount)
                        {
                            hashEventCount = battle.Record.Events.Count;
                            Console.WriteLine("观战状态哈希（第 " + battle.Turn + " 回合）："
                                + battle.StateHash().ToString("x16", CultureInfo.InvariantCulture));
                        }
                    }
                    if (viewer.IsFinished)
                    {
                        if (battle == null || battle.Phase != Phase.Over)
                            throw new IOException("观战结束标记缺少完整的终局。");
                        Console.WriteLine("观战结束：" + (battle.Winner < 0 ? "平局" : battle.Sides[battle.Winner].Name + " 获胜"));
                        Console.WriteLine("最终状态哈希：" + battle.StateHash().ToString("x16", CultureInfo.InvariantCulture));
                        PrintTeams(battle);
                        Console.WriteLine(BattleSummary.From(battle).ToText());
                        return 0;
                    }
                    Thread.Sleep(10);
                }
            }
        }

        sealed class SpectatorHost : IDisposable
        {
            readonly TcpHost listener;
            readonly List<TcpTransport> viewers = new List<TcpTransport>();
            SpectatorFeed feed;

            internal SpectatorHost(int port)
            {
                listener = TcpTransport.Listen(port, out int actualPort);
                Console.WriteLine("观战端口：" + actualPort);
                Console.WriteLine("观战延迟：2 回合");
                Console.Out.Flush();
            }

            internal void Start(Battle battle)
            {
                feed = new SpectatorFeed(battle, 2);
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
