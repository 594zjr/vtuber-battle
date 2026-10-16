using UnityEngine;

namespace StreamDuel.Game
{
    public static class Easing
    {
        public static float OutCubic(float value)
        {
            float remaining = 1f - Mathf.Clamp01(value);
            return 1f - remaining * remaining * remaining;
        }

        public static float InCubic(float value)
        {
            float t = Mathf.Clamp01(value);
            return t * t * t;
        }

        public static float Smooth(float value)
        {
            float t = Mathf.Clamp01(value);
            return t * t * (3f - 2f * t);
        }

        public static float Pop(float value)
        {
            float t = Mathf.Clamp01(value);
            return t < 0.65f ? 1.2f * OutCubic(t / 0.65f)
                : Mathf.Lerp(1.2f, 1f, Smooth((t - 0.65f) / 0.35f));
        }
    }
}
