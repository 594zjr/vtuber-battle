using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using StreamDuel.Core;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.UIElements;

namespace StreamDuel.Game
{
    public sealed class MenuSmoke : MonoBehaviour
    {
        [Serializable]
        sealed class Report
        {
            public string unityVersion, outputDirectory, failure;
            public string inputScope = "InputSystem.QueueStateEvent 在临时键盘/手柄上发送 Tab/Enter 与方向键/South，检查运行时焦点及路由；其余按钮以 NavigationSubmitEvent 检查回调。未声称人工或物理手柄验收。";
            public string layoutScope = "检查可见文字、控件的边界、文字测量、非祖先元素交叠及滚动视口；长许可全文通过滚动阅读，截图仍需人工视觉复核。";
            public string logScope = "从 BeforeSceneLoad 收集运行时警告和错误；完整启动日志与编辑器停止 Play 后 Console 计数另行保留。";
            public string quitScope = "Editor 仅检查退出按钮可达；Player 真实执行退出回调并通过 wantsToQuit 暂缓退出，写出证据后正常退出。";
            public bool completed, developmentToolsExpected, quitRequested;
            public int warnings, errors;
            public List<string> steps = new List<string>();
            public List<string> screenshots = new List<string>();
            public List<string> logs = new List<string>();
            public List<ElementBounds> bounds = new List<ElementBounds>();
        }

        [Serializable]
        sealed class ElementBounds
        {
            public string screen, element, text;
            public int width, height;
            public float x, y, w, h;
        }

        static readonly Dictionary<string, string> Routes = new Dictionary<string, string>
        {
            { "open-team", ScreenIds.Team }, { "open-online", ScreenIds.Online },
            { "open-replay", ScreenIds.Replay }, { "open-settings", ScreenIds.Settings },
            { "open-about", ScreenIds.About }
        };

        public static bool Completed { get; private set; }
        public static int ExitCode { get; private set; }
        public static string ReportPath { get; private set; }
        public static int RequestedWidth { get; private set; }
        public static int RequestedHeight { get; private set; }

        Report report;
        bool running, quitHooked;
        float deadline;
        ScreenRouter router;
        UIDocument document;
        BattleScreen legacy;
        Keyboard keyboard, previousKeyboard;
        Gamepad gamepad, previousGamepad;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Install()
        {
            string[] args = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(args, "--menu-smoke-test");
            if (index < 0) return;
            Completed = false;
            ExitCode = 1;
            ReportPath = null;
            RequestedWidth = RequestedHeight = 0;
            var host = new GameObject("StreamDuel.MenuSmoke");
            DontDestroyOnLoad(host);
            host.AddComponent<MenuSmoke>().Configure(args, index);
        }

        void Configure(string[] args, int index)
        {
            report = new Report
            {
                unityVersion = Application.unityVersion,
                developmentToolsExpected = Application.isEditor || Debug.isDebugBuild
            };
            running = true;
            deadline = Time.realtimeSinceStartup + 300f;
            Application.logMessageReceived += CollectLog;
            try
            {
                Require(index + 1 < args.Length && Path.IsPathFullyQualified(args[index + 1]),
                    "--menu-smoke-test 后必须提供绝对输出目录。");
                // 证据目录编号不参与游戏或 Battle 的随机流。
                report.outputDirectory = Path.Combine(Path.GetFullPath(args[index + 1]), "run-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(report.outputDirectory);
                ReportPath = Path.Combine(report.outputDirectory, "result.json");
                Debug.Log("MENU_SMOKE_OUTPUT " + report.outputDirectory);
            }
            catch (Exception ex) { Finish(ex.ToString()); }
        }

        void Start()
        {
            if (running) StartCoroutine(Guard(Run()));
        }

        void LateUpdate()
        {
            if (running && Time.realtimeSinceStartup >= deadline)
                Finish("主菜单验收超过 300 秒；必须使用真实渲染，不能以 -nographics 启动。");
        }

        void CollectLog(string message, string stack, LogType type)
        {
            if (type == LogType.Warning) report.warnings++;
            if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert) report.errors++;
            if (type != LogType.Log) report.logs.Add(type + ": " + message + "\n" + stack);
        }

