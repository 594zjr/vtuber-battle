using System;
using System.Collections.Generic;

namespace StreamDuel.Core
{
    public static class EffectCodes
    {
        public static IReadOnlyList<CodeInfo> All { get; } = Array.AsReadOnly(new[]
        {
            new CodeInfo("STATUS", "pre/post", "按 StatusDef.Cleanse 决定能否净化；下场保留",
                "Describe.Effect: describe.effect.status", "target", "status"),
            new CodeInfo("STAGE", "pre/post", "正等级可被消强化；负等级保留；下场清零",
                "Describe.Effect: describe.effect.stage", "target", "changes"),
            new CodeInfo("HEAL", "pre/post", "即时结算；不可驱散",
                "Describe.Effect: describe.effect.heal", "frac"),
            new CodeInfo("RECOIL", "pre/post", "即时结算；不可驱散",
                "Describe.Effect: describe.effect.recoil", "frac"),
            new CodeInfo("DRAIN", "pre/post", "即时结算；不可驱散",
                "Describe.Effect: describe.effect.drain", "frac"),
            new CodeInfo("PP_REDUCE", "pre/post", "即时结算；不可驱散",
                "Describe.Effect: describe.effect.pp.reduce", "target", "n"),
            new CodeInfo("FIXED_DAMAGE", "pre/post", "即时结算；不可驱散",
                "Describe.Effect: describe.effect.fixed.damage", "target", "n"),
            new CodeInfo("FIELD", "pre/post", "按 FieldDef.Dispel 决定能否消回合；下场清除",
                "Describe.Effect: describe.effect.field", "target", "field", "turns"),
            new CodeInfo("ADD_MARK", "pre/post", "按 FieldDef.Dispel 决定能否消回合；可被指定效果消耗；下场清除",
                "Describe.Effect: describe.effect.add.mark", "target", "field", "n"),
            new CodeInfo("POWER_MUL", "pre/post", "本次行动内即时结算；不可驱散",
                "Describe.Effect: describe.effect.power.mul", "mul"),
            new CodeInfo("POWER_PER_MARK", "pre/post", "本次行动内即时结算；不可驱散；consume 为 true 时移除自身该效果",
                "Describe.Effect: describe.effect.power.per.mark", "field", "per", "consume"),
            new CodeInfo("DISPEL_TURNS", "pre/post", "即时结算；不可驱散",
                "Describe.Effect: describe.effect.dispel.turns"),
            new CodeInfo("DISPEL_BOOST", "pre/post", "即时结算；不可驱散",
                "Describe.Effect: describe.effect.dispel.boost"),
            new CodeInfo("STEAL_BOOST", "pre/post", "转移后的正等级可被消强化；下场清零",
                "Describe.Effect: describe.effect.steal.boost", "target"),
            new CodeInfo("CLEANSE", "pre/post", "即时结算；不可驱散",
                "Describe.Effect: describe.effect.cleanse"),
        });
    }
}
