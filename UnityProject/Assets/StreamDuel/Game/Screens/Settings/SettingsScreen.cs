using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using StreamDuel.Core;
using UnityEngine;
using UnityEngine.UIElements;

namespace StreamDuel.Game
{
    public sealed class SettingsScreen : IScreen
    {
        readonly List<System.Action> unbind = new List<System.Action>();
        readonly List<PackChoice> packs = new List<PackChoice>();
        readonly List<Vector2Int> resolutions = new List<Vector2Int>();
        GameSettings draft;
        DropdownField animation, resolution, ai, pack;
        Toggle timer, fullscreen;
        IntegerField seconds;
        Label notice, packNotice;

        public string Id => ScreenIds.Settings;

        public VisualElement Build(ScreenRouter router, object args)
        {
            Dispose();
            draft = GameSettings.FromJson(GameRuntimeSettings.Current.ToJson());
            var page = new VisualElement { name = "settings-screen" };
            page.AddToClassList("screen");
            var panel = new VisualElement { name = "settings-panel" };
            panel.AddToClassList("screen-panel");
            panel.style.height = Length.Percent(100);
            panel.style.minHeight = 0;
            page.Add(panel);
            var title = new Label("设置");
            title.AddToClassList("screen-title");
            title.style.unityFont = router.Resources.BoldFont;
            title.style.flexShrink = 0;
            panel.Add(title);

            var scroll = new ScrollView(ScrollViewMode.Vertical) { name = "settings-scroll" };
            scroll.style.flexGrow = 1;
            scroll.style.flexShrink = 1;
            scroll.style.minHeight = 0;
            scroll.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            scroll.contentContainer.style.paddingRight = 18;
            panel.Add(scroll);
            AddNote(scroll, "修改后点击“保存并应用”。返回会放弃尚未保存的修改。");

            var playback = Section(scroll, "对战显示与计时");
            var animationChoices = new List<string> { "0.5 倍（慢速）", "1 倍（标准）", "2 倍（快速）", "跳过动画" };
            int speedIndex = !draft.Animations ? 3 : draft.AnimationSpeed == 0.5 ? 0 : draft.FastPlay || draft.AnimationSpeed == 2 ? 2 : 1;
            animation = Dropdown(playback, "settings-animation", "动画速度", animationChoices, speedIndex);
            timer = new Toggle("启用回合计时") { name = "settings-timer", value = draft.TurnTimer };
            AddControl(playback, timer);
            seconds = new IntegerField { name = "settings-turn-seconds", value = draft.TurnSeconds };
            Field(playback, "每次选择可用秒数（1—300）", seconds);
            seconds.SetEnabled(draft.TurnTimer);
            AddNote(playback, "此计时用于本机对战的行动与补位；联网对战暂不启用此本机限时。禁选与首发不计时。");

            var display = Section(scroll, "窗口与显示");
            fullscreen = new Toggle("全屏显示") { name = "settings-fullscreen", value = draft.Fullscreen };
            AddControl(display, fullscreen);
            LoadResolutions();
            resolution = Dropdown(display, "settings-resolution", "分辨率", resolutions.Select(ResolutionLabel).ToList(),
                resolutions.FindIndex(value => value.x == draft.Width && value.y == draft.Height));
            AddNote(display, "关闭全屏使用窗口模式。全屏采用无边框窗口，实际显示尺寸由系统决定。");

            var defaults = Section(scroll, "新对战默认选项");
            ai = Dropdown(defaults, "settings-ai", "电脑难度", new List<string> { "简单", "普通", "困难", "专家" }, (int)draft.AiLevel);
            string packWarning = LoadPacks();
            pack = Dropdown(defaults, "settings-pack", "默认内容包", packs.Select(value => value.Label).ToList(),
                packs.FindIndex(value => value.Id == draft.DefaultPack));
            packNotice = AddNote(defaults, "");
            packNotice.name = "settings-pack-notice";
            UpdatePackNotice();
            if (!string.IsNullOrEmpty(packWarning)) AddNote(defaults, packWarning);
            AddNote(defaults, "默认内容包用于下次进入对战；不会替换正在进行的对局。");

            notice = AddNote(panel, GameRuntimeSettings.Notice ?? "");
            notice.name = "settings-notice";
            notice.style.marginTop = 16;
            notice.style.flexShrink = 0;
            var actions = new VisualElement();
            actions.AddToClassList("screen-actions");
            actions.style.flexShrink = 0;
            panel.Add(actions);
            Button(actions, "settings-save", "保存并应用", Save);
            var navigation = args as ScreenArgs;
            Button(actions, "back-button", "返回", () => router.Show(navigation?.ReturnScreenId ?? ScreenIds.Menu, navigation?.ReturnArgs));

            Bind(animation, _ => Changed());
            Bind(timer, value => { seconds.SetEnabled(value); Changed(); });
            Bind(seconds, _ => Changed());
            Bind(fullscreen, _ => Changed());
            Bind(resolution, _ => Changed());
            Bind(ai, _ => Changed());
            Bind(pack, _ => { UpdatePackNotice(); Changed(); });
            return page;
        }

