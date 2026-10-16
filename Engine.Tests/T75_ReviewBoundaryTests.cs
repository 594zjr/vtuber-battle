using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using StreamDuel.Core;
using Xunit;

internal static class T75ReviewProtocol
{
    internal static readonly string[] HostTeam = { "unit1", "unit2", "unit3", "unit4", "unit5", "unit6" };
    internal static readonly string[] GuestTeam = { "unit6", "unit5", "unit4", "unit3", "unit2", "unit1" };
    internal static byte[] HostContribution => Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
    internal static byte[] GuestContribution => Enumerable.Range(32, 32).Select(value => (byte)value).ToArray();
    internal const uint FairSeed = 0xacb9eafd;
    internal const string HostName = "主\"机\\一\n😀";

    // 沿用 T-79 固定样例的原创数据值，新增协议不改变旧内容指纹或规则状态。
    internal static ContentDb FrozenContent()
    {
        var db = new ContentDb
        {
            PackId = "golden_net_v2", PackName = "协议固定样例", PackNotice = "仅测试用原创数据",
            Struggle = new SkillDef { Id = "struggle", Name = "测试硬撑", Type = "plain", Cat = "interact", Power = 1, Pp = 1, Acc = 100 }
        };
        db.Types.Add("plain", new TypeDef { Id = "plain", Name = "测试赛道", Color = "#808080" });
        db.CatNames.Add("status", "状态");
        db.CatNames.Add("interact", "互动");
        db.Personas.Add("plain", new PersonaDef
        {
            Id = "plain", Name = "测试人设", Code = "T_LOWHP_POWER", Node = "伤害计算", Desc = "固定测试参数",
            Params = new Dictionary<string, double> { ["below"] = 0, ["mul"] = 1 }
        });
        for (int i = 0; i < 5; i++)
        {
            string id = "idle" + i.ToString(CultureInfo.InvariantCulture);
            db.Skills.Add(id, new SkillDef { Id = id, Name = "测试技能" + i, Type = "plain", Cat = "status", Pp = 9, Acc = 100 });
        }
        for (int i = 1; i <= 6; i++)
            db.Streamers.Add(new StreamerDef
            {
                Id = "unit" + i.ToString(CultureInfo.InvariantCulture), Name = "样例" + i,
                Type = "plain", Persona = "plain", Fifth = "idle4", Blurb = "原创测试单位", Org = "测试组",
                Stats = new Dictionary<string, int> { ["hp"] = 100, ["atk"] = 50, ["def"] = 50, ["spa"] = 50, ["sdef"] = 50, ["spd"] = 10 + i },
                Skills = new List<string> { "idle0", "idle1", "idle2", "idle3" }
            });
        return db;
    }

    internal sealed class Link { internal bool Broken; }

    internal sealed class Transport : INetTransport
    {
        internal readonly Queue<string> Incoming = new Queue<string>();
        internal readonly List<string> Sent = new List<string>();
        internal Transport Peer;
        internal Link Link;
        internal string CutType, DropType;
        internal bool DeliverBeforeCut;
        internal Func<string, string> Rewrite;

        public void Send(string message)
        {
            Sent.Add(message);
            if (Link.Broken) throw new IOException("验收传输已断开。");
            if (Rewrite != null) message = Rewrite(message);
            string type = PayloadType(message);
            if (type == CutType)
            {
                CutType = null;
                if (DeliverBeforeCut) Peer.Incoming.Enqueue(message);
                Link.Broken = true;
                throw new IOException("验收写入中断。");
            }
            if (type != DropType) Peer.Incoming.Enqueue(message);
        }

        public bool TryReceive(out string message)
        {
            if (Incoming.Count > 0) { message = Incoming.Dequeue(); return true; }
            if (Link.Broken) throw new IOException("验收传输已断开。");
            message = null;
            return false;
        }
    }

    internal sealed class Pair
    {
        internal Transport A, B;
        internal readonly NetSession Host, Guest;

        internal Pair(bool handshake = true, bool hostTermination = false, bool guestTermination = false,
            double? turnSeconds = null, double? bankSeconds = null)
        {
            NewTransport(out A, out B);
            var db = FrozenContent();
            Host = new NetSession(db, A, true, HostTeam, HostName, 79, turnSeconds, bankSeconds,
                enableTermination: hostTermination, enableReconnect: true, seedContribution: HostContribution);
            Guest = new NetSession(db, B, false, GuestTeam, "客机", null,
                enableTermination: guestTermination, enableReconnect: true, seedContribution: GuestContribution);
            if (handshake)
            {
                Pump();
                Assert.Equal(NetState.Choose, Host.State);
                Assert.Equal(NetState.Choose, Guest.State);
            }
        }

