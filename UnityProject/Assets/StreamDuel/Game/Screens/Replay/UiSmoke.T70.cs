using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using StreamDuel.Core;
using UnityEngine;
using UnityEngine.UIElements;

namespace StreamDuel.Game
{
    public sealed partial class UiSmoke
    {
        string t70OriginalClipboard;
        bool t70ClipboardSaved;

        sealed class T70SaveStore : ISaveStore
        {
            readonly FileSaveStore files;
            public int Saves;
            public bool FailSave, FailLoad, FailList;

            public T70SaveStore(string path) { files = new FileSaveStore(path); }
            public void SaveReplay(string name, string json)
            {
                Saves++;
                if (FailSave) throw new IOException("T70 测试写入失败");
                files.SaveReplay(name, json);
            }
            public string LoadReplay(string name)
            {
                if (FailLoad) throw new IOException("T70 测试读取失败");
                return files.LoadReplay(name);
            }
            public List<string> ListReplays()
            {
                if (FailList) throw new IOException("T70 测试目录列表失败");
                return files.ListReplays();
            }
            public void SaveSettings(GameSettings settings) => files.SaveSettings(settings);
            public GameSettings LoadSettings() => files.LoadSettings();
            public void SaveProfile(Profile profile) => files.SaveProfile(profile);
            public Profile LoadProfile() => files.LoadProfile();
        }

        sealed class T70FallbackReplay : IScreen
        {
            public const string ScreenId = "t70-fallback-replay";
            readonly ReplayScreen replay;
            public T70FallbackReplay(Func<string, ContentDb> resolve) { replay = new ReplayScreen(resolve); }
            public string Id => ScreenId;
            public VisualElement Build(ScreenRouter owner, object args) => replay.Build(owner, args);
            public void Tick(float dt) => replay.Tick(dt);
            public void Dispose() => replay.Dispose();
        }

        sealed class T70Animator : IBattleAnimator
        {
            public List<AnimCue> LastCues;
            public int Plays, Skips, OverlappingPlays;
            public bool IsPlaying { get; private set; }

            public void Play(IReadOnlyList<AnimCue> cues)
            {
                if (IsPlaying) OverlappingPlays++;
                Plays++;
                LastCues = cues?.Select(cue => new AnimCue
                {
                    Start = cue.Start, Duration = cue.Duration, Kind = cue.Kind,
                    EventIndex = cue.EventIndex, LogIndex = cue.LogIndex, Side = cue.Side,
                    Slot = cue.Slot, Amount = cue.Amount, Id = cue.Id, Text = cue.Text, Style = cue.Style
                }).ToList();
                // 正时长提示由探针显式完成，才能验证页面是否等待动画、暂停和释放。
                IsPlaying = LastCues != null && LastCues.Any(cue => cue.Start + cue.Duration > 0);
            }

            public void SkipToEnd() { Skips++; Complete(); }
            public void Complete() { IsPlaying = false; }
        }

