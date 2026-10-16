using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using StreamDuel.Core;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace StreamDuel.Game
{
    public sealed class TeamCardPanel : IDisposable
    {
        readonly Font bold;
        readonly IPortraitProvider portraits;
        readonly Action<string> selectionChanged;
        readonly Action closed;
        readonly List<(VisualElement Element, bool Enabled)> background = new List<(VisualElement, bool)>();
        readonly List<VisualElement> focusOrder = new List<VisualElement>();
        readonly ScrollView scroll;
        readonly Button previous, next, close;
        readonly Label position, feedback;
        ContentDb content;
        string[] roster = Array.Empty<string>();
        int index;
        bool colorblind, disposed;
        VisualElement opener, focusScope, lastFocus;
        IVisualElementScheduledItem initialFocus;

        public VisualElement Root { get; }
        public bool IsOpen { get; private set; }
        public string CurrentId { get; private set; }

        public TeamCardPanel(Font regular, Font bold, IPortraitProvider portraits,
            Action<string> selectionChanged, Action closed)
        {
            this.bold = bold;
            this.portraits = portraits;
            this.selectionChanged = selectionChanged;
            this.closed = closed;
            Root = new VisualElement { name = "team-card-modal", pickingMode = PickingMode.Position };
            Root.AddToClassList("t134-card");
            Root.style.display = DisplayStyle.None;
            Root.style.unityFont = regular;
            var stylesheet = Resources.Load<StyleSheet>("T134Card");
            if (stylesheet == null) throw new InvalidOperationException("缺少主播资料卡样式 T134Card。");
            Root.styleSheets.Add(stylesheet);
            var dialog = Box(Root, "team-card-panel", "t134-card__panel");
            var header = Box(dialog, "team-card-header", "t134-card__header");
            Text(header, "team-card-title", "主播资料卡", "t134-card__title").style.unityFont = bold;
            position = Text(header, "team-card-position", "", "t134-card__muted");
            previous = Button(header, "team-card-prev", "上一位", () => Move(-1));
            next = Button(header, "team-card-next", "下一位", () => Move(1));
            close = Button(header, "team-card-close", "关闭", Close);
            feedback = Text(dialog, "team-card-error", "", "t134-card__error");
            feedback.style.display = DisplayStyle.None;
            scroll = new ScrollView(ScrollViewMode.Vertical)
            {
                name = "team-card-scroll", horizontalScrollerVisibility = ScrollerVisibility.Hidden,
                verticalScrollerVisibility = ScrollerVisibility.Auto, focusable = true, tabIndex = 0
            };
            scroll.AddToClassList("t134-card__scroll");
            scroll.contentContainer.AddToClassList("t134-card__content");
            dialog.Add(scroll);
            focusOrder.Add(scroll);
            var footer = Box(dialog, "team-card-footer", "t134-card__footer");
            Button(footer, "team-card-page-up", "向上翻页", () => Page(-1));
            Button(footer, "team-card-page-down", "向下翻页", () => Page(1));
            Text(footer, "team-card-controls", "← / → 或 LB / RB 切换主播 · Tab / 方向导航移动焦点 · Esc / B 关闭", "t134-card__controls");
            Root.RegisterCallback<KeyDownEvent>(OnKey, TrickleDown.TrickleDown);
            Root.RegisterCallback<NavigationMoveEvent>(OnNavigate, TrickleDown.TrickleDown);
            Root.RegisterCallback<NavigationCancelEvent>(OnCancel, TrickleDown.TrickleDown);
            Root.RegisterCallback<AttachToPanelEvent>(OnAttached);
            Root.RegisterCallback<DetachFromPanelEvent>(OnDetached);
        }

        public void Open(VisualElement host, ContentDb db, string id, bool colorblind)
        {
            if (disposed) throw new ObjectDisposedException(nameof(TeamCardPanel));
            if (host == null) throw new ArgumentNullException(nameof(host));
            var card = StreamerCard.Build(db, id);
            if (IsOpen) CloseCore(false, false);
            content = db;
            this.colorblind = colorblind;
            roster = db.Streamers.Select(streamer => streamer.Id).ToArray();
            index = Array.IndexOf(roster, id);
            opener = host.panel?.focusController.focusedElement as VisualElement;
            background.Clear();
            foreach (var child in host.Children().ToArray())
            {
                if (ReferenceEquals(child, Root)) continue;
                background.Add((child, child.enabledSelf));
                child.SetEnabled(false);
            }
            IsOpen = true;
            try
            {
                Root.style.display = DisplayStyle.Flex;
                host.Add(Root);
                AttachFocusGuard();
                Render(card, index);
                initialFocus = Root.schedule.Execute(() => { if (IsOpen) close.Focus(); });
            }
            catch (Exception error)
            {
                CloseCore(false, true);
                throw new InvalidOperationException("资料卡暂时无法打开，请重试。", error);
            }
        }

        void Render(StreamerCard card, int selectedIndex)
        {
            // 完整生成后才替换可见内容，提供器或宿主回调失败不会留下半张卡。
            var body = new VisualElement { name = "team-card-body" };
            body.style.minWidth = 0;
            var hero = Box(body, "team-card-hero", "t134-card__hero");
            var avatar = Box(hero, "team-card-avatar", "t134-card__avatar");
            Texture2D texture = portraits?.Get(content, card.Id);
            var image = new Image { name = "team-card-portrait", image = texture, scaleMode = ScaleMode.ScaleToFit };
            image.AddToClassList("t134-card__portrait");
            image.style.display = texture == null ? DisplayStyle.None : DisplayStyle.Flex;
            avatar.Add(image);
            var placeholder = Text(avatar, "team-card-placeholder", string.IsNullOrEmpty(card.Name) ? "人" : StringInfo.GetNextTextElement(card.Name), "t134-card__placeholder");
            placeholder.style.display = texture == null ? DisplayStyle.Flex : DisplayStyle.None;
            var identity = Box(hero, "team-card-identity", "t134-card__identity");
            Text(identity, "team-card-name", card.Name, "t134-card__name").style.unityFont = bold;
            Text(identity, "team-card-pack", card.PackName, "t134-card__muted");
            var tracks = Box(identity, "team-card-tracks", "t134-card__tracks");
            foreach (var track in card.Tracks)
            {
                var chip = Text(tracks, "team-card-track-" + track.Id, track.Symbol + " " + track.Name, "t134-card__track");
                chip.style.backgroundColor = TrackColor(track.Id);
            }

            var stats = Section(body, "team-card-stats", "六维基础数值");
            Text(stats, "team-card-stat-scale", "条形长度以本卡最高基础数值为满格；排名来自当前内容库。", "t134-card__muted");
            var statGrid = Box(stats, "team-card-stat-grid", "t134-card__stat-grid");
            int maximum = Math.Max(1, card.Stats.Max(stat => stat.Value));
            foreach (var stat in card.Stats)
            {
                var row = Box(statGrid, "stat-row-" + stat.Id, "t134-card__stat-row");
                var caption = Box(row, "stat-caption-" + stat.Id, "t134-card__stat-caption");
                Text(caption, "stat-name-" + stat.Id, stat.Name, "t134-card__stat-name");
                Text(caption, "stat-value-" + stat.Id, stat.Value.ToString(CultureInfo.InvariantCulture), "t134-card__stat-value");
                Text(caption, "stat-rank-" + stat.Id, "第 " + stat.Rank.ToString(CultureInfo.InvariantCulture)
                    + " / " + stat.Total.ToString(CultureInfo.InvariantCulture) + " 名", "t134-card__muted");
                var track = Box(row, "stat-track-" + stat.Id, "t134-card__bar");
                var fill = Box(track, "stat-fill-" + stat.Id, "t134-card__fill");
                // 仅将快照数值归一成条形宽度，不参与任何战斗计算。
                fill.style.width = Length.Percent(Mathf.Clamp01(stat.Value / (float)maximum) * 100);
            }

            var persona = Section(body, "team-card-persona", "人设特性");
            Text(persona, "team-card-persona-name", card.PersonaName, "t134-card__subtitle").style.unityFont = bold;
            Text(persona, "team-card-persona-text", card.PersonaText, "t134-card__body");
            var skills = Section(body, "team-card-skills", "技能资料");
            var skillGrid = Box(skills, "team-card-skill-grid", "t134-card__skill-grid");
            for (int skillIndex = 0; skillIndex < card.Skills.Count; skillIndex++) BuildSkill(skillGrid, card.Skills[skillIndex], skillIndex);
            var notice = Section(body, "team-card-source", "来源声明");
            Text(notice, "team-card-notice", card.Notice ?? "", "t134-card__body");
            if (string.IsNullOrEmpty(card.Notice)) notice.style.display = DisplayStyle.None;
            string oldId = CurrentId;
            CurrentId = card.Id;
            try { selectionChanged?.Invoke(CurrentId); }
            catch { CurrentId = oldId; throw; }
            index = selectedIndex;
            scroll.Clear();
            scroll.Add(body);
            scroll.scrollOffset = Vector2.zero;
            position.text = (index + 1).ToString(CultureInfo.InvariantCulture) + " / " + roster.Length.ToString(CultureInfo.InvariantCulture);
            previous.SetEnabled(roster.Length > 1);
            next.SetEnabled(roster.Length > 1);
            feedback.text = "";
            feedback.style.display = DisplayStyle.None;
        }

        void BuildSkill(VisualElement parent, StreamerCardSkill skill, int skillIndex)
        {
            string key = skillIndex.ToString(CultureInfo.InvariantCulture);
            var box = Box(parent, "team-card-skill-" + key, "t134-card__skill");
            box.EnableInClassList("t134-card__skill--fifth", skill.IsFifth);
            Text(box, "skill-kind-" + key, skill.IsFifth ? "必杀技能" : "常规技能 " + (skillIndex + 1), "t134-card__muted");
            Text(box, "skill-name-" + key, skill.Name, "t134-card__subtitle").style.unityFont = bold;
            var badges = Box(box, "skill-badges-" + key, "t134-card__tracks");
            var track = Text(badges, "skill-track-" + key, skill.TypeSymbol + " " + skill.TypeName, "t134-card__track");
            track.style.backgroundColor = TrackColor(skill.Type);
            Text(badges, "skill-category-" + key, skill.CategoryName, "t134-card__category");
            Text(box, "skill-numbers-" + key, "威力 " + Number(skill.Power) + " · 命中 " + Number(skill.Accuracy)
                + "% · PP " + Number(skill.Pp) + " · 先制 " + skill.Priority.ToString("+0;-0;0", CultureInfo.InvariantCulture), "t134-card__numbers");
            if (skill.Effects.Count == 0) Text(box, "skill-empty-" + key, "无附加效果", "t134-card__muted");
            for (int effectIndex = 0; effectIndex < skill.Effects.Count; effectIndex++)
            {
                var effect = skill.Effects[effectIndex];
                string effectKey = key + "-" + effectIndex.ToString(CultureInfo.InvariantCulture);
                var effectBox = Box(box, "skill-effect-row-" + effectKey, "t134-card__effect");
                Text(effectBox, "skill-timing-" + effectKey, effect.At, "t134-card__timing");
                Text(effectBox, "skill-effect-" + effectKey, effect.Text, "t134-card__body");
                var dispel = Text(effectBox, "skill-dispel-" + effectKey, effect.Dispel ?? "", "t134-card__muted");
                if (string.IsNullOrEmpty(effect.Dispel)) dispel.style.display = DisplayStyle.None;
            }
        }

        Color TrackColor(string typeId)
        {
            if (content.Types.TryGetValue(typeId, out var track)
                && ColorUtility.TryParseHtmlString(colorblind ? track.ColorAlt : track.Color, out Color color)) return color;
            return new Color(0.75f, 0.81f, 0.9f);
        }

        void Move(int direction)
        {
            if (!IsOpen || roster.Length < 2) return;
            int candidate = (index + direction + roster.Length) % roster.Length;
            try { Render(StreamerCard.Build(content, roster[candidate]), candidate); }
            catch (Exception)
            {
                feedback.text = "资料卡暂时无法切换；当前资料已保留，请关闭后重试。";
                feedback.style.display = DisplayStyle.Flex;
            }
        }

        void Page(int direction)
        {
            if (!IsOpen) return;
            float amount = Mathf.Max(80, scroll.contentViewport.layout.height * 0.8f);
            float maximum = Mathf.Max(0, scroll.contentContainer.layout.height - scroll.contentViewport.layout.height);
            scroll.scrollOffset = new Vector2(0, Mathf.Clamp(scroll.scrollOffset.y + direction * amount, 0, maximum));
        }

        void OnKey(KeyDownEvent evt)
        {
            if (!IsOpen) return;
            switch (evt.keyCode)
            {
                case KeyCode.Escape: Consume(evt); Close(); break;
                // 同一按键可能经多条 UI 输入链到达，导航操作统一在 Tick 读取公开按键状态。
                case KeyCode.LeftArrow:
                case KeyCode.RightArrow:
                case KeyCode.UpArrow:
                case KeyCode.DownArrow:
                case KeyCode.Tab:
                case KeyCode.PageUp:
                case KeyCode.PageDown: Consume(evt); break;
            }
        }

        void OnNavigate(NavigationMoveEvent evt)
        {
            if (!IsOpen) return;
            Consume(evt);
        }

        void OnCancel(NavigationCancelEvent evt)
        {
            if (!IsOpen) return;
            Consume(evt);
            Close();
        }

        void Consume(EventBase evt)
        {
            evt.StopPropagation();
            Root.panel?.focusController.IgnoreEvent(evt);
        }

        void FocusNext(int direction)
        {
            var choices = focusOrder.Where(element => element.enabledInHierarchy && element.resolvedStyle.display != DisplayStyle.None).ToList();
            if (choices.Count == 0) return;
            var focused = Root.panel?.focusController.focusedElement as VisualElement;
            int current = choices.IndexOf(focused);
            choices[(current + direction + choices.Count) % choices.Count].Focus();
        }

        void AttachFocusGuard()
        {
            if (focusScope != null || Root.panel == null) return;
            focusScope = Root.panel.visualTree;
            focusScope.RegisterCallback<FocusInEvent>(OnFocus, TrickleDown.TrickleDown);
        }

        void OnFocus(FocusInEvent evt)
        {
            if (!IsOpen) return;
            var target = evt.target as VisualElement;
            if (target != null && Root.Contains(target)) { lastFocus = target; return; }
            evt.StopPropagation();
            RestoreModalFocus();
        }

        bool IsModalFocus(VisualElement element)
        {
            return element != null && Root.Contains(element) && element.enabledInHierarchy
                && element.focusable && element.resolvedStyle.display != DisplayStyle.None;
        }

        void RestoreModalFocus()
        {
            var destination = IsModalFocus(lastFocus) ? lastFocus : close;
            destination.Focus();
        }

        void OnAttached(AttachToPanelEvent evt) { if (IsOpen && ReferenceEquals(evt.target, Root)) AttachFocusGuard(); }
        void OnDetached(DetachFromPanelEvent evt) { if (IsOpen && ReferenceEquals(evt.target, Root)) CloseCore(false, false); }

        public void Tick(float dt)
        {
            if (!IsOpen) return;
            // 对禁用背景调用 Focus 可能只清空焦点而不发 FocusIn，必须补查空焦点。
            if (Root.panel != null && !IsModalFocus(Root.panel.focusController.focusedElement as VisualElement)) RestoreModalFocus();
            var keyboard = Keyboard.current;
            var pad = Gamepad.current;
            int switchDirection = 0;
            if (keyboard != null)
            {
                if (keyboard.leftArrowKey.wasPressedThisFrame) switchDirection = -1;
                else if (keyboard.rightArrowKey.wasPressedThisFrame) switchDirection = 1;
            }
            if (switchDirection == 0 && pad != null)
            {
                if (pad.leftShoulder.wasPressedThisFrame) switchDirection = -1;
                else if (pad.rightShoulder.wasPressedThisFrame) switchDirection = 1;
            }
            if (switchDirection != 0) Move(switchDirection);

            int focusDirection = 0;
            if (keyboard != null)
            {
                if (keyboard.tabKey.wasPressedThisFrame) focusDirection = keyboard.shiftKey.isPressed ? -1 : 1;
                else if (keyboard.upArrowKey.wasPressedThisFrame) focusDirection = -1;
                else if (keyboard.downArrowKey.wasPressedThisFrame) focusDirection = 1;
                if (keyboard.pageUpKey.wasPressedThisFrame) Page(-1);
                else if (keyboard.pageDownKey.wasPressedThisFrame) Page(1);
            }
            if (focusDirection == 0 && pad != null)
            {
                if (pad.dpad.up.wasPressedThisFrame || pad.dpad.left.wasPressedThisFrame
                    || pad.leftStick.up.wasPressedThisFrame || pad.leftStick.left.wasPressedThisFrame) focusDirection = -1;
                else if (pad.dpad.down.wasPressedThisFrame || pad.dpad.right.wasPressedThisFrame
                    || pad.leftStick.down.wasPressedThisFrame || pad.leftStick.right.wasPressedThisFrame) focusDirection = 1;
            }
            if (focusDirection != 0) FocusNext(focusDirection);
        }

        public void Close() { CloseCore(true, true); }

        void CloseCore(bool notify, bool restoreFocus)
        {
            if (!IsOpen) return;
            IsOpen = false;
            initialFocus?.Pause(); initialFocus = null;
            focusScope?.UnregisterCallback<FocusInEvent>(OnFocus, TrickleDown.TrickleDown);
            focusScope = null;
            Root.style.display = DisplayStyle.None;
            Root.RemoveFromHierarchy();
            scroll.Clear();
            foreach (var state in background) state.Element.SetEnabled(state.Enabled);
            background.Clear();
            var returnTo = opener;
            opener = lastFocus = null;
            content = null;
            CurrentId = null;
            roster = Array.Empty<string>();
            if (notify) closed?.Invoke();
            if (restoreFocus && returnTo?.panel != null && returnTo.enabledInHierarchy) returnTo.Focus();
        }

        public void Dispose()
        {
            if (disposed) return;
            CloseCore(false, false);
            disposed = true;
            Root.UnregisterCallback<KeyDownEvent>(OnKey, TrickleDown.TrickleDown);
            Root.UnregisterCallback<NavigationMoveEvent>(OnNavigate, TrickleDown.TrickleDown);
            Root.UnregisterCallback<NavigationCancelEvent>(OnCancel, TrickleDown.TrickleDown);
            Root.UnregisterCallback<AttachToPanelEvent>(OnAttached);
            Root.UnregisterCallback<DetachFromPanelEvent>(OnDetached);
            Root.Clear();
            focusOrder.Clear();
        }

        VisualElement Section(VisualElement parent, string name, string title)
        {
            var section = Box(parent, name, "t134-card__section");
            Text(section, name + "-title", title, "t134-card__section-title").style.unityFont = bold;
            return section;
        }

        Button Button(VisualElement parent, string name, string label, Action action)
        {
            var button = new Button(action) { name = name, text = label, enableRichText = false, focusable = true, tabIndex = 0 };
            button.AddToClassList("t134-card__button");
            parent.Add(button);
            focusOrder.Add(button);
            return button;
        }

        static VisualElement Box(VisualElement parent, string name, string className)
        {
            var element = new VisualElement { name = name };
            element.AddToClassList(className);
            parent.Add(element);
            return element;
        }

        static Label Text(VisualElement parent, string name, string value, string className)
        {
            var label = new Label(value ?? "") { name = name, enableRichText = false };
            label.AddToClassList(className);
            parent.Add(label);
            return label;
        }

        static string Number(int value) { return value.ToString(CultureInfo.InvariantCulture); }
    }
}
