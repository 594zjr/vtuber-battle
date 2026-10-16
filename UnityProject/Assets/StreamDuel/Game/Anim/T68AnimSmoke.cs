using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using StreamDuel.Core;
using UnityEngine;
using UnityEngine.UIElements;

namespace StreamDuel.Game
{
    // 命令行验收复用真实按钮和 Play 帧，不参与正常游戏入口。
    public sealed class T68AnimSmoke : MonoBehaviour
    {
        [Serializable]
        sealed class Report
        {
            public string unityVersion, failure, naturalHash, skippedHash;
            public string input = "NavigationSubmitEvent 驱动真实 UI 按钮；不是人工鼠标验收。";
            public bool completed, concurrentEffects, skipChecked, pauseChecked;
            public int warnings, errors, naturalRounds, skippedRounds, winner;
            public List<string> screenshots = new List<string>();
            public List<string> cueKinds = new List<string>();
            public List<string> messages = new List<string>();
            public List<string> checks = new List<string>();
        }

        public static bool Completed { get; private set; }
        public static int ExitCode { get; private set; }
        public static int RequestedWidth { get; private set; }
        public static int RequestedHeight { get; private set; }
        public static string ReportPath { get; private set; }

        Report report;
        string output;
        ScreenRouter router;
        AnimPreviewScreen preview;
        bool running;
        float deadline;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Install()
        {
            string[] args = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(args, "--anim-smoke-test");
            if (index < 0) return;
            Completed = false;
            ExitCode = 1;
            RequestedWidth = RequestedHeight = 0;
            ReportPath = null;
            var host = new GameObject("StreamDuel.T68AnimSmoke");
            DontDestroyOnLoad(host);
            host.AddComponent<T68AnimSmoke>().Configure(args, index);
        }

        void Configure(string[] args, int index)
        {
            report = new Report { unityVersion = Application.unityVersion };
            running = true;
            deadline = Time.realtimeSinceStartup + 840;
            Application.logMessageReceived += OnLog;
            try
            {
                Require(index + 1 < args.Length && Path.IsPathFullyQualified(args[index + 1]), "缺少绝对验收输出目录。");
                output = Path.GetFullPath(args[index + 1]);
                string allowed = Path.GetFullPath(Path.Combine(Application.dataPath, "../Logs/T68"));
                Require(output == allowed || output.StartsWith(allowed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
                    "验收输出必须在本工程 Logs/T68 内。");
                Directory.CreateDirectory(output);
                ReportPath = Path.Combine(output, "result.json");
            }
            catch (Exception ex) { Finish(ex.ToString()); }
        }

        void Start() { if (running) StartCoroutine(Guard(Run())); }

        void LateUpdate()
        {
            if (running && Time.realtimeSinceStartup >= deadline) Finish("真实渲染验收超过 840 秒。");
        }

        void OnLog(string message, string stack, LogType type)
        {
            if (type == LogType.Warning) report.warnings++;
            if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert) report.errors++;
            if (type != LogType.Log) report.messages.Add(type + ": " + message + "\n" + stack);
        }

        IEnumerator Guard(IEnumerator root)
        {
            var work = new Stack<IEnumerator>();
            work.Push(root);
            while (running)
            {
                bool more = false;
                object current = null;
                string failure = null;
                try
                {
                    more = work.Peek().MoveNext();
                    if (more) current = work.Peek().Current;
                    else work.Pop();
                }
                catch (Exception ex) { failure = ex.ToString(); }
                if (failure != null || work.Count == 0) { Finish(failure); yield break; }
                if (!more) continue;
                if (current is IEnumerator nested) { work.Push(nested); continue; }
                yield return current;
            }
        }

