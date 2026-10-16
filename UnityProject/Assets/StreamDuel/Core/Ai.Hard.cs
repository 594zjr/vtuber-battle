using System;
using System.Collections.Generic;
using System.Linq;

namespace StreamDuel.Core
{
    public static partial class Ai
    {
        sealed class HardDamage
        {
            public double Min, Max, Expected;
            public bool Guaranteed;
        }

        sealed class HardOption
        {
            public BattleAction Action;
            public double Score;
        }

        static List<BattleAction> HardActions(Battle b, int s)
        {
            if (b.Active(s).Fainted) return new List<BattleAction> { BattleAction.Pass() };
            var legal = b.LegalFor(s);
            if (legal.Controlled != null) return new List<BattleAction> { BattleAction.Pass() };
            if (legal.Struggle) return new List<BattleAction> { new BattleAction { Type = ActionType.Struggle } };
            var actions = legal.Skills.Where(o => o.Usable).Select(o => BattleAction.Skill(o.Index)).ToList();
            if (actions.Count == 0) actions.Add(BattleAction.Pass());
            return actions;
        }

        static SkillDef HardSkill(Battle b, int s, BattleAction action)
        {
            if (action.Type == ActionType.Struggle) return b.Db.Struggle;
            return action.Type == ActionType.Skill ? b.Db.Skills[b.Active(s).Skills[action.Index].Id] : null;
        }

        static int HardFirst(Battle b, int s, BattleAction mine, BattleAction theirs)
        {
            return s == 0 ? b.PreviewOrder(mine, theirs).First : b.PreviewOrder(theirs, mine).First;
        }

        static HardDamage HardDamageFor(Battle b, int s, BattleAction action, string node)
        {
            var skill = HardSkill(b, s, action);
            var result = new HardDamage();
            if (skill == null || skill.Power <= 0 || skill.Cat == "status") return result;
            if (b.HardAiPreEffectsInterrupt(s, skill, node)) return result;

            if (action.Type == ActionType.Skill)
            {
                var preview = b.PreviewDamage(s, action.Index);
                result.Min = preview.Min;
                result.Max = preview.Max;
            }
            double minPower = 1, maxPower = 1, expectedPower = 1;
            bool complete = true;
            foreach (var effect in skill.Effects)
            {
                if (effect.At != "pre") continue;
                string why;
                if (!b.CondMet(b.Active(s), b.Active(1 - s), node, effect.Cond, out why) || effect.Chance <= 0) continue;
                if (effect.Code != "POWER_MUL" && effect.Code != "POWER_PER_MARK")
                {
                    // 给对手施加控制不降低主体伤害，控制概率不能取消已有的保底击倒。
                    if (effect.Code == "STATUS" && effect.Target == "foe" && b.Db.Statuses[effect.Status].IsControl) continue;
                    // 预览没有模拟其它命中前效果，不能把这个区间当作必定斩杀的证明。
                    complete = false;
                    continue;
                }
                var mark = effect.Code == "POWER_PER_MARK" ? b.Active(s).FieldOf(effect.Field) : null;
                double factor = effect.Code == "POWER_MUL" ? effect.Mul : 1 + effect.Per * (mark == null ? 0 : mark.Stacks);
                double chance = Math.Min(1, effect.Chance / 100.0);
                minPower *= chance < 1 ? Math.Min(1, factor) : factor;
                maxPower *= chance < 1 ? Math.Max(1, factor) : factor;
                expectedPower *= 1 + (factor - 1) * chance;
            }
            // PreviewDamage 假定先手并把概率威力视为触发，这里按真实节点和概率下界校正。
            if (action.Type == ActionType.Struggle || skill.Effects.Any(e => e.At == "pre"))
            {
                result.Min = b.CalcDamage(s, skill, minPower, true, 0.85).Dmg;
                result.Max = b.CalcDamage(s, skill, maxPower, true, 1).Dmg;
            }
            result.Expected = b.CalcDamage(s, skill, expectedPower, true).Dmg * Math.Min(1, skill.Acc / 100.0);
            result.Guaranteed = complete && skill.Acc >= 100;
            return result;
        }

