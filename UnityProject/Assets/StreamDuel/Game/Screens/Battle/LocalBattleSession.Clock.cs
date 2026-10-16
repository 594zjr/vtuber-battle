using System;
using System.Diagnostics;
using System.Globalization;
using StreamDuel.Core;

namespace StreamDuel.Game
{
    public sealed partial class LocalBattleSession
    {
        TurnClock teamClock;
        Battle teamClockBattle;
        int teamClockTurn;
        Phase teamClockPhase;
        bool teamClockDecisionOpen;
        bool teamClockNeedsPlayer, teamClockNeedsComputer;
        bool teamClockComputerReady;
        BattleAction teamClockComputerAction;
        int teamClockComputerReplacement = -1;
        long teamClockTimestamp;

        void PauseTeamClock() { teamClockTimestamp = Stopwatch.GetTimestamp(); }

        void ResetTeamClock()
        {
            teamClockBattle = b;
            teamClock = teamRuleset != null && (teamRuleset.TurnSeconds > 0 || teamRuleset.BankSeconds > 0)
                ? new TurnClock(teamRuleset.TurnSeconds, teamRuleset.BankSeconds) : null;
            teamClockDecisionOpen = false;
            teamClockComputerReady = false;
            teamClockNeedsPlayer = teamClockNeedsComputer = false;
            teamClockComputerReplacement = -1;
            teamClockTimestamp = Stopwatch.GetTimestamp();
        }

        bool TickTeamClock()
        {
            if (teamRuleset == null) return false;
            PumpTeamClock(null, null, null);
            return true;
        }

        bool SubmitTeamClockAction(BattleAction action)
        {
            if (teamRuleset == null) return false;
            PumpTeamClock(null, action, null);
            return true;
        }

        bool SubmitTeamClockReplacement(int index)
        {
            if (teamRuleset == null) return false;
            PumpTeamClock(null, null, index);
            return true;
        }

        bool AfterTeamClockPlayback()
        {
            if (teamRuleset == null) return false;
            // 逐行播放和跳过播放都不能把已播放的时间扣到新开放的输入窗口。
            teamClockTimestamp = Stopwatch.GetTimestamp();
            PumpTeamClock(null, null, null);
            return true;
        }

        internal void AdvanceTeamClockSmoke(double seconds)
        {
            if (!SmokeRequested) throw new InvalidOperationException("计时推进仅供隔离验收使用。");
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0)
                throw new ArgumentOutOfRangeException(nameof(seconds), "推进时间必须是有限的非负秒数。");
            if (teamRuleset == null) throw new InvalidOperationException("尚未开启显式赛制对战。");
            PumpTeamClock(seconds, null, null);
        }

        void PumpTeamClock(double? elapsedSeconds, BattleAction? action, int? replacement)
        {
            if (!ReferenceEquals(teamClockBattle, b)) ResetTeamClock();
            if (!isActiveAndEnabled || b == null || playing || (b.Phase != Phase.Choose && b.Phase != Phase.Replace))
            {
                teamClockTimestamp = Stopwatch.GetTimestamp();
                return;
            }

            bool opened = OpenTeamClockDecision();
            if (elapsedSeconds.HasValue) teamClockTimestamp = Stopwatch.GetTimestamp();
            else if (!opened) AccrueTeamClockTime();

            PrepareTeamClockComputer();
            if (elapsedSeconds.HasValue)
            {
                AdvanceTeamClockSeconds(elapsedSeconds.Value);
                teamClockTimestamp = Stopwatch.GetTimestamp();
            }

            bool expired = teamClock != null && teamClock.TimedOut(0);
            if (teamClockPhase == Phase.Choose)
            {
                if (!expired && !action.HasValue) return;
                var selected = expired ? TurnClock.TimeoutAction(b, 0) : action.Value;
                teamClock?.Submit(0);
                b.ResolveTurn(selected, teamClockComputerAction);
                CloseTeamClockDecision();
                Play();
                return;
            }

            if (teamClockNeedsPlayer && !expired && !replacement.HasValue) return;
            int selectedReplacement = -1;
            if (teamClockNeedsPlayer)
            {
                selectedReplacement = expired ? TurnClock.TimeoutReplacement(b, 0) : replacement.Value;
                if (selectedReplacement < 0)
                    throw new InvalidOperationException("补位超时，但本方没有可上场的队友。");
                teamClock?.Submit(0);
            }

            // 同一补位阶段先收齐选择，避免先补位的一方使另一方重新得到基本时间。
            bool replacePlayer = teamClockNeedsPlayer, replaceComputer = teamClockNeedsComputer;
            int computerReplacement = teamClockComputerReplacement;
            if (replacePlayer && b.Phase == Phase.Replace && b.Need[0]) b.Replace(0, selectedReplacement);
            if (replaceComputer && b.Phase == Phase.Replace && b.Need[1]) b.Replace(1, computerReplacement);
            CloseTeamClockDecision();
            Play();
        }

