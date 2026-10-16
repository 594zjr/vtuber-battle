using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using StreamDuel.Core;
using UnityEngine;
using UnityEngine.UIElements;

namespace StreamDuel.Game
{
    // 验收入口只在显式命令行参数下安装，按钮仍经过真实 UI 事件和 Play 帧。
    public sealed class T69FxSmoke : MonoBehaviour
    {
        [Serializable]
        sealed class Report
        {
            public string unityVersion, failure, battleHash, replayHash;
            public string input = "NavigationSubmitEvent 驱动真实按钮；非人工鼠标验收。";
            public bool completed;
            public int errors, warnings, rounds, winner;
            public List<string> checks = new List<string>();
            public List<string> screenshots = new List<string>();
            public List<string> messages = new List<string>();
            public List<string> effectMasks = new List<string>();
        }

        public static bool Completed { get; private set; }
        public static int ExitCode { get; private set; }
        public static int RequestedWidth { get; private set; }
        public static int RequestedHeight { get; private set; }
        public static string ReportPath { get; private set; }

        Report report;
        ScreenRouter router;
        FxPreviewScreen preview;
        string output;
        bool running;
        float deadline;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Install()
        {
            string[] args = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(args, "--fx-smoke-test");
            if (index < 0) return;
            Completed = false;
            ExitCode = 1;
            RequestedWidth = RequestedHeight = 0;
            ReportPath = null;
            var host = new GameObject("StreamDuel.T69FxSmoke");
            DontDestroyOnLoad(host);
            host.AddComponent<T69FxSmoke>().Configure(args, index);
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
                string allowed = Path.GetFullPath(Path.Combine(Application.dataPath, "../Logs/T69"));
                Require(output == allowed || output.StartsWith(allowed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
                    "验收输出必须在本工程 Logs/T69 内。");
                Directory.CreateDirectory(output);
                ReportPath = Path.Combine(output, "result.json");
            }
            catch (Exception ex) { Finish(ex.ToString()); }
        }

        void Start() { if (running) StartCoroutine(Guard(Run())); }