        static HardDamage HardPairDamage(Battle b, int s, BattleAction action, BattleAction other)
        {
            int first = HardFirst(b, s, action, other);
            if (first >= 0)
            {
                if (first != s)
                {
                    var me = b.Active(s);
                    var persona = b.PersonaOf(me);
                    if (persona.Code == "T_LOWHP_POWER" && me.Hp >= me.MaxHp * persona.P("below"))
                    {
                        var hit = HardDamageFor(b, 1 - s, other, Node.First);
                        // 只有保底命中也能跨阈值、最大伤害仍能存活时，才计入后手低人气增伤。
                        if (hit.Guaranteed && hit.Max < me.Hp && me.Hp - hit.Min < me.MaxHp * persona.P("below"))
                        {
                            var after = b.CopyForHardAi();
                            after.Active(s).Hp -= (int)hit.Min;
                            return HardDamageFor(after, s, action, Node.Second);
                        }
                    }
                }
                return HardDamageFor(b, s, action, first == s ? Node.First : Node.Second);
            }
            var early = HardDamageFor(b, s, action, Node.First);
            var late = HardDamageFor(b, s, action, Node.Second);
            return new HardDamage
            {
                Min = Math.Min(early.Min, late.Min), Max = Math.Max(early.Max, late.Max),
                Expected = (early.Expected + late.Expected) / 2,
                Guaranteed = early.Guaranteed && late.Guaranteed
            };
        }

        static BattleAction HardBestAttack(Battle b, int s, BattleAction other)
        {
            return HardActions(b, s).OrderByDescending(a => HardPairDamage(b, s, a, other).Expected).First();
        }

        static BattleAction HardThreatAttack(Battle b, int s, BattleAction other)
        {
            return HardActions(b, s).OrderByDescending(a =>
            {
                var damage = HardPairDamage(b, s, a, other);
                bool lethalFirst = damage.Min >= b.Active(1 - s).Hp && HardFirst(b, s, a, other) == s;
                return damage.Expected + (lethalFirst ? b.Active(1 - s).MaxHp * 4 : 0);
            }).First();
        }

        static double HardMaxHit(Battle b, int s, BattleAction other)
        {
            return HardActions(b, s).Max(a => HardPairDamage(b, s, a, other).Max);
        }

        static double HardHitsToKo(Battle b, int defender, double minDamage)
        {
            var fighter = b.Active(defender);
            if (minDamage <= 0) return double.PositiveInfinity;
            if (minDamage >= fighter.Hp) return 1;
            var persona = b.PersonaOf(fighter);
            // 只计入永久的回末回复；有限回合的直播间效果不能视作无限续航。
            double recovery = persona.Code == "T_REGEN" ? (int)(fighter.MaxHp * persona.P("frac")) : 0;
            return minDamage > recovery
                ? 1 + Math.Ceiling((fighter.Hp - minDamage) / (minDamage - recovery))
                : double.PositiveInfinity;
        }

        static double HardRecovery(Battle b, int side)
        {
            var fighter = b.Active(side);
            var persona = b.PersonaOf(fighter);
            double heal = persona.Code == "T_REGEN" ? persona.P("frac") : 0;
            heal += fighter.Fields.Sum(f => b.Db.Fields[f.Id].Heal);
            heal -= fighter.Fields.Sum(f => b.Db.Fields[f.Id].Dot);
            heal -= fighter.Statuses.Sum(status => b.Db.Statuses[status.Id].Dot);
            return heal * fighter.MaxHp;
        }

        static T HardPick<T>(Battle b, IList<T> values, Func<T, double> score)
        {
            double bestScore = values.Max(score);
            var best = values.Where(v => Math.Abs(score(v) - bestScore) <= 1e-9).ToList();
            return best.Count == 1 ? best[0] : best[(int)(b.Random() * best.Count)];
        }

        static BattleAction ChooseHard(Battle live, int s)
        {
            return ChooseHard(live, s, AiWeights.HardDefault);
        }

