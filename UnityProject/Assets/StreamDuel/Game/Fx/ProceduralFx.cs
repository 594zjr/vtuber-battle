using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace StreamDuel.Game
{
    public sealed partial class ProceduralFx : IFxPlayer, IDisposable
    {
        readonly VisualElement layer;
        readonly Func<VisualElement, Color> colorFor;
        readonly bool autoTick;
        readonly List<FxElement> active = new List<FxElement>();
        readonly Stack<FxElement> pool = new Stack<FxElement>();
        readonly IVisualElementScheduledItem scheduled;
        double lastTime;
        bool disposed;

        public int ActiveCount { get { return active.Count; } }
        public int CreatedCount { get; private set; }
        public int PooledCount { get { return pool.Count; } }

        // 独立 IFxPlayer 默认自行播放；已有动画时钟的拥有者传 false，统一调用 Tick。
        public ProceduralFx(VisualElement layer, Func<VisualElement, Color> colorFor = null, bool autoTick = true)
        {
            this.layer = layer ?? throw new ArgumentNullException(nameof(layer), "特效层不能为空。");
            this.colorFor = colorFor ?? (_ => new Color(0.25f, 0.85f, 1f));
            this.autoTick = autoTick;
            if (autoTick)
            {
                scheduled = layer.schedule.Execute(AutoTick).Every(16);
                scheduled.Pause();
            }
            layer.RegisterCallback<DetachFromPanelEvent>(OnLayerDetached);
            layer.RegisterCallback<AttachToPanelEvent>(OnLayerAttached);
        }

        public void Play(string fxId, VisualElement from, VisualElement to, float duration)
        {
            ThrowIfDisposed();
            if (!KnownEffect(fxId)) throw new ArgumentException("未知的程序特效编号。", nameof(fxId));
            CheckTime(duration, nameof(duration));
            if (from == null) throw new ArgumentNullException(nameof(from), "施放方节点不能为空。");
            if (to == null) throw new ArgumentNullException(nameof(to), "目标节点不能为空。");
            if (duration == 0) return;
            Color color = colorFor(from);
            if (!Finite(color.r) || !Finite(color.g) || !Finite(color.b) || !Finite(color.a))
                throw new ArgumentException("施放方赛道颜色必须是有限数值。", nameof(colorFor));
            color = new Color(Mathf.Clamp01(color.r), Mathf.Clamp01(color.g), Mathf.Clamp01(color.b), Mathf.Clamp01(color.a));
            if (autoTick && active.Count > 0) AutoTick();
            var element = pool.Count > 0 ? pool.Pop() : NewElement();
            element.Begin(fxId, from, to, duration, color);
            active.Add(element);
            layer.Add(element);
            element.MarkDirtyRepaint();
            if (autoTick)
            {
                lastTime = Time.realtimeSinceStartupAsDouble;
                scheduled.Resume();
            }
        }

        public void Tick(float dt)
        {
            ThrowIfDisposed();
            CheckTime(dt, nameof(dt));
            Advance(dt);
            if (autoTick) lastTime = Time.realtimeSinceStartupAsDouble;
        }

        public void Clear()
        {
            while (active.Count > 0) Release(active[active.Count - 1]);
            scheduled?.Pause();
        }

        public void Dispose()
        {
            if (disposed) return;
            Clear();
            disposed = true;
            layer.UnregisterCallback<DetachFromPanelEvent>(OnLayerDetached);
            layer.UnregisterCallback<AttachToPanelEvent>(OnLayerAttached);
            pool.Clear();
        }

        FxElement NewElement()
        {
            CreatedCount++;
            return new FxElement(Release);
        }

        void AutoTick()
        {
            if (disposed) return;
            double now = Time.realtimeSinceStartupAsDouble;
            double dt = Math.Max(0, now - lastTime);
            lastTime = now;
            Advance(dt);
        }

        void Advance(double dt)
        {
            for (int i = active.Count - 1; i >= 0; i--)
            {
                var element = active[i];
                // 时间线跳过或拥有者移除单个节点，也要归还池，避免保留旧演员引用。
                if (element.parent != layer || dt >= element.Duration - element.Elapsed)
                {
                    Release(element);
                    continue;
                }
                element.Elapsed += dt;
                element.MarkDirtyRepaint();
            }
            if (active.Count == 0) scheduled?.Pause();
        }

        void Release(FxElement element)
        {
            // 先退出活动列表，移除节点触发的 Detach 回调才能安全重入。
            if (!active.Remove(element)) return;
            element.RemoveFromHierarchy();
            element.Reset();
            pool.Push(element);
            if (active.Count == 0) scheduled?.Pause();
        }

        void OnLayerDetached(DetachFromPanelEvent evt)
        {
            if (ReferenceEquals(evt.target, layer)) Clear();
        }

        void OnLayerAttached(AttachToPanelEvent evt)
        {
            if (!ReferenceEquals(evt.target, layer) || !autoTick || active.Count == 0) return;
            lastTime = Time.realtimeSinceStartupAsDouble;
            scheduled.Resume();
        }

        void ThrowIfDisposed()
        {
            if (disposed) throw new ObjectDisposedException(nameof(ProceduralFx), "特效播放器已经释放。");
        }

        static void CheckTime(float value, string parameter)
        {
            if (!Finite(value) || value < 0)
                throw new ArgumentOutOfRangeException(parameter, "特效时间必须是有限的非负秒数。");
        }

        static bool Finite(float value) { return !float.IsNaN(value) && !float.IsInfinity(value); }

        static bool KnownEffect(string id)
        {
            return id == "slash" || id == "burst" || id == "wave" || id == "sparkle"
                || id == "beam" || id == "ring" || id == "shield" || id == "heal";
        }
    }
}
