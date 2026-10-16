using System;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace StreamDuel.Core
{
    public sealed class ReplayBranchInfo
    {
        public string SourceHash { get; }
        public int Turn { get; }
        public int HumanSide { get; }
        public AiLevel Level { get; }
        public uint Seed { get; }

        internal ReplayBranchInfo(string sourceHash, int turn, int humanSide, AiLevel level, uint seed)
        {
            SourceHash = sourceHash;
            Turn = turn;
            HumanSide = humanSide;
            Level = level;
            Seed = seed;
        }
    }

    public static class ReplayBranch
    {
        /// <summary>从已记录的第 turn 个行动回合开始接手，严格检查版本、政策和内容指纹。</summary>
        public static Battle Start(ContentDb db, BattleRecord record, int turn, int humanSide, AiLevel level)
        {
            if (db == null) throw new ArgumentNullException(nameof(db), "内容数据不能为空。");
            if (record == null) throw new ArgumentNullException(nameof(record), "录像记录不能为空。");
            if (turn < 1) throw new ArgumentOutOfRangeException(nameof(turn), "接手回合必须从第 1 回合开始。");
            if (humanSide < 0 || humanSide > 1) throw new ArgumentOutOfRangeException(nameof(humanSide), "接手方只能是 0 或 1。");
            if (!Enum.IsDefined(typeof(AiLevel), level)) throw new ArgumentOutOfRangeException(nameof(level), "电脑难度无效。");
            var compatibility = Replay.Check(db, record);
            if (!compatibility.CanReplay) throw new InvalidOperationException(compatibility.Reason);

            // 来源身份与前缀都取同一份独立快照，后续接手不会改写输入记录。
            string sourceJson = record.ToJson();
            var source = BattleRecord.FromJson(sourceJson);
            var cursor = new ReplayCursor(db, source);
            if (turn > cursor.MaxTurn) throw new ArgumentOutOfRangeException(nameof(turn), "接手回合超出录像已记录的行动回合范围。");
            cursor.Seek(turn - 1);
            if (cursor.Current.Phase != Phase.Choose || cursor.Current.Turn != turn)
                throw new InvalidOperationException("该回合开始时不能接手：对战已结束或尚未完成开场、补位。");
            var battle = cursor.Current.Clone();

            // 保留旧录像中缺省 RNG 等原始字段，不用重放生成的记录替代来源前缀。
            battle.Record.Version = source.Version;
            battle.Record.ContentHash = source.ContentHash;
            battle.Record.Seed = source.Seed;
            battle.Record.PackId = source.PackId;
            battle.Record.LeadPick = source.LeadPick;
            battle.Record.BanOne = source.BanOne;
            battle.Record.Modifiers = source.Modifiers;
            battle.Record.Names = source.Names;
            battle.Record.Teams = source.Teams;
            battle.Record.Events.Clear();
            int turns = 0;
            foreach (var entry in source.Events)
            {
                if (entry.Type == BattleRecordEventType.Turn && ++turns == turn) break;
                battle.Record.Events.Add(entry);
            }
            var info = new ReplayBranchInfo(SourceHash(sourceJson), turn, humanSide, level, DeriveSeed(source.Seed, turn));
            battle.ApplyReplayBranch(info);
            return battle;
        }

        public static uint DeriveSeed(uint seed, int turn)
        {
            if (turn < 1) throw new ArgumentOutOfRangeException(nameof(turn), "接手回合必须从第 1 回合开始。");
            unchecked
            {
                uint value = seed ^ (uint)turn * 0x9E3779B9u ^ 0xB5297A4Du;
                value ^= value >> 16;
                value *= 0x85EBCA6Bu;
                value ^= value >> 13;
                value *= 0xC2B2AE35u;
                return value ^ (value >> 16);
            }
        }

        static string SourceHash(string json)
        {
            using (var sha = SHA256.Create())
            {
                var result = new StringBuilder(64);
                foreach (byte value in sha.ComputeHash(Encoding.UTF8.GetBytes(json)))
                    result.Append(value.ToString("x2", CultureInfo.InvariantCulture));
                return result.ToString();
            }
        }

        internal static void ValidateInfo(ReplayBranchInfo info)
        {
            if (info == null) throw new FormatException("分支录像缺少来源信息。");
            if (info.SourceHash == null || info.SourceHash.Length != 64
                || !info.SourceHash.All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f'))
                throw new FormatException("分支录像来源指纹必须是 64 位小写十六进制字符串。");
            if (info.Turn < 1 || info.HumanSide < 0 || info.HumanSide > 1 || !Enum.IsDefined(typeof(AiLevel), info.Level))
                throw new FormatException("分支录像的回合、接手方或电脑难度无效。");
        }
    }

    public sealed partial class Battle
    {
        public bool IsBranch { get { return Record.IsBranch; } }

        internal void ApplyReplayBranch(ReplayBranchInfo info)
        {
            ReplayBranch.ValidateInfo(info);
            if (Phase != Phase.Choose || Turn != info.Turn)
                throw new InvalidOperationException("分支录像只能在指定回合开始的选择阶段接手。");
            if (info.Seed != ReplayBranch.DeriveSeed(Seed, info.Turn))
                throw new FormatException("分支录像的派生种子不正确。");
            Record.Version = Math.Max(Record.Version, BattleRecord.BranchVersion);
            Record.Events.Add(new BattleRecordEvent { Type = BattleRecordEventType.Branch, Branch = info, RngState = rng.State });
            rng.State = info.Seed;
            Add(Node.TurnStart, "从录像第 " + info.Turn.ToString(CultureInfo.InvariantCulture) + " 回合接手，继续与电脑对战", "branch", info.HumanSide);
        }
    }
}