        void LateUpdate()
        {
            if (running && Time.realtimeSinceStartup >= deadline) Finish("真实特效验收超过 840 秒。");
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
            Require(router != null && router.CurrentScreenId == ScreenIds.Menu, "正式菜单没有启动。");
            var legacy = router.GetComponent<BattleScreen>();
            Require(legacy != null && !legacy.enabled, "菜单打开时旧画面未停用。");
            yield return Resize(1280, 720);
            var menuArgs = new object();
            router.Show(ScreenIds.Menu, menuArgs);
            yield return null;
            yield return ExpandDeveloperTools();
            Require(router.Document.rootVisualElement.Q<Button>("open-animation-preview") != null
                && router.Document.rootVisualElement.Q<Button>("open-effect-preview") != null, "动画与特效预览入口没有并列提供。");
            ValidateLayout(router.Document.rootVisualElement.Q("menu-screen"));
            Capture("menu-developer");
            Submit("open-effect-preview");
            yield return null;
            BindPreview();
            string contentHash = preview.Content.ContentHash();
            yield return CheckEffects();
            yield return CheckIconsAndPortraits();
            Require(preview.Content.ContentHash() == contentHash, "特效、图标或头像修改了内容数据。");

            foreach (var size in new[] { new Vector2Int(1280, 720), new Vector2Int(1920, 1080) })
            {
                yield return Resize(size.x, size.y);
                preview.ClearEffects();
                yield return ScrollPreviewTo(preview.Page.Q("fx-icon-controls"));
                Submit("fx-turns-3");
                CheckBadges("3", true);
                preview.Page.Q<ScrollView>("fx-scroll").scrollOffset = Vector2.zero;
                yield return new WaitForEndOfFrame();
                Submit("fx-play-all");
                foreach (var player in preview.Players.Values) player.Tick(preview.Duration * 0.4f);
                yield return new WaitForEndOfFrame();
                Require(preview.Players.Count == 8 && preview.Players.Values.All(player => player.ActiveCount > 0), "全部播放没有同时呈现八种特效。");
                ValidateLayout(preview.Page);
                Capture("fx-overview");
                foreach (string id in ArtManifest.FxIds)
                {
                    Submit("fx-clear");
                    Require(preview.Players.Values.All(player => player.ActiveCount == 0), "清除按钮残留活动特效。");
                    yield return ScrollPreviewTo(preview.EffectCards[id]);
                    Submit("fx-play-" + id);
                    preview.Players[id].Tick(preview.Duration * 0.4f);
                    yield return new WaitForEndOfFrame();
                    Require(preview.Players[id].ActiveCount == 1, "逐个播放按钮没有启动指定特效：" + id);
                    ValidateLayout(preview.Page);
                    Capture("fx-" + id);
                }
                Submit("fx-clear");
                yield return ScrollPreviewTo(preview.Page.Q("fx-icon-controls"));
                Submit("fx-turns-9");
                CheckBadges("9", true);
                yield return ScrollPreviewTo(preview.Page.Q("fx-icons-grid"));
                ValidateLayout(preview.Page);
                Capture("icons-nine");
                yield return ScrollPreviewTo(preview.Page.Q("fx-icon-controls"));
                Submit("fx-turns-none");
                CheckBadges("", false);
            }
            Require(preview.Content.ContentHash() == contentHash, "预览操作修改了内容数据。");
            report.checks.Add("两分辨率顶部特效总览、八卡片逐个滚至可见后固定中段截图及八图标分区截图，3/9/无数字按钮与布局检查通过。");

            var oldPage = preview.Page;
            var oldPlayers = preview.Players.Values.ToArray();
            var oldPlay = oldPage.Q<Button>("fx-play-all");
            Submit("fx-play-all");
            Submit("fx-open-animation");
            yield return null;
            Require(router.CurrentScreenId == ScreenIds.AnimationPreview && oldPage.parent == null
                && oldPlayers.All(player => player.ActiveCount == 0), "进入动画预览未释放旧特效页面。");
            SendSubmit(oldPlay);
            Require(oldPlayers.All(player => player.ActiveCount == 0), "已卸载特效页的旧按钮仍能启动播放。");
            yield return CompleteBattle();
            Submit("back-button");
            yield return null;
            Require(router.CurrentScreenId == ScreenIds.EffectPreview, "动画预览没有返回特效预览。");
            BindPreview();
            Submit("fx-back");
            yield return null;
            Require(router.CurrentScreenId == ScreenIds.Menu && ReferenceEquals(router.CurrentArgs, menuArgs)
                && !legacy.enabled, "预览往返丢失菜单参数或启用了旧画面。");
            yield return new WaitForEndOfFrame();
            ValidateLayout(router.Document.rootVisualElement.Q("menu-screen"));
            Capture("menu-return");
            report.checks.Add("真实菜单→特效→动画→特效→菜单导航、播放中卸载清理和旧按钮失效通过。");
            report.completed = true;
        }

        void BindPreview()
        {
            Require(router.CurrentScreenId == ScreenIds.EffectPreview, "未进入特效预览。");
            preview = router.Document.rootVisualElement.Q(ScreenIds.EffectPreview + "-screen")?.userData as FxPreviewScreen;
            Require(preview != null && preview.Content != null && preview.Players.Count == 8 && preview.EffectCards.Count == 8,
                "特效预览未提供完整内容与八张卡片。");
            preview.Paused = true;
        }

