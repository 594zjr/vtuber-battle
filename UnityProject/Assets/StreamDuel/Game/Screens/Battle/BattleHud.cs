using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using StreamDuel.Core;
using UnityEngine;
using UnityEngine.UIElements;

namespace StreamDuel.Game
{
    public sealed partial class BattleHud : IDisposable
    {
        static readonly Color Background = new Color32(12, 17, 36, 255);
        static readonly Color Panel = new Color32(21, 28, 56, 255);
        static readonly Color Raised = new Color32(28, 37, 73, 255);
        static readonly Color TextColor = new Color32(232, 235, 250, 255);
        static readonly Color Muted = new Color32(152, 161, 200, 255);
        static readonly Color Accent = new Color32(255, 181, 71, 255);
        static readonly Color SwitchColor = new Color32(104, 185, 255, 255);

        readonly List<Action> unbind = new List<Action>();
        readonly List<Action> unbindLogs = new List<Action>();
        readonly List<Button> logRows = new List<Button>();
        readonly Button[] skills = new Button[5];
        readonly int[] skillIndices = new int[5];
        readonly Button[][] teams = { new Button[6], new Button[6] };
        readonly Label[] fighterNames = new Label[2];
        readonly Label[] fighterDetails = new Label[2];
        readonly Label[] stageLabels = new Label[2];
        readonly Image[] portraits = new Image[2];
        readonly ProgressBar[] hpBars = new ProgressBar[2];
        readonly ScrollView[] statusLists = new ScrollView[2];
        readonly List<Label>[] statusChips = { new List<Label>(), new List<Label>() };
        readonly List<Label> nodes = new List<Label>();
        readonly Label turnLabel, phaseLabel, timerLabel, noticeLabel, ownLabel, foeLabel, logLabel, outcomeLabel;
        readonly VisualElement nodeStrip, skillRow, outcomePanel;
        readonly ScrollView logScroll;
        readonly Button passButton, skipButton, menuButton, setupButton;
        readonly Action<int> selectLog;
        IReadOnlyList<LogEntry> shownLogs;
        IVisualElementScheduledItem scrollRequest;
        bool disposed;

        public VisualElement Root { get; }
        public BattleStage Stage { get; }

