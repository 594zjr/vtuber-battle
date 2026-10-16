using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using StreamDuel.Core;
using Xunit;

public class T109_ColorblindSessionTests
{
    static readonly string[] FirstTeam = { "bell", "enka", "shiori", "lumi", "hane", "momo" };
    static readonly string[] SecondTeam = { "kei", "yuan", "nyako", "buck", "kasumi", "bee" };
    static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions { IncludeFields = true };

    sealed class Transport : INetTransport
    {
        public readonly Queue<string> Incoming = new Queue<string>();
        public readonly List<string> Sent = new List<string>();
        public void Send(string message) { Sent.Add(message); }
        public bool TryReceive(out string message)
        {
            message = Incoming.Count == 0 ? null : Incoming.Dequeue();
            return message != null;
        }
    }

    static string Json<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);

    static ContentDb Fixture()
    {
        var db = BattleTests.LoadPack("original");
        db.Types["song"].Symbol = "◇";
        db.Types["song"].ColorAlt = "#010203";
        db.Streamer("bell").Type2 = "talk";
        return db;
    }

    static Battle MakeBattle(ContentDb db)
    {
        return new Battle(db, new IList<string>[] { FirstTeam, SecondTeam }, new[] { "甲方", "乙方" }, 109);
    }

    static HotseatSession MakeHotseat(ContentDb db, bool lead = false, bool ban = false)
    {
        return new HotseatSession(db, FirstTeam, SecondTeam, "甲方", "乙方",
            new BattleOptions { Seed = 109, LeadPick = lead, BanOne = ban });
    }

    static void AssertColors(ContentDb db, BattleViewModel view, bool alternate)
    {
        foreach (var side in view.Sides)
        {
            foreach (var fighter in side.Team)
            {
                var source = db.Streamer(fighter.Id);
                var expectedIds = new[] { source.Type, source.Type2 }.Where(id => id != null).ToArray();
                Assert.Equal(expectedIds, fighter.TrackViews.Select(track => track.Id));
                foreach (var track in fighter.TrackViews)
                {
                    var definition = db.Types[track.Id];
                    Assert.Equal(definition.Name, track.Name);
                    Assert.Equal(definition.Symbol, track.Symbol);
                    Assert.Equal(alternate ? definition.ColorAlt : definition.Color, track.Color);
                }
            }
        }
        foreach (var skill in view.Skills)
        {
            var definition = db.Types[skill.TrackId];
            Assert.Equal(definition.Name, skill.Track);
            Assert.Equal(definition.Symbol, skill.TrackSymbol);
            Assert.Equal(alternate ? definition.ColorAlt : definition.Color, skill.TrackColor);
        }
    }

    [Fact]
    public void HotseatForwardsLocalModeWithoutChangingTurnOwnershipChoicesOrRandomStream()
    {
        var db = Fixture();
        var session = MakeHotseat(db);
        var control = MakeHotseat(db);
        var settings = new GameSettings { ColorblindMode = true };
        string record = session.Record.ToJson();
        string log = Json(session.Log);
        var alternate = session.ViewFor(0, settings);
        AssertColors(db, alternate, true);
        string snapshot = Json(alternate);
        settings.ColorblindMode = false;
        Assert.Equal(snapshot, Json(alternate));
        AssertColors(db, session.ViewFor(0), false);
        Assert.Equal(Json(session.ViewFor(1)), Json(session.ViewFor(1, null)));
        Assert.Equal(Json(session.ViewFor(0)), Json(session.ViewFor(0, settings)));
        Assert.Equal(0, session.WhoseTurn);
        Assert.False(session.NeedsHandover);
        Assert.Equal(record, session.Record.ToJson());
        Assert.Equal(log, Json(session.Log));

        session.Submit(BattleAction.Skill(0));
        control.Submit(BattleAction.Skill(0));
        Assert.True(session.NeedsHandover);
        Assert.Equal(1, session.WhoseTurn);
        AssertColors(db, session.ViewFor(1, new GameSettings { ColorblindMode = true }), true);
        Assert.True(session.NeedsHandover);
        Assert.Equal(Json(control.ViewFor(1)), Json(session.ViewFor(1)));
        session.ConfirmHandover();
        control.ConfirmHandover();
        session.Submit(BattleAction.Pass());
        control.Submit(BattleAction.Pass());
        Assert.Equal(control.Record.ToJson(), session.Record.ToJson());
        Assert.Equal(Json(control.Log), Json(session.Log));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void HotseatKeepsOpeningPhaseActionFilteringWithAlternateColors(bool lead, bool ban)
    {
        var db = Fixture();
        var session = MakeHotseat(db, lead, ban);
        var view = session.ViewFor(0, new GameSettings { ColorblindMode = true });
        AssertColors(db, view, true);
        Assert.Equal(ban ? Phase.Ban : Phase.Lead, view.Phase);
        Assert.Empty(view.Skills);
        Assert.Empty(view.SwitchTargets);
        Assert.False(view.Struggle);
        Assert.False(view.FreePp);
        Assert.Null(view.ControlledReason);
        Assert.Throws<ArgumentOutOfRangeException>(() => session.ViewFor(-1, new GameSettings()));
    }

    [Fact]
    public void NetworkViewsForwardModeForEitherViewerWithoutChangingConfirmedStateOrMessages()
    {
        var db = Fixture();
        var hostTransport = new Transport();
        var guestTransport = new Transport();
        var host = new NetSession(db, hostTransport, true, FirstTeam, "房主", 109);
        var guest = new NetSession(db, guestTransport, false, SecondTeam, "客机", null);
        var settings = new GameSettings { ColorblindMode = true };
        Assert.Throws<InvalidOperationException>(() => host.ViewFor(0, settings));
        hostTransport.Incoming.Enqueue(Assert.Single(guestTransport.Sent));
        guestTransport.Incoming.Enqueue(Assert.Single(hostTransport.Sent));
        host.Poll();
        guest.Poll();
        Assert.Equal(NetState.Choose, host.State);
        Assert.Equal(NetState.Choose, guest.State);
        int hostMessages = hostTransport.Sent.Count;
        int guestMessages = guestTransport.Sent.Count;
        foreach (var session in new[] { host, guest })
        {
            string record = session.Battle.Record.ToJson();
            ulong hash = session.Battle.StateHash();
            string log = Json(session.Battle.Log);
            for (int side = 0; side < 2; side++)
            {
                var alternate = session.ViewFor(side, settings);
                AssertColors(db, alternate, true);
                AssertColors(db, session.ViewFor(side), false);
                Assert.Equal(Json(session.ViewFor(side)), Json(session.ViewFor(side, null)));
                Assert.Equal(Json(session.ViewFor(side)), Json(session.ViewFor(side, new GameSettings())));
                alternate.Sides[0].Team[0].TrackViews[0].Color = "changed snapshot";
                AssertColors(db, session.ViewFor(side, settings), true);
            }
            Assert.Throws<ArgumentOutOfRangeException>(() => session.ViewFor(2, settings));
            Assert.Equal(record, session.Battle.Record.ToJson());
            Assert.Equal(hash, session.Battle.StateHash());
            Assert.Equal(log, Json(session.Battle.Log));
        }
        Assert.Equal(hostMessages, hostTransport.Sent.Count);
        Assert.Equal(guestMessages, guestTransport.Sent.Count);
    }

    [Fact]
    public void ReplayCursorViewsKeepCurrentStepEventsAndRecordWhileSwitchingModes()
    {
        var db = Fixture();
        var played = MakeBattle(db);
        played.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        played.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        string original = played.Record.ToJson();
        var cursor = new ReplayCursor(db, played.Record, 1);
        Assert.True(cursor.Next());
        string events = Json(cursor.EventsOfCurrentStep);
        string record = cursor.Current.Record.ToJson();
        ulong hash = cursor.Current.StateHash();
        for (int side = 0; side < 2; side++)
        {
            AssertColors(db, cursor.View(side, new GameSettings { ColorblindMode = true }), true);
            AssertColors(db, cursor.View(side), false);
            Assert.Equal(Json(cursor.View(side)), Json(cursor.View(side, null)));
            Assert.Equal(Json(cursor.View(side)), Json(cursor.View(side, new GameSettings())));
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => cursor.View(-1, new GameSettings()));
        Assert.Equal(1, cursor.Turn);
        Assert.Equal(events, Json(cursor.EventsOfCurrentStep));
        Assert.Equal(record, cursor.Current.Record.ToJson());
        Assert.Equal(hash, cursor.Current.StateHash());
        Assert.Equal(original, played.Record.ToJson());
    }

    static ContentDb PackWithDisplay(string pack, string symbol = "◇", string colorAlt = "#010203")
    {
        string content = Path.Combine(AppContext.BaseDirectory, "Content");
        var rules = JsonNode.Parse(File.ReadAllText(Path.Combine(content, "StreamDuelRules.json")));
        rules["types"]["song"]["symbol"] = symbol;
        rules["types"]["song"]["colorAlt"] = colorAlt;
        return ContentDb.FromJson(rules.ToJsonString(), File.ReadAllText(Path.Combine(content, "Packs", pack + ".json")));
    }

    [Fact]
    public void MixedContentPreservesTrackDisplayAndOwnsIndependentTypeDefinitions()
    {
        var original = PackWithDisplay("original");
        var fan = PackWithDisplay("fan_asoul_vr");
        var mixed = ContentDb.Merge(original, fan);
        var reverse = ContentDb.Merge(fan, original);
        Assert.Equal("◇", mixed.Types["song"].Symbol);
        Assert.Equal("#010203", mixed.Types["song"].ColorAlt);
        Assert.Equal(mixed.Types["song"].Symbol, reverse.Types["song"].Symbol);
        Assert.Equal(mixed.Types["song"].ColorAlt, reverse.Types["song"].ColorAlt);
        Assert.NotSame(original.Types["song"], mixed.Types["song"]);
        Assert.NotSame(fan.Types["song"], mixed.Types["song"]);
        original.Types["song"].Symbol = "◆";
        original.Types["song"].ColorAlt = "#030201";
        Assert.Equal("◇", mixed.Types["song"].Symbol);
        Assert.Equal("#010203", mixed.Types["song"].ColorAlt);
        mixed.Types["song"].Symbol = "★";
        mixed.Types["song"].ColorAlt = "#040506";
        Assert.Equal("◇", fan.Types["song"].Symbol);
        Assert.Equal("#010203", fan.Types["song"].ColorAlt);
        Assert.Equal("◆", original.Types["song"].Symbol);
        Assert.Equal("#030201", original.Types["song"].ColorAlt);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void MixedContentRejectsDifferentOriginalOrCurrentDisplayEvenWhenFingerprintsMatch(bool baseline, bool symbol)
    {
        var first = PackWithDisplay("original");
        var second = baseline
            ? PackWithDisplay("fan_asoul_vr", symbol ? "◆" : "◇", symbol ? "#010203" : "#030201")
            : PackWithDisplay("fan_asoul_vr");
        string originalHash = first.ContentHash();
        string fanHash = second.ContentHash();
        if (baseline)
        {
            second.Types["song"].Symbol = first.Types["song"].Symbol;
            second.Types["song"].ColorAlt = first.Types["song"].ColorAlt;
        }
        else if (symbol) second.Types["song"].Symbol = "◆";
        else second.Types["song"].ColorAlt = "#030201";
        Assert.Equal(originalHash, first.ContentHash());
        Assert.Equal(fanHash, second.ContentHash());
        Assert.Contains("规则", Assert.Throws<KeyError>(() => ContentDb.Merge(first, second)).Message);
        Assert.Contains("规则", Assert.Throws<KeyError>(() => ContentDb.Merge(second, first)).Message);
    }

    [Fact]
    public void MergingLegacyRulesKeepsAlternateColorFallbackLiveAndIndependent()
    {
        string content = Path.Combine(AppContext.BaseDirectory, "Content");
        var rules = JsonNode.Parse(File.ReadAllText(Path.Combine(content, "StreamDuelRules.json")));
        foreach (var track in rules["types"].AsObject()) track.Value.AsObject().Remove("colorAlt");
        string legacyRules = rules.ToJsonString();
        var first = ContentDb.FromJson(legacyRules, File.ReadAllText(Path.Combine(content, "Packs", "original.json")));
        var second = ContentDb.FromJson(legacyRules, File.ReadAllText(Path.Combine(content, "Packs", "fan_asoul_vr.json")));
        string originalColor = first.Types["song"].Color;
        var merged = ContentDb.Merge(first, second);
        Assert.Equal(originalColor, merged.Types["song"].ColorAlt);

        merged.Types["song"].Color = "#010203";

        Assert.Equal("#010203", merged.Types["song"].ColorAlt);
        Assert.Equal(originalColor, first.Types["song"].Color);
        Assert.Equal(originalColor, first.Types["song"].ColorAlt);
        Assert.Equal(originalColor, second.Types["song"].Color);
        Assert.Equal(originalColor, second.Types["song"].ColorAlt);
        first.Types["song"].Color = "#040506";
        Assert.Equal("#040506", first.Types["song"].ColorAlt);
        Assert.Equal("#010203", merged.Types["song"].ColorAlt);
    }
}
