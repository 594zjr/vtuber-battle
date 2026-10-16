using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using StreamDuel.Core;
using UnityEngine;
using UnityEngine.UIElements;

namespace StreamDuel.Game
{
    public sealed class UiSmoke : MonoBehaviour
    {
        [Serializable]
        sealed class Report
        {
            public string unityVersion, outputDirectory, failure;
            public string navigation = "NavigationSubmitEvent 程序触发现有按钮回调；不代表人工鼠标验收。旧版返回菜单调用既有 MainMenuRequested 回调。";
            public string layoutScope = "检查当前可见按钮、文字、头像及血条的屏幕边界、滚动视口边界、文字测量和非祖先元素交叠；截图仍需人工复核视觉效果。";
            public string logScope = "从 BeforeSceneLoad 开始收集；更早的 Unity 启动诊断须另查完整 Player 日志。";
            public bool completed, providersChecked;
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

        public static bool Completed { get; private set; }
        public static int ExitCode { get; private set; }
        public static string ReportPath { get; private set; }
        public static int RequestedWidth { get; private set; }
        public static int RequestedHeight { get; private set; }

        Report report;
        bool running;
        float deadline;
        ScreenRouter router;
        UIDocument document;
        BattleScreen legacy;
        SolidColorPortraitProvider portraits;

        sealed class LifecycleProbe : IScreen
        {
            public const string ScreenId = "ui-smoke-lifecycle";
            public string Id { get { return ScreenId; } }
            public int Ticks, Disposals, DuplicateTicks;
            int lastTick = -1;

            public VisualElement Build(ScreenRouter owner, object args)
            {
                var page = new VisualElement();
                page.Add(new Label("路由生命周期验收"));
                return page;
            }

            public void Tick(float dt)
            {
                if (lastTick == Time.frameCount) DuplicateTicks++;
                lastTick = Time.frameCount;
                Ticks++;
            }

            public void Dispose() { Disposals++; }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Install()
        {
            string[] args = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(args, "--ui-smoke-test");
            if (index < 0) return;
            Completed = false;
            ExitCode = 1;
            ReportPath = null;
            RequestedWidth = RequestedHeight = 0;
            var host = new GameObject("StreamDuel.UiSmoke");
            DontDestroyOnLoad(host);
            host.AddComponent<UiSmoke>().Configure(args, index);
        }

        void Configure(string[] args, int index)
        {
            report = new Report { unityVersion = Application.unityVersion };
            running = true;
            deadline = Time.realtimeSinceStartup + 300f;
            Application.logMessageReceived += CollectLog;
            try
            {
                Require(index + 1 < args.Length && Path.IsPathFullyQualified(args[index + 1]),
                    "--ui-smoke-test 后必须提供绝对输出目录。");
                // 每轮证据独立保存；目录编号不参与游戏或 Battle 随机数。
                report.outputDirectory = Path.Combine(Path.GetFullPath(args[index + 1]), "run-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(report.outputDirectory);
                ReportPath = Path.Combine(report.outputDirectory, "result.json");
                Debug.Log("UI_SMOKE_OUTPUT " + report.outputDirectory);
            }
            catch (Exception ex) { Finish(ex.ToString()); }
        }

        void Start()
        {
            if (running) StartCoroutine(Guard(Run()));
        }

        void LateUpdate()
        {
            // 截图等待不到帧末时也要有退出路径，不能让验收进程一直挂起。
            if (running && Time.realtimeSinceStartup >= deadline)
                Finish("UI 验收超过 300 秒；必须使用真实渲染，不能以 -nographics 启动。");
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
                if (wait is IEnumerator nested)
                {
                    routines.Push(nested);
                    continue;
                }
                yield return wait;
            }
        }

