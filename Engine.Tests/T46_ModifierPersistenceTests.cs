using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using StreamDuel.Core;
using Xunit;

public class T46_ModifierPersistenceTests
{
    static readonly IList<string>[] Teams =
    {
        new[] { "bell", "enka", "shiori", "lumi", "hane", "momo" },
        new[] { "kei", "yuan", "nyako", "buck", "kasumi", "bee" }
    };

    static readonly string[] AllModifiers =
    {
        "sudden_death", "stage_cap_3", "no_switch", "hype_start", "hp_x2", "half_pp"
    };

    static Battle Make(List<string> modifiers = null, bool ban = false, bool lead = false)
    {
        return new Battle(BattleTests.LoadPack("original"), Teams, new[] { "甲", "乙" },
            new BattleOptions { Seed = 46, Modifiers = modifiers, BanOne = ban, LeadPick = lead });
    }

    static string RecordJson(string modifiersJson = null)
    {
        var record = new BattleRecord { Seed = 46, PackId = "original", Teams = Teams };
        string json = record.ToJson();
        return modifiersJson == null ? json : json.Insert(1, "\"modifiers\":" + modifiersJson + ",");
    }

    static string[] FullLog(Battle battle)
    {
        return battle.Log.Select(entry => string.Join("|", entry.Turn, entry.Node, entry.Kind, entry.Side, entry.Text,
            string.Join(",", entry.SnapActive), string.Join(",", entry.SnapHp[0]), string.Join(",", entry.SnapHp[1]))).ToArray();
    }

