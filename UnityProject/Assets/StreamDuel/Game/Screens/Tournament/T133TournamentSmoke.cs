using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using StreamDuel.Core;
using UnityEngine;
using UnityEngine.UIElements;

namespace StreamDuel.Game
{
    public sealed class T133TournamentSmoke : MonoBehaviour
    {
        [Serializable]
        sealed class Report
        {
            public string unityVersion, outputDirectory, failure;
            public string inputScope = "正式 UI 字段变更和 NavigationSubmitEvent；玩家整局由旧对战页公开验收桥推进，不声称人工鼠标验收。";
            public string storageScope = "最早 SmokeRequested 隔离玩家设置；赛事、坏档和录像仅写本轮 Logs/T-133，预设和剪贴板使用注入内存。";
            public string layoutScope = "检查两尺寸文字测量、屏幕边界、滚动裁切和非祖先文本重叠；截图仍需独立视觉复核。";
            public bool completed;
            public int warnings, errors, created, automatedGames, playerGames, naturalDraws, manuallyReported;
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

        public static bool Completed { get; private set; }
        public static int ExitCode { get; private set; }
        public static int RequestedWidth { get; private set; }
        public static int RequestedHeight { get; private set; }
        public static string ReportPath { get; private set; }

        Report report;
        ScreenRouter router;
        BattleScreen legacy;
        ContentDb db;
        MemoryTeamPresetStore presets;
        TeamPreset preset;
        string clipboard = "", storageDirectory;
        bool running;
        float deadline;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Install()
        {
            if (!Environment.GetCommandLineArgs().Contains("--t133-smoke-test")) return;
            Completed = false;
            ExitCode = 1;
            RequestedWidth = RequestedHeight = 0;
            ReportPath = null;
            var host = new GameObject("StreamDuel.T133TournamentSmoke");
            DontDestroyOnLoad(host);
            host.AddComponent<T133TournamentSmoke>().Configure();
        }

        public static string ReadOutputDirectory()
        {
            string[] args = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(args, "--t133-output-dir");
            if (args.Count(value => value == "--t133-smoke-test") != 1
                || args.Count(value => value == "--t133-output-dir") != 1 || index < 0
                || index + 1 >= args.Length || !Path.IsPathFullyQualified(args[index + 1]))
                throw new ArgumentException("赛事验收需要 --t133-smoke-test 与唯一的 --t133-output-dir 绝对目录。");
            string path = Path.GetFullPath(args[index + 1]);
            if (!Within(path, Path.Combine(ProjectDirectory(), "Logs", "T-133")))
                throw new ArgumentException("赛事验收输出必须位于当前工程 Logs/T-133 内。");
            return path;
        }

        public static string ProjectDirectory()
        {
            // Player 也必须构建在本工程 Logs/T-133 下；向上只算路径，不搜索用户目录。
            string path = Path.GetFullPath(Application.dataPath);
            while (!string.IsNullOrEmpty(path))
            {
                if (string.Equals(Path.GetFileName(path), "UnityProject", StringComparison.OrdinalIgnoreCase)) return path;
                path = Path.GetDirectoryName(path);
            }
            throw new InvalidOperationException("赛事验收须在工作树 UnityProject 或其验收构建目录运行。");
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
                Debug.Log("T133_TOURNAMENT_SMOKE_OUTPUT " + report.outputDirectory);
            }
            catch (Exception error) { Finish(error.ToString()); }
        }

        void Start() { if (running) StartCoroutine(Guard(Run())); }
        void LateUpdate()
        {
            if (running && Time.realtimeSinceStartup >= deadline) Finish("赛事真实渲染验收超过 840 秒。");
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
                float until = Time.realtimeSinceStartup + 30;
                while (!UnityEngine.Rendering.SplashScreen.isFinished)
                {
                    Require(Time.realtimeSinceStartup < until, "Unity 启动画面未结束。");
                    yield return null;
                }
            }
            yield return Settle();
            var routers = FindObjectsByType<ScreenRouter>(FindObjectsInactive.Include);
            Require(routers.Length == 1, "赛事验收必须只有一个界面路由。");
            router = routers[0];
            legacy = router.GetComponent<BattleScreen>();
            Require(legacy != null && router.CurrentScreenId == ScreenIds.Menu && !legacy.enabled, "默认入口未进入正式主菜单。");
            var saveField = typeof(BattleScreen).GetField("saveStore", BindingFlags.Instance | BindingFlags.NonPublic);
            Require(saveField != null && saveField.GetValue(legacy) == null, "验收启动读取了玩家设置。");
            Require(legacy.MenuPackErrors.Count == 0, "实际内容加载出现错误。");
            db = legacy.MenuPacks.First(pack => pack.PackId == "original");
            presets = new MemoryTeamPresetStore();
            preset = new TeamPreset { Name = "赛事验收预设", PackId = db.PackId,
                Ids = db.Streamers.Take(6).Select(member => member.Id).ToList() };
            presets.Save(new TeamPresetList { Items = new List<TeamPreset> { preset } });

