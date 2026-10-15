using System;
using System.Collections.Generic;
using System.Linq;
using StreamDuel.Core;
using UnityEngine;
using UnityEngine.UIElements;

namespace StreamDuel.Game
{
    internal sealed class OnlineBattleView : IDisposable
    {
        readonly List<Action> unbind = new List<Action>();
        readonly VisualElement root;
        readonly Action<BattleAction> submit;
        readonly Action<int> replace;

        public OnlineBattleView(VisualElement root, Action<BattleAction> submit, Action<int> replace)
        {
            this.root = root;
            this.submit = submit;
            this.replace = replace;
        }

        public void Refresh(NetSession session, bool finished)
        {
            Dispose();
            var battle = session.Battle;
            if (battle == null) return;
            var view = BattleViewModel.From(battle, session.MySide);
            root.Add(Text("第 " + view.Turn + " 回合 · " + (session.MySide == 0 ? "你是主机" : "你是客机")));
            if (!string.IsNullOrEmpty(view.Notice)) root.Add(Text(view.Notice, true));
            var sides = new VisualElement { name = "online-fighters" };
            sides.style.flexDirection = FlexDirection.Row;
            root.Add(sides);
            for (int order = 0; order < 2; order++)
            {
                int side = order == 0 ? session.MySide : 1 - session.MySide;
                var current = view.Sides[side];
                var fighter = current.Team[current.Active];
                var card = new VisualElement { name = "online-side-" + side };
                card.style.flexGrow = 1;
                card.style.flexBasis = 0;
                card.style.paddingLeft = card.style.paddingRight = 12;
                card.style.paddingTop = card.style.paddingBottom = 8;
                card.style.backgroundColor = (Color)new Color32(28, 37, 73, 255);
                sides.Add(card);
                card.Add(Text((order == 0 ? "我方 · " : "对方 · ") + current.Name));
                card.Add(Text(fighter.Name + " · " + fighter.Tracks));
                var hpText = Text("人气 " + fighter.Hp + " / " + fighter.MaxHp);
                hpText.name = "online-hp-value-" + side;
                card.Add(hpText);
                var hpBar = new ProgressBar { name = "online-hp-bar-" + side,
                    lowValue = 0, highValue = fighter.MaxHp, value = fighter.Hp };
                hpBar.style.height = hpBar.style.minHeight = 24;
                hpBar.style.flexShrink = 0;
                hpBar.style.marginTop = 4;
                hpBar.style.marginBottom = 8;
                var background = hpBar.Q(className: "unity-progress-bar__background");
                if (background != null)
                {
                    background.style.height = Length.Percent(100);
                    background.style.backgroundColor = (Color)new Color32(12, 17, 36, 255);
                }
                var progress = hpBar.Q(className: "unity-progress-bar__progress");
                if (progress != null)
                {
                    progress.style.height = Length.Percent(100);
                    progress.style.backgroundColor = (Color)new Color32(87, 210, 171, 255);
                }
                card.Add(hpBar);
                var persona = Text("人设：" + fighter.PersonaName, true);
                persona.tooltip = fighter.PersonaDescription;
                card.Add(persona);
                foreach (var chip in fighter.Statuses)
                {
                    var label = Text((chip.IsStatus ? "状态：" : "直播间效果：") + chip.Name + " · " + chip.Duration, true);
                    label.tooltip = chip.Tooltip;
                    card.Add(label);
                }
                card.Add(Text("能力等级：" + string.Join("　", fighter.Stages.Select(stage =>
                    battle.Db.StatName(stage.Key) + " " + stage.Value.ToString("+0;-0;0"))), true));
                card.Add(Text(string.Join("　", current.Team.Select((member, index) =>
                    (index + 1) + "." + member.Name + (member.Fainted ? "（下播）" : " " + member.Pct + "%"))), true));
            }

            var choices = new VisualElement { name = "online-actions" };
            choices.style.flexDirection = FlexDirection.Row;
            choices.style.flexWrap = Wrap.Wrap;
            root.Add(choices);
            bool choose = session.State == NetState.Choose && !session.HasSubmitted;
            bool replacement = session.State == NetState.Replace && !session.HasSubmitted;
            if (session.State == NetState.Replace)
            {
                root.Add(Text("请选择下一位开播的队员。"));
                foreach (int index in view.SwitchTargets)
                {
                    int target = index;
                    AddButton(choices, "online-replace-" + index,
                        "补位 " + (index + 1) + " · " + view.Sides[session.MySide].Team[index].Name,
                        replacement, () => replace(target));
                }
            }
            else if (!finished && session.State != NetState.Over && session.State != NetState.Error && session.State != NetState.Desync)
            {
                foreach (var skill in view.Skills)
                {
                    int index = skill.Index;
                    var button = AddButton(choices, "online-skill-" + index,
                        skill.Name + "\n" + skill.Track + " · " + skill.Category + " · PP " + skill.Pp + "/" + skill.MaxPp
                        + " · 先制 " + skill.Priority,
                        choose && skill.Usable, () => submit(BattleAction.Skill(index)));
                    button.tooltip = "威力 " + skill.Power + "；预计伤害 " + skill.DamageMin + "～" + skill.DamageMax
                        + "\n" + string.Join("\n", skill.Effects.Select(effect => effect.Timing + "：" + effect.Text + "；" + effect.Dispel));
                }
                foreach (int index in view.SwitchTargets)
                {
                    int target = index;
                    AddButton(choices, "online-switch-" + index,
                        "换人 " + (index + 1) + " · " + view.Sides[session.MySide].Team[index].Name,
                        choose, () => submit(BattleAction.SwitchTo(target)));
                }
                if (view.ControlledReason != null || view.Struggle)
                {
                    string pass = view.ControlledReason != null ? "受「" + view.ControlledReason + "」控制 · 跳过" : view.StruggleName;
                    AddButton(choices, "online-pass", pass, choose, () => submit(BattleAction.Pass()));
                }
            }
            root.Add(Text("对战日志（最近 12 条）", true));
            foreach (var entry in battle.Log.Skip(Math.Max(0, battle.Log.Count - 12)))
                root.Add(Text("第 " + entry.Turn + " 回合 · " + entry.Node + " · " + entry.Text, true));
        }

        Button AddButton(VisualElement parent, string name, string label, bool enabled, Action clicked)
        {
            var button = new Button { name = name, text = label };
            button.AddToClassList("screen-button");
            button.style.minWidth = 240;
            button.style.minHeight = 52;
            button.style.fontSize = 21;
            button.SetEnabled(enabled);
            button.clicked += clicked;
            unbind.Add(() => button.clicked -= clicked);
            parent.Add(button);
            return button;
        }

        internal static Label Text(string text, bool small = false)
        {
            var label = new Label(text) { enableRichText = false };
            label.style.whiteSpace = WhiteSpace.Normal;
            label.style.flexShrink = 0;
            if (small) label.AddToClassList("screen-note");
            return label;
        }

        public void Dispose()
        {
            foreach (var remove in unbind) remove();
            unbind.Clear();
            root.Clear();
        }
    }
}
