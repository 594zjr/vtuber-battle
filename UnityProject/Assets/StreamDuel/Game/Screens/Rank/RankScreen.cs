using System;
using System.Collections.Generic;
using System.Globalization;
using StreamDuel.Core;
using UnityEngine;
using UnityEngine.UIElements;

namespace StreamDuel.Game
{
    public sealed class RankScreen : IScreen
    {
        readonly List<Action> unbind = new List<Action>();
        VisualElement page, body, confirmation, badge;
        ScrollView history;
        Label season, rating, tier, next, games, placement, peak, notice, confirmationError;
        Button refresh, newSeason, confirm, cancel;
        Func<Profile> loadProfile, startNewSeason;
        PlayerRating snapshot;
        IVisualElementScheduledItem focusRequest;
        bool loaded, confirmationOpen, busy, disposed;
        int badgeTier = -1;

        public string Id => ScreenIds.Rank;

        public VisualElement Build(ScreenRouter router, object args)
        {
            Dispose();
            disposed = false;
            ConfigureSource(args as RankScreenArgs);
            var options = args as RankScreenArgs;
            var returnArgs = args as ScreenArgs;
            string returnScreen = options?.ReturnScreenId ?? returnArgs?.ReturnScreenId ?? ScreenIds.Menu;
            object returnPayload = options != null ? options.ReturnArgs : returnArgs?.ReturnArgs;
            if (string.IsNullOrWhiteSpace(returnScreen)) returnScreen = ScreenIds.Menu;

            page = new VisualElement { name = "rank-screen" };
            page.AddToClassList("rank-screen");
            var theme = UnityEngine.Resources.Load<StyleSheet>("RankTheme");
            if (theme == null) throw new InvalidOperationException("缺少排位页面样式资源 RankTheme。");
            page.styleSheets.Add(theme);
            body = new VisualElement { name = "rank-panel" };
            body.AddToClassList("rank-panel");
            page.Add(body);

            var heading = Container(body, "rank-heading");
            var title = Text(heading, "rank-title", "排位与赛季", "rank-title");
            title.style.unityFont = router.Resources.BoldFont;
            Text(heading, "rank-format", "本机 · 默认赛制", "rank-muted");

            var current = Container(body, "rank-current");
            badge = new VisualElement { name = "rank-badge", pickingMode = PickingMode.Ignore };
            badge.AddToClassList("rank-badge");
            current.Add(badge);
            badge.generateVisualContent += DrawBadge;
            var currentText = Container(current, "rank-current-text");
            season = Text(currentText, "rank-season", "当前赛季：—", "rank-season");
            var scoreRow = Container(currentText, "rank-score-row");
            rating = Text(scoreRow, "rank-rating", "排位分：—", "rank-score");
            tier = Text(scoreRow, "rank-tier", "段位：—", "rank-tier");
            next = Text(currentText, "rank-next", "下一段位：—", "rank-copy");
            var stats = Container(currentText, "rank-stats");
            games = Text(stats, "rank-games", "本季排位场数：—", "rank-copy");
            peak = Text(stats, "rank-peak", "本季最高分：—", "rank-copy");
            placement = Text(currentText, "rank-placement", "定级状态：—", "rank-copy");

            var historyHeading = Container(body, "rank-history-heading");
            Text(historyHeading, "rank-history-title", "历史赛季", "rank-subtitle");
            var historyButtons = Container(historyHeading, "rank-history-buttons");
            AddButton(historyButtons, "rank-history-up", "向上翻页", () => MoveHistory(-1), "rank-small-button");
            AddButton(historyButtons, "rank-history-down", "向下翻页", () => MoveHistory(1), "rank-small-button");
            history = new ScrollView(ScrollViewMode.Vertical) { name = "rank-history", focusable = true };
            history.AddToClassList("rank-history");
            history.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            history.verticalScrollerVisibility = ScrollerVisibility.Auto;
            body.Add(history);

            var noticeScroll = new ScrollView(ScrollViewMode.Vertical) { name = "rank-notice-scroll" };
            noticeScroll.AddToClassList("rank-notice-scroll");
            noticeScroll.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            notice = Text(noticeScroll.contentContainer, "rank-notice", "", "rank-notice");
            body.Add(noticeScroll);
            var footer = Container(body, "rank-footer");
            refresh = AddButton(footer, "rank-refresh", "刷新档案", Refresh);
            newSeason = AddButton(footer, "rank-new-season", "开始新赛季", OpenConfirmation);
            var spacer = new VisualElement();
            spacer.style.flexGrow = 1;
            footer.Add(spacer);
            var back = AddButton(footer, "rank-back", "返回", () => router.Show(returnScreen, returnPayload));
            Text(body, "rank-controls", "Tab / 方向键移动 · Enter 确认 · PageUp / PageDown 翻历史 · 弹窗内 Esc 取消", "rank-controls");
            BuildConfirmation();
            BindKeyboard();
            Refresh();
            focusRequest = page.schedule.Execute(() => { if (!disposed && !confirmationOpen) back.Focus(); });
            return page;
        }