        IEnumerator RunT70()
        {
            t70OriginalClipboard = GUIUtility.systemCopyBuffer;
            t70ClipboardSaved = true;
            report.navigation = "T70：NavigationSubmitEvent 程序触发真实按钮回调，字段使用公开 value；不是人工鼠标验收。";
            report.layoutScope = "T70：检查可见文字和控件的屏幕边界、可见完整文字测量及非祖先交叠；滚动表逐行滚入视口检查，另保留首尾截图；截图仍需视觉复核。";
            var rules = Resources.Load<TextAsset>("StreamDuelRules");
            var pack = Resources.Load<TextAsset>("Packs/original");
            Require(rules != null && pack != null, "T70 缺少内置规则或原创内容包。");
            var db = ContentDb.FromJson(rules.text, pack.text);
            var roster = db.Streamers.Select(streamer => streamer.Id).ToArray();
            var battle = new Battle(db, new IList<string>[] { roster.Take(6).ToArray(), roster.Skip(6).Take(6).ToArray() },
                new[] { "回看验收甲", "回看验收乙" }, 70061u, leadPick: true);
            battle.ChooseLead(0, Ai.ChooseLead(battle, 0));
            battle.ChooseLead(1, Ai.ChooseLead(battle, 1));
            for (int step = 0; step < 400 && battle.Phase != Phase.Over; step++)
            {
                if (battle.Phase == Phase.Choose)
                    battle.ResolveTurn(Ai.Choose(battle, 0), Ai.Choose(battle, 1));
                else if (battle.Phase == Phase.Replace)
                {
                    for (int side = 0; side < 2 && battle.Phase == Phase.Replace; side++)
                        if (battle.Need[side]) battle.Replace(side, Ai.ChooseReplacement(battle, side));
                }
                else throw new InvalidOperationException("T70 对战未进入可执行阶段：" + battle.Phase);
                if (step % 8 == 0) yield return null;
            }
            Require(battle.Phase == Phase.Over, "T70 固定种子对战未完成。");
            string originalRecord = battle.Record.ToJson();
            string originalLogs = T70LogSnapshot(battle);
            string originalSummary = BattleSummary.From(battle).ToText();
            ulong originalHash = battle.StateHash();
            File.WriteAllText(Path.Combine(report.outputDirectory, "source-record.json"), originalRecord);
            report.steps.Add("Core 固定种子 70061 完成 12 人对战：回合 " + battle.Turn + "，胜方 " + battle.Winner
                + "，状态哈希 " + originalHash.ToString("x16", CultureInfo.InvariantCulture) + "。");
            var store = new T70SaveStore(Path.Combine(report.outputDirectory, "saves"));
            var expected = new ReplayNavigator(db, BattleRecord.FromJson(originalRecord));
            Require(expected.Count >= 4, "T70 录像太短，无法覆盖首、中、尾回合。");
            var loadedContent = ContentDb.FromJson(rules.text, pack.text);
            loadedContent.PackId = "t70_loaded_pack";
            loadedContent.PackName = "验收加载内容包";
            var loadedBattle = new Battle(loadedContent, battle.Record.Teams, new[] { "已加载包甲", "已加载包乙" }, 70062u);
            loadedBattle.ResolveTurn(Ai.Choose(loadedBattle, 0), Ai.Choose(loadedBattle, 1));
            string loadedRecord = loadedBattle.Record.ToJson();
            var loadedStore = new T70SaveStore(Path.Combine(report.outputDirectory, "loaded-pack-saves"));
            loadedStore.SaveReplay("loaded_pack_replay", loadedRecord);
            int fallbackCalls = 0;
            // 路由只允许新增工厂；临时别名代理验证真实构造器，正式回看工厂保持原注册。
            router.Register(T70FallbackReplay.ScreenId, () => new T70FallbackReplay(id =>
            {
                fallbackCalls++;
                return id == loadedContent.PackId ? loadedContent : null;
            }));

            foreach (int size in new[] { 720, 1080 })
            {
                RequestedHeight = size;
                RequestedWidth = size == 720 ? 1280 : 1920;
                Screen.SetResolution(RequestedWidth, RequestedHeight, FullScreenMode.Windowed);
                float resizeDeadline = Time.realtimeSinceStartup + 15;
                while (Screen.width != RequestedWidth || Screen.height != RequestedHeight)
                {
                    Require(Time.realtimeSinceStartup < resizeDeadline, "T70 无法设置 GameView 分辨率。");
                    yield return null;
                }
                int rematches = 0, teamReturns = 0;
                var resultArgs = new ResultScreenArgs
                {
                    Battle = battle,
                    PlayerSide = 0,
                    SaveStore = store,
                    ResolvePack = id => id == db.PackId ? db : throw new KeyError("测试缺少内容包：" + id),
                    Rematch = () => rematches++,
                    ReturnToTeam = () => teamReturns++
                };
                router.Show(ScreenIds.Result, resultArgs);
                yield return T70Ready();
                Require(T70Label("result-outcome").text == (battle.Winner == -1 ? "平局" : battle.Winner == 0 ? "胜利" : "本局落败"),
                    "结算胜负与引擎不一致。");
                Require(T70Label("result-rounds").text.Contains(T70Number(battle.Turn)), "结算没有显示结束回合。");
                Require(T70Label("result-mvp").text.Contains("MVP"), "结算缺少 MVP 信息。");
                Capture("t70-result-top");
                T70ValidatePage(ScreenIds.Result);
                yield return T70CheckSummary(battle);
                Capture("t70-result-bottom");

                int saves = store.Saves;
                yield return T70Submit("result-save");
                Require(store.Saves == saves + 1 && !T70Button("result-save").enabledInHierarchy, "保存按钮没有完成一次真实保存或未阻止重复操作。");
                string savedName = store.ListReplays().First();
                Require(store.LoadReplay(savedName) == originalRecord, "保存录像内容与原对战记录不一致。");
                Require(T70Label("result-feedback").text.Contains("已保存"), "保存成功没有可见反馈。");
                yield return T70Submit("result-copy-report");
                Require(GUIUtility.systemCopyBuffer == BattleReport.Markdown(battle), "复制战报没有写入完整 Markdown。");
                yield return T70Submit("result-rematch");
                yield return T70Submit("result-return-team");
                Require(rematches == 1 && teamReturns == 1, "再来一局或返回组队回调没有且仅触发一次。");
                Button staleRematch = T70Button("result-rematch");
                yield return T70Submit("result-watch");
                Require(router.CurrentScreenId == ScreenIds.Replay, "结算回看按钮没有进入回看页。");
                SendSubmit(staleRematch);
                Require(rematches == 1, "已释放的结算按钮仍会调用回调。");
                expected.Seek(0);
                T70CheckFrame(expected);
                yield return T70Submit("back-button");
                Require(router.CurrentScreenId == ScreenIds.Result, "回看返回没有回到原结算。");
                report.steps.Add(RequestedWidth + "×" + RequestedHeight + "：12 位统计与 BattleSummary 逐值一致；真实保存/复制战报/再来一局/返回组队/回看返回及旧按钮解绑通过。");

                int replayReturns = 0;
                var replayArgs = new ReplayScreenArgs
                {
                    SaveStore = store,
                    ResolvePack = resultArgs.ResolvePack,
                    Return = () => { replayReturns++; router.Show(ScreenIds.Result, resultArgs); }
                };
                router.Show(ScreenIds.Replay, replayArgs);
                yield return T70Ready();
                Require(T70Label("replay-empty").resolvedStyle.display != DisplayStyle.None,
                    "没有传入录像时缺少回看空态。");
                Capture("t70-replay-library");
                var files = T70Element<DropdownField>("replay-files");
                Require(files.choices.Contains(savedName), "重新打开回看页没有列出刚保存的录像。");
                files.value = savedName;
                yield return T70Submit("open-replay-button");
                expected.Seek(0);
                T70CheckFrame(expected);
                yield return T70CheckReplayFields();
                yield return T70Reveal(document.rootVisualElement.Q<BattleStage>());
                Capture("t70-replay-start");
                T70ValidatePage(ScreenIds.Replay);
                while (expected.Index + 1 < expected.Count)
                {
                    yield return T70Submit("next-turn-button");
                    expected.Next();
                    T70CheckFrame(expected);
                }
                while (expected.Index > 0)
                {
                    yield return T70Submit("previous-turn-button");
                    expected.Previous();
                    T70CheckFrame(expected);
                }
                Require(!T70Button("previous-turn-button").enabledInHierarchy, "开场仍允许越界后退。");

                int middle = expected.Count / 2;
                yield return T70Jump(expected.Turns[middle]);
                expected.Seek(middle);
                T70CheckFrame(expected);
                yield return T70Reveal(document.rootVisualElement.Q<BattleStage>());
                Capture("t70-replay-middle");
                yield return T70Jump(expected.Turns[expected.Count - 1]);
                expected.Seek(expected.Count - 1);
                T70CheckFrame(expected);
                Require(!T70Button("next-turn-button").enabledInHierarchy, "录像末尾仍允许越界前进。");
                yield return T70Reveal(document.rootVisualElement.Q<BattleStage>());
                Capture("t70-replay-end");

                yield return T70Submit("copy-share-button");
                string share = GUIUtility.systemCopyBuffer;
                Require(share.StartsWith("SD1-", StringComparison.Ordinal)
                    && ShareCode.Decode(share).ToJson() == originalRecord, "复制分享码没有完整保留录像。");
                string beforeInvalid = T70Label("replay-status").text;
                T70Element<TextField>("share-code-field").value = "SD1-invalid!";
                yield return T70Submit("open-share-button");
                Require(T70Label("replay-status").text != beforeInvalid
                    && T70Label("replay-status").text.Contains("分享码"), "无效分享码没有可见错误。");
                T70CheckFrame(expected);
                Capture("t70-replay-invalid-share");
                GUIUtility.systemCopyBuffer = share;
                T70Element<TextField>("share-code-field").value = "";
                yield return T70Submit("paste-share-button");
                Require(T70Element<TextField>("share-code-field").value == share, "粘贴按钮没有读取分享码。");
                yield return T70Submit("open-share-button");
                expected.Seek(0);
                T70CheckFrame(expected);

                var speed = T70Element<DropdownField>("playback-speed");
                Require(speed.choices.SequenceEqual(new[] { "0.5x", "1x", "2x", "4x" }), "回看倍速选项不完整。");
                var durations = new Dictionary<string, float>();
                foreach (string choice in speed.choices.ToArray())
                {
                    yield return T70Jump(0);
                    speed.value = choice;
                    yield return T70Reveal(speed);
                    T70CheckNamedField(speed, speed.labelElement, choice, true);
                    string before = T70Label("replay-position").text;
                    float began = Time.realtimeSinceStartup;
                    yield return T70Submit("play-pause-button");
                    float playDeadline = Time.realtimeSinceStartup + 5;
                    while (T70Label("replay-position").text == before)
                    {
                        Require(Time.realtimeSinceStartup < playDeadline, "倍速 " + choice + " 没有自动前进。");
                        yield return null;
                    }
                    durations[choice] = Time.realtimeSinceStartup - began;
                    yield return T70Submit("play-pause-button");
                    string paused = T70Label("replay-position").text;
                    yield return new WaitForSecondsRealtime(0.3f);
                    Require(T70Label("replay-position").text == paused, "暂停后回看仍继续前进。");
                    report.steps.Add(RequestedWidth + "×" + RequestedHeight + "：" + choice + " 首次自动前进耗时 "
                        + durations[choice].ToString("F3", CultureInfo.InvariantCulture) + " 秒，暂停稳定。");
                }
                Require(durations["0.5x"] > durations["4x"] * 1.5f, "0.5x 与 4x 没有表现出不同的实际播放速度。");
                yield return T70Jump(expected.Turns[middle]);
                expected.Seek(middle);
                T70CheckFrame(expected);
                store.FailLoad = true;
                yield return T70Submit("open-replay-button");
                Require(T70Label("replay-status").text.Contains("T70 测试读取失败"), "录像读取失败没有显示原因。");
                T70CheckFrame(expected);
                store.FailLoad = false;
                Button staleNext = T70Button("next-turn-button");
                Label staleDetails = T70Label("replay-position");
                yield return T70Submit("play-pause-button");
                yield return T70Submit("back-button");
                Require(replayReturns == 1 && router.CurrentScreenId == ScreenIds.Result, "回看返回回调未执行。");
                string afterLeave = staleDetails.text;
                SendSubmit(staleNext);
                yield return new WaitForSecondsRealtime(0.4f);
                Require(staleDetails.text == afterLeave, "离开回看后仍在 Tick 或旧按钮仍改变状态。");

                store.FailSave = true;
                yield return T70Submit("result-save");
                Require(T70Label("result-feedback").text.Contains("T70 测试写入失败"), "录像写入失败没有显示原因。");
                Require(T70Button("result-save").enabledInHierarchy, "失败后保存按钮无法重试。");
                store.FailSave = false;
                Capture("t70-result-save-error");
                store.FailList = true;
                router.Show(ScreenIds.Replay, new ReplayScreenArgs
                {
                    Content = db, Record = battle.Record, SaveStore = store
                });
                yield return T70Ready();
                expected.Seek(0);
                T70CheckFrame(expected);
                var libraryError = T70Label("replay-library-status");
                Require(libraryError.text.Contains("T70 测试目录列表失败")
                    && libraryError.resolvedStyle.display != DisplayStyle.None,
                    "内存录像成功打开后覆盖了目录列表失败提示。");
                yield return T70Reveal(libraryError);
                Capture("t70-replay-library-error");
                store.FailList = false;
                yield return T70Submit("refresh-replays-button");
                Require(string.IsNullOrEmpty(libraryError.text) && libraryError.resolvedStyle.display == DisplayStyle.None,
                    "目录列表重试成功后仍留下旧错误。");
                Require(T70Element<DropdownField>("replay-files").choices.Contains(savedName), "目录重试后没有恢复录像列表。");
                T70CheckFrame(expected);

                int callsBefore = fallbackCalls;
                router.Show(T70FallbackReplay.ScreenId, new ReplayScreenArgs { SaveStore = loadedStore });
                yield return T70Ready();
                T70Element<DropdownField>("replay-files").value = "loaded_pack_replay";
                yield return T70Submit("open-replay-button");
                Require(fallbackCalls == callsBefore + 1, "文件加载没有调用构造器注入的已加载内容包解析器。");
                var loadedExpected = new ReplayNavigator(loadedContent, BattleRecord.FromJson(loadedRecord));
                T70CheckFrame(loadedExpected);
                Require(T70Label("replay-pack").text.Contains(loadedContent.PackName), "文件加载没有使用注入的自定义内容包。");
                T70Element<TextField>("share-code-field").value = ShareCode.Encode(BattleRecord.FromJson(loadedRecord));
                yield return T70Submit("open-share-button");
                Require(fallbackCalls == callsBefore + 2, "分享码加载没有调用构造器注入的内容包解析器。");
                T70CheckFrame(loadedExpected);
                yield return T70Submit("copy-share-button");
                Require(ShareCode.Decode(GUIUtility.systemCopyBuffer).ToJson() == loadedRecord, "已加载内容包的分享回环改变了录像。");
                Capture("t70-replay-loaded-pack");
                router.Show(ScreenIds.Replay, new ReplayScreenArgs { Content = db, Record = battle.Record, SaveStore = store });
                yield return T70Ready();
                T70CheckFrame(expected);
                Require(fallbackCalls == callsBefore + 2 && router.CurrentScreenId == ScreenIds.Replay,
                    "临时解析器探针影响了正式回看工厂。");
                yield return T70CheckAnimator(battle, store);
                Require(battle.StateHash() == originalHash && battle.Record.ToJson() == originalRecord
                    && T70LogSnapshot(battle) == originalLogs && BattleSummary.From(battle).ToText() == originalSummary,
                    "结算或回看污染了原始对战、录像、日志或累计统计。");
                report.steps.Add(RequestedWidth + "×" + RequestedHeight + "：保存后重新加载、首中尾/前后/跳转、有效和无效分享码、读写异常可见、回看 Dispose、原 Battle 不变通过。");
                report.steps.Add(RequestedWidth + "×" + RequestedHeight + "：短字段标签和值完整可见；目录失败不会被内存录像成功提示覆盖且重试清除；构造器解析已加载包的文件和分享码，正式工厂保持可用。");
            }
            router.Show(ScreenIds.Menu);
            yield return T70Ready();
            report.providersChecked = true;
        }

