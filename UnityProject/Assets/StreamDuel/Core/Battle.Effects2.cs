using System;
using System.Collections.Generic;
using System.Linq;

namespace StreamDuel.Core
{
    public sealed partial class Battle
    {
        void StealBoost(Ctx ctx, Fighter target)
        {
            if (ctx.P.Fainted || target.Fainted) return;
            if (target == ctx.P)
            {
                Add(ctx.Node, "偷强化：不能从自身转移能力提升", "stage", ctx.S);
                return;
            }

            var positive = target.Stages.Where(pair => pair.Value > 0).ToList();
            if (positive.Count == 0)
            {
                Add(ctx.Node, "偷强化：" + target.Name + " 没有能力提升", "stage", ctx.S);
                return;
            }
            var parts = new List<string>();
            foreach (var pair in positive)
            {
                int before = ctx.P.Stages[pair.Key];
                int after = Math.Min(6, before + pair.Value);
                target.Stages[pair.Key] = 0;
                ctx.P.Stages[pair.Key] = after;
                parts.Add(Db.StatName(pair.Key) + "：" + target.Name + " +" + pair.Value + " → 0，" +
                    ctx.P.Name + " " + before + " → " + after);
            }
            Add(ctx.Node, "偷强化：" + string.Join("；", parts) + "（每项上限 +6，目标下降等级保留）", "stage", ctx.S);
        }

        bool BlockWeakStatus(Fighter target, StatusDef status, string node)
        {
            var shield = target.Fields.FirstOrDefault(field => Db.Fields[field.Id].ImmuneWeak);
            if (shield == null) return false;
            Add(node, target.Name + " 的「" + status.Name + "」被" + Db.Fields[shield.Id].Name + "挡下", "status", target.Side);
            return true;
        }

        bool SwitchLocked(Fighter fighter)
        {
            return fighter.Fields.Any(field => Db.Fields[field.Id].PreventSwitch);
        }

        int DamageWithReflection(Ctx ctx, Fighter target, int amount, string text)
        {
            var attacker = ctx.P;
            int dealt = Damage(target, amount, ctx.Node, text, true, action: ctx, source: attacker);
            if (dealt <= 0 || attacker == target || attacker.Fainted || Phase == Phase.Over) return dealt;
            foreach (var field in target.Fields)
            {
                var definition = Db.Fields[field.Id];
                if (definition.ReflectDamage <= 0) continue;
                int reflected = Math.Max(1, (int)Math.Floor(dealt * definition.ReflectDamage));
                // 直接记反弹伤害，不再进入反弹入口，也不触发受反弹者的下播人设。
                Damage(attacker, reflected, ctx.Node,
                    "「" + definition.Name + "」反弹：" + attacker.Name + " 人气 -{n}", true, false, action: ctx, source: target);
                if (attacker.Fainted || Phase == Phase.Over) break;
            }
            return dealt;
        }
    }
}