        IEnumerator Run()
        {
            yield return null;
            var routers = FindObjectsByType<ScreenRouter>(FindObjectsInactive.Include);
            var documents = FindObjectsByType<UIDocument>(FindObjectsInactive.Include);
            Require(routers.Length == 1 && documents.Length == 1, "必须只有一个 ScreenRouter 和一个 UIDocument。");
            router = routers[0];
            document = documents[0];
            legacy = router.GetComponent<BattleScreen>();
            Require(legacy != null && router.CurrentScreenId == ScreenIds.Legacy && legacy.enabled,
                "默认入口没有保持可用的旧版对战。");
            Require(document.rootVisualElement.panel != null, "UIDocument 未连接到运行时面板。");
            report.steps.Add("单一 UIDocument，默认旧版对战入口已启用。");

            var rules = Resources.Load<TextAsset>("StreamDuelRules");
            var pack = Resources.Load<TextAsset>("Packs/original");
            Require(rules != null && pack != null, "缺少默认提供器验收用的内置内容包。");
            ContentDb db = ContentDb.FromJson(rules.text, pack.text);
            portraits = new SolidColorPortraitProvider();
            Texture2D texture = portraits.Get(db, db.Streamers[0].Id);
            Require(texture != null && texture.width > 0 && texture.height > 0
                && ReferenceEquals(texture, portraits.Get(db, db.Streamers[0].Id)), "默认头像未提供有效纹理或未复用缓存。");
            portraits.Dispose();
            portraits = null;
            yield return null;
            Require(texture == null, "默认头像缓存 Dispose 后纹理未释放。");
            var animator = new NullAnimator();
            animator.Play(Array.Empty<AnimCue>());
            Require(!animator.IsPlaying, "NullAnimator 没有立即结束。");
            animator.SkipToEnd();
            Require(!animator.IsPlaying, "NullAnimator 跳过后仍在播放。");
            var icon = new TextStatusIconProvider().Make("smoke-status", "状态占位");
            Require(icon is Label && ((Label)icon).text == "状态占位", "默认状态图标没有保留文字。");
            report.providersChecked = true;

            string[] pages = { ScreenIds.Team, ScreenIds.Battle, ScreenIds.Result, ScreenIds.Replay, ScreenIds.Settings, ScreenIds.Online };
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
                    Require(Time.realtimeSinceStartup < resizeDeadline,
                        "无法切换到验收分辨率 " + RequestedWidth + "×" + RequestedHeight + "。Editor 需要 bridge 设置 GameView。");
                    yield return null;
                }
                router.Show(ScreenIds.Menu);
                yield return null;
                yield return new WaitForEndOfFrame();
                Capture(ScreenIds.Menu);
                ValidatePage(ScreenIds.Menu);
                if (resolution == 0) yield return CheckLifecycle();

                foreach (string page in pages)
                {
                    // 正式菜单不再提供纯开发用的对战/结算占位入口，基础页面仍由路由直接验收。
                    bool developmentPage = page == ScreenIds.Battle || page == ScreenIds.Result;
                    if (developmentPage)
                        router.Show(page, new ScreenArgs { ReturnScreenId = ScreenIds.Menu });
                    else Submit("open-" + page);
                    yield return null;
                    yield return new WaitForEndOfFrame();
                    Require(router.CurrentScreenId == page && !legacy.enabled, "菜单按钮未进入界面：" + page);
                    Capture(page);
                    ValidatePage(page);
                    if (page == ScreenIds.Team) yield return CheckTeamScrollEnds();
                    if (page == ScreenIds.Battle) ValidateStage();
                    Submit("back-button");
                    yield return null;
                    yield return new WaitForEndOfFrame();
                    Require(router.CurrentScreenId == ScreenIds.Menu, "返回按钮未返回菜单：" + page);
                    report.steps.Add(RequestedWidth + "×" + RequestedHeight + "："
                        + (developmentPage ? "路由直接进入基础占位→" : "菜单按钮→") + page
                        + "→返回菜单（按钮使用 NavigationSubmitEvent）。");
                }

                var development = document.rootVisualElement.Q<Foldout>("developer-tools");
                if (Application.isEditor || Debug.isDebugBuild)
                {
                    Require(development != null, "Editor/Development 菜单缺少开发折叠区。");
                    development.value = true;
                    yield return null;
                    Submit("open-legacy");
                }
                else
                {
                    Require(development == null, "发布 Player 不应显示开发入口。");
                    router.Show(ScreenIds.Legacy);
                }
                yield return null;
                yield return new WaitForEndOfFrame();
                Require(router.CurrentScreenId == ScreenIds.Legacy && legacy.enabled
                    && document.rootVisualElement.resolvedStyle.display == DisplayStyle.None,
                    "旧版对战按钮未隐藏 UI Toolkit 并恢复旧版画面。");
                Capture(ScreenIds.Legacy);
                Require(legacy.MainMenuRequested != null, "旧版画面没有菜单回调。");
                legacy.MainMenuRequested();
                yield return null;
                Require(router.CurrentScreenId == ScreenIds.Menu && !legacy.enabled, "旧版菜单回调未恢复 UI Toolkit 菜单。");
                report.steps.Add(RequestedWidth + "×" + RequestedHeight + "：菜单→旧版→既有菜单回调。");
            }
            report.completed = true;
        }