        IEnumerator Run()
        {
            yield return null;
            router = FindAnyObjectByType<ScreenRouter>();
            Require(router != null && router.CurrentScreenId == ScreenIds.Menu, "默认入口未启动。");
            var legacy = router.GetComponent<BattleScreen>();
            Require(legacy != null && !legacy.enabled, "正式菜单启动时旧画面没有停用。");
            yield return Resize(1280, 720);
            yield return new WaitForEndOfFrame();
            ValidateLayout(router.Document.rootVisualElement.Q("menu-screen"));
            Capture("menu");
            yield return ExpandDeveloperTools();
            ValidateLayout(router.Document.rootVisualElement.Q("menu-screen"));
            Submit("open-animation-preview");
            yield return null;
            preview = router.Document.rootVisualElement.Q(ScreenIds.AnimationPreview + "-screen").userData as AnimPreviewScreen;
            Require(preview != null && router.CurrentScreenId == AnimPreviewScreen.ScreenId, "菜单未进入动画预览。");
            yield return WaitForAnimation();
            CheckFinalView();
            yield return new WaitForEndOfFrame();
            Capture("opening");
            ValidateLayout(router.Document.rootVisualElement.Q(ScreenIds.AnimationPreview + "-screen"));

            // 先检查独立阶段，截断 SwitchIn 的红测无需等待自然整局完成。
            var phaseStage = new BattleStage { name = "t68-phase-check-stage" };
            phaseStage.style.position = Position.Absolute;
            phaseStage.style.left = phaseStage.style.top = 0;
            phaseStage.style.width = 1000;
            phaseStage.style.height = 600;
            router.Document.rootVisualElement.Add(phaseStage);
            yield return new WaitForEndOfFrame();
            try { T68PhaseChecks.Run(phaseStage, report.checks); }
            finally { phaseStage.RemoveFromHierarchy(); }
            yield return new WaitForEndOfFrame();

            Submit("preview-speed-half");
            Require(preview.Speed == 0.5f, "0.5 倍速按钮无效。");
            Submit("preview-next");
            yield return WaitForAnimation();
            report.naturalRounds++;
            Submit("preview-speed-normal");
            Require(preview.Speed == 1, "1 倍速按钮无效。");
            Submit("preview-next");
            yield return WaitForAnimation();
            report.naturalRounds++;
            Submit("preview-speed-double");
            Require(preview.Speed == 2, "2 倍速按钮无效。");
            while (preview.Battle.Phase != Phase.Over)
            {
                Require(report.naturalRounds <= Battle.TurnCap + 2, "自然播放没有在回合上限内结束。");
                Submit("preview-next");
                yield return WaitForAnimation();
                report.naturalRounds++;
                CheckFinalView();
                if (report.naturalRounds % 5 == 0) Debug.Log("T68_ANIM_ROUND " + report.naturalRounds);
            }
            report.naturalHash = preview.Battle.StateHash().ToString("x16");
            report.winner = preview.Battle.Winner;
            yield return new WaitForEndOfFrame();
            ValidateLayout(router.Document.rootVisualElement.Q(ScreenIds.AnimationPreview + "-screen"));
            Capture("complete");
            report.checks.Add("固定种子完整对局逐段自然播放，0.5/1/2 倍速均由真实按钮选择。");

            yield return Resize(1920, 1080);
            Submit("preview-restart");
            yield return WaitForAnimation();
            yield return new WaitForEndOfFrame();
            ValidateLayout(router.Document.rootVisualElement.Q(ScreenIds.AnimationPreview + "-screen"));
            Capture("opening");
            Submit("preview-next");
            ulong hash = preview.Battle.StateHash();
            float movementDeadline = Time.realtimeSinceStartup + 10;
            while (Mathf.Abs(preview.Stage.Fighter0.Q<Image>(StageNames.Portrait).style.translate.value.x.value) < 0.1f
                && Mathf.Abs(preview.Stage.Fighter1.Q<Image>(StageNames.Portrait).style.translate.value.x.value) < 0.1f)
            {
                Require(preview.Animator.IsPlaying && Time.realtimeSinceStartup < movementDeadline, "没有观察到可用于暂停验收的头像运动。");
                yield return null;
            }
            Submit("preview-auto");
            Submit("preview-auto");
            Require(preview.IsPaused && !preview.AutoPlayEnabled, "自动播放暂停按钮无效。");
            string pausedVisual = VisualSignature(preview.Stage);
            float pausedUntil = Time.realtimeSinceStartup + 0.25f;
            while (Time.realtimeSinceStartup < pausedUntil)
            {
                yield return null;
                Require(preview.Battle.StateHash() == hash && VisualSignature(preview.Stage) == pausedVisual,
                    "暂停时动画或规则仍在推进。");
            }
            report.pauseChecked = true;
            Submit("preview-skip");
            Require(!preview.Animator.IsPlaying && preview.Battle.StateHash() == hash, "跳过改变了规则状态或未立即结束。");
            CheckFinalView();
            report.skipChecked = true;
            report.skippedRounds = 1;
            while (preview.Battle.Phase != Phase.Over)
            {
                Require(report.skippedRounds <= Battle.TurnCap + 2, "跳过播放没有在回合上限内结束。");
                Submit("preview-next");
                ulong beforeSkip = preview.Battle.StateHash();
                if (preview.Animator.IsPlaying) Submit("preview-skip");
                Require(beforeSkip == preview.Battle.StateHash(), "跳过动画改变了规则状态。");
                CheckFinalView();
                report.skippedRounds++;
                yield return null;
            }
            report.skippedHash = preview.Battle.StateHash().ToString("x16");
            Require(report.naturalHash == report.skippedHash, "重开后全部跳过与自然播放的最终哈希不同。");
            yield return new WaitForEndOfFrame();
            ValidateLayout(router.Document.rootVisualElement.Q(ScreenIds.AnimationPreview + "-screen"));
            Capture("complete");
            var checkStage = new BattleStage { name = "t68-independent-check-stage" };
            checkStage.style.position = Position.Absolute;
            checkStage.style.left = checkStage.style.top = 0;
            checkStage.style.width = 1000;
            checkStage.style.height = 600;
            router.Document.rootVisualElement.Add(checkStage);
            yield return new WaitForEndOfFrame();
            try { report.checks.AddRange(T68AnimatorChecks.Run(preview.Battle.Db, preview.Battle, checkStage)); }
            finally { checkStage.RemoveFromHierarchy(); }
            Submit("back-button");
            yield return null;
            Require(router.CurrentScreenId == ScreenIds.Menu && preview.Animator == null && preview.Battle == null,
                "退出预览未释放播放器/对局。");
            yield return new WaitForEndOfFrame();
            Capture("menu");
            ValidateLayout(router.Document.rootVisualElement.Q("menu-screen"));
            report.checks.Add("重开逐段跳过与自然播放最终哈希一致；每段 HP 终态与 Core 视图一致；返回菜单释放播放器。");
            Require(report.concurrentEffects, "未观察到跳字与闪白并行，需要检查播放调度。");
            yield return CheckMenuPreviewRoundTrip();
            report.completed = true;
        }