        static BattleAction ChooseHard(Battle live, int s, AiWeights weights)
        {
            // 本次只训练无风格内容，既有风格仍在旧策略基底上表达原来的偏好。
            if (StyleWeights(live, s) != null) weights = AiWeights.Legacy;
            var b = live.CopyForHardAi();
            b.PrepareHardAiTurn();
            var actions = HardActions(b, s);
            var opponent = HardActions(b, 1 - s);
            var foe = b.Active(1 - s);
            var me = b.Active(s);
            var kills = new List<HardOption>();
            foreach (var action in actions)
            {
                bool safeKill = opponent.All(other =>
                {
                    var damage = HardPairDamage(b, s, action, other);
                    return damage.Guaranteed && damage.Min >= foe.Hp &&
                        (HardFirst(b, s, action, other) == s || HardSkill(b, 1 - s, other) == null);
                });
                if (safeKill) kills.Add(new HardOption { Action = action, Score = HardScore(b, s, action, weights) });
            }
            if (kills.Count > 0) return HardPick(live, kills, o => o.Score).Action;

            var bestAttack = HardBestAttack(b, s, BattleAction.Pass());
            var enemyAttack = HardThreatAttack(b, 1 - s, bestAttack);
            var incoming = HardPairDamage(b, 1 - s, enemyAttack, bestAttack);
            var outgoing = HardPairDamage(b, s, bestAttack, enemyAttack);
            bool dyingFirst = HardFirst(b, s, bestAttack, enemyAttack) == 1 - s && incoming.Min >= me.Hp;
            bool fatalReply = incoming.Min >= me.Hp && outgoing.Min < foe.Hp;
            double myHits = HardHitsToKo(b, 1 - s, outgoing.Min);
            double theirHits = HardHitsToKo(b, s, incoming.Min);
            bool losesRace = b.ControlOf(me) == null && outgoing.Min > 0 && incoming.Min > 0 &&
                ((myHits >= theirHits + 1 && theirHits <= 3) || (double.IsInfinity(myHits) && !double.IsInfinity(theirHits)));
            if (dyingFirst || fatalReply || losesRace || (b.ControlOf(me) != null && incoming.Max >= me.Hp))
            {
                var switches = new List<HardOption>();
                double current = HardMatchScore(b, s);
                foreach (int index in b.LegalFor(s).Switches)
                {
                    var next = live.CopyForHardAi();
                    next.SwitchForHardAi(s, index, false);
                    next.PrepareHardAiTurn();
                    // 我方换人时对方是唯一行动者；新场上槽位不能再作为合法换人传给顺序预览。
                    double hit = HardActions(next, 1 - s).Max(a => HardDamageFor(next, 1 - s, a, Node.First).Max);
                    if (next.Active(s).Hp <= hit) continue;
                    // 换人这一回合没有输出，必须先付出接招的人气，再评价下回合的对位。
                    next.Active(s).Hp -= (int)Math.Ceiling(hit);
                    next = next.AdvanceHardAiSwitchTurn(s);
                    if (next.Active(s).Hp <= 0) continue;
                    double matchup = HardMatchScore(next, s);
                    bool urgent = dyingFirst || fatalReply || b.ControlOf(me) != null;
                    var nextAttack = HardBestAttack(next, s, BattleAction.Pass());
                    var nextEnemy = HardThreatAttack(next, 1 - s, nextAttack);
                    double nextMine = HardHitsToKo(next, 1 - s, HardPairDamage(next, s, nextAttack, nextEnemy).Min);
                    double nextTheirs = HardHitsToKo(next, s, HardPairDamage(next, 1 - s, nextEnemy, nextAttack).Min);
                    bool improvesRace = !double.IsInfinity(nextMine) &&
                        (nextMine < nextTheirs || (nextMine == nextTheirs && HardFirst(next, s, nextAttack, nextEnemy) == s));
                    if (matchup > current + (urgent ? 0.1 : 0.25) && (urgent || improvesRace))
                        switches.Add(new HardOption { Action = BattleAction.SwitchTo(index), Score = matchup });
                }
                if (switches.Count > 0) return HardPick(live, switches, o => o.Score).Action;
            }

            var choices = actions.Select(a => new HardOption { Action = a, Score = HardScore(b, s, a, weights) }).ToList();
            return HardPick(live, choices, o => o.Score).Action;
        }

