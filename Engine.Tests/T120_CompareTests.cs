using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using StreamDuel.Core;
using Xunit;

[Collection("Localization current")]
public class T120_CompareTests
{
    static readonly string[] StatIds = { "hp", "atk", "def", "spa", "sdef", "spd" };

    static string Snapshot(object value)
    {
        return JsonSerializer.Serialize(value, new JsonSerializerOptions { IncludeFields = true });
    }

    static Dictionary<string, int> ZeroStages()
    {
        return ContentDb.StatKeys.ToDictionary(id => id, id => 0);
    }

    static Battle Opening(ContentDb db, string attacker, string defender)
    {
        var battle = new Battle(db, new IList<string>[] { new[] { attacker }, new[] { defender } },
            new[] { "甲", "乙" }, 120);
        foreach (int side in new[] { 0, 1 })
            foreach (string id in ContentDb.StatKeys) battle.Active(side).Stages[id] = 0;
        return battle;
    }

    static void AssertRange(Battle.DamagePreview expected, Battle.DamagePreview actual)
    {
        Assert.Equal(expected.Min, actual.Min);
        Assert.Equal(expected.Max, actual.Max);
        Assert.Equal(expected.Eff, actual.Eff);
        Assert.Equal(expected.Notes, actual.Notes);
    }

    static void AssertDirection(ContentDb db, string attackerId, string defenderId,
        IList<StreamerSkillComparison> skills, IList<StreamerTrackComparison> tracks)
    {
        var attacker = db.Streamer(attackerId);
        var defender = db.Streamer(defenderId);
        var ids = attacker.Skills.Concat(new[] { attacker.Fifth }).ToArray();
        var opening = Opening(db, attackerId, defenderId);
        Assert.Equal(ids, skills.Select(skill => skill.Id));
        Assert.Equal(5, skills.Count);
        for (int index = 0; index < ids.Length; index++)
        {
            var actual = skills[index];
            var definition = db.Skills[ids[index]];
            var expected = DamageCalc.Range(db, attackerId, index, defenderId, ZeroStages(), ZeroStages());
            Assert.Equal(index, actual.Index);
            Assert.Equal(definition.Name, actual.Name);
            Assert.Equal(index == 4, actual.IsFifth);
            Assert.Equal(definition.Prio, actual.BasePriority);
            var priority = opening.PrioOf(0, definition);
            Assert.Equal(priority.Value, actual.Priority);
            Assert.Equal(priority.Parts, actual.PriorityNotes);
            AssertRange(expected, actual.Range);
            int hp = defender.Stats["hp"];
            Assert.Equal(expected.Max <= 0 ? (int?)null : (int)Math.Ceiling((double)hp / expected.Max), actual.MinHits);
            Assert.Equal(expected.Min <= 0 ? (int?)null : (int)Math.Ceiling((double)hp / expected.Min), actual.MaxHits);
        }
        var attackTypes = new[] { attacker.Type, attacker.Type2 }.Where(type => type != null).ToArray();
        Assert.Equal(attackTypes, tracks.Select(track => track.Id));
        foreach (var track in tracks)
        {
            Assert.Equal(db.Types[track.Id].Name, track.Name);
            Assert.Equal(db.Types[track.Id].Symbol, track.Symbol);
            Assert.Equal(db.TypeMul(track.Id, defender.Type, defender.Type2), track.Multiplier);
        }
    }

