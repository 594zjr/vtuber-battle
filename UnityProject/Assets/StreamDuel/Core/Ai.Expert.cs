using System;
using System.Collections.Generic;
using System.Linq;

namespace StreamDuel.Core
{
    public static partial class Ai
    {
        const int ExpertSamples = 3;
        const int ExpertBeam = 3;

        sealed class ExpertOption
        {
            public BattleAction Action;
            public double Score;
            public double Preference;
            public int CompletedSamples;
            public readonly List<Battle> Outcomes = new List<Battle>();
        }

        static Battle ExpertRoot(Battle live)
        {
            var root = live.CopyForSimulation();
            return root;
        }

        static void ExpertValidate(Battle b, int s)
        {
            if (b == null) throw new ArgumentNullException(nameof(b));
            if (s < 0 || s >= b.Sides.Length) throw new ArgumentOutOfRangeException(nameof(s), "对战方必须是 0 或 1");
        }

        static List<BattleAction> ExpertActions(Battle b, int s)
        {
            var legal = b.LegalFor(s);
            var actions = legal.Skills.Where(option => option.Usable)
                .Select(option => BattleAction.Skill(option.Index)).ToList();
            if (legal.Struggle) actions.Add(new BattleAction { Type = ActionType.Struggle });
            if (legal.Controlled != null || actions.Count == 0) actions.Add(BattleAction.Pass());
            // 控制或 PP 用尽不会禁止换人；锁麦是否允许换人由引擎提供。
            actions.AddRange(legal.Switches.Select(BattleAction.SwitchTo));
            return actions;
        }

        static BattleAction ChooseExpert(Battle live, int s)
        {
            return ChooseExpert(live, s, ExpertBudget.Default).Action;
        }

        public static ExpertDecision ChooseExpert(Battle live, int s, ExpertBudget budget)
        {
            ExpertValidate(live, s);
            if (live.Phase != Phase.Choose) throw new InvalidOperationException("现在不是选择行动的阶段");
            var search = new ExpertSearch(budget);
            var root = ExpertRoot(live);
            var options = ExpertActions(root, s).Select(action => new ExpertOption { Action = action }).ToList();

            // 每个候选使用相同的采样起点，避免候选遍历顺序改变随机收益。
            for (int sample = 0; sample < ExpertSamples; sample++)
            {
                var start = root.CopyForSimulation();
                for (int i = 0; i < sample * 11; i++) start.Random();
                var opponent = ChooseHard(start.CopyForSimulation(), 1 - s);
                foreach (var option in options)
                {
                    if (!search.TryVisit()) return ExpertPartial(live, root, s, options, search, true);
                    var next = start.CopyForSimulation();
                    ExpertResolve(next, s, option.Action, opponent);
                    if (!ExpertFillReplacements(next, search))
                        return ExpertPartial(live, root, s, options, search, true);
                    option.Score += ExpertValue(next, s) / ExpertSamples;
                    option.Outcomes.Add(next);
                    option.CompletedSamples++;
                }
            }

            // 保留并列候选的原有顺序；节点预算同时约束并列扩展和补位。
            foreach (var option in options)
            {
                option.Preference = StylePreference(root, s, option.Action) * 0.25;
                option.Score += option.Preference;
            }
            var finalists = options.OrderByDescending(option => option.Score).Take(ExpertBeam).ToList();
            double cutoff = finalists.Last().Score;
            foreach (var option in options.Except(finalists).Where(option => Math.Abs(option.Score - cutoff) <= 1e-9))
                finalists.Add(option);
            var expanded = new List<ExpertOption>();
            foreach (var option in finalists)
            {
                double total = 0;
                foreach (var next in option.Outcomes)
                {
                    if (!ExpertRollout(next, s, search, out double value))
                        return search.Finish(HardPick(live, expanded.Count > 0 ? expanded : finalists,
                            candidate => candidate.Score).Action);
                    total += value;
                }
                double future = total / option.Outcomes.Count;
                option.Score = option.Score * 0.3 + future * 0.7 + option.Preference * 0.7;
                expanded.Add(option);
            }
            return search.Finish(HardPick(live, finalists, option => option.Score).Action);
        }

        static int ChooseExpertReplacement(Battle live, int s)
        {
            return ChooseExpertReplacement(live, s, ExpertBudget.Default).Action.Index;
        }

        public static ExpertDecision ChooseExpertReplacement(Battle live, int s, ExpertBudget budget)
        {
            ExpertValidate(live, s);
            if (live.Phase != Phase.Replace || !live.Need[s])
                throw new InvalidOperationException("这一方现在不需要补位");
            var search = new ExpertSearch(budget);
            var root = ExpertRoot(live);
            var options = new List<ExpertOption>();
            for (int index = 0; index < root.Sides[s].Team.Count; index++)
            {
                if (index == root.Sides[s].Active || root.Sides[s].Team[index].Fainted) continue;
                var option = new ExpertOption { Action = BattleAction.SwitchTo(index) };
                options.Add(option);
                for (int sample = 0; sample < ExpertSamples; sample++)
                {
                    if (!search.TryVisit()) return ExpertPartial(live, root, s, options, search, false);
                    var next = root.CopyForSimulation();
                    for (int i = 0; i < sample * 11; i++) next.Random();
                    next.Replace(s, index);
                    if (!ExpertFillReplacements(next, search) || !ExpertRollout(next, s, search, out double future))
                        return ExpertPartial(live, root, s, options, search, false);
                    option.Score += (ExpertValue(next, s) * 0.3 + future * 0.7) / ExpertSamples;
                    option.CompletedSamples++;
                }
            }
            return search.Finish(HardPick(live, options, option => option.Score).Action);
        }