        IEnumerator CheckLifecycle()
        {
            object args = new object();
            for (int mode = 0; mode < 3; mode++)
            {
                router.Show(ScreenIds.Menu, args);
                yield return null;
                yield return null;
                VisualElement oldRoot = document.rootVisualElement;
                VisualElement oldPage = oldRoot.Q<VisualElement>(ScreenIds.Menu + "-screen");
                Button oldButton = oldRoot.Q<Button>("open-team");
                Require(oldPage != null && oldButton != null, "生命周期检查缺少原菜单。");
                if (mode == 1) router.gameObject.SetActive(false);
                else if (mode == 2) document.enabled = false;
                else router.enabled = false;
                yield return null;
                Require(oldPage.parent == null, "停用路由后旧页面仍挂在根节点。");
                if (mode == 1) router.gameObject.SetActive(true);
                else if (mode == 2) document.enabled = true;
                else router.enabled = true;
                yield return null;
                yield return null;
                yield return new WaitForEndOfFrame();
                document = router.Document;
                Require(FindObjectsByType<UIDocument>(FindObjectsInactive.Include).Length == 1,
                    "恢复路由后出现多个 UIDocument。");
                Require(document != null && document.rootVisualElement.panel != null
                    && router.CurrentScreenId == ScreenIds.Menu && ReferenceEquals(router.CurrentArgs, args),
                    "恢复路由后页面或参数丢失。");
                Require(oldPage.parent == null, "恢复路由后错误复用了已释放的页面。");
                if (!ReferenceEquals(oldRoot, document.rootVisualElement))
                    Require(oldRoot.childCount == 0 && oldRoot.panel == null, "旧 UIDocument 根节点没有释放页面或面板。");
                SendSubmit(oldButton);
                yield return null;
                Require(router.CurrentScreenId == ScreenIds.Menu && ReferenceEquals(router.CurrentArgs, args),
                    "已释放的旧按钮仍能触发导航。");
                ValidatePage(ScreenIds.Menu);
                Submit("open-team");
                yield return null;
                Require(router.CurrentScreenId == ScreenIds.Team, "恢复后的菜单按钮无法导航。");
                Submit("back-button");
                yield return null;
                Require(router.CurrentScreenId == ScreenIds.Menu && ReferenceEquals(router.CurrentArgs, args),
                    "恢复后的返回按钮丢失菜单参数。");
                report.steps.Add((mode == 1 ? "整个 GameObject" : mode == 2 ? "UIDocument.enabled" : "ScreenRouter.enabled")
                    + " 停用/恢复：新根可用、旧页分离、旧按钮不导航、新按钮可导航且参数保留。");
            }

            LifecycleProbe probe = null;
            router.Register(LifecycleProbe.ScreenId, () => probe = new LifecycleProbe());
            router.Show(LifecycleProbe.ScreenId);
            yield return null;
            yield return null;
            Require(probe != null && probe.Ticks > 0 && probe.DuplicateTicks == 0, "路由未按帧 Tick，或同一帧重复 Tick。");
            router.Show(ScreenIds.Menu, args);
            int ticks = probe.Ticks;
            yield return null;
            yield return null;
            Require(probe.Disposals == 1 && probe.Ticks == ticks, "已离开的界面没有只释放一次，或仍继续 Tick。");
            report.steps.Add("IScreen 生命周期探针：逐帧 Tick、离开只 Dispose 一次、离开后停止 Tick。");
        }

