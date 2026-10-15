using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using StreamDuel.Core;
using Xunit;

public class T41_BattleViewModelEdgeTests
{
    static Battle Make(string lead0 = "bell", string lead1 = "kei", BattleOptions options = null, ContentDb db = null)
    {
        db = db ?? BattleTests.LoadPack("original");
        var team0 = new[] { lead0 }.Concat(db.Streamers.Select(s => s.Id).Where(id => id != lead0).Take(5)).ToArray();
        var team1 = new[] { lead1 }.Concat(db.Streamers.Select(s => s.Id).Where(id => id != lead1).Take(5)).ToArray();
        return new Battle(db, new IList<string>[] { team0, team1 }, new[] { "甲方", "乙方" },
            options ?? new BattleOptions { Seed = 41 });
    }

    static string LogJson(Battle battle)
    {
        return JsonSerializer.Serialize(battle.Log, new JsonSerializerOptions { IncludeFields = true });
    }

    static void AssertSidesMatch(Battle battle, BattleViewModel view)
    {
        Assert.Equal(battle.Turn, view.Turn);
        Assert.Equal(battle.Phase, view.Phase);
        Assert.Equal(2, view.Sides.Length);
        for (int side = 0; side < 2; side++)
        {
            Assert.Equal(battle.Sides[side].Name, view.Sides[side].Name);
            Assert.Equal(battle.Sides[side].Active, view.Sides[side].Active);
            Assert.Equal(battle.Sides[side].Team.Count, view.Sides[side].Team.Count);
            for (int index = 0; index < battle.Sides[side].Team.Count; index++)
            {
                var fighter = battle.Sides[side].Team[index];
                var actual = view.Sides[side].Team[index];
                var persona = battle.PersonaOf(fighter);
                Assert.Equal(fighter.Def.Id, actual.Id);
                Assert.Equal(fighter.Name, actual.Name);
                Assert.Equal(Describe.Tracks(battle.Db, fighter.Def), actual.Tracks);
                Assert.Equal(fighter.Hp, actual.Hp);
                Assert.Equal(fighter.MaxHp, actual.MaxHp);
                Assert.Equal(fighter.Pct, actual.Pct);
                Assert.Equal(fighter.Fainted, actual.Fainted);
                Assert.Equal(persona.Id, actual.PersonaId);
                Assert.Equal(persona.Name, actual.PersonaName);
                Assert.Equal(persona.Desc, actual.PersonaDescription);
                Assert.Equal(fighter.Stages.OrderBy(pair => pair.Key), actual.Stages.OrderBy(pair => pair.Key));
            }
        }
    }

    [Fact]
    public void BothViewersKeepSideIdentityAndFollowActualSwitchesAndDamage()
    {
        var battle = Make();
        battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Skill(0));
        Assert.True(battle.Active(0).Hp < battle.Active(0).MaxHp);
        battle.ResolveTurn(BattleAction.SwitchTo(1), BattleAction.SwitchTo(2));

