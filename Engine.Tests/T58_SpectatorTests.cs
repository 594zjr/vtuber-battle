// T-58 验收测试：观战数据流。实现后把本文件移到 Engine.Tests/ 下。
using System.Linq;
using StreamDuel.Core;
using Xunit;

public class T58_SpectatorTests
{
    [Fact]
    public void SpectatorFollowsTheMatchWithDelay()
    {
        var db = BattleTests.LoadPack("original");
        var b = new Battle(db, new System.Collections.Generic.IList<string>[] {
            new[] { "bell", "enka", "shiori", "lumi", "hane", "momo" },
            new[] { "kei", "yuan", "nyako", "buck", "kasumi", "bee" } }, new[] { "A", "B" }, 12);
        var feed = new SpectatorFeed(b, delayTurns: 2);
        LoopbackPair.Create(out var host, out var viewerT);
        feed.AddViewer(host);
        var viewer = new SpectatorClient(db, viewerT);
        int guard = 0;
        while (b.Phase != Phase.Over && guard++ < 500)
        {
            if (b.Phase == Phase.Choose) b.ResolveTurn(Ai.Choose(b, 0), Ai.Choose(b, 1));
            else for (int s = 0; s < 2; s++) if (b.Need[s]) b.Replace(s, Ai.ChooseReplacement(b, s));
            feed.Pump();
            viewer.Poll();
            if (viewer.Battle != null && b.Phase != Phase.Over)
                Assert.True(viewer.Battle.Turn <= System.Math.Max(1, b.Turn - 2));
        }
        feed.Finish();
        viewer.Poll();
        Assert.Null(viewer.Error);
        Assert.Equal(b.StateHash(), viewer.Battle.StateHash());
    }
}
