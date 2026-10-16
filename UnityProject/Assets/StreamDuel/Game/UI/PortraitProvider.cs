using System;
using System.Collections.Generic;
using System.Globalization;
using StreamDuel.Core;
using UnityEngine;

namespace StreamDuel.Game
{
    public sealed class PortraitProvider : IPortraitProvider, IDisposable
    {
        const int PortraitSize = 128;
        const int GlyphSize = 80;
        const int GlyphBox = 84;
        readonly Font font;
        readonly Dictionary<(string Path, string Accent, string Initial), Texture2D> cache =
            new Dictionary<(string Path, string Accent, string Initial), Texture2D>();
        readonly HashSet<Texture2D> generated = new HashSet<Texture2D>();
        bool disposed;

        public PortraitProvider(Font font)
        {
            if (font == null) throw new ArgumentNullException(nameof(font));
            this.font = font;
        }

        public Texture2D Get(ContentDb db, string streamerId)
        {
            if (disposed) throw new ObjectDisposedException(nameof(PortraitProvider));
            if (db == null) throw new ArgumentNullException(nameof(db));
            if (streamerId == null) throw new ArgumentNullException(nameof(streamerId));

            // 即使已经缓存，也先通过当前内容库的下架门禁。
            var streamer = db.Streamer(streamerId);
            string path = db.Art.PortraitPath(streamerId);
            string accent = db.Art.Accent(streamerId);
            string name = string.IsNullOrWhiteSpace(streamer.Name) ? streamer.Id : streamer.Name.Trim();
            string initial = StringInfo.GetNextTextElement(name);
            var key = (path, accent, initial);
            Texture2D texture;
            if (cache.TryGetValue(key, out texture) && texture != null) return texture;

            texture = Resources.Load<Texture2D>(path);
            if (texture == null)
            {
                Color color;
                if (!ColorUtility.TryParseHtmlString(accent, out color))
                    throw new ArgumentException("头像强调色必须是有效的颜色：" + accent, nameof(db));
                texture = CreatePortrait(color, initial);
                generated.Add(texture);
            }
            cache[key] = texture;
            return texture;
        }

