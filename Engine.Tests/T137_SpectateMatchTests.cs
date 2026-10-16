using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Threading;
using System.Text.Json;
using System.Text.Json.Nodes;
using StreamDuel.Core;
using Xunit;
using static T137_SpectateMatchTests;

public class T137_SpectateMatchTests
{
    internal sealed class Wire : INetTransport
    {
        readonly INetTransport inner;
        public readonly List<string> Sent = new List<string>();
        readonly List<string> pending = new List<string>();
        public bool FailSend;
        public bool Hold;
        public int Received;

        public Wire(INetTransport inner) { this.inner = inner; }
        public void Send(string message)
        {
            if (FailSend) throw new IOException("测试观众发送失败");
            if (Hold) { pending.Add(message); return; }
            inner.Send(message);
            Sent.Add(message);
        }
        public bool TryReceive(out string message)
        {
            if (!inner.TryReceive(out message)) return false;
            Received++;
            return true;
        }
        public void Release()
        {
            Hold = false;
            foreach (string message in pending) Send(message);
            pending.Clear();
        }
    }

    internal sealed class Viewer
    {
        public readonly Wire Host, ClientWire;
        public readonly SpectatorClient Client;

        public Viewer(ContentDb db, SpectatorMatchFeed feed, bool match = true, bool chat = false, string name = "观众", bool delayJoin = false)
        {
            LoopbackPair.Create(out var host, out var client);
            Host = new Wire(host);
            ClientWire = new Wire(client) { Hold = delayJoin };
            Client = new SpectatorClient(db, ClientWire, enableChat: chat, displayName: name, enableMatch: match);
            feed.AddViewer(Host);
        }
    }

    internal static ContentDb Content()
    {
        // 固定规则让跨局哈希、延迟和计分的验收不依赖正式包平衡调整。
        var db = new ContentDb { PackId = "t137_fixture", PackName = "多局观战验收", PackNotice = "独立测试数据" };
        db.Types["talk"] = new TypeDef { Id = "talk", Name = "聊天", Color = "#808080" };
        db.Chart["talk"] = new Dictionary<string, double> { { "talk", 1 } };
        db.Personas["neutral"] = new PersonaDef { Id = "neutral", Name = "测试人设", Code = "T_LOWHP_POWER" };
        db.Skills["chip"] = new SkillDef
        {
            Id = "chip", Name = "测试扣血", Type = "talk", Cat = "status", Pp = 40,
            Effects = new List<EffectDef> { new EffectDef { At = "post", Code = "FIXED_DAMAGE", Target = "foe", N = 20 } }
        };
        db.Skills["finish"] = new SkillDef
        {
            Id = "finish", Name = "测试下播", Type = "talk", Cat = "status", Pp = 40,
            Effects = new List<EffectDef> { new EffectDef { At = "post", Code = "FIXED_DAMAGE", Target = "foe", N = 99999 } }
        };
        db.Skills["wait"] = new SkillDef { Id = "wait", Name = "测试等待", Type = "talk", Cat = "status", Pp = 40 };
        db.Struggle = new SkillDef { Id = "struggle", Name = "测试硬撑", Type = "talk", Cat = "interact", Power = 20, Pp = 1 };
        for (int index = 0; index < 12; index++)
            db.Streamers.Add(new StreamerDef
            {
                Id = "u" + index.ToString("D2"), Name = "测试主播" + index, Type = "talk", Persona = "neutral",
                Stats = new Dictionary<string, int>
                {
                    { "hp", 100 }, { "atk", 100 }, { "def", 100 }, { "spa", 100 }, { "sdef", 100 }, { "spd", 200 - index }
                },
                Skills = new List<string> { "chip", "finish", "wait", "wait" }, Fifth = "finish"
            });
        db.Validate();
        return db;
    }

    internal static Ruleset Rules(int bestOf = 3, bool preparation = false)
    {
        return new Ruleset
        {
            Id = "test_match", Name = "三局两胜验收", BestOf = bestOf,
            BanOne = preparation, LeadPick = preparation,
            Modifiers = preparation ? new List<string> { "hp_x2", "half_pp" } : null
        };
    }

    internal static Match Make(ContentDb db, int bestOf = 3, bool preparation = false)
    {
        return new Match(db, Rules(bestOf, preparation), new IList<string>[]
        {
            db.Streamers.Take(6).Select(unit => unit.Id).ToArray(), db.Streamers.Skip(6).Take(6).Select(unit => unit.Id).ToArray()
        }, new[] { "甲\n原名", "乙\"原名" }, uint.MaxValue);
    }

    static void Prepare(Battle battle)
    {
        if (battle.Phase == Phase.Ban) { battle.ChooseBan(0, 5); battle.ChooseBan(1, 4); }
        if (battle.Phase == Phase.Lead) { battle.ChooseLead(1, 2); battle.ChooseLead(0, 1); }
    }

