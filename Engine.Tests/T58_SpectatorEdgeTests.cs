using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using StreamDuel.Core;
using Xunit;

public class T58_SpectatorEdgeTests
{
    sealed class RecordingTransport : INetTransport
    {
        public readonly List<string> Sent = new List<string>();
        public readonly Queue<string> Incoming = new Queue<string>();
        public bool FailSend, FailReceive;

        public void Send(string message)
        {
            if (FailSend) throw new IOException("测试观众已断开");
            Sent.Add(message);
            Incoming.Enqueue(message);
        }

        public bool TryReceive(out string message)
        {
            if (FailReceive) throw new IOException("测试接收失败");
            if (Incoming.Count == 0) { message = null; return false; }
            message = Incoming.Dequeue();
            return true;
        }

        public void Inject(IEnumerable<string> messages)
        {
            foreach (string message in messages) Incoming.Enqueue(message);
        }
    }

    sealed class RecordedTurns
    {
        public ContentDb Db;
        public string[] Start, First, Second;
    }

    static ContentDb NeutralDb()
    {
        var db = BattleTests.LoadPack("original");
        db.Personas["spectator_neutral"] = new PersonaDef
        {
            Id = "spectator_neutral", Name = "观战测试人设", Code = "T_LOWHP_POWER"
        };
        foreach (var streamer in db.Streamers) streamer.Persona = "spectator_neutral";
        return db;
    }

    static Battle Make(ContentDb db, bool preparation = false)
    {
        return new Battle(db, new IList<string>[]
        {
            new[] { "bell", "enka", "shiori", "lumi", "hane", "momo" },
            new[] { "kei", "yuan", "nyako", "buck", "kasumi", "bee" }
        }, new[] { "甲方\n\"测试\"", "乙方\\测试" },
            new BattleOptions { Seed = 58, BanOne = preparation, LeadPick = preparation });
    }

    static string[] FullLog(Battle battle)
    {
        return battle.Log.Select(entry => string.Join("|", entry.Turn, entry.Node, entry.Kind, entry.Side, entry.Text,
            string.Join(",", entry.SnapActive), string.Join(",", entry.SnapHp[0]), string.Join(",", entry.SnapHp[1]))).ToArray();
    }