        internal void Pump(int count = 12)
        {
            for (int i = 0; i < count; i++) { Host.Poll(); Guest.Poll(); }
        }

        internal void Act(BattleAction action)
        {
            Host.Submit(action);
            Guest.Submit(action);
            Pump();
            Healthy(Host);
            Healthy(Guest);
        }

        internal void Disconnect()
        {
            A.Link.Broken = true;
            Pump();
            Assert.True(Host.IsDisconnected && Guest.IsDisconnected);
        }

        internal void Attach(Action<Transport, Transport> configure = null)
        {
            NewTransport(out A, out B);
            configure?.Invoke(A, B);
            Host.Reconnect(A, Host.SessionId);
            Guest.Reconnect(B, Guest.SessionId);
        }

        internal void Recover(Action<Transport, Transport> configure = null)
        {
            Attach(configure);
            Pump();
            foreach (var session in new[] { Host, Guest })
            {
                Healthy(session);
                Assert.False(session.IsDisconnected || session.IsReconnecting);
            }
        }
    }

    internal static void NewTransport(out Transport a, out Transport b)
    {
        var link = new Link();
        a = new Transport { Link = link };
        b = new Transport { Link = link };
        a.Peer = b;
        b.Peer = a;
    }

    internal static string Text(string message, string key)
    {
        using var json = JsonDocument.Parse(message);
        return json.RootElement.GetProperty(key).GetString();
    }

    internal static string Payload(string message) { return Text(message, "type") == "packet" ? Text(message, "payload") : message; }
    internal static string PayloadType(string message) { return Text(Payload(message), "type"); }
    internal static Dictionary<string, object> Object(string message) { return JsonSerializer.Deserialize<Dictionary<string, object>>(message); }

    internal static string RewriteControl(string message, string type, string key, object value)
    {
        if (Text(message, "type") != type) return message;
        var fields = Object(message);
        fields[key] = value;
        return JsonSerializer.Serialize(fields);
    }

    internal static void Healthy(NetSession session)
    {
        Assert.True(session.State != NetState.Error && session.State != NetState.Desync, session.Error ?? session.State.ToString());
    }

    internal static string Log(Battle battle) { return JsonSerializer.Serialize(battle.Log, new JsonSerializerOptions { IncludeFields = true }); }
}

