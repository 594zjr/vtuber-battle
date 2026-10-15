using System;
using System.Collections.Generic;
using System.Linq;

namespace StreamDuel.Core
{
    public sealed partial class Battle
    {
        readonly List<int>[] pickChoices = new List<int>[2];
        readonly List<BattleRecordEvent> pendingPicks = new List<BattleRecordEvent>();

        public int PickCount { get; private set; }

        public bool HasChosenPick(int side)
        {
            ValidatePickSide(side);
            return pickChoices[side] != null;
        }

        /// <summary>选人阶段可选择的原始六人下标；其它阶段返回空列表。</summary>
        public IReadOnlyList<int> AvailablePicks(int side)
        {
            ValidatePickSide(side);
            var available = new List<int>();
            if (Phase == Phase.Pick)
            {
                for (int i = 0; i < 6; i++)
                    if (i != banChoices[1 - side]) available.Add(i);
            }
            return available.AsReadOnly();
        }

        public void ChoosePick(int side, IList<int> indices)
        {
            ValidatePickSide(side);
            if (Phase != Phase.Pick) throw new InvalidOperationException("现在不是选择出战阵容的阶段");
            if (pickChoices[side] != null) throw new InvalidOperationException("这一方已经确认出战阵容");
            if (indices == null) throw new ArgumentNullException(nameof(indices), "出战阵容不能为空");
            var chosen = new List<int>(indices);
            if (chosen.Count != PickCount)
                throw new ArgumentException("必须选择 " + PickCount + " 位出战主播", nameof(indices));
            var seen = new HashSet<int>();
            foreach (int index in chosen)
            {
                if (index < 0 || index >= 6)
                    throw new ArgumentOutOfRangeException(nameof(indices), "出战位置必须是原始六人阵容中的号位");
                if (!seen.Add(index)) throw new ArgumentException("出战阵容不能重复选择同一号位", nameof(indices));
                if (index == banChoices[1 - side]) throw new ArgumentException("被禁用的主播不能出战", nameof(indices));
            }

            // 连回放和随机流检查点都暂存私有，避免第二位选择者从公开记录读出秘密阵容。
            pickChoices[side] = chosen;
            pendingPicks.Add(new BattleRecordEvent
            {
                Type = BattleRecordEventType.Pick, Side = side,
                Indices = new List<int>(chosen), RngState = rng.State
            });
            if (pickChoices[0] == null || pickChoices[1] == null) return;

            for (int s = 0; s < Sides.Length; s++)
            {
                int banned = banChoices[1 - s];
                var positions = pickChoices[s].Select(index => banned >= 0 && index > banned ? index - 1 : index).ToList();
                var team = Sides[s].Team;
                var selected = positions.Select(index => team[index]).ToList();
                // 禁用日志已经存在；过滤掉未出战者，并按同一身份映射调整历史人气和场上位置。
                foreach (var entry in Log)
                {
                    var previousHp = entry.SnapHp[s];
                    entry.SnapHp[s] = positions.Select(index => previousHp[index]).ToArray();
                    int active = positions.IndexOf(entry.SnapActive[s]);
                    entry.SnapActive[s] = active >= 0 ? active : 0;
                }
                team.Clear();
                team.AddRange(selected);
                for (int i = 0; i < team.Count; i++) team[i].Slot = i + 1;
                Sides[s].Active = 0;
            }

            Record.Events.AddRange(pendingPicks);
            pendingPicks.Clear();
            for (int s = 0; s < Sides.Length; s++)
                Add(Node.Start, Sides[s].Name + " 确认出战阵容：" + string.Join("、", Sides[s].Team.Select(f => "「" + f.Name + "」"))
                    + "；由「" + Active(s).Name + "」首发", "pick", s);
            StartBattle();
        }

        static void ValidatePickSide(int side)
        {
            if (side < 0 || side > 1) throw new ArgumentOutOfRangeException(nameof(side), "对战方必须是 0 或 1");
        }

        static void ValidatePickOptions(ContentDb db, IList<string>[] teams, int count)
        {
            if (count != 0 && count != 3 && count != 4)
                throw new ArgumentOutOfRangeException(nameof(BattleOptions.PickCount), "选人人数只能是 0、3 或 4");
            if (count == 0) return;
            if (db == null) throw new ArgumentNullException(nameof(db), "选人需要内容库");
            if (teams == null || teams.Length != 2)
                throw new ArgumentException("选人需要双方各一支合法六人阵容", nameof(teams));
            foreach (var team in teams)
            {
                var errors = TeamCheck.Validate(db, null, team);
                if (errors.Count > 0) throw new ArgumentException(string.Join("；", errors), nameof(teams));
            }
        }

        // 完整克隆及 AI 模拟都需要保留确认状态，但不能共享尚未公开的可变列表。
        void CopyPickStateFrom(Battle source)
        {
            PickCount = source.PickCount;
            for (int s = 0; s < pickChoices.Length; s++)
                pickChoices[s] = source.pickChoices[s] == null ? null : new List<int>(source.pickChoices[s]);
            foreach (var entry in source.pendingPicks)
                pendingPicks.Add(new BattleRecordEvent
                {
                    Type = entry.Type, Side = entry.Side,
                    Indices = new List<int>(entry.Indices), RngState = entry.RngState
                });
        }
    }
}
