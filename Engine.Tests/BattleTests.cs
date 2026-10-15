using System;
using System.IO;
using System.Linq;
using StreamDuel.Core;
using Xunit;

public class BattleTests
{
    // 供其它测试文件复用
    internal static ContentDb LoadPack(string pack) => Load(pack);

    static string Res(string rel) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Content", rel));
    static ContentDb Load(string pack) => ContentDb.FromJson(Res("StreamDuelRules.json"), Res(Path.Combine("Packs", pack + ".json")));
    static readonly ContentDb Db = Load("original");
    static readonly string[] Others = { "enka", "shiori", "lumi", "hane", "momo", "yuan" };

    static Battle Make(string lead0, string lead1, uint seed = 7, string[] rest0 = null, string[] rest1 = null)
    {
        var t0 = new[] { lead0 }.Concat((rest0 ?? new[] { "enka", "shiori", "lumi", "hane", "yuan" }).Where(x => x != lead0)).Take(6).ToList();
        var t1 = new[] { lead1 }.Concat((rest1 ?? new[] { "enka", "shiori", "lumi", "hane", "yuan", "momo" }).Where(x => x != lead1)).Take(6).ToList();
        return new Battle(Db, new System.Collections.Generic.IList<string>[] { t0, t1 }, new[] { "你", "对手" }, seed);
    }

    static int SkillIndex(Battle b, int s, string id) => b.Active(s).Skills.FindIndex(x => x.Id == id);

    [Fact]
    public void ContentLoadsWithFourSkillsPlusFifth()
    {
        Assert.Equal(12, Db.Streamers.Count);
        foreach (var s in Db.Streamers) { Assert.Equal(4, s.Skills.Count); Assert.True(Db.Skills.ContainsKey(s.Fifth)); }
    }

    [Fact]
    public void FanPackLoadsWithNeutralRenamesAndNotice()
    {
        var fan = Load("fan_holo_niji");
        Assert.Equal(12, fan.Streamers.Count);
        Assert.Contains("非官方同人作品", fan.PackNotice);
        Assert.Equal("回放轰炸", fan.Skills["dig_history"].Name);
        Assert.Equal("卡麦", fan.Statuses[fan.Personas[fan.Streamer("marine").Persona].Status].Name);
        Assert.Equal("船长反击", fan.Personas[fan.Streamer("marine").Persona].Name);
        // 改名只影响这个内容包，原创包不受影响
        Assert.Equal("翻黑历史", Db.Skills["dig_history"].Name);
        RunMany(fan, 200);
    }

    static int[] RunMany(ContentDb db, int n)
    {
        var ids = db.Streamers.Select(s => s.Id).ToArray();
        var rnd = new Random(1);
        int[] wins = new int[3];
        for (int i = 0; i < n; i++)
        {
            var sh = ids.OrderBy(_ => rnd.Next()).ToArray();
            var b = new Battle(db, new System.Collections.Generic.IList<string>[] { sh.Take(6).ToList(), sh.Skip(6).Take(6).ToList() }, new[] { "A", "B" }, (uint)i);
            int guard = 0;
            while (b.Phase != Phase.Over && guard++ < 500)
            {
                if (b.Phase == Phase.Choose) b.ResolveTurn(Ai.Choose(b, 0), Ai.Choose(b, 1));
                else for (int s = 0; s < 2; s++) if (b.Need[s]) b.Replace(s, Ai.ChooseReplacement(b, s));
            }
            Assert.Equal(Phase.Over, b.Phase);
            wins[b.Winner == -1 ? 2 : b.Winner]++;
        }
        return wins;
    }

    [Fact]
    public void ThousandAiBattlesFinishWithoutErrors()
    {
        var w = RunMany(Db, 1000);
        Assert.InRange(w[0], 350, 650); // 两边都是同一个电脑，胜率应接近一半
    }

    [Fact]
    public void SameSeedReplaysTheSameBattle()
    {
        string Run()
        {
            var b = Make("bell", "kei", 42);
            for (int i = 0; i < 30 && b.Phase != Phase.Over; i++)
            {
                if (b.Phase == Phase.Choose) b.ResolveTurn(Ai.Choose(b, 0), Ai.Choose(b, 1));
                else for (int s = 0; s < 2; s++) if (b.Need[s]) b.Replace(s, Ai.ChooseReplacement(b, s));
            }
            return string.Join("\n", b.Log.Select(l => l.Node + l.Text));
        }
        Assert.Equal(Run(), Run());
    }

    [Fact]
    public void PreHitConditionReadsHpBeforeDamage()
    {
        var b = Make("bell", "kei");
        var foe = b.Active(1); foe.Hp = (int)(foe.MaxHp * 0.55);
        int from = b.Log.Count;
        b.ResolveTurn(BattleAction.Skill(SkillIndex(b, 0, "high_note")), BattleAction.Pass());
        var lines = b.Log.Skip(from).ToList();
        int check = lines.FindIndex(l => l.Text.StartsWith("〔命中前判定〕"));
        int dmg = lines.FindIndex(l => l.Kind == "dmg" && l.Side == 1);
        Assert.True(check >= 0 && dmg > check, "命中前判定应该在伤害之前");
        Assert.Contains("高于 50%", lines[check].Text);
        Assert.DoesNotContain("不高于", lines[check].Text);
    }