            foreach (int width in new[] { 1280, 1920 })
            {
                yield return Resize(width, width == 1280 ? 720 : 1080);
                router.Show(ScreenIds.Menu);
                yield return Settle();
                var packField = Element<DropdownField>("menu-pack");
                int packIndex = legacy.MenuPacks.ToList().FindIndex(pack => pack.PackId == db.PackId);
                Require(packIndex >= 0 && packField.choices.Count == legacy.MenuPacks.Count, "主菜单内容包选项不完整。");
                packField.index = packIndex;
                yield return Settle();
                Require(legacy.MenuPack.PackId == db.PackId, "正式主菜单未选中验收预设所属内容包。");
                Capture("menu-with-tournament");
                Submit("open-tournament");
                yield return Settle();
                Require(router.CurrentScreenId == ScreenIds.Tournament && !legacy.enabled, "主菜单赛事按钮未进入正式赛事页。");
                Require(Element<VisualElement>("tournament-screen").userData is TournamentScreen, "赛事根未关联正式页面。");

                foreach (int format in new[] { 0, 1, 2, 3 })
                    foreach (int count in new[] { 2, 4, 16 })
                    {
                        yield return CreateTournament(format, count);
                        yield return CheckReload();
                        if (count == 2 && format == 0) yield return AutomaticBattle();
                        else if (count == 2 && format == 1) yield return PlayerBattle();
                        else if (count == 2 && format == 2)
                        {
                            yield return EliminationNaturalDraw();
                            yield return CompleteManually();
                        }
                        else if (count == 2 || count == 4 || format == 3) yield return CompleteManually();
                        else yield return ReportOneRound();
                        yield return CheckReport("format-" + format + "-players-" + count);
                    }
                yield return CheckCorruptLoad();
                Require(saveField.GetValue(legacy) == null, "赛事流程访问了普通玩家档案。");
                report.checks.Add(Size + "：四赛制和 2/4/16 人边界经真实 UI 创建、队伍预设确认、保存续赛、积分/树及战报验证。");
            }
            Require(report.created == 24 && report.automatedGames == 2 && report.playerGames == 2 && report.naturalDraws == 2,
                "两尺寸赛事创建或完整对局覆盖不足。");
            report.checks.AddRange(T133TournamentSessionChecks.RunChecks(db, Path.Combine(report.outputDirectory, "session-checks")));
            report.completed = true;
        }

        TournamentSession Session => ((TournamentScreen)Element<VisualElement>("tournament-screen").userData).CurrentSession;

        void ShowTournament()
        {
            router.Show(ScreenIds.Tournament, new TournamentScreenArgs
            {
                StorageDirectory = storageDirectory, Content = db, Presets = presets,
                WriteClipboard = text => clipboard = text
            });
        }