    [Theory]
    [InlineData("original")]
    [InlineData("original_wave2")]
    [InlineData("fan_asoul_vr")]
    [InlineData("fan_holo_niji")]
    public void EveryPackMemberComparesAllFiveSkillsDirectlyInBothDirections(string pack)
    {
        var db = BattleTests.LoadPack(pack);
        string content = Snapshot(db), hash = db.ContentHash();
        for (int index = 0; index < db.Streamers.Count; index++)
        {
            string a = db.Streamers[index].Id, b = db.Streamers[(index + 1) % db.Streamers.Count].Id;
            var comparison = StreamerCompare.Build(db, a, b);
            Assert.Equal(Snapshot(StreamerCard.Build(db, a)), Snapshot(comparison.A));
            Assert.Equal(Snapshot(StreamerCard.Build(db, b)), Snapshot(comparison.B));
            Assert.Equal(db.PackId, comparison.ASourcePackId);
            Assert.Equal(db.PackId, comparison.BSourcePackId);
            Assert.Equal(db.PackName, comparison.ASourcePackName);
            Assert.Equal(db.PackName, comparison.BSourcePackName);
            AssertDirection(db, a, b, comparison.AToBSkills, comparison.AToBTracks);
            AssertDirection(db, b, a, comparison.BToASkills, comparison.BToATracks);
            Assert.Equal(StatIds, comparison.Stats.Select(stat => stat.Id));
            foreach (var stat in comparison.Stats)
            {
                int first = db.Streamer(a).Stats[stat.Id], second = db.Streamer(b).Stats[stat.Id];
                Assert.Equal(first, stat.A);
                Assert.Equal(second, stat.B);
                Assert.Equal(db.StatName(stat.Id), stat.Name);
                Assert.Equal(first == second ? -1 : first > second ? 0 : 1, stat.HigherSide);
            }
            string text = comparison.ToText();
            Assert.Equal(text, StreamerCompare.ToText(comparison));
            foreach (var card in new[] { comparison.A, comparison.B })
            {
                Assert.Contains(card.Name, text);
                Assert.Contains(card.PersonaName, text);
                Assert.Contains(card.PersonaText, text);
                if (card.Notice.Length > 0) Assert.Contains(card.Notice, text);
                Assert.All(card.Skills, skill => Assert.Contains(skill.Name, text));
            }
            foreach (string placeholder in new[] { "（未知）", "(未知)", "TODO", "[compare.", "[describe." })
                Assert.DoesNotContain(placeholder, text);
        }
        Assert.Equal(content, Snapshot(db));
        Assert.Equal(hash, db.ContentHash());
    }

    [Fact]
    public void SixStatsMarkBothWinnersAndTiesInTheirFixedDisplayOrder()
    {
        var db = ControlledContent();
        int[] a = { 100, 300, 20, 40, 60, int.MaxValue };
        int[] b = { 100, 20, 300, 40, 61, 1 };
        int[] higher = { -1, 0, 1, -1, 1, 0 };
        for (int index = 0; index < StatIds.Length; index++)
        {
            db.Streamer("bell").Stats[StatIds[index]] = a[index];
            db.Streamer("kei").Stats[StatIds[index]] = b[index];
        }
        var comparison = StreamerCompare.Build(db, "bell", "kei");
        Assert.Equal(StatIds, comparison.Stats.Select(stat => stat.Id));
        Assert.Equal(a, comparison.Stats.Select(stat => stat.A));
        Assert.Equal(b, comparison.Stats.Select(stat => stat.B));
        Assert.Equal(higher, comparison.Stats.Select(stat => stat.HigherSide));
        Assert.All(comparison.Stats, stat => Assert.Contains(stat.Name, comparison.ToText()));
    }

    [Theory]
    [InlineData("original")]
    [InlineData("original_wave2")]
    [InlineData("fan_asoul_vr")]
    [InlineData("fan_holo_niji")]
    public void SelfComparisonIsSymmetricButOwnsIndependentCardsAndSkillRanges(string pack)
    {
        var db = BattleTests.LoadPack(pack);
        foreach (var member in db.Streamers)
        {
            var comparison = StreamerCompare.Build(db, member.Id, member.Id);
            Assert.Equal(Snapshot(comparison.A), Snapshot(comparison.B));
            Assert.NotSame(comparison.A, comparison.B);
            Assert.All(comparison.Stats, stat => { Assert.Equal(stat.A, stat.B); Assert.Equal(-1, stat.HigherSide); });
            Assert.Equal(Snapshot(comparison.AToBSkills), Snapshot(comparison.BToASkills));
            Assert.Equal(Snapshot(comparison.AToBTracks), Snapshot(comparison.BToATracks));
            for (int index = 0; index < 5; index++)
            {
                Assert.NotSame(comparison.AToBSkills[index], comparison.BToASkills[index]);
                Assert.NotSame(comparison.AToBSkills[index].Range, comparison.BToASkills[index].Range);
                Assert.NotSame(comparison.AToBSkills[index].Range.Notes, comparison.BToASkills[index].Range.Notes);
            }
        }
    }