    static void EqualBattle(Battle expected, Battle actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.StateHash(), actual.StateHash());
        Assert.Equal(expected.Record.ToJson(), actual.Record.ToJson());
        Assert.Equal(FullLog(expected), FullLog(actual));
        Assert.Equal(BattleSummary.From(expected).ToText(), BattleSummary.From(actual).ToText());
    }

    static RecordedTurns CaptureTwoTurns()
    {
        var db = NeutralDb();
        var battle = Make(db);
        var feed = new SpectatorFeed(battle, 0);
        var transport = new RecordingTransport();
        feed.AddViewer(transport);
        feed.Pump();
        var start = transport.Sent.ToArray();
        int previous = transport.Sent.Count;
        battle.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        feed.Pump();
        var first = transport.Sent.Skip(previous).ToArray();
        previous = transport.Sent.Count;
        battle.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        feed.Pump();
        return new RecordedTurns { Db = db, Start = start, First = first, Second = transport.Sent.Skip(previous).ToArray() };
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(int.MaxValue)]
    public void DelayReleasesOnlyTheEligiblePrefixAndRepeatedPumpsRevealNothingExtra(int delay)
    {
        var db = NeutralDb();
        var battle = Make(db);
        var feed = new SpectatorFeed(battle, delay);
        var transport = new RecordingTransport();
        feed.AddViewer(transport);
        var viewer = new SpectatorClient(db, transport);
        Assert.Null(viewer.Battle);
        feed.Pump();
        viewer.Poll();
        for (int completed = 1; completed <= 6; completed++)
        {
            battle.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
            feed.Pump();
            viewer.Poll();
            Assert.Null(viewer.Error);
            Assert.NotNull(viewer.Battle);
            int visible = delay > completed ? 0 : completed - delay;
            Assert.Equal(visible, viewer.Battle.Record.Events.Count);
            Assert.Equal(visible + 1, viewer.Battle.Turn);
            Assert.False(viewer.IsFinished);
            int sent = transport.Sent.Count;
            feed.Pump();
            feed.Pump();
            viewer.Poll();
            Assert.Equal(sent, transport.Sent.Count);
            if (delay == 0) EqualBattle(battle, viewer.Battle);
        }
        Assert.All(transport.Sent, message =>
        {
            Assert.DoesNotContain('\n', message);
            Assert.DoesNotContain('\r', message);
        });
    }

    [Fact]
    public void NegativeDelayIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SpectatorFeed(Make(NeutralDb()), -1));
    }

    [Fact]
    public void AJoiningViewerCatchesUpOnlyToTheDelayedPrefixWithoutDuplicatingOtherViewers()
    {
        var db = NeutralDb();
        var battle = Make(db);
        var feed = new SpectatorFeed(battle, 2);
        var firstTransport = new RecordingTransport();
        feed.AddViewer(firstTransport);
        var first = new SpectatorClient(db, firstTransport);
        for (int i = 0; i < 5; i++)
        {
            battle.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
            feed.Pump();
            first.Poll();
        }
        Assert.Equal(3, first.Battle.Record.Events.Count);
        int firstCount = firstTransport.Sent.Count;
        var lateTransport = new RecordingTransport();
        var late = new SpectatorClient(db, lateTransport);

        feed.AddViewer(lateTransport);
        feed.AddViewer(firstTransport);
        feed.Pump();
        first.Poll();
        late.Poll();

        Assert.Null(first.Error);
        Assert.Null(late.Error);
        Assert.Equal(firstCount, firstTransport.Sent.Count);
        EqualBattle(first.Battle, late.Battle);
        battle.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        feed.Pump();
        first.Poll();
        late.Poll();
        EqualBattle(first.Battle, late.Battle);
        Assert.Equal(4, late.Battle.Record.Events.Count);
    }

    [Fact]
    public void FinishRejectsRunningBattlesThenFlushesACompletedBattleExactlyOnce()
    {
        var db = NeutralDb();
        var battle = Make(db);
        var feed = new SpectatorFeed(battle, int.MaxValue);
        var transport = new RecordingTransport();
        var viewer = new SpectatorClient(db, transport);
        feed.AddViewer(transport);
        feed.Pump();
        viewer.Poll();
        int initial = transport.Sent.Count;

        Assert.Throws<InvalidOperationException>(() => feed.Finish());
        Assert.Equal(initial, transport.Sent.Count);
        Assert.False(viewer.IsFinished);
        while (battle.Phase != Phase.Over) battle.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        feed.Pump();
        viewer.Poll();
        Assert.Empty(viewer.Battle.Record.Events);
        Assert.False(viewer.IsFinished);

        feed.Finish();
        viewer.Poll();

        Assert.Null(viewer.Error);
        Assert.True(viewer.IsFinished);
        EqualBattle(battle, viewer.Battle);
        int sent = transport.Sent.Count;
        feed.Finish();
        feed.Pump();
        viewer.Poll();
        Assert.Equal(sent, transport.Sent.Count);
        Assert.Null(viewer.Error);
    }

    [Fact]
    public void BanLeadReplacementAndAiRandomDrawsRebuildTheCompleteBattle()
    {
        var db = BattleTests.LoadPack("original");
        var battle = Make(db, true);
        var feed = new SpectatorFeed(battle, 2);
        var transport = new RecordingTransport();
        var viewer = new SpectatorClient(db, transport);
        feed.AddViewer(transport);
        int steps = 0;
        while (battle.Phase != Phase.Over && steps++ < 500)
        {
            for (int i = 0; i < steps % 3; i++) battle.Random();
            if (battle.Phase == Phase.Ban)
            {
                int side = battle.HasChosenBan(1) ? 0 : 1;
                battle.ChooseBan(side, Ai.ChooseBan(battle, side));
            }
            else if (battle.Phase == Phase.Lead)
            {
                int side = battle.HasChosenLead(0) ? 1 : 0;
                battle.ChooseLead(side, Ai.ChooseLead(battle, side));
            }
            else if (battle.Phase == Phase.Choose)
                battle.ResolveTurn(Ai.Choose(battle, 0, AiLevel.Normal), Ai.Choose(battle, 1, AiLevel.Easy));
            else
                for (int side = 1; side >= 0; side--)
                    if (battle.Need[side]) battle.Replace(side, Ai.ChooseReplacement(battle, side));
            feed.Pump();
            viewer.Poll();
            Assert.Null(viewer.Error);
            if (viewer.Battle != null && battle.Phase != Phase.Over)
                Assert.True(viewer.Battle.Turn <= Math.Max(1, battle.Turn - 2));
        }
        Assert.Equal(Phase.Over, battle.Phase);
        Assert.Contains(battle.Record.Events, item => item.Type == BattleRecordEventType.Ban);
        Assert.Contains(battle.Record.Events, item => item.Type == BattleRecordEventType.Lead);
        Assert.Contains(battle.Record.Events, item => item.Type == BattleRecordEventType.Replace);

        feed.Finish();
        viewer.Poll();

        Assert.Null(viewer.Error);
        Assert.True(viewer.IsFinished);
        EqualBattle(battle, viewer.Battle);
        Assert.Equal(battle.Random(), viewer.Battle.Random());
    }

    [Fact]
    public void CreatingAndPumpingTheFeedDoesNotChangeTheLiveRandomStreamHistoryOrRecord()
    {
        var db = NeutralDb();
        var battle = Make(db);
        battle.Random();
        battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());
        var reference = battle.Clone();
        var feed = new SpectatorFeed(battle, 0);
        feed.AddViewer(new RecordingTransport());
        feed.Pump();
        feed.AddViewer(new RecordingTransport());
        feed.Pump();

        EqualBattle(reference, battle);
        Assert.Equal(reference.Random(), battle.Random());
    }

    [Theory]
    [InlineData("pack")]
    [InlineData("fingerprint")]
    public void MismatchedContentIsRejectedBeforeCreatingASpectatorBattle(string mismatch)
    {
        var captured = CaptureTwoTurns();
        var otherDb = NeutralDb();
        if (mismatch == "pack") otherDb.PackId = "different_pack";
        else otherDb.Streamers[0].Stats["hp"]++;
        var transport = new RecordingTransport();
        transport.Inject(captured.Start);
        var viewer = new SpectatorClient(otherDb, transport);

        viewer.Poll();

        Assert.False(string.IsNullOrWhiteSpace(viewer.Error));
        Assert.Null(viewer.Battle);
        Assert.False(viewer.IsFinished);
    }

    [Theory]
    [InlineData("json")]
    [InlineData("duplicate")]
    [InlineData("hash")]
    [InlineData("rng")]
    [InlineData("wire")]
    public void ARejectedFrameCannotChangePreviouslyConfirmedBattleState(string corruption)
    {
        var captured = CaptureTwoTurns();
        var transport = new RecordingTransport();
        transport.Inject(captured.Start.Concat(captured.First));
        var viewer = new SpectatorClient(captured.Db, transport);
        viewer.Poll();
        Assert.Null(viewer.Error);
        var confirmed = viewer.Battle.Clone();
        string frame = Assert.Single(captured.Second);
        var fields = (Dictionary<string, object>)MiniJson.Parse(frame);
        if (corruption == "json") frame = "{损坏";
        else if (corruption == "duplicate") frame = Assert.Single(captured.First);
        else if (corruption == "hash")
        {
            string previous = (string)fields["hash"];
            frame = frame.Replace("\"" + previous + "\"", "\"" + (previous == new string('0', 16) ? new string('1', 16) : new string('0', 16)) + "\"");
        }
        else if (corruption == "rng")
        {
            string previous = ((double)fields["rng"]).ToString("0", System.Globalization.CultureInfo.InvariantCulture);
            frame = frame.Replace("\"rng\":" + previous, "\"rng\":4294967296");
        }
        else frame = frame.Replace("\"a0\":\"P\"", "\"a0\":\"S9\"");
        Assert.NotEqual(Assert.Single(captured.Second), frame);
        transport.Incoming.Enqueue(frame);

        viewer.Poll();

        Assert.False(string.IsNullOrWhiteSpace(viewer.Error));
        Assert.False(viewer.IsFinished);
        EqualBattle(confirmed, viewer.Battle);
        transport.Inject(captured.Second);
        viewer.Poll();
        EqualBattle(confirmed, viewer.Battle);
    }

    [Fact]
    public void AnEventArrivingAheadOfItsPredecessorIsRejectedWithoutAdvancingTheBattle()
    {
        var captured = CaptureTwoTurns();
        var transport = new RecordingTransport();
        transport.Inject(captured.Start);
        var viewer = new SpectatorClient(captured.Db, transport);
        viewer.Poll();
        var confirmed = viewer.Battle.Clone();
        transport.Inject(captured.Second);

        viewer.Poll();

        Assert.False(string.IsNullOrWhiteSpace(viewer.Error));
        EqualBattle(confirmed, viewer.Battle);
    }

    [Fact]
    public void OneViewersSendFailureDoesNotStopAnotherViewer()
    {
        var db = NeutralDb();
        var battle = Make(db);
        var feed = new SpectatorFeed(battle, 0);
        var failing = new RecordingTransport { FailSend = true };
        var healthy = new RecordingTransport();
        var viewer = new SpectatorClient(db, healthy);
        feed.AddViewer(failing);
        feed.AddViewer(healthy);

        feed.Pump();
        viewer.Poll();
        battle.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        feed.Pump();
        viewer.Poll();

        Assert.Null(viewer.Error);
        EqualBattle(battle, viewer.Battle);
        Assert.Empty(failing.Sent);
    }

    [Fact]
    public void ReceiveFailureSetsErrorWithoutChangingTheConfirmedState()
    {
        var captured = CaptureTwoTurns();
        var transport = new RecordingTransport();
        transport.Inject(captured.Start.Concat(captured.First));
        var viewer = new SpectatorClient(captured.Db, transport);
        viewer.Poll();
        var confirmed = viewer.Battle.Clone();
        transport.FailReceive = true;

        viewer.Poll();

        Assert.False(string.IsNullOrWhiteSpace(viewer.Error));
        EqualBattle(confirmed, viewer.Battle);
    }

    [Fact]
    public void ZeroDelayStillHoldsTheFinalTurnUntilFinishWhenTheLiveTurnDoesNotAdvance()
    {
        var db = NeutralDb();
        var battle = Make(db);
        var feed = new SpectatorFeed(battle, 0);
        var transport = new RecordingTransport();
        var viewer = new SpectatorClient(db, transport);
        feed.AddViewer(transport);
        while (battle.Phase != Phase.Over)
        {
            battle.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
            feed.Pump();
            viewer.Poll();
        }

        Assert.Equal(Battle.TurnCap, battle.Turn);
        Assert.Equal(Battle.TurnCap, viewer.Battle.Turn);
        Assert.Equal(Phase.Choose, viewer.Battle.Phase);
        Assert.Equal(battle.Record.Events.Count - 1, viewer.Battle.Record.Events.Count);
        Assert.False(viewer.IsFinished);

        feed.Finish();
        viewer.Poll();

        Assert.Null(viewer.Error);
        Assert.True(viewer.IsFinished);
        EqualBattle(battle, viewer.Battle);
    }

    [Fact]
    public void AViewerJoiningAfterFinishReceivesOneCompleteStream()
    {
        var db = NeutralDb();
        var battle = Make(db);
        var feed = new SpectatorFeed(battle, int.MaxValue);
        while (battle.Phase != Phase.Over) battle.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        feed.Finish();
        var transport = new RecordingTransport();
        var viewer = new SpectatorClient(db, transport);

        feed.AddViewer(transport);
        feed.Pump();
        viewer.Poll();

        Assert.Null(viewer.Error);
        Assert.True(viewer.IsFinished);
        EqualBattle(battle, viewer.Battle);
        int sent = transport.Sent.Count;
        feed.AddViewer(transport);
        feed.Finish();
        feed.Pump();
        viewer.Poll();
        Assert.Equal(sent, transport.Sent.Count);
        Assert.Null(viewer.Error);
    }

    [Theory]
    [InlineData("struggle")]
    [InlineData("controlled")]
    [InlineData("invalid_index")]
    public void NonWireActionsAreCanonicalizedToPassWithoutChangingTheirResolvedResult(string mode)
    {
        var db = NeutralDb();
        if (mode == "struggle")
        {
            foreach (var skill in db.Skills.Values) skill.Pp = 0;
        }
        else if (mode == "controlled")
        {
            db.Skills["spectator_control"] = new SkillDef
            {
                Id = "spectator_control", Name = "观战控制测试", Type = "tech", Cat = "status", Pp = 10, Prio = 9,
                Effects = { new EffectDef { At = "post", Code = "STATUS", Target = "foe", Status = "disconnect" } }
            };
            db.Streamer("kei").Skills[0] = "spectator_control";
        }
        var battle = Make(db);
        var feed = new SpectatorFeed(battle, 0);
        var transport = new RecordingTransport();
        var viewer = new SpectatorClient(db, transport);
        feed.AddViewer(transport);
        if (mode == "controlled")
        {
            battle.ResolveTurn(BattleAction.Pass(), BattleAction.Skill(0));
            Assert.NotNull(battle.ControlOf(battle.Active(0)));
        }
        var action = mode == "struggle" ? new BattleAction { Type = ActionType.Struggle }
            : mode == "controlled" ? new BattleAction { Type = ActionType.PassControlled }
            : BattleAction.Skill(99);
        Assert.Throws<FormatException>(() => action.ToWire());

        battle.ResolveTurn(action, BattleAction.Pass());
        feed.Pump();
        viewer.Poll();

        Assert.Null(viewer.Error);
        Assert.Equal(battle.StateHash(), viewer.Battle.StateHash());
        Assert.Equal(FullLog(battle), FullLog(viewer.Battle));
        Assert.Equal(BattleSummary.From(battle).ToText(), BattleSummary.From(viewer.Battle).ToText());
        Assert.Equal(battle.Record.Events.Count, viewer.Battle.Record.Events.Count);
        Assert.Equal(ActionType.Pass, viewer.Battle.Record.Events.Last().A0.Type);
        Assert.Equal(action.Type, battle.Record.Events.Last().A0.Type);
    }
}
