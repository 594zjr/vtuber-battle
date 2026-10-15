using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using StreamDuel.Core;
using UnityEngine;
using UnityEngine.UIElements;

namespace StreamDuel.Game
{
    public sealed class T71Smoke : MonoBehaviour
    {
        [Serializable]
        sealed class Report
        {
            public string unityVersion, outputDirectory, failure;
            public string inputScope = "通过 NavigationSubmitEvent 和控件值变化调用现有 UI 回调，不代表人工鼠标验收。";
            public string networkScope = "同一进程内通过真实本机 TCP 套接字连接两个 NetSession，不代表第二台电脑或局域网发现验收。";
            public string displayScope = "Editor GameView 真实渲染两种尺寸；窗口/全屏设置的持久化不代表独立 Player 的显示模式人工验收。";
            public string logScope = "BeforeSceneLoad 后收集运行时日志；完整启动日志及停止 Play 后 Console 由 Editor bridge 另行记录。";
            public bool completed, settingsReloaded;
            public int warnings, errors;
            public List<string> checks = new List<string>();
            public List<string> screenshots = new List<string>();
            public List<string> logs = new List<string>();
            public List<MatchResult> matches = new List<MatchResult>();
        }

        [Serializable]
        sealed class MatchResult
        {
            public int width, height, localSide, turn, winner;
            public string localHash, peerHash;
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
        FileSaveStore store;
        TcpHost peerHost;
        TcpTransport peerTransport;
        Task<TcpTransport> pendingConnect;
        NetSession peerSession;
        bool peerSendingCompleted;
        static readonly FieldInfo CurrentScreen = typeof(ScreenRouter).GetField("current", BindingFlags.Instance | BindingFlags.NonPublic);
        VisualElement Root => document.rootVisualElement;
        OnlineScreen Online => CurrentScreen?.GetValue(router) as OnlineScreen;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Install()
        {
            Completed = false;
            ExitCode = 1;
            ReportPath = null;
            RequestedWidth = RequestedHeight = 0;
            string[] args = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(args, "--t71-smoke-test");
            if (index < 0) return;
            var host = new GameObject("StreamDuel.T71Smoke");
            DontDestroyOnLoad(host);
            host.AddComponent<T71Smoke>().Configure(args, index);
        }