        static double HardMatchScore(Battle b, int s)
        {
            var mine = HardBestAttack(b, s, BattleAction.Pass());
            var theirs = HardThreatAttack(b, 1 - s, mine);
            var outgoing = HardPairDamage(b, s, mine, theirs);
            double incoming = HardMaxHit(b, 1 - s, mine);
            return outgoing.Expected / Math.Max(1, b.Active(1 - s).Hp) - incoming / Math.Max(1, b.Active(s).Hp) + b.Active(s).HpRatio * 0.2;
        }

        static double HardScore(Battle b, int s, BattleAction action, AiWeights weights)
        {
            var skill = HardSkill(b, s, action);
            if (skill == null) return 0;
            var me = b.Active(s);
            var foe = b.Active(1 - s);
            var enemy = HardThreatAttack(b, 1 - s, action);
            var damage = HardPairDamage(b, s, action, enemy);
            var incoming = HardPairDamage(b, 1 - s, enemy, action);
            int first = HardFirst(b, s, action, enemy);
            bool goesFirst = first == s;
            if (b.HardAiPreEffectsInterrupt(s, skill, goesFirst ? Node.First : Node.Second)) return -1000;
            double accuracy = Math.Min(1, skill.Acc / 100.0);
            double dealt = Math.Min(damage.Expected, foe.Hp);
            double estimate = damage.Expected / Math.Max(1, foe.Hp);
            double threat = incoming.Expected / Math.Max(1, me.Hp);
            // 保留普通难度的基础估值，只用准确的先后手、伤害和防护收益补充判断。
            double score = estimate >= 1 ? weights.KnockoutScore + (goesFirst ? weights.FirstKnockoutBonus : 0) : estimate * weights.DamageScoreMultiplier;
            if (threat >= 1 && goesFirst && estimate >= 1) score += weights.ThreatenedFirstKnockoutBonus;
            double myHits = HardHitsToKo(b, 1 - s, damage.Min);
            double theirHits = HardHitsToKo(b, s, incoming.Min);
            bool winsRace = myHits < theirHits || (myHits == theirHits && goesFirst);
            if (damage.Min > 0 && winsRace) score += 0.1 / myHits;

            foreach (var effect in skill.Effects)
            {
                double probability = accuracy * Math.Min(1, effect.Chance / 100.0);
                if (probability <= 0) continue;
                var state = b.CopyForHardAi();
                if (effect.At == "post") state.Active(1 - s).Hp = Math.Max(0, foe.Hp - (int)dealt);
                string node = goesFirst ? Node.First : Node.Second;
                string why;
                if (!state.CondMet(state.Active(s), state.Active(1 - s), node, effect.Cond, out why)) continue;
                double benefit = 0;
                switch (effect.Code)
                {
                    case "HEAL": if (me.HpRatio < weights.HealMaxHpRatio) benefit = (1 - me.HpRatio) * weights.HealMissingHpScore; break;
                    case "DRAIN": benefit = Math.Min(me.MaxHp - me.Hp, dealt * effect.Frac) / me.MaxHp * weights.HealMissingHpScore; break;
                    case "RECOIL":
                        benefit = -dealt * effect.Frac / Math.Max(1, me.Hp) * 0.1;
                        if (dealt * effect.Frac >= me.Hp) benefit -= 0.8;
                        break;
                    case "STATUS":
                    {
                        if (effect.Target != "foe" || foe.Hp <= dealt) break;
                        var status = b.Db.Statuses[effect.Status];
                        if (status.IsControl)
                        {
                            if (b.ControlOf(foe) == null && !foe.Fields.Any(f => b.Db.Fields[f.Id].ImmuneControl))
                            {
                                benefit = weights.ControlStatusScore;
                                if (skill.Power <= 0 && goesFirst && status.Skip == 1)
                                {
                                    // 先手使用纯单次控制，双方都没有输出；只有持续效果带来的进展值得加分。
                                    double progress = HardRecovery(b, s) / me.MaxHp - HardRecovery(b, 1 - s) / foe.MaxHp;
                                    benefit = Math.Max(0, progress) + incoming.Expected / me.MaxHp * 0.1;
                                }
                                int remainingSkips = status.Skip - (first == 1 - s ? 0 : 1);
                                double remainingHp = me.Hp - (goesFirst ? 0 : incoming.Max);
                                bool wakesImmediately = status.WakeOnHit && effect.At == "pre" && skill.Power > 0;
                                // 控制必须能留到下一次行动，且本次施放前仍存活，才有下回合保命收益。
                                if (remainingSkips > 0 && remainingHp > 0 && !wakesImmediately && incoming.Max >= remainingHp)
                                    benefit += incoming.Max / me.MaxHp + 0.6;
                            }
                        }
                        else if (!foe.HasStatus(effect.Status) && !foe.Fields.Any(f => b.Db.Fields[f.Id].ImmuneWeak)) benefit = weights.WeakStatusScore;
                        break;
                    }
                    case "STAGE":
                    {
                        if (effect.Target == "self" && me.HpRatio > weights.StageMinHpRatio && threat < weights.StageMaxThreatRatio)
                        {
                            benefit = weights.StageScore * effect.Changes.Count(change => change.Value > 0 && me.Stages[change.Key] < weights.StageBoostTarget &&
                                ((change.Key != "atk" && change.Key != "spa") || HardActions(b, s).Any(a =>
                                HardSkill(b, s, a) != null && HardSkill(b, s, a).Power > 0 &&
                                HardSkill(b, s, a).Cat == (change.Key == "atk" ? "interact" : "talent"))));
                        }
                        else if (effect.Target != "self" && skill.Power == 0) benefit = weights.FoeStageScore;
                        benefit += HardProtectionBonus(b, s, effect, node, action, incoming.Max);
                        break;
                    }
                    case "FIELD":
                    {
                        var target = effect.Target == "self" ? me : foe;
                        if (target.HasField(effect.Field)) break;
                        benefit = FieldValue(b, s, effect, threat, goesFirst, weights);
                        benefit += HardProtectionBonus(b, s, effect, node, action, incoming.Max);
                        break;
                    }
                    case "DISPEL_TURNS":
                        benefit = foe.Fields.Where(f => b.Db.Fields[f.Id].Dispel)
                            .Sum(f => b.Db.Fields[f.Id].PreventSwitch ? weights.DispelTrapScore : weights.DispelFieldScore);
                        break;
                    case "DISPEL_BOOST": benefit = weights.DispelBoostScore * foe.Stages.Values.Count(n => n > 0); break;
                    case "STEAL_BOOST":
                    {
                        if (effect.Target != "foe") break;
                        foreach (var stage in foe.Stages.Where(change => change.Value > 0))
                            benefit += weights.StealBoostBaseScore + weights.StealBoostPerStageScore * Math.Min(stage.Value, b.StageLimit - me.Stages[stage.Key]);
                        benefit += HardProtectionBonus(b, s, effect, node, action, incoming.Max);
                        break;
                    }
                    case "CLEANSE": if (me.Statuses.Any(x => b.Db.Statuses[x.Id].Cleanse)) benefit = weights.CleanseScore; break;
                    case "ADD_MARK":
                    {
                        var field = b.Db.Fields[effect.Field];
                        var mark = me.FieldOf(effect.Field);
                        benefit = 0.09 * Math.Min(effect.N, Math.Max(0, field.MaxStacks - (mark == null ? 0 : mark.Stacks)));
                        break;
                    }
                }
                score += benefit * probability;
            }
            score += StylePreference(b, s, action);
            if (!goesFirst && incoming.Min >= me.Hp) score *= 0.08;
            return score;
        }

