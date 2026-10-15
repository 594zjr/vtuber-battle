using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using StreamDuel.Core;
using Xunit;

public class T29_NetSessionEdgeTests
{
    static readonly string[] HostTeam = { "bell", "enka", "shiori", "lumi", "hane", "momo" };
    static readonly string[] GuestTeam = { "kei", "yuan", "nyako", "buck", "kasumi", "bee" };

    // 暂扣哈希模拟一端结算已完成但确认尚未送达，不启动真实网络。
    sealed class Transport : INetTransport
    {
        readonly Queue<string> incoming = new Queue<string>();
        readonly Queue<string> held = new Queue<string>();
        public Transport Peer;
        public bool HoldHashes;
        public readonly List<string> Sent = new List<string>();

        public void Send(string message)
        {
            Sent.Add(message);
            if (HoldHashes && TypeOf(message) == "hash") held.Enqueue(message);
            else Peer.incoming.Enqueue(message);
        }

        public bool TryReceive(out string message)
        {
            if (incoming.Count == 0) { message = null; return false; }
            message = incoming.Dequeue();
            return true;
        }

        public void Inject(string message) { incoming.Enqueue(message); }

        public void ReleaseHash(Func<string, string> transform = null)
        {
            Assert.NotEmpty(held);
            string message = held.Dequeue();
            Peer.incoming.Enqueue(transform == null ? message : transform(message));
        }

        public void ReleaseAllHashes()
        {
            while (held.Count > 0) ReleaseHash();
        }

        public string[] Messages(string type) { return Sent.Where(message => TypeOf(message) == type).ToArray(); }
    }

    sealed class Pair
    {
        public readonly Transport HostTransport = new Transport();
        public readonly Transport GuestTransport = new Transport();
        public readonly NetSession Host;
        public readonly NetSession Guest;

        public Pair(uint seed = 19, string hostName = "主机", string guestName = "客机", uint? guestSeed = null, bool handshake = true)
        {
            HostTransport.Peer = GuestTransport;
            GuestTransport.Peer = HostTransport;
            Host = new NetSession(BattleTests.LoadPack("original"), HostTransport, true, HostTeam, hostName, seed,
                seedContribution: NetSeedTestData.Host);
            Guest = new NetSession(BattleTests.LoadPack("original"), GuestTransport, false, GuestTeam, guestName, guestSeed,
                seedContribution: NetSeedTestData.Guest);
            if (handshake)
            {
                Pump();
                Assert.Equal(NetState.Choose, Host.State);
                Assert.Equal(NetState.Choose, Guest.State);
            }
        }

        public void Pump(int count = 4)
        {
            for (int index = 0; index < count; index++) { Host.Poll(); Guest.Poll(); }
        }

        public void Both(Action<Battle> change)
        {
            change(Host.Battle);
            change(Guest.Battle);
        }

        public void PassTurn()
        {
            Host.Submit(BattleAction.Pass());
            Guest.Submit(BattleAction.Pass());
            Pump();
        }
    }

