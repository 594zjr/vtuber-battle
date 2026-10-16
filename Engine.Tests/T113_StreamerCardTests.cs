using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using StreamDuel.Core;
using Xunit;

[Collection("Localization current")]
public class T113_StreamerCardTests
{
    static readonly string[] StatIds = { "hp", "atk", "def", "spa", "sdef", "spd" };

    static string Snapshot(object value)
    {
        return JsonSerializer.Serialize(value, new JsonSerializerOptions { IncludeFields = true });
    }

    static double Matchup(ContentDb db, string attack, string defense)
    {
        return db.Chart.TryGetValue(attack, out var row) && row.TryGetValue(defense, out double value) ? value : 1;
    }

    [Theory]
    [InlineData("original")]
    [InlineData("original_wave2")]
    [InlineData("fan_holo_niji")]
    [InlineData("fan_asoul_vr")]
    public void EveryStreamerHasReadableIdentityStatsAndExactSkillAndPersonaDescriptions(string pack)
    {
        var db = BattleTests.LoadPack(pack);
        var glossary = Glossary.Build(db);
        string hash = db.ContentHash();
        foreach (var streamer in db.Streamers)
        {
            var card = StreamerCard.Build(db, streamer.Id);
            Assert.Equal(streamer.Id, card.Id);
            Assert.Equal(streamer.Name, card.Name);
            Assert.Equal(db.PackId, card.PackId);
            Assert.Equal(db.PackName, card.PackName);
            Assert.Equal(db.PackNotice, card.Notice);
            Assert.Equal(db.NoticeFor(streamer.Id), card.Notice);
            Assert.All(new[] { card.Id, card.Name, card.PackId, card.PackName, card.Notice },
                value => Assert.False(string.IsNullOrWhiteSpace(value)));
            var tracks = new[] { streamer.Type, streamer.Type2 }.Where(id => id != null).ToArray();
            Assert.Equal(tracks, card.Tracks.Select(track => track.Id));
            foreach (var track in card.Tracks)
            {
                Assert.Equal(db.Types[track.Id].Name, track.Name);
                Assert.Equal(db.Types[track.Id].Symbol, track.Symbol);
                Assert.All(new[] { track.Id, track.Name, track.Symbol },
                    value => Assert.False(string.IsNullOrWhiteSpace(value)));
            }
            Assert.Equal(StatIds, card.Stats.Select(stat => stat.Id));
            foreach (var stat in card.Stats)
            {
                Assert.Equal(db.StatNames[stat.Id], stat.Name);
                Assert.False(string.IsNullOrWhiteSpace(stat.Name));
                Assert.Equal(streamer.Stats[stat.Id], stat.Value);
                Assert.Equal(1 + db.Streamers.Count(other => other.Stats[stat.Id] > stat.Value), stat.Rank);
                Assert.Equal(db.Streamers.Count, stat.Total);
            }
            var persona = db.Personas[streamer.Persona];
            Assert.Equal(persona.Id, card.PersonaId);
            Assert.Equal(persona.Name, card.PersonaName);
            Assert.Equal(Assert.Single(glossary, entry => entry.Category == "persona" && entry.Id == persona.Id).Text,
                card.PersonaText);
            Assert.All(new[] { card.PersonaId, card.PersonaName, card.PersonaText },
                value => Assert.False(string.IsNullOrWhiteSpace(value)));
            Assert.Equal(streamer.Skills.Concat(new[] { streamer.Fifth }), card.Skills.Select(skill => skill.Id));
            Assert.Equal(5, card.Skills.Count);
            for (int index = 0; index < card.Skills.Count; index++)
            {
                var skill = card.Skills[index];
                var source = db.Skills[skill.Id];
                Assert.Equal(source.Name, skill.Name);
                Assert.Equal(source.Type, skill.Type);
                Assert.Equal(db.Types[source.Type].Name, skill.TypeName);
                Assert.Equal(db.Types[source.Type].Symbol, skill.TypeSymbol);
                Assert.Equal(source.Cat, skill.Category);
                Assert.Equal(db.CatNames[source.Cat], skill.CategoryName);
                Assert.All(new[] { skill.Id, skill.Name, skill.Type, skill.TypeName, skill.TypeSymbol,
                    skill.Category, skill.CategoryName }, value => Assert.False(string.IsNullOrWhiteSpace(value)));
                Assert.Equal(source.Power, skill.Power);
                Assert.Equal(source.Acc, skill.Accuracy);
                Assert.Equal(source.Pp, skill.Pp);
                Assert.Equal(source.Prio, skill.Priority);
                Assert.Equal(index == 4, skill.IsFifth);
                Assert.Equal(source.Effects.Count, skill.Effects.Count);
                for (int effectIndex = 0; effectIndex < source.Effects.Count; effectIndex++)
                {
                    var expected = Describe.Effect(db, source.Effects[effectIndex]);
                    var actual = skill.Effects[effectIndex];
                    Assert.Equal(expected.At, actual.At);
                    Assert.Equal(expected.Text, actual.Text);
                    Assert.Equal(expected.Dispel, actual.Dispel);
                    Assert.False(string.IsNullOrWhiteSpace(actual.At));
                    Assert.False(string.IsNullOrWhiteSpace(actual.Text));
                }
            }

            string text = card.ToText();
            Assert.Equal(text, StreamerCard.ToText(card));
            Assert.Contains(card.Name, text);
            Assert.Contains(card.PackName, text);
            Assert.Contains(db.PackNotice, text);
            Assert.Contains(card.PersonaName, text);
            Assert.Contains(card.PersonaText, text);
            Assert.All(card.Tracks, track => { Assert.Contains(track.Name, text); Assert.Contains(track.Symbol, text); });
            Assert.All(card.Stats, stat => Assert.Contains(stat.Name, text));
            Assert.All(card.Skills, skill =>
            {
                Assert.Contains(skill.Name, text);
                Assert.All(skill.Effects, effect =>
                {
                    Assert.Contains(effect.At, text);
                    Assert.Contains(effect.Text, text);
                    if (effect.Dispel != null) Assert.Contains(effect.Dispel, text);
                });
            });
            foreach (string placeholder in new[] { "（未知）", "(未知)", "TODO", "[glossary.", "[describe." })
                Assert.DoesNotContain(placeholder, text);
            // 同人包原始包名和声明含“占位”，必须保留来源原文，只禁止生成器额外引入占位字段。
            Assert.DoesNotContain("占位", text.Replace(card.PackName, "").Replace(card.Notice, ""));
        }
        Assert.Equal(hash, db.ContentHash());
    }

