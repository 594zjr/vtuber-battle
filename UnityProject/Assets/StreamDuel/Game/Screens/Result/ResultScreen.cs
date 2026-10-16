using System;
using System.Collections.Generic;
using System.Globalization;
using StreamDuel.Core;
using UnityEngine;
using UnityEngine.UIElements;

namespace StreamDuel.Game
{
    public sealed class ResultScreen : IScreen
    {
        readonly List<Action> unbind = new List<Action>();
        ScreenRouter router;
        ResultScreenArgs args;
        ISaveStore store;
        ITextClipboard clipboard;
        Label feedback;
        Button saveButton;
        bool disposed;

        public string Id => ScreenIds.Result;

        public VisualElement Build(ScreenRouter screenRouter, object screenArgs)
        {
            Dispose();
            disposed = false;
            router = screenRouter;
            args = screenArgs as ResultScreenArgs ?? new ResultScreenArgs();
            store = args.SaveStore;
            clipboard = args.Clipboard ?? new SystemTextClipboard();

            var page = new VisualElement { name = "result-screen" };
            page.AddToClassList("screen");
            page.style.minHeight = 0;
            var panel = new VisualElement { name = "result-panel" };
            panel.AddToClassList("screen-panel");
            panel.style.width = Length.Percent(100);
            panel.style.height = Length.Percent(100);
            panel.style.minHeight = 0;
            page.Add(panel);

            var title = Text("本局结算", "result-title", "screen-title");
            title.style.unityFont = router.Resources.BoldFont;
            title.style.flexShrink = 0;
            panel.Add(title);

            // 长名字、完整队伍统计及窄窗口都留在同一滚动区域，操作按钮也不会被挤出面板。
            var scroll = new ScrollView(ScrollViewMode.Vertical) { name = "result-scroll" };
            scroll.style.flexGrow = 1;
            scroll.style.flexShrink = 1;
            scroll.style.minHeight = 0;
            scroll.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            scroll.contentContainer.style.minWidth = 0;
            panel.Add(scroll);

            var battle = args.Battle;
            if (battle == null)
            {
                scroll.Add(Text("还没有结算记录", "result-empty"));
                scroll.Add(Text("完成一场对战后，可以在这里查看统计、保存录像和复制战报。", "result-empty-note", "screen-note"));
                var emptyActions = Actions(scroll);
                AddButton(emptyActions, "result-return-team", "返回组队", ReturnToTeam);
                feedback = Feedback(scroll);
                return page;
            }

            bool completed = battle.Phase == Phase.Over;
            var summary = BattleSummary.From(battle);
            string outcome = "对战尚未结束";
            if (completed)
            {
                if (battle.Winner == -1) outcome = "平局";
                else if (args.PlayerSide == 0 || args.PlayerSide == 1)
                    outcome = battle.Winner == args.PlayerSide ? "胜利" : "本局落败";
                else outcome = "对战结束";
            }
            var result = Text(outcome, "result-outcome", "screen-title");
            result.style.fontSize = 36;
            result.style.marginBottom = 8;
            result.style.unityFont = router.Resources.BoldFont;
            scroll.Add(result);
            string winner = completed && battle.Winner >= 0 && battle.Winner < battle.Sides.Length
                ? "获胜方：" + PlayerName(battle, battle.Winner) + " · " : "";
            scroll.Add(Text(winner + (completed ? "完成回合数：" : "当前回合：")
                + Number(completed ? summary.Turns : battle.Turn), "result-rounds"));
            scroll.Add(Text("内容包：" + battle.Db.PackName, "result-pack", "screen-note"));
            if (!string.IsNullOrWhiteSpace(battle.Db.PackNotice))
                scroll.Add(Text(battle.Db.PackNotice, "result-pack-notice", "screen-note"));
            var mvp = summary.Mvp();
            string mvpText = !completed ? "MVP：对战结束后评选" : !mvp.HasValue ? "MVP：本局暂无获选主播"
                : "MVP：" + PlayerName(battle, mvp.Value.Side) + " · " + Number(mvp.Value.Slot + 1)
                    + " 号位 · " + battle.Sides[mvp.Value.Side].Team[mvp.Value.Slot].Name;
            scroll.Add(Text(mvpText, "result-mvp", "screen-note"));
            if (!completed)
                scroll.Add(Text("以下为当前统计；完成对战后可保存本局录像、复制战报和再来一局。", "result-unfinished", "screen-note"));

            var actions = Actions(scroll);
            var rematch = AddButton(actions, "result-rematch", args.RematchLabel ?? "再来一局", () => args.Rematch?.Invoke());
            rematch.SetEnabled(completed && args.Rematch != null);
            rematch.tooltip = completed ? "使用当前阵容继续下一局或重新开始比赛。不可用时可返回组队。" : "对战结束后可使用。";
            saveButton = AddButton(actions, "result-save", "保存录像", SaveReplay);
            saveButton.SetEnabled(completed);
            var copy = AddButton(actions, "result-copy-report", "复制可读战报", CopyReport);
            copy.SetEnabled(completed);
            var replay = AddButton(actions, "result-watch", "回看本局", OpenReplay);
            replay.SetEnabled(completed);
            AddButton(actions, "result-return-team", "返回组队", ReturnToTeam);
            feedback = Feedback(scroll);

            BuildSummary(scroll, battle, summary);
            return page;
        }

