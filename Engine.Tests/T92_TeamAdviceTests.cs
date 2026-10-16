using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using StreamDuel.Core;
using Xunit;

[Collection("Localization current")]
public class T92_TeamAdviceTests : IDisposable
{
    static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions { IncludeFields = true };
    readonly Loc previousLocale;

    public T92_TeamAdviceTests()
    {
        previousLocale = Loc.Current;
        Loc.Current = null;
    }

    public void Dispose() { Loc.Current = previousLocale; }

    [Fact]
    public void FoeCounteringFiveMembersIsTheBanAndHasTheUniqueResistantAnswer()
    {
        var fixture = ObviousMatchup();
        var result = TeamAdvice.For(fixture.Db, fixture.Mine, fixture.Theirs);

        Assert.Equal(fixture.Theirs[0], result.BanSuggestion.Id);
        Assert.Equal(fixture.Theirs[0], result.Threats[0].Id);
        var answer = Assert.Single(result.Answers, item => item.ThreatId == fixture.Theirs[0]);
        Assert.Equal(fixture.Mine[5], answer.AnswerId);
        Assert.Contains(result.LeadSuggestions, item => item.Id == fixture.Mine[5]);
        AssertReferences(fixture.Db, fixture.Mine, fixture.Theirs, result);
        AssertReadable(fixture.Db, result);
    }

    [Theory]
    [InlineData("original")]
    [InlineData("original_wave2")]
    [InlineData("fan_holo_niji")]
    [InlineData("fan_asoul_vr")]
    public void EveryBuiltInPackReturnsTwoOwnLeadsAndOneOwnAnswerPerDistinctFoe(string pack)
    {
        var db = BattleTests.LoadPack(pack);
        var mine = db.Streamers.Take(6).Select(item => item.Id).ToArray();
        var theirs = db.Streamers.Select(item => item.Id).Where(id => !mine.Contains(id))
            .Concat(mine).Take(6).ToArray();
        string content = db.ContentHash();
        var result = TeamAdvice.For(db, mine, theirs);

        AssertReferences(db, mine, theirs, result);
        Assert.All(Reasons(result), reason =>
        {
            Assert.False(string.IsNullOrWhiteSpace(reason));
            Assert.Matches("[\u4e00-\u9fff]", reason);
            Assert.DoesNotContain("[advice.", reason);
        });
        Assert.Equal(content, db.ContentHash());
    }

    [Fact]
    public void UnusableMainAttackIsExcludedButAvailableFifthAttackChangesTheThreat()
    {
        var fixture = ObviousMatchup();
        var db = fixture.Db;
        var first = db.Streamer(fixture.Theirs[0]);
        var second = db.Streamer(fixture.Theirs[1]);
        db.Skills[first.Skills[0]].Pp = 0;
        second.Type = "song";
        db.Skills[second.Skills[0]].Type = "song";
        db.Skills[second.Skills[0]].Power = 100;
        Assert.Equal(second.Id, TeamAdvice.For(db, fixture.Mine, fixture.Theirs).BanSuggestion.Id);

        first.Fifth = "advice_fifth_pressure";
        db.Skills[first.Fifth] = Attack(first.Fifth, "song", 300, 1);
        Assert.Equal(first.Id, TeamAdvice.For(db, fixture.Mine, fixture.Theirs).BanSuggestion.Id);

        db.Skills[first.Fifth].Pp = 0;
        Assert.Equal(second.Id, TeamAdvice.For(db, fixture.Mine, fixture.Theirs).BanSuggestion.Id);
    }

    [Fact]
    public void CompletelyEqualMatchupsUseRosterOrderIncludingAnswerTies()
    {
        var fixture = ObviousMatchup();
        fixture.Db.Chart.Clear();
        foreach (var streamer in fixture.Db.Streamers)
        {
            streamer.Type = "talk";
            var skill = fixture.Db.Skills[streamer.Skills[0]];
            skill.Type = "talk";
            skill.Power = 40;
        }
        var mine = fixture.Mine.Reverse().ToArray();
        var theirs = fixture.Theirs.Reverse().ToArray();
        var result = TeamAdvice.For(fixture.Db, mine, theirs);

        Assert.Equal(mine.Take(2), result.LeadSuggestions.Select(item => item.Id));
        Assert.Equal(theirs.Take(2), result.Threats.Select(item => item.Id));
        Assert.Equal(theirs[0], result.BanSuggestion.Id);
        Assert.All(result.Answers, answer => Assert.Equal(mine[0], answer.AnswerId));
        AssertReferences(fixture.Db, mine, theirs, result);
    }

