using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StreamDuel.Core;
using Xunit;
using Xunit.Abstractions;

public class T33_AiWeightsEdgeTests
{
    readonly ITestOutputHelper output;

    public T33_AiWeightsEdgeTests(ITestOutputHelper output)
    {
        this.output = output;
    }

    static Battle Make(uint seed, string lead = null)
    {
        var db = BattleTests.LoadPack("original");
        var ids = db.Streamers.Select(s => s.Id).ToArray();
        int offset = (int)(seed % (uint)ids.Length);
        var rotated = ids.Skip(offset).Concat(ids.Take(offset)).ToArray();
        var own = lead == null ? rotated.Take(6).ToList() : new[] { lead }.Concat(ids.Where(id => id != lead).Take(5)).ToList();
        return new Battle(db, new IList<string>[] { own, rotated.Skip(6).Take(6).ToList() }, new[] { "A", "B" }, seed);
    }

    static string Play(uint seed, AiLevel level, Func<Battle, int, BattleAction> choose)
    {
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            // 固定采集基线时的文化，避免日志中的小数标点随运行环境变化。
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("zh-CN");
            var battle = Make(seed);
            int guard = 0;
            while (battle.Phase != Phase.Over && guard++ < 500)
            {
                if (battle.Phase == Phase.Choose) battle.ResolveTurn(choose(battle, 0), choose(battle, 1));
                else for (int side = 0; side < 2; side++)
                    if (battle.Need[side]) battle.Replace(side, Ai.ChooseReplacement(battle, side, level));
            }
            Assert.Equal(Phase.Over, battle.Phase);
            return JsonSerializer.Serialize(new
            {
                battle.Winner, battle.Turn,
                Log = battle.Log.Select(entry => new
                {
                    entry.Turn, entry.Side, entry.Node, entry.Text, entry.Kind, entry.SnapActive, entry.SnapHp
                }),
                NextRandom = battle.Random()
            });
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    static string Hash(string text)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(text)));
    }

    [Theory]
    [InlineData(0u, AiLevel.Normal, "266CE337B4C00F9D15C8E45FD2DD4255A2BCB9A2AE61931D52881A017BB555AC")]
    [InlineData(7u, AiLevel.Normal, "1B9D2B717949A2416D66CE8F8B76A89C26FECCE22CBD84A94568527631C5E1E0")]
    [InlineData(42u, AiLevel.Normal, "0222E7E8E66064D294EE330D5641D7E5DCFA1315F25A907634908B771C15C137")]
    [InlineData(7u, AiLevel.Easy, "AD0C8A37E9E78DA384301C971AF0FA7860A23F35B0C9BC1A5B4BA0A665462681")]
    [InlineData(7u, AiLevel.Hard, "FA0B2FA1E4486551B9DFA8A88FBEB4C2791936C6128429C358F407E402986A44")]
    public void ExistingStrategiesMatchTheirPreRefactorCompleteBattleBaselines(uint seed, AiLevel level, string expected)
    {
        // 在改动前的 e692be0c 采集，防止旧重载和 Default 一起漂移却互相比对通过。
        // 仅固定战斗行为，允许后续任务给回放或状态哈希增加元数据。
        // 当时 Ai.cs SHA256: FA1FFCE0785CA4CF02B381CAECD9F8134D512BE0D5FACF1D335F1A555A191FA3。
        // T-82 先用 32f190e 资源重现全部五个旧摘要，再同步三项显示名替换影响的四个摘要。
        // 胜方、回合、下一次随机数及所有日志非 Text 字段逐项相同；本地证据：bin/t82-ai-audit/audit.json。
        string actual = Play(seed, level, (battle, side) => Ai.Choose(battle, side, level));
        output.WriteLine("{0}/{1}: {2}", level, seed, Hash(actual));
        Assert.Equal(expected, Hash(actual));
        Assert.Equal(actual, Play(seed, level, (battle, side) => Ai.Choose(battle, side, level, AiWeights.Default)));
        if (level == AiLevel.Normal)
            Assert.Equal(actual, Play(seed, level, (battle, side) => Ai.Choose(battle, side)));
    }

    [Fact]
    public void DefaultsAlsoPreserveActionsAndRandomStateWhenControlledOrOutOfPp()
    {
        for (uint seed = 0; seed < 9; seed++)
        {
            var battles = new[] { Make(seed, "bell"), Make(seed, "bell"), Make(seed, "bell") };
            foreach (var battle in battles)
            {
                if (seed % 3 == 1) battle.Active(0).Statuses.Add(new StatusInst { Id = "stall", Left = 1 });
                if (seed % 3 == 2) foreach (var slot in battle.Active(0).Skills) slot.Pp = 0;
            }

            var legacy = Ai.Choose(battles[0], 0);
            var normal = Ai.Choose(battles[1], 0, AiLevel.Normal);
            var explicitDefault = Ai.Choose(battles[2], 0, AiLevel.Normal, AiWeights.Default);

            Assert.Equal(legacy.ToString(), normal.ToString());
            Assert.Equal(legacy.ToString(), explicitDefault.ToString());
            Assert.Equal(battles[0].StateHash(), battles[1].StateHash());
            Assert.Equal(battles[0].StateHash(), battles[2].StateHash());
            for (int draw = 0; draw < 4; draw++)
            {
                double expected = battles[0].Random();
                Assert.Equal(expected, battles[1].Random());
                Assert.Equal(expected, battles[2].Random());
            }
        }
    }

    [Fact]
    public void CopyAndDefaultInstancesDoNotShareMutableWeights()
    {
        var original = AiWeights.Default;
        string baseline = original.ToJson();
        var copy = original.Copy();
        string name = original.Names().First();
        double value = original.Get(name);

        original.Set(name, value + 1);
        Assert.Equal(baseline, copy.ToJson());
        Assert.Equal(baseline, AiWeights.Default.ToJson());

        copy.Set(name, value + 2);
        Assert.Equal(value + 1, original.Get(name));
        Assert.Equal(value + 2, copy.Get(name));
        Assert.Equal(baseline, AiWeights.Default.ToJson());
    }

    [Fact]
    public void JsonIsSortedCultureIndependentAndRoundTripsEveryWeight()
    {
        var weights = AiWeights.Default.Copy();
        string[] names = weights.Names().OrderBy(name => name, StringComparer.Ordinal).ToArray();
        for (int i = 0; i < names.Length; i++) weights.Set(names[i], (i + 1) * 0.12345678901234566);
        var reversed = AiWeights.Default.Copy();
        foreach (string name in names.Reverse()) reversed.Set(name, weights.Get(name));
        string expected = weights.ToJson();
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            Assert.Equal(expected, weights.ToJson());
            Assert.Equal(expected, reversed.ToJson());
            var restored = AiWeights.FromJson(expected);
            Assert.Equal(expected, restored.ToJson());
            using var json = JsonDocument.Parse(expected);
            Assert.Equal(names, json.RootElement.EnumerateObject().Select(property => property.Name).ToArray());
            foreach (string name in names)
            {
                Assert.Equal(weights.Get(name), restored.Get(name));
                Assert.Equal(weights.Get(name), json.RootElement.GetProperty(name).GetDouble());
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    [Fact]
    public void UnknownWeightNamesAreRejectedWithoutChangingTheConfiguration()
    {
        var weights = AiWeights.Default.Copy();
        string before = weights.ToJson();

        Assert.Throws<KeyNotFoundException>(() => weights.Get("NotAnAiWeight"));
        Assert.Throws<KeyNotFoundException>(() => weights.Set("NotAnAiWeight", 1));

        Assert.Equal(before, weights.ToJson());
    }

    static Battle HealingChoice()
    {
        var battle = Make(7, "kei");
        battle.Active(0).Hp = battle.Active(0).MaxHp / 3;
        foreach (var slot in battle.Active(0).Skills)
            slot.Pp = slot.Id == "teardown" || slot.Id == "hotfix" ? 1 : 0;
        foreach (var fighter in battle.Sides[0].Team.Skip(1))
        {
            fighter.Hp = 0;
            fighter.Fainted = true;
        }
        return battle;
    }

    [Fact]
    public void ChangingOnlyTheHealScoreChangesNormalDecisionAndActualOutcome()
    {
        var attacking = HealingChoice();
        var healing = HealingChoice();
        var attackWeights = AiWeights.Default.Copy();
        var healWeights = AiWeights.Default.Copy();
        attackWeights.Set("HealMissingHpScore", 0);
        healWeights.Set("HealMissingHpScore", 10);
        string attackJson = attackWeights.ToJson(), healJson = healWeights.ToJson();
        int initialHp = healing.Active(0).Hp;
        int initialFoeHp = attacking.Active(1).Hp;

        var attack = Ai.Choose(attacking, 0, AiLevel.Normal, attackWeights);
        var heal = Ai.Choose(healing, 0, AiLevel.Normal, healWeights);

        Assert.Equal(ActionType.Skill, attack.Type);
        Assert.Equal(ActionType.Skill, heal.Type);
        Assert.Equal("teardown", attacking.Active(0).Skills[attack.Index].Id);
        Assert.Equal("hotfix", healing.Active(0).Skills[heal.Index].Id);
        Assert.Equal(attackJson, attackWeights.ToJson());
        Assert.Equal(healJson, healWeights.ToJson());
        attacking.ResolveTurn(attack, BattleAction.Pass());
        healing.ResolveTurn(heal, BattleAction.Pass());
        Assert.True(attacking.Active(1).Hp < initialFoeHp);
        Assert.True(healing.Active(0).Hp > initialHp);
    }

    [Theory]
    [InlineData(AiLevel.Easy)]
    [InlineData(AiLevel.Hard)]
    public void OtherDifficultiesKeepTheirBehaviourWithCustomOrNullWeights(AiLevel level)
    {
        var weights = AiWeights.Default.Copy();
        foreach (string name in weights.Names().ToArray()) weights.Set(name, weights.Get(name) * 3 + 1);
        string expected = Play(7, level, (battle, side) => Ai.Choose(battle, side, level));

        Assert.Equal(expected, Play(7, level, (battle, side) => Ai.Choose(battle, side, level, weights)));
        Assert.Equal(expected, Play(7, level, (battle, side) => Ai.Choose(battle, side, level, null)));
    }
}