        public BattleHud(Action<int> skill, Action<int> ownSlot, Action<int> foeSlot, Action pass,
            Action skip, Action rematch, Action setup, Action<int> selectLog, Action menu)
        {
            this.selectLog = selectLog;
            Root = new VisualElement { name = "battle-hud" };
            Root.style.flexGrow = 1;
            Root.style.height = Length.Percent(100);
            Root.style.minHeight = 0;
            Root.style.minWidth = 0;
            Root.style.position = Position.Relative;
            Root.style.backgroundColor = Background;
            Root.style.color = TextColor;
            Padding(Root, 16);

            var header = Row();
            header.style.flexShrink = 0;
            header.style.minHeight = 48;
            Root.Add(header);
            turnLabel = Label("开播对决", "battle-turn", 28);
            turnLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            header.Add(turnLabel);
            timerLabel = Label("", "battle-timer", 20);
            timerLabel.style.flexGrow = 1;
            timerLabel.style.unityTextAlign = TextAnchor.MiddleRight;
            timerLabel.style.marginRight = 12;
            header.Add(timerLabel);
            setupButton = Button("重新组队", "battle-setup", setup);
            menuButton = Button("主菜单", "battle-menu", menu);
            header.Add(setupButton);
            header.Add(menuButton);

            noticeLabel = Label("", "battle-notice", 18);
            noticeLabel.style.color = Accent;
            noticeLabel.style.flexShrink = 0;
            Root.Add(noticeLabel);
            nodeStrip = Row();
            nodeStrip.name = "battle-nodes";
            nodeStrip.style.flexShrink = 0;
            nodeStrip.style.marginTop = 6;
            nodeStrip.style.marginBottom = 8;
            Root.Add(nodeStrip);

            var body = Row();
            body.style.flexGrow = 1;
            body.style.minHeight = 0;
            body.style.alignItems = Align.Stretch;
            Root.Add(body);
            var main = new VisualElement { name = "battle-main" };
            main.style.flexGrow = 7;
            main.style.flexBasis = 0;
            main.style.minWidth = 0;
            main.style.minHeight = 0;
            main.style.marginRight = 12;
            body.Add(main);

            foeLabel = Label("对方队伍", "battle-foe-label", 20);
            main.Add(foeLabel);
            main.Add(TeamRow(1, foeSlot));

            Stage = new BattleStage { name = "battle-stage" };
            Stage.style.minHeight = 270;
            Stage.style.flexGrow = 1;
            Stage.style.flexShrink = 1;
            Stage.style.marginTop = 8;
            Stage.style.marginBottom = 8;
            Stage.FieldBanner.style.marginTop = 4;
            Stage.FieldBanner.style.fontSize = 18;
            main.Add(Stage);
            PrepareFighter(0, Stage.Fighter0);
            PrepareFighter(1, Stage.Fighter1);

            ownLabel = Label("我方队伍", "battle-own-label", 20);
            main.Add(ownLabel);
            main.Add(TeamRow(0, ownSlot));
            phaseLabel = Label("", "battle-phase", 20);
            phaseLabel.style.color = Accent;
            phaseLabel.style.marginTop = 6;
            phaseLabel.style.marginBottom = 6;
            main.Add(phaseLabel);
            skillRow = Row();
            skillRow.name = "battle-skills";
            skillRow.style.alignItems = Align.Stretch;
            skillRow.style.flexShrink = 0;
            for (int i = 0; i < skills.Length; i++)
            {
                int position = i;
                skillIndices[i] = i;
                var button = Button("", "battle-skill-" + i, () => skill?.Invoke(skillIndices[position]));
                button.style.flexBasis = 0;
                button.style.flexGrow = 1;
                button.style.minHeight = 130;
                button.style.fontSize = 18;
                button.style.unityTextAlign = TextAnchor.UpperLeft;
                skills[i] = button;
                skillRow.Add(button);
            }
            main.Add(skillRow);
            var actions = Row();
            actions.style.flexShrink = 0;
            passButton = Button("跳过行动", "battle-pass", pass);
            skipButton = Button("跳过播放", "battle-skip", skip);
            actions.Add(passButton);
            actions.Add(skipButton);
            main.Add(actions);

            var history = new VisualElement { name = "battle-history" };
            history.style.flexGrow = 3;
            history.style.flexBasis = 0;
            history.style.minWidth = 0;
            history.style.minHeight = 0;
            history.style.backgroundColor = Panel;
            Padding(history, 10);
            body.Add(history);
            logLabel = Label("战斗记录", "battle-log-label", 22);
            history.Add(logLabel);
            var hint = Label("点击记录可高亮该行；播放时逐行显示最新记录。", "battle-log-hint", 16);
            hint.style.color = Muted;
            hint.style.marginBottom = 8;
            history.Add(hint);
            logScroll = new ScrollView(ScrollViewMode.Vertical) { name = "battle-log" };
            logScroll.style.flexGrow = 1;
            logScroll.style.minHeight = 0;
            logScroll.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            logScroll.contentContainer.style.minWidth = 0;
            history.Add(logScroll);

            outcomePanel = new VisualElement { name = "battle-outcome" };
            outcomePanel.style.position = Position.Absolute;
            outcomePanel.style.left = outcomePanel.style.top = outcomePanel.style.right = outcomePanel.style.bottom = 0;
            outcomePanel.style.backgroundColor = new Color(0, 0, 0, 0.78f);
            outcomePanel.style.alignItems = Align.Center;
            outcomePanel.style.justifyContent = Justify.Center;
            outcomePanel.style.display = DisplayStyle.None;
            var dialog = new VisualElement { name = "battle-outcome-dialog" };
            dialog.style.width = Length.Percent(68);
            dialog.style.maxWidth = 850;
            dialog.style.backgroundColor = Panel;
            Padding(dialog, 28);
            Border(dialog, Accent, 2);
            outcomeLabel = Label("对局结束", "battle-outcome-text", 32);
            outcomeLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
            outcomeLabel.style.marginBottom = 20;
            dialog.Add(outcomeLabel);
            var resultActions = Row();
            resultActions.style.flexWrap = Wrap.Wrap;
            resultActions.Add(Button("同样阵容再来", "battle-rematch", rematch));
            resultActions.Add(Button("重新组队", "battle-return", setup));
            resultActions.Add(Button("主菜单", "battle-outcome-menu", menu));
            dialog.Add(resultActions);
            outcomePanel.Add(dialog);
            BuildTooltip();
            Root.Add(outcomePanel);
        }