        void Save()
        {
            if (timer.value && (seconds.value < 1 || seconds.value > 300))
            {
                notice.text = "回合秒数需要是 1—300 之间的整数。";
                seconds.Focus();
                return;
            }
            draft.Animations = animation.index != 3;
            if (draft.Animations) draft.AnimationSpeed = animation.index == 0 ? 0.5 : animation.index == 2 ? 2 : 1;
            draft.FastPlay = draft.AnimationSpeed == 2;
            draft.TurnTimer = timer.value;
            if (seconds.value >= 1 && seconds.value <= 300) draft.TurnSeconds = seconds.value;
            draft.Fullscreen = fullscreen.value;
            if (resolution.index >= 0 && resolution.index < resolutions.Count)
            {
                draft.Width = resolutions[resolution.index].x;
                draft.Height = resolutions[resolution.index].y;
            }
            draft.AiLevel = (AiLevel)ai.index;
            if (pack.index >= 0 && pack.index < packs.Count) draft.DefaultPack = packs[pack.index].Id;
            GameRuntimeSettings.TrySave(draft, out string message);
            GameRuntimeSettings.ApplyDisplay();
            draft = GameSettings.FromJson(GameRuntimeSettings.Current.ToJson());
            notice.text = message;
        }

        void Changed()
        {
            notice.text = "有未保存的修改。" + (GameRuntimeSettings.Notice == null ? "" : "\n" + GameRuntimeSettings.Notice);
        }

        void UpdatePackNotice()
        {
            if (pack.index < 0 || pack.index >= packs.Count) return;
            var selected = packs[pack.index];
            packNotice.text = selected.Missing ? "此内容包当前不可用，保留原设置；请选择可用的内容包后保存。" : selected.Notice;
        }

        void LoadResolutions()
        {
            foreach (var value in Screen.resolutions) AddResolution(value.width, value.height);
            AddResolution(1280, 720);
            AddResolution(1600, 900);
            AddResolution(1920, 1080);
            AddResolution(Screen.width, Screen.height);
            AddResolution(draft.Width, draft.Height);
            resolutions.Sort((left, right) => left.x != right.x ? left.x.CompareTo(right.x) : left.y.CompareTo(right.y));
        }

        void AddResolution(int width, int height)
        {
            if (width < 640 || width > 7680 || height < 360 || height > 4320) return;
            var size = new Vector2Int(width, height);
            if (!resolutions.Contains(size)) resolutions.Add(size);
        }

        static string ResolutionLabel(Vector2Int value) => value.x + " × " + value.y;

