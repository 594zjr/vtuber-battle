using System;
using System.Collections.Generic;
using System.Linq;
using StreamDuel.Core;
using Xunit;

public class T54_RulesetEdgeTests
{
    static readonly string[] A = { "bell", "enka", "shiori", "lumi", "hane", "momo" };
    static readonly string[] B = { "yuan", "nyako", "buck", "kasumi", "bee", "enka" };

    static IList<string>[] Teams()
    {
        return new IList<string>[] { A.ToList(), B.ToList() };
    }

    static Ruleset Rule(int bestOf = 3)
    {
        return new Ruleset { Id = "edge", Name = "测试赛制", BestOf = bestOf, Modifiers = new List<string>() };
    }

    static Match Make(int bestOf = 3, uint seed = 54)
    {
        return new Match(BattleTests.LoadPack("original"), Rule(bestOf), Teams(), new[] { "甲方", "乙方" }, seed);
    }

    static void Finish(Match match, Battle battle, int winner)
    {
        // 这里验证多局管理，不重复模拟已由引擎测试覆盖的胜负结算。
        battle.Phase = Phase.Over;
        battle.Winner = winner;
        match.ReportResult(battle);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("true")]
    [InlineData("{} trailing")]
    [InlineData("{\"banOne\":1}")]
    [InlineData("{\"leadPick\":\"true\"}")]
    [InlineData("{\"bestOf\":1.5}")]
    [InlineData("{\"bestOf\":2147483648}")]
    [InlineData("{\"bestOf\":\"3\"}")]
    [InlineData("{\"turnSeconds\":0.25}")]
    [InlineData("{\"bankSeconds\":1e400}")]
    [InlineData("{\"name\":3}")]
    [InlineData("{\"season\":[]}")]
    [InlineData("{\"modifiers\":\"half_pp\"}")]
    [InlineData("{\"modifiers\":[3]}")]
    public void JsonRejectsInvalidShapesTypesAndNonIntegerNumbers(string json)
    {
        Assert.Throws<FormatException>(() => Ruleset.FromJson(json));
    }

