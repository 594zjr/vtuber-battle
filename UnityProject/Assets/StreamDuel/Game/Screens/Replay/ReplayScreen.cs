using System;
using System.Collections.Generic;
using System.Linq;
using StreamDuel.Core;
using UnityEngine;
using UnityEngine.UIElements;

namespace StreamDuel.Game
{
    public sealed partial class ReplayScreen : IScreen
    {
        readonly List<Action> unbind = new List<Action>();
        readonly List<string> replayNames = new List<string>();
        readonly Func<string, ContentDb> fallbackResolver;
        ScreenRouter router;
        ReplayScreenArgs args;
        ISaveStore saveStore;
        ReplayNavigator navigator;
        IBattleAnimator animator;
        IPortraitProvider portraits;
        PortraitProvider ownedPortraits;
        TimelineAnimator ownedAnimator;
        ContentDb animatedContent;
        ITextClipboard clipboard;
        GameSettings settings;
        Battle displayedBattle;
        VisualElement page, playback, details;
        BattleStage stage;
        ScrollView log;
        DropdownField files, speed;
        TextField shareCode;
        IntegerField jumpTurn;
        Label feedback, libraryFeedback, empty, position, packLabel;
        Button openFile, previous, next, jump, play, copyShare;
        bool playing, disposed = true;
        float elapsed;

        public string Id => ScreenIds.Replay;
        public Battle DisplayedBattle => displayedBattle?.Clone();
        public TimelineAnimator TimelineAnimator => animator as TimelineAnimator;
        public BattleStage Stage => stage;

        public ReplayScreen(Func<string, ContentDb> resolvePack = null)
        {
            fallbackResolver = resolvePack;
        }

        public VisualElement Build(ScreenRouter owner, object screenArgs)
        {
            Dispose();
            disposed = false;
            router = owner;
            args = screenArgs as ReplayScreenArgs ?? new ReplayScreenArgs();
            animator = args.Animator;
            clipboard = args.Clipboard ?? new SystemTextClipboard();
            settings = args.Settings ?? GameRuntimeSettings.Current;
            portraits = args.Portraits;
            if (portraits == null) portraits = ownedPortraits = new PortraitProvider(owner.Resources.RegularFont);

            page = new VisualElement { name = "replay-screen" };
            page.AddToClassList("screen");
            page.style.paddingTop = page.style.paddingBottom = 24;
            page.style.paddingLeft = page.style.paddingRight = 24;
            page.style.alignItems = Align.Stretch;
            var panel = new VisualElement();
            panel.AddToClassList("screen-panel");
            panel.style.width = Length.Percent(100);
            panel.style.maxWidth = 1800;
            panel.style.flexGrow = 1;
            panel.style.minHeight = 0;
            panel.style.paddingTop = panel.style.paddingBottom = 20;
            panel.style.paddingLeft = panel.style.paddingRight = 20;
            page.Add(panel);

            var header = Row();
            var title = Text("录像回看");
            title.AddToClassList("screen-title");
            title.style.flexGrow = 1;
            title.style.fontSize = 36;
            title.style.marginBottom = 0;
            header.Add(title);
            AddButton(header, "返回", "back-button", () => Attempt(() =>
            {
                StopPlayback();
                if (args.Return != null) args.Return();
                else router.Show(ScreenIds.Menu);
            }));
            panel.Add(header);
            var scroll = new ScrollView(ScrollViewMode.Vertical) { name = "replay-scroll" };
            scroll.style.flexGrow = 1;
            scroll.style.minHeight = 0;
            panel.Add(scroll);
            packLabel = Text("打开录像回看，或连接观战端口观看已公开的延迟画面。", "replay-pack");
            scroll.Add(packLabel);
            feedback = Text("", "replay-status");
            feedback.style.display = DisplayStyle.None;
            scroll.Add(feedback);
            BuildLibrary(scroll);
            BuildSharing(scroll);
            BuildSpectatorEntry(scroll);
            empty = Text("尚未打开录像。可以选择本地录像，或粘贴分享码打开。", "replay-empty");
            scroll.Add(empty);
            playback = new VisualElement();
            scroll.Add(playback);
            BuildPlayback();
            UpdateControls();

            if (args.Spectator != null) Attempt(() => BeginSpectating(args.Spectator, args.SpectatorConnection));
            else
            {
                AttemptLibrary(() =>
                {
                    saveStore = args.SaveStore ?? ReplayStorage.OpenDefault();
                    RefreshFiles();
                });
                if (args.Record != null) Attempt(() => OpenRecord(args.Record));
            }
            return page;
        }

