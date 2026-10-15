using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using StreamDuel.Core;
using UnityEngine;
using UnityEngine.UIElements;

namespace StreamDuel.Game
{
    public sealed class BattleUiSmoke : MonoBehaviour
    {
        [Serializable]
        sealed class Report
        {
            public string unityVersion, outputDirectory, failure;
            public string navigation = "真实 UI Toolkit 按钮通过 NavigationSubmitEvent 提交；悬停通过根节点 PointerMoveEvent 坐标命中或 PointerEnter/LeaveEvent 检查实际浮层；配置和边界夹具经公开 Args 注入，不代表人工鼠标验收。";
            public string logScope = "从 BeforeSceneLoad 收集全部警告和错误；更早的启动诊断另查完整 Editor 日志及退出后 Console 计数。";
            public string layoutScope = "检查渲染控件边界、文字测量和非祖先交叠；滚动行按视口裁剪，截图仍需人工复核。";
            public bool completed, timelineAvailable;
            public int warnings, errors, completedMatches, animationBatches;
            public List<string> steps = new List<string>();
            public List<string> screenshots = new List<string>();
            public List<string> logs = new List<string>();
            public List<string> eventRanges = new List<string>();
        }

        sealed class RecordingAnimator : IBattleAnimator
        {
            public readonly List<IReadOnlyList<AnimCue>> Calls = new List<IReadOnlyList<AnimCue>>();
            public bool IsPlaying { get; private set; }
            public void Play(IReadOnlyList<AnimCue> cues)
            {
                Require(!IsPlaying, "前一批动画尚未结束就播放了下一批。");
                Require(cues != null, "动画收到空提示列表。");
                Calls.Add(cues.ToArray());
                IsPlaying = true;
            }
            public void SkipToEnd() { IsPlaying = false; }
        }

        public static bool Completed { get; private set; }
        public static int ExitCode { get; private set; }
        public static string ReportPath { get; private set; }
        public static int RequestedWidth { get; private set; }
        public static int RequestedHeight { get; private set; }

        Report report;
        ScreenRouter router;
        UIDocument document;
        RecordingAnimator recording;
        Battle recordedBattle;
        int checkedCalls, lastEvent;
        bool running;
        float deadline;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Install()
        {
            string[] args = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(args, "--battle-ui-smoke");
            if (index < 0) index = Array.IndexOf(args, "--smoke-test");
            if (index < 0) return;
            Completed = false;
            ExitCode = 1;
            ReportPath = null;
            RequestedWidth = RequestedHeight = 0;
            var host = new GameObject("StreamDuel.BattleUiSmoke");
            DontDestroyOnLoad(host);
            host.AddComponent<BattleUiSmoke>().Configure(args, index);
        }