        public void Render(BattleViewModel view, IReadOnlyList<LogEntry> logs, int revealed, int selectedLog,
            bool busy, string timer, string outcome)
        {
            if (disposed) return;
            if (view == null) throw new ArgumentNullException(nameof(view));
            turnLabel.text = "开播对决 · 第 " + view.Turn + " 回合";
            timerLabel.text = timer ?? "";
            noticeLabel.text = view.Notice ?? "";
            noticeLabel.style.display = string.IsNullOrEmpty(view.Notice) ? DisplayStyle.None : DisplayStyle.Flex;
            phaseLabel.text = busy ? "正在播放结算，播放结束后可操作。" : PhaseText(view);
            Stage.FieldBanner.text = busy ? "结算播放中" : view.Phase == Phase.Over ? "对局结束" : "";
            menuButton.SetEnabled(!busy);
            setupButton.SetEnabled(!busy);
            skipButton.SetEnabled(busy);
            RenderNodes(view.NodeStrip);
            for (int side = 0; side < 2; side++)
            {
                var source = view.Sides != null && side < view.Sides.Length ? view.Sides[side] : null;
                RenderFighter(side, source);
                RenderTeam(side, source, view, busy);
            }
            ownLabel.text = view.Phase == Phase.Lead ? "选择我方首发" : view.Phase == Phase.Replace ? "选择我方补位" : "我方队伍 · 蓝边可换人";
            foeLabel.text = view.Phase == Phase.Ban ? "选择要禁用的对方主播" : "对方队伍";
            RenderSkills(view, busy);
            RenderLogs(logs, revealed, selectedLog, busy);
            outcomeLabel.text = string.IsNullOrEmpty(outcome) ? "对局结束" : outcome;
            outcomePanel.style.display = view.Phase == Phase.Over && !busy ? DisplayStyle.Flex : DisplayStyle.None;
            UpdateTooltipState(view, busy);
        }

        public void SetPortrait(int side, Texture2D texture)
        {
            if (side < 0 || side > 1) throw new ArgumentOutOfRangeException(nameof(side));
            if (!disposed) portraits[side].image = texture;
        }

        void PrepareFighter(int side, VisualElement fighter)
        {
            fighter.style.width = Length.Percent(48);
            fighter.style.paddingTop = fighter.style.paddingBottom = 10;
            fighter.style.paddingLeft = fighter.style.paddingRight = 14;
            fighterNames[side] = Label("", "battle-fighter-name-" + side, 25);
            fighterNames[side].style.unityFontStyleAndWeight = FontStyle.Bold;
            fighter.Insert(0, fighterNames[side]);
            fighterDetails[side] = Label("", "battle-fighter-details-" + side, 17);
            fighterDetails[side].style.color = Muted;
            BindTooltip(fighterDetails[side]);
            fighter.Insert(1, fighterDetails[side]);
            portraits[side] = fighter.Q<Image>(StageNames.Portrait);
            portraits[side].style.height = 110;
            portraits[side].style.marginTop = 5;
            portraits[side].scaleMode = ScaleMode.ScaleToFit;
            hpBars[side] = fighter.Q<ProgressBar>(StageNames.HpBar);
            StyleHpBar(hpBars[side], side);
            var statuses = fighter.Q<VisualElement>(StageNames.StatusRow);
            statuses.style.flexWrap = Wrap.NoWrap;
            statuses.style.height = 66;
            statuses.style.minHeight = 66;
            statusLists[side] = new ScrollView(ScrollViewMode.Vertical);
            statusLists[side].style.flexGrow = 1;
            statusLists[side].style.minWidth = 0;
            statusLists[side].style.minHeight = 0;
            statusLists[side].style.height = 66;
            statusLists[side].horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            statusLists[side].contentContainer.style.flexDirection = FlexDirection.Row;
            statusLists[side].contentContainer.style.flexWrap = Wrap.Wrap;
            statuses.Add(statusLists[side]);
            stageLabels[side] = Label("", "battle-stages-" + side, 16);
            stageLabels[side].style.color = Muted;
            fighter.Add(stageLabels[side]);
        }