        IEnumerator CreateTournament(int format, int count)
        {
            storageDirectory = Path.Combine(report.outputDirectory, "tournaments", RequestedWidth + "-" + format + "-" + count);
            ShowTournament();
            yield return Settle();
            Submit("tournament-new");
            yield return Settle();
            Element<DropdownField>("tournament-format").index = format;
            Element<IntegerField>("tournament-count").value = count;
            yield return Settle();
            int seeding = count == 16 ? 2 : count == 4 && format >= 2 ? 1 : 0;
            Element<DropdownField>("tournament-seeding").index = seeding;
            Element<TextField>("tournament-seed").value = "1330133";
            Element<Toggle>("tournament-tiebreak").value = false;
            for (int i = 0; i < count; i++)
            {
                Element<TextField>("tournament-player-" + i + "-name").value = "验收选手" + (i + 1);
                Element<DropdownField>("tournament-player-" + i + "-control").index = (format == 1 || format == 2) && count == 2 ? 0 : 1 + i % 3;
            }
            yield return Settle();
            ValidateLayout();
            Capture("create-" + format + "-" + count);

            // 每位报名者均走同一个正式组队页，不能由验收直接改赛事报名模型。
            for (int i = 0; i < count; i++)
            {
                Submit("tournament-player-" + i + "-team");
                yield return Settle();
                Require(router.CurrentScreenId == ScreenIds.Team, "赛事队伍没有复用正式 T66 页面。");
                Require(Element<Button>("team-start").text == "确认队伍", "赛事选队仍误触发普通对战。");
                Require(!Element<DropdownField>("team-season").enabledInHierarchy && Element<DropdownField>("team-season").index == 0,
                    "赛事仅选队模式没有锁定为不限定赛季。");
                Element<DropdownField>("team-presets").index = 0;
                Submit("team-preset-load");
                yield return Settle();
                var picked = Enumerable.Range(0, 6).Select(slot => Element<Button>("team-slot-" + slot).userData as string).ToArray();
                Require(picked.SequenceEqual(preset.Ids), "T66 预设没有还原六个号位的顺序。实际：" + string.Join(",", picked)
                    + "；期望：" + string.Join(",", preset.Ids) + "；页面：" + Element<Label>("team-status").text);
                if (i == 0) { yield return Settle(); Capture("preset-" + format + "-" + count); }
                Submit("team-start");
                yield return Settle();
                Require(router.CurrentScreenId == ScreenIds.Tournament && !legacy.enabled, "确认队伍没有返回赛事创建页。");
            }
            Submit("tournament-create");
            yield return Settle();
            Require(Session != null, "创建失败：" + Element<Label>("tournament-status").text);
            var tournament = Session.Tournament;
            Require(tournament.Players.Count == count && tournament.Format == new[] { "roundrobin", "swiss", "single", "double" }[format],
                "UI 赛制或人数没有交给 Core。");
            Require(tournament.Players.All(player => player.Team.SequenceEqual(preset.Ids)), "实际报名队伍不等于 T66 确认预设。");
            Require(Session.Controllers.Count == count && Session.Controllers.Select((level, i) =>
                level == ((format == 1 || format == 2) && count == 2 ? (AiLevel?)null : new[] { AiLevel.Normal, AiLevel.Hard, AiLevel.Expert }[i % 3])).All(value => value),
                "玩家/电脑难度没有按报名保存。");
            Require((tournament.SeedMode == TournamentSeedMode.ByRating) == (seeding == 2), "排位种子配置丢失。");
            if (seeding == 1) Require(tournament.Seed == 1330133, "随机种子没有交给 Core。");
            if (seeding == 2) Require(tournament.RatingSeeds.Count == count, "报名排位快照数量不正确。");
            Require(tournament.Pairings.Count > 0 && File.Exists(Session.SavePath), "创建后没有排第一轮或保存。");
            Require(Within(Session.SavePath, storageDirectory), "赛事存档离开隔离目录。");
            report.created++;
            CheckStandings();
            ValidateLayout();
            Capture("pairings-" + format + "-" + count);
            yield return ScrollEvidence("pairings-" + format + "-" + count);
        }

        IEnumerator CheckReload()
        {
            string before = Session.Tournament.ToJson();
            string savePath = Session.SavePath;
            var controllers = Session.Controllers.ToArray();
            var pairing = Session.Tournament.Pairings.First(item => !item.IsBye && !item.WinnerSide.HasValue);
            var expected = Session.CreateBattle(pairing.Id);
            Submit("tournament-new");
            yield return Settle();
            SelectSave(savePath);
            Submit("tournament-load");
            yield return Settle();
            Require(Session != null && Session.Tournament.ToJson() == before && Session.Controllers.SequenceEqual(controllers),
                "保存重载改变赛事、队伍或控制难度。");
            var resumed = Session.CreateBattle(pairing.Id);
            Require(resumed.Seed == expected.Seed && resumed.StateHash() == expected.StateHash()
                && resumed.Record.ToJson() == expected.Record.ToJson(), "未完成对局重启没有沿用相同种子/队伍/记录。");
        }

        void SelectSave(string path)
        {
            var saves = Element<DropdownField>("tournament-saves");
            string name = Path.GetFileNameWithoutExtension(path);
            int index = saves.choices.FindIndex(choice => choice.Contains(name));
            Require(index >= 0, "已保存赛事没有列入续赛选项：" + name);
            saves.index = index;
        }

