using System;
using System.Linq;
using StreamDuel.Core;
using UnityEngine;
using UnityEngine.UIElements;

namespace StreamDuel.Game
{
    public sealed class BattleScreen : IScreen
    {
        ScreenRouter router;
        BattleHud hud;
        IBattleAnimator animator;
        TimelineAnimator ownedAnimator;
        IPortraitProvider portraits;
        IDisposable ownedPortraits;
        BattleViewModel view;
        Battle displayBattle;
        Battle sourceBattle;
        Button emptyButton;
        Action emptyClick;
        int revealed, selectedLog = -1, timerSecond = -1;
        float lineElapsed;
        bool wasPlaying, disposed;

        public static BattleScreen ActiveInstance { get; private set; }
        public string Id => ScreenIds.Battle;
        public BattleScreenArgs Args { get; private set; }
        public Battle CurrentBattle => Args?.Session != null ? Args.Session.CurrentBattle : Args?.Battle;
        public bool IsBusy => animator != null && animator.IsPlaying;
        public int PlaybackFromEvent { get; private set; }
        public int PlaybackToEvent { get; private set; }
        public TimelineAnimator TimelineAnimator => animator as TimelineAnimator;
        public BattleStage Stage => hud?.Stage;
        bool IsCurrent => !disposed && ReferenceEquals(ActiveInstance, this);
        LocalBattleSession Session => Args?.Session;

        public VisualElement Build(ScreenRouter screenRouter, object args)
        {
            router = screenRouter;
            Args = args as BattleScreenArgs;
            if (Args == null && args is Battle battle) Args = new BattleScreenArgs { Battle = battle, Db = battle.Db };
            if (Args == null || (CurrentBattle == null && (Args.Db == null || Args.Teams == null)))
            {
                var empty = new VisualElement { name = "battle-empty" };
                empty.AddToClassList("screen");
                empty.Add(new Label("请先选择阵容，再开始对战。") { enableRichText = false });
                emptyButton = new Button { name = "battle-go-team", text = "前往组队" };
                emptyButton.AddToClassList("screen-button");
                emptyClick = () => { if (IsCurrent && emptyButton.enabledInHierarchy) router.Show(ScreenIds.Team); };
                emptyButton.clicked += emptyClick;
                empty.Add(emptyButton);
                ActiveInstance = this;
                return empty;
            }

            Args.Settings = Session?.Settings ?? Args.Settings ?? GameSettings.FromJson(GameRuntimeSettings.Current.ToJson());
            bool fresh = CurrentBattle == null;
            if (fresh)
                Args.Battle = new Battle(Args.Db, Args.Teams, Args.Names ?? new[] { "你", "对手" },
                    new BattleOptions { Seed = Args.Seed, BanOne = Args.BanOne, LeadPick = Args.LeadPick, Modifiers = Args.Modifiers });
            Args.Db = CurrentBattle.Db;
            animator = Args.Animator;
            portraits = Args.Portraits;
            if (portraits == null)
            {
                var provider = new PortraitProvider(router.Resources.RegularFont);
                portraits = provider;
                ownedPortraits = provider;
            }
            hud = new BattleHud(SubmitSkill, ChooseOwnSlot, ChooseFoeSlot, SubmitPass, SkipPlayback,
                Rematch, ReturnToSetup, SelectLog, ShowMenu, ShowResult, ToggleFastPlayback, ToggleTimer);
            Session?.SetVisible(true);
            if (Session == null && Args.Clock == null) ResetClock();
            int priorEvents = CurrentBattle.Events.Count, priorLogs = CurrentBattle.Log.Count;
            bool restoringPlayback = ReferenceEquals(Args.PendingBattle, CurrentBattle) && Args.PendingFromEvent >= 0;
            if (Session == null && !restoringPlayback) AutoChoices();
            RefreshView();
            revealed = CurrentBattle.Log.Count;
            PlaybackFromEvent = PlaybackToEvent = CurrentBattle.Events.Count;
            // 会话的 playing 标记只在尚未展示的批次存在时置位；菜单回来不能重播开场。
            if (restoringPlayback)
            {
                view = Args.PendingBefore;
                PlayNewEvents(Args.PendingFromEvent, Args.PendingFromLog);
            }
            else if (fresh || Session != null && Session.IsPlaybackPending) PlayNewEvents(0, 0);
            else if (CurrentBattle.Events.Count > priorEvents) PlayNewEvents(priorEvents, priorLogs);
            else Render();
            ActiveInstance = this;
            return hud.Root;
        }