    [Fact]
    public void PostHitConditionReadsHpAfterDamage()
    {
        var b = Make("bee", "kei");
        var foe = b.Active(1); foe.Hp = (int)(foe.MaxHp * 0.55);
        int from = b.Log.Count;
        b.ResolveTurn(BattleAction.Skill(SkillIndex(b, 0, "shock_mic")), BattleAction.Pass());
        var lines = b.Log.Skip(from).ToList();
        int check = lines.FindIndex(l => l.Text.StartsWith("〔命中后判定〕"));
        int dmg = lines.FindIndex(l => l.Kind == "dmg" && l.Side == 1);
        Assert.True(dmg >= 0 && check > dmg, "命中后判定应该在伤害之后");
        Assert.Contains("不高于 50%", lines[check].Text);
    }

    [Fact]
    public void DispelTurnsClearsTurnEffectsButNotMarksOrBoosts()
    {
        var b = Make("bell", "kei");
        var foe = b.Active(1);
        foe.Fields.Add(new FieldInst { Id = "shield", Turns = 3 });
        foe.Fields.Add(new FieldInst { Id = "blackhist", Turns = -1 });
        foe.Stages["def"] = 2;
        b.ResolveTurn(BattleAction.Skill(SkillIndex(b, 0, "noise_cancel")), BattleAction.Pass());
        Assert.False(foe.HasField("shield"));
        Assert.True(foe.HasField("blackhist"));
        Assert.Equal(2, foe.Stages["def"]);
    }

    [Fact]
    public void DispelBoostZeroesOnlyRaisedStages()
    {
        var b = Make("bee", "kei");
        var foe = b.Active(1);
        foe.Stages["atk"] = 2; foe.Stages["spd"] = -1;
        foe.Fields.Add(new FieldInst { Id = "shield", Turns = 3 });
        b.ResolveTurn(BattleAction.Skill(SkillIndex(b, 0, "ban_danmaku")), BattleAction.Pass());
        Assert.Equal(0, foe.Stages["atk"]);
        Assert.Equal(-1, foe.Stages["spd"]);
        Assert.True(foe.HasField("shield"));
    }

    [Fact]
    public void TeamSpiritSkipsPpOnlyWhileSlotSixIsLive()
    {
        var b = Make("nyako", "kei");
        int idx = SkillIndex(b, 0, "cat_walk");
        b.ResolveTurn(BattleAction.Skill(idx), BattleAction.Pass());
        Assert.Equal(b.Active(0).Skills[idx].Max, b.Active(0).Skills[idx].Pp);
        b.Sides[0].Team[5].Fainted = true; b.Sides[0].Team[5].Hp = 0;
        b.ResolveTurn(BattleAction.Skill(idx), BattleAction.Pass());
        Assert.Equal(b.Active(0).Skills[idx].Max - 1, b.Active(0).Skills[idx].Pp);
    }

    [Fact]
    public void ControlSkipsActionThenGrantsReconnectProtection()
    {
        var b = Make("kei", "enka");
        var foe = b.Active(1);
        foe.Statuses.Add(new StatusInst { Id = "stall", Left = 1 });
        int from = b.Log.Count;
        b.ResolveTurn(BattleAction.Pass(), BattleAction.Skill(0));
        Assert.Contains(b.Log.Skip(from), l => l.Text.Contains("无法行动"));
        Assert.DoesNotContain(b.Log.Skip(from), l => l.Kind == "act" && l.Side == 1);
        Assert.True(foe.HasField("reconnect"));
        Assert.Null(b.ControlOf(foe));
    }

    [Fact]
    public void PunctualGivesPriorityOnFirstTurnOnly()
    {
        var b = Make("bell", "kei");
        var sk = Db.Skills["full_volume"];
        Assert.Equal(1, b.PrioOf(0, sk).Value);
        b.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        Assert.Equal(0, b.PrioOf(0, sk).Value);
    }

    [Fact]
    public void HypeStacksBoostAndAreConsumed()
    {
        var b = Make("nyako", "yuan", 3, null, new[] { "yuan", "kei", "enka", "shiori", "lumi", "hane" });
        b.Active(1).Hp = b.Active(1).MaxHp = 99999;
        int punch = SkillIndex(b, 0, "meme_punch");
        b.ResolveTurn(BattleAction.Skill(punch), BattleAction.Pass());
        b.ResolveTurn(BattleAction.Skill(punch), BattleAction.Pass());
        Assert.Equal(2, b.Active(0).FieldOf("hype").Stacks);
        int from = b.Log.Count;
        b.ResolveTurn(BattleAction.Skill(4), BattleAction.Pass());
        Assert.Contains(b.Log.Skip(from), l => l.Text.Contains("2 层 → 威力 ×1.5"));
        Assert.False(b.Active(0).HasField("hype"));
    }

    [Fact]
    public void SwitchAlwaysHappensBeforeSkills()
    {
        var b = Make("kei", "bell");
        int from = b.Log.Count;
        b.ResolveTurn(BattleAction.SwitchTo(1), BattleAction.Skill(SkillIndex(b, 1, "warm_up")));
        var lines = b.Log.Skip(from).ToList();
        int sw = lines.FindIndex(l => l.Node == Node.Switch);
        int act = lines.FindIndex(l => l.Kind == "act");
        Assert.True(sw >= 0 && act > sw);
        Assert.Equal(1, b.Sides[0].Active);
    }
}