        IEnumerator AutomaticBattle()
        {
            var session = Session;
            var pairing = session.Tournament.Pairings.First(item => !item.IsBye && !item.WinnerSide.HasValue);
            Submit("pairing-" + pairing.Id);
            Require(Element<Button>("tournament-auto").enabledInHierarchy && !Element<Button>("tournament-play").enabledInHierarchy,
                "双电脑对阵的操作权限不正确。");
            string temporary = session.SavePath + ".tmp";
            Require(Within(temporary, storageDirectory), "临时文件夹具离开本轮隔离目录。");
            byte[] temporaryBytes = System.Text.Encoding.UTF8.GetBytes("T133 自动完局写入冲突夹具");
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                file.Write(temporaryBytes, 0, temporaryBytes.Length);
            byte[] checkpoint = File.ReadAllBytes(session.SavePath);
            Submit("tournament-auto");
            var screen = (TournamentScreen)Element<VisualElement>("tournament-screen").userData;
            Require(screen.IsAutomaticRunning, "自动按钮没有启动真实对局。");
            foreach (string name in new[] { "tournament-new", "tournament-load", "tournament-current", "tournament-saves" })
                Require(!Element<VisualElement>(name).enabledInHierarchy, "自动对局仍允许切换工具栏：" + name);
            float until = Time.realtimeSinceStartup + 180;
            while (screen.IsAutomaticRunning)
            {
                Require(Time.realtimeSinceStartup < until, "电脑自动完整对局未在期限内结束。");
                yield return null;
            }
            yield return Settle();
            var completed = screen.State.PendingResult;
            Require(completed != null && completed.Phase == Phase.Over && screen.State.PendingResultPairing == pairing.Id,
                "自动完局保存失败后丢失原对局。");
            string record = completed.Record.ToJson();
            ulong hash = completed.StateHash();
            Require(!session.Tournament.Pairings.First(item => item.Id == pairing.Id).WinnerSide.HasValue
                && File.ReadAllBytes(session.SavePath).SequenceEqual(checkpoint), "保存失败提前推进了比分或检查点。");
            Require(File.ReadAllBytes(temporary).SequenceEqual(temporaryBytes), "保存失败覆盖了已有临时文件。");
            Require(!Element<Button>("tournament-auto").enabledInHierarchy && !Element<Button>("tournament-record").enabledInHierarchy
                && Element<Button>("tournament-retry-save").enabledInHierarchy, "待保存原局可以被另一次开打或报分覆盖。");
            string replayId = ReadOnlyArchivedId(session, pairing.Id, completed.Winner);
            Require(ReplayLibrary.OpenReadOnly(session.ArchivePath, db.Policy).Load(replayId).ToJson() == record,
                "失败时归档不等于保留的原局。");
            ValidateLayout();
            Capture("automatic-save-failed-retained");
            // 只移除本方法以 CreateNew 创建并逐字核验的夹具，保留所有其它证据。
            File.Delete(temporary);
            Submit("tournament-retry-save");
            yield return Settle();
            Require(screen.State.PendingResult == null && session.Tournament.Pairings.First(item => item.Id == pairing.Id).WinnerSide == completed.Winner,
                "重试没有将同一结束对局登记成功。");
            Require(completed.StateHash() == hash && completed.Record.ToJson() == record, "重试改变了原对局状态或记录。");
            string archivedAfter = ReadOnlyArchivedId(session, pairing.Id, completed.Winner);
            Require(archivedAfter == replayId
                && ReplayLibrary.OpenReadOnly(session.ArchivePath, db.Policy).Load(replayId).ToJson() == record,
                "重试重复归档或重跑了另一局。");
            Require(session.BuildReport().ToJson().Contains("\"saved\""), "电脑完整对局未归档为可严格复算的赛事录像。");
            report.checks.Add(Size + "：自动对局期间工具栏禁用；完局写入冲突保留原 Battle/检查点，真实重试按钮成功登记且同一录像幂等。");
            report.automatedGames++;
            Capture("automatic-game-finished");
        }

