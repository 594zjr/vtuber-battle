using System.Collections.Generic;
using StreamDuel.Core;
using UnityEngine;
using UnityEngine.UIElements;

namespace StreamDuel.Game
{
    public interface IBattleAnimator
    {
        void Play(IReadOnlyList<AnimCue> cues);
        bool IsPlaying { get; }
        void SkipToEnd();
    }

    public interface IFxPlayer
    {
        void Play(string fxId, VisualElement from, VisualElement to, float duration);
    }

    public interface IStatusIconProvider
    {
        VisualElement Make(string iconId, string label);
    }

    public interface IPortraitProvider
    {
        Texture2D Get(ContentDb db, string streamerId);
    }
}
