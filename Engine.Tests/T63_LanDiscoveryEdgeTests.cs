using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using StreamDuel.Core;
using Xunit;

public class T63_LanDiscoveryEdgeTests
{
    static LanRoom Room(string name = "测试房间", int port = 7777)
    {
        return new LanRoom
        {
            Name = name,
            Port = port,
            Protocol = NetSession.ProtocolVersion,
            ContentHash = "hash",
            Ruleset = "casual"
        };
    }

    static Dictionary<string, object> Fields()
    {
        return new Dictionary<string, object>
        {
            ["magic"] = "SD-LAN-1",
            ["name"] = "房间",
            ["port"] = 7777,
            ["protocol"] = NetSession.ProtocolVersion,
            ["contentHash"] = "hash",
            ["ruleset"] = "casual"
        };
    }

    [Fact]
    public void RoundTripPreservesEscapedTextAndDoesNotTransmitLocalMetadata()
    {
        var room = Room("房间\"\\\n😀");
        room.Address = "198.51.100.9";
        room.LastSeen = 123;
        string text = LanBeacon.Encode(room);
        var decoded = LanBeacon.Decode(text);
        Assert.NotNull(decoded);
        Assert.Equal(room.Name, decoded.Name);
        Assert.Equal(room.Port, decoded.Port);
        Assert.Equal(room.Protocol, decoded.Protocol);
        Assert.Equal(room.ContentHash, decoded.ContentHash);
        Assert.Equal(room.Ruleset, decoded.Ruleset);
        Assert.Null(decoded.Address);
        Assert.Equal(0, decoded.LastSeen);
        using var json = JsonDocument.Parse(text);
        Assert.False(json.RootElement.TryGetProperty("address", out _));
        Assert.False(json.RootElement.TryGetProperty("lastSeen", out _));
        Assert.Equal("198.51.100.9", room.Address);
        Assert.Equal(123, room.LastSeen);
    }

    [Theory]
    [InlineData("magic")]
    [InlineData("name")]
    [InlineData("port")]
    [InlineData("protocol")]
    [InlineData("contentHash")]
    [InlineData("ruleset")]
    public void EveryRequiredFieldMustExistAndHaveTheRightType(string key)
    {
        var fields = Fields();
        fields.Remove(key);
        Assert.Null(LanBeacon.Decode(JsonSerializer.Serialize(fields)));
        fields = Fields();
        fields[key] = null;
        Assert.Null(LanBeacon.Decode(JsonSerializer.Serialize(fields)));
        fields[key] = key == "port" || key == "protocol" ? (object)"7777" : 7777;
        Assert.Null(LanBeacon.Decode(JsonSerializer.Serialize(fields)));
    }

    [Theory]
    [InlineData("port", 0)]
    [InlineData("port", -1)]
    [InlineData("port", 65536)]
    [InlineData("protocol", 0)]
    [InlineData("protocol", -1)]
    public void InvalidIntegerRangesAreRejectedForEncodingAndDecoding(string key, int value)
    {
        var fields = Fields();
        fields[key] = value;
        Assert.Null(LanBeacon.Decode(JsonSerializer.Serialize(fields)));
        var room = Room();
        if (key == "port") room.Port = value;
        else room.Protocol = value;
        Assert.Null(LanBeacon.Encode(room));
    }

