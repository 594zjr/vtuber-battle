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
        ScreenArgs navigation;
        BattleSetupPanel setup;
        BattleHud hud;
        IBattleAnimator animator;
        IPortraitProvider portraits;
        IDisposable ownedPortraits;
        BattleViewModel view;
        int revealed, selectedLog = -1, timerSecond = -1;
        float timerLeft = 10f, lineElapsed;
        bool wasPlaying, disposed;

        public static BattleScreen ActiveInstance { get; private set; }
        public string Id => ScreenIds.Battle;
        public BattleScreenArgs Args { get; private set; }
        public Battle CurrentBattle => Args?.Battle;
        public bool IsBusy => animator != null && animator.IsPlaying;
        public int PlaybackFromEvent { get; private set; }
        public int PlaybackToEvent { get; private set; }

        public VisualElement Build(ScreenRouter screenRouter, object args)
        {
            router = screenRouter;
            navigation = args as ScreenArgs;
            Args = args as BattleScreenArgs;
            if (Args == null && args is Battle battle) Args = new BattleScreenArgs { Battle = battle, Db = battle.Db };
            ActiveInstance = this;
            if (Args == null || (Args.Battle == null && (Args.Db == null || Args.Teams == null)))
            {
                setup = new BattleSetupPanel(router, start => router.Show(Id, start), ShowMenu);
                return setup.Root;
            }

            Args.Settings = Args.Settings ?? new GameSettings();
            timerLeft = Args.TimerRemaining;
            bool fresh = Args.Battle == null;
            if (fresh)
                Args.Battle = new Battle(Args.Db, Args.Teams, Args.Names ?? new[] { "你", "对手" },
                    new BattleOptions { Seed = Args.Seed, BanOne = Args.BanOne, LeadPick = Args.LeadPick, Modifiers = Args.Modifiers });
            Args.Db = Args.Battle.Db;
            animator = Args.Animator ?? new NullAnimator();
            portraits = Args.Portraits;
            if (portraits == null)
            {
                var provider = new SolidColorPortraitProvider();
                portraits = provider;
                ownedPortraits = provider;
            }
            hud = new BattleHud(SubmitSkill, ChooseOwnSlot, ChooseFoeSlot, SubmitPass, SkipPlayback,
                Rematch, ReturnToSetup, SelectLog, ShowMenu);
            int priorEvents = CurrentBattle.Events.Count, priorLogs = CurrentBattle.Log.Count;
            AutoChoices();
            RefreshView();
            revealed = CurrentBattle.Log.Count;
            // 恢复页面只重建显示，已经结算过的事件不能再次交给动画器。
            PlaybackFromEvent = PlaybackToEvent = CurrentBattle.Events.Count;
            if (fresh) PlayNewEvents(0, 0);
            else if (CurrentBattle.Events.Count > priorEvents) PlayNewEvents(priorEvents, priorLogs);
            else Render();
            return hud.Root;
        }

        public void SubmitSkill(int index)
        {
            if (!CanChoose) return;
            var legal = BattleViewModel.From(CurrentBattle, 0);
            if (!legal.Skills.Any(skill => skill.Index == index && skill.Usable)) return;
            Submit(BattleAction.Skill(index));
        }

        public void SubmitPass()
        {
            if (CanChoose) Submit(BattleAction.Pass());
        }

        bool CanChoose => !disposed && !IsBusy && CurrentBattle != null && CurrentBattle.Phase == Phase.Choose;

        void Submit(BattleAction action)
        {
            Mutate(() => CurrentBattle.ResolveTurn(action, Ai.Choose(CurrentBattle, 1, Args.Settings.AiLevel)));
        }

        public void ChooseOwnSlot(int index)
        {
            var battle = CurrentBattle;
            if (disposed || IsBusy || battle == null || index < 0 || index >= battle.Sides[0].Team.Count) return;
            if (battle.Phase == Phase.Lead && !battle.HasChosenLead(0))
                Mutate(() => battle.ChooseLead(0, index));
            else if (battle.Phase == Phase.Replace && battle.Need[0])
            {
                var team = BattleViewModel.From(battle, 0).Sides[0];
                if (index != team.Active && !team.Team[index].Fainted) Mutate(() => battle.Replace(0, index));
            }
            else if (CanChoose && BattleViewModel.From(battle, 0).SwitchTargets.Contains(index))
                Submit(BattleAction.SwitchTo(index));
        }

        public void ChooseFoeSlot(int index)
        {
            var battle = CurrentBattle;
            if (disposed || IsBusy || battle == null || battle.Phase != Phase.Ban || battle.HasChosenBan(0)
                || index < 0 || index >= battle.Sides[1].Team.Count) return;
            Mutate(() => battle.ChooseBan(0, index));
        }

        void Mutate(Action action)
        {
            int from = CurrentBattle.Events.Count, fromLog = CurrentBattle.Log.Count;
            action();
            AutoChoices();
            Args.TimerRemaining = timerLeft = 10f;
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

        void PlayNewEvents(int from, int fromLog)
        {
            PlaybackFromEvent = from;
            PlaybackToEvent = CurrentBattle.Events.Count;
            lineElapsed = 0;
            revealed = fromLog;
            if (PlaybackToEvent > from)
                animator.Play(BattleTimeline.Build(CurrentBattle.Events, from, PlaybackToEvent, PlaybackSpeed));
            wasPlaying = IsBusy;
            if (!wasPlaying) FinishPlayback();
            else Render();
        }

        double PlaybackSpeed => !Args.Settings.Animations ? 0 : Args.Settings.FastPlay ? 2 : 1;

        public void Tick(float dt)
        {
            if (disposed || CurrentBattle == null) return;
            if (IsBusy)
            {
                lineElapsed += Mathf.Max(0, dt);
                float delay = Args.Settings.FastPlay ? 0.05f : 0.25f;
                if (lineElapsed >= delay && revealed < CurrentBattle.Log.Count)
                {
                    revealed++;
                    lineElapsed = 0;
                    Render();
                }
                wasPlaying = true;
                return;
            }
            if (wasPlaying) FinishPlayback();
            if (!Args.Settings.TurnTimer || (CurrentBattle.Phase != Phase.Choose
                && !(CurrentBattle.Phase == Phase.Replace && CurrentBattle.Need[0]))) return;
            timerLeft = Mathf.Max(0, timerLeft - Mathf.Max(0, dt));
            Args.TimerRemaining = timerLeft;
            if (timerLeft <= 0)
            {
                if (CurrentBattle.Phase == Phase.Choose) SubmitPass();
                else ChooseOwnSlot(Ai.ChooseReplacement(CurrentBattle, 0, Args.Settings.AiLevel));
            }
            else if (timerSecond != Mathf.CeilToInt(timerLeft)) Render();
        }

        public void SkipPlayback()
        {
            if (disposed || CurrentBattle == null) return;
            animator.SkipToEnd();
            FinishPlayback();
        }

        void FinishPlayback()
        {
            wasPlaying = false;
            revealed = CurrentBattle.Log.Count;
            RefreshView();
            Render();
        }

        void RefreshView()
        {
            view = BattleViewModel.From(CurrentBattle, 0);
            for (int side = 0; side < 2; side++)
            {
                var team = view.Sides[side];
                hud.SetPortrait(side, portraits.Get(Args.Db, team.Team[team.Active].Id));
            }
        }

        void Render()
        {
            if (hud == null || view == null) return;
            timerSecond = Mathf.CeilToInt(timerLeft);
            string timer = Args.Settings.TurnTimer ? "本次选择 " + timerSecond + " 秒" : "不限时";
            string outcome = CurrentBattle.Phase != Phase.Over ? null
                : CurrentBattle.Winner < 0 ? "平局" : view.Sides[CurrentBattle.Winner].Name + " 获胜";
            hud.Render(view, CurrentBattle.Log, revealed, selectedLog, IsBusy, timer, outcome);
        }

        void SelectLog(int index)
        {
            if (index < 0 || index >= revealed) return;
            selectedLog = index;
            Render();
        }

        public void Rematch()
        {
            if (disposed || IsBusy || CurrentBattle == null || CurrentBattle.Phase != Phase.Over) return;
            var record = CurrentBattle.Record;
            router.Show(Id, new BattleScreenArgs
            {
                Db = Args.Db, Teams = record.Teams, Names = record.Names, Seed = record.Seed,
                BanOne = record.BanOne, LeadPick = record.LeadPick, Settings = Args.Settings,
                Modifiers = record.Modifiers?.ToList(),
                Animator = Args.Animator, Portraits = Args.Portraits, Isolated = Args.Isolated
            });
        }

        public void ReturnToSetup()
        {
            if (!disposed && !IsBusy) router.Show(Id);
        }

        void ShowMenu()
        {
            if (!disposed && !IsBusy)
            {
                if (CurrentBattle == null)
                    router.Show(navigation?.ReturnScreenId ?? ScreenIds.Menu, navigation?.ReturnArgs);
                else router.Show(ScreenIds.Menu, new ScreenArgs { ReturnScreenId = Id, ReturnArgs = Args });
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            // Router 先构建新页再释放旧页；重开复用的动画器此时已属于新页。
            if (ActiveInstance == null || ReferenceEquals(ActiveInstance, this)
                || !ReferenceEquals(ActiveInstance.animator, animator)) animator?.SkipToEnd();
            setup?.Dispose();
            hud?.Dispose();
            ownedPortraits?.Dispose();
            if (ReferenceEquals(ActiveInstance, this)) ActiveInstance = null;
        }
    }
}