        static void StyleHpBar(ProgressBar bar, int side)
        {
            bar.style.height = bar.style.minHeight = bar.style.maxHeight = 36;
            bar.style.flexShrink = 0;
            bar.style.marginTop = 6;
            Padding(bar, 0);
            Border(bar, Panel, 0);
            bar.style.backgroundColor = Panel;
            bar.style.backgroundImage = StyleKeyword.None;
            var background = bar.Q(className: "unity-progress-bar__background");
            if (background != null)
            {
                background.style.height = background.style.minHeight = 36;
                background.style.flexGrow = 1;
                background.style.marginLeft = background.style.marginRight = 0;
                background.style.marginTop = background.style.marginBottom = 0;
                background.style.backgroundColor = Background;
                background.style.backgroundImage = StyleKeyword.None;
                Padding(background, 0);
                Border(background, Background, 0);
            }
            var progress = bar.Q(className: "unity-progress-bar__progress");
            if (progress != null)
            {
                progress.style.height = Length.Percent(100);
                progress.style.marginLeft = progress.style.marginRight = 0;
                progress.style.marginTop = progress.style.marginBottom = 0;
                progress.style.backgroundColor = (Color)(side == 0 ? new Color32(36, 84, 126, 255) : new Color32(124, 68, 37, 255));
                progress.style.backgroundImage = StyleKeyword.None;
                Padding(progress, 0);
                Border(progress, Background, 0);
            }
            // 内置进度条的标题内边距不适合继承的大字号，明确留足一行文字的高度。
            bar.Query<TextElement>().ForEach(element =>
            {
                element.enableRichText = false;
                element.style.position = Position.Absolute;
                element.style.left = element.style.right = element.style.top = 0;
                element.style.height = element.style.minHeight = 36;
                element.style.marginLeft = element.style.marginRight = 0;
                element.style.marginTop = element.style.marginBottom = 0;
                element.style.fontSize = 18;
                element.style.color = TextColor;
                element.style.whiteSpace = WhiteSpace.NoWrap;
                element.style.unityTextAlign = TextAnchor.MiddleCenter;
                Padding(element, 0);
                Border(element, Background, 0);
            });
        }

        VisualElement TeamRow(int side, Action<int> choose)
        {
            var row = Row();
            row.name = side == 0 ? "battle-own-team" : "battle-foe-team";
            row.style.flexShrink = 0;
            row.style.alignItems = Align.Stretch;
            for (int index = 0; index < 6; index++)
            {
                int slot = index;
                var button = Button("", (side == 0 ? "battle-own-" : "battle-foe-") + index, () => choose?.Invoke(slot));
                button.style.flexGrow = 1;
                button.style.flexBasis = 0;
                button.style.minHeight = 68;
                button.style.fontSize = 17;
                teams[side][index] = button;
                row.Add(button);
            }
            return row;
        }