    [Theory]
    [InlineData("original")]
    [InlineData("original_wave2")]
    [InlineData("fan_holo_niji")]
    [InlineData("fan_asoul_vr")]
    public void CompleteMatchupListsAgreeWithTheRulesChartIncludingOmittedNeutralEntries(string pack)
    {
        var db = BattleTests.LoadPack(pack);
        foreach (var streamer in db.Streamers)
        {
            var card = StreamerCard.Build(db, streamer.Id);
            var incoming = new Dictionary<string, double>();
            var outgoing = new Dictionary<string, double>();
            var attackTypes = new Dictionary<string, string>();
            foreach (string id in db.Types.Keys)
            {
                double taken = Matchup(db, id, streamer.Type)
                    * (streamer.Type2 == null ? 1 : Matchup(db, id, streamer.Type2));
                if (taken > 1) incoming.Add(id, taken);
                double primary = Matchup(db, streamer.Type, id);
                double secondary = streamer.Type2 == null ? 1 : Matchup(db, streamer.Type2, id);
                double dealt = Math.Max(primary, secondary);
                if (dealt > 1)
                {
                    outgoing.Add(id, dealt);
                    attackTypes.Add(id, secondary > primary ? streamer.Type2 : streamer.Type);
                }
            }
            Assert.Equal(incoming.Keys.OrderBy(id => id, StringComparer.Ordinal), card.Weaknesses.Select(item => item.Id));
            Assert.Equal(outgoing.Keys.OrderBy(id => id, StringComparer.Ordinal), card.Strengths.Select(item => item.Id));
            foreach (var weakness in card.Weaknesses)
            {
                Assert.Equal(incoming[weakness.Id], weakness.Multiplier);
                Assert.Equal(weakness.Id, weakness.AttackTypeId);
                Assert.Equal(db.Types[weakness.Id].Name, weakness.Name);
                Assert.Equal(db.Types[weakness.Id].Symbol, weakness.Symbol);
            }
            foreach (var strength in card.Strengths)
            {
                Assert.Equal(outgoing[strength.Id], strength.Multiplier);
                Assert.Equal(attackTypes[strength.Id], strength.AttackTypeId);
                Assert.Equal(db.Types[strength.Id].Name, strength.Name);
                Assert.Equal(db.Types[strength.Id].Symbol, strength.Symbol);
            }
        }
    }

