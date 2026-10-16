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

public class T95_TunedWeightsTests
{
    // 在读取候选和运行比赛前冻结；这三个区间不得用于训练或挑选候选。
    const uint TeamSeedBase = 0x95A00000;
    const uint BattleSeedBase = 0x95B00000;
    const int Samples = 50;
    const int Games = Samples * 4;

    // fe489bc690101a7526c3c7d595f04b4e7a88aa80 的全部 36 个字段。
    // 逐字段赋值，避免新 Default 或新增参数悄悄进入旧对手。
    static readonly Dictionary<string, double> OldValues = new Dictionary<string, double>(StringComparer.Ordinal)
    {
        { "ControlledSwitchMinHpRatio", 0.3 },
        { "ControlledSwitchProbability", 0.35 },
        { "DamageScoreMultiplier", 1 },
        { "KnockoutScore", 1.6 },
        { "FirstKnockoutBonus", 0.6 },
        { "ThreatenedFirstKnockoutBonus", 1 },
        { "ControlStatusScore", 0.5 },
        { "WeakStatusScore", 0.3 },
        { "StageBoostTarget", 2 },
        { "StageMinHpRatio", 0.6 },
        { "StageMaxThreatRatio", 0.5 },
        { "StageScore", 0.22 },
        { "FoeStageScore", 0.18 },
        { "HealMaxHpRatio", 0.5 },
        { "HealMissingHpScore", 0.9 },
        { "FieldMaxThreatRatio", 0.6 },
        { "FieldSafeScore", 0.32 },
        { "FieldThreatenedScore", 0.12 },
        { "ReflectScoreCap", 0.7 },
        { "ReflectDamageScoreMultiplier", 2 },
        { "WeakImmunityScore", 0.4 },
        { "TrapAfflictedScore", 0.4 },
        { "TrapScore", 0.22 },
        { "DispelFieldScore", 0.2 },
        { "DispelTrapScore", -0.2 },
        { "DispelBoostScore", 0.12 },
        { "StealBoostBaseScore", 0.12 },
        { "StealBoostPerStageScore", 0.08 },
        { "CleanseScore", 0.4 },
        { "PpReduceScore", 0 },
        { "RandomScoreRange", 0.08 },
        { "SwitchMinThreatRatio", 0.9 },
        { "SwitchMaxActionScore", 0.5 },
        { "SwitchMinMatchupGain", 0.5 },
        { "SwitchProbability", 0.6 },
        { "MatchupSlowerPenalty", 0.2 }
    };

    readonly ITestOutputHelper output;

    public T95_TunedWeightsTests(ITestOutputHelper output)
    {
        this.output = output;
    }

    static AiWeights OldWeights()
    {
        var weights = new AiWeights();
        Assert.Equal(OldValues.Keys.OrderBy(name => name, StringComparer.Ordinal), weights.Names());
        foreach (var pair in OldValues) weights.Set(pair.Key, pair.Value);
        return weights;
    }

