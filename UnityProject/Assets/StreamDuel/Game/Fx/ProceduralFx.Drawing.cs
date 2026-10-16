using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace StreamDuel.Game
{
    public sealed partial class ProceduralFx
    {
        sealed class FxElement : VisualElement
        {
            readonly Action<FxElement> detached;
            string id;
            VisualElement from, to;
            Color color;
            public double Duration, Elapsed;

            public FxElement(Action<FxElement> detached)
            {
                this.detached = detached;
                pickingMode = PickingMode.Ignore;
                style.position = Position.Absolute;
                style.left = style.right = style.top = style.bottom = 0;
                generateVisualContent += Draw;
                RegisterCallback<DetachFromPanelEvent>(OnDetached);
            }

            public void Begin(string effect, VisualElement source, VisualElement target, float duration, Color tint)
            {
                id = effect;
                name = "procedural-fx-" + effect;
                from = source;
                to = target;
                color = tint;
                Duration = duration;
                Elapsed = 0;
            }

            public void Reset()
            {
                id = null;
                from = to = null;
                Duration = Elapsed = 0;
            }

            void OnDetached(DetachFromPanelEvent evt)
            {
                if (ReferenceEquals(evt.target, this)) detached(this);
            }

            void Draw(MeshGenerationContext context)
            {
                if (from == null || to == null || Duration <= 0) return;
                Vector2 source = this.WorldToLocal(from.worldBound.center);
                Vector2 target = this.WorldToLocal(to.worldBound.center);
                if (!Finite(source.x) || !Finite(source.y) || !Finite(target.x) || !Finite(target.y)) return;
                float t = Mathf.Clamp01((float)(Elapsed / Duration));
                Vector2 targetMin = this.WorldToLocal(to.worldBound.min);
                Vector2 targetMax = this.WorldToLocal(to.worldBound.max);
                float size = Mathf.Clamp(Mathf.Min(Mathf.Abs(targetMax.x - targetMin.x),
                    Mathf.Abs(targetMax.y - targetMin.y)) * 0.45f, 24, 64);
                if (!Finite(size)) return;
                var painter = context.painter2D;
                switch (id)
                {
                    case "slash": Slash(painter, target, size, t); break;
                    case "burst": Burst(painter, target, size, t); break;
                    case "wave": Wave(painter, source, target, size, t); break;
                    case "sparkle": Sparkle(painter, source, size, t); break;
                    case "beam": Beam(painter, source, target, size, t); break;
                    case "ring": Ring(painter, target, size, t); break;
                    case "shield": Shield(painter, source, target, size, t); break;
                    case "heal": Heal(painter, source, size, t); break;
                }
            }

            Color Tint(float alpha, float white = 0)
            {
                Color result = Color.Lerp(color, Color.white, white);
                result.a = color.a * Mathf.Clamp01(alpha);
                return result;
            }

            static float Fade(float t) { return Mathf.Clamp01((1 - t) * 3); }

            void Slash(Painter2D p, Vector2 center, float size, float t)
            {
                Vector2 direction = new Vector2(0.7071f, -0.7071f);
                Vector2 normal = new Vector2(0.7071f, 0.7071f);
                float reach = size * (0.4f + Mathf.Min(1, t * 3) * 1.1f);
                float width = size * 0.17f * (1 - t * 0.65f);
                p.fillColor = Tint(Fade(t) * 0.25f);
                Diamond(p, center, direction, normal, reach * 1.12f, width * 2.3f);
                p.fillColor = Tint(Fade(t));
                Diamond(p, center, direction, normal, reach, width);
                p.strokeColor = Tint(Fade(t), 0.75f);
                p.lineWidth = 2;
                Line(p, center - direction * reach * 0.7f, center + direction * reach * 0.95f);
            }

            void Burst(Painter2D p, Vector2 center, float size, float t)
            {
                float outer = size * (0.3f + t * 1.35f);
                float inner = outer * (0.15f + 0.55f * t);
                p.strokeColor = Tint(Fade(t));
                p.lineWidth = 3.5f * (1 - t) + 1;
                for (int i = 0; i < 12; i++)
                {
                    Vector2 direction = Unit(i * Mathf.PI / 6);
                    float scale = i % 2 == 0 ? 1 : 0.73f;
                    Line(p, center + direction * inner, center + direction * outer * scale);
                }
                p.fillColor = Tint(Fade(t) * (1 - t), 0.4f);
                Star(p, center, size * 0.35f * (1 - t), 0);
            }

            void Wave(Painter2D p, Vector2 from, Vector2 to, float size, float t)
            {
                Vector2 direction = Direction(from, to);
                Vector2 normal = new Vector2(-direction.y, direction.x);
                for (int i = 0; i < 3; i++)
                {
                    float progress = Mathf.Clamp01(t * 1.45f - i * 0.17f);
                    float visibility = Mathf.Clamp01((t * 1.45f - i * 0.17f) * 8) * (1 - progress);
                    Vector2 center = Vector2.Lerp(from, to, progress);
                    p.strokeColor = Tint(visibility, i * 0.12f);
                    p.lineWidth = 3.5f;
                    p.BeginPath();
                    for (int j = 0; j <= 16; j++)
                    {
                        float angle = -Mathf.PI * 0.42f + Mathf.PI * 0.84f * j / 16;
                        Vector2 point = center + direction * Mathf.Cos(angle) * size * 0.3f
                            + normal * Mathf.Sin(angle) * size * (0.4f + progress * 0.6f);
                        if (j == 0) p.MoveTo(point); else p.LineTo(point);
                    }
                    p.Stroke();
                }
            }

            void Sparkle(Painter2D p, Vector2 center, float size, float t)
            {
                for (int i = 0; i < 7; i++)
                {
                    float pulse = 0.35f + 0.65f * Mathf.Pow(Mathf.Sin(t * Mathf.PI * 2 + i * 0.8f), 2);
                    Vector2 position = center + Unit(i * Mathf.PI * 2 / 7 - 0.5f) * size * (0.65f + (i % 2) * 0.35f);
                    p.fillColor = Tint(Fade(t) * pulse, i % 2 == 0 ? 0.2f : 0.6f);
                    Star(p, position, size * (0.1f + 0.13f * pulse), t * 0.3f);
                }
            }

            void Beam(Painter2D p, Vector2 from, Vector2 to, float size, float t)
            {
                float visibility = Mathf.Min(1, t * 8 + 0.15f) * Fade(t);
                float pulse = 0.8f + 0.2f * Mathf.Sin(t * Mathf.PI * 6);
                p.strokeColor = Tint(visibility * 0.2f);
                p.lineWidth = size * 0.35f * pulse;
                Line(p, from, to);
                p.strokeColor = Tint(visibility * 0.9f);
                p.lineWidth = size * 0.12f * pulse;
                Line(p, from, to);
                p.strokeColor = Tint(visibility, 0.8f);
                p.lineWidth = 2;
                Line(p, from, to);
                p.fillColor = Tint(visibility, 0.5f);
                Star(p, from, size * 0.23f * pulse, 0);
                Star(p, to, size * 0.32f * pulse, Mathf.PI / 4);
            }

            void Ring(Painter2D p, Vector2 center, float size, float t)
            {
                p.strokeColor = Tint(Fade(t));
                p.lineWidth = 4 * (1 - t) + 1;
                Circle(p, center, size * (0.2f + 1.3f * t), false);
                p.strokeColor = Tint(Fade(t) * 0.4f, 0.4f);
                p.lineWidth = 2;
                Circle(p, center, size * (0.1f + t), false);
            }

            void Shield(Painter2D p, Vector2 from, Vector2 to, float size, float t)
            {
                Vector2 direction = Direction(from, to);
                Vector2 normal = new Vector2(-direction.y, direction.x);
                Vector2 center = from + direction * size * 0.65f;
                float extent = size * (0.65f + Mathf.Min(1, t * 4) * 0.2f);
                p.BeginPath();
                p.MoveTo(center - normal * extent - direction * extent * 0.2f);
                p.LineTo(center - normal * extent * 0.8f + direction * extent * 0.35f);
                p.LineTo(center + direction * extent * 0.6f);
                p.LineTo(center + normal * extent * 0.8f + direction * extent * 0.35f);
                p.LineTo(center + normal * extent - direction * extent * 0.2f);
                p.ClosePath();
                p.fillColor = Tint(Fade(t) * 0.24f);
                p.Fill();
                p.strokeColor = Tint(Fade(t));
                p.lineWidth = 3;
                p.Stroke();
                p.strokeColor = Tint(Fade(t) * 0.75f, 0.4f);
                p.lineWidth = 2;
                Line(p, center - normal * extent * 0.55f, center + normal * extent * 0.55f);
            }

            void Heal(Painter2D p, Vector2 center, float size, float t)
            {
                for (int i = 0; i < 9; i++)
                {
                    float phase = (t + i * 0.11f) % 1;
                    float x = (i % 3 - 1) * size * 0.48f + Mathf.Sin(i * 2.2f + t * 2) * size * 0.12f;
                    Vector2 position = center + new Vector2(x, size * 0.65f - phase * size * 1.9f);
                    float visibility = Mathf.Sin(phase * Mathf.PI) * Fade(t);
                    p.fillColor = Tint(visibility, 0.25f + (i % 2) * 0.35f);
                    Circle(p, position, size * (0.045f + (i % 3) * 0.017f), true);
                    if (i % 3 == 0) Star(p, position, size * 0.13f, 0);
                }
            }

            static Vector2 Direction(Vector2 from, Vector2 to)
            {
                Vector2 difference = to - from;
                return difference.sqrMagnitude > 0.001f ? difference.normalized : Vector2.right;
            }

            static Vector2 Unit(float angle) { return new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)); }

            static void Line(Painter2D p, Vector2 from, Vector2 to)
            {
                p.BeginPath();
                p.MoveTo(from);
                p.LineTo(to);
                p.Stroke();
            }

            static void Circle(Painter2D p, Vector2 center, float radius, bool fill)
            {
                p.BeginPath();
                for (int i = 0; i < 40; i++)
                {
                    Vector2 point = center + Unit(i * Mathf.PI * 2 / 40) * radius;
                    if (i == 0) p.MoveTo(point); else p.LineTo(point);
                }
                p.ClosePath();
                if (fill) p.Fill(); else p.Stroke();
            }

            static void Star(Painter2D p, Vector2 center, float radius, float rotation)
            {
                p.BeginPath();
                for (int i = 0; i < 8; i++)
                {
                    Vector2 point = center + Unit(rotation + i * Mathf.PI / 4) * radius * (i % 2 == 0 ? 1 : 0.28f);
                    if (i == 0) p.MoveTo(point); else p.LineTo(point);
                }
                p.ClosePath();
                p.Fill();
            }

            static void Diamond(Painter2D p, Vector2 center, Vector2 direction, Vector2 normal, float length, float width)
            {
                p.BeginPath();
                p.MoveTo(center - direction * length);
                p.LineTo(center + normal * width);
                p.LineTo(center + direction * length);
                p.LineTo(center - normal * width);
                p.ClosePath();
                p.Fill();
            }
        }
    }
}