    [Fact]
    public void TiedStatsUseCompetitionRanksAndDoNotDependOnRosterOrder()
    {
        var db = BattleTests.LoadPack("original");
        int[] values = { 900, 900, 700, 700, 700, 100, 100, 100, 100, 100, 100, 100 };
        int[] ranks = { 1, 1, 3, 3, 3, 6, 6, 6, 6, 6, 6, 6 };
        string[] ids = db.Streamers.Select(streamer => streamer.Id).ToArray();
        for (int stat = 0; stat < StatIds.Length; stat++)
            for (int index = 0; index < ids.Length; index++)
                db.Streamer(ids[index]).Stats[StatIds[stat]] = values[(index + stat) % values.Length];
        db.Streamers.Reverse();
        for (int index = 0; index < ids.Length; index++)
        {
            var card = StreamerCard.Build(db, ids[index]);
            for (int stat = 0; stat < StatIds.Length; stat++)
            {
                var value = Assert.Single(card.Stats, entry => entry.Id == StatIds[stat]);
                Assert.Equal(values[(index + stat) % values.Length], value.Value);
                Assert.Equal(ranks[(index + stat) % ranks.Length], value.Rank);
                Assert.Equal(12, value.Total);
            }
        }
        Assert.Equal(ids.Reverse(), db.Streamers.Select(streamer => streamer.Id));
    }

    [Theory]
    [InlineData("talk", null, 2)]
    [InlineData("talk", "horror", 4)]
    [InlineData("talk", "song", 1)]
    [InlineData("song", "talk", 1)]
    public void DefensiveMatchupMultipliesBothTracksAndOmitsCancelledWeaknesses(string primary, string secondary, double expected)
    {
        var db = BattleTests.LoadPack("original");
        var streamer = db.Streamers[0];
        streamer.Type = primary;
        streamer.Type2 = secondary;
        var card = StreamerCard.Build(db, streamer.Id);
        Assert.Equal(new[] { primary, secondary }.Where(id => id != null), card.Tracks.Select(track => track.Id));
        if (expected > 1)
            Assert.Equal(expected, Assert.Single(card.Weaknesses, weakness => weakness.Id == "song").Multiplier);
        else Assert.DoesNotContain(card.Weaknesses, weakness => weakness.Id == "song");
    }

    [Fact]
    public void OffensiveMatchupChoosesOneAttackingTrackAndBreaksTiesInFavorOfPrimary()
    {
        var db = BattleTests.LoadPack("original");
        var streamer = db.Streamers[0];
        streamer.Type = "song";
        streamer.Type2 = "game";
        db.Chart.Clear();
        db.Chart["song"] = new Dictionary<string, double> { { "art", 2 }, { "tech", 0.5 }, { "talk", 2 }, { "meme", 0.5 } };
        db.Chart["game"] = new Dictionary<string, double> { { "art", 2 }, { "tech", 4 }, { "meme", 0.5 } };

        var card = StreamerCard.Build(db, streamer.Id);

        Assert.Equal(new[] { "art", "talk", "tech" }, card.Strengths.Select(strength => strength.Id));
        var tied = Assert.Single(card.Strengths, strength => strength.Id == "art");
        Assert.Equal(2, tied.Multiplier);
        Assert.Equal("song", tied.AttackTypeId);
        var secondary = Assert.Single(card.Strengths, strength => strength.Id == "tech");
        Assert.Equal(4, secondary.Multiplier);
        Assert.Equal("game", secondary.AttackTypeId);
        Assert.Equal("song", Assert.Single(card.Strengths, strength => strength.Id == "talk").AttackTypeId);
        Assert.DoesNotContain(card.Strengths, strength => strength.Id == "meme");
    }