        static double HardProtectionBonus(Battle b, int s, EffectDef effect, string node, BattleAction action, double before)
        {
            if (before <= 0) return 0;
            var after = b.CopyForHardAi();
            after.ApplyHardAiEffect(s, effect, node);
            double nextHit = HardMaxHit(after, 1 - s, action);
            double saved = Math.Max(0, before - nextHit);
            double bonus = saved / after.Active(s).MaxHp;
            if (before >= b.Active(s).Hp && nextHit < b.Active(s).Hp) bonus += 0.6;
            var attack = HardBestAttack(b, s, BattleAction.Pass());
            var enemy = HardThreatAttack(b, 1 - s, attack);
            double myHits = HardHitsToKo(b, 1 - s, HardPairDamage(b, s, attack, enemy).Min);
            double beforeHits = HardHitsToKo(b, s, before);
            double afterHits = HardHitsToKo(after, s, nextHit);
            if (beforeHits <= myHits && afterHits > myHits && !double.IsInfinity(myHits)) bonus += 0.3;
            return bonus;
        }

        static int ChooseHardReplacement(Battle live, int s)
        {
            var candidates = new List<HardOption>();
            foreach (int index in Enumerable.Range(0, live.Sides[s].Team.Count).Where(i => !live.Sides[s].Team[i].Fainted && i != live.Sides[s].Active))
            {
                var b = live.CopyForHardAi();
                b.SwitchForHardAi(s, index, true);
                b.PrepareHardAiTurn();
                var attack = HardBestAttack(b, s, BattleAction.Pass());
                var enemy = HardThreatAttack(b, 1 - s, attack);
                double hit = HardMaxHit(b, 1 - s, attack);
                var damage = HardPairDamage(b, s, attack, enemy);
                double survival = (b.Active(s).Hp - hit) / b.Active(s).MaxHp;
                double offense = Math.Min(1.5, damage.Expected / Math.Max(1, b.Active(1 - s).Hp));
                double score = survival + offense * 1.1;
                if (survival > 0) score += 1;
                if (damage.Guaranteed && damage.Min >= b.Active(1 - s).Hp && HardFirst(b, s, attack, enemy) == s) score += 0.8;
                candidates.Add(new HardOption { Action = BattleAction.SwitchTo(index), Score = score });
            }
            return HardPick(live, candidates, c => c.Score).Action.Index;
        }
    }

