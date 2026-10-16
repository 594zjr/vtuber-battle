using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using StreamDuel.Core;
using Xunit;

[Collection("Localization current")]
public class T88_IntegrationReviewTests
{
    static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions { IncludeFields = true };

    static ContentDb Content(int selfDamage = 300)
    {
        var db = BattleTests.LoadPack("original");
        SkillDef Skill(string id, params EffectDef[] effects) => new SkillDef
        {
            Id = id, Name = "审查技能" + id, Type = "talk", Cat = "status", Pp = 100, Acc = 100,
            Effects = effects.ToList()
        };
        db.Skills["review_finish"] = Skill("review_finish",
            new EffectDef { At = "post", Code = "FIXED_DAMAGE", Target = "foe", N = 10000 });
        db.Skills["review_wear"] = Skill("review_wear",
            new EffectDef { At = "post", Code = "FIXED_DAMAGE", Target = "self", N = selfDamage });
        db.Skills["review_delayed"] = Skill("review_delayed",
            new EffectDef { At = "post", Code = "FIXED_DAMAGE", Target = "foe", N = 1 },
            new EffectDef { At = "post", Code = "DELAYED_DAMAGE", Target = "foe", Turns = 1, N = 10000 });
        db.Skills["review_heal"] = Skill("review_heal",
            new EffectDef { At = "post", Code = "HEAL", Target = "self", Frac = 1 });
        db.Skills["review_wait"] = Skill("review_wait");
        for (int i = 0; i < db.Streamers.Count; i++)
        {
            var fighter = db.Streamers[i];
            fighter.Persona = "pure";
            fighter.Stats["hp"] = 400;
            fighter.Stats["spd"] = 200 - i;
            fighter.Skills = new List<string> { "review_finish", "review_wear", "review_delayed", "review_heal" };
            fighter.Fifth = "review_wait";
        }
        db.Validate();
        return db;
    }

    static Battle Make(ContentDb db = null, bool ban = false, bool lead = false, bool doubleHp = false)
    {
        db = db ?? Content();
        return new Battle(db, new IList<string>[]
        {
            db.Streamers.Take(6).Select(f => f.Id).ToArray(),
            db.Streamers.Skip(6).Take(6).Select(f => f.Id).ToArray()
        }, new[] { "审查甲队", "审查乙队" }, new BattleOptions
        {
            Seed = 88, BanOne = ban, LeadPick = lead,
            Modifiers = doubleHp ? new List<string> { "hp_x2" } : null
        });
    }

