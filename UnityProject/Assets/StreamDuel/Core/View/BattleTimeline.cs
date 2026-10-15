using System;
using System.Collections.Generic;

namespace StreamDuel.Core
{
    public enum CueKind
    {
        Enter, SwitchOut, SwitchIn, Cast, Projectile, Hit, Flash, FloatText,
        HpBar, Faint, StatusIcon, FieldBanner, Miss, End
    }

    public sealed class AnimCue
    {
        public double Start, Duration;
        public CueKind Kind;
        public int EventIndex, LogIndex, Side, Slot, Amount;
        public string Id, Text, Style;
    }

    public static class BattleTimeline
    {
        public static List<AnimCue> Build(IReadOnlyList<BattleEvent> events, int from, int to, double speed = 1.0)
        {
            if (events == null) throw new ArgumentNullException(nameof(events));
            if (from < 0 || from > events.Count) throw new ArgumentOutOfRangeException(nameof(from));
            if (to < from || to > events.Count) throw new ArgumentOutOfRangeException(nameof(to));
            if (double.IsNaN(speed) || double.IsInfinity(speed)) throw new ArgumentOutOfRangeException(nameof(speed));

            var cues = new List<AnimCue>();
            var loc = Loc.Current;
            double start = 0;
            double end = 0;
            for (int i = from; i < to; i++)
            {
                var entry = events[i];
                if (entry == null) throw new ArgumentException(loc.T("timeline.err.event.null"), nameof(events));

                void Add(CueKind kind, double duration, double offset = 0, string text = null, string style = null)
                {
                    cues.Add(new AnimCue
                    {
                        Start = start + offset,
                        Duration = duration,
                        Kind = kind,
                        EventIndex = i,
                        LogIndex = entry.LogIndex,
                        Side = entry.Side,
                        Slot = entry.Slot,
                        Amount = entry.Amount,
                        Id = entry.Id,
                        Text = text,
                        Style = style
                    });
                    end = Math.Max(end, start + offset + duration);
                }

                switch (entry.Kind)
                {
                    case BattleEventKind.Act:
                        Add(CueKind.Cast, 0.35);
                        Add(CueKind.Projectile, 0.25, 0.35);
                        break;
                    case BattleEventKind.Damage:
                        Add(CueKind.Hit, 0.2);
                        Add(CueKind.Flash, 0.12);
                        Add(CueKind.FloatText, 0.8, text: loc.T("timeline.damage", entry.Amount), style: "damage");
                        Add(CueKind.HpBar, 0.4);
                        break;
                    case BattleEventKind.Crit:
                        Add(CueKind.FloatText, 0.8, text: loc.T("timeline.crit"), style: "crit");
                        break;
                    case BattleEventKind.Miss:
                        Add(CueKind.Miss, 0.3);
                        Add(CueKind.FloatText, 0.8, text: loc.T("timeline.miss"), style: "miss");
                        break;
                    case BattleEventKind.Heal:
                        Add(CueKind.FloatText, 0.8, text: loc.T("timeline.heal", entry.Amount), style: "heal");
                        Add(CueKind.HpBar, 0.4);
                        break;
                    case BattleEventKind.Faint:
                        Add(CueKind.Faint, 0.5);
                        break;
                    case BattleEventKind.Switch:
                        Add(CueKind.SwitchOut, 0.3);
                        Add(CueKind.SwitchIn, 0.35, 0.3);
                        var incoming = cues[cues.Count - 1];
                        // Switch 只标识离场者；只有本范围内配对的 Enter 才能确定新主播。
                        var next = IncomingFighter(events, i + 1, to, entry);
                        incoming.Slot = next == null ? -1 : next.Slot;
                        incoming.Id = next == null ? null : next.Id;
                        break;
                    case BattleEventKind.Enter:
                        Add(CueKind.Enter, 0.4);
                        break;
                    case BattleEventKind.StatusAdd:
                    case BattleEventKind.StatusRemove:
                        Add(CueKind.StatusIcon, 0.3, style: "status");
                        if (entry.Kind == BattleEventKind.StatusRemove) cues[cues.Count - 1].Amount = 0;
                        break;
                    case BattleEventKind.FieldAdd:
                    case BattleEventKind.FieldRemove:
                        Add(CueKind.FieldBanner, 0.6, style: "status");
                        if (entry.Kind == BattleEventKind.FieldRemove) cues[cues.Count - 1].Amount = 0;
                        break;
                    case BattleEventKind.StageChange:
                        bool increased = entry.Amount > 0;
                        Add(CueKind.FloatText, 0.8,
                            text: loc.T(increased ? "timeline.stage.up" : "timeline.stage.down", StatName(loc, entry.Id), entry.Amount),
                            style: increased ? "stage-up" : "stage-down");
                        break;
                    case BattleEventKind.End:
                        Add(CueKind.End, 0);
                        break;
                    case BattleEventKind.PpChange:
                    case BattleEventKind.Info:
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(events), loc.T("timeline.err.event.kind"));
                }

                // Crit 定位施放者，Damage 定位受击者；只共享时刻，不修改各自的目标。
                bool pairedCritical = entry.Kind == BattleEventKind.Crit && i + 1 < to
                    && events[i + 1] != null && events[i + 1].Kind == BattleEventKind.Damage;
                if (!pairedCritical) start = end;
            }

            foreach (var cue in cues)
            {
                cue.Start = speed <= 0 ? 0 : cue.Start / speed;
                cue.Duration = speed <= 0 ? 0 : cue.Duration / speed;
            }
            return cues;
        }

        public static double TotalDuration(IList<AnimCue> cues)
        {
            if (cues == null) throw new ArgumentNullException(nameof(cues));
            double end = 0;
            foreach (var cue in cues)
            {
                if (cue == null) throw new ArgumentException(Loc.Current.T("timeline.err.cue.null"), nameof(cues));
                end = Math.Max(end, cue.Start + cue.Duration);
            }
            return end;
        }

        private static string StatName(Loc loc, string id)
        {
            switch (id)
            {
                case "atk":
                case "def":
                case "spa":
                case "sdef":
                case "spd":
                    return loc.T("timeline.stat." + id);
                default:
                    return loc.T("timeline.stat.unknown");
            }
        }

        private static BattleEvent IncomingFighter(IReadOnlyList<BattleEvent> events, int from, int to, BattleEvent outgoing)
        {
            for (int i = from; i < to; i++)
            {
                var entry = events[i];
                if (entry == null) return null;
                if (entry.Kind == BattleEventKind.Info || entry.Kind == BattleEventKind.PpChange) continue;
                return entry.Kind == BattleEventKind.Enter && entry.Side == outgoing.Side && entry.Turn == outgoing.Turn
                    && string.Equals(entry.Node, outgoing.Node, StringComparison.Ordinal) ? entry : null;
            }
            return null;
        }
    }
}