    [Fact]
    public void JsonDefaultsAreOptionalAndAllConfigurationFieldsRoundTrip()
    {
        var defaults = Ruleset.FromJson("{}");
        Assert.Equal(1, defaults.BestOf);
        Assert.False(defaults.BanOne);
        Assert.False(defaults.LeadPick);
        Assert.Equal(0, defaults.TurnSeconds);
        Assert.Equal(0, defaults.BankSeconds);
        Assert.Null(defaults.Id);
        Assert.Null(defaults.Name);
        Assert.Null(defaults.Season);
        defaults.Validate(BattleTests.LoadPack("original"));
        var optionalNulls = Ruleset.FromJson("{\"id\":null,\"name\":null,\"season\":null,\"modifiers\":null}");
        optionalNulls.Validate(BattleTests.LoadPack("original"));
        Assert.True(optionalNulls.Modifiers == null || optionalNulls.Modifiers.Count == 0);

        var original = new Ruleset
        {
            Id = "custom", Name = "测试\n\"赛制\"\\", Season = "s1", BanOne = true, LeadPick = true, BestOf = 5,
            Modifiers = new List<string> { "hype_start", "half_pp" }, TurnSeconds = 17, BankSeconds = 125
        };
        var restored = Ruleset.FromJson(original.ToJson());

        Assert.Equal(original.Id, restored.Id);
        Assert.Equal(original.Name, restored.Name);
        Assert.Equal(original.Season, restored.Season);
        Assert.Equal(original.BanOne, restored.BanOne);
        Assert.Equal(original.LeadPick, restored.LeadPick);
        Assert.Equal(original.BestOf, restored.BestOf);
        Assert.Equal(original.Modifiers, restored.Modifiers);
        Assert.Equal(original.TurnSeconds, restored.TurnSeconds);
        Assert.Equal(original.BankSeconds, restored.BankSeconds);
        restored.Modifiers.Clear();
        Assert.Equal(2, original.Modifiers.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(6)]
    public void ValidationRejectsUnsupportedBestOfValues(int bestOf)
    {
        Assert.Throws<KeyError>(() => Rule(bestOf).Validate(BattleTests.LoadPack("original")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("HALF_PP")]
    [InlineData("missing_modifier")]
    public void ValidationRejectsUnknownOrNullModifierIds(string modifier)
    {
        var rule = Rule();
        rule.Modifiers.Add(modifier);

        Assert.Throws<KeyError>(() => rule.Validate(BattleTests.LoadPack("original")));
    }

    [Fact]
    public void ValidationChecksSeasonAndClocksWithoutRewritingTheCallerConfiguration()
    {
        var db = BattleTests.LoadPack("original");
        var rule = Rule();
        rule.Season = "missing_season";
        Assert.Contains("missing_season", Assert.Throws<KeyError>(() => rule.Validate(db)).Message);
        rule.Season = "s1";
        rule.TurnSeconds = -1;
        Assert.Throws<KeyError>(() => rule.Validate(db));
        rule.TurnSeconds = 0;
        rule.BankSeconds = -1;
        Assert.Throws<KeyError>(() => rule.Validate(db));
        rule.BankSeconds = int.MaxValue;
        var modifiers = new List<string> { "hype_start", "half_pp", "half_pp" };
        rule.Modifiers = modifiers;
        string before = rule.ToJson();

        rule.Validate(db);

        Assert.Same(modifiers, rule.Modifiers);
        Assert.Equal(new[] { "hype_start", "half_pp", "half_pp" }, modifiers);
        Assert.Equal(before, rule.ToJson());
    }

    [Fact]
    public void CheckTeamAppliesBannedLimitedAndBasicTeamRules()
    {
        var db = BattleTests.LoadPack("original");
        var rule = Rule();
        rule.Season = "s1";
        var banned = new[] { "kei", "enka", "shiori", "lumi", "hane", "momo" };
        var limited = new[] { "bell", "bee", "shiori", "lumi", "hane", "momo" };
        Assert.Equal(TeamCheck.Validate(db, "s1", banned), rule.CheckTeam(db, banned));
        Assert.NotEmpty(rule.CheckTeam(db, banned));
        Assert.NotEmpty(rule.CheckTeam(db, limited));
        Assert.Empty(rule.CheckTeam(db, A));
        rule.Season = null;
        Assert.Empty(rule.CheckTeam(db, banned));
        Assert.NotEmpty(rule.CheckTeam(db, A.Take(5).ToArray()));
        Assert.NotEmpty(rule.CheckTeam(db, Enumerable.Repeat("bell", 6).ToArray()));
        Assert.NotEmpty(rule.CheckTeam(db, new[] { "missing", "enka", "shiori", "lumi", "hane", "momo" }));
    }

    [Fact]
    public void PresetsHaveTheirAdvertisedModesAndDoNotShareMutableConfiguration()
    {
        var catalog = Rulesets.All;
        var original = catalog.ToDictionary(rule => rule.Id, rule => rule.ToJson());
        var casual = Assert.Single(catalog, rule => rule.Id == "casual");
        var ranked = Assert.Single(catalog, rule => rule.Id == "ranked");
        var chaos = Assert.Single(catalog, rule => rule.Id == "chaos");
        Assert.Equal(1, casual.BestOf);
        Assert.False(casual.BanOne);
        Assert.Equal(0, casual.TurnSeconds);
        Assert.Equal(0, casual.BankSeconds);
        Assert.True(ranked.BanOne && ranked.LeadPick);
        Assert.Equal(3, ranked.BestOf);
        Assert.Equal(10, ranked.TurnSeconds);
        Assert.Equal(30, ranked.BankSeconds);
        Assert.Equal(1, chaos.BestOf);
        Assert.Equal(new[] { "half_pp", "hype_start" }, chaos.Modifiers.OrderBy(id => id, StringComparer.Ordinal));
        var db = BattleTests.LoadPack("original");
        foreach (var preset in catalog) preset.Validate(db);

        ranked.Name = "修改后的名字";
        ranked.BestOf = 5;
        chaos.Modifiers.Clear();
        if (catalog is IList<Ruleset> mutable) Assert.Throws<NotSupportedException>(() => mutable.Add(Rule()));

        Assert.Equal(original.Count, Rulesets.All.Count);
        foreach (var preset in Rulesets.All) Assert.Equal(original[preset.Id], preset.ToJson());
    }

    [Fact]
    public void MatchSnapshotsRulesTeamsAndNamesAndAppliesConfigurationToEveryGame()
    {
        var db = BattleTests.LoadPack("original");
        var rule = Rule();
        rule.Season = "s1";
        rule.BanOne = rule.LeadPick = true;
        rule.Modifiers.AddRange(new[] { "hype_start", "half_pp", "half_pp" });
        var teams = Teams();
        var names = new[] { "原甲方", "原乙方" };
        var match = new Match(db, rule, teams, names, 100);
        rule.BestOf = 1;
        rule.Season = "missing";
        rule.BanOne = rule.LeadPick = false;
        rule.Modifiers.Clear();
        teams[0][0] = "missing";
        teams[1].Clear();
        names[0] = "更改后的甲方";

        var first = match.StartNextGame();

        AssertConfigured(first);
        first.ChooseBan(0, 0);
        first.ChooseBan(1, 0);
        Assert.Equal(Phase.Lead, first.Phase);
        first.ChooseLead(0, 0);
        first.ChooseLead(1, 0);
        Assert.Equal(Phase.Choose, first.Phase);
        Assert.Equal(1, first.Active(0).FieldOf("hype").Stacks);
        Assert.Equal(1, first.Active(1).FieldOf("hype").Stacks);
        var skill = first.Active(0).Skills[0];
        Assert.Equal((db.Skills[skill.Id].Pp + 1) / 2, skill.Max);
        Finish(match, first, 0);
        Assert.False(match.Over);

        var second = match.StartNextGame();

        AssertConfigured(second);
        Assert.Equal(101u, second.Seed);

        void AssertConfigured(Battle battle)
        {
            Assert.Equal(Phase.Ban, battle.Phase);
            Assert.Equal(A, battle.Sides[0].Team.Select(fighter => fighter.Def.Id));
            Assert.Equal(B, battle.Sides[1].Team.Select(fighter => fighter.Def.Id));
            Assert.Equal("原甲方", battle.Sides[0].Name);
            Assert.Equal("原乙方", battle.Sides[1].Name);
            Assert.True(battle.Record.BanOne && battle.Record.LeadPick);
            Assert.Equal(new[] { "half_pp", "hype_start" }, battle.Modifiers);
        }
    }

    [Fact]
    public void OnlyTheCurrentFinishedGameCanBeReportedOnce()
    {
        var match = Make();
        Assert.Throws<InvalidOperationException>(() => match.ReportResult(null));
        var first = match.StartNextGame();
        Assert.Throws<InvalidOperationException>(() => match.StartNextGame());
        Assert.Throws<InvalidOperationException>(() => match.ReportResult(first));
        first.Phase = Phase.Over;
        first.Winner = 0;
        Assert.Throws<InvalidOperationException>(() => match.StartNextGame());
        Assert.Throws<InvalidOperationException>(() => match.ReportResult(first.Clone()));
        var foreign = Make().StartNextGame();
        foreign.Phase = Phase.Over;
        foreign.Winner = 1;
        Assert.Throws<InvalidOperationException>(() => match.ReportResult(foreign));
        Assert.Equal(new[] { 0, 0 }, match.Wins);
        Assert.Equal(0, match.Draws);
        Assert.False(match.Over);

        match.ReportResult(first);

        Assert.Equal(new[] { 1, 0 }, match.Wins);
        Assert.Throws<InvalidOperationException>(() => match.ReportResult(first));
        var second = match.StartNextGame();
        Assert.Equal(55u, second.Seed);
        Assert.Throws<InvalidOperationException>(() => match.ReportResult(first));
        Assert.Equal(new[] { 1, 0 }, match.Wins);
        Finish(match, second, 1);
        Assert.Equal(new[] { 1, 1 }, match.Wins);
    }

    [Theory]
    [InlineData(-2)]
    [InlineData(2)]
    public void InvalidWinnerDoesNotCountOrReleaseThePendingGame(int winner)
    {
        var match = Make();
        var game = match.StartNextGame();
        game.Phase = Phase.Over;
        game.Winner = winner;

        Assert.Throws<InvalidOperationException>(() => match.ReportResult(game));
        Assert.Throws<InvalidOperationException>(() => match.StartNextGame());
        Assert.Equal(new[] { 0, 0 }, match.Wins);
        Assert.Equal(0, match.Draws);
        Finish(match, game, -1);
        Assert.Equal(1, match.Draws);
        Assert.Equal(55u, match.StartNextGame().Seed);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    public void DrawsCountTowardTheGameLimitWithoutAddingWins(int bestOf)
    {
        var match = Make(bestOf);
        for (int game = 0; game < bestOf; game++)
        {
            Assert.False(match.Over);
            Finish(match, match.StartNextGame(), -1);
            Assert.Equal(game + 1, match.Draws);
            Assert.Equal(new[] { 0, 0 }, match.Wins);
        }
        Assert.True(match.Over);
        Assert.Throws<InvalidOperationException>(() => match.StartNextGame());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    public void FirstSideToTheWinThresholdEndsTheMatchAndScoresCannotBeEdited(int bestOf)
    {
        var match = Make(bestOf);
        int needed = bestOf / 2 + 1;
        for (int game = 0; game < needed; game++)
        {
            Assert.False(match.Over);
            Finish(match, match.StartNextGame(), 1);
            var exposed = match.Wins;
            exposed[0] = exposed[1] = 999;
            Assert.Equal(new[] { 0, game + 1 }, match.Wins);
        }
        Assert.True(match.Over);
        Assert.Equal(0, match.Draws);
        Assert.Throws<InvalidOperationException>(() => match.StartNextGame());
    }

    [Fact]
    public void SplitWinsAndADrawEndAtTheLimitWithoutInventingAMatchWin()
    {
        var match = Make();
        foreach (int winner in new[] { 0, -1, 1 }) Finish(match, match.StartNextGame(), winner);

        Assert.True(match.Over);
        Assert.Equal(new[] { 1, 1 }, match.Wins);
        Assert.Equal(1, match.Draws);
        Assert.Throws<InvalidOperationException>(() => match.StartNextGame());
    }

    [Fact]
    public void GameSeedsWrapDeterministicallyAndFailedCallsDoNotConsumeAGameNumber()
    {
        var first = Make(seed: uint.MaxValue);
        var repeated = Make(seed: uint.MaxValue);
        var expected = new[] { uint.MaxValue, 0u, 1u };
        for (int i = 0; i < expected.Length; i++)
        {
            var a = first.StartNextGame();
            var b = repeated.StartNextGame();
            Assert.Throws<InvalidOperationException>(() => first.StartNextGame());
            Assert.Throws<InvalidOperationException>(() => first.ReportResult(a));
            Assert.Equal(expected[i], a.Seed);
            Assert.Equal(a.Seed, b.Seed);
            Assert.Equal(a.StateHash(), b.StateHash());
            Assert.Equal(a.Record.ToJson(), b.Record.ToJson());
            Assert.Equal(a.Random(), b.Random());
            Finish(first, a, -1);
            Finish(repeated, b, -1);
        }
        Assert.True(first.Over && repeated.Over);
    }

    [Fact]
    public void ContentChangesRejectTheNextGameWithoutAdvancingItsSeedOrScores()
    {
        var db = BattleTests.LoadPack("original");
        var match = new Match(db, Rule(), Teams(), new[] { "甲", "乙" }, 20);
        Finish(match, match.StartNextGame(), -1);
        string originalName = db.Skills["warm_up"].Name;
        db.Skills["warm_up"].Name = "中途修改内容";

        Assert.Throws<KeyError>(() => match.StartNextGame());
        Assert.Equal(new[] { 0, 0 }, match.Wins);
        Assert.Equal(1, match.Draws);
        Assert.False(match.Over);
        db.Skills["warm_up"].Name = originalName;

        Assert.Equal(21u, match.StartNextGame().Seed);
    }

    [Fact]
    public void ConstructionRejectsIllegalTeamsBeforeStartingABattle()
    {
        var db = BattleTests.LoadPack("original");
        var seasonal = Rule();
        seasonal.Season = "s1";
        var banned = Teams();
        banned[0][0] = "kei";
        Assert.Throws<KeyError>(() => new Match(db, seasonal, banned, new[] { "甲", "乙" }, 1));
        var duplicate = Teams();
        duplicate[1][1] = duplicate[1][0];
        Assert.Throws<KeyError>(() => new Match(db, Rule(), duplicate, new[] { "甲", "乙" }, 1));
        var missing = Teams();
        missing[1][0] = "missing";
        Assert.Throws<KeyError>(() => new Match(db, Rule(), missing, new[] { "甲", "乙" }, 1));
        var shortTeam = Teams();
        shortTeam[0].RemoveAt(0);
        Assert.Throws<KeyError>(() => new Match(db, Rule(), shortTeam, new[] { "甲", "乙" }, 1));
    }

    [Fact]
    public void ConstructionRequiresTwoTeamsAndTwoNonNullNamesButAllowsEmptyDisplayNames()
    {
        var db = BattleTests.LoadPack("original");
        Assert.ThrowsAny<ArgumentException>(() => new Match(db, Rule(), null, new[] { "甲", "乙" }, 1));
        Assert.ThrowsAny<ArgumentException>(() => new Match(db, Rule(), new IList<string>[] { A }, new[] { "甲", "乙" }, 1));
        Assert.ThrowsAny<ArgumentException>(() => new Match(db, Rule(), Teams(), null, 1));
        Assert.ThrowsAny<ArgumentException>(() => new Match(db, Rule(), Teams(), new[] { "甲" }, 1));
        Assert.ThrowsAny<ArgumentException>(() => new Match(db, Rule(), Teams(), new[] { "甲", null }, 1));
        var empty = new Match(db, Rule(), Teams(), new[] { "", "" }, 1).StartNextGame();

        Assert.Equal("", empty.Sides[0].Name);
        Assert.Equal("", empty.Sides[1].Name);
    }
}
