using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace StreamDuel.Core
{
    public sealed class BranchCompareSide
    {
        public int Side { get; }
        public string Name { get; }
        public int Remaining { get; }
        public long Hp { get; }
        public long MaxHp { get; }
        public double HpPercent { get; }

        internal BranchCompareSide(int side, SideState state)
        {
            Side = side;
            Name = state.Name;
            Remaining = state.Team.Count(fighter => !fighter.Fainted && fighter.Hp > 0);
            Hp = state.Team.Sum(fighter => (long)fighter.Hp);
            MaxHp = state.Team.Sum(fighter => (long)fighter.MaxHp);
            HpPercent = MaxHp == 0 ? 0 : 100.0 * Hp / MaxHp;
        }
    }

    public sealed class BranchCompareMoment
    {
        public int Turn { get; }
        public string Kind { get; }
        public string Text { get; }
        public int Swing { get; }

        internal BranchCompareMoment(KeyMoment source)
        {
            Turn = source.Turn;
            Kind = source.Kind;
            Text = source.Text;
            Swing = source.Swing;
        }
    }

    public sealed class BranchCompareOutcome
    {
        public int Winner { get; }
        /// <summary>实际完成的 Turn 事件数，投降或和局不虚增行动回合。</summary>
        public int Turns { get; }
        public int EndTurn { get; }
        public ulong StateHash { get; }
        public IReadOnlyList<BranchCompareSide> Sides { get; }
        public IReadOnlyList<BranchCompareMoment> KeyMoments { get; }

        internal BranchCompareOutcome(Battle battle, int viewerSide, int branchTurn)
        {
            Winner = battle.Winner;
            Turns = battle.Record.Events.Count(entry => entry.Type == BattleRecordEventType.Turn);
            EndTurn = battle.Turn;
            StateHash = battle.StateHash();
            Sides = Enumerable.Range(0, 2).Select(side => new BranchCompareSide(side, battle.Sides[side]))
                .ToList().AsReadOnly();
            // 先取得全部候选，接手前的高分事件才不会挤掉接手后的关键回合。
            KeyMoments = BattleReview.KeyMoments(battle, viewerSide, int.MaxValue)
                .Where(moment => moment.Turn >= branchTurn).Select(moment => new BranchCompareMoment(moment))
                .ToList().AsReadOnly();
        }
    }

    public sealed class BranchCompareResult
    {
        public int Turn { get; }
        public int HumanSide { get; }
        public BranchCompareOutcome Original { get; }
        public BranchCompareOutcome Branch { get; }
        public string Conclusion { get; }

        internal BranchCompareResult(ReplayBranchInfo info, Battle original, Battle branch)
        {
            Turn = info.Turn;
            HumanSide = info.HumanSide;
            Original = new BranchCompareOutcome(original, HumanSide, Turn);
            Branch = new BranchCompareOutcome(branch, HumanSide, Turn);
            Conclusion = Describe(Original, Branch, HumanSide);
        }

        static string Describe(BranchCompareOutcome original, BranchCompareOutcome branch, int side)
        {
            if (original.Winner != branch.Winner)
                return "原录像" + Result(original.Winner, side) + "，接手后" + Result(branch.Winner, side);
            if (branch.Winner == side && original.Turns != branch.Turns)
            {
                int difference = original.Turns - branch.Turns;
                return "接手后" + (difference > 0 ? "提前 " : "延后 ")
                    + Math.Abs(difference).ToString(CultureInfo.InvariantCulture) + " 回合获胜";
            }
            return "结果相同";
        }

        static string Result(int winner, int side)
        {
            return winner == -1 ? "平局" : winner == side ? "获胜" : "落败";
        }
    }

    public static class BranchCompare
    {
        /// <summary>
        /// 只接受两份已结束的录像，严格检查版本、政策和当前内容指纹。
        /// source 必须是最后一次接手的直接来源；摘要、开场信息及接手边界前的原始事件均须一致。
        /// 返回独立只读快照，不修改输入，也不把相同操作视为相同随机结果。
        /// </summary>
        public static BranchCompareResult Compare(ContentDb db, BattleRecord source, BattleRecord branch)
        {
            if (db == null) throw new ArgumentNullException(nameof(db), "内容数据不能为空。");
            if (source == null) throw new ArgumentNullException(nameof(source), "原录像不能为空。");
            if (branch == null) throw new ArgumentNullException(nameof(branch), "接手录像不能为空。");
            var original = Snapshot(db, source, "原录像");
            var changed = Snapshot(db, branch, "接手录像");
            var info = changed.Branch;
            if (info == null) throw new InvalidOperationException("接手录像缺少来源信息，不能与原录像对照。");

            var originalBattle = ReplayFinished(db, original, "原录像");
            Battle expected;
            try { expected = ReplayBranch.Start(db, original, info.Turn, info.HumanSide, info.Level); }
            catch (Exception error) when (IsRecordError(error))
            {
                throw new InvalidOperationException("接手录像来源或接手边界无效：" + error.Message, error);
            }
            int boundary = changed.Events.FindLastIndex(entry => entry.Type == BattleRecordEventType.Branch);
            // Start 保留来源的可选 RNG 等字段；不使用重放后重新生成的事件判断前缀身份。
            var prefix = BattleRecord.FromJson(changed.ToJson());
            prefix.Events.RemoveRange(boundary + 1, prefix.Events.Count - boundary - 1);
            if (!string.Equals(expected.Record.ToJson(), prefix.ToJson(), StringComparison.Ordinal))
                throw new InvalidOperationException("接手录像来源与给定原录像不符：来源摘要、开场信息或接手前的事件不同。");

            var changedBattle = ReplayFinished(db, changed, "接手录像");
            return new BranchCompareResult(info, originalBattle, changedBattle);
        }

        static BattleRecord Snapshot(ContentDb db, BattleRecord record, string label)
        {
            var compatible = Replay.Check(db, record);
            if (!compatible.CanReplay) throw new InvalidOperationException(label + "不能重放：" + compatible.Reason);
            try { return BattleRecord.FromJson(record.ToJson()); }
            catch (Exception error) when (IsRecordError(error))
            {
                throw new FormatException(label + "结构无效：" + error.Message, error);
            }
        }

        static Battle ReplayFinished(ContentDb db, BattleRecord record, string label)
        {
            Battle battle;
            try { battle = Replay.Run(db, record); }
            catch (Exception error) when (IsRecordError(error))
            {
                throw new InvalidOperationException(label + "无法完整重放：" + error.Message, error);
            }
            if (battle.Phase != Phase.Over || battle.Winner < -1 || battle.Winner > 1)
                throw new InvalidOperationException(label + "尚未结束，不能比较最终结局。");
            return battle;
        }

        static bool IsRecordError(Exception error)
        {
            return error is FormatException || error is ArgumentException || error is InvalidOperationException
                || error is KeyError || error is KeyNotFoundException || error is IndexOutOfRangeException
                || error is NullReferenceException;
        }
    }
}