        public void SubmitSkill(int index)
        {
            if (!CanChoose) return;
            if (!BattleViewModel.From(CurrentBattle, 0).Skills.Any(skill => skill.Index == index && skill.Usable)) return;
            Submit(BattleAction.Skill(index));
        }

        public void SubmitPass()
        {
            if (CanChoose) Submit(BattleAction.Pass());
        }

        bool CanChoose => IsCurrent && !IsBusy && CurrentBattle != null && CurrentBattle.Phase == Phase.Choose;

        void Submit(BattleAction action)
        {
            Mutate(() =>
            {
                if (Session != null) Session.Submit(action);
                else CurrentBattle.ResolveTurn(action, Ai.Choose(CurrentBattle, 1, Args.Settings.AiLevel));
            });
        }

        public void ChooseOwnSlot(int index)
        {
            var battle = CurrentBattle;
            if (!IsCurrent || IsBusy || battle == null || index < 0 || index >= battle.Sides[0].Team.Count) return;
            if (battle.Phase == Phase.Lead && !battle.HasChosenLead(0))
                Mutate(() => { if (Session != null) Session.ChooseOwnSlot(index); else battle.ChooseLead(0, index); });
            else if (battle.Phase == Phase.Replace && battle.Need[0]
                && BattleViewModel.From(battle, 0).SwitchTargets.Contains(index))
                Mutate(() => { if (Session != null) Session.ChooseOwnSlot(index); else battle.Replace(0, index); });
            else if (CanChoose && BattleViewModel.From(battle, 0).SwitchTargets.Contains(index))
                Submit(BattleAction.SwitchTo(index));
        }

        public void ChooseFoeSlot(int index)
        {
            var battle = CurrentBattle;
            if (!IsCurrent || IsBusy || battle == null || battle.Phase != Phase.Ban || battle.HasChosenBan(0)
                || index < 0 || index >= battle.Sides[1].Team.Count) return;
            Mutate(() => { if (Session != null) Session.ChooseFoeSlot(index); else battle.ChooseBan(0, index); });
        }

        void Mutate(Action action)
        {
            int from = CurrentBattle.Events.Count, fromLog = CurrentBattle.Log.Count;
            action();
            if (Session == null)
            {
                AutoChoices();
                ResetClock();
            }
            selectedLog = -1;
            PlayNewEvents(from, fromLog);
        }

        void AutoChoices()
        {
            var battle = CurrentBattle;
            if (battle.Phase == Phase.Ban && !battle.HasChosenBan(1)) battle.ChooseBan(1, Ai.ChooseBan(battle, 1));
            if (battle.Phase == Phase.Lead && !battle.HasChosenLead(1)) battle.ChooseLead(1, Ai.ChooseLead(battle, 1));
            if (battle.Phase == Phase.Replace && battle.Need[1]) battle.Replace(1, Ai.ChooseReplacement(battle, 1, Args.Settings.AiLevel));
        }

        void ResetClock()
        {
            Args.Clock = new TurnClock(Args.Settings.TurnSeconds, 0);
            Args.TimerRemaining = (float)Args.Clock.TurnLeft(0);
        }

