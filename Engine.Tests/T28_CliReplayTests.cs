using System.Collections.Generic;
using System.Linq;
using StreamDuel.Core;
using Xunit;

public class T28_CliReplayTests
{
    [Theory]
    [InlineData("original")]
    [InlineData("fan_asoul_vr")]
    [InlineData("fan_holo_niji")]
    public void AutoSeedOneWithBanAndLeadReplaysTheCompleteCliBattle(string packId)
    {
        var db = BattleTests.LoadPack(packId);
        var player = db.Streamers.Take(6).Select(streamer => streamer.Id).ToArray();
        var opponent = db.Streamers.Select(streamer => streamer.Id).Where(id => !player.Contains(id))
            .Concat(player).Take(6).ToArray();
        var battle = new Battle(db, new IList<string>[] { player, opponent }, new[] { "你", "电脑" },
            new BattleOptions { Seed = 1, BanOne = true, LeadPick = true });

        // AI 与结算共享随机流，必须保持 CLI 先选择再确认、甲方先于乙方的调用顺序。
        Assert.Equal(Phase.Ban, battle.Phase);
        for (int side = 0; side < 2; side++)
            battle.ChooseBan(side, Ai.ChooseBan(battle, side));
        Assert.Equal(Phase.Lead, battle.Phase);
        Assert.All(battle.Sides, side => Assert.Equal(5, side.Team.Count));
        for (int side = 0; side < 2; side++)
            battle.ChooseLead(side, Ai.ChooseLead(battle, side));
        Assert.Equal(Phase.Choose, battle.Phase);

        for (int guard = 0; battle.Phase != Phase.Over && guard < 500; guard++)
        {
            if (battle.Phase == Phase.Replace)
            {
                for (int side = 0; side < 2 && battle.Phase == Phase.Replace; side++)
                    if (battle.Need[side])
                        battle.Replace(side, Ai.ChooseReplacement(battle, side));
                continue;
            }

            Assert.Equal(Phase.Choose, battle.Phase);
            var action = Ai.Choose(battle, 0, AiLevel.Normal);
            var foeAction = Ai.Choose(battle, 1, AiLevel.Normal);
            battle.ResolveTurn(action, foeAction);
        }

        Assert.Equal(Phase.Over, battle.Phase);
        Assert.InRange(battle.Winner, -1, 1);
        string json = battle.Record.ToJson();
        var saved = BattleRecord.FromJson(json);
        Assert.Equal(json, saved.ToJson());
        Assert.Equal(1u, saved.Seed);
        Assert.Equal(packId, saved.PackId);
        Assert.True(saved.BanOne);
        Assert.True(saved.LeadPick);
        Assert.Equal(new[] { "你", "电脑" }, saved.Names);
        Assert.Equal(player, saved.Teams[0]);
        Assert.Equal(opponent, saved.Teams[1]);
        Assert.Equal(new[]
        {
            BattleRecordEventType.Ban, BattleRecordEventType.Ban,
            BattleRecordEventType.Lead, BattleRecordEventType.Lead
        }, saved.Events.Take(4).Select(entry => entry.Type));
        Assert.Equal(new[] { 0, 1, 0, 1 }, saved.Events.Take(4).Select(entry => entry.Side));
        Assert.Contains(saved.Events, entry => entry.Type == BattleRecordEventType.Turn);
        Assert.Contains(saved.Events, entry => entry.Type == BattleRecordEventType.Replace);
        Assert.All(saved.Events, entry => Assert.True(entry.RngState.HasValue));

        // 重新载入内容模拟独立的 --replay 进程，不复用对局中的可变内容对象。
        var replayed = Replay.Run(BattleTests.LoadPack(packId), saved);
        Assert.Equal(Phase.Over, replayed.Phase);
        Assert.Equal(battle.Winner, replayed.Winner);
        Assert.Equal(battle.Turn, replayed.Turn);
        Assert.Equal(battle.StateHash(), replayed.StateHash());
        Assert.Equal(json, replayed.Record.ToJson());
        AssertSameLog(battle, replayed);
        AssertSameSummary(battle, replayed);
    }

    static void AssertSameLog(Battle expected, Battle actual)
    {
        Assert.NotEmpty(expected.Log);
        Assert.Equal(expected.Log.Count, actual.Log.Count);
        for (int index = 0; index < expected.Log.Count; index++)
        {
            var left = expected.Log[index];
            var right = actual.Log[index];
            Assert.Equal(left.Turn, right.Turn);
            Assert.Equal(left.Node, right.Node);
            Assert.Equal(left.Text, right.Text);
            Assert.Equal(left.Kind, right.Kind);
            Assert.Equal(left.Side, right.Side);
            Assert.Equal(left.SnapActive, right.SnapActive);
            Assert.Equal(left.SnapHp[0], right.SnapHp[0]);
            Assert.Equal(left.SnapHp[1], right.SnapHp[1]);
        }
    }

    static void AssertSameSummary(Battle expected, Battle actual)
    {
        var left = BattleSummary.From(expected);
        var right = BattleSummary.From(actual);
        Assert.Equal(left.ToText(), right.ToText());
        for (int side = 0; side < 2; side++)
        {
            Assert.Equal(5, left.Sides[side].Count);
            Assert.Equal(left.Sides[side].Count, right.Sides[side].Count);
            Assert.Equal(expected.Sides[side].Team.Select(fighter => fighter.Def.Id),
                left.Sides[side].Select(stats => stats.StreamerId));
            for (int index = 0; index < left.Sides[side].Count; index++)
            {
                var before = left.Sides[side][index];
                var after = right.Sides[side][index];
                Assert.Equal(before.StreamerId, after.StreamerId);
                Assert.Equal(before.DamageDealt, after.DamageDealt);
                Assert.Equal(before.DamageTaken, after.DamageTaken);
                Assert.Equal(before.Kos, after.Kos);
                Assert.Equal(before.TurnsActive, after.TurnsActive);
                Assert.Equal(before.Fainted, after.Fainted);
                Assert.Equal(before.SkillUses.OrderBy(pair => pair.Key), after.SkillUses.OrderBy(pair => pair.Key));
            }
        }
    }
}