        void RenderFighter(int side, SideView source)
        {
            var fighter = source != null && source.Active >= 0 && source.Active < source.Team.Count ? source.Team[source.Active] : null;
            fighterNames[side].text = fighter == null ? "等待上场" : source.Name + " · " + fighter.Name;
            fighterDetails[side].text = fighter == null ? "" : fighter.Tracks + " · 人设「" + fighter.PersonaName + "」";
            fighterDetails[side].tooltip = fighter == null ? "" : fighter.PersonaDescription;
            hpBars[side].value = fighter == null ? 0 : fighter.Pct;
            hpBars[side].title = fighter == null ? "" : "人气 " + fighter.Hp + " / " + fighter.MaxHp + "（" + fighter.Pct + "%）";
            int chipCount = fighter == null ? 0 : Math.Max(1, fighter.Statuses.Count);
            while (statusChips[side].Count > chipCount)
            {
                int index = statusChips[side].Count - 1;
                UnbindTooltip(statusChips[side][index]);
                statusChips[side][index].RemoveFromHierarchy();
                statusChips[side].RemoveAt(index);
            }
            while (statusChips[side].Count < chipCount)
            {
                var label = Label("", "", 16);
                label.AddToClassList("status-icon");
                label.style.paddingTop = label.style.paddingBottom = 3;
                label.style.marginTop = label.style.marginBottom = 2;
                BindTooltip(label);
                statusChips[side].Add(label);
                statusLists[side].Add(label);
            }
            if (fighter == null) { stageLabels[side].text = ""; return; }
            for (int index = 0; index < chipCount; index++)
            {
                var chip = index < fighter.Statuses.Count ? fighter.Statuses[index] : null;
                var label = statusChips[side][index];
                label.name = chip == null ? "battle-status-empty-" + side : "battle-status-" + side + "-" + chip.Id;
                label.text = chip == null ? "无状态或直播间效果" : chip.Name + " · " + chip.Duration;
                label.tooltip = chip == null ? "" : chip.Tooltip;
                label.style.color = chip == null ? Muted : chip.IsControl ? Accent : TextColor;
            }
            stageLabels[side].text = string.Join("  ", fighter.Stages.Where(pair => pair.Value != 0)
                .Select(pair => StatName(pair.Key) + " " + pair.Value.ToString("+0;-0;0", CultureInfo.InvariantCulture)));
        }

        void RenderTeam(int side, SideView source, BattleViewModel view, bool busy)
        {
            for (int index = 0; index < 6; index++)
            {
                var button = teams[side][index];
                bool exists = source != null && index < source.Team.Count;
                button.style.display = exists ? DisplayStyle.Flex : DisplayStyle.None;
                if (!exists) continue;
                var fighter = source.Team[index];
                bool selectable = side == 0
                    ? view.Phase == Phase.Lead
                        || view.Phase == Phase.Replace && !fighter.Fainted && index != source.Active
                        || view.Phase == Phase.Choose && view.SwitchTargets.Contains(index)
                    : view.Phase == Phase.Ban;
                string state = fighter.Fainted ? "已下播" : index == source.Active ? "在场" : "后排";
                if (selectable && side == 0 && view.Phase == Phase.Choose) state = "可换人";
                if (selectable && side == 0 && view.Phase == Phase.Replace) state = "可补位";
                button.text = (index + 1) + " · " + fighter.Name + "\n" + fighter.Hp + "/" + fighter.MaxHp + " · " + state;
                button.tooltip = fighter.Name + " · " + fighter.Tracks + "\n人气 " + fighter.Hp + "/" + fighter.MaxHp
                    + "（" + fighter.Pct + "%）\n人设「" + fighter.PersonaName + "」：" + fighter.PersonaDescription;
                button.SetEnabled(selectable && !busy);
                Border(button, selectable ? SwitchColor : index == source.Active ? Accent : Raised, 1);
            }
        }

