using System;
using System.Collections.Generic;
using System.Linq;
using StreamDuel.Core;
using Xunit;

public class T24_StateHashEdgeTests
{
    private static Battle Make(bool leadPick = false)
    {
        var db = BattleTests.LoadPack("original");
        return new Battle(db, new IList<string>[]
        {
            new[] { "bell", "enka", "shiori", "lumi", "hane", "momo" },
            new[] { "kei", "yuan", "nyako", "buck", "kasumi", "bee" }
        }, new[] { "甲", "乙" }, 83, leadPick);
    }

    private static Fighter Subject(Battle battle)
    {
        return battle.Sides[1].Team[5];
    }

    private static void AddCollections(Battle battle)
    {
        var fighter = Subject(battle);
        fighter.Statuses.Add(new StatusInst { Id = "hater", Left = 3 });
        fighter.Statuses.Add(new StatusInst { Id = "flame", Left = 4 });
        fighter.Fields.Add(new FieldInst { Id = "shield", Turns = 2, Stacks = 0 });
        fighter.Fields.Add(new FieldInst { Id = "hype", Turns = -1, Stacks = 2 });
    }

    [Theory]
    [InlineData("hp")]
    [InlineData("fainted")]
    [InlineData("lastSkillIndex")]
    [InlineData("skillId")]
    [InlineData("skillPp")]
    [InlineData("stageAtk")]
    [InlineData("stageDef")]
    [InlineData("stageSpa")]
    [InlineData("stageSdef")]
    [InlineData("stageSpd")]
    [InlineData("statusId")]
    [InlineData("statusLeft")]
    [InlineData("fieldId")]
    [InlineData("fieldTurns")]
    [InlineData("fieldStacks")]
    [InlineData("active0")]
    [InlineData("active1")]
    [InlineData("phase")]
    [InlineData("turn")]
    [InlineData("rng")]
    [InlineData("winner")]
    [InlineData("need0")]
    [InlineData("need1")]
    [InlineData("actedFirst0")]
    [InlineData("actedFirst1")]
    [InlineData("packId")]
    [InlineData("fighterId")]
    [InlineData("slot")]
    [InlineData("side")]
    [InlineData("maxHp")]
    [InlineData("enteredTurn")]
    [InlineData("skillMax")]
    [InlineData("skillFifth")]
    public void IndependentStateChangeChangesHash(string member)
    {
        var battle = Make();
        AddCollections(battle);
        ulong before = battle.StateHash();
        var fighter = Subject(battle);

        // 分别改动字段，避免一个已覆盖字段掩盖另一个字段的遗漏。
        switch (member)
        {
            case "hp": fighter.Hp--; break;
            case "fainted": fighter.Fainted = true; break;
            case "lastSkillIndex": fighter.LastSkillIndex = 2; break;
            case "skillId": fighter.Skills[0].Id = "warm_up"; break;
            case "skillPp": fighter.Skills[0].Pp--; break;
            case "stageAtk": fighter.Stages["atk"] = 1; break;
            case "stageDef": fighter.Stages["def"] = 1; break;
            case "stageSpa": fighter.Stages["spa"] = 1; break;
            case "stageSdef": fighter.Stages["sdef"] = 1; break;
            case "stageSpd": fighter.Stages["spd"] = 1; break;
            case "statusId": fighter.Statuses[0].Id = "siphon"; break;
            case "statusLeft": fighter.Statuses[0].Left--; break;
            case "fieldId": fighter.Fields[0].Id = "filter"; break;
            case "fieldTurns": fighter.Fields[0].Turns--; break;
            case "fieldStacks": fighter.Fields[1].Stacks++; break;
            case "active0": battle.Sides[0].Active = 1; break;
            case "active1": battle.Sides[1].Active = 1; break;
            case "phase": battle.Phase = Phase.Replace; break;
            case "turn": battle.Turn++; break;
            case "rng": battle.Random(); break;
            case "winner": battle.Winner = 0; break;
            case "need0": battle.Need[0] = true; break;
            case "need1": battle.Need[1] = true; break;
            case "actedFirst0": battle.ActedFirst[0] = true; break;
            case "actedFirst1": battle.ActedFirst[1] = true; break;
            case "packId": battle.Db.PackId = "original_other"; break;
            case "fighterId": fighter.Def.Id = "bee_other"; break;
            case "slot": fighter.Slot = 1; break;
            case "side": fighter.Side = 0; break;
            case "maxHp": fighter.MaxHp++; break;
            case "enteredTurn": fighter.EnteredTurn++; break;
            case "skillMax": fighter.Skills[0].Max++; break;
            case "skillFifth": fighter.Skills[0].Fifth = true; break;
            default: throw new ArgumentException("未知状态项：" + member);
        }

        Assert.NotEqual(before, battle.StateHash());
    }

    [Fact]
    public void BothTeamsAndEveryBenchSlotAreIncluded()
    {
        var battle = Make();
        ulong before = battle.StateHash();
        foreach (var side in battle.Sides)
        {
            foreach (var fighter in side.Team)
            {
                fighter.Hp--;
                Assert.NotEqual(before, battle.StateHash());
                fighter.Hp++;
                Assert.Equal(before, battle.StateHash());
            }
        }
    }

