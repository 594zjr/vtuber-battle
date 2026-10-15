using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using StreamDuel.Core;
using Xunit;
using Xunit.Abstractions;

public class T12_HardAiTests
{
    readonly ITestOutputHelper output;

    public T12_HardAiTests(ITestOutputHelper output)
    {
        this.output = output;
    }

    [Fact]
    public void HardWinsAtLeast58PercentOfTheFixed300GameNormalBenchmark()
    {
        var db = BattleTests.LoadPack("original");
        int wins = 0, losses = 0, draws = 0;
        var seatGames = new int[2];
        var seatWins = new int[2];
        for (uint seed = 0; seed < 300; seed++)
        {
            int hardSide = (int)(seed % 2);
            var battle = Play(db, seed, hardSide);
            seatGames[hardSide]++;
            if (battle.Winner == hardSide)
            {
                wins++;
                seatWins[hardSide]++;
            }
            else if (battle.Winner == -1) draws++;
            else losses++;
        }

        output.WriteLine("固定基准：原创包，seed=0..299，按 seed % 12 轮换原名单，前6/后6分队。");
        output.WriteLine("困难胜 {0}/300（{1:P2}），负 {2}，平 {3}；0侧 {4}/150，1侧 {5}/150。",
            wins, wins / 300.0, losses, draws, seatWins[0], seatWins[1]);
        Assert.Equal(new[] { 150, 150 }, seatGames);
        Assert.Equal(300, wins + losses + draws);
        Assert.True(wins >= 174, "固定300局：困难胜 " + wins + "，负 " + losses + "，平 " + draws + "；要求至少174胜");
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(42u)]
    public void SameSeedProducesIdenticalCompleteLogsAndRecords(uint seed)
    {
        var db = BattleTests.LoadPack("original");
        int hardSide = (int)(seed % 2);
        var first = Play(db, seed, hardSide);
        var second = Play(db, seed, hardSide);

        Assert.Equal(first.Winner, second.Winner);
        Assert.Equal(FullLog(first), FullLog(second));
        Assert.Equal(first.Record.ToJson(), second.Record.ToJson());
        Assert.Equal(Snapshot(first), Snapshot(second));
        Assert.Equal(first.Random(), second.Random());
    }

    [Fact]
    public void UniqueGuaranteedFirstKnockoutPreservesAllStateAndRandomness()
    {
        var battle = MakeKnockout(91);
        var untouched = MakeKnockout(91);
        var before = Snapshot(battle);
        int reply = Skill(battle.Active(1), "quick_mic");
        Assert.True(battle.PreviewDamage(1, reply).Min >= battle.Active(0).Hp);

        var action = Ai.Choose(battle, 0, AiLevel.Hard);

        Assert.Equal(before, Snapshot(battle));
        for (int i = 0; i < 8; i++) Assert.Equal(untouched.Random(), battle.Random());
        Assert.Equal(ActionType.Skill, action.Type);
        Assert.Equal(0, battle.PreviewOrder(action, BattleAction.Skill(reply)).First);
        Assert.True(battle.PreviewDamage(0, action.Index).Min >= battle.Active(1).Hp);
        Assert.Equal(100, battle.Db.Skills[battle.Active(0).Skills[action.Index].Id].Acc);

        battle.ResolveTurn(action, BattleAction.Skill(reply));

        Assert.True(battle.Active(1).Fainted);
        Assert.False(battle.Active(0).Fainted);
    }

    [Fact]
    public void FinalEquivalentChoicesUseOnlyOneSeededTieBreak()
    {
        var selected = new HashSet<int>();
        for (uint seed = 0; seed < 16; seed++)
        {
            var battle = MakeKnockout(seed);
            var untouched = MakeKnockout(seed);
            for (int index = 0; index < battle.Active(0).Skills.Count; index++)
            {
                var slot = battle.Active(0).Skills[index];
                slot.Id = "warm_up";
                slot.Pp = index < 2 ? 1 : 0;
                slot.Max = 1;
                slot.Fifth = false;
            }
            string before = Snapshot(battle);

            var action = Ai.Choose(battle, 0, AiLevel.Hard);

            Assert.Equal(ActionType.Skill, action.Type);
            Assert.Contains(action.Index, new[] { 0, 1 });
            Assert.Equal(before, Snapshot(battle));
            untouched.Random();
            for (int i = 0; i < 4; i++) Assert.Equal(untouched.Random(), battle.Random());
            selected.Add(action.Index);
        }
        Assert.Equal(2, selected.Count);
    }