    [Fact]
    public void HandshakePreservesEscapedNamesAndUsesBothCommittedContributions()
    {
        const string hostName = "主\"机\\一\n第二行\t\r";
        const string guestName = "客机😀\"\\";
        var pair = new Pair(uint.MaxValue, hostName, guestName, 7, false);
        Assert.Null(pair.Host.Battle);
        Assert.Null(pair.Guest.Battle);
        Assert.Equal(NetState.Handshake, pair.Host.State);
        Assert.Equal(NetState.Handshake, pair.Guest.State);

        pair.Pump();

        foreach (var session in new[] { pair.Host, pair.Guest })
        {
            Assert.Equal(NetState.Choose, session.State);
            Assert.Equal(NetSeedTestData.Seed, session.Battle.Seed);
            Assert.Equal(new[] { hostName, guestName }, session.Battle.Sides.Select(side => side.Name));
            Assert.Equal(HostTeam, session.Battle.Sides[0].Team.Select(fighter => fighter.Def.Id));
            Assert.Equal(GuestTeam, session.Battle.Sides[1].Team.Select(fighter => fighter.Def.Id));
        }
        foreach (var message in pair.HostTransport.Sent.Concat(pair.GuestTransport.Sent))
        {
            Assert.DoesNotContain("\n", message);
            Assert.DoesNotContain("\r", message);
            using var parsed = JsonDocument.Parse(message);
            string type = parsed.RootElement.GetProperty("type").GetString();
            Assert.True(type == "hello" || type == "seed_reveal");
        }
        foreach (var item in new[] { (pair.HostTransport, 0, NetSeedTestData.Host), (pair.GuestTransport, 1, NetSeedTestData.Guest) })
        {
            Assert.Equal(new[] { "hello", "seed_reveal" }, item.Item1.Sent.Select(TypeOf));
            using var hello = JsonDocument.Parse(Assert.Single(item.Item1.Messages("hello")));
            Assert.Equal(item.Item2, hello.RootElement.GetProperty("side").GetInt32());
            Assert.Equal(NetSeedTestData.Commit(item.Item3), hello.RootElement.GetProperty("seedCommit").GetString());
            using var reveal = JsonDocument.Parse(Assert.Single(item.Item1.Messages("seed_reveal")));
            Assert.Equal(NetSeedTestData.Hex(item.Item3), reveal.RootElement.GetProperty("value").GetString());
        }
        Assert.Equal(pair.Host.Battle.StateHash(), pair.Guest.Battle.StateHash());
    }

    [Fact]
    public void SlowSubmissionAndRepeatedPollsNeverResolveOrSendTwice()
    {
        var pair = new Pair();
        ulong before = pair.Host.Battle.StateHash();
        pair.Host.Submit(BattleAction.Pass());
        pair.Pump(20);
        Assert.Equal(before, pair.Host.Battle.StateHash());
        Assert.Equal(before, pair.Guest.Battle.StateHash());
        Assert.Empty(pair.Host.Battle.Record.Events);
        Assert.True(pair.Host.HasSubmitted);
        Assert.False(pair.Guest.HasSubmitted);
        Assert.Equal(NetState.Waiting, pair.Host.State);
        Assert.Equal(NetState.Choose, pair.Guest.State);
        Assert.Single(pair.HostTransport.Messages("commit"));
        Assert.Empty(pair.GuestTransport.Messages("commit"));
        Assert.Empty(pair.HostTransport.Messages("reveal"));
        Assert.Empty(pair.GuestTransport.Messages("reveal"));

        pair.Guest.Submit(BattleAction.Pass());
        pair.Pump(20);
        Assert.Equal(NetState.Choose, pair.Host.State);
        Assert.Equal(NetState.Choose, pair.Guest.State);
        Assert.False(pair.Host.HasSubmitted);
        Assert.False(pair.Guest.HasSubmitted);
        Assert.Single(pair.Host.Battle.Record.Events);
        Assert.Single(pair.Guest.Battle.Record.Events);
        Assert.Single(pair.HostTransport.Messages("hash"));
        Assert.Single(pair.GuestTransport.Messages("hash"));
        Assert.Equal(pair.Host.Battle.StateHash(), pair.Guest.Battle.StateHash());
    }

    [Fact]
    public void MatchingHashBeforeNextActionInTheSameQueueOpensTheNextTurn()
    {
        var pair = new Pair();
        pair.HostTransport.HoldHashes = true;
        pair.GuestTransport.HoldHashes = true;
        pair.PassTurn();
        Assert.Equal(NetState.Waiting, pair.Host.State);
        Assert.Equal(NetState.Waiting, pair.Guest.State);

        // 主机先收到确认即可进入下一阶段；客机稍后一次 Poll 收到确认和新行动。
        pair.GuestTransport.ReleaseAllHashes();
        pair.Host.Poll();
        Assert.Equal(NetState.Choose, pair.Host.State);
        pair.HostTransport.ReleaseAllHashes();
        pair.HostTransport.HoldHashes = false;
        pair.GuestTransport.HoldHashes = false;
        pair.Host.Submit(BattleAction.Pass());
        pair.Guest.Poll();
        Assert.Equal(NetState.Choose, pair.Guest.State);
        pair.Guest.Submit(BattleAction.Pass());
        pair.Pump();

        Assert.Equal(NetState.Choose, pair.Host.State);
        Assert.Equal(NetState.Choose, pair.Guest.State);
        Assert.Equal(2, pair.Host.Battle.Record.Events.Count);
        Assert.Equal(2, pair.Guest.Battle.Record.Events.Count);
        Assert.Equal(pair.Host.Battle.StateHash(), pair.Guest.Battle.StateHash());
    }