    [Fact]
    public void ReversingTheViewerKeepsAllIndicesAndAnswersRelativeToThatViewer()
    {
        var fixture = ObviousMatchup();
        var result = TeamAdvice.For(fixture.Db, fixture.Theirs, fixture.Mine);

        Assert.Equal(fixture.Mine[5], result.BanSuggestion.Id);
        AssertReferences(fixture.Db, fixture.Theirs, fixture.Mine, result);
        AssertReadable(fixture.Db, result);
    }

    [Fact]
    public void RepeatedCallsAcceptReadOnlyRostersAndDoNotChangeContentOrAnExistingBattle()
    {
        var fixture = ObviousMatchup();
        var mine = Array.AsReadOnly(fixture.Mine);
        var theirs = Array.AsReadOnly(fixture.Theirs);
        string[] originalMine = mine.ToArray(), originalTheirs = theirs.ToArray();
        var battle = new Battle(fixture.Db, new IList<string>[] { mine, theirs }, new[] { "我方", "对方" }, 923);
        var expected = battle.Clone();
        string content = fixture.Db.ContentHash();
        string definitions = Snapshot(fixture.Db);
        ulong state = battle.StateHash();
        string record = battle.Record.ToJson();
        string logs = Snapshot(battle.Log);
        string events = Snapshot(battle.Events);

        var first = TeamAdvice.For(fixture.Db, mine, theirs);
        var second = TeamAdvice.For(fixture.Db, mine, theirs);
        Assert.Equal(Snapshot(first), Snapshot(second));
        Assert.NotSame(first, second);
        Assert.NotSame(first.LeadSuggestions, second.LeadSuggestions);
        Assert.NotSame(first.Threats, second.Threats);
        Assert.NotSame(first.Answers, second.Answers);
        Assert.Equal(originalMine, mine);
        Assert.Equal(originalTheirs, theirs);
        Assert.Equal(content, fixture.Db.ContentHash());
        Assert.Equal(definitions, Snapshot(fixture.Db));
        Assert.Equal(state, battle.StateHash());
        Assert.Equal(record, battle.Record.ToJson());
        Assert.Equal(logs, Snapshot(battle.Log));
        Assert.Equal(events, Snapshot(battle.Events));
        for (int index = 0; index < 8; index++) Assert.Equal(expected.Random(), battle.Random());
    }

    [Fact]
    public void ResultsRemainSnapshotsAfterRosterAndDisplayNameChanges()
    {
        var fixture = ObviousMatchup();
        var original = TeamAdvice.For(fixture.Db, fixture.Mine, fixture.Theirs);
        string before = Snapshot(original);
        AssertReadOnly(original.LeadSuggestions);
        AssertReadOnly(original.Threats);
        AssertReadOnly(original.Answers);

        string[] labels = { "新甲", "新乙", "新丙", "新丁", "新戊", "新己", "新庚", "新辛", "新壬", "新癸", "新子", "新丑" };
        for (int i = 0; i < fixture.Db.Streamers.Count; i++) fixture.Db.Streamers[i].Name = labels[i];
        Array.Reverse(fixture.Mine);
        Array.Reverse(fixture.Theirs);

        Assert.Equal(before, Snapshot(original));
        var rebuilt = TeamAdvice.For(fixture.Db, fixture.Mine, fixture.Theirs);
        AssertReferences(fixture.Db, fixture.Mine, fixture.Theirs, rebuilt);
        Assert.NotEqual(original.BanSuggestion.Name, rebuilt.BanSuggestion.Name);
        Assert.Equal(original.BanSuggestion.Id, rebuilt.BanSuggestion.Id);
        Assert.Equal(before, Snapshot(original));
    }