        IEnumerator T70CheckAnimator(Battle source, ISaveStore store)
        {
            string originalRecord = source.Record.ToJson();
            ulong originalHash = source.StateHash();
            var expected = new ReplayNavigator(source.Db, source.Record);
            var animator = new T70Animator();
            router.Show(ScreenIds.Replay, new ReplayScreenArgs
            {
                Content = source.Db, Record = source.Record, SaveStore = store, Animator = animator
            });
            yield return T70Ready();
            Require(animator.Plays == 1, "初载录像没有且仅提交一次动画提示。");
            T70CheckCues(animator, expected, 0, "初载");
            T70CheckFrame(expected);

            var speed = T70Element<DropdownField>("playback-speed");
            string[] choices = { "0.5x", "1x", "2x", "4x" };
            double[] rates = { .5, 1, 2, 4 };
            for (int option = 0; option < choices.Length; option++)
            {
                string context = choices[option];
                int plays = animator.Plays;
                speed.value = choices[option];
                Require(animator.Plays == plays && !animator.IsPlaying, "切换倍速重复提交了当前帧。");
                yield return T70Submit("next-turn-button");
                expected.Next();
                Require(animator.Plays == plays + 1, context + " 单步前进没有且仅提交一帧。");
                T70CheckCues(animator, expected, rates[option], context + " 单步");
                T70CheckFrame(expected);

                plays = animator.Plays;
                yield return T70Submit("play-pause-button");
                yield return new WaitForSecondsRealtime((float)(1.1 / rates[option]));
                Require(animator.IsPlaying && animator.Plays == plays, context + " 未等待当前动画结束就自动前进。");
                T70CheckFrame(expected);
                animator.Complete();
                float playDeadline = Time.realtimeSinceStartup + 5;
                while (animator.Plays == plays)
                {
                    Require(Time.realtimeSinceStartup < playDeadline, context + " 动画完成后没有自动前进。");
                    yield return null;
                }
                expected.Next();
                Require(animator.Plays == plays + 1, context + " 自动前进重复提交或跳过了一帧。");
                T70CheckCues(animator, expected, rates[option], context + " 自动");
                T70CheckFrame(expected);
                plays = animator.Plays;
                int skips = animator.Skips;
                yield return T70Submit("play-pause-button");
                Require(!animator.IsPlaying && animator.Skips > skips && animator.Plays == plays,
                    context + " 暂停没有结束动画，或重新提交了提示。");
                yield return new WaitForSecondsRealtime(.3f);
                Require(animator.Plays == plays && !animator.IsPlaying, context + " 暂停后仍遗留播放。");
                T70CheckFrame(expected);

                yield return T70Submit("previous-turn-button");
                expected.Previous();
                Require(animator.Plays == plays + 1, "后退没有且仅提交一帧。");
                T70CheckCues(animator, expected, 0, context + " 后退");
                T70CheckFrame(expected);
                plays = animator.Plays;
                yield return T70Jump(expected.Turns[expected.Index + 1]);
                expected.Next();
                Require(animator.Plays == plays + 1, "向相邻回合跳转没有且仅提交一帧。");
                T70CheckCues(animator, expected, 0, context + " 向前跳转");
                T70CheckFrame(expected);
                plays = animator.Plays;
                yield return T70Jump(expected.Turns[expected.Index]);
                Require(animator.Plays == plays && !animator.IsPlaying, "同回合跳转重复提交了提示。");
                T70CheckFrame(expected);
                yield return T70Jump(0);
                expected.Seek(0);
                Require(animator.Plays == plays + 1, "向开场跳转没有且仅提交一帧。");
                T70CheckCues(animator, expected, 0, context + " 返回开场");
                T70CheckFrame(expected);
            }

            yield return T70Submit("next-turn-button");
            expected.Next();
            T70CheckCues(animator, expected, 4, "改速前");
            int previousPlays = animator.Plays, previousSkips = animator.Skips;
            speed.value = "0.5x";
            Require(animator.Skips > previousSkips && !animator.IsPlaying && animator.Plays == previousPlays,
                "动画中改速没有落到终态，或重复提交了当前帧。");
            T70CheckFrame(expected);
            yield return T70Submit("next-turn-button");
            expected.Next();
            T70CheckCues(animator, expected, .5, "改速后");
            yield return T70Submit("play-pause-button");
            var staleNext = T70Button("next-turn-button");
            var stalePosition = T70Label("replay-position");
            previousPlays = animator.Plays;
            previousSkips = animator.Skips;
            yield return T70Submit("back-button");
            Require(router.CurrentScreenId == ScreenIds.Menu && animator.Skips > previousSkips && !animator.IsPlaying,
                "离开回看没有释放正在播放的动画。");
            string positionAfterLeave = stalePosition.text;
            SendSubmit(staleNext);
            yield return new WaitForSecondsRealtime(.3f);
            Require(animator.Plays == previousPlays && !animator.IsPlaying && stalePosition.text == positionAfterLeave,
                "Dispose 后动画或旧按钮仍在推进回看。");
            Require(animator.OverlappingPlays == 0, "新帧覆盖了尚未结束的动画。");
            Require(source.Record.ToJson() == originalRecord && source.StateHash() == originalHash,
                "动画探针改变了源录像或对战状态哈希。");
            report.steps.Add(RequestedWidth + "×" + RequestedHeight
                + "：注入 IBattleAnimator，初载/后退/跳转保留零时长提示；四档单步和自动提示逐字段等于 BattleTimeline.Build；"
                + "自动等待动画、暂停、改速、Dispose 及旧按钮隔离通过，源录像/hash 不变。");
        }