    [Fact]
    public void IncomingFirstKnockoutSwitchesToTheSurvivingBetterMatchup()
    {
        var battle = MakeBattle(12, "bell", "bee");
        var untouched = MakeBattle(12, "bell", "bee");
        battle.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        untouched.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        battle.Active(0).Hp = 1;
        KeepOnlySkill(battle.Active(1), "gear_blade");
        for (int index = 2; index < 6; index++) Faint(battle.Sides[0].Team[index]);
        int reply = Skill(battle.Active(1), "gear_blade");
        Assert.True(battle.PreviewDamage(1, reply).Min >= battle.Active(0).Hp);
        Assert.Equal(1, battle.PreviewOrder(BattleAction.Skill(Skill(battle.Active(0), "full_volume")), BattleAction.Skill(reply)).First);
        Assert.All(battle.LegalFor(0).Skills.Where(skill => skill.Usable),
            skill => Assert.True(battle.PreviewDamage(0, skill.Index).Min < battle.Active(1).Hp));
        string before = Snapshot(battle);

        var action = Ai.Choose(battle, 0, AiLevel.Hard);

        Assert.Equal(before, Snapshot(battle));
        for (int i = 0; i < 4; i++) Assert.Equal(untouched.Random(), battle.Random());
        Assert.Equal(ActionType.Switch, action.Type);
        Assert.Equal(1, action.Index);
        var replacement = battle.Sides[0].Team[action.Index];
        battle.ResolveTurn(action, BattleAction.Skill(reply));
        Assert.Same(replacement, battle.Active(0));
        Assert.False(replacement.Fainted);
        Assert.True(replacement.Hp > 0 && replacement.Hp < replacement.MaxHp);
    }

    [Fact]
    public void LowerDamagePriorityAttackStillTriggersASafeSwitch()
    {
        var battle = MakeBattle(33, "bell", "enka");
        battle.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        KeepOnlySkill(battle.Active(0), "high_note");
        foreach (var slot in battle.Active(1).Skills)
            slot.Pp = slot.Id == "quick_mic" || slot.Id == "yolo_rush" ? 1 : 0;
        for (int index = 2; index < 6; index++) Faint(battle.Sides[0].Team[index]);
        int ownSkill = Skill(battle.Active(0), "high_note");
        int quick = Skill(battle.Active(1), "quick_mic");
        int powerful = Skill(battle.Active(1), "yolo_rush");
        var quickDamage = battle.PreviewDamage(1, quick);
        var powerfulDamage = battle.PreviewDamage(1, powerful);
        battle.Active(0).Hp = quickDamage.Min;

        Assert.True(powerfulDamage.Min > quickDamage.Max);
        Assert.Equal(0, battle.PreviewOrder(BattleAction.Skill(ownSkill), BattleAction.Skill(powerful)).First);
        Assert.Equal(1, battle.PreviewOrder(BattleAction.Skill(ownSkill), BattleAction.Skill(quick)).First);
        Assert.True(battle.PreviewDamage(0, ownSkill).Max < battle.Active(1).Hp);
        battle.Sides[0].Active = 1;
        Assert.True(battle.PreviewDamage(1, quick).Max < battle.Active(0).Hp);
        Assert.True(battle.PreviewDamage(1, powerful).Max < battle.Active(0).Hp);
        battle.Sides[0].Active = 0;
        string before = Snapshot(battle);

        var action = Ai.Choose(battle, 0, AiLevel.Hard);

        Assert.Equal(before, Snapshot(battle));
        Assert.Equal(ActionType.Switch, action.Type);
        Assert.Equal(1, action.Index);
        battle.ResolveTurn(action, BattleAction.Skill(quick));
        Assert.False(battle.Active(0).Fainted);
        Assert.True(battle.Active(0).Hp < battle.Active(0).MaxHp);
    }

