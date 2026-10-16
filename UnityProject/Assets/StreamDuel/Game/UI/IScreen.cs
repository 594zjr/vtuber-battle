using UnityEngine.UIElements;

namespace StreamDuel.Game
{
    public interface IScreen
    {
        string Id { get; }
        VisualElement Build(ScreenRouter router, object args);
        void Tick(float dt);
        void Dispose();
    }

    public static class ScreenIds
    {
        public const string Menu = "menu";
        public const string About = "about";
        public const string AnimationPreview = "animation-preview";
        public const string EffectPreview = "effect-preview";
        public const string Team = "team";
        public const string Battle = "battle";
        public const string Result = "result";
        public const string Replay = "replay";
        public const string Settings = "settings";
        public const string Online = "online";
        public const string Tournament = "tournament";
        public const string Legacy = "legacy";
    }

    public sealed class ScreenArgs
    {
        public string ReturnScreenId = ScreenIds.Menu;
        public object ReturnArgs;
    }
}
