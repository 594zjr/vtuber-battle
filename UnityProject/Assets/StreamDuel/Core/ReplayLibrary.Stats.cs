using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace StreamDuel.Core
{
    public sealed class ReplayFighterStats
    {
        public string StreamerId;
        public int DamageDealt;
        public int Kos;
    }

    public sealed class ReplayStats
    {
        public int PlayerSide;
        public AiLevel? OpponentAiLevel;
        public List<ReplayFighterStats> Fighters = new List<ReplayFighterStats>();
    }

    public sealed partial class ReplayLibrary
    {
        public List<ReplayEntry> ReadStatsSnapshot()
        {
            FileStream readLock;
            try
            {
                readLock = new FileStream(CheckedPath(".library.lock"), FileMode.Open, FileAccess.Read, FileShare.Read);
            }
            catch (FileNotFoundException)
            {
                // 首次保存前没有锁文件，查询不能为了互斥创建文件；此时不保证与新保存跨文件串行。
                return Scan();
            }
            // 已有锁允许只读查询并行，排斥保存写入；统计不重写索引或触发容量清理。
            using (readLock) return Scan();
        }

        static ReplayStats CreateStats(BattleRecord record, BattleSummary summary, int? playerSide, AiLevel? aiLevel)
        {
            if (!playerSide.HasValue)
            {
                if (aiLevel.HasValue) throw new ArgumentException("记录电脑难度时必须明确本方视角。", nameof(aiLevel));
                return null;
            }
            if (playerSide.Value < 0 || playerSide.Value > 1)
                throw new ArgumentOutOfRangeException(nameof(playerSide), "录像统计的本方必须为 0 或 1。");
            if (aiLevel.HasValue && !Enum.IsDefined(typeof(AiLevel), aiLevel.Value))
                throw new ArgumentOutOfRangeException(nameof(aiLevel), "录像统计的电脑难度无效。");
            if (summary.Sides == null || summary.Sides.Length != 2 || summary.Sides[playerSide.Value] == null)
                throw new ArgumentException("录像统计缺少本方实际出战名单。", nameof(summary));

            var stats = new ReplayStats { PlayerSide = playerSide.Value, OpponentAiLevel = aiLevel };
            foreach (var fighter in summary.Sides[playerSide.Value])
            {
                if (fighter == null) throw new ArgumentException("录像统计的出战成员不能为空。", nameof(summary));
                // 只复制统计需要的值，保存过程不保留可被调用方改写的 Summary 引用。
                stats.Fighters.Add(new ReplayFighterStats
                {
                    StreamerId = fighter.StreamerId, DamageDealt = fighter.DamageDealt, Kos = fighter.Kos
                });
            }
            try { ValidateStats(stats, record); }
            catch (FormatException ex) { throw new ArgumentException(ex.Message, nameof(summary), ex); }
            return stats;
        }

        static ReplayStats ReadStats(Dictionary<string, object> metadata, BattleRecord record)
        {
            if (!metadata.TryGetValue("stats", out var value) || value == null) return null;
            if (!(value is Dictionary<string, object> source)) throw new FormatException("录像统计必须为对象。");
            Integer(source, "version", 1, 1);
            var stats = new ReplayStats { PlayerSide = Integer(source, "playerSide", 0, 1) };
            if (!source.TryGetValue("aiLevel", out var level)) throw new FormatException("录像统计缺少电脑难度字段。");
            if (level != null)
            {
                if (!(level is string name)) throw new FormatException("录像统计的电脑难度无效。");
                switch (name)
                {
                    case "easy": stats.OpponentAiLevel = AiLevel.Easy; break;
                    case "normal": stats.OpponentAiLevel = AiLevel.Normal; break;
                    case "hard": stats.OpponentAiLevel = AiLevel.Hard; break;
                    case "expert": stats.OpponentAiLevel = AiLevel.Expert; break;
                    default: throw new FormatException("录像统计的电脑难度无效。");
                }
            }
            if (!source.TryGetValue("fighters", out var rawFighters) || !(rawFighters is List<object> fighters))
                throw new FormatException("录像统计缺少实际出战名单。");
            foreach (var valueFighter in fighters)
            {
                if (!(valueFighter is Dictionary<string, object> fighter)) throw new FormatException("录像统计的出战成员无效。");
                stats.Fighters.Add(new ReplayFighterStats
                {
                    StreamerId = Text(fighter, "streamerId"),
                    DamageDealt = Integer(fighter, "damageDealt", 0, int.MaxValue),
                    Kos = Integer(fighter, "kos", 0, int.MaxValue)
                });
            }
            ValidateStats(stats, record);
            return stats;
        }

        static void ValidateStats(ReplayStats stats, BattleRecord record)
        {
            if (stats.Fighters.Count < 1 || stats.Fighters.Count > 6)
                throw new FormatException("录像统计的实际出战人数必须为 1～6。");
            var roster = new HashSet<string>(record.Teams[stats.PlayerSide], StringComparer.Ordinal);
            var used = new HashSet<string>(StringComparer.Ordinal);
            foreach (var fighter in stats.Fighters)
            {
                if (string.IsNullOrWhiteSpace(fighter.StreamerId) || !roster.Contains(fighter.StreamerId)
                    || !used.Add(fighter.StreamerId))
                    throw new FormatException("录像统计的出战成员必须属于本方原始队伍且不能重复。");
                if (fighter.DamageDealt < 0 || fighter.Kos < 0)
                    throw new FormatException("录像统计的伤害和击倒数必须为非负整数。");
            }
        }

        static string StatsMetadata(ReplayStats stats)
        {
            if (stats == null) return "";
            string level = null;
            if (stats.OpponentAiLevel.HasValue)
            {
                switch (stats.OpponentAiLevel.Value)
                {
                    case AiLevel.Easy: level = "easy"; break;
                    case AiLevel.Normal: level = "normal"; break;
                    case AiLevel.Hard: level = "hard"; break;
                    case AiLevel.Expert: level = "expert"; break;
                    default: throw new FormatException("录像统计的电脑难度无效。");
                }
            }
            var json = new StringBuilder(",\"stats\":{\"version\":1,\"playerSide\":")
                .Append(stats.PlayerSide.ToString(CultureInfo.InvariantCulture))
                .Append(",\"aiLevel\":").Append(Quote(level)).Append(",\"fighters\":[");
            for (int i = 0; i < stats.Fighters.Count; i++)
            {
                if (i > 0) json.Append(',');
                var fighter = stats.Fighters[i];
                json.Append("{\"streamerId\":").Append(Quote(fighter.StreamerId))
                    .Append(",\"damageDealt\":").Append(fighter.DamageDealt.ToString(CultureInfo.InvariantCulture))
                    .Append(",\"kos\":").Append(fighter.Kos.ToString(CultureInfo.InvariantCulture)).Append('}');
            }
            return json.Append("]}").ToString();
        }
    }
}
