using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace StreamDuel.Core
{
    public enum BattleEventKind
    {
        Act, Damage, Heal, Faint, Enter, Switch, StatusAdd, StatusRemove,
        FieldAdd, FieldRemove, StageChange, PpChange, Crit, Miss, Info, End
    }

    public sealed class BattleEvent
    {
        /// <summary>对应 Battle.Log 的零起点下标，使动画能与原始日志及快照对齐。</summary>
        public int LogIndex;
        /// <summary>对应日志记录的回合，避免回合推进后读取到新的回合数。</summary>
        public int Turn;
        /// <summary>所属或受到变化的一方（0/1）；全局事件为 -1。End 使用胜方，平局为 -1。</summary>
        public int Side;
        /// <summary>该方队伍的零起点下标；Info、End 及未指向具体主播的事件为 -1。</summary>
        public int Slot;
        /// <summary>
        /// Damage/Heal 为实际扣除/增加的人气正值，未改变人气时不发这两类事件；StageChange/PpChange 为有符号变化量。
        /// StatusAdd 为添加或刷新后的剩余次数/回合；FieldAdd 为持续回合（-1 直到下场）或印记实际层数变化量。
        /// StatusRemove/FieldRemove、Act/Crit/Miss、Faint/Enter/Switch、Info/End 均为 0。
        /// </summary>
        public int Amount;
        /// <summary>对应日志的节点名，使同回合内的动画仍保留结算时机。</summary>
        public string Node;
        /// <summary>技能、状态、直播间效果或能力的 id；Faint/Enter/Switch 为主播 id，无对应内容时为 null。</summary>
        public string Id;
        public BattleEventKind Kind;
        /// <summary>Damage 的不可变计算快照；关闭采集时为 null，不写入录像。</summary>
        public DamageBreakdown DamageBreakdown { get; internal set; }
    }

    public sealed partial class Battle
    {
        private readonly List<BattleEvent> battleEvents = new List<BattleEvent>();
        private ReadOnlyCollection<BattleEvent> eventsView;

        public IReadOnlyList<BattleEvent> Events
        {
            get { return eventsView ?? (eventsView = battleEvents.AsReadOnly()); }
        }

        internal void ClearEventHistory()
        {
            battleEvents.Clear();
        }

        void Emit(BattleEventKind kind, Fighter fighter = null, string id = null, int amount = 0, int side = -1,
            DamageBreakdown damageBreakdown = null)
        {
            if (Log.Count == 0) throw new InvalidOperationException("结构化事件必须对应已有日志");
            int logIndex = Log.Count - 1;
            var entry = Log[logIndex];
            battleEvents.Add(new BattleEvent
            {
                LogIndex = logIndex,
                Turn = entry.Turn,
                Side = fighter == null ? side : fighter.Side,
                Slot = fighter == null ? -1 : fighter.Slot - 1,
                Amount = amount,
                Node = entry.Node,
                Id = id,
                Kind = kind,
                DamageBreakdown = damageBreakdown
            });
        }

        void CopyEventsFrom(Battle source)
        {
            foreach (var entry in source.battleEvents)
            {
                battleEvents.Add(new BattleEvent
                {
                    LogIndex = entry.LogIndex,
                    Turn = entry.Turn,
                    Side = entry.Side,
                    Slot = entry.Slot,
                    Amount = entry.Amount,
                    Node = entry.Node,
                    Id = entry.Id,
                    Kind = entry.Kind,
                    DamageBreakdown = entry.DamageBreakdown
                });
            }
        }
    }
}