        IEnumerator Guard(IEnumerator routine)
        {
            var routines = new Stack<IEnumerator>();
            routines.Push(routine);
            while (running)
            {
                bool more = false;
                object wait = null;
                string failure = null;
                try
                {
                    more = routines.Peek().MoveNext();
                    if (more) wait = routines.Peek().Current;
                    else routines.Pop();
                }
                catch (Exception ex) { failure = ex.ToString(); }
                if (failure != null || routines.Count == 0)
                {
                    Finish(failure);
                    yield break;
                }
                if (!more) continue;
                if (wait is IEnumerator nested) { routines.Push(nested); continue; }
                yield return wait;
            }
        }

        IEnumerator Settle()
        {
            yield return null;
            yield return null;
            yield return new WaitForEndOfFrame();
        }

        IEnumerator Run()
        {
            yield return Settle();
            var routers = FindObjectsByType<ScreenRouter>(FindObjectsInactive.Include);
            var documents = FindObjectsByType<UIDocument>(FindObjectsInactive.Include);
            Require(routers.Length == 1 && documents.Length == 1, "必须只有一个 ScreenRouter 和一个 UIDocument。");
            router = routers[0];
            document = documents[0];
            legacy = router.GetComponent<BattleScreen>();
            Require(legacy != null && router.CurrentScreenId == ScreenIds.Menu && !legacy.enabled,
                "普通启动没有进入正式主菜单。");
            Require(document.rootVisualElement.panel != null, "UIDocument 未连接运行时面板。");
            Require(legacy.MenuPacks.Count > 0 && legacy.MenuPackErrors.Count == 0, "内容包读取不完整。");
            report.steps.Add("正式菜单为默认入口；只有一个 UIDocument，旧画面暂停，内置包载入无错误。");

            previousKeyboard = Keyboard.current;
            previousGamepad = Gamepad.current;
            keyboard = InputSystem.AddDevice<Keyboard>("T81SmokeKeyboard");
            gamepad = InputSystem.AddDevice<Gamepad>("T81SmokeGamepad");
            yield return Settle();

            int[] widths = { 1280, 1920 };
            int[] heights = { 720, 1080 };
            for (int resolution = 0; resolution < widths.Length; resolution++)
            {
                RequestedWidth = widths[resolution];
                RequestedHeight = heights[resolution];
                Screen.SetResolution(RequestedWidth, RequestedHeight, FullScreenMode.Windowed);
                float resizeDeadline = Time.realtimeSinceStartup + 15f;
                while (Screen.width != RequestedWidth || Screen.height != RequestedHeight)
                {
                    Require(Time.realtimeSinceStartup < resizeDeadline, "验收分辨率切换超时。Editor 需要 bridge 设置 GameView。");
                    yield return null;
                }
                router.Show(ScreenIds.Menu);
                yield return Settle();
                SelectPack("original");
                yield return Settle();
                ValidateMenu();
                Capture("menu-original");
                ValidatePage(ScreenIds.Menu);
                string fanId = null;
                foreach (var pack in legacy.MenuPacks)
                    if (pack.PackId.StartsWith("fan_", StringComparison.Ordinal)) { fanId = pack.PackId; break; }
                Require(fanId != null, "验收缺少同人内容包。");
                SelectPack(fanId);
                yield return Settle();
                ValidateMenu();
                Capture("menu-fan");
                ValidatePage(ScreenIds.Menu);
                report.steps.Add(Size + "：原创包/同人包下拉回调与旧组队共用内容库，notice 原文常驻显示。");

                yield return CheckDuplicatePackLabels();
                yield return CheckInput(false);
                yield return CheckInput(true);
                foreach (var route in Routes)
                {
                    Submit(route.Key);
                    yield return Settle();
                    Require(router.CurrentScreenId == route.Value && !legacy.enabled, "菜单路由失败：" + route.Key);
                    if (route.Value == ScreenIds.About) yield return CheckAbout();
                    else ValidatePage(route.Value);
                    Submit(route.Value == ScreenIds.About ? "about-back" : "back-button");
                    yield return Settle();
                    Require(router.CurrentScreenId == ScreenIds.Menu, "返回正式菜单失败：" + route.Value);
                    Require(legacy.MenuPack.PackId == fanId, "返回菜单丢失选择的内容包。");
                }
                report.steps.Add(Size + "：对电脑、联网、录像、设置、关于按钮及返回均通过。");
                yield return CheckDevelopmentTools();
            }
            yield return CheckDisposal();
            yield return CheckMissingContentGuard();
            if (!Application.isEditor)
            {
                Application.wantsToQuit += CaptureQuitRequest;
                quitHooked = true;
                Submit("quit-game");
                yield return null;
                Require(report.quitRequested, "退出按钮没有请求退出 Player。");
                RemoveQuitHook();
                report.steps.Add("发布 Player 退出按钮实际请求退出；wantsToQuit 暂缓一次以保存证据。");
            }
            report.completed = true;
        }

