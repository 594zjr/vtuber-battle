using System;
using System.Collections.Generic;
using System.Linq;
using StreamDuel.Core;
using UnityEngine;
using UnityEngine.UIElements;

namespace StreamDuel.Game
{
    public sealed class AnimPreviewScreen : IScreen
    {
        public const string ScreenId = ScreenIds.AnimationPreview;
        public const uint PreviewSeed = 68;

        readonly List<Action> unbind = new List<Action>();
        readonly Dictionary<float, Button> speedButtons = new Dictionary<float, Button>();
        ScreenRouter router;
        string returnScreenId;
        object returnArgs;
        ContentDb db;
        VisualElement page;
        Label status, result;
        Button nextButton, autoButton, skipButton;
        bool disposed = true, autoPlay, paused;
        int displayedTurn;
        float speed = 1;

        public string Id => ScreenId;
        public Battle Battle { get; private set; }
        public TimelineAnimator Animator { get; private set; }
        public BattleStage Stage { get; private set; }
        public IReadOnlyList<AnimCue> CurrentCues { get; private set; } = Array.Empty<AnimCue>();
        public bool AutoPlayEnabled => autoPlay;
        public bool IsPaused => paused;
        public float Speed => speed;

        public VisualElement Build(ScreenRouter screenRouter, object args)
        {
            if (screenRouter == null) throw new ArgumentNullException(nameof(screenRouter));
            Dispose();
            router = screenRouter;
            var navigation = args as ScreenArgs;
            returnScreenId = navigation?.ReturnScreenId ?? ScreenIds.Menu;
            returnArgs = navigation?.ReturnArgs;
            db = null;
            Stage = null;
            disposed = false;
            speed = 1;

            page = new VisualElement { name = ScreenId + "-screen", userData = this };
            page.style.flexGrow = 1;
            page.style.minHeight = 0;
            page.style.paddingLeft = page.style.paddingRight = 20;
            page.style.paddingTop = page.style.paddingBottom = 14;
            page.style.backgroundColor = (Color)new Color32(12, 17, 36, 255);

            var header = Row("preview-header");
            header.style.justifyContent = Justify.SpaceBetween;
            header.style.alignItems = Align.Center;
            var title = Text("动画预览", "preview-title", 32);
            title.style.color = (Color)new Color32(255, 181, 71, 255);
            title.style.unityFont = router.Resources.BoldFont;
            header.Add(title);
            AddButton(header, "back-button", "返回菜单", ReturnToMenu);
            page.Add(header);

            try
            {
                var policy = UnityPackPolicy.Load();
                var rules = UnityEngine.Resources.Load<TextAsset>("StreamDuelRules");
                var pack = UnityEngine.Resources.Load<TextAsset>("Packs/original");
                if (rules == null || pack == null) throw new InvalidOperationException("动画预览缺少原创包或规则数据。");
                var loaded = PackLoader.LoadJson(rules.text, pack.text, "original.json", policy);
                if (loaded.Db == null) throw new InvalidOperationException(loaded.Error);
                db = loaded.Db;
            }
            catch (Exception ex)
            {
                status = Text("动画预览暂不可用：" + ex.Message, "preview-status", 20);
                status.style.color = (Color)new Color32(232, 235, 250, 255);
                page.Add(status);
                return page;
            }

            var controls = Row("preview-controls");
            controls.style.flexWrap = Wrap.Wrap;
            nextButton = AddButton(controls, "preview-next", "下一回合（含补位）", NextStep);
            autoButton = AddButton(controls, "preview-auto", "自动播放", ToggleAutoPlay);
            skipButton = AddButton(controls, "preview-skip", "跳过本段", Skip);
            AddButton(controls, "preview-restart", "重开", Restart);
            var speedLabel = Text("速度", "preview-speed-label", 20);
            speedLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
            speedLabel.style.marginLeft = 12;
            controls.Add(speedLabel);
            AddSpeedButton(controls, 0.5f, "0.5×", "preview-speed-half");
            AddSpeedButton(controls, 1, "1×", "preview-speed-normal");
            AddSpeedButton(controls, 2, "2×", "preview-speed-double");
            page.Add(controls);

            status = Text("", "preview-status", 20);
            status.style.marginTop = 6;
            status.style.marginBottom = 4;
            page.Add(status);

            Stage = new BattleStage { name = "preview-stage" };
            // 预览让舞台占用剩余高度，避免共用舞台的固定最小高度挤出小窗口按钮。
            Stage.style.minHeight = 0;
            Stage.style.flexBasis = 0;
            Stage.style.flexGrow = 1;
            Stage.style.flexShrink = 1;
            Stage.style.marginTop = 0;
            // 头顶跳字需要完整的上飘空间，留白同时从头像可用高度中扣除。
            Stage.style.paddingTop = 104;
            Stage.style.overflow = Overflow.Hidden;
            ConfigureFighter(Stage.Fighter0);
            ConfigureFighter(Stage.Fighter1);
            Stage.FieldBanner.style.minHeight = 28;
            Stage.FieldBanner.style.marginTop = 4;
            Stage.FieldBanner.style.marginBottom = 4;
            Stage.FieldBanner.style.fontSize = 18;
            Stage.RegisterCallback<GeometryChangedEvent>(OnStageGeometry);
            page.Add(Stage);

            result = Text("", "preview-result", 22);
            result.style.minHeight = 30;
            result.style.unityTextAlign = TextAnchor.MiddleCenter;
            result.style.color = (Color)new Color32(255, 181, 71, 255);
            page.Add(result);
            var hint = Text("原创包 · 双方普通电脑 · 种子 68　｜　跳过仅结束当前动画；重开使用同一阵容与种子。", "preview-hint", 18);
            hint.style.color = (Color)new Color32(152, 161, 200, 255);
            hint.style.unityTextAlign = TextAnchor.MiddleCenter;
            page.Add(hint);

            try { Restart(); }
            catch { Dispose(); throw; }
            return page;
        }

