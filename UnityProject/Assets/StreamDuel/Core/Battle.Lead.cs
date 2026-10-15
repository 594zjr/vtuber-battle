using System;

namespace StreamDuel.Core
{
    public sealed partial class Battle
    {
        readonly int[] leadChoices = { -1, -1 };

        public bool HasChosenLead(int side)
        {
            if (side < 0 || side >= Sides.Length) throw new ArgumentOutOfRangeException(nameof(side), "对战方必须是 0 或 1");
            return leadChoices[side] >= 0;
        }

        public void ChooseLead(int side, int index)
        {
            if (side < 0 || side >= Sides.Length) throw new ArgumentOutOfRangeException(nameof(side), "对战方必须是 0 或 1");
            if (Phase != Phase.Lead) throw new InvalidOperationException("现在不是选择首发的阶段");
            if (leadChoices[side] >= 0) throw new InvalidOperationException("这一方已经选择首发");
            if (index < 0 || index >= Sides[side].Team.Count) throw new ArgumentOutOfRangeException(nameof(index), "首发位置不在队伍中");

            Record.Events.Add(new BattleRecordEvent { Type = BattleRecordEventType.Lead, Side = side, To = index, RngState = rng.State });
            leadChoices[side] = index;
            if (leadChoices[0] < 0 || leadChoices[1] < 0) return;

            // 双方确认后才统一重排，避免先选择的一方改变另一方看到的阵容顺序。
            for (int s = 0; s < Sides.Length; s++)
            {
                var team = Sides[s].Team;
                var lead = team[leadChoices[s]];
                team.RemoveAt(leadChoices[s]);
                team.Insert(0, lead);
                for (int i = 0; i < team.Count; i++) team[i].Slot = i + 1;
                Sides[s].Active = 0;
            }
            // 日志快照沿用最终队伍顺序，逐行回放才能把人气对应到正确的主播。
            for (int s = 0; s < Sides.Length; s++)
                Add(Node.Start, Sides[s].Name + " 选择「" + Active(s).Name + "」首发，移至 1 号位", "lead", s);
            StartBattle();
        }

        void StartBattle()
        {
            Phase = Phase.Choose;
            Add(Node.Start, "对决开始", "turn");
            foreach (int s in SpeedOrder()) Enter(s, Node.Start);
        }
    }
}
