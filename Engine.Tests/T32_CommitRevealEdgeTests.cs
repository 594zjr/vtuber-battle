using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StreamDuel.Core;
using Xunit;

public class T32_CommitRevealEdgeTests
{
    static readonly string[] HostTeam = { "bell", "enka", "shiori", "lumi", "hane", "momo" };
    static readonly string[] GuestTeam = { "kei", "yuan", "nyako", "buck", "kasumi", "bee" };

    sealed class Transport : INetTransport
    {
        readonly Queue<string> incoming = new Queue<string>();
        readonly Queue<string> held = new Queue<string>();
        long highestIncoming;
        public Transport Peer;
        public bool Hold, DuplicateReveals;
        public Func<string, string> Rewrite;
        public readonly List<string> Sent = new List<string>();

        public void Send(string message)
        {
            if (Rewrite != null) message = Rewrite(message);
            Sent.Add(message);
            if (Hold && !NetWireTestTransport.IsAck(message)) held.Enqueue(message);
            else Peer.incoming.Enqueue(message);
            // 这里模拟恶意再次公开，而非协议 4 允许的同序号线路重发。
            if (DuplicateReveals && Field(message, "type") == "reveal")
                Peer.incoming.Enqueue(NetWireTestTransport.WithSequence(message, NetWireTestTransport.Sequence(message) + 1));
        }

        public bool TryReceive(out string message)
        {
            if (incoming.Count == 0) { message = null; return false; }
            message = incoming.Dequeue();
            highestIncoming = Math.Max(highestIncoming, NetWireTestTransport.Sequence(message));
            return true;
        }

        public void Inject(string message)
        {
            // 已带 seq 的握手变体保留原序号；手写业务包分配新的序号。
            try
            {
                if (NetWireTestTransport.Sequence(message) == 0)
                    message = NetWireTestTransport.StampNew(message, ref highestIncoming);
            }
            catch (JsonException) { }
            incoming.Enqueue(message);
        }

        public void ReleaseNext(string expectedType)
        {
            Assert.NotEmpty(held);
            string message = held.Dequeue();
            Assert.Equal(expectedType, Field(message, "type"));
            Peer.incoming.Enqueue(message);
        }

        public string[] Messages(string type, string kind = null)
        {
            return NetWireTestTransport.Business(Sent).Where(message => Field(message, "type") == type && (kind == null || Field(message, "kind") == kind)).ToArray();
        }
    }

    sealed class Pair
    {
        public readonly Transport A = new Transport(), B = new Transport();
        public readonly NetSession Host, Guest;
        public Pair(bool handshake = true)
        {
            A.Peer = B;
            B.Peer = A;
            Host = new NetSession(BattleTests.LoadPack("original"), A, true, HostTeam, "主机", 19);
            Guest = new NetSession(BattleTests.LoadPack("original"), B, false, GuestTeam, "客机", null);
            if (handshake)
            {
                Pump();
                Assert.Equal(NetState.Choose, Host.State);
                Assert.Equal(NetState.Choose, Guest.State);
            }
        }

        public void Pump(int count = 6)
        {
            for (int i = 0; i < count; i++) { Host.Poll(); Guest.Poll(); }
        }

        public void PassTurn()
        {
            Host.Submit(BattleAction.Pass());
            Guest.Submit(BattleAction.Pass());
            Pump();
        }

        public void EnterReplacement(params int[] sides)
        {
            foreach (var session in new[] { Host, Guest })
                foreach (int side in sides)
                {
                    session.Battle.Active(side).Hp = 0;
                    session.Battle.Active(side).Fainted = true;
                }
            PassTurn();
        }
    }

    static string Field(string message, string name)
    {
        using var json = JsonDocument.Parse(message);
        return json.RootElement.GetProperty(name).GetString();
    }