    static IList<string>[] SampleTeams(ContentDb db, int suite, int sample)
    {
        var ids = db.Streamers.Select(streamer => streamer.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        Assert.Equal(12, ids.Length);
        // 等待首发选择的构造路径不抽随机数，保留冻结协议的 Mulberry32 起点和洗牌序列。
        var sampling = new Battle(db, new IList<string>[] { ids.Take(6).ToArray(), ids.Skip(6).Take(6).ToArray() },
            new[] { "抽样甲", "抽样乙" }, new BattleOptions
            {
                Seed = TeamSeedBase + (uint)suite * 0x10000u + (uint)sample,
                LeadPick = true
            });
        for (int index = ids.Length - 1; index > 0; index--)
        {
            int other = (int)(sampling.Random() * (index + 1));
            string temporary = ids[index];
            ids[index] = ids[other];
            ids[other] = temporary;
        }
        return new IList<string>[] { ids.Take(6).ToArray(), ids.Skip(6).Take(6).ToArray() };
    }

    static Battle Run(ContentDb db, IList<string>[] teams, uint seed, int chooseFirst,
        AiLevel[] levels, AiWeights[] weights)
    {
        var battle = new Battle(db, teams, new[] { "A", "B" }, seed);
        int steps = 0;
        while (battle.Phase != Phase.Over && steps++ < Battle.TurnCap * 3)
        {
            if (battle.Phase == Phase.Choose)
            {
                var actions = new BattleAction[2];
                for (int offset = 0; offset < 2; offset++)
                {
                    int side = (chooseFirst + offset) % 2;
                    actions[side] = Ai.ChooseWithWeights(battle, side, levels[side], weights[side]);
                }
                battle.ResolveTurn(actions[0], actions[1]);
            }
            else
            {
                Assert.Equal(Phase.Replace, battle.Phase);
                for (int offset = 0; offset < 2 && battle.Phase == Phase.Replace; offset++)
                {
                    int side = (chooseFirst + offset) % 2;
                    if (battle.Need[side])
                        battle.Replace(side, Ai.ChooseReplacementWithWeights(battle, side, levels[side], weights[side]));
                }
            }
        }
        Assert.Equal(Phase.Over, battle.Phase);
        Assert.InRange(battle.Winner, -1, 1);
        return battle;
    }

    [Theory]
    [InlineData(0, 104)]
    [InlineData(1, 104)]
    [InlineData(2, 110)]
    [Trait("Category", "T95Holdout")]
    public void FrozenIndependentTwoHundredGameHoldoutMeetsTheRequiredWinRate(int suite, int requiredWins)
    {
        var db = BattleTests.LoadPack("original");
        Assert.All(db.Streamers, streamer => Assert.Null(streamer.AiStyle));
        AiLevel candidateLevel = suite == 0 ? AiLevel.Normal : AiLevel.Hard;
        AiLevel opponentLevel = suite == 1 ? AiLevel.Hard : AiLevel.Normal;
        var candidate = suite == 0 ? AiWeights.Default : AiWeights.HardDefault;
        var opponent = suite == 2 ? AiWeights.Default : OldWeights();
        string candidateJson = candidate.ToJson(), opponentJson = opponent.ToJson();
        int wins = 0, losses = 0, draws = 0;
        for (int sample = 0; sample < Samples; sample++)
        {
            var teams = SampleTeams(db, suite, sample);
            uint seed = BattleSeedBase + (uint)suite * 0x10000u + (uint)sample;
            // 每个种子覆盖候选所在双方与选招调用先后的笛卡尔积，阵容和随机起点相同。
            for (int candidateSide = 0; candidateSide < 2; candidateSide++)
                for (int chooseFirst = 0; chooseFirst < 2; chooseFirst++)
                {
                    var levels = new[] { opponentLevel, opponentLevel };
                    var weights = new[] { opponent, opponent };
                    levels[candidateSide] = candidateLevel;
                    weights[candidateSide] = candidate;
                    var battle = Run(db, teams, seed, chooseFirst, levels, weights);
                    if (battle.Winner == candidateSide) wins++;
                    else if (battle.Winner == -1) draws++;
                    else losses++;
                }
        }
        Assert.Equal(Games, wins + losses + draws);
        Assert.Equal(candidateJson, candidate.ToJson());
        Assert.Equal(opponentJson, opponent.ToJson());
        output.WriteLine("T95-holdout-v1 suite={0}: 胜 {1}，负 {2}，平 {3}；胜率 {4}/{5}={6:P2}",
            suite, wins, losses, draws, wins, Games, (double)wins / Games);
        // 平局保留在全部 200 局的分母，不用排除平局来抬高胜率。
        Assert.True(wins >= requiredWins,
            $"固定独立验收组 {suite} 仅胜 {wins}/{Games}（负 {losses}，平 {draws}），至少需要 {requiredWins} 胜。");
    }

    [Fact]
    public void LegacyIsExactlyTheFrozenOldConfigurationAndProfilesAreIndependent()
    {
        var frozen = OldWeights();
        Assert.Equal(36, OldValues.Count);
        Assert.Equal(frozen.ToJson(), AiWeights.Legacy.ToJson());
        Func<AiWeights>[] profiles = { () => AiWeights.Default, () => AiWeights.HardDefault, () => AiWeights.Legacy };
        var initial = profiles.Select(profile => profile().ToJson()).ToArray();
        foreach (var profile in profiles)
        {
            var changed = profile();
            var copy = changed.Copy();
            string original = changed.ToJson();
            foreach (string name in changed.Names()) changed.Set(name, changed.Get(name) + 0.125);
            Assert.Equal(original, copy.ToJson());
            Assert.Equal(original, profile().ToJson());
            Assert.Equal(original, AiWeights.FromJson(original).ToJson());
        }
        Assert.Equal(initial, profiles.Select(profile => profile().ToJson()));
    }

    static Battle Position(uint seed)
    {
        var db = BattleTests.LoadPack("original");
        var ids = db.Streamers.Select(streamer => streamer.Id).ToArray();
        int offset = (int)(seed % (uint)ids.Length);
        var order = ids.Skip(offset).Concat(ids.Take(offset)).ToArray();
        return new Battle(db, new IList<string>[] { order.Take(6).ToArray(), order.Skip(6).Take(6).ToArray() },
            new[] { "A", "B" }, seed);
    }

    [Theory]
    [InlineData(AiLevel.Normal)]
    [InlineData(AiLevel.Hard)]
    public void DefaultActionAndReplacementOverloadsUseTheirOwnProfile(AiLevel level)
    {
        for (uint seed = 0; seed < 12; seed++)
        {
            var ordinary = Position(seed);
            var explicitWeights = ordinary.Clone();
            var weights = level == AiLevel.Normal ? AiWeights.Default : AiWeights.HardDefault;
            string weightJson = weights.ToJson();
            Assert.Equal(Ai.Choose(ordinary, 0, level).ToString(),
                Ai.ChooseWithWeights(explicitWeights, 0, level, weights).ToString());
            Assert.Equal(ordinary.StateHash(), explicitWeights.StateHash());
            Assert.Equal(ordinary.Random(), explicitWeights.Random());
            foreach (var battle in new[] { ordinary, explicitWeights })
            {
                battle.Active(0).Hp = 0;
                battle.Active(0).Fainted = true;
                battle.Need[0] = true;
                battle.Phase = Phase.Replace;
            }
            Assert.Equal(Ai.ChooseReplacement(ordinary, 0, level),
                Ai.ChooseReplacementWithWeights(explicitWeights, 0, level, weights));
            Assert.Equal(ordinary.StateHash(), explicitWeights.StateHash());
            Assert.Equal(weightJson, weights.ToJson());
        }
    }

    [Fact]
    public void EasyActionsAndReplacementsIgnoreExplicitTuningWeights()
    {
        var changed = OldWeights();
        foreach (string name in changed.Names()) changed.Set(name, changed.Get(name) * 10 + 2);
        for (uint seed = 0; seed < 20; seed++)
        {
            var ordinary = Position(seed);
            var supplied = ordinary.Clone();
            Assert.Equal(Ai.Choose(ordinary, 0, AiLevel.Easy).ToString(),
                Ai.ChooseWithWeights(supplied, 0, AiLevel.Easy, changed).ToString());
            Assert.Equal(ordinary.StateHash(), supplied.StateHash());
            Assert.Equal(ordinary.Random(), supplied.Random());
            foreach (var battle in new[] { ordinary, supplied })
            {
                battle.Active(0).Hp = 0;
                battle.Active(0).Fainted = true;
                battle.Need[0] = true;
                battle.Phase = Phase.Replace;
            }
            Assert.Equal(Ai.ChooseReplacement(ordinary, 0, AiLevel.Easy),
                Ai.ChooseReplacementWithWeights(supplied, 0, AiLevel.Easy, changed));
            Assert.Equal(ordinary.StateHash(), supplied.StateHash());
        }
    }

    [Theory]
    [InlineData(AiLevel.Normal)]
    [InlineData(AiLevel.Hard)]
    public void ExplicitTuningWeightsCanChangeRecoveryDecisionsWithoutLeakingIntoOtherCalls(AiLevel level)
    {
        var db = BattleTests.LoadPack("original");
        db.Skills["tune_hit"] = new SkillDef
            { Id = "tune_hit", Name = "调参互动", Type = "talk", Cat = "interact", Power = 40, Pp = 10 };
        db.Skills["tune_heal"] = new SkillDef
        {
            Id = "tune_heal", Name = "调参回复", Type = "talk", Cat = "status", Pp = 10,
            Effects = new List<EffectDef> { new EffectDef { At = "post", Code = "HEAL", Target = "self", Frac = 0.5 } }
        };
        db.Skills["tune_idle"] = new SkillDef
            { Id = "tune_idle", Name = "调参空招", Type = "talk", Cat = "status", Pp = 10 };
        foreach (var streamer in db.Streamers)
        {
            streamer.Type = "talk";
            streamer.Type2 = null;
            streamer.Persona = "pure";
            streamer.Stats["hp"] = 1000;
            foreach (string stat in ContentDb.StatKeys) streamer.Stats[stat] = 100;
            streamer.Skills = new List<string> { "tune_hit", "tune_heal", "tune_idle", "tune_idle" };
            streamer.Fifth = "tune_idle";
        }
        var ids = db.Streamers.Select(streamer => streamer.Id).ToArray();
        var initial = new Battle(db, new IList<string>[] { ids.Take(6).ToArray(), ids.Skip(6).Take(6).ToArray() },
            new[] { "A", "B" }, 95);
        initial.Active(0).Hp = 250;
        initial.Active(0).Fields.Add(new FieldInst { Id = "lockin", Turns = 3 });
        for (int index = 2; index < initial.Active(0).Skills.Count; index++) initial.Active(0).Skills[index].Pp = 0;
        foreach (var slot in initial.Active(1).Skills) slot.Id = "tune_idle";
        var attack = OldWeights();
        var heal = OldWeights();
        attack.RandomScoreRange = heal.RandomScoreRange = 0;
        attack.HealMissingHpScore = 0;
        heal.HealMissingHpScore = 20;
        string attackJson = attack.ToJson(), healJson = heal.ToJson();
        string defaultJson = AiWeights.Default.ToJson(), hardJson = AiWeights.HardDefault.ToJson();
        var first = initial.Clone();
        var second = initial.Clone();
        var repeated = initial.Clone();

        Assert.Equal(BattleAction.Skill(0).ToString(), Ai.ChooseWithWeights(first, 0, level, attack).ToString());
        Assert.Equal(BattleAction.Skill(1).ToString(), Ai.ChooseWithWeights(second, 0, level, heal).ToString());
        Assert.Equal(BattleAction.Skill(0).ToString(), Ai.ChooseWithWeights(repeated, 0, level, attack).ToString());
        Assert.Equal(first.StateHash(), repeated.StateHash());
        Assert.Equal(first.Random(), repeated.Random());
        Assert.Equal(attackJson, attack.ToJson());
        Assert.Equal(healJson, heal.ToJson());
        Assert.Equal(defaultJson, AiWeights.Default.ToJson());
        Assert.Equal(hardJson, AiWeights.HardDefault.ToJson());
    }

    [Theory]
    [InlineData("aggressive", 0, 1)]
    [InlineData("defensive", 1, 0)]
    [InlineData("tricky", 2, 0)]
    public void ExplicitNormalWeightsRespectStyleAcrossAllPublicEntrypoints(string style, int styleChoice, int callerChoice)
    {
        Battle PositionWithStyle(string selectedStyle, uint seed)
        {
            var db = BattleTests.LoadPack("original");
            db.Skills["tune_style_hit"] = new SkillDef
                { Id = "tune_style_hit", Name = "风格互动", Type = "talk", Cat = "interact", Power = 500, Pp = 10 };
            db.Skills["tune_style_heal"] = new SkillDef
            {
                Id = "tune_style_heal", Name = "风格回复", Type = "talk", Cat = "status", Pp = 10,
                Effects = new List<EffectDef> { new EffectDef { At = "post", Code = "HEAL", Target = "self", Frac = 0.5 } }
            };
            db.Skills["tune_style_control"] = new SkillDef
            {
                Id = "tune_style_control", Name = "风格控制", Type = "talk", Cat = "status", Pp = 10,
                Effects = new List<EffectDef> { new EffectDef { At = "post", Code = "STATUS", Target = "foe", Status = "disconnect" } }
            };
            db.Skills["tune_style_idle"] = new SkillDef
                { Id = "tune_style_idle", Name = "风格空招", Type = "talk", Cat = "status", Pp = 10 };
            foreach (var streamer in db.Streamers)
            {
                streamer.Type = "talk";
                streamer.Type2 = null;
                streamer.Persona = "pure";
                streamer.AiStyle = selectedStyle;
                streamer.Stats["hp"] = 1000;
                foreach (string stat in ContentDb.StatKeys) streamer.Stats[stat] = 100;
                streamer.Skills = new List<string> { "tune_style_hit", "tune_style_heal", "tune_style_control", "tune_style_idle" };
                streamer.Fifth = "tune_style_idle";
            }
            var ids = db.Streamers.Select(streamer => streamer.Id).ToArray();
            var battle = new Battle(db, new IList<string>[] { ids.Take(6).ToArray(), ids.Skip(6).Take(6).ToArray() },
                new[] { "A", "B" }, seed);
            battle.Active(0).Hp = 400;
            battle.Active(0).Skills[3].Pp = battle.Active(0).Skills[4].Pp = 0;
            battle.Active(0).Fields.Add(new FieldInst { Id = "lockin", Turns = 3 });
            foreach (var slot in battle.Active(1).Skills) slot.Id = "tune_style_idle";
            return battle;
        }

        var caller = OldWeights();
        caller.DamageScoreMultiplier = callerChoice == 0 ? 100 : 0;
        caller.HealMissingHpScore = callerChoice == 1 ? 100 : 0;
        caller.ControlStatusScore = 0;
        caller.RandomScoreRange = 0;
        string callerJson = caller.ToJson();
        foreach (uint seed in new uint[] { 1, 53, 2026 })
        {
            var ordinary = PositionWithStyle(style, seed);
            var normalLevel = ordinary.Clone();
            var oldFourArgument = ordinary.Clone();
            var explicitWeights = ordinary.Clone();
            var expected = Ai.Choose(ordinary, 0);
            Assert.Equal(BattleAction.Skill(styleChoice).ToString(), expected.ToString());
            Assert.Equal(expected.ToString(), Ai.Choose(normalLevel, 0, AiLevel.Normal).ToString());
            Assert.Equal(expected.ToString(), Ai.Choose(oldFourArgument, 0, AiLevel.Normal, caller).ToString());
            Assert.Equal(expected.ToString(), Ai.ChooseWithWeights(explicitWeights, 0, AiLevel.Normal, caller).ToString());
            Assert.Equal(ordinary.StateHash(), normalLevel.StateHash());
            Assert.Equal(ordinary.StateHash(), oldFourArgument.StateHash());
            Assert.Equal(ordinary.StateHash(), explicitWeights.StateHash());
            for (int draw = 0; draw < 4; draw++)
            {
                double next = ordinary.Random();
                Assert.Equal(next, normalLevel.Random());
                Assert.Equal(next, oldFourArgument.Random());
                Assert.Equal(next, explicitWeights.Random());
            }
            // 去掉风格后同一组调用者权重必须改变选择，证明覆盖的不是无效参数。
            var unstyled = PositionWithStyle(null, seed);
            var overridden = Ai.ChooseWithWeights(unstyled, 0, AiLevel.Normal, caller);
            Assert.Equal(BattleAction.Skill(callerChoice).ToString(), overridden.ToString());
            Assert.NotEqual(expected.ToString(), overridden.ToString());
            Assert.Equal(callerJson, caller.ToJson());
        }
    }

    [Theory]
    [InlineData("aggressive")]
    [InlineData("defensive")]
    [InlineData("tricky")]
    public void StyledHardKeepsItsLegacyBaseRegardlessOfSuppliedTuningWeights(string style)
    {
        var legacy = AiWeights.Legacy;
        var changed = OldWeights();
        foreach (string name in changed.Names()) changed.Set(name, changed.Get(name) * 10 + 2);
        string legacyJson = legacy.ToJson(), changedJson = changed.ToJson();
        foreach (uint seed in new uint[] { 1, 53, 2026 })
            for (int side = 0; side < 2; side++)
                foreach (int hpPercent in new[] { 100, 40, 10 })
                {
                    var ordinary = Position(seed);
                    // 只给当前演员风格，避免把未配置风格的对手或候选队友也纳入兼容承诺。
                    ordinary.Active(side).Def.AiStyle = style;
                    ordinary.Active(side).Hp = Math.Max(1, ordinary.Active(side).MaxHp * hpPercent / 100);
                    var explicitLegacy = ordinary.Clone();
                    var explicitChanged = ordinary.Clone();

                    var expected = Ai.Choose(ordinary, side, AiLevel.Hard);
                    Assert.Equal(expected.ToString(),
                        Ai.ChooseWithWeights(explicitLegacy, side, AiLevel.Hard, legacy).ToString());
                    Assert.Equal(expected.ToString(),
                        Ai.ChooseWithWeights(explicitChanged, side, AiLevel.Hard, changed).ToString());
                    Assert.Equal(ordinary.StateHash(), explicitLegacy.StateHash());
                    Assert.Equal(ordinary.StateHash(), explicitChanged.StateHash());
                    for (int draw = 0; draw < 4; draw++)
                    {
                        double next = ordinary.Random();
                        Assert.Equal(next, explicitLegacy.Random());
                        Assert.Equal(next, explicitChanged.Random());
                    }
                }
        Assert.Equal(legacyJson, legacy.ToJson());
        Assert.Equal(changedJson, changed.ToJson());
    }

    [Theory]
    [InlineData(AiLevel.Easy, "AD0C8A37E9E78DA384301C971AF0FA7860A23F35B0C9BC1A5B4BA0A665462681")]
    [InlineData(AiLevel.Normal, "1B9D2B717949A2416D66CE8F8B76A89C26FECCE22CBD84A94568527631C5E1E0")]
    [InlineData(AiLevel.Hard, "FA0B2FA1E4486551B9DFA8A88FBEB4C2791936C6128429C358F407E402986A44")]
    public void EasyAndExplicitOldWeightsRetainThePreTuningFullBattleSnapshots(AiLevel level, string expected)
    {
        // 摘要直接取自本任务基点的 T33_AiWeightsEdgeTests，未用新实现重新生成。
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("zh-CN");
            var setup = Position(7);
            var old = OldWeights();
            var battle = Run(setup.Db, setup.Record.Teams, 7, 0, new[] { level, level }, new[] { old, old });
            string snapshot = JsonSerializer.Serialize(new
            {
                battle.Winner, battle.Turn,
                Log = battle.Log.Select(entry => new
                {
                    entry.Turn, entry.Side, entry.Node, entry.Text, entry.Kind, entry.SnapActive, entry.SnapHp
                }),
                NextRandom = battle.Random()
            });
            using var sha = SHA256.Create();
            string actual = Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(snapshot)));
            output.WriteLine("冻结旧摘要 {0}: {1}", level, actual);
            Assert.Equal(expected, actual);
        }
        finally { CultureInfo.CurrentCulture = previousCulture; }
    }
}
