using System;

namespace StreamDuel.Core
{
    public sealed class ExpertBudget
    {
        public const int DefaultMaxNodes = 128;
        public static ExpertBudget Default => new ExpertBudget();
        public static ExpertBudget Unlimited => new ExpertBudget(int.MaxValue);
        public int MaxNodes { get; }

        public ExpertBudget(int maxNodes = DefaultMaxNodes)
        {
            if (maxNodes < 0) throw new ArgumentOutOfRangeException(nameof(maxNodes));
            MaxNodes = maxNodes;
        }
    }

    public sealed class ExpertDecision
    {
        public BattleAction Action { get; }
        public int SimulatedNodes { get; }
        public bool BudgetExhausted { get; }

        internal ExpertDecision(BattleAction action, int simulatedNodes, bool budgetExhausted)
        {
            Action = action;
            SimulatedNodes = simulatedNodes;
            BudgetExhausted = budgetExhausted;
        }
    }

    public static partial class Ai
    {
        sealed class ExpertSearch
        {
            readonly int maxNodes;
            int nodes;
            bool exhausted;

            public ExpertSearch(ExpertBudget budget)
            {
                if (budget == null) throw new ArgumentNullException(nameof(budget));
                maxNodes = budget.MaxNodes;
            }

            public bool TryVisit()
            {
                if (nodes == maxNodes)
                {
                    exhausted = true;
                    return false;
                }
                nodes++;
                return true;
            }

            public ExpertDecision Finish(BattleAction action)
            {
                return new ExpertDecision(action, nodes, exhausted);
            }
        }
    }
}