        string Size => RequestedWidth + "×" + RequestedHeight;

        void SelectPack(string id)
        {
            var dropdown = document.rootVisualElement.Q<DropdownField>("menu-pack");
            Require(dropdown != null && dropdown.enabledInHierarchy, "内容包选择器不可用。");
            int index = -1;
            for (int i = 0; i < legacy.MenuPacks.Count; i++)
                if (legacy.MenuPacks[i].PackId == id) { index = i; break; }
            Require(index >= 0 && dropdown.choices.Count == legacy.MenuPacks.Count, "下拉选项与可用内容包不一致。");
            dropdown.index = index;
            Require(legacy.MenuPack.PackId == id, "下拉变更没有更新共用内容包。");
        }

        IEnumerator CheckDuplicatePackLabels()
        {
            Require(!legacy.enabled && !legacy.MenuPackLocked, "重名内容包检查须在菜单中且没有锁定的对局。");
            var rules = Resources.Load<TextAsset>("StreamDuelRules");
            var original = Resources.Load<TextAsset>("Packs/original");
            Require(rules != null && original != null, "重名内容包检查缺少内置规则或原创包资源。");
            var fixture = new List<ContentDb>();
            string[] ids = { "a", "b", "c" };
            string[] names = { "同名", "同名", "同名（a）" };
            string[] notices = { "A", "B", "C" };
            for (int i = 0; i < ids.Length; i++)
            {
                var pack = ContentDb.FromJson(rules.text, original.text);
                pack.PackId = ids[i];
                pack.PackName = names[i];
                pack.PackNotice = notices[i];
                fixture.Add(pack);
            }

            FieldInfo Field(string name)
            {
                var field = typeof(BattleScreen).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
                Require(field != null, "重名内容包检查找不到旧组件字段：" + name);
                return field;
            }

            // 夹具只替换本次冒烟的内存来源；真实回调会更新选择、组队和滚动位置，结束后一并还原。
            var saved = new Dictionary<FieldInfo, object>();
            foreach (string name in new[] { "packs", "db", "pick", "teamDetail", "rosterScroll", "teamDetailScroll" })
            {
                var field = Field(name);
                saved.Add(field, field.GetValue(legacy));
            }
            var settings = Field("settings").GetValue(legacy) as GameSettings;
            Require(settings != null, "重名内容包检查缺少内存设置。");
            string previousDefaultPack = settings.DefaultPack;
            var previousPack = legacy.MenuPack;
            object previousArgs = router.CurrentArgs;
            try
            {
                Field("packs").SetValue(legacy, fixture);
                Field("db").SetValue(legacy, fixture[1]);
                settings.DefaultPack = "b";
                router.Show(ScreenIds.Menu);
                yield return Settle();
                var dropdown = document.rootVisualElement.Q<DropdownField>("menu-pack");
                Require(dropdown != null && dropdown.enabledInHierarchy && dropdown.choices.Count == 3,
                    "重名内容包夹具没有生成三个可选项。");
                Require(ReferenceEquals(legacy.MenuPack, fixture[1]) && dropdown.index == 1
                    && document.rootVisualElement.Q<Label>("pack-notice").text == "B",
                    "重名内容包检查没有从第二个内容包 b 开始。");

                // 模拟弹出列表按显示值选中第三项，不能以直接调用 SelectMenuPack 绕过 Dropdown 的值到下标映射。
                dropdown.value = dropdown.choices[2];
                yield return Settle();
                Require(ReferenceEquals(legacy.MenuPack, fixture[2]) && dropdown.index == 2
                    && document.rootVisualElement.Q<Label>("pack-notice").text == "C"
                    && settings.DefaultPack == "c",
                    "选择第三项 c 时回调选中了同标签的其它包，或声明与内存设置没有同步。");
                Require(new HashSet<string>(dropdown.choices, StringComparer.Ordinal).Count == 3,
                    "重名内容包的最终显示标签仍然重复。");
                Require(dropdown.choices[2] == fixture[2].PackName,
                    "为重名内容包去重时改变了原本唯一的包名。");
                ValidateMenu();
                ValidatePage(ScreenIds.Menu);

                router.Show(ScreenIds.Menu);
                yield return Settle();
                dropdown = document.rootVisualElement.Q<DropdownField>("menu-pack");
                Require(dropdown != null && dropdown.choices.Count == 3 && dropdown.index == 2
                    && dropdown.value == dropdown.choices[2] && ReferenceEquals(legacy.MenuPack, fixture[2])
                    && document.rootVisualElement.Q<Label>("pack-notice").text == "C",
                    "重建菜单后第三个内容包 c 的选择或声明丢失。");
                Require(new HashSet<string>(dropdown.choices, StringComparer.Ordinal).Count == 3,
                    "重建菜单后出现重复的内容包显示标签。");
                for (int i = 0; i < fixture.Count; i++)
                    Require(ReferenceEquals(legacy.MenuPacks[i], fixture[i]), "内容包去重改变了原始排列顺序。");
                ValidateMenu();
                ValidatePage(ScreenIds.Menu);
                report.steps.Add(Size + "：同名/同名/同名（a）三个包从 b 经 Dropdown.value 选择 c，最终标签唯一、声明为 C，重建后仍选 c。");
            }
            finally
            {
                foreach (var entry in saved) entry.Key.SetValue(legacy, entry.Value);
                settings.DefaultPack = previousDefaultPack;
                router.Show(ScreenIds.Menu, previousArgs);
            }
            yield return Settle();
            Require(ReferenceEquals(legacy.MenuPack, previousPack) && settings.DefaultPack == previousDefaultPack,
                "重名内容包检查结束后没有还原原内容包选择。");
            ValidateMenu();
        }