        void BuildSummary(VisualElement parent, Battle battle, BattleSummary summary)
        {
            var heading = Text("每位主播的本局统计", "result-summary-title");
            heading.style.unityFont = router.Resources.BoldFont;
            heading.style.marginTop = 24;
            parent.Add(heading);
            for (int side = 0; side < summary.Sides.Length; side++)
            {
                var section = new VisualElement { name = "result-team-" + side };
                section.style.marginTop = 20;
                section.style.flexShrink = 0;
                parent.Add(section);
                string sideLabel = args.PlayerSide == 0 || args.PlayerSide == 1
                    ? (side == args.PlayerSide ? "我方" : "对方") : (side == 0 ? "甲方" : "乙方");
                var teamTitle = Text(sideLabel + " · " + PlayerName(battle, side), "result-team-title-" + side);
                teamTitle.style.unityFont = router.Resources.BoldFont;
                section.Add(teamTitle);

                for (int index = 0; index < summary.Sides[side].Count; index++)
                {
                    var stats = summary.Sides[side][index];
                    string suffix = side + "-" + index;
                    var row = new VisualElement { name = "result-fighter-" + suffix };
                    row.style.marginTop = 12;
                    row.style.paddingTop = 12;
                    row.style.paddingBottom = 12;
                    row.style.borderTopWidth = 1;
                    row.style.borderTopColor = (Color)new Color32(43, 54, 102, 255);
                    row.style.flexShrink = 0;
                    section.Add(row);
                    row.Add(Text(Number(index + 1) + " 号位 · " + battle.Sides[side].Team[index].Name,
                        "result-name-" + suffix));
                    var values = new VisualElement { name = "result-values-" + suffix };
                    values.style.flexDirection = FlexDirection.Row;
                    values.style.flexWrap = Wrap.Wrap;
                    row.Add(values);
                    AddStat(values, "造成伤害", stats.DamageDealt, "result-damage-" + suffix);
                    AddStat(values, "承受伤害", stats.DamageTaken, "result-taken-" + suffix);
                    AddStat(values, "击倒", stats.Kos, "result-kos-" + suffix);
                    AddStat(values, "在场回合", stats.TurnsActive, "result-turns-" + suffix);
                    row.Add(Text(stats.Fainted ? "已下播" : "仍在播", "result-state-" + suffix, "screen-note"));
                }
            }
        }