    [Fact]
    public void FasterNonlethalAttackDoesNotIgnoreTheFatalReply()
    {
        var db = BattleTests.LoadPack("original");
        var battle = new Battle(db, new IList<string>[]
        {
            new[] { "hane", "kasumi", "enka", "shiori", "lumi", "bell" },
            new[] { "yuan", "nyako", "buck", "kei", "momo", "bee" }
        }, new[] { "甲方", "乙方" }, 17);
        battle.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        battle.Active(0).Hp = 69;
        battle.Active(1).Hp = 340;
        for (int index = 2; index < 6; index++) Faint(battle.Sides[0].Team[index]);
        int reply = Skill(battle.Active(1), "grudge_hit");
        Assert.True(battle.PreviewDamage(1, reply).Min >= battle.Active(0).Hp);
        foreach (var skill in battle.LegalFor(0).Skills.Where(skill => skill.Usable))
        {
            Assert.Equal(0, battle.PreviewOrder(BattleAction.Skill(skill.Index), BattleAction.Skill(reply)).First);
            Assert.True(battle.PreviewDamage(0, skill.Index).Max < battle.Active(1).Hp);
        }
        battle.Sides[0].Active = 1;
        foreach (var skill in battle.LegalFor(1).Skills.Where(skill => skill.Usable))
            Assert.True(battle.PreviewDamage(1, skill.Index).Max < battle.Active(0).Hp);
        battle.Sides[0].Active = 0;
        string before = Snapshot(battle);

        var action = Ai.Choose(battle, 0, AiLevel.Hard);

        Assert.Equal(before, Snapshot(battle));
        Assert.Equal(ActionType.Switch, action.Type);
        Assert.Equal(1, action.Index);
        battle.ResolveTurn(action, BattleAction.Skill(reply));
        Assert.False(battle.Active(0).Fainted);
        Assert.True(battle.Active(0).Hp < battle.Active(0).MaxHp);
        Assert.Equal(69, battle.Sides[0].Team[0].Hp);
    }

    [Fact]
    public void SwitchEvaluationPaysForEntryDamageBeforeTheNextTurn()
    {
        var db = BattleTests.LoadPack("original");
        var battle = new Battle(db, new IList<string>[]
        {
            new[] { "momo", "hane", "shiori", "enka", "buck", "kei" },
            new[] { "bell", "yuan", "nyako", "lumi", "kasumi", "bee" }
        }, new[] { "甲方", "乙方" }, 55);
        battle.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        battle.Active(0).Hp = 100;
        battle.Sides[0].Team[1].Hp = 165;
        for (int index = 3; index < 6; index++) Faint(battle.Sides[0].Team[index]);
        battle.Active(1).Skills[Skill(battle.Active(1), "full_volume")].Pp = 0;
        int entryHit = Skill(battle.Active(1), "high_note");
        int nextHit = Skill(battle.Active(1), "warm_up");
        battle.Sides[0].Active = 1;
        var entryDamage = battle.PreviewDamage(1, entryHit);
        var nextDamage = battle.PreviewDamage(1, nextHit);
        Assert.True(entryDamage.Max < battle.Active(0).Hp);
        Assert.True(battle.Active(0).Hp - entryDamage.Min < nextDamage.Min);
        foreach (var skill in battle.LegalFor(0).Skills.Where(skill => skill.Usable))
            Assert.Equal(1, battle.PreviewOrder(BattleAction.Skill(skill.Index), BattleAction.Skill(nextHit)).First);
        battle.Sides[0].Active = 0;
        string before = Snapshot(battle);

        var action = Ai.Choose(battle, 0, AiLevel.Hard);

        Assert.Equal(before, Snapshot(battle));
        Assert.Equal(ActionType.Switch, action.Type);
        Assert.Equal(2, action.Index);
        battle.ResolveTurn(action, BattleAction.Skill(entryHit));
        Assert.False(battle.Active(0).Fainted);
        battle.ResolveTurn(BattleAction.Pass(), BattleAction.Skill(nextHit));
        Assert.False(battle.Active(0).Fainted);
    }

    [Fact]
    public void SwitchingMustAccountForTheOpponentsActedFirstPowerBonus()
    {
        Battle MakeThreat()
        {
            var db = BattleTests.LoadPack("original");
            var battle = new Battle(db, new IList<string>[]
            {
                new[] { "hane", "bell", "enka", "shiori", "lumi", "kasumi" },
                new[] { "momo", "yuan", "nyako", "buck", "kei", "bee" }
            }, new[] { "甲方", "乙方" }, 7);
            battle.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
            battle.Active(0).Hp = 1;
            battle.Sides[0].Team[1].Hp = 130;
            for (int index = 2; index < 6; index++) Faint(battle.Sides[0].Team[index]);
            KeepOnlySkill(battle.Active(1), "scare_combo");
            return battle;
        }

        var battle = MakeThreat();
        int attack = Skill(battle.Active(1), "scare_combo");
        Assert.Equal(1, battle.PreviewOrder(BattleAction.SwitchTo(1), BattleAction.Skill(attack)).First);
        battle.Sides[0].Active = 1;
        Assert.True(battle.StatVal(battle.Active(0), "spd") > battle.StatVal(battle.Active(1), "spd"));
        Assert.True(battle.PreviewDamage(1, attack).Min >= battle.Active(0).Hp);
        Assert.True(battle.CalcDamage(1, battle.Db.Skills["scare_combo"], 1, true, 1).Dmg < battle.Active(0).Hp);
        battle.Sides[0].Active = 0;

        var forcedSwitch = MakeThreat();
        forcedSwitch.ResolveTurn(BattleAction.SwitchTo(1), BattleAction.Skill(attack));
        Assert.True(forcedSwitch.Sides[0].Team[1].Fainted);
        Assert.Contains(forcedSwitch.Log, entry => entry.Node == Node.First && entry.Kind == "dmg" && entry.Side == 0);
        string before = Snapshot(battle);

        var action = Ai.Choose(battle, 0, AiLevel.Hard);

        Assert.Equal(before, Snapshot(battle));
        Assert.NotEqual(ActionType.Switch, action.Type);
    }