    [Theory]
    [InlineData("original")]
    [InlineData("original_wave2")]
    [InlineData("fan_asoul_vr")]
    [InlineData("fan_holo_niji")]
    public void SwappingTheTwoMembersExactlySwapsCardsDirectionsAndWinnerMarkers(string pack)
    {
        var db = BattleTests.LoadPack(pack);
        string a = db.Streamers[0].Id, b = db.Streamers[1].Id;
        var forward = StreamerCompare.Build(db, a, b);
        var reverse = StreamerCompare.Build(db, b, a);
        Assert.Equal(Snapshot(forward.A), Snapshot(reverse.B));
        Assert.Equal(Snapshot(forward.B), Snapshot(reverse.A));
        Assert.Equal(Snapshot(forward.AToBSkills), Snapshot(reverse.BToASkills));
        Assert.Equal(Snapshot(forward.BToASkills), Snapshot(reverse.AToBSkills));
        Assert.Equal(Snapshot(forward.AToBTracks), Snapshot(reverse.BToATracks));
        Assert.Equal(Snapshot(forward.BToATracks), Snapshot(reverse.AToBTracks));
        for (int index = 0; index < 6; index++)
        {
            var first = forward.Stats[index]; var second = reverse.Stats[index];
            Assert.Equal(first.Id, second.Id);
            Assert.Equal(first.A, second.B);
            Assert.Equal(first.B, second.A);
            Assert.Equal(first.HigherSide < 0 ? -1 : 1 - first.HigherSide, second.HigherSide);
        }
    }

    [Fact]
    public void TrackComparisonKeepsEachAttackingTrackAndMultipliesBothDefendingTracks()
    {
        var db = ControlledContent();
        var a = db.Streamer("bell"); var b = db.Streamer("kei");
        a.Type = "song"; a.Type2 = "game";
        b.Type = "talk"; b.Type2 = "horror";
        db.Chart.Clear();
        db.Chart["song"] = new Dictionary<string, double> { { "talk", 2 }, { "horror", 2 } };
        db.Chart["game"] = new Dictionary<string, double> { { "talk", 2 }, { "horror", 0.5 } };
        db.Chart["talk"] = new Dictionary<string, double> { { "song", 0.5 }, { "game", 0.5 } };
        var comparison = StreamerCompare.Build(db, a.Id, b.Id);
        Assert.Equal(new[] { "song", "game" }, comparison.AToBTracks.Select(track => track.Id));
        Assert.Equal(new[] { 4.0, 1.0 }, comparison.AToBTracks.Select(track => track.Multiplier));
        Assert.Equal(new[] { "talk", "horror" }, comparison.BToATracks.Select(track => track.Id));
        Assert.Equal(new[] { 0.25, 1.0 }, comparison.BToATracks.Select(track => track.Multiplier));
        AssertDirection(db, a.Id, b.Id, comparison.AToBSkills, comparison.AToBTracks);
        AssertDirection(db, b.Id, a.Id, comparison.BToASkills, comparison.BToATracks);
    }

    [Theory]
    [InlineData("original_wave2")]
    [InlineData("fan_asoul_vr")]
    [InlineData("fan_holo_niji")]
    public void MixedMembersRetainTheirRealSourcePacksAndNoticesRegardlessOfMergeOrder(string otherPack)
    {
        var original = BattleTests.LoadPack("original");
        var other = BattleTests.LoadPack(otherPack);
        string a = original.Streamers[0].Id, b = other.Streamers[0].Id;
        var mixed = ContentDb.Merge(original, other);
        var reverse = ContentDb.Merge(other, original);
        var comparison = StreamerCompare.Build(mixed, a, b);
        Assert.Equal(original.PackId, comparison.ASourcePackId);
        Assert.Equal(original.PackName, comparison.ASourcePackName);
        Assert.Equal(other.PackId, comparison.BSourcePackId);
        Assert.Equal(other.PackName, comparison.BSourcePackName);
        Assert.Equal(original.PackNotice, comparison.A.Notice);
        Assert.Equal(other.PackNotice, comparison.B.Notice);
        Assert.Equal(mixed.PackId, comparison.A.PackId);
        Assert.Equal(mixed.PackId, comparison.B.PackId);
        AssertDirection(mixed, a, b, comparison.AToBSkills, comparison.AToBTracks);
        AssertDirection(mixed, b, a, comparison.BToASkills, comparison.BToATracks);
        Assert.Equal(Snapshot(comparison), Snapshot(StreamerCompare.Build(reverse, a, b)));
        string text = comparison.ToText();
        foreach (var source in new[] { original, other })
        {
            Assert.Contains(source.PackId, text);
            Assert.Contains(source.PackName, text);
            Assert.Contains(source.PackNotice, text);
        }
    }