        void BuildLibrary(VisualElement parent)
        {
            var row = Row();
            row.name = "replay-library-controls";
            files = new DropdownField("本地录像") { name = "replay-files" };
            files.style.flexGrow = 1;
            files.style.flexBasis = 420;
            files.style.minWidth = 320;
            StyleField(files, files.labelElement, 100);
            row.Add(files);
            openFile = AddButton(row, "打开录像", "open-replay-button", () => Attempt(() =>
            {
                if (saveStore == null || string.IsNullOrEmpty(files.value))
                    throw new InvalidOperationException("请先选择一份本地录像。");
                OpenRecord(BattleRecord.FromJson(saveStore.LoadReplay(files.value)));
            }));
            AddButton(row, "刷新列表", "refresh-replays-button", () => AttemptLibrary(() =>
            {
                if (saveStore == null) saveStore = args.SaveStore ?? ReplayStorage.OpenDefault();
                RefreshFiles();
                Message(replayNames.Count == 0 ? "本地暂无录像，可以粘贴分享码打开。" : "本地录像列表已刷新。");
            }));
            parent.Add(row);
            libraryFeedback = Text("", "replay-library-status");
            libraryFeedback.style.display = DisplayStyle.None;
            libraryFeedback.style.color = new Color(1f, .57f, .62f);
            parent.Add(libraryFeedback);
        }

        void BuildSharing(VisualElement parent)
        {
            var row = Row();
            row.name = "replay-share-controls";
            shareCode = new TextField("分享码") { name = "share-code-field" };
            shareCode.style.flexGrow = 1;
            shareCode.style.flexBasis = 420;
            shareCode.style.minWidth = 320;
            StyleField(shareCode, shareCode.labelElement, 100);
            row.Add(shareCode);
            AddButton(row, "粘贴并打开", "paste-share-button", () => Attempt(() =>
            {
                shareCode.SetValueWithoutNotify(clipboard.Text ?? "");
                OpenShare();
            }));
            AddButton(row, "打开分享码", "open-share-button", () => Attempt(OpenShare));
            copyShare = AddButton(row, "复制分享码", "copy-share-button", () => Attempt(() =>
            {
                RequireReplay();
                string code = ShareCode.Encode(navigator.Record);
                clipboard.Text = code;
                shareCode.SetValueWithoutNotify(code);
                Message("分享码已复制，可发送给使用相同内容包版本的玩家。");
            }));
            parent.Add(row);
        }

