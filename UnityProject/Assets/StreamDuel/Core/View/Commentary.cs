using System;
using System.Collections.Generic;

namespace StreamDuel.Core
{
    public sealed class CommentLine
    {
        public int EventIndex;
        public string Text;
        public int Priority;
    }

    public static class Commentary
    {
        private sealed class Candidate
        {
            public int Index, Priority;
            public string Category;
            public object[] Args;
        }

        /// <summary>
        /// 按当前完整事件历史每回合选优先级最高的两条，再返回半开区间内的结果。
        /// 同优先级保留较早事件；返回绝对事件下标，不修改对战或推进随机数。
        /// </summary>
        public static List<CommentLine> For(Battle b, int fromEvent, int toEvent)
        {
            if (b == null) throw new ArgumentNullException(nameof(b));
            if (fromEvent < 0 || fromEvent > b.Events.Count) throw new ArgumentOutOfRangeException(nameof(fromEvent));
            if (toEvent < fromEvent || toEvent > b.Events.Count) throw new ArgumentOutOfRangeException(nameof(toEvent));
            var result = new List<CommentLine>();
            if (fromEvent == toEvent) return result;

            var turns = new Dictionary<int, List<Candidate>>();
            var streaks = new int[2][] { new int[b.Sides[0].Team.Count], new int[b.Sides[1].Team.Count] };
            var hits = new BattleEvent[2][]
            {
                new BattleEvent[b.Sides[0].Team.Count], new BattleEvent[b.Sides[1].Team.Count]
            };
            BattleEvent action = null;
            bool opened = false;

            for (int i = 0; i < b.Events.Count; i++)
            {
                var entry = b.Events[i];
                if (entry == null) throw new ArgumentException("对战事件不能为 null。", nameof(b));
                if (action != null && (action.Turn != entry.Turn || action.Node != entry.Node)) action = null;
                var fighter = FighterAt(b, entry.Side, entry.Slot);

                void Offer(string category, int priority, string name = "", string detail = "", int amount = 0, string target = "")
                {
                    if (!turns.TryGetValue(entry.Turn, out var choices))
                    {
                        choices = new List<Candidate>();
                        turns.Add(entry.Turn, choices);
                    }
                    choices.Add(new Candidate
                    {
                        Index = i, Priority = priority, Category = category,
                        Args = new object[] { name, detail, amount, target }
                    });
                    choices.Sort((left, right) => left.Priority == right.Priority
                        ? left.Index.CompareTo(right.Index) : right.Priority.CompareTo(left.Priority));
                    if (choices.Count > 2) choices.RemoveAt(2);
                }

                switch (entry.Kind)
                {
                    case BattleEventKind.Act:
                        action = entry;
                        var skill = Skill(b, entry.Id);
                        var snapshot = Snapshot(b, entry);
                        if (fighter == null || skill == null || skill.Power <= 0 || snapshot == null) break;
                        int foeSide = 1 - entry.Side;
                        if (snapshot.SnapActive == null || snapshot.SnapActive.Length <= foeSide) break;
                        int foeSlot = snapshot.SnapActive[foeSide];
                        var foe = FighterAt(b, foeSide, foeSlot);
                        if (foe == null || Hp(snapshot, foeSide, foeSlot) <= 0) break;
                        // 对位关系在出招时已确定，文案不声称命中，也不把固定伤害套进赛道倍率。
                        double multiplier = b.Db.TypeMul(skill.Type, foe.Type, foe.Type2);
                        if (multiplier > 1) Offer("effective", 20, fighter.Name, skill.Name, target: foe.Name);
                        else if (multiplier < 1) Offer("resisted", 20, fighter.Name, skill.Name, target: foe.Name);
                        break;
                    case BattleEventKind.Crit:
                        if (fighter != null) Offer("crit", 70, fighter.Name, Skill(b, entry.Id)?.Name ?? "");
                        break;
                    case BattleEventKind.Miss:
                        if (fighter != null) Offer("miss", 65, fighter.Name, Skill(b, entry.Id)?.Name ?? "");
                        action = null;
                        break;
                    case BattleEventKind.Damage:
                        if (fighter != null)
                        {
                            // 反弹也携带原技能 id，但受击者是施放者；持续伤害没有技能来源。
                            hits[entry.Side][entry.Slot] = action != null && entry.Side != action.Side
                                && entry.Id != null && entry.Id == action.Id ? action : null;
                        }
                        break;
                    case BattleEventKind.Faint:
                        if (fighter == null) break;
                        var source = hits[entry.Side][entry.Slot];
                        var attacker = source == null ? null : FighterAt(b, source.Side, source.Slot);
                        string category = "knockout";
                        int priority = 90;
                        if (attacker != null && source.Turn == entry.Turn && source.Node == entry.Node)
                        {
                            int count = ++streaks[source.Side][source.Slot];
                            int hp = Hp(Snapshot(b, entry), source.Side, source.Slot);
                            if (hp > 0 && (long)hp * 4 <= attacker.MaxHp)
                            {
                                category = "comeback";
                                priority = 100;
                            }
                            else if (hp > 0 && count >= 2)
                            {
                                category = "streak";
                                priority = 95;
                            }
                        }
                        Offer(category, priority, category == "knockout" ? fighter.Name : attacker.Name,
                            target: fighter.Name);
                        streaks[entry.Side][entry.Slot] = 0;
                        hits[entry.Side][entry.Slot] = null;
                        break;
                    case BattleEventKind.Enter:
                        if (fighter == null) break;
                        if (entry.Node == Node.Start)
                        {
                            if (!opened) Offer("opening", 110);
                            opened = true;
                        }
                        else if (LivingCount(Snapshot(b, entry), entry.Side) == 1)
                            Offer("last", 80, fighter.Name);
                        else Offer("switch", 30, fighter.Name);
                        break;
                    case BattleEventKind.Switch:
                        if (fighter != null) streaks[entry.Side][entry.Slot] = 0;
                        break;
                    case BattleEventKind.StatusAdd:
                    case BattleEventKind.StatusRemove:
                        if (fighter != null && entry.Id != null
                            && b.Db.Statuses.TryGetValue(entry.Id, out var status) && status.IsControl)
                            Offer(entry.Kind == BattleEventKind.StatusAdd ? "control" : "cleanse",
                                entry.Kind == BattleEventKind.StatusAdd ? 60 : 55, fighter.Name, status.Name);
                        break;
                    case BattleEventKind.FieldAdd:
                        if (fighter != null && entry.Id != null && b.Db.Fields.TryGetValue(entry.Id, out var field))
                            Offer("field", 40, fighter.Name, field.Name);
                        break;
                    case BattleEventKind.End:
                        if (entry.Side == -1) Offer("draw", 120);
                        else if (entry.Side == 0 || entry.Side == 1) Offer("victory", 120, b.Sides[entry.Side].Name);
                        break;
                }
            }

            var loc = Loc.Current;
            foreach (var choices in turns.Values)
            {
                foreach (var choice in choices)
                {
                    if (choice.Index < fromEvent || choice.Index >= toEvent) continue;
                    result.Add(new CommentLine
                    {
                        EventIndex = choice.Index, Priority = choice.Priority,
                        Text = loc.T("comment." + choice.Category + "." + (choice.Index % 3), choice.Args)
                    });
                }
            }
            result.Sort((left, right) => left.EventIndex.CompareTo(right.EventIndex));
            return result;
        }

