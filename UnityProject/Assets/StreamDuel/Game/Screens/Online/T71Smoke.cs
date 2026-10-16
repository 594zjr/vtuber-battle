using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
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
            public string networkScope = "同一进程内使用真实 TCP 双会话与 UDP loopback 房间公告，覆盖列表发现后加入及开房广播状态；不代表第二台设备或跨设备广播可达性验收。";
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
        double peerPollTime;
        string networkSaveDirectory;
        string profileBeforeConnection;
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
                Require(index + 1 < args.Length && args.Count(value => value == "--t71-smoke-test") == 1
                    && Path.IsPathFullyQualified(args[index + 1]),
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
                store.SaveSettings(new GameSettings { ColorblindMode = true });
                GameRuntimeSettings.UseStore(store);
                Require(GameRuntimeSettings.Current.ColorblindMode, "加载设置丢失已有色弱模式。");
                networkSaveDirectory = Path.Combine(report.outputDirectory, "network-save");
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
                yield return CheckTeamDefaults();
                yield return OpenOnline();
                yield return PageScreenshots("online", "online-content");
                yield return ConnectPeer(resolution == 0, null, false, resolution == 1);
                yield return CheckQuickChat();
                yield return PlayMatch();
                yield return PageScreenshots("online-result", "online-content");
                yield return ReturnToMenu();

                yield return OpenOnline();
                yield return ConnectPeer(false, null, false);
                DisposePeer();
                yield return Until(() => Online.Session != null && Online.Session.IsDisconnected, 10,
                    "真实 TCP 断线没有进入重连等待。");
                yield return Until(() => StatusText().Contains("断开") || StatusText().Contains("重连"), 5,
                    "断线等待缺少中文状态。");
                yield return Screenshot("reconnect-wait");
                // 真实套接字断线后，仅推进公开会话时钟，避免验收固定空等一分钟。
                Online.Session.Poll(NetSession.ReconnectTimeoutSeconds);
                yield return ExpectError("断开", "disconnect");
                yield return ReturnToMenu();
                yield return Screenshot("error-return");
                if (resolution == 0)
                {
                    yield return CheckSettingsStorageFailures();
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
            string beforeInvalid = store.LoadSettings().ToJson();
            Control<Toggle>("settings-timer").value = true;
            Control<IntegerField>("settings-turn-seconds").value = 301;
            Submit("settings-save");
            yield return null;
            Require(store.LoadSettings().ToJson() == beforeInvalid
                && Control<Label>("settings-notice").text.Contains("1—300"), "非法秒数写入存档或没有中文提示。");
            Control<DropdownField>("settings-animation").index = 2;
            Control<Toggle>("settings-timer").value = true;
            Control<IntegerField>("settings-turn-seconds").value = 23;
            Control<Toggle>("settings-fullscreen").value = fullscreen;
            Control<Toggle>("settings-colorblind").value = true;
            Choose(Control<DropdownField>("settings-resolution"), text => text == RequestedWidth + " × " + RequestedHeight);
            Choose(Control<DropdownField>("settings-ai"), text => text == "困难");
            Choose(Control<DropdownField>("settings-pack"), text => text.Contains("[original_wave3]"));
            Submit("settings-save");
            yield return null;
            var loaded = store.LoadSettings();
            Require(loaded.AnimationSpeed == 2 && loaded.Animations && loaded.TurnTimer && loaded.TurnSeconds == 23
                && loaded.Fullscreen == fullscreen && loaded.Width == RequestedWidth && loaded.Height == RequestedHeight
                && loaded.AiLevel == AiLevel.Hard && loaded.DefaultPack == "original_wave3" && loaded.ColorblindMode,
                "设置保存后重读不一致或丢失色弱模式。");
            Control<Toggle>("settings-colorblind").value = false;
            Control<IntegerField>("settings-turn-seconds").value = 99;
            Submit("back-button");
            yield return null;
            Submit("open-settings");
            yield return null;
            Require(Control<DropdownField>("settings-animation").index == 2
                && Control<Toggle>("settings-timer").value
                && Control<IntegerField>("settings-turn-seconds").value == 23
                && Control<Toggle>("settings-fullscreen").value == fullscreen
                && Control<Toggle>("settings-colorblind").value
                && Control<DropdownField>("settings-resolution").value == RequestedWidth + " × " + RequestedHeight
                && Control<DropdownField>("settings-ai").value == "困难"
                && Control<DropdownField>("settings-pack").value.Contains("[original_wave3]"),
                "重开设置页没有恢复保存值，或返回错误地保存了草稿。");
            report.settingsReloaded = true;
            report.checks.Add(Size + "：通过控件保存设置，项目内 FileSaveStore 重读和重新打开页面一致。");
        }

        IEnumerator CheckTeamDefaults()
        {
            Submit("open-team");
            yield return null;
            Require(router.CurrentScreenId == ScreenIds.Team, "设置后没有进入组队页。");
            Require(Control<DropdownField>("team-pack").value.Contains("original_wave3")
                && Control<DropdownField>("team-level").value == "困难", "组队页没有采用新默认包与难度。");
            object teamScreen = CurrentScreen.GetValue(router);
            var field = typeof(TeamScreen).GetField("settings", BindingFlags.Instance | BindingFlags.NonPublic);
            var selected = field?.GetValue(teamScreen) as GameSettings;
            Require(selected != null && selected.TurnTimer && selected.TurnSeconds == 23 && selected.ColorblindMode,
                "组队设置没有继承回合秒数或色弱模式。");
            yield return Screenshot("settings-to-team");
            Submit("back-button");
            yield return null;
            CheckBattleQuickSettings();
            report.checks.Add(Size + "：真实菜单跳转组队页采用 original_wave3 / Hard / 23 秒 / 色弱模式。");
        }

        void CheckBattleQuickSettings()
        {
            var legacy = FindObjectsByType<LocalBattleSession>(FindObjectsInactive.Include).Single();
            var field = typeof(LocalBattleSession).GetField("settings", BindingFlags.Instance | BindingFlags.NonPublic);
            var callback = typeof(LocalBattleSession).GetMethod("SaveBattleQuickSettings", BindingFlags.Instance | BindingFlags.NonPublic);
            Require(field != null && callback != null, "没有找到战中快捷设置真实保存回调。");
            object prior = field.GetValue(legacy);
            var latest = GameSettings.FromJson(GameRuntimeSettings.Current.ToJson());
            var inBattle = new GameSettings { DefaultPack = "original", AiLevel = AiLevel.Easy,
                TurnSeconds = 10, ColorblindMode = false, FastPlay = false, Animations = false, TurnTimer = false };
            string battleSnapshot = inBattle.ToJson();
            try
            {
                // 仅注入旧设置快照并调用真实回调；不把反射调用声明为鼠标操作验收。
                field.SetValue(legacy, inBattle);
                callback.Invoke(legacy, new object[] { true });
                var expected = GameSettings.FromJson(latest.ToJson());
                expected.FastPlay = inBattle.FastPlay;
                expected.AnimationSpeed = inBattle.AnimationSpeed;
                expected.Animations = inBattle.Animations;
                Require(GameRuntimeSettings.Current.ToJson() == expected.ToJson()
                    && store.LoadSettings().ToJson() == expected.ToJson(), "快速开关覆盖了设置页后来保存的其它默认值。");
                callback.Invoke(legacy, new object[] { false });
                expected.TurnTimer = inBattle.TurnTimer;
                Require(GameRuntimeSettings.Current.ToJson() == expected.ToJson()
                    && store.LoadSettings().ToJson() == expected.ToJson() && inBattle.ToJson() == battleSnapshot,
                    "计时开关覆盖了其它默认值或替换了本局快照。");
            }
            finally
            {
                field.SetValue(legacy, prior);
                Require(GameRuntimeSettings.TrySave(latest, out _), "快捷开关回归后无法恢复隔离设置。");
            }
            report.checks.Add(Size + "：旧本局设置快照调用两个真实快捷保存回调，只改对应字段，保留新默认包/难度/秒数/色弱模式。");
        }

        IEnumerator CheckSettingsStorageFailures()
        {
            string disk = store.LoadSettings().ToJson();
            string originalFile = File.ReadAllText(Path.Combine(report.outputDirectory, "save/settings.json"));
            var shared = GameRuntimeSettings.Current;
            GameRuntimeSettings.UseStore(new FailingStore(store, false));
            var changed = GameSettings.FromJson(shared.ToJson());
            changed.TurnSeconds = 29;
            Require(!GameRuntimeSettings.TrySave(changed, out string writeNotice)
                && writeNotice.Contains("暂未保存") && ReferenceEquals(shared, GameRuntimeSettings.Current)
                && shared.TurnSeconds == 29 && shared.ColorblindMode && store.LoadSettings().ToJson() == disk,
                "写失败没有保留内存选择或覆盖了原文件。");
            GameRuntimeSettings.UseStore(new FailingStore(store, true));
            changed.TurnSeconds = 31;
            Require(!GameRuntimeSettings.TrySave(changed, out string readNotice)
                && readNotice.Contains("未覆盖原存档") && shared.TurnSeconds == 31
                && shared.ColorblindMode && store.LoadSettings().ToJson() == disk,
                "读失败后自动覆盖了原文件或丢失内存选择。");
            Require(File.ReadAllText(Path.Combine(report.outputDirectory, "save/settings.json")) == originalFile,
                "设置失败改变了原文件内容。");
            Submit("open-settings");
            yield return null;
            Require(Control<Label>("settings-notice").text.Contains("未覆盖原存档"), "存档失败没有显示在设置页。");
            yield return Screenshot("settings-storage-error");
            Submit("back-button");
            GameRuntimeSettings.UseStore(store);
            Require(ReferenceEquals(shared, GameRuntimeSettings.Current)
                && GameRuntimeSettings.Current.ToJson() == disk, "恢复存档后共享设置对象或数据不一致。");
            report.checks.Add("注入读/写 IOException：内存选择保留、旧文件字节不变、中文提示可见、恢复后共享对象不替换。");
        }

        IEnumerator OpenOnline()
        {
            Require(router.CurrentScreenId == ScreenIds.Menu, "联网用例开始前没有回到菜单。");
            Submit("open-online");
            yield return null;
            Require(Online != null, "联网按钮未打开 OnlineScreen。");
            Online.SaveDirectoryOverride = networkSaveDirectory;
            profileBeforeConnection = FileSaveStore.LoadProfileSnapshot(networkSaveDirectory).ToJson();
            Control<TextField>("nickname").value = "本机验收";
            Choose(Control<DropdownField>("pack"), text => text == "原创主播" || text.Contains("[original]"));
            yield return null;
            for (int i = 0; i < 6; i++) Control<DropdownField>("team-slot-" + i).index = i;
            Require(Online.SelectedContent.PackId == "original", "联网页没有选择原创包。");
        }

        IEnumerator ConnectPeer(bool localHost, ContentDb overrideContent, bool wrongVersion, bool useDiscovery = false)
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
                yield return Until(() => screen.IsBroadcasting, 5, "开房没有启动局域网广播。");
                int port = screen.ListeningPort;
                pendingConnect = Task.Run(() => TcpTransport.Connect("127.0.0.1", port, 5000));
                yield return Until(() => pendingConnect.IsCompleted, 10, "对端连接本机开房超时。");
                peerTransport = pendingConnect.GetAwaiter().GetResult();
                pendingConnect = null;
            }
            else
            {
                peerHost = TcpTransport.Listen(0, out int port);
                if (useDiscovery)
                {
                    Submit("discover-rooms");
                    string buttonName = "lan-room-127.0.0.1-" + port;
                    var room = new LanRoom { Name = "本机发现验收", Port = port,
                        Protocol = NetSession.ReconnectProtocolVersion,
                        ContentHash = peerDb.ContentHash(), Ruleset = "casual" };
                    byte[] beacon = Encoding.UTF8.GetBytes(LanBeacon.Encode(room));
                    float scanTimeout = Time.realtimeSinceStartup + 4;
                    while (Root.Q<Button>(buttonName) == null)
                    {
                        Require(Time.realtimeSinceStartup < scanTimeout, "真实 UDP 公告没有出现在房间列表："
                            + Control<Label>("lan-discovery-status").text);
                        using (var sender = new UdpClient(AddressFamily.InterNetwork))
                            sender.Send(beacon, beacon.Length, new IPEndPoint(IPAddress.Loopback, LanBeacon.Port));
                        yield return new WaitForSecondsRealtime(0.05f);
                    }
                    yield return PageScreenshots("lan-discovery", "online-content");
                    Submit(buttonName);
                    Require(Control<TextField>("address").value == "127.0.0.1"
                        && Control<IntegerField>("join-port").value == port, "选择房间未采用公告端点。");
                    report.checks.Add(Size + "：真实 UDP loopback 公告经界面扫描列出，点击房间行后连接真实 TCP 对端。");
                }
                else
                {
                    Control<TextField>("address").value = "127.0.0.1";
                    Control<IntegerField>("join-port").value = port;
                    Submit("join-button");
                }
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
            peerSession = new NetSession(peerDb, peerTransport, !localHost, peerTeam, "TCP 对端", localHost ? (uint?)null : 7u,
                enableTermination: true, rating: new PlayerRating { Rating = 1500, Games = 0 }, enableReconnect: true);
            peerPollTime = Time.realtimeSinceStartupAsDouble;
            if (overrideContent != null)
            {
                yield return null;
                yield break;
            }
            float timeout = Time.realtimeSinceStartup + 15;
            while (screen.Session == null || screen.Session.State != NetState.Choose || peerSession.State != NetState.Choose)
            {
                Require(Time.realtimeSinceStartup < timeout, "真实 TCP 握手超时：" + StatusText());
                PollPeer();
                Require(peerSession.State != NetState.Error && peerSession.State != NetState.Desync,
                    "对端握手失败：" + peerSession.Error);
                yield return null;
            }
            report.checks.Add(Size + "：" + (localHost ? "UI 开房" : "UI 加入") + "真实 127.0.0.1 TCP 握手成功。");
        }

        IEnumerator PlayMatch()
        {
            var screen = Online;
            yield return PageScreenshots("online-battle", "online-content");
            float timeout = Time.realtimeSinceStartup + 120;
            bool reconnectChecked = screen.Session.MySide != 0;
            while (!screen.IsFinished || peerSession.State != NetState.Over)
            {
                Require(Time.realtimeSinceStartup < timeout, "联网整局超过 120 秒：" + StatusText());
                PollPeer();
                var local = screen.Session;
                Require(local != null && local.State != NetState.Error && local.State != NetState.Desync,
                    "本方会话失败：" + (local?.Error ?? StatusText()));
                Require(peerSession.State != NetState.Error && peerSession.State != NetState.Desync,
                    "对端会话失败：" + peerSession.Error);
                if (!reconnectChecked && local.Battle.Turn >= 2 && local.State == NetState.Choose
                    && peerSession.Battle.Turn == local.Battle.Turn && !local.HasSubmitted
                    && (peerSession.State == NetState.Choose || peerSession.State == NetState.Waiting))
                {
                    yield return CheckReconnect();
                    reconnectChecked = true;
                }
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
                    peerSession.PrepareClose();
                    peerTransport.CompleteSending();
                    peerSendingCompleted = true;
                }
                yield return null;
            }
            var mine = screen.Session;
            Require(reconnectChecked, "本机开房整局没有覆盖中途物理断线恢复。");
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
            var replayed = Replay.Run(mine.Battle.Db, BattleRecord.FromJson(mine.Battle.Record.ToJson()));
            Require(replayed.StateHash() == mine.Battle.StateHash(), "真实联网整局回放的终局哈希不一致。");
            var before = Profile.FromJson(profileBeforeConnection);
            var after = FileSaveStore.LoadProfileSnapshot(networkSaveDirectory);
            int games = before.Ratings.TryGetValue(Profile.DefaultRatingFormat, out var priorRating) ? priorRating.Games : 0;
            Require(after.Ratings.TryGetValue(Profile.DefaultRatingFormat, out var savedRating) && savedRating.Games == games + 1,
                "真实终局没有恰好累计一次本机排位记录。");
            Require(!string.IsNullOrWhiteSpace(Control<Label>("online-rating-summary").text), "终局没有展示本机排位变化。");
            string saved = after.ToJson();
            yield return null;
            yield return null;
            Require(FileSaveStore.LoadProfileSnapshot(networkSaveDirectory).ToJson() == saved, "终局重复 Tick 重复累计档案。");
            report.checks.Add(Size + "：真实 TCP 整局完成，双方终局哈希一致，回合=" + mine.Battle.Turn + "。");
        }

        IEnumerator CheckQuickChat()
        {
            var session = Online.Session;
            ulong hash = session.Battle.StateHash();
            Submit("chat-0");
            float timeout = Time.realtimeSinceStartup + 5;
            while (peerSession.ReceivedChats.Count == 0)
            {
                Require(Time.realtimeSinceStartup < timeout, "快捷语按钮未向真实 TCP 对端发送消息。");
                PollPeer();
                yield return null;
            }
            Require(peerSession.ReceivedChats[0].Index == 0, "快捷语按钮发送了错误编号。");
            Require(peerSession.SendQuickChat(1, 1), "对端快捷语无法发送。");
            yield return Until(() => session.ReceivedChats.Count == 1, 5, "界面会话未收到对端快捷语。");
            Control<Toggle>("mute-chat").value = true;
            Require(session.MuteOpponent, "屏蔽开关未接入会话。");
            Require(peerSession.SendQuickChat(2, 2), "屏蔽用例的快捷语无法发送。");
            yield return new WaitForSecondsRealtime(0.25f);
            Require(session.ReceivedChats.Count == 1 && session.Battle.StateHash() == hash,
                "屏蔽快捷语进入显示队列或改变了战斗状态。");
            Control<Toggle>("mute-chat").value = false;
            yield return PageScreenshots("quick-chat", "online-content");
            report.checks.Add(Size + "：UI 快捷语经真实 TCP 收发；屏蔽开关生效且不改变状态哈希。");
        }

        IEnumerator CheckReconnect()
        {
            var screen = Online;
            var local = screen.Session;
            var peer = peerSession;
            ulong hash = local.Battle.StateHash();
            string record = local.Battle.Record.ToJson();
            bool peerSubmitted = peer.HasSubmitted;
            Require(hash == peer.Battle.StateHash(), "断线前双方不在同一稳定节点。");
            peerTransport.Dispose();
            peerTransport = null;
            float deadline = Time.realtimeSinceStartup + 10;
            while (!local.IsDisconnected || !peer.IsDisconnected)
            {
                Require(Time.realtimeSinceStartup < deadline, "物理关闭 TCP 后双方没有进入断线等待。");
                PollPeer();
                yield return null;
            }
            yield return PageScreenshots("reconnecting", "online-content");
            int port = screen.ListeningPort;
            Require(port > 0, "恢复过程中房主丢失原监听端口。");
            pendingConnect = Task.Run(() => TcpTransport.Connect("127.0.0.1", port, 5000));
            yield return Until(() => pendingConnect.IsCompleted, 10, "真实 TCP 恢复连接超时。");
            peerTransport = pendingConnect.GetAwaiter().GetResult();
            pendingConnect = null;
            peer.Reconnect(peerTransport, peer.SessionId);
            deadline = Time.realtimeSinceStartup + 15;
            while (local.IsDisconnected || peer.IsDisconnected || local.State != NetState.Choose
                || peer.State != (peerSubmitted ? NetState.Waiting : NetState.Choose))
            {
                Require(Time.realtimeSinceStartup < deadline, "恢复原会话核对超时：" + StatusText());
                PollPeer();
                Require(local.State != NetState.Error && peer.State != NetState.Error
                    && local.State != NetState.Desync && peer.State != NetState.Desync,
                    "物理断线恢复失败：" + local.Error + " / " + peer.Error);
                yield return null;
            }
            Require(ReferenceEquals(local, screen.Session) && ReferenceEquals(peer, peerSession)
                && local.Battle.StateHash() == hash && peer.Battle.StateHash() == hash
                && local.Battle.Record.ToJson() == record && !local.HasSubmitted && peer.HasSubmitted == peerSubmitted,
                "重连没有保留原会话、稳定状态或已有录像事件。");
            Require(FileSaveStore.LoadProfileSnapshot(networkSaveDirectory).ToJson() == profileBeforeConnection,
                "恢复过程中提前累计了本机战绩。");
            yield return PageScreenshots("reconnected", "online-content");
            report.checks.Add(Size + "：第 " + local.Battle.Turn
                + " 回合物理关闭 TCP，UI 房主自动接受恢复连接；双方保持原 NetSession/状态哈希/录像，随后继续整局且只结算一次。");
        }

        void PollPeer()
        {
            if (peerSession == null) return;
            double now = Time.realtimeSinceStartupAsDouble;
            double elapsed = Math.Max(0, now - peerPollTime);
            peerPollTime = now;
            bool disconnected = peerSession.IsDisconnected;
            var state = peerSession.State;
            int events = peerSession.Battle?.Record.Events.Count ?? -1;
            long timerRevision = peerSession.TimerRevision;
            peerSession.Poll(elapsed);
            bool samePhase = !disconnected && !peerSession.IsDisconnected && state == peerSession.State
                && events == (peerSession.Battle?.Record.Events.Count ?? -1) && timerRevision == peerSession.TimerRevision;
            peerSession.Tick(samePhase ? elapsed : 0);
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
                PollPeer();
                yield return null;
            }
            Require(Online.Session == null || Online.Session.State == NetState.Error || Online.Session.State == NetState.Desync,
                "错误提示后仍保留可继续的会话。");
            Require(FileSaveStore.LoadProfileSnapshot(networkSaveDirectory).ToJson() == profileBeforeConnection,
                "握手失败或断线错误写入了本机战绩/排位。");
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
                if (string.IsNullOrEmpty(text.text) || text.panel == null || !VisibleInHierarchy(text)) return;
                Rect visible = Root.worldBound;
                for (var parent = text.hierarchy.parent; parent != null; parent = parent.hierarchy.parent)
                {
                    if (parent is ScrollView scroll) visible = Intersection(visible, scroll.contentViewport.worldBound);
                }
                Rect bounds = text.worldBound;
                if (!visible.Overlaps(bounds)) return;
                Require(Finite(bounds.x) && Finite(bounds.y) && Finite(bounds.width) && Finite(bounds.height),
                    "文字布局包含非有限数值：" + TextLayoutDetails(text));
                // 滚动区域允许边缘裁切，完整可见的文字才检查文本测量。
                if (bounds.xMin >= visible.xMin - 2 && bounds.xMax <= visible.xMax + 2
                    && bounds.yMin >= visible.yMin - 2 && bounds.yMax <= visible.yMax + 2)
                {
                    Vector2 measured = text.MeasureTextSize(text.text, text.contentRect.width,
                        text.resolvedStyle.whiteSpace == WhiteSpace.Normal ? VisualElement.MeasureMode.Exactly : VisualElement.MeasureMode.Undefined,
                        0, VisualElement.MeasureMode.Undefined);
                    Require(!text.isElided && measured.x <= text.contentRect.width + 3 && measured.y <= text.contentRect.height + 3,
                        "可见文字可能被截断：" + text.name + " / " + text.text + " measured=" + measured
                        + " content=" + text.contentRect + " visible=" + visible + " ancestors=" + TextLayoutDetails(text));
                }
            });
        }

        static bool VisibleInHierarchy(VisualElement element)
        {
            // Foldout 的逻辑 parent 会跨过隐藏内容容器，必须检查完整物理祖先链。
            for (var ancestor = element; ancestor != null; ancestor = ancestor.hierarchy.parent)
                if (!ancestor.visible || ancestor.resolvedStyle.display == DisplayStyle.None
                    || ancestor.resolvedStyle.opacity == 0) return false;
            return true;
        }

        static string TextLayoutDetails(VisualElement element)
        {
            var details = new StringBuilder();
            for (var ancestor = element; ancestor != null; ancestor = ancestor.hierarchy.parent)
            {
                if (details.Length > 0) details.Append(" <- ");
                details.Append(ancestor.GetType().Name).Append('#').Append(ancestor.name)
                    .Append(" [").Append(ancestor.worldBound).Append("; visible=").Append(ancestor.visible)
                    .Append("; display=").Append(ancestor.resolvedStyle.display)
                    .Append("; opacity=").Append(ancestor.resolvedStyle.opacity).Append(']');
            }
            return details.ToString();
        }

        static Rect Intersection(Rect a, Rect b)
        {
            float left = Mathf.Max(a.xMin, b.xMin), top = Mathf.Max(a.yMin, b.yMin);
            return new Rect(left, top, Mathf.Max(0, Mathf.Min(a.xMax, b.xMax) - left), Mathf.Max(0, Mathf.Min(a.yMax, b.yMax) - top));
        }

        static bool Finite(float value) { return !float.IsNaN(value) && !float.IsInfinity(value); }
        static void Require(bool condition, string failure) { if (!condition) throw new InvalidOperationException(failure); }

        sealed class FailingStore : ISaveStore
        {
            readonly ISaveStore source;
            readonly bool failRead;
            public FailingStore(ISaveStore source, bool failRead) { this.source = source; this.failRead = failRead; }
            public GameSettings LoadSettings()
            {
                if (failRead) throw new IOException("验收读取失败");
                return source.LoadSettings();
            }
            public void SaveSettings(GameSettings value) { throw new IOException("验收写入失败"); }
            public Profile LoadProfile() { return source.LoadProfile(); }
            public void SaveProfile(Profile value) { source.SaveProfile(value); }
            public void SaveReplay(string name, string json) { source.SaveReplay(name, json); }
            public List<string> ListReplays() { return source.ListReplays(); }
            public string LoadReplay(string name) { return source.LoadReplay(name); }
        }

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
