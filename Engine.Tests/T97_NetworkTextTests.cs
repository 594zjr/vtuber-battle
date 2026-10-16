using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using StreamDuel.Core;
using Xunit;

public class T97_NetworkTextTests
{
    static readonly string[] HostTeam = { "bell", "enka", "shiori", "lumi", "hane", "momo" };
    static readonly string[] GuestTeam = { "kei", "yuan", "nyako", "buck", "kasumi", "bee" };

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

    static Dictionary<string, object> Hello(ContentDb db, object name)
    {
        return new Dictionary<string, object>
        {
            ["type"] = "hello", ["version"] = NetSession.ProtocolVersion,
            ["packId"] = db.PackId, ["contentHash"] = db.ContentHash(),
            ["seed"] = null, ["name"] = name, ["team"] = GuestTeam
        };
    }

    [Fact]
    public void BothPeersDisplayCleanNamesWithoutChangingWireRecordOrHash()
    {
        var db = BattleTests.LoadPack("original");
        string hostName = "房主\u200b\n" + TextLint.DefaultBannedWords.First();
        string guestName = "客机" + string.Concat(Enumerable.Repeat("😀", 16));
        var hostTransport = new Transport();
        var guestTransport = new Transport();
        var host = new NetSession(db, hostTransport, true, HostTeam, hostName, 97);
        var guest = new NetSession(db, guestTransport, false, GuestTeam, guestName, null);
        Assert.Empty(host.NameInputs);
        Assert.Empty(host.DisplayNames);
        Assert.Throws<InvalidOperationException>(() => host.ViewFor(0));
        using (var hello = JsonDocument.Parse(Assert.Single(hostTransport.Sent)))
            Assert.Equal(hostName, hello.RootElement.GetProperty("name").GetString());
        hostTransport.Incoming.Enqueue(Assert.Single(guestTransport.Sent));
        guestTransport.Incoming.Enqueue(Assert.Single(hostTransport.Sent));
        host.Poll();
        guest.Poll();

        foreach (var session in new[] { host, guest })
        {
            Assert.Equal(NetState.Choose, session.State);
            string record = session.Battle.Record.ToJson();
            ulong hash = session.Battle.StateHash();
            string[] log = session.Battle.Log.Select(entry => entry.Text).ToArray();
            var inputs = session.NameInputs;
            var names = session.DisplayNames;
            Assert.Equal(TextInput.Clean(hostName, TextInputKind.PlayerName).Text, names[0]);
            Assert.Equal("客机" + string.Concat(Enumerable.Repeat("😀", 10)), names[1]);
            Assert.All(inputs, input => Assert.True(input.Changed));
            Assert.DoesNotContain(TextLint.DefaultBannedWords.First(), inputs[0].Message);
            Assert.Throws<NotSupportedException>(() => ((IList<string>)names)[0] = "改写");
            Assert.Throws<NotSupportedException>(() => ((IList<TextInputResult>)inputs)[0] = inputs[1]);
            var view = session.ViewFor(session.MySide);
            Assert.Equal(names, view.Sides.Select(side => side.Name));
            view.Sides[0].Name = "修改显示快照";
            Assert.Equal(new[] { hostName, guestName }, session.Battle.Sides.Select(side => side.Name));
            Assert.Equal(new[] { hostName, guestName }, session.Battle.Record.Names);
            Assert.Equal(record, session.Battle.Record.ToJson());
            Assert.Equal(hash, session.Battle.StateHash());
            Assert.Equal(log, session.Battle.Log.Select(entry => entry.Text));
        }
        Assert.Equal(host.Battle.StateHash(), guest.Battle.StateHash());
    }