    [Fact]
    public void BothReplacementsApplyHostFirstAndMatchEverySameTurnHashInFifoOrder()
    {
        var pair = new Pair();
        var reference = Reference();
        pair.Both(FaintActives);
        FaintActives(reference);
        pair.PassTurn();
        reference.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        Assert.Equal(NetState.Replace, pair.Host.State);
        Assert.Equal(NetState.Replace, pair.Guest.State);
        pair.HostTransport.HoldHashes = true;
        pair.GuestTransport.HoldHashes = true;

        // 客机先提交也不能改变两端补位执行顺序。
        pair.Guest.SubmitReplace(2);
        pair.Pump();
        Assert.Single(pair.Host.Battle.Record.Events);
        pair.Host.SubmitReplace(1);
        pair.Pump();
        reference.Replace(0, 1);
        string firstHash = reference.StateHash().ToString("x16");
        reference.Replace(1, 2);
        string secondHash = reference.StateHash().ToString("x16");
        foreach (var session in new[] { pair.Host, pair.Guest })
        {
            Assert.Equal(NetState.Waiting, session.State);
            Assert.Equal(new[] { 0, 1 }, session.Battle.Record.Events.Skip(1).Select(item => item.Side));
            Assert.Equal(reference.StateHash(), session.Battle.StateHash());
            Assert.Equal(LogText(reference), LogText(session.Battle));
        }
        foreach (var transport in new[] { pair.HostTransport, pair.GuestTransport })
        {
            var hashes = transport.Messages("hash");
            Assert.Equal(3, hashes.Length);
            AssertHash(hashes[1], 2, firstHash);
            AssertHash(hashes[2], 2, secondHash);
        }

        pair.HostTransport.ReleaseHash();
        pair.GuestTransport.ReleaseHash();
        pair.Pump();
        Assert.Equal(NetState.Waiting, pair.Host.State);
        Assert.Equal(NetState.Waiting, pair.Guest.State);
        pair.HostTransport.ReleaseHash();
        pair.GuestTransport.ReleaseHash();
        pair.Pump();
        Assert.Equal(NetState.Choose, pair.Host.State);
        Assert.Equal(NetState.Choose, pair.Guest.State);
        pair.HostTransport.HoldHashes = false;
        pair.GuestTransport.HoldHashes = false;
        pair.PassTurn();
        reference.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        AssertHash(pair.HostTransport.Messages("hash").Last(), 2, reference.StateHash().ToString("x16"));
        Assert.Equal(reference.StateHash(), pair.Guest.Battle.StateHash());
    }

    [Fact]
    public void OnlyTheSideNeedingReplacementCanSubmitAndInvalidTargetsDoNotMutate()
    {
        var pair = new Pair();
        pair.Both(battle => Faint(battle.Active(1)));
        pair.PassTurn();
        Assert.Equal(NetState.Waiting, pair.Host.State);
        Assert.Equal(NetState.Replace, pair.Guest.State);
        AssertInvalidWithoutMutation(pair.Host, pair.HostTransport, () => pair.Host.SubmitReplace(1));
        AssertInvalidWithoutMutation(pair.Guest, pair.GuestTransport, () => pair.Guest.SubmitReplace(-1));
        AssertInvalidWithoutMutation(pair.Guest, pair.GuestTransport, () => pair.Guest.SubmitReplace(0));
        AssertInvalidWithoutMutation(pair.Guest, pair.GuestTransport, () => pair.Guest.SubmitReplace(6));
        pair.Guest.SubmitReplace(1);
        pair.Pump();
        Assert.Equal(NetState.Choose, pair.Host.State);
        Assert.Equal(NetState.Choose, pair.Guest.State);
        Assert.Equal(pair.Host.Battle.StateHash(), pair.Guest.Battle.StateHash());
        Assert.Equal(1, pair.Host.Battle.Record.Events.Last().Side);
    }

