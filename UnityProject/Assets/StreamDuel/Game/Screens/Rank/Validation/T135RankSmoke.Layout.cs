using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace StreamDuel.Game
{
    public sealed partial class T135RankSmoke
    {
        void ValidateLayout()
        {
            var confirmation = router.Document.rootVisualElement.Q<VisualElement>("rank-confirmation");
            bool modal = confirmation != null && Visible(confirmation);
            var page = modal ? confirmation : Element<VisualElement>("rank-screen");
            ValidateTextLayout(page);
            Rect screen = router.Document.rootVisualElement.worldBound;
            string[] pinned = modal ? new[] { "rank-confirm", "rank-cancel" }
                : new[] { "rank-back", "rank-refresh", "rank-new-season", "rank-season", "rank-rating", "rank-tier", "rank-next", "rank-games", "rank-placement" };
            foreach (string name in pinned)
            {
                var element = Element<VisualElement>(name);
                Require(Visible(element) && element.worldBound.width > 2 && element.worldBound.height > 2 && Contains(screen, element.worldBound),
                    "排位常驻信息或按钮不完整可见：" + name);
            }
        }

        void ValidateTextLayout(VisualElement page)
        {
            var texts = new List<TextElement>();
            page.Query<TextElement>().ForEach(text => { if (Visible(text) && !string.IsNullOrWhiteSpace(text.text)) texts.Add(text); });
            Rect screen = router.Document.rootVisualElement.worldBound;
            var displayed = new List<TextElement>();
            var bounds = new List<Rect>();
            foreach (var text in texts)
            {
                Rect rect = text.worldBound, clip = screen;
                bool scrolling = false;
                for (var ancestor = text.parent; ancestor != null; ancestor = ancestor.parent)
                    if (ancestor is ScrollView scroll)
                    {
                        clip = Intersection(clip, scroll.contentViewport.worldBound);
                        scrolling = true;
                    }
                Rect seen = Intersection(rect, clip);
                if (seen.width <= 0 || seen.height <= 0) continue;
                Require(Finite(rect.x) && Finite(rect.y) && Finite(rect.width) && Finite(rect.height)
                    && rect.width > 0 && rect.height > 0, "排位文字布局无效：" + text.name);
                Require(scrolling ? rect.xMin >= clip.xMin - 2 && rect.xMax <= clip.xMax + 2 : Contains(screen, rect),
                    "排位文字超出屏幕可读范围：" + text.name + " " + text.text);
                var content = text.contentRect;
                Vector2 measured = text.MeasureTextSize(text.text, content.width,
                    text.resolvedStyle.whiteSpace == WhiteSpace.Normal ? VisualElement.MeasureMode.Exactly : VisualElement.MeasureMode.Undefined,
                    0, VisualElement.MeasureMode.Undefined);
                Require(!text.isElided && measured.x <= content.width + 2 && measured.y <= content.height + 2,
                    "排位文字可能裁切：" + text.name + " measured=" + measured + " content=" + content + " " + text.text);
                report.bounds.Add(new BoundsEntry
                {
                    name = text.name, text = text.text, width = RequestedWidth, height = RequestedHeight,
                    x = rect.x, y = rect.y, w = rect.width, h = rect.height
                });
                displayed.Add(text);
                bounds.Add(seen);
            }
            Require(displayed.Count > 0, "排位页面没有可见文字。");
            for (int i = 0; i < displayed.Count; i++)
                for (int j = i + 1; j < displayed.Count; j++)
                {
                    if (displayed[i].Contains(displayed[j]) || displayed[j].Contains(displayed[i])) continue;
                    Rect overlap = Intersection(bounds[i], bounds[j]);
                    Require(overlap.width <= 1 || overlap.height <= 1,
                        "排位可见文字重叠：" + displayed[i].name + " / " + displayed[j].name);
                }
        }
    }
}