        void BuildPlayback()
        {
            var controls = Row();
            previous = AddButton(controls, "上一回合", "previous-turn-button", () => Attempt(() => Move(false)));
            next = AddButton(controls, "下一回合", "next-turn-button", () => Attempt(() => Move(true)));
            play = AddButton(controls, "自动播放", "play-pause-button", () => Attempt(TogglePlayback));
            int initialSpeed = settings.AnimationSpeed == .5 ? 0 : settings.AnimationSpeed == 2 ? 2 : 1;
            speed = new DropdownField("速度", new List<string> { "0.5x", "1x", "2x", "4x" }, initialSpeed)
            {
                name = "playback-speed"
            };
            speed.style.width = speed.style.minWidth = 210;
            StyleField(speed, speed.labelElement, 64);
            EventCallback<ChangeEvent<string>> changedSpeed = _ => Attempt(() =>
            {
                elapsed = 0;
                animator?.SkipToEnd();
            });
            speed.RegisterValueChangedCallback(changedSpeed);
            var speedField = speed;
            unbind.Add(() => speedField.UnregisterValueChangedCallback(changedSpeed));
            controls.Add(speed);
            jumpTurn = new IntegerField("回合") { name = "jump-turn-field", value = 0 };
            jumpTurn.style.width = jumpTurn.style.minWidth = 210;
            StyleField(jumpTurn, jumpTurn.labelElement, 64);
            controls.Add(jumpTurn);
            jump = AddButton(controls, "跳转", "jump-turn-button", () => Attempt(Jump));
            playback.Add(controls);
            position = Text("", "replay-position");
            playback.Add(position);

            var body = Row();
            body.style.alignItems = Align.FlexStart;
            stage = new BattleStage();
            stage.style.flexGrow = 2;
            stage.style.flexBasis = 600;
            stage.style.minWidth = 480;
            stage.style.minHeight = 330;
            foreach (var fighter in new[] { stage.Fighter0, stage.Fighter1 })
            {
                fighter.style.width = Length.Percent(46);
                fighter.style.paddingTop = fighter.style.paddingBottom = 14;
                fighter.style.paddingLeft = fighter.style.paddingRight = 14;
                fighter.Insert(0, Text("", "fighter-name"));
                fighter.Insert(1, Text("", "fighter-tracks"));
                var portrait = fighter.Q<Image>(StageNames.Portrait);
                portrait.style.width = 140;
                portrait.style.height = 164;
                portrait.style.alignSelf = Align.Center;
                StyleHpBar(fighter.Q<ProgressBar>(StageNames.HpBar));
            }
            body.Add(stage);
            details = new VisualElement { name = "replay-details" };
            details.style.flexGrow = 1;
            details.style.flexBasis = 340;
            details.style.minWidth = 300;
            details.style.paddingLeft = 16;
            body.Add(details);
            playback.Add(body);
            playback.Add(Text("当前回合日志"));
            log = new ScrollView(ScrollViewMode.Vertical) { name = "replay-log" };
            log.style.height = 200;
            log.style.minHeight = 160;
            playback.Add(log);
        }

        void RefreshFiles()
        {
            var names = saveStore.ListReplays();
            string selected = files.value;
            replayNames.Clear();
            replayNames.AddRange(names);
            files.choices = new List<string>(replayNames);
            files.SetValueWithoutNotify(replayNames.Contains(selected) ? selected : replayNames.FirstOrDefault() ?? "");
            PlainText(files);
            empty.text = replayNames.Count == 0
                ? "本地暂无录像。可以粘贴分享码打开，或连接观战端口。"
                : "尚未打开录像。请选择本地录像，或粘贴分享码打开。";
            UpdateControls();
        }

        void OpenShare()
        {
            OpenRecord(ShareCode.Decode(shareCode.value));
        }

        void OpenRecord(BattleRecord record)
        {
            if (Spectating) throw new InvalidOperationException("请先停止观战，再打开录像。");
            ReplayStorage.CheckPolicy(record);
            var content = args.Content;
            if (content == null || content.PackId != record.PackId)
            {
                var resolve = args.ResolvePack ?? fallbackResolver ?? ReplayStorage.ResolveBuiltIn;
                content = resolve(record.PackId);
            }
            if (content == null) throw new InvalidOperationException("未找到录像所需的内容包。");
            // 完整验证成功后才替换当前录像，坏分享码不会抹掉正在回看的内容。
            var candidate = new ReplayNavigator(content, record, bestEffort: true);
            StopPlayback();
            animator?.SkipToEnd();
            navigator = candidate;
            displayedBattle = null;
            RefreshFrame();
            Message("录像已打开。回合 0 为开场，可逐回合查看或自动播放。"
                + (string.IsNullOrEmpty(candidate.CompatibilityWarning) ? "" : "\n兼容提示：" + candidate.CompatibilityWarning));
        }

        void RequireReplay()
        {
            if (Spectating) throw new InvalidOperationException("观战只显示已公开画面，不能跳转或导出尚未完整的录像。");
            if (navigator == null) throw new InvalidOperationException("请先打开一份录像。");
        }

