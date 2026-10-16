using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using StreamDuel.Core;

namespace StreamDuel.Bench
{
    internal sealed class ExpertBenchmarkResult
    {
        public int DefaultMaxNodes { get; } = ExpertBudget.DefaultMaxNodes;
        public int UnlimitedMaxNodes { get; } = ExpertBudget.Unlimited.MaxNodes;
        public int DiagnosticMaxNodes { get; } = 8;
        public string NodeDefinition { get; }
        public string Workload { get; }
        public string ComplexConstruction { get; }
        public List<SnapshotResult> Snapshots { get; }
        public List<ExpertCaseResult> Cases { get; }
        public List<ExpertCaseResult> Diagnostics { get; }

        internal ExpertBenchmarkResult(string nodeDefinition, string workload, string construction,
            List<SnapshotResult> snapshots, List<ExpertCaseResult> cases, List<ExpertCaseResult> diagnostics)
        {
            NodeDefinition = nodeDefinition;
            Workload = workload;
            ComplexConstruction = construction;
            Snapshots = snapshots;
            Cases = cases;
            Diagnostics = diagnostics;
        }
    }

    internal sealed class ExpertCaseResult
    {
        public string Mode { get; }
        public int MaxNodes { get; }
        public string Snapshot { get; }
        public int Side { get; }
        public string ActionType { get; }
        public int ActionIndex { get; }
        public int SimulatedNodes { get; }
        public bool BudgetExhausted { get; }
        public string PostDecisionHash { get; }
        public MetricResult Timing { get; }

        internal ExpertCaseResult(string mode, int maxNodes, string snapshot, int side,
            ExpertDecision decision, ulong postDecisionHash, MetricResult timing)
        {
            Mode = mode;
            MaxNodes = maxNodes;
            Snapshot = snapshot;
            Side = side;
            ActionType = decision.Action.Type.ToString();
            ActionIndex = decision.Action.Index;
            SimulatedNodes = decision.SimulatedNodes;
            BudgetExhausted = decision.BudgetExhausted;
            PostDecisionHash = postDecisionHash.ToString("x16", CultureInfo.InvariantCulture);
            Timing = timing;
        }
    }

    internal static class ExpertBenchmark
    {
        const string NodeDefinition = "每次真正执行的模拟 ResolveTurn 或 Replace 在调用前扣 1 节点；"
            + "克隆、评分和 Hard 启发式不计节点。无法取得下一节点时停止模拟，未完成补位的候选不评分；"
            + "BudgetExhausted 仅在仍需转换但预算不足时为 true，恰好用完但已完成不算截断。";
        const string Construction = "构造压力局面，不宣称可从当前阵容的合法行动完整重放：克隆原开局，保持 Choose、种子、阵容、技能和 PP；"
            + "全部 12 人均存活，HP=floor(MaxHp*(0.45+0.08*((槽位+方号)%6)))；"
            + "五项等级按 StatKeys 顺序设为 ((槽位+方号+属性序号)%5)-2；"
            + "双方场上追加 flame(3)、hater(2)、siphon(4)，并用 shield(2)、filter(3)、sub(4)、charge(2)、"
            + "blackhist(-1)、hype(-1,3层)、mirror(3) 替换直播间效果。括号为剩余回合，内容定义不修改。";

