using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using StreamDuel.Core;

namespace StreamDuel.Cli
{
    internal static partial class Program
    {
        static TcpTransport WaitForNetworkGuest(ContentDb db, CliOptions options, Action poll = null)
        {
            var nameInput = options.LanHostName == null ? null
                : TextInput.Clean(options.LanHostName, TextInputKind.RoomName);
            if (nameInput != null && nameInput.Changed)
                Console.WriteLine("房间名提示：" + nameInput.Message);
            using (var listener = TcpTransport.Listen(options.HostPort.Value, out int actualPort))
            {
                LanRoom room = options.LanHostName == null ? null : new LanRoom
                {
                    Name = nameInput.Text,
                    Port = actualPort,
                    Protocol = NetSession.TerminationProtocolVersion,
                    ContentHash = db.ContentHash(),
                    Ruleset = "casual"
                };
                using (var beacon = room == null ? null : new LanBeaconSender(room))
                {
                    if (beacon != null)
                    {
                        if (beacon.Error != null) throw new IOException(beacon.Error);
                        var advertised = LanBeacon.Decode(LanBeacon.Encode(room));
                        if (!string.Equals(room.Name, advertised.Name, StringComparison.Ordinal))
                            Console.WriteLine("房间名提示：局域网广播沿用现有长度限制，房名已进一步缩短。");
                        Console.WriteLine("局域网房间：" + advertised.DisplayName);
                        Console.WriteLine("广播端口：" + LanBeacon.Port + "（UDP，每秒一次）");
                    }
                    Console.WriteLine("监听端口：" + actualPort);
                    Console.WriteLine("等待对手连接……（Ctrl+C 退出）");
                    Console.Out.Flush();
                    using (var cancellation = new LanCancellation())
                    {
                        var timer = Stopwatch.StartNew();
                        double previous = 0;
                        while (!cancellation.IsCancelled)
                        {
                            poll?.Invoke();
                            double now = timer.Elapsed.TotalSeconds;
                            if (beacon != null)
                            {
                                beacon.Tick(now - previous);
                                if (beacon.Error != null) throw new IOException(beacon.Error);
                            }
                            previous = now;
                            var connection = listener.Accept(100);
                            if (connection == null) continue;
                            if (!cancellation.IsCancelled) return connection;
                            connection.Dispose();
                        }
                        return null;
                    }
                }
            }
        }

        static int ListLanRooms()
        {
            using (var listener = new LanBeaconListener())
            {
                if (listener.Error != null) throw new IOException(listener.Error);
                Console.WriteLine("正在搜索局域网房间（5 秒）……");
                Console.Out.Flush();
                using (var cancellation = new LanCancellation())
                {
                    var timer = Stopwatch.StartNew();
                    var observed = new Dictionary<string, LanRoom>(StringComparer.Ordinal);
                    while (!cancellation.IsCancelled && timer.Elapsed.TotalSeconds < 5)
                    {
                        double now = timer.Elapsed.TotalSeconds;
                        listener.Poll(now);
                        if (listener.Error != null) throw new IOException(listener.Error);
                        // 保留本次搜索窗口内的发现记录，结束时再区分仍在线与已过期的房间。
                        foreach (var room in listener.RoomList.Rooms(now))
                            observed[room.Address + ":" + room.Port.ToString(CultureInfo.InvariantCulture)] = room;
                        Thread.Sleep(20);
                    }
                    if (cancellation.IsCancelled)
                    {
                        Console.WriteLine("已取消局域网搜索。");
                        return 0;
                    }
                    double finished = timer.Elapsed.TotalSeconds;
                    Console.WriteLine("发现 " + observed.Count + " 个房间（本次 5 秒内的记录）：");
                    foreach (var room in observed.Values.OrderBy(room => room.DisplayName, StringComparer.Ordinal)
                        .ThenBy(room => room.Address, StringComparer.Ordinal).ThenBy(room => room.Port))
                    {
                        string endpoint = room.Address + ":" + room.Port.ToString(CultureInfo.InvariantCulture);
                        var nameInput = room.NameInput;
                        Console.WriteLine("房间：" + nameInput.Text);
                        if (nameInput.Changed) Console.WriteLine("  房间名提示：" + nameInput.Message);
                        Console.WriteLine("  地址：" + endpoint + "；协议：" + room.Protocol
                            + "；规则：" + LanText(room.Ruleset) + "；内容指纹：" + LanText(room.ContentHash));
                        if (finished - room.LastSeen > 3)
                            Console.WriteLine("  状态：已失联（超过 3 秒未收到广播）");
                        else if (room.Protocol != NetSession.TerminationProtocolVersion)
                            Console.WriteLine("  状态：协议版本不兼容，当前版本为 " + NetSession.TerminationProtocolVersion + "。");
                        else
                        {
                            Console.WriteLine("  状态：可见（加入时仍会校验内容一致性）");
                            Console.WriteLine("  加入：--join " + endpoint);
                        }
                    }
                    if (observed.Count == 0) Console.WriteLine("暂未发现房间；请确认房主仍在等待连接，并处于同一局域网。");
                    return 0;
                }
            }
        }

        static string LanText(string text)
        {
            var result = new StringBuilder();
            foreach (char character in text)
                result.Append(char.IsControl(character) || char.GetUnicodeCategory(character) == UnicodeCategory.Format
                    ? ' ' : character);
            return result.ToString();
        }

        sealed class LanCancellation : IDisposable
        {
            int cancelled;

            public bool IsCancelled { get { return Volatile.Read(ref cancelled) != 0; } }

            public LanCancellation() { Console.CancelKeyPress += Cancel; }

            void Cancel(object sender, ConsoleCancelEventArgs args)
            {
                // 让调用线程退出 using 范围，释放广播和监听套接字。
                args.Cancel = true;
                Interlocked.Exchange(ref cancelled, 1);
            }

            public void Dispose() { Console.CancelKeyPress -= Cancel; }
        }
    }
}
