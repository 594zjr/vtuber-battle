using System.Collections.Generic;
using System.Linq;
using StreamDuel.Core;
using Xunit;

public class T46_EventIntegrationTests
{
    static readonly IList<string>[] Teams =
    {
        new[] { "bell", "enka", "shiori", "lumi", "hane", "momo" },
        new[] { "kei", "yuan", "nyako", "buck", "kasumi", "bee" }
    };

    static Battle Make(bool openingChoices, params string[] modifiers)
    {
        var db = BattleTests.LoadPack("original");
        db.Personas["modifier_event_neutral"] = new PersonaDef
        {
            Id = "modifier_event_neutral", Name = "测试人设", Code = "T_LOWHP_POWER"
        };
        AddSkill(db, "modifier_event_setup", new EffectDef
        {
            Code = "STAGE", At = "post", Target = "self",
            Changes = new Dictionary<string, int> { { "atk", 2 }, { "def", -2 } }
        });
        AddSkill(db, "modifier_event_boost", new EffectDef
        {
            Code = "STAGE", At = "post", Target = "self",
            Changes = new Dictionary<string, int> { { "atk", 99 }, { "def", -99 } }
        });
        AddSkill(db, "modifier_event_steal", new EffectDef { Code = "STEAL_BOOST", At = "post", Target = "foe" });
        AddSkill(db, "modifier_event_wait");
        AddSkill(db, "modifier_event_fifth");
        for (int side = 0; side < Teams.Length; side++)
            foreach (string id in Teams[side])
            {
                var definition = db.Streamer(id);
                definition.Persona = "modifier_event_neutral";
                definition.Stats["hp"] = 160;
                definition.Stats["spd"] = 200 - side * 100;
                definition.Skills = new List<string>
                {
                    "modifier_event_setup", "modifier_event_boost", "modifier_event_steal", "modifier_event_wait"
                };
                definition.Fifth = "modifier_event_fifth";
            }
        return new Battle(db, Teams, new[] { "甲", "乙" }, new BattleOptions
        {
            Seed = 46, BanOne = openingChoices, LeadPick = openingChoices, Modifiers = modifiers.ToList()
        });
    }

    static void AddSkill(ContentDb db, string id, params EffectDef[] effects)
    {
        var skill = new SkillDef { Id = id, Name = "修饰器事件测试", Type = "talk", Cat = "status", Pp = 30, Prio = 5, Acc = 100 };
        skill.Effects.AddRange(effects);
        db.Skills[id] = skill;
    }

    static void FinishOpening(Battle battle)
    {
        battle.ChooseBan(0, 0);
        battle.ChooseBan(1, 0);
        battle.ChooseLead(0, 2);
        battle.ChooseLead(1, 3);
    }

    static void AssertLogLink(Battle battle, BattleEvent entry, string node, int turn, string kind)
    {
        Assert.InRange(entry.LogIndex, 0, battle.Log.Count - 1);
        Assert.Equal(node, entry.Node);
        Assert.Equal(turn, entry.Turn);
        var log = battle.Log[entry.LogIndex];
        Assert.Equal(entry.Node, log.Node);
        Assert.Equal(entry.Turn, log.Turn);
        Assert.Equal(kind, log.Kind);
    }

    static string EventDump(Battle battle)
    {
        return string.Join("\n", battle.Events.Select(entry =>
            $"{entry.LogIndex}|{entry.Turn}|{entry.Node}|{entry.Kind}|{entry.Side}|{entry.Slot}|{entry.Id}|{entry.Amount}"));
    }

    [Fact]
    public void OpeningHypeEventsIdentifyOnlyTheFinalLeadsAfterBothChoices()
    {
        var battle = Make(true, "hype_start");
        battle.ChooseBan(0, 0);
        battle.ChooseBan(1, 0);
        battle.ChooseLead(0, 2);
        Assert.DoesNotContain(battle.Events, entry => entry.Kind == BattleEventKind.FieldAdd && entry.Id == "hype");

        battle.ChooseLead(1, 3);

        var added = battle.Events.Where(entry => entry.Kind == BattleEventKind.FieldAdd && entry.Id == "hype").ToArray();
        Assert.Equal(2, added.Length);
        for (int side = 0; side < 2; side++)
        {
            var entry = Assert.Single(added, item => item.Side == side);
            Assert.Equal(0, entry.Slot);
            Assert.Equal(1, entry.Amount);
            Assert.Equal(1, battle.Sides[side].Team[entry.Slot].FieldOf("hype").Stacks);
            Assert.Contains(battle.Active(side).Name, battle.Log[entry.LogIndex].Text);
            AssertLogLink(battle, entry, Node.Start, 1, "field");
        }
    }

