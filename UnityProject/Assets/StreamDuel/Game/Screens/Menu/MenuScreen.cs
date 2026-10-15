using System;
using System.Collections.Generic;
using StreamDuel.Core;
using UnityEngine;
using UnityEngine.UIElements;

namespace StreamDuel.Game
{
    public sealed class MenuScreen : IScreen
    {
        readonly BattleScreen legacy;
        readonly List<Action> unbind = new List<Action>();
        VisualElement root;
        DropdownField packField;
        Label packNotice;
        Label packStatus;
        string selectionError;
        VisualElement trackLegend;
        List<string> packIds;
        IVisualElementScheduledItem initialFocus;

        public string Id => ScreenIds.Menu;

        public MenuScreen(BattleScreen battleScreen)
        {
            legacy = battleScreen ?? throw new ArgumentNullException(nameof(battleScreen));
        }

        public VisualElement Build(ScreenRouter router, object args)
        {
            Dispose();
            root = new VisualElement { name = "menu-screen" };
            root.AddToClassList("sd-menu");
            var style = Resources.Load<StyleSheet>("T81Menu");
            if (style == null) throw new InvalidOperationException("缺少主菜单样式资源 T81Menu。");
            root.styleSheets.Add(style);

            var shell = MakeElement("menu-shell", "sd-menu__shell", root);
            var header = MakeElement("menu-header", "sd-menu__header", shell);
            var brand = MakeElement("menu-brand", "sd-menu__brand", header);
            var title = MakeLabel("menu-title", "开播对决", "sd-menu__title", brand);
            title.style.unityFont = router.Resources.BoldFont;
            MakeLabel("menu-tagline", "组好阵容，准备开播。", "sd-menu__muted", brand);
            BuildPackPicker(header);

            var body = MakeElement("menu-body", "sd-menu__body", shell);
            var introduction = MakeElement("menu-introduction", "sd-menu__introduction", body);
            MakeLabel("menu-eyebrow", "回合制对战", "sd-menu__eyebrow", introduction);
            var matchup = MakeLabel("menu-matchup", "6 对 6", "sd-menu__matchup", introduction);
            matchup.style.unityFont = router.Resources.BoldFont;
            MakeLabel("menu-description", "集结六位主播\n用你的阵容与选择，迎接每一回合。", "sd-menu__description", introduction);
            trackLegend = MakeElement("track-legend", "sd-menu__tracks", introduction);

            var buttons = MakeElement("menu-actions", "sd-menu__actions", body);
            var primary = AddPage(router, buttons, "open-team", ScreenIds.Team, "对电脑", args);
            primary.AddToClassList("sd-menu__button--primary");
            AddPage(router, buttons, "open-online", ScreenIds.Online, "联网对战", args);
            AddPage(router, buttons, "open-replay", ScreenIds.Replay, "录像", args);
            AddPage(router, buttons, "open-settings", ScreenIds.Settings, "设置", args);
            AddPage(router, buttons, "open-about", ScreenIds.About, "关于", args);
            AddButton(buttons, "quit-game", "退出", Quit);

            var noticePanel = MakeElement("pack-notice-panel", "sd-menu__notice-panel", shell);
            MakeLabel("pack-notice-title", "内容包声明", "sd-menu__notice-title", noticePanel);
            packNotice = MakeLabel("pack-notice", "", "sd-menu__notice", noticePanel);
            RefreshPack();

            if (Application.isEditor || Debug.isDebugBuild) BuildDeveloperTools(router, shell, args);
            MakeLabel("menu-footer", "非官方同人作品 · 免费 · 非商业", "sd-menu__footer", shell);

            // 构建时尚未接入面板，等路由挂载后再设置初始焦点。
            initialFocus = root.schedule.Execute(() =>
            {
                if (root != null && root.panel != null) primary.Focus();
            });
            return root;
        }