        static void T70CheckCues(T70Animator animator, ReplayNavigator expected, double speed, string context)
        {
            var cues = BattleTimeline.Build(expected.Current.Events, expected.FrameEventStart,
                expected.Current.Events.Count, speed);
            Require(cues.Count > 0, context + " 验收局面缺少有效提示。");
            Require(animator.LastCues != null && animator.LastCues.Count == cues.Count,
                context + " 传给动画接口的提示为空或数量与 Core 不同。");
            for (int index = 0; index < cues.Count; index++)
            {
                var actual = animator.LastCues[index];
                var wanted = cues[index];
                Require(actual.Start == wanted.Start && actual.Duration == wanted.Duration
                    && actual.Kind == wanted.Kind && actual.EventIndex == wanted.EventIndex
                    && actual.LogIndex == wanted.LogIndex && actual.Side == wanted.Side && actual.Slot == wanted.Slot
                    && actual.Amount == wanted.Amount && actual.Id == wanted.Id && actual.Text == wanted.Text
                    && actual.Style == wanted.Style, context + " 提示 " + index + " 与公开 Core 时间线不一致。");
            }
            Require(speed > 0 ? animator.IsPlaying && BattleTimeline.TotalDuration(cues) > 0
                : !animator.IsPlaying && animator.LastCues.All(cue => cue.Start == 0 && cue.Duration == 0),
                context + " 动画时长或直接终态不符合所选倍速。");
        }

