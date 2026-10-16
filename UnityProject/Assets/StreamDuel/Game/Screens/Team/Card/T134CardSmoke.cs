using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using StreamDuel.Core;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.UIElements;

namespace StreamDuel.Game
{
    // 正式页面使用虚拟输入设备；仅夹具和证据输出被隔离，不替换页面事件处理。
    public sealed class T134CardSmoke : MonoBehaviour
    {
        [Serializable]
        sealed class Report
        {
            public string unityVersion, outputDirectory, failure, finalHash;
            public string inputScope = "InputSystem 虚拟 Keyboard/Gamepad 经正式 UI 输入链；不是人工键盘或实体手柄验收。完整对局经实际 BattleScreen 提交桥推进。";
            public string storageScope = "SmokeRequested 在 Awake 前隔离玩家存档；预设/剪贴板/provider 为内存注入，证据仅写本工程 Logs/T134。未扫描用户目录。";
            public string layoutScope = "1280×720 和 1920×1080，正常/色弱、长文本、滚动上中下；程序检查字形测量与边界，截图仍须独立目视复核。";
            public bool completed;
            public int errors, warnings, turns, winner, portraitCalls, presetSaves;
            public List<string> checks = new List<string>();
            public List<string> messages = new List<string>();
            public List<string> screenshots = new List<string>();
            public List<BoundsEntry> bounds = new List<BoundsEntry>();
        }

        [Serializable]
        sealed class BoundsEntry
        {
            public string name, text, phase;
            public int width, height;
            public float x, y, w, h;
        }

        sealed class ProbeStore : ITeamPresetStore
        {
            public int Saves;
            public TeamPresetList Load() { return new TeamPresetList(); }
            public void Save(TeamPresetList value) { Saves++; }
        }

        sealed class ProbePortraits : IPortraitProvider, IDisposable
        {
            readonly IPortraitProvider inner;
            public bool ReturnNull, Disposed;
            public int Calls;
            public ContentDb LastDb;
            public string LastId;
            public ProbePortraits(IPortraitProvider inner) { this.inner = inner; }
            public Texture2D Get(ContentDb db, string id)
            {
                Require(!Disposed, "借用头像提供器被资料卡提前释放。");
                Calls++; LastDb = db; LastId = id;
                return ReturnNull ? null : inner.Get(db, id);
            }
            public void Dispose() { Disposed = true; }
        }

        public static bool Completed { get; private set; }
        public static int ExitCode { get; private set; }
        public static int RequestedWidth { get; private set; }
        public static int RequestedHeight { get; private set; }
        public static string ReportPath { get; private set; }
        Report report;
        ScreenRouter router;
        BattleScreen legacy;
        PortraitProvider ownedPortraits;
        ProbePortraits portraits;
        readonly ProbeStore store = new ProbeStore();
        Keyboard keyboard, previousKeyboard;
        Gamepad gamepad, previousGamepad;
        string output;
        string requestedInput = "无";
        VisualElement inputTraceRoot;
        bool running;
        float deadline;
        VisualElement Page => router.Document.rootVisualElement.Q("team-screen");
        TeamCardPanel Card => (Page.userData as TeamScreen)?.T134CardPanel;
        VisualElement Modal => Card?.Root;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Install()
        {
            if (!Environment.GetCommandLineArgs().Contains("--t134-smoke-test")) return;
            Completed = false; ExitCode = 1; ReportPath = null;
            RequestedWidth = RequestedHeight = 0;
            var host = new GameObject("StreamDuel.T134CardSmoke");
            DontDestroyOnLoad(host);
            host.AddComponent<T134CardSmoke>().Configure();
        }

        public static string ReadOutputDirectory()
        {
            var args = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(args, "--t134-output-dir");
            if (args.Count(arg => arg == "--t134-smoke-test") != 1 || index < 0 || index + 1 >= args.Length
                || args.Count(arg => arg == "--t134-output-dir") != 1 || !Path.IsPathFullyQualified(args[index + 1]))
                throw new ArgumentException("资料卡验收需要 --t134-smoke-test 与唯一的 --t134-output-dir 绝对目录。");
            string path = Path.GetFullPath(args[index + 1]);
            string allowed = Path.GetFullPath(Path.Combine(Application.dataPath, "../Logs/T134"));
            if (!Within(path, allowed)) throw new ArgumentException("资料卡验收产物须位于本工程 Logs/T134。");
            return path;
        }

