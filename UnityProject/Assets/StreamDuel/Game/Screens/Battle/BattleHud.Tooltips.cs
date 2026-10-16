using System;
using System.Collections.Generic;
using StreamDuel.Core;
using UnityEngine;
using UnityEngine.UIElements;

namespace StreamDuel.Game
{
    public sealed partial class BattleHud
    {
        readonly Dictionary<VisualElement, Action> tooltipBindings = new Dictionary<VisualElement, Action>();
        VisualElement tooltipPanel, tooltipTarget, pointerTarget, focusedTarget;
        Label tooltipText;
        ScrollView tooltipScroll;
        BattleViewModel tooltipView;
        IVisualElementScheduledItem tooltipHide;
        bool tooltipAllowed = true, overTooltip;

        void BuildTooltip()
        {
            tooltipPanel = new VisualElement { name = "battle-tooltip", pickingMode = PickingMode.Position };
            tooltipPanel.style.position = Position.Absolute;
            tooltipPanel.style.display = DisplayStyle.None;
            tooltipPanel.style.backgroundColor = Panel;
            tooltipPanel.style.width = 560;
            tooltipPanel.style.maxWidth = Length.Percent(90);
            Padding(tooltipPanel, 12);
            Border(tooltipPanel, Accent, 1);
            tooltipScroll = new ScrollView(ScrollViewMode.Vertical) { name = "battle-tooltip-scroll" };
            tooltipScroll.style.minHeight = 0;
            tooltipScroll.style.flexShrink = 1;
            tooltipScroll.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            tooltipScroll.contentContainer.style.minWidth = 0;
            tooltipText = Label("", "battle-tooltip-text", 20);
            tooltipText.style.color = TextColor;
            tooltipText.style.flexShrink = 0;
            tooltipScroll.Add(tooltipText);
            tooltipPanel.Add(tooltipScroll);
            Root.Add(tooltipPanel);

            EventCallback<PointerEnterEvent> enter = _ => { overTooltip = true; tooltipHide?.Pause(); };
            EventCallback<PointerLeaveEvent> leave = _ => { overTooltip = false; ScheduleTooltipHide(); };
            EventCallback<GeometryChangedEvent> geometry = _ => PlaceTooltip();
            EventCallback<PointerMoveEvent> move = OnTooltipPointerMove;
            EventCallback<PointerDownEvent> down = evt =>
            {
                var target = evt.target as VisualElement;
                if (target != null && target != tooltipPanel && !tooltipPanel.Contains(target)) HideTooltip();
            };
            EventCallback<KeyDownEvent> key = evt => { if (evt.keyCode == KeyCode.Escape) HideTooltip(); };
            EventCallback<PointerLeaveEvent> rootLeave = _ => HideTooltip();
            tooltipPanel.RegisterCallback(enter);
            tooltipPanel.RegisterCallback(leave);
            tooltipPanel.RegisterCallback(geometry);
            Root.RegisterCallback(geometry);
            Root.RegisterCallback(move, TrickleDown.TrickleDown);
            Root.RegisterCallback(down, TrickleDown.TrickleDown);
            Root.RegisterCallback(key, TrickleDown.TrickleDown);
            Root.RegisterCallback(rootLeave);
            unbind.Add(() =>
            {
                tooltipPanel.UnregisterCallback(enter);
                tooltipPanel.UnregisterCallback(leave);
                tooltipPanel.UnregisterCallback(geometry);
                Root.UnregisterCallback(geometry);
                Root.UnregisterCallback(move, TrickleDown.TrickleDown);
                Root.UnregisterCallback(down, TrickleDown.TrickleDown);
                Root.UnregisterCallback(key, TrickleDown.TrickleDown);
                Root.UnregisterCallback(rootLeave);
            });
        }

        void BindTooltip(VisualElement element)
        {
            EventCallback<PointerEnterEvent> enter = _ => { pointerTarget = element; ShowTooltip(element); };
            EventCallback<PointerLeaveEvent> leave = _ =>
            {
                if (pointerTarget == element) pointerTarget = null;
                if (focusedTarget == element) focusedTarget = null;
                ScheduleTooltipHide();
            };
            EventCallback<FocusInEvent> focus = _ => { focusedTarget = element; ShowTooltip(element); };
            EventCallback<FocusOutEvent> blur = _ =>
            {
                if (focusedTarget == element) focusedTarget = null;
                if (pointerTarget == element) pointerTarget = null;
                ScheduleTooltipHide();
            };
            element.RegisterCallback(enter);
            element.RegisterCallback(leave);
            element.RegisterCallback(focus);
            element.RegisterCallback(blur);
            tooltipBindings.Add(element, () =>
            {
                element.UnregisterCallback(enter);
                element.UnregisterCallback(leave);
                element.UnregisterCallback(focus);
                element.UnregisterCallback(blur);
            });
        }