    [Fact]
    public void SuddenDeathDamageEventsUseActualHpLossAndTheTurnEndLog()
    {
        var battle = Make(false, "sudden_death");
        battle.Turn = 10;
        battle.Active(0).Hp = 3;

        battle.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());

        var losses = battle.Events.Where(entry => entry.Kind == BattleEventKind.Damage).ToArray();
        Assert.Equal(2, losses.Length);
        var clipped = Assert.Single(losses, entry => entry.Side == 0);
        Assert.Equal(3, clipped.Amount);
        Assert.Equal(10, Assert.Single(losses, entry => entry.Side == 1).Amount);
        Assert.Equal(0, battle.Active(0).Hp);
        Assert.Equal(150, battle.Active(1).Hp);
        Assert.Equal(0, battle.Log[clipped.LogIndex].SnapHp[0][clipped.Slot]);
        var faint = Assert.Single(battle.Events, entry => entry.Kind == BattleEventKind.Faint && entry.Side == 0);
        Assert.True(clipped.LogIndex < faint.LogIndex);
        foreach (var entry in losses)
        {
            Assert.Equal(0, entry.Slot);
            AssertLogLink(battle, entry, Node.TurnEnd, 10, "dmg");
            Assert.Contains("人气骤降", battle.Log[entry.LogIndex].Text);
        }
    }

    [Fact]
    public void StageCapAndStealEventsReportOnlyTheirClampedSignedDeltas()
    {
        var battle = Make(false, "stage_cap_3");
        battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Skill(1));
        var capped = battle.Events.Where(entry => entry.Kind == BattleEventKind.StageChange && entry.Side == 1).ToArray();
        Assert.Equal(3, Assert.Single(capped, entry => entry.Id == "atk").Amount);
        Assert.Equal(-3, Assert.Single(capped, entry => entry.Id == "def").Amount);
        int previous = battle.Events.Count;

        battle.ResolveTurn(BattleAction.Skill(2), BattleAction.Pass());

        var transferred = battle.Events.Skip(previous).Where(entry => entry.Kind == BattleEventKind.StageChange).ToArray();
        Assert.Equal(2, transferred.Length);
        var gained = Assert.Single(transferred, entry => entry.Side == 0);
        var lost = Assert.Single(transferred, entry => entry.Side == 1);
        Assert.Equal(1, gained.Amount);
        Assert.Equal(-3, lost.Amount);
        Assert.All(transferred, entry => Assert.Equal("atk", entry.Id));
        Assert.Equal(lost.LogIndex, gained.LogIndex);
        AssertLogLink(battle, gained, Node.First, 2, "stage");
        Assert.Equal(3, battle.Active(0).Stages["atk"]);
        Assert.Equal(0, battle.Active(1).Stages["atk"]);
        Assert.Equal(-3, battle.Active(1).Stages["def"]);
        previous = battle.Events.Count;

        battle.ResolveTurn(BattleAction.Skill(1), BattleAction.Pass());

        var remaining = Assert.Single(battle.Events.Skip(previous), entry => entry.Kind == BattleEventKind.StageChange);
        Assert.Equal("def", remaining.Id);
        Assert.Equal(-1, remaining.Amount);
        Assert.Equal(-3, battle.Active(0).Stages["def"]);
    }

    [Fact]
    public void CombinedModifierEventsAreIsolatedInClonesAndRebuiltByReplay()
    {
        var battle = Make(true, "hype_start", "stage_cap_3", "sudden_death");
        FinishOpening(battle);
        battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Skill(1));
        battle.ResolveTurn(BattleAction.Skill(2), BattleAction.Pass());
        while (battle.Turn <= 10) battle.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        Assert.Contains(battle.Events, entry => entry.Kind == BattleEventKind.Damage && entry.Turn == 10);
        string original = EventDump(battle);
        string record = battle.Record.ToJson();
        var copy = battle.Clone();
        Assert.Equal(original, EventDump(copy));
        var copyHype = copy.Events.First(entry => entry.Kind == BattleEventKind.FieldAdd && entry.Id == "hype");
        var sourceHype = battle.Events.First(entry => entry.Kind == BattleEventKind.FieldAdd && entry.Id == "hype");
        Assert.NotSame(sourceHype, copyHype);

        copyHype.Amount = 99;
        copy.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());

        Assert.Equal(original, EventDump(battle));
        Assert.True(copy.Events.Count > battle.Events.Count);
        var replay = Replay.Run(battle.Db, BattleRecord.FromJson(record));
        Assert.Equal(original, EventDump(replay));
        Assert.Equal(battle.StateHash(), replay.StateHash());
    }
}
