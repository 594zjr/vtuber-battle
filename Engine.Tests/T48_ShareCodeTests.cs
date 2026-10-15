// T-48 验收测试：回放分享码。实现后把本文件移到 Engine.Tests/ 下。
using System;
using System.Linq;
using StreamDuel.Core;
using Xunit;

public class T48_ShareCodeTests
{
    static Battle Play(uint seed)
    {
        var db = BattleTests.LoadPack("original");
        var ids = db.Streamers.Select(s => s.Id).ToArray();
        var b = new Battle(db, new System.Collections.Generic.IList<string>[] { ids.Take(6).ToList(), ids.Skip(6).Take(6).ToList() }, new[] { "A", "B" },
            new BattleOptions { Seed = seed, BanOne = true, LeadPick = true });
        b.ChooseBan(0, Ai.ChooseBan(b, 0)); b.ChooseBan(1, Ai.ChooseBan(b, 1));
        b.ChooseLead(0, Ai.ChooseLead(b, 0)); b.ChooseLead(1, Ai.ChooseLead(b, 1));
        int guard = 0;
        while (b.Phase != Phase.Over && guard++ < 500)
        {
            if (b.Phase == Phase.Choose) b.ResolveTurn(Ai.Choose(b, 0), Ai.Choose(b, 1));
            else for (int s = 0; s < 2; s++) if (b.Need[s]) b.Replace(s, Ai.ChooseReplacement(b, s));
        }
        return b;
    }

    [Fact]
    public void RoundTripsAndStaysShort()
    {
        for (uint seed = 1; seed <= 10; seed++)
        {
            var b = Play(seed);
            var code = ShareCode.Encode(b.Record);
            Assert.StartsWith("SD1-", code);
            Assert.True(code.Length < 1500, "分享码太长：" + code.Length);
            Assert.Matches("^SD1-[A-Za-z0-9_-]+$", code);
            var r = ShareCode.Decode(code);
            Assert.Equal(b.StateHash(), Replay.Run(b.Db, r).StateHash());
        }
    }

    [Fact]
    public void CorruptedCodeIsRejected()
    {
        var code = ShareCode.Encode(Play(3).Record);
        var broken = code.Substring(0, code.Length - 3) + (code.EndsWith("A") ? "BBB" : "AAA");
        Assert.Throws<FormatException>(() => ShareCode.Decode(broken));
        Assert.Throws<FormatException>(() => ShareCode.Decode("SD9-abc"));
        Assert.Throws<FormatException>(() => ShareCode.Decode(""));
    }

    [Fact]
    public void WhitespaceAndLineBreaksAreIgnored()
    {
        var b = Play(5);
        var code = ShareCode.Encode(b.Record);
        var messy = "  " + code.Substring(0, 20) + "\n" + code.Substring(20) + " \r\n";
        Assert.Equal(b.Record.ToJson(), ShareCode.Decode(messy).ToJson());
    }
}
