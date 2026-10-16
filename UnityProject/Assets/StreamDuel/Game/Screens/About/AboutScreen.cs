using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace StreamDuel.Game
{
    public sealed class AboutScreen : IScreen
    {
        readonly List<Action> unbind = new List<Action>();
        readonly List<Button> tabs = new List<Button>();
        readonly List<Label> texts = new List<Label>();
        VisualElement page;
        ScrollView scroll;
        IVisualElementScheduledItem initialFocus;

        public string Id => ScreenIds.About;

        public VisualElement Build(ScreenRouter router, object args)
        {
            Dispose();
            page = new VisualElement { name = "about-screen" };
            page.AddToClassList("about-screen");
            var theme = UnityEngine.Resources.Load<StyleSheet>("AboutTheme");
            if (theme == null) throw new InvalidOperationException("缺少关于页面样式资源 AboutTheme。");
            page.styleSheets.Add(theme);

            var panel = new VisualElement { name = "about-panel" };
            panel.AddToClassList("about-panel");
            page.Add(panel);
            var heading = new VisualElement();
            heading.AddToClassList("about-heading");
            panel.Add(heading);
            var title = AddLabel(heading, "about-title", "关于开播对决", "about-title");
            title.style.unityFont = router.Resources.BoldFont;
            AddLabel(heading, "about-version", "版本 " + Application.version, "about-version");
            AddLabel(panel, "about-free-notice", "本作免费、非商业，不接受任何形式的付费", "about-free-notice");
            AddLabel(panel, "about-project-notice", "非官方同人作品 · 代码与内容包使用各自的许可", "about-muted");

            var tabRow = new VisualElement { name = "about-license-tabs" };
            tabRow.AddToClassList("about-tabs");
            panel.Add(tabRow);
            AddTab(tabRow, "code", "代码 · MIT", 0);
            AddTab(tabRow, "content", "内容包 · CC BY-NC-SA 4.0", 1);
            AddTab(tabRow, "third-party", "第三方与字体", 2);

            scroll = new ScrollView(ScrollViewMode.Vertical) { name = "about-license-scroll" };
            scroll.AddToClassList("about-license-scroll");
            scroll.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            scroll.verticalScrollerVisibility = ScrollerVisibility.Auto;
            panel.Add(scroll);
            AddDocument("about-code-license", "About/CodeLicense");
            AddDocument("about-content-license", "About/ContentLicense");
            AddDocument("about-third-party", "About/ThirdParty");
            SelectDocument(0);

            var footer = new VisualElement();
            footer.AddToClassList("about-footer");
            panel.Add(footer);
            AddButton(footer, "about-scroll-up", "向上翻页", () => MovePage(-1));
            AddButton(footer, "about-scroll-down", "向下翻页", () => MovePage(1));
            var spacer = new VisualElement();
            spacer.style.flexGrow = 1;
            footer.Add(spacer);
            AddButton(footer, "about-back", "返回主菜单", () => router.Show(ScreenIds.Menu));
            AddLabel(panel, "about-controls", "方向键 / 左摇杆移动 · Enter / A 确认 · Esc / B 返回", "about-controls");

            EventCallback<NavigationCancelEvent> cancel = evt =>
            {
                evt.StopPropagation();
                router.Show(ScreenIds.Menu);
            };
            page.RegisterCallback(cancel);
            unbind.Add(() => page.UnregisterCallback(cancel));
            initialFocus = page.schedule.Execute(() => tabs[0].Focus());
            return page;
        }

        void AddTab(VisualElement row, string suffix, string title, int index)
        {
            var tab = AddButton(row, "about-tab-" + suffix, title, () => SelectDocument(index));
            tab.AddToClassList("about-tab");
            tabs.Add(tab);
        }

        void AddDocument(string name, string resourcePath)
        {
            var asset = UnityEngine.Resources.Load<TextAsset>(resourcePath);
            if (asset == null || string.IsNullOrWhiteSpace(asset.text))
                throw new InvalidOperationException("缺少关于页面许可全文：" + resourcePath);
            var text = AddLabel(scroll.contentContainer, name, asset.text, "about-license-text");
            texts.Add(text);
        }

        void SelectDocument(int selected)
        {
            for (int i = 0; i < tabs.Count; i++)
            {
                tabs[i].EnableInClassList("about-tab-selected", i == selected);
                texts[i].style.display = i == selected ? DisplayStyle.Flex : DisplayStyle.None;
            }
            scroll.scrollOffset = Vector2.zero;
        }

        void MovePage(int direction)
        {
            float amount = Mathf.Max(80, scroll.contentViewport.layout.height * 0.8f);
            float maximum = Mathf.Max(0, scroll.contentContainer.layout.height - scroll.contentViewport.layout.height);
            scroll.scrollOffset = new Vector2(0, Mathf.Clamp(scroll.scrollOffset.y + direction * amount, 0, maximum));
        }

        Button AddButton(VisualElement parent, string name, string text, Action clicked)
        {
            var button = new Button { name = name, text = text };
            button.AddToClassList("about-button");
            button.clicked += clicked;
            unbind.Add(() => button.clicked -= clicked);
            parent.Add(button);
            return button;
        }

        static Label AddLabel(VisualElement parent, string name, string text, string className)
        {
            var label = new Label(text) { name = name, enableRichText = false };
            label.AddToClassList(className);
            parent.Add(label);
            return label;
        }

        public void Tick(float dt) { }

        public void Dispose()
        {
            initialFocus?.Pause();
            initialFocus = null;
            foreach (var remove in unbind) remove();
            unbind.Clear();
            tabs.Clear();
            texts.Clear();
            scroll = null;
            page = null;
        }
    }
}