    [Theory]
    [InlineData("port")]
    [InlineData("protocol")]
    public void IntegerFieldsRejectFractionalBooleanAndOverflowValues(string key)
    {
        var fields = Fields();
        foreach (object value in new object[] { 1.5, true, new object[0], new Dictionary<string, object>(), 1e30 })
        {
            fields[key] = value;
            Assert.Null(LanBeacon.Decode(JsonSerializer.Serialize(fields)));
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(65535)]
    public void PortBoundariesAndUnknownPositiveProtocolRemainDiscoverable(int port)
    {
        var room = Room(port: port);
        room.Protocol = int.MaxValue;
        var decoded = LanBeacon.Decode(LanBeacon.Encode(room));
        Assert.NotNull(decoded);
        Assert.Equal(port, decoded.Port);
        Assert.Equal(int.MaxValue, decoded.Protocol);
    }

    [Fact]
    public void NullEncodeArgumentsAreRejectedWithoutThrowing()
    {
        Assert.Null(LanBeacon.Encode(null));
        var room = Room();
        room.Name = null;
        Assert.Null(LanBeacon.Encode(room));
        room = Room();
        room.ContentHash = null;
        Assert.Null(LanBeacon.Encode(room));
        room = Room();
        room.Ruleset = null;
        Assert.Null(LanBeacon.Encode(room));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{")]
    [InlineData("{\"magic\":\"SD-LAN-1\",}")]
    [InlineData("{\"magic\":\"SD-LAN-1\"} trailing")]
    [InlineData("{\"magic\":\"SD-LAN-1\",\"port\":01}")]
    [InlineData("{\"magic\":\"SD-LAN-1\",\"port\":NaN}")]
    [InlineData("{\"magic\":\"SD-LAN-1\",\"port\":1e999}")]
    [InlineData("{\"name\":\"\\x\"}")]
    [InlineData("{\"name\":\"\\u123\"}")]
    public void MalformedPacketsAreIgnoredWithoutExceptions(string text)
    {
        Assert.Null(LanBeacon.Decode(text));
    }

    [Fact]
    public void DuplicateKeysIncludingEscapedAndNestedKeysAreRejected()
    {
        string text = LanBeacon.Encode(Room());
        Assert.Null(LanBeacon.Decode(text.Insert(1, "\"port\":1,")));
        Assert.Null(LanBeacon.Decode(text.Insert(1, "\"content\\u0048ash\":\"other\",")));
        Assert.Null(LanBeacon.Decode(text.Insert(1, "\"extra\":{\"a\":1,\"a\":2},")));
        Assert.NotNull(LanBeacon.Decode(text.Insert(1, "\"extra\":[{\"a\":1},{\"a\":2}],")));
    }

    [Fact]
    public void InvalidUnicodeInKnownOrUnknownFieldsIsIgnored()
    {
        string text = LanBeacon.Encode(Room());
        Assert.Null(LanBeacon.Decode(text.Insert(1, "\"extra\":\"\\ud800\",")));
        Assert.Null(LanBeacon.Decode(text.Insert(1, "\"\\udc00\":1,")));
        Assert.Null(LanBeacon.Decode(text.Replace("hash", "\\ud800")));
        Assert.Null(LanBeacon.Decode(text.Insert(1, "\"extra\":\"\ud800\",")));
        var room = Room("\ud800");
        Assert.Null(LanBeacon.Encode(room));
        room = Room();
        room.ContentHash = "\udc00";
        Assert.Null(LanBeacon.Encode(room));
    }

    [Fact]
    public void NamesAreTruncatedWithoutMutatingInputsOrSplittingSurrogatePairs()
    {
        var room = Room(new string('房', 40));
        Assert.Equal(new string('房', 24), LanBeacon.Decode(LanBeacon.Encode(room)).Name);
        Assert.Equal(40, room.Name.Length);
        var fields = Fields();
        fields["name"] = room.Name;
        Assert.Equal(new string('房', 24), LanBeacon.Decode(JsonSerializer.Serialize(fields)).Name);
        room.Name = new string('a', 23) + "😀";
        Assert.Equal(new string('a', 23), LanBeacon.Decode(LanBeacon.Encode(room)).Name);
        room.Name = new string('a', 22) + "😀x";
        Assert.Equal(new string('a', 22) + "😀", LanBeacon.Decode(LanBeacon.Encode(room)).Name);
    }

    [Fact]
    public void PacketLimitIsExactly512Utf8BytesIncludingEscapes()
    {
        var room = Room("房");
        room.ContentHash = "";
        string baseline = LanBeacon.Encode(room);
        int padding = 512 - Encoding.UTF8.GetByteCount(baseline);
        room.ContentHash = new string('x', padding);
        string exact = LanBeacon.Encode(room);
        Assert.NotNull(exact);
        Assert.Equal(512, Encoding.UTF8.GetByteCount(exact));
        Assert.NotNull(LanBeacon.Decode(exact));
        Assert.Null(LanBeacon.Decode(exact + " "));
        room.ContentHash += "x";
        Assert.Null(LanBeacon.Encode(room));
    }

    [Fact]
    public void MultibytePacketsAreLimitedBeforeDecodedNamesCanBeTruncated()
    {
        string text = LanBeacon.Encode(Room("x"));
        string oversized = text.Replace("\"name\":\"x\"", "\"name\":\"" + new string('房', 170) + "\"");
        Assert.True(oversized.Length < 512);
        Assert.True(Encoding.UTF8.GetByteCount(oversized) > 512);
        Assert.Null(LanBeacon.Decode(oversized));
        var room = Room();
        room.ContentHash = new string('房', 200);
        Assert.Null(LanBeacon.Encode(room));
    }

    [Fact]
    public void EmptyStringFieldsArePresentRatherThanMissing()
    {
        var room = Room("");
        room.ContentHash = room.Ruleset = "";
        var decoded = LanBeacon.Decode(LanBeacon.Encode(room));
        Assert.NotNull(decoded);
        Assert.Equal("", decoded.Name);
        Assert.Equal("", decoded.ContentHash);
        Assert.Equal("", decoded.Ruleset);
    }

    [Fact]
    public void PacketSuppliedAddressAndLastSeenCannotOverrideTheReceiver()
    {
        var fields = Fields();
        fields["address"] = "203.0.113.99";
        fields["lastSeen"] = 999999;
        string text = JsonSerializer.Serialize(fields);
        Assert.Null(LanBeacon.Decode(text).Address);
        Assert.Equal(0, LanBeacon.Decode(text).LastSeen);
        var list = new LanRoomList();
        list.Receive(text, "192.168.1.5", 2);
        var room = Assert.Single(list.Rooms(2));
        Assert.Equal("192.168.1.5", room.Address);
        Assert.Equal(2, room.LastSeen);
        Assert.Empty(list.Rooms(5.01));
    }

    [Fact]
    public void SameEndpointUpdatesEveryAnnouncementFieldAndSnapshotsRemainIndependent()
    {
        var list = new LanRoomList();
        list.Receive(LanBeacon.Encode(Room("旧房名")), "192.168.1.5", 0);
        var first = Assert.Single(list.Rooms(0));
        var replacement = Room("新房名");
        replacement.Protocol++;
        replacement.ContentHash = "newhash";
        replacement.Ruleset = "ranked";
        list.Receive(LanBeacon.Encode(replacement), "192.168.1.5", 1);
        var snapshot = list.Rooms(1);
        var updated = Assert.Single(snapshot);
        Assert.Equal("旧房名", first.Name);
        Assert.Equal(0, first.LastSeen);
        Assert.Equal("新房名", updated.Name);
        Assert.Equal(replacement.Protocol, updated.Protocol);
        Assert.Equal("newhash", updated.ContentHash);
        Assert.Equal("ranked", updated.Ruleset);
        Assert.Equal(1, updated.LastSeen);
        updated.Name = updated.Address = updated.ContentHash = updated.Ruleset = "mutated";
        updated.Port = updated.Protocol = 0;
        updated.LastSeen = double.PositiveInfinity;
        snapshot.Clear();
        var actual = Assert.Single(list.Rooms(1));
        Assert.Equal("新房名", actual.Name);
        Assert.Equal("192.168.1.5", actual.Address);
        Assert.Equal("newhash", actual.ContentHash);
        Assert.Equal("ranked", actual.Ruleset);
        Assert.Equal(7777, actual.Port);
        Assert.Equal(replacement.Protocol, actual.Protocol);
        Assert.Equal(1, actual.LastSeen);
        Assert.Empty(list.Rooms(4.01));
    }

    [Fact]
    public void EndpointIdentityIncludesBothNormalizedAddressAndPort()
    {
        var list = new LanRoomList();
        string text = LanBeacon.Encode(Room());
        list.Receive(text, "2001:0db8:0:0:0:0:0:1", 0);
        list.Receive(text, "2001:db8::1", 1);
        list.Receive(text, "192.168.1.5", 1);
        list.Receive(text, "::ffff:192.168.1.5", 2);
        list.Receive(LanBeacon.Encode(Room(port: 7778)), "192.168.1.5", 2);
        list.Receive(text, "192.168.1.6", 2);
        var rooms = list.Rooms(2);
        Assert.Equal(4, rooms.Count);
        Assert.Contains(rooms, room => room.Address == "2001:db8::1" && room.LastSeen == 1);
        Assert.Contains(rooms, room => room.Address == "192.168.1.5" && room.Port == 7777 && room.LastSeen == 2);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-an-address")]
    [InlineData("localhost")]
    [InlineData("192.168.1.5:7777")]
    public void InvalidSenderAddressesAreIgnored(string address)
    {
        var list = new LanRoomList();
        list.Receive(LanBeacon.Encode(Room()), address, 0);
        Assert.Empty(list.Rooms(0));
    }

    [Fact]
    public void ExactThreeSecondBoundaryIsKeptAndBadPacketsDoNotRefreshIt()
    {
        var list = new LanRoomList();
        list.Receive(LanBeacon.Encode(Room()), "192.168.1.5", 0);
        list.Receive("bad packet", "192.168.1.5", 2.9);
        var room = Assert.Single(list.Rooms(3));
        Assert.Equal(0, room.LastSeen);
        Assert.Empty(list.Rooms(3.000001));
        list.Receive(LanBeacon.Encode(Room("重新开房")), "192.168.1.5", 4);
        Assert.Equal("重新开房", Assert.Single(list.Rooms(4)).Name);
    }

    [Theory]
    [InlineData(-1.0)]
    [InlineData(-double.Epsilon)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void InvalidTimeIsRejectedWithoutChangingTheList(double now)
    {
        var list = new LanRoomList();
        list.Receive(LanBeacon.Encode(Room()), "192.168.1.5", 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => list.Receive("bad packet", "192.168.1.5", now));
        Assert.Throws<ArgumentOutOfRangeException>(() => list.Rooms(now));
        Assert.Equal(1, Assert.Single(list.Rooms(1)).LastSeen);
    }

    [Fact]
    public void TimeCannotMoveBackwardsToExtendOrResurrectRooms()
    {
        var list = new LanRoomList();
        string text = LanBeacon.Encode(Room());
        list.Receive(text, "192.168.1.5", 5);
        Assert.Throws<ArgumentOutOfRangeException>(() => list.Receive(text, "192.168.1.5", 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => list.Rooms(4));
        Assert.Single(list.Rooms(8));
        Assert.Empty(list.Rooms(9));
        Assert.Throws<ArgumentOutOfRangeException>(() => list.Rooms(5));
        Assert.Empty(list.Rooms(double.MaxValue));
    }
}