    [Theory]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("1")]
    [InlineData("{}")]
    [InlineData("\"half_pp\"")]
    [InlineData("[null]")]
    [InlineData("[1]")]
    [InlineData("[false]")]
    [InlineData("[{}]")]
    [InlineData("[[]]")]
    [InlineData("[\"half_pp\",null]")]
    public void JsonRejectsNonArraysAndNonStringElements(string value)
    {
        var error = Assert.Throws<FormatException>(() => BattleRecord.FromJson(RecordJson(value)));
        Assert.Contains("modifiers", error.Message);
    }

    [Fact]
    public void JsonRoundTripPreservesStringsAndRejectsNullWhenWriting()
    {
        var record = BattleRecord.FromJson(RecordJson());
        record.Modifiers = new List<string> { "half_pp", "转义\"\\\n\t", "stage_cap_3" };
        string json = record.ToJson();
        Assert.DoesNotContain(json.ToCharArray(), c => c < ' ');
        Assert.Equal(record.Modifiers, BattleRecord.FromJson(json).Modifiers);

        record.Modifiers.Add(null);
        Assert.Throws<FormatException>(() => record.ToJson());
    }

    [Fact]
    public void MissingAndEmptyModifiersKeepLegacyRecordAndBattleBehavior()
    {
        string oldJson = RecordJson().Replace(",\"version\":2", "");
        var oldRecord = BattleRecord.FromJson(oldJson);
        Assert.Equal(1, oldRecord.Version);
        Assert.Null(oldRecord.Modifiers);
        var emptyRecord = BattleRecord.FromJson(RecordJson("[]"));
        Assert.Empty(emptyRecord.Modifiers);
        Assert.Equal(RecordJson(), emptyRecord.ToJson());

        var plain = Make();
        var empty = Make(new List<string>());
        Assert.Equal(plain.StateHash(), empty.StateHash());
        Assert.Equal(plain.Record.ToJson(), empty.Record.ToJson());
        Assert.DoesNotContain("\"modifiers\"", plain.Record.ToJson());
        Assert.Equal(plain.StateHash(), Replay.Run(plain.Db, oldRecord).StateHash());
        Assert.Equal(plain.StateHash(), Replay.Run(plain.Db, emptyRecord).StateHash());
    }

    [Theory]
    [InlineData("unknown_modifier")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("HALF_PP")]
    public void ReplayRejectsUnknownIdsAtBattleConstruction(string id)
    {
        var record = BattleRecord.FromJson(RecordJson());
        record.Modifiers = new List<string> { id };
        var parsed = BattleRecord.FromJson(record.ToJson());
        Assert.Throws<KeyError>(() => Replay.Run(BattleTests.LoadPack("original"), parsed));
    }

    [Fact]
    public void HashDistinguishesInactiveRulesButIgnoresOrderingAndDuplicates()
    {
        var plain = Make();
        var cap = Make(new List<string> { "stage_cap_3" });
        var locked = Make(new List<string> { "no_switch" });
        var combined = Make(new List<string> { "stage_cap_3", "no_switch", "stage_cap_3" });
        var reordered = Make(new List<string> { "no_switch", "stage_cap_3" });

        Assert.Equal(4, new[] { plain.StateHash(), cap.StateHash(), locked.StateHash(), combined.StateHash() }.Distinct().Count());
        Assert.Equal(combined.StateHash(), reordered.StateHash());
        Assert.Equal(combined.Record.ToJson(), reordered.Record.ToJson());
        Assert.Equal(new[] { "no_switch", "stage_cap_3" }, combined.Modifiers);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CopiesOwnReadOnlyRulesAndIndependentRecordLists(bool hardPrediction)
    {
        var input = new List<string> { "stage_cap_3", "no_switch" };
        var battle = Make(input);
        input.Clear();
        var expected = new[] { "no_switch", "stage_cap_3" };
        Assert.Equal(expected, battle.Modifiers);
        Assert.Equal(expected, battle.Record.Modifiers);

        // 困难 AI 使用轻量内部副本，也必须携带影响合法行动和效果结算的规则。
        var copy = hardPrediction
            ? (Battle)typeof(Battle).GetMethod("CopyForHardAi", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(battle, null)
            : battle.Clone();
        Assert.Equal(expected, copy.Modifiers);
        Assert.NotSame(battle.Modifiers, copy.Modifiers);
        Assert.NotSame(battle.Record.Modifiers, copy.Record.Modifiers);
        Assert.Empty(copy.LegalFor(0).Switches);
        var list = Assert.IsAssignableFrom<IList<string>>(copy.Modifiers);
        Assert.Throws<NotSupportedException>(() => list.Add("half_pp"));

        ulong originalHash = battle.StateHash();
        ulong copyHash = copy.StateHash();
        copy.Record.Modifiers.Add("half_pp");
        Assert.Equal(expected, battle.Record.Modifiers);
        battle.Record.Modifiers.Clear();
        Assert.Equal(new[] { "no_switch", "stage_cap_3", "half_pp" }, copy.Record.Modifiers);
        Assert.Equal(expected, battle.Modifiers);
        Assert.Equal(expected, copy.Modifiers);
        Assert.Equal(originalHash, battle.StateHash());
        Assert.Equal(copyHash, copy.StateHash());

        battle.Record.Modifiers = null;
        Assert.Null(battle.Clone().Record.Modifiers);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void CombinedModifiersReplayThroughOpeningAndSuddenDeath(bool ban, bool lead)
    {
        var battle = Make(AllModifiers.ToList(), ban, lead);
        if (ban)
        {
            battle.ChooseBan(1, 2);
            battle.Random();
            battle.ChooseBan(0, 1);
        }
        if (lead)
        {
            battle.ChooseLead(1, 2);
            battle.Random();
            battle.ChooseLead(0, 3);
        }
        for (int turn = 0; turn < 12; turn++)
        {
            battle.Random();
            battle.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        }

        var record = BattleRecord.FromJson(battle.Record.ToJson());
        var replay = Replay.Run(battle.Db, record);
        Assert.Equal(battle.Modifiers, replay.Modifiers);
        Assert.Equal(battle.StateHash(), replay.StateHash());
        Assert.Equal(battle.Record.ToJson(), replay.Record.ToJson());
        Assert.Equal(FullLog(battle), FullLog(replay));
        Assert.Equal(BattleSummary.From(battle).ToText(), BattleSummary.From(replay).ToText());
        for (int draw = 0; draw < 5; draw++) Assert.Equal(battle.Random(), replay.Random());
    }

    [Fact]
    public void CloneRetainsModifiersDuringPendingBanAndLaterTurns()
    {
        var battle = Make(AllModifiers.ToList(), true, true);
        battle.ChooseBan(1, 2);
        var copy = battle.Clone();
        Assert.Equal(battle.StateHash(), copy.StateHash());
        foreach (var current in new[] { battle, copy })
        {
            current.ChooseBan(0, 1);
            current.ChooseLead(1, 2);
            current.ChooseLead(0, 3);
        }
        for (int turn = 0; turn < 12; turn++)
        {
            ulong before = battle.StateHash();
            copy.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
            Assert.Equal(before, battle.StateHash());
            battle.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
            Assert.Equal(battle.StateHash(), copy.StateHash());
        }
        Assert.Equal(battle.Record.ToJson(), copy.Record.ToJson());
        Assert.Equal(FullLog(battle), FullLog(copy));
    }
}