        string LoadPacks()
        {
            var rules = UnityEngine.Resources.Load<TextAsset>("StreamDuelRules");
            int errors = 0;
            if (rules != null)
            {
                foreach (var asset in UnityEngine.Resources.LoadAll<TextAsset>("Packs").OrderBy(value => value.name, StringComparer.Ordinal))
                {
                    if (asset.name == "LICENSE") continue;
                    try { AddPack(ContentDb.FromJson(rules.text, asset.text)); }
                    catch (Exception) { errors++; }
                }
                if (!GameRuntimeSettings.IsSmokeTest)
                    foreach (var result in PackLoader.LoadDirectory(rules.text, Path.Combine(Application.persistentDataPath, "packs")))
                    {
                        if (result.Db == null) errors++;
                        else AddPack(result.Db);
                    }
            }
            else errors++;
            if (!packs.Any(value => value.Id == draft.DefaultPack))
                packs.Add(new PackChoice { Id = draft.DefaultPack, Label = "未找到内容包 [" + draft.DefaultPack + "]", Missing = true });
            return errors > 0 ? "有 " + errors + " 个内容包未能读取，可继续使用列表中的其它内容包。" : "";
        }

        void AddPack(ContentDb db)
        {
            if (packs.Any(value => value.Id == db.PackId)) return;
            packs.Add(new PackChoice { Id = db.PackId, Label = db.PackName + " [" + db.PackId + "]", Notice = db.PackNotice ?? "" });
        }

        static VisualElement Section(VisualElement parent, string text)
        {
            var section = new VisualElement();
            section.style.flexShrink = 0;
            section.style.marginTop = 20;
            section.style.paddingBottom = 16;
            parent.Add(section);
            var title = new Label(text) { enableRichText = false };
            title.style.fontSize = 28;
            title.style.color = (Color)new Color32(255, 181, 71, 255);
            title.style.whiteSpace = WhiteSpace.Normal;
            title.style.marginBottom = 12;
            section.Add(title);
            return section;
        }

        static Label AddNote(VisualElement parent, string text)
        {
            var label = new Label(text) { enableRichText = false };
            label.AddToClassList("screen-note");
            label.style.flexShrink = 0;
            label.style.marginTop = 8;
            parent.Add(label);
            return label;
        }

        static void AddControl(VisualElement parent, VisualElement control)
        {
            control.style.minHeight = 52;
            control.style.height = StyleKeyword.Auto;
            control.style.flexShrink = 0;
            control.style.minWidth = 0;
            control.style.fontSize = 24;
            control.style.marginTop = 4;
            control.style.marginBottom = 10;
            control.style.whiteSpace = WhiteSpace.Normal;
            parent.Add(control);
            var input = control.Q(className: "unity-base-field__input");
            if (input != null)
            {
                input.style.minHeight = 48;
                input.style.paddingLeft = 12;
                input.style.paddingRight = 12;
                input.style.backgroundColor = (Color)new Color32(28, 37, 73, 255);
                input.style.color = (Color)new Color32(232, 235, 250, 255);
            }
            control.Query<Label>().ForEach(label => { label.enableRichText = false; label.style.whiteSpace = WhiteSpace.Normal; });
        }

        static void Field(VisualElement parent, string text, VisualElement control)
        {
            var label = new Label(text) { enableRichText = false };
            label.style.whiteSpace = WhiteSpace.Normal;
            label.style.flexShrink = 0;
            parent.Add(label);
            AddControl(parent, control);
        }

        static DropdownField Dropdown(VisualElement parent, string name, string title, List<string> choices, int index)
        {
            var field = new DropdownField(choices, Math.Max(0, index)) { name = name };
            Field(parent, title, field);
            return field;
        }

        void Bind<T>(BaseField<T> field, System.Action<T> changed)
        {
            EventCallback<ChangeEvent<T>> callback = evt => changed(evt.newValue);
            field.RegisterValueChangedCallback(callback);
            unbind.Add(() => field.UnregisterValueChangedCallback(callback));
        }

        void Button(VisualElement parent, string name, string text, System.Action action)
        {
            var button = new Button { name = name, text = text };
            button.AddToClassList("screen-button");
            button.clicked += action;
            unbind.Add(() => button.clicked -= action);
            parent.Add(button);
        }

        public void Tick(float dt) { }

        public void Dispose()
        {
            foreach (var remove in unbind) remove();
            unbind.Clear();
            packs.Clear();
            resolutions.Clear();
            draft = null;
            animation = resolution = ai = pack = null;
            timer = fullscreen = null;
            seconds = null;
            notice = packNotice = null;
        }

        sealed class PackChoice
        {
            public string Id, Label, Notice;
            public bool Missing;
        }
    }
}