    internal static void End(Battle battle, int winner)
    {
        if (winner == -1) battle.ResolveSurrenders(true, true);
        else battle.Surrender(1 - winner);
        Assert.Equal(Phase.Over, battle.Phase);
        Assert.Equal(winner, battle.Winner);
    }

    static string TypeOf(string message)
    {
        using var json = JsonDocument.Parse(message);
        return json.RootElement.GetProperty("type").GetString();
    }

    static string Edit(string message, Action<JsonObject> edit)
    {
        var obj = JsonNode.Parse(message).AsObject();
        edit(obj);
        return obj.ToJsonString();
    }

    static string Log(Battle battle)
    {
        return JsonSerializer.Serialize(battle.Log, new JsonSerializerOptions { IncludeFields = true });
    }

    internal static void SameBattle(Battle expected, Battle actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.StateHash(), actual.StateHash());
        Assert.Equal(expected.Record.ToJson(), actual.Record.ToJson());
        Assert.Equal(Log(expected), Log(actual));
        Assert.Equal(BattleSummary.From(expected).ToText(), BattleSummary.From(actual).ToText());
        Assert.Equal(expected.Clone().Random(), actual.Clone().Random());
    }

    static void Pump(SpectatorMatchFeed feed, params Viewer[] viewers)
    {
        for (int i = 0; i < 3; i++)
        {
            feed.Pump();
            foreach (var viewer in viewers) viewer.Client.Poll();
        }
        Assert.All(viewers, viewer => Assert.Null(viewer.Client.Error));
    }

    [Theory]
    [InlineData(3, "010", 2, 1, 0)]
    [InlineData(3, "00", 2, 0, 0)]
    [InlineData(5, "01010", 3, 2, 0)]
    [InlineData(3, "0dd", 1, 0, 2)]
    [InlineData(3, "ddd", 0, 0, 3)]
    public void ViewersFollowEveryGameAndTheFinalScoreWithoutChangingThePlayers(int bestOf, string results, int wins0, int wins1, int draws)
    {
        var db = Content();
        var match = Make(db, bestOf, preparation: true);
        var feed = new SpectatorMatchFeed(Rules(bestOf, preparation: true), 0);
        var viewer = new Viewer(db, feed);
        var completed = new List<Battle>();
        for (int game = 0; game < results.Length; game++)
        {
            var battle = match.StartNextGame();
            var initial = battle.Clone();
            feed.StartGame(battle);
            Pump(feed, viewer);
            SameBattle(initial, battle);
            Assert.True(viewer.Client.HasMatch);
            Assert.Equal("三局两胜验收", viewer.Client.MatchName);
            Assert.Equal(bestOf, viewer.Client.BestOf);
            Assert.Equal(game + 1, viewer.Client.GameNumber);
            SameBattle(initial, viewer.Client.Battle);
            Assert.Equal(unchecked(uint.MaxValue + (uint)game), battle.Seed);
            Assert.Equal(6, viewer.Client.Battle.Sides[0].Team.Count);
            Assert.All(viewer.Client.Battle.Sides.SelectMany(side => side.Team), fighter =>
            {
                Assert.Equal(fighter.MaxHp, fighter.Hp);
                Assert.All(fighter.Skills, skill => Assert.Equal(skill.Max, skill.Pp));
            });
            Prepare(battle);
            battle.Random();
            battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Skill(0));
            var beforePump = battle.Clone();
            Pump(feed, viewer);
            SameBattle(beforePump, battle);
            SameBattle(battle, viewer.Client.Battle);
            End(battle, results[game] == 'd' ? -1 : results[game] - '0');
            var finished = battle.Clone();
            match.ReportResult(battle);
            feed.FinishGame();
            Pump(feed, viewer);
            SameBattle(finished, battle);
            SameBattle(battle, viewer.Client.Battle);
            Assert.Equal(match.Wins, viewer.Client.ReleasedWins);
            Assert.Equal(match.Draws, viewer.Client.MatchDraws);
            Assert.Equal(game + 1, viewer.Client.CompletedGames.Count);
            Assert.False(viewer.Client.IsMatchFinished);
            completed.Add(finished);
        }
        Assert.True(match.Over);
        feed.Finish();
        Pump(feed, viewer);
        Assert.True(viewer.Client.IsMatchFinished);
        Assert.Equal(new[] { wins0, wins1 }, viewer.Client.ReleasedWins);
        Assert.Equal(draws, viewer.Client.MatchDraws);
        Assert.Throws<NotSupportedException>(() => Assert.IsAssignableFrom<IList<SpectatorMatchGameResult>>(viewer.Client.CompletedGames).Clear());
        ulong currentHash = viewer.Client.Battle.StateHash();
        for (int index = 0; index < completed.Count; index++)
        {
            var result = viewer.Client.CompletedGames[index];
            Assert.Equal(index + 1, result.GameNumber);
            Assert.Equal(completed[index].Winner, result.Winner);
            SameBattle(completed[index], result.Battle);
            result.Battle.Active(0).Hp = 0;
            SameBattle(completed[index], result.Battle);
            var score = result.Wins;
            score[0] = 99;
            Assert.NotEqual(99, result.Wins[0]);
            Assert.Equal(currentHash, viewer.Client.Battle.StateHash());
        }
        var exposedWins = viewer.Client.ReleasedWins;
        exposedWins[0] = 99;
        Assert.Equal(wins0, viewer.Client.ReleasedWins[0]);
        int sent = viewer.Host.Sent.Count;
        feed.Finish();
        Pump(feed, viewer);
        Assert.Equal(sent, viewer.Host.Sent.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(int.MaxValue)]
    public void DelayAndScoreReleaseAreEnforcedAgainForEachGame(int delay)
    {
        var db = Content();
        var match = Make(db);
        var feed = new SpectatorMatchFeed(Rules(), delay);
        var viewer = new Viewer(db, feed);
        for (int game = 0; game < 2; game++)
        {
            var battle = match.StartNextGame();
            feed.StartGame(battle);
            Pump(feed, viewer);
            for (int turn = 1; turn <= 6; turn++)
            {
                battle.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
                Pump(feed, viewer);
                Assert.Equal(delay > turn ? 1 : turn - delay + 1, viewer.Client.Battle.Turn);
                Assert.Equal(new[] { game, 0 }, viewer.Client.ReleasedWins);
                Assert.Equal(game, viewer.Client.CompletedGames.Count);
            }
            End(battle, 0);
            match.ReportResult(battle);
            Pump(feed, viewer);
            Assert.Equal(new[] { game + 1, 0 }, match.Wins);
            Assert.Equal(new[] { game, 0 }, viewer.Client.ReleasedWins);
            Assert.NotEqual(Phase.Over, viewer.Client.Battle.Phase);
            int sent = viewer.Host.Sent.Count;
            Pump(feed, viewer);
            Assert.Equal(sent, viewer.Host.Sent.Count);
            feed.FinishGame();
            Pump(feed, viewer);
            Assert.Equal(new[] { game + 1, 0 }, viewer.Client.ReleasedWins);
            SameBattle(battle, viewer.Client.Battle);
        }
        feed.Finish();
        Pump(feed, viewer);
        Assert.True(viewer.Client.IsMatchFinished);
    }

    [Fact]
    public void LateViewerStartsAtTheCurrentGameAndOnlySeesPreviouslyReleasedScores()
    {
        var db = Content();
        var match = Make(db);
        var feed = new SpectatorMatchFeed(Rules(), 2);
        var early = new Viewer(db, feed);
        var first = match.StartNextGame();
        feed.StartGame(first);
        End(first, 0);
        match.ReportResult(first);
        feed.FinishGame();
        Pump(feed, early);
        var second = match.StartNextGame();
        feed.StartGame(second);
        for (int i = 0; i < 5; i++) second.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        Pump(feed, early);
        int earlyCount = early.Host.Sent.Count;
        var late = new Viewer(db, feed);
        feed.AddViewer(early.Host);
        Pump(feed, early, late);
        Assert.Equal(earlyCount, early.Host.Sent.Count);
        Assert.Equal(2, late.Client.GameNumber);
        Assert.Equal(new[] { 1, 0 }, late.Client.ReleasedWins);
        Assert.Equal(0, late.Client.MatchDraws);
        Assert.Equal(4, late.Client.Battle.Turn);
        SameBattle(early.Client.Battle, late.Client.Battle);
        End(second, 1);
        match.ReportResult(second);
        var beforeRelease = new Viewer(db, feed);
        Pump(feed, early, late, beforeRelease);
        Assert.Equal(new[] { 1, 0 }, beforeRelease.Client.ReleasedWins);
        Assert.NotEqual(Phase.Over, beforeRelease.Client.Battle.Phase);
        feed.FinishGame();
        Pump(feed, early, late, beforeRelease);
        Assert.Equal(new[] { 1, 1 }, late.Client.ReleasedWins);
        Assert.Equal(new[] { 1, 1 }, beforeRelease.Client.ReleasedWins);
        SameBattle(second, late.Client.Battle);
    }

    [Fact]
    public void LegacyViewerReceivesExactlyOneUnextendedGameWhileNewViewerContinues()
    {
        var db = Content();
        var match = Make(db);
        var feed = new SpectatorMatchFeed(Rules(), 0);
        var legacy = new Viewer(db, feed, match: false);
        var modern = new Viewer(db, feed);
        var first = match.StartNextGame();
        feed.StartGame(first);
        Pump(feed, legacy, modern);
        first.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        End(first, 0);
        match.ReportResult(first);
        feed.FinishGame();
        Pump(feed, legacy, modern);
        Assert.True(legacy.Client.IsFinished);
        Assert.False(legacy.Client.HasMatch);
        SameBattle(first, legacy.Client.Battle);
        Assert.All(legacy.Host.Sent, message => Assert.Contains(TypeOf(message), new[] { "spectator_start", "spectator_event", "spectator_end" }));
        Assert.Single(legacy.Host.Sent, message => TypeOf(message) == "spectator_start");
        Assert.Single(legacy.Host.Sent, message => TypeOf(message) == "spectator_end");
        for (int i = 0; i < legacy.Host.Sent.Count; i++)
        {
            using var frame = JsonDocument.Parse(legacy.Host.Sent[i]);
            Assert.Equal(i, frame.RootElement.GetProperty("seq").GetInt32());
            Assert.Equal(2, frame.RootElement.GetProperty("version").GetInt32());
        }
        using (var opening = JsonDocument.Parse(legacy.Host.Sent[0]))
        {
            Assert.Equal(2, opening.RootElement.GetProperty("version").GetInt32());
            Assert.Equal(0, opening.RootElement.GetProperty("seq").GetInt32());
            Assert.False(opening.RootElement.TryGetProperty("game", out _));
        }
        int legacyCount = legacy.Host.Sent.Count;
        var second = match.StartNextGame();
        feed.StartGame(second);
        End(second, 0);
        match.ReportResult(second);
        feed.FinishGame();
        feed.Finish();
        Pump(feed, legacy, modern);
        Assert.Equal(legacyCount, legacy.Host.Sent.Count);
        SameBattle(first, legacy.Client.Battle);
        SameBattle(second, modern.Client.Battle);
        Assert.True(modern.Client.IsMatchFinished);
    }

    [Fact]
    public void MatchAwareClientShowsAnUnextendedSingleGameButWaitsForEofToConfirmCompletion()
    {
        var db = Content();
        var match = Make(db);
        var battle = match.StartNextGame();
        var feed = new SpectatorFeed(battle, 0);
        LoopbackPair.Create(out var host, out var client);
        var viewer = new SpectatorClient(db, client, enableMatch: true);
        feed.AddViewer(host);
        feed.Pump();
        viewer.Poll();
        End(battle, 0);
        feed.Finish();
        viewer.Poll();
        Assert.Null(viewer.Error);
        Assert.False(viewer.IsFinished);
        Assert.False(viewer.HasMatch);
        SameBattle(battle, viewer.Battle);
    }

    internal sealed class RecordedMatch
    {
        public ContentDb Db;
        public string[] Messages;
        public List<Battle> Games = new List<Battle>();
    }

    internal static RecordedMatch Capture()
    {
        var db = Content();
        var result = new RecordedMatch { Db = db };
        var match = Make(db);
        var feed = new SpectatorMatchFeed(Rules(), 0);
        var viewer = new Viewer(db, feed);
        for (int game = 0; game < 2; game++)
        {
            var battle = match.StartNextGame();
            feed.StartGame(battle);
            Pump(feed, viewer);
            battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());
            End(battle, 0);
            match.ReportResult(battle);
            feed.FinishGame();
            Pump(feed, viewer);
            result.Games.Add(battle);
        }
        feed.Finish();
        Pump(feed, viewer);
        result.Messages = viewer.Host.Sent.ToArray();
        return result;
    }

    internal static string KindOf(string message)
    {
        using var parsed = JsonDocument.Parse(message);
        return parsed.RootElement.GetProperty("kind").GetString();
    }

    static int GameOf(string message)
    {
        using var parsed = JsonDocument.Parse(message);
        return parsed.RootElement.GetProperty("game").GetInt32();
    }

    internal static bool IsInner(string message, string type)
    {
        using var parsed = JsonDocument.Parse(message);
        return parsed.RootElement.GetProperty("kind").GetString() == "frame"
            && TypeOf(parsed.RootElement.GetProperty("json").GetString()) == type;
    }

    [Theory]
    [InlineData("score")]
    [InlineData("draws")]
    [InlineData("winner")]
    [InlineData("hash")]
    [InlineData("extra-field")]
    [InlineData("future")]
    [InlineData("duplicate-field")]
    [InlineData("result-before-base-end")]
    [InlineData("duplicate-result")]
    [InlineData("jump-game")]
    [InlineData("old-game-frame")]
    [InlineData("different-seed-opening")]
    [InlineData("early-match-end")]
    [InlineData("impossible-initial-score")]
    public void InvalidMatchFramesCannotPolluteTheLastConfirmedBattleOrScore(string defect)
    {
        var capture = Capture();
        int firstResult = Array.FindIndex(capture.Messages, message => KindOf(message) == "game_end");
        int secondStart = Array.FindIndex(capture.Messages, message => KindOf(message) == "game_start" && GameOf(message) == 2);
        int index = firstResult;
        string bad = capture.Messages[index];
        switch (defect)
        {
            case "score": bad = Edit(bad, obj => obj["wins"][0] = 2); break;
            case "draws": bad = Edit(bad, obj => obj["draws"] = 1); break;
            case "winner": bad = Edit(bad, obj => obj["winner"] = 1); break;
            case "hash": bad = Edit(bad, obj => obj["hash"] = "0000000000000000"); break;
            case "extra-field": bad = Edit(bad, obj => obj["secret"] = true); break;
            case "future": bad = Edit(bad, obj => obj["version"] = 2); break;
            case "duplicate-field": bad = bad.Substring(0, bad.Length - 1) + ",\"wins\":[1,0]}"; break;
            case "result-before-base-end":
                index = Array.FindIndex(capture.Messages, message => IsInner(message, "spectator_end")); break;
            case "duplicate-result": index = firstResult + 1; break;
            case "jump-game":
                index = secondStart;
                bad = Edit(capture.Messages[index], obj => obj["game"] = 3); break;
            case "old-game-frame":
                index = Array.FindIndex(capture.Messages, message => KindOf(message) == "frame" && GameOf(message) == 2);
                bad = Edit(capture.Messages[index], obj => obj["game"] = 1); break;
            case "different-seed-opening":
            {
                index = Array.FindIndex(capture.Messages, message => KindOf(message) == "frame" && GameOf(message) == 2);
                var record = capture.Games[1].Record;
                var other = new Battle(capture.Db, record.Teams, record.Names, record.Seed ^ 7u);
                var otherFeed = new SpectatorFeed(other, 0);
                LoopbackPair.Create(out var otherHost, out var unusedClient);
                var wire = new Wire(otherHost);
                otherFeed.AddViewer(wire);
                otherFeed.Pump();
                string validOpening = Assert.Single(wire.Sent);
                var independent = new SpectatorClient(capture.Db, unusedClient);
                independent.Poll();
                Assert.Null(independent.Error);
                SameBattle(other, independent.Battle);
                bad = Edit(capture.Messages[index], obj => obj["json"] = validOpening);
                break;
            }
            case "early-match-end":
                index = secondStart;
                bad = Edit(capture.Messages.Last(), obj => { obj["game"] = 1; obj["wins"][0] = 1; }); break;
            case "impossible-initial-score":
                index = 0;
                bad = Edit(capture.Messages[index], obj => obj["wins"][0] = 1); break;
        }
        LoopbackPair.Create(out var host, out var client);
        var viewer = new SpectatorClient(capture.Db, client, enableMatch: true);
        foreach (string message in capture.Messages.Take(index)) host.Send(message);
        viewer.Poll();
        Assert.Null(viewer.Error);
        string before = viewer.Battle?.Record.ToJson();
        ulong? hash = viewer.Battle?.StateHash();
        var battleBefore = viewer.Battle;
        var wins = viewer.ReleasedWins;
        int draws = viewer.MatchDraws, game = viewer.GameNumber, completed = viewer.CompletedGames.Count;
        host.Send(bad);

        viewer.Poll();

        Assert.NotNull(viewer.Error);
        Assert.Matches("[\u4e00-\u9fff]", viewer.Error);
        Assert.Same(battleBefore, viewer.Battle);
        Assert.Equal(before, viewer.Battle?.Record.ToJson());
        Assert.Equal(hash, viewer.Battle?.StateHash());
        Assert.Equal(wins, viewer.ReleasedWins);
        Assert.Equal(draws, viewer.MatchDraws);
        Assert.Equal(game, viewer.GameNumber);
        Assert.Equal(completed, viewer.CompletedGames.Count);
        Assert.False(viewer.IsMatchFinished);
        foreach (string message in capture.Messages.Skip(index)) host.Send(message);
        viewer.Poll();
        Assert.Equal(hash, viewer.Battle?.StateHash());
        Assert.Equal(wins, viewer.ReleasedWins);
    }

    [Fact]
    public void DelayedCapabilityJoinReplaysOnlyTheAlreadyReleasedPrefixAndKeepsStateConsistent()
    {
        var db = Content();
        var match = Make(db);
        var feed = new SpectatorMatchFeed(Rules(), 2);
        var viewer = new Viewer(db, feed, delayJoin: true);
        var battle = match.StartNextGame();
        feed.StartGame(battle);
        for (int i = 0; i < 5; i++) battle.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        Pump(feed, viewer);
        Assert.False(viewer.Client.HasMatch);
        Assert.Equal(4, viewer.Client.Battle.Turn);
        var before = viewer.Client.Battle.Clone();
        viewer.ClientWire.Release();

        Pump(feed, viewer);

        Assert.True(viewer.Client.HasMatch);
        Assert.Equal(1, viewer.Client.GameNumber);
        Assert.Equal(new[] { 0, 0 }, viewer.Client.ReleasedWins);
        SameBattle(before, viewer.Client.Battle);
        int count = viewer.Host.Sent.Count;
        viewer.ClientWire.Send("{\"type\":\"spectator_match\",\"version\":1,\"kind\":\"join\"}");
        Pump(feed, viewer);
        Assert.Equal(count, viewer.Host.Sent.Count);
    }

    [Fact]
    public void ChatLimitsRestartAtTheSameVisibleTurnOfANewGameWithoutTouchingPlayers()
    {
        var db = Content();
        var match = Make(db);
        var feed = new SpectatorMatchFeed(Rules(), 2, chatEnabled: true);
        var sender = new Viewer(db, feed, chat: true, name: "观众甲");
        var recipient = new Viewer(db, feed, chat: true, name: "观众乙");
        for (int game = 0; game < 2; game++)
        {
            var battle = match.StartNextGame();
            feed.StartGame(battle);
            Pump(feed, sender, recipient);
            Assert.Equal(1, sender.Client.Battle.Turn);
            var before = battle.Clone();
            int existing = recipient.Client.ReceivedChats.Count;
            for (int index = 0; index < 3; index++) Assert.True(sender.Client.SendQuickChat(index));
            Assert.False(sender.Client.SendQuickChat(3));
            Pump(feed, sender, recipient);
            Assert.Equal(existing + 3, recipient.Client.ReceivedChats.Count);
            Assert.All(recipient.Client.ReceivedChats.Skip(existing), chat => Assert.Equal(1, chat.Turn));
            SameBattle(before, battle);
            End(battle, 0);
            match.ReportResult(battle);
            feed.FinishGame();
            Pump(feed, sender, recipient);
        }
        feed.Finish();
        Pump(feed, sender, recipient);
        Assert.True(recipient.Client.IsMatchFinished);
    }

    [Fact]
    public void FailedViewerAndMalformedUploadsDoNotBlockAnotherViewerOrChangeTheBattle()
    {
        var db = Content();
        var match = Make(db);
        var feed = new SpectatorMatchFeed(Rules(), 0);
        var failed = new Viewer(db, feed);
        var healthy = new Viewer(db, feed);
        var battle = match.StartNextGame();
        feed.StartGame(battle);
        Pump(feed, failed, healthy);
        failed.Host.FailSend = true;
        foreach (string message in new[]
        {
            "不是JSON", "{\"type\":\"spectator_match\",\"version\":99,\"kind\":\"join\"}",
            "{\"type\":\"spectator_match\",\"version\":1,\"kind\":\"game_end\",\"wins\":[99,0]}",
            "{\"type\":\"spectator_match\",\"version\":1,\"kind\":\"join\",\"extra\":true}"
        }) healthy.ClientWire.Send(message);
        battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());
        var before = battle.Clone();
        Pump(feed, healthy);
        SameBattle(before, battle);
        SameBattle(battle, healthy.Client.Battle);
        Assert.Equal(new[] { 0, 0 }, healthy.Client.ReleasedWins);
        End(battle, 0);
        match.ReportResult(battle);
        feed.FinishGame();
        Pump(feed, healthy);
        Assert.Equal(new[] { 1, 0 }, healthy.Client.ReleasedWins);
    }

    [Fact]
    public void HostCannotSkipAnUnfinishedGameOrEndAMatchBeforeItsRulesAllow()
    {
        var db = Content();
        var match = Make(db);
        var feed = new SpectatorMatchFeed(Rules(), 0);
        var viewer = new Viewer(db, feed);
        var battle = match.StartNextGame();
        feed.StartGame(battle);
        Pump(feed, viewer);
        var before = battle.Clone();
        int sent = viewer.Host.Sent.Count;
        foreach (Action action in new Action[] { feed.FinishGame, feed.Finish, () => feed.StartGame(battle.Clone()) })
        {
            var error = Record.Exception(action);
            Assert.NotNull(error);
            Assert.Matches("[\u4e00-\u9fff]", error.Message);
        }
        Assert.Equal(sent, viewer.Host.Sent.Count);
        SameBattle(before, battle);
        End(battle, 0);
        match.ReportResult(battle);
        feed.FinishGame();
        Pump(feed, viewer);
        var tooEarly = Record.Exception(feed.Finish);
        Assert.NotNull(tooEarly);
        Assert.Matches("[\u4e00-\u9fff]", tooEarly.Message);
        Assert.Equal(new[] { 1, 0 }, viewer.Client.ReleasedWins);
        Assert.False(viewer.Client.IsMatchFinished);
    }

    [Fact]
    public void LateViewerUsesThePublishedSnapshotEvenIfTheCallerMutatesTheFinishedBattle()
    {
        var db = Content();
        var match = Make(db, bestOf: 1);
        var feed = new SpectatorMatchFeed(Rules(bestOf: 1), 2);
        var early = new Viewer(db, feed);
        var battle = match.StartNextGame();
        feed.StartGame(battle);
        battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());
        End(battle, 0);
        var confirmed = battle.Clone();
        match.ReportResult(battle);
        feed.FinishGame();
        Pump(feed, early);
        SameBattle(confirmed, early.Client.Battle);
        battle.Record.Events.Clear();
        battle.Active(0).Hp = 0;
        battle.Winner = 1;
        var late = new Viewer(db, feed);

        feed.Finish();
        Pump(feed, early, late);

        Assert.True(late.Client.IsMatchFinished);
        Assert.Equal(new[] { 1, 0 }, late.Client.ReleasedWins);
        Assert.Equal(0, Assert.Single(late.Client.CompletedGames).Winner);
        SameBattle(confirmed, late.Client.Battle);
        SameBattle(confirmed, late.Client.CompletedGames[0].Battle);
        SameBattle(confirmed, early.Client.Battle);
    }

    [Fact]
    public void AGameEndingByFaintingTheWholeTeamPublishesItsVerifiedResultAndMatchScore()
    {
        var db = Content();
        var match = Make(db, bestOf: 1);
        var feed = new SpectatorMatchFeed(Rules(bestOf: 1), 2);
        var viewer = new Viewer(db, feed);
        var battle = match.StartNextGame();
        feed.StartGame(battle);
        int turns = 0;
        while (battle.Phase != Phase.Over)
        {
            Assert.True(turns++ < 7);
            battle.ResolveTurn(BattleAction.Skill(1), BattleAction.Pass());
            if (battle.Phase == Phase.Replace) battle.Replace(1, battle.LegalFor(1).Switches.First());
            var before = battle.Clone();
            Pump(feed, viewer);
            SameBattle(before, battle);
            Assert.Equal(new[] { 0, 0 }, viewer.Client.ReleasedWins);
        }
        Assert.Equal(0, battle.Winner);
        Assert.All(battle.Sides[1].Team, fighter => Assert.True(fighter.Fainted && fighter.Hp == 0));
        Assert.DoesNotContain(battle.Record.Events, entry => entry.Type == BattleRecordEventType.Surrender);
        match.ReportResult(battle);
        feed.FinishGame();
        feed.Finish();
        Pump(feed, viewer);
        Assert.True(viewer.Client.IsMatchFinished);
        Assert.Equal(new[] { 1, 0 }, viewer.Client.ReleasedWins);
        SameBattle(battle, viewer.Client.Battle);
        SameBattle(battle, Assert.Single(viewer.Client.CompletedGames).Battle);
    }

    [Fact]
    public void InvalidDataAfterTheMatchEndingClearsCompletionFlagsButKeepsConfirmedSnapshots()
    {
        var capture = Capture();
        LoopbackPair.Create(out var host, out var client);
        var viewer = new SpectatorClient(capture.Db, client, enableMatch: true);
        foreach (string message in capture.Messages) host.Send(message);
        viewer.Poll();
        Assert.Null(viewer.Error);
        Assert.True(viewer.IsFinished);
        Assert.True(viewer.IsMatchFinished);
        var confirmed = viewer.Battle;
        string record = confirmed.Record.ToJson();
        ulong hash = confirmed.StateHash();
        var wins = viewer.ReleasedWins;
        var results = viewer.CompletedGames.ToArray();

        host.Send("{不是合法JSON");
        viewer.Poll();

        Assert.NotNull(viewer.Error);
        Assert.Matches("[\u4e00-\u9fff]", viewer.Error);
        Assert.False(viewer.IsFinished);
        Assert.False(viewer.IsMatchFinished);
        Assert.Same(confirmed, viewer.Battle);
        Assert.Equal(record, viewer.Battle.Record.ToJson());
        Assert.Equal(hash, viewer.Battle.StateHash());
        Assert.Equal(wins, viewer.ReleasedWins);
        Assert.Equal(2, viewer.CompletedGames.Count);
        for (int index = 0; index < results.Length; index++)
        {
            Assert.Same(results[index], viewer.CompletedGames[index]);
            SameBattle(capture.Games[index], viewer.CompletedGames[index].Battle);
        }
    }


}