    [Fact]
    public void SourceIdentityDoesNotDependOnStreamerPrefixesOrDistinctNotices()
    {
        var first = BattleTests.LoadPack("original");
        var second = BattleTests.LoadPack("fan_asoul_vr");
        first.PackId = "unrelated-first"; first.PackName = "来源甲";
        second.PackId = "unrelated-second"; second.PackName = "来源乙";
        first.PackNotice = second.PackNotice = "同一份声明";
        var db = ContentDb.Merge(second, first);
        string a = first.Streamers[0].Id, b = second.Streamers[0].Id;
        var comparison = StreamerCompare.Build(db, a, b);
        Assert.Equal(first.PackId, comparison.ASourcePackId);
        Assert.Equal(first.PackName, comparison.ASourcePackName);
        Assert.Equal(second.PackId, comparison.BSourcePackId);
        Assert.Equal(second.PackName, comparison.BSourcePackName);
        Assert.Equal("同一份声明", comparison.A.Notice);
        Assert.Equal("同一份声明", comparison.B.Notice);
        string snapshot = Snapshot(comparison), hash = db.ContentHash();
        first.PackId = "changed-first"; first.PackName = "改名甲"; first.PackNotice = "改后的声明";
        second.PackId = "changed-second"; second.PackName = "改名乙"; second.Streamers.Clear();
        Assert.Equal("unrelated-first", db.SourcePackIdFor(a));
        Assert.Equal("来源甲", db.SourcePackNameFor(a));
        Assert.Equal("unrelated-second", db.SourcePackIdFor(b));
        Assert.Equal("来源乙", db.SourcePackNameFor(b));
        Assert.Equal(snapshot, Snapshot(StreamerCompare.Build(db, a, b)));
        Assert.Equal(hash, db.ContentHash());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(22)]
    [InlineData(27)]
    [InlineData(100)]
    [InlineData(int.MaxValue)]
    public void HitCountsRoundUpAtBothDamageEndpointsWithoutIntegerOverflow(int hp)
    {
        var db = ControlledContent();
        db.Streamer("kei").Stats["hp"] = hp;
        var comparison = StreamerCompare.Build(db, "bell", "kei");
        var skill = comparison.AToBSkills[4];
        Assert.Equal(22, skill.Range.Min);
        Assert.Equal(27, skill.Range.Max);
        Assert.Equal((int)Math.Ceiling((double)hp / 27), skill.MinHits);
        Assert.Equal((int)Math.Ceiling((double)hp / 22), skill.MaxHits);
        Assert.True(skill.MinHits >= 1);
        Assert.True(skill.MaxHits >= skill.MinHits);
    }

    [Fact]
    public void ZeroPreviewDoesNotClaimThatFixedDamageCannotKnockOutAndZeroPpStillHasARange()
    {
        var db = ControlledContent();
        db.Skills["t120_status"].Effects.Add(new EffectDef { At = "post", Code = "FIXED_DAMAGE", Target = "foe", N = 100 });
        var comparison = StreamerCompare.Build(db, "bell", "kei");
        var zero = comparison.AToBSkills[2];
        Assert.Equal(0, zero.Range.Min);
        Assert.Equal(0, zero.Range.Max);
        Assert.Null(zero.MinHits);
        Assert.Null(zero.MaxHits);
        Assert.Contains("无可预览的常规伤害，几下击倒不适用", comparison.ToText());
        Assert.DoesNotContain("无法击倒", comparison.ToText());
        Assert.DoesNotContain("不可击倒", comparison.ToText());
        Assert.Equal(0, comparison.A.Skills[3].Pp);
        Assert.True(comparison.AToBSkills[3].Range.Min > 0);
        AssertDirection(db, "bell", "kei", comparison.AToBSkills, comparison.AToBTracks);
        var actualBattle = Opening(db, "bell", "kei");
        actualBattle.ResolveTurn(BattleAction.Skill(2), BattleAction.Pass());
        Assert.Equal(Phase.Over, actualBattle.Phase);
        Assert.Equal(0, actualBattle.Winner);
    }