        void Move(bool forward)
        {
            RequireReplay();
            StopPlayback();
            animator.SkipToEnd();
            int previousIndex = navigator.Index;
            if (forward) navigator.Next();
            else navigator.Previous();
            if (navigator.Index != previousIndex) RefreshFrame(forward);
            else UpdateControls();
        }

        void Jump()
        {
            RequireReplay();
            int target = -1;
            for (int i = 0; i < navigator.Count; i++)
                if (navigator.Turns[i] == jumpTurn.value) { target = i; break; }
            if (target < 0)
                throw new ArgumentOutOfRangeException(nameof(jumpTurn), "录像中没有该回合，请输入 0 至 "
                    + navigator.Turns[navigator.Count - 1] + " 之间的已有回合。");
            StopPlayback();
            animator.SkipToEnd();
            if (target != navigator.Index)
            {
                navigator.Seek(target);
                RefreshFrame();
            }
            else UpdateControls();
            Message(target == 0 ? "已回到开场。" : "已跳转到第 " + navigator.Turns[target] + " 回合。");
        }

        void TogglePlayback()
        {
            RequireReplay();
            if (playing)
            {
                StopPlayback();
                // 接口没有暂停进度，暂停时先落到当前回合终态，避免画面继续播放。
                animator.SkipToEnd();
            }
            else
            {
                if (navigator.Index == navigator.Count - 1)
                {
                    animator.SkipToEnd();
                    navigator.Seek(0);
                    RefreshFrame();
                }
                playing = navigator.Count > 1;
                elapsed = 0;
            }
            UpdateControls();
        }

        public void Tick(float dt)
        {
            if (disposed || dt <= 0 || float.IsNaN(dt) || float.IsInfinity(dt)) return;
            Attempt(() =>
            {
                TimelineAnimator?.Tick(dt);
                if (Spectating) { TickSpectator(); return; }
                if (!playing || navigator == null) return;
                if (animator.IsPlaying) return;
                // 切回窗口时不把长时间挂起当成连续播放，避免瞬间跳过整份录像。
                elapsed += Math.Min(dt, 1) * PlaybackSpeed;
                while (playing && elapsed >= 1 && !animator.IsPlaying)
                {
                    elapsed -= 1;
                    navigator.Next();
                    RefreshFrame(true);
                    if (navigator.Index == navigator.Count - 1)
                    {
                        StopPlayback();
                        UpdateControls();
                        Message("已播放到录像末尾。");
                    }
                }
            });
        }

        void StopPlayback()
        {
            playing = false;
            elapsed = 0;
        }

        float PlaybackSpeed => speed.index == 0 ? .5f : speed.index == 2 ? 2 : speed.index == 3 ? 4 : 1;

        void RefreshFrame(bool animate = false)
        {
            var battle = navigator.Current;
            int turn = navigator.Turns[navigator.Index];
            position.text = (turn == 0 ? "开场" : "第 " + turn + " 回合") + " · "
                + (navigator.Index + 1) + " / " + navigator.Count + (battle.Phase == Phase.Over ? " · 已结束" : "");
            jumpTurn.SetValueWithoutNotify(turn);
            RenderBattle(battle, battle.Log.Count - navigator.FrameLog.Count, navigator.FrameEventStart, animate);
        }