// 仅真实 TCP 用例复用项目既有集合，Loopback 规则验收继续并行。
[Collection("TCP loopback")]
public class T137_SpectateMatchTcpTests
{
    static void Until(Func<bool> condition, Action poll)
    {
        var watch = Stopwatch.StartNew();
        while (!condition() && watch.ElapsedMilliseconds < 5000)
        {
            poll();
            if (!condition()) Thread.Sleep(1);
        }
        Assert.True(condition(), "等待本机观战 TCP 消息超时。");
    }

    [Theory]
    [InlineData("during-game")]
    [InlineData("between-games")]
    [InlineData("after-last-game")]
    [InlineData("after-match")]
    public void TcpEofRequiresTheWholeMatchEndingRatherThanOnlyASingleGameEnding(string point)
    {
        var capture = Capture();
        int count = point == "during-game" ? Array.FindIndex(capture.Messages, message => IsInner(message, "spectator_event"))
            : point == "between-games" ? Array.FindIndex(capture.Messages, message => KindOf(message) == "game_end") + 1
            : point == "after-last-game" ? capture.Messages.Length - 1 : capture.Messages.Length;
        using var listener = TcpTransport.Listen(0, out int port);
        using var client = TcpTransport.Connect("127.0.0.1", port, 5000);
        using var host = listener.Accept(5000);
        Assert.NotNull(host);
        var viewer = new SpectatorClient(capture.Db, client, enableMatch: true);
        foreach (string message in capture.Messages.Take(count)) host.Send(message);
        host.CompleteSending();
        Until(() => client.PeerClosed && client.ReceiveDrained, viewer.Poll);
        viewer.Poll();
        if (point == "after-match")
        {
            Assert.Null(viewer.Error);
            Assert.True(viewer.IsMatchFinished);
            Assert.Equal(new[] { 2, 0 }, viewer.ReleasedWins);
        }
        else
        {
            Assert.NotNull(viewer.Error);
            Assert.Contains("中断", viewer.Error);
            Assert.False(viewer.IsMatchFinished);
        }
    }