        void UnbindTooltip(VisualElement element)
        {
            if (!tooltipBindings.TryGetValue(element, out var release)) return;
            release();
            tooltipBindings.Remove(element);
            if (tooltipTarget == element) HideTooltip();
        }

        void UpdateTooltipState(BattleViewModel view, bool busy)
        {
            tooltipAllowed = !busy && view.Phase != Phase.Over;
            if (!tooltipAllowed || !ReferenceEquals(tooltipView, view)) HideTooltip();
            tooltipView = view;
        }

        void OnTooltipPointerMove(PointerMoveEvent evt)
        {
            if (disposed || !tooltipAllowed) return;
            var position = new Vector2(evt.position.x, evt.position.y);
            if (tooltipPanel.style.display.value != DisplayStyle.None && tooltipPanel.worldBound.Contains(position))
            {
                overTooltip = true;
                tooltipHide?.Pause();
                return;
            }
            overTooltip = false;
            VisualElement target = null;
            // 禁用按钮不会接收自身悬停事件，由仍启用的根节点查找它的提示。
            foreach (var candidate in tooltipBindings.Keys)
                if (!string.IsNullOrEmpty(candidate.tooltip) && IsTooltipHit(candidate, position)
                    && (target == null || target.Contains(candidate))) target = candidate;
            pointerTarget = target;
            focusedTarget = null;
            if (target != null) ShowTooltip(target);
            else ScheduleTooltipHide();
        }

        bool IsTooltipHit(VisualElement element, Vector2 position)
        {
            if (!element.worldBound.Contains(position)) return false;
            for (var parent = element; parent != null; parent = parent.parent)
            {
                if (!parent.visible || parent.resolvedStyle.display == DisplayStyle.None) return false;
                if (parent is ScrollView scroll && !scroll.contentViewport.worldBound.Contains(position)) return false;
                if (parent == Root) return true;
            }
            return false;
        }

        void ShowTooltip(VisualElement target)
        {
            if (disposed || !tooltipAllowed || tooltipPanel == null) return;
            if (string.IsNullOrEmpty(target.tooltip)) { HideTooltip(); return; }
            tooltipHide?.Pause();
            if (tooltipTarget != target) tooltipScroll.scrollOffset = Vector2.zero;
            tooltipTarget = target;
            tooltipText.text = target.tooltip;
            tooltipPanel.style.display = DisplayStyle.Flex;
            float availableWidth = Mathf.Max(1, Root.contentRect.width - 24);
            tooltipPanel.style.width = Mathf.Min(560, availableWidth);
            tooltipScroll.style.maxHeight = Mathf.Min(360, Mathf.Max(1, Root.contentRect.height - 50));
            PlaceTooltip();
        }

        void PlaceTooltip()
        {
            if (disposed || tooltipTarget == null || tooltipPanel.style.display.value == DisplayStyle.None) return;
            float width = tooltipPanel.layout.width, height = tooltipPanel.layout.height;
            if (float.IsNaN(width) || float.IsNaN(height)) return;
            var anchor = Root.WorldToLocal(tooltipTarget.worldBound.min);
            var bottom = Root.WorldToLocal(tooltipTarget.worldBound.max);
            float maxX = Mathf.Max(12, Root.layout.width - width - 12);
            float maxY = Mathf.Max(12, Root.layout.height - height - 12);
            float top = bottom.y + 8;
            if (top > maxY) top = anchor.y - height - 8;
            tooltipPanel.style.left = Mathf.Clamp(anchor.x, 12, maxX);
            tooltipPanel.style.top = Mathf.Clamp(top, 12, maxY);
        }

        void ScheduleTooltipHide()
        {
            if (disposed || tooltipPanel == null) return;
            if (tooltipHide != null && tooltipHide.isActive) return;
            tooltipHide = Root.schedule.Execute(() =>
            {
                if (disposed || overTooltip) return;
                if (pointerTarget != null) ShowTooltip(pointerTarget);
                else if (focusedTarget != null) ShowTooltip(focusedTarget);
                else HideTooltip();
            }).StartingIn(120);
        }

        void HideTooltip()
        {
            tooltipHide?.Pause();
            tooltipTarget = pointerTarget = focusedTarget = null;
            overTooltip = false;
            if (tooltipPanel != null) tooltipPanel.style.display = DisplayStyle.None;
        }

        void DisposeTooltip()
        {
            HideTooltip();
            foreach (var release in tooltipBindings.Values) release();
            tooltipBindings.Clear();
            tooltipView = null;
        }
    }
}