        public void Restart()
        {
            if (disposed || db == null) return;
            Animator?.Dispose();
            Animator = null;
            autoPlay = paused = false;
            displayedTurn = 0;
            var teams = new IList<string>[]
            {
                db.Streamers.Take(6).Select(x => x.Id).ToArray(),
                db.Streamers.Skip(6).Concat(db.Streamers.Take(6)).Take(6).Select(x => x.Id).ToArray()
            };
            Battle = new Battle(db, teams, new[] { "左方", "右方" }, PreviewSeed);
            Animator = new TimelineAnimator(Stage, db);
            var view = BattleViewModel.From(Battle, 0);
            Animator.Prepare(Battle, view, view);
            CurrentCues = BattleTimeline.Build(Battle.Events, 0, Battle.Events.Count).AsReadOnly();
            Animator.Play(CurrentCues);
            RefreshControls();
        }

        public void NextStep()
        {
            if (disposed || Animator == null || Animator.IsPlaying || Battle.Phase == Phase.Over) return;
            autoPlay = paused = false;
            AdvanceRound();
        }

        void AdvanceRound()
        {
            var before = BattleViewModel.From(Battle, 0);
            int from = Battle.Events.Count;
            displayedTurn = Battle.Turn;
            if (Battle.Phase == Phase.Choose)
            {
                var left = Ai.Choose(Battle, 0, AiLevel.Normal);
                var right = Ai.Choose(Battle, 1, AiLevel.Normal);
                Battle.ResolveTurn(left, right);
            }
            for (int side = 0; side < 2 && Battle.Phase == Phase.Replace; side++)
                if (Battle.Need[side]) Battle.Replace(side, Ai.ChooseReplacement(Battle, side, AiLevel.Normal));
            var after = BattleViewModel.From(Battle, 0);
            Animator.Prepare(Battle, before, after);
            CurrentCues = BattleTimeline.Build(Battle.Events, from, Battle.Events.Count).AsReadOnly();
            Animator.Play(CurrentCues);
            RefreshControls();
        }

        public void SetAutoPlay(bool enabled)
        {
            if (disposed || Animator == null) return;
            autoPlay = enabled && (Battle.Phase != Phase.Over || Animator.IsPlaying);
            paused = !enabled && Animator.IsPlaying;
            RefreshControls();
        }

        public void ToggleAutoPlay()
        {
            SetAutoPlay(!autoPlay);
        }

        public void SetSpeed(float value)
        {
            if (value != 0.5f && value != 1 && value != 2)
                throw new ArgumentOutOfRangeException(nameof(value), "播放速度只能是 0.5、1 或 2 倍。");
            speed = value;
            if (!disposed && Animator != null) RefreshControls();
        }

