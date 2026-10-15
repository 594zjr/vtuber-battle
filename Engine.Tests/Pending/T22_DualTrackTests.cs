// T-22 验收测试：双赛道。实现后把本文件移到 Engine.Tests/ 下。
using System;
using StreamDuel.Core;
using Xunit;

public class T22_DualTrackTests
{
    static Battle Make(ContentDb db)
    {
        return new Battle(db, new System.Collections.Generic.IList<string>[] {
            new[] { "bell", "enka", "shiori", "lumi", "hane", "momo" },
            new[] { "kei", "yuan", "nyako", "buck", "kasumi", "bee" } }, new[] { "你", "对手" }, 1);
    }

    [Fact]
    public void EffectivenessIsTheProductOfBothTracks()
    {
        var db = BattleTests.LoadPack("original");
        db.Streamer("kei").Type2 = "horror";
        db.Validate();
        var b = Make(db);
        int idx = b.Active(0).Skills.FindIndex(s => db.Skills[s.Id].Power > 0);
        var sk = db.Skills[b.Active(0).Skills[idx].Id];
        double want = db.TypeMul(sk.Type, "tech") * db.TypeMul(sk.Type, "horror");
        Assert.Equal(want, b.PreviewDamage(0, idx).Eff, 6);
    }

    [Fact]
    public void SameTrackBonusAppliesToSecondTrack()
    {
        var db = BattleTests.LoadPack("original");
        var b1 = Make(db);
        int idx = b1.Active(0).Skills.FindIndex(s => db.Skills[s.Id].Power > 0 && db.Skills[s.Id].Type == "song");
        // bell 本身是 song；把主赛道改掉、第二赛道设为 song，加成应保持不变
        int before = b1.PreviewDamage(0, idx).Max;
        db.Streamer("bell").Type = "art";
        db.Streamer("bell").Type2 = "song";
        var b2 = Make(db);
        Assert.Equal(before, b2.PreviewDamage(0, idx).Max);
    }

    [Fact]
    public void InvalidSecondTrackIsRejected()
    {
        var db = BattleTests.LoadPack("original");
        db.Streamer("kei").Type2 = "nope";
        Assert.Throws<KeyError>(() => db.Validate());
        db.Streamer("kei").Type2 = "tech"; // 与主赛道相同也不行
        Assert.Throws<KeyError>(() => db.Validate());
    }

    [Fact]
    public void SingleTrackBehaviourIsUnchanged()
    {
        var db = BattleTests.LoadPack("original");
        Assert.Null(db.Streamer("kei").Type2);
    }
}
