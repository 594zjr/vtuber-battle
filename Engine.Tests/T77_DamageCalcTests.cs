using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using StreamDuel.Core;
using Xunit;

public class T77_DamageCalcTests
{
    static readonly string[] TeamA = { "bell", "enka", "shiori", "lumi", "hane", "momo" };
    static readonly string[] TeamB = { "kei", "yuan", "nyako", "buck", "kasumi", "bee" };

    [Theory]
    [InlineData("bell", "kei")]
    [InlineData("momo", "shiori")]
    [InlineData("nyako", "lumi")]
    [InlineData("bee", "kasumi")]
    public void RangeMatchesEveryFieldOfTheSameOneOnOneBattle(string attacker, string defender)
    {
        var db = BattleTests.LoadPack("original");
        var battle = OneOnOne(db, attacker, defender);
        for (int i = 0; i < battle.Active(0).Skills.Count; i++)
            AssertPreview(battle.PreviewDamage(0, i), DamageCalc.Range(db, attacker, i, defender));
    }

    [Fact]
    public void RangeStatusSkillHasZeroDamage()
    {
        var db = BattleTests.LoadPack("original");
        int index = db.Streamer("bell").Skills.IndexOf("go_live_early");
        var preview = DamageCalc.Range(db, "bell", index, "kei");
        Assert.Equal(0, preview.Min);
        Assert.Equal(0, preview.Max);
    }

    [Fact]
    public void RangeAppliesBothStageMapsAndPlusTwoRaisesDamage()
    {
        var db = BattleTests.LoadPack("original");
        int index = db.Streamer("bell").Skills.IndexOf("full_volume");
        var normal = DamageCalc.Range(db, "bell", index, "kei");
        var attackStages = new Dictionary<string, int> { { "spa", 2 }, { "spd", -6 } };
        var defenseStages = new Dictionary<string, int> { { "sdef", -2 }, { "atk", 6 } };
        var enhanced = DamageCalc.Range(db, "bell", index, "kei", attackStages);
        Assert.True(enhanced.Min > normal.Min);
        Assert.True(enhanced.Max > normal.Max);

        var battle = OneOnOne(db, "bell", "kei");
        foreach (var stage in attackStages) battle.Active(0).Stages[stage.Key] = stage.Value;
        foreach (var stage in defenseStages) battle.Active(1).Stages[stage.Key] = stage.Value;
        AssertPreview(battle.PreviewDamage(0, index),
            DamageCalc.Range(db, "bell", index, "kei", attackStages, defenseStages));
    }

    [Fact]
    public void MatchupIsSixBySixAndMatchesRealLeadChoices()
    {
        var db = BattleTests.LoadPack("original");
        var actual = DamageCalc.Matchup(db, TeamA, TeamB);
        Assert.Equal(6, actual.GetLength(0));
        Assert.Equal(6, actual.GetLength(1));
        for (int i = 0; i < 6; i++)
        {
            for (int j = 0; j < 6; j++)
            {
                var battle = WithLeads(db, TeamA, TeamB, i, j);
                Assert.True(actual[i, j] >= 0);
                Assert.Equal(BestPercentage(battle), actual[i, j]);
            }
        }
    }

    [Fact]
    public void MatchupRoundsHalfUpIncludesFifthSkillAndDoesNotCapAtOneHundred()
    {
        var db = ControlledContent();
        var preview = DamageCalc.Range(db, TeamA[0], 4, TeamB[0]);
        Assert.Equal(22, preview.Min);
        Assert.Equal(27, preview.Max);
        Assert.Equal(25, DamageCalc.Matchup(db, TeamA, TeamB)[0, 0]);

        db.Streamer(TeamB[0]).Stats["hp"] = 10;
        Assert.Equal(245, DamageCalc.Matchup(db, TeamA, TeamB)[0, 0]);
    }

    [Fact]
    public void MatchupSkipsUnavailableSkillsAndDoesNotCountStruggle()
    {
        var db = ControlledContent();
        db.Skills["calc_middle"].Pp = 0;
        db.Skills["calc_strong"].Pp = 0;
        Assert.Equal(11, DamageCalc.Matchup(db, TeamA, TeamB)[0, 0]);

        db.Skills["calc_weak"].Pp = 0;
        db.Skills["calc_status"].Pp = 0;
        var battle = WithLeads(db, TeamA, TeamB, 0, 0);
        Assert.True(battle.LegalFor(0).Struggle);
        foreach (int value in DamageCalc.Matchup(db, TeamA, TeamB)) Assert.Equal(0, value);
    }

