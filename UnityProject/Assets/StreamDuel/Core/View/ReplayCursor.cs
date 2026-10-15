using System;
using System.Collections.Generic;
using System.Linq;

namespace StreamDuel.Core
{
    /// <summary>
    /// 按已回放的行动回合数定位。位置 0 包含首个行动前的禁用、首发等记录；
    /// 每一步包含一个 Turn 记录及其后、下一个 Turn 前的补位等记录。
    /// 录像在构造时复制，内容定义与 Battle.Clone 一样共享，调用方应保持不变。
    /// </summary>
    public sealed class ReplayCursor
    {
        static readonly IReadOnlyList<BattleEvent> EmptyEvents = Array.Empty<BattleEvent>();
        readonly BattleRecordEvent[] entries;
        readonly int[] boundaries;
        readonly Dictionary<int, Battle> snapshots = new Dictionary<int, Battle>();
        readonly int snapshotEvery;

        /// <summary>已回放的 Turn 记录数；不等同于 Current.Turn 的待行动回合号。</summary>
        public int Turn { get; private set; }

        public int MaxTurn { get { return boundaries.Length - 1; } }

        /// <summary>仅供读取当前对战；调用方不得修改它或调用会推进对战的方法。</summary>
        public Battle Current { get; private set; }

        /// <summary>最近一次成功 Next 的新增事件；跳转、后退及边界处的无效移动会清空。</summary>
        public IReadOnlyList<BattleEvent> EventsOfCurrentStep { get; private set; } = EmptyEvents;

        public ReplayCursor(ContentDb db, BattleRecord record, int snapshotEvery = 5)
        {
            if (db == null) throw new ArgumentNullException(nameof(db), "内容数据不能为空");
            if (record == null) throw new ArgumentNullException(nameof(record), "对战记录不能为空");
            if (snapshotEvery <= 0)
                throw new ArgumentOutOfRangeException(nameof(snapshotEvery), "快照间隔必须大于零");
            this.snapshotEvery = snapshotEvery;

            // 复用回放入口的内容指纹、内容包及开场选项校验，避免游标绕过兼容性检查。
            var battle = Replay.Run(db, CopyHeader(record));
            entries = record.Events.Select(CopyEntry).ToArray();
            var positions = new List<int>();
            for (int i = 0; i < entries.Length; i++)
            {
                if (entries[i].Type == BattleRecordEventType.Turn) positions.Add(i);
            }
            positions.Add(entries.Length);
            boundaries = positions.ToArray();

            Apply(battle, 0, boundaries[0]);
            snapshots.Add(0, battle.Clone());
            for (int turn = 1; turn <= MaxTurn; turn++)
            {
                Apply(battle, boundaries[turn - 1], boundaries[turn]);
                if (turn % snapshotEvery == 0) snapshots.Add(turn, battle.Clone());
            }
            Current = snapshots[0].Clone();
        }

        public bool Next()
        {
            EventsOfCurrentStep = EmptyEvents;
            if (Turn == MaxTurn) return false;

            int eventStart = Current.Events.Count;
            Apply(Current, boundaries[Turn], boundaries[Turn + 1]);
            Turn++;

            // 补位发生在 Battle.Turn 递增后，必须按新增后缀取事件，不能按回合号筛选。
            var events = new List<BattleEvent>();
            for (int i = eventStart; i < Current.Events.Count; i++)
                events.Add(CopyEvent(Current.Events[i]));
            EventsOfCurrentStep = events.AsReadOnly();
            return true;
        }

        public bool Prev()
        {
            EventsOfCurrentStep = EmptyEvents;
            if (Turn == 0) return false;
            Seek(Turn - 1);
            return true;
        }

        public void Seek(int turn)
        {
            EventsOfCurrentStep = EmptyEvents;
            int target = Math.Max(0, Math.Min(turn, MaxTurn));
            if (target == Turn) return;

            int snapshotTurn = target - target % snapshotEvery;
            var battle = snapshots[snapshotTurn].Clone();
            Apply(battle, boundaries[snapshotTurn], boundaries[target]);
            Current = battle;
            Turn = target;
        }

        public void ToEnd()
        {
            Seek(MaxTurn);
        }

        public BattleViewModel View(int viewerSide)
        {
            return BattleViewModel.From(Current, viewerSide);
        }

        void Apply(Battle battle, int start, int end)
        {
            for (int i = start; i < end; i++)
            {
                var entry = entries[i];
                if (entry.RngState.HasValue) battle.RestoreReplayRng(entry.RngState.Value);
                switch (entry.Type)
                {
                    case BattleRecordEventType.Turn:
                        battle.ResolveTurn(entry.A0, entry.A1);
                        break;
                    case BattleRecordEventType.Replace:
                        battle.Replace(entry.Side, entry.To);
                        break;
                    case BattleRecordEventType.Lead:
                        battle.ChooseLead(entry.Side, entry.To);
                        break;
                    case BattleRecordEventType.Ban:
                        battle.ChooseBan(entry.Side, entry.To);
                        break;
                    default:
                        throw new FormatException("未知回放事件类型：" + entry.Type);
                }
            }
        }

        static BattleRecord CopyHeader(BattleRecord source)
        {
            return new BattleRecord
            {
                Version = source.Version,
                ContentHash = source.ContentHash,
                Seed = source.Seed,
                PackId = source.PackId,
                LeadPick = source.LeadPick,
                BanOne = source.BanOne,
                Modifiers = source.Modifiers == null ? null : new List<string>(source.Modifiers),
                Names = source.Names == null ? null : (string[])source.Names.Clone(),
                Teams = source.Teams.Select(team => (IList<string>)new List<string>(team)).ToArray()
            };
        }

        static BattleRecordEvent CopyEntry(BattleRecordEvent source)
        {
            if (source == null) throw new FormatException("回放事件不能为空");
            return new BattleRecordEvent
            {
                Type = source.Type,
                A0 = source.A0,
                A1 = source.A1,
                Side = source.Side,
                To = source.To,
                RngState = source.RngState
            };
        }

        static BattleEvent CopyEvent(BattleEvent source)
        {
            return new BattleEvent
            {
                LogIndex = source.LogIndex,
                Turn = source.Turn,
                Side = source.Side,
                Slot = source.Slot,
                Amount = source.Amount,
                Node = source.Node,
                Id = source.Id,
                Kind = source.Kind
            };
        }
    }
}