        static bool Within(string path, string directory)
        {
            string root = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(path, root, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        void Configure()
        {
            report = new Report { unityVersion = Application.unityVersion };
            running = true; deadline = Time.realtimeSinceStartup + 840;
            Application.logMessageReceived += OnLog;
            try
            {
                output = ReadOutputDirectory();
                Directory.CreateDirectory(output);
                report.outputDirectory = output;
                ReportPath = Path.Combine(output, "result.json");
            }
            catch (Exception ex) { Finish(ex.ToString()); }
        }

        void Start() { if (running) StartCoroutine(Guard(Run())); }
        void LateUpdate() { if (running && Time.realtimeSinceStartup >= deadline) Finish("资料卡验收超过 840 秒。"); }
        void OnLog(string message, string stack, LogType type)
        {
            if (type == LogType.Warning) report.warnings++;
            if (type == LogType.Error || type == LogType.Assert || type == LogType.Exception) report.errors++;
            if (type != LogType.Log) report.messages.Add(type + ": " + message + "\n" + stack);
        }

        IEnumerator Guard(IEnumerator root)
        {
            var pending = new Stack<IEnumerator>(); pending.Push(root);
            while (running)
            {
                bool more = false; object next = null; string failure = null;
                try { more = pending.Peek().MoveNext(); if (more) next = pending.Peek().Current; else pending.Pop(); }
                catch (Exception ex) { failure = ex.ToString(); }
                if (failure != null || pending.Count == 0) { Finish(failure); yield break; }
                if (!more) continue;
                if (next is IEnumerator nested) { pending.Push(nested); continue; }
                yield return next;
            }
        }

        IEnumerator Run()
        {
            float ready = Time.realtimeSinceStartup + 40;
            while (router == null)
            {
                router = FindAnyObjectByType<ScreenRouter>();
                Require(Time.realtimeSinceStartup < ready, "没有加载正式 ScreenRouter。");
                yield return null;
            }
            yield return Settle();
            legacy = router.GetComponent<BattleScreen>();
            Require(legacy != null && router.CurrentScreenId == ScreenIds.Menu, "资料卡验收没有从正式菜单启动。");
            Require(typeof(BattleScreen).GetField("saveStore", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(legacy) == null,
                "验收启动前没有隔离真实存档。");
            ownedPortraits = new PortraitProvider(router.Resources.RegularFont);
            portraits = new ProbePortraits(ownedPortraits);
            previousKeyboard = Keyboard.current; previousGamepad = Gamepad.current;
            keyboard = InputSystem.AddDevice<Keyboard>("T134SmokeKeyboard");
            gamepad = InputSystem.AddDevice<Gamepad>("T134SmokeGamepad");
            inputTraceRoot = router.Document.rootVisualElement;
            inputTraceRoot.RegisterCallback<KeyDownEvent>(TraceKey, TrickleDown.TrickleDown);
            inputTraceRoot.RegisterCallback<NavigationMoveEvent>(TraceMove, TrickleDown.TrickleDown);
            inputTraceRoot.RegisterCallback<NavigationSubmitEvent>(TraceSubmit, TrickleDown.TrickleDown);
            inputTraceRoot.RegisterCallback<NavigationCancelEvent>(TraceCancel, TrickleDown.TrickleDown);
            yield return Settle();
            var original = legacy.MenuPacks.First(db => db.PackId == "original");
            var fan = legacy.MenuPacks.First(db => db.PackId.StartsWith("fan_", StringComparison.Ordinal));
            var synthetic = LongFixture();
            string originalHash = original.ContentHash();
            for (int resolution = 0; resolution < 2; resolution++)
            {
                yield return Resize(resolution == 0 ? 1280 : 1920, resolution == 0 ? 720 : 1080);
                Progress("资料卡 " + Screen.width + "×" + Screen.height);
                foreach (bool colorblind in new[] { false, true })
                {
                    ShowContext(original, colorblind);
                    yield return Settle();
                    Element<Button>(Page, "team-auto").Focus();
                    yield return PressKey(Key.Enter); yield return Settle();
                    Require(Picked().Length == 6, "背景输入隔离夹具未选满六人。");
                    yield return CheckInput(original, colorblind, false);
                    yield return CardEvidence(original, colorblind, "original-" + (colorblind ? "alt" : "normal"));
                    yield return CloseByKey(false);
                    yield return CheckInput(original, colorblind, true);
                    yield return CloseByKey(true);
                }
                ShowContext(fan, false);
                yield return Settle();
                yield return OpenByKey(fan.Streamers[0].Id, false);
                CheckCard(fan, false);
                Require(!string.IsNullOrEmpty(StreamerCard.Build(fan, Card.CurrentId).Notice), "同人夹具没有声明。");
                yield return CardEvidence(fan, false, "fan-notice");
                yield return CloseByKey(false);

                portraits.ReturnNull = true;
                ShowContext(synthetic, true);
                yield return Settle();
                yield return OpenByKey(synthetic.Streamers[0].Id, false);
                Require(IsVisible(Element<Label>(Modal, "team-card-placeholder")), "缺图时没有显示占位。");
                Require(Element<Image>(Modal, "team-card-portrait").image == null, "缺图占位仍使用上一包的图片。");
                yield return CardEvidence(synthetic, true, "long-null-portrait");
                yield return CloseByKey(false);
                portraits.ReturnNull = false;
                // 同一个 id 换回原包，防止缓存将上一张长文或空头像带回。
                ShowContext(original, false);
                yield return Settle();
                yield return OpenByKey(original.Streamers[0].Id, false);
                CheckCard(original, false);
                Require(Element<Image>(Modal, "team-card-portrait").image != null, "换回正常 provider 后没有恢复头像。");
                var previousPanel = Card;
                router.Show(ScreenIds.Menu);
                yield return Settle();
                Require(!previousPanel.IsOpen && router.Document.rootVisualElement.Q("team-card-modal") == null, "页面退出后仍残留模态卡片。");
                Require(!portraits.Disposed, "页面退出释放了借用 provider。");
            }
            Require(original.ContentHash() == originalHash, "浏览资料卡修改了原始内容库。");
            Require(store.Saves == 0, "资料卡浏览意外保存了预设。");
            report.checks.Add("正常/色弱、原创/同人、同 ID 换内容库、null 头像与借用生命周期均通过；浏览未保存预设或改变内容哈希。");
            yield return CompleteBattle(original);
            Require(store.Saves == 0, "开战验收意外保存了预设。");
            report.portraitCalls = portraits.Calls; report.presetSaves = store.Saves;
            report.completed = true;
        }

        void ShowContext(ContentDb db, bool colorblind, bool liveBattle = false)
        {
            router.Show(ScreenIds.Team, new TeamScreenArgs
            {
                Content = db, Packs = new[] { db }, Presets = store, Portraits = portraits,
                Settings = new GameSettings { DefaultPack = db.PackId, ColorblindMode = colorblind, AiLevel = AiLevel.Normal, FastPlay = true },
                Seed = 1340134, ReadClipboard = () => "", WriteClipboard = value => { },
                StartBattle = liveBattle ? new Action<TeamBattleRequest>(legacy.StartTeamBattle) : value => { },
                NavigateToBattle = liveBattle, Return = () => router.Show(ScreenIds.Menu)
            });
        }

        IEnumerator OpenByKey(string id, bool pad)
        {
            var button = Element<Button>(Page, "streamer-card-" + id);
            Element<ScrollView>(Page, "team-roster").ScrollTo(button);
            button.Focus(); yield return Settle();
            Require(ReferenceEquals(Page.focusController.focusedElement, button), "资料卡打开前无法聚焦主播卡。");
            int calls = portraits.Calls;
            if (pad) yield return PressGamepad(GamepadButton.South); else yield return PressKey(Key.Enter);
            yield return Settle();
            Require(Card != null && Card.IsOpen && Card.CurrentId == id, "真实输入没有打开指定主播卡：" + id);
            Require(portraits.Calls > calls && portraits.LastId == id, "资料卡没有调用借入头像 provider。");
            CheckFocus();
        }

        IEnumerator CheckInput(ContentDb db, bool alt, bool pad)
        {
            string id = db.Streamers[0].Id;
            string[] selected = Picked();
            var enabled = Page.Children().ToDictionary(child => child, child => child.enabledSelf);
            yield return OpenByKey(id, pad);
            foreach (var pair in enabled)
                Require(!pair.Key.enabledInHierarchy, "模态打开后背景仍可输入：" + pair.Key.name);
            string before = Card.CurrentId;
            if (pad) yield return PressGamepad(GamepadButton.RightShoulder); else yield return PressKey(Key.RightArrow);
            yield return Settle();
            Require(Card.CurrentId == db.Streamers[1].Id, "真实左右切换必须恰好到下一位，实际为：" + Card.CurrentId);
            CheckCard(db, alt);
            if (pad) yield return PressGamepad(GamepadButton.LeftShoulder); else yield return PressKey(Key.LeftArrow);
            yield return Settle();
            Require(Card.CurrentId == before, "反向切换未回到原主播。");
            if (!pad)
            {
                yield return PressKey(Key.RightArrow, legacyKey: KeyCode.RightArrow);
                yield return Settle();
                Require(Card.CurrentId == db.Streamers[1].Id, "真实方向短按叠加 legacy KeyDown 后没有恰好切换一位。");
                yield return PressKey(Key.LeftArrow, legacyKey: KeyCode.LeftArrow);
                yield return Settle();
                Require(Card.CurrentId == before, "混合输入反向切换没有恰好回到原主播。");
                report.checks.Add("InputSystem 方向短按叠加 legacy KeyDown：正反向每次恰好切一位，无双处理。");
            }
            var closeButton = Element<Button>(Modal, "team-card-close");
            closeButton.Focus(); yield return Settle();
            var focusChanges = new List<VisualElement>();
            EventCallback<FocusInEvent> collectFocus = evt =>
            {
                var target = evt.target as VisualElement;
                if (target != null && (focusChanges.Count == 0 || !ReferenceEquals(focusChanges[focusChanges.Count - 1], target)))
                    focusChanges.Add(target);
            };
            Modal.RegisterCallback(collectFocus, TrickleDown.TrickleDown);
            if (pad) yield return PressGamepad(GamepadButton.DpadDown); else yield return PressKey(Key.Tab);
            CheckFocus();
            var forwardFocus = Page.focusController.focusedElement;
            Require(focusChanges.Count == 1 && !ReferenceEquals(forwardFocus, closeButton), "单次导航未恰好移动一次焦点，次数=" + focusChanges.Count);
            focusChanges.Clear();
            if (pad) yield return PressGamepad(GamepadButton.DpadUp); else yield return PressKey(Key.Tab, true);
            Require(focusChanges.Count == 1 && ReferenceEquals(Page.focusController.focusedElement, closeButton), "单次反向导航未恰好一次回到原焦点，次数=" + focusChanges.Count);
            focusChanges.Clear();
            if (pad) yield return PressGamepad(GamepadButton.DpadDown); else yield return PressKey(Key.Tab);
            Require(focusChanges.Count == 1 && ReferenceEquals(Page.focusController.focusedElement, forwardFocus), "相同单步导航未恰好移动一次到相同焦点。");
            Modal.UnregisterCallback(collectFocus, TrickleDown.TrickleDown);
            for (int i = 0; i < 12; i++)
            {
                if (pad) yield return PressGamepad(GamepadButton.DpadDown); else yield return PressKey(Key.Tab);
                CheckFocus();
            }
            if (!pad) { yield return PressKey(Key.Tab, true); CheckFocus(); }
            var clear = Element<Button>(Page, "team-clear");
            clear.Focus(); yield return Settle();
            CheckFocus();
            Require(Picked().SequenceEqual(selected), "模态内导航泄漏成组队操作。");
            yield return CloseByKey(pad);
            foreach (var pair in enabled) Require(pair.Key.enabledSelf == pair.Value, "关闭没有恢复背景启用状态：" + pair.Key.name);
            Require(ReferenceEquals(Page.focusController.focusedElement, Element<Button>(Page, "streamer-card-" + id)), "关闭没有还原打开者焦点。");
            Require(Picked().SequenceEqual(selected), "关闭卡片改变了阵容。");
            // 独立详情入口也必须走真实确认事件，不以直接 Open 调用代替。
            Element<Button>(Page, "team-open-card").Focus(); yield return Settle();
            if (pad) yield return PressGamepad(GamepadButton.South); else yield return PressKey(Key.Enter);
            yield return Settle();
            Require(Card.IsOpen && Card.CurrentId == id, "详情打开按钮没有通过真实输入打开当前主播。");
            report.checks.Add(Screen.width + "×" + Screen.height + " " + (alt ? "色弱" : "正常") + " "
                + (pad ? "Gamepad South/East/肩键/Dpad" : "Keyboard Enter/Escape/左右/Tab/ShiftTab") + "：开关、切换、焦点闭环与背景隔离通过。");
        }

        IEnumerator CloseByKey(bool pad)
        {
            Require(Card != null && Card.IsOpen, "关闭前卡片未打开。");
            if (pad) yield return PressGamepad(GamepadButton.East); else yield return PressKey(Key.Escape);
            yield return Settle();
            Require(router.CurrentScreenId == ScreenIds.Team, "关闭资料卡泄漏为退出组队页面。");
            Require(!Card.IsOpen && Page.Q("team-card-modal") == null, "真实取消输入未关闭资料卡。");
        }

        void CheckFocus()
        {
            var focused = Page.focusController.focusedElement as VisualElement;
            Require(focused != null && (ReferenceEquals(focused, Modal) || Modal.Contains(focused)), "模态焦点逃到背景：" + focused?.name);
        }

        void CheckCard(ContentDb db, bool alt)
        {
            var expected = StreamerCard.Build(db, Card.CurrentId);
            Require(Element<Label>(Modal, "team-card-name").text == expected.Name, "资料卡名字不对应当前快照。");
            Require(Element<Label>(Modal, "team-card-pack").text.Contains(expected.PackName), "资料卡内容包名称丢失。");
            Require(Element<Label>(Modal, "team-card-persona-name").text.Contains(expected.PersonaName), "资料卡人设名称丢失。");
            Require(Element<Label>(Modal, "team-card-persona-text").text == expected.PersonaText, "资料卡人设说明不对应 Core。");
            var notice = Modal.Q<Label>("team-card-notice");
            Require(string.IsNullOrEmpty(expected.Notice) ? notice == null || string.IsNullOrEmpty(notice.text) || !IsVisible(notice)
                : notice != null && notice.text == expected.Notice, "资料卡同人声明不对应 Core。");
            Require(Modal.Query<Button>().ToList().All(button => !button.name.Contains("compare") && !button.text.Contains("对比")), "尚无 T120 时出现对比入口。");
            var tracks = Element<VisualElement>(Modal, "team-card-tracks").Query<Label>().ToList();
            foreach (var track in expected.Tracks)
            {
                var label = tracks.SingleOrDefault(item => item.text.Contains(track.Name) && item.text.Contains(track.Symbol));
                Require(label != null, "赛道中文名称或符号遗漏：" + track.Name);
                Color color;
                Require(ColorUtility.TryParseHtmlString(alt ? db.Types[track.Id].ColorAlt : db.Types[track.Id].Color, out color), "赛道颜色无效。");
                Require(NearColor(label.resolvedStyle.color, color) || NearColor(label.resolvedStyle.backgroundColor, color), "赛道颜色未按模式切换：" + track.Name);
            }
            Require(expected.Stats.Count == 6 && expected.Skills.Count == 5, "验收卡缺少六维或全部五技能。");
            foreach (var stat in expected.Stats)
            {
                Require(Element<Label>(Modal, "stat-value-" + stat.Id).text.Contains(Number(stat.Value)), "六维值不匹配：" + stat.Id);
                string rank = Element<Label>(Modal, "stat-rank-" + stat.Id).text;
                Require(rank.Contains(Number(stat.Rank)) && rank.Contains(Number(stat.Total)), "六维排名或总数不匹配：" + stat.Id);
                Require(Element<VisualElement>(Modal, "stat-fill-" + stat.Id).resolvedStyle.width > 0, "六维条不可见：" + stat.Id);
            }
            for (int i = 0; i < expected.Skills.Count; i++)
            {
                var skill = expected.Skills[i];
                Require(Element<Label>(Modal, "skill-name-" + i).text.Contains(skill.Name), "遗漏技能名称：" + i);
                string numbers = Element<Label>(Modal, "skill-numbers-" + i).text;
                foreach (int value in new[] { skill.Power, skill.Accuracy, skill.Pp, skill.Priority })
                    Require(numbers.Contains(Number(value)), "技能数值遗漏：" + i + "/" + value);
                string body = string.Join("\n", Element<VisualElement>(Modal, "team-card-skill-" + i).Query<Label>().ToList().Select(label => label.text));
                Require(body.Contains(skill.TypeName) && body.Contains(skill.TypeSymbol) && body.Contains(skill.CategoryName), "技能赛道或类别遗漏：" + i);
                Require(!skill.IsFifth || body.Contains("必杀"), "第五技能没有明确必杀标识。");
                for (int j = 0; j < skill.Effects.Count; j++)
                {
                    var effect = skill.Effects[j];
                    Require(Element<Label>(Modal, "skill-effect-" + i + "-" + j).text == effect.Text, "技能效果说明不对应 Core。");
                    Require(Element<Label>(Modal, "skill-timing-" + i + "-" + j).text == effect.At, "技能判定时机不对应 Core。");
                    var dispel = Modal.Q<Label>("skill-dispel-" + i + "-" + j);
                    Require(string.IsNullOrEmpty(effect.Dispel) ? dispel == null || !IsVisible(dispel) || string.IsNullOrEmpty(dispel.text)
                        : dispel != null && dispel.text == effect.Dispel, "技能驱散规则不对应 Core。");
                }
            }
            Require(ReferenceEquals(portraits.LastDb, db) && portraits.LastId == expected.Id, "provider 接收到陈旧内容库或主播。");
        }

        IEnumerator CardEvidence(ContentDb db, bool alt, string suffix)
        {
            CheckCard(db, alt);
            var scroll = Element<ScrollView>(Modal, "team-card-scroll");
            string[] points = { "top", "middle", "bottom" };
            for (int step = 0; step < 3; step++)
            {
                scroll.scrollOffset = new Vector2(0, step * scroll.verticalScroller.highValue / 2f);
                yield return Settle();
                ValidateLayout(Modal, suffix + "-" + points[step]);
                Capture(suffix + "-" + points[step]);
            }
            var fifth = Element<VisualElement>(Modal, "team-card-skill-4");
            scroll.ScrollTo(fifth); yield return Settle();
            Require(Intersect(fifth.worldBound, scroll.contentViewport.worldBound).height > 0, "第五技能无法滚动到视口。");
            scroll.scrollOffset = Vector2.zero; yield return Settle();
            Element<Button>(Modal, "team-card-page-down").Focus();
            yield return PressKey(Key.Enter); yield return Settle();
            Require(scroll.scrollOffset.y > 0, "真实翻页按键未推进资料卡滚动。");
            Element<Button>(Modal, "team-card-page-up").Focus();
            yield return PressKey(Key.Enter); yield return Settle();
            Require(scroll.scrollOffset.y < scroll.verticalScroller.highValue || scroll.verticalScroller.highValue == 0, "真实向上翻页未生效。");
            report.checks.Add(suffix + "：全部五技能/六维排名/人设/声明与 StreamerCard 一致，滚动与翻页按键通过。");
        }

        static ContentDb LongFixture()
        {
            var db = ContentDb.FromJson(Resources.Load<TextAsset>("StreamDuelRules").text, Resources.Load<TextAsset>("Packs/original").text);
            string longText = string.Concat(Enumerable.Repeat("用于验证中文长文本换行与完整阅读的原创资料", 5));
            var member = db.Streamers[0];
            member.Name = longText;
            member.Type2 = db.Types.Keys.First(id => id != member.Type);
            db.PackName = "内存长文本夹具" + longText;
            db.PackNotice = "非官方同人作品；" + longText;
            var persona = db.Personas[member.Persona];
            persona.Name = "长人设名称" + longText;
            persona.Code = "T_ENTER_FIELD";
            persona.Field = db.Fields.Keys.First();
            persona.Params["turns"] = 3;
            db.Fields[persona.Field].Name = "长效果名称" + longText;
            foreach (string skillId in member.Skills.Concat(new[] { member.Fifth }))
            {
                db.Skills[skillId].Name = "长技能名称" + longText;
                db.Skills[skillId].Effects.Add(new EffectDef { At = "post", Code = "FIELD", Target = "self", Field = persona.Field, Turns = 3 });
            }
            return db;
        }

        IEnumerator CompleteBattle(ContentDb db)
        {
            Progress("完整实际 BattleScreen 对局");
            ShowContext(db, false, true); yield return Settle();
            Element<Button>(Page, "team-auto").Focus(); yield return PressKey(Key.Enter); yield return Settle();
            Require(Picked().Length == 6, "真实自动组队按钮未选满阵容。");
            Element<Button>(Page, "team-start").Focus(); yield return PressKey(Key.Enter); yield return Settle();
            var battle = legacy.CurrentTeamBattle;
            Require(battle != null && legacy.enabled && router.CurrentScreenId == ScreenIds.Legacy, "真实开战按钮没有进入 BattleScreen。");
            Capture("battle-opening");
            legacy.AdvanceTeamSmoke();
            Require(battle.Phase == Phase.Choose, "首发确认后没有进入行动阶段。");
            legacy.AdvanceTeamSmoke();
            var expected = battle.Clone();
            var mine = Ai.Choose(expected, 0, AiLevel.Normal);
            var foe = Ai.Choose(expected, 1, AiLevel.Normal);
            expected.ResolveTurn(mine, foe);
            legacy.AdvanceTeamSmoke();
            Require(battle.StateHash() == expected.StateHash() && battle.Record.ToJson() == expected.Record.ToJson(), "实际 UI 提交与相同状态 Core AI 结算不一致。");
            bool captured = false; int steps = 0;
            while (battle.Phase != Phase.Over)
            {
                Require(++steps <= 5000, "实际 UI 对局未能完成。");
                legacy.AdvanceTeamSmoke(); yield return null;
                if (!captured && battle.Turn >= 2) { yield return new WaitForEndOfFrame(); Capture("battle-playing"); captured = true; }
            }
            legacy.AdvanceTeamSmoke(); yield return Settle(); Capture("battle-finished");
            string record = battle.Record.ToJson();
            var replay = Replay.Run(db, BattleRecord.FromJson(record));
            Require(replay.StateHash() == battle.StateHash() && replay.Log.Select(entry => entry.Text).SequenceEqual(battle.Log.Select(entry => entry.Text)), "整局 Replay 哈希或日志不一致。");
            Require(battle.Record.ToJson() == record, "重放改变了原始记录。");
            File.WriteAllText(Path.Combine(output, "battle-record.json"), record);
            report.finalHash = battle.StateHash().ToString("x16"); report.turns = battle.Turn; report.winner = battle.Winner;
            report.checks.Add("真实组队确认到 BattleScreen 完整对局，首回合 Core 克隆动作/记录/哈希一致，完整回放最终哈希和日志一致。");
        }

        string[] Picked()
        {
            return Enumerable.Range(0, 6).Select(i => Element<Button>(Page, "team-slot-" + i).userData as string)
                .Where(id => !string.IsNullOrEmpty(id)).ToArray();
        }
        static T Element<T>(VisualElement root, string name) where T : VisualElement
        {
            var value = root?.Q<T>(name); Require(value != null, "缺少控件：" + name); return value;
        }
        static string Number(int value) { return value.ToString(CultureInfo.InvariantCulture); }
        void Progress(string stage)
        {
            File.WriteAllText(Path.Combine(output, "progress.txt"), stage);
        }
        static bool NearColor(Color a, Color b) { return Mathf.Abs(a.r - b.r) < .015f && Mathf.Abs(a.g - b.g) < .015f && Mathf.Abs(a.b - b.b) < .015f; }
        static bool IsVisible(VisualElement element)
        {
            if (element == null) return false;
            for (var current = element; current != null; current = current.hierarchy.parent)
                if (current.resolvedStyle.display == DisplayStyle.None || current.resolvedStyle.visibility == Visibility.Hidden) return false;
            return true;
        }
        IEnumerator Settle() { yield return null; yield return null; yield return new WaitForEndOfFrame(); }
        IEnumerator PressKey(Key key, bool shift = false, KeyCode? legacyKey = null)
        {
            requestedInput = "Keyboard " + (shift ? "Shift+" : "") + key + (legacyKey.HasValue ? " + legacy KeyDown" : "");
            InputSystem.QueueStateEvent(keyboard, shift ? new KeyboardState(Key.LeftShift, key) : new KeyboardState(key));
            if (legacyKey.HasValue)
            {
                var target = Page.focusController.focusedElement as VisualElement;
                Require(target != null, "混合输入回归没有真实焦点。");
                using (var evt = KeyDownEvent.GetPooled('\0', legacyKey.Value, EventModifiers.None)) target.SendEvent(evt);
            }
            yield return null; yield return null;
            InputSystem.QueueStateEvent(keyboard, new KeyboardState());
            yield return null; yield return null;
        }
        IEnumerator PressGamepad(GamepadButton button)
        {
            requestedInput = "Gamepad " + button;
            InputSystem.QueueStateEvent(gamepad, new GamepadState().WithButton(button));
            yield return null; yield return null;
            InputSystem.QueueStateEvent(gamepad, new GamepadState());
            yield return null; yield return null;
        }
        IEnumerator Resize(int width, int height)
        {
            RequestedWidth = width; RequestedHeight = height;
            Screen.SetResolution(width, height, FullScreenMode.Windowed);
            float until = Time.realtimeSinceStartup + 20;
            while (Screen.width != width || Screen.height != height)
            {
                Require(Time.realtimeSinceStartup < until, "分辨率切换超时；须用 T134SmokeEditor 驱动。"); yield return null;
            }
            yield return Settle();
        }

        void ValidateLayout(VisualElement page, string phase)
        {
            var viewports = page.Query<ScrollView>().ToList().Select(scroll => scroll.contentViewport).ToList();
            foreach (var text in page.Query<TextElement>().ToList())
            {
                if (!IsVisible(text) || string.IsNullOrEmpty(text.text)) continue;
                Rect visible = text.worldBound;
                for (var ancestor = text.hierarchy.parent; ancestor != null; ancestor = ancestor.hierarchy.parent)
                {
                    if (viewports.Contains(ancestor))
                    {
                        Require(visible.xMin >= ancestor.worldBound.xMin - 2 && visible.xMax <= ancestor.worldBound.xMax + 2,
                            "文字横向超出滚动视口：" + text.name);
                        visible = Intersect(visible, ancestor.worldBound);
                        if (!HasArea(visible)) break;
                    }
                    else if (ancestor.style.overflow.value == Overflow.Hidden || ancestor is ScrollView)
                        Require(Contains(ancestor.worldBound, visible), "文字被卡片内部裁切：" + text.name + " / " + ancestor.name);
                }
                if (HasArea(visible)) Require(Contains(router.Document.rootVisualElement.worldBound, visible), "文字超出屏幕：" + text.name);
                var measured = text.MeasureTextSize(text.text, text.contentRect.width,
                    text.resolvedStyle.whiteSpace == WhiteSpace.Normal ? VisualElement.MeasureMode.Exactly : VisualElement.MeasureMode.Undefined,
                    0, VisualElement.MeasureMode.Undefined);
                Require(!text.isElided && measured.x <= text.contentRect.width + 2 && measured.y <= text.contentRect.height + 2,
                    "完整文字发生截断：" + text.name + " measured=" + measured + " box=" + text.contentRect);
                report.bounds.Add(new BoundsEntry { name = text.name, text = text.text, phase = phase, width = Screen.width, height = Screen.height,
                    x = text.worldBound.x, y = text.worldBound.y, w = text.worldBound.width, h = text.worldBound.height });
            }
            var buttons = page.Query<Button>().ToList().Where(button => IsVisible(button) && HasArea(ViewportBounds(button, viewports))).ToList();
            for (int i = 0; i < buttons.Count; i++) for (int j = i + 1; j < buttons.Count; j++)
                Require(!ViewportBounds(buttons[i], viewports).Overlaps(ViewportBounds(buttons[j], viewports)), "按钮发生重叠：" + buttons[i].name + "/" + buttons[j].name);
        }
        static bool Contains(Rect outer, Rect inner)
        {
            return inner.xMin >= outer.xMin - 2 && inner.yMin >= outer.yMin - 2 && inner.xMax <= outer.xMax + 2 && inner.yMax <= outer.yMax + 2;
        }
        static bool HasArea(Rect rect) { return rect.width > 0 && rect.height > 0; }
        static Rect Intersect(Rect a, Rect b)
        {
            float x = Math.Max(a.xMin, b.xMin), y = Math.Max(a.yMin, b.yMin);
            return new Rect(x, y, Math.Max(0, Math.Min(a.xMax, b.xMax) - x), Math.Max(0, Math.Min(a.yMax, b.yMax) - y));
        }
        static Rect ViewportBounds(VisualElement element, IList<VisualElement> viewports)
        {
            var bounds = element.worldBound;
            for (var ancestor = element.hierarchy.parent; ancestor != null; ancestor = ancestor.hierarchy.parent)
                if (viewports.Contains(ancestor)) bounds = Intersect(bounds, ancestor.worldBound);
            return bounds;
        }
        void Capture(string name)
        {
            var texture = ScreenCapture.CaptureScreenshotAsTexture();
            try
            {
                Require(texture != null && texture.width == RequestedWidth && texture.height == RequestedHeight, "截图尺寸不匹配。");
                string file = RequestedWidth + "x" + RequestedHeight + "-" + name + ".png";
                File.WriteAllBytes(Path.Combine(output, file), texture.EncodeToPNG()); report.screenshots.Add(file);
                var pixels = texture.GetPixels32(); int dark = 255, light = 0;
                for (int i = 0; i < pixels.Length; i += Math.Max(1, pixels.Length / 65536))
                {
                    int value = (299 * pixels[i].r + 587 * pixels[i].g + 114 * pixels[i].b) / 1000;
                    dark = Math.Min(dark, value); light = Math.Max(light, value);
                }
                Require(light - dark >= 48, "截图没有有效画面。");
            }
            finally { if (texture != null) Destroy(texture); }
        }
        static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        void TraceKey(KeyDownEvent evt) { TraceInput(evt, "key=" + evt.keyCode); }
        void TraceMove(NavigationMoveEvent evt) { TraceInput(evt, "direction=" + evt.direction); }
        void TraceSubmit(NavigationSubmitEvent evt) { TraceInput(evt, "submit"); }
        void TraceCancel(NavigationCancelEvent evt) { TraceInput(evt, "cancel"); }
        void TraceInput(EventBase evt, string detail)
        {
            string device = evt.GetType().GetProperty("deviceType")?.GetValue(evt)?.ToString() ?? "无设备字段";
            string target = (evt.target as VisualElement)?.name;
            string focused = (inputTraceRoot?.focusController.focusedElement as VisualElement)?.name;
            string actual = "left=" + (keyboard?.leftArrowKey.isPressed ?? false) + ",right=" + (keyboard?.rightArrowKey.isPressed ?? false);
            File.AppendAllText(Path.Combine(output, "input-events.txt"), "frame=" + Time.frameCount + " request=" + requestedInput
                + " event=" + evt.GetType().Name + " device=" + device + " " + detail + " target=" + target
                + " focus=" + focused + " " + actual + Environment.NewLine);
        }
        void ReleaseInputTrace()
        {
            if (inputTraceRoot == null) return;
            inputTraceRoot.UnregisterCallback<KeyDownEvent>(TraceKey, TrickleDown.TrickleDown);
            inputTraceRoot.UnregisterCallback<NavigationMoveEvent>(TraceMove, TrickleDown.TrickleDown);
            inputTraceRoot.UnregisterCallback<NavigationSubmitEvent>(TraceSubmit, TrickleDown.TrickleDown);
            inputTraceRoot.UnregisterCallback<NavigationCancelEvent>(TraceCancel, TrickleDown.TrickleDown);
            inputTraceRoot = null;
        }
        void ReleaseDevices()
        {
            if (keyboard != null && keyboard.added) InputSystem.RemoveDevice(keyboard);
            if (gamepad != null && gamepad.added) InputSystem.RemoveDevice(gamepad);
            keyboard = null; gamepad = null;
            if (previousKeyboard != null && previousKeyboard.added) previousKeyboard.MakeCurrent();
            if (previousGamepad != null && previousGamepad.added) previousGamepad.MakeCurrent();
        }
        void OnDestroy() { ReleaseInputTrace(); ReleaseDevices(); ownedPortraits?.Dispose(); Application.logMessageReceived -= OnLog; }
        void Finish(string failure)
        {
            if (!running) return;
            if (failure != null && output != null && RequestedWidth > 0)
                try { Capture("failure"); } catch (Exception ex) { report.messages.Add("失败截图不可用：" + ex.Message); }
            running = false; report.failure = failure;
            if (failure != null) report.completed = false;
            ReleaseInputTrace(); ReleaseDevices();
            ExitCode = report.completed && report.errors == 0 && report.warnings == 0 ? 0 : 1;
            Application.logMessageReceived -= OnLog;
            if (ReportPath != null) File.WriteAllText(ReportPath, JsonUtility.ToJson(report, true));
            Completed = true;
            if (ExitCode == 0) Debug.Log("T134_CARD_SMOKE_PASS " + ReportPath);
            else Debug.LogError("T134_CARD_SMOKE_FAIL " + failure + " " + ReportPath);
        }
    }
}