    [Fact]
    public void OpeningPriorityAndDamageRetainFieldAndPersonaEffects()
    {
        var db = ControlledContent();
        db.Fields["t120_opening"] = new FieldDef { Id = "t120_opening", Name = "开场灯光", Kind = "turn", Prio = 2, DmgTakenMul = 0.5 };
        db.Personas["t120_enter"] = new PersonaDef
        {
            Id = "t120_enter", Name = "开场保护", Code = "T_ENTER_FIELD", Node = Node.Start,
            Field = "t120_opening", Params = { { "turns", 3 } }
        };
        db.Streamer("kei").Persona = "t120_enter";
        db.Streamer("bell").Persona = "punctual";
        db.Skills["t120_strong"].Prio = -2;
        var comparison = StreamerCompare.Build(db, "bell", "kei");
        Assert.Equal(-2, comparison.AToBSkills[4].BasePriority);
        Assert.Equal(Opening(db, "bell", "kei").PrioOf(0, db.Skills["t120_strong"]).Value, comparison.AToBSkills[4].Priority);
        Assert.Contains(comparison.AToBSkills[4].PriorityNotes, note => note.Contains(db.Personas["punctual"].Name));
        Assert.Equal(0, comparison.BToASkills[4].Priority);
        Assert.Contains("开场灯光+2", comparison.BToASkills[4].PriorityNotes);
        Assert.Contains(comparison.AToBSkills[4].Range.Notes, note => note.Contains("开场灯光"));
        AssertDirection(db, "bell", "kei", comparison.AToBSkills, comparison.AToBTracks);
        AssertDirection(db, "kei", "bell", comparison.BToASkills, comparison.BToATracks);
    }

