using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using StreamDuel.Core;
using Xunit;

public class T25_HashAiIntegrationTests
{
    [Fact]
    public void EitherSidesPendingBanChoiceChangesTheHashBeforeTeamsChange()
    {
        for (int side = 0; side < 2; side++)
        {
            var first = Make(true);
            var second = Make(true);
            ulong unchosen = first.StateHash();

            first.ChooseBan(side, 0);
            second.ChooseBan(side, 1);

            Assert.Equal(Phase.Ban, first.Phase);
            Assert.Equal(Phase.Ban, second.Phase);
            Assert.All(first.Sides, state => Assert.Equal(6, state.Team.Count));
            Assert.All(second.Sides, state => Assert.Equal(6, state.Team.Count));
            Assert.NotEqual(unchosen, first.StateHash());
            Assert.NotEqual(first.StateHash(), second.StateHash());
        }
    }

    [Fact]
    public void PendingBanHashesIncludeTheFollowingLeadPhaseOption()
    {
        var withLead = Make(true);
        var withoutLead = Make(false);

        Assert.Equal(Phase.Ban, withLead.Phase);
        Assert.Equal(Phase.Ban, withoutLead.Phase);
        Assert.NotEqual(withLead.StateHash(), withoutLead.StateHash());

        withLead.ChooseBan(0, 2);
        withoutLead.ChooseBan(0, 2);
        Assert.NotEqual(withLead.StateHash(), withoutLead.StateHash());
    }

    [Fact]
    public void HardEvaluationCopyKeepsBanMetadataAndOwnsItsNames()
    {
        var battle = Make(true);
        battle.ChooseBan(0, 3);
        string bannedId = battle.Sides[1].Team[3].Def.Id;
        string before = Snapshot(battle);
        var method = typeof(Battle).GetMethod("CopyForHardAi", BindingFlags.Instance | BindingFlags.NonPublic);
        var copy = (Battle)method.Invoke(battle, null);

        Assert.True(copy.HasChosenBan(0));
        Assert.False(copy.HasChosenBan(1));
        Assert.True(copy.Record.BanOne);
        Assert.True(copy.Record.LeadPick);
        Assert.Equal(battle.Record.Names, copy.Record.Names);
        Assert.NotSame(battle.Record.Names, copy.Record.Names);
        Assert.Empty(copy.Record.Events);

        copy.Record.Names[0] = "评估副本";
        copy.ChooseBan(1, 2);

        Assert.Equal(Phase.Lead, copy.Phase);
        Assert.DoesNotContain(copy.Sides[1].Team, fighter => fighter.Def.Id == bannedId);
        Assert.Equal(before, Snapshot(battle));
        Assert.False(battle.HasChosenBan(1));
        Assert.Equal(Phase.Ban, battle.Phase);
    }

    [Fact]
    public void HardBattleAfterBanAndLeadReplaysNamesHashStatisticsAndState()
    {
        var battle = Make(true);
        for (int side = 0; side < 2; side++) battle.ChooseBan(side, Ai.ChooseBan(battle, side));
        Assert.Equal(Phase.Lead, battle.Phase);
        for (int side = 0; side < 2; side++) battle.ChooseLead(side, Ai.ChooseLead(battle, side));
        Assert.All(battle.Sides, state => Assert.Equal(5, state.Team.Count));

        int guard = 500;
        while (battle.Phase != Phase.Over && guard-- > 0)
        {
            if (battle.Phase == Phase.Choose)
            {
                string before = Snapshot(battle);
                string summaryBefore = BattleSummary.From(battle).ToText();
                var first = Ai.Choose(battle, 0, AiLevel.Hard);
                var second = Ai.Choose(battle, 1, AiLevel.Hard);
                // 平局可消耗真实随机数，但候选行动的模拟不能污染战斗和统计。
                Assert.Equal(before, Snapshot(battle));
                Assert.Equal(summaryBefore, BattleSummary.From(battle).ToText());
                battle.ResolveTurn(first, second);
            }
            else
            {
                Assert.Equal(Phase.Replace, battle.Phase);
                for (int side = 0; side < 2; side++)
                    if (battle.Need[side]) battle.Replace(side, Ai.ChooseReplacement(battle, side, AiLevel.Hard));
            }
        }

        Assert.Equal(Phase.Over, battle.Phase);
        Assert.InRange(battle.Winner, -1, 1);
        string json = battle.Record.ToJson();
        var replay = Replay.Run(battle.Db, BattleRecord.FromJson(json));

        Assert.Equal(new[] { "自定义甲方", "自定义乙方" }, replay.Record.Names);
        Assert.Equal(battle.Sides.Select(side => side.Name), replay.Sides.Select(side => side.Name));
        Assert.Equal(battle.StateHash(), replay.StateHash());
        Assert.Equal(BattleSummary.From(battle).ToText(), BattleSummary.From(replay).ToText());
        Assert.Equal(Snapshot(battle), Snapshot(replay));
        Assert.Equal(json, replay.Record.ToJson());
        for (int i = 0; i < 8; i++) Assert.Equal(battle.Random(), replay.Random());
    }

    static Battle Make(bool leadPick)
    {
        return new Battle(BattleTests.LoadPack("original"), new IList<string>[]
        {
            new[] { "bell", "enka", "shiori", "lumi", "hane", "momo" },
            new[] { "kei", "yuan", "nyako", "buck", "kasumi", "bee" }
        }, new[] { "自定义甲方", "自定义乙方" }, new BattleOptions { Seed = 25, BanOne = true, LeadPick = leadPick });
    }

    static string Snapshot(Battle battle)
    {
        return JsonSerializer.Serialize(battle, new JsonSerializerOptions { IncludeFields = true });
    }
}
