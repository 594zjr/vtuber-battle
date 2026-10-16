using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using StreamDuel.Core;
using UnityEngine;
using UnityEngine.UIElements;

namespace StreamDuel.Game
{
    internal static partial class T66ReviewChecks
    {
        public static IEnumerator RunRulesets(ScreenRouter router, LocalBattleSession legacy, Action<string> capture)
        {
            RulesetRequire(Environment.GetCommandLineArgs().Contains("--t66-smoke-test"), "赛制回归仅供隔离验收。");
            var save = typeof(GameRuntimeSettings).GetField("store", BindingFlags.Static | BindingFlags.NonPublic);
            var memoryStoreType = typeof(GameRuntimeSettings).GetNestedType("MemorySaveStore", BindingFlags.NonPublic);
            RulesetRequire(GameRuntimeSettings.IsSmokeTest && save != null && memoryStoreType != null
                && save.GetValue(null)?.GetType() == memoryStoreType, "赛制回归必须使用隔离的内存设置仓库。");
            var db = legacy.MenuPacks.First(pack => pack.PackId == "original");
            string season = db.Seasons.Keys.OrderBy(id => id, StringComparer.Ordinal).FirstOrDefault();
            string[] defaults = Rulesets.All.Select(rule => rule.ToJson()).ToArray();
            foreach (var definition in Rulesets.All)
            {
                RulesetShow(router, legacy, db, season);
                yield return null;
                yield return new WaitForEndOfFrame();
                var selector = router.Document.rootVisualElement.Q<DropdownField>("team-ruleset");
                RulesetRequire(selector != null && selector.choices.Count == Rulesets.All.Count + 1
                    && selector.index == 0, "赛制列表缺项或原单局默认改变。");
                selector.index = Rulesets.All.ToList().FindIndex(rule => rule.Id == definition.Id) + 1;
                RulesetSubmit(router, "team-auto");
                yield return new WaitForEndOfFrame();
                capture("ruleset-" + definition.Id + "-team");
                RulesetSubmit(router, "team-start");
                yield return new WaitForEndOfFrame();
                RulesetRequire(router.CurrentScreenId == ScreenIds.Battle && legacy.enabled, "赛制没有由真实开始按钮交接到对战页。");
                var match = RulesetField<Match>(legacy, "teamMatch");
                var applied = RulesetField<Ruleset>(legacy, "teamRuleset");
                var expectedRule = Ruleset.FromJson(definition.ToJson()); expectedRule.Season = season;
                RulesetRequire(applied.ToJson() == expectedRule.ToJson(), "交接时丢失赛制或独立赛季参数。");
                RulesetRequire(defaults.SequenceEqual(Rulesets.All.Select(rule => rule.ToJson())), "组队修改了内置赛制定义。");
                var initial = legacy.CurrentTeamBattle;
                var originalTeams = initial.Record.Teams.Select(team => team.ToArray()).ToArray();
                int games = 0;
                bool replacementChecked = false;
                do
                {
                    var current = legacy.CurrentTeamBattle;
                    RulesetRequire(current.Seed == unchecked(660066u + (uint)games), "下一局没有使用 Match 派生的种子。");
                    RulesetRequire(current.Record.BanOne == definition.BanOne && current.Record.LeadPick == definition.LeadPick
                        && current.Record.Modifiers.SequenceEqual(definition.Modifiers.OrderBy(id => id, StringComparer.Ordinal)),
                        "实际 BattleRecord 的禁用、首发或修饰器与所选赛制不一致。");
                    for (int side = 0; side < 2; side++)
                        RulesetRequire(current.Record.Teams[side].SequenceEqual(originalTeams[side]), "下一局继承了上局五人或重排后的名单。");
                    var clock = RulesetField<TurnClock>(legacy, "teamClock");
                    if (definition.Id == "ranked")
                    {
                        RulesetRequire(current.Phase == Phase.Ban && current.Sides.All(side => side.Team.Count == 6), "排位没有进入真实六人禁用阶段。");
                        RulesetRequire(current.Sides.SelectMany(side => side.Team).All(fighter => fighter.Hp == fighter.MaxHp
                            && !fighter.Fainted && fighter.Statuses.Count == 0 && fighter.Fields.Count == 0
                            && fighter.Stages.Values.All(value => value == 0) && fighter.Skills.All(skill => skill.Pp == skill.Max)),
                            "新局继承了上一局的人气、PP、状态或能力变化。");
                        RulesetRequire(clock != null && clock.TurnLeft(0) == 10 && clock.BankLeft(0) == 30
                            && clock.TurnLeft(1) == 10 && clock.BankLeft(1) == 30, "排位新局没有重置双方 10/30 时间。");
                        ulong unchanged = current.StateHash();
                        legacy.AdvanceTeamClockSmoke(400);
                        RulesetRequire(current.StateHash() == unchanged && clock.TurnLeft(0) == 10 && clock.BankLeft(0) == 30,
                            "禁用阶段扣除了时间或改变了对局。");
                        if (games == 0) capture("ruleset-ranked-ban");
                        legacy.AdvanceTeamSmoke();
                        RulesetRequire(current.Phase == Phase.Lead && current.Sides.All(side => side.Team.Count == 5), "禁用没有由 Core 移除双方各一人并进入首发。");
                        unchanged = current.StateHash();
                        legacy.AdvanceTeamClockSmoke(400);
                        RulesetRequire(current.StateHash() == unchanged && clock.TurnLeft(0) == 10 && clock.BankLeft(0) == 30,
                            "首发阶段扣除了时间或改变了对局。");
                        yield return new WaitForEndOfFrame();
                        if (games == 0) capture("ruleset-ranked-lead");
                    }
                    else RulesetRequire(current.Phase == Phase.Choose && clock == null,
                        "休闲/乱斗仍被旧首发选择或 10 秒设置覆盖。");

                    if (definition.Id == "ranked" && games == 0)
                    {
                        // 首发提交、开场播放结束后再检查真实计时管道；电脑选择已缓存，不重新调用 AI。
                        legacy.AdvanceTeamSmoke(); legacy.AdvanceTeamSmoke();
                        RulesetRequire(current.Phase == Phase.Choose && !RulesetField<bool>(legacy, "playing"), "计时夹具未进入可出招阶段。");
                        legacy.AdvanceTeamClockSmoke(1.25);
                        double beforeRepeat = clock.TurnLeft(0), bankBeforeRepeat = clock.BankLeft(0);
                        ulong beforeRepeatHash = current.StateHash();
                        RulesetInvoke(legacy, "AfterPlayback");
                        RulesetRequire(clock.TurnLeft(0) <= beforeRepeat && clock.BankLeft(0) <= bankBeforeRepeat
                            && current.StateHash() == beforeRepeatHash, "重复播放完成回调重置了当前决策时间或重复选择电脑行动。");
                        double pausedTurn = clock.TurnLeft(0), pausedBank = clock.BankLeft(0);
                        var cached = RulesetField<BattleAction>(legacy, "teamClockComputerAction");
                        router.Show(ScreenIds.Menu);
                        legacy.AdvanceTeamClockSmoke(400);
                        RulesetRequire(clock.TurnLeft(0) == pausedTurn && clock.BankLeft(0) == pausedBank,
                            "菜单隐藏期间仍扣除赛制时间。");
                        // 模拟任意长的隐藏经过时间；重新启用只修正时钟基线，不重建预算或选择。
                        RulesetSetField(legacy, "teamClockTimestamp", 0L);
                        router.Show(ScreenIds.Battle, legacy.CreateBattleScreenArgs());
                        RulesetRequire(RulesetField<long>(legacy, "teamClockTimestamp") > 0
                            && clock.TurnLeft(0) == pausedTurn && clock.BankLeft(0) == pausedBank
                            && RulesetField<BattleAction>(legacy, "teamClockComputerAction").ToString() == cached.ToString()
                            && current.StateHash() == beforeRepeatHash, "重返对战没有保留预算、电脑选择和当前对局。");
                        var expected = current.Clone();
                        var foe = RulesetField<BattleAction>(legacy, "teamClockComputerAction");
                        expected.ResolveTurn(TurnClock.TimeoutAction(expected, 0), foe);
                        RulesetSetField(legacy, "timerOn", false);
                        legacy.AdvanceTeamClockSmoke(clock.TurnLeft(0) + clock.BankLeft(0) + 1);
                        RulesetRequire(current.StateHash() == expected.StateHash() && current.Record.ToJson() == expected.Record.ToJson(),
                            "出招超时没有使用 Core 默认行动，或重复选择电脑行动消耗了随机数。");
                        ulong playingHash = current.StateHash(); double bank = clock.BankLeft(0);
                        legacy.AdvanceTeamClockSmoke(400);
                        RulesetRequire(current.StateHash() == playingHash && clock.BankLeft(0) == bank, "播放期间仍扣时或提交行动。");
                    }

                    int steps = 0;
                    while ((current.Phase != Phase.Over || RulesetField<bool>(legacy, "playing")) && steps++ < 5000)
                    {
                        bool waiting = !RulesetField<bool>(legacy, "playing");
                        if (definition.Id == "ranked" && !replacementChecked && waiting
                            && current.Phase == Phase.Replace && current.Need[0])
                        {
                            int slot = TurnClock.TimeoutReplacement(current, 0);
                            var expected = current.Clone();
                            int other = RulesetField<int>(legacy, "teamClockComputerReplacement");
                            expected.Replace(0, slot);
                            if (expected.Phase == Phase.Replace && expected.Need[1]) expected.Replace(1, other);
                            double savedBank = clock.BankLeft(0), otherTurn = clock.TurnLeft(1), otherBank = clock.BankLeft(1);
                            RulesetInvoke(legacy, "AfterPlayback");
                            RulesetRequire(clock.BankLeft(0) <= savedBank, "重复补位回调增加了时间池。");
                            legacy.AdvanceTeamClockSmoke(clock.TurnLeft(0) + clock.BankLeft(0) + 1);
                            RulesetRequire(current.StateHash() == expected.StateHash() && current.Record.ToJson() == expected.Record.ToJson(),
                                "补位超时没有按 Core 最小合法位置补位，或双方补位顺序不一致。");
                            RulesetRequire(clock.TurnLeft(1) == otherTurn && clock.BankLeft(1) == otherBank,
                                "无需补位或已提交的电脑仍在扣时。");
                            replacementChecked = true;
                        }
                        else if (definition.Id == "ranked" && !replacementChecked && waiting && current.Phase == Phase.Choose)
                            RulesetInvoke(legacy, "Submit", BattleAction.Pass());
                        else legacy.AdvanceTeamSmoke();
                        if (steps % 40 == 0) yield return null;
                    }
                    RulesetRequire(current.Phase == Phase.Over && !RulesetField<bool>(legacy, "playing"), "赛制真实对局未在验收步数内完成。");
                    games++;
                    var completed = RulesetField<List<Battle>>(legacy, "teamMatchGames");
                    string finishedRecord = current.Record.ToJson(); ulong finishedHash = current.StateHash();
                    var score = match.Wins; int draws = match.Draws;
                    RulesetInvoke(legacy, "AfterPlayback"); RulesetInvoke(legacy, "SkipPlayback");
                    RulesetRequire(completed.Count == games && match.Wins.SequenceEqual(score) && match.Draws == draws,
                        "重复播放完成回调重复上报或重复计分。");
                    RulesetReplay(current);
                    yield return AwaitBattleFinal(router, legacy);
                    if (games == 1 || match.Over) capture("ruleset-" + definition.Id + "-result-" + games);
                    if (match.Over) break;
                    RulesetInvoke(legacy, "ContinueTeamMatch");
                    RulesetRequire(ReferenceEquals(match, RulesetField<Match>(legacy, "teamMatch"))
                        && !ReferenceEquals(current, legacy.CurrentTeamBattle), "继续下一局错误新建了整场比赛或复用了旧 Battle。");
                    RulesetRequire(current.StateHash() == finishedHash && current.Record.ToJson() == finishedRecord,
                        "开启下一局改写了已完成局的记录或状态。");
                }
                while (games <= definition.BestOf);
                RulesetRequire(match.Over && games <= definition.BestOf, "赛制超出 Core 局数上限。");
                RulesetRequire(definition.BestOf == 1 ? games == 1 : games >= 2, "排位只打了一局或单局赛制被扩成多局。");
                RulesetRequire(definition.Id != "ranked" || replacementChecked, "排位夹具没有实际经过本方补位，不能宣称补位超时已验证。");
                router.Show(ScreenIds.Menu);
                yield return null;
            }
            yield return RulesetScoreCases(router, legacy, db, season);
            var fan = legacy.MenuPacks.FirstOrDefault(pack => pack.PackId.StartsWith("fan_", StringComparison.Ordinal));
            if (fan != null)
            {
                RulesetShow(router, legacy, fan, null);
                yield return null;
                var field = router.Document.rootVisualElement.Q<DropdownField>("team-ruleset");
                field.index = Rulesets.All.ToList().FindIndex(rule => rule.Id == "ranked") + 1;
                RulesetSubmit(router, "team-auto"); RulesetSubmit(router, "team-start");
                RulesetRequire(legacy.CurrentTeamBattle.Phase == Phase.Ban, "同人排位没有进入禁用阶段。");
                yield return new WaitForEndOfFrame(); capture("ruleset-ranked-fan-ban");
                legacy.AdvanceTeamSmoke();
                RulesetRequire(legacy.CurrentTeamBattle.Phase == Phase.Lead, "同人排位没有进入首发阶段。");
                yield return new WaitForEndOfFrame(); capture("ruleset-ranked-fan-lead");
                legacy.CurrentTeamBattle.ResolveSurrenders(false, true);
                RulesetInvoke(legacy, "SkipPlayback");
                yield return AwaitBattleFinal(router, legacy); capture("ruleset-ranked-fan-result");
                router.Show(ScreenIds.Menu);
            }
        }