    public sealed partial class Battle
    {
        // 评估副本没有开场和随机结算，定义只读共享，所有可变战斗数据单独持有。
        Battle(Battle source)
        {
            Db = source.Db;
            Seed = source.Seed;
            rng = new Rng(0);
            Turn = source.Turn;
            Phase = source.Phase;
            Winner = source.Winner;
            CopyTerminationFrom(source);
            leadPickAfterBan = source.leadPickAfterBan;
            modifiers = new List<string>(source.modifiers).AsReadOnly();
            Record = new BattleRecord
            {
                Version = source.Record.Version, ContentHash = source.Record.ContentHash,
                Seed = source.Record.Seed, PackId = source.Record.PackId, LeadPick = source.Record.LeadPick,
                BanOne = source.Record.BanOne,
                Modifiers = source.Record.Modifiers == null ? null : new List<string>(source.Record.Modifiers),
                Names = source.Record.Names == null ? null : source.Record.Names.ToArray(),
                Teams = source.Record.Teams.Select(team => (IList<string>)new List<string>(team)).ToArray()
            };
            for (int s = 0; s < Sides.Length; s++)
            {
                Need[s] = source.Need[s];
                ActedFirst[s] = source.ActedFirst[s];
                leadChoices[s] = source.leadChoices[s];
                banChoices[s] = source.banChoices[s];
                Sides[s] = new SideState { Name = source.Sides[s].Name, Active = source.Sides[s].Active };
                foreach (var fighter in source.Sides[s].Team)
                {
                    Sides[s].Team.Add(new Fighter
                    {
                        Def = fighter.Def, Slot = fighter.Slot, Side = fighter.Side, Hp = fighter.Hp, MaxHp = fighter.MaxHp,
                        EnteredTurn = fighter.EnteredTurn, Fainted = fighter.Fainted,
                        LastSkillIndex = fighter.LastSkillIndex,
                        FirstControlImmunityUsed = fighter.FirstControlImmunityUsed, EndureUsed = fighter.EndureUsed,
                        Stages = new Dictionary<string, int>(fighter.Stages),
                        Skills = fighter.Skills.Select(k => new SkillSlot { Id = k.Id, Pp = k.Pp, Max = k.Max, Fifth = k.Fifth }).ToList(),
                        Statuses = fighter.Statuses.Select(k => new StatusInst { Id = k.Id, Left = k.Left }).ToList(),
                        Fields = fighter.Fields.Select(k => new FieldInst { Id = k.Id, Turns = k.Turns, Stacks = k.Stacks }).ToList()
                    });
                }
            }
        }

