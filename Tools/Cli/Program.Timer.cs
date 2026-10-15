using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using StreamDuel.Core;

namespace StreamDuel.Cli
{
    internal static partial class Program
    {
        private sealed class ChoiceExpiredException : Exception { }

        private sealed class ConsoleInput
        {
            private Task<string> pending;
            private bool stopped;

            public void Start()
            {
                // 超时不取消阻塞读取，下一阶段复用它，避免多个 ReadLine 争抢同一行。
                if (pending == null && !stopped) pending = Task.Run(() => Console.ReadLine());
            }

            public bool EndRequested
            {
                get
                {
                    if (pending != null && pending.IsCompleted)
                    {
                        string line = pending.GetAwaiter().GetResult();
                        if (line == null || line.Trim().Equals("q", StringComparison.OrdinalIgnoreCase)) stopped = true;
                    }
                    return stopped;
                }
            }

            public bool TryTake(out string line)
            {
                line = null;
                if (pending == null || !pending.IsCompleted) return false;
                line = pending.GetAwaiter().GetResult();
                pending = null;
                if (line == null || line.Trim().Equals("q", StringComparison.OrdinalIgnoreCase)) stopped = true;
                return true;
            }

            public void DiscardCompleted()
            {
                // 已完成的旧选择不跨阶段提交；EOF 和退出请求仍然有效。
                if (!EndRequested && pending != null && pending.IsCompleted) pending = null;
            }
        }

        private sealed class ClockDisplay
        {
            private string previous;

            public void Show(TurnClock clock, Battle battle, int mySide)
            {
                if (clock == null || battle.Phase == Phase.Over) return;
                string text = "计时（第 " + battle.Turn + " 回合 · "
                    + (battle.Phase == Phase.Replace ? "补位" : "选招") + "）：本方基本 "
                    + Seconds(clock.TurnLeft(mySide)) + " 秒 / 总时间 " + Seconds(clock.BankLeft(mySide))
                    + " 秒；对方基本 " + Seconds(clock.TurnLeft(1 - mySide)) + " 秒 / 总时间 "
                    + Seconds(clock.BankLeft(1 - mySide)) + " 秒";
                if (text == previous) return;
                previous = text;
                Console.WriteLine(text);
            }

            private static string Seconds(double seconds)
            {
                return Math.Ceiling(seconds).ToString("0", CultureInfo.InvariantCulture);
            }
        }

        private sealed class LocalTimer
        {
            private readonly TurnClock clock;
            private readonly Battle battle;
            private readonly Stopwatch elapsed = Stopwatch.StartNew();
            private readonly ClockDisplay display = new ClockDisplay();

            public LocalTimer(TurnClock clock, Battle battle)
            {
                this.clock = clock;
                this.battle = battle;
                clock.StartTurn();
                if (battle.Phase == Phase.Replace)
                    for (int side = 0; side < 2; side++)
                        if (!battle.Need[side]) clock.Submit(side);
            }

            public void Pump()
            {
                double seconds = elapsed.Elapsed.TotalSeconds;
                elapsed.Restart();
                clock.Tick(0, seconds);
                clock.Tick(1, seconds);
                display.Show(clock, battle, 0);
            }
        }

        private readonly struct ChoiceStamp
        {
            private readonly int turn;
            private readonly Phase phase;
            private readonly int events;

            public ChoiceStamp(Battle battle)
            {
                turn = battle.Turn;
                phase = battle.Phase;
                events = battle.Record.Events.Count;
            }

            public bool Matches(Battle battle)
            {
                return turn == battle.Turn && phase == battle.Phase && events == battle.Record.Events.Count;
            }
        }

        private sealed class NetworkPump
        {
            private readonly NetSession session;
            private readonly Stopwatch elapsed = Stopwatch.StartNew();
            private readonly ClockDisplay display = new ClockDisplay();
            private long observedTimerRevision;

            public NetworkPump(NetSession session)
            {
                this.session = session;
                observedTimerRevision = session.TimerRevision;
            }

            public void Pump()
            {
                double seconds = elapsed.Elapsed.TotalSeconds;
                elapsed.Restart();
                var beforePoll = new ChoiceStamp(session.Battle);
                NetState oldState = session.State;
                long timerRevision = session.TimerRevision;
                session.Poll();
                CheckNetwork(session);
                // 刚通过哈希确认开放的新阶段，不承担前一阶段等待所花的时间。
                if (observedTimerRevision != timerRevision || timerRevision != session.TimerRevision || !beforePoll.Matches(session.Battle)
                    || oldState == NetState.Waiting && session.State != oldState)
                {
                    seconds = 0;
                    elapsed.Restart();
                }
                bool choosing = !session.HasSubmitted && (session.State == NetState.Choose || session.State == NetState.Replace);
                Phase phase = session.Battle.Phase;
                var beforeTick = new ChoiceStamp(session.Battle);
                timerRevision = session.TimerRevision;
                session.Tick(seconds);
                CheckNetwork(session);
                if (timerRevision != session.TimerRevision) elapsed.Restart();
                observedTimerRevision = session.TimerRevision;
                if (choosing && (session.HasSubmitted || !beforeTick.Matches(session.Battle)))
                    PrintTimeout(session.Battle, session.MySide, phase == Phase.Replace);
                display.Show(session.Clock, session.Battle, session.MySide);
            }
        }

