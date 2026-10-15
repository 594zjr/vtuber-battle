// 把效果数据翻译成给玩家看的说明文字。判定时机和能否被消除永远单独标出来。
using System.Linq;

namespace StreamDuel.Core
{
    public sealed class EffectText { public string At, Text, Dispel; }

    public static class Describe
    {
        public static string Tracks(ContentDb db, StreamerDef streamer)
        {
            return db.Types[streamer.Type].Name + (streamer.Type2 == null ? "" : "/" + db.Types[streamer.Type2].Name);
        }

        public static string Frac(double f)
        {
            if (System.Math.Abs(f - 0.5) < 1e-6) return "1/2";
            if (System.Math.Abs(f - 0.25) < 1e-6) return "1/4";
            if (System.Math.Abs(f - 0.125) < 1e-6) return "1/8";
            return System.Math.Round(f * 100) + "%";
        }

        public static string Cond(CondDef c)
        {
            if (c == null) return "";
            switch (c.Kind)
            {
                case "foeHpAbove": return "若对手人气高于 " + System.Math.Round(c.Value * 100) + "%，";
                case "foeWeak": return "若对手处于弱化类状态，";
                case "actedFirst": return "若本方先手，";
                case "actedSecond": return "若本方后手，";
                case "foeBuffed": return "若对手有能力提升，";
            }
            return c.Kind + "，";
        }

        public static EffectText Effect(ContentDb db, EffectDef e)
        {
            string tgt = e.Target == "self" ? "自身" : "对手";
            string ch = e.Chance < 100 ? e.Chance + "% " : "";
            string what, dispel = null;
            switch (e.Code)
            {
                case "STATUS":
                {
                    var s = db.Statuses[e.Status];
                    what = ch + "使" + tgt + "「" + s.Name + "」（" + (s.IsControl ? "控制类" : "弱化类") + "）";
                    dispel = s.Cleanse ? "可净化" : "不可净化";
                    break;
                }
                case "STAGE":
                    what = ch + tgt + string.Join("、", e.Changes.Select(kv => db.StatName(kv.Key) + (kv.Value > 0 ? "+" : "") + kv.Value));
                    if (e.Changes.Values.Any(v => v > 0)) dispel = "提升可被消强化";
                    break;
                case "STEAL_BOOST":
                    what = ch + "将" + tgt + "所有大于 0 的能力等级加到自身（每项最高 +6），并清空目标的这些提升；目标下降等级不变";
                    dispel = "转移即时结算；转移后大于 0 的能力等级可被消强化，下场清零";
                    break;
                case "HEAL": what = "回复最大人气的 " + Frac(e.Frac); break;
                case "RECOIL": what = "自身受到造成伤害 " + Frac(e.Frac) + " 的反噬"; break;
                case "DRAIN": what = "回复造成伤害的 " + Frac(e.Frac); break;
                case "PP_REDUCE":
                    what = ch + tgt + "最后使用的技能 PP -" + e.N + "（最低为 0，本次上场还没有用过技能则无效）";
                    dispel = "即时结算，不可驱散";
                    break;
                case "FIXED_DAMAGE":
                    what = ch + "对" + tgt + "造成固定 " + e.N + " 点伤害（不受伤害倍率影响，可解除挂机，不触发被击中后人设）";
                    dispel = "即时结算，不可驱散";
                    break;
                case "FIELD":
                {
                    var f = db.Fields[e.Field];
                    what = ch + tgt + "获得「" + f.Name + "」" + (e.Turns < 0 ? "直到下场" : e.Turns + " 回合") + "：" + f.Desc;
                    dispel = f.Dispel ? "可被消回合" : "消回合清不掉";
                    break;
                }
                case "ADD_MARK": what = tgt + "「" + db.Fields[e.Field].Name + "」+" + e.N + " 层"; dispel = "印记，消回合清不掉"; break;
                case "POWER_PER_MARK": what = "自身每层「" + db.Fields[e.Field].Name + "」威力 +" + System.Math.Round(e.Per * 100) + "%" + (e.Consume ? "，然后消耗全部层数" : ""); break;
                case "DISPEL_TURNS": what = "消回合：清掉对手所有回合类直播间效果（印记和保护类清不掉）"; break;
                case "DISPEL_BOOST": what = "消强化：对手所有能力提升归零"; break;
                case "CLEANSE": what = "净化自身所有弱化类状态"; break;
                case "POWER_MUL": what = "本次威力 ×" + e.Mul; break;
                default: what = e.Code; break;
            }
            return new EffectText { At = e.At == "pre" ? "命中前判定" : "命中后判定", Text = Cond(e.Cond) + what, Dispel = dispel };
        }