        void RenderBattle(Battle source, int logStart, int eventStart, bool animate)
        {
            var battle = TextInput.ForDisplay(source);
            var view = BattleViewModel.From(battle, 0, settings);
            var before = animate && displayedBattle != null
                ? BattleViewModel.From(TextInput.ForDisplay(displayedBattle), 0, settings) : view;
            if (args.Animator == null && !ReferenceEquals(animatedContent, source.Db))
            {
                ownedAnimator?.Dispose();
                animatedContent = source.Db;
                animator = ownedAnimator = new TimelineAnimator(stage, source.Db, portraits,
                    skillFx: id => source.Db.Art.SkillFx(id));
                // 资源头像本身已有程序生成字形，避免占位动画再叠一层相同首字。
                foreach (var card in new[] { stage.Fighter0, stage.Fighter1 })
                    card.Q<Label>("timeline-fighter-initial").style.display = DisplayStyle.None;
            }
            packLabel.text = "内容包：" + battle.Db.PackName
                + (string.IsNullOrEmpty(view.Notice) ? "" : "\n" + view.Notice);
            stage.FieldBanner.text = battle.Phase == Phase.Over
                ? battle.Winner < 0 ? "对战结束：平局" : "对战结束：" + view.Sides[battle.Winner].Name + " 获胜"
                : "当前已公开局面";
            details.Clear();
            for (int side = 0; side < view.Sides.Length; side++)
            {
                var sideView = view.Sides[side];
                var active = sideView.Team[sideView.Active];
                RenderFighter(side == 0 ? stage.Fighter0 : stage.Fighter1, battle.Db, sideView, active);
                details.Add(Text(sideView.Name + " · " + active.Name));
                details.Add(Text("人设：" + active.PersonaName + "\n" + active.PersonaDescription));
                if (battle.Db.IsMixed) details.Add(Text("来源声明：" + battle.Db.NoticeFor(active.Id)));
                if (active.Statuses.Count == 0) details.Add(Text("当前无状态或直播间效果。"));
                foreach (var chip in active.Statuses) details.Add(Text(chip.Tooltip));
                details.Add(Text("队伍：" + string.Join(" / ", sideView.Team.Select(fighter => fighter.Name + " "
                    + (fighter.Fainted ? "已退场" : fighter.Hp + "/" + fighter.MaxHp)))));
            }
            log.Clear();
            foreach (var entry in battle.Log.Skip(logStart)) log.Add(Text("【" + entry.Node + "】" + entry.Text));
            if (log.contentContainer.childCount == 0) log.Add(Text("本回合暂无日志。"));
            log.scrollOffset = Vector2.zero;
            // 后退和跳转仍保留全部提示，由 Core 的零倍速语义直接呈现终态。
            TimelineAnimator?.Prepare(battle, before, view);
            animator.Play(BattleTimeline.Build(battle.Events, eventStart,
                battle.Events.Count, animate && settings.Animations ? PlaybackSpeed : 0));
            // 留住自己的公开快照，下一次客户端收帧或游标推进不能改变动画起点。
            displayedBattle = source.Clone();
            UpdateControls();
        }

        void RenderFighter(VisualElement card, ContentDb content, SideView side, FighterView fighter)
        {
            card.Q<Label>("fighter-name").text = side.Name + " · " + fighter.Name;
            card.Q<Label>("fighter-tracks").text = fighter.Tracks;
            card.Q<Image>(StageNames.Portrait).image = portraits.Get(content, fighter.Id);
            var hp = card.Q<ProgressBar>(StageNames.HpBar);
            hp.value = fighter.Pct;
            hp.title = fighter.Hp + " / " + fighter.MaxHp;
            PlainText(hp);
            var titleContainer = hp.Q<VisualElement>(className: "unity-progress-bar__title-container");
            // 时间线显示逐帧人气，静态终态标题不能叠在动画数值上；保留 title 供无动画模式读取。
            if (titleContainer != null)
                titleContainer.style.display = TimelineAnimator != null ? DisplayStyle.None : DisplayStyle.Flex;
            var statuses = card.Q<VisualElement>(StageNames.StatusRow);
            statuses.Clear();
            if (TimelineAnimator != null) return;
            foreach (var chip in fighter.Statuses)
            {
                var badge = Text(chip.Name + " " + chip.Duration);
                badge.tooltip = chip.Tooltip;
                badge.AddToClassList("status-icon");
                statuses.Add(badge);
            }
            if (fighter.Statuses.Count == 0) statuses.Add(Text("无状态"));
        }

        void UpdateControls()
        {
            bool loaded = Spectating ? displayedBattle != null : navigator != null;
            if (playback != null) playback.style.display = loaded ? DisplayStyle.Flex : DisplayStyle.None;
            if (empty != null) empty.style.display = loaded ? DisplayStyle.None : DisplayStyle.Flex;
            bool replay = loaded && !Spectating;
            copyShare?.SetEnabled(replay);
            previous?.SetEnabled(replay && navigator.Index > 0);
            next?.SetEnabled(replay && navigator.Index + 1 < navigator.Count);
            jump?.SetEnabled(replay);
            jumpTurn?.SetEnabled(replay);
            play?.SetEnabled(replay && navigator.Count > 1);
            if (play != null) play.text = playing ? "暂停" : "自动播放";
            openFile?.SetEnabled(!Spectating && saveStore != null && replayNames.Count > 0);
            UpdateSpectatorControls();
        }