        static IEnumerator RulesetScoreCases(ScreenRouter router, LocalBattleSession legacy, ContentDb db, string season)
        {
            // 只用公开的 Core 终止入口造胜/负/平局，检查界面的整场推进；不伪造 Winner 或比分。
            foreach (int[] winners in new[] { new[] { 0, 0 }, new[] { 0, 1, -1 } })
            {
                RulesetShow(router, legacy, db, season);
                yield return null;
                var field = router.Document.rootVisualElement.Q<DropdownField>("team-ruleset");
                field.index = Rulesets.All.ToList().FindIndex(rule => rule.Id == "ranked") + 1;
                RulesetSubmit(router, "team-auto"); RulesetSubmit(router, "team-start");
                var match = RulesetField<Match>(legacy, "teamMatch");
                for (int index = 0; index < winners.Length; index++)
                {
                    var game = legacy.CurrentTeamBattle;
                    RulesetRequire(game.Seed == unchecked(660066u + (uint)index), "比分夹具种子顺序错误。");
                    int winner = winners[index];
                    game.ResolveSurrenders(winner != 0, winner != 1);
                    RulesetInvoke(legacy, "SkipPlayback");
                    RulesetReplay(game);
                    RulesetRequire(match.Over == (index == winners.Length - 1), "连胜提前结束或平局消耗局数的行为与 Match 不同。");
                    if (!match.Over) RulesetInvoke(legacy, "ContinueTeamMatch");
                }
                RulesetRequire(match.Wins.SequenceEqual(winners.Length == 2 ? new[] { 2, 0 } : new[] { 1, 1 })
                    && match.Draws == (winners.Length == 2 ? 0 : 1)
                    && RulesetField<List<Battle>>(legacy, "teamMatchGames").Count == winners.Length, "结束比分或完成局数错误。");
                var previous = match;
                RulesetInvoke(legacy, "ContinueTeamMatch");
                RulesetRequire(!ReferenceEquals(previous, RulesetField<Match>(legacy, "teamMatch"))
                    && legacy.CurrentTeamBattle.Seed == 660066
                    && RulesetField<List<Battle>>(legacy, "teamMatchGames").Count == 0,
                    "整场结束后的重新比赛没有新建独立 Match。");
            }
            router.Show(ScreenIds.Menu);
            yield return null;
        }

