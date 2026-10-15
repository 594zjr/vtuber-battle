using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using StreamDuel.Core;
using Xunit;

public class T22_DualTrackEdgeTests
{
    static readonly string[] Team0 = { "bell", "enka", "shiori", "lumi", "hane", "momo" };
    static readonly string[] Team1 = { "kei", "yuan", "nyako", "buck", "kasumi", "bee" };

    static Battle Make(ContentDb db, uint seed = 7, bool leadPick = false)
    {
        return new Battle(db, new IList<string>[] { Team0, Team1 }, new[] { "你", "对手" }, seed, leadPick);
    }

    static ContentDb ReadSecondTrack(string jsonValue)
    {
        string root = Path.Combine(AppContext.BaseDirectory, "Content");
        string rules = File.ReadAllText(Path.Combine(root, "StreamDuelRules.json"));
        string pack = File.ReadAllText(Path.Combine(root, "Packs", "original.json"));
        pack = pack.Replace("\"id\": \"bell\"", "\"id\": \"bell\", \"type2\": " + jsonValue);
        return ContentDb.FromJson(rules, pack);
    }

    static Battle DamageBattle(string defense, string defense2, uint seed = 7,
        string attack = "art", string attack2 = "song")
    {
        var db = BattleTests.LoadPack("original");
        db.Streamer("bell").Type = attack;
        db.Streamer("bell").Type2 = attack2;
        db.Streamer("bell").Stats["spa"] = 100;
        db.Streamer("kei").Type = defense;
        db.Streamer("kei").Type2 = defense2;
        db.Streamer("kei").Stats["sdef"] = 100;
        db.Streamer("kei").Stats["hp"] = 10000;
        db.Skills["dual_track_hit"] = new SkillDef
        {
            Id = "dual_track_hit", Name = "双赛道测试", Type = "song", Cat = "talent", Power = 98, Pp = 20
        };
        db.Streamer("bell").Skills[0] = "dual_track_hit";
        db.Validate();
        return Make(db, seed);
    }

    [Fact]
    public void OptionalSecondTrackLoadsFromJsonAndDisplaysBothLocalizedNames()
    {
        var db = ReadSecondTrack("\"talk\"");

        Assert.Equal("talk", db.Streamer("bell").Type2);
        Assert.Equal("歌回/杂谈", Describe.Tracks(db, db.Streamer("bell")));
        Assert.Null(db.Streamer("kei").Type2);
        Assert.Equal("硬核", Describe.Tracks(db, db.Streamer("kei")));
        Assert.Null(ReadSecondTrack("null").Streamer("bell").Type2);
    }

    [Theory]
    [InlineData("song")]
    [InlineData("missing_track")]
    [InlineData("")]
    public void JsonLoadingRejectsDuplicateOrUnknownSecondTracksWithTheStreamerId(string type2)
    {
        var error = Assert.Throws<KeyError>(() => ReadSecondTrack("\"" + type2 + "\""));

        Assert.Contains("bell", error.Message);
    }

    [Theory]
    [InlineData("talk", "horror", 4.0, 510, 600)]
    [InlineData("talk", "meme", 1.0, 127, 150)]
    [InlineData("song", "meme", 0.25, 31, 37)]
    public void DefensiveProductsApplyToDamageAndStatusPreviewMetadata(string first, string second,
        double effectiveness, int min, int max)
    {
        var b = DamageBattle(first, second);

        Assert.Equal(effectiveness, b.Db.TypeMul("song", first, second));
        Assert.Equal(effectiveness, b.Db.TypeMul("song", second, first));
        Assert.Equal(b.Db.TypeMul("song", first), b.Db.TypeMul("song", first, null));
        var preview = b.PreviewDamage(0, 0);
        Assert.Equal(effectiveness, preview.Eff);
        Assert.Equal(min, preview.Min);
        Assert.Equal(max, preview.Max);

        var skill = b.Db.Skills["dual_track_hit"];
        skill.Power = 0;
        skill.Cat = "status";
        var statusPreview = b.PreviewDamage(0, 0);
        Assert.Equal(effectiveness, statusPreview.Eff);
        Assert.Equal(0, statusPreview.Min);
        Assert.Equal(0, statusPreview.Max);
    }

