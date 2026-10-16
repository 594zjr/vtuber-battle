using System;
using System.Globalization;
using StreamDuel.Core;
using UnityEngine;
using UnityEngine.UIElements;

namespace StreamDuel.Game
{
    public interface IStatusDurationIconProvider : IStatusIconProvider
    {
        VisualElement Make(string iconId, string label, int? remaining);
    }

    public sealed class ProceduralIcons : IStatusDurationIconProvider
    {
        readonly ContentDb db;

        public ProceduralIcons(ContentDb db = null)
        {
            this.db = db;
        }

        public VisualElement Make(string iconId, string label)
        {
            return Make(iconId, label, null);
        }

        public VisualElement Make(string iconId, string label, int? remaining)
        {
            string resolved = Resolve(iconId);
            string text = string.IsNullOrEmpty(label) ? DefaultLabel(resolved) : label;
            var root = new VisualElement { name = "procedural-icon", tooltip = text, pickingMode = PickingMode.Ignore };
            root.AddToClassList("status-icon");
            root.AddToClassList("procedural-status-icon");
            root.style.flexDirection = FlexDirection.Row;
            root.style.alignItems = Align.Center;
            root.style.flexShrink = 0;
            root.style.maxWidth = 230;
            root.style.paddingLeft = root.style.paddingRight = 5;
            root.style.paddingTop = root.style.paddingBottom = 3;
            root.style.marginRight = 6;
            root.style.marginBottom = 5;
            root.style.backgroundColor = new Color(0.10f, 0.14f, 0.23f, 0.96f);
            root.style.borderTopLeftRadius = root.style.borderTopRightRadius = 8;
            root.style.borderBottomLeftRadius = root.style.borderBottomRightRadius = 8;

            var box = new VisualElement { name = "procedural-icon-frame", pickingMode = PickingMode.Ignore };
            box.style.width = 36;
            box.style.height = 36;
            box.style.flexShrink = 0;
            var glyph = new IconGlyph(resolved) { name = "procedural-icon-glyph" };
            glyph.AddToClassList("procedural-icon-" + resolved);
            box.Add(glyph);
            var badge = new Label
            {
                name = "procedural-icon-remaining",
                enableRichText = false,
                pickingMode = PickingMode.Ignore
            };
            badge.style.position = Position.Absolute;
            badge.style.right = -2;
            badge.style.bottom = -1;
            badge.style.minWidth = 17;
            // 共用中文字体的行高大于字号，角标仍需保留完整一行的空间。
            badge.style.height = 24;
            badge.style.marginLeft = badge.style.marginRight = badge.style.marginTop = badge.style.marginBottom = 0;
            badge.style.paddingTop = badge.style.paddingBottom = 0;
            badge.style.paddingLeft = badge.style.paddingRight = 3;
            badge.style.fontSize = 11;
            badge.style.unityFontStyleAndWeight = FontStyle.Bold;
            badge.style.unityTextAlign = TextAnchor.MiddleCenter;
            badge.style.whiteSpace = WhiteSpace.NoWrap;
            badge.style.color = Color.white;
            badge.style.backgroundColor = new Color(0.04f, 0.06f, 0.12f, 1);
            badge.style.borderTopLeftRadius = badge.style.borderTopRightRadius = 6;
            badge.style.borderBottomLeftRadius = badge.style.borderBottomRightRadius = 6;
            box.Add(badge);
            root.Add(box);
            var title = new Label(text)
            {
                name = "procedural-icon-label",
                tooltip = text,
                enableRichText = false,
                pickingMode = PickingMode.Ignore
            };
            title.style.marginLeft = 7;
            title.style.fontSize = 15;
            title.style.color = new Color(0.92f, 0.95f, 1);
            title.style.whiteSpace = WhiteSpace.Normal;
            title.style.flexShrink = 1;
            root.Add(title);
            UpdateRemaining(root, remaining);
            return root;
        }

        public static void UpdateRemaining(VisualElement icon, int? remaining)
        {
            if (icon == null) throw new ArgumentNullException(nameof(icon));
            var badge = icon.Q<Label>("procedural-icon-remaining");
            if (badge == null) return;
            bool visible = remaining.HasValue && remaining.Value >= 0;
            badge.text = visible ? remaining.Value.ToString(CultureInfo.InvariantCulture) : string.Empty;
            badge.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
        }

        string Resolve(string iconId)
        {
            // 同名状态的包内覆盖优先；图标总览使用不带内容库的提供者即可直绘规范图形。
            if (iconId != null && db != null && (db.Statuses.ContainsKey(iconId) || db.Fields.ContainsKey(iconId)))
                iconId = db.Art.StatusIcon(iconId);
            switch (iconId)
            {
                case "lock": case "down": case "up": case "shield":
                case "mark": case "clock": case "heart": case "star": return iconId;
                default: return "unknown";
            }
        }

        static string DefaultLabel(string id)
        {
            switch (id)
            {
                case "lock": return "控制";
                case "down": return "弱化";
                case "up": return "强化";
                case "shield": return "保护";
                case "mark": return "印记";
                case "clock": return "时限效果";
                case "heart": return "回复";
                case "star": return "增益";
                default: return "未知效果";
            }
        }

        sealed class IconGlyph : VisualElement
        {
            readonly string id;
            static readonly Color Ink = new Color(0.08f, 0.11f, 0.18f, 1);

