// T-24 验收测试：对战状态哈希与行动编码（为以后联网对战做准备）。实现后把本文件移到 Engine.Tests/ 下。
using System.Collections.Generic;
using System.Linq;
using StreamDuel.Core;
using Xunit;

public class T24_StateHashTests
{
    static Battle Make(uint seed)
    {
        var db = BattleTests.LoadPack("original");
        return new Battle(db, new IList<string>[] {
            new[] { "bell", "enka", "shiori", "lumi", "hane", "momo" },
            new[] { "kei", "yuan", "nyako", "buck", "kasumi", "bee" } }, new[] { "A", "B" }, seed);
    }

    static List<ulong> Run(Battle b, int turns)
    {
        var hashes = new List<ulong> { b.StateHash() };
        for (int i = 0; i < turns && b.Phase != Phase.Over; i++)
        {
            if (b.Phase == Phase.Choose) b.ResolveTurn(Ai.Choose(b, 0), Ai.Choose(b, 1));
            else for (int s = 0; s < 2; s++) if (b.Need[s]) b.Replace(s, Ai.ChooseReplacement(b, s));
            hashes.Add(b.StateHash());
        }
        return hashes;
    }

    [Fact]
    public void SameInputsGiveSameHashes()
    {
        Assert.Equal(Run(Make(4), 20), Run(Make(4), 20));
    }

    [Fact]
    public void DifferentActionChangesHash()
    {
        var a = Make(4); var b = Make(4);
        Assert.Equal(a.StateHash(), b.StateHash());
        a.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());
        b.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        Assert.NotEqual(a.StateHash(), b.StateHash());
    }

    [Fact]
    public void ReplayEndsWithTheSameHash()
    {
        var b = Make(8);
        Run(b, 200);
        var again = Replay.Run(b.Db, BattleRecord.FromJson(b.Record.ToJson()));
        Assert.Equal(b.StateHash(), again.StateHash());
    }

    [Fact]
    public void ActionsRoundTripThroughWireFormat()
    {
        foreach (var a in new[] { BattleAction.Skill(0), BattleAction.Skill(4), BattleAction.SwitchTo(5), BattleAction.Pass() })
        {
            var w = a.ToWire();
            Assert.True(w.Length <= 4);
            var back = BattleAction.FromWire(w);
            Assert.Equal(a.ToWire(), back.ToWire());
        }
        Assert.Throws<System.FormatException>(() => BattleAction.FromWire("X9"));
    }
}
