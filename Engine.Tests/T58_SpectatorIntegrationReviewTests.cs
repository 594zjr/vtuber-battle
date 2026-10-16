using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using StreamDuel.Core;
using Xunit;

public class T58_SpectatorIntegrationReviewTests
{
    sealed class Transport : INetTransport
    {
        public readonly List<string> Sent = new List<string>();
        public readonly Queue<string> Incoming = new Queue<string>();

        public void Send(string message)
        {
            Sent.Add(message);
            Incoming.Enqueue(message);
        }

        public bool TryReceive(out string message)
        {
            if (Incoming.Count == 0) { message = null; return false; }
            message = Incoming.Dequeue();
            return true;
        }
    }

    static Battle Make(ContentDb db, Phase phase = Phase.Choose, bool modifiers = false)
    {
        return new Battle(db, new IList<string>[]
        {
            new[] { "bell", "enka", "shiori", "lumi", "hane", "momo" },
            new[] { "kei", "yuan", "nyako", "buck", "kasumi", "bee" }
        }, new[] { "甲\n\"原名\"", "乙\\原名" }, new BattleOptions
        {
            Seed = 5802,
            BanOne = phase == Phase.Ban,
            LeadPick = phase == Phase.Ban || phase == Phase.Lead,
            Modifiers = modifiers ? new List<string> { "hp_x2", "half_pp", "hype_start" } : null
        });
    }

    static string[] Log(Battle battle)
    {
        return battle.Log.Select(entry => string.Join("|", entry.Turn, entry.Node, entry.Kind, entry.Side, entry.Text,
            string.Join(",", entry.SnapActive), string.Join(",", entry.SnapHp[0]), string.Join(",", entry.SnapHp[1]))).ToArray();
    }

    static void Same(Battle source, Battle viewer)
    {
        Assert.Equal(source.StateHash(), viewer.StateHash());
        Assert.Equal(source.Record.ToJson(), viewer.Record.ToJson());
        Assert.Equal(Log(source), Log(viewer));
        Assert.Equal(source.Clone().Random(), viewer.Clone().Random());
    }

    static string Edit(string message, Action<Dictionary<string, object>> edit)
    {
        var fields = JsonSerializer.Deserialize<Dictionary<string, object>>(message);
        edit(fields);
        return JsonSerializer.Serialize(fields);
    }

    static string Legacy(string message)
    {
        return Edit(message, fields =>
        {
            fields["version"] = 1;
            fields.Remove("recordVersion");
            fields.Remove("modifiers");
        });
    }

    [Fact]
    public void ModifiersPreparationAndLaterTerminationShareTheSameReplayAndRandomStream()
    {
        var db = BattleTests.LoadPack("original");
        var source = Make(db, Phase.Ban, true);
        var feed = new SpectatorFeed(source, 0);
        var transport = new Transport();
        var viewer = new SpectatorClient(db, transport);
        feed.AddViewer(transport);
        feed.Pump();
        viewer.Poll();
        Assert.Null(viewer.Error);
        Same(source, viewer.Battle);
        Assert.Equal(new[] { "half_pp", "hp_x2", "hype_start" }, viewer.Battle.Modifiers);
        using (var opening = JsonDocument.Parse(transport.Sent[0]))
        {
            Assert.Equal(2, opening.RootElement.GetProperty("version").GetInt32());
            Assert.Equal(2, opening.RootElement.GetProperty("recordVersion").GetInt32());
        }

        source.Random();
        source.OfferDraw(0);
        source.RespondDraw(1, false);
        source.ChooseBan(0, 5);
        source.Random();
        source.ChooseBan(1, 0);
        source.ChooseLead(1, 2);
        source.ChooseLead(0, 1);
        source.Random();
        source.ResolveTurn(BattleAction.Skill(0), BattleAction.Skill(0));
        feed.Pump();
        viewer.Poll();
        Assert.Null(viewer.Error);
        Same(source, viewer.Battle);
        Assert.Equal(BattleRecord.TerminationVersion, viewer.Battle.Record.Version);
        source.Surrender(0);
        var sourceBeforeFeed = source.Clone();
        feed.Finish();
        viewer.Poll();
        Assert.Null(viewer.Error);
        Assert.True(viewer.IsFinished);
        Same(source, viewer.Battle);
        Same(sourceBeforeFeed, source);
    }