        void Configure(string[] args, int index)
        {
            report = new Report { unityVersion = Application.unityVersion };
            running = true;
            deadline = Time.realtimeSinceStartup + 420;
            Application.logMessageReceived += CollectLog;
            try
            {
                Require(index + 1 < args.Length && Path.IsPathFullyQualified(args[index + 1]),
                    "--t71-smoke-test 后必须提供工程内的绝对输出目录。");
                string project = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
                string output = Path.GetFullPath(args[index + 1]);
                Require(output.StartsWith(project.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase), "验收输出目录必须位于本工作树的 UnityProject 内。");
                RejectLinks(project, output);
                report.outputDirectory = Path.Combine(output, "run-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(report.outputDirectory);
                ReportPath = Path.Combine(report.outputDirectory, "result.json");
                store = new FileSaveStore(Path.Combine(report.outputDirectory, "save"));
                GameRuntimeSettings.UseStore(store);
                Debug.Log("T71_SMOKE_OUTPUT " + report.outputDirectory);
            }
            catch (Exception ex) { Finish(ex.ToString()); }
        }

        static void RejectLinks(string project, string output)
        {
            for (var directory = new DirectoryInfo(output); directory != null; directory = directory.Parent)
            {
                if (directory.Exists)
                    Require((directory.Attributes & FileAttributes.ReparsePoint) == 0, "验收目录不能经过重解析点。");
                if (string.Equals(directory.FullName.TrimEnd(Path.DirectorySeparatorChar),
                    project.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) return;
            }
            throw new InvalidOperationException("验收目录没有位于当前工程内。");
        }

        void Start() { if (running) StartCoroutine(Guard(Run())); }

        void LateUpdate()
        {
            if (running && Time.realtimeSinceStartup >= deadline)
                Finish("T71 验收超过 420 秒；必须使用正常图形渲染。");
        }

        void CollectLog(string message, string stack, LogType type)
        {
            if (type == LogType.Warning) report.warnings++;
            if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert) report.errors++;
            if (type != LogType.Log) report.logs.Add(type + ": " + message + "\n" + stack);
        }

        IEnumerator Guard(IEnumerator routine)
        {
            var stack = new Stack<IEnumerator>();
            stack.Push(routine);
            while (running)
            {
                bool more = false;
                object wait = null;
                string failure = null;
                try
                {
                    more = stack.Peek().MoveNext();
                    if (more) wait = stack.Peek().Current;
                    else stack.Pop();
                }
                catch (Exception ex) { failure = ex.ToString(); }
                if (failure != null || stack.Count == 0)
                {
                    Finish(failure);
                    yield break;
                }
                if (!more) continue;
                if (wait is IEnumerator nested) { stack.Push(nested); continue; }
                yield return wait;
            }
        }

        IEnumerator Run()
        {
            yield return null;
            var routers = FindObjectsByType<ScreenRouter>(FindObjectsInactive.Include);
            Require(routers.Length == 1, "必须只有一个界面路由。");
            router = routers[0];
            document = router.Document;
            Require(document != null && Root.panel != null, "界面没有连接运行时面板。");
            for (int resolution = 0; resolution < 2; resolution++)
            {
                RequestedWidth = resolution == 0 ? 1280 : 1920;
                RequestedHeight = resolution == 0 ? 720 : 1080;
                Screen.SetResolution(RequestedWidth, RequestedHeight, FullScreenMode.Windowed);
                yield return Until(() => Screen.width == RequestedWidth && Screen.height == RequestedHeight,
                    15, "GameView 未切到目标分辨率。");
                router.Show(ScreenIds.Menu);
                yield return null;
                Submit("open-settings");
                yield return SetAndCheckSettings();
                yield return PageScreenshots("settings", "settings-scroll");
                Submit("back-button");
                yield return null;
                Require(router.CurrentScreenId == ScreenIds.Menu, "设置返回没有回到菜单。");
                yield return OpenOnline();
                yield return PageScreenshots("online", "online-content");
                yield return ConnectPeer(resolution == 0, null, false);
                yield return PlayMatch();
                yield return Screenshot("online-result");
                yield return ReturnToMenu();

                yield return OpenOnline();
                yield return ConnectPeer(false, null, false);
                DisposePeer();
                yield return ExpectError("断开", "disconnect");
                yield return ReturnToMenu();
                yield return Screenshot("error-return");
                if (resolution == 0)
                {
                    yield return CheckCancel();
                    yield return OpenOnline();
                    yield return ConnectPeer(false, null, true);
                    yield return ExpectError("版本", "version-error");
                    yield return ReturnToMenu();

                    yield return OpenOnline();
                    ContentDb changed = OriginalContent();
                    changed.Streamers[0].Stats["hp"]++;
                    Require(changed.ContentHash() != Online.SelectedContent.ContentHash(), "内容差异样本指纹没有变化。");
                    yield return ConnectPeer(false, changed, false);
                    yield return ExpectError("内容不一致", "content-error");
                    yield return ReturnToMenu();
                }
            }
            report.completed = true;
        }

        IEnumerator SetAndCheckSettings()
        {
            yield return null;
            Require(router.CurrentScreenId == ScreenIds.Settings, "设置按钮未导航。");
            for (int animation = 0; animation < 4; animation++)
            {
                Control<DropdownField>("settings-animation").index = animation;
                Submit("settings-save");
                yield return null;
                var savedAnimation = store.LoadSettings();
                Require(savedAnimation.Animations == (animation != 3)
                    && (animation == 3 || savedAnimation.AnimationSpeed == (animation == 0 ? 0.5 : animation == 1 ? 1 : 2)),
                    "动画速度选项未正确保存：" + animation);
            }
            bool fullscreen = RequestedWidth == 1920;
            Control<DropdownField>("settings-animation").index = 2;
            Control<Toggle>("settings-timer").value = true;
            Control<IntegerField>("settings-turn-seconds").value = 23;
            Control<Toggle>("settings-fullscreen").value = fullscreen;
            Choose(Control<DropdownField>("settings-resolution"), text => text == RequestedWidth + " × " + RequestedHeight);
            Choose(Control<DropdownField>("settings-ai"), text => text == "困难");
            Choose(Control<DropdownField>("settings-pack"), text => text.Contains("[original]"));
            Submit("settings-save");
            yield return null;
            var loaded = store.LoadSettings();
            Require(loaded.AnimationSpeed == 2 && loaded.Animations && loaded.TurnTimer && loaded.TurnSeconds == 23
                && loaded.Fullscreen == fullscreen && loaded.Width == RequestedWidth && loaded.Height == RequestedHeight
                && loaded.AiLevel == AiLevel.Hard && loaded.DefaultPack == "original", "设置保存后重读不一致。");
            Submit("back-button");
            yield return null;
            Submit("open-settings");
            yield return null;
            Require(Control<DropdownField>("settings-animation").index == 2
                && Control<Toggle>("settings-timer").value
                && Control<IntegerField>("settings-turn-seconds").value == 23
                && Control<Toggle>("settings-fullscreen").value == fullscreen
                && Control<DropdownField>("settings-resolution").value == RequestedWidth + " × " + RequestedHeight
                && Control<DropdownField>("settings-ai").value == "困难"
                && Control<DropdownField>("settings-pack").value.Contains("[original]"), "重开设置页没有恢复保存值。");
            report.settingsReloaded = true;
            report.checks.Add(Size + "：通过控件保存设置，项目内 FileSaveStore 重读和重新打开页面一致。");
        }

        IEnumerator OpenOnline()
        {
            Require(router.CurrentScreenId == ScreenIds.Menu, "联网用例开始前没有回到菜单。");
            Submit("open-online");
            yield return null;
            Require(Online != null, "联网按钮未打开 OnlineScreen。");
            Control<TextField>("nickname").value = "本机验收";
            Choose(Control<DropdownField>("pack"), text => text == "原创主播" || text.Contains("[original]"));
            yield return null;
            for (int i = 0; i < 6; i++) Control<DropdownField>("team-slot-" + i).index = i;
            Require(Online.SelectedContent.PackId == "original", "联网页没有选择原创包。");
        }

        IEnumerator ConnectPeer(bool localHost, ContentDb overrideContent, bool wrongVersion)
        {
            DisposePeer();
            var screen = Online;
            Require(screen != null, "没有联网界面。");
            ContentDb peerDb = overrideContent ?? screen.SelectedContent;
            if (localHost)
            {
                Control<IntegerField>("host-port").value = 0;
                Submit("host-button");
                yield return Until(() => screen.ListeningPort > 0, 10, "开房没有取得实际监听端口。");
                int port = screen.ListeningPort;
                pendingConnect = Task.Run(() => TcpTransport.Connect("127.0.0.1", port, 5000));
                yield return Until(() => pendingConnect.IsCompleted, 10, "对端连接本机开房超时。");
                peerTransport = pendingConnect.GetAwaiter().GetResult();
                pendingConnect = null;
            }
            else
            {
                peerHost = TcpTransport.Listen(0, out int port);
                Control<TextField>("address").value = "127.0.0.1";
                Control<IntegerField>("join-port").value = port;
                Submit("join-button");
                float until = Time.realtimeSinceStartup + 10;
                while (peerTransport == null)
                {
                    Require(Time.realtimeSinceStartup < until, "界面加入本机监听器超时。");
                    peerTransport = peerHost.Accept(0);
                    yield return null;
                }
                peerHost.Dispose();
                peerHost = null;
            }
            if (wrongVersion)
            {
                // 让真实收包路径判定版本错误，不直接修改界面错误状态。
                peerTransport.Send("{\"type\":\"hello\",\"version\":1}");
                yield break;
            }
            var peerTeam = peerDb.Streamers.Skip(6).Take(6).Select(streamer => streamer.Id).ToArray();
            Require(peerTeam.Length == 6, "验收包没有足够的另一队成员。");
            peerSession = new NetSession(peerDb, peerTransport, !localHost, peerTeam, "TCP 对端", localHost ? (uint?)null : 7u);
            if (overrideContent != null)
            {
                yield return null;
                yield break;
            }
            float timeout = Time.realtimeSinceStartup + 15;
            while (screen.Session == null || screen.Session.State != NetState.Choose || peerSession.State != NetState.Choose)
            {
                Require(Time.realtimeSinceStartup < timeout, "真实 TCP 握手超时：" + StatusText());
                peerSession.Poll();
                Require(peerSession.State != NetState.Error && peerSession.State != NetState.Desync,
                    "对端握手失败：" + peerSession.Error);
                yield return null;
            }
            report.checks.Add(Size + "：" + (localHost ? "UI 开房" : "UI 加入") + "真实 127.0.0.1 TCP 握手成功。");
        }

        IEnumerator PlayMatch()
        {
            var screen = Online;
            yield return Screenshot("online-battle");
            float timeout = Time.realtimeSinceStartup + 120;
            while (!screen.IsFinished || peerSession.State != NetState.Over)
            {
                Require(Time.realtimeSinceStartup < timeout, "联网整局超过 120 秒：" + StatusText());
                peerSession.Poll();
                var local = screen.Session;
                Require(local != null && local.State != NetState.Error && local.State != NetState.Desync,
                    "本方会话失败：" + (local?.Error ?? StatusText()));
                Require(peerSession.State != NetState.Error && peerSession.State != NetState.Desync,
                    "对端会话失败：" + peerSession.Error);
                if (peerSession.State == NetState.Choose && !peerSession.HasSubmitted)
                    peerSession.Submit(Ai.Choose(peerSession.Battle.Clone(), peerSession.MySide, AiLevel.Normal));
                else if (peerSession.State == NetState.Replace && !peerSession.HasSubmitted
                    && peerSession.Battle.Need[peerSession.MySide])
                    peerSession.SubmitReplace(Ai.ChooseReplacement(peerSession.Battle.Clone(), peerSession.MySide, AiLevel.Normal));
                if (local.State == NetState.Choose && !local.HasSubmitted)
                {
                    BattleAction action = Ai.Choose(local.Battle.Clone(), local.MySide, AiLevel.Normal);
                    Submit(action.Type == ActionType.Skill ? "online-skill-" + action.Index
                        : action.Type == ActionType.Switch ? "online-switch-" + action.Index : "online-pass");
                }
                else if (local.State == NetState.Replace && !local.HasSubmitted && local.Battle.Need[local.MySide])
                    Submit("online-replace-" + Ai.ChooseReplacement(local.Battle.Clone(), local.MySide, AiLevel.Normal));
                if (peerSession.State == NetState.Over && !peerSendingCompleted)
                {
                    peerTransport.CompleteSending();
                    peerSendingCompleted = true;
                }
                yield return null;
            }
            var mine = screen.Session;
            string localHash = mine.Battle.StateHash().ToString("x16");
            string peerHash = peerSession.Battle.StateHash().ToString("x16");
            Require(mine.State == NetState.Over && localHash == peerHash && mine.Battle.Winner == peerSession.Battle.Winner,
                "双方终局哈希或胜者不一致。");
            report.matches.Add(new MatchResult
            {
                width = RequestedWidth, height = RequestedHeight, localSide = mine.MySide,
                turn = mine.Battle.Turn, winner = mine.Battle.Winner, localHash = localHash, peerHash = peerHash
            });
            File.WriteAllText(Path.Combine(report.outputDirectory, Size + "-record.json"), mine.Battle.Record.ToJson());
            report.checks.Add(Size + "：真实 TCP 整局完成，双方终局哈希一致，回合=" + mine.Battle.Turn + "。");
        }

        IEnumerator CheckCancel()
        {
            yield return OpenOnline();
            var screen = Online;
            Control<IntegerField>("host-port").value = 0;
            Submit("host-button");
            yield return Until(() => screen.ListeningPort > 0, 10, "取消用例没有开始监听。");
            int port = screen.ListeningPort;
            Submit("cancel-button");
            yield return Until(() => !screen.IsConnecting && !screen.IsClosing && screen.Session == null,
                10, "取消监听后仍在连接或关闭中。");
            using (var check = TcpTransport.Listen(port, out int actual))
                Require(actual == port, "取消后不能重新使用监听端口。");
            report.checks.Add("取消开房后端口已释放，可返回菜单。");
            yield return Screenshot("cancelled");
            yield return ReturnToMenu();
        }

        IEnumerator ExpectError(string expected, string imageName)
        {
            float timeout = Time.realtimeSinceStartup + 12;
            while (!StatusText().Contains(expected) || Online.IsConnecting || Online.IsClosing)
            {
                Require(Time.realtimeSinceStartup < timeout, "没有及时出现中文错误「" + expected + "」：" + StatusText());
                peerSession?.Poll();
                yield return null;
            }
            Require(Online.Session == null || Online.Session.State == NetState.Error || Online.Session.State == NetState.Desync,
                "错误提示后仍保留可继续的会话。");
            yield return Screenshot(imageName);
            report.checks.Add(Size + "：真实 TCP 错误「" + expected + "」显示中文提示且可以返回。");
        }

        IEnumerator ReturnToMenu()
        {
            Submit("back-button");
            yield return Until(() => router.CurrentScreenId == ScreenIds.Menu, 10, "返回按钮未回到菜单。");
            DisposePeer();
            yield return null;
        }

        static ContentDb OriginalContent()
        {
            return ContentDb.FromJson(Resources.Load<TextAsset>("StreamDuelRules").text,
                Resources.Load<TextAsset>("Packs/original").text);
        }

        T Control<T>(string name) where T : VisualElement
        {
            var control = Root.Q<T>(name);
            Require(control != null, "找不到控件：" + name);
            return control;
        }

        static void Choose(DropdownField field, Func<string, bool> predicate)
        {
            int index = field.choices.FindIndex(text => predicate(text));
            Require(index >= 0, "没有找到下拉选项：" + field.name);
            field.index = index;
        }

        void Submit(string name)
        {
            var button = Control<Button>(name);
            Require(button.enabledInHierarchy && button.visible, "按钮当前不可用：" + name);
            button.Focus();
            using (var action = NavigationSubmitEvent.GetPooled())
            {
                action.target = button;
                button.SendEvent(action);
            }
        }

        string StatusText() { return Root.Q<Label>("online-status")?.text ?? string.Empty; }
        string Size => RequestedWidth + "x" + RequestedHeight;

        IEnumerator Until(Func<bool> condition, float seconds, string failure)
        {
            float end = Time.realtimeSinceStartup + seconds;
            while (!condition())
            {
                Require(Time.realtimeSinceStartup < end, failure);
                yield return null;
            }
        }

        IEnumerator Screenshot(string name)
        {
            yield return null;
            yield return new WaitForEndOfFrame();
            Texture2D image = null;
            try
            {
                image = ScreenCapture.CaptureScreenshotAsTexture();
                Require(image != null && image.width == RequestedWidth && image.height == RequestedHeight, "截图尺寸不正确。");
                var pixels = image.GetPixels32();
                int darkest = 255, lightest = 0, bright = 0;
                int stride = Math.Max(1, pixels.Length / 65536);
                for (int i = 0; i < pixels.Length; i += stride)
                {
                    var pixel = pixels[i];
                    int value = (299 * pixel.r + 587 * pixel.g + 114 * pixel.b) / 1000;
                    darkest = Math.Min(darkest, value);
                    lightest = Math.Max(lightest, value);
                    if (value >= 150) bright++;
                }
                byte[] png = image.EncodeToPNG();
                string file = Size + "-" + name + ".png";
                File.WriteAllBytes(Path.Combine(report.outputDirectory, file), png);
                report.screenshots.Add(file);
                Require(lightest - darkest >= 48 && bright >= 8, "截图接近全黑或没有有效文字：" + file);
                ValidateVisibleText();
            }
            finally { if (image != null) Destroy(image); }
        }

        IEnumerator PageScreenshots(string name, string scrollName)
        {
            var scroll = Root.Q<ScrollView>(scrollName);
            if (scroll != null) scroll.scrollOffset = Vector2.zero;
            yield return Screenshot(name);
            if (scroll != null && scroll.verticalScroller.highValue > 1)
            {
                scroll.scrollOffset = new Vector2(0, scroll.verticalScroller.highValue);
                yield return Screenshot(name + "-bottom");
                scroll.scrollOffset = Vector2.zero;
                yield return null;
            }
        }

        void ValidateVisibleText()
        {
            Root.Query<TextElement>().ForEach(text =>
            {
                if (string.IsNullOrEmpty(text.text) || !text.visible || text.panel == null
                    || text.resolvedStyle.display == DisplayStyle.None) return;
                Rect visible = Root.worldBound;
                for (var parent = text.parent; parent != null; parent = parent.parent)
                {
                    if (parent.resolvedStyle.display == DisplayStyle.None) return;
                    if (parent is ScrollView scroll) visible = Intersection(visible, scroll.contentViewport.worldBound);
                }
                Rect bounds = text.worldBound;
                if (!visible.Overlaps(bounds)) return;
                Require(Finite(bounds.x) && Finite(bounds.y) && Finite(bounds.width) && Finite(bounds.height), "文字布局包含非有限数值。");
                // 滚动区域允许边缘裁切，完整可见的文字才检查文本测量。
                if (bounds.xMin >= visible.xMin - 2 && bounds.xMax <= visible.xMax + 2
                    && bounds.yMin >= visible.yMin - 2 && bounds.yMax <= visible.yMax + 2)
                {
                    Vector2 measured = text.MeasureTextSize(text.text, text.contentRect.width,
                        text.resolvedStyle.whiteSpace == WhiteSpace.Normal ? VisualElement.MeasureMode.Exactly : VisualElement.MeasureMode.Undefined,
                        0, VisualElement.MeasureMode.Undefined);
                    Require(!text.isElided && measured.x <= text.contentRect.width + 3 && measured.y <= text.contentRect.height + 3,
                        "可见文字可能被截断：" + text.name + " / " + text.text);
                }
            });
        }

        static Rect Intersection(Rect a, Rect b)
        {
            float left = Mathf.Max(a.xMin, b.xMin), top = Mathf.Max(a.yMin, b.yMin);
            return new Rect(left, top, Mathf.Max(0, Mathf.Min(a.xMax, b.xMax) - left), Mathf.Max(0, Mathf.Min(a.yMax, b.yMax) - top));
        }

        static bool Finite(float value) { return !float.IsNaN(value) && !float.IsInfinity(value); }
        static void Require(bool condition, string failure) { if (!condition) throw new InvalidOperationException(failure); }

        void DisposePeer()
        {
            peerSession = null;
            peerSendingCompleted = false;
            peerTransport?.Dispose();
            peerTransport = null;
            peerHost?.Dispose();
            peerHost = null;
            if (pendingConnect != null)
            {
                // 超时退出后也领取并关闭迟到的连接，不能把后台任务遗留在编辑器里。
                pendingConnect.ContinueWith(task =>
                {
                    if (task.Status == TaskStatus.RanToCompletion) task.Result.Dispose();
                    else if (task.IsFaulted) { var ignored = task.Exception; }
                }, TaskScheduler.Default);
                pendingConnect = null;
            }
        }

        void Finish(string failure)
        {
            if (!running) return;
            running = false;
            try
            {
                DisposePeer();
                if (failure != null) { report.failure = failure; report.completed = false; }
                ExitCode = report.completed && report.errors == 0 && report.warnings == 0 ? 0 : 1;
                if (ReportPath != null) File.WriteAllText(ReportPath, JsonUtility.ToJson(report, true));
                else ExitCode = 1;
            }
            catch (Exception ex) { ExitCode = 1; Debug.LogError("T71 验收报告写入失败：" + ex); }
            finally
            {
                Application.logMessageReceived -= CollectLog;
                Completed = true;
                if (ExitCode == 0) Debug.Log("T71_SMOKE_PASS " + ReportPath);
                else Debug.LogError("T71_SMOKE_FAIL " + failure + " " + ReportPath);
                if (!Application.isEditor) Application.Quit(ExitCode);
            }
        }

        void OnDestroy()
        {
            Application.logMessageReceived -= CollectLog;
            DisposePeer();
        }
    }
}