        string ReadOnlyArchivedId(TournamentSession session, int pairingId, int winner)
        {
            // 战报只读查询关联录像；给独立赛事副本补结果，不改待保存的真实赛事。
            var snapshot = Tournament.FromJson(db, session.Tournament.ToJson());
            var pairing = snapshot.Pairings.First(item => item.Id == pairingId);
            if (!pairing.WinnerSide.HasValue) snapshot.ReportResult(pairing, winner);
            var result = (Dictionary<string, object>)MiniJson.Parse(TournamentReport.Build(snapshot,
                ReplayLibrary.OpenReadOnly(session.ArchivePath, db.Policy)).ToJson());
            var coverage = (Dictionary<string, object>)result["coverage"];
            Require((double)coverage["completedGames"] == 1 && (double)coverage["archivedGames"] == 1
                && (double)coverage["missingGames"] == 0 && (double)coverage["invalidGames"] == 0,
                "只读战报没有验证唯一的已归档原局。");
            var rounds = (List<object>)result["rounds"];
            var match = rounds.Cast<Dictionary<string, object>>().SelectMany(round => ((List<object>)round["matches"]).Cast<Dictionary<string, object>>())
                .Single(item => (double)item["pairingId"] == pairingId);
            var replay = (Dictionary<string, object>)match["replay"];
            Require((string)replay["status"] == "saved" && replay["id"] is string, "原局录像关联有重复或无法严格复算。");
            string id = (string)replay["id"];
            var files = Directory.GetFiles(session.ArchivePath, "*.json", SearchOption.TopDirectoryOnly)
                .Where(path => !string.Equals(Path.GetFileName(path), "index.json", StringComparison.OrdinalIgnoreCase)).ToArray();
            Require(files.Length == 1 && Path.GetFileNameWithoutExtension(files[0]) == id, "独占验收录像库不是同一份唯一录像。");
            return id;
        }

        IEnumerator EliminationNaturalDraw()
        {
            var session = Session;
            var pairing = session.Tournament.Pairings.Single(item => !item.IsBye && !item.WinnerSide.HasValue);
            Require(session.Tournament.IsElimination && !session.Tournament.AutoTiebreak, "缺少关闭加赛的淘汰赛夹具。");
            byte[] checkpoint = File.ReadAllBytes(session.SavePath);
            Submit("pairing-" + pairing.Id);
            Submit("tournament-play");
            yield return Settle();
            var battle = legacy.CurrentTeamBattle;
            Require(legacy.TournamentBattleActive && battle != null, "自然平局夹具未从真实 UI 进入旧对战页。");
            ulong initial = battle.StateHash();
            uint seed = battle.Seed;
            int turns = 0;
            while (battle.Phase != Phase.Over)
            {
                Require(++turns <= Battle.TurnCap && battle.Phase == Phase.Choose, "正常跳过回合未能走到自然结束。");
                battle.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
                yield return null;
            }
            Require(battle.Winner == -1 && battle.Turn == Battle.TurnCap, "双方正常跳过未产生 Core 回合上限平局。");
            var replayed = Replay.Run(db, battle.Record);
            Require(replayed.StateHash() == battle.StateHash() && replayed.Winner == -1, "自然平局录像无法严格复算。");
            int steps = 0;
            while (legacy.TournamentBattleActive)
            {
                Require(++steps < 10, "自然平局无法通过原结束回调返回赛事。");
                legacy.AdvanceTournamentSmoke();
                yield return null;
            }
            yield return Settle();
            var screen = (TournamentScreen)Element<VisualElement>("tournament-screen").userData;
            Require(screen.State.PendingResult == null && !session.Tournament.Pairings[pairing.Id].WinnerSide.HasValue
                && File.ReadAllBytes(session.SavePath).SequenceEqual(checkpoint), "关闭加赛的自然平局锁住重试或写入虚构晋级结果。");
            Require(Element<Button>("tournament-play").enabledInHierarchy && Element<Button>("tournament-record").enabledInHierarchy
                && Element<Label>("tournament-status").text.Contains("平局"), "平局未恢复待赛操作或缺少重赛说明。");
            Require(!Directory.Exists(session.ArchivePath) || Directory.GetFiles(session.ArchivePath, "*.json").Length == 0,
                "淘汰赛平局被误归档为决胜录像。");
            Capture("elimination-natural-draw-rematch");
            Submit("tournament-play");
            yield return Settle();
            Require(legacy.TournamentBattleActive && legacy.CurrentTeamBattle.Seed == seed && legacy.CurrentTeamBattle.StateHash() == initial,
                "自然平局后不能从同一种子和阵容重新开打。");
            legacy.CancelTournamentBattle();
            yield return Settle();
            Require(!session.Tournament.Pairings[pairing.Id].WinnerSide.HasValue, "取消重赛改变了待赛结果。");
            report.naturalDraws++;
            report.checks.Add(Size + "：关闭加赛的淘汰赛经 Core 正常跳过至自然平局，结束回调恢复待赛；同种子重赛成功，未虚构比分或决胜录像。");
        }