        void RenderSkills(BattleViewModel view, bool busy)
        {
            skillRow.style.display = view.Phase == Phase.Choose ? DisplayStyle.Flex : DisplayStyle.None;
            for (int position = 0; position < skills.Length; position++)
            {
                var button = skills[position];
                var skill = position < view.Skills.Count ? view.Skills[position] : null;
                button.style.display = skill == null ? DisplayStyle.None : DisplayStyle.Flex;
                if (skill == null) continue;
                skillIndices[position] = skill.Index;
                string reason = skill.Usable ? "" : !string.IsNullOrEmpty(view.ControlledReason)
                    ? view.ControlledReason : skill.Pp <= 0 ? "PP 已耗尽" : "当前不可用";
                string damage = skill.DamageMin + "–" + skill.DamageMax;
                button.text = (skill.Fifth ? "第五技能 · " : "") + skill.Name + "\n"
                    + skill.Track + " · " + skill.Category + "\nPP " + skill.Pp + "/" + skill.MaxPp
                    + (view.FreePp ? "（免消耗）" : "") + " · 先制 " + skill.Priority
                    + "\n伤害 " + damage + " · 克制 ×" + skill.Effectiveness.ToString("0.##", CultureInfo.InvariantCulture)
                    + (reason.Length == 0 ? "" : "\n" + reason);
                var description = new StringBuilder(button.text);
                foreach (string part in skill.PriorityParts) description.Append('\n').Append(part);
                foreach (var effect in skill.Effects)
                    description.Append('\n').Append(effect.Timing).Append("：").Append(effect.Text).Append("；").Append(effect.Dispel);
                button.tooltip = description.ToString();
                button.style.color = skill.Fifth ? Accent : TextColor;
                Border(button, skill.Fifth ? Accent : Raised, skill.Fifth ? 2 : 1);
                button.SetEnabled(!busy && view.Phase == Phase.Choose && skill.Usable);
            }
            bool hasPass = view.Struggle || !string.IsNullOrEmpty(view.ControlledReason);
            passButton.text = view.Struggle ? view.StruggleName + "（技能均无 PP）" : "受控 · 跳过本次行动";
            passButton.tooltip = view.Struggle ? "没有可用 PP 时，可以硬撑或换人。" : view.ControlledReason;
            passButton.style.display = view.Phase == Phase.Choose && hasPass ? DisplayStyle.Flex : DisplayStyle.None;
            passButton.SetEnabled(!busy && view.Phase == Phase.Choose && hasPass);
        }

        void RenderNodes(IReadOnlyList<NodeView> source)
        {
            if (source == null) return;
            if (nodes.Count != source.Count)
            {
                foreach (var node in nodes) UnbindTooltip(node);
                nodeStrip.Clear();
                nodes.Clear();
                foreach (var node in source)
                {
                    var label = Label("", "", 17);
                    label.style.flexGrow = 1;
                    label.style.flexBasis = 0;
                    label.style.unityTextAlign = TextAnchor.MiddleCenter;
                    label.style.marginRight = 3;
                    Padding(label, 5);
                    BindTooltip(label);
                    nodes.Add(label);
                    nodeStrip.Add(label);
                }
            }
            for (int i = 0; i < source.Count; i++)
            {
                nodes[i].text = source[i].Name;
                nodes[i].style.color = source[i].HasContent ? Accent : Muted;
                nodes[i].style.backgroundColor = source[i].HasContent ? Raised : Panel;
                nodes[i].tooltip = source[i].HasContent ? "本回合已有此节点记录" : "本回合暂无此节点记录";
            }
        }

