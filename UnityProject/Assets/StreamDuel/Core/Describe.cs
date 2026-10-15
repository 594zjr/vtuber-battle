// 把效果数据翻译成给玩家看的说明文字。判定时机和能否被消除永远单独标出来。
using System.Linq;

namespace StreamDuel.Core
{
    public sealed class EffectText { public string At, Text, Dispel; }

    public static class Describe
    {
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

        public static string FieldKind(FieldDef f)
        {
            switch (f.Kind) { case "turn": return "回合类"; case "mark": return "印记"; case "protect": return "保护"; }
            return f.Kind;
        }
    }
}