        IEnumerator PlayerBattle()
        {
            var session = Session;
            var pairing = session.Tournament.Pairings.First(item => !item.IsBye && !item.WinnerSide.HasValue);
            uint expectedSeed = session.Tournament.GameSeed(pairing, 0);
            Submit("pairing-" + pairing.Id);
            Require(Element<Button>("tournament-play").enabledInHierarchy && !Element<Button>("tournament-auto").enabledInHierarchy,
                "真人对阵被允许静默自动打完。");
            Submit("tournament-play");
            yield return Settle();
            Require(router.CurrentScreenId == ScreenIds.Legacy && legacy.enabled && legacy.TournamentBattleActive,
                "玩家对阵没有进入旧对战页。");
            var battle = legacy.CurrentTeamBattle;
            Require(battle != null && battle.Seed == expectedSeed, "玩家对局丢失赛事种子。");
            Capture("player-game-start");
            bool captured = false;
            int steps = 0;
            while (legacy.TournamentBattleActive)
            {
                Require(++steps < 12000, "玩家完整对局无法结束。");
                legacy.AdvanceTournamentSmoke();
                if (!captured && battle.Turn >= 2 && legacy.TournamentBattleActive)
                {
                    yield return Settle();
                    Capture("player-game-playing");
                    captured = true;
                }
                yield return null;
            }
            yield return Settle();
            Require(captured && battle.Phase == Phase.Over && router.CurrentScreenId == ScreenIds.Tournament && !legacy.enabled,
                "玩家整局没有正常完成并返回赛事。");
            Require(session.Tournament.Pairings.First(item => item.Id == pairing.Id).WinnerSide == battle.Winner,
                "玩家真实胜负没有登记至原对阵。");
            var replay = Replay.Run(db, battle.Record);
            Require(replay.StateHash() == battle.StateHash()
                && replay.Log.Select(entry => entry.Text).SequenceEqual(battle.Log.Select(entry => entry.Text)), "玩家对局录像不能复算。");
            Require(session.BuildReport().ToJson().Contains("\"saved\""), "玩家对局未归档为可严格复算的赛事录像。");
            report.playerGames++;
            Capture("player-game-returned");
        }

        IEnumerator ReportOneRound()
        {
            var pending = Session.Tournament.Pairings.Where(item => !item.IsBye && !item.WinnerSide.HasValue).Select(item => item.Id).ToArray();
            foreach (int id in pending) yield return ManualResult(id);
        }

        IEnumerator CompleteManually()
        {
            int steps = 0;
            while (!Session.Tournament.IsFinished)
            {
                Require(++steps < 150, "手工报分未能推进至赛事完成。");
                var pending = Session.Tournament.Pairings.FirstOrDefault(item => !item.IsBye && !item.WinnerSide.HasValue);
                if (pending != null) yield return ManualResult(pending.Id);
                else
                {
                    Submit("tournament-next");
                    yield return Settle();
                }
            }
            ValidateLayout();
            yield return ScrollEvidence("completed-" + Session.Tournament.Format + "-" + Session.Tournament.Players.Count);
            if (Session.Tournament.Format == "double" && Session.Tournament.Players.Count == 16)
            {
                var scroll = Element<ScrollView>("tournament-pairings-scroll");
                foreach (string group in new[] { "winners", "losers", "final" })
                {
                    var heading = Element<Label>("bracket-title-" + group);
                    Vector2 position = scroll.contentContainer.WorldToLocal(heading.worldBound.position);
                    scroll.scrollOffset = new Vector2(Mathf.Clamp(0, scroll.horizontalScroller.lowValue, scroll.horizontalScroller.highValue),
                        Mathf.Clamp(position.y, scroll.verticalScroller.lowValue, scroll.verticalScroller.highValue));
                    yield return Settle();
                    bool visibleCard = false;
                    Rect viewport = scroll.contentViewport.worldBound;
                    Element<VisualElement>("bracket-lane-" + group).Query<Button>().ForEach(card =>
                    {
                        Rect rect = card.worldBound, seen = Intersection(rect, viewport);
                        if (card.name.StartsWith("pairing-", StringComparison.Ordinal) && Visible(card)
                            && rect.width > 8 && rect.height > 8 && seen.width >= rect.width - 2 && seen.height >= rect.height - 2)
                            visibleCard = true;
                    });
                    Require(visibleCard, "分组截图缺少完整可见的对阵卡：" + group);
                    ValidateLayout();
                    Capture("completed-double-16-" + group);
                }
            }
        }