        void Configure(string[] args, int index)
        {
            report = new Report { unityVersion = Application.unityVersion, timelineAvailable = BattleAnimationTimeline.IsAvailable };
            running = true;
            deadline = Time.realtimeSinceStartup + 540;
            Application.logMessageReceived += CollectLog;
            try
            {
                Require(index + 1 < args.Length && Path.IsPathFullyQualified(args[index + 1]), "对战验收参数后必须提供绝对输出目录。");
                string output = Path.GetFullPath(args[index + 1]);
                string logs = Path.GetFullPath(Path.Combine(Application.dataPath, "../Logs"));
                Require(output.Equals(logs, StringComparison.OrdinalIgnoreCase)
                    || output.StartsWith(logs + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
                    "验收产物必须位于本工程忽略的 Logs 目录。");
                report.outputDirectory = Path.Combine(output, "run-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(report.outputDirectory);
                ReportPath = Path.Combine(report.outputDirectory, "result.json");
                Debug.Log("BATTLE_UI_SMOKE_OUTPUT " + report.outputDirectory);
            }
            catch (Exception ex) { Finish(ex.ToString()); }
        }

        void Start() { if (running) StartCoroutine(Guard(Run())); }
        void LateUpdate()
        {
            if (running && Time.realtimeSinceStartup >= deadline)
                Finish("对战界面验收超过 540 秒；截图需要真实图形渲染。");
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
                object wait = null;
                string failure = null;
                bool more = false;
                try
                {
                    more = stack.Peek().MoveNext();
                    if (more) wait = stack.Peek().Current;
                    else stack.Pop();
                }
                catch (Exception ex) { failure = ex.ToString(); }
                if (failure != null || stack.Count == 0) { Finish(failure); yield break; }
                if (!more) continue;
                if (wait is IEnumerator nested) { stack.Push(nested); continue; }
                yield return wait;
            }
        }

        IEnumerator Run()
        {
            yield return null;
            var routers = FindObjectsByType<ScreenRouter>(FindObjectsInactive.Include);
            var documents = FindObjectsByType<UIDocument>(FindObjectsInactive.Include);
            Require(routers.Length == 1 && documents.Length == 1, "必须只有一个路由和 UIDocument。");
            router = routers[0];
            document = documents[0];
            Require(router.CurrentScreenId == ScreenIds.Menu && document.rootVisualElement.panel != null,
                "默认菜单没有连接到运行时面板。");
            Require(BattleSetupPanel.IsolatedRequested, "验收没有启用隔离设置模式。");
            int[] widths = { 1280, 1920 }, heights = { 720, 1080 };
            for (int resolution = 0; resolution < widths.Length; resolution++)
            {
                yield return Resize(widths[resolution], heights[resolution]);
                router.Show(ScreenIds.Menu);
                yield return null;
                Submit("open-battle");
                yield return null;
                yield return new WaitForEndOfFrame();
                Require(document.rootVisualElement.Q("battle-setup") != null, "菜单没有打开组队配置。");
                Capture("setup");
                Submit("setup-start");
                yield return null;
                Require(Current != null && Current.CurrentBattle != null, "配置页开播按钮没有创建对局。");
                yield return CheckSetupSelection();
                Submit("battle-menu");
                yield return null;
                Require(router.CurrentScreenId == ScreenIds.Menu, "对战菜单按钮没有返回主菜单。");
                report.steps.Add(Size + "：菜单→配置→开播→菜单全部经过按钮提交。");
                yield return CompleteMatch();
                yield return CheckUnavailableAndReplacement();
                yield return CheckRuntimeTooltips();
                yield return CheckRematchAndTimer();
            }
            Require(report.completedMatches == 2, "没有在两个分辨率分别完成真实对局。");
            report.completed = true;
        }

        IEnumerator CheckSetupSelection()
        {
            var original = Current.CurrentBattle;
            var db = original.Db;
            var originalIds = original.Sides[0].Team.Select(fighter => fighter.Def.Id).ToArray();
            Require(originalIds.Length == 6, "组队保存夹具开局不是六人阵容。");
            string added = db.Streamers.Select(streamer => streamer.Id).FirstOrDefault(id => !originalIds.Contains(id));
            Require(added != null, "组队保存夹具没有原未选择的主播。");
            var expected = originalIds.Where((_, index) => index != 1).Concat(new[] { added }).ToArray();

            Submit("battle-setup");
            yield return null;
            Require(Current.CurrentBattle == null && document.rootVisualElement.Q("setup-slots") != null,
                "对战没有返回组队配置。");
            Submit("setup-slot-1");
            yield return null;
            Require(!Button("setup-start").enabledInHierarchy, "移出一位主播后仍允许六人开播。");
            var candidate = Button("setup-pick-" + added);
            var roster = document.rootVisualElement.Q<ScrollView>("setup-roster-scroll");
            Require(roster != null, "组队候选列表没有滚动容器。");
            roster.ScrollTo(candidate);
            yield return null;
            Require(Overlaps(roster.contentViewport.worldBound, candidate.worldBound), "原未选主播的加入按钮没有滚动到可见区域。");
            Submit(candidate.name);
            yield return null;
            Require(Button("setup-start").enabledInHierarchy, "补入一位主播后阵容仍不可开播。");
            Submit("setup-start");
            yield return null;
            Require(Current.CurrentBattle != original && Current.Args.Isolated
                && Current.CurrentBattle.Sides[0].Team.Select(fighter => fighter.Def.Id).SequenceEqual(expected),
                "手动修改的六人 ID 或顺序没有用于实际对战。");

            Submit("battle-setup");
            yield return null;
            Require(Current.CurrentBattle == null, "保存检查没有回到组队页。");
            for (int index = 0; index < expected.Length; index++)
            {
                var slot = Button("setup-slot-" + index);
                Require(slot.enabledInHierarchy && slot.userData as string == expected[index]
                    && slot.text == (index + 1) + " · " + db.Streamer(expected[index]).Name + " ×",
                    "返回组队后第 " + (index + 1) + " 槽的 ID、成员或顺序没有保留。");
            }
            yield return new WaitForEndOfFrame();
            Capture("setup-preserved");
            Submit("setup-start");
            yield return null;
            Require(Current.CurrentBattle != null && Current.Args.Isolated
                && Current.CurrentBattle.Sides[0].Team.Select(fighter => fighter.Def.Id).SequenceEqual(expected),
                "返回组队后重新开播使用了不同的六人 ID 或顺序。");
            report.steps.Add(Size + "：真实按钮移出第 2 槽并加入原未选主播，开播→重新组队→再开播的六人 ID 与顺序保留："
                + string.Join(",", expected) + "（隔离同会话）。");
        }

        IEnumerator Resize(int width, int height)
        {
            RequestedWidth = width;
            RequestedHeight = height;
            Screen.SetResolution(width, height, FullScreenMode.Windowed);
            float until = Time.realtimeSinceStartup + 20;
            while (Screen.width != width || Screen.height != height)
            {
                Require(Time.realtimeSinceStartup < until, "GameView 未切换到 " + Size + "。");
                yield return null;
            }
            yield return null;
        }

        static ContentDb LoadContent()
        {
            var rules = Resources.Load<TextAsset>("StreamDuelRules");
            var pack = Resources.Load<TextAsset>("Packs/original");
            Require(rules != null && pack != null, "缺少原创内容资源。");
            return ContentDb.FromJson(rules.text, pack.text);
        }

        static IList<string>[] Teams(ContentDb db)
        {
            var ids = db.Streamers.Select(fighter => fighter.Id).ToArray();
            return new IList<string>[] { ids.Take(6).ToArray(), ids.Skip(6).Take(6).ToArray() };
        }

        BattleScreen Current => BattleScreen.ActiveInstance;
        string Size => RequestedWidth + "x" + RequestedHeight;

        void OpenRecorded(Battle battle = null)
        {
            var db = battle?.Db ?? LoadContent();
            recording = new RecordingAnimator();
            recordedBattle = null;
            checkedCalls = lastEvent = 0;
            router.Show(ScreenIds.Battle, new BattleScreenArgs
            {
                Battle = battle, Db = db, Teams = Teams(db), Names = new[] { "你", "电脑" },
                Seed = 67, BanOne = battle == null, LeadPick = battle == null, Animator = recording,
                Isolated = true, Settings = new GameSettings { DefaultPack = "original", AiLevel = AiLevel.Normal, Animations = true }
            });
            Require(Current != null && Current.CurrentBattle != null, "注入配置没有创建对战画面。");
            recordedBattle = Current.CurrentBattle;
            if (battle != null) lastEvent = battle.Events.Count;
        }

        IEnumerator CompleteMatch()
        {
            OpenRecorded();
            yield return DrainPlayback();
            var battle = Current.CurrentBattle;
            Require(battle.Phase == Phase.Ban, "禁用配置没有进入 Ban。");
            yield return new WaitForEndOfFrame();
            Capture("ban");
            Submit("battle-foe-" + Ai.ChooseBan(battle, 0));
            yield return DrainPlayback();
            Require(battle.Phase == Phase.Lead, "禁用后未进入首发阶段。");
            Require(battle.Sides.All(side => side.Team.Count == 5), "禁用没有将双方队伍变为五人。");
            yield return new WaitForEndOfFrame();
            Capture("lead");
            Submit("battle-own-" + Ai.ChooseLead(battle, 0));
            yield return DrainPlayback();
            Require(battle.Phase == Phase.Choose, "首发后没有进入行动阶段。");
            ValidateHud();
            yield return new WaitForEndOfFrame();
            Capture("battle");

            var legal = battle.LegalFor(0);
            Require(legal.Switches.Count > 0, "验收开局没有可用换人。");
            int switched = legal.Switches[0];
            Submit("battle-own-" + switched);
            yield return DrainPlayback();
            Require(battle.Record.Events.Any(entry => entry.Type == BattleRecordEventType.Turn && entry.A0.Type == ActionType.Switch),
                "换人按钮没有提交换人行动。");
            report.steps.Add(Size + "：禁用、首发、主动换人及播放锁/跳过通过。");

            int guard = 0;
            while (battle.Phase != Phase.Over)
            {
                Require(guard++ < 500, "对战没有在有限操作内结束。");
                yield return DrainPlayback();
                if (battle.Phase == Phase.Over) break;
                if (battle.Phase == Phase.Replace)
                {
                    Require(battle.Need[0], "电脑补位没有由控制器完成。");
                    Submit("battle-own-" + Ai.ChooseReplacement(battle, 0));
                }
                else
                {
                    Require(battle.Phase == Phase.Choose, "出现未处理的对战阶段。");
                    var action = Ai.Choose(battle, 0);
                    Submit(action.Type == ActionType.Skill ? "battle-skill-" + action.Index
                        : action.Type == ActionType.Switch ? "battle-own-" + action.Index : "battle-pass");
                }
                yield return null;
            }
            yield return DrainPlayback();
            Require(lastEvent == battle.Events.Count, "结束时仍有结构化事件没有交给动画接口。");
            yield return new WaitForEndOfFrame();
            Require(Shown(document.rootVisualElement.Q("battle-outcome")), "终局弹层没有出现。");
            ValidateHud();
            Capture("result");
            File.WriteAllText(Path.Combine(report.outputDirectory, Size + "-record.json"), battle.Record.ToJson());
            report.completedMatches++;
            Submit("battle-rematch");
            yield return null;
            Require(Current.CurrentBattle != battle && Current.CurrentBattle.Phase == Phase.Ban,
                "重来没有创建新的禁用对局。");
            yield return new WaitForEndOfFrame();
            Capture("rematch");
            Submit("battle-setup");
            yield return null;
            Require(document.rootVisualElement.Q("battle-setup") != null && Current.CurrentBattle == null,
                "重新组队没有回到配置页。");
            router.Show(ScreenIds.Battle, new BattleScreenArgs { Battle = battle, Isolated = true });
            yield return null;
            Submit("battle-return");
            yield return null;
            Require(Current.CurrentBattle == null && document.rootVisualElement.Q<Button>("setup-start") != null,
                "结算弹窗的重新组队按钮没有回到配置。");
            Submit("back-button");
            yield return null;
            Require(router.CurrentScreenId == ScreenIds.Menu, "配置返回按钮没有回到菜单。");
            report.steps.Add(Size + "：完整对局、终局弹层、同阵容重来与返回组队通过。");
        }

        IEnumerator DrainPlayback()
        {
            int guard = 0;
            while (true)
            {
                yield return null;
                var screen = Current;
                Require(screen != null, "播放期间对战界面丢失。");
                var battle = screen.CurrentBattle;
                Require(ReferenceEquals(recordedBattle, battle), "同一动画检查意外跨越了对局。");
                if (recording.Calls.Count > checkedCalls)
                {
                    Require(recording.Calls.Count == checkedCalls + 1, "同一帧未检查就派发了多批动画。");
                    Require(screen.PlaybackFromEvent == lastEvent && screen.PlaybackToEvent > lastEvent
                        && screen.PlaybackToEvent <= battle.Events.Count, "动画事件范围重复、跳过或越界。");
                    if (BattleAnimationTimeline.IsAvailable)
                    {
                        double speed = !screen.Args.Settings.Animations ? 0 : screen.Args.Settings.FastPlay ? 2 : 1;
                        var expected = BattleAnimationTimeline.Build(battle, lastEvent, screen.PlaybackToEvent, speed);
                        var actual = recording.Calls[checkedCalls];
                        Require(actual.Count == expected.Count, "动画提示数量与本批时间线不同。");
                        for (int index = 0; index < expected.Count; index++)
                            Require(JsonUtility.ToJson(actual[index]) == JsonUtility.ToJson(expected[index]), "动画提示与新增事件的时间线不同。");
                    }
                    report.eventRanges.Add(Size + ":" + lastEvent + ".." + screen.PlaybackToEvent);
                    lastEvent = screen.PlaybackToEvent;
                    checkedCalls++;
                    report.animationBatches++;
                }
                if (!screen.IsBusy)
                {
                    int calls = recording.Calls.Count;
                    yield return null;
                    Require(recording.Calls.Count == calls, "空闲 Tick 重放了旧事件。");
                    break;
                }
                Require(guard++ < 30, "动画跳过后没有退出播放状态。");
                foreach (string prefix in new[] { "battle-skill-", "battle-own-", "battle-foe-" })
                    for (int index = 0; index < (prefix == "battle-skill-" ? 5 : 6); index++)
                    {
                        var button = document.rootVisualElement.Q<Button>(prefix + index);
                        if (button != null && Shown(button)) Require(!button.enabledInHierarchy, "播放时行动按钮仍启用：" + button.name);
                    }
                Require(!Button("battle-pass").enabledInHierarchy, "播放时跳过行动按钮仍启用。");
                int actions = battle.Record.Events.Count;
                SendSubmit(Button("battle-skill-0"));
                Require(battle.Record.Events.Count == actions, "播放期间禁用按钮仍提交了行动。");
                Submit("battle-skip");
                yield return null;
            }
        }

        IEnumerator CheckUnavailableAndReplacement()
        {
            var db = LoadContent();
            var battle = new Battle(db, Teams(db), new[] { "你", "电脑" }, 68);
            battle.Active(0).Skills[0].Pp = 0;
            OpenRecorded(battle);
            yield return DrainPlayback();
            Require(!Button("battle-skill-0").enabledInHierarchy, "PP 为零的技能仍可点击。");
            Require(Button("battle-skill-0").text.Contains("PP 已耗尽"),
                "无 PP 技能没有显示不可用原因。");
            int count = battle.Record.Events.Count;
            SendSubmit(Button("battle-skill-0"));
            Require(battle.Record.Events.Count == count, "无 PP 技能按钮仍提交了行动。");
            yield return new WaitForEndOfFrame();
            Capture("no-pp");
            yield return CheckTooltip("battle-skill-0", true, "tooltip-no-pp", "PP 已耗尽");

            db = LoadContent();
            battle = new Battle(db, Teams(db), new[] { "你", "电脑" }, 69);
            string control = db.Statuses.First(pair => pair.Value.IsControl).Key;
            battle.Active(0).Statuses.Add(new StatusInst { Id = control, Left = 2 });
            OpenRecorded(battle);
            yield return DrainPlayback();
            Require(!string.IsNullOrEmpty(BattleViewModel.From(battle, 0).ControlledReason), "控制夹具未生效。");
            for (int index = 0; index < 5; index++)
            {
                var skill = Button("battle-skill-" + index);
                Require(!skill.enabledInHierarchy, "受控时技能仍可用。");
                Require(skill.text.Contains(BattleViewModel.From(battle, 0).ControlledReason), "受控技能没有显示不可用原因。");
            }
            Require(Button("battle-pass").enabledInHierarchy, "受控时没有提供跳过行动。");
            count = battle.Record.Events.Count;
            SendSubmit(Button("battle-skill-0"));
            Require(battle.Record.Events.Count == count, "受控禁用技能仍提交行动。");
            yield return new WaitForEndOfFrame();
            Capture("controlled");
            yield return CheckTooltip("battle-skill-0", true, "tooltip-controlled", BattleViewModel.From(battle, 0).ControlledReason);
            Submit("battle-pass");
            yield return DrainPlayback();
            Require(battle.Record.Events.Count > count, "受控跳过按钮没有提交行动。");

            db = LoadContent();
            db.Skills["smoke_replace"] = new SkillDef
            {
                Id = "smoke_replace", Name = "补位验收", Type = "game", Cat = "status", Pp = 10,
                Effects = { new EffectDef { At = "post", Code = "FIXED_DAMAGE", Target = "foe", N = 10000 } }
            };
            db.Streamers[6].Skills[0] = "smoke_replace";
            battle = new Battle(db, Teams(db), new[] { "你", "电脑" }, 70);
            battle.ResolveTurn(BattleAction.Pass(), BattleAction.Skill(0));
            Require(battle.Phase == Phase.Replace && battle.Need[0], "补位边界夹具未到达预期阶段。");
            OpenRecorded(battle);
            yield return DrainPlayback();
            yield return new WaitForEndOfFrame();
            Capture("replace");
            int replacement = Ai.ChooseReplacement(battle, 0);
            Submit("battle-own-" + replacement);
            yield return DrainPlayback();
            Require(!battle.Need[0] && battle.Sides[0].Active == replacement, "补位按钮没有切换到指定主播。");
            yield return CheckLog();
            Submit("battle-menu");
            yield return null;
            report.steps.Add(Size + "：独立边界夹具验证 PP/控制禁用、UI 跳过、UI 补位和日志滚动高亮。");
        }

        IEnumerator CheckRuntimeTooltips()
        {
            var db = LoadContent();
            var battle = new Battle(db, Teams(db), new[] { "你", "电脑" }, 73);
            battle.Active(0).Statuses.Add(new StatusInst { Id = "flame", Left = 3 });
            battle.Active(0).Fields.Add(new FieldInst { Id = "shield", Turns = 2, Stacks = 1 });
            OpenRecorded(battle);
            yield return DrainPlayback();
            var view = BattleViewModel.From(battle, 0);
            var skill = view.Skills.First(item => item.Effects.Count > 0);
            var skillParts = new List<string> { skill.Name };
            foreach (var effect in skill.Effects)
            {
                skillParts.Add(effect.Timing);
                skillParts.Add(effect.Text);
                skillParts.Add(effect.Dispel);
            }
            yield return CheckTooltip("battle-skill-" + skill.Index, true, "tooltip-skill", skillParts.ToArray());

            var chips = view.Sides[0].Team[view.Sides[0].Active].Statuses;
            Require(chips.Any(chip => chip.IsStatus) && chips.Any(chip => !chip.IsStatus), "悬停夹具缺少状态或直播间效果。");
            foreach (var chip in chips)
                yield return CheckTooltip("battle-status-0-" + chip.Id, false,
                    chip.IsStatus ? "tooltip-status" : "tooltip-field", chip.Tooltip, chip.Duration, chip.Timing, chip.Description);

            var oldRoot = document.rootVisualElement.Q("battle-hud");
            var oldChip = oldRoot.Q("battle-status-0-flame");
            var oldSkill = oldRoot.Q<Button>("battle-skill-0");
            var oldPanel = oldRoot.Q("battle-tooltip");
            SendPointerEnter(oldChip);
            yield return null;
            Require(Shown(oldPanel), "释放检查前没有显示悬停浮层。");
            Submit("battle-menu");
            yield return null;
            Require(router.CurrentScreenId == ScreenIds.Menu && oldRoot.panel == null, "返回菜单后旧 HUD 没有卸载。");
            SendPointerMove(oldRoot, oldSkill.worldBound.center);
            SendPointerEnter(oldChip);
            using (var focus = FocusInEvent.GetPooled()) { focus.target = oldSkill; oldSkill.SendEvent(focus); }
            yield return new WaitForSecondsRealtime(0.2f);
            Require(!Shown(oldPanel) && document.rootVisualElement.Q("battle-tooltip") == null,
                "旧 HUD 释放后遗留悬停回调重新显示了浮层。");
            report.steps.Add(Size + "：实际悬停浮层显示技能时机/驱散、状态剩余/时机/净化和直播间效果；PP0/受控禁用按钮由根坐标命中，移出隐藏且释放后旧回调无效。");
        }

        IEnumerator CheckTooltip(string targetName, bool moveFromRoot, string screenshot, params string[] expected)
        {
            var root = document.rootVisualElement.Q("battle-hud");
            var target = root?.Q(targetName);
            Require(target != null && Shown(target), "悬停目标不存在或不可见：" + targetName);
            for (var parent = target.parent; parent != null; parent = parent.parent)
                if (parent is ScrollView scroll) { scroll.ScrollTo(target); break; }
            yield return null;
            SendPointerMove(root, new Vector2(root.worldBound.xMin + 1, root.worldBound.yMin + 1));
            yield return new WaitForSecondsRealtime(0.2f);
            if (moveFromRoot) SendPointerMove(root, target.worldBound.center);
            else SendPointerEnter(target);
            yield return null;
            yield return new WaitForEndOfFrame();
            var panel = root.Q("battle-tooltip");
            var text = root.Q<Label>("battle-tooltip-text");
            Require(Shown(panel) && Shown(text) && !string.IsNullOrEmpty(text.text), "运行时悬停没有显示真实浮层：" + targetName);
            Require(text.text == target.tooltip, "浮层显示的内容没有对应当前悬停控件：" + targetName);
            foreach (string part in expected)
                Require(!string.IsNullOrEmpty(part) && text.text.Contains(part), "悬停浮层缺少完整规则信息：" + targetName + " / " + part);
            Require(Contains(document.rootVisualElement.worldBound, panel.worldBound)
                && panel.worldBound.width > 0 && panel.worldBound.height > 0, "悬停浮层超出屏幕或没有尺寸。");
            ValidateLayout(panel, document.rootVisualElement.worldBound);
            Capture(screenshot);
            using (var leave = PointerLeaveEvent.GetPooled()) { leave.target = target; target.SendEvent(leave); }
            SendPointerMove(root, new Vector2(root.worldBound.xMin + 1, root.worldBound.yMin + 1));
            yield return new WaitForSecondsRealtime(0.2f);
            Require(!Shown(panel), "指针移出后悬停浮层没有隐藏：" + targetName);
        }

        static void SendPointerMove(VisualElement root, Vector2 position)
        {
            using (var move = PointerMoveEvent.GetPooled(new Event { type = EventType.MouseMove, mousePosition = position }))
            {
                Require(Vector2.Distance(new Vector2(move.position.x, move.position.y), position) < 0.1f,
                    "指针移动事件没有保留面板坐标。");
                move.target = root;
                root.SendEvent(move);
            }
        }

        static void SendPointerEnter(VisualElement target)
        {
            using (var enter = PointerEnterEvent.GetPooled()) { enter.target = target; target.SendEvent(enter); }
        }

        IEnumerator CheckRematchAndTimer()
        {
            var db = LoadContent();
            db.Skills["smoke_finish"] = new SkillDef
            {
                Id = "smoke_finish", Name = "重开验收", Type = "game", Cat = "status", Pp = 20,
                Effects = { new EffectDef { At = "post", Code = "FIXED_DAMAGE", Target = "foe", N = 10000 } }
            };
            db.Streamers[0].Skills[0] = "smoke_finish";
            var finished = new Battle(db, Teams(db), new[] { "你", "电脑" },
                new BattleOptions { Seed = 71, Modifiers = new List<string> { "no_switch", "hp_x2" } });
            int guard = 0;
            while (finished.Phase != Phase.Over)
            {
                Require(guard++ < 30, "重开夹具未结束。");
                if (finished.Phase == Phase.Choose) finished.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());
                else for (int side = 0; side < 2; side++)
                    if (finished.Need[side]) finished.Replace(side, Ai.ChooseReplacement(finished, side));
            }
            OpenRecorded(finished);
            yield return null;
            Submit("battle-rematch");
            recordedBattle = Current.CurrentBattle;
            checkedCalls = lastEvent = 0;
            Require(recordedBattle != finished && recordedBattle.Phase == Phase.Choose,
                "无禁用/首发配置的重来没有直接进入选择阶段。");
            Require(recording.Calls.Count == 1 && Current.IsBusy,
                "重来时旧页面释放错误地取消了新页面共用动画器的播放。");
            Require(recordedBattle.Modifiers.SequenceEqual(finished.Modifiers)
                && recordedBattle.Active(0).MaxHp == finished.Active(0).MaxHp
                && recordedBattle.LegalFor(0).Switches.Count == 0, "重来丢失了规则修饰器。");
            yield return DrainPlayback();
            Submit("battle-menu");
            yield return null;

            db = LoadContent();
            var timed = new Battle(db, Teams(db), new[] { "你", "电脑" }, 72);
            router.Show(ScreenIds.Battle, new BattleScreenArgs
            {
                Battle = timed, Isolated = true,
                Settings = new GameSettings { TurnTimer = true, DefaultPack = "original" }
            });
            int events = timed.Record.Events.Count;
            Current.Tick(9);
            Require(timed.Record.Events.Count == events, "计时器在 10 秒之前提前提交。");
            Submit("battle-menu");
            yield return null;
            Require(router.CurrentScreenId == ScreenIds.Menu, "计时对局未进入菜单。");
            Submit("back-button");
            yield return null;
            Require(ReferenceEquals(Current.CurrentBattle, timed), "菜单返回没有恢复同一场对局。");
            Current.Tick(2);
            Require(timed.Record.Events.Count > events, "菜单往返重置了剩余选择时间。");
            Submit("battle-menu");
            yield return null;
            report.steps.Add(Size + "：无首发重来不会被旧页取消动画；9 秒后往返菜单再过 2 秒仍触发超时行动。");
        }

