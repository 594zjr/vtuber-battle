using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using StreamDuel.Core;
using Xunit;

internal static class NetSeedTestData
{
    public static byte[] Host { get { return Enumerable.Range(0, 32).Select(value => (byte)value).ToArray(); } }
    public static byte[] Guest { get { return Enumerable.Range(32, 32).Select(value => (byte)value).ToArray(); } }
    public static uint Seed { get { return Combine(Host, Guest); } }

    public static uint Combine(byte[] host, byte[] guest)
    {
        using var sha = SHA256.Create();
        byte[] hash = sha.ComputeHash(host.Concat(guest).ToArray());
        return (uint)hash[0] | (uint)hash[1] << 8 | (uint)hash[2] << 16 | (uint)hash[3] << 24;
    }

    public static string Hex(byte[] value) { return Convert.ToHexString(value).ToLowerInvariant(); }

    public static string Commit(byte[] value)
    {
        using var sha = SHA256.Create();
        return Hex(sha.ComputeHash(value));
    }
}

public class T87_FairSeedTests
{
    static readonly string[] HostTeam = { "bell", "enka", "shiori", "lumi", "hane", "momo" };
    static readonly string[] GuestTeam = { "kei", "yuan", "nyako", "buck", "kasumi", "bee" };

    sealed class Transport : INetTransport
    {
        readonly Queue<string> incoming = new Queue<string>();
        readonly Queue<string> held = new Queue<string>();
        public Transport Peer;
        public Func<string, string> Rewrite;
        public string FailOnType;
        public readonly HashSet<string> HoldTypes = new HashSet<string>();
        public readonly HashSet<string> DuplicateTypes = new HashSet<string>();
        public readonly List<string> Sent = new List<string>();

        public void Send(string message)
        {
            if (Field(message, "type") == FailOnType) throw new InvalidOperationException("模拟传输发送失败。");
            if (Rewrite != null) message = Rewrite(message);
            Sent.Add(message);
            string type = Field(message, "type");
            if (HoldTypes.Contains(type)) held.Enqueue(message);
            else Peer.incoming.Enqueue(message);
            if (DuplicateTypes.Contains(type)) Peer.incoming.Enqueue(message);
        }

        public bool TryReceive(out string message)
        {
            if (incoming.Count == 0) { message = null; return false; }
            message = incoming.Dequeue();
            return true;
        }

        public void Inject(string message) { incoming.Enqueue(message); }
        public void ReleaseAll() { while (held.Count > 0) Peer.incoming.Enqueue(held.Dequeue()); }
        public string[] Messages(string type) { return Sent.Where(message => Field(message, "type") == type).ToArray(); }
    }

    sealed class Pair
    {
        public readonly Transport A = new Transport(), B = new Transport();
        public readonly ContentDb HostDb = BattleTests.LoadPack("original"), GuestDb = BattleTests.LoadPack("original");
        public readonly NetSession Host, Guest;

        public Pair(byte[] hostBytes = null, byte[] guestBytes = null, bool randomDefaults = false,
            Func<string, string> hostRewrite = null, Func<string, string> guestRewrite = null,
            uint? oldHostSeed = 19, uint? oldGuestSeed = 99, bool timer = false, string hostSendFailure = null)
        {
            A.Peer = B;
            B.Peer = A;
            A.Rewrite = hostRewrite;
            B.Rewrite = guestRewrite;
            A.FailOnType = hostSendFailure;
            Host = new NetSession(HostDb, A, true, HostTeam, "主机", oldHostSeed,
                timer ? 2 : (double?)null, timer ? 3 : (double?)null,
                randomDefaults ? null : hostBytes ?? NetSeedTestData.Host);
            Guest = new NetSession(GuestDb, B, false, GuestTeam, "客机", oldGuestSeed,
                seedContribution: randomDefaults ? null : guestBytes ?? NetSeedTestData.Guest);
        }

        public void Pump(int count = 6)
        {
            for (int index = 0; index < count; index++) { Host.Poll(); Guest.Poll(); }
        }