    [Fact]
    public void TerminalBattleIsNotOverUntilItsFinalHashHasBeenChecked()
    {
        var pair = new Pair();
        pair.Both(battle => battle.Turn = Battle.TurnCap);
        pair.HostTransport.HoldHashes = true;
        pair.GuestTransport.HoldHashes = true;
        pair.PassTurn();
        Assert.Equal(Phase.Over, pair.Host.Battle.Phase);
        Assert.Equal(Phase.Over, pair.Guest.Battle.Phase);
        Assert.Equal(NetState.Waiting, pair.Host.State);
        Assert.Equal(NetState.Waiting, pair.Guest.State);
        pair.HostTransport.ReleaseAllHashes();
        pair.GuestTransport.ReleaseAllHashes();
        pair.Pump(12);
        Assert.Equal(NetState.Over, pair.Host.State);
        Assert.Equal(NetState.Over, pair.Guest.State);
        Assert.Equal(pair.Host.Battle.StateHash(), pair.Guest.Battle.StateHash());
        AssertInvalidWithoutMutation(pair.Host, pair.HostTransport, () => pair.Host.Submit(BattleAction.Pass()));
    }

    [Fact]
    public void PeerAlreadyOverStillReceivesTheOtherPeersFinalHashError()
    {
        var pair = new Pair();
        pair.Both(battle => battle.Turn = Battle.TurnCap);
        pair.HostTransport.HoldHashes = true;
        pair.PassTurn();
        Assert.Equal(NetState.Over, pair.Host.State);
        Assert.Equal(NetState.Waiting, pair.Guest.State);
        string correct = pair.Host.Battle.StateHash().ToString("x16");
        string tampered = (correct[0] == '0' ? "1" : "0") + correct.Substring(1);

        // 一端已确认终局，不代表另一端也收到了相同状态，迟到报错仍须可见。
        pair.HostTransport.ReleaseHash(message => message.Replace(correct, tampered));
        pair.Guest.Poll();
        Assert.Equal(NetState.Desync, pair.Guest.State);
        Assert.Contains(correct, pair.Guest.Error);
        Assert.Contains(tampered, pair.Guest.Error);
        Assert.Single(pair.GuestTransport.Messages("error"));
        pair.Host.Poll();
        Assert.Equal(NetState.Error, pair.Host.State);
        Assert.Contains(pair.Guest.Error, pair.Host.Error);
        Assert.Equal(pair.Host.Battle.StateHash(), pair.Guest.Battle.StateHash());
        Assert.Single(pair.Host.Battle.Record.Events);
        Assert.Single(pair.Guest.Battle.Record.Events);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RngConsumedWhileWaitingForHashesCannotLeakIntoTheNextPhase(bool terminal)
    {
        var pair = new Pair();
        if (terminal) pair.Both(battle => battle.Turn = Battle.TurnCap);
        pair.HostTransport.HoldHashes = true;
        pair.GuestTransport.HoldHashes = true;
        pair.PassTurn();
        ulong settled = pair.Host.Battle.StateHash();
        Assert.Equal(settled, pair.Guest.Battle.StateHash());
        for (int index = 0; index < 5; index++) pair.Host.Battle.Random();
        pair.Guest.Battle.Random();
        Assert.NotEqual(settled, pair.Host.Battle.StateHash());
        Assert.NotEqual(settled, pair.Guest.Battle.StateHash());

        pair.GuestTransport.ReleaseAllHashes();
        pair.Host.Poll();
        Assert.Equal(terminal ? NetState.Over : NetState.Choose, pair.Host.State);
        Assert.Equal(settled, pair.Host.Battle.StateHash());
        Assert.Equal(NetState.Waiting, pair.Guest.State);
        pair.HostTransport.ReleaseAllHashes();
        pair.Guest.Poll();
        Assert.Equal(terminal ? NetState.Over : NetState.Choose, pair.Guest.State);
        Assert.Equal(settled, pair.Guest.Battle.StateHash());
    }

    [Fact]
    public void InvalidOrDuplicateLocalSubmissionsDoNotSendOrMutate()
    {
        var pair = new Pair(handshake: false);
        Assert.Throws<InvalidOperationException>(() => pair.Host.Submit(BattleAction.Pass()));
        Assert.Throws<InvalidOperationException>(() => pair.Guest.SubmitReplace(1));
        Assert.Single(pair.HostTransport.Sent);
        Assert.Single(pair.GuestTransport.Sent);
        pair.Pump();
        AssertInvalidWithoutMutation(pair.Host, pair.HostTransport, () => pair.Host.SubmitReplace(1));
        foreach (var action in new[]
        {
            BattleAction.Skill(-1), BattleAction.Skill(5), BattleAction.SwitchTo(6),
            new BattleAction { Type = (ActionType)99 },
            new BattleAction { Type = ActionType.Struggle },
            new BattleAction { Type = ActionType.PassControlled }
        })
            AssertInvalidWithoutMutation(pair.Host, pair.HostTransport, () => pair.Host.Submit(action));

        pair.Host.Submit(BattleAction.Pass());
        AssertInvalidWithoutMutation(pair.Host, pair.HostTransport, () => pair.Host.Submit(BattleAction.Pass()));
        pair.Guest.Submit(BattleAction.Pass());
        pair.Pump();
        Assert.Equal(NetState.Choose, pair.Host.State);
        Assert.Equal(NetState.Choose, pair.Guest.State);
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("{\"type\":\"unknown\"}")]
    [InlineData("{\"type\":\"action\",\"turn\":0,\"action\":\"P\"}")]
    [InlineData("{\"type\":\"action\",\"turn\":999,\"action\":\"P\"}")]
    [InlineData("{\"type\":\"action\",\"turn\":1,\"action\":\"S5\"}")]
    [InlineData("{\"type\":\"replace\",\"turn\":1,\"to\":1}")]
    public void InvalidRemoteMessagesStopBothPeersWithoutResolving(string message)
    {
        var pair = new Pair();
        ulong before = pair.Host.Battle.StateHash();
        pair.HostTransport.Inject(message);
        pair.Pump();
        Assert.Equal(NetState.Error, pair.Host.State);
        Assert.Equal(NetState.Error, pair.Guest.State);
        Assert.False(string.IsNullOrWhiteSpace(pair.Host.Error));
        Assert.False(string.IsNullOrWhiteSpace(pair.Guest.Error));
        Assert.Equal(before, pair.Host.Battle.StateHash());
        Assert.Empty(pair.Host.Battle.Record.Events);
        int sent = pair.HostTransport.Sent.Count + pair.GuestTransport.Sent.Count;
        pair.Pump(12);
        Assert.Equal(sent, pair.HostTransport.Sent.Count + pair.GuestTransport.Sent.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DuplicateHelloOrCommitIsRejected(bool hello)
    {
        var pair = new Pair();
        string message = hello ? pair.GuestTransport.Messages("hello").Single() :
            "{\"type\":\"commit\",\"turn\":1,\"kind\":\"action\",\"digest\":\"" + new string('0', 64) + "\"}";
        pair.HostTransport.Inject(message);
        if (!hello) pair.HostTransport.Inject(message);
        pair.Pump();
        Assert.Equal(NetState.Error, pair.Host.State);
        Assert.Equal(NetState.Error, pair.Guest.State);
        Assert.Empty(pair.Host.Battle.Record.Events);
    }

    [Fact]
    public void WrongProtocolVersionReportsAnErrorDuringHandshake()
    {
        var pair = new Pair(handshake: false);
        Assert.True(pair.HostTransport.TryReceive(out var guestHello));
        using var json = JsonDocument.Parse(guestHello);
        var hello = json.RootElement;
        pair.HostTransport.Inject(JsonSerializer.Serialize(new
        {
            type = "hello", version = NetSession.ProtocolVersion + 1, packId = hello.GetProperty("packId").GetString(),
            contentHash = hello.GetProperty("contentHash").GetString(),
            seed = (uint?)null, name = "客机", team = GuestTeam
        }));
        pair.Pump();
        Assert.Equal(NetState.Error, pair.Host.State);
        Assert.Equal(NetState.Error, pair.Guest.State);
        Assert.Null(pair.Host.Battle);
        Assert.Contains("版本不一致", pair.Host.Error);
    }

    [Fact]
    public void ChoosingWithDifferentLocalRngConsumptionCannotChangeTheSharedResolution()
    {
        var pair = new Pair();
        var reference = Reference();
        for (int index = 0; index < 17; index++) pair.Host.Battle.Random();
        for (int index = 0; index < 3; index++) pair.Guest.Battle.Random();
        Assert.NotEqual(pair.Host.Battle.StateHash(), pair.Guest.Battle.StateHash());
        pair.Host.Submit(BattleAction.Skill(0));
        pair.Guest.Submit(BattleAction.Skill(0));
        pair.Pump();
        reference.ResolveTurn(BattleAction.Skill(0), BattleAction.Skill(0));
        Assert.Equal(reference.StateHash(), pair.Host.Battle.StateHash());
        Assert.Equal(reference.StateHash(), pair.Guest.Battle.StateHash());
        Assert.Equal(reference.Record.ToJson(), pair.Host.Battle.Record.ToJson());
        Assert.Equal(LogText(reference), LogText(pair.Guest.Battle));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LegalDerivedActionsTravelAsPassAndKeepTheirBattleMeaning(bool controlled)
    {
        var pair = new Pair();
        var reference = Reference();
        Action<Battle> setup = battle =>
        {
            if (controlled) battle.Active(0).Statuses.Add(new StatusInst { Id = "stall", Left = 1 });
            else foreach (var skill in battle.Active(0).Skills) skill.Pp = 0;
        };
        pair.Both(setup);
        setup(reference);
        var derived = controlled ? new BattleAction { Type = ActionType.PassControlled } : Ai.Choose(pair.Host.Battle, 0);
        Assert.Equal(controlled ? ActionType.PassControlled : ActionType.Struggle, derived.Type);
        pair.Host.Submit(derived);
        pair.Guest.Submit(BattleAction.Pass());
        pair.Pump();
        reference.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        using var message = JsonDocument.Parse(pair.HostTransport.Messages("reveal").Single());
        Assert.Single(pair.HostTransport.Messages("commit"));
        Assert.Equal("P", message.RootElement.GetProperty("action").GetString());
        Assert.Equal(reference.StateHash(), pair.Host.Battle.StateHash());
        Assert.Equal(reference.StateHash(), pair.Guest.Battle.StateHash());
        Assert.Equal(LogText(reference), LogText(pair.Host.Battle));
        if (controlled) Assert.Contains(pair.Host.Battle.Log, entry => entry.Text.Contains("无法行动"));
        else Assert.Contains(pair.Host.Battle.Log, entry => entry.Kind == "act" && entry.Side == 0);
    }

    static void AssertInvalidWithoutMutation(NetSession session, Transport transport, Action call)
    {
        ulong hash = session.Battle.StateHash();
        string record = session.Battle.Record.ToJson();
        string log = LogText(session.Battle);
        int sent = transport.Sent.Count;
        NetState state = session.State;
        bool submitted = session.HasSubmitted;
        Assert.Throws<InvalidOperationException>(call);
        Assert.Equal(hash, session.Battle.StateHash());
        Assert.Equal(record, session.Battle.Record.ToJson());
        Assert.Equal(log, LogText(session.Battle));
        Assert.Equal(sent, transport.Sent.Count);
        Assert.Equal(state, session.State);
        Assert.Equal(submitted, session.HasSubmitted);
    }

    static Battle Reference()
    {
        return new Battle(BattleTests.LoadPack("original"), new IList<string>[] { HostTeam, GuestTeam }, new[] { "主机", "客机" }, NetSeedTestData.Seed);
    }

    static void FaintActives(Battle battle) { Faint(battle.Active(0)); Faint(battle.Active(1)); }
    static void Faint(Fighter fighter) { fighter.Fainted = true; fighter.Hp = 0; }
    static string LogText(Battle battle) { return string.Join("\n", battle.Log.Select(entry => entry.Turn + ":" + entry.Node + ":" + entry.Side + ":" + entry.Kind + ":" + entry.Text)); }

    static string TypeOf(string message)
    {
        using var json = JsonDocument.Parse(message);
        return json.RootElement.GetProperty("type").GetString();
    }

    static void AssertHash(string message, int turn, string expected)
    {
        using var json = JsonDocument.Parse(message);
        Assert.Equal(turn, json.RootElement.GetProperty("turn").GetInt32());
        string hash = json.RootElement.GetProperty("hash").GetString();
        Assert.Matches("^[0-9a-f]{16}$", hash);
        Assert.Equal(expected, hash);
    }
}