        IEnumerator CheckLog()
        {
            var scroll = document.rootVisualElement.Q<ScrollView>("battle-log");
            Require(scroll != null, "缺少可滚动的战斗记录。");
            var first = Button("battle-log-0");
            var last = Button("battle-log-" + (Current.CurrentBattle.Log.Count - 1));
            scroll.ScrollTo(first);
            yield return null;
            Require(Overlaps(scroll.contentViewport.worldBound, first.worldBound), "日志无法滚动到第一行。");
            Submit(first.name);
            yield return null;
            Require(first.ClassListContains("battle-log-selected"), "日志按钮未高亮选中行。");
            scroll.ScrollTo(last);
            yield return null;
            Require(Overlaps(scroll.contentViewport.worldBound, last.worldBound), "日志无法滚动到末行。");
            Submit(last.name);
            yield return null;
            Require(last.ClassListContains("battle-log-selected") && !first.ClassListContains("battle-log-selected"), "日志高亮没有移至末行。");
            yield return new WaitForEndOfFrame();
            Capture("log-scroll");
        }

        Button Button(string name)
        {
            var button = document.rootVisualElement.Q<Button>(name);
            Require(button != null, "找不到按钮：" + name);
            return button;
        }

        void Submit(string name)
        {
            var button = Button(name);
            Require(button.enabledInHierarchy && Shown(button), "按钮不可提交：" + name);
            button.Focus();
            SendSubmit(button);
        }