    static string Digest(string wire, string nonce)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(wire + "|" + nonce))).ToLowerInvariant();
    }

    static string Commit(string wire = "P", string nonce = "00000000000000000000000000000000", int turn = 1, string kind = "action")
    {
        return JsonSerializer.Serialize(new { type = "commit", turn, kind, digest = Digest(wire, nonce) });
    }

    static string Reveal(string wire = "P", string nonce = "00000000000000000000000000000000")
    {
        return JsonSerializer.Serialize(new { type = "reveal", turn = 1, kind = "action", action = wire, nonce });
    }

    static string FullLog(Battle battle)
    {
        return JsonSerializer.Serialize(battle.Log, new JsonSerializerOptions { IncludeFields = true });
    }

    [Fact]
    public void VersionOnePeerIsRejectedBeforeConstructingTheBattle()
    {
        var pair = new Pair(handshake: false);
        Assert.True(pair.A.TryReceive(out string hello));
        var fields = JsonSerializer.Deserialize<Dictionary<string, object>>(hello);
        fields["version"] = 1;
        pair.A.Inject(JsonSerializer.Serialize(fields));

        pair.Pump();

        Assert.Equal(NetState.Error, pair.Host.State);
        Assert.Equal(NetState.Error, pair.Guest.State);
        Assert.Contains("版本", pair.Host.Error);
        Assert.Null(pair.Host.Battle);
    }

    [Fact]
    public void CommitReceiptAllowsLocalChoiceButNeitherSideRevealsUntilItHasBothCommits()
    {
        var pair = new Pair();
        ulong before = pair.Host.Battle.StateHash();
        pair.Host.Submit(BattleAction.Skill(0));
        pair.Pump(20);
        Assert.Equal(NetState.Choose, pair.Guest.State);
        Assert.False(pair.Guest.HasSubmitted);
        Assert.Empty(pair.A.Messages("reveal"));
        Assert.Empty(pair.B.Messages("commit"));
        Assert.Empty(pair.B.Messages("reveal"));
        Assert.Equal(before, pair.Host.Battle.StateHash());
        Assert.Equal(before, pair.Guest.Battle.StateHash());
        using (var json = JsonDocument.Parse(Assert.Single(pair.A.Messages("commit"))))
        {
            Assert.False(json.RootElement.TryGetProperty("action", out _));
            Assert.False(json.RootElement.TryGetProperty("nonce", out _));
            Assert.Matches("^[0-9a-f]{64}$", json.RootElement.GetProperty("digest").GetString());
        }

        pair.A.Hold = pair.B.Hold = true;
        pair.Guest.Submit(BattleAction.Skill(0));
        Assert.Single(pair.B.Messages("commit"));
        Assert.Single(pair.B.Messages("reveal"));
        Assert.Empty(pair.A.Messages("reveal"));
        Assert.Equal(before, pair.Guest.Battle.StateHash());
        pair.B.ReleaseNext("commit");
        pair.Host.Poll();
        Assert.Single(pair.A.Messages("reveal"));
        Assert.Equal(before, pair.Host.Battle.StateHash());
        Assert.Empty(pair.Host.Battle.Record.Events);

        pair.B.ReleaseNext("reveal");
        pair.Host.Poll();
        Assert.Single(pair.Host.Battle.Record.Events);
        Assert.Empty(pair.Guest.Battle.Record.Events);
        pair.A.ReleaseNext("reveal");
        pair.Guest.Poll();
        pair.A.ReleaseNext("hash");
        pair.B.ReleaseNext("hash");
        pair.Pump();
        Assert.Equal(NetState.Choose, pair.Host.State);
        Assert.Equal(NetState.Choose, pair.Guest.State);
        Assert.Equal(pair.Host.Battle.StateHash(), pair.Guest.Battle.StateHash());
    }

    [Theory]
    [InlineData("action")]
    [InlineData("nonce")]
    public void AlteringEitherRevealedValueBreaksTheCommitmentWithoutResolving(string alteredField)
    {
        var pair = new Pair();
        ulong before = pair.Host.Battle.StateHash();
        pair.B.Rewrite = message =>
        {
            if (Field(message, "type") != "reveal") return message;
            var values = JsonSerializer.Deserialize<Dictionary<string, object>>(message);
            values[alteredField] = alteredField == "action" ? "P" :
                (Field(message, "nonce")[0] == '0' ? "1" : "0") + Field(message, "nonce").Substring(1);
            return JsonSerializer.Serialize(values);
        };
        pair.Host.Submit(BattleAction.Skill(0));
        pair.Guest.Submit(BattleAction.Skill(0));
        pair.Pump();

        Assert.Equal(NetState.Error, pair.Host.State);
        Assert.Contains("承诺", pair.Host.Error);
        Assert.Equal(NetState.Error, pair.Guest.State);
        Assert.Equal(before, pair.Host.Battle.StateHash());
        Assert.Empty(pair.Host.Battle.Record.Events);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EarlyRevealCannotForceAnUncommittedPeerToResolve(bool receivedPeerCommit)
    {
        var pair = new Pair();
        ulong before = pair.Host.Battle.StateHash();
        if (receivedPeerCommit) pair.A.Inject(Commit());
        pair.A.Inject(Reveal());
        pair.Pump();

        Assert.Equal(NetState.Error, pair.Host.State);
        Assert.Equal(NetState.Error, pair.Guest.State);
        Assert.False(pair.Host.HasSubmitted);
        Assert.Empty(pair.A.Messages("reveal"));
        Assert.Equal(before, pair.Host.Battle.StateHash());
        Assert.Empty(pair.Host.Battle.Record.Events);
    }

    [Fact]
    public void RepeatedRevealNeverResolvesTwice()
    {
        var pair = new Pair();
        pair.B.DuplicateReveals = true;
        pair.Host.Submit(BattleAction.Pass());
        pair.Guest.Submit(BattleAction.Pass());
        pair.Pump();

        Assert.Equal(NetState.Error, pair.Host.State);
        Assert.Contains("上一阶段哈希尚未确认", pair.Host.Error);
        Assert.Equal(NetState.Error, pair.Guest.State);
        Assert.Single(pair.Host.Battle.Record.Events);
        Assert.Single(pair.A.Messages("hash"));
        ulong after = pair.Host.Battle.StateHash();
        pair.Pump(20);
        Assert.Equal(after, pair.Host.Battle.StateHash());
        Assert.Single(pair.Host.Battle.Record.Events);
    }

    [Theory]
    [InlineData(0, "action")]
    [InlineData(999, "action")]
    [InlineData(1, "replace")]
    [InlineData(1, "unknown")]
    public void CommitTurnAndKindMustMatchTheCurrentPhase(int turn, string kind)
    {
        var pair = new Pair();
        ulong before = pair.Host.Battle.StateHash();
        pair.A.Inject(Commit(turn: turn, kind: kind));
        pair.Pump();
        Assert.Equal(NetState.Error, pair.Host.State);
        Assert.Equal(NetState.Error, pair.Guest.State);
        Assert.Equal(before, pair.Host.Battle.StateHash());
        Assert.Empty(pair.Host.Battle.Record.Events);
    }

    [Theory]
    [InlineData("S5")]
    [InlineData("W6")]
    public void AValidDigestDoesNotMakeAnInvalidActionEncodingAcceptable(string wire)
    {
        var pair = new Pair();
        ulong before = pair.Host.Battle.StateHash();
        pair.Host.Submit(BattleAction.Pass());
        pair.A.Inject(Commit(wire));
        pair.A.Inject(Reveal(wire));

        pair.Host.Poll();

        Assert.Equal(NetState.Error, pair.Host.State);
        Assert.Equal(before, pair.Host.Battle.StateHash());
        Assert.Empty(pair.Host.Battle.Record.Events);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void SingleReplacementSendsOnlyTheRequiredSidesCommitAndReveal(int side)
    {
        var pair = new Pair();
        pair.EnterReplacement(side);
        var required = side == 0 ? pair.Host : pair.Guest;
        var other = side == 0 ? pair.Guest : pair.Host;
        var sender = side == 0 ? pair.A : pair.B;
        var idle = side == 0 ? pair.B : pair.A;
        var reference = pair.Host.Battle.Clone();
        Assert.Equal(NetState.Replace, required.State);
        Assert.Equal(NetState.Waiting, other.State);
        Assert.Throws<InvalidOperationException>(() => other.SubmitReplace(1));

        required.SubmitReplace(1);
        pair.Pump();
        reference.Replace(side, 1);

        Assert.Single(sender.Messages("commit", "replace"));
        Assert.Equal("W1", Field(Assert.Single(sender.Messages("reveal", "replace")), "action"));
        Assert.Empty(idle.Messages("commit", "replace"));
        Assert.Empty(idle.Messages("reveal", "replace"));
        Assert.Equal(NetState.Choose, pair.Host.State);
        Assert.Equal(NetState.Choose, pair.Guest.State);
        Assert.Equal(reference.StateHash(), pair.Host.Battle.StateHash());
        Assert.Equal(reference.StateHash(), pair.Guest.Battle.StateHash());
        Assert.Equal(FullLog(reference), FullLog(pair.Host.Battle));
    }

    [Fact]
    public void DoubleReplacementWaitsForBothCommitsThenKeepsHostFirstOrder()
    {
        var pair = new Pair();
        pair.EnterReplacement(0, 1);
        var reference = pair.Host.Battle.Clone();
        ulong before = reference.StateHash();
        pair.Guest.SubmitReplace(2);
        pair.Pump(20);
        Assert.Empty(pair.B.Messages("reveal", "replace"));
        Assert.Equal(before, pair.Host.Battle.StateHash());
        Assert.Equal(before, pair.Guest.Battle.StateHash());

        pair.Host.SubmitReplace(1);
        pair.Pump();
        reference.Replace(0, 1);
        reference.Replace(1, 2);

        foreach (var transport in new[] { pair.A, pair.B })
        {
            Assert.Single(transport.Messages("commit", "replace"));
            Assert.Single(transport.Messages("reveal", "replace"));
            Assert.Equal(3, transport.Messages("hash").Length);
        }
        Assert.Equal(new[] { 0, 1 }, pair.Host.Battle.Record.Events.Skip(1).Select(item => item.Side));
        Assert.Equal(NetState.Choose, pair.Host.State);
        Assert.Equal(NetState.Choose, pair.Guest.State);
        Assert.Equal(reference.StateHash(), pair.Host.Battle.StateHash());
        Assert.Equal(reference.StateHash(), pair.Guest.Battle.StateHash());
    }

    [Fact]
    public void FreshNoncesValidateIndependentlyAndNeverChangeBattleRandomnessOrRecords()
    {
        var first = new Pair();
        var second = new Pair();
        foreach (var pair in new[] { first, second })
        {
            pair.Host.Submit(BattleAction.Skill(0));
            pair.Guest.Submit(BattleAction.Skill(0));
            pair.Pump();
            Assert.Equal(NetState.Choose, pair.Host.State);
            Assert.Equal(NetState.Choose, pair.Guest.State);
            foreach (var transport in new[] { pair.A, pair.B })
            {
                string commit = Assert.Single(transport.Messages("commit"));
                string reveal = Assert.Single(transport.Messages("reveal"));
                string nonce = Field(reveal, "nonce");
                Assert.Matches("^[0-9a-f]{32}$", nonce);
                Assert.Equal(Digest(Field(reveal, "action"), nonce), Field(commit, "digest"));
                Assert.DoesNotContain(transport.Sent, message => Field(message, "type") == "action" || Field(message, "type") == "replace");
            }
        }
        Assert.NotEqual(Field(first.A.Messages("reveal").Single(), "nonce"), Field(second.A.Messages("reveal").Single(), "nonce"));
        Assert.Equal(first.Host.Battle.StateHash(), second.Host.Battle.StateHash());
        Assert.Equal(first.Host.Battle.Record.ToJson(), second.Host.Battle.Record.ToJson());
        Assert.Equal(FullLog(first.Host.Battle), FullLog(second.Host.Battle));
        for (int i = 0; i < 8; i++) Assert.Equal(first.Host.Battle.Random(), second.Host.Battle.Random());
    }
}