        void ValidateMenu()
        {
            var root = document.rootVisualElement;
            var notice = root.Q<Label>("pack-notice");
            Require(notice != null && notice.visible && notice.resolvedStyle.display != DisplayStyle.None
                && notice.text == legacy.MenuPack.PackNotice, "菜单同人声明未按内容包原文显示。");
            string[] names = { "open-team", "open-online", "open-replay", "open-settings", "open-about", "quit-game" };
            string[] labels = { "对电脑", "联网对战", "录像", "设置", "关于", "退出" };
            for (int i = 0; i < names.Length; i++)
            {
                var button = root.Q<Button>(names[i]);
                Require(button != null && button.text == labels[i] && button.focusable && button.enabledInHierarchy,
                    "正式菜单按钮缺失、文字错误或不能聚焦：" + names[i]);
            }
            bool found = root.Q<Foldout>("developer-tools") != null;
            Require(found == report.developmentToolsExpected, "开发入口的 Editor/Development/Release 可见规则错误。");
        }

        IEnumerator CheckInput(bool useGamepad)
        {
            var first = document.rootVisualElement.Q<Button>("open-team");
            first.Focus();
            yield return Settle();
            Require(ReferenceEquals(document.rootVisualElement.focusController.focusedElement, first), "不能设置按键验收初始焦点。");
            if (useGamepad) yield return PressGamepad(GamepadButton.DpadDown);
            else yield return PressKey(Key.Tab);
            var selected = document.rootVisualElement.focusController.focusedElement as Button;
            Require(selected != null && !ReferenceEquals(selected, first) && Routes.ContainsKey(selected.name),
                (useGamepad ? "手柄方向键" : "键盘 Tab") + "没有移动到另一个正式菜单按钮；实际焦点="
                + (document.rootVisualElement.focusController.focusedElement as VisualElement)?.name);
            string expected = Routes[selected.name];
            string selectedName = selected.name;
            if (useGamepad) yield return PressGamepad(GamepadButton.South);
            else yield return PressKey(Key.Enter);
            yield return Settle();
            Require(router.CurrentScreenId == expected, (useGamepad ? "手柄 South" : "键盘 Enter") + "没有确认菜单按钮。");
            string backName = expected == ScreenIds.About ? "about-back" : "back-button";
            var back = document.rootVisualElement.Q<Button>(backName);
            Require(back != null, "输入验收缺少返回按钮。");
            back.Focus();
            if (useGamepad) yield return PressGamepad(GamepadButton.South);
            else yield return PressKey(Key.Enter);
            yield return Settle();
            Require(router.CurrentScreenId == ScreenIds.Menu, "真实输入确认返回失败。");
            report.steps.Add(Size + "：InputSystem " + (useGamepad ? "Gamepad dpad/South" : "Keyboard Tab/Enter")
                + "，open-team→" + selectedName + "→" + expected + "→返回菜单。");
        }

