using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using StreamDuel.Core;
using UnityEngine;

namespace StreamDuel.Game
{
    public sealed partial class LocalBattleSession : MonoBehaviour
    {
        public Action MainMenuRequested;
        public Action TeamScreenRequested;
        public Action<Battle> ResultRequested;
        ContentDb db;
        Battle b;
        List<string> pick = new List<string>();
        List<string>[] lastTeams;
        bool playing, timerOn;
        float timerLeft;
        TurnClock localClock;
        Battle localClockBattle;
        int localClockTurn;
        Phase localClockPhase;
        bool localClockDecisionOpen;

        public Battle CurrentBattle => b;
        public Battle CurrentTeamBattle => b;
        public GameSettings Settings => settings;
        public AiLevel CurrentTeamAiLevel => settings == null ? AiLevel.Normal : settings.AiLevel;
        public bool IsPlaybackPending => playing;
        public string ResultRematchLabel => teamMatch != null && !teamMatch.Over ? "继续下一局" : "再来一局";
        public string MatchScoreText => teamMatch == null ? ""
            : teamRuleset.Name + "：你 " + teamMatch.Wins[0] + " : " + teamMatch.Wins[1]
                + " 电脑；平局 " + teamMatch.Draws + "；已完成 " + teamMatchGames.Count
                + " 局" + (teamMatch.Over ? "（整场结束）" : "")
                + (string.IsNullOrEmpty(teamMatchError) ? "" : "\n" + teamMatchError);
        public string TimerText => teamRuleset != null ? TeamClockText()
            : !timerOn ? "不限时" : "本次选择 " + Math.Ceiling(timerLeft).ToString("0", CultureInfo.InvariantCulture)
                + " 秒" + (playing ? "（播放暂停计时）" : b?.Phase == Phase.Over ? "（本局结束）" : "");
        static bool SmokeRequested => GameRuntimeSettings.IsSmokeTest
            || Environment.GetCommandLineArgs().Contains("--battle-ui-smoke");

        void Awake() { enabled = false; }
        void OnEnable() { PauseTeamClock(); }
        void OnDisable() { PauseTeamClock(); }

        public void SetVisible(bool visible)
        {
            // 界面切换只暂停经过时间，不重建决策预算或再次抽取电脑行动。
            PauseTeamClock();
            enabled = visible;
        }

        public BattleScreenArgs CreateBattleScreenArgs()
        {
            return new BattleScreenArgs
            {
                Session = this, OriginalRequest = teamBattleRequest,
                Battle = b, Db = db, Settings = settings,
                Teams = lastTeams?.Select(team => (IList<string>)new List<string>(team)).ToArray(),
                Isolated = SmokeRequested, TimerRemaining = timerLeft
            };
        }

        public void RematchFromResult()
        {
            if (teamMatch != null) ContinueTeamMatch();
            else if (teamBattleRequest != null) StartTeamBattle(teamBattleRequest);
            else throw new InvalidOperationException("没有可重新开始的组队记录。");
        }

        public void ReturnToTeamFromResult() { RequestTeamScreen(); }
        public ContentDb FindLoadedPack(string packId) { return packs.FirstOrDefault(pack => pack.PackId == packId); }

        public void Tick(float dt, bool visible, bool busy)
        {
            if (!visible || !isActiveAndEnabled || b == null || busy || playing)
            {
                PauseTeamClock();
                return;
            }
            if (float.IsNaN(dt) || float.IsInfinity(dt) || dt < 0) return;
            if (TickTeamClock()) return;
            if (!timerOn || (b.Phase != Phase.Choose && b.Phase != Phase.Replace)) return;
            OpenLocalClock();
            if (b.Phase == Phase.Replace && !b.Need[0]) { AfterPlayback(); return; }
            localClock.Tick(0, dt);
            timerLeft = (float)localClock.TurnLeft(0);
            if (!localClock.TimedOut(0)) return;
            if (b.Phase == Phase.Choose) Submit(TurnClock.TimeoutAction(b, 0));
            else DoReplace(TurnClock.TimeoutReplacement(b, 0));
        }

