using System;
using System.Collections.Generic;
using System.Globalization;
using StreamDuel.Core;
using UnityEngine;
using UnityEngine.UIElements;

namespace StreamDuel.Game
{
    public sealed partial class TimelineAnimator : IBattleAnimator, IDisposable
    {
        readonly BattleStage stage;
        readonly ContentDb db;
        readonly IPortraitProvider portraits;
        readonly IFxPlayer fx;
        readonly IStatusIconProvider icons;
        readonly Func<string, string> skillFx;
        readonly bool ownsPortraits;
        readonly SideVisual[] visuals = new SideVisual[2];
        readonly List<VisualElement> temporary = new List<VisualElement>();
        readonly List<RunningCue> timeline = new List<RunningCue>();
        readonly List<LogSnapshot> logs = new List<LogSnapshot>();
        readonly List<BattleEventKind> eventKinds = new List<BattleEventKind>();
        DisplaySnapshot before, after, display;
        double elapsed, duration;
        bool disposed;

        public bool IsPlaying { get; private set; }

        public TimelineAnimator(BattleStage stage, ContentDb db, IPortraitProvider portraits = null,
            IFxPlayer fx = null, IStatusIconProvider icons = null, Func<string, string> skillFx = null)
        {
            this.stage = stage ?? throw new ArgumentNullException(nameof(stage));
            this.db = db ?? throw new ArgumentNullException(nameof(db));
            this.portraits = portraits ?? new SolidColorPortraitProvider();
            ownsPortraits = portraits == null;
            this.fx = fx ?? new NullFx();
            this.icons = icons ?? new TextStatusIconProvider();
            this.skillFx = skillFx ?? (id => id);
            try
            {
                visuals[0] = new SideVisual(stage.Fighter0);
                visuals[1] = new SideVisual(stage.Fighter1);
            }
            catch
            {
                foreach (var visual in visuals) visual?.Dispose();
                if (ownsPortraits) ((IDisposable)this.portraits).Dispose();
                throw;
            }
        }

        public void Prepare(Battle battle, BattleViewModel before, BattleViewModel after)
        {
            ThrowIfDisposed();
            if (battle == null) throw new ArgumentNullException(nameof(battle));
            var first = DisplaySnapshot.Copy(before);
            var last = DisplaySnapshot.Copy(after);
            ClearPlayback();
            this.before = first;
            this.after = last;
            logs.Clear();
            foreach (var entry in battle.Log) logs.Add(new LogSnapshot(entry));
            eventKinds.Clear();
            foreach (var entry in battle.Events) eventKinds.Add(entry.Kind);
            Render(first);
        }

        public void Play(IReadOnlyList<AnimCue> cues)
        {
            ThrowIfDisposed();
            if (before == null) throw new InvalidOperationException("请先准备动画前后的视图。");
            if (cues == null) throw new ArgumentNullException(nameof(cues));
            var copy = new List<RunningCue>();
            for (int i = 0; i < cues.Count; i++)
            {
                var cue = cues[i];
                if (cue == null || !Finite(cue.Start) || !Finite(cue.Duration)
                    || cue.Start < 0 || cue.Duration < 0 || !Finite(cue.Start + cue.Duration)
                    || !Enum.IsDefined(typeof(CueKind), cue.Kind))
                    throw new ArgumentException("动画提示的类型或时间无效。", nameof(cues));
                copy.Add(new RunningCue(cue, i));
            }
            copy.Sort((a, b) => a.Cue.Start != b.Cue.Start
                ? a.Cue.Start.CompareTo(b.Cue.Start) : a.Order.CompareTo(b.Order));
            var arrivals = new RunningCue[2];
            foreach (var item in copy)
            {
                var cue = item.Cue;
                if (!ValidSide(cue.Side)) continue;
                if (cue.Kind == CueKind.SwitchOut) arrivals[cue.Side] = null;
                if (cue.Kind == CueKind.SwitchIn) arrivals[cue.Side] = item;
                if (cue.Kind == CueKind.Enter)
                {
                    var arrival = arrivals[cue.Side];
                    // 时间线保留换人和 Enter 两个事件，已由配对 SwitchIn 滑入的头像只更新快照。
                    item.SuppressEntrance = arrival != null && arrival.Cue.Slot == cue.Slot
                        && string.Equals(arrival.Cue.Id, cue.Id, StringComparison.Ordinal)
                        && arrival.Cue.EventIndex < cue.EventIndex;
                    arrivals[cue.Side] = null;
                }
            }
            ClearPlayback();
            Render(before);
            timeline.AddRange(copy);
            var firstSeen = new bool[2];
            foreach (var item in timeline)
            {
                var cue = item.Cue;
                if (!ValidSide(cue.Side) || firstSeen[cue.Side]) continue;
                firstSeen[cue.Side] = true;
                // 首个提示为登场的当前头像在轮到自身之前保持隐藏，避免先出现再退回屏外。
                if (cue.Kind == CueKind.Enter && !item.SuppressEntrance && IsCurrent(cue))
                {
                    visuals[cue.Side].Visible = false;
                    visuals[cue.Side].Root.style.opacity = 0;
                }
            }
            foreach (var item in timeline) duration = Math.Max(duration, item.Cue.Start + item.Cue.Duration);
            IsPlaying = timeline.Count > 0;
            if (IsPlaying) Advance(0);
            else Render(after);
        }