        void PlayNewEvents(int from, int fromLog)
        {
            PlaybackFromEvent = from;
            PlaybackToEvent = CurrentBattle.Events.Count;
            lineElapsed = 0;
            revealed = fromLog;
            var before = view;
            RefreshView();
            if (PlaybackToEvent > from)
            {
                Args.PendingBattle = CurrentBattle;
                Args.PendingBefore = before;
                Args.PendingFromEvent = from;
                Args.PendingFromLog = fromLog;
                Session?.BeginPlayback();
                if (Args.Animator == null)
                {
                    // 动画与静态 HUD 共用舞台；批次结束立即归还节点，不能叠加状态或血条。
                    hud.Render(before, displayBattle.Log, revealed, selectedLog, true, TimerText, null, SettingsNotice);
                    hud.SetStageAnimation(true);
                    animator = ownedAnimator = new TimelineAnimator(hud.Stage, Args.Db, portraits,
                        skillFx: id => Args.Db.Art.SkillFx(id));
                    foreach (var card in new[] { hud.Stage.Fighter0, hud.Stage.Fighter1 })
                        card.Q<Label>("timeline-fighter-initial").style.display = DisplayStyle.None;
                }
                TimelineAnimator?.Prepare(displayBattle, before, view);
                animator.Play(BattleTimeline.Build(displayBattle.Events, from, PlaybackToEvent, PlaybackSpeed));
            }
            wasPlaying = IsBusy;
            if (!wasPlaying) FinishPlayback();
            else Render();
        }

        double PlaybackSpeed => !Args.Settings.Animations ? 0
            : Args.Settings.AnimationSpeed == 1 && Args.Settings.FastPlay ? 2 : Args.Settings.AnimationSpeed;

        public void Tick(float dt)
        {
            if (!IsCurrent || CurrentBattle == null || float.IsNaN(dt) || float.IsInfinity(dt) || dt < 0) return;
            if (IsBusy)
            {
                TimelineAnimator?.Tick(dt);
                if (!IsBusy) { FinishPlayback(); return; }
                lineElapsed += dt;
                float delay = (float)(0.25 / Math.Max(0.5, PlaybackSpeed));
                if (lineElapsed >= delay && revealed < CurrentBattle.Log.Count)
                {
                    int lines = (int)(lineElapsed / delay);
                    revealed = Math.Min(CurrentBattle.Log.Count, revealed + lines);
                    lineElapsed %= delay;
                    Render();
                }
                wasPlaying = true;
                return;
            }
            if (wasPlaying) { FinishPlayback(); return; }
            if (!ReferenceEquals(sourceBattle, CurrentBattle) || displayBattle.Events.Count != CurrentBattle.Events.Count
                || displayBattle.Log.Count != CurrentBattle.Log.Count || view.Phase != CurrentBattle.Phase)
            {
                bool sameBattle = ReferenceEquals(sourceBattle, CurrentBattle);
                Args.Db = CurrentBattle.Db;
                Args.Battle = CurrentBattle;
                if (Session != null) Args.Settings = Session.Settings;
                PlayNewEvents(sameBattle ? displayBattle.Events.Count : 0, sameBattle ? displayBattle.Log.Count : 0);
                return;
            }
            if (Session != null)
            {
                int from = CurrentBattle.Events.Count, fromLog = CurrentBattle.Log.Count;
                Session.Tick(dt, true, false);
                if (Session.IsPlaybackPending || CurrentBattle.Events.Count != from) PlayNewEvents(from, fromLog);
                else Render();
                return;
            }
            if (!Args.Settings.TurnTimer || (CurrentBattle.Phase != Phase.Choose
                && !(CurrentBattle.Phase == Phase.Replace && CurrentBattle.Need[0]))) return;
            bool timeout = Args.Clock.Tick(0, dt);
            Args.TimerRemaining = (float)Args.Clock.TurnLeft(0);
            if (timeout)
            {
                if (CurrentBattle.Phase == Phase.Choose) Submit(TurnClock.TimeoutAction(CurrentBattle, 0));
                else ChooseOwnSlot(TurnClock.TimeoutReplacement(CurrentBattle, 0));
            }
            else if (timerSecond != Mathf.CeilToInt(Args.TimerRemaining)) Render();
        }