        void Attempt(Action action)
        {
            if (disposed) return;
            try { action(); }
            catch (Exception error)
            {
                StopPlayback();
                UpdateControls();
                Message("无法完成操作：" + error.Message, true);
            }
        }

        void AttemptLibrary(Action action)
        {
            if (disposed) return;
            try
            {
                action();
                libraryFeedback.text = "";
                libraryFeedback.style.display = DisplayStyle.None;
            }
            catch (Exception error)
            {
                libraryFeedback.text = "本地录像库无法读取：" + error.Message;
                libraryFeedback.style.display = DisplayStyle.Flex;
                UpdateControls();
            }
        }

        void Message(string message, bool error = false)
        {
            if (feedback == null) return;
            feedback.text = message;
            feedback.style.color = error ? new Color(1f, .57f, .62f) : new Color(.65f, .9f, .75f);
            feedback.style.display = DisplayStyle.Flex;
        }

        Button AddButton(VisualElement parent, string label, string name, Action action)
        {
            var button = new Button { text = label, name = name, enableRichText = false };
            button.AddToClassList("screen-button");
            button.style.minWidth = 136;
            button.style.minHeight = 48;
            button.style.fontSize = 22;
            button.style.paddingTop = button.style.paddingBottom = 6;
            button.style.paddingLeft = button.style.paddingRight = 14;
            button.style.marginTop = button.style.marginBottom = 4;
            button.style.marginLeft = button.style.marginRight = 4;
            button.clicked += action;
            unbind.Add(() => button.clicked -= action);
            parent.Add(button);
            return button;
        }

        static VisualElement Row()
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.flexWrap = Wrap.Wrap;
            row.style.alignItems = Align.Center;
            row.style.marginTop = row.style.marginBottom = 5;
            return row;
        }

        static Label Text(string value, string name = null)
        {
            var label = new Label(value) { name = name, enableRichText = false };
            label.style.whiteSpace = WhiteSpace.Normal;
            label.style.fontSize = 20;
            label.style.flexShrink = 0;
            label.style.marginTop = label.style.marginBottom = 3;
            return label;
        }

        static void PlainText(VisualElement element)
        {
            element.Query<TextElement>().ForEach(text => text.enableRichText = false);
        }

        static void StyleField(VisualElement field, Label label, float labelWidth)
        {
            field.style.minHeight = 48;
            field.style.flexShrink = 0;
            field.style.marginTop = field.style.marginBottom = 4;
            field.style.marginLeft = field.style.marginRight = 4;
            field.style.fontSize = 22;
            field.style.alignItems = Align.Center;
            // Unity 默认标签最小宽度会吃掉短字段的输入区，必须一起覆盖宽度约束。
            label.style.width = label.style.minWidth = label.style.maxWidth = labelWidth;
            label.style.flexBasis = labelWidth;
            label.style.flexGrow = label.style.flexShrink = 0;
            label.style.marginLeft = label.style.marginTop = label.style.marginBottom = 0;
            label.style.marginRight = 8;
            label.style.whiteSpace = WhiteSpace.Normal;
            var input = field.Q<VisualElement>(className: "unity-base-field__input");
            if (input != null)
            {
                input.style.flexGrow = 1;
                input.style.flexShrink = 1;
                input.style.minWidth = 100;
                input.style.height = input.style.minHeight = 44;
                input.style.backgroundColor = (Color)new Color32(28, 37, 73, 255);
                input.style.borderTopColor = input.style.borderBottomColor = input.style.borderLeftColor
                    = input.style.borderRightColor = (Color)new Color32(91, 106, 155, 255);
                input.style.borderTopWidth = input.style.borderBottomWidth = input.style.borderLeftWidth
                    = input.style.borderRightWidth = 1;
                input.style.paddingLeft = input.style.paddingRight = 8;
                input.style.paddingTop = input.style.paddingBottom = 3;
                input.style.borderTopLeftRadius = input.style.borderTopRightRadius
                    = input.style.borderBottomLeftRadius = input.style.borderBottomRightRadius = 6;
            }
            field.Query<TextElement>().ForEach(text =>
            {
                text.enableRichText = false;
                text.style.fontSize = 22;
                text.style.color = (Color)new Color32(232, 235, 250, 255);
                text.style.unityTextAlign = TextAnchor.MiddleLeft;
            });
            var selected = field.Q<TextElement>(className: "unity-base-popup-field__text");
            if (selected != null)
            {
                selected.style.minWidth = 0;
                selected.style.flexGrow = 1;
                selected.style.flexShrink = 1;
            }
            var arrow = field.Q<VisualElement>(className: "unity-base-popup-field__arrow");
            if (arrow != null)
            {
                arrow.style.width = arrow.style.minWidth = 18;
                arrow.style.height = 18;
                arrow.style.flexShrink = 0;
                arrow.style.marginLeft = 8;
                arrow.style.unityBackgroundImageTintColor = (Color)new Color32(232, 235, 250, 255);
            }
        }