    [Theory]
    [InlineData("art", null, 100)]
    [InlineData("song", null, 150)]
    [InlineData("art", "song", 150)]
    [InlineData("song", "art", 150)]
    public void EitherOwnTrackGivesExactlyOneSameTrackBonus(string first, string second, int max)
    {
        var b = DamageBattle("tech", null, attack: first, attack2: second);

        Assert.Equal(max, b.PreviewDamage(0, 0).Max);
    }

    [Theory]
    [InlineData("talk", "horror")]
    [InlineData("talk", "meme")]
    [InlineData("song", "meme")]
    public void DualTrackPreviewBoundsRealNonCriticalDamageWithoutChangingTheBattle(string first, string second)
    {
        int nonCritical = 0;
        for (uint seed = 1; seed <= 8; seed++)
        {
            var b = DamageBattle(first, second, seed);
            var control = DamageBattle(first, second, seed);
            var preview = b.PreviewDamage(0, 0);
            int hp = b.Active(1).Hp;
            int logCount = b.Log.Count;
            b.PreviewDamage(0, 0);
            Assert.Equal(logCount, b.Log.Count);
            Assert.Equal(hp, b.Active(1).Hp);
            Assert.Equal(20, b.Active(0).Skills[0].Pp);

            b.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());
            control.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());

            Assert.Equal(control.Log.Select(l => l.Node + l.Text), b.Log.Select(l => l.Node + l.Text));
            Assert.Equal(control.Record.ToJson(), b.Record.ToJson());
            Assert.Equal(control.Random(), b.Random());
            if (!b.Log.Any(l => l.Kind == "dmg" && l.Text.Contains("暴击")))
            {
                Assert.InRange(hp - b.Active(1).Hp, preview.Min, preview.Max);
                nonCritical++;
            }
        }
        Assert.True(nonCritical > 0);
    }

    static ContentDb AiContent(string scenario)
    {
        var db = BattleTests.LoadPack("original");
        foreach (string type in new[] { "game", "song", "art" })
        {
            string id = "dual_ai_" + type;
            db.Skills[id] = new SkillDef { Id = id, Name = "对位测试", Type = type, Cat = "talent", Power = 40, Pp = 20 };
        }
        foreach (var streamer in db.Streamers)
        {
            streamer.Type = "art";
            streamer.Type2 = null;
            streamer.Persona = "pure";
            streamer.Stats["spd"] = 300;
            streamer.Skills = Enumerable.Repeat("dual_ai_game", 4).ToList();
            streamer.Fifth = "dual_ai_game";
        }
        foreach (string id in Team1)
        {
            var foe = db.Streamer(id);
            foe.Type = "tech";
            foe.Stats["spd"] = 200;
            string skill = scenario == "enemy_defense" ? "dual_ai_art" :
                scenario == "own_bonus" ? "dual_ai_game" : "dual_ai_song";
            foe.Skills = Enumerable.Repeat(skill, 4).ToList();
            foe.Fifth = skill;
            if (scenario == "enemy_defense") foe.Type2 = "horror";
        }
        var best = db.Streamer(Team0[2]);
        if (scenario == "own_defense") best.Type2 = "meme";
        else
        {
            if (scenario == "own_bonus")
            {
                foreach (string id in Team0)
                {
                    db.Streamer(id).Skills = Enumerable.Repeat("dual_ai_song", 4).ToList();
                    db.Streamer(id).Fifth = "dual_ai_song";
                }
                best.Type2 = "song";
            }
            else
            {
                best.Skills = Enumerable.Repeat("dual_ai_song", 4).ToList();
                best.Fifth = "dual_ai_song";
            }
        }
        db.Validate();
        return db;
    }

    [Theory]
    [InlineData("own_defense")]
    [InlineData("enemy_defense")]
    [InlineData("own_bonus")]
    public void AiLeadReplacementAndVoluntarySwitchUseBothTracks(string scenario)
    {
        var db = AiContent(scenario);
        var lead = Make(db, leadPick: true);
        var active = Make(db);

        Assert.Equal(2, Ai.ChooseLead(lead, 0));
        Assert.Equal(2, Ai.ChooseReplacement(active, 0));
        active.Active(0).Statuses.Add(new StatusInst { Id = "stall", Left = 1 });
        var action = Ai.Choose(active, 0);
        Assert.Equal(ActionType.Switch, action.Type);
        Assert.Equal(2, action.Index);
    }
}