        public void SkipPlayback()
        {
            if (!IsCurrent || CurrentBattle == null || !IsBusy) return;
            animator.SkipToEnd();
            FinishPlayback();
        }

        void FinishPlayback()
        {
            wasPlaying = false;
            Args.PendingBattle = null;
            Args.PendingBefore = null;
            Args.PendingFromEvent = -1;
            if (ownedAnimator != null)
            {
                ownedAnimator.Dispose();
                ownedAnimator = null;
                animator = null;
                hud.SetStageAnimation(false);
            }
            revealed = CurrentBattle.Log.Count;
            RefreshView();
            int from = CurrentBattle.Events.Count, fromLog = CurrentBattle.Log.Count;
            if (Session != null && Session.IsPlaybackPending) Session.CompletePlayback();
            else if (Session == null && CurrentBattle.Phase == Phase.Replace && CurrentBattle.Need[1]) AutoChoices();
            // 入场伤害可能再次要求电脑补位；每个入场事件单独播放后才继续，恢复页面不重复抽取 AI。
            if (Session != null && Session.IsPlaybackPending || CurrentBattle.Events.Count != from
                || CurrentBattle.Log.Count != fromLog)
                PlayNewEvents(from, fromLog);
            else Render();
        }

        void RefreshView()
        {
            // 清理仅用于展示的副本；玩家原名、日志、录像和哈希仍由原对局保留。
            displayBattle = TextInput.ForDisplay(CurrentBattle);
            sourceBattle = CurrentBattle;
            view = BattleViewModel.From(displayBattle, 0, Args.Settings);
            if (ownedAnimator != null) return;
            for (int side = 0; side < 2; side++)
            {
                var team = view.Sides[side];
                hud.SetPortrait(side, portraits.Get(Args.Db, team.Team[team.Active].Id));
            }
        }

        string TimerText => Session != null ? Session.TimerText
            : Args.Settings.TurnTimer ? "本次选择 " + Mathf.CeilToInt(Args.TimerRemaining) + " 秒" : "不限时";
        string SettingsNotice => Session != null ? Session.MenuSettingsNotice : Args?.SettingsNotice;

        void Render()
        {
            if (hud == null || view == null) return;
            timerSecond = Mathf.CeilToInt(Args.TimerRemaining);
            string outcome = CurrentBattle.Phase != Phase.Over ? null
                : CurrentBattle.Winner < 0 ? "平局" : view.Sides[CurrentBattle.Winner].Name + " 获胜";
            hud.SetMatch(Session?.MatchScoreText, Session?.ResultRematchLabel);
            hud.SetQuickSettings(Args.Settings, Session == null || Session.CanChangeTurnTimer, IsBusy);
            hud.Render(view, displayBattle.Log, revealed, selectedLog, IsBusy, TimerText, outcome, SettingsNotice);
        }

        void SelectLog(int index)
        {
            if (!IsCurrent || index < 0 || index >= revealed) return;
            selectedLog = index;
            Render();
        }

        BattleScreenArgs RematchArgs()
        {
            var record = CurrentBattle.Record;
            return new BattleScreenArgs
            {
                Db = Args.Db, Teams = record.Teams, Names = record.Names, Seed = record.Seed,
                BanOne = record.BanOne, LeadPick = record.LeadPick, Settings = Args.Settings,
                Modifiers = record.Modifiers?.ToList(), Animator = Args.Animator,
                Portraits = Args.Portraits, Isolated = Args.Isolated
            };
        }

        public void Rematch()
        {
            if (!IsCurrent || IsBusy || CurrentBattle == null || CurrentBattle.Phase != Phase.Over) return;
            if (Session != null)
            {
                var session = Session;
                session.RematchFromResult();
                router.Show(Id, session.CreateBattleScreenArgs());
            }
            else router.Show(Id, RematchArgs());
        }