        IEnumerator CheckEffects()
        {
            var surface = new VisualElement { name = "t69-isolated-surface", pickingMode = PickingMode.Ignore };
            surface.style.position = Position.Absolute;
            surface.style.left = surface.style.top = surface.style.right = surface.style.bottom = 0;
            surface.style.backgroundColor = Color.black;
            var from = Anchor("t69-caster", 25);
            var to = Anchor("t69-target", 75);
            surface.Add(from);
            surface.Add(to);
            var layer = new VisualElement { name = "t69-owned-layer", pickingMode = PickingMode.Ignore };
            layer.style.position = Position.Absolute;
            layer.style.left = layer.style.top = layer.style.right = layer.style.bottom = 0;
            surface.Add(layer);
            router.Document.rootVisualElement.Add(surface);
            int colorCalls = 0;
            var fx = new ProceduralFx(layer, element =>
            {
                Require(ReferenceEquals(element, from), "颜色解析收到的不是施放方。");
                colorCalls++;
                return Color.magenta;
            }, false);
            var foreign = new VisualElement { name = "t69-foreign" };
            layer.Add(foreign);
            try
            {
                yield return new WaitForEndOfFrame();
                fx.Play("slash", from, to, 0);
                Require(fx.ActiveCount == 0 && fx.CreatedCount == 0 && colorCalls == 0, "零时长创建了元素或解析颜色。");
                fx.Play("slash", from, to, 1);
                var first = layer.Q("procedural-fx-slash");
                Require(first != null && colorCalls == 1, "特效没有生成节点或施放方颜色不是每次解析一次。");
                fx.Tick(0.25f);
                Require(fx.ActiveCount == 1 && first.parent == layer, "特效早于传入时长结束。");
                fx.Tick(0.75f);
                Require(fx.ActiveCount == 0 && fx.PooledCount == 1 && first.parent == null, "传入时长结束后没有回收。");
                fx.Play("heal", from, to, 1);
                Require(ReferenceEquals(layer.Q("procedural-fx-heal"), first) && fx.CreatedCount == 1, "不同特效没有复用回收元素。");
                fx.Clear();
                fx.Play("beam", from, to, 0.5f);
                fx.Play("wave", from, to, 1);
                fx.Tick(0.5f);
                Require(fx.ActiveCount == 1 && layer.Q("procedural-fx-wave") != null && layer.Q("procedural-fx-beam") == null,
                    "并发特效没有独立按各自时长结束。");
                fx.Tick(0.5f);
                Require(fx.ActiveCount == 0 && fx.CreatedCount == 2 && fx.PooledCount == 2, "并发播放未正常归池。");
                var masks = new HashSet<ulong>();
                foreach (string id in ArtManifest.FxIds)
                {
                    fx.Play(id, from, to, 1);
                    fx.Tick(0.4f);
                    yield return new WaitForEndOfFrame();
                    var texture = ScreenCapture.CaptureScreenshotAsTexture();
                    try
                    {
                        int colored = 0;
                        ulong mask = 14695981039346656037UL;
                        foreach (var pixel in texture.GetPixels32())
                        {
                            bool match = pixel.r > 24 && pixel.b > 24 && pixel.r > pixel.g * 1.5f && pixel.b > pixel.g * 1.5f;
                            if (match) colored++;
                            mask = unchecked((mask ^ (match ? 1UL : 0UL)) * 1099511628211UL);
                        }
                        Require(colored >= 12, "特效没有实际绘出施放方颜色：" + id);
                        Require(masks.Add(mask), "两种特效的真实渲染形状相同：" + id);
                        report.effectMasks.Add(id + ":" + mask.ToString("x16") + ":pixels=" + colored);
                    }
                    finally { if (texture != null) Destroy(texture); }
                    fx.Tick(0.6f);
                    Require(fx.ActiveCount == 0 && foreign.parent == layer, "特效结束误删外来节点。");
                }
                foreach (string id in ArtManifest.FxIds) fx.Play(id, from, to, 1);
                Require(fx.ActiveCount == 8 && fx.CreatedCount == 8, "八特效并发没有独立节点。");
                fx.Clear();
                Require(fx.ActiveCount == 0 && fx.PooledCount == 8 && layer.childCount == 1 && foreign.parent == layer,
                    "清除没有归池或误删外来节点。");
                fx.Play("ring", from, to, 1);
                layer.Q("procedural-fx-ring").RemoveFromHierarchy();
                fx.Tick(0);
                Require(fx.ActiveCount == 0 && fx.PooledCount == 8, "被拥有者移除的节点没有回收。");
                fx.Play("shield", from, to, 1);
                layer.RemoveFromHierarchy();
                Require(fx.ActiveCount == 0 && foreign.parent == layer, "特效层卸载没有只回收自有元素。");
                fx.Dispose();
                fx.Dispose();
                Require(foreign.parent == layer, "释放播放器误删外来节点。");
                report.checks.Add("八种特效真实GPU形状互异且使用施放方颜色；零时长、独立时长、八并发、跨种类池复用、外来节点保留及卸载清理通过。");
            }
            finally { fx.Dispose(); surface.RemoveFromHierarchy(); }
            yield return new WaitForEndOfFrame();
        }