    [Fact]
    public void MatchupMovesSixthMemberToLeadAndHonorsFreePpPersona()
    {
        var db = ControlledContent();
        foreach (var skill in db.Streamer(TeamA[5]).Skills) db.Skills[skill].Pp = 0;
        db.Skills[db.Streamer(TeamA[5]).Fifth].Pp = 0;
        db.Streamer(TeamA[5]).Persona = "teamspirit";

        var alone = OneOnOne(db, TeamA[5], TeamB[0]);
        Assert.False(alone.LegalFor(0).FreePp);
        Assert.All(alone.LegalFor(0).Skills, option => Assert.False(option.Usable));

        var battle = WithLeads(db, TeamA, TeamB, 5, 0);
        Assert.Equal(TeamA[5], battle.Active(0).Def.Id);
        Assert.Equal(1, battle.Active(0).Slot);
        Assert.Equal(TeamA[4], battle.Sides[0].Team[5].Def.Id);
        Assert.True(battle.LegalFor(0).FreePp);
        var matrix = DamageCalc.Matchup(db, TeamA, TeamB);
        Assert.Equal(BestPercentage(battle), matrix[5, 0]);
        Assert.True(matrix[5, 0] > 0);
        Assert.Equal(0, matrix[0, 0]);
    }

    [Fact]
    public void MatchupRetainsDefendersBenchGuardWhileRangeHasNoBench()
    {
        var db = ControlledContent();
        db.Streamer(TeamB[3]).Persona = "family";
        var single = DamageCalc.Range(db, TeamA[0], 4, TeamB[3]);
        Assert.Equal(22, single.Min);
        Assert.Equal(27, single.Max);

        var battle = WithLeads(db, TeamA, TeamB, 0, 3);
        var matrix = DamageCalc.Matchup(db, TeamA, TeamB);
        Assert.Equal(BestPercentage(battle), matrix[0, 3]);
        Assert.Equal(19, matrix[0, 3]);
        Assert.True(matrix[0, 3] < matrix[0, 0]);
    }

    [Fact]
    public void CallsAreDeterministicAndLeaveInputsContentAndExternalBattleUntouched()
    {
        var db = BattleTests.LoadPack("original");
        var teamA = TeamA.ToList();
        var teamB = TeamB.ToList();
        var attackValues = new Dictionary<string, int> { { "spa", 2 }, { "spd", -1 } };
        var defenseValues = new Dictionary<string, int> { { "sdef", 1 } };
        var attackStages = new ReadOnlyDictionary<string, int>(attackValues);
        var defenseStages = new ReadOnlyDictionary<string, int>(defenseValues);
        var external = WithLeads(db, teamA, teamB, 1, 2);
        var untouched = external.Clone();
        ulong hash = external.StateHash();
        string content = db.ContentHash();
        string record = external.Record.ToJson();
        string[] logs = external.Log.Select(line => line.Node + ":" + line.Text).ToArray();

        var first = DamageCalc.Range(db, "bell", 1, "kei", attackStages, defenseStages);
        var firstMatrix = DamageCalc.Matchup(db, teamA, teamB);
        var second = DamageCalc.Range(db, "bell", 1, "kei", attackStages, defenseStages);
        var secondMatrix = DamageCalc.Matchup(db, teamA, teamB);
        AssertPreview(first, second);
        Assert.Equal(firstMatrix.Cast<int>(), secondMatrix.Cast<int>());
        first.Notes.Add("调用方修改自己的结果");
        Assert.DoesNotContain("调用方修改自己的结果", second.Notes);
        Assert.Equal(TeamA, teamA);
        Assert.Equal(TeamB, teamB);
        Assert.Equal(new[] { "spa", "spd" }, attackValues.Keys);
        Assert.Equal(new[] { 2, -1 }, attackValues.Values);
        Assert.Equal(1, defenseValues["sdef"]);
        Assert.Single(defenseValues);
        Assert.Equal(content, db.ContentHash());
        Assert.Equal(hash, external.StateHash());
        Assert.Equal(record, external.Record.ToJson());
        Assert.Equal(logs, external.Log.Select(line => line.Node + ":" + line.Text));
        for (int i = 0; i < 5; i++) Assert.Equal(untouched.Random(), external.Random());
    }

    [Fact]
    public void InvalidRangeArgumentsFailWithoutChangingContent()
    {
        var db = BattleTests.LoadPack("original");
        string content = db.ContentHash();
        Assert.Throws<ArgumentNullException>(() => DamageCalc.Range(null, "bell", 0, "kei"));
        Assert.Throws<KeyError>(() => DamageCalc.Range(db, "missing", 0, "kei"));
        Assert.Throws<KeyError>(() => DamageCalc.Range(db, "bell", 0, "missing"));
        Assert.Throws<ArgumentOutOfRangeException>(() => DamageCalc.Range(db, "bell", -1, "kei"));
        Assert.Throws<ArgumentOutOfRangeException>(() => DamageCalc.Range(db, "bell", 5, "kei"));
        Assert.ThrowsAny<ArgumentException>(() => DamageCalc.Range(db, "bell", 0, "kei",
            new Dictionary<string, int> { { "hp", 1 } }));
        Assert.ThrowsAny<ArgumentException>(() => DamageCalc.Range(db, "bell", 0, "kei",
            new Dictionary<string, int> { { "atk", 7 } }));
        Assert.ThrowsAny<ArgumentException>(() => DamageCalc.Range(db, "bell", 0, "kei", null,
            new Dictionary<string, int> { { "def", -7 } }));
        Assert.Equal(content, db.ContentHash());
    }