        IEnumerator ExpandDeveloperTools()
        {
            var foldout = router.Document.rootVisualElement.Q<Foldout>("developer-tools");
            Require(foldout != null && !foldout.value, "Editor 动画验收缺少默认折叠的开发入口。");
            foldout.value = true;
            yield return null;
            yield return new WaitForEndOfFrame();
            Require(IsVisible(foldout) && foldout.value, "开发折叠区没有实际展开。");
        }

        IEnumerator CheckMenuPreviewRoundTrip()
        {
            var legacy = router.GetComponent<BattleScreen>();
            Require(legacy != null && !legacy.enabled && !legacy.MenuPackLocked,
                "菜单往返检查必须从未锁定的正式菜单开始。");
            string previousPackId = legacy.MenuPack.PackId;
            object previousArgs = router.CurrentArgs;
            object menuArgs = new object();
            int fanIndex = -1;
            for (int i = 0; i < legacy.MenuPacks.Count; i++)
                if (legacy.MenuPacks[i].PackId.StartsWith("fan_", StringComparison.Ordinal))
                {
                    fanIndex = i;
                    break;
                }
            Require(fanIndex >= 0, "菜单往返检查缺少内置同人包。");
            ContentDb fanPack = legacy.MenuPacks[fanIndex];
            try
            {
                router.Show(ScreenIds.Menu, menuArgs);
                yield return null;
                yield return new WaitForEndOfFrame();
                var dropdown = router.Document.rootVisualElement.Q<DropdownField>("menu-pack");
                Require(dropdown != null && dropdown.enabledInHierarchy
                    && dropdown.choices.Count == legacy.MenuPacks.Count, "菜单选包入口不可用。");
                dropdown.value = dropdown.choices[fanIndex];
                yield return null;
                yield return new WaitForEndOfFrame();
                Require(ReferenceEquals(legacy.MenuPack, fanPack)
                    && dropdown.index == fanIndex
                    && router.Document.rootVisualElement.Q<Label>("pack-notice").text == fanPack.PackNotice,
                    "真实下拉选择没有同步同人包与声明。");
                yield return ExpandDeveloperTools();
                Submit("open-animation-preview");
                var oldPage = router.Document.rootVisualElement.Q(ScreenIds.AnimationPreview + "-screen");
                var leaving = oldPage?.userData as AnimPreviewScreen;
                Require(leaving != null && router.CurrentScreenId == ScreenIds.AnimationPreview
                    && leaving.Animator != null && leaving.Animator.IsPlaying && !legacy.enabled,
                    "短往返必须在真实预览开场动画仍播放时进行。");
                Require(leaving.Battle != null && leaving.Battle.Db.PackId == "original",
                    "固定原创预览不应改为菜单选中的同人包。");
                var oldAnimator = leaving.Animator;
                var oldBattle = leaving.Battle;
                ulong oldHash = oldBattle.StateHash();
                string oldRecord = oldBattle.Record.ToJson();
                var oldRestart = oldPage.Q<Button>("preview-restart");
                var oldBack = oldPage.Q<Button>("back-button");
                Require(oldRestart != null && oldBack != null, "短往返缺少真实重开或返回按钮。");

                Submit("back-button");
                Require(router.CurrentScreenId == ScreenIds.Menu && leaving.Animator == null
                    && leaving.Battle == null && !oldAnimator.IsPlaying && oldPage.parent == null,
                    "播放中返回未释放播放器、对局或旧页面。");
                Require(leaving.Stage.FloatLayer.childCount == 0 && leaving.Stage.FxLayer.childCount == 0
                    && leaving.Stage.Q("timeline-flash") == null,
                    "播放中返回仍残留自有临时表现节点。");
                yield return null;
                yield return new WaitForEndOfFrame();

                // 保留旧按钮引用并发送同一种事件，确保卸载后的控件不能重新启动旧对局。
                SendSubmit(oldRestart);
                SendSubmit(oldBack);
                yield return null;
                Require(router.CurrentScreenId == ScreenIds.Menu && leaving.Animator == null
                    && leaving.Battle == null && !oldAnimator.IsPlaying
                    && oldBattle.StateHash() == oldHash && oldBattle.Record.ToJson() == oldRecord,
                    "已卸载预览的旧按钮仍能导航、重开或改变原对局。");
                Require(ReferenceEquals(router.CurrentArgs, menuArgs),
                    "T68 回归：动画预览返回丢失了 ScreenArgs.ReturnArgs。");
                var restoredDropdown = router.Document.rootVisualElement.Q<DropdownField>("menu-pack");
                var restoredNotice = router.Document.rootVisualElement.Q<Label>("pack-notice");
                Require(!legacy.enabled && ReferenceEquals(legacy.MenuPack, fanPack)
                    && restoredDropdown != null && restoredDropdown.index == fanIndex
                    && restoredNotice != null && restoredNotice.text == fanPack.PackNotice,
                    "动画预览往返改变了菜单同人包选择、声明或旧画面停用状态。");
                ValidateLayout(router.Document.rootVisualElement.Q("menu-screen"));
                report.checks.Add("T81 整合：同人包与菜单参数经真实开发按钮进入固定原创预览；播放中返回释放对象、旧按钮失效，包选择/声明/参数保持。");
            }
            finally
            {
                legacy.SelectMenuPack(previousPackId);
                router.Show(ScreenIds.Menu, previousArgs);
            }
            yield return null;
            yield return new WaitForEndOfFrame();
            Require(!legacy.enabled && legacy.MenuPack.PackId == previousPackId
                && ReferenceEquals(router.CurrentArgs, previousArgs), "短往返检查未还原原菜单上下文。");
        }

