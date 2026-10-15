// 把效果数据翻译成给玩家看的说明文字。判定时机和能否被消除永远单独标出来。
using System.Linq;

namespace StreamDuel.Core
{
    public sealed class EffectText { public string At, Text, Dispel; }

    public static class Describe
    {
        private static StreamDuel.Core.Loc Loc { get { return StreamDuel.Core.Loc.Current; } }

        public static string Tracks(ContentDb db, StreamerDef streamer)
        {
            return streamer.Type2 == null ? db.Types[streamer.Type].Name ?? "" :
                Loc.T("describe.tracks.dual", db.Types[streamer.Type].Name, db.Types[streamer.Type2].Name);
        }

        public static string Frac(double f)
        {
            if (System.Math.Abs(f - 0.5) < 1e-6) return Loc.T("describe.fraction.half");
            if (System.Math.Abs(f - 0.25) < 1e-6) return Loc.T("describe.fraction.quarter");
            if (System.Math.Abs(f - 0.125) < 1e-6) return Loc.T("describe.fraction.eighth");
            return Loc.T("describe.fraction.percent", System.Math.Round(f * 100));
        }

        public static string Cond(CondDef c)
        {
            if (c == null) return "";
            switch (c.Kind)
            {
                case "foeHpAbove": return Loc.T("describe.condition.foe.hp.above", System.Math.Round(c.Value * 100));
                case "foeWeak": return Loc.T("describe.condition.foe.weak");
                case "actedFirst": return Loc.T("describe.condition.acted.first");
                case "actedSecond": return Loc.T("describe.condition.acted.second");
                case "foeBuffed": return Loc.T("describe.condition.foe.buffed");
            }
            return Loc.T("describe.condition.unknown", c.Kind);
        }

        public static EffectText Effect(ContentDb db, EffectDef e)
        {
            string tgt = e.Target == "self" ? Loc.T("describe.target.self") : Loc.T("describe.target.foe");
            string ch = e.Chance < 100 ? Loc.T("describe.chance", e.Chance) : "";
            string what, dispel = null;
            switch (e.Code)
            {
                case "STATUS":
                {
                    var s = db.Statuses[e.Status];
                    what = Loc.T("describe.effect.status", ch, tgt, s.Name,
                        s.IsControl ? Loc.T("describe.status.control") : Loc.T("describe.status.weak"));
                    dispel = s.Cleanse ? Loc.T("describe.dispel.cleanse") : Loc.T("describe.dispel.no.cleanse");
                    break;
                }
                case "STAGE":
                    what = Loc.T("describe.effect.stage", ch, tgt, string.Join(Loc.T("describe.list.separator"),
                        e.Changes.Select(kv => kv.Value > 0
                            ? Loc.T("describe.stage.increase", db.StatName(kv.Key), kv.Value)
                            : Loc.T("describe.stage.change", db.StatName(kv.Key), kv.Value))));
                    if (e.Changes.Values.Any(v => v > 0)) dispel = Loc.T("describe.dispel.boost");
                    break;
                case "STEAL_BOOST":
                    what = Loc.T("describe.effect.steal.boost", ch, tgt);
                    dispel = Loc.T("describe.dispel.steal.boost");
                    break;
                case "HEAL": what = Loc.T("describe.effect.heal", Frac(e.Frac)); break;
                case "RECOIL": what = Loc.T("describe.effect.recoil", Frac(e.Frac)); break;
                case "DRAIN": what = Loc.T("describe.effect.drain", Frac(e.Frac)); break;
                case "PP_REDUCE":
                    what = Loc.T("describe.effect.pp.reduce", ch, tgt, e.N);
                    dispel = Loc.T("describe.dispel.instant");
                    break;
                case "FIXED_DAMAGE":
                    what = Loc.T("describe.effect.fixed.damage", ch, tgt, e.N);
                    dispel = Loc.T("describe.dispel.instant");
                    break;
                case "FIELD":
                {
                    var f = db.Fields[e.Field];
                    what = Loc.T("describe.effect.field", ch, tgt, f.Name,
                        e.Turns < 0 ? Loc.T("describe.duration.until.leave") : Loc.T("describe.duration.turns", e.Turns), f.Desc);
                    dispel = f.Dispel ? Loc.T("describe.dispel.turns") : Loc.T("describe.dispel.no.turns");
                    break;
                }
                case "ADD_MARK": what = Loc.T("describe.effect.add.mark", tgt, db.Fields[e.Field].Name, e.N); dispel = Loc.T("describe.dispel.mark"); break;
                case "POWER_PER_MARK": what = Loc.T("describe.effect.power.per.mark", db.Fields[e.Field].Name, System.Math.Round(e.Per * 100),
                    e.Consume ? Loc.T("describe.effect.consume.marks") : ""); break;
                case "DISPEL_TURNS": what = Loc.T("describe.effect.dispel.turns"); break;
                case "DISPEL_BOOST": what = Loc.T("describe.effect.dispel.boost"); break;
                case "CLEANSE": what = Loc.T("describe.effect.cleanse"); break;
                case "POWER_MUL": what = Loc.T("describe.effect.power.mul", e.Mul); break;
                default: what = e.Code; break;
            }
            return new EffectText { At = e.At == "pre" ? Loc.T("describe.at.pre") : Loc.T("describe.at.post"), Text = Cond(e.Cond) + what, Dispel = dispel };
        }

        public static EffectText Persona(ContentDb db, PersonaDef persona)
        {
            string at = persona.Node, text = persona.Desc ?? "", dispel = Loc.T("describe.dispel.persona");
            switch (persona.Code)
            {
                case "T_KO_HEAL":
                    at = Loc.T("describe.at.ko");
                    text = Loc.T("describe.persona.ko.heal", Frac(persona.P("frac")));
                    dispel = Loc.T("describe.dispel.persona.heal", dispel);
                    break;
                case "T_FIRST_CONTROL_IMMUNE":
                    at = Loc.T("describe.at.control");
                    text = Loc.T("describe.persona.first.control.immune");
                    break;
                case "T_ENDURE":
                    at = Loc.T("describe.at.damage");
                    text = Loc.T("describe.persona.endure");
                    break;
                case "T_ALLY_PP_ON_ENTER":
                    at = Loc.T("describe.at.enter");
                    text = Loc.T("describe.persona.ally.pp.enter", (int)persona.P("n"));
                    dispel = Loc.T("describe.dispel.persona.pp", dispel);
                    break;
                case "T_STAGE_ON_HIT":
                {
                    at = Loc.T("describe.at.hit");
                    var changes = ContentDb.StatKeys.Where(k => (int)persona.P(k) > 0)
                        .Select(k => Loc.T("describe.stage.increase", db.StatName(k), (int)persona.P(k)));
                    text = Loc.T("describe.persona.stage.hit", string.Join(Loc.T("describe.list.separator"), changes));
                    dispel = Loc.T("describe.dispel.persona.stage", dispel);
                    break;
                }
            }
            return new EffectText { At = at, Text = text, Dispel = dispel };
        }

        public static string FieldKind(FieldDef f)
        {
            switch (f.Kind) { case "turn": return Loc.T("describe.field.turn"); case "mark": return Loc.T("describe.field.mark"); case "protect": return Loc.T("describe.field.protect"); }
            return f.Kind;
        }
    }
}
