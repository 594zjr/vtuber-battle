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
    public sealed partial class UiSmoke
    {
        readonly List<IDisposable> t70Connections = new List<IDisposable>();

        sealed class T70UnusedStore : ISaveStore
        {
            public int Calls;
            Exception Fail() { Calls++; return new InvalidOperationException("观战不得访问存档。"); }
            public void SaveReplay(string name, string json) { throw Fail(); }
            public string LoadReplay(string name) { throw Fail(); }
            public List<string> ListReplays() { throw Fail(); }
            public void SaveSettings(GameSettings value) { throw Fail(); }
            public GameSettings LoadSettings() { throw Fail(); }
            public void SaveProfile(Profile value) { throw Fail(); }
            public Profile LoadProfile() { throw Fail(); }
        }

        ReplayScreen T70CurrentReplay()
        {
            var field = typeof(ScreenRouter).GetField("current", BindingFlags.Instance | BindingFlags.NonPublic);
            var current = field?.GetValue(router) as ReplayScreen;
            Require(current != null, "真实回看页面没有建立。");
            return current;
        }

        IEnumerator T70CheckDefaultPresentation(Battle source, T70SaveStore store)
        {
            router.Show(ScreenIds.Replay, new ReplayScreenArgs
            {
                Content = source.Db, Record = source.Record, SaveStore = store, Clipboard = t70Clipboard,
                Settings = new GameSettings { Animations = true, AnimationSpeed = 1, ColorblindMode = true }
            });
            yield return T70Ready();
            var screen = T70CurrentReplay();
            var animator = screen.TimelineAnimator;
            Require(animator != null, "正常回看未使用主线 TimelineAnimator。");
            var portraitsField = typeof(ReplayScreen).GetField("portraits", BindingFlags.Instance | BindingFlags.NonPublic);
            var portraits = portraitsField?.GetValue(screen) as PortraitProvider;
            Require(portraits != null, "正常回看未使用主线 PortraitProvider。");
            var expected = new ReplayNavigator(source.Db, source.Record);
            T70CheckFrame(expected);
            var generated = screen.Stage.Fighter0.Q<Image>(StageNames.Portrait).image;
            Require(generated != null && generated.width == 128 && generated.height == 128
                && generated.name.StartsWith("StreamDuel.Portrait.", StringComparison.Ordinal), "原创包未显示程序生成的 128 像素头像。");
            Require(ReferenceEquals(generated, portraits.Get(source.Db, expected.Current.Active(0).Def.Id)), "头像缓存重复生成同一头像。");
            yield return T70Reveal(screen.Stage);
            Capture("t70-real-portraits-opening");
            T70Element<DropdownField>("playback-speed").value = "1x";
            yield return T70Submit("next-turn-button");
            expected.Next();
            Require(animator.IsPlaying, "正常回看前进未启动真实时间线动画。");
            Capture("t70-real-timeline-playing");
            float deadline = Time.realtimeSinceStartup + 20;
            while (animator.IsPlaying)
            {
                Require(Time.realtimeSinceStartup < deadline, "真实时间线动画没有按 Tick 完成。");
                yield return null;
            }
            T70CheckFrame(expected);
            Require(screen.Stage.FxLayer.childCount == 0, "正常动画完成后残留临时特效。");
            yield return T70PagePictures("t70-real-timeline-finished");
            yield return T70Submit("previous-turn-button");
            expected.Previous();
            Require(!animator.IsPlaying, "回看后退没有直接落到公开帧终态。");
            T70CheckFrame(expected);
            var stale = T70Button("next-turn-button");
            yield return T70Submit("back-button");
            SendSubmit(stale);
            yield return T70Ready();
            Require(!animator.IsPlaying && generated == null, "回看退出未释放动画或自有生成头像。");
            report.steps.Add(RequestedWidth + "×" + RequestedHeight
                + "：默认真实 TimelineAnimator Tick 完成、后退和释放；PortraitProvider 生成头像、缓存及释放通过，色弱模式开启。");
        }

        IEnumerator T70CheckSpectator(ContentDb db)
        {
            var roster = db.Streamers.Select(item => item.Id).ToArray();
            var source = new Battle(db, new IList<string>[] { roster.Take(6).ToArray(), roster.Skip(6).Take(6).ToArray() },
                new[] { "观战验收甲", "观战验收乙" }, 70070u);
            var listener = TcpTransport.Listen(0, out int port);
            t70Connections.Add(listener);
            var viewerWire = TcpTransport.Connect("127.0.0.1", port, 5000);
            t70Connections.Add(viewerWire);
            var hostWire = listener.Accept(5000);
            Require(hostWire != null, "真实观战 TCP 没有接受连接。");
            t70Connections.Add(hostWire);
            listener.Dispose();
            var feed = new SpectatorFeed(source, 2);
            feed.AddViewer(hostWire);
            var client = new SpectatorClient(db, viewerWire);
            var forbiddenStore = new T70UnusedStore();
            string clipboardBefore = t70Clipboard.Text;
            router.Show(ScreenIds.Replay, new ReplayScreenArgs
            {
                Content = db, Spectator = client, SpectatorConnection = viewerWire,
                SaveStore = forbiddenStore, Clipboard = t70Clipboard, Animator = new NullAnimator()
            });
            feed.Pump();
            var screen = T70CurrentReplay();
            yield return T70WaitFor(() => screen.DisplayedBattle != null, "观战开场没有通过 TCP 到达。");
            Require(screen.Spectating && screen.DisplayedBattle.StateHash() == source.StateHash(), "观战开场不一致。");
            ulong openingHash = screen.DisplayedBattle.StateHash();
            foreach (string name in new[] { "previous-turn-button", "next-turn-button", "play-pause-button", "jump-turn-button", "copy-share-button" })
                Require(!T70Button(name).enabledInHierarchy, "观战仍允许修改回放或导出未完录像：" + name);
            source.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
            var firstPublic = source.Clone();
            feed.Pump();
            yield return new WaitForSecondsRealtime(.15f);
            Require(screen.DisplayedBattle.StateHash() == openingHash && client.Battle.StateHash() == openingHash,
                "两回合延迟在第一个源回合结束时泄露了未来局面。");
            source.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
            feed.Pump();
            yield return new WaitForSecondsRealtime(.15f);
            Require(screen.DisplayedBattle.StateHash() == openingHash && client.Battle.StateHash() == openingHash,
                "两回合延迟在第二个源回合结束时提前释放局面。");
            yield return T70PagePictures("t70-spectator-delayed-opening");
            source.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
            feed.Pump();
            yield return T70WaitFor(() => screen.DisplayedBattle?.StateHash() == firstPublic.StateHash(), "延迟后的第一个公开回合没有到达。");
            T70CheckStageBattle(screen.Stage, firstPublic);
            Require(screen.DisplayedBattle.Record.ToJson() == firstPublic.Record.ToJson()
                && T70LogSnapshot(screen.DisplayedBattle) == T70LogSnapshot(firstPublic), "观战公开帧混入未来事件或日志。");
            Require(!screen.SpectatorFinished && source.Turn - screen.DisplayedBattle.Turn == 2, "观战延迟或完赛标志错误。");
            yield return T70PagePictures("t70-spectator-two-turn-delay");
            for (int step = 0; step < 500 && source.Phase != Phase.Over; step++)
            {
                T70AdvanceBattle(source);
                feed.Pump();
                if (step % 6 == 0) yield return null;
            }
            Require(source.Phase == Phase.Over, "真实观战源对局未完成。");
            feed.Finish();
            hostWire.CompleteSending();
            yield return T70WaitFor(() => screen.SpectatorFinished, "观战未在校验结束消息后显示终局。", 20);
            Require(client.Error == null && client.IsFinished && screen.DisplayedBattle.StateHash() == source.StateHash()
                && screen.DisplayedBattle.Record.ToJson() == source.Record.ToJson(), "观战终局状态或完整录像与真实源对局不一致。");
            T70CheckStageBattle(screen.Stage, source);
            yield return T70PagePictures("t70-spectator-finished");
            Require(forbiddenStore.Calls == 0 && t70Clipboard.Text == clipboardBefore, "观战访问了存档或剪贴板。");
            var stale = T70Button("next-turn-button");
            yield return T70Submit("back-button");
            SendSubmit(stale);
            bool closed = false;
            try { viewerWire.TryReceive(out _); } catch (ObjectDisposedException) { closed = true; }
            Require(closed, "页面没有关闭显式交给它所有的观战连接。");
            hostWire.Dispose();
            report.steps.Add(RequestedWidth + "×" + RequestedHeight
                + "：真实同机 TCP SpectatorFeed/Client 延迟 2 回合，未公开帧隔离，整局 finish/hash/record 一致，导航分享禁用，存档零访问、内存剪贴板不变及连接释放通过；不代表第二台设备网络验收。");
            yield return T70CheckSpectatorDisconnect(db, roster);
        }

        IEnumerator T70CheckSpectatorDisconnect(ContentDb db, string[] roster)
        {
            var source = new Battle(db, new IList<string>[] { roster.Take(6).ToArray(), roster.Skip(6).Take(6).ToArray() },
                new[] { "断线验收甲", "断线验收乙" }, 70071u);
            var listener = TcpTransport.Listen(0, out int port); t70Connections.Add(listener);
            var store = new T70SaveStore(System.IO.Path.Combine(report.outputDirectory, "spectator-menu-saves"));
            router.Show(ScreenIds.Replay, new ReplayScreenArgs
            {
                Content = db, SaveStore = store,
                Clipboard = t70Clipboard, Animator = new NullAnimator()
            });
            yield return T70Ready();
            T70Element<TextField>("spectator-address").value = "127.0.0.1";
            T70Element<IntegerField>("spectator-port").value = port;
            T70Element<TextField>("spectator-pack").value = db.PackId;
            yield return T70Submit("watch-button");
            var host = listener.Accept(5000); Require(host != null, "真实连接观战按钮没有建立 TCP。"); t70Connections.Add(host);
            listener.Dispose();
            var feed = new SpectatorFeed(source, 0); feed.AddViewer(host);
            var screen = T70CurrentReplay(); feed.Pump();
            yield return T70WaitFor(() => screen.DisplayedBattle != null, "断线验收未显示开场。");
            ulong visibleHash = screen.DisplayedBattle.StateHash();
            host.Dispose();
            yield return T70WaitFor(() => T70Label("replay-status").text.Contains("观战失败"), "未收到结束标记的物理断线未显示错误。");
            yield return T70Ready();
            Require(!screen.SpectatorFinished && screen.DisplayedBattle.StateHash() == visibleHash
                && !string.IsNullOrEmpty(T70Label("replay-status").text) && store.Saves == 0,
                "观战断线误报完赛、改变公开局面或写入录像。");
            yield return T70PagePictures("t70-spectator-disconnected");
            yield return T70Submit("back-button");
        }

        IEnumerator T70CheckResultRoutes(ContentDb db, T70SaveStore store)
        {
            var roster = db.Streamers.Select(item => item.Id).ToArray();
            foreach (bool draw in new[] { false, true })
            {
                var battle = new Battle(db, new IList<string>[] { roster.Take(6).ToArray(), roster.Skip(6).Take(6).ToArray() },
                    new[] { "零回合甲", "零回合乙" }, 70072u);
                if (draw) { battle.OfferDraw(0); battle.RespondDraw(1, true); }
                else battle.Surrender(0);
                router.Show(ScreenIds.Result, new ResultScreenArgs { Battle = battle, PlayerSide = 1, SaveStore = store, Clipboard = t70Clipboard });
                yield return T70Ready();
                Require(T70Label("result-rounds").text.EndsWith("完成回合数：0", StringComparison.Ordinal)
                    && T70Label("result-mvp").text == "MVP：本局暂无获选主播", "零回合终止错误显示完成回合或 MVP。");
                Require(T70Label("result-outcome").text == (draw ? "平局" : "胜利"), "乙方视角结算胜负错误。");
                yield return T70Submit("result-copy-report");
                Require(t70Clipboard.Text == BattleReport.Markdown(battle, 1), "乙方战报没有使用乙方视角。");
                yield return T70PagePictures("t70-result-zero-turn-" + (draw ? "draw" : "surrender"), "result-scroll");
            }
            foreach (bool ranked in new[] { false, true })
            {
                router.Show(ScreenIds.Team, new TeamScreenArgs
                {
                    Content = db, Packs = legacy.MenuPacks, Policy = db.Policy,
                    Settings = new GameSettings { DefaultPack = db.PackId, FastPlay = true, TurnTimer = false },
                    Presets = new MemoryTeamPresetStore(), Seed = 70073u,
                    StartBattle = legacy.StartTeamBattle, NavigateToBattle = true,
                    ReadClipboard = () => "", WriteClipboard = value => { }
                });
                yield return T70Ready();
                var rule = T70Element<DropdownField>("team-ruleset");
                rule.index = ranked ? Rulesets.All.ToList().FindIndex(value => value.Id == "ranked") + 1 : 0;
                yield return T70Submit("team-auto");
                yield return T70Submit("team-start");
                Require(router.CurrentScreenId == ScreenIds.Battle, "真实组队开始未交接到对战页。");
                var first = legacy.CurrentTeamBattle;
                var match = T70LegacyField<Match>("teamMatch");
                Require((match != null) == ranked && legacy.ResultRequested != null, "组队赛制或结算路由没有接好。");
                first.ResolveSurrenders(false, true);
                T70InvokeLegacy("SkipPlayback");
                // 正式结算按钮委托的真实入口，不替换 GameBoot 回调；此处不声称人工鼠标点击。
                legacy.ResultRequested(first);
                yield return T70Ready();
                Require(router.CurrentScreenId == ScreenIds.Result && ReferenceEquals(((ResultScreenArgs)router.CurrentArgs).Battle, first),
                    "正式查看结算回调没有打开真实本局。");
                Require(T70Button("result-rematch").text == (ranked ? "继续下一局" : "再来一局"), "结算再战文案未区分整场比赛。");
                ulong firstHash = first.StateHash(); string firstRecord = first.Record.ToJson();
                yield return T70Submit("result-rematch");
                var second = legacy.CurrentTeamBattle;
                Require(router.CurrentScreenId == ScreenIds.Battle && second != null && !ReferenceEquals(first, second)
                    && first.StateHash() == firstHash && first.Record.ToJson() == firstRecord, "结算再战没有新建本局或改写上局。");
                if (ranked)
                {
                    Require(ReferenceEquals(match, T70LegacyField<Match>("teamMatch")) && match.Wins.SequenceEqual(new[] { 1, 0 })
                        && second.Seed == first.Seed + 1, "继续下一局丢失整场比分或派生种子。");
                    second.ResolveSurrenders(false, true); T70InvokeLegacy("SkipPlayback");
                    Require(match.Over && match.Wins.SequenceEqual(new[] { 2, 0 }), "第二局没有按真实 Match 计分完赛。");
                    legacy.ResultRequested(second); yield return T70Ready();
                    Require(T70Button("result-rematch").text == "再来一局", "整场结束仍显示继续下一局。");
                    yield return T70Submit("result-rematch");
                    Require(!ReferenceEquals(match, T70LegacyField<Match>("teamMatch"))
                        && T70LegacyField<Match>("teamMatch").Wins.SequenceEqual(new[] { 0, 0 }), "整场再战没有创建全新比赛。");
                    second = legacy.CurrentTeamBattle;
                }
                second.ResolveSurrenders(true, true); T70InvokeLegacy("SkipPlayback");
                legacy.ResultRequested(second); yield return T70Ready();
                yield return T70Submit("result-return-team");
                Require(router.CurrentScreenId == ScreenIds.Team && legacy.CurrentTeamBattle == null, "真实结算返回组队未清理旧对局。");
            }
            report.steps.Add(RequestedWidth + "×" + RequestedHeight
                + "：零完成回合投降/和局无 MVP、乙方报告视角；真实 Team 开始→LocalBattleSession.ResultRequested→Result→再战/返回组队；排位继续保留 1:0、2:0 完赛后新 Match 归零通过。正式结算入口以真实委托调用，按钮采用 NavigationSubmitEvent。");
        }

        static void T70AdvanceBattle(Battle battle)
        {
            if (battle.Phase == Phase.Choose) battle.ResolveTurn(Ai.Choose(battle, 0), Ai.Choose(battle, 1));
            else if (battle.Phase == Phase.Replace)
            {
                for (int side = 0; side < 2 && battle.Phase == Phase.Replace; side++)
                    if (battle.Need[side]) battle.Replace(side, Ai.ChooseReplacement(battle, side));
            }
            else throw new InvalidOperationException("观战源对局进入意外阶段：" + battle.Phase);
        }

        static void T70CheckStageBattle(BattleStage stage, Battle battle)
        {
            for (int side = 0; side < 2; side++)
            {
                var card = side == 0 ? stage.Fighter0 : stage.Fighter1;
                var hp = card.Q<ProgressBar>(StageNames.HpBar);
                Require(hp.title == battle.Active(side).Hp + " / " + battle.Active(side).MaxHp
                    && Mathf.Abs(hp.value - battle.Active(side).Pct) <= 1, "观战舞台人气与已公开 Core 快照不同。");
                Require(card.Q<Label>("fighter-name").text == battle.Sides[side].Name + " · " + battle.Active(side).Name,
                    "观战舞台上场主播与已公开快照不同。");
            }
        }

        IEnumerator T70PagePictures(string name, string scrollName = "replay-scroll")
        {
            var scroll = T70Element<ScrollView>(scrollName);
            scroll.scrollOffset = Vector2.zero; yield return T70Ready();
            T70ValidatePage(router.CurrentScreenId); Capture(name + "-top");
            scroll.scrollOffset = new Vector2(0, scroll.verticalScroller.highValue); yield return T70Ready();
            T70ValidatePage(router.CurrentScreenId); Capture(name + "-bottom");
        }

        static IEnumerator T70WaitFor(Func<bool> condition, string reason, float seconds = 10)
        {
            float deadline = Time.realtimeSinceStartup + seconds;
            while (!condition()) { Require(Time.realtimeSinceStartup < deadline, reason); yield return null; }
            yield return T70Ready();
        }

        T T70LegacyField<T>(string name)
        {
            var field = typeof(LocalBattleSession).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Require(field != null, "缺少真实本地对战字段：" + name);
            return (T)field.GetValue(legacy);
        }

        void T70InvokeLegacy(string name)
        {
            var method = typeof(LocalBattleSession).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Require(method != null, "缺少真实本地对战入口：" + name);
            try { method.Invoke(legacy, null); }
            catch (TargetInvocationException error) { throw error.InnerException ?? error; }
        }

        void OnDestroy()
        {
            foreach (var connection in t70Connections) connection.Dispose();
            t70Connections.Clear();
        }
    }
}