        IEnumerator PressKey(Key key)
        {
            InputSystem.QueueStateEvent(keyboard, new KeyboardState(key));
            yield return null;
            yield return null;
            InputSystem.QueueStateEvent(keyboard, new KeyboardState());
            yield return null;
            yield return null;
        }

        IEnumerator PressGamepad(GamepadButton button)
        {
            InputSystem.QueueStateEvent(gamepad, new GamepadState().WithButton(button));
            yield return null;
            yield return null;
            InputSystem.QueueStateEvent(gamepad, new GamepadState());
            yield return null;
            yield return null;
        }

        IEnumerator CheckAbout()
        {
            var root = document.rootVisualElement;
            var version = root.Q<Label>("about-version");
            var free = root.Q<Label>("about-free-notice");
            Require(version != null && version.text.Contains(Application.version), "关于页缺少当前 Application.version。");
            Require(free != null && free.text == "本作免费、非商业，不接受任何形式的付费", "关于页免费声明不完整。");
            string[] tabs = { "code", "content", "third-party" };
            string[] labels = { "about-code-license", "about-content-license", "about-third-party" };
            string[] resources = { "About/CodeLicense", "About/ContentLicense", "About/ThirdParty" };
            for (int i = 0; i < tabs.Length; i++)
            {
                Submit("about-tab-" + tabs[i]);
                yield return Settle();
                var text = root.Q<Label>(labels[i]);
                Require(text != null && text.visible && text.resolvedStyle.display != DisplayStyle.None && !string.IsNullOrWhiteSpace(text.text),
                    "许可页文本未显示：" + tabs[i]);
                Require(!text.enableRichText, "许可原文不应按富文本解释。");
                var source = Resources.Load<TextAsset>(resources[i]);
                Require(source != null && text.text == source.text, "许可页没有完整保留打包资源原文：" + tabs[i]);
                Capture("about-" + tabs[i]);
                ValidatePage(ScreenIds.About);
                var scroll = root.Q<ScrollView>("about-license-scroll");
                Require(scroll != null, "关于页缺少许可滚动容器。");
                if (text.worldBound.height > scroll.contentViewport.worldBound.height + 4)
                {
                    Submit("about-scroll-down");
                    yield return Settle();
                    Require(scroll.scrollOffset.y > 0, "长许可无法通过翻页按钮滚动。");
                    Capture("about-" + tabs[i] + "-scrolled");
                }
            }
            report.steps.Add(Size + "：关于版本、免费声明、三类许可文本与 Resources 原文逐字相等，长文可滚动。");
        }