        private static int? ReadPumpedNumber(ConsoleInput input, string prompt, Action pump, Func<bool> expired)
        {
            while (true)
            {
                Console.Write(prompt);
                input.Start();
                while (true)
                {
                    if (input.EndRequested) return null;
                    pump();
                    if (expired()) throw new ChoiceExpiredException();
                    if (input.TryTake(out string line))
                    {
                        if (line == null || line.Trim().Equals("q", StringComparison.OrdinalIgnoreCase)) return null;
                        if (int.TryParse(line.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int value)) return value;
                        Console.WriteLine("请输入数字，或输入 q 退出。");
                        break;
                    }
                    Thread.Sleep(10);
                }
            }
        }

        private static void PrintTimeout(Battle battle, int side, bool replacement)
        {
            Console.WriteLine(TextInput.Clean(battle.Sides[side].Name, TextInputKind.PlayerName).Text + (replacement
                ? "：补位超时，提交编号最小的可上场队友。"
                : "：超时，提交默认跳过（按当前状态结算）。"));
        }

        private static bool ReadTimedActions(Battle battle, CliOptions options, TurnClock clock, ConsoleInput input,
            out BattleAction action, out BattleAction foeAction)
        {
            action = foeAction = default;
            var timer = new LocalTimer(clock, battle);
            var actions = new BattleAction[2];
            if (!options.Auto)
            {
                input.DiscardCompleted();
                input.Start();
            }
            foreach (int side in options.Auto ? new[] { 0, 1 } : new[] { 1, 0 })
            {
                if (!options.Auto && input.EndRequested) return false;
                timer.Pump();
                if (!clock.TimedOut(side))
                {
                    if (side != 0 || options.Auto) actions[side] = Ai.Choose(battle, side, options.Level);
                    else
                    {
                        try
                        {
                            var selected = ReadAction(battle, side,
                                prompt => ReadPumpedNumber(input, prompt, timer.Pump, () => clock.TimedOut(side)));
                            if (!selected.HasValue) return false;
                            actions[side] = selected.Value;
                        }
                        catch (ChoiceExpiredException) { }
                    }
                    timer.Pump();
                }
                if (clock.TimedOut(side))
                {
                    actions[side] = TurnClock.TimeoutAction(battle, side);
                    PrintTimeout(battle, side, false);
                }
                clock.Submit(side);
                if (side == 0) input.DiscardCompleted();
            }
            action = actions[0];
            foeAction = actions[1];
            return true;
        }

        private static bool ReadTimedReplacements(Battle battle, CliOptions options, TurnClock clock, ConsoleInput input)
        {
            var timer = new LocalTimer(clock, battle);
            var choices = new[] { -1, -1 };
            if (!options.Auto && battle.Need[0])
            {
                input.DiscardCompleted();
                input.Start();
            }
            foreach (int side in options.Auto ? new[] { 0, 1 } : new[] { 1, 0 })
            {
                if (!battle.Need[side]) continue;
                if (!options.Auto && input.EndRequested) return false;
                timer.Pump();
                if (!clock.TimedOut(side))
                {
                    if (side != 0 || options.Auto) choices[side] = Ai.ChooseReplacement(battle, side, options.Level);
                    else
                    {
                        try
                        {
                            var selected = ReadReplacement(battle, side,
                                prompt => ReadPumpedNumber(input, prompt, timer.Pump, () => clock.TimedOut(side)));
                            if (!selected.HasValue) return false;
                            choices[side] = selected.Value;
                        }
                        catch (ChoiceExpiredException) { }
                    }
                    timer.Pump();
                }
                if (clock.TimedOut(side))
                {
                    choices[side] = TurnClock.TimeoutReplacement(battle, side);
                    PrintTimeout(battle, side, true);
                }
                clock.Submit(side);
                if (side == 0) input.DiscardCompleted();
            }
            for (int side = 0; side < 2 && battle.Phase == Phase.Replace; side++)
                if (battle.Need[side]) battle.Replace(side, choices[side]);
            return true;
        }
    }
}