        static void RulesetShow(ScreenRouter router, LocalBattleSession legacy, ContentDb db, string season)
        {
            router.Show(ScreenIds.Team, new TeamScreenArgs
            {
                Content = db, Packs = legacy.MenuPacks, Policy = db.Policy, SeasonId = season,
                Settings = new GameSettings { DefaultPack = db.PackId, AiLevel = AiLevel.Normal, FastPlay = true, TurnTimer = true },
                Presets = new MemoryTeamPresetStore(), Seed = 660066,
                StartBattle = legacy.StartTeamBattle, NavigateToBattle = true,
                ReadClipboard = () => "", WriteClipboard = value => { }, Return = () => router.Show(ScreenIds.Menu)
            });
        }

        public static IEnumerator AwaitBattleFinal(ScreenRouter router, LocalBattleSession legacy)
        {
            var battle = legacy.CurrentTeamBattle;
            RulesetRequire(battle != null && battle.Phase == Phase.Over, "终局画面等待必须从实际已结束的会话开始。");
            string record = battle.Record.ToJson(), score = legacy.MatchScoreText;
            ulong hash = battle.StateHash();
            string expectedOutcome = battle.Winner < 0 ? "平局"
                : TextInput.ForDisplay(battle).Sides[battle.Winner].Name + " 获胜";
            float until = Time.realtimeSinceStartup + 5;
            for (int frame = 0; frame < 60; frame++)
            {
                var screen = BattleScreen.ActiveInstance;
                RulesetRequire(router.CurrentScreenId == ScreenIds.Battle && screen != null
                    && ReferenceEquals(screen.CurrentBattle, battle), "终局截图等待期间丢失了当前正式对战页。");
                // 验收桥可以在一帧推进多步，先完成可见旧批次，再让正式 Tick 接收当前会话快照。
                if (screen.IsBusy) screen.SkipPlayback();
                screen.Tick(0);
                yield return null;
                yield return new WaitForEndOfFrame();
                RulesetRequire(battle.StateHash() == hash && battle.Record.ToJson() == record
                    && legacy.MatchScoreText == score, "等待终局显示时重复结算、计分或改写了最终录像。");
                var root = router.Document.rootVisualElement;
                var outcome = root.Q("battle-outcome");
                var turn = root.Q<Label>("battle-turn");
                var phase = root.Q<Label>("battle-phase");
                var displayedScore = root.Q<Label>("battle-match-score");
                var winner = root.Q<Label>("battle-outcome-text");
                bool shown = outcome != null && outcome.worldBound.width > 0 && outcome.worldBound.height > 0;
                for (var element = outcome; element != null && shown; element = element.hierarchy.parent)
                    shown = element.visible && element.resolvedStyle.display != DisplayStyle.None;
                if (!screen.IsBusy && !legacy.IsPlaybackPending && shown
                    && turn != null && turn.text == "开播对决 · 第 " + battle.Turn + " 回合"
                    && phase != null && phase.text == "对局结束。"
                    && screen.Stage.FieldBanner.text == "对局结束"
                    && displayedScore != null && displayedScore.text == score
                    && winner != null && winner.text == expectedOutcome)
                    yield break;
                RulesetRequire(Time.realtimeSinceStartup < until, "正式终局弹层、回合、阶段或比分未追上已结束的会话。");
            }
            throw new InvalidOperationException("正式终局画面在限定帧数内未同步，不能保存为终局证据。");
        }