        static void SendSubmit(Button button)
        {
            using (var submit = NavigationSubmitEvent.GetPooled()) { submit.target = button; button.SendEvent(submit); }
        }

        static bool Shown(VisualElement element)
        {
            if (element == null) return false;
            for (var item = element; item != null; item = item.parent)
                if (!item.visible || item.resolvedStyle.display == DisplayStyle.None) return false;
            return true;
        }

        static bool Overlaps(Rect a, Rect b) => a.xMin < b.xMax && a.xMax > b.xMin && a.yMin < b.yMax && a.yMax > b.yMin;

        void ValidateHud()
        {
            var root = document.rootVisualElement.Q("battle-hud");
            Require(root != null, "缺少正式对战 HUD。");
            var stage = root.Q<BattleStage>();
            Require(stage != null && stage.Q<VisualElement>(StageNames.FxLayer) != null
                && stage.Q<VisualElement>(StageNames.FloatLayer) != null, "舞台缺少动画与特效挂点。");
            foreach (string name in new[] { StageNames.Fighter0, StageNames.Fighter1 })
            {
                var fighter = stage.Q(name);
                Require(fighter != null && fighter.Q<Image>(StageNames.Portrait)?.image != null
                    && fighter.Q<ProgressBar>(StageNames.HpBar) != null, "舞台缺少头像或人气条。");
            }
            Require(root.Q<Label>("battle-timer") != null, "HUD 缺少计时显示。");
            var outcome = root.Q("battle-outcome");
            var tooltip = root.Q("battle-tooltip");
            ValidateLayout(Shown(outcome) ? outcome : Shown(tooltip) ? tooltip : root, document.rootVisualElement.worldBound);
        }

