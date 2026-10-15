// T-63 验收测试：局域网房间发现（只测纯逻辑）。实现后把本文件移到 Engine.Tests/ 下。
using StreamDuel.Core;
using Xunit;

public class T63_LanDiscoveryTests
{
    static LanRoom Room(string name = "小明的房间", int port = 7777) =>
        new LanRoom { Name = name, Port = port, Protocol = NetSession.ProtocolVersion, ContentHash = "abc", Ruleset = "casual" };

    [Fact]
    public void EncodeDecodeRoundTrip()
    {
        var r = LanBeacon.Decode(LanBeacon.Encode(Room()));
        Assert.NotNull(r);
        Assert.Equal("小明的房间", r.Name);
        Assert.Equal(7777, r.Port);
        Assert.Equal("abc", r.ContentHash);
        Assert.Equal(47777, LanBeacon.Port);
    }

    [Theory]
    [InlineData("")]
    [InlineData("hello")]
    [InlineData("{\"magic\":\"OTHER\",\"name\":\"x\",\"port\":1}")]
    [InlineData("{\"magic\":\"SD-LAN-1\",\"name\":\"x\",\"port\":0}")]
    [InlineData("{\"magic\":\"SD-LAN-1\",\"name\":\"x\",\"port\":70000}")]
    [InlineData("{\"magic\":\"SD-LAN-1\",\"port\":7777}")]
    public void GarbageIsIgnored(string text)
    {
        Assert.Null(LanBeacon.Decode(text));
    }

    [Fact]
    public void LongNamesAreCutAndHugePacketsDropped()
    {
        var r = LanBeacon.Decode(LanBeacon.Encode(Room(new string('房', 40))));
        Assert.Equal(24, r.Name.Length);
        Assert.Null(LanBeacon.Decode("{\"magic\":\"SD-LAN-1\",\"name\":\"" + new string('x', 600) + "\",\"port\":7777}"));
    }

    [Fact]
    public void ListDedupesUsesSenderAddressAndExpires()
    {
        var list = new LanRoomList();
        var text = LanBeacon.Encode(Room());
        list.Receive(text, "192.168.1.5", 0);
        list.Receive(text, "192.168.1.5", 1);
        list.Receive(LanBeacon.Encode(Room("另一个", 7778)), "192.168.1.6", 1);
        list.Receive("junk", "192.168.1.7", 1);
        var rooms = list.Rooms(1.5);
        Assert.Equal(2, rooms.Count);
        Assert.Contains(rooms, x => x.Address == "192.168.1.5" && x.Port == 7777);
        list.Receive(text, "192.168.1.5", 2);
        Assert.Single(list.Rooms(4.49));
        Assert.Empty(list.Rooms(10));
    }
}