        static void AddStat(VisualElement parent, string text, int value, string name)
        {
            var label = Text(text + "：" + Number(value), name, "screen-note");
            label.style.width = StyleKeyword.Auto;
            label.style.minWidth = 180;
            label.style.marginRight = 16;
            label.style.marginTop = 6;
            parent.Add(label);
        }

        static Label Text(string value, string name, string className = null)
        {
            var label = new Label(value ?? "") { name = name, enableRichText = false };
            label.style.whiteSpace = WhiteSpace.Normal;
            label.style.width = Length.Percent(100);
            label.style.minWidth = 0;
            label.style.flexShrink = 0;
            label.style.marginBottom = 6;
            if (className != null) label.AddToClassList(className);
            return label;
        }

        static VisualElement Actions(VisualElement parent)
        {
            var actions = new VisualElement { name = "result-actions" };
            actions.AddToClassList("screen-actions");
            actions.style.flexShrink = 0;
            parent.Add(actions);
            return actions;
        }

        static Label Feedback(VisualElement parent)
        {
            var label = Text("", "result-feedback", "screen-note");
            label.style.display = DisplayStyle.None;
            parent.Add(label);
            return label;
        }

        Button AddButton(VisualElement parent, string name, string text, Action action)
        {
            var button = new Button { name = name, text = text, enableRichText = false };
            button.AddToClassList("screen-button");
            button.style.minWidth = 220;
            button.style.maxWidth = Length.Percent(100);
            Action handler = () =>
            {
                if (disposed || !button.enabledInHierarchy) return;
                try { action(); }
                catch (Exception exception) { ShowFeedback("操作未完成：" + exception.Message); }
            };
            button.clicked += handler;
            unbind.Add(() => button.clicked -= handler);
            parent.Add(button);
            return button;
        }

        ISaveStore Store()
        {
            if (store == null) store = ReplayStorage.OpenDefault();
            return store;
        }

        void SaveReplay()
        {
            try
            {
                string name = ReplayStorage.Save(Store(), args.Battle);
                saveButton.text = "录像已保存";
                saveButton.SetEnabled(false);
                ShowFeedback("录像已保存：" + name + "。可在回看页面的录像列表打开。");
            }
            catch (ReplayRetentionException exception)
            {
                saveButton.text = "录像已保存";
                saveButton.SetEnabled(false);
                ShowFeedback(exception.Message);
            }
        }

        void CopyReport()
        {
            clipboard.Text = BattleReport.Markdown(args.Battle, args.PlayerSide == 1 ? 1 : 0);
            ShowFeedback("可读战报已复制，可粘贴到文本编辑器查看。");
        }

        void OpenReplay()
        {
            var resultArgs = args;
            var route = router;
            var replayStore = store ?? resultArgs.SaveStore;
            resultArgs.SaveStore = replayStore;
            // 路由切页会释放当前屏幕，返回操作只保留输入和路由，不捕获已释放的屏幕字段。
            route.Show(ScreenIds.Replay, new ReplayScreenArgs
            {
                Content = resultArgs.Battle.Db,
                Record = resultArgs.Battle.Record,
                SaveStore = replayStore,
                Clipboard = clipboard,
                ResolvePack = resultArgs.ResolvePack,
                Return = () => route.Show(ScreenIds.Result, resultArgs)
            });
        }

        void ReturnToTeam()
        {
            if (args.ReturnToTeam != null) args.ReturnToTeam();
            else router.Show(ScreenIds.Team);
        }

        void ShowFeedback(string message)
        {
            if (disposed || feedback == null) return;
            feedback.text = message;
            feedback.style.display = DisplayStyle.Flex;
        }

        static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
        static string PlayerName(Battle battle, int side) => TextInput.Clean(battle.Sides[side].Name, TextInputKind.PlayerName).Text;

        public void Tick(float dt) { }

        public void Dispose()
        {
            disposed = true;
            foreach (var remove in unbind) remove();
            unbind.Clear();
            router = null;
            args = null;
            store = null;
            clipboard = null;
            feedback = null;
            saveButton = null;
        }
    }
}