        bool OpenTeamClockDecision()
        {
            if (teamClockDecisionOpen && teamClockTurn == b.Turn && teamClockPhase == b.Phase) return false;
            teamClockDecisionOpen = true;
            teamClockTurn = b.Turn;
            teamClockPhase = b.Phase;
            teamClockNeedsPlayer = b.Phase == Phase.Choose || b.Need[0];
            teamClockNeedsComputer = b.Phase == Phase.Choose || b.Need[1];
            teamClockComputerReady = false;
            teamClockComputerReplacement = -1;
            teamClock?.StartTurn();
            if (!teamClockNeedsPlayer) teamClock?.Submit(0);
            if (!teamClockNeedsComputer) teamClock?.Submit(1);
            teamClockTimestamp = Stopwatch.GetTimestamp();
            return true;
        }

        void PrepareTeamClockComputer()
        {
            if (teamClockComputerReady || !teamClockNeedsComputer) return;
            bool expired = teamClock != null && teamClock.TimedOut(1);
            AiLevel level = settings == null ? AiLevel.Normal : settings.AiLevel;
            if (teamClockPhase == Phase.Choose)
            {
                var selected = expired ? TurnClock.TimeoutAction(b, 1) : Ai.Choose(b, 1, level);
                // 电脑选择在主线程同步完成，也属于其尚未提交时经过的时间。
                AccrueTeamClockTime();
                teamClockComputerAction = teamClock != null && teamClock.TimedOut(1)
                    ? TurnClock.TimeoutAction(b, 1) : selected;
            }
            else
            {
                int selected = expired ? TurnClock.TimeoutReplacement(b, 1) : Ai.ChooseReplacement(b, 1, level);
                AccrueTeamClockTime();
                teamClockComputerReplacement = teamClock != null && teamClock.TimedOut(1)
                    ? TurnClock.TimeoutReplacement(b, 1) : selected;
                if (teamClockComputerReplacement < 0)
                    throw new InvalidOperationException("补位超时，但电脑没有可上场的队友。");
            }
            teamClockComputerReady = true;
            teamClock?.Submit(1);
        }

        void CloseTeamClockDecision()
        {
            // 结算可在同一回合再次要求补位，不能仅靠 Turn/Phase 判断是否出现新选择。
            teamClockDecisionOpen = false;
            teamClockTimestamp = Stopwatch.GetTimestamp();
        }

        void AccrueTeamClockTime()
        {
            long now = Stopwatch.GetTimestamp();
            double seconds = Math.Max(0, (now - teamClockTimestamp) / (double)Stopwatch.Frequency);
            teamClockTimestamp = now;
            AdvanceTeamClockSeconds(seconds);
        }

        void AdvanceTeamClockSeconds(double seconds)
        {
            if (teamClock == null) return;
            teamClock.Tick(0, seconds);
            teamClock.Tick(1, seconds);
        }

        string TeamClockText()
        {
            if (teamClock == null) return "赛制计时：关闭";
            string suffix = playing ? "（播放暂停计时）" : b != null && b.Phase == Phase.Over ? "（本局结束）" : "";
            return "本方基本 " + TeamClockSeconds(teamClock.TurnLeft(0))
                + " 秒 / 总时间 " + TeamClockSeconds(teamClock.BankLeft(0))
                + " 秒；电脑基本 " + TeamClockSeconds(teamClock.TurnLeft(1))
                + " 秒 / 总时间 " + TeamClockSeconds(teamClock.BankLeft(1)) + " 秒" + suffix;
        }

        static string TeamClockSeconds(double seconds)
        {
            return Math.Ceiling(seconds).ToString("0", CultureInfo.InvariantCulture);
        }
    }
}
