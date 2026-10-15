using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace StreamDuel.Core
{
    public sealed class KeyMoment
    {
        public int Turn;
        public string Kind;
        public string Text;
        public int Swing;
    }

    public static class BattleReview
    {
        private static Loc Loc { get { return StreamDuel.Core.Loc.Current; } }

        private sealed class Round
        {
            public int Turn, Index;
            public long Before, After, Mine, Foe;
            public int Swing { get { return Clamp(After - Before); } }
        }

        private sealed class Candidate
        {
            public int Index;
            public KeyMoment Moment;
        }

        private sealed class Arrival
        {
            public int Slot, Turn;
            public bool Resisted, Effective;
        }

        public static List<KeyMoment> KeyMoments(Battle b, int viewerSide, int max = 5)
        {
            if (b == null) throw new ArgumentNullException(nameof(b));
            if (viewerSide < 0 || viewerSide > 1) throw new ArgumentOutOfRangeException(nameof(viewerSide));
            if (max <= 0) return new List<KeyMoment>();

            // 每条快照发生在日志事件之后，下一回合的基线必须承接上一条快照。
            var rounds = new Dictionary<int, Round>();
            long previous = 0;
            bool hasPrevious = false;
            for (int index = 0; index < b.Log.Count; index++)
            {
                var line = b.Log[index];
                if (!HasSnapshot(line)) continue;
                long mine = Total(line.SnapHp[viewerSide]), foe = Total(line.SnapHp[1 - viewerSide]);
                long difference = mine - foe;
                if (!rounds.TryGetValue(line.Turn, out var round))
                {
                    round = new Round { Turn = line.Turn, Index = index, Before = hasPrevious ? previous : difference };
                    rounds.Add(line.Turn, round);
                }
                round.After = difference;
                round.Mine = mine;
                round.Foe = foe;
                previous = difference;
                hasPrevious = true;
            }

            var candidates = new List<Candidate>();
            var byLine = b.Events.Where(e => e != null && e.LogIndex >= 0 && e.LogIndex < b.Log.Count)
                .GroupBy(e => e.LogIndex).ToDictionary(group => group.Key, group => group.ToList());
            var switched = new BattleEvent[2];
            var arrivals = new Arrival[2];
            // 控制可在先手命中时解除，但回合初已锁定的后手仍可能来不及行动。
            var controlled = new HashSet<string>(StringComparer.Ordinal);
            BattleEvent action = null;
            int winner = -1, endIndex = -1;

            for (int index = 0; index < b.Log.Count; index++)
            {
                var line = b.Log[index];
                if (line == null) continue;
                int swing = rounds.TryGetValue(line.Turn, out var round) ? round.Swing : 0;
                if (IsSide(line.Side) && line.SnapActive != null && line.SnapActive.Length == 2
                    && line.Kind == "status" && IsActionNode(line.Node))
                {
                    string key = Target(line.Side, line.SnapActive[line.Side]);
                    if (controlled.Contains(key) && TrySkipped(line.Text, out string name, out string status))
                        Add(candidates, index, line.Turn, "control", swing,
                            Loc.T("review.control", line.Turn, Side(line.Side, viewerSide), name, status, Signed(swing)));
                }
                if (!byLine.TryGetValue(index, out var events)) continue;
                foreach (var e in events)
                {
                    if (e.Kind == BattleEventKind.End) { winner = e.Side; endIndex = index; }
                    if (!IsSide(e.Side)) continue;
                    if (e.Kind == BattleEventKind.StatusAdd && line.Text != null
                        && line.Text.Contains("（控制类，将跳过 ")) controlled.Add(Target(e.Side, e.Slot));
                    if (e.Kind == BattleEventKind.Act) action = e;
                    if (e.Kind == BattleEventKind.Switch)
                    {
                        arrivals[e.Side] = null;
                        switched[e.Side] = e.Node == Node.Switch ? e : null;
                    }
                    if (e.Kind == BattleEventKind.Enter)
                    {
                        var old = switched[e.Side];
                        arrivals[e.Side] = old != null && old.Turn == e.Turn && old.Node == e.Node
                            ? new Arrival { Slot = e.Slot, Turn = e.Turn } : null;
                        switched[e.Side] = null;
                    }
                    if (e.Kind == BattleEventKind.Faint)
                    {
                        // Faint 没有来源字段；反噬、反弹和持续伤害不能归功于最近的出招者。
                        string name = StripSuffix(line.Text, " 下播了") ?? e.Id ?? Loc.T("review.slot", e.Slot + 1);
                        if (HasSnapshot(line))
                            Add(candidates, index, line.Turn, "ko", swing,
                                Loc.T("review.ko", line.Turn, Side(e.Side, viewerSide), name,
                                    Alive(line.SnapHp[viewerSide]), Alive(line.SnapHp[1 - viewerSide])));
                        arrivals[e.Side] = null;
                    }
                    if (e.Kind != BattleEventKind.Damage || e.Amount <= 0 || action == null
                        || action.Side == e.Side || action.Turn != e.Turn || action.Node != e.Node
                        || action.Id != e.Id || !TryDirectDamage(line.Text, e.Amount, out string target, out string tail)) continue;

                    var defender = arrivals[e.Side];
                    if (defender != null && defender.Slot == e.Slot && !defender.Resisted
                        && tail.StartsWith("，赛道被克", StringComparison.Ordinal))
                    {
                        defender.Resisted = true;
                        Add(candidates, index, line.Turn, "switch", swing,
                            Loc.T("review.switch.resist", line.Turn, Side(e.Side, viewerSide), target, defender.Turn, e.Amount));
                    }
                    var attacker = arrivals[action.Side];
                    if (attacker != null && attacker.Slot == action.Slot && !attacker.Effective
                        && tail.StartsWith("，赛道克制", StringComparison.Ordinal))
                    {
                        attacker.Effective = true;
                        string actor = Before(b.Log[action.LogIndex].Text, " 使用「")
                            ?? Loc.T("review.slot", action.Slot + 1);
                        Add(candidates, index, line.Turn, "switch", swing,
                            Loc.T("review.switch.effective", line.Turn, Side(action.Side, viewerSide), actor,
                                attacker.Turn, target, e.Amount));
                    }
                }
            }

            var largest = rounds.Values.Where(round => round.Swing != 0)
                .OrderByDescending(round => Math.Abs((long)round.Swing)).ThenBy(round => round.Turn).FirstOrDefault();
            if (largest != null)
                Add(candidates, largest.Index, largest.Turn, "swing", largest.Swing,
                    Loc.T("review.swing", largest.Turn, Signed(largest.Before), Signed(largest.After), Signed(largest.Swing)));

            if (winner == viewerSide && endIndex >= 0 && HasSnapshot(b.Log[endIndex]))
            {
                var start = rounds.Values.Where(round => round.Index <= endIndex && round.Foe > 0 && round.Mine * 2 <= round.Foe)
                    .OrderBy(round => round.Turn).FirstOrDefault();
                if (start != null)
                {
                    var final = b.Log[endIndex].SnapHp;
                    int swing = Clamp(Total(final[viewerSide]) - Total(final[1 - viewerSide]) - start.After);
                    Add(candidates, start.Index, start.Turn, "comeback", swing,
                        Loc.T("review.comeback", start.Turn, start.Mine, start.Foe, Signed(swing)));
                }
            }

            return candidates.OrderByDescending(candidate => Math.Abs((long)candidate.Moment.Swing))
                .ThenBy(candidate => candidate.Moment.Turn).ThenBy(candidate => candidate.Index)
                .Take(max).OrderBy(candidate => candidate.Moment.Turn).ThenBy(candidate => candidate.Index)
                .Select(candidate => candidate.Moment).ToList();
        }

        private static bool TrySkipped(string text, out string name, out string status)
        {
            name = null;
            status = null;
            if (text == null) return false;
            int marker = text.LastIndexOf(" 处于「", StringComparison.Ordinal);
            int end = text.LastIndexOf("」，无法行动", StringComparison.Ordinal);
            if (marker >= 0 && end > marker && IsSkipTail(text.Substring(end + "」，无法行动".Length)))
            {
                name = text.Substring(0, marker);
                status = text.Substring(marker + 4, end - marker - 4);
                return true;
            }
            name = StripSuffix(text, " 刚回过神来，本回合来不及行动");
            if (name == null) return false;
            status = Loc.T("review.control.recovered");
            return true;
        }

        private static bool IsSkipTail(string tail)
        {
            if (tail.Length == 0) return true;
            const string prefix = "（还要跳过 ";
            const string suffix = " 次）";
            return tail.StartsWith(prefix, StringComparison.Ordinal) && tail.EndsWith(suffix, StringComparison.Ordinal)
                && int.TryParse(tail.Substring(prefix.Length, tail.Length - prefix.Length - suffix.Length),
                    NumberStyles.None, CultureInfo.InvariantCulture, out int remaining) && remaining > 0;
        }

        private static bool TryDirectDamage(string text, int amount, out string name, out string tail)
        {
            name = null;
            tail = null;
            if (text == null) return false;
            string marker = " 人气 -" + amount.ToString(CultureInfo.InvariantCulture);
            int index = text.LastIndexOf(marker, StringComparison.Ordinal);
            if (index < 0) return false;
            name = text.Substring(0, index);
            tail = text.Substring(index + marker.Length);
            return true;
        }

        private static void Add(List<Candidate> candidates, int index, int turn, string kind, int swing, string text)
        {
            candidates.Add(new Candidate { Index = index, Moment = new KeyMoment { Turn = turn, Kind = kind, Swing = swing, Text = text } });
        }

        private static bool IsSide(int side) { return side == 0 || side == 1; }
        private static bool IsActionNode(string node) { return node == Node.First || node == Node.Second; }
        private static string Target(int side, int slot) { return side + ":" + slot; }
        private static string Side(int side, int viewer) { return Loc.T(side == viewer ? "review.side.self" : "review.side.foe"); }
        private static string Signed(long value) { return value.ToString("+0;-0;0", CultureInfo.InvariantCulture); }
        private static int Clamp(long value) { return (int)Math.Max(int.MinValue, Math.Min(int.MaxValue, value)); }
        private static long Total(int[] hp) { long value = 0; foreach (int n in hp) value += Math.Max(0, n); return value; }
        private static int Alive(int[] hp) { return hp.Count(value => value > 0); }
        private static bool HasSnapshot(LogEntry line)
        {
            return line != null && line.SnapHp != null && line.SnapHp.Length == 2 && line.SnapHp[0] != null && line.SnapHp[1] != null;
        }
        private static string StripSuffix(string text, string suffix)
        {
            return text != null && text.EndsWith(suffix, StringComparison.Ordinal) ? text.Substring(0, text.Length - suffix.Length) : null;
        }
        private static string Before(string text, string marker)
        {
            int index = text == null ? -1 : text.LastIndexOf(marker, StringComparison.Ordinal);
            return index < 0 ? null : text.Substring(0, index);
        }
    }
}