    [Fact]
    public void BuildingAndFormattingCardsDoNotChangeContentBattleHistoryOrRandomStream()
    {
        var db = BattleTests.LoadPack("original");
        var ids = db.Streamers.Select(streamer => streamer.Id).ToArray();
        var battle = new Battle(db, new IList<string>[] { ids.Take(6).ToArray(), ids.Skip(6).ToArray() }, new[] { "甲", "乙" }, 113);
        battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Skill(0));
        var baseline = battle.Clone();
        ulong stateHash = battle.StateHash();
        string contentHash = db.ContentHash(), content = Snapshot(db);
        string log = Snapshot(battle.Log), events = Snapshot(battle.Events), record = battle.Record.ToJson();
        foreach (string id in ids)
        {
            var card = StreamerCard.Build(db, id);
            string snapshot = Snapshot(card);
            string text = card.ToText();
            Assert.Equal(text, StreamerCard.Build(db, id).ToText());
            Assert.Equal(text, StreamerCard.ToText(card));
            Assert.Equal(snapshot, Snapshot(card));
        }
        Assert.Equal(contentHash, db.ContentHash());
        Assert.Equal(content, Snapshot(db));
        Assert.Equal(stateHash, battle.StateHash());
        Assert.Equal(log, Snapshot(battle.Log));
        Assert.Equal(events, Snapshot(battle.Events));
        Assert.Equal(record, battle.Record.ToJson());
        for (int draw = 0; draw < 8; draw++) Assert.Equal(baseline.Random(), battle.Random());
    }

    [Fact]
    public void CardCollectionsAndNestedDescriptionsAreIndependentSnapshotsThatCanBeRebuilt()
    {
        var db = BattleTests.LoadPack("original");
        var source = db.Streamer("bell");
        source.Type2 = "tech";
        var first = StreamerCard.Build(db, source.Id);
        var second = StreamerCard.Build(db, source.Id);
        string secondSnapshot = Snapshot(second), secondText = second.ToText(), content = Snapshot(db);
        first.Name = "快照的名字";
        first.Tracks[0].Name = "快照赛道";
        first.Tracks[0].Symbol = "新符号";
        first.Stats[0].Value = -1;
        first.Stats[0].Rank = -1;
        first.Skills[0].Power = -1;
        first.Skills.First(skill => skill.Effects.Count > 0).Effects[0].Text = "快照效果";
        first.Weaknesses[0].Multiplier = 99;
        first.Strengths[0].AttackTypeId = "快照攻击赛道";
        first.Tracks.Clear();
        first.Stats.Clear();
        first.Skills.Clear();
        first.Weaknesses.Clear();
        first.Strengths.Clear();
        Assert.Equal(content, Snapshot(db));
        Assert.Equal(secondSnapshot, Snapshot(second));
        Assert.Equal(secondText, second.ToText());

        source.Name = "修改后的内容名";
        source.Stats["hp"] += 37;
        db.Types[source.Type].Symbol = "替换符号";
        db.Skills[source.Skills[0]].Power += 29;
        Assert.Equal(secondSnapshot, Snapshot(second));
        Assert.Equal(secondText, second.ToText());
        var rebuilt = StreamerCard.Build(db, source.Id);
        Assert.Equal(source.Name, rebuilt.Name);
        Assert.Equal(source.Stats["hp"], Assert.Single(rebuilt.Stats, stat => stat.Id == "hp").Value);
        Assert.Equal("替换符号", rebuilt.Tracks[0].Symbol);
        Assert.Equal(db.Skills[source.Skills[0]].Power, rebuilt.Skills[0].Power);
    }

    [Fact]
    public void InvalidInputsFailExplicitlyInsteadOfReturningPlaceholderCards()
    {
        var db = BattleTests.LoadPack("original");
        Assert.Throws<ArgumentNullException>(() => StreamerCard.Build(null, "bell"));
        Assert.Throws<ArgumentNullException>(() => StreamerCard.Build(db, null));
        Assert.Throws<ArgumentException>(() => StreamerCard.Build(db, ""));
        Assert.Throws<ArgumentException>(() => StreamerCard.Build(db, " \t "));
        Assert.Contains("no_such_streamer", Assert.Throws<KeyError>(() => StreamerCard.Build(db, "no_such_streamer")).Message);
        Assert.Throws<ArgumentNullException>(() => StreamerCard.ToText(null));
    }

    [Fact]
    public void RebuildingUsesCurrentPersonaParametersAndReferencedNamesInsteadOfStaleDescriptions()
    {
        var db = BattleTests.LoadPack("original");
        string[] ids = { "shiori", "buck", "kasumi" };
        var before = ids.Select(id => StreamerCard.Build(db, id)).ToArray();
        string[] snapshots = before.Select(card => Snapshot(card)).ToArray();
        string[] texts = before.Select(card => card.ToText()).ToArray();
        db.Personas["healing"].Params["frac"] = 0.37;
        db.Personas["healing"].Name = "更新后的疗愈人设";
        db.Personas["trollback"].Params["chance"] = 17;
        db.Personas["beauty"].Params["turns"] = 9;
        db.Statuses["flame"].Name = "铃音标记";
        db.Fields["filter"].Name = "折叠护罩";
        foreach (string persona in new[] { "healing", "trollback", "beauty" })
            db.Personas[persona].Desc = "陈旧人设说明不能显示";

        var healing = StreamerCard.Build(db, "shiori");
        var contact = StreamerCard.Build(db, "buck");
        var entering = StreamerCard.Build(db, "kasumi");

        Assert.Equal("更新后的疗愈人设", healing.PersonaName);
        Assert.Contains("37%", healing.PersonaText);
        Assert.Contains("回复", healing.PersonaText);
        Assert.Contains("铃音标记", contact.PersonaText);
        Assert.Contains("17", contact.PersonaText);
        Assert.DoesNotContain("炎上", contact.PersonaText);
        Assert.Contains("折叠护罩", entering.PersonaText);
        Assert.Contains("9", entering.PersonaText);
        Assert.DoesNotContain("美颜滤镜", entering.PersonaText);
        foreach (var rebuilt in new[] { healing, contact, entering })
        {
            Assert.DoesNotContain("陈旧人设说明不能显示", rebuilt.PersonaText);
            Assert.Contains(rebuilt.PersonaText, rebuilt.ToText());
        }
        for (int index = 0; index < before.Length; index++)
        {
            Assert.Equal(snapshots[index], Snapshot(before[index]));
            Assert.Equal(texts[index], before[index].ToText());
        }
    }

    [Fact]
    public void MixedRosterUsesCurrentCombinedRanksAndPreservesEachStreamersSourceNotice()
    {
        var original = BattleTests.LoadPack("original");
        var fan = BattleTests.LoadPack("fan_holo_niji");
        var mixed = ContentDb.Merge(original, fan);
        string originalSnapshot = Snapshot(original), fanSnapshot = Snapshot(fan);
        Assert.NotEqual(original.PackNotice, fan.PackNotice);
        foreach (var source in new[] { original, fan })
        {
            string otherNotice = source == original ? fan.PackNotice : original.PackNotice;
            foreach (var member in source.Streamers)
            {
                var card = StreamerCard.Build(mixed, member.Id);
                Assert.Equal(mixed.PackId, card.PackId);
                Assert.Equal(mixed.PackName, card.PackName);
                Assert.Equal(source.PackNotice, card.Notice);
                Assert.Contains(source.PackNotice, card.ToText());
                Assert.DoesNotContain(otherNotice, card.ToText());
                Assert.All(card.Stats, stat =>
                {
                    Assert.Equal(original.Streamers.Count + fan.Streamers.Count, stat.Total);
                    Assert.Equal(1 + mixed.Streamers.Count(other => other.Stats[stat.Id] > stat.Value), stat.Rank);
                });
            }
        }
        Assert.Equal(originalSnapshot, Snapshot(original));
        Assert.Equal(fanSnapshot, Snapshot(fan));
    }

    [Fact]
    public void PolicyRemovedStreamerCannotBeDisplayedAndRanksUseTheRemainingRoster()
    {
        string content = Path.Combine(AppContext.BaseDirectory, "Content");
        string rules = File.ReadAllText(Path.Combine(content, "StreamDuelRules.json"));
        string pack = File.ReadAllText(Path.Combine(content, "Packs", "original.json"));
        var original = ContentDb.FromJson(rules, pack);
        var policy = PackPolicy.FromJson("{\"disabledStreamers\":[\"bell\"],\"reason\":{\"bell\":\"请求下架\"}}");
        var filtered = ContentDb.FromJson(rules, pack, policy);
        var error = Assert.Throws<KeyError>(() => StreamerCard.Build(filtered, "bell"));
        Assert.Contains("该主播已下架", error.Message);
        Assert.Contains("请求下架", error.Message);
        foreach (var member in filtered.Streamers)
        {
            var card = StreamerCard.Build(filtered, member.Id);
            Assert.All(card.Stats, stat =>
            {
                Assert.Equal(11, stat.Total);
                Assert.Equal(1 + filtered.Streamers.Count(other => other.Stats[stat.Id] > stat.Value), stat.Rank);
            });
        }
        filtered.Streamers.Add(original.Streamer("bell"));
        Assert.Contains("该主播已下架", Assert.Throws<KeyError>(() => StreamerCard.Build(filtered, "bell")).Message);
        Assert.Equal("bell", StreamerCard.Build(original, "bell").Id);
    }
}
