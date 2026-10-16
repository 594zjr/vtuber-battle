using System;
using StreamDuel.Core;
using Xunit;

public class T128_LanRulesetTests
{
    private static LanRoom Room()
    {
        return new LanRoom
        {
            Name = "同场对决", Port = 7777, Protocol = 5, ContentHash = "abcdef",
            Ruleset = "custom", RulesetName = "自定义三局", BestOf = 3
        };
    }

    [Fact]
    public void ActualRulesetAndBestOfSurviveBroadcastAndIndependentRoomSnapshots()
    {
        var source = Room();
        string json = LanBeacon.Encode(source);
        var decoded = LanBeacon.Decode(json);
        Assert.NotNull(decoded);
        Assert.Equal("custom", decoded.Ruleset);
        Assert.Equal("自定义三局", decoded.RulesetName);
        Assert.Equal(3, decoded.BestOf);

        var list = new LanRoomList();
        list.Receive(json, "127.0.0.1", 0);
        var shown = Assert.Single(list.Rooms(1));
        shown.RulesetName = "被改动";
        shown.BestOf = 5;
        var again = Assert.Single(list.Rooms(2));
        Assert.Equal("自定义三局", again.RulesetName);
        Assert.Equal(3, again.BestOf);
        Assert.Equal("127.0.0.1", again.Address);
    }

    [Fact]
    public void LegacyBeaconKeepsOriginalBytesAndNoInferredGameCount()
    {
        var source = new LanRoom
        {
            Name = "room", Port = 7777, Protocol = 3, ContentHash = "abc", Ruleset = "casual"
        };
        string expected = "{\"magic\":\"SD-LAN-1\",\"name\":\"room\",\"port\":7777,\"protocol\":3,\"contentHash\":\"abc\",\"ruleset\":\"casual\"}";
        Assert.Equal(expected, LanBeacon.Encode(source));
        var decoded = LanBeacon.Decode(expected);
        Assert.NotNull(decoded);
        Assert.Null(decoded.RulesetName);
        Assert.Equal(0, decoded.BestOf);
    }

    [Theory]
    [InlineData("\"rulesetName\":\"三局\"")]
    [InlineData("\"bestOf\":3")]
    [InlineData("\"rulesetName\":null,\"bestOf\":3")]
    [InlineData("\"rulesetName\":\" \",\"bestOf\":3")]
    [InlineData("\"rulesetName\":\"三局\",\"bestOf\":2")]
    [InlineData("\"rulesetName\":\"三局\",\"bestOf\":\"3\"")]
    [InlineData("\"rulesetName\":\"三局\",\"bestOf\":3,\"bestOf\":5")]
    public void IncompleteAmbiguousOrInvalidDisplayMetadataIsRejected(string fields)
    {
        string json = "{\"magic\":\"SD-LAN-1\",\"name\":\"room\",\"port\":7777,\"protocol\":5,\"contentHash\":\"abc\",\"ruleset\":\"custom\"," + fields + "}";
        Assert.Null(LanBeacon.Decode(json));
    }

    [Fact]
    public void MetadataCannotBypassPacketSizeOrUtf8Validation()
    {
        var source = Room();
        source.RulesetName = new string('界', 200);
        Assert.Null(LanBeacon.Encode(source));
        source.RulesetName = "坏\ud800";
        Assert.Null(LanBeacon.Encode(source));
        source.RulesetName = "三局";
        source.BestOf = 2;
        Assert.Null(LanBeacon.Encode(source));
        source.RulesetName = null;
        source.BestOf = 3;
        Assert.Null(LanBeacon.Encode(source));
    }
}
