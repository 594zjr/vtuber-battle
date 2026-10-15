using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace StreamDuel.Core
{
    public sealed class FighterStats
    {
        public string StreamerId;
        public int DamageDealt, DamageTaken, Kos, TurnsActive;
        public bool Fainted;
        public Dictionary<string, int> SkillUses = new Dictionary<string, int>(StringComparer.Ordinal);
    }

    public sealed class BattleSummary
    {
        public List<FighterStats>[] Sides;
        public int Winner = -1;
        public int Turns;
        public string EndReason = "";
        readonly string[] sideNames = new string[2];
        readonly List<string>[] fighterNames = { new List<string>(), new List<string>() };
        readonly Dictionary<string, string> skillNames = new Dictionary<string, string>(StringComparer.Ordinal);

        public static BattleSummary From(Battle b)
        {
            if (b == null) throw new ArgumentNullException(nameof(b));
            var summary = new BattleSummary
            {
                Sides = new[] { new List<FighterStats>(), new List<FighterStats>() },
                Winner = b.Winner,
                EndReason = b.EndReason,
                Turns = b.Record.Events.Count(entry => entry.Type == BattleRecordEventType.Turn)
            };
            for (int side = 0; side < b.Sides.Length; side++)
            {
                summary.sideNames[side] = b.Sides[side].Name;
                foreach (var fighter in b.Sides[side].Team)
                {
                    summary.Sides[side].Add(b.SnapshotStats(fighter));
                    summary.fighterNames[side].Add(fighter.Name);
                }
            }
            foreach (var pair in b.Db.Skills) summary.skillNames[pair.Key] = pair.Value.Name;
            summary.skillNames["struggle"] = b.Db.Struggle.Name;
            return summary;
        }

        public string ToText()
        {
            var text = new StringBuilder("对战统计\n");
            for (int side = 0; side < Sides.Length; side++)
            {
                text.Append(side == 0 ? "甲方：" : "乙方：").Append(sideNames[side]).Append('\n');
                text.Append("号位\t主播\t造成伤害\t承受伤害\t击倒\t在场回合\t状态\t技能使用\n");
                for (int index = 0; index < Sides[side].Count; index++)
                {
                    var stats = Sides[side][index];
                    var uses = stats.SkillUses.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair =>
                    {
                        string name;
                        if (!skillNames.TryGetValue(pair.Key, out name)) name = pair.Key;
                        return name + "（" + pair.Key + "）×" + Number(pair.Value);
                    });
                    text.Append(Number(index + 1)).Append('\t').Append(fighterNames[side][index]).Append('\t')
                        .Append(Number(stats.DamageDealt)).Append('\t').Append(Number(stats.DamageTaken)).Append('\t')
                        .Append(Number(stats.Kos)).Append('\t').Append(Number(stats.TurnsActive)).Append('\t')
                        .Append(stats.Fainted ? "已下播" : "在播").Append('\t')
                        .Append(stats.SkillUses.Count == 0 ? "无" : string.Join("；", uses)).Append('\n');
                }
            }
            return text.ToString();
        }

        static string Number(int value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }
    }

    public sealed partial class Battle
    {
        // 以战斗单位为键，首发重排和换人不会把累计值移到别的主播身上。
        readonly Dictionary<Fighter, FighterStats> fighterStats = new Dictionary<Fighter, FighterStats>();

        FighterStats StatsFor(Fighter fighter)
        {
            FighterStats stats;
            if (!fighterStats.TryGetValue(fighter, out stats))
            {
                stats = new FighterStats();
                fighterStats.Add(fighter, stats);
            }
            return stats;
        }

        internal FighterStats SnapshotStats(Fighter fighter)
        {
            FighterStats stats;
            if (!fighterStats.TryGetValue(fighter, out stats)) stats = new FighterStats();
            return new FighterStats
            {
                StreamerId = fighter.Def.Id,
                DamageDealt = stats.DamageDealt,
                DamageTaken = stats.DamageTaken,
                Kos = stats.Kos,
                TurnsActive = stats.TurnsActive,
                Fainted = fighter.Fainted,
                SkillUses = new Dictionary<string, int>(stats.SkillUses, StringComparer.Ordinal)
            };
        }

        void RecordDamage(Fighter target, Fighter source, int amount)
        {
            if (amount <= 0) return;
            StatsFor(target).DamageTaken += amount;
            if (source != null && source.Side != target.Side)
            {
                StatsFor(source).DamageDealt += amount;
                totalDamageDealt[source.Side] += amount;
            }
        }

        void RecordSkillUse(Fighter fighter, string skillId)
        {
            var uses = StatsFor(fighter).SkillUses;
            int count;
            uses.TryGetValue(skillId, out count);
            uses[skillId] = count + 1;
        }

        void RecordTurnEnd()
        {
            // 节点开始时统一计数，避免先结算的一方全灭使另一方漏记本回合。
            for (int side = 0; side < Sides.Length; side++) StatsFor(Active(side)).TurnsActive++;
        }
    }
}