    [Fact]
    public void MatchAwareClientAcceptsNormalEofAfterAnOldSingleGameStream()
    {
        var db = Content();
        var battle = Make(db).StartNextGame();
        var feed = new SpectatorFeed(battle, 0);
        using var listener = TcpTransport.Listen(0, out int port);
        using var client = TcpTransport.Connect("127.0.0.1", port, 5000);
        using var host = listener.Accept(5000);
        var viewer = new SpectatorClient(db, client, enableMatch: true);
        feed.AddViewer(host);
        End(battle, 0);
        feed.Finish();
        host.CompleteSending();
        Until(() => client.PeerClosed && client.ReceiveDrained, viewer.Poll);
        viewer.Poll();
        Assert.Null(viewer.Error);
        Assert.True(viewer.IsFinished);
        Assert.False(viewer.HasMatch);
        SameBattle(battle, viewer.Battle);
    }

    [Fact]
    public void ClosingTcpUploadAfterChatRegistrationKeepsReceivingChatsAcrossGames()
    {
        var db = Content();
        var match = Make(db);
        var feed = new SpectatorMatchFeed(Rules(), 0, chatEnabled: true);
        using var listener = TcpTransport.Listen(0, out int port);
        using var client = TcpTransport.Connect("127.0.0.1", port, 5000);
        using var host = listener.Accept(5000);
        var readOnlyViewer = new SpectatorClient(db, client, enableChat: true, displayName: "半关闭观众", enableMatch: true);
        feed.AddViewer(host);
        var sender = new Viewer(db, feed, chat: true, name: "在线观众");
        Action pump = () =>
        {
            feed.Pump();
            readOnlyViewer.Poll();
            sender.Client.Poll();
            Assert.Null(readOnlyViewer.Error);
            Assert.Null(sender.Client.Error);
        };
        var first = match.StartNextGame();
        feed.StartGame(first);
        Until(() => readOnlyViewer.HasMatch && readOnlyViewer.Battle != null && sender.Client.Battle != null, pump);
        // 收到开场才会发出聊天登记；先让收件端登记被主机处理，避免首条聊天早到而不被转发。
        Until(() => sender.Host.Received >= sender.ClientWire.Sent.Count, pump);
        // 确认服务器实际接受过登记；仅客户端成功发送请求不足以证明具有聊天接收能力。
        Assert.True(readOnlyViewer.SendQuickChat(0));
        Until(() => sender.Client.ReceivedChats.Count == 1, pump);
        Assert.Equal("半关闭观众", sender.Client.ReceivedChats[0].SenderName);
        Assert.True(sender.Client.SendQuickChat(3));
        Until(() => readOnlyViewer.ReceivedChats.Count == 1, pump);
        client.CompleteSending();
        Until(() => host.PeerClosed && host.ReceiveDrained, pump);
        pump();
        End(first, 0);
        match.ReportResult(first);
        feed.FinishGame();
        Until(() => readOnlyViewer.CompletedGames.Count == 1, pump);
        var second = match.StartNextGame();
        feed.StartGame(second);
        Until(() => readOnlyViewer.GameNumber == 2 && readOnlyViewer.Battle != null
            && sender.Client.GameNumber == 2 && sender.Client.Battle != null, pump);
        Until(() => sender.Host.Received >= sender.ClientWire.Sent.Count, pump);
        var before = second.Clone();
        Assert.Equal(1, readOnlyViewer.Battle.Turn);
        for (int index = 0; index < 3; index++) Assert.True(sender.Client.SendQuickChat(index));
        Assert.False(sender.Client.SendQuickChat(4));

        Until(() => readOnlyViewer.ReceivedChats.Count == 4, pump);

        Assert.Equal(new[] { 0, 1, 2 }, readOnlyViewer.ReceivedChats.Skip(1).Select(chat => chat.Index));
        Assert.All(readOnlyViewer.ReceivedChats.Skip(1), chat =>
        {
            Assert.Equal("在线观众", chat.SenderName);
            Assert.Equal(1, chat.Turn);
        });
        SameBattle(before, second);
        End(second, 0);
        match.ReportResult(second);
        feed.FinishGame();
        feed.Finish();
        Until(() => readOnlyViewer.IsMatchFinished && sender.Client.IsMatchFinished, pump);
        Assert.Equal(new[] { 2, 0 }, readOnlyViewer.ReleasedWins);
        SameBattle(second, readOnlyViewer.Battle);
    }
}