        public void Skip()
        {
            if (disposed || Animator == null) return;
            Animator.SkipToEnd();
            paused = false;
            RefreshControls();
        }

        public void Tick(float dt)
        {
            if (disposed || Animator == null || dt < 0 || float.IsNaN(dt) || float.IsInfinity(dt)) return;
            if (!paused) Animator.Tick(dt * speed);
            if (!Animator.IsPlaying)
            {
                if (Battle.Phase == Phase.Over) autoPlay = false;
                else if (autoPlay && !paused) AdvanceRound();
            }
            RefreshControls();
        }

        void RefreshControls()
        {
            bool playing = Animator.IsPlaying;
            bool ended = Battle.Phase == Phase.Over && !playing;
            nextButton.SetEnabled(!playing && !ended);
            skipButton.SetEnabled(playing);
            autoButton.SetEnabled(!ended);
            autoButton.text = autoPlay ? "暂停" : paused ? "继续自动播放" : "自动播放";
            foreach (var pair in speedButtons)
                pair.Value.style.borderBottomColor = pair.Key == speed
                    ? (Color)new Color32(255, 181, 71, 255) : (Color)new Color32(43, 54, 102, 255);
            string segment = displayedTurn == 0 ? "开场" : "第 " + displayedTurn + " 回合（含补位）";
            string playback = ended ? "对局结束" : paused ? "已暂停" : playing ? "播放中" : "播放完毕";
            status.text = segment + " · " + playback;
            result.text = ended
                ? Battle.Winner < 0 ? "本局平局" : Battle.Sides[Battle.Winner].Name + "获胜"
                : !playing ? "准备推进第 " + Battle.Turn + " 回合" : "";
        }

        public void ReturnToMenu()
        {
            if (!disposed) router.Show(returnScreenId, returnArgs);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            autoPlay = paused = false;
            foreach (var remove in unbind) remove();
            unbind.Clear();
            speedButtons.Clear();
            Stage?.UnregisterCallback<GeometryChangedEvent>(OnStageGeometry);
            Animator?.Dispose();
            Animator = null;
            Battle = null;
            CurrentCues = Array.Empty<AnimCue>();
            returnScreenId = null;
            returnArgs = null;
        }

        void OnStageGeometry(GeometryChangedEvent evt)
        {
            float portraitHeight = Mathf.Clamp(evt.newRect.height - 104 - 180, 100, 320);
            Stage.Fighter0.Q<Image>(StageNames.Portrait).style.height = portraitHeight;
            Stage.Fighter1.Q<Image>(StageNames.Portrait).style.height = portraitHeight;
        }

        static void ConfigureFighter(VisualElement fighter)
        {
            fighter.style.width = Length.Percent(40);
            fighter.style.paddingTop = fighter.style.paddingBottom = 12;
            fighter.style.paddingLeft = fighter.style.paddingRight = 16;
            fighter.Q<ProgressBar>(StageNames.HpBar).style.marginTop = 8;
            fighter.Q(StageNames.StatusRow).style.minHeight = 32;
        }

        void AddSpeedButton(VisualElement parent, float value, string label, string name)
        {
            speedButtons[value] = AddButton(parent, name, label, () => SetSpeed(value));
        }

        Button AddButton(VisualElement parent, string name, string label, Action clicked)
        {
            var button = new Button { name = name, text = label };
            button.AddToClassList("screen-button");
            button.style.minWidth = 64;
            button.style.minHeight = 44;
            button.style.paddingLeft = button.style.paddingRight = 14;
            button.style.paddingTop = button.style.paddingBottom = 6;
            button.style.marginLeft = button.style.marginRight = 4;
            button.style.marginTop = button.style.marginBottom = 4;
            button.style.fontSize = 20;
            button.style.whiteSpace = WhiteSpace.NoWrap;
            button.clicked += clicked;
            unbind.Add(() => button.clicked -= clicked);
            parent.Add(button);
            return button;
        }

        static VisualElement Row(string name)
        {
            var row = new VisualElement { name = name };
            row.style.flexDirection = FlexDirection.Row;
            row.style.flexShrink = 0;
            return row;
        }

        static Label Text(string text, string name, int size)
        {
            var label = new Label(text) { name = name, enableRichText = false };
            label.style.fontSize = size;
            label.style.whiteSpace = WhiteSpace.Normal;
            label.style.flexShrink = 0;
            return label;
        }
    }
}