        static VisualElement Anchor(string name, float percent)
        {
            var element = new VisualElement { name = name, pickingMode = PickingMode.Ignore };
            element.style.position = Position.Absolute;
            element.style.left = Length.Percent(percent);
            element.style.top = Length.Percent(50);
            element.style.width = element.style.height = 48;
            element.style.backgroundColor = new Color(0.15f, 0.15f, 0.15f);
            return element;
        }

        IEnumerator CheckIconsAndPortraits()
        {
            var icons = new ProceduralIcons();
            foreach (string id in ArtManifest.IconIds)
            {
                var icon = icons.Make(id, "剩余三回合", 3);
                Require(icon.Q("procedural-icon-glyph").ClassListContains("procedural-icon-" + id), "图标没有匹配规范几何类型：" + id);
                var badge = icon.Q<Label>("procedural-icon-remaining");
                Require(badge.text == "3" && badge.style.display.value == DisplayStyle.Flex, "图标剩余数字没有显示。");
                ProceduralIcons.UpdateRemaining(icon, 0);
                Require(badge.text == "0" && badge.style.display.value == DisplayStyle.Flex, "零回合数字被隐藏。");
                ProceduralIcons.UpdateRemaining(icon, null);
                Require(badge.text == "" && badge.style.display.value == DisplayStyle.None, "无时长仍显示旧数字。");
                ProceduralIcons.UpdateRemaining(icon, -1);
                Require(badge.style.display.value == DisplayStyle.None, "永久效果显示了负数数字。");
                var inferred = icons.Make(id, "文字包含 8 不能作为剩余次数");
                Require(inferred.Q<Label>("procedural-icon-remaining").style.display.value == DisplayStyle.None,
                    "图标从显示说明自行猜测剩余次数。");
            }
            Require(icons.Make("not-an-icon", null).Q("procedural-icon-glyph").ClassListContains("procedural-icon-unknown"),
                "未知图标没有安全占位。");
            var rules = Resources.Load<TextAsset>("StreamDuelRules");
            var original = Resources.Load<TextAsset>("Packs/original");
            string art = "\"art\":{\"portraitDir\":\"Portraits/T69Missing\",\"streamers\":{\"bell\":{\"accent\":\"#33ff66\",\"portrait\":\"alternate\"}},"
                + "\"skillFx\":{\"full_volume\":\"ring\"},\"statusIcons\":{\"stall\":\"heart\"}},";
            var mapped = ContentDb.FromJson(rules.text, "{" + art + original.text.TrimStart().Substring(1));
            Require(mapped.Art.PortraitPath("bell") == "Portraits/T69Missing/alternate" && mapped.Art.Accent("bell") == "#33ff66"
                && mapped.Art.SkillFx("full_volume") == "ring", "自定义资源清单没有保留路径、强调色和技能映射。");
            var mappedIcon = new ProceduralIcons(mapped).Make("stall", "回合数", 2);
            Require(mappedIcon.Q("procedural-icon-glyph").ClassListContains("procedural-icon-heart"), "状态图标没有消费内容包清单映射。");
            var portraits = new ProceduralPortraitProvider();
            Texture2D first = null, changed = null;
            try
            {
                first = portraits.Get(preview.Content, "bell");
                changed = portraits.Get(mapped, "bell");
                Require(first != null && first.width == 128 && first.height == 128 && changed != null
                    && ReferenceEquals(first, portraits.Get(preview.Content, "bell")) && !ReferenceEquals(first, changed),
                    "头像缺失资源回退、缓存或清单缓存键不正确。");
                var defaultPixels = ReadTexture(first);
                var mappedPixels = ReadTexture(changed);
                Require(defaultPixels.Distinct().Count() >= 4 && !defaultPixels.SequenceEqual(mappedPixels),
                    "头像没有几何图形或没有使用资源清单强调色。");
                Require(mapped.ContentHash() == preview.Content.ContentHash(), "仅替换美术资源映射改变了内容指纹。");
            }
            finally { portraits.Dispose(); }
            yield return null;
            Require(first == null && changed == null, "头像提供者释放后仍保留生成纹理。");
            report.checks.Add("八图标规范类型与badge的3/0/空/永久状态、未知占位、清单状态映射通过；头像128像素GPU回退/缓存/强调色与释放通过，未假称验证不存在的外部图片资源。");
        }