    [Fact]
    public void UntrustedPeerWhitespaceNameUsesDefaultOnlyForDisplay()
    {
        var db = BattleTests.LoadPack("original");
        var transport = new Transport();
        var session = new NetSession(db, transport, true, HostTeam, "主机", 97);
        const string raw = " \u3000\u200b\n\t";
        transport.Incoming.Enqueue(JsonSerializer.Serialize(Hello(db, raw)));
        session.Poll();
        Assert.Equal(NetState.Choose, session.State);
        Assert.True(session.NameInputs[1].UsedDefault);
        Assert.Equal("玩家", session.DisplayNames[1]);
        Assert.Equal(raw, session.Battle.Sides[1].Name);
        Assert.Equal(raw, session.Battle.Record.Names[1]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(7)]
    [InlineData(true)]
    public void PeerNameWithWrongStructureStillFails(object name)
    {
        var db = BattleTests.LoadPack("original");
        var transport = new Transport();
        var session = new NetSession(db, transport, true, HostTeam, "主机", 97);
        transport.Incoming.Enqueue(JsonSerializer.Serialize(Hello(db, name)));
        session.Poll();
        Assert.Equal(NetState.Error, session.State);
        Assert.Null(session.Battle);
        Assert.Empty(session.DisplayNames);
        Assert.Contains("name", session.Error);
    }

    [Fact]
    public void MissingPeerNameStillFailsAndNullLocalNameDoesNotBecomeValid()
    {
        var db = BattleTests.LoadPack("original");
        var transport = new Transport();
        var session = new NetSession(db, transport, true, HostTeam, "主机", 97);
        var hello = Hello(db, "客机");
        hello.Remove("name");
        transport.Incoming.Enqueue(JsonSerializer.Serialize(hello));
        session.Poll();
        Assert.Equal(NetState.Error, session.State);
        Assert.Null(session.Battle);
        var invalidLocal = new NetSession(db, new Transport(), true, HostTeam, null, 97);
        Assert.Equal(NetState.Error, invalidLocal.State);
        Assert.Empty(invalidLocal.NameInputs);
    }

    static LanRoom Room(string name)
    {
        return new LanRoom
        {
            Name = name, Port = 7777, Protocol = NetSession.ProtocolVersion,
            ContentHash = "hash", Ruleset = "casual"
        };
    }

    [Fact]
    public void ReceivedRoomNameIsCleanForDisplayAndAnnouncementRemainsUnchanged()
    {
        string banned = TextLint.DefaultBannedWords.First();
        var room = Room("房\u200b  间\n" + banned);
        string encoded = LanBeacon.Encode(room);
        var received = LanBeacon.Decode(encoded);
        Assert.NotNull(received);
        string original = received.Name;
        Assert.Equal(TextInput.Clean(original, TextInputKind.RoomName).Text, received.DisplayName);
        Assert.DoesNotContain(banned, received.DisplayName);
        Assert.DoesNotContain(banned, received.NameInput.Message);
        Assert.True(received.NameInput.Changed);
        Assert.Equal(original, received.Name);
        Assert.Equal(encoded, LanBeacon.Encode(received));

        var list = new LanRoomList();
        list.Receive(encoded, "127.0.0.1", 0);
        var snapshot = Assert.Single(list.Rooms(0));
        Assert.Equal(received.DisplayName, snapshot.DisplayName);
        snapshot.Name = "显示副本";
        Assert.Equal(original, Assert.Single(list.Rooms(0)).Name);
    }

    [Fact]
    public void RoomDisplayDefaultAndUnicodeLimitDoNotChangeLegacyWireRules()
    {
        var empty = LanBeacon.Decode(LanBeacon.Encode(Room("")));
        Assert.Equal("", empty.Name);
        Assert.True(empty.NameInput.UsedDefault);
        Assert.Equal(TextInput.Clean("", TextInputKind.RoomName).Text, empty.DisplayName);
        var unicode = Room(string.Concat(Enumerable.Repeat("😀", 30)));
        Assert.Equal(string.Concat(Enumerable.Repeat("😀", 24)), unicode.DisplayName);
        Assert.Equal(60, unicode.Name.Length);
        var received = LanBeacon.Decode(LanBeacon.Encode(unicode));
        Assert.Equal(string.Concat(Enumerable.Repeat("😀", 12)), received.DisplayName);
        Assert.Equal(24, received.Name.Length);
    }
}
