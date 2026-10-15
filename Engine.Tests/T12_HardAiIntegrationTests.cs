using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using StreamDuel.Core;
using Xunit;

public class T12_HardAiIntegrationTests
{
    const string InterruptedSkill = "hard_integration_self_damage";
    const string SafeSkill = "hard_integration_safe_hit";

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(7u)]
    [InlineData(8u)]
    [InlineData(9u)]
    public void HarmlessPreControlDoesNotHideAGuaranteedFirstKnockout(uint seed)
    {
        var db = BattleTests.LoadPack("original");
        var battle = new Battle(db, new IList<string>[]
        {
            new[] { "bell", "enka", "shiori", "lumi", "hane", "kei" },
            new[] { "momo", "yuan", "nyako", "buck", "kasumi", "bee" }
        }, new[] { "甲方", "乙方" }, seed);
        int highNote = battle.Active(0).Skills.FindIndex(slot => slot.Id == "high_note");
        int fullVolume = battle.Active(0).Skills.FindIndex(slot => slot.Id == "full_volume");
        foreach (var slot in battle.Active(0).Skills)
            if (slot.Id != "high_note" && slot.Id != "full_volume") slot.Pp = 0;
        battle.Active(1).Hp = battle.PreviewDamage(0, highNote).Min;

        // 控制概率不影响主体伤害；在条件成立时也不能丢掉必中的击倒机会。
        Assert.True(battle.Active(1).Hp > battle.Active(1).MaxHp * 0.5);
        Assert.Equal(100, db.Skills["high_note"].Acc);
        Assert.Equal(85, db.Skills["full_volume"].Acc);
        Assert.True(battle.PreviewDamage(0, highNote).Min >= battle.Active(1).Hp);
        Assert.True(battle.PreviewDamage(0, fullVolume).Min >= battle.Active(1).Hp);
        Assert.All(battle.LegalFor(1).Skills.Where(option => option.Usable), option =>
            Assert.Equal(0, battle.PreviewOrder(BattleAction.Skill(highNote), BattleAction.Skill(option.Index)).First));

        var action = Ai.Choose(battle, 0, AiLevel.Hard);

        Assert.Equal(ActionType.Skill, action.Type);
        Assert.Equal(highNote, action.Index);
        battle.ResolveTurn(action, BattleAction.Pass());
        Assert.True(battle.Active(1).Fainted);
        Assert.False(battle.Active(0).Fainted);
    }

    [Fact]
    public void GuaranteedPreSelfKnockoutCannotOutscoreAnAttackThatWillActuallyExecute()
    {
        var battle = MakeSelfDamageBattle(false);
        var untouched = MakeSelfDamageBattle(false);
        string before = Snapshot(battle);

        // 主体伤害预览足够击倒，但实际行动会先在自伤处中断。
        Assert.True(battle.PreviewDamage(0, 0).Min >= battle.Active(1).Hp);
        untouched.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());
        Assert.True(untouched.Active(0).Fainted);
        Assert.Equal(1000, untouched.Active(1).Hp);
        Assert.Contains(untouched.Log, entry => entry.Node == Node.First && entry.Text.Contains("技能中断"));

        var randomControl = MakeSelfDamageBattle(false);
        var action = Ai.Choose(battle, 0, AiLevel.Hard);

        Assert.Equal(before, Snapshot(battle));
        for (int i = 0; i < 8; i++) Assert.Equal(randomControl.Random(), battle.Random());
        Assert.Equal(ActionType.Skill, action.Type);
        Assert.Equal(1, action.Index);
        Assert.Equal(SafeSkill, battle.Active(0).Skills[action.Index].Id);

        battle.ResolveTurn(action, BattleAction.Pass());
        Assert.False(battle.Active(0).Fainted);
        Assert.True(battle.Active(1).Hp < 1000);
        Assert.DoesNotContain(battle.Log, entry => entry.Text.Contains("技能中断"));
    }

    [Fact]
    public void GuaranteedHealingBeforeSelfDamageKeepsTheAttackAvailable()
    {
        var battle = MakeSelfDamageBattle(true);
        var untouched = MakeSelfDamageBattle(true);
        string before = Snapshot(battle);

        var action = Ai.Choose(battle, 0, AiLevel.Hard);

        Assert.Equal(before, Snapshot(battle));
        for (int i = 0; i < 8; i++) Assert.Equal(untouched.Random(), battle.Random());
        Assert.Equal(ActionType.Skill, action.Type);
        Assert.Equal(0, action.Index);
        Assert.Equal(InterruptedSkill, battle.Active(0).Skills[action.Index].Id);

        battle.ResolveTurn(action, BattleAction.Pass());
        Assert.False(battle.Active(0).Fainted);
        Assert.Equal(50, battle.Active(0).Hp);
        Assert.True(battle.Active(1).Fainted);
        Assert.DoesNotContain(battle.Log, entry => entry.Text.Contains("技能中断"));
    }

    static Battle MakeSelfDamageBattle(bool healBeforeDamage)
    {
        var db = BattleTests.LoadPack("original");
        var selfDamage = new SkillDef
        {
            Id = InterruptedSkill, Name = "审查自伤", Type = "tech", Cat = "talent", Power = 5000, Pp = 10
        };
        if (healBeforeDamage)
            selfDamage.Effects.Add(new EffectDef { At = "pre", Code = "HEAL", Target = "self", Frac = 1 });
        selfDamage.Effects.Add(new EffectDef { At = "pre", Code = "FIXED_DAMAGE", Target = "self", N = 150 });
        db.Skills.Add(selfDamage.Id, selfDamage);
        db.Skills.Add(SafeSkill, new SkillDef
        {
            Id = SafeSkill, Name = "审查普通攻击", Type = "tech", Cat = "talent", Power = 20, Pp = 10
        });
        var mine = db.Streamer("bell");
        mine.Persona = "pure";
        mine.Stats["hp"] = 200;
        mine.Stats["spa"] = 100;
        mine.Stats["spd"] = 1000;
        var foe = db.Streamer("kei");
        foe.Persona = "pure";
        foe.Stats["hp"] = 1000;
        foe.Stats["sdef"] = 100;
        foe.Stats["spd"] = 1;
        var battle = new Battle(db, new IList<string>[]
        {
            new[] { "bell", "enka", "shiori", "lumi", "hane", "momo" },
            new[] { "kei", "yuan", "nyako", "buck", "kasumi", "bee" }
        }, new[] { "甲方", "乙方" }, 7);
        battle.Active(0).Hp = 100;
        foreach (var slot in battle.Active(0).Skills) slot.Pp = 0;
        battle.Active(0).Skills[0].Id = InterruptedSkill;
        battle.Active(0).Skills[0].Pp = 5;
        battle.Active(0).Skills[1].Id = SafeSkill;
        battle.Active(0).Skills[1].Pp = 5;
        foreach (var slot in battle.Active(1).Skills)
        {
            slot.Id = SafeSkill;
            slot.Pp = 5;
        }
        return battle;
    }

    static string Snapshot(Battle battle)
    {
        return JsonSerializer.Serialize(battle, new JsonSerializerOptions { IncludeFields = true });
    }
}