        static ExpertDecision ExpertPartial(Battle live, Battle root, int s,
            List<ExpertOption> options, ExpertSearch search, bool includePreference)
        {
            // 不将缺少的样本记为零，也不把未完成补位的中间状态当成候选结果。
            var ready = options.Where(option => option.CompletedSamples == ExpertSamples).ToList();
            if (ready.Count == 0) ready = options.Where(option => option.CompletedSamples > 0).ToList();
            if (ready.Count == 0) return search.Finish(options[0].Action);
            foreach (var option in ready)
            {
                option.Score *= (double)ExpertSamples / option.CompletedSamples;
                if (includePreference) option.Score += StylePreference(root, s, option.Action) * 0.25;
            }
            return search.Finish(HardPick(live, ready, option => option.Score).Action);
        }

        static void ExpertResolve(Battle b, int s, BattleAction mine, BattleAction theirs)
        {
            if (s == 0) b.ResolveTurn(mine, theirs);
            else b.ResolveTurn(theirs, mine);
        }

        static bool ExpertFillReplacements(Battle b, ExpertSearch search)
        {
            for (int side = 0; side < 2 && b.Phase == Phase.Replace; side++)
                if (b.Need[side])
                {
                    if (!search.TryVisit()) return false;
                    b.Replace(side, ChooseHardReplacement(b, side));
                }
            return true;
        }

        static bool ExpertRollout(Battle after, int s, ExpertSearch search, out double value)
        {
            value = 0;
            if (after.Phase == Phase.Over)
            {
                value = ExpertValue(after, s);
                return true;
            }
            if (!search.TryVisit()) return false;
            var next = after.CopyForSimulation();
            var a0 = ChooseHard(next, 0);
            var a1 = ChooseHard(next, 1);
            next.ResolveTurn(a0, a1);
            if (!ExpertFillReplacements(next, search)) return false;
            value = ExpertValue(next, s);
            return true;
        }

        static double ExpertValue(Battle b, int s)
        {
            if (b.Phase == Phase.Over) return b.Winner == -1 ? 0 : b.Winner == s ? 1000 : -1000;
            return ExpertSideValue(b, s) - ExpertSideValue(b, 1 - s);
        }

        static double ExpertSideValue(Battle b, int s)
        {
            double value = 0;
            foreach (var fighter in b.Sides[s].Team)
            {
                if (fighter.Fainted) continue;
                // 接近回合上限时，终局按人气比例判定，逐渐降低单纯存活的人头奖励。
                double alive = 2.5 * Math.Min(1, (Battle.TurnCap - b.Turn) / 10.0);
                value += alive + 2 * fighter.HpRatio;
                bool active = fighter == b.Active(s);
                foreach (var status in fighter.Statuses)
                {
                    var definition = b.Db.Statuses[status.Id];
                    value -= active
                        ? definition.IsControl ? 0.3 * Math.Min(2, status.Left) : definition.Dot * Math.Min(3, status.Left) * 1.5
                        : 0.04;
                }
                if (!active) continue;

                bool freePp = b.LegalFor(s).FreePp;
                bool interact = fighter.Skills.Any(slot => (slot.Pp > 0 || freePp) &&
                    b.Db.Skills[slot.Id].Power > 0 && b.Db.Skills[slot.Id].Cat == "interact");
                bool talent = fighter.Skills.Any(slot => (slot.Pp > 0 || freePp) &&
                    b.Db.Skills[slot.Id].Power > 0 && b.Db.Skills[slot.Id].Cat == "talent");
                foreach (var stage in fighter.Stages)
                {
                    if (stage.Key == "atk" && !interact || stage.Key == "spa" && !talent) continue;
                    value += Math.Max(-3, Math.Min(3, stage.Value)) * 0.1 * (0.5 + fighter.HpRatio);
                }
                foreach (var field in fighter.Fields)
                {
                    var definition = b.Db.Fields[field.Id];
                    double turns = field.Turns < 0 ? 2 : Math.Min(2, field.Turns);
                    value += turns * (definition.Heal - definition.Dot) * 1.5;
                    value += turns * (1 - definition.DmgTakenMul) * 0.2;
                    if (talent) value += (definition.TalentMul - 1) * 0.18;
                    value += definition.Prio * 0.08;
                    if (definition.ImmuneControl) value += 0.06;
                    value += field.Stacks * 0.035;
                }
                if (b.PersonaOf(fighter).Code == "T_ENDURE" && !fighter.EndureUsed) value += 0.12;
                if (fighter.Skills.All(slot => slot.Pp <= 0) && !freePp) value -= 0.25;
            }
            return value;
        }
    }
}
