using System;

namespace StreamDuel.Core
{
    public sealed class BattleOptions
    {
        public uint? Seed;
        public bool LeadPick;
        public bool BanOne;
    }

    public sealed partial class Battle
    {
        readonly int[] banChoices = { -1, -1 };
        readonly bool leadPickAfterBan;

        public bool HasChosenBan(int side)
        {
            if (side < 0 || side >= Sides.Length) throw new ArgumentOutOfRangeException(nameof(side), "对战方必须是 0 或 1");
            return banChoices[side] >= 0;
        }

        public void ChooseBan(int side, int foeIndex)
        {
            if (side < 0 || side >= Sides.Length) throw new ArgumentOutOfRangeException(nameof(side), "对战方必须是 0 或 1");
            if (Phase != Phase.Ban) throw new InvalidOperationException("现在不是选择禁用的阶段");
            if (banChoices[side] >= 0) throw new InvalidOperationException("这一方已经选择禁用");
            if (foeIndex < 0 || foeIndex >= Sides[1 - side].Team.Count)
                throw new ArgumentOutOfRangeException(nameof(foeIndex), "禁用位置不在对手队伍中");

            Record.Events.Add(new BattleRecordEvent { Type = BattleRecordEventType.Ban, Side = side, To = foeIndex, RngState = rng.State });
            banChoices[side] = foeIndex;
            if (banChoices[0] < 0 || banChoices[1] < 0) return;

            // 双方确认后统一移除，第二位选择者看到的仍是原始六人阵容。
            var banned = new Fighter[2];
            for (int s = 0; s < Sides.Length; s++)
            {
                var team = Sides[s].Team;
                int index = banChoices[1 - s];
                banned[s] = team[index];
                team.RemoveAt(index);
                for (int i = 0; i < team.Count; i++) team[i].Slot = i + 1;
                Sides[s].Active = 0;
            }
            for (int s = 0; s < Sides.Length; s++)
                Add(Node.Start, Sides[s].Name + " 禁用了 " + Sides[1 - s].Name + " 的「" + banned[1 - s].Name + "」", "ban", s);

            if (leadPickAfterBan) Phase = Phase.Lead;
            else StartBattle();
        }
    }
}