    [Fact]
    public void InvalidMatchupTeamsAreRejectedButMirrorTeamsAreAllowed()
    {
        var db = BattleTests.LoadPack("original");
        Assert.Throws<ArgumentNullException>(() => DamageCalc.Matchup(null, TeamA, TeamB));
        Assert.ThrowsAny<ArgumentException>(() => DamageCalc.Matchup(db, null, TeamB));
        Assert.ThrowsAny<ArgumentException>(() => DamageCalc.Matchup(db, TeamA, null));
        Assert.ThrowsAny<ArgumentException>(() => DamageCalc.Matchup(db, TeamA.Take(5).ToList(), TeamB));
        Assert.ThrowsAny<ArgumentException>(() => DamageCalc.Matchup(db, TeamA, TeamB.Concat(new[] { "bell" }).ToList()));
        var duplicate = TeamA.ToArray();
        duplicate[1] = duplicate[0];
        Assert.ThrowsAny<ArgumentException>(() => DamageCalc.Matchup(db, duplicate, TeamB));
        var missing = TeamB.ToArray();
        missing[5] = "missing";
        Assert.ThrowsAny<ArgumentException>(() => DamageCalc.Matchup(db, TeamA, missing));
        var mirror = DamageCalc.Matchup(db, TeamA, TeamA);
        Assert.Equal(6, mirror.GetLength(0));
        Assert.Equal(6, mirror.GetLength(1));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, -1)]
    [InlineData(1, 0)]
    [InlineData(1, -1)]
    public void MatchupRejectsNonpositiveMaximumHp(int side, int hp)
    {
        var db = ControlledContent();
        string id = side == 0 ? TeamA[0] : TeamB[0];
        db.Streamer(id).Stats["hp"] = hp;
        var error = Assert.Throws<ArgumentException>(() => DamageCalc.Matchup(db, TeamA, TeamB));
        Assert.Contains("最大人气必须大于 0", error.Message);
    }

    static Battle OneOnOne(ContentDb db, string attacker, string defender)
    {
        return new Battle(db, new IList<string>[] { new[] { attacker }, new[] { defender } },
            new[] { "攻击方", "防守方" }, 0);
    }

    static Battle WithLeads(ContentDb db, IList<string> attackers, IList<string> defenders, int i, int j)
    {
        var battle = new Battle(db, new[] { attackers, defenders }, new[] { "攻击方", "防守方" }, 0, true);
        battle.ChooseLead(0, i);
        battle.ChooseLead(1, j);
        return battle;
    }

    static void AssertPreview(Battle.DamagePreview expected, Battle.DamagePreview actual)
    {
        Assert.Equal(expected.Min, actual.Min);
        Assert.Equal(expected.Max, actual.Max);
        Assert.Equal(expected.Eff, actual.Eff);
        Assert.Equal(expected.Notes, actual.Notes);
    }

    static int BestPercentage(Battle battle)
    {
        double best = 0;
        foreach (var skill in battle.LegalFor(0).Skills.Where(option => option.Usable))
        {
            var preview = battle.PreviewDamage(0, skill.Index);
            best = Math.Max(best, ((double)preview.Min + preview.Max) / 2);
        }
        return (int)Math.Round(best / battle.Active(1).MaxHp * 100, MidpointRounding.AwayFromZero);
    }

    static ContentDb ControlledContent()
    {
        var db = BattleTests.LoadPack("original");
        db.Chart.Clear();
        AddSkill(db, "calc_weak", 10, 5);
        AddSkill(db, "calc_middle", 15, 5);
        AddSkill(db, "calc_status", 0, 5, "status");
        AddSkill(db, "calc_empty", 80, 0);
        AddSkill(db, "calc_strong", 25, 5);
        foreach (var streamer in db.Streamers)
        {
            streamer.Type = "song";
            streamer.Type2 = null;
            streamer.Persona = "punctual";
            foreach (string stat in ContentDb.StatKeys) streamer.Stats[stat] = 100;
            streamer.Stats["hp"] = 100;
            streamer.Skills = new List<string> { "calc_weak", "calc_middle", "calc_status", "calc_empty" };
            streamer.Fifth = "calc_strong";
        }
        return db;
    }

    static void AddSkill(ContentDb db, string id, int power, int pp, string category = "talent")
    {
        db.Skills[id] = new SkillDef { Id = id, Name = "测试技能", Type = "game", Cat = category, Power = power, Pp = pp };
    }
}