        public static EffectText Persona(ContentDb db, PersonaDef persona)
        {
            string at = persona.Node, text = persona.Desc ?? "", dispel = "人设不可消除";
            switch (persona.Code)
            {
                case "T_KO_HEAL":
                    at = "击倒对手后";
                    text = "本方技能直接击倒对手后（含固定伤害，且对战未结束），自身回复最大人气的 " + Frac(persona.P("frac")) + "（向下取整，不超过最大人气）";
                    dispel += "；回复即时结算";
                    break;
                case "T_FIRST_CONTROL_IMMUNE":
                    at = "被施加控制类状态时";
                    text = "每次登场后，第一次本来可施加的控制类状态无效；下场再登场时重新计算。已因重连保护或已有控制而无效的施加不消耗本次免疫";
                    break;
                case "T_ENDURE":
                    at = "受到伤害时";
                    text = "每场对战仅一次：受到会使人气归零的伤害时，保留 1 点人气；下场不重置，反弹伤害不触发";
                    break;
                case "T_ALLY_PP_ON_ENTER":
                    at = "登场时";
                    text = "登场时，己方后排所有还在播的队友，每个技能 PP +" + (int)persona.P("n") + "，不超过各技能上限";
                    dispel += "；PP 回复即时结算";
                    break;
                case "T_STAGE_ON_HIT":
                {
                    at = "被击中后";
                    var changes = ContentDb.StatKeys.Where(k => (int)persona.P(k) > 0)
                        .Select(k => db.StatName(k) + "+" + (int)persona.P(k));
                    text = "每次受到普通技能直接伤害，实际扣除人气且没有下播时，自身" + string.Join("、", changes) + "（能力等级最高 +6）；固定伤害、持续伤害、反噬和反弹不触发。反弹使行动方下播后，不再触发";
                    dispel += "；能力提升可被消强化，下场清零";
                    break;
                }
            }
            return new EffectText { At = at, Text = text, Dispel = dispel };
        }

        public static string FieldKind(FieldDef f)
        {
            switch (f.Kind) { case "turn": return "回合类"; case "mark": return "印记"; case "protect": return "保护"; }
            return f.Kind;
        }

        public static string StatusKind(StatusDef status)
        {
            return status.IsControl ? "控制类" : "弱化类";
        }

        public static string StatusDuration(StatusDef status, int remaining)
        {
            return status.IsControl ? "跳过 " + remaining + " 次" : remaining + " 回合";
        }

        public static string StatusTiming(StatusDef status)
        {
            return status.IsControl ? "自身行动时" : Node.TurnEnd;
        }

        public static string StatusTooltip(StatusDef status, int remaining)
        {
            // 控制消耗的是行动次数，不能把它写成按回合结束倒数。
            return status.Name + " · " + StatusKind(status) + " · 剩余：" + StatusDuration(status, remaining)
                + " · 时机：" + StatusTiming(status) + " · " + (status.Cleanse ? "可净化" : "不可净化")
                + "\n" + status.Desc;
        }

        public static string FieldDuration(FieldDef field, int turns, int stacks)
        {
            return field.MaxStacks > 0 ? stacks + " 层" : turns < 0 ? "直到下场" : turns + " 回合";
        }

        public static string FieldTooltip(FieldDef field, int turns, int stacks)
        {
            string remaining = turns < 0 ? "直到下场" : turns + " 回合";
            string layers = field.MaxStacks > 0 ? " · " + stacks + " 层" : "";
            return field.Name + " · " + FieldKind(field) + layers + " · 剩余：" + remaining
                + " · 时机：" + field.When + " · " + (field.Dispel ? "可被消回合" : "消回合清不掉")
                + " · 下场清空\n" + field.Desc;
        }
    }
}
