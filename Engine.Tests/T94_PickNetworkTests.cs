using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StreamDuel.Core;
using Xunit;

public class T94_PickNetworkTests
{
    static readonly string[] HostTeam = { "bell", "enka", "shiori", "lumi", "hane", "momo" };
    static readonly string[] GuestTeam = { "kei", "yuan", "nyako", "buck", "kasumi", "bee" };
    const string Nonce = "000102030405060708090a0b0c0d0e0f";

    sealed class Transport : INetTransport
    {
        public readonly Queue<string> Incoming = new Queue<string>();
        public readonly List<string> Sent = new List<string>();
        public readonly HashSet<string> HoldTypes = new HashSet<string>();
        public Transport Peer;
        public Func<string, string> Rewrite;

        public void Send(string message)
        {
            if (Rewrite != null) message = Rewrite(message);
            Sent.Add(message);
            if (!HoldTypes.Contains(Text(message, "type"))) Peer.Incoming.Enqueue(message);
        }

        public bool TryReceive(out string message)
        {
            if (Incoming.Count == 0) { message = null; return false; }
            message = Incoming.Dequeue();
            return true;
        }

        public string[] Messages(string type) { return Sent.Where(item => Text(item, "type") == type).ToArray(); }
    }

    sealed class Pair
    {
        public readonly ContentDb Db = BattleTests.LoadPack("original");
        public readonly Transport A = new Transport(), B = new Transport();
        public readonly NetSession Host, Guest;

        public Pair(int count = 3, bool ban = false, int guestCount = 0, bool guestBan = false,
            double? turn = null, double? bank = null, bool handshake = true,
            Func<string, string> hostRewrite = null, Func<string, string> guestRewrite = null)
        {
            A.Peer = B; B.Peer = A;
            A.Rewrite = hostRewrite; B.Rewrite = guestRewrite;
            Host = new NetSession(Db, A, true, HostTeam, "主机", 94, turn, bank, count, ban);
            Guest = new NetSession(Db, B, false, GuestTeam, "客机", 999, null, null, guestCount, guestBan);
            if (handshake) Pump();
        }

        public void Pump()
        {
            for (int i = 0; i < 8; i++) { Host.Poll(); Guest.Poll(); }
        }

        public void CompleteBans(int hostChoice = 1, int guestChoice = 4)
        {
            Host.SubmitBan(hostChoice);
            Guest.SubmitBan(guestChoice);
            Pump();
        }
    }

    static string Text(string message, string field)
    {
        using var json = JsonDocument.Parse(message);
        return json.RootElement.GetProperty(field).GetString();
    }

    static string EditHello(string message, Action<Dictionary<string, object>> edit)
    {
        if (Text(message, "type") != "hello") return message;
        var fields = JsonSerializer.Deserialize<Dictionary<string, object>>(message);
        edit(fields);
        return JsonSerializer.Serialize(fields);
    }