        Texture2D CreatePortrait(Color accent, string initial)
        {
            var pixels = new Color[PortraitSize * PortraitSize];
            Color bottom = Color.Lerp(accent, Color.black, 0.45f);
            Color top = Color.Lerp(accent, Color.white, 0.12f);
            for (int y = 0; y < PortraitSize; y++)
            {
                for (int x = 0; x < PortraitSize; x++)
                {
                    float blend = (0.8f * y + 0.2f * (PortraitSize - 1 - x)) / (PortraitSize - 1);
                    pixels[y * PortraitSize + x] = Color.Lerp(bottom, top, blend);
                }
            }

            float[] glyph = ReadGlyph(initial);
            Color outline = new Color(0.04f, 0.06f, 0.1f, 1f);
            for (int y = 1; y < PortraitSize - 1; y++)
            {
                for (int x = 1; x < PortraitSize - 1; x++)
                {
                    int index = y * PortraitSize + x;
                    float edge = Mathf.Max(glyph[index - 1], glyph[index + 1]);
                    edge = Mathf.Max(edge, Mathf.Max(glyph[index - PortraitSize], glyph[index + PortraitSize]));
                    pixels[index] = Color.Lerp(pixels[index], outline, edge * 0.8f);
                    pixels[index] = Color.Lerp(pixels[index], Color.white, glyph[index]);
                }
            }

            var texture = new Texture2D(PortraitSize, PortraitSize, TextureFormat.RGBA32, false)
            {
                name = "StreamDuel.Portrait." + initial,
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            try
            {
                texture.SetPixels(pixels);
                texture.Apply(false, true);
                return texture;
            }
            catch
            {
                DestroyGenerated(texture);
                throw;
            }
        }

        float[] ReadGlyph(string initial)
        {
            int size = font.dynamic ? GlyphSize : 0;
            if (font.dynamic) font.RequestCharactersInTexture(initial + "?", size, FontStyle.Normal);
            CharacterInfo info = default;
            bool found = initial.Length > 0 && !char.IsSurrogate(initial[0]) &&
                font.GetCharacterInfo(initial[0], out info, size, FontStyle.Normal);
            // 外部内容可能用字体未收录的字符；沿用字体的缺字标记，避免整张头像加载失败。
            if (!found || info.maxX <= info.minX || info.maxY <= info.minY)
            {
                if (!font.GetCharacterInfo('?', out info, size, FontStyle.Normal))
                    throw new InvalidOperationException("头像字体无法提供可绘制字形。");
            }
            var atlas = font.material == null ? null : font.material.mainTexture;
            if (atlas == null || info.maxX <= info.minX || info.maxY <= info.minY)
                throw new InvalidOperationException("头像字体无法提供可绘制字形。");

            Vector2 lower = Vector2.Min(Vector2.Min(info.uvBottomLeft, info.uvBottomRight),
                Vector2.Min(info.uvTopLeft, info.uvTopRight));
            Vector2 upper = Vector2.Max(Vector2.Max(info.uvBottomLeft, info.uvBottomRight),
                Vector2.Max(info.uvTopLeft, info.uvTopRight));
            int left = Mathf.Clamp(Mathf.FloorToInt(lower.x * atlas.width), 0, atlas.width - 1);
            int bottom = Mathf.Clamp(Mathf.FloorToInt(lower.y * atlas.height), 0, atlas.height - 1);
            int width = Mathf.Clamp(Mathf.CeilToInt(upper.x * atlas.width) - left, 1, atlas.width - left);
            int height = Mathf.Clamp(Mathf.CeilToInt(upper.y * atlas.height) - bottom, 1, atlas.height - bottom);

            RenderTexture previous = RenderTexture.active;
            RenderTexture temporary = null;
            Texture2D readable = null;
            try
            {
                // 字体图集通常不可直接读；只回读当前字形，并立即使用，避免图集重建使 UV 失效。
                temporary = RenderTexture.GetTemporary(atlas.width, atlas.height, 0,
                    RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
                Graphics.Blit(atlas, temporary);
                RenderTexture.active = temporary;
                readable = new Texture2D(width, height, TextureFormat.RGBA32, false, true)
                {
                    hideFlags = HideFlags.HideAndDontSave,
                    wrapMode = TextureWrapMode.Clamp
                };
                readable.ReadPixels(new Rect(left, bottom, width, height), 0, 0, false);

                float scale = (float)GlyphBox / Mathf.Max(info.maxX - info.minX, info.maxY - info.minY);
                int outputWidth = Mathf.Max(1, Mathf.RoundToInt((info.maxX - info.minX) * scale));
                int outputHeight = Mathf.Max(1, Mathf.RoundToInt((info.maxY - info.minY) * scale));
                int offsetX = (PortraitSize - outputWidth) / 2;
                int offsetY = (PortraitSize - outputHeight) / 2;
                var glyph = new float[PortraitSize * PortraitSize];
                for (int y = 0; y < outputHeight; y++)
                {
                    float v = (y + 0.5f) / outputHeight;
                    Vector2 uvLeft = Vector2.Lerp(info.uvBottomLeft, info.uvTopLeft, v);
                    Vector2 uvRight = Vector2.Lerp(info.uvBottomRight, info.uvTopRight, v);
                    for (int x = 0; x < outputWidth; x++)
                    {
                        Vector2 uv = Vector2.Lerp(uvLeft, uvRight, (x + 0.5f) / outputWidth);
                        float u = (uv.x * atlas.width - left) / width;
                        float w = (uv.y * atlas.height - bottom) / height;
                        glyph[(y + offsetY) * PortraitSize + x + offsetX] = readable.GetPixelBilinear(u, w).a;
                    }
                }
                return glyph;
            }
            finally
            {
                RenderTexture.active = previous;
                if (temporary != null) RenderTexture.ReleaseTemporary(temporary);
                DestroyGenerated(readable);
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            // Resources 返回的用户纹理不归本实例所有。
            foreach (var texture in generated) DestroyGenerated(texture);
            generated.Clear();
            cache.Clear();
        }

        static void DestroyGenerated(Texture2D texture)
        {
            if (texture == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(texture);
            else UnityEngine.Object.DestroyImmediate(texture);
        }
    }
}
