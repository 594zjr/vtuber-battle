using System;
using System.Collections.Generic;
using System.Linq;

namespace StreamDuel.Core
{
    public sealed class HotseatTimeout
    {
        public int Side { get; }
        public BattleAction Action { get; }
        public string Text { get; }

        internal HotseatTimeout(int side, BattleAction action, string text)
        {
            Side = side;
            Action = action;
            Text = text;
        }
    }

    public sealed partial class HotseatSession
    {
        const decimal TimerEpsilon = 0.000000000001m;
        static readonly IReadOnlyList<HotseatTimeout> NoTimeouts = Array.Empty<HotseatTimeout>();
        readonly Fighter[] previousSkillUsers = new Fighter[2];
        readonly int[] previousSkills = { -1, -1 };
        decimal choiceTimeLeft;

        public int TurnTimeLimitSeconds { get; }
        public double TurnTimeLeftSeconds
        {
            get { return battle.Phase == Phase.Over ? 0 : (double)choiceTimeLeft; }
        }

        public IReadOnlyList<HotseatTimeout> Tick(double dt)
        {
            if (double.IsNaN(dt) || double.IsInfinity(dt) || dt < 0)
                throw new ArgumentOutOfRangeException(nameof(dt), "经过时间必须是有限非负秒数。");
            if (dt == 0 || !CanTickChoice()) return NoTimeouts;

            int side = WhoseTurn;
            // 同一人最多结算本回合并提交下回合；更长预算一定遇到交接，不必转换巨大 double。
            decimal budget = (decimal)Math.Min(dt, 2.0 * TurnTimeLimitSeconds);
            var timeouts = new List<HotseatTimeout>();
            while (budget > 0 && timeouts.Count < 2 && CanTickChoice() && WhoseTurn == side)
            {
                // 十进制累积避免 0.1 的二进制误差，极小容差吸收分段转换的末位舍入。
                if (budget + TimerEpsilon < choiceTimeLeft)
                {
                    choiceTimeLeft -= budget;
                    break;
                }
                budget = Math.Max(0, budget - choiceTimeLeft);
                string description;
                var action = ChooseTimeoutAction(side, out description);
                Submit(action);
                timeouts.Add(new HotseatTimeout(side, action, "超时，已自动选择 " + description));
            }
            return timeouts.Count == 0 ? NoTimeouts : timeouts.AsReadOnly();
        }

        bool CanTickChoice()
        {
            return TurnTimeLimitSeconds > 0 && battle.Phase == Phase.Choose && !NeedsHandover
                && WhoseTurn >= 0 && WhoseTurn < 2 && !submitted[WhoseTurn];
        }

        BattleAction ChooseTimeoutAction(int side, out string description)
        {
            var legal = battle.LegalFor(side);
            var active = battle.Active(side);
            SkillOption selected = null;
            if (ReferenceEquals(previousSkillUsers[side], active))
                selected = legal.Skills.FirstOrDefault(skill => skill.Usable && skill.Index == previousSkills[side]);
            if (selected == null)
                selected = legal.Skills.Where(skill => skill.Usable).OrderBy(skill => skill.Index).FirstOrDefault();
            if (selected != null)
            {
                description = "「" + battle.Db.Skills[selected.Id].Name + "」";
                return BattleAction.Skill(selected.Index);
            }
            description = legal.Struggle ? "「" + battle.Db.Struggle.Name + "」" : "跳过";
            return BattleAction.Pass();
        }

        void RememberTurnSkills(Fighter[] users)
        {
            for (int side = 0; side < 2; side++)
            {
                // LastSkillIndex 会跨跳过和换人保留，不能把更早的技能误当成上一回合选择。
                bool skill = actions[side].Type == ActionType.Skill;
                previousSkillUsers[side] = skill ? users[side] : null;
                previousSkills[side] = skill ? actions[side].Index : -1;
            }
        }

        void ResetChoiceTimer()
        {
            choiceTimeLeft = TurnTimeLimitSeconds;
        }

        static void ValidateTurnTimeLimit(int seconds)
        {
            if (seconds != 0 && seconds != 10 && seconds != 20 && seconds != 30 && seconds != 60)
                throw new ArgumentOutOfRangeException(nameof(seconds), "同屏选招限时只允许 0、10、20、30 或 60 秒。");
        }
    }
}