        public void ReturnToSetup()
        {
            if (!IsCurrent || IsBusy) return;
            if (Session != null) Session.ReturnToTeamFromResult();
            else router.Show(ScreenIds.Team);
        }

        public void ShowResult()
        {
            if (!IsCurrent || IsBusy || CurrentBattle == null || CurrentBattle.Phase != Phase.Over) return;
            var session = Session;
            var route = router;
            var rematch = session == null ? RematchArgs() : null;
            var db = Args.Db;
            route.Show(ScreenIds.Result, new ResultScreenArgs
            {
                Battle = CurrentBattle, PlayerSide = 0,
                Rematch = session != null ? (Action)(() =>
                {
                    session.RematchFromResult();
                    route.Show(ScreenIds.Battle, session.CreateBattleScreenArgs());
                }) : () => route.Show(ScreenIds.Battle, rematch),
                RematchLabel = session?.ResultRematchLabel,
                ReturnToTeam = session != null ? (Action)session.ReturnToTeamFromResult : () => route.Show(ScreenIds.Team),
                ResolvePack = session != null ? (Func<string, ContentDb>)session.FindLoadedPack : id => id == db.PackId ? db : null
            });
        }

        void ShowMenu()
        {
            if (IsCurrent && !IsBusy)
                router.Show(ScreenIds.Menu, new ScreenArgs { ReturnScreenId = Id, ReturnArgs = Args });
        }

        void ToggleFastPlayback()
        {
            if (!IsCurrent || IsBusy || Args == null) return;
            bool next = !Args.Settings.FastPlay;
            if (Session != null) Session.SetFastPlayback(next);
            else
            {
                Args.Settings.FastPlay = next;
                Args.Settings.AnimationSpeed = next ? 2 : 1;
                Args.Settings.Animations = true;
                SaveStandaloneQuickSettings(true);
            }
            Render();
        }

        void ToggleTimer()
        {
            if (!IsCurrent || IsBusy || Args == null || Session != null && !Session.CanChangeTurnTimer) return;
            bool next = !Args.Settings.TurnTimer;
            if (Session != null) Session.SetTurnTimer(next);
            else
            {
                Args.Settings.TurnTimer = next;
                ResetClock();
                SaveStandaloneQuickSettings(false);
            }
            Render();
        }

        void SaveStandaloneQuickSettings(bool playback)
        {
            if (Args.Isolated) return;
            var latest = GameSettings.FromJson(GameRuntimeSettings.Current.ToJson());
            if (playback)
            {
                latest.FastPlay = Args.Settings.FastPlay;
                latest.AnimationSpeed = Args.Settings.AnimationSpeed;
                latest.Animations = Args.Settings.Animations;
            }
            else latest.TurnTimer = Args.Settings.TurnTimer;
            Args.SettingsNotice = GameRuntimeSettings.TrySave(latest, out string message) ? null : message;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            // Router 先 Build 新页；旧页不能取消新页复用的动画器或暂停已经接管的同一会话。
            bool sharedAnimator = ActiveInstance != null && !ReferenceEquals(ActiveInstance, this)
                && ReferenceEquals(ActiveInstance.animator, animator);
            if (!sharedAnimator) animator?.SkipToEnd();
            ownedAnimator?.Dispose();
            ownedAnimator = null;
            if (Session != null && (ActiveInstance == null || ReferenceEquals(ActiveInstance, this)
                || !ReferenceEquals(ActiveInstance.Session, Session))) Session.SetVisible(false);
            if (emptyButton != null) emptyButton.clicked -= emptyClick;
            hud?.Dispose();
            ownedPortraits?.Dispose();
            if (ReferenceEquals(ActiveInstance, this)) ActiveInstance = null;
        }
    }
}