    static string Digest(string action)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(action + "|" + Nonce))).ToLowerInvariant();
    }

    [Theory]
    [InlineData(3, false)]
    [InlineData(4, false)]
    [InlineData(3, true)]
    [InlineData(4, true)]
    [InlineData(0, true)]
    public void OpeningSelectionsAndFullBattleStaySynchronizedAndReplayable(int count, bool ban)
    {
        var pair = new Pair(count, ban);
        Assert.Equal(ban ? NetState.Ban : NetState.Pick, pair.Host.State);
        for (int step = 0; step < Battle.TurnCap * 6 && (pair.Host.State != NetState.Over || pair.Guest.State != NetState.Over); step++)
        {
            foreach (var session in new[] { pair.Host, pair.Guest })
            {
                Assert.NotEqual(NetState.Error, session.State);
                Assert.NotEqual(NetState.Desync, session.State);
                if (session.State == NetState.Ban) session.SubmitBan(session.MySide == 0 ? 1 : 4);
                else if (session.State == NetState.Pick)
                    session.SubmitPick(session.Battle.AvailablePicks(session.MySide).Reverse().Take(count).ToArray());
                else if (session.State == NetState.Choose) session.Submit(Ai.Choose(session.Battle, session.MySide, AiLevel.Normal));
                else if (session.State == NetState.Replace)
                    session.SubmitReplace(Ai.ChooseReplacement(session.Battle, session.MySide, AiLevel.Normal));
            }
            pair.Pump();
        }
        Assert.Equal(NetState.Over, pair.Host.State);
        Assert.Equal(NetState.Over, pair.Guest.State);
        Assert.Equal(pair.Host.Battle.StateHash(), pair.Guest.Battle.StateHash());
        Assert.Equal(pair.Host.Battle.Record.ToJson(), pair.Guest.Battle.Record.ToJson());
        Assert.Equal(pair.Host.Battle.StateHash(), Replay.Run(pair.Db, pair.Host.Battle.Record).StateHash());
        Assert.All(pair.Host.Battle.Sides, side => Assert.Equal(count == 0 ? 5 : count, side.Team.Count));
        Assert.Equal(ban ? 2 : 0, pair.Host.Battle.Record.Events.Count(item => item.Type == BattleRecordEventType.Ban));
        Assert.Equal(count == 0 ? 0 : 2, pair.Host.Battle.Record.Events.Count(item => item.Type == BattleRecordEventType.Pick));
    }

    [Theory]
    [InlineData(3, false, 4, true)]
    [InlineData(4, true, 3, false)]
    [InlineData(0, false, 4, true)]
    [InlineData(0, true, 0, false)]
    public void HostAloneChoosesOpeningRules(int count, bool ban, int guestCount, bool guestBan)
    {
        var pair = new Pair(count, ban, guestCount, guestBan);
        foreach (var session in new[] { pair.Host, pair.Guest })
        {
            Assert.Equal(count, session.Battle.PickCount);
            Assert.Equal(ban, session.Battle.Record.BanOne);
            Assert.Equal(94u, session.Battle.Seed);
            Assert.Equal(ban ? NetState.Ban : count > 0 ? NetState.Pick : NetState.Choose, session.State);
        }
        using var host = JsonDocument.Parse(Assert.Single(pair.A.Messages("hello")));
        using var guest = JsonDocument.Parse(Assert.Single(pair.B.Messages("hello")));
        Assert.Equal(3, host.RootElement.GetProperty("version").GetInt32());
        Assert.Equal(1, guest.RootElement.GetProperty("pickVersion").GetInt32());
        Assert.False(guest.RootElement.TryGetProperty("pickCount", out _));
        Assert.False(guest.RootElement.TryGetProperty("banOne", out _));
        Assert.Equal(count != 0 || ban, host.RootElement.TryGetProperty("pickCount", out _));
    }

    [Fact]
    public void OrdinaryVersionThreeDoesNotRequirePickCapability()
    {
        Func<string, string> legacy = message => EditHello(message, fields => fields.Remove("pickVersion"));
        var pair = new Pair(0, hostRewrite: legacy, guestRewrite: legacy);
        Assert.Equal(NetState.Choose, pair.Host.State);
        Assert.Equal(NetState.Choose, pair.Guest.State);
        Assert.Equal(pair.Host.Battle.StateHash(), pair.Guest.Battle.StateHash());
    }

    [Theory]
    [InlineData(true, 3, false)]
    [InlineData(false, 4, false)]
    [InlineData(false, 0, true)]
    public void EnabledOpeningRejectsPeerWithoutCapability(bool stripHost, int count, bool ban)
    {
        Func<string, string> legacy = message => EditHello(message, fields => fields.Remove("pickVersion"));
        var pair = new Pair(count, ban, hostRewrite: stripHost ? legacy : null, guestRewrite: stripHost ? null : legacy);
        Assert.Equal(NetState.Error, pair.Host.State);
        Assert.Equal(NetState.Error, pair.Guest.State);
        Assert.Contains("pickVersion", stripHost ? pair.Guest.Error : pair.Host.Error);
        Assert.Null(stripHost ? pair.Guest.Battle : pair.Host.Battle);
    }

    [Fact]
    public void GuestCannotDeclareOpeningRulesInHello()
    {
        var pair = new Pair(3, guestRewrite: message => EditHello(message, fields =>
        {
            fields["pickCount"] = 4;
            fields["banOne"] = true;
        }));
        Assert.Equal(NetState.Error, pair.Host.State);
        Assert.Contains("主机决定", pair.Host.Error);
        Assert.Null(pair.Host.Battle);
    }

    [Theory]
    [InlineData("count")]
    [InlineData("ban")]
    [InlineData("missing")]
    public void InvalidHostOpeningFieldsAreRejected(string field)
    {
        var pair = new Pair(3, hostRewrite: message => EditHello(message, values =>
        {
            if (field == "count") values["pickCount"] = 2;
            else if (field == "ban") values["banOne"] = "true";
            else values.Remove("banOne");
        }));
        Assert.Equal(NetState.Error, pair.Guest.State);
        Assert.Null(pair.Guest.Battle);
        Assert.Matches("[\u4e00-\u9fff]", pair.Guest.Error);
    }

    [Fact]
    public void PickRemainsSecretAndDoesNotTouchBattleUntilBothCommitAndReveal()
    {
        var pair = new Pair();
        ulong initial = pair.Host.Battle.StateHash();
        var choice = new[] { 5, 3, 1 };
        pair.Host.SubmitPick(choice);
        choice[0] = 0;
        pair.Guest.Poll();
        Assert.True(pair.Host.HasSubmitted);
        Assert.False(pair.Guest.HasSubmitted);
        Assert.Equal(NetState.Waiting, pair.Host.State);
        Assert.Equal(NetState.Pick, pair.Guest.State);
        Assert.Empty(pair.A.Messages("reveal"));
        Assert.Empty(pair.B.Messages("reveal"));
        using (var commit = JsonDocument.Parse(Assert.Single(pair.A.Messages("commit"))))
        {
            Assert.Equal("pick", commit.RootElement.GetProperty("kind").GetString());
            Assert.False(commit.RootElement.TryGetProperty("action", out _));
            Assert.False(commit.RootElement.TryGetProperty("nonce", out _));
        }
        foreach (var session in new[] { pair.Host, pair.Guest })
        {
            Assert.Equal(initial, session.Battle.StateHash());
            Assert.Empty(session.Battle.Record.Events);
            Assert.Empty(session.Battle.Log);
            Assert.Empty(session.Battle.Events);
            Assert.False(session.Battle.HasChosenPick(0));
            Assert.False(session.Battle.HasChosenPick(1));
            Assert.All(session.Battle.Sides, side => Assert.Equal(6, side.Team.Count));
        }
        pair.Guest.SubmitPick(new[] { 0, 2, 4 });
        pair.Pump();
        Assert.Equal(NetState.Choose, pair.Host.State);
        Assert.Equal(NetState.Choose, pair.Guest.State);
        Assert.False(pair.Host.HasSubmitted);
        Assert.Equal("K5,3,1", Text(Assert.Single(pair.A.Messages("reveal")), "action"));
        Assert.Equal(new[] { HostTeam[5], HostTeam[3], HostTeam[1] }, pair.Host.Battle.Sides[0].Team.Select(item => item.Def.Id));
        Assert.Single(pair.A.Messages("hash"));
        Assert.Single(pair.B.Messages("hash"));
        Assert.Equal(pair.Host.Battle.StateHash(), pair.Guest.Battle.StateHash());
    }

    [Fact]
    public void BanAlsoUsesSecretCommitRevealBeforeOpeningPick()
    {
        var pair = new Pair(4, true);
        pair.Host.SubmitBan(2);
        pair.Guest.Poll();
        Assert.Empty(pair.A.Messages("reveal"));
        Assert.False(pair.Host.Battle.HasChosenBan(0));
        Assert.False(pair.Guest.Battle.HasChosenBan(0));
        Assert.Empty(pair.Host.Battle.Record.Events);
        pair.Guest.SubmitBan(5);
        pair.Pump();
        Assert.Equal(NetState.Pick, pair.Host.State);
        Assert.Equal(NetState.Pick, pair.Guest.State);
        Assert.Equal("B2", Text(Assert.Single(pair.A.Messages("reveal")), "action"));
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, pair.Host.Battle.AvailablePicks(0));
        Assert.Equal(new[] { 0, 1, 3, 4, 5 }, pair.Guest.Battle.AvailablePicks(1));
        Assert.Single(pair.A.Messages("hash"));
        Assert.Equal(pair.Host.Battle.Record.ToJson(), pair.Guest.Battle.Record.ToJson());
    }

    [Fact]
    public void InvalidLocalPicksAndBannedSlotsNeverSendACommit()
    {
        var pair = new Pair(3, true);
        Assert.Throws<InvalidOperationException>(() => pair.Host.SubmitBan(-1));
        Assert.Throws<InvalidOperationException>(() => pair.Host.SubmitBan(6));
        Assert.Throws<InvalidOperationException>(() => pair.Host.SubmitPick(new[] { 0, 1, 2 }));
        Assert.Empty(pair.A.Messages("commit"));
        pair.CompleteBans();
        int count = pair.A.Sent.Count;
        foreach (var invalid in new int[][] { null, new[] { 0, 1 }, new[] { 0, 1, 2, 3 }, new[] { 0, 0, 1 },
            new[] { -1, 0, 1 }, new[] { 0, 1, 6 }, new[] { 0, 1, 4 } })
            Assert.Throws<InvalidOperationException>(() => pair.Host.SubmitPick(invalid));
        Assert.Equal(count, pair.A.Sent.Count);
        Assert.False(pair.Host.HasSubmitted);
        pair.Host.SubmitPick(new[] { 0, 2, 5 });
        Assert.Throws<InvalidOperationException>(() => pair.Host.SubmitPick(new[] { 1, 2, 3 }));
    }

    [Theory]
    [InlineData("K0,0,1")]
    [InlineData("K0,1,6")]
    [InlineData("K0,1")]
    [InlineData("K00,1,2")]
    [InlineData("K0, 1,2")]
    [InlineData("W1")]
    public void MatchingCommitmentDoesNotMakeInvalidPickEncodingLegal(string wire)
    {
        var pair = new Pair(guestRewrite: message =>
        {
            string type = Text(message, "type");
            if (type != "commit" && type != "reveal") return message;
            var fields = JsonSerializer.Deserialize<Dictionary<string, object>>(message);
            if (type == "commit") fields["digest"] = Digest(wire);
            else { fields["action"] = wire; fields["nonce"] = Nonce; }
            return JsonSerializer.Serialize(fields);
        });
        pair.Host.SubmitPick(new[] { 0, 1, 2 });
        pair.Guest.SubmitPick(new[] { 3, 4, 5 });
        pair.Pump();
        Assert.Equal(NetState.Error, pair.Host.State);
        Assert.Contains("选人", pair.Host.Error);
        Assert.Empty(pair.Host.Battle.Record.Events);
        Assert.All(pair.Host.Battle.Sides, side => Assert.Equal(6, side.Team.Count));
    }

    [Fact]
    public void AlteringACommittedPickIsRejectedBeforeBattleChanges()
    {
        var pair = new Pair(guestRewrite: message =>
        {
            if (Text(message, "type") != "reveal") return message;
            var fields = JsonSerializer.Deserialize<Dictionary<string, object>>(message);
            fields["action"] = "K2,1,0";
            return JsonSerializer.Serialize(fields);
        });
        pair.Host.SubmitPick(new[] { 0, 1, 2 });
        pair.Guest.SubmitPick(new[] { 3, 4, 5 });
        pair.Pump();
        Assert.Equal(NetState.Error, pair.Host.State);
        Assert.Contains("承诺", pair.Host.Error);
        Assert.Empty(pair.Host.Battle.Record.Events);
    }

    [Fact]
    public void CorrectDigestCannotRevealABannedSlotOrPartiallyApplyEitherPick()
    {
        var pair = new Pair(3, true);
        pair.CompleteBans();
        ulong initial = pair.Host.Battle.StateHash();
        pair.B.Rewrite = message =>
        {
            string type = Text(message, "type");
            if (type != "commit" && type != "reveal") return message;
            var fields = JsonSerializer.Deserialize<Dictionary<string, object>>(message);
            if (type == "commit") fields["digest"] = Digest("K0,1,2");
            else { fields["action"] = "K0,1,2"; fields["nonce"] = Nonce; }
            return JsonSerializer.Serialize(fields);
        };
        pair.Host.SubmitPick(new[] { 0, 2, 5 });
        pair.Guest.SubmitPick(new[] { 0, 2, 3 });
        pair.Pump();
        Assert.Equal(NetState.Error, pair.Host.State);
        Assert.Contains("被禁用", pair.Host.Error);
        Assert.Equal(initial, pair.Host.Battle.StateHash());
        Assert.All(pair.Host.Battle.Record.Events, item => Assert.Equal(BattleRecordEventType.Ban, item.Type));
        Assert.False(pair.Host.Battle.HasChosenPick(0));
        Assert.False(pair.Host.Battle.HasChosenPick(1));
    }

    [Theory]
    [InlineData("early")]
    [InlineData("wrong-kind")]
    [InlineData("duplicate")]
    public void EarlyRevealWrongKindAndDuplicateCommitAreRejected(string invalid)
    {
        var pair = new Pair();
        if (invalid == "early") pair.A.Incoming.Enqueue(JsonSerializer.Serialize(new
        {
            type = "reveal", turn = 1, kind = "pick", action = "K0,1,2", nonce = Nonce
        }));
        else if (invalid == "wrong-kind") pair.A.Incoming.Enqueue(JsonSerializer.Serialize(new
        {
            type = "commit", turn = 1, kind = "action", digest = Digest("K0,1,2")
        }));
        else
        {
            pair.Guest.SubmitPick(new[] { 0, 1, 2 });
            pair.A.Incoming.Enqueue(Assert.Single(pair.B.Messages("commit")));
        }
        pair.Host.Poll();
        Assert.Equal(NetState.Error, pair.Host.State);
        Assert.Empty(pair.Host.Battle.Record.Events);
        Assert.Matches("[\u4e00-\u9fff]", pair.Host.Error);
    }

    [Fact]
    public void BanHashMustBeAcknowledgedBeforePickingAndLocalRandomDrawsDoNotDesync()
    {
        var pair = new Pair(3, true);
        pair.A.HoldTypes.Add("hash"); pair.B.HoldTypes.Add("hash");
        pair.Host.Battle.Random(); pair.Host.Battle.Random(); pair.Guest.Battle.Random();
        pair.CompleteBans();
        Assert.Equal(NetState.Waiting, pair.Host.State);
        Assert.Equal(NetState.Waiting, pair.Guest.State);
        Assert.Throws<InvalidOperationException>(() => pair.Host.SubmitPick(new[] { 0, 2, 5 }));
        pair.A.HoldTypes.Clear(); pair.B.HoldTypes.Clear();
        pair.A.Incoming.Enqueue(Assert.Single(pair.B.Messages("hash")));
        pair.B.Incoming.Enqueue(Assert.Single(pair.A.Messages("hash")));
        pair.Pump();
        Assert.Equal(NetState.Pick, pair.Host.State);
        pair.Host.Battle.Random();
        pair.Host.SubmitPick(new[] { 5, 2, 0 });
        pair.Guest.SubmitPick(new[] { 4, 3, 0 });
        pair.Pump();
        Assert.Equal(NetState.Choose, pair.Host.State);
        Assert.Equal(pair.Host.Battle.StateHash(), pair.Guest.Battle.StateHash());
        Assert.Equal(pair.Host.Battle.Record.ToJson(), pair.Guest.Battle.Record.ToJson());
        Assert.Equal(pair.Host.Battle.StateHash(), Replay.Run(pair.Db, pair.Host.Battle.Record).StateHash());
    }

    [Fact]
    public void OpeningChoicesDoNotSpendClockBudgetButRevealWaitStillTimesOut()
    {
        var pair = new Pair(3, true, turn: 2, bank: 7);
        pair.Host.Tick(1000); pair.Guest.Tick(1000);
        Assert.Equal(NetState.Ban, pair.Host.State);
        Assert.Equal(7, pair.Host.Clock.BankLeft(0));
        pair.CompleteBans();
        pair.Host.Tick(1000); pair.Guest.Tick(1000);
        Assert.Equal(NetState.Pick, pair.Host.State);
        Assert.Equal(7, pair.Guest.Clock.BankLeft(1));
        pair.B.HoldTypes.Add("reveal"); pair.B.HoldTypes.Add("hash");
        pair.Guest.SubmitPick(new[] { 0, 2, 3 });
        pair.Host.Poll();
        pair.Host.Tick(1000);
        Assert.Equal(NetState.Pick, pair.Host.State);
        long revision = pair.Host.TimerRevision;
        pair.Host.SubmitPick(new[] { 0, 1, 2 });
        Assert.True(pair.Host.TimerRevision > revision);
        pair.Pump();
        pair.Host.Tick(9);
        Assert.Equal(NetState.Waiting, pair.Host.State);
        pair.Host.Tick(1);
        Assert.Equal(NetState.Error, pair.Host.State);
        Assert.Contains("公开", pair.Host.Error);
        Assert.Equal(7, pair.Host.Clock.BankLeft(0));
    }

    [Fact]
    public void PickHashBlocksActionsAndUsesHashGraceWithoutSpendingBank()
    {
        var pair = new Pair(3, turn: 2, bank: 7);
        pair.A.HoldTypes.Add("hash"); pair.B.HoldTypes.Add("hash");
        pair.Host.SubmitPick(new[] { 0, 1, 2 });
        pair.Guest.SubmitPick(new[] { 3, 4, 5 });
        pair.Pump();
        Assert.Equal(Phase.Choose, pair.Host.Battle.Phase);
        Assert.Equal(NetState.Waiting, pair.Host.State);
        Assert.Throws<InvalidOperationException>(() => pair.Host.Submit(BattleAction.Pass()));
        Assert.Single(pair.A.Messages("hash"));
        pair.Host.Tick(9);
        Assert.Equal(NetState.Waiting, pair.Host.State);
        pair.Host.Tick(1);
        Assert.Equal(NetState.Error, pair.Host.State);
        Assert.Contains("哈希", pair.Host.Error);
        Assert.Equal(7, pair.Host.Clock.BankLeft(0));
        Assert.DoesNotContain(pair.Host.Battle.Record.Events, item => item.Type == BattleRecordEventType.Turn);
    }

    [Fact]
    public void NormalClockStartsFreshAfterOpeningAndInvalidLocalCountErrors()
    {
        var pair = new Pair(3, turn: 2, bank: 7);
        pair.Host.Tick(1000); pair.Guest.Tick(1000);
        pair.Host.SubmitPick(new[] { 0, 1, 2 }); pair.Guest.SubmitPick(new[] { 3, 4, 5 });
        pair.Pump();
        Assert.Equal(2, pair.Host.Clock.TurnLeft(0));
        Assert.Equal(7, pair.Host.Clock.BankLeft(0));
        pair.Host.Tick(3);
        Assert.Equal(6, pair.Host.Clock.BankLeft(0));
        var invalid = new Pair(2, handshake: false);
        Assert.Equal(NetState.Error, invalid.Host.State);
        Assert.Contains("选人数", invalid.Host.Error);
        Assert.Empty(invalid.A.Messages("hello"));
    }
}