        public void Tick(float unscaledDeltaTime)
        {
            ThrowIfDisposed();
            if (float.IsNaN(unscaledDeltaTime) || float.IsInfinity(unscaledDeltaTime) || unscaledDeltaTime < 0)
                throw new ArgumentOutOfRangeException(nameof(unscaledDeltaTime));
            if (IsPlaying) Advance(Math.Min(duration, elapsed + unscaledDeltaTime));
        }

        public void SkipToEnd()
        {
            ThrowIfDisposed();
            ClearPlayback();
            if (after != null) Render(after);
        }

        void Advance(double time)
        {
            // 跨过多个节点时仍逐边界处理，前一段终态先于后一段起点，不能漏掉换人或状态移除。
            while (elapsed < time)
            {
                double next = time;
                foreach (var item in timeline)
                {
                    double boundary = item.Started ? item.Cue.Start + item.Cue.Duration : item.Cue.Start;
                    if (!item.Finished && boundary > elapsed && boundary < next) next = boundary;
                }
                PaintAt(next);
                elapsed = next;
            }
            PaintAt(time);
            elapsed = time;
            if (elapsed >= duration)
            {
                ClearPlayback();
                Render(after);
            }
        }

        void PaintAt(double time)
        {
            var poses = new[] { new Pose(), new Pose() };
            foreach (var item in timeline)
            {
                if (item.Finished || time < item.Cue.Start) continue;
                if (!item.Started)
                {
                    item.Started = true;
                    Begin(item);
                }
                float t = item.Cue.Duration <= 0 ? 1 : Mathf.Clamp01((float)((time - item.Cue.Start) / item.Cue.Duration));
                UpdateCue(item, t, poses);
                if (t >= 1)
                {
                    Finish(item);
                    item.Finished = true;
                }
            }
            for (int side = 0; side < 2; side++)
            {
                var visual = visuals[side];
                var pose = poses[side];
                SetTranslate(visual.Root, pose.RootX, 0);
                visual.Root.style.opacity = visual.Visible ? pose.RootOpacity : 0;
                SetTranslate(visual.Portrait, pose.X, pose.Y + (visual.Fainted ? 24 : 0));
                visual.Portrait.style.opacity = visual.Fainted ? 0 : pose.Opacity;
                visual.Portrait.tintColor = Color.Lerp(Color.white, Color.gray, visual.Fainted ? 1 : pose.Gray);
                DrawHp(side);
            }
        }

        void Begin(RunningCue item)
        {
            var cue = item.Cue;
            if (cue.Kind == CueKind.End) return;
            if (!ValidSide(cue.Side)) return;
            var visual = visuals[cue.Side];
            switch (cue.Kind)
            {
                case CueKind.Enter:
                case CueKind.SwitchIn:
                    // 截断 Switch 未提供新人身份，不能拿终态 active 推断而让旧主播重新滑入。
                    if (cue.Kind == CueKind.SwitchIn && !ValidSlot(cue.Side, cue.Slot)) break;
                    int slot = cue.Slot >= 0 ? cue.Slot : after.Active[cue.Side];
                    if (ValidSlot(cue.Side, slot))
                    {
                        display.Active[cue.Side] = slot;
                        var fighter = display.Team[cue.Side][slot];
                        fighter.Hp = SnapshotHp(cue, slot, fighter.Hp);
                        fighter.Fainted = false;
                        ShowFighter(cue.Side);
                    }
                    visual.Visible = true;
                    visual.Fainted = false;
                    break;
                case CueKind.HpBar:
                    if (!IsCurrent(cue)) break;
                    item.HpFrom = visual.Hp;
                    item.HpTo = SnapshotHp(cue, cue.Slot, (int)visual.Hp);
                    break;
                case CueKind.Flash:
                    if (IsCurrent(cue))
                    {
                        item.Element = Overlay("timeline-flash", Color.white);
                        visual.Portrait.Add(item.Element);
                        temporary.Add(item.Element);
                    }
                    break;
                case CueKind.FloatText:
                    item.Element = MakeFloat(cue);
                    break;
                case CueKind.Projectile:
                    // 特效提供者可以在共用层生成节点；只回收本次调用新增的节点，不清掉别人的表现。
                    var existing = new HashSet<VisualElement>(stage.FxLayer.Children());
                    fx.Play(skillFx(cue.Id), visual.Root, visuals[1 - cue.Side].Root, (float)cue.Duration);
                    foreach (var child in stage.FxLayer.Children())
                        if (!existing.Contains(child)) temporary.Add(child);
                    break;
                case CueKind.StatusIcon:
                    BeginChip(item, true);
                    break;
                case CueKind.FieldBanner:
                    BeginChip(item, false);
                    item.Element = MakeBanner(cue);
                    break;
            }
        }