        for (int viewer = 0; viewer < 2; viewer++)
        {
            var view = BattleViewModel.From(battle, viewer);
            AssertSidesMatch(battle, view);
            Assert.Equal(battle.Active(viewer).Skills.Select(slot => slot.Id), view.Skills.Select(skill => skill.Id));
            Assert.Equal(battle.LegalFor(viewer).Switches, view.SwitchTargets);
        }
        Assert.Equal(1, BattleViewModel.From(battle, 0).Sides[0].Active);
        Assert.Equal(2, BattleViewModel.From(battle, 1).Sides[1].Active);
    }

    [Fact]
    public void RepeatedViewsPreserveStateDefinitionsRandomSequenceAndHistory()
    {
        var battle = Make("nyako", "bell");
        battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Skill(0));
        battle.Active(0).Fields.Add(new FieldInst { Id = "charge", Turns = 3 });
        battle.Active(0).FieldOf("hype").Stacks = 3;
        battle.Active(1).Statuses.Add(new StatusInst { Id = "flame", Left = 2 });
        battle.Active(1).Fields.Add(new FieldInst { Id = "shield", Turns = 2 });
        var baseline = battle.Clone();
        ulong stateHash = battle.StateHash();
        var contentHash = battle.Db.ContentHash();
        string record = battle.Record.ToJson();
        string log = LogJson(battle);

        for (int index = 0; index < 4; index++)
        {
            BattleViewModel.From(battle, 0);
            BattleViewModel.From(battle, 1);
        }

        Assert.Equal(stateHash, battle.StateHash());
        Assert.Equal(contentHash, battle.Db.ContentHash());
        Assert.Equal(record, battle.Record.ToJson());
        Assert.Equal(log, LogJson(battle));
        Assert.Equal(3, battle.Active(0).FieldOf("hype").Stacks);
        Assert.Equal(3, battle.Active(0).FieldOf("charge").Turns);
        for (int index = 0; index < 12; index++) Assert.Equal(baseline.Random(), battle.Random());
    }

    [Fact]
    public void ViewCollectionsAndNestedValuesAreIndependentSnapshots()
    {
        var battle = Make("nyako", "bell");
        var fighter = battle.Active(0);
        fighter.Hp = fighter.MaxHp - 11;
        fighter.Stages["atk"] = 2;
        fighter.Statuses.Add(new StatusInst { Id = "flame", Left = 2 });
        fighter.Fields.Add(new FieldInst { Id = "hype", Turns = -1, Stacks = 3 });
        var first = BattleViewModel.From(battle, 0);
        var second = BattleViewModel.From(battle, 0);
        var saved = first.Sides[0].Team[0];
        int hp = saved.Hp;
        string name = saved.Name;
        string skillName = first.Skills[0].Name;
        string effectText = first.Skills[0].Effects[0].Text;

        fighter.Hp--;
        fighter.Stages["atk"] = -1;
        fighter.Statuses[0].Left = 1;
        fighter.Fields[0].Stacks = 1;
        fighter.Def.Name = "快照后的名字";
        battle.Db.Skills[fighter.Skills[0].Id].Name = "快照后的技能名";
        battle.Sides[0].Name = "快照后的队名";
        Assert.Equal(hp, saved.Hp);
        Assert.Equal(name, saved.Name);
        Assert.Equal(2, saved.Stages["atk"]);
        Assert.Equal(2, saved.Statuses.Single(chip => chip.IsStatus).Turns);
        Assert.Equal(3, saved.Statuses.Single(chip => !chip.IsStatus).Stacks);
        Assert.Equal(skillName, first.Skills[0].Name);
        Assert.Equal("甲方", first.Sides[0].Name);

        ulong hash = battle.StateHash();
        saved.Stages["atk"] = 6;
        saved.Statuses[0].Name = "只改视图芯片";
        saved.Statuses[0].Turns = 99;
        saved.Statuses.Clear();
        first.Skills[0].Effects[0].Text = "只改视图说明";
        first.Skills[0].Effects.Clear();
        first.Skills.Clear();
        first.SwitchTargets.Clear();
        first.NodeStrip[0].HasContent = !first.NodeStrip[0].HasContent;
        first.Sides[0].Team.Clear();

        Assert.Equal(hash, battle.StateHash());
        Assert.Equal(-1, fighter.Stages["atk"]);
        Assert.Single(fighter.Statuses);
        Assert.Single(fighter.Fields);
        Assert.Equal(6, battle.Sides[0].Team.Count);
        Assert.Equal(6, second.Sides[0].Team.Count);
        Assert.Equal(2, second.Sides[0].Team[0].Stages["atk"]);
        Assert.Equal(2, second.Sides[0].Team[0].Statuses.Count);
        Assert.Equal(2, second.Sides[0].Team[0].Statuses.Single(chip => chip.IsStatus).Turns);
        Assert.Equal(effectText, second.Skills[0].Effects[0].Text);
        Assert.Equal(5, second.Skills.Count);
        Assert.NotEmpty(second.SwitchTargets);
        Assert.NotEqual(first.NodeStrip[0].HasContent, second.NodeStrip[0].HasContent);
    }

    [Fact]
    public void SkillViewsUseModifiedPriorityDamageAndDisplayNamesForEachViewer()
    {
        var battle = Make("bell", "lumi");
        battle.Active(0).Fields.Add(new FieldInst { Id = "early", Turns = 3 });
        battle.Active(0).Fields.Add(new FieldInst { Id = "charge", Turns = 3 });
        battle.Active(0).Stages["spa"] = 1;
        battle.Active(0).Def.Type2 = "tech";
        battle.Active(1).Def.Type2 = "horror";
        battle.Active(1).Fields.Add(new FieldInst { Id = "shield", Turns = 2 });
        battle.Active(0).Skills[0].Pp = 7;
        battle.Active(0).Skills[0].Max = 11;

        for (int viewer = 0; viewer < 2; viewer++)
        {
            var view = BattleViewModel.From(battle, viewer);
            var legal = battle.LegalFor(viewer);
            foreach (var skill in view.Skills)
            {
                var slot = battle.Active(viewer).Skills[skill.Index];
                var definition = battle.Db.Skills[slot.Id];
                var option = legal.Skills.Single(item => item.Index == skill.Index);
                var preview = battle.PreviewDamage(viewer, skill.Index);
                Assert.Equal(slot.Id, skill.Id);
                Assert.Equal(definition.Name, skill.Name);
                Assert.Equal(definition.Type, skill.TrackId);
                Assert.Equal(battle.Db.Types[definition.Type].Name, skill.Track);
                Assert.Equal(definition.Cat, skill.CategoryId);
                Assert.Equal(battle.Db.CatNames[definition.Cat], skill.Category);
                Assert.Equal(definition.Power, skill.Power);
                Assert.Equal(slot.Pp, skill.Pp);
                Assert.Equal(slot.Max, skill.MaxPp);
                Assert.Equal(slot.Fifth, skill.Fifth);
                Assert.Equal(option.Prio.Value, skill.Priority);
                Assert.Equal(option.Usable, skill.Usable);
                Assert.Equal(preview.Eff, skill.Effectiveness);
                Assert.Equal(preview.Min, skill.DamageMin);
                Assert.Equal(preview.Max, skill.DamageMax);
                Assert.Equal(definition.Effects.Count, skill.Effects.Count);
                for (int index = 0; index < definition.Effects.Count; index++)
                {
                    var source = definition.Effects[index];
                    var description = Describe.Effect(battle.Db, source);
                    Assert.Equal(source.At == "pre" ? "命中前" : "命中后", skill.Effects[index].Timing);
                    Assert.Equal(description.Text, skill.Effects[index].Text);
                    Assert.Equal(description.Dispel, skill.Effects[index].Dispel);
                }
            }
        }
        var first = BattleViewModel.From(battle, 0);
        Assert.Equal("歌回/硬核", first.Sides[0].Team[0].Tracks);
        Assert.Equal("杂谈/恐怖", first.Sides[1].Team[0].Tracks);
        Assert.Equal(4, first.Skills.Single(skill => skill.Id == "warm_up").Priority);
        Assert.Equal(4.0, first.Skills.Single(skill => skill.Id == "full_volume").Effectiveness);
        Assert.Equal(0, first.Skills.Single(skill => skill.Id == "go_live_early").DamageMax);
        Assert.True(first.Skills[4].Fifth);

        battle.Turn++;
        battle.Active(0).Fields.Add(new FieldInst { Id = "lag", Turns = 2 });
        Assert.Equal(2, BattleViewModel.From(battle, 0).Skills.Single(skill => skill.Id == "warm_up").Priority);
        Assert.Equal(3, battle.Active(0).FieldOf("charge").Turns);
    }

    [Fact]
    public void ChipsDistinguishControlWeaknessTimedEffectsMarksAndProtection()
    {
        var battle = Make();
        var fighter = battle.Active(0);
        fighter.Statuses.Add(new StatusInst { Id = "disconnect", Left = 1 });
        fighter.Statuses.Add(new StatusInst { Id = "flame", Left = 2 });
        fighter.Fields.Add(new FieldInst { Id = "shield", Turns = 1 });
        fighter.Fields.Add(new FieldInst { Id = "hype", Turns = -1, Stacks = 3 });
        fighter.Fields.Add(new FieldInst { Id = "blackhist", Turns = -1 });
        battle.Sides[0].Team[1].Fields.Add(new FieldInst { Id = "reconnect", Turns = 2 });
        var view = BattleViewModel.From(battle, 0);
        var chips = view.Sides[0].Team[0].Statuses;
        Assert.Equal(5, chips.Count);

        var control = chips.Single(chip => chip.Id == "disconnect");
        Assert.True(control.IsStatus);
        Assert.True(control.IsControl);
        Assert.False(control.CanDispel);
        Assert.Equal(1, control.Turns);
        Assert.Equal("控制类", control.Category);
        Assert.Contains("1", control.Duration);
        Assert.Contains("次", control.Duration);
        Assert.Contains("行动", control.Timing);
        Assert.Contains("不可净化", control.Tooltip);

        var weak = chips.Single(chip => chip.Id == "flame");
        Assert.True(weak.IsStatus);
        Assert.False(weak.IsControl);
        Assert.True(weak.CanDispel);
        Assert.Equal(2, weak.Turns);
        Assert.Equal("弱化类", weak.Category);
        Assert.Contains("2", weak.Duration);
        Assert.Contains("回合", weak.Duration);
        Assert.Contains("可净化", weak.Tooltip);
        Assert.Equal("回合结束", weak.Timing);

        var shield = chips.Single(chip => chip.Id == "shield");
        Assert.False(shield.IsStatus);
        Assert.True(shield.CanDispel);
        Assert.Equal("回合类", shield.Category);
        Assert.Equal(1, shield.Turns);
        Assert.Contains("可被消回合", shield.Tooltip);
        var mark = chips.Single(chip => chip.Id == "hype");
        Assert.False(mark.IsStatus);
        Assert.False(mark.CanDispel);
        Assert.Equal("印记", mark.Category);
        Assert.Equal(-1, mark.Turns);
        Assert.Equal(3, mark.Stacks);
        Assert.Contains("3", mark.Duration);
        Assert.Contains("层", mark.Duration);
        Assert.Contains("清不掉", mark.Tooltip);
        Assert.Contains("直到下场", chips.Single(chip => chip.Id == "blackhist").Duration);
        var protection = view.Sides[0].Team[1].Statuses.Single(chip => chip.Id == "reconnect");
        Assert.False(protection.IsStatus);
        Assert.False(protection.CanDispel);
        Assert.Equal("保护", protection.Category);
        Assert.Equal(2, protection.Turns);

        foreach (var chip in chips.Concat(new[] { protection }))
        {
            Assert.Contains(chip.Name, chip.Tooltip);
            Assert.Contains(chip.Category, chip.Tooltip);
            Assert.Contains(chip.Duration, chip.Tooltip);
            Assert.Contains(chip.Timing, chip.Tooltip);
            Assert.False(string.IsNullOrWhiteSpace(chip.Description));
            Assert.Contains(chip.Description, chip.Tooltip);
        }
    }

    [Fact]
    public void ExhaustionFreePpControlAndSwitchLocksFollowCoreLegality()
    {
        var battle = Make("nyako", "kei");
        foreach (var slot in battle.Active(0).Skills) slot.Pp = 0;
        var free = BattleViewModel.From(battle, 0);
        Assert.True(free.FreePp);
        Assert.False(free.Struggle);
        Assert.All(free.Skills, skill => Assert.True(skill.Usable));

        battle.Sides[0].Team[5].Hp = 0;
        battle.Sides[0].Team[5].Fainted = true;
        var exhausted = BattleViewModel.From(battle, 0);
        Assert.False(exhausted.FreePp);
        Assert.True(exhausted.Struggle);
        Assert.All(exhausted.Skills, skill => Assert.False(skill.Usable));
        Assert.Equal(new[] { 1, 2, 3, 4 }, exhausted.SwitchTargets);

        battle.Active(0).Statuses.Add(new StatusInst { Id = "stall", Left = 1 });
        var controlled = BattleViewModel.From(battle, 0);
        Assert.Equal(battle.LegalFor(0).Controlled, controlled.ControlledReason);
        Assert.False(controlled.Struggle);
        Assert.All(controlled.Skills, skill => Assert.False(skill.Usable));
        Assert.Equal(exhausted.SwitchTargets, controlled.SwitchTargets);
        battle.Active(0).Fields.Add(new FieldInst { Id = "lockin", Turns = 2 });
        Assert.Empty(BattleViewModel.From(battle, 0).SwitchTargets);
        Assert.Equal(battle.LegalFor(1).Switches, BattleViewModel.From(battle, 1).SwitchTargets);
    }

    [Theory]
    [InlineData("original", false)]
    [InlineData("fan_asoul_vr", true)]
    [InlineData("fan_holo_niji", true)]
    public void NoticeOnlyContainsTheFanPackDeclaration(string pack, bool fan)
    {
        var db = BattleTests.LoadPack(pack);
        var battle = Make(db.Streamers[0].Id, db.Streamers[1].Id, db: db);
        var view = BattleViewModel.From(battle, 1);
        if (fan)
        {
            Assert.Equal(db.PackNotice, view.Notice);
            Assert.Contains("非官方同人作品", view.Notice);
        }
        else Assert.Equal("", view.Notice);
    }

    [Fact]
    public void NodeStripHasEightOrderedNodesAndUsesOnlyTheCurrentTurn()
    {
        var battle = Make();
        battle.Turn = 7;
        battle.Log.Clear();
        battle.Log.Add(new LogEntry { Turn = 6, Node = Node.Start, Text = "上回合" });
        battle.Log.Add(new LogEntry { Turn = 7, Node = Node.First, Text = "本回合行动" });
        battle.Log.Add(new LogEntry { Turn = 7, Node = Node.TurnEnd, Text = "本回合结束" });
        battle.Log.Add(new LogEntry { Turn = 7, Node = Node.TurnEnd, Text = "同节点第二行" });
        battle.Log.Add(new LogEntry { Turn = 7, Node = Node.TurnHeader, Text = "回合标题不属于节点条" });
        battle.Log.Add(new LogEntry { Turn = 8, Node = Node.Over, Text = "另一回合" });
        var view = BattleViewModel.From(battle, 1);

        Assert.Equal(new[] { Node.Start, Node.Switch, Node.TurnStart, Node.First, Node.Second, Node.TurnEnd, Node.Replace, Node.Over },
            view.NodeStrip.Select(node => node.Name));
        Assert.Equal(new[] { false, false, false, true, false, true, false, false }, view.NodeStrip.Select(node => node.HasContent));
        battle.Turn = 8;
        var next = BattleViewModel.From(battle, 0);
        Assert.Single(next.NodeStrip, node => node.HasContent);
        Assert.True(next.NodeStrip.Single(node => node.Name == Node.Over).HasContent);
        Assert.True(view.NodeStrip.Single(node => node.Name == Node.First).HasContent);
    }

    [Fact]
    public void BanAndLeadViewsPreserveRemainingFivePersonTeams()
    {
        var battle = Make("nyako", "kei", new BattleOptions { Seed = 41, BanOne = true, LeadPick = true });
        var before = BattleViewModel.From(battle, 0);
        Assert.Equal(Phase.Ban, before.Phase);
        Assert.All(before.Sides, side => Assert.Equal(6, side.Team.Count));
        battle.ChooseBan(0, 5);
        battle.ChooseBan(1, 5);

        var lead = BattleViewModel.From(battle, 1);
        Assert.Equal(Phase.Lead, lead.Phase);
        Assert.All(lead.Sides, side => Assert.Equal(5, side.Team.Count));
        Assert.False(BattleViewModel.From(battle, 0).FreePp);
        AssertSidesMatch(battle, lead);
        string selected0 = battle.Sides[0].Team[2].Def.Id;
        string selected1 = battle.Sides[1].Team[4].Def.Id;
        battle.ChooseLead(0, 2);
        battle.ChooseLead(1, 4);
        var choose = BattleViewModel.From(battle, 1);
        Assert.Equal(Phase.Choose, choose.Phase);
        Assert.Equal(selected0, choose.Sides[0].Team[0].Id);
        Assert.Equal(selected1, choose.Sides[1].Team[0].Id);
        Assert.Equal(battle.LegalFor(1).Switches, choose.SwitchTargets);
        AssertSidesMatch(battle, choose);
        Assert.All(before.Sides, side => Assert.Equal(6, side.Team.Count));
        Assert.All(lead.Sides, side => Assert.Equal(5, side.Team.Count));
    }

    [Fact]
    public void ReplacementAndFinishedViewsMirrorActualBattlePhases()
    {
        var battle = Make();
        battle.Active(0).Hp = 1;
        battle.ResolveTurn(BattleAction.Pass(), BattleAction.Skill(0));
        Assert.Equal(Phase.Replace, battle.Phase);
        var replacement = BattleViewModel.From(battle, 0);
        Assert.Equal(Phase.Replace, replacement.Phase);
        Assert.True(replacement.Sides[0].Team[0].Fainted);
        Assert.Equal(0, replacement.Sides[0].Team[0].Pct);
        AssertSidesMatch(battle, replacement);

        battle.Replace(0, 1);
        battle.Turn = Battle.TurnCap;
        battle.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        Assert.Equal(Phase.Over, battle.Phase);
        var finished = BattleViewModel.From(battle, 1);
        AssertSidesMatch(battle, finished);
        Assert.Equal(Phase.Over, finished.Phase);
        Assert.True(finished.NodeStrip.Single(node => node.Name == Node.Over).HasContent);
    }
}