    [Fact]
    public void BuildsAreDeterministicAndDoNotMutateContentExternalBattleHistoryOrRandomState()
    {
        var db = BattleTests.LoadPack("original");
        var external = Opening(db, "bell", "kei");
        external.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());
        var untouched = external.Clone();
        string content = Snapshot(db), hash = db.ContentHash(), state = Snapshot(external);
        var first = StreamerCompare.Build(db, "bell", "kei");
        var second = StreamerCompare.Build(db, "bell", "kei");
        Assert.Equal(Snapshot(first), Snapshot(second));
        Assert.Equal(first.ToText(), second.ToText());
        Assert.Equal(content, Snapshot(db));
        Assert.Equal(hash, db.ContentHash());
        Assert.Equal(state, Snapshot(external));
        Assert.Equal(untouched.StateHash(), external.StateHash());
        for (int index = 0; index < 5; index++) Assert.Equal(untouched.Random(), external.Random());
    }

    [Fact]
    public void ComparisonResultsAreIndependentSnapshotsOfCardsStatsRangesAndPriorityNotes()
    {
        var db = ControlledContent();
        var first = StreamerCompare.Build(db, "bell", "kei");
        var second = StreamerCompare.Build(db, "bell", "kei");
        string content = Snapshot(db), snapshot = Snapshot(second), text = second.ToText();
        first.A.Name = "副本甲";
        first.B.Stats[0].Value = 1;
        first.Stats[0].A = 1;
        first.AToBTracks[0].Multiplier = 99;
        first.AToBSkills[0].Range.Min = 999;
        first.AToBSkills[0].Range.Notes.Add("仅修改副本");
        first.BToASkills[0].PriorityNotes.Add("仅修改副本先制");
        first.A.Skills.Clear();
        first.BToASkills.Clear();
        Assert.Equal(content, Snapshot(db));
        Assert.Equal(snapshot, Snapshot(second));
        Assert.Equal(text, second.ToText());
        db.Streamer("bell").Name = "更新后的内容名";
        db.Streamer("bell").Stats["hp"] = 555;
        db.Skills["t120_strong"].Power = 50;
        Assert.Equal(snapshot, Snapshot(second));
        Assert.Equal(text, second.ToText());
        var rebuilt = StreamerCompare.Build(db, "bell", "kei");
        Assert.Equal("更新后的内容名", rebuilt.A.Name);
        Assert.Equal(555, rebuilt.Stats[0].A);
        Assert.True(rebuilt.AToBSkills[4].Range.Min > second.AToBSkills[4].Range.Min);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, -1)]
    [InlineData(1, 0)]
    [InlineData(1, -1)]
    public void NonpositiveMaximumHpIsRejectedWithChineseError(int side, int hp)
    {
        var db = ControlledContent();
        db.Streamer(side == 0 ? "bell" : "kei").Stats["hp"] = hp;
        string before = Snapshot(db);
        AssertChinese(Assert.Throws<ArgumentException>(() => StreamerCompare.Build(db, "bell", "kei")));
        Assert.Equal(before, Snapshot(db));
    }

    [Fact]
    public void InvalidInputsAndMissingIdsProduceChineseErrorsWithoutChangingContent()
    {
        var db = BattleTests.LoadPack("original");
        string before = Snapshot(db);
        AssertChinese(Assert.Throws<ArgumentNullException>(() => StreamerCompare.Build(null, "bell", "kei")));
        AssertChinese(Assert.ThrowsAny<ArgumentException>(() => StreamerCompare.Build(db, null, "kei")));
        AssertChinese(Assert.ThrowsAny<ArgumentException>(() => StreamerCompare.Build(db, "bell", null)));
        foreach (string empty in new[] { "", " \t " })
        {
            AssertChinese(Assert.ThrowsAny<ArgumentException>(() => StreamerCompare.Build(db, empty, "kei")));
            AssertChinese(Assert.ThrowsAny<ArgumentException>(() => StreamerCompare.Build(db, "bell", empty)));
        }
        AssertChinese(Assert.Throws<KeyError>(() => StreamerCompare.Build(db, "missing_a", "kei")), "missing_a");
        AssertChinese(Assert.Throws<KeyError>(() => StreamerCompare.Build(db, "bell", "missing_b")), "missing_b");
        AssertChinese(Assert.Throws<ArgumentNullException>(() => StreamerCompare.ToText(null)));
        Assert.Equal(before, Snapshot(db));
    }

    [Fact]
    public void DisabledStreamerCannotBypassComparisonByBeingManuallyRestoredToTheRoster()
    {
        string resources = Path.Combine(AppContext.BaseDirectory, "Content");
        string rules = File.ReadAllText(Path.Combine(resources, "StreamDuelRules.json"));
        string pack = File.ReadAllText(Path.Combine(resources, "Packs", "original.json"));
        var policy = PackPolicy.FromJson("{\"disabledStreamers\":[\"bell\"],\"reason\":{\"bell\":\"请求下架\"}}");
        var db = ContentDb.FromJson(rules, pack, policy);
        db.Streamers.Add(BattleTests.LoadPack("original").Streamer("bell"));
        string before = Snapshot(db);
        foreach (bool first in new[] { false, true })
        {
            var error = Assert.Throws<KeyError>(() => StreamerCompare.Build(db, first ? "bell" : "kei", first ? "kei" : "bell"));
            Assert.Contains("该主播已下架", error.Message);
            Assert.Contains("请求下架", error.Message);
        }
        Assert.Contains("请求下架", Assert.Throws<KeyError>(() => db.SourcePackIdFor("bell")).Message);
        Assert.Contains("请求下架", Assert.Throws<KeyError>(() => db.SourcePackNameFor("bell")).Message);
        Assert.Equal(before, Snapshot(db));
    }

    static void AssertChinese(Exception error, string id = null)
    {
        Assert.Matches("[\\u4e00-\\u9fff]", error.Message);
        if (id != null) Assert.Contains(id, error.Message);
    }

    static ContentDb ControlledContent()
    {
        var db = BattleTests.LoadPack("original");
        db.Personas["t120_neutral"] = new PersonaDef
        {
            Id = "t120_neutral", Name = "稳定发挥", Code = "T_INTERACT_REDUCE", Node = Node.First,
            Desc = "互动伤害保持原值", Params = { { "mul", 1 } }
        };
        db.Skills["t120_weak"] = new SkillDef { Id = "t120_weak", Name = "常规输出", Type = "game", Cat = "talent", Power = 10, Pp = 10 };
        db.Skills["t120_middle"] = new SkillDef { Id = "t120_middle", Name = "稳定输出", Type = "game", Cat = "talent", Power = 20, Pp = 10 };
        db.Skills["t120_status"] = new SkillDef { Id = "t120_status", Name = "状态动作", Type = "game", Cat = "status", Pp = 10 };
        db.Skills["t120_empty"] = new SkillDef { Id = "t120_empty", Name = "耗尽输出", Type = "game", Cat = "talent", Power = 30, Pp = 0 };
        db.Skills["t120_strong"] = new SkillDef { Id = "t120_strong", Name = "必杀输出", Type = "game", Cat = "talent", Power = 25, Pp = 5 };
        foreach (var member in db.Streamers)
        {
            member.Type = "song";
            member.Type2 = null;
            member.Persona = "t120_neutral";
            member.Stats["hp"] = 100;
            foreach (string id in ContentDb.StatKeys) member.Stats[id] = 100;
            member.Skills = new List<string> { "t120_weak", "t120_middle", "t120_status", "t120_empty" };
            member.Fifth = "t120_strong";
        }
        return db;
    }
}
