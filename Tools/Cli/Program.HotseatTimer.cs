using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using StreamDuel.Core;

namespace StreamDuel.Cli
{
    internal static partial class Program
    {
        private sealed class HotseatChoiceExpiredException : Exception { }

        private sealed class HotseatTimedInput
        {
            private readonly Stopwatch clock = Stopwatch.StartNew();
            private readonly StringBuilder text = new StringBuilder();
            private Task<string> pending;
            private double previous;
            private bool needsSynchronization;

            public void ResetClock() { previous = clock.Elapsed.TotalSeconds; }

            public bool Synchronize(HotseatSession session)
            {
                if (!needsSynchronization) return true;
                Console.WriteLine("超时前的输入已失效；请输入“继续”重新确认（q 退出），旧数字或空回车不会提交选择或确认交接。");
                while (true)
                {
                    Console.Write("重新确认 > ");
                    string line = ReadLine(session);
                    if (line == null || line.Trim().Equals("q", StringComparison.OrdinalIgnoreCase)) return false;
                    if (line.Trim() == "继续")
                    {
                        needsSynchronization = false;
                        return true;
                    }
                    Console.WriteLine("已忽略过期输入；请输入“继续”，或 q 退出。");
                }
            }

            public string ReadLine(HotseatSession session)
            {
                Console.Out.Flush();
                while (true)
                {
                    double now = clock.Elapsed.TotalSeconds;
                    var timeouts = session.Tick(now - previous);
                    previous = now;
                    if (timeouts.Count > 0)
                    {
                        Console.WriteLine();
                        foreach (var timeout in timeouts)
                        {
                            var name = session.ViewFor(timeout.Side).Sides[timeout.Side].Name;
                            Console.WriteLine(name + "：" + timeout.Text);
                        }
                        text.Clear();
                        // ReadLine 不能安全取消；保留唯一读取者，由明确的确认词隔离迟到的旧行。
                        needsSynchronization = true;
                        if (!Console.IsInputRedirected)
                            while (Console.KeyAvailable) Console.ReadKey(intercept: true);
                        throw new HotseatChoiceExpiredException();
                    }
                    if (TryRead(out string line))
                    {
                        Console.WriteLine();
                        return line;
                    }
                    Thread.Sleep(10);
                }
            }

            private bool TryRead(out string line)
            {
                line = null;
                if (Console.IsInputRedirected)
                {
                    if (pending == null) pending = Task.Run(() => Console.ReadLine());
                    if (!pending.IsCompleted) return false;
                    line = pending.GetAwaiter().GetResult();
                    pending = null;
                    return true;
                }
                while (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(intercept: true);
                    if (key.Key == ConsoleKey.Enter)
                    {
                        line = text.ToString();
                        text.Clear();
                        return true;
                    }
                    if ((key.Modifiers & ConsoleModifiers.Control) != 0
                        && (key.Key == ConsoleKey.D || key.Key == ConsoleKey.Z))
                    {
                        text.Clear();
                        return true;
                    }
                    if (key.Key == ConsoleKey.Backspace)
                    {
                        if (text.Length > 0) text.Length--;
                    }
                    else if (!char.IsControl(key.KeyChar)) text.Append(key.KeyChar);
                }
                return false;
            }
        }

        private static string ReadHotseatLine(HotseatSession session, HotseatTimedInput input)
        {
            return input == null ? ReadHotseatLine() : input.ReadLine(session);
        }
    }
}