        IEnumerator T70CheckSummary(Battle battle)
        {
            var summary = BattleSummary.From(battle);
            for (int side = 0; side < 2; side++)
            {
                for (int index = 0; index < summary.Sides[side].Count; index++)
                {
                    var stats = summary.Sides[side][index];
                    string suffix = side + "-" + index;
                    Require(T70Label("result-name-" + suffix).text.Contains(battle.Sides[side].Team[index].Name), "结算主播名字不对应号位。");
                    Require(T70Label("result-damage-" + suffix).text == "造成伤害：" + T70Number(stats.DamageDealt), "结算造成伤害不一致。");
                    Require(T70Label("result-taken-" + suffix).text == "承受伤害：" + T70Number(stats.DamageTaken), "结算承受伤害不一致。");
                    Require(T70Label("result-kos-" + suffix).text == "击倒：" + T70Number(stats.Kos), "结算击倒不一致。");
                    Require(T70Label("result-turns-" + suffix).text == "在场回合：" + T70Number(stats.TurnsActive), "结算在场回合不一致。");
                    Require(T70Label("result-state-" + suffix).text == (stats.Fainted ? "已下播" : "仍在播"), "结算在播状态不一致。");
                    var row = T70Element<VisualElement>("result-fighter-" + suffix);
                    yield return T70Reveal(row);
                    Require(Contains(T70Element<ScrollView>("result-scroll").contentViewport.worldBound, row.worldBound),
                        "结算统计行无法完整滚入视口：" + suffix);
                    T70ValidatePage(ScreenIds.Result);
                }
            }
        }

