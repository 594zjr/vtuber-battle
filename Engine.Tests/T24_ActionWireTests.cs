using System;
using StreamDuel.Core;
using Xunit;

public class T24_ActionWireTests
{
    [Theory]
    [InlineData("S0", ActionType.Skill, 0)]
    [InlineData("S1", ActionType.Skill, 1)]
    [InlineData("S2", ActionType.Skill, 2)]
    [InlineData("S3", ActionType.Skill, 3)]
    [InlineData("S4", ActionType.Skill, 4)]
    [InlineData("W0", ActionType.Switch, 0)]
    [InlineData("W1", ActionType.Switch, 1)]
    [InlineData("W2", ActionType.Switch, 2)]
    [InlineData("W3", ActionType.Switch, 3)]
    [InlineData("W4", ActionType.Switch, 4)]
    [InlineData("W5", ActionType.Switch, 5)]
    [InlineData("P", ActionType.Pass, 0)]
    public void SupportedActionsUseTheExactWireFormat(string wire, ActionType type, int index)
    {
        var action = new BattleAction { Type = type, Index = index };
        Assert.Equal(wire, action.ToWire());

        var decoded = BattleAction.FromWire(wire);
        Assert.Equal(type, decoded.Type);
        Assert.Equal(index, decoded.Index);
        Assert.Equal(wire, decoded.ToWire());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("S")]
    [InlineData("W")]
    [InlineData("P0")]
    [InlineData("S5")]
    [InlineData("W6")]
    [InlineData("S-1")]
    [InlineData("W-1")]
    [InlineData("S00")]
    [InlineData("W05")]
    [InlineData("s0")]
    [InlineData("w0")]
    [InlineData("p")]
    [InlineData(" S0")]
    [InlineData("W0 ")]
    [InlineData("P\n")]
    [InlineData("S\uFF10")]
    [InlineData("W\u0661")]
    [InlineData("P\0")]
    [InlineData("X9")]
    public void InvalidWireTextThrowsFormatException(string wire)
    {
        Assert.Throws<FormatException>(() => BattleAction.FromWire(wire));
    }

    [Theory]
    [InlineData(ActionType.Skill, -1)]
    [InlineData(ActionType.Skill, 5)]
    [InlineData(ActionType.Skill, int.MinValue)]
    [InlineData(ActionType.Skill, int.MaxValue)]
    [InlineData(ActionType.Switch, -1)]
    [InlineData(ActionType.Switch, 6)]
    [InlineData(ActionType.Switch, int.MinValue)]
    [InlineData(ActionType.Switch, int.MaxValue)]
    [InlineData(ActionType.Struggle, 0)]
    [InlineData(ActionType.PassControlled, 0)]
    [InlineData((ActionType)(-1), 0)]
    [InlineData((ActionType)99, 0)]
    public void ActionsOutsideTheInputProtocolCannotBeEncoded(ActionType type, int index)
    {
        var action = new BattleAction { Type = type, Index = index };

        Assert.Throws<FormatException>(() => action.ToWire());
    }
}
