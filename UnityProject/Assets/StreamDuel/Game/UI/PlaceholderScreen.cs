using System;
using System.Collections.Generic;
using UnityEngine.UIElements;

namespace StreamDuel.Game
{
    public abstract class PlaceholderScreen : IScreen
    {
        readonly List<Action> unbind = new List<Action>();
        public abstract string Id { get; }
        protected abstract string Title { get; }
        protected virtual string DefaultReturnId => ScreenIds.Menu;
        protected virtual string ReturnLabel => "返回";

        public VisualElement Build(ScreenRouter router, object args)
        {
            Dispose();
            var page = new VisualElement { name = Id + "-screen" };
            page.AddToClassList("screen");
            var panel = new VisualElement();
            panel.AddToClassList("screen-panel");
            page.Add(panel);
            var title = new Label(Title);
            title.AddToClassList("screen-title");
            title.style.unityFont = router.Resources.BoldFont;
            panel.Add(title);
            var content = new VisualElement();
            content.AddToClassList("screen-content");
            panel.Add(content);
            BuildContent(router, content, args);
            var actions = new VisualElement();
            actions.AddToClassList("screen-actions");
            panel.Add(actions);
            var navigation = args as ScreenArgs;
            string returnId = navigation?.ReturnScreenId ?? DefaultReturnId;
            object returnArgs = navigation?.ReturnArgs;
            AddButton(actions, "back-button", ReturnLabel, () => router.Show(returnId, returnArgs));
            return page;
        }

        protected virtual void BuildContent(ScreenRouter router, VisualElement content, object args) { }

        protected void AddButton(VisualElement parent, string name, string text, Action clicked)
        {
            var button = new Button { name = name, text = text };
            button.AddToClassList("screen-button");
            button.clicked += clicked;
            unbind.Add(() => button.clicked -= clicked);
            parent.Add(button);
        }

        public virtual void Tick(float dt) { }

        public virtual void Dispose()
        {
            foreach (var remove in unbind) remove();
            unbind.Clear();
        }
    }
}
