using System;
using System.Collections.Generic;

namespace StreamDuel.Core
{
    public sealed class BattleOptions
    {
        public uint? Seed;
        public bool LeadPick;
        public bool BanOne;
        public int BanCount;
        public List<string> Modifiers;

        internal int EffectiveBanCount()
        {
            if (BanCount < 0 || BanCount > 2)
                throw new ArgumentOutOfRangeException(nameof(BanCount), "每方禁用人数只能是 0、1 或 2");
            return BanCount > 0 ? BanCount : BanOne ? 1 : 0;
        }
    }

    public sealed partial class Battle
    {
        readonly int[] banChoices = { -1, -1 };
        readonly int[] secondBanChoices = { -1, -1 };
        readonly int banCount;
        readonly bool leadPickAfterBan;

        public int BanCount { get { return banCount; } }

        public bool HasChosenBan(int side)
        {
            if (side < 0 || side >= Sides.Length) throw new ArgumentOutOfRangeException(nameof(side), "对战方必须是 0 或 1");
            return banCount == 2 ? secondBanChoices[side] >= 0 : banChoices[side] >= 0;
        }

        /// <summary>本方尚可禁用的对手原始位置；全部确认之前队伍不移除、不重排。</summary>
        public IReadOnlyList<int> LegalBanTargets(int side)
        {
            if (side < 0 || side >= Sides.Length) throw new ArgumentOutOfRangeException(nameof(side), "对战方必须是 0 或 1");
            var targets = new List<int>();
            if (Phase == Phase.Ban && !HasChosenBan(side))
                for (int i = 0; i < Sides[1 - side].Team.Count; i++)
                    if (i != banChoices[side] && i != secondBanChoices[side]) targets.Add(i);
            return targets.AsReadOnly();
        }

        public void ChooseBan(int side, int foeIndex)
        {
            if (side < 0 || side >= Sides.Length) throw new ArgumentOutOfRangeException(nameof(side), "对战方必须是 0 或 1");
            if (Phase != Phase.Ban) throw new InvalidOperationException("现在不是选择禁用的阶段");
            if (HasChosenBan(side)) throw new InvalidOperationException("这一方已经选择禁用");
            if (foeIndex < 0 || foeIndex >= Sides[1 - side].Team.Count)
                throw new ArgumentOutOfRangeException(nameof(foeIndex), "禁用位置不在对手队伍中");
            if (banChoices[side] == foeIndex) throw new InvalidOperationException("不能重复禁用同一位主播");

            Record.Events.Add(new BattleRecordEvent { Type = BattleRecordEventType.Ban, Side = side, To = foeIndex, RngState = rng.State });
            if (banCount == 2)
            {
                if (banChoices[side] < 0) banChoices[side] = foeIndex;
                else secondBanChoices[side] = foeIndex;
                if (HasChosenBan(0) && HasChosenBan(1)) CompleteDoubleBan();
                return;
            }
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
                // 禁用前也可能留下和局日志，快照须与移除后的队伍保持同一索引。
                foreach (var entry in Log)
                {
                    var oldHp = entry.SnapHp[s];
                    var hp = new int[oldHp.Length - 1];
                    Array.Copy(oldHp, 0, hp, 0, index);
                    Array.Copy(oldHp, index + 1, hp, index, hp.Length - index);
                    entry.SnapHp[s] = hp;
                    int oldActive = entry.SnapActive[s];
                    entry.SnapActive[s] = oldActive == index ? 0 : oldActive > index ? oldActive - 1 : oldActive;
                }
                for (int i = 0; i < team.Count; i++) team[i].Slot = i + 1;
                Sides[s].Active = 0;
            }
            for (int s = 0; s < Sides.Length; s++)
                Add(Node.Start, Sides[s].Name + " 禁用了 " + Sides[1 - s].Name + " 的「" + banned[1 - s].Name + "」", "ban", s);

            if (leadPickAfterBan) Phase = Phase.Lead;
            else StartBattle();
        }

        void CompleteDoubleBan()
        {
            var banned = new Fighter[2][];
            for (int side = 0; side < Sides.Length; side++)
            {
                var team = Sides[side].Team;
                int first = banChoices[1 - side], second = secondBanChoices[1 - side];
                banned[side] = new[] { team[first], team[second] };
                // 先删较大原始下标，第二个目标和禁用前日志快照才不会错位。
                RemoveBannedFighter(side, Math.Max(first, second));
                RemoveBannedFighter(side, Math.Min(first, second));
                for (int i = 0; i < team.Count; i++) team[i].Slot = i + 1;
                Sides[side].Active = 0;
            }
            for (int side = 0; side < Sides.Length; side++)
                foreach (var fighter in banned[1 - side])
                    Add(Node.Start, Sides[side].Name + " 禁用了 " + Sides[1 - side].Name + " 的「" + fighter.Name + "」", "ban", side);
            if (leadPickAfterBan) Phase = Phase.Lead;
            else StartBattle();
        }

        void RemoveBannedFighter(int side, int index)
        {
            Sides[side].Team.RemoveAt(index);
            foreach (var entry in Log)
            {
                var oldHp = entry.SnapHp[side];
                var hp = new int[oldHp.Length - 1];
                Array.Copy(oldHp, 0, hp, 0, index);
                Array.Copy(oldHp, index + 1, hp, index, hp.Length - index);
                entry.SnapHp[side] = hp;
                int oldActive = entry.SnapActive[side];
                entry.SnapActive[side] = oldActive == index ? 0 : oldActive > index ? oldActive - 1 : oldActive;
            }
        }
    }
}
