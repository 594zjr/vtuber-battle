// T-02 验收测试：对战记录与回放。实现后把本文件移到 Engine.Tests/ 下。
using System.Linq;
using StreamDuel.Core;
using Xunit;

public class T02_ReplayTests
{
    static Battle PlayOut(ContentDb db, uint seed)
    {
        var ids = db.Streamers.Select(s => s.Id).ToList();
        var b = new Battle(db, new System.Collections.Generic.IList<string>[] { ids.Take(6).ToList(), ids.Skip(6).Take(6).ToList() }, new[] { "你", "对手" }, seed);
        int guard = 0;
        while (b.Phase != Phase.Over && guard++ < 500)
        {
            if (b.Phase == Phase.Choose) b.ResolveTurn(Ai.Choose(b, 0), Ai.Choose(b, 1));
            else for (int s = 0; s < 2; s++) if (b.Need[s]) b.Replace(s, Ai.ChooseReplacement(b, s));
        }
        return b;
    }

    [Fact]
    public void RecordReplaysToIdenticalLog()
    {
        var db = BattleTests.LoadPack("original");
        var b = PlayOut(db, 99);
        BattleRecord rec = b.Record;                 // 对战过程中自动记录：种子、内容包 id、双方队伍、每回合双方行动、每次补位
        string json = rec.ToJson();                  // 只用 MiniJson 能读的格式
        var back = BattleRecord.FromJson(json);
        var replayed = Replay.Run(db, back);         // 按记录重放，不调用 Ai
        Assert.Equal(b.Log.Select(l => l.Node + l.Text), replayed.Log.Select(l => l.Node + l.Text));
        Assert.Equal(b.Winner, replayed.Winner);
    }

    [Fact]
    public void ReplayRejectsRecordFromAnotherPack()
    {
        var db = BattleTests.LoadPack("original");
        var rec = PlayOut(db, 5).Record;
        var fan = BattleTests.LoadPack("fan_holo_niji");
        Assert.ThrowsAny<System.Exception>(() => Replay.Run(fan, rec));
    }
}
