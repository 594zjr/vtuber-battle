using System;
using System.Collections.Generic;
using StreamDuel.Core;
using UnityEngine;

namespace StreamDuel.Game
{
    public sealed class ProceduralPortraitProvider : IPortraitProvider, IDisposable
    {
        const int Size = 128;
        readonly Dictionary<string, Texture2D> resources = new Dictionary<string, Texture2D>(StringComparer.Ordinal);
        readonly Dictionary<(string Path, string Id, string Main, string Accent), Texture2D> generated
            = new Dictionary<(string, string, string, string), Texture2D>();
        bool disposed;

        public Texture2D Get(ContentDb db, string streamerId)
        {
            if (disposed) throw new ObjectDisposedException(nameof(ProceduralPortraitProvider));
            if (db == null) throw new ArgumentNullException(nameof(db));
            if (streamerId == null) throw new ArgumentNullException(nameof(streamerId));
            var streamer = db.Streamer(streamerId);
            string path = db.Art.PortraitPath(streamerId);
            if (!resources.TryGetValue(path, out var borrowed))
            {
                borrowed = Resources.Load<Texture2D>(path);
                // 缺失资源也记住，重复显示同一角色时不反复向 Resources 查询。
                resources.Add(path, borrowed);
            }
            if (borrowed != null) return borrowed;

            string main = db.Types[streamer.Type].Color;
            string accent = db.Art.Accent(streamerId);
            var key = (path, streamerId, main, accent);
            if (generated.TryGetValue(key, out var texture) && texture != null) return texture;
            ColorUtility.TryParseHtmlString(main, out var mainColor);
            ColorUtility.TryParseHtmlString(accent, out var accentColor);
            texture = Make(streamerId, mainColor, accentColor);
            generated[key] = texture;
            return texture;
        }

        static Texture2D Make(string id, Color main, Color accent)
        {
            uint identity = StableIdentity(id);
            var pixels = new Color32[Size * Size];
            Color background = Color.Lerp(new Color(0.04f, 0.06f, 0.11f), main, 0.22f);
            Color outline = Color.Lerp(new Color(0.07f, 0.10f, 0.17f), main, 0.18f);
            Color face = Color.Lerp(accent, Color.white, 0.82f);
            Color hair = Color.Lerp(main, outline, 0.5f);
            for (int y = 0; y < Size; y++)
                for (int x = 0; x < Size; x++)
                {
                    Color sum = Color.clear;
                    // 固定四点覆盖让小尺寸轮廓平滑，不需要随机采样或字体纹理。
                    for (int sy = 0; sy < 2; sy++)
                        for (int sx = 0; sx < 2; sx++)
                        {
                            float u = (x + (sx + 0.5f) / 2) / Size;
                            float v = (y + (sy + 0.5f) / 2) / Size;
                            sum += Pixel(u, v, identity, background, outline, face, hair, main, accent);
                        }
                    pixels[y * Size + x] = sum * 0.25f;
                }
            var texture = new Texture2D(Size, Size, TextureFormat.RGBA32, false)
            {
                name = "StreamDuel.ProceduralPortrait." + id,
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            return texture;
        }

        static Color Pixel(float x, float y, uint identity, Color background, Color outline,
            Color face, Color hair, Color main, Color accent)
        {
            Color color = background;
            if (x + y > 1.15f) color = Color.Lerp(background, main, 0.24f);
            float ring = Distance(x, y, 0.5f, 0.53f);
            if (ring > 0.39f && ring < 0.42f) color = Color.Lerp(main, accent, 0.5f);
            if (Ellipse(x, y, 0.5f, 0.13f, 0.36f, 0.25f)) color = outline;
            if (Ellipse(x, y, 0.5f, 0.12f, 0.31f, 0.22f)) color = main;
            if (Mathf.Abs(x - 0.5f) < 0.075f && y > 0.27f && y < 0.43f) color = face;
            if (Ellipse(x, y, 0.5f, 0.60f, 0.225f, 0.265f)) color = hair;
            if (Ellipse(x, y, 0.5f, 0.56f, 0.185f, 0.22f)) color = face;

            // 仅用 id 的稳定摘要选择几何刘海和配饰，角色区分不影响任何对战随机流。
            int style = (int)(identity % 3);
            if (Ellipse(x, y, 0.5f, 0.68f, 0.215f, 0.17f)
                && y > (style == 0 ? 0.64f + (x - 0.5f) * 0.5f
                    : style == 1 ? 0.64f - (x - 0.5f) * 0.5f : 0.66f + Mathf.Abs(x - 0.5f) * 0.35f)) color = hair;
            if (Ellipse(x, y, 0.43f, 0.56f, 0.019f, 0.026f)
                || Ellipse(x, y, 0.57f, 0.56f, 0.019f, 0.026f)) color = outline;
            if (Mathf.Abs(x - 0.5f) < 0.035f && Mathf.Abs(y - 0.47f) < 0.009f) color = outline;

            float headset = Distance(x, y, 0.5f, 0.58f);
            if (headset > 0.235f && headset < 0.261f && y > 0.58f) color = accent;
            if (Ellipse(x, y, 0.265f, 0.55f, 0.035f, 0.073f)
                || Ellipse(x, y, 0.735f, 0.55f, 0.035f, 0.073f)) color = accent;
            if (Mathf.Abs(x - 0.70f) < 0.011f && y > 0.44f && y < 0.53f) color = accent;
            if (Mathf.Abs(y - 0.44f) < 0.011f && x > 0.60f && x < 0.71f) color = accent;
            if (Ellipse(x, y, 0.60f, 0.44f, 0.027f, 0.018f)) color = outline;

            float emblemX = (identity & 4) == 0 ? 0.78f : 0.22f;
            float diamond = Mathf.Abs(x - emblemX) + Mathf.Abs(y - 0.83f);
            if (diamond < 0.055f) color = accent;
            if (Mathf.Abs(x - 0.5f) < 0.025f && y > 0.08f && y < 0.22f) color = accent;
            color.a = 1;
            return color;
        }

        static bool Ellipse(float x, float y, float centerX, float centerY, float radiusX, float radiusY)
        {
            float dx = (x - centerX) / radiusX, dy = (y - centerY) / radiusY;
            return dx * dx + dy * dy <= 1;
        }

        static float Distance(float x, float y, float centerX, float centerY)
        {
            float dx = x - centerX, dy = y - centerY;
            return Mathf.Sqrt(dx * dx + dy * dy);
        }

        static uint StableIdentity(string id)
        {
            uint hash = 2166136261;
            foreach (char value in id) hash = unchecked((hash ^ value) * 16777619);
            return hash;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            // Resources 资源只借用；只释放本提供者自行分配的几何头像。
            foreach (var texture in generated.Values)
            {
                if (texture == null) continue;
                if (Application.isPlaying) UnityEngine.Object.Destroy(texture);
                else UnityEngine.Object.DestroyImmediate(texture);
            }
            generated.Clear();
            resources.Clear();
        }
    }
}
