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
    public sealed partial class BattleUiSmoke
    {
        sealed class QuickSettingsFailStore : ISaveStore
        {
            readonly ISaveStore source;
            public int SaveAttempts;
            public QuickSettingsFailStore(ISaveStore source) { this.source = source; }
            public void SaveSettings(GameSettings settings)
            {
                SaveAttempts++;
                throw new IOException("隔离验收：设置写入失败。");
            }
            public GameSettings LoadSettings() => source.LoadSettings();
            public void SaveProfile(Profile profile) => source.SaveProfile(profile);
            public Profile LoadProfile() => source.LoadProfile();
            public void SaveReplay(string name, string json) => source.SaveReplay(name, json);
            public List<string> ListReplays() => source.ListReplays();
            public string LoadReplay(string name) => source.LoadReplay(name);
        }

        IEnumerator CheckRealPresentationAndVersions()
        {
            var db = LoadContent();
            router.Show(ScreenIds.Battle, new BattleScreenArgs
            {
                Db = db, Teams = Teams(db), Names = new[] { "真实动画甲", "真实动画乙" },
                Seed = 670076u, LeadPick = false, Isolated = true,
                Settings = new GameSettings
                {
                    DefaultPack = db.PackId, AiLevel = AiLevel.Normal, Animations = true,
                    AnimationSpeed = 4, ColorblindMode = true, TurnTimer = false
                }
            });
            var screen = Current;
            Require(screen != null, "真实动画验收没有建立正式对战页面。");
            var animators = new List<TimelineAnimator>();
            var openingAnimator = screen.TimelineAnimator;
            var portraits = typeof(BattleScreen).GetField("portraits", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(screen) as PortraitProvider;
            Require(openingAnimator != null && portraits != null, "正式对战默认没有使用主线 TimelineAnimator 和 PortraitProvider。");
            animators.Add(openingAnimator);
            var battle = screen.CurrentBattle;
            yield return WaitRealPlayback(screen, animators);
            ValidateHud();
            var stage = document.rootVisualElement.Q<BattleStage>();
            var texture = stage.Fighter0.Q<Image>(StageNames.Portrait).image;
            Require(texture != null && texture.width == 128 && texture.height == 128,
                "正式对战没有显示主线生成的占位头像。");
            Require(ReferenceEquals(texture, portraits.Get(db, battle.Active(0).Def.Id)), "正式对战重复生成同一个头像。");
            yield return new WaitForEndOfFrame(); Capture("real-timeline-opening");
            bool invalidRejected = false;
            try
            {
                router.Show(ScreenIds.Battle, new BattleScreenArgs
                {
                    Db = db, Teams = new IList<string>[] { new[] { "missing-review-member" }, new[] { "missing-review-member" } },
                    Settings = new GameSettings(), Isolated = true
                });
            }
            catch (ArgumentException) { invalidRejected = true; }
            catch (KeyNotFoundException) { invalidRejected = true; }
            catch (KeyError) { invalidRejected = true; }
            Require(invalidRejected && ReferenceEquals(Current, screen) && ReferenceEquals(screen.CurrentBattle, battle),
                "无效开局构建失败后破坏了仍在显示的正式对战页。");
            bool playingCaptured = false;
            int guard = 0;
            while (battle.Phase != Phase.Over)
            {
                Require(guard++ < 500, "真实时间线完整对局超过有限操作上限。");
                Require(ReferenceEquals(Current, screen) && ReferenceEquals(screen.CurrentBattle, battle), "真实播放意外更换了对局。");
                if (battle.Phase == Phase.Replace)
                {
                    Require(battle.Need[0], "真实时间线结束后仍等待电脑补位。");
                    Submit("battle-own-" + Ai.ChooseReplacement(battle, 0));
                }
                else
                {
                    Require(battle.Phase == Phase.Choose, "真实时间线完整对局出现未处理阶段。");
                    var action = Ai.Choose(battle, 0);
                    Submit(action.Type == ActionType.Skill ? "battle-skill-" + action.Index
                        : action.Type == ActionType.Switch ? "battle-own-" + action.Index : "battle-pass");
                }
                var activeAnimator = screen.TimelineAnimator;
                if (activeAnimator != null && !animators.Contains(activeAnimator)) animators.Add(activeAnimator);
                if (!playingCaptured)
                {
                    Require(screen.IsBusy && screen.TimelineAnimator != null && screen.TimelineAnimator.IsPlaying,
                        "真实行动没有启动时间线播放。");
                    Require(!Button("battle-pass").enabledInHierarchy, "真实动画播放时输入未锁定。");
                    yield return new WaitForEndOfFrame(); Capture("real-timeline-playing");
                    ulong pendingHash = battle.StateHash(); string pendingRecord = battle.Record.ToJson();
                    int pendingFrom = screen.PlaybackFromEvent, pendingTo = screen.PlaybackToEvent;
                    Require(screen.IsBusy, "文档停启夹具未处于真实播放中。");
                    document.enabled = false; yield return null; yield return null;
                    Require(Current == null && texture == null, "停用文档没有释放旧页和自有头像。");
                    document.enabled = true;
                    float restoreUntil = Time.realtimeSinceStartup + 5;
                    while (Current == null)
                    {
                        Require(Time.realtimeSinceStartup < restoreUntil, "文档重新启用未恢复对战。");
                        yield return null;
                    }
                    screen = Current;
                    Require(ReferenceEquals(screen.CurrentBattle, battle) && screen.IsBusy
                        && screen.PlaybackFromEvent == pendingFrom && screen.PlaybackToEvent == pendingTo
                        && battle.StateHash() == pendingHash && battle.Record.ToJson() == pendingRecord,
                        "文档恢复重播了此前批次、重复出招或改变了当前状态/随机流。");
                    stage = screen.Stage;
                    texture = stage.Fighter0.Q<Image>(StageNames.Portrait).image;
                    Require(texture != null, "恢复后正式舞台未重新获得自有头像。");
                    playingCaptured = true;
                }
                yield return WaitRealPlayback(screen, animators);
            }
            Require(playingCaptured && !screen.IsBusy && animators.All(value => !value.IsPlaying)
                && Shown(document.rootVisualElement.Q("battle-outcome")),
                "真实完整对局未播放到可操作终局。");
            ValidateHud();
            yield return new WaitForEndOfFrame(); Capture("real-timeline-finished");
            Require(stage.FxLayer.childCount == 0 && stage.FloatLayer.childCount == 0, "真实动画终局遗留临时特效。");
            AssertReplay(battle, "完整真实动画对局");
            File.WriteAllText(Path.Combine(report.outputDirectory, Size + "-real-record-v" + battle.Record.Version + ".json"), battle.Record.ToJson());
            report.realCompletedMatches++;
            Submit("battle-menu"); yield return null; yield return null;
            Require(texture == null && animators.All(value => !value.IsPlaying),
                "离开正式对战没有释放自有头像或停止时间线。");

            // 通过公开行为得到版本 3 终止和版本 4 接手，不能直接改版本常量伪造兼容性。
            var source = new Battle(db, Teams(db), new[] { "兼容甲", "兼容乙" }, 670077u);
            source.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
            var terminated = source.Clone(); terminated.Surrender(1);
            var branch = ReplayBranch.Start(db, source.Record, 1, 0, AiLevel.Normal);
            branch.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
            branch.OfferDraw(0); branch.RespondDraw(1, true);
            Require(terminated.Record.Version == BattleRecord.TerminationVersion && branch.Record.Version == BattleRecord.BranchVersion,
                "真实公开行为没有产生终止/分支录像版本。");
            foreach (var versioned in new[] { terminated, branch })
            {
                string record = versioned.Record.ToJson(); ulong hash = versioned.StateHash();
                var restored = Replay.Run(db, BattleRecord.FromJson(record));
                AssertReplay(versioned, "版本 " + versioned.Record.Version);
                router.Show(ScreenIds.Battle, new BattleScreenArgs
                {
                    Battle = restored, Db = db, Isolated = true,
                    Settings = new GameSettings { DefaultPack = db.PackId, Animations = true, ColorblindMode = true }
                });
                yield return null; yield return new WaitForEndOfFrame();
                Require(Current != null && ReferenceEquals(Current.CurrentBattle, restored) && !Current.IsBusy
                    && Shown(document.rootVisualElement.Q("battle-outcome")), "历史版本终局未正确显示或重播了历史动画。");
                ValidateHud(); Capture("record-v" + versioned.Record.Version + "-result");
                Require(restored.StateHash() == hash && versioned.Record.ToJson() == record,
                    "正式 HUD 改写了历史版本对局或源记录。");
                Submit("battle-menu"); yield return null;
            }
            report.steps.Add(Size + "：默认真实时间线与主线头像播放完整对局、锁定输入、终局/释放；原生终止 v"
                + terminated.Record.Version + " 与接手 v" + branch.Record.Version
                + " 经 Replay.Run 后在正式 HUD 显示，状态/日志/统计/后续随机数一致，源记录不变。");
        }

        IEnumerator CheckQuickSettingsIsolation()
        {
            var session = router.GetComponent<LocalBattleSession>();
            Require(session != null && GameRuntimeSettings.IsSmokeTest, "快捷设置验收缺少隔离本地会话。");
            var saved = GameSettings.FromJson(GameRuntimeSettings.Current.ToJson());
            var storeField = typeof(GameRuntimeSettings).GetField("store", BindingFlags.Static | BindingFlags.NonPublic);
            var originalStore = storeField?.GetValue(null) as ISaveStore;
            Require(originalStore != null, "快捷设置验收缺少可恢复的隔离保存仓库。");
            var db = LoadContent();
            try
            {
                router.Show(ScreenIds.Team, new TeamScreenArgs
                {
                    Content = db, Packs = session.MenuPacks, Policy = db.Policy,
                    Settings = new GameSettings
                    {
                        DefaultPack = db.PackId, AiLevel = AiLevel.Normal, TurnSeconds = 10,
                        FastPlay = false, TurnTimer = false, ColorblindMode = false
                    },
                    Presets = new MemoryTeamPresetStore(), Seed = 670078u,
                    StartBattle = session.StartTeamBattle, NavigateToBattle = true,
                    ReadClipboard = () => "", WriteClipboard = value => { }
                });
                yield return null;
                Submit("team-auto"); Submit("team-start");
                yield return WaitRealPlayback(Current);
                var chosen = Current.Args.Settings;
                var changedDefaults = new GameSettings
                {
                    DefaultPack = "original_wave3", AiLevel = AiLevel.Expert, TurnSeconds = 17,
                    ColorblindMode = true, Animations = false, AnimationSpeed = 4, FastPlay = false, TurnTimer = false
                };
                Require(GameRuntimeSettings.TrySave(changedDefaults, out _), "隔离默认设置未保存。");
                Submit("battle-fast");
                var expected = GameSettings.FromJson(changedDefaults.ToJson());
                expected.FastPlay = true; expected.Animations = true; expected.AnimationSpeed = 2;
                Require(chosen.FastPlay && chosen.AnimationSpeed == 2 && chosen.Animations
                    && GameRuntimeSettings.Current.ToJson() == expected.ToJson(),
                    "本局快进按钮覆盖了新默认包、难度、时长、色弱或其他未修改字段。");
                Submit("battle-timer-toggle"); expected.TurnTimer = true;
                Require(chosen.TurnTimer && chosen.DefaultPack == db.PackId && chosen.AiLevel == AiLevel.Normal
                    && chosen.TurnSeconds == 10 && !chosen.ColorblindMode
                    && GameRuntimeSettings.Current.ToJson() == expected.ToJson(),
                    "本局限时按钮覆盖了新默认设置或把当前新默认回灌到旧局快照。");
                Submit("battle-timer-toggle");
                var notice = document.rootVisualElement.Q<Label>("battle-notice");
                Require(notice != null, "正式对战缺少保存状态提示。");
                string originalNotice = notice.text;
                var failureStore = new QuickSettingsFailStore(originalStore);
                GameRuntimeSettings.UseStore(failureStore);
                Submit("battle-fast");
                yield return new WaitForEndOfFrame();
                Require(failureStore.SaveAttempts == 1 && !chosen.FastPlay && chosen.AnimationSpeed == 1
                    && !GameRuntimeSettings.Current.FastPlay && Shown(notice)
                    && notice.text.Contains("设置暂未保存")
                    && (string.IsNullOrEmpty(originalNotice) || notice.text.Contains(originalNotice)),
                    "快捷设置写入失败时本局未生效、中文提示未显示或原内容声明被覆盖。");
                ValidateHud(); Capture("quick-settings-save-failure");
                GameRuntimeSettings.UseStore(originalStore);
                Submit("battle-fast");
                Require(chosen.FastPlay && chosen.AnimationSpeed == 2 && GameRuntimeSettings.Current.FastPlay
                    && notice.text == originalNotice && !notice.text.Contains("设置暂未保存")
                    && ReferenceEquals(storeField.GetValue(null), originalStore),
                    "恢复保存后快捷设置未成功生效、失败提示未清除或原内容声明改变。");
                Submit("battle-setup"); yield return null;
                var rule = document.rootVisualElement.Q<DropdownField>("team-ruleset");
                Require(rule != null, "快捷设置回归没有找到正式赛制选择。");
                rule.index = Rulesets.All.ToList().FindIndex(value => value.Id == "ranked") + 1;
                Submit("team-auto"); Submit("team-start");
                yield return WaitRealPlayback(Current);
                string immutable = Current.Args.Settings.ToJson();
                var timer = Button("battle-timer-toggle");
                Require(!timer.enabledInHierarchy, "显式赛制仍允许普通限时按钮覆盖规则时间。");
                SendSubmit(timer);
                Require(Current.Args.Settings.ToJson() == immutable, "显式赛制禁用计时按钮仍修改了设置。");
                Submit("battle-menu"); yield return null;
            }
            finally
            {
                GameRuntimeSettings.UseStore(originalStore);
                GameRuntimeSettings.TrySave(saved, out _);
            }
            report.steps.Add(Size + "：真实 Team 会话快进/限时按钮只保存所改字段，旧局快照与新默认包/难度/时长/色弱互不覆盖；保存失败仍本局生效并显示中文提示，恢复仓库重试清除提示且保留内容声明；显式赛制计时按钮禁用且回调无效。");
        }

        IEnumerator WaitRealPlayback(BattleScreen screen, List<TimelineAnimator> animators = null)
        {
            float until = Time.realtimeSinceStartup + 30;
            // 正式 Tick 可以在完成当前批次后派发电脑补位，连续两帧稳定才进入下一次玩家输入。
            int idleFrames = 0;
            while (idleFrames < 2)
            {
                Require(Time.realtimeSinceStartup < until, "真实时间线没有按 Tick 完成当前结算。");
                var animator = screen.TimelineAnimator;
                if (animator != null && animators != null && !animators.Contains(animator)) animators.Add(animator);
                yield return null;
                Require(ReferenceEquals(Current, screen), "等待真实时间线期间页面被替换。");
                idleFrames = screen.IsBusy ? 0 : idleFrames + 1;
            }
        }

        static void AssertReplay(Battle battle, string context)
        {
            string record = battle.Record.ToJson(); ulong hash = battle.StateHash();
            var replay = Replay.Run(battle.Db, BattleRecord.FromJson(record));
            Require(replay.StateHash() == hash
                && replay.Log.Select(entry => entry.Text).SequenceEqual(battle.Log.Select(entry => entry.Text))
                && BattleSummary.From(replay).ToText() == BattleSummary.From(battle).ToText()
                && replay.Random() == battle.Clone().Random(), context + " 的状态、日志、统计或后续随机数无法重放。");
            Require(battle.StateHash() == hash && battle.Record.ToJson() == record, context + " 的校验改写了来源。");
        }
    }
}