    [Fact]
    public void InjectedLocaleControlsEveryReasonAndScopeWithoutChangingRecommendations()
    {
        var fixture = ObviousMatchup();
        string content = fixture.Db.ContentHash();
        var original = TeamAdvice.For(fixture.Db, fixture.Mine, fixture.Theirs);
        string before = Snapshot(original);
        var chinese = Loc.Current;
        var texts = new Dictionary<string, string>
        {
            { "advice.scope", "注入的范围说明" },
            { "advice.reason.ban", "注入的禁用理由" },
            { "advice.reason.lead", "注入的首发理由" },
            { "advice.reason.threat", "注入的威胁理由" },
            { "advice.reason.answer", "注入的应对理由" }
        };
        try
        {
            Loc.Current = Loc.FromJson(JsonSerializer.Serialize(new { language = "test", texts }), chinese);
            var translated = TeamAdvice.For(fixture.Db, fixture.Mine, fixture.Theirs);
            Assert.Equal(texts["advice.scope"], translated.Scope);
            Assert.Equal(texts["advice.reason.ban"], translated.BanSuggestion.Reason);
            Assert.All(translated.LeadSuggestions, item => Assert.Equal(texts["advice.reason.lead"], item.Reason));
            Assert.All(translated.Threats, item => Assert.Equal(texts["advice.reason.threat"], item.Reason));
            Assert.All(translated.Answers, item => Assert.Equal(texts["advice.reason.answer"], item.Reason));
            Assert.Equal(original.BanSuggestion.Id, translated.BanSuggestion.Id);
            Assert.Equal(original.LeadSuggestions.Select(item => item.Id), translated.LeadSuggestions.Select(item => item.Id));
            Assert.Equal(original.Threats.Select(item => item.Id), translated.Threats.Select(item => item.Id));
            Assert.Equal(original.Answers.Select(item => (item.ThreatId, item.AnswerId)),
                translated.Answers.Select(item => (item.ThreatId, item.AnswerId)));
            AssertReferences(fixture.Db, fixture.Mine, fixture.Theirs, translated);
        }
        finally { Loc.Current = chinese; }
        Assert.Equal(content, fixture.Db.ContentHash());
        Assert.Equal(before, Snapshot(original));
        Assert.Equal(before, Snapshot(TeamAdvice.For(fixture.Db, fixture.Mine, fixture.Theirs)));
    }

    [Fact]
    public void InvalidRostersAreRejectedWithoutRepairingOrMutatingThemAndMirrorsAreAllowed()
    {
        var fixture = ObviousMatchup();
        string content = fixture.Db.ContentHash();
        Assert.Throws<ArgumentNullException>(() => TeamAdvice.For(null, fixture.Mine, fixture.Theirs));
        Assert.ThrowsAny<ArgumentException>(() => TeamAdvice.For(fixture.Db, null, fixture.Theirs));
        Assert.ThrowsAny<ArgumentException>(() => TeamAdvice.For(fixture.Db, fixture.Mine, null));
        var invalidRosters = new[]
        {
            Array.Empty<string>(), fixture.Mine.Take(5).ToArray(), fixture.Mine.Concat(new[] { fixture.Theirs[0] }).ToArray(),
            new[] { fixture.Mine[0], fixture.Mine[0], fixture.Mine[2], fixture.Mine[3], fixture.Mine[4], fixture.Mine[5] },
            new[] { fixture.Mine[0], fixture.Mine[1], fixture.Mine[2], fixture.Mine[3], fixture.Mine[4], "advice_missing_member" }
        };
        foreach (var invalid in invalidRosters)
        {
            string[] before = invalid.ToArray();
            Assert.ThrowsAny<ArgumentException>(() => TeamAdvice.For(fixture.Db, invalid, fixture.Theirs));
            Assert.ThrowsAny<ArgumentException>(() => TeamAdvice.For(fixture.Db, fixture.Mine, invalid));
            Assert.Equal(before, invalid);
        }
        AssertReferences(fixture.Db, fixture.Mine, fixture.Mine, TeamAdvice.For(fixture.Db, fixture.Mine, fixture.Mine));
        Assert.Equal(content, fixture.Db.ContentHash());
    }

    static string Snapshot<T>(T value) { return JsonSerializer.Serialize(value, JsonOptions); }

    static IEnumerable<string> Reasons(TeamAdviceResult result)
    {
        return new[] { result.Scope, result.BanSuggestion.Reason }
            .Concat(result.LeadSuggestions.Select(item => item.Reason))
            .Concat(result.Threats.Select(item => item.Reason))
            .Concat(result.Answers.Select(item => item.Reason));
    }

    static void AssertReadable(ContentDb db, TeamAdviceResult result)
    {
        var internalIds = db.Streamers.Select(item => item.Id).Concat(db.Types.Keys)
            .Concat(db.Streamers.SelectMany(item => item.Skills)).Concat(db.Streamers.Select(item => item.Fifth)).Distinct();
        foreach (string reason in Reasons(result))
        {
            Assert.False(string.IsNullOrWhiteSpace(reason));
            Assert.Matches("[\u4e00-\u9fff]", reason);
            Assert.DoesNotContain("[advice.", reason);
            Assert.DoesNotContain("_", reason);
            foreach (string id in internalIds) Assert.DoesNotContain(id, reason);
        }
    }