        IEnumerator WaitForAnimation()
        {
            foreach (var cue in preview.CurrentCues)
                if (!report.cueKinds.Contains(cue.Kind.ToString())) report.cueKinds.Add(cue.Kind.ToString());
            ulong hash = preview.Battle.StateHash();
            bool captured = report.screenshots.Exists(name => name.EndsWith("-hit.png", StringComparison.Ordinal));
            while (preview.Animator.IsPlaying)
            {
                Require(hash == preview.Battle.StateHash(), "只播放动画时规则状态发生改变。");
                bool concurrent = preview.Stage.FloatLayer.childCount > 0 && preview.Stage.Q("timeline-flash") != null;
                report.concurrentEffects |= concurrent;
                if (concurrent && !captured)
                {
                    yield return new WaitForEndOfFrame();
                    Capture("hit");
                    captured = true;
                }
                yield return null;
            }
            Require(hash == preview.Battle.StateHash(), "播放结束时规则状态发生改变。");
            CheckFinalView();
        }

        void CheckFinalView()
        {
            var view = BattleViewModel.From(preview.Battle, 0);
            for (int side = 0; side < 2; side++)
            {
                var fighter = view.Sides[side].Team[view.Sides[side].Active];
                var root = side == 0 ? preview.Stage.Fighter0 : preview.Stage.Fighter1;
                var fill = root.Q("timeline-hp-fill");
                var trail = root.Q("timeline-hp-trail");
                float expected = fighter.Hp / (float)fighter.MaxHp;
                Require(fill != null && trail != null && Mathf.Abs(fill.style.scale.value.value.x - expected) < 0.0001f
                    && Mathf.Abs(trail.style.scale.value.value.x - expected) < 0.0001f
                    && root.Q<Label>("timeline-hp-value").text == fighter.Hp + " / " + fighter.MaxHp,
                    "人气条终态不匹配 Core：" + side + "，HP=" + fighter.Hp + "/" + fighter.MaxHp);
                var portrait = root.Q<Image>(StageNames.Portrait);
                Require(root.Q<Label>("timeline-fighter-name").text == fighter.Name && portrait.image != null
                    && portrait.tooltip == fighter.Name && root.style.translate.value.x.value == 0
                    && root.style.opacity.value == 1 && portrait.style.translate.value.x.value == 0
                    && portrait.style.translate.value.y.value == (fighter.Fainted ? 24 : 0)
                    && portrait.style.opacity.value == (fighter.Fainted ? 0 : 1), "当前头像/名字/姿态终态不匹配 Core。");
                var expectedChips = new HashSet<string>(StringComparer.Ordinal);
                foreach (var chip in fighter.Statuses)
                    expectedChips.Add("timeline-icon-" + (chip.IsStatus ? "status/" : "field/") + chip.Id);
                var actualChips = new HashSet<string>(StringComparer.Ordinal);
                foreach (var chip in root.Q(StageNames.StatusRow).Children()) actualChips.Add(chip.name);
                Require(expectedChips.SetEquals(actualChips), "状态图标终态不匹配 Core。");
            }
            Require(preview.Stage.FloatLayer.childCount == 0 && preview.Stage.FxLayer.childCount == 0
                && preview.Stage.Q("timeline-flash") == null, "终态仍残留临时表现节点。");
        }