        internal static ExpertBenchmarkResult Run(List<Battle> originalPositions, List<MetricResult> metrics, bool quick)
        {
            var positions = originalPositions.Concat(new[] { MakeComplex(originalPositions[0]) }).ToList();
            var labels = new[] { "开局", "中盘", "后半程", "构造复杂局面" };
            var snapshots = positions.Select((battle, index) => new SnapshotResult(labels[index], battle)).ToList();
            var cases = new List<ExpertCaseResult>();
            var modes = new[] { "default_budget", "unlimited" };
            var budgets = new[] { ExpertBudget.Default, ExpertBudget.Unlimited };
            int countPerCase = quick ? 4 : 40;

            for (int modeIndex = 0; modeIndex < modes.Length; modeIndex++)
            {
                string mode = modes[modeIndex];
                var budget = budgets[modeIndex];
                var samples = new List<double>();
                for (int positionIndex = 0; positionIndex < positions.Count; positionIndex++)
                {
                    var position = positions[positionIndex];
                    if (position.Phase != Phase.Choose)
                        throw new InvalidOperationException("专家基准局面必须处于 Choose 阶段。");
                    for (int side = 0; side < 2; side++)
                    {
                        // 预热同时建立结果基线；哈希和重复性校验都在计时之外。
                        var warm = position.Clone();
                        var expected = Ai.ChooseExpert(warm, side, budget);
                        Validate(position, side, budget, expected);
                        ulong expectedHash = warm.StateHash();
                        if (modeIndex == 0)
                        {
                            var normalEntry = position.Clone();
                            var action = Ai.Choose(normalEntry, side, AiLevel.Expert);
                            if (!SameAction(action, expected.Action) || normalEntry.StateHash() != expectedHash)
                                throw new InvalidOperationException("默认专家入口与显式默认预算不一致。");
                        }
                        var caseSamples = new double[countPerCase];
                        for (int sample = 0; sample < countPerCase; sample++)
                        {
                            var input = position.Clone();
                            long started = Stopwatch.GetTimestamp();
                            var decision = Ai.ChooseExpert(input, side, budget);
                            long finished = Stopwatch.GetTimestamp();
                            caseSamples[sample] = (finished - started) * (1000.0 / Stopwatch.Frequency);
                            Validate(position, side, budget, decision);
                            if (!SameAction(expected.Action, decision.Action)
                                || expected.SimulatedNodes != decision.SimulatedNodes
                                || expected.BudgetExhausted != decision.BudgetExhausted
                                || expectedHash != input.StateHash())
                                throw new InvalidOperationException("专家预算结果不可复现：" + labels[positionIndex] + " / " + side + " / " + mode);
                        }
                        samples.AddRange(caseSamples);
                        string caseId = "ai.expert." + mode + ".position" + positionIndex + ".side" + side;
                        var timing = new MetricResult(caseId, labels[positionIndex] + " / " + side, caseSamples, false);
                        cases.Add(new ExpertCaseResult(mode, budget.MaxNodes, labels[positionIndex], side, expected, expectedHash, timing));
                    }
                }
                string title = modeIndex == 0 ? "专家显式默认预算（" + budget.MaxNodes + " 节点）" : "专家无预算对照";
                metrics.Add(new MetricResult("ai.expert." + mode, title, samples.ToArray(), false));
            }

            var diagnostics = new List<ExpertCaseResult>();
            var smallBudget = new ExpertBudget(8);
            for (int index = 0; index < positions.Count; index++)
            {
                for (int side = 0; side < 2; side++)
                {
                    var first = positions[index].Clone();
                    var second = positions[index].Clone();
                    var firstDecision = Ai.ChooseExpert(first, side, smallBudget);
                    var secondDecision = Ai.ChooseExpert(second, side, smallBudget);
                    Validate(positions[index], side, smallBudget, firstDecision);
                    Validate(positions[index], side, smallBudget, secondDecision);
                    if (!SameAction(firstDecision.Action, secondDecision.Action)
                        || firstDecision.SimulatedNodes != secondDecision.SimulatedNodes
                        || firstDecision.BudgetExhausted != secondDecision.BudgetExhausted
                        || first.StateHash() != second.StateHash())
                        throw new InvalidOperationException("小预算专家诊断不可复现。");
                    diagnostics.Add(new ExpertCaseResult("diagnostic_only", smallBudget.MaxNodes,
                        labels[index], side, firstDecision, first.StateHash(), null));
                }
            }

            string workload = "专家追加对照使用原三快照和一个明示构造的复杂局面，均为 Choose 阶段，双方等量采样；"
                + "显式默认预算 " + ExpertBudget.DefaultMaxNodes + " 节点，无预算为 int.MaxValue。"
                + "每种预算的每局面每方预热 1 次，快速/完整模式分别采样 4/40 次（合计 32/320 次）；"
                + "仅计时 Ai.ChooseExpert，克隆、合法性与动作/节点/截断/决策后哈希重复性校验在计时外；"
                + "同一输入重复采样必须完全一致。另对每局面每方用 8 节点重复 2 次诊断，不计时，不替代默认预算指标。"
                + "原 ai.expert 仍只测原三快照的默认 Ai.Choose。节点定义：" + NodeDefinition;
            return new ExpertBenchmarkResult(NodeDefinition, workload, Construction, snapshots, cases, diagnostics);
        }

        static Battle MakeComplex(Battle opening)
        {
            var battle = opening.Clone();
            for (int side = 0; side < battle.Sides.Length; side++)
            {
                for (int slot = 0; slot < battle.Sides[side].Team.Count; slot++)
                {
                    var fighter = battle.Sides[side].Team[slot];
                    fighter.Hp = Math.Max(1, (int)(fighter.MaxHp * (0.45 + 0.08 * ((slot + side) % 6))));
                    for (int stat = 0; stat < ContentDb.StatKeys.Length; stat++)
                        fighter.Stages[ContentDb.StatKeys[stat]] = (slot + side + stat) % 5 - 2;
                }
                var active = battle.Active(side);
                active.Statuses.Add(new StatusInst { Id = "flame", Left = 3 });
                active.Statuses.Add(new StatusInst { Id = "hater", Left = 2 });
                active.Statuses.Add(new StatusInst { Id = "siphon", Left = 4 });
                active.Fields.Clear();
                active.Fields.Add(new FieldInst { Id = "shield", Turns = 2 });
                active.Fields.Add(new FieldInst { Id = "filter", Turns = 3 });
                active.Fields.Add(new FieldInst { Id = "sub", Turns = 4 });
                active.Fields.Add(new FieldInst { Id = "charge", Turns = 2 });
                active.Fields.Add(new FieldInst { Id = "blackhist", Turns = -1 });
                active.Fields.Add(new FieldInst { Id = "hype", Turns = -1, Stacks = 3 });
                active.Fields.Add(new FieldInst { Id = "mirror", Turns = 3 });
            }
            return battle;
        }

        static bool SameAction(BattleAction left, BattleAction right)
        {
            return left.Type == right.Type && left.Index == right.Index;
        }

        static void Validate(Battle input, int side, ExpertBudget budget, ExpertDecision decision)
        {
            if (decision.SimulatedNodes < 0 || decision.SimulatedNodes > budget.MaxNodes)
                throw new InvalidOperationException("专家报告的模拟节点数超出预算。");
            var legal = input.LegalFor(side);
            var action = decision.Action;
            bool valid = action.Type == ActionType.Pass
                || action.Type == ActionType.Skill && legal.Skills.Any(skill => skill.Index == action.Index && skill.Usable)
                || action.Type == ActionType.Switch && legal.Switches.Contains(action.Index)
                || action.Type == ActionType.Struggle && legal.Struggle
                || action.Type == ActionType.PassControlled && legal.Controlled != null;
            if (!valid) throw new InvalidOperationException("专家预算返回了非法行动。");
        }
    }
}