public class T75_ReviewBoundaryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FirstCommitWriteFailurePreservesTheExactCommitAndLockedChoice(bool delivered)
    {
        var pair = new T75ReviewProtocol.Pair();
        var interrupted = pair.A;
        interrupted.CutType = "commit";
        interrupted.DeliverBeforeCut = delivered;
        pair.Host.Submit(BattleAction.Skill(0));
        pair.Pump();
        Assert.True(pair.Host.IsDisconnected && pair.Guest.IsDisconnected);
        Assert.True(pair.Host.HasSubmitted);
        Assert.Throws<InvalidOperationException>(() => pair.Host.Submit(BattleAction.Skill(1)));
        string commit = interrupted.Sent.Single(message => T75ReviewProtocol.PayloadType(message) == "commit");

        pair.Recover();

        Assert.True(pair.Host.HasSubmitted);
        Assert.Throws<InvalidOperationException>(() => pair.Host.Submit(BattleAction.Skill(1)));
        if (!delivered)
            Assert.Equal(commit, pair.A.Sent.Single(message => T75ReviewProtocol.PayloadType(message) == "commit"));
        Assert.Empty(pair.Host.Battle.Record.Events);
        pair.Guest.Submit(BattleAction.Pass());
        pair.Pump();
        T75ReviewProtocol.Healthy(pair.Host);
        T75ReviewProtocol.Healthy(pair.Guest);
        Assert.Equal("S0", Assert.Single(pair.Host.Battle.Record.Events).A0.ToWire());
        Assert.Equal(pair.Host.Battle.StateHash(), pair.Guest.Battle.StateHash());
        Assert.Equal(pair.Host.Battle.Record.ToJson(), pair.Guest.Battle.Record.ToJson());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AlreadyProcessedJournalEntriesAreIdempotentOnlyWhenTheirPayloadIsIdentical(bool corruptDuplicate)
    {
        var pair = new T75ReviewProtocol.Pair();
        pair.Act(BattleAction.Skill(0));
        string record = pair.Guest.Battle.Record.ToJson(), log = T75ReviewProtocol.Log(pair.Guest.Battle);
        ulong hash = pair.Guest.Battle.StateHash();
        pair.Disconnect();
        pair.Attach((a, b) =>
        {
            b.Rewrite = message => T75ReviewProtocol.RewriteControl(message, "resume", "received", 0);
            if (corruptDuplicate)
                a.Rewrite = message =>
                {
                    if (T75ReviewProtocol.PayloadType(message) != "commit") return message;
                    var envelope = T75ReviewProtocol.Object(message);
                    var payload = T75ReviewProtocol.Object(T75ReviewProtocol.Payload(message));
                    payload["digest"] = new string('0', 64);
                    envelope["payload"] = JsonSerializer.Serialize(payload);
                    return JsonSerializer.Serialize(envelope);
                };
        });

        pair.Pump();

        Assert.Equal(hash, pair.Guest.Battle.StateHash());
        Assert.Equal(record, pair.Guest.Battle.Record.ToJson());
        Assert.Equal(log, T75ReviewProtocol.Log(pair.Guest.Battle));
        if (corruptDuplicate)
        {
            Assert.Equal(NetState.Error, pair.Guest.State);
            Assert.Contains("重复", pair.Guest.Error);
        }
        else
        {
            T75ReviewProtocol.Healthy(pair.Host);
            T75ReviewProtocol.Healthy(pair.Guest);
            Assert.False(pair.Host.IsDisconnected || pair.Guest.IsDisconnected);
            pair.Act(BattleAction.Pass());
            Assert.Equal(2, pair.Guest.Battle.Record.Events.Count);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingReplayPacketOrSkippedSequenceCannotBeHiddenByResumed(bool skipSequence)
    {
        var pair = new T75ReviewProtocol.Pair();
        pair.A.CutType = "reveal";
        pair.Host.Submit(BattleAction.Pass());
        pair.Guest.Submit(BattleAction.Pass());
        pair.Pump();
        Assert.True(pair.Host.IsDisconnected && pair.Guest.IsDisconnected);
        string record = pair.Guest.Battle.Record.ToJson();
        pair.Attach((a, b) =>
        {
            if (!skipSequence) a.DropType = "reveal";
            else a.Rewrite = message =>
            {
                if (T75ReviewProtocol.PayloadType(message) != "reveal") return message;
                var envelope = T75ReviewProtocol.Object(message);
                envelope["sequence"] = 3;
                return JsonSerializer.Serialize(envelope);
            };
        });

        pair.Pump();

        Assert.Equal(NetState.Error, pair.Guest.State);
        Assert.Equal(record, pair.Guest.Battle.Record.ToJson());
        Assert.Empty(pair.Guest.Battle.Record.Events);
        Assert.Contains(skipSequence ? "序号" : "缺失消息", pair.Guest.Error);
    }

    [Fact]
    public void AQueuedResumeAtExactlySixtySecondsCannotReopenTheExpiredSession()
    {
        var pair = new T75ReviewProtocol.Pair();
        pair.Disconnect();
        pair.Host.Poll(59.75);
        pair.Guest.Poll(59.75);
        string before = pair.Host.Battle.Record.ToJson();
        pair.Attach();
        Assert.NotEmpty(pair.A.Incoming);

        pair.Host.Poll(0.25);

        Assert.Equal(NetState.Error, pair.Host.State);
        Assert.Equal(0, pair.Host.ReconnectSecondsRemaining);
        Assert.Equal(before, pair.Host.Battle.Record.ToJson());
        T75ReviewProtocol.NewTransport(out var candidate, out _);
        Assert.Throws<InvalidOperationException>(() => pair.Host.Reconnect(candidate, pair.Host.SessionId));
    }

    [Fact]
    public void LostGoodbyeCanResumeWithoutResolvingOrAppendingTheFinalTurnAgain()
    {
        var pair = new T75ReviewProtocol.Pair();
        pair.Host.Battle.Turn = Battle.TurnCap;
        pair.Guest.Battle.Turn = Battle.TurnCap;
        pair.Act(BattleAction.Pass());
        Assert.Equal(NetState.Over, pair.Host.State);
        string record = pair.Host.Battle.Record.ToJson(), log = T75ReviewProtocol.Log(pair.Host.Battle);
        pair.A.CutType = "goodbye";
        pair.Host.PrepareClose();
        pair.Pump();
        Assert.True(pair.Host.IsDisconnected && pair.Guest.IsDisconnected);

        pair.Recover();

        Assert.Equal(NetState.Over, pair.Host.State);
        Assert.Equal(NetState.Over, pair.Guest.State);
        Assert.Equal(record, pair.Host.Battle.Record.ToJson());
        Assert.Equal(record, pair.Guest.Battle.Record.ToJson());
        Assert.Equal(log, T75ReviewProtocol.Log(pair.Host.Battle));
        Assert.Equal(log, T75ReviewProtocol.Log(pair.Guest.Battle));
        Assert.Equal(pair.Host.Battle.StateHash(), pair.Guest.Battle.StateHash());
        Assert.Single(pair.Host.Battle.Events, entry => entry.Kind == BattleEventKind.End);
    }
}
