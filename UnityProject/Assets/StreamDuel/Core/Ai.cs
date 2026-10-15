// 电脑对手：按估算伤害和局面给每个选项打分，挑最高的，带一点随机。
using System;
using System.Linq;

namespace StreamDuel.Core
{
    public enum AiLevel
    {
        Easy,
        Normal
    }

    public static class Ai
    {
        static double Estimate(Battle b, int s, SkillDef sk, bool assumeFirst)
        {
            var p = b.Active(s); var f = b.Active(1 - s);
            double pm = 1;
            foreach (var e in sk.Effects)
            {
                if (e.At != "pre") continue;
                string why;
                if (e.Code == "POWER_MUL" && b.CondMet(p, f, assumeFirst ? Node.First : Node.Second, e.Cond, out why)) pm *= e.Mul;
                if (e.Code == "POWER_PER_MARK") { var m = p.FieldOf(e.Field); pm *= 1 + e.Per * (m == null ? 0 : m.Stacks); }
            }
            return b.CalcDamage(s, sk, pm, true).Dmg * (sk.Acc / 100.0);
        }

        // 对面场上主播对 s 方场上主播的最大估算伤害占比
        static double Threat(Battle b, int s)
        {
            var f = b.Active(1 - s); var p = b.Active(s);
            double best = 0;
            foreach (var sl in f.Skills)
            {
                var sk = b.Db.Skills[sl.Id];
                if (sk.Power > 0 && sl.Pp > 0) best = Math.Max(best, Estimate(b, 1 - s, sk, false));
            }
            return best / Math.Max(1, p.Hp);
        }

        static double Matchup(Battle b, int s, int idx)
        {
            var cand = b.Sides[s].Team[idx]; var f = b.Active(1 - s);
            double mine = cand.Skills.Select(sl => b.Db.Skills[sl.Id]).Where(k => k.Power > 0).Select(k => b.Db.TypeMul(k.Type, f.Type) * (k.Type == cand.Type ? 1.5 : 1)).DefaultIfEmpty(0).Max();
            double theirs = f.Skills.Select(sl => b.Db.Skills[sl.Id]).Where(k => k.Power > 0).Select(k => b.Db.TypeMul(k.Type, cand.Type) * (k.Type == f.Type ? 1.5 : 1)).DefaultIfEmpty(0).Max();
            return mine - theirs + cand.HpRatio - (cand.Def.Stats["spd"] < f.Def.Stats["spd"] ? 0.2 : 0);
        }

        static bool CanInflictWeak(Battle b, int s)
        {
            foreach (var option in b.LegalFor(s).Skills)
            {
                if (!option.Usable) continue;
                if (b.Db.Skills[option.Id].Effects.Any(e => e.Code == "STATUS" && e.Target == "foe"
                    && e.Chance > 0 && !b.Db.Statuses[e.Status].IsControl)) return true;
            }
            var persona = b.PersonaOf(b.Active(s));
            return persona.Code == "T_CONTACT_STATUS" && persona.P("chance") > 0 && persona.Status != null
                && !b.Db.Statuses[persona.Status].IsControl
                && b.LegalFor(1 - s).Skills.Any(o => o.Usable && b.Db.Skills[o.Id].Cat == "interact");
        }

        static double FieldValue(Battle b, int s, EffectDef effect, double threat, bool goFirst)
        {
            var p = b.Active(s); var f = b.Active(1 - s);
            var target = effect.Target == "self" ? p : f;
            if (target.HasField(effect.Field)) return 0;
            var field = b.Db.Fields[effect.Field];
            if (field.ReflectDamage > 0)
            {
                if (effect.Target != "self" || (!goFirst && threat >= 1)) return 0;
                double incoming = Math.Min(p.Hp, threat * p.Hp);
                return Math.Min(0.7, incoming * field.ReflectDamage / Math.Max(1, f.Hp) * 2);
            }
            if (field.ImmuneWeak)
            {
                if (effect.Target != "self" || p.Fields.Any(x => b.Db.Fields[x.Id].ImmuneWeak)) return 0;
                return CanInflictWeak(b, 1 - s) ? 0.4 : 0;
            }
            if (field.PreventSwitch)
            {
                if (effect.Target != "foe" || b.LegalFor(1 - s).Switches.Count == 0) return 0;
                return b.ControlOf(f) != null || f.Statuses.Any(x => !b.Db.Statuses[x.Id].IsControl) ? 0.4 : 0.22;
            }
            return threat < 0.6 ? 0.32 : 0.12;
        }