        void ResetLocalClock()
        {
            localClock = null;
            localClockBattle = null;
            localClockDecisionOpen = false;
            timerLeft = settings?.TurnSeconds ?? 10;
        }

        void OpenLocalClock()
        {
            if (localClockDecisionOpen && ReferenceEquals(localClockBattle, b)
                && localClockTurn == b.Turn && localClockPhase == b.Phase) return;
            localClock = new TurnClock(settings.TurnSeconds, 0);
            localClock.StartTurn();
            localClockBattle = b;
            localClockTurn = b.Turn;
            localClockPhase = b.Phase;
            localClockDecisionOpen = true;
            timerLeft = settings.TurnSeconds;
        }

        public void Submit(BattleAction action)
        {
            if (b == null || playing || b.Phase != Phase.Choose) return;
            if (SubmitTeamClockAction(action)) return;
            b.ResolveTurn(action, Ai.Choose(b, 1, settings.AiLevel));
            localClockDecisionOpen = false;
            Play();
        }

        public void ChooseOwnSlot(int index)
        {
            if (b == null || playing) return;
            if (b.Phase == Phase.Lead) SubmitLead(index);
            else if (b.Phase == Phase.Replace) DoReplace(index);
            else if (b.Phase == Phase.Choose) Submit(BattleAction.SwitchTo(index));
        }

        public void ChooseFoeSlot(int index) { if (!playing) SubmitTeamBan(index); }

        void SubmitLead(int index)
        {
            if (b == null || b.Phase != Phase.Lead || b.HasChosenLead(0)) return;
            b.ChooseLead(0, index);
            Play();
        }

        void DoReplace(int index)
        {
            if (b == null || playing || b.Phase != Phase.Replace || !b.Need[0]) return;
            if (SubmitTeamClockReplacement(index)) return;
            b.Replace(0, index);
            if (b.Phase == Phase.Replace && b.Need[1]) b.Replace(1, Ai.ChooseReplacement(b, 1, settings.AiLevel));
            localClockDecisionOpen = false;
            Play();
        }

        void Play() { playing = true; PauseTeamClock(); }
        public void BeginPlayback() { Play(); }
        public void CompletePlayback() { SkipPlayback(); }
        void SkipPlayback() { playing = false; AfterPlayback(); }

        void AfterPlayback()
        {
            if (b == null) return;
            ReportTeamMatchCompletion();
            if (AfterTeamClockPlayback()) return;
            if (b.Phase == Phase.Replace && b.Need[1])
            {
                b.Replace(1, Ai.ChooseReplacement(b, 1, settings.AiLevel));
                if (!b.Need[0]) { localClockDecisionOpen = false; Play(); return; }
            }
            if (b.Phase == Phase.Choose || b.Phase == Phase.Replace) OpenLocalClock();
        }

        public bool AdvanceTeamSmoke()
        {
            if (!SmokeRequested) throw new InvalidOperationException("自动推进仅供隔离验收使用。");
            if (b == null) return false;
            if (playing) { SkipPlayback(); return b.Phase != Phase.Over; }
            if (b.Phase == Phase.Over) return false;
            if (b.Phase == Phase.Ban) SubmitTeamBan(Ai.ChooseBan(b, 0));
            else if (b.Phase == Phase.Lead) SubmitLead(Ai.ChooseLead(b, 0));
            else if (b.Phase == Phase.Replace)
            {
                if (b.Need[0]) DoReplace(Ai.ChooseReplacement(b, 0, settings.AiLevel));
                else AfterPlayback();
            }
            else if (b.Phase == Phase.Choose) Submit(Ai.Choose(b, 0, settings.AiLevel));
            return b.Phase != Phase.Over;
        }
    }
}