    static void AssertReferences(ContentDb db, IList<string> mine, IList<string> theirs, TeamAdviceResult result)
    {
        Assert.NotNull(result);
        Assert.Equal(2, result.LeadSuggestions.Count);
        Assert.Equal(2, result.Threats.Count);
        Assert.Equal(2, result.Answers.Count);
        Assert.Equal(2, result.LeadSuggestions.Select(item => item.Id).Distinct().Count());
        Assert.Equal(2, result.Threats.Select(item => item.Id).Distinct().Count());
        AssertEntry(db, theirs, result.BanSuggestion);
        Assert.Equal(result.Threats[0].Id, result.BanSuggestion.Id);
        Assert.All(result.LeadSuggestions, entry => AssertEntry(db, mine, entry));
        Assert.All(result.Threats, entry => AssertEntry(db, theirs, entry));
        for (int i = 0; i < result.Threats.Count; i++)
        {
            var threat = result.Threats[i];
            var answer = result.Answers[i];
            Assert.Equal(threat.Id, answer.ThreatId);
            Assert.Equal(threat.Index, answer.ThreatIndex);
            Assert.Equal(threat.Name, answer.ThreatName);
            Assert.InRange(answer.AnswerIndex, 0, mine.Count - 1);
            Assert.Equal(mine[answer.AnswerIndex], answer.AnswerId);
            Assert.Equal(db.Streamer(answer.AnswerId).Name, answer.AnswerName);
        }
    }

    static void AssertEntry(ContentDb db, IList<string> roster, TeamAdviceEntry entry)
    {
        Assert.NotNull(entry);
        Assert.InRange(entry.Index, 0, roster.Count - 1);
        Assert.Equal(roster[entry.Index], entry.Id);
        Assert.Equal(db.Streamer(entry.Id).Name, entry.Name);
    }

    static void AssertReadOnly<T>(IReadOnlyList<T> entries)
    {
        if (entries is IList<T> writable)
        {
            Assert.True(writable.IsReadOnly);
            Assert.Throws<NotSupportedException>(() => writable.Clear());
        }
    }

    sealed class Fixture
    {
        public ContentDb Db;
        public string[] Mine, Theirs;
    }

    static Fixture ObviousMatchup()
    {
        var db = BattleTests.LoadPack("original");
        var mine = db.Streamers.Take(6).Select(item => item.Id).ToArray();
        var theirs = db.Streamers.Skip(6).Take(6).Select(item => item.Id).ToArray();
        db.Chart.Clear();
        db.Chart["song"] = new Dictionary<string, double> { { "talk", 2 }, { "game", 0.5 } };
        db.Chart["game"] = new Dictionary<string, double> { { "song", 2 } };
        for (int i = 0; i < 4; i++)
        {
            string id = "advice_idle_" + i;
            db.Skills[id] = new SkillDef { Id = id, Name = "静待开场", Type = "talk", Cat = "status", Power = 0, Pp = 5 };
        }
        string[] names = { "青岚", "白露", "紫苏", "星雨", "远山", "护航", "强音", "微光", "小舟", "静叶", "暖泉", "浅月" };
        for (int i = 0; i < db.Streamers.Count; i++)
        {
            var streamer = db.Streamers[i];
            streamer.Name = names[i];
            streamer.Type = i < 6 ? "talk" : "art";
            streamer.Type2 = null;
            streamer.Persona = "punctual";
            foreach (string stat in ContentDb.StatKeys) streamer.Stats[stat] = 100;
            streamer.Stats["hp"] = 300;
            string attack = "advice_attack_" + i;
            db.Skills[attack] = Attack(attack, streamer.Type, i < 6 ? 40 : 8, 5);
            streamer.Skills = new List<string> { attack, "advice_idle_0", "advice_idle_1", "advice_idle_2" };
            streamer.Fifth = "advice_idle_3";
        }
        var counter = db.Streamer(mine[5]);
        counter.Type = "game";
        db.Skills[counter.Skills[0]].Type = "game";
        db.Skills[counter.Skills[0]].Power = 60;
        var threat = db.Streamer(theirs[0]);
        threat.Type = "song";
        db.Skills[threat.Skills[0]].Type = "song";
        db.Skills[threat.Skills[0]].Power = 70;
        return new Fixture { Db = db, Mine = mine, Theirs = theirs };
    }

    static SkillDef Attack(string id, string track, int power, int pp)
    {
        return new SkillDef { Id = id, Name = "公开演出", Type = track, Cat = "talent", Power = power, Pp = pp };
    }
}