    [Theory]
    [InlineData(Phase.Choose, "single")]
    [InlineData(Phase.Choose, "double")]
    [InlineData(Phase.Choose, "draw")]
    [InlineData(Phase.Ban, "single")]
    [InlineData(Phase.Ban, "double")]
    [InlineData(Phase.Ban, "draw")]
    [InlineData(Phase.Lead, "single")]
    [InlineData(Phase.Lead, "double")]
    [InlineData(Phase.Lead, "draw")]
    public void EveryTerminationPhaseKeepsTheEndingPrivateUntilFinishEvenAtZeroDelay(Phase phase, string ending)
    {
        var db = BattleTests.LoadPack("original");
        var source = Make(db, phase, true);
        var feed = new SpectatorFeed(source, 0);
        var transport = new Transport();
        var viewer = new SpectatorClient(db, transport);
        feed.AddViewer(transport);
        feed.Pump();
        viewer.Poll();
        source.Random();
        if (ending == "single") source.Surrender(1);
        else if (ending == "double") source.ResolveSurrenders(true, true);
        else
        {
            source.OfferDraw(0);
            source.RespondDraw(1, true);
        }
        feed.Pump();
        viewer.Poll();
        Assert.Null(viewer.Error);
        Assert.Equal(phase, viewer.Battle.Phase);
        Assert.Equal(-2, viewer.Battle.Winner);
        Assert.False(viewer.IsFinished);
        Assert.Equal(ending == "draw" ? 1 : 0, viewer.Battle.Record.Events.Count);

        feed.Finish();
        viewer.Poll();
        Assert.Null(viewer.Error);
        Assert.True(viewer.IsFinished);
        Assert.Equal(ending == "single" ? 0 : -1, viewer.Battle.Winner);
        Same(source, viewer.Battle);
    }

    [Fact]
    public void DelayedOfferAndResponseStayQueuedUntilTheirTurnBoundaryOrFinish()
    {
        var db = BattleTests.LoadPack("original");
        var source = Make(db);
        var feed = new SpectatorFeed(source, 2);
        var transport = new Transport();
        var viewer = new SpectatorClient(db, transport);
        feed.AddViewer(transport);
        source.OfferDraw(0);
        source.RespondDraw(1, false);
        feed.Pump();
        viewer.Poll();
        Assert.Empty(viewer.Battle.Record.Events);
        source.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        feed.Pump();
        viewer.Poll();
        Assert.Empty(viewer.Battle.Record.Events);
        source.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        feed.Pump();
        viewer.Poll();
        Assert.Null(viewer.Error);
        Assert.Equal(new[] { BattleRecordEventType.DrawOffer, BattleRecordEventType.DrawResponse },
            viewer.Battle.Record.Events.Select(entry => entry.Type));
        source.Surrender(0);
        feed.Pump();
        viewer.Poll();
        Assert.Equal(Phase.Choose, viewer.Battle.Phase);
        feed.Finish();
        viewer.Poll();
        Assert.Null(viewer.Error);
        Assert.True(viewer.IsFinished);
        Same(source, viewer.Battle);
    }