        internal static void ValidateLayout(VisualElement root, Rect screen)
        {
            var elements = new List<VisualElement>();
            Collect(root, elements);
            foreach (var element in elements)
            {
                Rect bounds = element.worldBound;
                Require(Finite(bounds.x) && Finite(bounds.y) && Finite(bounds.width) && Finite(bounds.height)
                    && bounds.width > 0 && bounds.height > 0, "布局无效：" + element.name);
                bool scrolling = false;
                for (var parent = element.parent; parent != null; parent = parent.parent)
                    if (parent is ScrollView) scrolling = true;
                if (!scrolling) Require(Contains(screen, bounds), "控件超出屏幕：" + element.name);
                if (element is TextElement text && !string.IsNullOrEmpty(text.text))
                {
                    var content = text.contentRect;
                    var measured = text.MeasureTextSize(text.text, content.width,
                        text.resolvedStyle.whiteSpace == WhiteSpace.Normal ? VisualElement.MeasureMode.Exactly : VisualElement.MeasureMode.Undefined,
                        0, VisualElement.MeasureMode.Undefined);
                    Require(!text.isElided && measured.x <= content.width + 2 && measured.y <= content.height + 2,
                        "文字被裁切：" + element.name + " " + text.text + " measured=" + measured + " content=" + content);
                }
            }
            for (int a = 0; a < elements.Count; a++)
                for (int b = a + 1; b < elements.Count; b++)
                {
                    if (elements[a].Contains(elements[b]) || elements[b].Contains(elements[a])) continue;
                    Rect first = VisibleBounds(elements[a]), second = VisibleBounds(elements[b]);
                    Require(Mathf.Min(first.xMax, second.xMax) - Mathf.Max(first.xMin, second.xMin) <= 1
                        || Mathf.Min(first.yMax, second.yMax) - Mathf.Max(first.yMin, second.yMin) <= 1,
                        "控件交叠：" + elements[a].name + " / " + elements[b].name);
                }
        }