    static Battle AtPhase(string phase)
    {
        var battle = Make(ban: phase == "ban", lead: phase == "lead");
        if (phase == "replace")
        {
            battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());
            Assert.Equal(Phase.Replace, battle.Phase);
        }
        return battle;
    }

    static (int, string, int) Key(CommentLine line) => (line.EventIndex, line.Text, line.Priority);

    static string Expected(string category, int index, string name = "", string detail = "", string target = "")
    {
        return Loc.Current.T("comment." + category + "." + index % 3, name, detail, 0, target);
    }

    static void AssertReplayAndPurity(Battle battle)
    {
        string record = battle.Record.ToJson();
        string logs = JsonSerializer.Serialize(battle.Log, JsonOptions);
        string events = JsonSerializer.Serialize(battle.Events, JsonOptions);
        string contentHash = battle.Db.ContentHash();
        ulong hash = battle.StateHash();
        var untouched = battle.Clone();
        var all = Commentary.For(battle, 0, battle.Events.Count).Select(Key).ToArray();
        Assert.All(all.GroupBy(line => battle.Events[line.Item1].Turn), group => Assert.InRange(group.Count(), 1, 2));
        Assert.Equal(all, Commentary.For(battle.Clone(), 0, battle.Events.Count).Select(Key));
        var replay = Replay.Run(battle.Db, BattleRecord.FromJson(record));
        Assert.Equal(all, Commentary.For(replay, 0, replay.Events.Count).Select(Key));
        Assert.Equal(hash, replay.StateHash());
        Assert.Equal(record, battle.Record.ToJson());
        Assert.Equal(logs, JsonSerializer.Serialize(battle.Log, JsonOptions));
        Assert.Equal(events, JsonSerializer.Serialize(battle.Events, JsonOptions));
        Assert.Equal(contentHash, battle.Db.ContentHash());
        Assert.Equal(hash, battle.StateHash());
        var afterQueries = battle.Clone();
        for (int i = 0; i < 8; i++) Assert.Equal(untouched.Random(), afterQueries.Random());
    }

    [Theory]
    [InlineData("ban", "surrender")]
    [InlineData("ban", "double")]
    [InlineData("ban", "draw")]
    [InlineData("lead", "surrender")]
    [InlineData("lead", "double")]
    [InlineData("lead", "draw")]
    [InlineData("choose", "surrender")]
    [InlineData("choose", "double")]
    [InlineData("choose", "draw")]
    [InlineData("replace", "surrender")]
    [InlineData("replace", "double")]
    [InlineData("replace", "draw")]
    public void VersionThreeTerminationUsesTheActualEndEventInEverySupportedPhase(string phase, string ending)
    {
        var battle = AtPhase(phase);
        int from = battle.Events.Count;
        if (ending == "surrender") battle.Surrender(0);
        else if (ending == "double") battle.ResolveSurrenders(true, true);
        else { battle.OfferDraw(0); battle.RespondDraw(1, true); }
        Assert.Equal(BattleRecord.TerminationVersion, battle.Record.Version);
        Assert.Equal(ending == "surrender" ? 1 : -1, battle.Winner);
        int end = Enumerable.Range(from, battle.Events.Count - from)
            .Single(index => battle.Events[index].Kind == BattleEventKind.End);
        var line = Assert.Single(Commentary.For(battle, from, battle.Events.Count));
        Assert.Equal(end, line.EventIndex);
        Assert.Equal(120, line.Priority);
        Assert.Equal(Expected(ending == "surrender" ? "victory" : "draw", end,
            ending == "surrender" ? battle.Sides[1].Name : ""), line.Text);
        Assert.DoesNotContain(battle.Events.Skip(from), entry => entry.Kind == BattleEventKind.Faint);
        if (phase == "ban" || phase == "lead")
        {
            Assert.DoesNotContain(battle.Events, entry => entry.Kind == BattleEventKind.Enter);
            Assert.Single(Commentary.For(battle, 0, battle.Events.Count));
        }
        AssertReplayAndPurity(battle);
    }

    [Theory]
    [InlineData("ban")]
    [InlineData("lead")]
    [InlineData("choose")]
    [InlineData("replace")]
    public void RejectedDrawDoesNotInventVictoryOrReplaceExistingCommentary(string phase)
    {
        var battle = AtPhase(phase);
        var before = Commentary.For(battle, 0, battle.Events.Count).Select(Key).ToArray();
        battle.OfferDraw(0);
        battle.RespondDraw(1, false);
        Assert.NotEqual(Phase.Over, battle.Phase);
        Assert.DoesNotContain(battle.Events, entry => entry.Kind == BattleEventKind.End);
        Assert.Equal(before, Commentary.For(battle, 0, battle.Events.Count).Select(Key));
        AssertReplayAndPurity(battle);
    }

    [Theory]
    [InlineData(false, 300, "comeback")]
    [InlineData(false, 299, "knockout")]
    [InlineData(true, 600, "comeback")]
    [InlineData(true, 599, "knockout")]
    public void QuarterHpBoundaryUsesMatchMaximumAndReorderedHistoricalAttacker(bool doubleHp, int cost, string category)
    {
        var battle = Make(Content(cost), ban: true, lead: true, doubleHp: doubleHp);
        battle.OfferDraw(0);
        battle.RespondDraw(1, false);
        battle.ChooseBan(0, 2);
        battle.ChooseBan(1, 1);
        battle.ChooseLead(0, 3);
        battle.ChooseLead(1, 4);
        Assert.Equal(5, battle.Sides[0].Team.Count);
        Assert.Equal(5, battle.Sides[1].Team.Count);
        string attacker = battle.Active(0).Name, target = battle.Active(1).Name;
        battle.ResolveTurn(BattleAction.Skill(1), BattleAction.Pass());
        Assert.Equal((doubleHp ? 800 : 400) - cost, battle.Active(0).Hp);
        battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());
        int faint = Enumerable.Range(0, battle.Events.Count).Single(index => battle.Events[index].Kind == BattleEventKind.Faint);
        battle.Replace(1, 1);
        battle.ResolveTurn(BattleAction.Skill(3), BattleAction.Pass());
        battle.ResolveTurn(BattleAction.SwitchTo(1), BattleAction.Pass());
        var line = Assert.Single(Commentary.For(battle, faint, faint + 1));
        Assert.Equal(Expected(category, faint, category == "comeback" ? attacker : target, target: target), line.Text);
        AssertReplayAndPurity(battle);
    }

    [Fact]
    public void DelayedKnockoutDoesNotReuseTheSkillsDirectHitOrTheAttackersPreviousStreak()
    {
        var battle = Make();
        battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());
        battle.Replace(1, 1);
        battle.ResolveTurn(BattleAction.Skill(1), BattleAction.Pass());
        Assert.Equal(100, battle.Active(0).Hp);
        int from = battle.Events.Count;
        string target = battle.Active(1).Name;
        battle.ResolveTurn(BattleAction.Skill(2), BattleAction.Pass());
        Assert.Contains(battle.Events.Skip(from), entry => entry.Kind == BattleEventKind.Damage && entry.Id == "review_delayed");
        Assert.Contains(battle.Events.Skip(from), entry => entry.Kind == BattleEventKind.Damage && entry.Id == null && entry.Node == Node.TurnEnd);
        int faint = Enumerable.Range(from, battle.Events.Count - from).Single(index => battle.Events[index].Kind == BattleEventKind.Faint);
        var line = Assert.Single(Commentary.For(battle, faint, faint + 1));
        Assert.Equal(90, line.Priority);
        Assert.Equal(Expected("knockout", faint, target, target: target), line.Text);
        AssertReplayAndPurity(battle);
    }

    [Fact]
    public void VersionFourBranchPreservesPrefixCommentaryAndReplaysItsOwnOutcomeWithoutRandomDraws()
    {
        var source = Make();
        source.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        source.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());
        source.Replace(1, 1);
        source.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        source.Surrender(1);
        string originalRecord = source.Record.ToJson();
        var branch = ReplayBranch.Start(source.Db, source.Record, 3, 0, AiLevel.Normal);
        Assert.Equal(BattleRecord.BranchVersion, branch.Record.Version);
        int prefix = branch.Events.Count;
        var prefixLines = Commentary.For(branch, 0, prefix).Select(Key).ToArray();
        branch.ResolveTurn(BattleAction.Skill(1), BattleAction.Pass());
        branch.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());
        branch.Replace(1, 2);
        branch.OfferDraw(0);
        branch.RespondDraw(1, true);
        Assert.Equal(-1, branch.Winner);
        Assert.Equal(0, source.Winner);
        Assert.Equal(prefixLines, Commentary.For(branch, 0, prefix).Select(Key));
        int end = branch.Events.Count - 1;
        Assert.Equal(BattleEventKind.End, branch.Events[end].Kind);
        Assert.Equal(Expected("draw", end), Assert.Single(Commentary.For(branch, end, end + 1)).Text);
        Assert.Equal(originalRecord, source.Record.ToJson());
        AssertReplayAndPurity(branch);
    }
}
