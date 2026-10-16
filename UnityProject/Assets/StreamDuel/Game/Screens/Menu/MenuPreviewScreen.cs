using UnityEngine.UIElements;

namespace StreamDuel.Game
{
    public sealed class MenuPreviewScreen : PlaceholderScreen
    {
        readonly string id;
        readonly string title;

        public MenuPreviewScreen(string id, string title)
        {
            this.id = id;
            this.title = title;
        }

        public override string Id => id;
        protected override string Title => title;

        protected override void BuildContent(ScreenRouter router, VisualElement content, object args)
        {
            content.Add(new Label("此预览界面正在制作中。"));
        }
    }
}