        void UpdateCue(RunningCue item, float t, Pose[] poses)
        {
            var cue = item.Cue;
            if (!ValidSide(cue.Side)) return;
            var pose = poses[cue.Side];
            float direction = cue.Side == 0 ? -1 : 1;
            switch (cue.Kind)
            {
                case CueKind.Enter:
                case CueKind.SwitchIn:
                    if (!item.SuppressEntrance)
                    {
                        pose.RootX += direction * SlideDistance() * (1 - Easing.OutCubic(t));
                        pose.RootOpacity *= Easing.OutCubic(t);
                    }
                    break;
                case CueKind.SwitchOut:
                    if (IsCurrent(cue) && t < 1)
                    {
                        pose.RootX += direction * SlideDistance() * Easing.InCubic(t);
                        pose.RootOpacity *= 1 - Easing.Smooth(t);
                    }
                    break;
                case CueKind.Cast:
                    if (IsCurrent(cue)) pose.X -= direction * 24 * Mathf.Sin(t * Mathf.PI);
                    break;
                case CueKind.Hit:
                    if (IsCurrent(cue)) pose.X += Mathf.Sin(t * Mathf.PI * 6) * 13 * (1 - t);
                    break;
                case CueKind.Miss:
                    // 未命中事件定位施放者，保持其语义，不把它改成对手受击。
                    if (IsCurrent(cue)) pose.X += direction * 12 * Mathf.Sin(t * Mathf.PI);
                    break;
                case CueKind.Flash:
                    if (item.Element != null) item.Element.style.opacity = 0.8f * (1 - t);
                    break;
                case CueKind.FloatText:
                    if (item.Element != null)
                    {
                        SetTranslate(item.Element, 0, -56 * Easing.OutCubic(t));
                        item.Element.style.opacity = 1 - Easing.InCubic(t);
                    }
                    break;
                case CueKind.HpBar:
                    if (IsCurrent(cue))
                    {
                        visuals[cue.Side].Hp = Mathf.Lerp(item.HpFrom, item.HpTo, Easing.OutCubic(t));
                        visuals[cue.Side].TrailHp = Mathf.Lerp(item.HpFrom, item.HpTo,
                            Easing.Smooth(Mathf.Clamp01((t - 0.35f) / 0.65f)));
                    }
                    break;
                case CueKind.Faint:
                    if (IsCurrent(cue))
                    {
                        pose.Gray = Math.Max(pose.Gray, Easing.OutCubic(t));
                        pose.Y += 24 * Easing.InCubic(t);
                        pose.Opacity *= 1 - Easing.InCubic(t);
                    }
                    break;
                case CueKind.StatusIcon:
                    AnimateChip(item, t);
                    break;
                case CueKind.FieldBanner:
                    AnimateChip(item, t);
                    if (item.Element != null)
                    {
                        float visible = t < 0.25f ? Easing.OutCubic(t / 0.25f)
                            : t > 0.7f ? 1 - Easing.InCubic((t - 0.7f) / 0.3f) : 1;
                        SetTranslate(item.Element, 0, -56 * (1 - visible));
                        item.Element.style.opacity = visible;
                    }
                    break;
            }
        }

        void Finish(RunningCue item)
        {
            var cue = item.Cue;
            if (ValidSide(cue.Side))
            {
                if (cue.Kind == CueKind.SwitchOut && IsCurrent(cue)) visuals[cue.Side].Visible = false;
                if (cue.Kind == CueKind.Faint && IsCurrent(cue))
                {
                    visuals[cue.Side].Fainted = true;
                    display.Team[cue.Side][cue.Slot].Fainted = true;
                }
                if (cue.Kind == CueKind.HpBar && IsCurrent(cue)) display.Team[cue.Side][cue.Slot].Hp = (int)item.HpTo;
                if ((cue.Kind == CueKind.StatusIcon || cue.Kind == CueKind.FieldBanner) && item.Removing && item.Chip != null)
                {
                    item.Chip.RemoveFromHierarchy();
                    if (visuals[cue.Side].Chips.TryGetValue(item.ChipKey, out var current) && ReferenceEquals(current, item.Chip))
                        visuals[cue.Side].Chips.Remove(item.ChipKey);
                }
            }
            if (item.Element != null)
            {
                item.Element.RemoveFromHierarchy();
                temporary.Remove(item.Element);
                item.Element = null;
            }
        }