    [Fact]
    public void ASlowerSingleActionControlPreventsTheNextLethalAttack()
    {
        Battle MakeThreat()
        {
            var battle = MakeBattle(1, "nyako", "bell");
            battle.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
            battle.Active(0).Hp = 60;
            for (int index = 1; index < 6; index++) Faint(battle.Sides[0].Team[index]);
            foreach (var slot in battle.Active(0).Skills)
                slot.Pp = slot.Id == "meme_punch" || slot.Id == "sudden_cute" ? 1 : 0;
            KeepOnlySkill(battle.Active(1), "warm_up");
            battle.Active(1).Skills[Skill(battle.Active(1), "warm_up")].Pp = 5;
            return battle;
        }

        var battle = MakeThreat();
        int attack = Skill(battle.Active(1), "warm_up");
        int control = Skill(battle.Active(0), "sudden_cute");
        var incoming = battle.PreviewDamage(1, attack);
        Assert.Equal(1, battle.PreviewOrder(BattleAction.Skill(control), BattleAction.Skill(attack)).First);
        Assert.True(incoming.Max < battle.Active(0).Hp);
        Assert.True(battle.Active(0).Hp - incoming.Min < incoming.Min);
        Assert.True(battle.PreviewDamage(0, Skill(battle.Active(0), "meme_punch")).Max < battle.Active(1).Hp);
        var withoutControl = MakeThreat();
        withoutControl.ResolveTurn(BattleAction.Pass(), BattleAction.Skill(attack));
        Assert.False(withoutControl.Active(0).Fainted);
        withoutControl.ResolveTurn(BattleAction.Pass(), BattleAction.Skill(attack));
        Assert.True(withoutControl.Active(0).Fainted);
        string before = Snapshot(battle);

        var action = Ai.Choose(battle, 0, AiLevel.Hard);

        Assert.Equal(before, Snapshot(battle));
        Assert.Equal(BattleAction.Skill(control).ToString(), action.ToString());
        battle.ResolveTurn(action, BattleAction.Skill(attack));
        Assert.False(battle.Active(0).Fainted);
        Assert.Equal("stall", battle.ControlOf(battle.Active(1)).Id);
        Assert.Equal(1, battle.ControlOf(battle.Active(1)).Left);
        int remainingHp = battle.Active(0).Hp;
        int logStart = battle.Log.Count;
        battle.ResolveTurn(BattleAction.Pass(), BattleAction.Skill(attack));
        Assert.Equal(remainingHp, battle.Active(0).Hp);
        Assert.False(battle.Active(0).Fainted);
        Assert.DoesNotContain(battle.Log.Skip(logStart), entry => entry.Side == 1 && entry.Kind == "act");
    }

    [Fact]
    public void ReplacementPrefersTheCandidateThatSurvivesAndDealsMoreDamage()
    {
        var battle = MakeBattle(21, "bell", "bee");
        var untouched = MakeBattle(21, "bell", "bee");
        KeepOnlySkill(battle.Active(1), "gear_blade");
        for (int index = 0; index < 6; index++)
            if (index != 1 && index != 4) Faint(battle.Sides[0].Team[index]);
        battle.Sides[0].Team[4].Hp = 1;
        battle.Need[0] = true;
        battle.Phase = Phase.Replace;
        int reply = Skill(battle.Active(1), "gear_blade");

        // 两个候选在生存和输出上均无取舍，测试不规定综合评分的内部权重。
        battle.Sides[0].Active = 1;
        int survivingDamage = battle.PreviewDamage(1, reply).Max;
        int strongerAttack = battle.Active(0).Skills.Select((slot, index) => battle.PreviewDamage(0, index).Min).Max();
        Assert.True(survivingDamage < battle.Active(0).Hp);
        battle.Sides[0].Active = 4;
        Assert.True(battle.PreviewDamage(1, reply).Min >= battle.Active(0).Hp);
        int weakerAttack = battle.Active(0).Skills.Select((slot, index) => battle.PreviewDamage(0, index).Max).Max();
        Assert.True(strongerAttack > weakerAttack);
        battle.Sides[0].Active = 0;
        string before = Snapshot(battle);

        int replacement = Ai.ChooseReplacement(battle, 0, AiLevel.Hard);

        Assert.Equal(before, Snapshot(battle));
        for (int i = 0; i < 4; i++) Assert.Equal(untouched.Random(), battle.Random());
        Assert.Equal(1, replacement);
        battle.Replace(0, replacement);
        battle.ResolveTurn(BattleAction.Pass(), BattleAction.Skill(reply));
        Assert.False(battle.Active(0).Fainted);
    }

