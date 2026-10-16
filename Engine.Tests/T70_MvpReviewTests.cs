using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using StreamDuel.Core;
using Xunit;

public class T70_MvpReviewTests
{
    static FighterStats Stats(int dealt = 0, int taken = 0, int kos = 0)
    {
        return new FighterStats { DamageDealt = dealt, DamageTaken = taken, Kos = kos };
    }

    static BattleSummary Summary(int winner, FighterStats[] left, FighterStats[] right = null)
    {
        return new BattleSummary
        {
            Winner = winner, Turns = 1,
            Sides = new[] { new List<FighterStats>(left), new List<FighterStats>(right ?? Array.Empty<FighterStats>()) }
        };
    }

    static void WinnerIs(BattleSummary summary, int side, int slot)
    {
        var mvp = summary.Mvp();
        Assert.True(mvp.HasValue);
        Assert.Equal((side, slot), mvp.Value);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void OnlyTheWinningSideIsEligibleEvenWhenTheLoserScoresMore(int winner)
    {
        var summary = Summary(winner, new[] { Stats(1), Stats(20) }, new[] { Stats(1), Stats(20) });
        summary.Sides[1 - winner][0] = Stats(int.MaxValue, int.MaxValue, 6);
        summary.Sides[winner][1].Fainted = true;
        WinnerIs(summary, winner, 1);
    }

    [Theory]
    [InlineData(0, 199, 0, 0)]
    [InlineData(0, 201, 0, 1)]
    [InlineData(0, 0, 1, 1)]
    [InlineData(99, 0, 0, 0)]
    [InlineData(101, 0, 0, 1)]
    public void ScoreUsesExactHalfTakenDamageAndOneHundredPerKnockout(int dealt, int taken, int kos, int expectedSlot)
    {
        var summary = Summary(0, new[] { Stats(100), Stats(dealt, taken, kos) });
        WinnerIs(summary, 0, expectedSlot);
    }

    [Fact]
    public void HalfPointLeadBeatsMoreKnockoutsAndMoreDamageBeforeTieBreaks()
    {
        WinnerIs(Summary(0, new[] { Stats(100), Stats(kos: 1), Stats(taken: 201) }), 0, 2);
    }

    [Fact]
    public void EqualScoresPreferKnockoutsThenDamageThenEarlierTeamSlot()
    {
        WinnerIs(Summary(0, new[] { Stats(102), Stats(taken: 4, kos: 1) }), 0, 1);
        WinnerIs(Summary(0, new[] { Stats(100, 2), Stats(101) }), 0, 1);
        WinnerIs(Summary(0, new[] { Stats(), Stats(40, 8, 1), Stats(40, 8, 1) }), 0, 1);
    }

    [Fact]
    public void DrawComparesBothSidesAndTeamSlotBeforeStableSideOrder()
    {
        WinnerIs(Summary(-1, new[] { Stats(10) }, new[] { Stats(11) }), 1, 0);
        WinnerIs(Summary(-1, new[] { Stats(), Stats(20) }, new[] { Stats(20) }), 1, 0);
        WinnerIs(Summary(-1, new[] { Stats(), Stats(20) }, new[] { Stats(), Stats(20) }), 0, 1);
    }

    [Fact]
    public void FullIntegerCountersDoNotOverflowAndOneTakenPointStillBreaksTheTie()
    {
        var summary = Summary(1, new[] { Stats() }, new[]
        {
            Stats(int.MaxValue, int.MaxValue - 1, int.MaxValue),
            Stats(int.MaxValue, int.MaxValue, int.MaxValue)
        });
        WinnerIs(summary, 1, 1);
    }

    [Theory]
    [InlineData(-2)]
    [InlineData(-3)]
    [InlineData(2)]
    public void UnfinishedOrInvalidWinnerCannotPublishAnMvp(int winner)
    {
        Assert.Null(Summary(winner, new[] { Stats(50) }, new[] { Stats(40) }).Mvp());
    }

    [Fact]
    public void UninitializedAndMissingEligibleStatisticsReturnNull()
    {
        Assert.Null(new BattleSummary().Mvp());
        Assert.Null(new BattleSummary { Turns = 1, Winner = 0 }.Mvp());
        Assert.Null(Summary(0, Array.Empty<FighterStats>(), new[] { Stats(100) }).Mvp());
        Assert.Null(new BattleSummary { Turns = 1, Winner = 0, Sides = new List<FighterStats>[] { null, new List<FighterStats> { Stats(100) } } }.Mvp());
        Assert.Null(Summary(0, new FighterStats[] { null }).Mvp());
        WinnerIs(Summary(0, new[] { null, Stats(3) }), 0, 1);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, true)]
    public void OpeningSurrenderOrDrawHasNoMvpWithoutAResolvedTurn(bool ban, bool lead, bool draw)
    {
        var battle = MakeBattle(ban, lead);
        if (draw) { battle.OfferDraw(0); battle.RespondDraw(1, true); }
        else battle.Surrender(1);

        var summary = BattleSummary.From(battle);
        Assert.Equal(Phase.Over, battle.Phase);
        Assert.Equal(0, summary.Turns);
        Assert.Null(summary.Mvp());
        Assert.Null(BattleSummary.From(Replay.Run(battle.Db, battle.Record)).Mvp());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ZeroDamagePassSwitchAndStatusSkillAreStillResolvedActions(int action)
    {
        var battle = MakeBattle();
        var chosen = action == 0 ? BattleAction.Pass() : action == 1 ? BattleAction.SwitchTo(2) : BattleAction.Skill(1);
        battle.ResolveTurn(chosen, BattleAction.Pass());
        Assert.Equal(Phase.Choose, battle.Phase);
        battle.ResolveSurrenders(true, true);

        var summary = BattleSummary.From(battle);
        Assert.Equal(1, summary.Turns);
        Assert.All(summary.Sides.SelectMany(side => side), stats =>
        {
            Assert.Equal(0, stats.DamageDealt);
            Assert.Equal(0, stats.DamageTaken);
            Assert.Equal(0, stats.Kos);
        });
        if (action == 2) Assert.Equal(1, summary.Sides[0][0].SkillUses["pregame"]);
        WinnerIs(summary, 0, 0);
    }

    [Fact]
    public void RealBanAndLeadBattleUsesCurrentTeamSlotsAndMvpQueryIsPureAndReplayable()
    {
        var battle = MakeBattle(true, true);
        battle.ChooseBan(0, 5);
        battle.ChooseBan(1, 5);
        string selected = battle.Sides[0].Team[2].Def.Id;
        battle.ChooseLead(0, 2);
        battle.ChooseLead(1, 3);
        battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());
        battle.ResolveTurn(BattleAction.SwitchTo(2), BattleAction.Skill(0));
        battle.Surrender(1);
        var summary = BattleSummary.From(battle);
        Assert.Equal(31, summary.Sides[0][0].DamageDealt);
        Assert.Equal(31, summary.Sides[0][2].DamageTaken);
        Assert.Equal(selected, summary.Sides[0][0].StreamerId);

        var jsonOptions = new JsonSerializerOptions { IncludeFields = true };
        string before = JsonSerializer.Serialize(summary, jsonOptions), text = summary.ToText();
        string record = battle.Record.ToJson(), log = JsonSerializer.Serialize(battle.Log, jsonOptions);
        ulong hash = battle.StateHash();
        double random = battle.Clone().Random();
        for (int i = 0; i < 3; i++) WinnerIs(summary, 0, 0);
        var replay = Replay.Run(battle.Db, battle.Record);
        WinnerIs(BattleSummary.From(replay), 0, 0);
        Assert.Equal(text, BattleSummary.From(replay).ToText());
        Assert.Equal(before, JsonSerializer.Serialize(summary, jsonOptions));
        Assert.Equal(text, summary.ToText());
        Assert.Equal(record, battle.Record.ToJson());
        Assert.Equal(log, JsonSerializer.Serialize(battle.Log, jsonOptions));
        Assert.Equal(hash, battle.StateHash());
        Assert.Equal(random, battle.Clone().Random());
    }

    internal static Battle MakeBattle(bool ban = false, bool lead = false)
    {
        var db = BattleTests.LoadPack("original");
        foreach (var streamer in db.Streamers)
        {
            streamer.Persona = "pure";
            streamer.Skills = new List<string> { "full_volume", "pregame", "deep_breath", "quick_mic" };
            streamer.Fifth = "high_note";
        }
        var attack = db.Skills["full_volume"];
        attack.Cat = "status";
        attack.Power = 0;
        attack.Acc = 100;
        attack.Effects = new List<EffectDef> { new EffectDef { At = "post", Code = "FIXED_DAMAGE", Target = "foe", N = 31 } };
        db.Skills["pregame"].Effects = new List<EffectDef>();
        db.Skills["pregame"].Cat = "status";
        db.Skills["pregame"].Power = 0;
        db.Validate();
        var ids = db.Streamers.Select(streamer => streamer.Id).ToArray();
        return new Battle(db, new IList<string>[] { ids.Take(6).ToArray(), ids.Skip(6).Take(6).ToArray() },
            new[] { "MVP甲", "MVP乙" }, new BattleOptions { Seed = 70, BanOne = ban, LeadPick = lead });
    }
}