        public void AssertReady(uint expected)
        {
            Pump();
            Assert.Equal(NetState.Choose, Host.State);
            Assert.Equal(NetState.Choose, Guest.State);
            Assert.Equal(expected, Host.Battle.Seed);
            Assert.Equal(expected, Guest.Battle.Seed);
            Assert.Equal(expected, Host.Battle.Record.Seed);
            Assert.Equal(expected, Guest.Battle.Record.Seed);
            Assert.Equal(Host.Battle.StateHash(), Guest.Battle.StateHash());
        }
    }

    static string Field(string message, string name)
    {
        using var json = JsonDocument.Parse(message);
        return json.RootElement.GetProperty(name).GetString();
    }

    static string Edit(string message, string type, Action<Dictionary<string, object>> change)
    {
        if (Field(message, "type") != type) return message;
        var fields = JsonSerializer.Deserialize<Dictionary<string, object>>(message);
        change(fields);
        return JsonSerializer.Serialize(fields);
    }

    static string Reveal(byte[] bytes)
    {
        return JsonSerializer.Serialize(new { type = "seed_reveal", value = NetSeedTestData.Hex(bytes) });
    }

    [Fact]
    public void KnownVectorUsesHostThenGuestSha256AndLittleEndianPrefix()
    {
        // SHA-256(00..3f) = fdeab9acf3710362bd2658cdc9a29e8f9c757fcf9811603a8c447cd1d9151108。
        Assert.Equal(0xacb9eafdu, NetSeedTestData.Seed);
        Assert.NotEqual(NetSeedTestData.Seed, NetSeedTestData.Combine(NetSeedTestData.Guest, NetSeedTestData.Host));
        var pair = new Pair();
        Assert.Null(pair.Host.Battle);
        Assert.Null(pair.Guest.Battle);
        Assert.Empty(pair.A.Messages("seed_reveal"));
        Assert.Empty(pair.B.Messages("seed_reveal"));
        Assert.Equal(4, NetSession.ProtocolVersion);
        foreach (var item in new[] { (pair.A, 0, NetSeedTestData.Host), (pair.B, 1, NetSeedTestData.Guest) })
        {
            using var hello = JsonDocument.Parse(Assert.Single(item.Item1.Messages("hello")));
            Assert.Equal(4, hello.RootElement.GetProperty("version").GetInt32());
            Assert.Equal(item.Item2, hello.RootElement.GetProperty("side").GetInt32());
            Assert.Equal(NetSeedTestData.Commit(item.Item3), hello.RootElement.GetProperty("seedCommit").GetString());
            Assert.False(hello.RootElement.TryGetProperty("seed", out _));
            Assert.False(hello.RootElement.TryGetProperty("value", out _));
        }

        pair.Host.Poll();
        Assert.Equal(NetState.Handshake, pair.Host.State);
        Assert.Null(pair.Host.Battle);
        Assert.Equal(NetSeedTestData.Hex(NetSeedTestData.Host), Field(Assert.Single(pair.A.Messages("seed_reveal")), "value"));
        Assert.Empty(pair.B.Messages("seed_reveal"));
        pair.AssertReady(0xacb9eafdu);
        Assert.Equal(NetSeedTestData.Hex(NetSeedTestData.Guest), Field(Assert.Single(pair.B.Messages("seed_reveal")), "value"));
        Assert.Equal(new[] { "hello", "seed_reveal" }, pair.A.Sent.Select(message => Field(message, "type")));
        Assert.Equal(new[] { "hello", "seed_reveal" }, pair.B.Sent.Select(message => Field(message, "type")));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void ChangingEitherCommittedContributionChangesFinalSeed(int side)
    {
        byte[] host = NetSeedTestData.Host, guest = NetSeedTestData.Guest;
        (side == 0 ? host : guest)[11] ^= 0x80;
        uint expected = NetSeedTestData.Combine(host, guest);
        Assert.NotEqual(NetSeedTestData.Seed, expected);
        new Pair(host, guest).AssertReady(expected);
    }

    [Fact]
    public void LegacySeedArgumentsCannotOverrideTheCommittedContributions()
    {
        var first = new Pair(oldHostSeed: 0, oldGuestSeed: uint.MaxValue);
        var second = new Pair(oldHostSeed: uint.MaxValue, oldGuestSeed: 0);
        first.AssertReady(NetSeedTestData.Seed);
        second.AssertReady(NetSeedTestData.Seed);
        Assert.Equal(first.Host.Battle.Record.ToJson(), second.Host.Battle.Record.ToJson());
    }

    [Fact]
    public void InjectedContributionIsCopiedBeforeTheCallerCanModifyIt()
    {
        byte[] host = NetSeedTestData.Host, guest = NetSeedTestData.Guest;
        var pair = new Pair(host, guest);
        Array.Fill(host, (byte)0xff);
        Array.Clear(guest, 0, guest.Length);
        pair.AssertReady(NetSeedTestData.Seed);
        Assert.Equal(NetSeedTestData.Hex(NetSeedTestData.Host), Field(Assert.Single(pair.A.Messages("seed_reveal")), "value"));
        Assert.Equal(NetSeedTestData.Hex(NetSeedTestData.Guest), Field(Assert.Single(pair.B.Messages("seed_reveal")), "value"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HandshakeCompletionOrFailureNeverClearsCallerOwnedContributionArrays(bool fail)
    {
        byte[] host = NetSeedTestData.Host, guest = NetSeedTestData.Guest;
        var pair = new Pair(host, guest, hostSendFailure: fail ? "seed_reveal" : null);
        if (fail)
        {
            pair.Pump();
            Assert.Equal(NetState.Error, pair.Host.State);
        }
        else pair.AssertReady(NetSeedTestData.Seed);
        Assert.Equal(NetSeedTestData.Host, host);
        Assert.Equal(NetSeedTestData.Guest, guest);
    }

    [Theory]
    [InlineData("hello")]
    [InlineData("seed_reveal")]
    public void FailedSeedMessageSendCannotContinueHandshakeOrConstructBattle(string type)
    {
        var pair = new Pair(hostSendFailure: type);
        pair.Pump();
        Assert.Equal(NetState.Error, pair.Host.State);
        Assert.Contains("发送消息失败", pair.Host.Error);
        Assert.Null(pair.Host.Battle);
        Assert.Empty(pair.A.Messages("seed_reveal"));
        int sent = pair.A.Sent.Count;
        pair.Pump();
        Assert.Equal(sent, pair.A.Sent.Count);
    }

    [Fact]
    public void DefaultGenerationProducesFreshFullLengthContributionsWithMatchingCommitments()
    {
        var first = new Pair(randomDefaults: true);
        var second = new Pair(randomDefaults: true);
        first.Pump();
        second.Pump();
        var values = new List<string>();
        foreach (var pair in new[] { first, second })
        {
            foreach (var transport in new[] { pair.A, pair.B })
            {
                string value = Field(Assert.Single(transport.Messages("seed_reveal")), "value");
                Assert.Matches("^[0-9a-f]{64}$", value);
                Assert.Equal(NetSeedTestData.Commit(Convert.FromHexString(value)), Field(Assert.Single(transport.Messages("hello")), "seedCommit"));
                values.Add(value);
            }
            uint expected = NetSeedTestData.Combine(Convert.FromHexString(values[values.Count - 2]), Convert.FromHexString(values[values.Count - 1]));
            pair.AssertReady(expected);
        }
        Assert.Equal(4, values.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(31)]
    [InlineData(33)]
    public void InvalidInjectedContributionLengthFailsBeforeSendingHello(int length)
    {
        var pair = new Pair(hostBytes: new byte[length]);
        Assert.Equal(NetState.Error, pair.Host.State);
        Assert.Contains("32", pair.Host.Error);
        Assert.Null(pair.Host.Battle);
        Assert.Empty(pair.A.Messages("hello"));
        Assert.Empty(pair.A.Messages("seed_reveal"));
        Assert.Single(pair.A.Messages("error"));
        pair.Pump();
        Assert.Equal(NetState.Error, pair.Guest.State);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("null")]
    [InlineData("number")]
    [InlineData("short")]
    [InlineData("long")]
    [InlineData("upper")]
    [InlineData("nonhex")]
    public void InvalidSeedCommitmentIsRejectedBeforeRevealingLocalContribution(string mode)
    {
        var pair = new Pair(guestRewrite: message => Edit(message, "hello", fields =>
        {
            if (mode == "missing") fields.Remove("seedCommit");
            else fields["seedCommit"] = InvalidValue(mode, NetSeedTestData.Commit(NetSeedTestData.Guest));
        }));
        pair.Host.Poll();
        Assert.Equal(NetState.Error, pair.Host.State);
        Assert.Null(pair.Host.Battle);
        Assert.Empty(pair.A.Messages("seed_reveal"));
        Assert.False(string.IsNullOrWhiteSpace(pair.Host.Error));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("null")]
    [InlineData("number")]
    [InlineData("short")]
    [InlineData("long")]
    [InlineData("upper")]
    [InlineData("nonhex")]
    public void MalformedSeedRevealCannotStartBattle(string mode)
    {
        var pair = new Pair(guestRewrite: message => Edit(message, "seed_reveal", fields =>
        {
            if (mode == "missing") fields.Remove("value");
            else fields["value"] = InvalidValue(mode, NetSeedTestData.Hex(NetSeedTestData.Guest));
        }));
        pair.Pump();
        Assert.Equal(NetState.Error, pair.Host.State);
        Assert.Equal(NetState.Error, pair.Guest.State);
        Assert.Null(pair.Host.Battle);
        Assert.False(string.IsNullOrWhiteSpace(pair.Host.Error));
    }

    static object InvalidValue(string mode, string valid)
    {
        if (mode == "null") return null;
        if (mode == "number") return 42;
        if (mode == "short") return valid.Substring(2);
        if (mode == "long") return valid + "00";
        if (mode == "upper") return valid.ToUpperInvariant();
        return new string('g', 64);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void TamperedRevealFromEitherSideFailsCommitmentAndNotifiesPeer(int cheatingSide)
    {
        Func<string, string> tamper = message => Edit(message, "seed_reveal", fields => fields["value"] = new string('f', 64));
        var pair = new Pair(hostRewrite: cheatingSide == 0 ? tamper : null, guestRewrite: cheatingSide == 1 ? tamper : null);
        pair.Pump();
        var receiver = cheatingSide == 0 ? pair.Guest : pair.Host;
        Assert.Equal(NetState.Error, pair.Host.State);
        Assert.Equal(NetState.Error, pair.Guest.State);
        Assert.Contains("承诺", receiver.Error);
        Assert.Null(receiver.Battle);
        Assert.Equal(cheatingSide == 0 ? pair.Guest.Error : pair.Host.Error,
            Field(Assert.Single((cheatingSide == 0 ? pair.B : pair.A).Messages("error")), "message"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(-1)]
    public void HostRejectsPeerHelloWithWrongOrInvalidRole(int side)
    {
        var pair = new Pair(guestRewrite: message => Edit(message, "hello", fields => fields["side"] = side));
        pair.Pump();
        Assert.Equal(NetState.Error, pair.Host.State);
        Assert.Null(pair.Host.Battle);
        Assert.Empty(pair.A.Messages("seed_reveal"));
        Assert.False(string.IsNullOrWhiteSpace(pair.Host.Error));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void LegacyProtocolReportsChineseVersionMismatchBeforeBattle(int version)
    {
        var pair = new Pair(guestRewrite: message => Edit(message, "hello", fields => fields["version"] = version));
        pair.Pump();
        Assert.Equal(NetState.Error, pair.Host.State);
        Assert.Equal(NetState.Error, pair.Guest.State);
        Assert.Contains("版本不一致", pair.Host.Error);
        Assert.Null(pair.Host.Battle);
        Assert.Empty(pair.A.Messages("seed_reveal"));
    }

    [Fact]
    public void RevealingBeforePeerCommitmentIsRejectedWithoutLocalReveal()
    {
        var pair = new Pair();
        Assert.True(pair.A.TryReceive(out _));
        pair.A.Inject(Reveal(NetSeedTestData.Guest));
        pair.Host.Poll();
        Assert.Equal(NetState.Error, pair.Host.State);
        Assert.Null(pair.Host.Battle);
        Assert.Empty(pair.A.Messages("seed_reveal"));
        Assert.False(string.IsNullOrWhiteSpace(pair.Host.Error));
    }

    [Fact]
    public void RepeatedHelloWhileAwaitingRevealIsRejected()
    {
        var pair = new Pair();
        pair.A.Inject(Assert.Single(pair.B.Messages("hello")));
        pair.Host.Poll();
        Assert.Equal(NetState.Error, pair.Host.State);
        Assert.Contains("重复", pair.Host.Error);
        Assert.Null(pair.Host.Battle);
        Assert.Single(pair.A.Messages("seed_reveal"));
    }

    [Fact]
    public void DuplicateSeedRevealIsRejectedWithoutAnyBattleAction()
    {
        var pair = new Pair();
        pair.B.DuplicateTypes.Add("seed_reveal");
        pair.Pump();
        Assert.Equal(NetState.Error, pair.Host.State);
        Assert.Equal(NetState.Error, pair.Guest.State);
        Assert.Contains("重复", pair.Host.Error);
        Assert.Empty(pair.Host.Battle.Record.Events);
        Assert.Empty(pair.Guest.Battle.Record.Events);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LateSeedRevealCannotChangeAnActiveOrFinishedBattle(bool finished)
    {
        var pair = new Pair();
        pair.AssertReady(NetSeedTestData.Seed);
        if (finished)
        {
            pair.Host.Battle.Turn = Battle.TurnCap;
            pair.Guest.Battle.Turn = Battle.TurnCap;
            pair.Host.Submit(BattleAction.Pass());
            pair.Guest.Submit(BattleAction.Pass());
            pair.Pump();
            Assert.Equal(NetState.Over, pair.Host.State);
            Assert.Equal(NetState.Over, pair.Guest.State);
        }
        ulong before = pair.Host.Battle.StateHash();
        string record = pair.Host.Battle.Record.ToJson();
        pair.A.Inject(Assert.Single(pair.B.Messages("seed_reveal")));
        pair.Pump();
        Assert.Equal(NetState.Error, pair.Host.State);
        Assert.Equal(NetState.Error, pair.Guest.State);
        Assert.Equal(before, pair.Host.Battle.StateHash());
        Assert.Equal(record, pair.Host.Battle.Record.ToJson());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingHelloTimesOutAtExactlyTenSecondsWithoutRequiringBattleClock(bool timer)
    {
        var pair = new Pair(timer: timer);
        if (!timer) Assert.Null(pair.Host.Clock);
        pair.Host.Tick(9.75);
        Assert.Equal(NetState.Handshake, pair.Host.State);
        pair.Host.Tick(0.25);
        Assert.Equal(NetState.Error, pair.Host.State);
        Assert.Contains("超时", pair.Host.Error);
        Assert.Null(pair.Host.Battle);
        Assert.Empty(pair.A.Messages("seed_reveal"));
        int sent = pair.A.Sent.Count;
        pair.Host.Tick(100);
        Assert.Equal(sent, pair.A.Sent.Count);
    }

    [Fact]
    public void ReceivingHelloStartsASeparateTenSecondRevealDeadline()
    {
        var pair = new Pair();
        long initial = pair.Host.TimerRevision;
        pair.Host.Tick(9);
        pair.Host.Poll();
        Assert.True(pair.Host.TimerRevision > initial);
        long revealing = pair.Host.TimerRevision;
        pair.Host.Tick(9.75);
        Assert.Equal(NetState.Handshake, pair.Host.State);
        Assert.Equal(revealing, pair.Host.TimerRevision);
        pair.Host.Tick(0.25);
        Assert.Equal(NetState.Error, pair.Host.State);
        Assert.Contains("超时", pair.Host.Error);
        Assert.Null(pair.Host.Battle);
        Assert.Single(pair.A.Messages("seed_reveal"));
    }

    [Theory]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidHandshakeTickDoesNotConsumeTheDeadline(double seconds)
    {
        var pair = new Pair();
        long before = pair.Host.TimerRevision;
        Assert.Throws<ArgumentOutOfRangeException>(() => pair.Host.Tick(seconds));
        Assert.Equal(before, pair.Host.TimerRevision);
        pair.Host.Tick(9.75);
        Assert.Equal(NetState.Handshake, pair.Host.State);
        pair.Host.Tick(0.25);
        Assert.Equal(NetState.Error, pair.Host.State);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void ContentChangedAfterBothCommitmentsButBeforeRevealCannotCreateChangedBattle(int changedSide)
    {
        var pair = new Pair();
        pair.A.HoldTypes.Add("seed_reveal");
        pair.B.HoldTypes.Add("seed_reveal");
        pair.Pump();
        Assert.Null(pair.Host.Battle);
        Assert.Null(pair.Guest.Battle);
        Assert.Single(pair.A.Messages("seed_reveal"));
        Assert.Single(pair.B.Messages("seed_reveal"));
        (changedSide == 0 ? pair.HostDb : pair.GuestDb).Streamer("bell").Stats["hp"]++;
        pair.A.ReleaseAll();
        pair.B.ReleaseAll();
        pair.Pump();
        var changed = changedSide == 0 ? pair.Host : pair.Guest;
        Assert.Equal(NetState.Error, pair.Host.State);
        Assert.Equal(NetState.Error, pair.Guest.State);
        Assert.Null(changed.Battle);
        Assert.Contains("内容不一致", changed.Error);
    }

    [Fact]
    public void BattleActionsCannotArriveBeforeSeedExchangeCompletes()
    {
        var pair = new Pair();
        pair.Host.Poll();
        pair.A.Inject("{\"type\":\"commit\",\"turn\":1,\"kind\":\"action\",\"digest\":\"" + new string('0', 64) + "\"}");
        pair.Host.Poll();
        Assert.Equal(NetState.Error, pair.Host.State);
        Assert.Null(pair.Host.Battle);
        Assert.False(string.IsNullOrWhiteSpace(pair.Host.Error));
    }

    [Fact]
    public void FullNetworkBattleAndReplayHaveIdenticalHashesAndOnlyTheFinalSeedIsRecorded()
    {
        var pair = new Pair();
        pair.AssertReady(NetSeedTestData.Seed);
        for (int step = 0; step < 5000; step++)
        {
            pair.Pump();
            foreach (var session in new[] { pair.Host, pair.Guest })
            {
                Assert.NotEqual(NetState.Error, session.State);
                Assert.NotEqual(NetState.Desync, session.State);
                if (session.State == NetState.Choose && !session.HasSubmitted)
                    session.Submit(Ai.Choose(session.Battle, session.MySide));
                if (session.State == NetState.Replace && !session.HasSubmitted)
                    session.SubmitReplace(Ai.ChooseReplacement(session.Battle, session.MySide));
            }
            if (pair.Host.State == NetState.Over && pair.Guest.State == NetState.Over) break;
        }
        Assert.Equal(NetState.Over, pair.Host.State);
        Assert.Equal(NetState.Over, pair.Guest.State);
        Assert.Equal(pair.Host.Battle.StateHash(), pair.Guest.Battle.StateHash());
        Assert.Equal(pair.Host.Battle.Winner, pair.Guest.Battle.Winner);
        string json = pair.Host.Battle.Record.ToJson();
        Assert.Equal(json, pair.Guest.Battle.Record.ToJson());
        Assert.DoesNotContain("seedCommit", json);
        Assert.DoesNotContain("seed_reveal", json);
        foreach (byte[] contribution in new[] { NetSeedTestData.Host, NetSeedTestData.Guest })
        {
            Assert.DoesNotContain(NetSeedTestData.Hex(contribution), json);
            Assert.DoesNotContain(NetSeedTestData.Commit(contribution), json);
        }
        var record = BattleRecord.FromJson(json);
        Assert.Equal(NetSeedTestData.Seed, record.Seed);
        Assert.NotEmpty(record.Events);
        var replay = Replay.Run(BattleTests.LoadPack("original"), record);
        Assert.Equal(Phase.Over, replay.Phase);
        Assert.Equal(pair.Host.Battle.StateHash(), replay.StateHash());
        Assert.Equal(json, replay.Record.ToJson());
        Assert.Equal(Log(pair.Host.Battle), Log(pair.Guest.Battle));
        Assert.Equal(Log(pair.Host.Battle), Log(replay));
    }

    static string Log(Battle battle)
    {
        return JsonSerializer.Serialize(battle.Log, new JsonSerializerOptions { IncludeFields = true });
    }
}