        IEnumerator ManualResult(int id)
        {
            var tournament = Session.Tournament;
            var pairing = tournament.Pairings.First(item => item.Id == id);
            Submit("pairing-" + id);
            if (tournament.IsElimination && tournament.Players.Count == 4 && id == 0)
            {
                byte[] before = File.ReadAllBytes(Session.SavePath);
                Element<DropdownField>("tournament-result").index = 2;
                Submit("tournament-record");
                yield return Settle();
                Require(!Session.Tournament.Pairings.First(item => item.Id == id).WinnerSide.HasValue
                    && File.ReadAllBytes(Session.SavePath).SequenceEqual(before), "淘汰赛非法平局改变了待赛或存档。");
                Require(!string.IsNullOrWhiteSpace(Element<Label>("tournament-status").text), "非法平局缺少中文提示。");
            }
            int winner = !tournament.IsElimination && tournament.Players.Count == 4 && id == 0 ? -1 : 0;
            Element<DropdownField>("tournament-result").index = winner == -1 ? 2 : winner;
            Submit("tournament-record");
            yield return Settle();
            Require(Session.Tournament.Pairings.First(item => item.Id == id).WinnerSide == winner,
                "手工报分没有交给 Core。场次=" + id + "；界面提示：" + Element<Label>("tournament-status").text);
            Require(pairing.Games.Count == 0, "手工比分虚构了实际对战记录。");
            CheckStandings();
            report.manuallyReported++;
        }

        void CheckStandings()
        {
            var standings = Session.Tournament.Standings();
            var rows = Element<VisualElement>("tournament-standings").Children()
                .Where(row => row.name.StartsWith("standing-", StringComparison.Ordinal)).Select(row => row.name);
            Require(rows.SequenceEqual(standings.Select(standing => "standing-" + standing.Player)), "积分表顺序没有采用 Core 排名。");
            foreach (var standing in standings)
            {
                Require(Element<Label>("standing-rank-" + standing.Player).text == standing.Rank.ToString(CultureInfo.InvariantCulture)
                    && Element<Label>("standing-name-" + standing.Player).text == standing.Name
                    && Element<Label>("standing-score-" + standing.Player).text == standing.Points + " / " + standing.OpponentPoints
                    && Element<Label>("standing-record-" + standing.Player).text == standing.Wins + " / " + standing.Draws + " / " + standing.Losses + " / " + standing.Byes,
                    "积分、对手分或胜平负/轮空与 Core 不符。");
            }
        }

        IEnumerator CheckReport(string suffix)
        {
            string expected = Session.BuildReport().ToText();
            Submit("tournament-report");
            yield return Settle();
            Require(Element<Label>("tournament-report-text").text == expected, "结束页战报不是 Core TournamentReport 原文。");
            clipboard = "未复制";
            Submit("tournament-copy-report");
            Require(clipboard == expected, "复制按钮没有复制完整 Core 战报。");
            ValidateLayout();
            Capture("report-" + suffix);
            yield return ScrollEvidence("report-" + suffix);
        }

        IEnumerator CheckCorruptLoad()
        {
            string path = Session.SavePath;
            byte[] bad = System.Text.Encoding.UTF8.GetBytes("{本轮赛事坏档");
            File.WriteAllBytes(path, bad);
            DateTime stamp = File.GetLastWriteTimeUtc(path);
            ShowTournament();
            yield return Settle();
            Submit("tournament-new");
            yield return Settle();
            SelectSave(path);
            Submit("tournament-load");
            yield return Settle();
            Require(File.ReadAllBytes(path).SequenceEqual(bad) && File.GetLastWriteTimeUtc(path) == stamp,
                "坏档读取改写了原字节或时间。");
            Require(!string.IsNullOrWhiteSpace(Element<Label>("tournament-status").text), "拒绝坏档未显示中文原因。");
            Capture("corrupt-save-retained");
            report.checks.Add(Size + "：真实载入入口拒绝坏 JSON，原字节和 mtime 完整保留。");
        }

        // 以下几何检查同时覆盖滚动容器裁切后的实际可见区域。
        IEnumerator ScrollEvidence(string suffix)
        {
            var scrolls = new List<ScrollView>();
            Element<VisualElement>("tournament-screen").Query<ScrollView>().ForEach(scroll => scrolls.Add(scroll));
            foreach (var scroll in scrolls)
                scroll.scrollOffset = new Vector2(scroll.horizontalScroller.highValue, scroll.verticalScroller.highValue);
            yield return Settle();
            ValidateLayout();
            Capture(suffix + "-scrolled");
            foreach (var scroll in scrolls) scroll.scrollOffset = Vector2.zero;
            yield return Settle();
        }