        IEnumerator CheckDevelopmentTools()
        {
            var foldout = document.rootVisualElement.Q<Foldout>("developer-tools");
            if (!report.developmentToolsExpected)
            {
                Require(foldout == null && document.rootVisualElement.Q<Button>("open-legacy") == null
                    && document.rootVisualElement.Q<Button>("open-animation-preview") == null
                    && document.rootVisualElement.Q<Button>("open-effect-preview") == null, "发布包泄露开发入口。");
                report.steps.Add(Size + "：非 Development Player 不创建开发入口节点。");
                yield break;
            }
            Require(foldout != null && !foldout.value, "开发入口必须默认折叠。");
            foldout.value = true;
            yield return Settle();
            Capture("menu-development");
            ValidatePage(ScreenIds.Menu);
            string[] names = { "open-animation-preview", "open-effect-preview" };
            string[] targets = { ScreenIds.AnimationPreview, ScreenIds.EffectPreview };
            for (int i = 0; i < names.Length; i++)
            {
                Submit(names[i]);
                yield return Settle();
                Require(router.CurrentScreenId == targets[i], "开发预览路由失败：" + names[i]);
                Submit("back-button");
                yield return Settle();
                Require(router.CurrentScreenId == ScreenIds.Menu, "开发预览返回失败。");
                document.rootVisualElement.Q<Foldout>("developer-tools").value = true;
                yield return Settle();
            }
            Submit("open-legacy");
            yield return Settle();
            Require(router.CurrentScreenId == ScreenIds.Legacy && legacy.enabled, "旧版对战开发入口失效。");
            Require(legacy.MainMenuRequested != null, "旧版缺少返回菜单回调。");
            legacy.MainMenuRequested();
            yield return Settle();
            Require(router.CurrentScreenId == ScreenIds.Menu && !legacy.enabled, "旧版返回菜单失效。");
            report.steps.Add(Size + "：开发折叠、两个预览占位路由及旧版往返均通过。");
        }

        IEnumerator CheckDisposal()
        {
            var old = document.rootVisualElement.Q<Button>("open-about");
            Submit("open-team");
            yield return Settle();
            SendSubmit(old);
            yield return Settle();
            Require(router.CurrentScreenId == ScreenIds.Team, "已释放菜单的旧按钮仍能触发导航。");
            Submit("back-button");
            yield return Settle();
            Require(router.CurrentScreenId == ScreenIds.Menu, "回调释放检查不能返回菜单。");
            report.steps.Add("旧菜单卸载后按钮回调已解除，新菜单回调仍可用。");
        }

        IEnumerator CheckMissingContentGuard()
        {
            Require(!legacy.enabled, "内容恢复检查须在旧组件停用时运行。");
            var previousPack = legacy.MenuPack;
            var previousPacks = legacy.MenuPacks;
            var field = typeof(BattleScreen).GetField("db", BindingFlags.Instance | BindingFlags.NonPublic);
            Require(field != null, "内容恢复检查找不到旧组件的内容库字段。");
            // 只模拟内容库遗失以覆盖恢复 guard；不把这一检查当作真实脚本域重载验收。
            field.SetValue(legacy, null);
            legacy.EnsureMenuContentLoaded();
            Require(!legacy.enabled && legacy.MenuPack != null && !ReferenceEquals(previousPack, legacy.MenuPack),
                "停用组件的内容恢复 guard 没有重建内容库。");
            Require(legacy.MenuPack.PackId == previousPack.PackId && legacy.MenuPacks.Count == previousPacks.Count,
                "内容恢复 guard 丢失选包或重复载入内容包。");
            for (int i = 0; i < previousPacks.Count; i++)
                Require(legacy.MenuPacks[i].PackId == previousPacks[i].PackId, "恢复后的内容包顺序改变。");
            router.Show(ScreenIds.Menu);
            yield return Settle();
            ValidateMenu();
            Submit("open-team");
            yield return Settle();
            Require(router.CurrentScreenId == ScreenIds.Team, "内容恢复后菜单不能导航。");
            Submit("back-button");
            yield return Settle();
            report.steps.Add("模拟 db 遗失：停用的旧组件可经 EnsureMenuContentLoaded 恢复内容，包数量/顺序/选择保留且新菜单可用；这不是实际域重载证明。");
        }