        void ConfigureSource(RankScreenArgs options)
        {
            if (options != null && (options.LoadProfile != null || options.StartNewSeason != null))
            {
                loadProfile = options.LoadProfile;
                startNewSeason = options.StartNewSeason;
                return;
            }
            // 默认验收入口必须在求值用户档案路径之前分流，浏览页面也不能触碰用户存档。
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "--t135-smoke-test") >= 0)
            {
                var memory = new Profile();
                loadProfile = () => memory;
                startNewSeason = () =>
                {
                    PlayerRating previous;
                    var value = memory.Ratings.TryGetValue(Profile.DefaultRatingFormat, out previous)
                        ? previous.Clone() : new PlayerRating();
                    RatingSystem.NewSeason(value);
                    memory.Ratings[Profile.DefaultRatingFormat] = value;
                    return memory;
                };
                return;
            }
            string path = Application.persistentDataPath;
            loadProfile = () => FileSaveStore.LoadProfileSnapshot(path, true);
            startNewSeason = () => new FileSaveStore(path).NewRatingSeason();
        }

        void Refresh()
        {
            if (disposed || busy || confirmationOpen) return;
            try
            {
                if (loadProfile == null) throw new InvalidOperationException("没有提供排位档案读取接口。");
                var value = Snapshot(loadProfile());
                snapshot = value;
                loaded = true;
                Render();
                Message(startNewSeason == null ? "档案已读取；此来源只支持查看，不能开始新赛季。" :
                    "仅显示本机默认赛制的排位记录。赛季由你手动开启，不随日期自动切换。", false);
            }
            catch (Exception ex)
            {
                loaded = false;
                if (snapshot == null) history.Clear();
                Message("读取排位档案失败：" + ex.Message + (snapshot == null
                    ? "\n未显示默认分数；请修复档案后刷新。" : "\n当前保留上次读取的快照，并非最新档案；请刷新成功后再切季。"), true);
            }
            UpdateControls();
        }

        static PlayerRating Snapshot(Profile profile)
        {
            if (profile == null || profile.Ratings == null) throw new FormatException("排位档案为空或缺少有效排位记录。");
            PlayerRating value;
            if (!profile.Ratings.TryGetValue(Profile.DefaultRatingFormat, out value)) return new PlayerRating();
            if (value == null) throw new FormatException("默认赛制排位记录不能为空。");
            return value.Clone();
        }

        void Render()
        {
            var currentTier = RatingSystem.TierOf(snapshot.Rating);
            season.text = "第 " + Number(snapshot.Season) + " 赛季";
            rating.text = "排位分 " + Number(snapshot.Rating);
            tier.text = currentTier.Name;
            games.text = "本季排位场数：" + Number(snapshot.Games);
            peak.text = "本季最高分：" + Number(snapshot.PeakRating);
            placement.text = snapshot.IsPlaced ? "已定级 · 新赛季继续保留定级资格" :
                "定级中 · 本赛季已完成 " + Number(snapshot.Games) + " / 10 场";
            badgeTier = 0;
            for (int i = 0; i < RatingSystem.Tiers.Count; i++)
                if (ReferenceEquals(RatingSystem.Tiers[i], currentTier)) badgeTier = i;
            if (badgeTier + 1 < RatingSystem.Tiers.Count)
            {
                var following = RatingSystem.Tiers[badgeTier + 1];
                long difference = (long)following.MinRating - snapshot.Rating;
                next.text = "距离「" + following.Name + "」还差 " + difference.ToString(CultureInfo.InvariantCulture) + " 分";
            }
            else next.text = "已达最高段位";
            badge.MarkDirtyRepaint();
            history.Clear();
            if (snapshot.History.Count == 0)
                Text(history.contentContainer, "rank-history-empty", "暂无历史赛季。开始新赛季后，当前赛季会归档到这里。", "rank-empty");
            for (int i = snapshot.History.Count - 1; i >= 0; i--)
            {
                var item = snapshot.History[i];
                var row = new VisualElement { name = "rank-history-season-" + Number(item.Season), userData = item };
                row.AddToClassList("rank-history-row");
                Text(row, "rank-history-title-" + Number(item.Season), "第 " + Number(item.Season) + " 赛季 · " + item.Tier, "rank-history-name");
                Text(row, "rank-history-values-" + Number(item.Season), "最终分 " + Number(item.FinalRating)
                    + "　最高分 " + Number(item.PeakRating) + "　排位场数 " + Number(item.Games), "rank-copy");
                history.Add(row);
            }
            history.scrollOffset = Vector2.zero;
        }

        void BuildConfirmation()
        {
            confirmation = new VisualElement { name = "rank-confirmation" };
            confirmation.AddToClassList("rank-confirmation");
            confirmation.style.display = DisplayStyle.None;
            page.Add(confirmation);
            var dialog = Container(confirmation, "rank-dialog");
            Text(dialog, "rank-confirm-title", "开始新赛季？", "rank-title");
            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.AddToClassList("rank-dialog-scroll");
            scroll.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            dialog.Add(scroll);
            Text(scroll.contentContainer, "rank-confirm-description", "当前赛季的最终分、最高分、段位和场数将归档。新赛季会按排位规则调整起始分，并从 0 场开始；已取得的定级资格保留。\n\n确认后会读取最新档案并保存新赛季，此操作不能通过本页撤销。", "rank-dialog-copy");
            confirmationError = Text(scroll.contentContainer, "rank-confirm-error", "", "rank-error");
            var actions = Container(dialog, "rank-dialog-actions");
            cancel = AddButton(actions, "rank-cancel", "取消", CloseConfirmation, null, true);
            confirm = AddButton(actions, "rank-confirm", "确认开始新赛季", ConfirmNewSeason, null, true);
        }

        void OpenConfirmation()
        {
            if (!loaded || startNewSeason == null || busy || confirmationOpen) return;
            confirmationError.text = "";
            confirmationOpen = true;
            body.SetEnabled(false);
            confirmation.style.display = DisplayStyle.Flex;
            cancel.Focus();
            RequestCancelFocus();
        }

        void CloseConfirmation()
        {
            if (!confirmationOpen || busy || disposed) return;
            confirmationOpen = false;
            focusRequest?.Pause();
            confirmation.style.display = DisplayStyle.None;
            body.SetEnabled(true);
            UpdateControls();
            newSeason.Focus();
        }

        void ConfirmNewSeason()
        {
            if (!confirmationOpen || !loaded || busy || startNewSeason == null || disposed) return;
            busy = true;
            confirm.SetEnabled(false);
            cancel.SetEnabled(false);
            bool succeeded = false;
            try
            {
                var value = Snapshot(startNewSeason());
                snapshot = value;
                Render();
                Message("已开启第 " + Number(snapshot.Season) + " 赛季，上一赛季已归档。", false);
                succeeded = true;
            }
            catch (Exception ex)
            {
                string error = "开始新赛季失败：" + ex.Message + "\n当前显示快照未改变。可重试，或取消后刷新档案。";
                confirmationError.text = error;
                Message(error, true);
            }
            finally
            {
                busy = false;
                confirm.SetEnabled(true);
                cancel.SetEnabled(true);
            }
            if (succeeded) CloseConfirmation();
        }

        void BindKeyboard()
        {
            var root = page;
            EventCallback<GeometryChangedEvent> resize = evt => root.EnableInClassList("rank-compact", evt.newRect.height < 850);
            root.RegisterCallback(resize);
            unbind.Add(() => root.UnregisterCallback(resize));
            EventCallback<FocusInEvent> focusIn = evt =>
            {
                if (confirmationOpen && evt.target is VisualElement target && !confirmation.Contains(target))
                    RequestCancelFocus();
            };
            root.RegisterCallback(focusIn, TrickleDown.TrickleDown);
            unbind.Add(() => root.UnregisterCallback(focusIn, TrickleDown.TrickleDown));
            var modal = confirmation;
            EventCallback<FocusOutEvent> focusOut = evt =>
            {
                if (confirmationOpen && (!(evt.relatedTarget is VisualElement target) || !modal.Contains(target)))
                    RequestCancelFocus();
            };
            modal.RegisterCallback(focusOut);
            unbind.Add(() => modal.UnregisterCallback(focusOut));
            EventCallback<NavigationCancelEvent> cancelEvent = evt =>
            {
                if (!confirmationOpen) return;
                evt.StopImmediatePropagation();
                root.focusController?.IgnoreEvent(evt);
                CloseConfirmation();
            };
            root.RegisterCallback(cancelEvent, TrickleDown.TrickleDown);
            unbind.Add(() => root.UnregisterCallback(cancelEvent, TrickleDown.TrickleDown));
            EventCallback<NavigationMoveEvent> move = evt =>
            {
                if (!confirmationOpen) return;
                evt.StopImmediatePropagation();
                root.focusController?.IgnoreEvent(evt);
                ToggleConfirmationFocus();
            };
            root.RegisterCallback(move, TrickleDown.TrickleDown);
            unbind.Add(() => root.UnregisterCallback(move, TrickleDown.TrickleDown));
            EventCallback<KeyDownEvent> key = evt =>
            {
                if (confirmationOpen)
                {
                    if (evt.keyCode != KeyCode.Escape && evt.keyCode != KeyCode.Tab) return;
                    evt.StopImmediatePropagation();
                    root.focusController?.IgnoreEvent(evt);
                    if (evt.keyCode == KeyCode.Escape) CloseConfirmation();
                    else ToggleConfirmationFocus();
                    return;
                }
                if (evt.keyCode != KeyCode.PageUp && evt.keyCode != KeyCode.PageDown
                    && evt.keyCode != KeyCode.Home && evt.keyCode != KeyCode.End) return;
                evt.StopImmediatePropagation();
                root.focusController?.IgnoreEvent(evt);
                if (evt.keyCode == KeyCode.Home) history.scrollOffset = Vector2.zero;
                else if (evt.keyCode == KeyCode.End) history.scrollOffset = new Vector2(0, HistoryMaximum());
                else MoveHistory(evt.keyCode == KeyCode.PageUp ? -1 : 1);
            };
            root.RegisterCallback(key, TrickleDown.TrickleDown);
            unbind.Add(() => root.UnregisterCallback(key, TrickleDown.TrickleDown));
        }

        void ToggleConfirmationFocus()
        {
            if (ReferenceEquals(page.focusController?.focusedElement, cancel)) confirm.Focus();
            else cancel.Focus();
        }

        void RequestCancelFocus()
        {
            focusRequest?.Pause();
            focusRequest = page.schedule.Execute(() => { if (!disposed && confirmationOpen) cancel.Focus(); });
        }

        void MoveHistory(int direction)
        {
            if (confirmationOpen || history == null) return;
            float distance = Mathf.Max(80, history.contentViewport.layout.height * 0.8f);
            history.scrollOffset = new Vector2(0, Mathf.Clamp(history.scrollOffset.y + direction * distance, 0, HistoryMaximum()));
        }

        float HistoryMaximum()
        {
            return Mathf.Max(0, history.contentContainer.layout.height - history.contentViewport.layout.height);
        }

        void UpdateControls()
        {
            refresh.SetEnabled(!busy && loadProfile != null);
            newSeason.SetEnabled(!busy && loaded && startNewSeason != null);
        }

        void Message(string text, bool error)
        {
            notice.text = text;
            notice.EnableInClassList("rank-error", error);
        }

        void DrawBadge(MeshGenerationContext context)
        {
            var painter = context.painter2D;
            Color[] colors = { new Color(0.55f, 0.70f, 0.80f), new Color(0.44f, 0.82f, 0.70f),
                new Color(0.96f, 0.72f, 0.34f), new Color(0.74f, 0.61f, 0.96f), new Color(1f, 0.54f, 0.57f) };
            painter.strokeColor = badgeTier < 0 ? new Color(0.50f, 0.55f, 0.64f) : colors[Mathf.Min(badgeTier, colors.Length - 1)];
            painter.fillColor = new Color(0.10f, 0.15f, 0.27f);
            painter.lineWidth = 5;
            var bounds = badge.contentRect;
            var center = new Vector2(bounds.width / 2, bounds.height / 2);
            float radius = Mathf.Max(0, Mathf.Min(bounds.width, bounds.height) / 2 - 10);
            int sides = 4 + Mathf.Max(0, badgeTier);
            painter.BeginPath();
            for (int i = 0; i < sides; i++)
            {
                float angle = (i * 360f / sides - 90) * Mathf.Deg2Rad;
                var point = center + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius;
                if (i == 0) painter.MoveTo(point);
                else painter.LineTo(point);
            }
            painter.ClosePath();
            painter.Fill();
            painter.Stroke();
            painter.BeginPath();
            painter.MoveTo(center + new Vector2(-radius * 0.40f, radius * 0.18f));
            painter.LineTo(center + new Vector2(0, -radius * 0.35f));
            painter.LineTo(center + new Vector2(radius * 0.40f, radius * 0.18f));
            painter.Stroke();
        }

        Button AddButton(VisualElement parent, string name, string text, Action action, string extraClass = null, bool modal = false)
        {
            var button = new Button { name = name, text = text, enableRichText = false };
            button.AddToClassList("rank-button");
            if (extraClass != null) button.AddToClassList(extraClass);
            Action clicked = () => { if (!disposed && !busy && confirmationOpen == modal) action(); };
            button.clicked += clicked;
            unbind.Add(() => button.clicked -= clicked);
            parent.Add(button);
            return button;
        }

        static VisualElement Container(VisualElement parent, string className)
        {
            var element = new VisualElement();
            element.AddToClassList(className);
            parent.Add(element);
            return element;
        }

        static Label Text(VisualElement parent, string name, string text, string className)
        {
            var label = new Label(text) { name = name, enableRichText = false };
            label.AddToClassList(className);
            parent.Add(label);
            return label;
        }

        static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

        public void Tick(float dt) { }

        public void Dispose()
        {
            disposed = true;
            focusRequest?.Pause();
            focusRequest = null;
            foreach (var remove in unbind) remove();
            unbind.Clear();
            if (badge != null) badge.generateVisualContent -= DrawBadge;
            loadProfile = null;
            startNewSeason = null;
            snapshot = null;
            page = body = confirmation = badge = null;
            history = null;
            season = rating = tier = next = games = placement = peak = notice = confirmationError = null;
            refresh = newSeason = confirm = cancel = null;
            loaded = confirmationOpen = busy = false;
            badgeTier = -1;
        }
    }
}
