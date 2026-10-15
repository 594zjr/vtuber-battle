// T-06 验收测试：电脑难度。实现后把本文件移到 Engine.Tests/ 下。
using System.Linq;
using StreamDuel.Core;
using Xunit;

public class T06_AiLevelTests
{
    static int Play(ContentDb db, uint seed, AiLevel l0, AiLevel l1)
    {
        var all = db.Streamers.Select(s => s.Id).ToList();
        int k = (int)(seed % (uint)all.Count);
        var ids = all.Skip(k).Concat(all.Take(k)).ToList(); // 按种子轮换阵容，结果可复现
        var b = new Battle(db, new System.Collections.Generic.IList<string>[] { ids.Take(6).ToList(), ids.Skip(6).Take(6).ToList() }, new[] { "A", "B" }, seed);
        int guard = 0;
        while (b.Phase != Phase.Over && guard++ < 500)
        {
            if (b.Phase == Phase.Choose) b.ResolveTurn(Ai.Choose(b, 0, l0), Ai.Choose(b, 1, l1));
            else for (int s = 0; s < 2; s++) if (b.Need[s]) b.Replace(s, Ai.ChooseReplacement(b, s));
        }
        return b.Winner;
    }

    [Fact]
    public void NormalBeatsEasyMostOfTheTime()
    {
        var db = BattleTests.LoadPack("original");
        int normalWins = 0, games = 300;
        for (uint i = 0; i < games; i++)
        {
            bool normalFirst = i % 2 == 0; // 交换座位，避免座位偏差
            int w = normalFirst ? Play(db, i, AiLevel.Normal, AiLevel.Easy) : Play(db, i, AiLevel.Easy, AiLevel.Normal);
            if ((normalFirst && w == 0) || (!normalFirst && w == 1)) normalWins++;
        }
        Assert.True(normalWins >= games * 0.6, "普通难度只赢了 " + normalWins + "/" + games);
    }

    [Fact]
    public void TwoArgumentChooseStaysNormal()
    {
        var db = BattleTests.LoadPack("original");
        var ids = db.Streamers.Select(s => s.Id).ToList();
        var a = new Battle(db, new System.Collections.Generic.IList<string>[] { ids.Take(6).ToList(), ids.Skip(6).Take(6).ToList() }, new[] { "A", "B" }, 3);
        var b = new Battle(db, new System.Collections.Generic.IList<string>[] { ids.Take(6).ToList(), ids.Skip(6).Take(6).ToList() }, new[] { "A", "B" }, 3);
        Assert.Equal(Ai.Choose(a, 0).ToString(), Ai.Choose(b, 0, AiLevel.Normal).ToString());
    }

    static Battle MakeAiBattle(ContentDb db, uint seed, string lead = "bell")
    {
        var ids = db.Streamers.Select(streamer => streamer.Id).ToList();
        var team0 = new[] { lead }.Concat(ids.Where(id => id != lead).Take(5)).ToList();
        var team1 = new[] { "nyako" }.Concat(ids.Where(id => id != "nyako").Take(5)).ToList();
        return new Battle(db, new System.Collections.Generic.IList<string>[] { team0, team1 }, new[] { "A", "B" }, seed);
    }

    [Fact]
    public void EasyPassesWhenControlledWithoutConsumingRandom()
    {
        var db = BattleTests.LoadPack("original");
        foreach (string status in new[] { "stall", "disconnect", "afk" })
        {
            var battle = MakeAiBattle(db, 1);
            var unchanged = MakeAiBattle(db, 1);
            battle.Active(0).Statuses.Add(new StatusInst { Id = status, Left = 2 });
            foreach (var slot in battle.Active(0).Skills) slot.Pp = 0;

            Assert.Equal(ActionType.Pass, Ai.Choose(battle, 0, AiLevel.Easy).Type);
            Assert.Equal(2, battle.Active(0).Statuses[0].Left);
            Assert.Equal(unchanged.Random(), battle.Random());
        }
    }