    [Fact]
    public void StageDictionaryInsertionOrderDoesNotChangeHash()
    {
        var first = Make();
        var second = Make();
        for (int i = 0; i < ContentDb.StatKeys.Length; i++)
            Subject(first).Stages[ContentDb.StatKeys[i]] = i - 2;
        Subject(second).Stages = new Dictionary<string, int>();
        foreach (string key in ContentDb.StatKeys.Reverse())
            Subject(second).Stages.Add(key, Subject(first).Stages[key]);

        Assert.Equal(first.StateHash(), second.StateHash());
    }

    [Theory]
    [InlineData("statuses")]
    [InlineData("fields")]
    [InlineData("skills")]
    [InlineData("team")]
    public void OrderedCollectionsKeepTheirMeaningfulOrder(string collection)
    {
        var battle = Make();
        AddCollections(battle);
        ulong before = battle.StateHash();
        switch (collection)
        {
            case "statuses": Subject(battle).Statuses.Reverse(); break;
            case "fields": Subject(battle).Fields.Reverse(); break;
            case "skills": Subject(battle).Skills.Reverse(); break;
            case "team": battle.Sides[1].Team.Reverse(); break;
        }

        Assert.NotEqual(before, battle.StateHash());
    }

    [Fact]
    public void HashDoesNotMutateCollectionsHistoryOrRandomState()
    {
        var battle = Make();
        var untouched = Make();
        AddCollections(battle);
        AddCollections(untouched);
        var fighter = Subject(battle);
        fighter.Stages = fighter.Stages.Reverse().ToDictionary(pair => pair.Key, pair => pair.Value);
        string[] stageOrder = fighter.Stages.Keys.ToArray();
        var statuses = fighter.Statuses.ToArray();
        var fields = fighter.Fields.ToArray();
        var skills = fighter.Skills.ToArray();
        var team = battle.Sides[1].Team.ToArray();
        string record = battle.Record.ToJson();
        string[] log = battle.Log.Select(entry => entry.Node + ":" + entry.Text).ToArray();
        int hp = fighter.Hp;
        int turn = battle.Turn;
        Phase phase = battle.Phase;

        ulong first = battle.StateHash();
        Assert.Equal(first, battle.StateHash());
        Assert.Equal(first, untouched.StateHash());
        Assert.Equal(stageOrder, fighter.Stages.Keys.ToArray());
        Assert.Equal(statuses, fighter.Statuses.ToArray());
        Assert.Equal(fields, fighter.Fields.ToArray());
        Assert.Equal(skills, fighter.Skills.ToArray());
        Assert.Equal(team, battle.Sides[1].Team.ToArray());
        Assert.Equal(record, battle.Record.ToJson());
        Assert.Equal(log, battle.Log.Select(entry => entry.Node + ":" + entry.Text).ToArray());
        Assert.Equal(hp, fighter.Hp);
        Assert.Equal(turn, battle.Turn);
        Assert.Equal(phase, battle.Phase);
        for (int i = 0; i < 5; i++) Assert.Equal(untouched.Random(), battle.Random());
    }

    [Theory]
    [InlineData("skill1", 2, "skill", 12)]
    [InlineData("技|1", 2, "技|", 12)]
    [InlineData("台\0后", 2, "台", 2)]
    [InlineData("台😀", 2, "台😃", 2)]
    public void StringAndNumberBoundariesDoNotMerge(string firstId, int firstPp, string secondId, int secondPp)
    {
        var first = Make();
        var second = Make();
        Subject(first).Skills[0].Id = firstId;
        Subject(first).Skills[0].Pp = firstPp;
        Subject(second).Skills[0].Id = secondId;
        Subject(second).Skills[0].Pp = secondPp;

        Assert.NotEqual(first.StateHash(), second.StateHash());
    }

    [Fact]
    public void AdjacentCollectionBoundariesRemainDistinct()
    {
        var first = Make();
        var second = Make();
        // 空标识和零值用于隔离集合边界，防止把状态和效果直接拼成同一段字节。
        for (int i = 0; i < 3; i++)
            Subject(first).Statuses.Add(new StatusInst { Id = "", Left = 0 });
        for (int i = 0; i < 2; i++)
            Subject(second).Fields.Add(new FieldInst { Id = "", Turns = 0, Stacks = 0 });

        Assert.NotEqual(first.StateHash(), second.StateHash());
    }

    [Fact]
    public void PendingLeadConfirmationAndChosenIndexAreIncluded()
    {
        var unchosen = Make(true);
        var firstChoice = Make(true);
        var otherChoice = Make(true);
        firstChoice.ChooseLead(0, 1);
        otherChoice.ChooseLead(0, 2);

        Assert.Equal(Phase.Lead, firstChoice.Phase);
        Assert.Equal(unchosen.Sides[0].Active, firstChoice.Sides[0].Active);
        Assert.Equal(unchosen.Sides[0].Team.Select(fighter => fighter.Def.Id),
            firstChoice.Sides[0].Team.Select(fighter => fighter.Def.Id));
        Assert.NotEqual(unchosen.StateHash(), firstChoice.StateHash());
        Assert.NotEqual(firstChoice.StateHash(), otherChoice.StateHash());
    }

    [Fact]
    public void PresentationAndHistoricalLogsDoNotChangeCurrentStateHash()
    {
        var battle = Make();
        ulong before = battle.StateHash();
        battle.Sides[0].Name = "改名后的显示文字";
        Subject(battle).Def.Name = "显示名";
        battle.Log.Clear();
        battle.Record.Events.Add(new BattleRecordEvent
        {
            Type = BattleRecordEventType.Turn,
            A0 = BattleAction.Pass(),
            A1 = BattleAction.Pass()
        });

        Assert.Equal(before, battle.StateHash());
    }
}