        void T70CheckFrame(ReplayNavigator expected)
        {
            var stage = document.rootVisualElement.Q<BattleStage>();
            Require(stage != null, "回看没有复用 BattleStage。");
            foreach (int side in new[] { 0, 1 })
            {
                var fighter = stage.Q<VisualElement>(side == 0 ? StageNames.Fighter0 : StageNames.Fighter1);
                var hp = fighter?.Q<ProgressBar>(StageNames.HpBar);
                Require(hp != null && Mathf.Abs(hp.value - expected.Current.Active(side).Pct) <= 1f,
                    "回看舞台人气与 Core 回合快照不一致：" + side);
                Require(hp.title == expected.Current.Active(side).Hp + " / " + expected.Current.Active(side).MaxHp,
                    "回看舞台人气数值与 Core 不一致：" + side);
                Require(fighter.Q<Label>("fighter-name").text == expected.Current.Sides[side].Name + " · " + expected.Current.Active(side).Name,
                    "回看舞台上场主播与 Core 不一致：" + side);
                Require(fighter.Q<Image>(StageNames.Portrait)?.image != null, "回看没有可用的占位头像。");
            }
            string position = expected.Index == 0 ? "开场" : "第 " + T70Number(expected.Turns[expected.Index]) + " 回合";
            position += " · " + T70Number(expected.Index + 1) + " / " + T70Number(expected.Count);
            if (expected.Current.Phase == Phase.Over) position += " · 已结束";
            Require(T70Label("replay-position").text == position, "回看当前位置说明与导航状态不一致。");
            Require(T70Element<VisualElement>("replay-details").childCount > 0, "回看缺少主播详情。");
            var expectedLog = expected.FrameLog.Select(entry => "【" + entry.Node + "】" + entry.Text).ToList();
            if (expectedLog.Count == 0) expectedLog.Add("本回合暂无日志。");
            var shownLog = T70Element<ScrollView>("replay-log").contentContainer.Children().Cast<Label>().Select(label => label.text);
            Require(shownLog.SequenceEqual(expectedLog), "回看当前帧日志有遗漏、错序或混入其他回合。");
        }

