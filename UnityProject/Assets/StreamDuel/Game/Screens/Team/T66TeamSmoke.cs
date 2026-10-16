using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using StreamDuel.Core;
using UnityEngine;
using UnityEngine.UIElements;

namespace StreamDuel.Game
{
    // 冒烟操作使用正式页面回调，但存档和剪贴板均由内存夹具隔离。
    public sealed class T66TeamSmoke : MonoBehaviour
    {
        [Serializable]
        sealed class Report
        {
            public string unityVersion, outputDirectory, failure, finalHash;
            public string inputScope = "DropdownField / TextField 变更与 NavigationSubmitEvent 驱动真实 UI 回调；不是人工鼠标验收。完整对局从组队按钮交接后经 LocalBattleSession 的公开验收桥推进。";
            public string storageScope = "最早 SmokeRequested 分支禁止读取和写入玩家设置；常规预设和剪贴板为内存注入；坏档回归仅使用本工程 Logs/T-66 下独占文件，证据也只写此目录。";
            public string layoutScope = "两种分辨率检查文字测量、控件边界、滚动裁切和非祖先文字重叠；长队伍码允许在输入框内横向滚动。截图需独立视觉复核。";
            public bool completed;
            public int warnings, errors, turns, winner;
            public List<string> checks = new List<string>();
            public List<string> messages = new List<string>();
            public List<string> screenshots = new List<string>();
            public List<BoundsEntry> bounds = new List<BoundsEntry>();
        }

        [Serializable]
        sealed class BoundsEntry
        {
            public string name, text;
            public int width, height;
            public float x, y, w, h;
        }

        sealed class ProbeStore : ITeamPresetStore
        {
            public bool FailLoad, FailSave;
            public int Loads, Saves;
            public string Json = new TeamPresetList().ToJson();
            public TeamPresetList Load()
            {
                Loads++;
                if (FailLoad) throw new IOException("验收：预设读取失败");
                return TeamPresetList.FromJson(Json);
            }
            public void Save(TeamPresetList value)
            {
                Saves++;
                if (FailSave) throw new IOException("验收：预设保存失败");
                Json = value.ToJson();
            }
        }

        static readonly string[] RequiredPacks = { "original", "original_wave2", "original_wave3", "fan_holo_niji", "fan_asoul_vr" };
        public static bool Completed { get; private set; }
        public static int ExitCode { get; private set; }
        public static int RequestedWidth { get; private set; }
        public static int RequestedHeight { get; private set; }
        public static string ReportPath { get; private set; }

        Report report;
        ScreenRouter router;
        LocalBattleSession legacy;
        PortraitProvider portraits;
        TeamScreenArgs context;
        TeamBattleRequest requestedBattle;
        ProbeStore store;
        string clipboard = "";
        bool running;
        float deadline;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Install()
        {
            if (!Environment.GetCommandLineArgs().Contains("--t66-smoke-test")) return;
            Completed = false;
            ExitCode = 1;
            RequestedWidth = RequestedHeight = 0;
            ReportPath = null;
            var host = new GameObject("StreamDuel.T66TeamSmoke");
            DontDestroyOnLoad(host);
            host.AddComponent<T66TeamSmoke>().Configure();
        }

        public static string ReadOutputDirectory()
        {
            string[] args = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(args, "--t66-output-dir");
            if (args.Count(value => value == "--t66-smoke-test") != 1
                || args.Count(value => value == "--t66-output-dir") != 1 || index + 1 >= args.Length
                || index < 0 || !Path.IsPathFullyQualified(args[index + 1]))
                throw new ArgumentException("组队验收需要 --t66-smoke-test 与唯一的 --t66-output-dir 绝对目录。");
            string path = Path.GetFullPath(args[index + 1]);
            string project = ProjectDirectory();
            string allowed = Path.Combine(project, "Logs", "T-66");
            if (!Within(path, allowed)) throw new ArgumentException("组队验收输出必须位于当前工程 Logs/T-66 内。");
            return path;
        }

        public static string ProjectDirectory()
        {
            // Player 构建也放在本工程 Logs/T-66 中，向上只做路径运算，不搜索或读取用户目录。
            string path = Path.GetFullPath(Application.dataPath);
            while (!string.IsNullOrEmpty(path))
            {
                if (string.Equals(Path.GetFileName(path), "UnityProject", StringComparison.OrdinalIgnoreCase)) return path;
                path = Path.GetDirectoryName(path);
            }
            throw new InvalidOperationException("组队验收须在工作树 UnityProject 或其验收构建目录运行。");
        }

