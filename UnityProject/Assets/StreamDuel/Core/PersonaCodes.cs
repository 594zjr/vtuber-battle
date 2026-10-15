using System;
using System.Collections.Generic;

namespace StreamDuel.Core
{
    public static class PersonaCodes
    {
        public static IReadOnlyList<CodeInfo> All { get; } = Array.AsReadOnly(new[]
        {
            new CodeInfo("T_LOWHP_POWER", "本方造成普通技能伤害时", "人设本体永久生效；不可消除",
                "Describe.Persona: PersonaDef.Node/Desc", "below", "mul"),
            new CodeInfo("T_EXECUTE", "本方造成普通技能伤害时", "人设本体永久生效；不可消除",
                "Describe.Persona: PersonaDef.Node/Desc", "below", "mul"),
            new CodeInfo("T_REGEN", "回合结束", "人设本体不可消除；人气回复即时结算",
                "Describe.Persona: PersonaDef.Node/Desc", "frac"),
            new CodeInfo("T_BENCH_GUARD", "受到普通技能伤害时", "人设本体永久生效；不可消除",
                "Describe.Persona: PersonaDef.Node/Desc", "per"),
            new CodeInfo("T_INTERACT_REDUCE", "受到普通互动技能伤害时", "人设本体永久生效；不可消除",
                "Describe.Persona: PersonaDef.Node/Desc", "mul"),
            new CodeInfo("T_FIRST_TURN_PRIO", "排先手时", "人设本体永久生效；不可消除",
                "Describe.Persona: PersonaDef.Node/Desc", "prio"),
            new CodeInfo("T_TURNSTART_CLEANSE", "回合开始", "人设本体不可消除；移除状态即时结算",
                "Describe.Persona: PersonaDef.Node/Desc"),
            new CodeInfo("T_FREE_PP_SLOT6", "检查技能可用性及使用技能时", "人设本体永久生效；不可消除",
                "Describe.Persona: PersonaDef.Node/Desc"),
            new CodeInfo("T_CONTACT_STATUS", "被普通互动技能击中后", "人设本体不可消除；所施状态按 StatusDef.Cleanse 决定能否净化",
                "Describe.Persona: PersonaDef.Node/Desc", "chance", "status"),
            new CodeInfo("T_ENTER_FIELD", "登场时", "人设本体不可消除；所授效果按 FieldDef.Dispel 决定能否消回合；下场清除",
                "Describe.Persona: PersonaDef.Node/Desc", "turns", "field"),
            new CodeInfo("T_FAINT_CURSE", "下播时（反弹导致的下播除外）", "人设本体不可消除；产生的正等级可被消强化；下场清零",
                "Describe.Persona: PersonaDef.Node/Desc", "n"),
            new CodeInfo("T_SPEED_IF_FIRST", "回合结束", "人设本体不可消除；产生的正等级可被消强化；下场清零",
                "Describe.Persona: PersonaDef.Node/Desc；StageLimit != 6 时 describe.persona.speed.first", "n"),
            new CodeInfo("T_KO_HEAL", "本方技能直接击倒对手后", "人设本体不可消除；人气回复即时结算",
                "Describe.Persona: describe.persona.ko.heal", "frac"),
            new CodeInfo("T_FIRST_CONTROL_IMMUNE", "被施加控制类状态时", "人设本体不可消除；每次登场重置免疫次数",
                "Describe.Persona: describe.persona.first.control.immune"),
            new CodeInfo("T_ENDURE", "受到伤害时（反弹除外）", "人设本体不可消除；每场对战一次；下场不重置",
                "Describe.Persona: describe.persona.endure"),
            new CodeInfo("T_ALLY_PP_ON_ENTER", "登场时", "人设本体不可消除；PP 回复即时结算",
                "Describe.Persona: describe.persona.ally.pp.enter", "n"),
            new CodeInfo("T_STAGE_ON_HIT", "受到普通技能直接伤害后", "人设本体不可消除；产生的正等级可被消强化；下场清零",
                "Describe.Persona: describe.persona.stage.hit", "atk", "def", "spa", "sdef", "spd"),
        });
    }
}