    [Fact]
    public void LegacyProtocolOneStillReplaysTheOriginalCompleteBasicStream()
    {
        var db = BattleTests.LoadPack("original");
        var source = Make(db);
        var feed = new SpectatorFeed(source, 0);
        var output = new Transport();
        feed.AddViewer(output);
        while (source.Phase != Phase.Over) source.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        feed.Finish();
        var input = new Transport();
        foreach (string message in output.Sent) input.Incoming.Enqueue(Legacy(message));
        var viewer = new SpectatorClient(db, input);
        viewer.Poll();
        Assert.Null(viewer.Error);
        Assert.True(viewer.IsFinished);
        Same(source, viewer.Battle);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("record_version")]
    [InlineData("unknown_modifier")]
    [InlineData("null_modifier")]
    [InlineData("duplicate_key")]
    public void InvalidExtendedOpeningNeverPublishesAPartialBattle(string problem)
    {
        var db = BattleTests.LoadPack("original");
        var feed = new SpectatorFeed(Make(db), 0);
        var output = new Transport();
        feed.AddViewer(output);
        feed.Pump();
        string message = output.Sent[0];
        if (problem == "duplicate_key") message = message.Insert(1, "\"recordVersion\":2,");
        else message = Edit(message, fields =>
        {
            if (problem == "version") fields["version"] = 3;
            else if (problem == "record_version") fields["recordVersion"] = BattleRecord.CurrentVersion + 1;
            else if (problem == "unknown_modifier") fields["modifiers"] = new[] { "unavailable_modifier" };
            else fields["modifiers"] = new object[] { null };
        });
        var input = new Transport();
        input.Incoming.Enqueue(message);
        var viewer = new SpectatorClient(db, input);
        viewer.Poll();
        Assert.NotNull(viewer.Error);
        Assert.Null(viewer.Battle);
        Assert.False(viewer.IsFinished);
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("accept_type")]
    [InlineData("side")]
    [InlineData("future_version")]
    [InlineData("version_change")]
    [InlineData("unknown_field")]
    [InlineData("q_turn")]
    [InlineData("without_offer")]
    public void RejectedTerminationFramesKeepTheEntireLastConfirmedSnapshot(string problem)
    {
        var db = BattleTests.LoadPack("original");
        var source = Make(db);
        var feed = new SpectatorFeed(source, 0);
        var output = new Transport();
        feed.AddViewer(output);
        source.OfferDraw(0);
        feed.Pump();
        source.RespondDraw(1, true);
        feed.Finish();
        string start = output.Sent[0], offer = output.Sent[1], response = output.Sent[2];
        var input = new Transport();
        input.Incoming.Enqueue(start);
        if (problem != "without_offer") input.Incoming.Enqueue(offer);
        var viewer = new SpectatorClient(db, input);
        viewer.Poll();
        Assert.Null(viewer.Error);
        var previous = viewer.Battle;
        var confirmed = previous.Clone();
        response = Edit(response, fields =>
        {
            if (problem == "hash") fields["hash"] = "0000000000000000";
            else if (problem == "accept_type") fields["accept"] = "true";
            else if (problem == "side") fields["side"] = -1;
            else if (problem == "future_version") fields["version"] = 3;
            else if (problem == "version_change") fields["version"] = 1;
            else if (problem == "unknown_field") fields["extra"] = true;
            else if (problem == "without_offer") fields["seq"] = 1;
            else
            {
                fields["event"] = "turn";
                fields.Remove("side");
                fields.Remove("accept");
                fields["a0"] = "Q";
                fields["a1"] = "P";
            }
        });
        input.Incoming.Enqueue(response);
        viewer.Poll();
        Assert.NotNull(viewer.Error);
        Assert.False(viewer.IsFinished);
        Assert.Same(previous, viewer.Battle);
        Same(confirmed, viewer.Battle);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DownlistedOriginalRosterIsRejectedBeforeFingerprintFallback(bool pack)
    {
        var sourceDb = BattleTests.LoadPack("original");
        var source = Make(sourceDb, Phase.Ban);
        var feed = new SpectatorFeed(source, 0);
        var output = new Transport();
        feed.AddViewer(output);
        feed.Pump();
        string rules = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Content", "StreamDuelRules.json"));
        string content = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Content", "Packs", "original.json"));
        var policy = PackPolicy.FromJson(pack ? "{\"disabledPacks\":[\"retired_original\"]}"
            : "{\"disabledStreamers\":[\"bell\"]}");
        var db = ContentDb.FromJson(rules, content, policy);
        string opening = pack ? Edit(output.Sent[0], fields => fields["packId"] = "retired_original") : output.Sent[0];
        var input = new Transport();
        input.Incoming.Enqueue(opening);
        var viewer = new SpectatorClient(db, input);
        viewer.Poll();
        Assert.Contains("下架", viewer.Error);
        Assert.Null(viewer.Battle);
    }

    [Fact]
    public void FeedRejectsFutureAndMislabelledTerminationRecordVersionsBeforePublishing()
    {
        var db = BattleTests.LoadPack("original");
        var future = Make(db);
        future.Record.Version = BattleRecord.CurrentVersion + 1;
        Assert.Throws<FormatException>(() => new SpectatorFeed(future, 0));
        var source = Make(db);
        var feed = new SpectatorFeed(source, 0);
        var transport = new Transport();
        feed.AddViewer(transport);
        source.Surrender(0);
        source.Record.Version = 2;
        Assert.Throws<FormatException>(() => feed.Finish());
        Assert.Empty(transport.Sent);
    }
}