    [Fact]
    public void EasyStrugglesWithoutUsableSkillsWithoutConsumingRandom()
    {
        var db = BattleTests.LoadPack("original");
        var battle = MakeAiBattle(db, 1);
        var unchanged = MakeAiBattle(db, 1);
        foreach (var slot in battle.Active(0).Skills) slot.Pp = 0;

        Assert.Equal(ActionType.Struggle, Ai.Choose(battle, 0, AiLevel.Easy).Type);
        Assert.Equal(unchanged.Random(), battle.Random());
    }

    [Fact]
    public void EasyHonorsFreePpUntilSlotSixFaints()
    {
        var db = BattleTests.LoadPack("original");
        for (uint seed = 0; seed < 32; seed++)
        {
            var battle = MakeAiBattle(db, seed, "nyako");
            foreach (var slot in battle.Active(0).Skills) slot.Pp = 0;

            Assert.True(battle.LegalFor(0).FreePp);
            var action = Ai.Choose(battle, 0, AiLevel.Easy);
            Assert.Equal(ActionType.Skill, action.Type);
            Assert.True(battle.LegalFor(0).Skills[action.Index].Usable);

            battle.Sides[0].Team[5].Hp = 0;
            battle.Sides[0].Team[5].Fainted = true;
            Assert.False(battle.LegalFor(0).FreePp);
            Assert.Equal(ActionType.Struggle, Ai.Choose(battle, 0, AiLevel.Easy).Type);
        }
    }

    [Fact]
    public void EasyChoosesOnlyUsableSkillSlots()
    {
        var db = BattleTests.LoadPack("original");
        foreach (var usable in new[] { new[] { 3 }, new[] { 1, 4 } })
        {
            var chosen = new System.Collections.Generic.HashSet<int>();
            for (uint seed = 0; seed < 64; seed++)
            {
                var battle = MakeAiBattle(db, seed);
                for (int i = 0; i < battle.Active(0).Skills.Count; i++)
                    battle.Active(0).Skills[i].Pp = usable.Contains(i) ? 1 : 0;

                var action = Ai.Choose(battle, 0, AiLevel.Easy);
                Assert.Equal(ActionType.Skill, action.Type);
                Assert.Contains(action.Index, usable);
                chosen.Add(action.Index);
            }
            Assert.Equal(usable.Length, chosen.Count);
        }
    }

    [Fact]
    public void NormalOverloadPreservesActionsAndRandomState()
    {
        var db = BattleTests.LoadPack("original");
        for (uint seed = 0; seed < 32; seed++)
        {
            var original = MakeAiBattle(db, seed);
            var normal = MakeAiBattle(db, seed);
            foreach (var battle in new[] { original, normal })
            {
                if (seed % 3 == 1) battle.Active(0).Statuses.Add(new StatusInst { Id = "stall", Left = 1 });
                if (seed % 3 == 2) foreach (var slot in battle.Active(0).Skills) slot.Pp = 0;
            }

            Assert.Equal(Ai.Choose(original, 0).ToString(), Ai.Choose(normal, 0, AiLevel.Normal).ToString());
            Assert.Equal(original.Random(), normal.Random());
        }
    }

    [Fact]
    public void EasyReplacesNormalSwitchWithFirstUsableSkill()
    {
        var db = BattleTests.LoadPack("original");
        var normal = MakeAiBattle(db, 1);
        var easy = MakeAiBattle(db, 1);
        foreach (var battle in new[] { normal, easy })
        {
            battle.Active(0).Hp = 1;
            for (int i = 0; i < battle.Active(0).Skills.Count; i++)
                battle.Active(0).Skills[i].Pp = i == 2 ? 1 : 0;
        }

        // 两局在简单难度选择普通策略后使用同一段随机序列，确保确实覆盖换人替代。
        Assert.True(normal.Random() >= 0.5);
        Assert.Equal(ActionType.Switch, Ai.Choose(normal, 0).Type);
        Assert.Equal(BattleAction.Skill(2).ToString(), Ai.Choose(easy, 0, AiLevel.Easy).ToString());
        Assert.Equal(normal.Random(), easy.Random());
    }
}
