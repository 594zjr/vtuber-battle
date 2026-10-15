using System;

namespace StreamDuel.Core
{
    public sealed partial class Battle
    {
        internal void ResolveTurnWithAiTakeover(BattleAction a0, BattleAction a1, int mask)
        {
            if (mask < 0 || mask > 3)
                throw new ArgumentOutOfRangeException(nameof(mask), "电脑代打标记必须是 0 至 3 的整数");
            if (mask == 0)
            {
                ResolveTurn(a0, a1);
                return;
            }
            if (Phase != Phase.Choose) throw new InvalidOperationException("现在不是选择行动的阶段");

            int recordIndex = Record.Events.Count;
            for (int side = 0; side < 2; side++)
                if ((mask & (1 << side)) != 0)
                    Add(Node.TurnStart, Sides[side].Name + " 本回合由电脑代打", "info", side);
            ResolveTurn(a0, a1);
            Record.Events[recordIndex].AiTakeoverMask = mask;
        }

        internal void ReplaceWithAiTakeover(int side, int to, bool takeover)
        {
            if (!takeover)
            {
                Replace(side, to);
                return;
            }
            if (side < 0 || side > 1) throw new ArgumentOutOfRangeException(nameof(side));
            if (Phase != Phase.Replace || !Need[side]) throw new InvalidOperationException("这一方现在不需要补位");
            var state = Sides[side];
            if (to == state.Active || to < 0 || to >= state.Team.Count || state.Team[to].Fainted)
                throw new InvalidOperationException("不能派出这位主播");

            // 先验证选择再记录来源，拒绝的补位不能污染日志或战绩；终局日志仍保持最后一条。
            int recordIndex = Record.Events.Count;
            Add(Node.Replace, state.Name + " 由电脑代打选择补位", "info", side);
            Replace(side, to);
            Record.Events[recordIndex].AiTakeoverMask = 1 << side;
        }
    }
}