        static Color32[] ReadTexture(Texture texture)
        {
            var temporary = RenderTexture.GetTemporary(texture.width, texture.height, 0, RenderTextureFormat.ARGB32);
            var previous = RenderTexture.active;
            Texture2D copy = null;
            try
            {
                Graphics.Blit(texture, temporary);
                RenderTexture.active = temporary;
                copy = new Texture2D(texture.width, texture.height, TextureFormat.RGBA32, false);
                copy.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0);
                copy.Apply();
                return copy.GetPixels32();
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(temporary);
                if (copy != null) Destroy(copy);
            }
        }

        void CheckBadges(string expected, bool visible)
        {
            foreach (string id in ArtManifest.IconIds)
            {
                var icon = preview.Page.Q("fx-icon-" + id);
                var badge = icon?.Q<Label>("procedural-icon-remaining");
                Require(badge != null && badge.text == expected && (badge.style.display.value != DisplayStyle.None) == visible,
                    "预览数字按钮没有同步全部图标：" + id);
            }
        }

        IEnumerator ScrollPreviewTo(VisualElement target)
        {
            var scroll = preview.Page.Q<ScrollView>("fx-scroll");
            Require(scroll != null && target != null, "特效预览滚动目标不存在。");
            scroll.ScrollTo(target);
            yield return null;
            yield return new WaitForEndOfFrame();
            Require(Contains(scroll.contentViewport.worldBound, target.worldBound), "滚动后目标仍未完整可见：" + target.name);
        }

        IEnumerator CheckTimelineClock(Battle source)
        {
            string signature = BattleSignature(source);
            var stage = new BattleStage { name = "t69-clock-check-stage" };
            stage.style.position = Position.Absolute;
            stage.style.left = stage.style.top = 0;
            stage.style.width = 1000;
            stage.style.height = 600;
            router.Document.rootVisualElement.Add(stage);
            var fx = new ProceduralFx(stage.FxLayer, ignored => Color.magenta, false);
            TimelineAnimator animator = null;
            try
            {
                yield return new WaitForEndOfFrame();
                float activeElapsed = 0;
                animator = new TimelineAnimator(stage, source.Db, fx: fx, skillFx: ignored => "beam", tickFx: seconds =>
                {
                    if (fx.ActiveCount > 0) activeElapsed += seconds;
                    fx.Tick(seconds);
                });
                var view = BattleViewModel.From(source, 0);
                animator.Prepare(source, view, view);
                animator.Play(new[]
                {
                    new AnimCue { Kind = CueKind.Projectile, Start = 0.35, Duration = 0.25,
                        Side = 0, Slot = view.Sides[0].Active, Id = "clock-display", EventIndex = -1, LogIndex = -1 },
                    // 保留后续提示，防止整条时间线清理掩盖特效自身没有按时结束的问题。
                    new AnimCue { Kind = CueKind.End, Start = 1, Duration = 0, Side = -1, EventIndex = -1, LogIndex = -1 }
                });
                Require(fx.ActiveCount == 0, "延迟特效在提示起点前已创建。");
                animator.Tick(0.4f);
                Require(fx.ActiveCount == 1 && Math.Abs(activeElapsed - 0.05f) < 0.00001f,
                    "跨过 0.35 秒起点的一帧，没有仅推进新特效 0.05 秒。");
                animator.Tick(0.21f);
                Require(animator.IsPlaying && fx.ActiveCount == 0 && fx.PooledCount == 1,
                    "跨提示起点后的特效超过自身 0.25 秒仍残留，或靠整条时间线清理才消失。");
                Require(BattleSignature(source) == signature, "跨提示起点展示检查修改了真实对局。");
                report.checks.Add("真实 ProceduralFx 跨 0.35 秒起点：Tick(0.4) 仅推进 0.05 秒，随后 Tick(0.21) 独立归池；后续时间线仍播放，真实对局不变。");
            }
            finally
            {
                animator?.Dispose();
                fx.Dispose();
                stage.RemoveFromHierarchy();
            }
            yield return new WaitForEndOfFrame();
        }