        void BuildPackPicker(VisualElement parent)
        {
            var panel = MakeElement("menu-pack-panel", "sd-menu__pack-panel", parent);
            MakeLabel("menu-pack-label", "当前内容包", "sd-menu__pack-label", panel);
            var choices = new List<string>();
            packIds = new List<string>();
            IReadOnlyList<ContentDb> packs = legacy.MenuPacks;
            // 先保留原始名称，避免重名项的包 id 后缀占用另一包本来就有的名称。
            var reservedLabels = new HashSet<string>(StringComparer.Ordinal);
            foreach (var pack in packs)
                reservedLabels.Add(string.IsNullOrWhiteSpace(pack.PackName) ? pack.PackId : pack.PackName);
            var usedLabels = new HashSet<string>(StringComparer.Ordinal);
            foreach (var pack in packs)
            {
                string label = string.IsNullOrWhiteSpace(pack.PackName) ? pack.PackId : pack.PackName;
                bool repeated = false;
                foreach (var other in packs)
                {
                    if (!ReferenceEquals(pack, other)
                        && string.Equals(label, string.IsNullOrWhiteSpace(other.PackName) ? other.PackId : other.PackName, StringComparison.Ordinal))
                        repeated = true;
                }
                string choice = label;
                if (repeated || usedLabels.Contains(choice))
                {
                    string baseChoice = label + "（" + pack.PackId + "）";
                    choice = baseChoice;
                    int suffix = 2;
                    while (reservedLabels.Contains(choice) || usedLabels.Contains(choice))
                    {
                        choice = baseChoice + "（" + suffix.ToString(System.Globalization.CultureInfo.InvariantCulture) + "）";
                        suffix++;
                    }
                }
                usedLabels.Add(choice);
                choices.Add(choice);
                packIds.Add(pack.PackId);
            }
            packField = new DropdownField { name = "menu-pack", choices = choices, focusable = true };
            packField.AddToClassList("sd-menu__pack-field");
            panel.Add(packField);
            packField.SetEnabled(choices.Count > 0 && !legacy.MenuPackLocked);
            packStatus = MakeLabel("menu-pack-status", "", "sd-menu__pack-status", panel);

            if (legacy.MenuPackErrors.Count > 0)
            {
                MakeLabel("menu-pack-errors-title", "部分内容包加载失败", "sd-menu__pack-status", panel);
                var errors = new ScrollView(ScrollViewMode.Vertical)
                {
                    name = "menu-pack-errors",
                    horizontalScrollerVisibility = ScrollerVisibility.Hidden,
                    verticalScrollerVisibility = ScrollerVisibility.Auto,
                    focusable = true
                };
                errors.AddToClassList("sd-menu__pack-errors");
                panel.Add(errors);
                MakeLabel("menu-pack-errors-text", string.Join("\n", legacy.MenuPackErrors), "sd-menu__pack-error-text", errors);
            }
            EventCallback<ChangeEvent<string>> onChange = evt =>
            {
                int index = packField.index;
                if (index < 0 || index >= packIds.Count) return;
                try
                {
                    legacy.SelectMenuPack(packIds[index]);
                    selectionError = null;
                    RefreshPack();
                }
                catch (Exception ex)
                {
                    selectionError = "无法切换内容包：" + ex.Message;
                    RefreshPack();
                }
            };
            packField.RegisterValueChangedCallback(onChange);
            var field = packField;
            unbind.Add(() => field.UnregisterValueChangedCallback(onChange));
        }

        void RefreshPack()
        {
            var notices = new List<string>();
            if (legacy.MenuPackLocked) notices.Add("返回旧版组队后可切换内容包");
            else if (packIds.Count == 0) notices.Add("没有可用内容包");
            if (!string.IsNullOrEmpty(legacy.MenuSettingsNotice)) notices.Add(legacy.MenuSettingsNotice);
            if (!string.IsNullOrEmpty(selectionError)) notices.Add(selectionError);
            packStatus.text = string.Join("\n", notices);
            ContentDb pack = legacy.MenuPack;
            if (pack == null) return;
            int index = packIds.IndexOf(pack.PackId);
            if (index >= 0) packField.SetValueWithoutNotify(packField.choices[index]);
            if (packNotice != null) packNotice.text = pack.PackNotice ?? "";
            if (trackLegend != null)
            {
                trackLegend.Clear();
                foreach (var pair in pack.Types)
                {
                    var chip = MakeLabel("track-" + pair.Key, pair.Value.Name, "sd-menu__track", trackLegend);
                    Color color;
                    if (ColorUtility.TryParseHtmlString(pair.Value.Color, out color)) chip.style.backgroundColor = color;
                }
            }
        }

        void BuildDeveloperTools(ScreenRouter router, VisualElement shell, object args)
        {
            var developer = new Foldout { name = "developer-tools", text = "开发", value = false };
            developer.AddToClassList("sd-menu__developer");
            shell.Add(developer);
            MakeLabel("developer-note", "动画与特效预览入口暂为占位界面。", "sd-menu__developer-note", developer);
            var buttons = MakeElement("developer-actions", "sd-menu__developer-actions", developer);
            AddPage(router, buttons, "open-legacy", ScreenIds.Legacy, "旧版对战", args);
            AddPage(router, buttons, "open-animation-preview", ScreenIds.AnimationPreview, "动画预览", args);
            AddPage(router, buttons, "open-effect-preview", ScreenIds.EffectPreview, "特效预览", args);
        }

        Button AddPage(ScreenRouter router, VisualElement parent, string name, string screenId, string text, object args)
        {
            return AddButton(parent, name, text,
                () => router.Show(screenId, new ScreenArgs { ReturnScreenId = ScreenIds.Menu, ReturnArgs = args }));
        }

        Button AddButton(VisualElement parent, string name, string text, Action onClick)
        {
            var button = new Button { name = name, text = text, focusable = true, enableRichText = false };
            button.AddToClassList("sd-menu__button");
            button.clicked += onClick;
            unbind.Add(() => button.clicked -= onClick);
            parent.Add(button);
            return button;
        }

        static VisualElement MakeElement(string name, string className, VisualElement parent)
        {
            var element = new VisualElement { name = name };
            element.AddToClassList(className);
            parent.Add(element);
            return element;
        }

        static Label MakeLabel(string name, string text, string className, VisualElement parent)
        {
            var label = new Label(text) { name = name, enableRichText = false };
            label.AddToClassList(className);
            parent.Add(label);
            return label;
        }

        static void Quit()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }

        public void Tick(float dt) { }

        public void Dispose()
        {
            initialFocus?.Pause();
            initialFocus = null;
            foreach (var callback in unbind) callback();
            unbind.Clear();
            root = null;
            packField = null;
            packNotice = null;
            packStatus = null;
            selectionError = null;
            trackLegend = null;
            packIds = null;
        }
    }
}