        static void StyleHpBar(ProgressBar hp)
        {
            hp.style.height = hp.style.minHeight = 44;
            hp.style.flexShrink = 0;
            hp.style.fontSize = 20;
            foreach (string className in new[] { "unity-progress-bar__container", "unity-progress-bar__background" })
            {
                var element = hp.Q<VisualElement>(className: className);
                if (element == null) continue;
                element.style.height = element.style.minHeight = 44;
                element.style.marginTop = element.style.marginBottom = 0;
                element.style.paddingTop = element.style.paddingBottom = 0;
            }
            var background = hp.Q<VisualElement>(className: "unity-progress-bar__background");
            if (background != null) background.style.backgroundColor = (Color)new Color32(16, 24, 47, 255);
            var fill = hp.Q<VisualElement>(className: "unity-progress-bar__progress");
            if (fill != null)
            {
                fill.style.height = Length.Percent(100);
                fill.style.backgroundColor = (Color)new Color32(35, 88, 81, 255);
            }
            var titleContainer = hp.Q<VisualElement>(className: "unity-progress-bar__title-container");
            if (titleContainer != null)
            {
                titleContainer.style.position = Position.Absolute;
                titleContainer.style.left = titleContainer.style.right = 0;
                titleContainer.style.top = titleContainer.style.bottom = 0;
                titleContainer.style.alignItems = Align.Center;
                titleContainer.style.justifyContent = Justify.Center;
            }
            hp.Query<Label>().ForEach(title =>
            {
                title.enableRichText = false;
                title.style.fontSize = 20;
                title.style.minHeight = 32;
                title.style.marginTop = title.style.marginBottom = 0;
                title.style.paddingTop = title.style.paddingBottom = 0;
                title.style.whiteSpace = WhiteSpace.NoWrap;
                title.style.unityTextAlign = TextAnchor.MiddleCenter;
                title.style.color = Color.white;
            });
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            StopPlayback();
            CloseSpectator();
            foreach (var remove in unbind) remove();
            unbind.Clear();
            try { animator?.SkipToEnd(); }
            catch (Exception) { }
            ownedAnimator?.Dispose();
            ownedAnimator = null;
            animatedContent = null;
            ownedPortraits?.Dispose();
            ownedPortraits = null;
            portraits = null;
            navigator = null;
            saveStore = null;
            animator = null;
            displayedBattle = null;
            clipboard = null;
            settings = null;
            router = null;
            args = null;
            page = playback = details = null;
            stage = null;
            log = null;
            files = speed = null;
            shareCode = null;
            jumpTurn = null;
            feedback = libraryFeedback = empty = position = packLabel = null;
            openFile = previous = next = jump = play = copyShare = null;
            replayNames.Clear();
        }
    }
}
