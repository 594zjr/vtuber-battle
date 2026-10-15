using StreamDuel.Core;
using UnityEngine;
using UnityEngine.UIElements;

namespace StreamDuel.Game
{
    public sealed class MenuScreen : PlaceholderScreen
    {
        bool returningToBattle;
        public override string Id => ScreenIds.Menu;
        protected override string Title => "开播对决";
        protected override string DefaultReturnId => ScreenIds.Battle;
        protected override string ReturnLabel => returningToBattle ? "返回对战" : "开始组队";

        protected override void BuildContent(ScreenRouter router, VisualElement content, object args)
        {
            returningToBattle = args is ScreenArgs navigation && navigation.ReturnScreenId == ScreenIds.Battle && navigation.ReturnArgs != null;
            var buttons = new VisualElement();
            buttons.AddToClassList("menu-grid");
            content.Add(buttons);
            AddPage(router, buttons, ScreenIds.Team, "组队", args);
            AddPage(router, buttons, ScreenIds.Battle, "对战", args);
            AddPage(router, buttons, ScreenIds.Result, "结算", args);
            AddPage(router, buttons, ScreenIds.Replay, "回看", args);
            AddPage(router, buttons, ScreenIds.Settings, "设置", args);
            AddPage(router, buttons, ScreenIds.Online, "联网", args);

            var legend = new VisualElement { name = "track-legend" };
            legend.style.flexDirection = FlexDirection.Row;
            legend.style.flexWrap = Wrap.Wrap;
            content.Add(legend);
            var rules = UnityEngine.Resources.Load<TextAsset>("StreamDuelRules");
            var original = UnityEngine.Resources.Load<TextAsset>("Packs/original");
            var db = ContentDb.FromJson(rules.text, original.text);
            foreach (var pair in db.Types)
            {
                var chip = new Label(pair.Value.Name) { name = "track-" + pair.Key, enableRichText = false };
                chip.AddToClassList("type-chip");
                Color color;
                if (ColorUtility.TryParseHtmlString(pair.Value.Color, out color)) chip.style.backgroundColor = color;
                legend.Add(chip);
            }
        }

        void AddPage(ScreenRouter router, VisualElement content, string id, string label, object returnArgs)
        {
            if (id == ScreenIds.Team || id == ScreenIds.Battle)
            {
                AddButton(content, "open-" + id, label,
                    () => router.Show(ScreenIds.Battle, new ScreenArgs { ReturnScreenId = ScreenIds.Menu, ReturnArgs = returnArgs }));
                return;
            }
            AddButton(content, "open-" + id, label,
                () => router.Show(id, new ScreenArgs { ReturnScreenId = ScreenIds.Menu, ReturnArgs = returnArgs }));
        }
    }
}
