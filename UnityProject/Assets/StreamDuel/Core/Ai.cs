// 电脑对手：按估算伤害和局面给每个选项打分，挑最高的，带一点随机。
using System;
using System.Collections.Generic;
using System.Linq;

namespace StreamDuel.Core
{
    public enum AiLevel
    {
        Easy,
        Normal,
        Hard,
        Expert
    }

    public static partial class Ai
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
            return Matchup(b, s, idx, AiWeights.Default);
        }

        static double Matchup(Battle b, int s, int idx, AiWeights w)
        {
            return Matchup(b, b.Sides[s].Team[idx], b.Active(1 - s), w);
        }

        static double Matchup(Battle b, Fighter cand, Fighter f)
        {
            return Matchup(b, cand, f, AiWeights.Default);
        }

        static double Matchup(Battle b, Fighter cand, Fighter f, AiWeights w)
        {
            double mine = cand.Skills.Select(sl => b.Db.Skills[sl.Id]).Where(k => k.Power > 0).Select(k => b.Db.TypeMul(k.Type, f.Type, f.Type2) * (cand.HasType(k.Type) ? 1.5 : 1)).DefaultIfEmpty(0).Max();
            double theirs = f.Skills.Select(sl => b.Db.Skills[sl.Id]).Where(k => k.Power > 0).Select(k => b.Db.TypeMul(k.Type, cand.Type, cand.Type2) * (f.HasType(k.Type) ? 1.5 : 1)).DefaultIfEmpty(0).Max();
            return mine - theirs + cand.HpRatio - (cand.Def.Stats["spd"] < f.Def.Stats["spd"] ? w.MatchupSlowerPenalty : 0);
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
            return FieldValue(b, s, effect, threat, goFirst, AiWeights.Default);
        }

        static double FieldValue(Battle b, int s, EffectDef effect, double threat, bool goFirst, AiWeights w)
        {
            var p = b.Active(s); var f = b.Active(1 - s);
            var target = effect.Target == "self" ? p : f;
            if (target.HasField(effect.Field)) return 0;
            var field = b.Db.Fields[effect.Field];
            if (field.ReflectDamage > 0)
            {
                if (effect.Target != "self" || (!goFirst && threat >= 1)) return 0;
                double incoming = Math.Min(p.Hp, threat * p.Hp);
                return Math.Min(w.ReflectScoreCap, incoming * field.ReflectDamage / Math.Max(1, f.Hp) * w.ReflectDamageScoreMultiplier);
            }
            if (field.ImmuneWeak)
            {
                if (effect.Target != "self" || p.Fields.Any(x => b.Db.Fields[x.Id].ImmuneWeak)) return 0;
                return CanInflictWeak(b, 1 - s) ? w.WeakImmunityScore : 0;
            }
            if (field.PreventSwitch)
            {
                if (effect.Target != "foe" || b.LegalFor(1 - s).Switches.Count == 0) return 0;
                return b.ControlOf(f) != null || f.Statuses.Any(x => !b.Db.Statuses[x.Id].IsControl) ? w.TrapAfflictedScore : w.TrapScore;
            }
            return threat < w.FieldMaxThreatRatio ? w.FieldSafeScore : w.FieldThreatenedScore;
        }

        public static int ChooseBan(Battle b, int s)
        {
            if (b == null) throw new ArgumentNullException(nameof(b));
            if (s < 0 || s >= b.Sides.Length) throw new ArgumentOutOfRangeException(nameof(s), "对战方必须是 0 或 1");
            if (b.Phase != Phase.Ban) throw new InvalidOperationException("现在不是选择禁用的阶段");
            if (b.HasChosenBan(s)) throw new InvalidOperationException("这一方已经选择禁用");
            var team = b.Sides[s].Team;
            var foes = b.Sides[1 - s].Team;
            var best = new List<int>();
            double bestScore = double.NegativeInfinity;
            for (int i = 0; i < foes.Count; i++)
            {
                double score = team.Average(ally => Matchup(b, foes[i], ally));
                if (score > bestScore + 1e-9)
                {
                    bestScore = score;
                    best.Clear();
                    best.Add(i);
                }
                else if (Math.Abs(score - bestScore) <= 1e-9) best.Add(i);
            }
            return best.Count == 1 ? best[0] : best[(int)(b.Random() * best.Count)];
        }

        public static int ChooseLead(Battle b, int s)
        {
            if (b == null) throw new ArgumentNullException(nameof(b));
            if (s < 0 || s >= b.Sides.Length) throw new ArgumentOutOfRangeException(nameof(s), "对战方必须是 0 或 1");
            if (b.Phase != Phase.Lead) throw new InvalidOperationException("现在不是选择首发的阶段");
            var team = b.Sides[s].Team;
            var foes = b.Sides[1 - s].Team;
            var best = new List<int>();
            double bestScore = double.NegativeInfinity;
            for (int i = 0; i < team.Count; i++)
            {
                double score = foes.Average(foe => Matchup(b, team[i], foe));
                if (score > bestScore + 1e-9)
                {
                    bestScore = score;
                    best.Clear();
                    best.Add(i);
                }
                else if (Math.Abs(score - bestScore) <= 1e-9) best.Add(i);
            }
            return best.Count == 1 ? best[0] : best[(int)(b.Random() * best.Count)];
        }

        public static BattleAction Choose(Battle b, int s)
        {
            return ChooseNormal(b, s, StyleWeights(b, s) ?? AiWeights.Default);
        }

        static BattleAction ChooseNormal(Battle b, int s, AiWeights w)
        {
            var L = b.LegalFor(s); var p = b.Active(s); var f = b.Active(1 - s);
            if (L.Controlled != null)
            {
                if (L.Switches.Count > 0 && p.HpRatio > w.ControlledSwitchMinHpRatio && b.Random() < w.ControlledSwitchProbability)
                    return BattleAction.SwitchTo(L.Switches.OrderByDescending(i => Matchup(b, s, i, w)).First());
                return BattleAction.Pass();
            }
            if (L.Struggle) return new BattleAction { Type = ActionType.Struggle };
            double threat = Threat(b, s);
            double bestV = double.MinValue; BattleAction best = BattleAction.Pass();
            foreach (var o in L.Skills)
            {
                if (!o.Usable) continue;
                var sk = b.Db.Skills[o.Id];
                bool goFirst = o.Prio.Value > 0 || (o.Prio.Value == 0 && b.StatVal(p, "spd") > b.StatVal(f, "spd"));
                double v = NormalSkillScore(b, s, sk, goFirst, threat, w);
                v += b.Random() * w.RandomScoreRange;
                if (v > bestV) { bestV = v; best = BattleAction.Skill(o.Index); }
            }
            if (L.Switches.Count > 0 && threat >= w.SwitchMinThreatRatio && bestV < w.SwitchMaxActionScore)
            {
                int c = L.Switches.OrderByDescending(i => Matchup(b, s, i, w)).First();
                if (Matchup(b, s, c, w) > Matchup(b, s, b.Sides[s].Active, w) + w.SwitchMinMatchupGain && b.Random() < w.SwitchProbability) return BattleAction.SwitchTo(c);
            }
            return best;
        }

        static double NormalSkillScore(Battle b, int s, SkillDef sk, bool goFirst, double threat, AiWeights w)
        {
            var p = b.Active(s); var f = b.Active(1 - s);
            bool foeLocked = b.ControlOf(f) != null || f.Fields.Any(x => b.Db.Fields[x.Id].ImmuneControl);
            bool foeWeakImmune = f.Fields.Any(x => b.Db.Fields[x.Id].ImmuneWeak);
            double v = 0;
            if (sk.Power > 0)
            {
                double est = Estimate(b, s, sk, goFirst) / Math.Max(1, f.Hp);
                v = est >= 1 ? w.KnockoutScore + (goFirst ? w.FirstKnockoutBonus : 0) : est * w.DamageScoreMultiplier;
                if (threat >= 1 && goFirst && est >= 1) v += w.ThreatenedFirstKnockoutBonus;
            }
            foreach (var e in sk.Effects)
            {
                double ch = e.Chance / 100.0;
                switch (e.Code)
                {
                    case "STATUS":
                        if (e.Target != "foe") break;
                        if (b.Db.Statuses[e.Status].IsControl) { if (!foeLocked) v += w.ControlStatusScore * ch; }
                        else if (!foeWeakImmune && !f.HasStatus(e.Status)) v += w.WeakStatusScore * ch;
                        break;
                    case "STAGE":
                        if (e.Target == "self")
                        {
                            int up = e.Changes.Count(kv => kv.Value > 0 && p.Stages[kv.Key] < w.StageBoostTarget);
                            if (up > 0 && p.HpRatio > w.StageMinHpRatio && threat < w.StageMaxThreatRatio) v += w.StageScore * up;
                        }
                        else if (sk.Power == 0) v += w.FoeStageScore;
                        break;
                    case "HEAL": if (p.HpRatio < w.HealMaxHpRatio) v += (1 - p.HpRatio) * w.HealMissingHpScore; break;
                    case "FIELD": v += FieldValue(b, s, e, threat, goFirst, w) * ch; break;
                    case "DISPEL_TURNS":
                        v += f.Fields.Where(x => b.Db.Fields[x.Id].Dispel)
                            .Sum(x => b.Db.Fields[x.Id].PreventSwitch ? w.DispelTrapScore : w.DispelFieldScore);
                        break;
                    case "DISPEL_BOOST": v += w.DispelBoostScore * f.Stages.Values.Count(x => x > 0); break;
                    case "STEAL_BOOST":
                    {
                        if (e.Target != "foe") break;
                        string why;
                        if (!b.CondMet(p, f, goFirst ? Node.First : Node.Second, e.Cond, out why)) break;
                        foreach (var stage in f.Stages.Where(kv => kv.Value > 0))
                        {
                            int gain = Math.Min(stage.Value, b.StageLimit - p.Stages[stage.Key]);
                            v += (w.StealBoostBaseScore + w.StealBoostPerStageScore * gain) * ch;
                        }
                        break;
                    }
                    case "CLEANSE": if (p.Statuses.Any(x => b.Db.Statuses[x.Id].Cleanse)) v += w.CleanseScore; break;
                    case "PP_REDUCE":
                    {
                        if (w.PpReduceScore == 0 || e.Target != "foe" || e.N <= 0 || f.LastSkillIndex < 0 || f.LastSkillIndex >= f.Skills.Count) break;
                        string why;
                        if (!b.CondMet(p, f, goFirst ? Node.First : Node.Second, e.Cond, out why) || b.LegalFor(1 - s).FreePp) break;
                        var slot = f.Skills[f.LastSkillIndex];
                        v += w.PpReduceScore * Math.Min(e.N, slot.Pp) / Math.Max(1, slot.Max) * ch * sk.Acc / 100.0;
                        break;
                    }
                }
            }
            return v;
        }

        public static BattleAction Choose(Battle b, int s, AiLevel level)
        {
            if (level == AiLevel.Normal) return Choose(b, s);
            if (level == AiLevel.Hard) return ChooseHard(b, s);
            if (level == AiLevel.Expert) return ChooseExpert(b, s);

            var legal = b.LegalFor(s);
            if (legal.Controlled != null) return BattleAction.Pass();
            if (legal.Struggle) return new BattleAction { Type = ActionType.Struggle };

            var skills = legal.Skills.Where(option => option.Usable).ToList();
            if (skills.Count == 0) return BattleAction.Pass();
            if (b.Random() < 0.5)
                return BattleAction.Skill(skills[(int)(b.Random() * skills.Count)].Index);

            var action = ChooseNormal(b, s, AiWeights.Default);
            return action.Type == ActionType.Switch ? BattleAction.Skill(skills[0].Index) : action;
        }

        public static BattleAction Choose(Battle b, int s, AiLevel level, AiWeights w)
        {
            if (level != AiLevel.Normal) return Choose(b, s, level);
            if (w == null) throw new ArgumentNullException(nameof(w));
            return ChooseNormal(b, s, StyleWeights(b, s) ?? w);
        }

        public static int ChooseReplacement(Battle b, int s)
        {
            var sd = b.Sides[s];
            return Enumerable.Range(0, sd.Team.Count).Where(i => !sd.Team[i].Fainted && i != sd.Active).OrderByDescending(i => Matchup(b, s, i)).First();
        }

        public static int ChooseReplacement(Battle b, int s, AiLevel level)
        {
            if (level == AiLevel.Expert) return ChooseExpertReplacement(b, s);
            return level == AiLevel.Hard ? ChooseHardReplacement(b, s) : ChooseReplacement(b, s);
        }
    }
}