        public static bool Within(string path, string directory)
        {
            string root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(full, root, StringComparison.OrdinalIgnoreCase)
                || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        void Configure()
        {
            report = new Report { unityVersion = Application.unityVersion };
            running = true;
            deadline = Time.realtimeSinceStartup + 840;
            Application.logMessageReceived += OnLog;
            try
            {
                report.outputDirectory = Path.Combine(ReadOutputDirectory(), "run-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(report.outputDirectory);
                ReportPath = Path.Combine(report.outputDirectory, "result.json");
                Debug.Log("T66_TEAM_SMOKE_OUTPUT " + report.outputDirectory);
            }
            catch (Exception error) { Finish(error.ToString()); }
        }

        void Start() { if (running) StartCoroutine(Guard(Run())); }
        void LateUpdate()
        {
            if (running && Time.realtimeSinceStartup >= deadline) Finish("组队真实渲染验收超过 840 秒。");
        }

        void OnLog(string message, string stack, LogType type)
        {
            if (type == LogType.Warning) report.warnings++;
            if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert) report.errors++;
            if (type != LogType.Log) report.messages.Add(type + ": " + message + "\n" + stack);
        }

        IEnumerator Guard(IEnumerator root)
        {
            var pending = new Stack<IEnumerator>();
            pending.Push(root);
            while (running)
            {
                bool more = false;
                object current = null;
                string failure = null;
                try
                {
                    more = pending.Peek().MoveNext();
                    if (more) current = pending.Peek().Current;
                    else pending.Pop();
                }
                catch (Exception error) { failure = error.ToString(); }
                if (failure != null || pending.Count == 0) { Finish(failure); yield break; }
                if (!more) continue;
                if (current is IEnumerator nested) { pending.Push(nested); continue; }
                yield return current;
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
            if (!Application.isEditor)
            {
                float splashDeadline = Time.realtimeSinceStartup + 30;
                while (!UnityEngine.Rendering.SplashScreen.isFinished)
                {
                    Require(Time.realtimeSinceStartup < splashDeadline, "Unity 启动画面未结束。");
                    yield return null;
                }
            }
            yield return Settle();
            var routers = FindObjectsByType<ScreenRouter>(FindObjectsInactive.Include);
            Require(routers.Length == 1, "组队验收必须只有一个界面路由。");
            router = routers[0];
            legacy = router.GetComponent<LocalBattleSession>();
            Require(legacy != null && router.CurrentScreenId == ScreenIds.Menu && !legacy.enabled, "默认入口没有进入正式主菜单。");
            var saveField = typeof(GameRuntimeSettings).GetField("store", BindingFlags.Static | BindingFlags.NonPublic);
            var memoryStoreType = typeof(GameRuntimeSettings).GetNestedType("MemorySaveStore", BindingFlags.NonPublic);
            Require(GameRuntimeSettings.IsSmokeTest && saveField != null && memoryStoreType != null
                && saveField.GetValue(null)?.GetType() == memoryStoreType,
                "启动必须使用隔离的内存设置仓库；冒烟模式必须在 Awake 之前生效。");
            Require(legacy.MenuPackErrors.Count == 0, "实际内容加载出现错误。");
            foreach (string id in RequiredPacks) Require(legacy.MenuPacks.Any(pack => pack.PackId == id), "缺少验收内容包：" + id);
            portraits = new PortraitProvider(router.Resources.RegularFont);
            CheckPortraits();
            store = new ProbeStore();
            report.checks.Add("启动即使用内存设置；五个规定内容包通过实际加载器载入。系统剪贴板未读写。");

            foreach (int width in new[] { 1280, 1920 })
            {
                yield return Resize(width, width == 1280 ? 720 : 1080);
                router.Show(ScreenIds.Menu);
                yield return Settle();
                Submit("open-team");
                yield return Settle();
                Require(router.CurrentScreenId == ScreenIds.Team && !legacy.enabled, "真实菜单按钮未进入正式组队页。");
                Require(Element<VisualElement>("team-screen") != null, "正式 Team 工厂仍为占位页面。");
                Require(saveField.GetValue(null)?.GetType() == memoryStoreType,
                    "进入组队页后不再使用隔离的内存设置仓库。");

                ShowContext(legacy.MenuPacks.First(pack => pack.PackId == "original"));
                yield return Settle();
                foreach (string id in RequiredPacks)
                {
                    SelectPack(id);
                    yield return Settle();
                    ContentDb db = legacy.MenuPacks.First(pack => pack.PackId == id);
                    Require(Element<Label>("team-pack-notice").text == db.PackNotice, "内容包声明没有原样更新：" + id);
                    Require(Element<DropdownField>("team-season").choices.Count == db.Seasons.Count + 1, "赛季列表与包内定义不一致。");
                    Submit("team-clear");
                    Require(Picked().Count == 0 && !Element<Button>("team-start").enabledInHierarchy, "空队伍可以开始对战。");
                    Require(!string.IsNullOrWhiteSpace(Element<Label>("team-errors").text), "人数不足时没有 TeamCheck 错误。");
                    string[] ids = db.Streamers.Take(6).Select(member => member.Id).ToArray();
                    foreach (string member in ids) Submit("team-pick-" + member);
                    Require(Picked().SequenceEqual(ids) && Element<Button>("team-start").enabledInHierarchy, "六个号位未按实际点击顺序选入。");
                    Submit("team-pick-" + ids[0]);
                    Require(Picked().Count == 5 && !Element<Button>("team-start").enabledInHierarchy, "重复点击没有移除成员或仍允许五人开战。");
                    Submit("team-pick-" + ids[0]);
                    Require(Picked().Count == 6 && Picked().Distinct().Count() == 6, "重复点击产生重复成员。");
                    Submit("team-auto");
                    Require(TeamCheck.Validate(db, null, Picked()).Count == 0, "自动组队没有生成合法六人队伍。");
                    Submit("team-auto");
                    Require(TeamCheck.Validate(db, null, Picked()).Count == 0, "重复自动组队破坏了合法性。");
                    foreach (var member in db.Streamers)
                    {
                        Submit("streamer-card-" + member.Id);
                        yield return Settle();
                        CheckDetail(db, member);
                    }
                    Element<ScrollView>("team-roster").scrollOffset = Vector2.zero;
                    yield return Settle();
                    ValidateLayout();
                    Capture("team-" + id);
                    yield return ScrollEvidence(id);
                    report.checks.Add(Size + "：" + id + " 的声明、六人选择/移除、自动组队、全部主播五技能及判定时机/驱散说明通过。");
                }
                SelectPack("original");
                yield return Settle();
                yield return CheckSeasonAndPersistence();
                yield return CheckInvalidSelection();
                yield return T66ReviewChecks.RunRulesets(router, legacy, Capture);
                report.checks.Add(Size + "：三个显式赛制经真实组队按钮交接；原六人、赛季、禁用、首发、修饰器、种子、多局比分、出招和补位超时及逐局回放通过；默认旧单局断言保留。");
                yield return T66ReviewChecks.RunLabels(router, legacy);
                yield return Settle();
                ValidateLayout();
                Capture("review-label-selection");
                report.checks.Add(Size + "：最终显示标签冲突的第二包、第二赛季均经真实 UI 正确选中，Core 校验与开战参数一致。");
            }
            yield return CheckPolicy();
            yield return CheckStorageFailures();
            yield return T66ReviewChecks.RunStorage(router, legacy, report.outputDirectory);
            report.checks.Add("真实预设文件：坏 JSON/坏 UTF-8 禁写且原字节和 mtime 保留；修复并显式刷新后恢复；UTF-8 BOM 兼容。");
            yield return CheckRememberFailureRefreshesSelection();
            yield return CheckReturnPreservesSettingsAndSeason();
            yield return CompleteBattle();
            report.completed = true;
        }

        void ShowContext(ContentDb db, TeamPreset selection = null, IReadOnlyList<ContentDb> packs = null, ProbeStore storage = null,
            bool liveBattle = false, Action<TeamScreenArgs> configure = null)
        {
            context = new TeamScreenArgs
            {
                Content = db, Packs = packs ?? legacy.MenuPacks, Selection = selection,
                Settings = new GameSettings { DefaultPack = db.PackId, AiLevel = AiLevel.Normal, FastPlay = true, TurnTimer = false },
                Seed = 660066, Presets = storage ?? store, Portraits = portraits,
                ReadClipboard = () => clipboard, WriteClipboard = value => clipboard = value,
                StartBattle = liveBattle ? new Action<TeamBattleRequest>(legacy.StartTeamBattle) : value => requestedBattle = value,
                NavigateToBattle = liveBattle,
                Return = () => router.Show(ScreenIds.Menu)
            };
            configure?.Invoke(context);
            router.Show(ScreenIds.Team, context);
        }

        void SelectPack(string id)
        {
            var dropdown = Element<DropdownField>("team-pack");
            int index = -1;
            for (int i = 0; i < context.Packs.Count; i++) if (context.Packs[i].PackId == id) index = i;
            Require(index >= 0 && dropdown.choices.Count == context.Packs.Count, "内容包下拉与实际可用列表不一致。");
            dropdown.index = index;
        }

        List<string> Picked()
        {
            var selected = new List<string>();
            for (int i = 0; i < 6; i++)
            {
                var slot = Element<Button>("team-slot-" + i);
                if (slot.userData is string id && !string.IsNullOrEmpty(id)) selected.Add(id);
            }
            return selected;
        }

        void CheckDetail(ContentDb db, StreamerDef member)
        {
            string stats = Element<Label>("streamer-stats-" + member.Id).text;
            foreach (string stat in new[] { "hp", "atk", "def", "spa", "sdef", "spd" })
                Require(stats.Contains(db.StatName(stat) + " " + member.Stats[stat]), "主播卡遗漏六维：" + member.Id + "/" + stat);
            Require(Element<Label>("streamer-blurb-" + member.Id).text == member.Blurb, "主播卡简介与内容包不一致：" + member.Id);
            Require(Element<Label>("team-detail-name").text.Contains(member.Name), "详情没有切换到被点击的主播。");
            string detail = Element<Label>("team-detail-text").text;
            foreach (string skillId in member.Skills.Concat(new[] { member.Fifth }))
            {
                var skill = db.Skills[skillId];
                Require(detail.Contains(skill.Name), "详情遗漏技能：" + skill.Name);
                foreach (var effect in skill.Effects)
                {
                    var text = Describe.Effect(db, effect);
                    Require(detail.Contains(text.At) && detail.Contains(text.Text), "详情遗漏效果或判定时机：" + skill.Name);
                    if (!string.IsNullOrEmpty(text.Dispel)) Require(detail.Contains(text.Dispel), "详情遗漏驱散说明：" + skill.Name);
                }
            }
            Require(detail.Contains("PP") && detail.Contains("命中") && detail.Contains("先制"), "技能参数说明不完整。");
        }

        IEnumerator CheckSeasonAndPersistence()
        {
            var db = legacy.MenuPacks.First(pack => pack.PackId == "original");
            var season = Element<DropdownField>("team-season");
            Require(db.Seasons.ContainsKey("s1") && season.choices.Count == 2, "缺少第一赛季验收夹具。");
            Submit("team-clear");
            foreach (string id in new[] { "enka", "shiori", "lumi", "bell", "hane", "kei" }) Submit("team-pick-" + id);
            season.index = 1;
            Require(!Element<Button>("team-start").enabledInHierarchy, "包含赛季禁用主播仍能开战。");
            foreach (string error in TeamCheck.Validate(db, "s1", Picked())) Require(Element<Label>("team-errors").text.Contains(error), "未显示 Core 的赛季错误。");
            Submit("team-auto");
            Require(TeamCheck.Validate(db, "s1", Picked()).Count == 0, "赛季自动组队不合法。");
            string[] selected = Picked().ToArray();
            Element<TextField>("team-preset-name").value = "组队验收预设";
            Submit("team-preset-save");
            Require(store.Saves > 0 && TeamPresetList.FromJson(store.Json).Items.Any(item => item.Ids.SequenceEqual(selected)), "预设按钮没有保存当前队伍。");
            Submit("team-clear");
            var presets = Element<DropdownField>("team-presets");
            Require(presets.choices.Count > 0, "保存后的预设没有进入下拉列表。");
            presets.index = presets.choices.Count - 1;
            Submit("team-preset-load");
            Require(Picked().SequenceEqual(selected), "预设读取没有恢复成员与顺序。");
            Submit("team-code-copy");
            string validCode = clipboard;
            Require(TeamCode.Decode(validCode, db).Ids.SequenceEqual(selected), "复制按钮生成的队伍码不匹配。");
            Submit("team-clear");
            Submit("team-code-paste");
            Require(Picked().SequenceEqual(selected), "粘贴按钮没有恢复队伍码。");
            clipboard = "SDT1-损坏的验收码";
            Submit("team-code-paste");
            Require(Picked().SequenceEqual(selected) && !string.IsNullOrWhiteSpace(Element<Label>("team-status").text), "损坏队伍码改变了已确认队伍或没有错误反馈。");
            clipboard = validCode;
            yield return Settle();
            ValidateLayout();
            Capture("season-presets");
            report.checks.Add(Size + "：赛季禁用、赛季自动组队、预设保存/读取、有效队伍码与损坏码保持原队伍通过。");
            var fan = legacy.MenuPacks.First(pack => pack.PackId == "fan_holo_niji");
            string[] fanIds = fan.Streamers.Take(6).Select(member => member.Id).ToArray();
            clipboard = TeamCode.Encode(new TeamPreset { Name = "跨包队伍码", PackId = fan.PackId, Ids = fanIds.ToList() });
            Submit("team-code-paste");
            Require(Picked().SequenceEqual(fanIds) && Element<Label>("team-pack-notice").text == fan.PackNotice
                && Element<DropdownField>("team-season").index == 0, "跨包队伍码没有原子切换队伍、声明和赛季。");
            yield return Settle();
            ValidateLayout();
            report.checks.Add(Size + "：跨包队伍码经验证后切换内容包，显示正确同人声明并重置为不限赛季。");
        }

        IEnumerator CheckInvalidSelection()
        {
            var db = legacy.MenuPacks.First(pack => pack.PackId == "original");
            var duplicate = new TeamPreset { Name = "重复夹具", PackId = db.PackId,
                Ids = new List<string> { "enka", "enka", "lumi", "bell", "hane", "momo" } };
            ShowContext(db, duplicate);
            yield return Settle();
            Require(!Element<Button>("team-start").enabledInHierarchy, "重复成员的注入预设仍能开战。");
            foreach (string error in duplicate.Check(db, null)) Require(Element<Label>("team-errors").text.Contains(error), "重复成员错误未从 TeamCheck 传到界面。");
            Submit("team-auto");
            Require(TeamCheck.Validate(db, null, Picked()).Count == 0, "非法预设后自动组队无法恢复。");
            Submit("back-button");
            yield return Settle();
            Require(router.CurrentScreenId == ScreenIds.Menu, "组队返回回调未执行。");
            report.checks.Add(Size + "：重复预设被拒绝，自动组队恢复合法状态，返回按钮回到主菜单。");
        }

        IEnumerator CheckPolicy()
        {
            var rules = Resources.Load<TextAsset>("StreamDuelRules");
            var raw = Resources.Load<TextAsset>("Packs/original");
            Require(rules != null && raw != null, "缺少下架门禁验收资源。");
            var policy = PackPolicy.FromJson("{\"disabledStreamers\":[\"bell\"],\"reason\":{\"bell\":\"验收下架\"}}");
            var filtered = ContentDb.FromJson(rules.text, raw.text, policy);
            var previous = new TeamPreset { Name = "旧预设", PackId = "original",
                Ids = new List<string> { "enka", "shiori", "lumi", "bell", "hane", "momo" } };
            ShowContext(filtered, previous, new[] { filtered });
            yield return Settle();
            Require(router.Document.rootVisualElement.Q("streamer-card-bell") == null, "下架主播仍显示在选择网格。");
            Require(!Element<Button>("team-start").enabledInHierarchy && Element<Label>("team-errors").text.Contains("该主播已下架"), "保存过的下架阵容绕过开战门禁。");
            Submit("team-auto");
            string[] valid = Picked().ToArray();
            Require(!valid.Contains("bell") && TeamCheck.Validate(filtered, null, valid).Count == 0, "自动队伍包含下架成员。");
            clipboard = TeamCode.Encode(previous);
            Submit("team-code-paste");
            Require(Picked().SequenceEqual(valid) && Element<Label>("team-status").text.Contains("该主播已下架"), "旧队伍码绕过下架门禁或破坏当前合法队伍。");
            yield return Settle();
            ValidateLayout();
            Capture("policy-guard");
            report.checks.Add("真实 PackPolicy 过滤后网格隐藏下架主播，旧预设和队伍码不可绕过，自动组队只选可用成员。");
        }

        IEnumerator CheckStorageFailures()
        {
            var db = legacy.MenuPacks.First(pack => pack.PackId == "original");
            var existing = new TeamPreset { Name = "既有预设", PackId = db.PackId,
                Ids = db.Streamers.Take(6).Select(member => member.Id).ToList() };
            var preloaded = new ProbeStore { Json = new TeamPresetList { Items = new List<TeamPreset> { existing } }.ToJson() };
            ShowContext(db, storage: preloaded);
            yield return Settle();
            Require(Element<DropdownField>("team-presets").choices.Count == 1, "既有 Core 格式预设没有被读取。");
            Submit("team-preset-load");
            Require(Picked().SequenceEqual(existing.Ids) && Element<TextField>("team-preset-name").value == existing.Name,
                "既有预设的名称或成员顺序不兼容。");
            var failing = new ProbeStore { FailLoad = true };
            ShowContext(db, storage: failing);
            yield return Settle();
            Require(failing.Loads > 0 && !string.IsNullOrWhiteSpace(Element<Label>("team-storage-status").text), "预设读取失败没有可见提示。");
            Require(!Element<Button>("team-preset-save").enabledInHierarchy, "未读到原预设时仍允许覆盖保存。");
            failing.FailLoad = false;
            failing.FailSave = true;
            ShowContext(db, storage: failing);
            yield return Settle();
            Submit("team-auto");
            string[] selected = Picked().ToArray();
            Element<TextField>("team-preset-name").value = "失败后重试";
            Submit("team-preset-save");
            Require(Picked().SequenceEqual(selected) && !string.IsNullOrWhiteSpace(Element<Label>("team-storage-status").text), "保存失败丢失了内存阵容或没有提示。");
            failing.FailSave = false;
            Submit("team-preset-save");
            Require(TeamPresetList.FromJson(failing.Json).Items.Any(item => item.Ids.SequenceEqual(selected)), "保存失败后无法正常重试。");
            Require(string.IsNullOrEmpty(Element<Label>("team-storage-status").text), "保存重试成功后仍显示存储错误。");
            yield return Settle();
            report.checks.Add("预设读取/保存故障有可见提示，失败保留内存阵容，后续保存可重试。");
        }

        IEnumerator CheckRememberFailureRefreshesSelection()
        {
            var db = legacy.MenuPacks.First(pack => pack.PackId == "original");
            int selectionWrites = 0, viewWrites = 0;
            ShowContext(db, configure: args =>
            {
                args.Remember = (preset, settings) =>
                {
                    selectionWrites++;
                    throw new IOException("验收：选择未持久化");
                };
                args.RememberView = (preset, season, settings) =>
                {
                    viewWrites++;
                    throw new IOException("验收：设置未持久化");
                };
            });
            yield return Settle();
            Submit("team-pick-enka");
            Require(Picked().SequenceEqual(new[] { "enka" }) && !Element<Button>("team-start").enabledInHierarchy,
                "保存选择回调失败阻止了选人或刷新开战状态。");
            Require(Element<Label>("team-storage-status").text.Contains("验收：选择未持久化"), "选人保存失败没有持续的独立提示。");
            Submit("team-auto");
            string[] selected = Picked().ToArray();
            Require(TeamCheck.Validate(db, null, selected).Count == 0 && Element<Button>("team-start").enabledInHierarchy,
                "保存选择回调失败阻止了自动组队刷新。");
            Require(Element<Label>("team-storage-status").text.Contains("验收：选择未持久化"), "自动组队成功反馈覆盖了保存失败提示。");
            Submit("team-slot-0");
            Require(Picked().SequenceEqual(selected.Skip(1)) && !Element<Button>("team-start").enabledInHierarchy,
                "保存选择回调失败阻止了队伍位移除与错误刷新。");
            Require(Element<Label>("team-storage-status").text.Contains("验收：选择未持久化"), "移除队伍位后保存错误提示丢失。");
            Element<DropdownField>("team-level").index = (int)AiLevel.Hard;
            Require(Element<DropdownField>("team-level").index == (int)AiLevel.Hard && viewWrites > 0
                && Element<Label>("team-storage-status").text.Contains("验收：设置未持久化"),
                "保存视图回调失败撤回了内存设置或被普通反馈覆盖。");
            Require(selectionWrites >= 3, "选人、自动组队、移除没有经过真实保存回调。");
            yield return Settle();
            ValidateLayout();
            Capture("remember-failure");
            report.checks.Add("Remember/RememberView 抛出存储故障后，选人、自动组队、队伍位移除和难度仍更新；独立错误提示不被成功操作反馈覆盖。");
        }

        IEnumerator CheckReturnPreservesSettingsAndSeason()
        {
            router.Show(ScreenIds.Menu);
            yield return Settle();
            Submit("open-team");
            yield return Settle();
            int originalIndex = Array.FindIndex(legacy.MenuPacks.ToArray(), pack => pack.PackId == "original");
            int fanIndex = Array.FindIndex(legacy.MenuPacks.ToArray(), pack => pack.PackId == "fan_holo_niji");
            Require(originalIndex >= 0 && fanIndex >= 0, "返回设置验收缺少内容包。");
            Element<DropdownField>("team-pack").index = originalIndex;
            Element<DropdownField>("team-season").index = 1;
            Element<DropdownField>("team-level").index = (int)AiLevel.Hard;
            Submit("team-auto");
            string[] selected = Picked().ToArray();
            Element<TextField>("team-preset-name").value = "返回保留的队伍";
            Submit("back-button");
            yield return Settle();
            Require(router.CurrentScreenId == ScreenIds.Menu, "返回主菜单失败。");
            Submit("open-team");
            yield return Settle();
            Require(Element<DropdownField>("team-pack").index == originalIndex
                && Element<DropdownField>("team-season").index == 1
                && Element<DropdownField>("team-level").index == (int)AiLevel.Hard
                && Picked().SequenceEqual(selected)
                && Element<TextField>("team-preset-name").value == "返回保留的队伍",
                "经正式菜单返回组队后丢失内容包、赛季、难度、队伍顺序或名称。");
            Element<DropdownField>("team-pack").index = fanIndex;
            Require(Element<DropdownField>("team-season").index == 0, "新内容包错误继承了其它包的赛季。");
            Element<DropdownField>("team-pack").index = originalIndex;
            Require(Element<DropdownField>("team-season").index == 1 && Picked().SequenceEqual(selected),
                "切回原包后没有恢复它自己的赛季与队伍。");
            Element<DropdownField>("team-pack").index = fanIndex;
            Submit("back-button");
            yield return Settle();
            Submit("open-team");
            yield return Settle();
            Require(Element<DropdownField>("team-pack").index == fanIndex
                && Element<DropdownField>("team-level").index == (int)AiLevel.Hard,
                "返回菜单后没有保留最后内容包或电脑难度。");
            Element<DropdownField>("team-pack").index = originalIndex;
            Require(Element<DropdownField>("team-season").index == 1 && Picked().SequenceEqual(selected),
                "重建页面后 LoadSeason 没有恢复原包赛季与队伍。");
            yield return Settle();
            ValidateLayout();
            Capture("returned-settings");
            report.checks.Add("正式菜单往返保留队伍名/顺序、电脑难度、内容包及各包自己的赛季；页面重建后的 LoadSeason 路径通过。");
        }

        IEnumerator CompleteBattle()
        {
            var db = legacy.MenuPacks.First(pack => pack.PackId == "original");
            var levels = new[] { AiLevel.Easy, AiLevel.Hard, AiLevel.Expert, AiLevel.Normal };
            for (int index = 0; index < levels.Length; index++)
            {
                ShowContext(db, liveBattle: true);
                yield return Settle();
                Submit("team-auto");
                string[] selected = Picked().ToArray();
                Element<DropdownField>("team-level").index = (int)levels[index];
                Submit("team-start");
                yield return Settle();
                requestedBattle = (router.CurrentArgs as BattleScreenArgs)?.OriginalRequest;
                Require(requestedBattle != null && requestedBattle.Settings.AiLevel == levels[index], "电脑难度没有通过开战按钮传给 Battle 参数。");
                Require(requestedBattle.Team.Check(db, requestedBattle.SeasonId).Count == 0 && requestedBattle.Content == db,
                    "开战交接的阵容或内容包不合法。");
                var current = legacy.CurrentTeamBattle;
                Require(current != null && legacy.enabled && router.CurrentScreenId == ScreenIds.Battle,
                    "实际组队按钮没有交接到可玩的对战页面。");
                Require(legacy.CurrentTeamAiLevel == levels[index], "对战桥没有使用组队页选择的电脑难度。");
                Require(current.Sides[0].Team.Select(fighter => fighter.Def.Id).SequenceEqual(selected), "实际对局的玩家阵容与 UI 请求不同。");
                Require(current.Phase == Phase.Lead, "正式对战没有进入首发选择。");
                if (levels[index] == AiLevel.Normal) Capture("battle-start");
                legacy.AdvanceTeamSmoke();
                Require(current.Phase == Phase.Choose, "首发确认后没有进入可行动阶段。");
                legacy.AdvanceTeamSmoke();
                var expected = current.Clone();
                BattleAction expectedMine = Ai.Choose(expected, 0, levels[index]);
                BattleAction expectedFoe = Ai.Choose(expected, 1, levels[index]);
                expected.ResolveTurn(expectedMine, expectedFoe);
                int oldEvents = current.Record.Events.Count;
                legacy.AdvanceTeamSmoke();
                var actualTurn = current.Record.Events.Skip(oldEvents).Single(entry => entry.Type == BattleRecordEventType.Turn);
                var expectedTurn = expected.Record.Events.Last(entry => entry.Type == BattleRecordEventType.Turn);
                Require(actualTurn.A0.ToString() == expectedMine.ToString() && actualTurn.A1.ToString() == expectedFoe.ToString(),
                    "正式桥接出招与相同状态所选难度的 Core AI 不同：" + levels[index]);
                Require(actualTurn.RngState == expectedTurn.RngState && current.StateHash() == expected.StateHash(),
                    "所选难度的正式出招改变了随机消费或结算状态：" + levels[index]);
                Require(current.Clone().Random() == expected.Clone().Random(), "正式出招后随机流不一致：" + levels[index]);
                report.checks.Add("实际 " + levels[index] + " 首回合双方动作、记录随机位置、状态哈希和后续随机值均与同状态 Core AI 克隆对照一致。");
            }
            var battle = legacy.CurrentTeamBattle;
            bool captured = false;
            int steps = 0;
            while (battle.Phase != Phase.Over)
            {
                Require(++steps <= 5000, "通过正式桥接推进的对局未正常结束。");
                legacy.AdvanceTeamSmoke();
                yield return null;
                if (!captured && battle.Turn >= 2)
                {
                    yield return new WaitForEndOfFrame();
                    Capture("battle-playing");
                    captured = true;
                }
            }
            legacy.AdvanceTeamSmoke();
            yield return Settle();
            Require(captured && battle.Log.Count > 0 && battle.Record.Events.Count > 0, "缺少完整对局日志或中途画面。");
            yield return T66ReviewChecks.AwaitBattleFinal(router, legacy);
            Capture("battle-finished");
            File.WriteAllText(Path.Combine(report.outputDirectory, "battle-record.json"), battle.Record.ToJson());
            var replayed = Replay.Run(db, BattleRecord.FromJson(battle.Record.ToJson()));
            Require(replayed.StateHash() == battle.StateHash() && replayed.Log.Select(entry => entry.Text).SequenceEqual(battle.Log.Select(entry => entry.Text)),
                "经真实 UI 交接产生的整局记录无法复现状态和日志。");
            report.turns = battle.Turn;
            report.winner = battle.Winner;
            report.finalHash = battle.StateHash().ToString("x16");
            report.checks.Add("四档电脑难度经真实开战按钮传递；普通难度实际桥接进入对战，按既有提交/补位接口自动完成整局并保存原始记录；终局截图前由正式页面完成旧播放，核对终局弹层、回合、阶段、胜者与比分和会话一致，状态与录像不变。");
        }

        void CheckPortraits()
        {
            foreach (string packId in RequiredPacks)
            {
                var db = legacy.MenuPacks.First(pack => pack.PackId == packId);
                foreach (var member in db.Streamers)
                {
                    var texture = portraits.Get(db, member.Id);
                    Require(texture != null && texture.width > 0 && texture.height > 0, "头像为空：" + member.Id);
                    Require(ReferenceEquals(texture, portraits.Get(db, member.Id)), "相同头像请求没有使用缓存：" + member.Id);
                }
            }
            report.checks.Add("五包全部主播头像非空且相同请求返回同一缓存纹理；同一 provider 用于实际组队网格。");
        }

        IEnumerator Resize(int width, int height)
        {
            RequestedWidth = width;
            RequestedHeight = height;
            Screen.SetResolution(width, height, FullScreenMode.Windowed);
            float until = Time.realtimeSinceStartup + 20;
            while (Screen.width != width || Screen.height != height)
            {
                Require(Time.realtimeSinceStartup < until, "分辨率切换超时；Editor 需要 T66SmokeEditor 桥。");
                yield return null;
            }
            yield return Settle();
        }

        IEnumerator ScrollEvidence(string suffix)
        {
            var scrolls = new List<ScrollView>();
            Element<VisualElement>("team-screen").Query<ScrollView>().ForEach(scroll => scrolls.Add(scroll));
            foreach (var scroll in scrolls) scroll.scrollOffset = new Vector2(0, scroll.verticalScroller.highValue);
            yield return Settle();
            ValidateLayout();
            Capture("team-" + suffix + "-scrolled");
            foreach (var scroll in scrolls) scroll.scrollOffset = Vector2.zero;
            yield return Settle();
        }

        void ValidateLayout()
        {
            var page = Element<VisualElement>("team-screen");
            var texts = new List<TextElement>();
            page.Query<TextElement>().ForEach(text => { if (Visible(text) && !string.IsNullOrEmpty(text.text)) texts.Add(text); });
            Rect screen = router.Document.rootVisualElement.worldBound;
            var visible = new List<TextElement>();
            var clipped = new List<Rect>();
            foreach (var text in texts)
            {
                Rect rect = text.worldBound, clip = screen;
                bool longCode = InsideNamed(text, "team-code") && text != Element<TextField>("team-code").labelElement;
                bool scrolled = false;
                for (var ancestor = text.parent; ancestor != null; ancestor = ancestor.parent)
                    if (ancestor is ScrollView scroll)
                    {
                        clip = Intersection(clip, scroll.contentViewport.worldBound);
                        scrolled = true;
                    }
                Rect seen = Intersection(rect, clip);
                if (seen.width <= 0 || seen.height <= 0) continue;
                Require(Finite(rect.x) && Finite(rect.y) && Finite(rect.width) && Finite(rect.height) && rect.width > 0 && rect.height > 0,
                    "文字节点布局无效：" + text.name);
                Require(longCode || (scrolled ? rect.xMin >= clip.xMin - 2 && rect.xMax <= clip.xMax + 2 : Contains(screen, rect)),
                    "控件超出可读宽度：" + text.name + " " + text.text);
                if (!longCode)
                {
                    Vector2 measured = text.MeasureTextSize(text.text, text.contentRect.width,
                        text.resolvedStyle.whiteSpace == WhiteSpace.Normal ? VisualElement.MeasureMode.Exactly : VisualElement.MeasureMode.Undefined,
                        0, VisualElement.MeasureMode.Undefined);
                    Require(!text.isElided && measured.x <= text.contentRect.width + 2 && measured.y <= text.contentRect.height + 2,
                        "文字可能裁切：" + text.name + " measured=" + measured + " content=" + text.contentRect + " " + text.text);
                }
                report.bounds.Add(new BoundsEntry { name = text.name, text = text.text, width = RequestedWidth, height = RequestedHeight,
                    x = rect.x, y = rect.y, w = rect.width, h = rect.height });
                visible.Add(text);
                clipped.Add(seen);
            }
            Require(visible.Count > 0, "组队页面没有可见文本。");
            for (int i = 0; i < visible.Count; i++)
                for (int j = i + 1; j < visible.Count; j++)
                {
                    if (visible[i].Contains(visible[j]) || visible[j].Contains(visible[i])) continue;
                    Rect overlap = Intersection(clipped[i], clipped[j]);
                    Require(overlap.width <= 1 || overlap.height <= 1,
                        "可见文字重叠：" + visible[i].name + " / " + visible[j].name + " " + visible[i].text + " / " + visible[j].text);
                }
            foreach (string name in new[] { "team-pack", "team-season", "team-level", "team-ruleset", "team-preset-name", "team-code", "team-presets" })
            {
                var field = Element<VisualElement>(name);
                Require(field.worldBound.width > 8 && field.worldBound.height > 8, "输入控件没有可用宽高：" + name);
            }
            foreach (string name in new[] { "team-pack-notice", "team-start", "team-auto", "team-clear", "back-button" }
                .Concat(Enumerable.Range(0, 6).Select(index => "team-slot-" + index)))
            {
                var pinned = Element<VisualElement>(name);
                Require(Visible(pinned) && pinned.worldBound.width > 2 && pinned.worldBound.height > 2 && Contains(screen, pinned.worldBound),
                    "常驻声明、队伍位或按钮离开可见区域：" + name);
            }
        }

        T Element<T>(string name) where T : VisualElement
        {
            var element = router.Document.rootVisualElement.Q<T>(name);
            Require(element != null, "缺少真实 UI 节点：" + name);
            return element;
        }

        void Submit(string name)
        {
            var button = Element<Button>(name);
            Require(button.enabledInHierarchy && button.visible, "不能提交不可用按钮：" + name);
            button.Focus();
            using (var evt = NavigationSubmitEvent.GetPooled()) { evt.target = button; button.SendEvent(evt); }
        }

        static bool Visible(VisualElement element)
        {
            for (var current = element; current != null; current = current.parent)
                if (!current.visible || current.resolvedStyle.display == DisplayStyle.None) return false;
            return true;
        }
        static bool InsideNamed(VisualElement element, string name)
        {
            for (var current = element; current != null; current = current.parent) if (current.name == name) return true;
            return false;
        }
        static Rect Intersection(Rect first, Rect second) => Rect.MinMaxRect(Mathf.Max(first.xMin, second.xMin), Mathf.Max(first.yMin, second.yMin), Mathf.Min(first.xMax, second.xMax), Mathf.Min(first.yMax, second.yMax));
        static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        static bool Contains(Rect outer, Rect inner) => inner.xMin >= outer.xMin - 2 && inner.yMin >= outer.yMin - 2 && inner.xMax <= outer.xMax + 2 && inner.yMax <= outer.yMax + 2;
        static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        string Size => RequestedWidth + "×" + RequestedHeight;

        void Capture(string name)
        {
            Texture2D texture = null;
            try
            {
                texture = ScreenCapture.CaptureScreenshotAsTexture();
                Require(texture != null && texture.width == RequestedWidth && texture.height == RequestedHeight, "截图分辨率不符。");
                byte[] png = texture.EncodeToPNG();
                Require(png != null && png.Length > 8, "截图编码失败。");
                string file = RequestedWidth + "x" + RequestedHeight + "-" + name + ".png";
                string path = Path.Combine(report.outputDirectory, file);
                File.WriteAllBytes(path, png);
                Require(new FileInfo(path).Length == png.Length, "截图没有完整写入。");
                var pixels = texture.GetPixels32();
                int step = Math.Max(1, pixels.Length / 65536), low = 255, high = 0, bright = 0;
                for (int index = 0; index < pixels.Length; index += step)
                {
                    var pixel = pixels[index];
                    int luminance = (299 * pixel.r + 587 * pixel.g + 114 * pixel.b) / 1000;
                    low = Math.Min(low, luminance); high = Math.Max(high, luminance);
                    if (luminance >= 150) bright++;
                }
                Require(high - low >= 48 && bright >= 8, "截图没有有效渲染内容：" + file);
                report.screenshots.Add(file);
            }
            finally { if (texture != null) Destroy(texture); }
        }

        void OnDestroy()
        {
            Application.logMessageReceived -= OnLog;
            portraits?.Dispose();
            portraits = null;
        }

        void Finish(string failure)
        {
            if (!running) return;
            running = false;
            try
            {
                report.failure = failure;
                if (failure != null) report.completed = false;
                ExitCode = report.completed && report.errors == 0 && report.warnings == 0 ? 0 : 1;
                if (ReportPath != null) File.WriteAllText(ReportPath, JsonUtility.ToJson(report, true));
                else ExitCode = 1;
            }
            catch (Exception error) { ExitCode = 1; Debug.LogError("组队验收报告写入失败：" + error); }
            finally
            {
                Application.logMessageReceived -= OnLog;
                Completed = true;
                if (ExitCode == 0) Debug.Log("T66_TEAM_SMOKE_PASS " + ReportPath);
                else Debug.LogError("T66_TEAM_SMOKE_FAIL " + (failure ?? "检测到警告或错误") + " " + ReportPath);
                if (!Application.isEditor) Application.Quit(ExitCode);
            }
        }
    }
}
