using System;
using System.Collections.Generic;
using System.Linq;
using StreamDuel.Core;
using Xunit;

public class T65_BalanceCheckEdgeTests
{
    [Fact]
    public void DrawsGiveEveryRosterMemberHalfAWin()
    {
        var db = NeutralRoster();
        foreach (var skill in db.Skills.Values) skill.Power = 0;
        var rates = BalanceCheck.WinRates(db, 3, 0, AiLevel.Normal);
        Assert.Equal(12, rates.Count);
        Assert.All(rates.Values, rate => Assert.Equal(0.5, rate));
    }

    [Fact]
    public void SixMemberMirrorRosterCountsBothSides()
    {
        var db = BattleTests.LoadPack("original");
        db.Streamers.RemoveRange(6, db.Streamers.Count - 6);
        var rates = BalanceCheck.WinRates(db, 5, uint.MaxValue, AiLevel.Easy);
        Assert.Equal(6, rates.Count);
        Assert.All(rates.Values, rate => Assert.Equal(0.5, rate));
    }

    [Fact]
    public void DominantStreamerIsNotHiddenByMirroringOrClamping()
    {
        var db = NeutralRoster();
        var dominant = db.Streamers[0];
        dominant.Stats["hp"] = 1000000;
        dominant.Stats["atk"] = 1000000;
        dominant.Stats["def"] = 1000000;
        dominant.Stats["spd"] = 1000000;
        var rates = BalanceCheck.WinRates(db, 5, 7, AiLevel.Normal);
        Assert.Equal(1.0, rates[dominant.Id]);
        Assert.Equal(6.0, rates.Values.Sum(), 10);
    }

    [Fact]
    public void ReorderingRosterDoesNotChangeRatesOrContent()
    {
        var db = BattleTests.LoadPack("original");
        string before = db.ContentHash();
        var first = BalanceCheck.WinRates(db, 5, 17, AiLevel.Normal);
        Assert.Equal(before, db.ContentHash());
        db.Streamers.Reverse();
        var second = BalanceCheck.WinRates(db, 5, 17, AiLevel.Normal);
        Assert.Equal(before, db.ContentHash());
        foreach (var rate in first) Assert.Equal(rate.Value, second[rate.Key]);
    }

    [Fact]
    public void InvalidInputsAreRejected()
    {
        var db = BattleTests.LoadPack("original");
        Assert.Throws<ArgumentNullException>(() => BalanceCheck.WinRates(null, 1, 1, AiLevel.Normal));
        Assert.Throws<ArgumentOutOfRangeException>(() => BalanceCheck.WinRates(db, 0, 1, AiLevel.Normal));
        Assert.Throws<ArgumentOutOfRangeException>(() => BalanceCheck.WinRates(db, -1, 1, AiLevel.Normal));
        Assert.Throws<ArgumentOutOfRangeException>(() => BalanceCheck.WinRates(db, 1, 1, (AiLevel)99));
        db.Streamers.RemoveRange(5, db.Streamers.Count - 5);
        Assert.Throws<ArgumentException>(() => BalanceCheck.WinRates(db, 1, 1, AiLevel.Normal));
    }

    [Fact]
    public void LargerRosterSamplesEveryMemberAndReproducesUnclampedRatesWithoutMutatingContent()
    {
        var db = NeutralRoster();
        var source = db.Streamers[0];
        for (int i = 0; i < 7; i++)
        {
            db.Streamers.Add(new StreamerDef
            {
                Id = "review_extra_" + i, Name = "测试主播" + i,
                Type = source.Type, Type2 = source.Type2, Persona = source.Persona, Fifth = source.Fifth,
                Blurb = source.Blurb, Org = source.Org, AiStyle = source.AiStyle,
                Stats = new Dictionary<string, int>(source.Stats), Skills = new List<string>(source.Skills)
            });
        }
        var dominant = db.Streamers.Last();
        Strengthen(dominant);
        Assert.Equal(10, source.Stats["hp"]);
        Assert.Equal(10, source.Stats["atk"]);
        string before = db.ContentHash();

        var first = BalanceCheck.WinRates(db, 12, 6501, AiLevel.Normal);
        Assert.Equal(before, db.ContentHash());
        db.Streamers.Reverse();
        var second = BalanceCheck.WinRates(db, 12, 6501, AiLevel.Normal);

        Assert.Equal(19, first.Count);
        Assert.Equal(db.Streamers.Select(s => s.Id).OrderBy(id => id, StringComparer.Ordinal), first.Keys);
        Assert.All(first.Values, rate => Assert.True(!double.IsNaN(rate) && rate >= 0 && rate <= 1));
        Assert.Equal(1.0, first[dominant.Id]);
        Assert.Contains(first.Values, rate => rate < 1.0);
        Assert.Equal(first.Count, second.Count);
        foreach (var rate in first) Assert.Equal(rate.Value, second[rate.Key]);
        Assert.Equal(before, db.ContentHash());
    }

    [Theory]
    [InlineData(AiLevel.Hard)]
    [InlineData(AiLevel.Expert)]
    public void SearchDifficultiesAreReproducibleAndLeaveContentUnchanged(AiLevel level)
    {
        var db = NeutralRoster();
        var dominant = db.Streamers[0];
        Strengthen(dominant);
        string before = db.ContentHash();

        var first = BalanceCheck.WinRates(db, 1, 6502, level);
        Assert.Equal(before, db.ContentHash());
        var second = BalanceCheck.WinRates(db, 1, 6502, level);

        Assert.Equal(12, first.Count);
        Assert.Equal(1.0, first[dominant.Id]);
        Assert.Equal(6, first.Values.Count(rate => rate == 1.0));
        Assert.Equal(6, first.Values.Count(rate => rate == 0.0));
        Assert.Equal(first.Count, second.Count);
        foreach (var rate in first) Assert.Equal(rate.Value, second[rate.Key]);
        Assert.Equal(before, db.ContentHash());
    }

    static void Strengthen(StreamerDef streamer)
    {
        streamer.Stats["hp"] = 1000000;
        streamer.Stats["atk"] = 1000000;
        streamer.Stats["def"] = 1000000;
        streamer.Stats["spd"] = 1000000;
    }

    static ContentDb NeutralRoster()
    {
        var db = BattleTests.LoadPack("original");
        foreach (var streamer in db.Streamers)
        {
            streamer.Type = "song";
            streamer.Type2 = null;
            foreach (string stat in ContentDb.StatKeys) streamer.Stats[stat] = 10;
            streamer.Stats["hp"] = 10;
        }
        foreach (var persona in db.Personas.Values)
        {
            persona.Code = "T_LOWHP_POWER";
            persona.Params.Clear();
            persona.Params["below"] = 0;
            persona.Params["mul"] = 1;
        }
        foreach (var skill in db.Skills.Values)
        {
            skill.Cat = "interact";
            skill.Type = "song";
            skill.Power = 10;
            skill.Pp = 1000;
            skill.Acc = 100;
            skill.Prio = 0;
            skill.Effects.Clear();
        }
        return db;
    }
}