        IEnumerator T70Jump(int turn)
        {
            var field = document.rootVisualElement.Q<IntegerField>("jump-turn-field");
            if (field != null) field.value = turn;
            else T70Element<TextField>("jump-turn-field").value = T70Number(turn);
            yield return T70Submit("jump-turn-button");
            var numeric = document.rootVisualElement.Q<IntegerField>("jump-turn-field");
            if (numeric != null)
            {
                yield return T70Reveal(numeric);
                T70CheckNamedField(numeric, numeric.labelElement, T70Number(numeric.value), true);
            }
        }

        IEnumerator T70CheckReplayFields()
        {
            var files = T70Element<DropdownField>("replay-files");
            yield return T70Reveal(files);
            T70CheckNamedField(files, files.labelElement, files.value, false);
            var share = T70Element<TextField>("share-code-field");
            yield return T70Reveal(share);
            T70CheckNamedField(share, share.labelElement, share.value, false);
            var speed = T70Element<DropdownField>("playback-speed");
            yield return T70Reveal(speed);
            T70CheckNamedField(speed, speed.labelElement, speed.value, true);
            var turn = T70Element<IntegerField>("jump-turn-field");
            yield return T70Reveal(turn);
            T70CheckNamedField(turn, turn.labelElement, T70Number(turn.value), true);
        }

        void T70CheckNamedField(VisualElement field, Label label, string value, bool requireFullValue)
        {
            var input = field.Q<VisualElement>(className: "unity-base-field__input");
            Require(input != null && label != null, "字段缺少标签或输入区：" + field.name);
            foreach (var element in new[] { field, label, input })
            {
                Rect bounds = element.worldBound;
                Require(element.visible && element.resolvedStyle.display != DisplayStyle.None
                    && Finite(bounds.width) && Finite(bounds.height) && bounds.width > 0 && bounds.height > 0,
                    "字段标签或输入区不可见：" + field.name);
                Require(Contains(document.rootVisualElement.worldBound, bounds), "字段超出屏幕：" + field.name);
                for (var ancestor = element.parent; ancestor != null; ancestor = ancestor.parent)
                    if (ancestor is ScrollView scroll)
                        Require(Contains(scroll.contentViewport.worldBound, bounds), "字段不能完整滚入视口：" + field.name);
            }
            Require(Contains(field.worldBound, label.worldBound) && Contains(field.worldBound, input.worldBound)
                && label.worldBound.xMax <= input.worldBound.xMin + 2, "字段标签挤压或覆盖输入值：" + field.name);
            T70CheckFieldText(label, field.name + " 标签");
            Require(input.contentRect.width > 0 && input.contentRect.height > 0, "字段输入区没有有效内容空间：" + field.name);
            if (!requireFullValue) return;
            var textElements = new List<TextElement>();
            if (input is TextElement ownText) textElements.Add(ownText);
            input.Query<TextElement>().ForEach(text => textElements.Add(text));
            var visibleValue = textElements.FirstOrDefault(text => text.text == value && text.visible
                && text.resolvedStyle.display != DisplayStyle.None);
            Require(visibleValue != null, "字段值没有实际显示：" + field.name + "=" + value);
            Require(Contains(input.worldBound, visibleValue.worldBound), "字段值超出输入区：" + field.name);
            T70CheckFieldText(visibleValue, field.name + " 值");
            report.bounds.Add(new ElementBounds
            {
                screen = ScreenIds.Replay, element = field.name + "/value", text = value,
                width = RequestedWidth, height = RequestedHeight,
                x = visibleValue.worldBound.x, y = visibleValue.worldBound.y,
                w = visibleValue.worldBound.width, h = visibleValue.worldBound.height
            });
        }

        static void T70CheckFieldText(TextElement text, string owner)
        {
            Require(text.contentRect.width > 0 && text.contentRect.height > 0, "字段文字没有可见空间：" + owner);
            var measured = text.MeasureTextSize(text.text, 0, VisualElement.MeasureMode.Undefined, 0, VisualElement.MeasureMode.Undefined);
            Require(!text.isElided && measured.x <= text.contentRect.width + 2 && measured.y <= text.contentRect.height + 2,
                "字段文字显示不完整：" + owner + " text=" + text.text + " measured=" + measured + " content=" + text.contentRect);
        }