        static void RulesetReplay(Battle game)
        {
            string record = game.Record.ToJson(); ulong hash = game.StateHash();
            var replayed = Replay.Run(game.Db, BattleRecord.FromJson(record));
            RulesetRequire(replayed.StateHash() == hash
                && replayed.Log.Select(entry => entry.Text).SequenceEqual(game.Log.Select(entry => entry.Text))
                && BattleSummary.From(replayed).ToText() == BattleSummary.From(game).ToText()
                && replayed.Random() == game.Clone().Random(), "赛制单局的状态、日志、统计或后续随机数无法回放。");
            RulesetRequire(game.Record.ToJson() == record && game.StateHash() == hash, "验证回放改动了源对局。");
        }

        static T RulesetField<T>(LocalBattleSession target, string name)
        {
            var field = typeof(LocalBattleSession).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            RulesetRequire(field != null, "缺少赛制桥字段：" + name);
            return (T)field.GetValue(target);
        }

        static void RulesetSetField<T>(LocalBattleSession target, string name, T value)
        {
            var field = typeof(LocalBattleSession).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            RulesetRequire(field != null, "缺少赛制桥字段：" + name);
            field.SetValue(target, value);
        }

        static void RulesetInvoke(LocalBattleSession target, string name, params object[] args)
        {
            var method = typeof(LocalBattleSession).GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            RulesetRequire(method != null, "缺少赛制桥入口：" + name);
            try { method.Invoke(target, args); }
            catch (TargetInvocationException error) { throw error.InnerException ?? error; }
        }

        static void RulesetSubmit(ScreenRouter router, string name)
        {
            var button = router.Document.rootVisualElement.Q<Button>(name);
            RulesetRequire(button != null && button.enabledInHierarchy && button.visible, "赛制回归按钮不可用：" + name);
            using (var evt = NavigationSubmitEvent.GetPooled()) { evt.target = button; button.SendEvent(evt); }
        }

        static void RulesetRequire(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