            public IconGlyph(string id)
            {
                this.id = id;
                pickingMode = PickingMode.Ignore;
                style.width = style.height = 36;
                generateVisualContent += Draw;
            }

            void Draw(MeshGenerationContext context)
            {
                var pen = context.painter2D;
                Color color = Tint(id);
                pen.lineWidth = 2.4f;
                pen.strokeColor = color;
                pen.fillColor = color;
                switch (id)
                {
                    case "lock":
                        Path(pen, false, new Vector2(11, 16), new Vector2(11, 10), new Vector2(13, 6),
                            new Vector2(18, 5), new Vector2(23, 6), new Vector2(25, 10), new Vector2(25, 16));
                        Polygon(pen, new Vector2(8, 15), new Vector2(28, 15), new Vector2(28, 29), new Vector2(8, 29));
                        pen.fillColor = Ink;
                        Circle(pen, 18, 21, 2.4f, true);
                        pen.strokeColor = Ink;
                        Path(pen, false, new Vector2(18, 21), new Vector2(18, 26));
                        break;
                    case "down":
                        Polygon(pen, new Vector2(14, 6), new Vector2(22, 6), new Vector2(22, 19),
                            new Vector2(29, 19), new Vector2(18, 31), new Vector2(7, 19), new Vector2(14, 19));
                        break;
                    case "up":
                        Polygon(pen, new Vector2(14, 30), new Vector2(22, 30), new Vector2(22, 17),
                            new Vector2(29, 17), new Vector2(18, 5), new Vector2(7, 17), new Vector2(14, 17));
                        break;
                    case "shield":
                        Polygon(pen, new Vector2(18, 4), new Vector2(29, 9), new Vector2(27, 23),
                            new Vector2(23, 28), new Vector2(18, 32), new Vector2(13, 28), new Vector2(9, 23), new Vector2(7, 9));
                        pen.strokeColor = Ink;
                        Path(pen, false, new Vector2(18, 10), new Vector2(18, 25));
                        break;
                    case "mark":
                        Polygon(pen, new Vector2(18, 3), new Vector2(31, 18), new Vector2(18, 32), new Vector2(5, 18));
                        pen.strokeColor = Ink;
                        Circle(pen, 18, 18, 5, false);
                        break;
                    case "clock":
                        Circle(pen, 18, 18, 13, false);
                        Path(pen, false, new Vector2(18, 9), new Vector2(18, 18), new Vector2(25, 22));
                        break;
                    case "heart":
                        Polygon(pen, new Vector2(18, 11), new Vector2(13, 6), new Vector2(8, 6), new Vector2(4, 11),
                            new Vector2(4, 17), new Vector2(18, 30), new Vector2(32, 17), new Vector2(32, 11),
                            new Vector2(28, 6), new Vector2(23, 6));
                        break;
                    case "star":
                        pen.BeginPath();
                        for (int i = 0; i < 10; i++)
                        {
                            float angle = -Mathf.PI / 2 + i * Mathf.PI / 5;
                            float radius = i % 2 == 0 ? 14 : 6;
                            var point = new Vector2(18 + Mathf.Cos(angle) * radius, 18 + Mathf.Sin(angle) * radius);
                            if (i == 0) pen.MoveTo(point); else pen.LineTo(point);
                        }
                        pen.ClosePath();
                        pen.Fill();
                        break;
                    default:
                        Path(pen, true, new Vector2(18, 4), new Vector2(31, 18), new Vector2(18, 31), new Vector2(5, 18));
                        Circle(pen, 18, 18, 3, true);
                        break;
                }
            }

            static Color Tint(string id)
            {
                switch (id)
                {
                    case "lock": return new Color(0.85f, 0.68f, 1);
                    case "down": return new Color(1, 0.51f, 0.60f);
                    case "up": return new Color(0.40f, 0.86f, 1);
                    case "shield": return new Color(0.44f, 0.68f, 1);
                    case "mark": return new Color(1, 0.69f, 0.32f);
                    case "clock": return new Color(0.76f, 0.84f, 0.96f);
                    case "heart": return new Color(0.43f, 0.92f, 0.70f);
                    case "star": return new Color(1, 0.85f, 0.39f);
                    default: return new Color(0.69f, 0.74f, 0.83f);
                }
            }

            static void Polygon(Painter2D pen, params Vector2[] points)
            {
                pen.BeginPath();
                pen.MoveTo(points[0]);
                for (int i = 1; i < points.Length; i++) pen.LineTo(points[i]);
                pen.ClosePath();
                pen.Fill();
            }

            static void Path(Painter2D pen, bool closed, params Vector2[] points)
            {
                pen.BeginPath();
                pen.MoveTo(points[0]);
                for (int i = 1; i < points.Length; i++) pen.LineTo(points[i]);
                if (closed) pen.ClosePath();
                pen.Stroke();
            }

            static void Circle(Painter2D pen, float x, float y, float radius, bool fill)
            {
                pen.BeginPath();
                for (int i = 0; i < 32; i++)
                {
                    float angle = i * Mathf.PI / 16;
                    var point = new Vector2(x + Mathf.Cos(angle) * radius, y + Mathf.Sin(angle) * radius);
                    if (i == 0) pen.MoveTo(point); else pen.LineTo(point);
                }
                pen.ClosePath();
                if (fill) pen.Fill(); else pen.Stroke();
            }
        }
    }
}
