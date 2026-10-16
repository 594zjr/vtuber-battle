using System;
using System.Collections.Generic;
using StreamDuel.Core;
using Xunit;

public class T98_RecordShapeReviewTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void VersionThreeRejectsSurrenderInsideAnOrdinaryTurn(bool left, bool right)
    {
        var db = BattleTests.LoadPack("original");
        var battle = new Battle(db, new IList<string>[]
        {
            new[] { "bell", "enka", "shiori", "lumi", "hane", "momo" },
            new[] { "kei", "yuan", "nyako", "buck", "kasumi", "bee" }
        }, new[] { "甲方", "乙方" }, 98);
        var record = battle.Record;
        record.Version = 3;
        string emptyJson = record.ToJson();
        Assert.Contains("\"events\":[]", emptyJson);
        string eventJson = "{\"type\":\"turn\",\"a0\":{" + ActionJson(left)
            + "},\"a1\":{" + ActionJson(right) + "}}";
        string json = emptyJson.Replace("\"events\":[]", "\"events\":[" + eventJson + "]");
        record.Events.Add(new BattleRecordEvent
        {
            Type = BattleRecordEventType.Turn,
            A0 = left ? BattleAction.Surrender() : BattleAction.Pass(),
            A1 = right ? BattleAction.Surrender() : BattleAction.Pass()
        });

        Assert.False(Replay.Check(db, record).CanReplay);
        Assert.Throws<FormatException>(() => record.ToJson());
        Assert.Throws<FormatException>(() => BattleRecord.FromJson(json));
        Assert.Throws<FormatException>(() => Replay.Run(db, record));
        Assert.Throws<InvalidOperationException>(() => Replay.RunBestEffort(db, record));
    }

    static string ActionJson(bool surrender)
    {
        return "\"t\":\"" + (surrender ? "Surrender" : "Pass") + "\",\"i\":0";
    }
}
