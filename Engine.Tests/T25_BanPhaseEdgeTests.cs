using System;
using System.Collections.Generic;
using System.Linq;
using StreamDuel.Core;
using Xunit;

public class T25_BanPhaseEdgeTests
{
    static IList<string>[] Teams()
    {
        return new IList<string>[]
        {
            new[] { "bell", "enka", "shiori", "lumi", "hane", "momo" },
            new[] { "kei", "yuan", "nyako", "buck", "kasumi", "bee" }
        };
    }

    static Battle Make(bool lead = false, ContentDb db = null, IList<string>[] teams = null, uint seed = 31)
    {
        return new Battle(db ?? BattleTests.LoadPack("original"), teams ?? Teams(), new[] { "A", "B" },
            new BattleOptions { Seed = seed, BanOne = true, LeadPick = lead });
    }

    static string[] Ids(Battle b, int side) => b.Sides[side].Team.Select(f => f.Def.Id).ToArray();

    static string[] FullLog(Battle b)
    {
        return b.Log.Select(l => string.Join("|", l.Turn, l.Node, l.Kind, l.Side, l.Text,
            string.Join(",", l.SnapActive), string.Join(",", l.SnapHp[0]), string.Join(",", l.SnapHp[1]))).ToArray();
    }

    static void AssertSame(Battle expected, Battle actual)
    {
        Assert.Equal(expected.Phase, actual.Phase);
        Assert.Equal(expected.Turn, actual.Turn);
        Assert.Equal(expected.Winner, actual.Winner);
        Assert.Equal(FullLog(expected), FullLog(actual));
        for (int side = 0; side < 2; side++)
        {
            Assert.Equal(expected.Sides[side].Name, actual.Sides[side].Name);
            Assert.Equal(Ids(expected, side), Ids(actual, side));
            Assert.Equal(expected.Sides[side].Team.Select(f => f.Slot), actual.Sides[side].Team.Select(f => f.Slot));
            Assert.Equal(expected.Sides[side].Team.Select(f => f.Hp), actual.Sides[side].Team.Select(f => f.Hp));
            Assert.Equal(expected.HasChosenBan(side), actual.HasChosenBan(side));
            Assert.Equal(expected.HasChosenLead(side), actual.HasChosenLead(side));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void BothBansResolveTogetherWithoutOpeningRemovedFighters(int firstSide)
    {
        var teams = Teams();
        teams[0][0] = "kasumi";
        teams[1][0] = "kasumi";
        teams[1][4] = "kei";
        var originals = teams.Select(t => t.ToArray()).ToArray();
        var b = Make(teams: teams);
        var removed = new[] { b.Active(0), b.Active(1) };
        Assert.Empty(b.Log);

        b.ChooseBan(firstSide, 0);

        Assert.Equal(Phase.Ban, b.Phase);
        Assert.True(b.HasChosenBan(firstSide));
        Assert.False(b.HasChosenBan(1 - firstSide));
        Assert.Single(b.Record.Events);
        Assert.Empty(b.Log);
        for (int side = 0; side < 2; side++) Assert.Equal(originals[side], Ids(b, side));
        Assert.All(b.Sides.SelectMany(s => s.Team), f => Assert.Empty(f.Fields));
        Assert.Equal(new Rng(31).Next(), b.Random());

        b.ChooseBan(1 - firstSide, 0);

        Assert.Equal(Phase.Choose, b.Phase);
        Assert.Equal(1, b.Turn);
        for (int side = 0; side < 2; side++)
        {
            Assert.Equal(originals[side].Skip(1), Ids(b, side));
            Assert.Equal(Enumerable.Range(1, 5), b.Sides[side].Team.Select(f => f.Slot));
            Assert.Equal(originals[side], teams[side]);
            Assert.Equal(originals[side], b.Record.Teams[side]);
            Assert.Empty(removed[side].Fields);
        }
        Assert.Equal(2, b.Log.Count(l => l.Text.Contains("禁用了")));
        Assert.Equal(2, b.Log.Count(l => l.Kind == "enter"));
        Assert.DoesNotContain(b.Log, l => l.Kind == "trait");
        Assert.All(b.Log, l => Assert.Equal(Node.Start, l.Node));
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(2, 0)]
    [InlineData(0, -1)]
    [InlineData(1, 6)]
    public void InvalidBansDoNotChangeChoicesRecordsOrRosters(int side, int index)
    {
        var b = Make();
        string record = b.Record.ToJson();

        Assert.Throws<ArgumentOutOfRangeException>(() => b.ChooseBan(side, index));

        Assert.Equal(record, b.Record.ToJson());
        Assert.Equal(Phase.Ban, b.Phase);
        Assert.False(b.HasChosenBan(0));
        Assert.False(b.HasChosenBan(1));
        for (int s = 0; s < 2; s++) Assert.Equal(Teams()[s], Ids(b, s));
        Assert.Empty(b.Log);
        Assert.Equal(new Rng(31).Next(), b.Random());
        if (side < 0 || side > 1) Assert.Throws<ArgumentOutOfRangeException>(() => b.HasChosenBan(side));
    }

    [Fact]
    public void RepeatedBansAndWrongPhaseCallsLeaveTheRecordUntouched()
    {
        var b = Make(lead: true);
        string before = b.Record.ToJson();
        Assert.Throws<InvalidOperationException>(() => b.ResolveTurn(BattleAction.Pass(), BattleAction.Pass()));
        Assert.Throws<InvalidOperationException>(() => b.Replace(0, 1));
        Assert.Throws<InvalidOperationException>(() => b.ChooseLead(0, 1));
        Assert.Equal(before, b.Record.ToJson());
        b.ChooseBan(0, 2);
        string pending = b.Record.ToJson();
        Assert.Throws<InvalidOperationException>(() => b.ChooseBan(0, 3));
        Assert.Equal(pending, b.Record.ToJson());
        b.ChooseBan(1, 0);
        string selected = b.Record.ToJson();
        Assert.Equal(Phase.Lead, b.Phase);
        Assert.Throws<InvalidOperationException>(() => b.ChooseBan(1, 1));
        Assert.Throws<InvalidOperationException>(() => Ai.ChooseBan(b, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => b.ChooseLead(0, 5));
        Assert.Equal(selected, b.Record.ToJson());
        b.ChooseLead(0, 0);
        b.ChooseLead(1, 0);
        string started = b.Record.ToJson();
        Assert.Throws<InvalidOperationException>(() => b.ChooseBan(0, 0));
        Assert.Equal(started, b.Record.ToJson());
    }

    [Fact]
    public void LeadIndicesReferToTheFiveRemainingFightersAndOpeningWaitsForBothLeads()
    {
        var b = Make(lead: true);
        b.ChooseBan(0, 0);
        b.ChooseBan(1, 1);
        Assert.Equal(Phase.Lead, b.Phase);
        Assert.Equal(new[] { "bell", "shiori", "lumi", "hane", "momo" }, Ids(b, 0));
        Assert.Equal(new[] { "yuan", "nyako", "buck", "kasumi", "bee" }, Ids(b, 1));
        b.ChooseLead(0, 4);
        Assert.DoesNotContain(b.Log, l => l.Kind == "enter" || l.Kind == "trait");

        b.ChooseLead(1, 3);

        Assert.Equal(new[] { "momo", "bell", "shiori", "lumi", "hane" }, Ids(b, 0));
        Assert.Equal(new[] { "kasumi", "yuan", "nyako", "buck", "bee" }, Ids(b, 1));
        Assert.Equal(Phase.Choose, b.Phase);
        Assert.Equal(2, b.Active(1).FieldOf("filter").Turns);
        Assert.Equal(2, b.Log.Count(l => l.Kind == "enter"));
        Assert.Equal(new[] { 0, 1, 4, 3 }, b.Record.Events.Select(e => e.To));
        Assert.All(b.Sides, s => Assert.Equal(Enumerable.Range(1, 5), s.Team.Select(f => f.Slot)));
    }

    [Fact]
    public void FivePersonRosterHasNoSixthSlotForFreePpEvenWhenOriginalSixthSurvives()
    {
        var teams = Teams();
        teams[0][0] = "nyako";
        var b = Make(teams: teams);
        b.ChooseBan(0, 0);
        b.ChooseBan(1, 1);

        Assert.Equal("momo", b.Sides[0].Team[4].Def.Id);
        Assert.Equal(5, b.Sides[0].Team[4].Slot);
        Assert.False(b.LegalFor(0).FreePp);
        int skill = b.Active(0).Skills.FindIndex(s => s.Id == "cat_walk");
        int pp = b.Active(0).Skills[skill].Pp;
        b.ResolveTurn(BattleAction.Skill(skill), BattleAction.Pass());
        Assert.Equal(pp - 1, b.Active(0).Skills[skill].Pp);
        foreach (var slot in b.Active(0).Skills) slot.Pp = 0;
        Assert.True(b.LegalFor(0).Struggle);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DisabledBanOptionsMatchTheLegacyConstructor(bool lead)
    {
        var db = BattleTests.LoadPack("original");
        var legacy = new Battle(db, Teams(), new[] { "A", "B" }, 31, lead);
        var options = new Battle(db, Teams(), new[] { "A", "B" },
            new BattleOptions { Seed = 31, LeadPick = lead });
        foreach (var b in new[] { legacy, options })
        {
            if (lead) { b.ChooseLead(0, 3); b.ChooseLead(1, 4); }
            b.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());
        }
        AssertSame(legacy, options);
        Assert.Equal(legacy.Record.ToJson(), options.Record.ToJson());
        Assert.Equal(legacy.Random(), options.Random());
        Assert.All(options.Sides, s => Assert.Equal(6, s.Team.Count));
    }

    [Fact]
    public void LegacyRecordWithoutBanLeadOrNamesStillUsesTheOriginalDefaults()
    {
        const string json = "{\"seed\":31,\"packId\":\"original\",\"teams\":[" +
            "[\"bell\",\"enka\",\"shiori\",\"lumi\",\"hane\",\"momo\"]," +
            "[\"kei\",\"yuan\",\"nyako\",\"buck\",\"kasumi\",\"bee\"]]," +
            "\"events\":[{\"type\":\"turn\",\"a0\":{\"t\":\"Pass\",\"i\":0},\"a1\":{\"t\":\"Pass\",\"i\":0}}]}";
        var db = BattleTests.LoadPack("original");
        var record = BattleRecord.FromJson(json);
        var expected = new Battle(db, Teams(), new[] { "你", "对手" }, 31);
        expected.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());

        Assert.False(record.BanOne);
        Assert.False(record.LeadPick);
        AssertSame(expected, Replay.Run(db, record));
    }

    static ContentDb AiContent(bool tie)
    {
        var db = BattleTests.LoadPack("original");
        foreach (string type in new[] { "game", "song" })
        {
            string id = "ban_test_" + type;
            db.Skills[id] = new SkillDef { Id = id, Name = "禁用测试", Type = type, Cat = "talent", Power = 40, Pp = 10 };
        }
        foreach (var streamer in db.Streamers)
        {
            streamer.Type = "art";
            streamer.Persona = "pure";
            streamer.Stats["spd"] = 300;
            streamer.Skills = Enumerable.Repeat("ban_test_game", 4).ToList();
            streamer.Fifth = "ban_test_game";
        }
        if (!tie)
        {
            var teams = Teams();
            for (int i = 0; i < teams[0].Count; i++)
            {
                var own = db.Streamer(teams[0][i]);
                own.Type = i == 0 ? "tech" : "horror";
                own.Skills = Enumerable.Repeat("iron_wall", 4).ToList();
                own.Fifth = "iron_wall";
            }
            var threat = db.Streamer(teams[1][4]);
            threat.Skills = Enumerable.Repeat("ban_test_song", 4).ToList();
            threat.Fifth = "ban_test_song";
        }
        return db;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void AiBansTheUniqueThreatToTheWholeRosterWithoutSpendingRandomness(int side)
    {
        var teams = Teams();
        if (side == 1) teams = new[] { teams[1], teams[0] };
        var b = Make(db: AiContent(false), teams: teams);
        var control = Make(db: AiContent(false), teams: teams);

        Assert.Equal(4, Ai.ChooseBan(b, side));

        Assert.Equal(control.Random(), b.Random());
        Assert.False(b.HasChosenBan(side));
        Assert.Empty(b.Record.Events);
        Assert.Empty(b.Log);
    }

    [Fact]
    public void AiTieBreakingIsSeededAndDoesNotLockInTheReturnedBan()
    {
        var choices = new HashSet<int>();
        var db = AiContent(true);
        for (uint seed = 1; seed <= 8; seed++)
        {
            var first = Make(db: db, seed: seed);
            var second = Make(db: db, seed: seed);
            int selected = Ai.ChooseBan(first, 0);
            Assert.InRange(selected, 0, 5);
            Assert.Equal(selected, Ai.ChooseBan(second, 0));
            Assert.Equal(first.Random(), second.Random());
            Assert.False(first.HasChosenBan(0));
            Assert.Empty(first.Record.Events);
            choices.Add(selected);
        }
        Assert.True(choices.Count > 1);
    }

    [Fact]
    public void PartialBanRecordReplaysAndCanContinueWithTheSameRosterAndRandomness()
    {
        var b = Make(lead: true);
        AssertSame(b, Replay.Run(b.Db, BattleRecord.FromJson(b.Record.ToJson())));
        b.Random();
        b.ChooseBan(1, 2);
        var replayed = Replay.Run(b.Db, BattleRecord.FromJson(b.Record.ToJson()));
        AssertSame(b, replayed);
        Assert.Equal(b.Random(), replayed.Random());
        b.ChooseBan(0, 4);
        replayed.ChooseBan(0, 4);
        AssertSame(b, replayed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompleteBanBattleReplaysNamesSnapshotsAndRandomStream(bool lead)
    {
        var b = Make(lead: lead, seed: 73);
        b.Random();
        b.ChooseBan(1, Ai.ChooseBan(b, 1));
        b.Random();
        b.ChooseBan(0, Ai.ChooseBan(b, 0));
        if (lead)
        {
            b.ChooseLead(1, Ai.ChooseLead(b, 1));
            b.ChooseLead(0, Ai.ChooseLead(b, 0));
        }
        int guard = 500;
        while (b.Phase != Phase.Over && guard-- > 0)
        {
            if (b.Phase == Phase.Choose) b.ResolveTurn(Ai.Choose(b, 0), Ai.Choose(b, 1));
            else for (int side = 0; side < 2; side++) if (b.Need[side]) b.Replace(side, Ai.ChooseReplacement(b, side));
        }
        Assert.Equal(Phase.Over, b.Phase);
        Assert.Equal(2, b.Record.Events.Count(e => e.Type == BattleRecordEventType.Ban));
        Assert.Equal(Teams()[0], b.Record.Teams[0]);
        Assert.Equal(Teams()[1], b.Record.Teams[1]);
        string json = b.Record.ToJson();
        var replayed = Replay.Run(b.Db, BattleRecord.FromJson(json));
        AssertSame(b, replayed);
        Assert.Equal(json, replayed.Record.ToJson());
        Assert.Equal(b.Random(), replayed.Random());
        Assert.Equal(new[] { "A", "B" }, replayed.Sides.Select(s => s.Name));
    }
}