        void Submit(string name)
        {
            Button button = document.rootVisualElement.Q<Button>(name);
            Require(button != null && button.enabledInHierarchy && button.visible, "找不到可用导航按钮：" + name);
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

        void ValidateStage()
        {
            var stage = document.rootVisualElement.Q<BattleStage>();
            Require(stage != null, "对战占位页缺少 BattleStage。");
            foreach (string name in new[] { StageNames.Fighter0, StageNames.Fighter1 })
            {
                var fighter = stage.Q<VisualElement>(name);
                Require(fighter != null && fighter.Q<Image>(StageNames.Portrait) != null
                    && fighter.Q<ProgressBar>(StageNames.HpBar) != null
                    && fighter.Q<VisualElement>(StageNames.StatusRow) != null, "对战舞台缺少上场位子节点：" + name);
            }
            Require(stage.Q<VisualElement>(StageNames.FxLayer) != null
                && stage.Q<VisualElement>(StageNames.FloatLayer) != null
                && stage.Q<Label>(StageNames.FieldBanner) != null, "对战舞台缺少表现层节点。");
            new NullFx().Play("smoke-fx", stage.Fighter0, stage.Fighter1, 0);
        }

        IEnumerator CheckTeamScrollEnds()
        {
            var page = document.rootVisualElement.Q<VisualElement>(ScreenIds.Team + "-screen");
            var roster = page?.Q<ScrollView>("team-roster");
            var detail = page?.Q<ScrollView>("team-detail");
            var grid = page?.Q<VisualElement>("team-grid");
            Require(page != null && roster != null && detail != null && grid != null && grid.childCount > 0,
                "正式组队页缺少主播网格或详情滚动区，不能回退成占位页。");
            var scrolls = new List<ScrollView>();
            page.Query<ScrollView>().ForEach(scroll => scrolls.Add(scroll));
            var offsets = new List<Vector2>();
            foreach (var scroll in scrolls) offsets.Add(scroll.scrollOffset);
            float rosterBefore = roster.scrollOffset.y;
            Require(Finite(roster.verticalScroller.highValue) && roster.verticalScroller.highValue > rosterBefore + 1,
                "正式组队名单必须有尚未验收的后排滚动内容。");
            try
            {
                foreach (var scroll in scrolls)
                {
                    float end = scroll.verticalScroller.highValue;
                    Require(Finite(end), "滚动范围无效：" + scroll.name);
                    // 内容短于视口时 Unity 的 highValue 可以为负，此时应保持在起点。
                    scroll.scrollOffset = new Vector2(0, Mathf.Max(0, end));
                }
                yield return null;
                yield return null;
                yield return new WaitForEndOfFrame();
                Require(roster.scrollOffset.y > rosterBefore + 1
                    && Mathf.Abs(roster.scrollOffset.y - roster.verticalScroller.highValue) <= 2,
                    "主播网格没有真正滚到名单末尾。");
                var lastPick = grid[grid.childCount - 1].Q<Button>(className: "sd-team__pick");
                Require(lastPick != null && lastPick.visible && lastPick.resolvedStyle.display != DisplayStyle.None,
                    "最后一位主播没有可见的选入按钮。");
                bool scrolled;
                Rect clip = VisibleClip(lastPick, document.rootVisualElement.worldBound, out scrolled);
                Require(scrolled && lastPick.worldBound.width > 0 && lastPick.worldBound.height > 0
                    && Contains(clip, lastPick.worldBound), "名单末位的选入按钮没有完整进入滚动视口。");
                ValidatePage(ScreenIds.Team);
                Capture(ScreenIds.Team + "-scrolled");
                report.steps.Add(RequestedWidth + "×" + RequestedHeight
                    + "：正式组队页所有滚动区滚到末尾；末位主播按钮实际可见，末尾文字、头像、边界与交叠重新验收。");
            }
            finally
            {
                for (int i = 0; i < scrolls.Count; i++) scrolls[i].scrollOffset = offsets[i];
            }
            yield return null;
            yield return new WaitForEndOfFrame();
            ValidatePage(ScreenIds.Team);
        }

        void ValidatePage(string screenId)
        {
            Require(document.rootVisualElement.resolvedStyle.display != DisplayStyle.None, "UI Toolkit 根节点不可见。");
            var page = document.rootVisualElement.Q<VisualElement>(screenId + "-screen");
            Require(page != null, "界面根节点缺失：" + screenId);
            var elements = new List<VisualElement>();
            CollectVisible(page, elements);
            Rect rootBounds = document.rootVisualElement.worldBound;
            Require(elements.Count > 0, "界面没有可见文字或控件：" + screenId);
            var visible = new List<VisualElement>();
            var clipped = new List<Rect>();
            foreach (var element in elements)
            {
                Rect rect = element.worldBound;
                Require(Finite(rect.x) && Finite(rect.y) && Finite(rect.width) && Finite(rect.height)
                    && rect.width > 0 && rect.height > 0, "控件布局无效：" + DescribeHierarchy(element));
                bool scrolled;
                Rect clip = VisibleClip(element, rootBounds, out scrolled);
                Rect seen = Intersection(rect, clip);
                // 纵向滚动区的屏外前后排不是当前画面；非滚动元素仍必须完整位于屏幕。
                if (scrolled && seen.height <= 0) continue;
                if (scrolled)
                    Require(clip.width > 0 && rect.xMin >= clip.xMin - 2 && rect.xMax <= clip.xMax + 2,
                        "控件被滚动视口横向裁切：" + Describe(element));
                else Require(Contains(rootBounds, rect), "控件超出屏幕：" + Describe(element));
                var text = element as TextElement;
                if (text != null && !string.IsNullOrEmpty(text.text))
                {
                    Rect content = text.contentRect;
                    Vector2 measured = text.MeasureTextSize(text.text, content.width,
                        text.resolvedStyle.whiteSpace == WhiteSpace.Normal ? VisualElement.MeasureMode.Exactly : VisualElement.MeasureMode.Undefined,
                        0, VisualElement.MeasureMode.Undefined);
                    Require(!text.isElided && measured.x <= content.width + 2 && measured.y <= content.height + 2,
                        "文字可能被裁切：" + Describe(element) + " text=" + text.text
                        + " measured=" + measured + " content=" + content);
                }
                report.bounds.Add(new ElementBounds
                {
                    screen = screenId, element = Describe(element), text = text?.text,
                    width = RequestedWidth, height = RequestedHeight,
                    x = rect.x, y = rect.y, w = rect.width, h = rect.height
                });
                visible.Add(element);
                clipped.Add(seen);
            }
            Require(visible.Count > 0, "界面没有进入可见视口的文字或控件：" + screenId);
            for (int i = 0; i < visible.Count; i++)
                for (int j = i + 1; j < visible.Count; j++)
                {
                    if (visible[i].Contains(visible[j]) || visible[j].Contains(visible[i])) continue;
                    Rect a = clipped[i], b = clipped[j];
                    float width = Mathf.Min(a.xMax, b.xMax) - Mathf.Max(a.xMin, b.xMin);
                    float height = Mathf.Min(a.yMax, b.yMax) - Mathf.Max(a.yMin, b.yMin);
                    Require(width <= 1 || height <= 1, "控件交叠：" + Describe(visible[i]) + " / " + Describe(visible[j]));
                }
        }

        static Rect VisibleClip(VisualElement element, Rect rootBounds, out bool scrolled)
        {
            Rect clip = rootBounds;
            scrolled = false;
            for (var ancestor = element.hierarchy.parent; ancestor != null; ancestor = ancestor.hierarchy.parent)
                if (ancestor is ScrollView scroll)
                {
                    clip = Intersection(clip, scroll.contentViewport.worldBound);
                    scrolled = true;
                }
            return clip;
        }

        static Rect Intersection(Rect first, Rect second)
        {
            return Rect.MinMaxRect(Mathf.Max(first.xMin, second.xMin), Mathf.Max(first.yMin, second.yMin),
                Mathf.Min(first.xMax, second.xMax), Mathf.Min(first.yMax, second.yMax));
        }

        static void CollectVisible(VisualElement element, List<VisualElement> output)
        {
            // Foldout 的逻辑父链会越过内部内容容器，物理父链才能识别折叠隐藏。
            for (var ancestor = element; ancestor != null; ancestor = ancestor.hierarchy.parent)
                if (!ancestor.visible || ancestor.resolvedStyle.display == DisplayStyle.None) return;
            if ((element is TextElement text && !string.IsNullOrEmpty(text.text)) || element is Image || element is ProgressBar)
                output.Add(element);
            foreach (VisualElement child in element.Children()) CollectVisible(child, output);
        }

        static string Describe(VisualElement element)
        {
            return element.GetType().Name + "#" + element.name;
        }

        static string DescribeHierarchy(VisualElement element)
        {
            var rows = new List<string>();
            for (var ancestor = element; ancestor != null; ancestor = ancestor.hierarchy.parent)
            {
                rows.Add(Describe(ancestor) + " classes=[" + string.Join(",", ancestor.GetClasses()) + "]"
                    + " display=" + ancestor.resolvedStyle.display
                    + " inlineDisplay=" + ancestor.style.display.value + "/" + ancestor.style.display.keyword
                    + " visible=" + ancestor.visible + " visibility=" + ancestor.resolvedStyle.visibility
                    + " rect=" + ancestor.worldBound.ToString("F3") + " layout=" + ancestor.layout.ToString("F3")
                    + (ancestor is Foldout foldout ? " foldoutValue=" + foldout.value : ""));
            }
            return string.Join("\n  parent: ", rows);
        }

        static bool Contains(Rect outer, Rect inner)
        {
            return inner.xMin >= outer.xMin - 2 && inner.yMin >= outer.yMin - 2
                && inner.xMax <= outer.xMax + 2 && inner.yMax <= outer.yMax + 2;
        }

        static bool Finite(float value) { return !float.IsNaN(value) && !float.IsInfinity(value); }

        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        void Capture(string screen)
        {
            Texture2D image = null;
            try
            {
                image = ScreenCapture.CaptureScreenshotAsTexture();
                Require(image != null && image.width == RequestedWidth && image.height == RequestedHeight, "截图分辨率不正确。");
                byte[] png = image.EncodeToPNG();
                Require(png != null && png.Length > 8, "截图编码失败。");
                string name = RequestedWidth + "x" + RequestedHeight + "-" + screen + ".png";
                string path = Path.Combine(report.outputDirectory, name);
                File.WriteAllBytes(path, png);
                Require(new FileInfo(path).Length == png.Length, "截图写入不完整。");
                report.screenshots.Add(name);
                ValidateRenderedPixels(image, name);
            }
            finally { if (image != null) Destroy(image); }
        }

        static void ValidateRenderedPixels(Texture2D image, string name)
        {
            // 隐藏窗口可能返回尺寸正确的黑图；先保留截图，再检查它确实含有亮暗内容。
            Color32[] pixels = image.GetPixels32();
            int stride = Math.Max(1, pixels.Length / 65536);
            int sampled = 0, bright = 0, darkest = 255, lightest = 0;
            for (int i = 0; i < pixels.Length; i += stride)
            {
                Color32 pixel = pixels[i];
                int luminance = (299 * pixel.r + 587 * pixel.g + 114 * pixel.b) / 1000;
                darkest = Math.Min(darkest, luminance);
                lightest = Math.Max(lightest, luminance);
                if (luminance >= 150) bright++;
                sampled++;
            }
            Require(sampled > 0 && lightest - darkest >= 48 && bright >= Math.Max(8, sampled / 2000),
                "截图缺少有效渲染内容（全黑、近乎单色或没有足够亮文字）：" + name
                + "，亮度范围=" + darkest + ".." + lightest + "，亮像素=" + bright + "/" + sampled);
        }

        void Finish(string failure)
        {
            if (!running) return;
            running = false;
            try
            {
                portraits?.Dispose();
                portraits = null;
                report.failure = failure;
                if (failure != null) report.completed = false;
                ExitCode = report.completed && report.warnings == 0 && report.errors == 0 ? 0 : 1;
                if (ReportPath != null) File.WriteAllText(ReportPath, JsonUtility.ToJson(report, true));
                else ExitCode = 1;
            }
            catch (Exception ex)
            {
                ExitCode = 1;
                Debug.LogError("UI 验收报告写入失败：" + ex);
            }
            finally
            {
                Application.logMessageReceived -= CollectLog;
                Completed = true;
                if (ExitCode == 0) Debug.Log("UI_SMOKE_PASS " + ReportPath);
                else Debug.LogError("UI_SMOKE_FAIL " + (failure ?? "检测到警告或错误，请查看报告及完整 Player 日志。") + " " + ReportPath);
                if (!Application.isEditor) Application.Quit(ExitCode);
            }
        }
    }
}