        IEnumerator CompleteBattle()
        {
            var animation = router.Document.rootVisualElement.Q(ScreenIds.AnimationPreview + "-screen")?.userData as AnimPreviewScreen;
            Require(animation != null && animation.Battle != null, "动画入口未创建真实 Core 对局。");
            Submit("preview-speed-double");
            yield return WaitForAnimation(animation);
            yield return CheckTimelineClock(animation.Battle);
            foreach (var size in new[] { new Vector2Int(1280, 720), new Vector2Int(1920, 1080) })
            {
                yield return Resize(size.x, size.y);
                yield return new WaitForEndOfFrame();
                ValidateLayout(router.Document.rootVisualElement.Q(ScreenIds.AnimationPreview + "-screen"));
                Capture("core-ai-opening");
            }
            while (animation.Battle.Phase != Phase.Over)
            {
                Require(report.rounds < Battle.TurnCap + 2, "Core AI 完整对局超过回合上限。");
                Submit("preview-next");
                yield return WaitForAnimation(animation);
                report.rounds++;
                if (report.rounds % 10 == 0) Debug.Log("T69_CORE_AI_ROUND " + report.rounds);
            }
            var battle = animation.Battle;
            report.battleHash = battle.StateHash().ToString("x16");
            report.winner = battle.Winner;
            string record = battle.Record.ToJson();
            string signature = BattleSignature(battle);
            var replayed = Replay.Run(battle.Db, BattleRecord.FromJson(record));
            report.replayHash = replayed.StateHash().ToString("x16");
            Require(report.battleHash == report.replayHash && replayed.Record.ToJson() == record
                && BattleSignature(replayed) == signature && BattleSignature(battle) == signature, "完整AI局与独立重放的哈希、记录或日志事件不一致。");
            File.WriteAllText(Path.Combine(output, "complete-battle.json"), record);
            yield return new WaitForEndOfFrame();
            ValidateLayout(router.Document.rootVisualElement.Q(ScreenIds.AnimationPreview + "-screen"));
            Capture("core-ai-complete");
            report.checks.Add("Editor Play真实按钮推进普通AI完整局；每段自然动画前后哈希/录像/完整日志与事件不变，最终独立Replay.Run逐项一致。");
        }

        IEnumerator WaitForAnimation(AnimPreviewScreen animation)
        {
            string before = BattleSignature(animation.Battle);
            ulong hash = animation.Battle.StateHash();
            float limit = Time.realtimeSinceStartup + 60;
            while (animation.Animator.IsPlaying)
            {
                Require(Time.realtimeSinceStartup < limit && animation.Battle.StateHash() == hash, "表现播放超时或改变了规则/RNG。");
                yield return null;
            }
            Require(BattleSignature(animation.Battle) == before, "表现播放改变了录像、日志或结构化事件。");
        }

        static string BattleSignature(Battle battle)
        {
            var result = new StringBuilder(battle.StateHash().ToString("x16")).Append(battle.Record.ToJson());
            foreach (var log in battle.Log)
            {
                result.Append('|').Append(log.Turn).Append('|').Append(log.Side).Append('|').Append(log.Node)
                    .Append('|').Append(log.Kind).Append('|').Append(log.Text);
                if (log.SnapActive != null) result.Append('|').Append(string.Join(",", log.SnapActive));
                if (log.SnapHp != null) foreach (var side in log.SnapHp) result.Append('|').Append(string.Join(",", side));
            }
            foreach (var entry in battle.Events)
                result.Append('|').Append(entry.LogIndex).Append('|').Append(entry.Turn).Append('|').Append(entry.Node)
                    .Append('|').Append(entry.Kind).Append('|').Append(entry.Side).Append('|').Append(entry.Slot)
                    .Append('|').Append(entry.Id).Append('|').Append(entry.Amount);
            return result.ToString();
        }

        IEnumerator ExpandDeveloperTools()
        {
            var foldout = router.Document.rootVisualElement.Q<Foldout>("developer-tools");
            Require(foldout != null, "缺少开发预览菜单。");
            foldout.value = true;
            yield return null;
            yield return new WaitForEndOfFrame();
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
            Require(button != null && button.enabledInHierarchy && IsVisible(button), "按钮不可用：" + name);
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
            for (var current = element; current != null; current = current.hierarchy.parent)
                if (!current.visible || current.resolvedStyle.display == DisplayStyle.None || current.resolvedStyle.opacity == 0) return false;
            return element != null;
        }

