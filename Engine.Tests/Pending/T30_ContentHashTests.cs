// T-30 验收测试：内容指纹与带版本的对战记录。实现后把本文件移到 Engine.Tests/ 下。
using System;
using System.Linq;
using StreamDuel.Core;
using Xunit;

public class T30_ContentHashTests
{
    [Fact]
    public void HashIsStableAndSensitive()
    {
        var a = BattleTests.LoadPack("original");
        var b = BattleTests.LoadPack("original");
        Assert.Equal(a.ContentHash(), b.ContentHash());
        Assert.Equal(16, a.ContentHash().Length);
        Assert.NotEqual(a.ContentHash(), BattleTests.LoadPack("fan_asoul_vr").ContentHash());
        b.Skills["warm_up"].Power += 1;
        Assert.NotEqual(a.ContentHash(), b.ContentHash());
    }

    static Battle Play(ContentDb db)
    {
        var ids = db.Streamers.Select(s => s.Id).ToArray();
        var b = new Battle(db, new System.Collections.Generic.IList<string>[] { ids.Take(6).ToList(), ids.Skip(6).Take(6).ToList() }, new[] { "A", "B" }, 3);
        for (int i = 0; i < 10 && b.Phase != Phase.Over; i++)
        {
            if (b.Phase == Phase.Choose) b.ResolveTurn(Ai.Choose(b, 0), Ai.Choose(b, 1));
            else for (int s = 0; s < 2; s++) if (b.Need[s]) b.Replace(s, Ai.ChooseReplacement(b, s));
        }
        return b;
    }

    [Fact]
    public void RecordCarriesVersionAndHash()
    {
        var db = BattleTests.LoadPack("original");
        var json = Play(db).Record.ToJson();
        Assert.Contains("\"version\":2", json);
        var r = BattleRecord.FromJson(json);
        Assert.Equal(2, r.Version);
        Assert.Equal(db.ContentHash(), r.ContentHash);
    }

    [Fact]
    public void ReplayWithChangedContentIsRefused()
    {
        var db = BattleTests.LoadPack("original");
        var r = BattleRecord.FromJson(Play(db).Record.ToJson());
        var changed = BattleTests.LoadPack("original");
        changed.Skills["warm_up"].Power += 1;
        var ex = Assert.Throws<InvalidOperationException>(() => Replay.Run(changed, r));
        Assert.Contains("内容", ex.Message);
    }

    [Fact]
    public void OldRecordWithoutVersionStillReplays()
    {
        var db = BattleTests.LoadPack("original");
        var json = Play(db).Record.ToJson();
        // 去掉 version 和 contentHash，模拟旧版记录
        var old = System.Text.RegularExpressions.Regex.Replace(json, ",\"(version|contentHash)\":(\"[^\"]*\"|[0-9]+)", "");
        var r = BattleRecord.FromJson(old);
        Assert.Equal(1, r.Version);
        var again = Replay.Run(db, r);
        Assert.True(again.Log.Count > 0);
    }
}