        void Submit(string name)
        {
            var button = document.rootVisualElement.Q<Button>(name);
            Require(button != null && button.enabledInHierarchy && button.visible, "找不到可用按钮：" + name);
            button.Focus();
            SendSubmit(button);
        }

        static void SendSubmit(Button button)
        {
            using (var submit = NavigationSubmitEvent.GetPooled())
            {
                submit.target = button;
                button.SendEvent(submit);
            }
        }

        void ValidatePage(string id)
        {
            var page = document.rootVisualElement.Q<VisualElement>(id + "-screen");
            Require(page != null, "缺少界面根节点：" + id);
            var elements = new List<VisualElement>();
            CollectVisible(page, elements);
            Rect screen = document.rootVisualElement.worldBound;
            var clipped = new List<Rect>();
            var visible = new List<VisualElement>();
            foreach (var element in elements)
            {
                Rect rect = element.worldBound;
                Rect clip = screen;
                bool scrolled = false;
                for (var ancestor = element.hierarchy.parent; ancestor != null; ancestor = ancestor.hierarchy.parent)
                    if (ancestor is ScrollView scroll)
                    {
                        clip = Intersection(clip, scroll.contentViewport.worldBound);
                        scrolled = true;
                    }
                Rect seen = Intersection(rect, clip);
                if (seen.width <= 0 || seen.height <= 0) continue;
                Require(Finite(rect.x) && Finite(rect.y) && Finite(rect.width) && Finite(rect.height)
                    && rect.width > 0 && rect.height > 0, "控件布局无效：" + Describe(element));
                Require(scrolled ? rect.xMin >= clip.xMin - 2 && rect.xMax <= clip.xMax + 2 : Contains(screen, rect),
                    "控件超出可读宽度或屏幕：" + Describe(element));
                var text = element as TextElement;
                if (text != null && !string.IsNullOrEmpty(text.text))
                {
                    Rect content = text.contentRect;
                    Vector2 measured = text.MeasureTextSize(text.text, content.width,
                        text.resolvedStyle.whiteSpace == WhiteSpace.Normal ? VisualElement.MeasureMode.Exactly : VisualElement.MeasureMode.Undefined,
                        0, VisualElement.MeasureMode.Undefined);
                    Require(!text.isElided && measured.x <= content.width + 2 && measured.y <= content.height + 2,
                        "文字可能被裁切：" + Describe(element) + " measured=" + measured + " content=" + content);
                }
                report.bounds.Add(new ElementBounds
                {
                    screen = id, element = Describe(element), text = text?.text,
                    width = RequestedWidth, height = RequestedHeight,
                    x = rect.x, y = rect.y, w = rect.width, h = rect.height
                });
                visible.Add(element);
                clipped.Add(seen);
            }
            Require(visible.Count > 0, "页面没有可见文字或控件。");
            for (int i = 0; i < visible.Count; i++)
                for (int j = i + 1; j < visible.Count; j++)
                {
                    if (visible[i].Contains(visible[j]) || visible[j].Contains(visible[i])) continue;
                    Rect overlap = Intersection(clipped[i], clipped[j]);
                    Require(overlap.width <= 1 || overlap.height <= 1, "可见控件交叠：" + Describe(visible[i]) + " / " + Describe(visible[j]));
                }
        }

        static void CollectVisible(VisualElement element, List<VisualElement> output)
        {
            // Foldout 的子项枚举会越过内部容器，真实可见性须检查完整父链。
            for (var ancestor = element; ancestor != null; ancestor = ancestor.hierarchy.parent)
                if (!ancestor.visible || ancestor.resolvedStyle.display == DisplayStyle.None) return;
            if (element is TextElement text && !string.IsNullOrEmpty(text.text)) output.Add(element);
            foreach (var child in element.Children()) CollectVisible(child, output);
        }