        static void Collect(VisualElement element, List<VisualElement> output)
        {
            if (!Shown(element)) return;
            Rect visible = VisibleBounds(element);
            if (visible.width <= 0 || visible.height <= 0) return;
            if ((element is TextElement text && !string.IsNullOrEmpty(text.text)) || element is Image || element is ProgressBar)
                output.Add(element);
            foreach (var child in element.Children()) Collect(child, output);
        }

        static Rect VisibleBounds(VisualElement element)
        {
            Rect result = element.worldBound;
            for (var parent = element.parent; parent != null; parent = parent.parent)
            {
                if (!(parent is ScrollView scroll)) continue;
                Rect clip = scroll.contentViewport.worldBound;
                result = Rect.MinMaxRect(Mathf.Max(result.xMin, clip.xMin), Mathf.Max(result.yMin, clip.yMin),
                    Mathf.Min(result.xMax, clip.xMax), Mathf.Min(result.yMax, clip.yMax));
            }
            return result;
        }

        static bool Contains(Rect outer, Rect inner) => inner.xMin >= outer.xMin - 2 && inner.yMin >= outer.yMin - 2
            && inner.xMax <= outer.xMax + 2 && inner.yMax <= outer.yMax + 2;
        static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

        void Capture(string name)
        {
            Texture2D image = null;
            try
            {
                image = ScreenCapture.CaptureScreenshotAsTexture();
                Require(image != null && image.width == RequestedWidth && image.height == RequestedHeight, "截图尺寸不正确。");
                byte[] bytes = image.EncodeToPNG();
                Require(bytes != null && bytes.Length > 8, "截图编码失败。");
                string file = Size + "-" + name + ".png";
                File.WriteAllBytes(Path.Combine(report.outputDirectory, file), bytes);
                report.screenshots.Add(file);
                var pixels = image.GetPixels32();
                int dark = 255, light = 0, bright = 0, sampled = 0;
                for (int index = 0; index < pixels.Length; index += Math.Max(1, pixels.Length / 65536))
                {
                    var color = pixels[index];
                    int value = (299 * color.r + 587 * color.g + 114 * color.b) / 1000;
                    dark = Math.Min(dark, value); light = Math.Max(light, value); sampled++;
                    if (value >= 150) bright++;
                }
                Require(light - dark >= 48 && bright >= Math.Max(8, sampled / 2000), "截图没有有效的图形和文字：" + file);
            }
            finally { if (image != null) Destroy(image); }
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
                if (ReportPath == null) ExitCode = 1;
                else File.WriteAllText(ReportPath, JsonUtility.ToJson(report, true));
            }
            catch (Exception ex) { ExitCode = 1; Debug.LogError("对战界面验收报告写入失败：" + ex); }
            finally
            {
                Application.logMessageReceived -= CollectLog;
                Completed = true;
                if (ExitCode == 0) Debug.Log("BATTLE_UI_SMOKE_PASS " + ReportPath);
                else Debug.LogError("BATTLE_UI_SMOKE_FAIL " + (failure ?? "检测到警告或错误。") + " " + ReportPath);
                if (!Application.isEditor) Application.Quit(ExitCode);
            }
        }
    }
}