        void BeginChip(RunningCue item, bool status)
        {
            var cue = item.Cue;
            if (!ValidSlot(cue.Side, cue.Slot)) return;
            var kind = cue.EventIndex >= 0 && cue.EventIndex < eventKinds.Count ? eventKinds[cue.EventIndex]
                : cue.Amount == 0 ? (status ? BattleEventKind.StatusRemove : BattleEventKind.FieldRemove)
                : (status ? BattleEventKind.StatusAdd : BattleEventKind.FieldAdd);
            item.Removing = kind == BattleEventKind.StatusRemove || kind == BattleEventKind.FieldRemove;
            item.ChipKey = ChipKey(status, cue.Id);
            var fighter = display.Team[cue.Side][cue.Slot];
            fighter.Chips.RemoveAll(chip => chip.Key == item.ChipKey);
            var data = new ChipData { Key = item.ChipKey, Id = cue.Id, Name = EffectName(cue.Id, status) };
            if (!item.Removing) fighter.Chips.Add(data);
            if (!IsCurrent(cue)) return;
            if (!visuals[cue.Side].Chips.TryGetValue(item.ChipKey, out item.Chip) && !item.Removing)
            {
                item.Chip = MakeChip(data);
                visuals[cue.Side].Chips.Add(item.ChipKey, item.Chip);
                visuals[cue.Side].StatusRow.Add(item.Chip);
            }
        }

        static void AnimateChip(RunningCue item, float t)
        {
            if (item.Chip == null) return;
            float scale = item.Removing ? 1 - Easing.InCubic(t) : Easing.Pop(t);
            item.Chip.style.scale = new Scale(new Vector3(scale, scale, 1));
            item.Chip.style.opacity = item.Removing ? 1 - t : Mathf.Clamp01(scale);
        }

        string EffectName(string id, bool status)
        {
            if (status && id != null && db.Statuses.TryGetValue(id, out var statusDef)) return statusDef.Name;
            if (!status && id != null && db.Fields.TryGetValue(id, out var field)) return field.Name;
            return id ?? string.Empty;
        }

        int SnapshotHp(AnimCue cue, int slot, int fallback)
        {
            // HP 只读引擎生成的逐行快照；Amount 仅供跳字，绝不用于推算战斗数值。
            if (cue.LogIndex < 0 || cue.LogIndex >= logs.Count) return fallback;
            var hp = logs[cue.LogIndex].Hp;
            return hp != null && cue.Side < hp.Length && hp[cue.Side] != null && slot >= 0 && slot < hp[cue.Side].Length
                ? hp[cue.Side][slot] : fallback;
        }

        bool ValidSide(int side) { return side >= 0 && side < 2; }
        bool ValidSlot(int side, int slot) { return ValidSide(side) && display != null && slot >= 0 && slot < display.Team[side].Count; }
        bool IsCurrent(AnimCue cue) { return ValidSlot(cue.Side, cue.Slot) && display.Active[cue.Side] == cue.Slot; }
        static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
        static string ChipKey(bool status, string id) { return (status ? "status/" : "field/") + id; }
        float SlideDistance()
        {
            float width = stage.resolvedStyle.width;
            return float.IsNaN(width) || float.IsInfinity(width) ? 960 : Mathf.Max(500, width);
        }

        void ClearPlayback()
        {
            IsPlaying = false;
            foreach (var element in temporary) element.RemoveFromHierarchy();
            temporary.Clear();
            timeline.Clear();
            elapsed = duration = 0;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            ClearPlayback();
            foreach (var visual in visuals) visual.Dispose();
            // 注入的 provider 和纹理由创建者拥有；只释放本实例默认创建的头像缓存。
            if (ownsPortraits) ((IDisposable)portraits).Dispose();
            before = after = display = null;
            logs.Clear();
            eventKinds.Clear();
        }

        void ThrowIfDisposed()
        {
            if (disposed) throw new ObjectDisposedException(nameof(TimelineAnimator));
        }

        sealed class RunningCue
        {
            public readonly AnimCue Cue;
            public readonly int Order;
            public bool Started, Finished, Removing, SuppressEntrance;
            public float HpFrom, HpTo;
            public VisualElement Element, Chip;
            public string ChipKey;

            public RunningCue(AnimCue cue, int order)
            {
                Cue = new AnimCue { Start = cue.Start, Duration = cue.Duration, Kind = cue.Kind,
                    EventIndex = cue.EventIndex, LogIndex = cue.LogIndex, Side = cue.Side, Slot = cue.Slot,
                    Amount = cue.Amount, Id = cue.Id, Text = cue.Text, Style = cue.Style };
                Order = order;
            }
        }

        sealed class Pose
        {
            public float RootX, X, Y, Gray;
            public float RootOpacity = 1, Opacity = 1;
        }
    }
}