        internal Battle CopyForHardAi() { return new Battle(this); }

        internal bool HardAiPreEffectsInterrupt(int side, SkillDef skill, string node)
        {
            if (!skill.Effects.Any(effect => effect.At == "pre" &&
                (effect.Code == "FIXED_DAMAGE" || effect.Code == "RECOIL"))) return false;
            var state = CopyForHardAi();
            var context = new Ctx { S = side, P = state.Active(side), F = state.Active(1 - side), Skill = skill, Node = node };
            foreach (var effect in skill.Effects.Where(effect => effect.At == "pre"))
            {
                string why;
                if (effect.Chance <= 0 || !state.CondMet(context.P, context.F, node, effect.Cond, out why)) continue;
                // 只证明确定的中断；概率分支不能代替真实对局预先掷骰。
                if (effect.Chance < 100) return false;
                state.RunEffect(context, effect);
                if (context.Interrupted) return true;
                if (state.Phase == Phase.Over) return false;
            }
            return false;
        }

        internal void PrepareHardAiTurn()
        {
            for (int s = 0; s < Sides.Length; s++) PersonaTurnStart(s, Active(s));
        }

        internal void SwitchForHardAi(int side, int index, bool replacement)
        {
            SwitchTo(side, index, replacement ? Node.Replace : Node.Switch);
            Phase = Phase.Choose;
        }

        internal Battle AdvanceHardAiSwitchTurn(int side)
        {
            ActedFirst[side] = false;
            ActedFirst[1 - side] = true;
            int speed = StatVal(Active(side), "spd"), otherSpeed = StatVal(Active(1 - side), "spd");
            if (speed == otherSpeed)
            {
                // 回末同速仍有两种顺序；不替真正的随机结算预选有利的一种。
                var other = CopyForHardAi();
                FinishHardAiSwitchTurn(side);
                other.FinishHardAiSwitchTurn(1 - side);
                return other.Active(side).Hp < Active(side).Hp ||
                    (other.Active(side).Hp == Active(side).Hp && other.Active(1 - side).Hp > Active(1 - side).Hp)
                    ? other : this;
            }
            FinishHardAiSwitchTurn(speed > otherSpeed ? side : 1 - side);
            return this;
        }

        void FinishHardAiSwitchTurn(int first)
        {
            TurnEndFor(first);
            TurnEndFor(1 - first);
            Turn++;
            PrepareHardAiTurn();
        }

        internal void ApplyHardAiEffect(int side, EffectDef effect, string node)
        {
            var assumed = new EffectDef
            {
                At = effect.At, Code = effect.Code, Target = effect.Target, Status = effect.Status,
                Field = effect.Field, Chance = 100, Turns = effect.Turns, N = effect.N, Frac = effect.Frac,
                Mul = effect.Mul, Per = effect.Per, Consume = effect.Consume, Changes = effect.Changes, Cond = effect.Cond
            };
            RunEffect(new Ctx { S = side, P = Active(side), F = Active(1 - side), Skill = Db.Struggle, Node = node }, assumed);
        }
    }
}