        static string VisualSignature(VisualElement element)
        {
            var signature = new StringBuilder();
            void Add(VisualElement current)
            {
                signature.Append(current.name).Append('|').Append(current.style.translate.value.x.value)
                    .Append('|').Append(current.style.translate.value.y.value)
                    .Append('|').Append(current.style.scale.value.value.x).Append('|').Append(current.style.scale.value.value.y)
                    .Append('|').Append(current.style.opacity.value)
                    .Append('|').Append(current.childCount).Append('|');
                if (current is TextElement text) signature.Append(text.text);
                if (current is Image image) signature.Append(image.tintColor);
                foreach (var child in current.Children()) Add(child);
            }
            Add(element);
            return signature.ToString();
        }

        IEnumerator Resize(int width, int height)
        {
            RequestedWidth = width;
            RequestedHeight = height;
            float limit = Time.realtimeSinceStartup + 20;
            while (Screen.width != width || Screen.height != height)
            {
                Require(Time.realtimeSinceStartup < limit, "GameView 无法切换到指定分辨率。");
                yield return null;
            }
            yield return null;
        }

        void Submit(string name)
        {
            var button = router.Document.rootVisualElement.Q<Button>(name);
            Require(button != null && button.enabledInHierarchy && button.visible && IsVisible(button), "按钮不可用：" + name);
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

        static bool Contains(Rect outer, Rect inner)
        {
            return inner.xMin >= outer.xMin - 2 && inner.yMin >= outer.yMin - 2
                && inner.xMax <= outer.xMax + 2 && inner.yMax <= outer.yMax + 2;
        }

        static bool IsVisible(VisualElement element)
        {
            if (element == null) return false;
            // Foldout 的逻辑父链会越过内部隐藏容器，须检查物理祖先链。
            for (var ancestor = element; ancestor != null; ancestor = ancestor.hierarchy.parent)
                if (!ancestor.visible || ancestor.resolvedStyle.display == DisplayStyle.None
                    || ancestor.resolvedStyle.opacity == 0) return false;
            return true;
        }

        void ValidateLayout(VisualElement page)
        {
            Require(page != null, "界面根节点缺失。");
            var elements = page.Query<TextElement>().ToList();
            foreach (var text in elements)
            {
                if (!IsVisible(text) || !text.visible || text.resolvedStyle.display == DisplayStyle.None || text.resolvedStyle.opacity == 0
                    || string.IsNullOrEmpty(text.text)) continue;
                Rect bounds = text.worldBound;
                Require(Contains(router.Document.rootVisualElement.worldBound, bounds), "文字超出屏幕：" + text.name + " " + text.text);
                for (var ancestor = text.hierarchy.parent; ancestor != null; ancestor = ancestor.hierarchy.parent)
                    if (ancestor.style.overflow.value == Overflow.Hidden || ancestor is ScrollView)
                        Require(Contains(ancestor.worldBound, bounds), "文字被祖先裁切：" + text.name + " / " + ancestor.name);
                Vector2 measured = text.MeasureTextSize(text.text, text.contentRect.width,
                    text.resolvedStyle.whiteSpace == WhiteSpace.Normal ? VisualElement.MeasureMode.Exactly : VisualElement.MeasureMode.Undefined,
                    0, VisualElement.MeasureMode.Undefined);
                Require(!text.isElided && measured.x <= text.contentRect.width + 2 && measured.y <= text.contentRect.height + 2,
                    "文字裁切：" + text.name + " " + text.text + "，测量=" + measured + " 内容=" + text.contentRect);
            }
            var buttons = page.Query<Button>().ToList().FindAll(button => IsVisible(button));
            for (int i = 0; i < buttons.Count; i++)
                for (int j = i + 1; j < buttons.Count; j++)
                    Require(!buttons[i].worldBound.Overlaps(buttons[j].worldBound), "按钮重叠。");
            report.checks.Add(Screen.width + "×" + Screen.height + " " + page.name + "：可见文字边界/测量与按钮间不重叠检查通过。");
        }

        void Capture(string name)
        {
            var texture = ScreenCapture.CaptureScreenshotAsTexture();
            try
            {
                Require(texture != null && texture.width == RequestedWidth && texture.height == RequestedHeight, "截图尺寸错误。");
                string filename = RequestedWidth + "x" + RequestedHeight + "-" + name + ".png";
                File.WriteAllBytes(Path.Combine(output, filename), texture.EncodeToPNG());
                report.screenshots.Add(filename);
                Color32[] pixels = texture.GetPixels32();
                int dark = 255, light = 0;
                for (int i = 0; i < pixels.Length; i += Math.Max(1, pixels.Length / 65536))
                {
                    int value = (299 * pixels[i].r + 587 * pixels[i].g + 114 * pixels[i].b) / 1000;
                    dark = Math.Min(dark, value);
                    light = Math.Max(light, value);
                }
                Require(light - dark >= 48, "截图没有有效画面。");
            }
            finally { if (texture != null) Destroy(texture); }
        }

        static void Require(bool value, string message)
        {
            if (!value) throw new InvalidOperationException(message);
        }

        void Finish(string failure)
        {
            if (!running) return;
            running = false;
            report.failure = failure;
            if (failure != null) report.completed = false;
            ExitCode = report.completed && report.errors == 0 && report.warnings == 0 ? 0 : 1;
            Application.logMessageReceived -= OnLog;
            if (ReportPath != null) File.WriteAllText(ReportPath, JsonUtility.ToJson(report, true));
            Completed = true;
            if (ExitCode == 0) Debug.Log("T68_ANIM_SMOKE_PASS " + ReportPath);
            else Debug.LogError("T68_ANIM_SMOKE_FAIL " + failure + " " + ReportPath);
        }
    }
}