        static Rect Intersection(Rect a, Rect b)
        {
            return Rect.MinMaxRect(Mathf.Max(a.xMin, b.xMin), Mathf.Max(a.yMin, b.yMin), Mathf.Min(a.xMax, b.xMax), Mathf.Min(a.yMax, b.yMax));
        }

        static string Describe(VisualElement element) { return element.GetType().Name + "#" + element.name; }
        static bool Finite(float value) { return !float.IsNaN(value) && !float.IsInfinity(value); }
        static bool Contains(Rect outer, Rect inner)
        {
            return inner.xMin >= outer.xMin - 2 && inner.yMin >= outer.yMin - 2 && inner.xMax <= outer.xMax + 2 && inner.yMax <= outer.yMax + 2;
        }
        static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

        void Capture(string name)
        {
            Texture2D image = null;
            try
            {
                image = ScreenCapture.CaptureScreenshotAsTexture();
                Require(image != null && image.width == RequestedWidth && image.height == RequestedHeight, "截图分辨率不正确。");
                byte[] png = image.EncodeToPNG();
                Require(png != null && png.Length > 8, "截图编码失败。");
                string file = RequestedWidth + "x" + RequestedHeight + "-" + name + ".png";
                string path = Path.Combine(report.outputDirectory, file);
                File.WriteAllBytes(path, png);
                Require(new FileInfo(path).Length == png.Length, "截图写入不完整。");
                report.screenshots.Add(file);
                var pixels = image.GetPixels32();
                int stride = Math.Max(1, pixels.Length / 65536), count = 0, bright = 0, darkest = 255, lightest = 0;
                for (int i = 0; i < pixels.Length; i += stride)
                {
                    var p = pixels[i];
                    int luminance = (299 * p.r + 587 * p.g + 114 * p.b) / 1000;
                    darkest = Math.Min(darkest, luminance);
                    lightest = Math.Max(lightest, luminance);
                    if (luminance >= 150) bright++;
                    count++;
                }
                Require(lightest - darkest >= 48 && bright >= Math.Max(8, count / 2000), "截图没有有效渲染内容（全黑或近乎单色）：" + file);
            }
            finally { if (image != null) Destroy(image); }
        }

        bool CaptureQuitRequest() { report.quitRequested = true; return false; }
        void RemoveQuitHook()
        {
            if (!quitHooked) return;
            Application.wantsToQuit -= CaptureQuitRequest;
            quitHooked = false;
        }

        void ReleaseInputDevices()
        {
            if (keyboard != null && keyboard.added) InputSystem.RemoveDevice(keyboard);
            if (gamepad != null && gamepad.added) InputSystem.RemoveDevice(gamepad);
            keyboard = null;
            gamepad = null;
            if (previousKeyboard != null && previousKeyboard.added) previousKeyboard.MakeCurrent();
            if (previousGamepad != null && previousGamepad.added) previousGamepad.MakeCurrent();
        }

        void OnDestroy()
        {
            RemoveQuitHook();
            ReleaseInputDevices();
            Application.logMessageReceived -= CollectLog;
        }

        void Finish(string failure)
        {
            if (!running) return;
            running = false;
            try
            {
                RemoveQuitHook();
                ReleaseInputDevices();
                report.failure = failure;
                if (failure != null) report.completed = false;
                ExitCode = report.completed && report.warnings == 0 && report.errors == 0 ? 0 : 1;
                if (ReportPath != null) File.WriteAllText(ReportPath, JsonUtility.ToJson(report, true));
                else ExitCode = 1;
            }
            catch (Exception ex)
            {
                ExitCode = 1;
                Debug.LogError("主菜单验收报告写入失败：" + ex);
            }
            finally
            {
                Application.logMessageReceived -= CollectLog;
                Completed = true;
                if (ExitCode == 0) Debug.Log("MENU_SMOKE_PASS " + ReportPath);
                else Debug.LogError("MENU_SMOKE_FAIL " + (failure ?? "检测到警告或错误，请查看报告及完整日志。") + " " + ReportPath);
                if (!Application.isEditor) Application.Quit(ExitCode);
            }
        }
    }
}