        public static BattleAction Choose(Battle b, int s)
        {
            var L = b.LegalFor(s); var p = b.Active(s); var f = b.Active(1 - s);
            if (L.Controlled != null)
            {
                if (L.Switches.Count > 0 && p.HpRatio > 0.3 && b.Random() < 0.35)
                    return BattleAction.SwitchTo(L.Switches.OrderByDescending(i => Matchup(b, s, i)).First());
                return BattleAction.Pass();
            }
            if (L.Struggle) return new BattleAction { Type = ActionType.Struggle };
            double threat = Threat(b, s);
            bool foeLocked = b.ControlOf(f) != null || f.Fields.Any(x => b.Db.Fields[x.Id].ImmuneControl);
            bool foeWeakImmune = f.Fields.Any(x => b.Db.Fields[x.Id].ImmuneWeak);
            double bestV = double.MinValue; BattleAction best = BattleAction.Pass();
            foreach (var o in L.Skills)
            {
                if (!o.Usable) continue;
                var sk = b.Db.Skills[o.Id];
                bool goFirst = o.Prio.Value > 0 || (o.Prio.Value == 0 && b.StatVal(p, "spd") > b.StatVal(f, "spd"));
                double v = 0;
                if (sk.Power > 0)
                {
                    double est = Estimate(b, s, sk, goFirst) / Math.Max(1, f.Hp);
                    v = est >= 1 ? 1.6 + (goFirst ? 0.6 : 0) : est;
                    if (threat >= 1 && goFirst && est >= 1) v += 1;
                }
                foreach (var e in sk.Effects)
                {
                    double ch = e.Chance / 100.0;
                    switch (e.Code)
                    {
                        case "STATUS":
                            if (e.Target != "foe") break;
                            if (b.Db.Statuses[e.Status].IsControl) { if (!foeLocked) v += 0.5 * ch; }
                            else if (!foeWeakImmune && !f.HasStatus(e.Status)) v += 0.3 * ch;
                            break;
                        case "STAGE":
                            if (e.Target == "self")
                            {
                                int up = e.Changes.Count(kv => kv.Value > 0 && p.Stages[kv.Key] < 2);
                                if (up > 0 && p.HpRatio > 0.6 && threat < 0.5) v += 0.22 * up;
                            }
                            else if (sk.Power == 0) v += 0.18;
                            break;
                        case "HEAL": if (p.HpRatio < 0.5) v += (1 - p.HpRatio) * 0.9; break;
                        case "FIELD": v += FieldValue(b, s, e, threat, goFirst) * ch; break;
                        case "DISPEL_TURNS":
                            v += f.Fields.Where(x => b.Db.Fields[x.Id].Dispel)
                                .Sum(x => b.Db.Fields[x.Id].PreventSwitch ? -0.2 : 0.2);
                            break;
                        case "DISPEL_BOOST": v += 0.12 * f.Stages.Values.Count(x => x > 0); break;
                        case "STEAL_BOOST":
                        {
                            if (e.Target != "foe") break;
                            string why;
                            if (!b.CondMet(p, f, goFirst ? Node.First : Node.Second, e.Cond, out why)) break;
                            foreach (var stage in f.Stages.Where(kv => kv.Value > 0))
                            {
                                int gain = Math.Min(stage.Value, 6 - p.Stages[stage.Key]);
                                v += (0.12 + 0.08 * gain) * ch;
                            }
                            break;
                        }
                        case "CLEANSE": if (p.Statuses.Any(x => b.Db.Statuses[x.Id].Cleanse)) v += 0.4; break;
                    }
                }
                v += b.Random() * 0.08;
                if (v > bestV) { bestV = v; best = BattleAction.Skill(o.Index); }
            }
            if (L.Switches.Count > 0 && threat >= 0.9 && bestV < 0.5)
            {
                int c = L.Switches.OrderByDescending(i => Matchup(b, s, i)).First();
                if (Matchup(b, s, c) > Matchup(b, s, b.Sides[s].Active) + 0.5 && b.Random() < 0.6) return BattleAction.SwitchTo(c);
            }
            return best;
        }

        public static BattleAction Choose(Battle b, int s, AiLevel level)
        {
            if (level == AiLevel.Normal) return Choose(b, s);

            var legal = b.LegalFor(s);
            if (legal.Controlled != null) return BattleAction.Pass();
            if (legal.Struggle) return new BattleAction { Type = ActionType.Struggle };

            var skills = legal.Skills.Where(option => option.Usable).ToList();
            if (skills.Count == 0) return BattleAction.Pass();
            if (b.Random() < 0.5)
                return BattleAction.Skill(skills[(int)(b.Random() * skills.Count)].Index);

            var action = Choose(b, s);
            return action.Type == ActionType.Switch ? BattleAction.Skill(skills[0].Index) : action;
        }

        public static int ChooseReplacement(Battle b, int s)
        {
            var sd = b.Sides[s];
            return Enumerable.Range(0, sd.Team.Count).Where(i => !sd.Team[i].Fainted && i != sd.Active).OrderByDescending(i => Matchup(b, s, i)).First();
        }
    }
}