        private static Fighter FighterAt(Battle b, int side, int slot)
        {
            return side >= 0 && side < b.Sides.Length && slot >= 0 && slot < b.Sides[side].Team.Count
                ? b.Sides[side].Team[slot] : null;
        }

        private static SkillDef Skill(Battle b, string id)
        {
            if (id == null) return null;
            if (b.Db.Skills.TryGetValue(id, out var skill)) return skill;
            return b.Db.Struggle.Id == id ? b.Db.Struggle : null;
        }

        private static LogEntry Snapshot(Battle b, BattleEvent entry)
        {
            return entry.LogIndex >= 0 && entry.LogIndex < b.Log.Count ? b.Log[entry.LogIndex] : null;
        }

        private static int Hp(LogEntry entry, int side, int slot)
        {
            return entry != null && entry.SnapHp != null && side >= 0 && side < entry.SnapHp.Length
                && entry.SnapHp[side] != null && slot >= 0 && slot < entry.SnapHp[side].Length
                ? entry.SnapHp[side][slot] : -1;
        }

        private static int LivingCount(LogEntry entry, int side)
        {
            if (entry == null || entry.SnapHp == null || side < 0 || side >= entry.SnapHp.Length
                || entry.SnapHp[side] == null) return -1;
            int alive = 0;
            foreach (int hp in entry.SnapHp[side]) if (hp > 0) alive++;
            return alive;
        }
    }
}