        void ValidateLayout(VisualElement page)
        {
            Require(page != null, "界面根节点缺失。");
            var viewports = page.Query<ScrollView>().ToList().Select(scroll => scroll.contentViewport).ToList();
            foreach (var text in page.Query<TextElement>().ToList())
            {
                if (!IsVisible(text) || string.IsNullOrEmpty(text.text)) continue;
                Rect visible = text.worldBound;
                for (var ancestor = text.hierarchy.parent; ancestor != null; ancestor = ancestor.hierarchy.parent)
                {
                    if (viewports.Contains(ancestor))
                    {
                        // 垂直滚动只允许上下离开视口；卡片内部的裁切仍按完整文字边界检查。
                        Require(visible.xMin >= ancestor.worldBound.xMin - 2 && visible.xMax <= ancestor.worldBound.xMax + 2,
                            "文字横向超出滚动视口：" + text.name);
                        visible = Intersect(visible, ancestor.worldBound);
                        if (!HasArea(visible)) break;
                    }
                    else if (ancestor.style.overflow.value == Overflow.Hidden || ancestor is ScrollView)
                        Require(Contains(ancestor.worldBound, visible), "文字被裁切：" + text.name + " / " + ancestor.name
                            + " text=" + visible + " container=" + ancestor.worldBound);
                }
                if (HasArea(visible))
                    Require(Contains(router.Document.rootVisualElement.worldBound, visible), "文字超出屏幕：" + text.name + " " + text.text);
                var measured = text.MeasureTextSize(text.text, text.contentRect.width,
                    text.resolvedStyle.whiteSpace == WhiteSpace.Normal ? VisualElement.MeasureMode.Exactly : VisualElement.MeasureMode.Undefined,
                    0, VisualElement.MeasureMode.Undefined);
                Require(!text.isElided && measured.x <= text.contentRect.width + 2 && measured.y <= text.contentRect.height + 2,
                    "文字内容裁切：" + text.name + " " + text.text);
            }
            var buttons = page.Query<Button>().ToList().FindAll(button => IsVisible(button) && HasArea(ViewportBounds(button, viewports)));
            for (int i = 0; i < buttons.Count; i++)
                for (int j = i + 1; j < buttons.Count; j++)
                    Require(!ViewportBounds(buttons[i], viewports).Overlaps(ViewportBounds(buttons[j], viewports)), "按钮互相重叠。");
            report.checks.Add(Screen.width + "×" + Screen.height + " " + page.name + "：可见区域边界、完整文字尺寸、卡片内部裁切与可见按钮不重叠检查通过。");
        }

        static bool HasArea(Rect bounds) { return bounds.width > 0 && bounds.height > 0; }

        static Rect Intersect(Rect first, Rect second)
        {
            float left = Math.Max(first.xMin, second.xMin), top = Math.Max(first.yMin, second.yMin);
            return new Rect(left, top, Math.Max(0, Math.Min(first.xMax, second.xMax) - left),
                Math.Max(0, Math.Min(first.yMax, second.yMax) - top));
        }

        static Rect ViewportBounds(VisualElement element, IList<VisualElement> viewports)
        {
            Rect bounds = element.worldBound;
            for (var ancestor = element.hierarchy.parent; ancestor != null; ancestor = ancestor.hierarchy.parent)
                if (viewports.Contains(ancestor)) bounds = Intersect(bounds, ancestor.worldBound);
            return bounds;
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
                int dark = 255, light = 0;
                var pixels = texture.GetPixels32();
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

        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        void Finish(string failure)
        {
            if (!running) return;
            if (failure != null && !string.IsNullOrEmpty(output) && RequestedWidth > 0)
            {
                try { Capture("failure"); }
                catch (Exception captureError) { report.messages.Add("验收失败截图不可用：" + captureError.Message); }
            }
            running = false;
            report.failure = failure;
            if (failure != null) report.completed = false;
            ExitCode = report.completed && report.errors == 0 && report.warnings == 0 ? 0 : 1;
            Application.logMessageReceived -= OnLog;
            if (ReportPath != null) File.WriteAllText(ReportPath, JsonUtility.ToJson(report, true));
            Completed = true;
            if (ExitCode == 0) Debug.Log("T69_FX_SMOKE_PASS " + ReportPath);
            else Debug.LogError("T69_FX_SMOKE_FAIL " + failure + " " + ReportPath);
        }
    }
}
