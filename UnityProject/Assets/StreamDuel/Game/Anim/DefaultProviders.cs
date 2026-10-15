using System;
using System.Collections.Generic;
using StreamDuel.Core;
using UnityEngine;
using UnityEngine.UIElements;

namespace StreamDuel.Game
{
    public sealed class NullAnimator : IBattleAnimator
    {
        public bool IsPlaying { get { return false; } }

        public void Play(IReadOnlyList<AnimCue> cues)
        {
        }

        public void SkipToEnd()
        {
        }
    }

    public sealed class NullFx : IFxPlayer
    {
        public void Play(string fxId, VisualElement from, VisualElement to, float duration)
        {
        }
    }

    public sealed class TextStatusIconProvider : IStatusIconProvider
    {
        public VisualElement Make(string iconId, string label)
        {
            var icon = new Label(label ?? iconId ?? string.Empty)
            {
                tooltip = label ?? iconId ?? string.Empty,
                enableRichText = false,
                pickingMode = PickingMode.Ignore
            };
            icon.AddToClassList("status-icon");
            return icon;
        }
    }

    // 创建者负责在不再展示头像时 Dispose；调用者只借用纹理，避免多个同色头像重复释放。
    public sealed class SolidColorPortraitProvider : IPortraitProvider, IDisposable
    {
        readonly Dictionary<uint, Texture2D> textures = new Dictionary<uint, Texture2D>();
        bool disposed;

        public Texture2D Get(ContentDb db, string streamerId)
        {
            if (disposed) throw new ObjectDisposedException(nameof(SolidColorPortraitProvider));
            if (db == null) throw new ArgumentNullException(nameof(db));
            if (streamerId == null) throw new ArgumentNullException(nameof(streamerId));

            var streamer = db.Streamer(streamerId);
            TypeDef type;
            Color color;
            if (!db.Types.TryGetValue(streamer.Type, out type)
                || !ColorUtility.TryParseHtmlString(type.Color, out color))
                color = Color.gray;
            Color32 bytes = color;
            uint key = ((uint)bytes.r << 24) | ((uint)bytes.g << 16) | ((uint)bytes.b << 8) | bytes.a;
            Texture2D texture;
            if (textures.TryGetValue(key, out texture) && texture != null) return texture;

            texture = new Texture2D(1, 1, TextureFormat.RGBA32, false)
            {
                name = "StreamDuel.Portrait." + ColorUtility.ToHtmlStringRGBA(color),
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };
            texture.SetPixel(0, 0, color);
            // 纯色头像不再改像素，上传后释放 CPU 副本。
            texture.Apply(false, true);
            textures[key] = texture;
            return texture;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            foreach (var texture in textures.Values)
            {
                if (texture == null) continue;
                if (Application.isPlaying) UnityEngine.Object.Destroy(texture);
                else UnityEngine.Object.DestroyImmediate(texture);
            }
            textures.Clear();
        }
    }
}