        void ValidateLayout()
        {
            var page = Element<VisualElement>("tournament-screen");
            var texts = new List<TextElement>();
            page.Query<TextElement>().ForEach(text => { if (Visible(text) && !string.IsNullOrEmpty(text.text)) texts.Add(text); });
            Rect screen = router.Document.rootVisualElement.worldBound;
            var visible = new List<TextElement>();
            var clipped = new List<Rect>();
            foreach (var text in texts)
            {
                Rect rect = text.worldBound, clip = screen;
                bool scrolled = false, horizontal = false, editable = false;
                for (var ancestor = text.parent; ancestor != null; ancestor = ancestor.parent)
                {
                    if (ancestor is ScrollView scroll)
                    {
                        clip = Intersection(clip, scroll.contentViewport.worldBound);
                        scrolled = true;
                        horizontal |= scroll.mode != ScrollViewMode.Vertical;
                    }
                    if (ancestor is TextField field && !ReferenceEquals(text, field.labelElement)) editable = true;
                }
                Rect seen = Intersection(rect, clip);
                if (seen.width <= 0 || seen.height <= 0) continue;
                Require(Finite(rect.x) && Finite(rect.y) && Finite(rect.width) && Finite(rect.height) && rect.width > 0 && rect.height > 0,
                    "文字节点布局无效：" + text.name);
                Require(editable || horizontal || (scrolled ? rect.xMin >= clip.xMin - 2 && rect.xMax <= clip.xMax + 2 : Contains(screen, rect)),
                    "控件超出可读区域：" + text.name + " " + text.text);
                if (!editable)
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
            Require(visible.Count > 0, "赛事页面没有可见文本。");
            for (int i = 0; i < visible.Count; i++)
                for (int j = i + 1; j < visible.Count; j++)
                {
                    if (visible[i].Contains(visible[j]) || visible[j].Contains(visible[i])) continue;
                    Rect overlap = Intersection(clipped[i], clipped[j]);
                    Require(overlap.width <= 1 || overlap.height <= 1,
                        "可见文字重叠：" + visible[i].name + " / " + visible[j].name + " " + visible[i].text + " / " + visible[j].text);
                }
            foreach (string name in new[] { "tournament-title", "tournament-new", "tournament-load", "tournament-back" })
            {
                var pinned = Element<VisualElement>(name);
                Require(Visible(pinned) && pinned.worldBound.width > 2 && pinned.worldBound.height > 2 && Contains(screen, pinned.worldBound),
                    "赛事常驻按钮离开可见区域：" + name);
            }
        }

        IEnumerator Resize(int width, int height)
        {
            RequestedWidth = width;
            RequestedHeight = height;
            Screen.SetResolution(width, height, FullScreenMode.Windowed);
            float until = Time.realtimeSinceStartup + 20;
            while (Screen.width != width || Screen.height != height)
            {
                Require(Time.realtimeSinceStartup < until, "分辨率切换超时；Editor 需要 T133SmokeEditor 桥。");
                yield return null;
            }
            yield return Settle();
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
        }

        void Finish(string failure)
        {
            if (!running) return;
            running = false;
            try
            {
                report.failure = failure;
                if (failure != null)
                {
                    report.completed = false;
                    if (report.outputDirectory != null)
                        try { Capture("failure"); }
                        catch (Exception captureError) { report.messages.Add("失败现场截图不可用：" + captureError.Message); }
                }
                ExitCode = report.completed && report.errors == 0 && report.warnings == 0 ? 0 : 1;
                if (ReportPath != null) File.WriteAllText(ReportPath, JsonUtility.ToJson(report, true));
                else ExitCode = 1;
            }
            catch (Exception error) { ExitCode = 1; Debug.LogError("赛事验收报告写入失败：" + error); }
            finally
            {
                Application.logMessageReceived -= OnLog;
                Completed = true;
                if (ExitCode == 0) Debug.Log("T133_TOURNAMENT_SMOKE_PASS " + ReportPath);
                else Debug.LogError("T133_TOURNAMENT_SMOKE_FAIL " + (failure ?? "检测到警告或错误") + " " + ReportPath);
                if (!Application.isEditor) Application.Quit(ExitCode);
            }
        }
    }
}