    static Battle Play(ContentDb db, uint seed, int hardSide)
    {
        var all = db.Streamers.Select(streamer => streamer.Id).ToList();
        int offset = (int)(seed % (uint)all.Count);
        var ids = all.Skip(offset).Concat(all.Take(offset)).ToList();
        var battle = new Battle(db, new IList<string>[] { ids.Take(6).ToList(), ids.Skip(6).Take(6).ToList() },
            new[] { "甲方", "乙方" }, seed);
        var levels = hardSide == 0 ? new[] { AiLevel.Hard, AiLevel.Normal } : new[] { AiLevel.Normal, AiLevel.Hard };
        int guard = 0;
        while (battle.Phase != Phase.Over && guard++ < 500)
        {
            if (battle.Phase == Phase.Choose)
                battle.ResolveTurn(Ai.Choose(battle, 0, levels[0]), Ai.Choose(battle, 1, levels[1]));
            else
                for (int side = 0; side < 2; side++)
                    if (battle.Need[side])
                        battle.Replace(side, side == hardSide ? Ai.ChooseReplacement(battle, side, AiLevel.Hard) : Ai.ChooseReplacement(battle, side));
        }
        Assert.True(battle.Phase == Phase.Over, "seed=" + seed + " 未在500步内结束");
        Assert.InRange(battle.Winner, -1, 1);
        return battle;
    }

    static Battle MakeBattle(uint seed, string ownLead, string foeLead)
    {
        var db = BattleTests.LoadPack("original");
        var ids = db.Streamers.Select(streamer => streamer.Id).ToList();
        return new Battle(db, new IList<string>[]
        {
            new[] { ownLead }.Concat(ids.Where(id => id != ownLead).Take(5)).ToList(),
            new[] { foeLead }.Concat(ids.Where(id => id != foeLead).Take(5)).ToList()
        }, new[] { "甲方", "乙方" }, seed);
    }

    static Battle MakeKnockout(uint seed)
    {
        var battle = MakeBattle(seed, "bell", "enka");
        battle.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        battle.Active(0).Hp = 100;
        battle.Active(1).Hp = 1;
        KeepOnlySkill(battle.Active(1), "quick_mic");
        battle.Active(0).Fields.Add(new FieldInst { Id = "charge", Turns = 3 });
        battle.Active(0).Fields.Add(new FieldInst { Id = "hype", Turns = -1, Stacks = 3 });
        battle.Active(0).Statuses.Add(new StatusInst { Id = "flame", Left = 4 });
        battle.Active(0).Stages["spa"] = 1;
        battle.Sides[0].Team[3].Statuses.Add(new StatusInst { Id = "stall", Left = 2 });
        battle.Sides[0].Team[4].Stages["sdef"] = -2;
        return battle;
    }

    static void KeepOnlySkill(Fighter fighter, string id)
    {
        foreach (var skill in fighter.Skills) skill.Pp = skill.Id == id ? 1 : 0;
    }

    static int Skill(Fighter fighter, string id)
    {
        int index = fighter.Skills.FindIndex(skill => skill.Id == id);
        Assert.True(index >= 0, "场景需要技能 " + id);
        return index;
    }

    static void Faint(Fighter fighter)
    {
        fighter.Hp = 0;
        fighter.Fainted = true;
    }

    static string Snapshot(Battle battle)
    {
        // 同时覆盖公开定义、全部Fighter字段、日志快照与记录；随机流另用对照局逐项比较。
        return JsonSerializer.Serialize(battle, new JsonSerializerOptions { IncludeFields = true });
    }

    static string[] FullLog(Battle battle)
    {
        return battle.Log.Select(entry => string.Join("|", entry.Turn, entry.Node, entry.Kind, entry.Side, entry.Text,
            string.Join(",", entry.SnapActive), string.Join(",", entry.SnapHp[0]), string.Join(",", entry.SnapHp[1]))).ToArray();
    }
}