        IEnumerator T70Submit(string name)
        {
            Button button = T70Button(name);
            yield return T70Reveal(button);
            Submit(name);
            yield return T70Ready();
        }

        IEnumerator T70Reveal(VisualElement element)
        {
            for (VisualElement ancestor = element.parent; ancestor != null; ancestor = ancestor.parent)
                if (ancestor is ScrollView scroll) scroll.ScrollTo(element);
            yield return T70Ready();
        }

        static IEnumerator T70Ready()
        {
            yield return null;
            yield return new WaitForEndOfFrame();
        }

        T T70Element<T>(string name) where T : VisualElement
        {
            var element = document.rootVisualElement.Q<T>(name);
            Require(element != null, "T70 缺少语义节点：" + name);
            return element;
        }

        Button T70Button(string name) => T70Element<Button>(name);
        Label T70Label(string name) => T70Element<Label>(name);
        static string T70Number(int value) => value.ToString(CultureInfo.InvariantCulture);

        static string T70LogSnapshot(Battle battle)
        {
            return string.Join("\n", battle.Log.Select(entry => entry.Turn + "|" + entry.Side + "|" + entry.Node + "|" + entry.Kind + "|" + entry.Text
                + "|" + string.Join(",", entry.SnapActive) + "|" + string.Join(",", entry.SnapHp[0]) + "|" + string.Join(",", entry.SnapHp[1])));
        }

        void RestoreT70Clipboard()
        {
            if (!t70ClipboardSaved) return;
            try
            {
                GUIUtility.systemCopyBuffer = t70OriginalClipboard;
                t70ClipboardSaved = false;
                t70OriginalClipboard = null;
            }
            catch (Exception error)
            {
                report.errors++;
                report.logs.Add("T70 无法恢复系统剪贴板：" + error.Message);
            }
        }

        void OnDestroy()
        {
            RestoreT70Clipboard();
        }

        void T70ValidatePage(string screenId)
        {
            var page = T70Element<VisualElement>(screenId + "-screen");
            var elements = new List<VisualElement>();
            CollectVisible(page, elements);
            var visible = new List<VisualElement>();
            Rect screen = document.rootVisualElement.worldBound;
            foreach (var element in elements)
            {
                Rect bounds = element.worldBound;
                Require(Finite(bounds.x) && Finite(bounds.y) && Finite(bounds.width) && Finite(bounds.height), "T70 控件布局不是有限数值。");
                bool clipped = false, outside = false, inputText = false;
                for (var ancestor = element.parent; ancestor != null; ancestor = ancestor.parent)
                {
                    if (ancestor is TextField || ancestor is IntegerField || ancestor is DropdownField) inputText = true;
                    if (ancestor is ScrollView scroll)
                    {
                        Rect viewport = scroll.contentViewport.worldBound;
                        if (!viewport.Overlaps(bounds)) outside = true;
                        if (!Contains(viewport, bounds)) clipped = true;
                    }
                }
                if (outside || clipped || inputText || bounds.width <= 0 || bounds.height <= 0) continue;
                Require(Contains(screen, bounds), "T70 控件超出屏幕：" + Describe(element));
                if (element is TextElement text && !string.IsNullOrEmpty(text.text))
                {
                    Vector2 measured = text.MeasureTextSize(text.text, text.contentRect.width,
                        text.resolvedStyle.whiteSpace == WhiteSpace.Normal ? VisualElement.MeasureMode.Exactly : VisualElement.MeasureMode.Undefined,
                        0, VisualElement.MeasureMode.Undefined);
                    Require(!text.isElided && measured.x <= text.contentRect.width + 2 && measured.y <= text.contentRect.height + 2,
                        "T70 可见文字被裁切：" + Describe(element) + " " + text.text);
                }
                visible.Add(element);
                report.bounds.Add(new ElementBounds
                {
                    screen = screenId, element = Describe(element), text = (element as TextElement)?.text,
                    width = RequestedWidth, height = RequestedHeight,
                    x = bounds.x, y = bounds.y, w = bounds.width, h = bounds.height
                });
            }
            Require(visible.Count > 0, "T70 页面没有可见文字或控件。");
            for (int first = 0; first < visible.Count; first++)
                for (int second = first + 1; second < visible.Count; second++)
                {
                    if (visible[first].Contains(visible[second]) || visible[second].Contains(visible[first])) continue;
                    Rect a = visible[first].worldBound, b = visible[second].worldBound;
                    Require(Mathf.Min(a.xMax, b.xMax) - Mathf.Max(a.xMin, b.xMin) <= 1
                        || Mathf.Min(a.yMax, b.yMax) - Mathf.Max(a.yMin, b.yMin) <= 1,
                        "T70 可见控件交叠：" + Describe(visible[first]) + " / " + Describe(visible[second]));
                }
        }
    }
}