        void RenderLogs(IReadOnlyList<LogEntry> logs, int revealed, int selected, bool busy)
        {
            int count = logs == null ? 0 : Math.Max(0, Math.Min(revealed, logs.Count));
            if (!ReferenceEquals(shownLogs, logs) || count < logRows.Count)
            {
                foreach (var release in unbindLogs) release();
                unbindLogs.Clear();
                logRows.Clear();
                logScroll.Clear();
                shownLogs = logs;
            }
            bool appended = count > logRows.Count;
            while (logRows.Count < count)
            {
                int index = logRows.Count;
                var line = logs[index];
                var button = Button(line == null ? "" : "第 " + line.Turn + " 回合 · " + line.Node + "\n" + line.Text,
                    "battle-log-" + index, () => selectLog?.Invoke(index), unbindLogs);
                button.style.fontSize = 18;
                button.style.unityTextAlign = TextAnchor.UpperLeft;
                button.style.flexShrink = 0;
                button.tooltip = line == null ? "" : line.Text;
                logRows.Add(button);
                logScroll.Add(button);
            }
            int highlighted = selected >= 0 && selected < count ? selected : count - 1;
            for (int i = 0; i < logRows.Count; i++)
            {
                bool current = i == highlighted;
                logRows[i].EnableInClassList("battle-log-selected", current);
                logRows[i].style.backgroundColor = current ? Raised : Panel;
                logRows[i].style.color = current ? Accent : TextColor;
                Border(logRows[i], current ? Accent : Panel, 1);
                logRows[i].SetEnabled(!busy);
            }
            logLabel.text = "战斗记录 · " + count + " 条";
            if (appended && selected < 0 && count > 0)
            {
                scrollRequest?.Pause();
                var last = logRows[count - 1];
                scrollRequest = logScroll.schedule.Execute(() => { if (!disposed) logScroll.ScrollTo(last); });
            }
        }

        Button Button(string text, string name, Action action, List<Action> bindings = null)
        {
            var button = new Button { text = text, name = name, enableRichText = false };
            button.AddToClassList("screen-button");
            button.style.minWidth = 0;
            button.style.minHeight = 38;
            button.style.marginTop = button.style.marginBottom = 3;
            button.style.marginLeft = button.style.marginRight = 3;
            button.style.fontSize = 20;
            button.style.whiteSpace = WhiteSpace.Normal;
            Padding(button, 8);
            Action handler = () =>
            {
                if (disposed) return;
                HideTooltip();
                action?.Invoke();
            };
            button.clicked += handler;
            (bindings ?? unbind).Add(() => button.clicked -= handler);
            BindTooltip(button);
            if (bindings != null) bindings.Add(() => UnbindTooltip(button));
            return button;
        }

        static Label Label(string text, string name, int size)
        {
            var label = new Label(text) { name = name, enableRichText = false };
            label.style.fontSize = size;
            label.style.whiteSpace = WhiteSpace.Normal;
            label.style.minWidth = 0;
            return label;
        }

        static VisualElement Row()
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.minWidth = 0;
            return row;
        }

        static void Padding(VisualElement element, float amount)
        {
            element.style.paddingLeft = element.style.paddingRight = amount;
            element.style.paddingTop = element.style.paddingBottom = amount;
        }

        static void Border(VisualElement element, Color color, float width)
        {
            element.style.borderLeftColor = element.style.borderRightColor = color;
            element.style.borderTopColor = element.style.borderBottomColor = color;
            element.style.borderLeftWidth = element.style.borderRightWidth = width;
            element.style.borderTopWidth = element.style.borderBottomWidth = width;
        }

        static string PhaseText(BattleViewModel view)
        {
            switch (view.Phase)
            {
                case Phase.Ban: return "开局禁用：点击对方队伍中的一位主播。";
                case Phase.Lead: return "选择首发：点击我方队伍中的一位主播。";
                case Phase.Replace: return "选择补位：点击仍在播的我方主播。";
                case Phase.Over: return "对局结束。";
                default: return !string.IsNullOrEmpty(view.ControlledReason) ? view.ControlledReason
                    : view.Struggle ? "普通技能与第五技能均无可用 PP，请使用硬撑或换人。"
                    : "选择技能，或点击蓝边队友换人；悬停可查看完整说明。";
            }
        }

        static string StatName(string id)
        {
            switch (id)
            {
                case "atk": return "互动";
                case "def": return "抗压";
                case "spa": return "才艺";
                case "sdef": return "定力";
                case "spd": return "反应";
                default: return id;
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            DisposeTooltip();
            scrollRequest?.Pause();
            foreach (var release in unbindLogs) release();
            foreach (var release in unbind) release();
            unbindLogs.Clear();
            unbind.Clear();
            logRows.Clear();
            shownLogs = null;
            for (int side = 0; side < portraits.Length; side++) portraits[side].image = null;
            Root.RemoveFromHierarchy();
        }
    }
}
