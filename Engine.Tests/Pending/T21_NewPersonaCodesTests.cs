// T-21 验收测试：5 个新人设代码。实现后把本文件移到 Engine.Tests/ 下。
using System.Linq;
using StreamDuel.Core;
using Xunit;

public class T21_NewPersonaCodesTests
{
    static ContentDb WithPersona(string code, params (string k, double v)[] ps)
    {
        var db = BattleTests.LoadPack("original");
        var p = new PersonaDef { Id = "test_p", Name = "测试人设", Code = code, Node = "测试", Desc = "测试" };
        foreach (var kv in ps) p.Params[kv.k] = kv.v;
        db.Personas["test_p"] = p;
        db.Streamer("kei").Persona = "test_p";
        return db;
    }

    static Battle Make(ContentDb db, uint seed = 1)
    {
        return new Battle(db, new System.Collections.Generic.IList<string>[] {
            new[] { "kei", "enka", "shiori", "lumi", "hane", "momo" },
            new[] { "bell", "yuan", "nyako", "buck", "kasumi", "bee" } }, new[] { "你", "对手" }, seed);
    }

    static int Fixed(ContentDb db, string id, string target, int n, int prio = 5)
    {
        db.Skills[id] = new SkillDef { Id = id, Name = "测试" + id, Type = "tech", Cat = "status", Pp = 10, Prio = prio,
            Effects = { new EffectDef { At = "post", Code = "FIXED_DAMAGE", Target = target, N = n } } };
        return 0;
    }

    [Fact]
    public void KoHealRestoresAfterKnockingOut()
    {
        var db = WithPersona("T_KO_HEAL", ("frac", 0.25));
        Fixed(db, "test_kill", "foe", 999999);
        var b = Make(db);
        b.Active(0).Skills[0] = new SkillSlot { Id = "test_kill", Pp = 10, Max = 10 };
        b.Active(0).Hp = 10;
        b.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());
        Assert.True(b.Sides[1].Team[0].Fainted);
        Assert.Equal(10 + b.Active(0).MaxHp / 4, b.Sides[0].Team[0].Hp);
    }

    [Fact]
    public void FirstControlAfterEnteringIsIgnored()
    {
        var db = WithPersona("T_FIRST_CONTROL_IMMUNE");
        var b = Make(db);
        db.Skills["test_stall"] = new SkillDef { Id = "test_stall", Name = "测试卡麦", Type = "tech", Cat = "status", Pp = 10, Prio = 5,
            Effects = { new EffectDef { At = "post", Code = "STATUS", Target = "foe", Status = "stall", Chance = 100 } } };
        b.Active(1).Skills[0] = new SkillSlot { Id = "test_stall", Pp = 10, Max = 10 };
        b.ResolveTurn(BattleAction.Pass(), BattleAction.Skill(0));
        Assert.Null(b.ControlOf(b.Active(0)));
        b.ResolveTurn(BattleAction.Pass(), BattleAction.Skill(0));
        Assert.NotNull(b.ControlOf(b.Active(0)));
    }

    [Fact]
    public void EndureSurvivesOneLethalHitPerBattle()
    {
        var db = WithPersona("T_ENDURE");
        Fixed(db, "test_kill", "foe", 999999);
        var b = Make(db);
        b.Active(1).Skills[0] = new SkillSlot { Id = "test_kill", Pp = 10, Max = 10 };
        b.ResolveTurn(BattleAction.Pass(), BattleAction.Skill(0));
        Assert.False(b.Active(0).Fainted);
        Assert.Equal(1, b.Active(0).Hp);
        b.ResolveTurn(BattleAction.Pass(), BattleAction.Skill(0));
        Assert.True(b.Sides[0].Team[0].Fainted);
    }

    [Fact]
    public void AllyPpOnEnterRestoresBench()
    {
        var db = WithPersona("T_ALLY_PP_ON_ENTER", ("n", 2));
        var b = Make(db);
        // 先让 2 号位（enka）用掉 PP，再把 kei 换下又换上
        var enka = b.Sides[0].Team[1];
        foreach (var s in enka.Skills) s.Pp = s.Max - 3;
        b.ResolveTurn(BattleAction.SwitchTo(1), BattleAction.Pass());
        b.ResolveTurn(BattleAction.SwitchTo(0), BattleAction.Pass());
        Assert.True(enka.Skills.All(s => s.Pp == s.Max - 1));
    }

    [Fact]
    public void StageOnHitRaisesStatWhenDamagedButAlive()
    {
        var db = WithPersona("T_STAGE_ON_HIT", ("def", 1)); // 参数名即能力名，值是升几级
        Fixed(db, "test_poke", "foe", 5);
        var b = Make(db);
        b.Active(1).Skills[0] = new SkillSlot { Id = "test_poke", Pp = 10, Max = 10 };
        b.ResolveTurn(BattleAction.Pass(), BattleAction.Skill(0));
        Assert.Equal(1, b.Active(0).Stages["def"]);
    }
}
