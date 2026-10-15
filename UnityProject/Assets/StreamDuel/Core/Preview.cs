using System;
using System.Collections.Generic;

namespace StreamDuel.Core
{
    public sealed partial class Battle
    {
        public sealed class DamagePreview
        {
            public int Min, Max;
            public double Eff;
            public List<string> Notes = new List<string>();
        }

        public sealed class OrderPreview
        {
            public int First;
            public string Reason;
        }

        /// <summary>按当前状态、本方先手且技能命中估算；不含暴击，不改变对战状态。</summary>
        public DamagePreview PreviewDamage(int side, int skillIndex)
        {
            if (side < 0 || side >= Sides.Length) throw new ArgumentOutOfRangeException(nameof(side));
            var p = Active(side);
            if (skillIndex < 0 || skillIndex >= p.Skills.Count) throw new ArgumentOutOfRangeException(nameof(skillIndex));
            var f = Active(1 - side);
            var skill = Db.Skills[p.Skills[skillIndex].Id];
            var preview = new DamagePreview { Eff = Db.TypeMul(skill.Type, f.Type) };
            if (skill.Power <= 0 || skill.Cat == "status") return preview;

            preview.Notes.Add("按当前状态、本方先手且技能命中估算，不含暴击");
            double powerMul = 1;
            foreach (var effect in skill.Effects)
            {
                if (effect.At != "pre") continue;
                if (effect.Code != "POWER_MUL" && effect.Code != "POWER_PER_MARK")
                {
                    preview.Notes.Add("其他命中前效果未计入伤害预览");
                    continue;
                }

                string why;
                bool applies = CondMet(p, f, Node.First, effect.Cond, out why);
                if (effect.Cond != null) preview.Notes.Add(why + " → 条件" + (applies ? "满足" : "不满足"));
                if (!applies || effect.Chance <= 0) continue;
                if (effect.Chance < 100) preview.Notes.Add(effect.Chance + "% 威力效果按触发估算");
                if (effect.Code == "POWER_MUL")
                {
                    powerMul *= effect.Mul;
                    preview.Notes.Add("威力 ×" + effect.Mul.ToString("0.##"));
                }
                else
                {
                    var mark = p.FieldOf(effect.Field);
                    int stacks = mark == null ? 0 : mark.Stacks;
                    double mul = 1 + effect.Per * stacks;
                    powerMul *= mul;
                    preview.Notes.Add(Db.Fields[effect.Field].Name + " " + stacks + " 层 → 威力 ×" + mul.ToString("0.##"));
                }
            }

            var min = CalcDamage(side, skill, powerMul, true, 0.85);
            var max = CalcDamage(side, skill, powerMul, true, 1.0);
            preview.Min = min.Dmg;
            preview.Max = max.Dmg;
            preview.Eff = max.Eff;
            preview.Notes.AddRange(max.Notes);
            return preview;
        }

        /// <summary>使用结算时的行动纠正规则预测先手；平手时只报告随机，不掷骰。</summary>
        public OrderPreview PreviewOrder(BattleAction a0, BattleAction a1)
        {
            a0 = Normalize(0, a0);
            a1 = Normalize(1, a1);
            if (a0.Type == ActionType.Switch && a1.Type == ActionType.Switch)
                return new OrderPreview { First = -1, Reason = "双方换人，没有行动" };
            if (a0.Type == ActionType.Switch)
                return new OrderPreview { First = 1, Reason = "对方换人，本方唯一行动，先手行动" };
            if (a1.Type == ActionType.Switch)
                return new OrderPreview { First = 0, Reason = "对方换人，本方唯一行动，先手行动" };

            int p0 = ActPrio(0, a0), p1 = ActPrio(1, a1);
            int s0 = StatVal(Active(0), "spd"), s1 = StatVal(Active(1), "spd");
            if (p0 == p1 && s0 == s1)
                return new OrderPreview { First = -1, Reason = "随机" };

            int first = p0 != p1 ? (p0 > p1 ? 0 : 1) : (s0 > s1 ? 0 : 1);
            int firstPrio = first == 0 ? p0 : p1, secondPrio = first == 0 ? p1 : p0;
            int firstSpeed = first == 0 ? s0 : s1, secondSpeed = first == 0 ? s1 : s0;
            string reason = p0 != p1
                ? "先制 " + firstPrio + " 对 " + secondPrio
                : "先制都是 " + firstPrio + "，反应 " + firstSpeed + " 对 " + secondSpeed;
            return new OrderPreview { First = first, Reason = reason };
        }
    }
}
