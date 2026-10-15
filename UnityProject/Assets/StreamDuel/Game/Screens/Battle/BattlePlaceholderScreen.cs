using UnityEngine.UIElements;

namespace StreamDuel.Game
{
    public sealed class BattlePlaceholderScreen : PlaceholderScreen
    {
        public override string Id => ScreenIds.Battle;
        protected override string Title => "对战";

        protected override void BuildContent(ScreenRouter router, VisualElement content, object args)
        {
            content.Add(new BattleStage());
        }
    }
}
