using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using StreamDuel.Core;
using Xunit;

public class T136_Pack3StyleTests
{
    static readonly string[] Ids =
    {
        "og3_liling", "og3_yaye", "og3_rongjiao", "og3_paochi", "og3_ruandian", "og3_miantang",
        "og3_zhiyuan", "og3_guodong", "og3_xingguan", "og3_yunkou", "og3_modi", "og3_jufan"
    };
    static readonly AiLevel[] Levels = { AiLevel.Easy, AiLevel.Normal, AiLevel.Hard, AiLevel.Expert };
    static readonly string[] OldStyles = { "aggressive", "defensive", "tricky" };
    static readonly JsonSerializerOptions Json = new JsonSerializerOptions { IncludeFields = true };

    public static IEnumerable<object[]> Streamers()
    {
        foreach (string id in Ids) yield return new object[] { id };
    }

    public static IEnumerable<object[]> StreamerLevels()
    {
        foreach (string id in Ids)
            foreach (var level in Levels) yield return new object[] { id, level };
    }

    public static IEnumerable<object[]> ExistingPackLevels()
    {
        foreach (string pack in new[] { "original", "original_wave2", "fan_asoul_vr", "fan_holo_niji" })
            foreach (var level in Levels) yield return new object[] { pack, level };
    }

    static ContentDb Pack(bool styled = true)
    {
        var db = BattleTests.LoadPack("original_wave3");
        if (!styled) foreach (var streamer in db.Streamers) streamer.AiStyle = null;
        return db;
    }

    static Battle Make(ContentDb db, string lead, uint seed = 136, bool locked = false)
    {
        var roster = db.Streamers.Select(streamer => streamer.Id).ToArray();
        int start = Array.IndexOf(roster, lead);
        Assert.InRange(start, 0, roster.Length - 1);
        var ordered = Enumerable.Range(0, roster.Length).Select(offset => roster[(start + offset) % roster.Length]).ToArray();
        return new Battle(db, new IList<string>[] { ordered.Take(6).ToArray(), ordered.Skip(6).Take(6).ToArray() },
            new[] { "风格甲", "风格乙" }, new BattleOptions
            {
                Seed = seed, Modifiers = locked ? new List<string> { "no_switch" } : null
            });
    }

    static string Snapshot(Battle battle)
    {
        // 私有随机流可按决策需要推进；真实状态、内容、统计及历史不得被搜索分支改写。
        return JsonSerializer.Serialize(new
        {
            battle.Phase, battle.Turn, battle.Winner, battle.Need, battle.ActedFirst, battle.Sides,
            battle.Log, battle.Events, Record = battle.Record.ToJson(), Summary = BattleSummary.From(battle),
            Content = battle.Db.ContentHash()
        }, Json);
    }

    static void AssertAction(Battle battle, int side, BattleAction action)
    {
        var legal = battle.LegalFor(side);
        if (action.Type == ActionType.Switch) Assert.Contains(action.Index, legal.Switches);
        else if (legal.Controlled != null) Assert.Equal(ActionType.Pass, action.Type);
        else if (legal.Struggle) Assert.Equal(ActionType.Struggle, action.Type);
        else
        {
            Assert.Equal(ActionType.Skill, action.Type);
            Assert.Contains(legal.Skills, option => option.Index == action.Index && option.Usable);
        }
    }

    static void Prepare(Battle battle, int position)
    {
        if (position == 1) battle.Active(0).Hp = Math.Max(1, battle.Active(0).MaxHp / 4);
        if (position == 2) battle.Active(0).Statuses.Add(new StatusInst { Id = "disconnect", Left = 3 });
        if (position == 3) foreach (var slot in battle.Active(0).Skills) slot.Pp = 0;
    }

    [Fact]
    public void EveryThirdWaveStreamerHasItsOwnRecognizedStyleAndDistinctWeightSet()
    {
        var db = Pack();
        Assert.Equal(Ids.OrderBy(id => id, StringComparer.Ordinal), db.Streamers.Select(streamer => streamer.Id).OrderBy(id => id, StringComparer.Ordinal));
        Assert.Equal(12, db.Streamers.Count);
        Assert.Equal(12, db.Streamers.Select(streamer => streamer.AiStyle).Distinct(StringComparer.Ordinal).Count());
        var presets = new List<string>();
        foreach (var streamer in db.Streamers)
        {
            Assert.Equal(streamer.Id, streamer.AiStyle);
            var weights = AiStyles.Get(streamer.AiStyle);
            Assert.NotNull(weights);
            presets.Add(weights.ToJson());
        }
        Assert.Equal(12, presets.Distinct(StringComparer.Ordinal).Count());
        db.Validate();
        Assert.Null(AiStyles.Get(null));
        Assert.Null(AiStyles.Get("og3_unknown"));
    }

    [Theory]
    [MemberData(nameof(Streamers))]
    public void DedicatedWeightsAreFiniteLegalIndependentAndKeepTheRandomSamplingContract(string id)
    {
        var weights = AiStyles.Get(id);
        Assert.NotNull(weights);
        var baseline = AiWeights.Default;
        Assert.Equal(baseline.Names(), weights.Names());
        Assert.All(weights.Names(), name => Assert.True(double.IsFinite(weights.Get(name)), id + ":" + name));
        foreach (string name in weights.Names().Where(name => name.EndsWith("Probability", StringComparison.Ordinal)))
            Assert.InRange(weights.Get(name), 0, 1);
        Assert.InRange(weights.ControlledSwitchMinHpRatio, 0, 1);
        Assert.InRange(weights.HealMaxHpRatio, 0, 1);
        Assert.InRange(weights.StageMinHpRatio, 0, 1);
        Assert.InRange(weights.StageBoostTarget, 0, 6);
        Assert.True(weights.DamageScoreMultiplier > 0);
        Assert.Equal(baseline.RandomScoreRange, weights.RandomScoreRange);
        Assert.Equal(baseline.ControlledSwitchProbability, weights.ControlledSwitchProbability);
        Assert.Equal(baseline.SwitchProbability, weights.SwitchProbability);
        string frozen = weights.ToJson();
        Assert.Equal(frozen, AiWeights.FromJson(frozen).ToJson());
        Assert.NotSame(weights, AiStyles.Get(id));
        foreach (string name in weights.Names()) weights.Set(name, weights.Get(name) + 10);
        Assert.NotEqual(frozen, weights.ToJson());
        Assert.Equal(frozen, AiStyles.Get(id).ToJson());
        Assert.Equal(baseline.ToJson(), AiWeights.Default.ToJson());
        var db = Pack();
        db.Streamer(id).AiStyle = id.ToUpperInvariant();
        var error = Assert.Throws<KeyError>(() => db.Validate());
        Assert.Contains(id, error.Message);
        Assert.Contains("aiStyle", error.Message);
    }

    [Theory]
    [InlineData("og3_liling", "og3_bell_skip", "priority")]
    [InlineData("og3_yaye", "og3_slow_letter", "weak")]
    [InlineData("og3_rongjiao", "og3_scene_hold", "trap")]
    [InlineData("og3_paochi", "og3_scale_tuning", "boost")]
    [InlineData("og3_ruandian", "og3_ribbon_mirror", "reflect")]
    [InlineData("og3_miantang", "og3_warm_sip", "heal")]
    [InlineData("og3_zhiyuan", "og3_wind_palette", "boost")]
    [InlineData("og3_guodong", "og3_jelly_hop", "damage")]
    [InlineData("og3_xingguan", "og3_quiet_reserve", "regen")]
    [InlineData("og3_yunkou", "og3_pattern_reset", "dispel")]
    [InlineData("og3_modi", "og3_curiosity_pause", "control")]
    [InlineData("og3_jufan", "og3_sunny_landing", "damage")]
    public void EachStyleHasMatchingActionsInItsOwnersRealFourSkillsOrFifth(string id, string skillId, string purpose)
    {
        var db = Pack();
        var streamer = db.Streamer(id);
        var owned = streamer.Skills.Concat(new[] { streamer.Fifth }).ToArray();
        Assert.Equal(4, streamer.Skills.Count);
        Assert.All(owned, skill => Assert.True(db.Skills.ContainsKey(skill), id + ":" + skill));
        Assert.Contains(skillId, owned);
        var skill = db.Skills[skillId];
        Assert.NotNull(AiStyles.Get(streamer.AiStyle));
        switch (purpose)
        {
            case "priority": Assert.True(skill.Power > 0 && skill.Prio > 0); break;
            case "damage": Assert.True(skill.Power > 0); break;
            case "weak": Assert.Contains(skill.Effects, effect => effect.Code == "STATUS" && !db.Statuses[effect.Status].IsControl); break;
            case "control": Assert.Contains(skill.Effects, effect => effect.Code == "STATUS" && db.Statuses[effect.Status].IsControl); break;
            case "boost": Assert.Contains(skill.Effects, effect => effect.Code == "STAGE" && effect.Target == "self" && effect.Changes.Values.Any(value => value > 0)); break;
            case "trap": Assert.Contains(skill.Effects, effect => effect.Code == "FIELD" && effect.Target == "foe" && db.Fields[effect.Field].PreventSwitch); break;
            case "reflect": Assert.Contains(skill.Effects, effect => effect.Code == "FIELD" && db.Fields[effect.Field].ReflectDamage > 0); break;
            case "regen": Assert.Contains(skill.Effects, effect => effect.Code == "FIELD" && db.Fields[effect.Field].Heal > 0); break;
            case "heal": Assert.Contains(skill.Effects, effect => effect.Code == "HEAL" && effect.Frac > 0); break;
            case "dispel": Assert.Contains(skill.Effects, effect => effect.Code == "DISPEL_BOOST" && effect.Target == "foe"); break;
            default: throw new InvalidOperationException("验收必须明确技能用途。");
        }

        var position = Make(db, id);
        position.Active(0).Fields.Clear(); position.Active(1).Fields.Clear();
        position.Active(0).Statuses.Clear(); position.Active(1).Statuses.Clear();
        foreach (string stat in ContentDb.StatKeys)
        {
            position.Active(0).Stages[stat] = 0;
            position.Active(1).Stages[stat] = 0;
        }
        if (purpose == "heal") position.Active(0).Hp = position.Active(0).MaxHp / 4;
        if (purpose == "dispel") position.Active(1).Stages["spa"] = 2;
        if (purpose == "priority" || purpose == "damage")
        {
            if (id == "og3_jufan") position.Active(1).Hp = 1;
            else { position.Active(1).MaxHp *= 10; position.Active(1).Hp = position.Active(1).MaxHp; }
        }
        double threat = purpose == "reflect" ? 0.5 : id == "og3_jufan" ? 2 : 0;
        string before = Snapshot(position); ulong hash = position.StateHash();
        double normal = Score(position, skill, threat, AiWeights.Default);
        double preferred = Score(position, skill, threat, AiStyles.Get(id));
        Assert.True(preferred > normal, id + " 的现有技能应获得严格更高的对应风格估值。");
        Assert.Equal(before, Snapshot(position));
        Assert.Equal(hash, position.StateHash());
    }

    static double Score(Battle battle, SkillDef skill, double threat, AiWeights weights)
    {
        var method = typeof(Ai).GetMethod("NormalSkillScore", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return (double)method.Invoke(null, new object[] { battle, 0, skill, true, threat, weights });
    }

    [Theory]
    [InlineData("og3_liling")]
    [InlineData("og3_paochi")]
    [InlineData("og3_zhiyuan")]
    [InlineData("og3_jufan")]
    public void RealBoostAndRecoverySkillsExpressAHealthyOpeningAndALowHpChangeOfPlan(string id)
    {
        foreach (uint seed in new uint[] { 1, 136, 2026 })
        {
            Battle Position(bool styled, bool low)
            {
                var battle = Make(Pack(styled), id, seed, locked: true);
                foreach (var slot in battle.Active(0).Skills) slot.Pp = 0;
                battle.Active(0).Skills[2].Pp = 5;
                battle.Active(0).Skills[3].Pp = 5;
                foreach (var slot in battle.Active(1).Skills) slot.Pp = 0;
                if (low) battle.Active(0).Hp = battle.Active(0).MaxHp / 4;
                Assert.Contains(battle.Db.Skills[battle.Active(0).Skills[2].Id].Effects,
                    effect => effect.Code == "STAGE" && effect.Target == "self");
                Assert.Contains(battle.Db.Skills[battle.Active(0).Skills[3].Id].Effects, effect => effect.Code == "HEAL");
                Assert.Empty(battle.LegalFor(0).Switches);
                Assert.Equal(2, battle.LegalFor(0).Skills.Count(option => option.Usable));
                return battle;
            }
            var opening = Position(true, false);
            Assert.Equal(BattleAction.Skill(2).ToString(), Ai.Choose(opening, 0).ToString());
            var low = Position(true, true);
            var plainLow = Position(false, true);
            Assert.Equal(BattleAction.Skill(3).ToString(), Ai.Choose(low, 0).ToString());
            Assert.Equal(BattleAction.Skill(2).ToString(), Ai.Choose(plainLow, 0).ToString());
            Assert.Equal(low.Random(), plainLow.Random());
        }
    }

    [Theory]
    [MemberData(nameof(StreamerLevels))]
    public void OpeningLowHpControlAndNoPpChoicesAreLegalDeterministicAndDoNotMutateThePosition(string id, AiLevel level)
    {
        for (int position = 0; position < 4; position++)
        {
            var battle = Make(Pack(), id, (uint)(136001 + Array.IndexOf(Ids, id) * 10 + position));
            Prepare(battle, position);
            var same = battle.Clone();
            string before = Snapshot(battle);
            var action = Ai.Choose(battle, 0, level);
            var again = Ai.Choose(same, 0, level);
            AssertAction(battle, 0, action);
            Assert.Equal(action.ToString(), again.ToString());
            Assert.Equal(before, Snapshot(battle));
            Assert.Equal(before, Snapshot(same));
            Assert.Equal(battle.StateHash(), same.StateHash());
            Assert.Equal(battle.Random(), same.Random());
        }
    }

    [Theory]
    [MemberData(nameof(Streamers))]
    public void EasyIgnoresTheDedicatedStylesForBothRandomAndScoredBranches(string id)
    {
        for (uint seed = 0; seed < 12; seed++)
        {
            var styled = Make(Pack(), id, seed);
            var plain = Make(Pack(false), id, seed);
            if (seed % 2 == 1) { Prepare(styled, 1); Prepare(plain, 1); }
            var expected = Ai.Choose(plain, 0, AiLevel.Easy);
            var actual = Ai.Choose(styled, 0, AiLevel.Easy);
            AssertAction(styled, 0, actual);
            Assert.Equal(expected.ToString(), actual.ToString());
            Assert.Equal(plain.Random(), styled.Random());
        }
    }

    [Theory]
    [MemberData(nameof(Streamers))]
    public void NormalUsesTheActiveOwnersPresetOverCallerWeightsIncludingAfterSwitching(string id)
    {
        var styled = Make(Pack(), id, 136500);
        var plain = Make(Pack(false), id, 136500);
        var caller = AiWeights.Default;
        caller.DamageScoreMultiplier = 100;
        caller.HealMissingHpScore = 100;
        caller.ControlStatusScore = 100;
        string callerBefore = caller.ToJson();
        for (int phase = 0; phase < 2; phase++)
        {
            if (phase == 1)
            {
                styled.ResolveTurn(BattleAction.SwitchTo(1), BattleAction.Pass());
                plain.ResolveTurn(BattleAction.SwitchTo(1), BattleAction.Pass());
                Assert.Equal(1, styled.Sides[0].Active);
            }
            string owner = styled.Active(0).Def.Id;
            var matchingWeights = AiStyles.Get(owner);
            string weightsBefore = matchingWeights.ToJson();
            string before = Snapshot(styled);
            var actual = Ai.Choose(styled, 0, AiLevel.Normal, caller);
            var expected = Ai.ChooseWithWeights(plain, 0, AiLevel.Normal, matchingWeights);
            Assert.Equal(expected.ToString(), actual.ToString());
            AssertAction(styled, 0, actual);
            Assert.Equal(before, Snapshot(styled));
            Assert.Equal(styled.Random(), plain.Random());
            Assert.Equal(callerBefore, caller.ToJson());
            Assert.Equal(weightsBefore, matchingWeights.ToJson());
        }
    }

    [Fact]
    public void StylesDoNotChangeBanOrLeadChoicesOrTheirRandomConsumption()
    {
        var db = Pack(); var plainDb = Pack(false);
        var teams = new IList<string>[] { Ids.Take(6).ToArray(), Ids.Skip(6).Take(6).ToArray() };
        var options = new BattleOptions { Seed = 136600, BanOne = true, LeadPick = true };
        var styled = new Battle(db, teams, new[] { "甲", "乙" }, options);
        var plain = new Battle(plainDb, teams, new[] { "甲", "乙" }, options);
        for (int side = 0; side < 2; side++)
        {
            int banned = Ai.ChooseBan(styled, side);
            Assert.Equal(Ai.ChooseBan(plain, side), banned);
            styled.ChooseBan(side, banned); plain.ChooseBan(side, banned);
        }
        Assert.Equal(Phase.Lead, styled.Phase);
        for (int side = 0; side < 2; side++)
        {
            int lead = Ai.ChooseLead(styled, side);
            Assert.Equal(Ai.ChooseLead(plain, side), lead);
            styled.ChooseLead(side, lead); plain.ChooseLead(side, lead);
        }
        Assert.Equal(Phase.Choose, styled.Phase);
        Assert.Equal(styled.StateHash(), plain.StateHash());
        Assert.Equal(styled.Random(), plain.Random());
        Assert.Equal(JsonSerializer.Serialize(styled.Log, Json), JsonSerializer.Serialize(plain.Log, Json));
        Assert.Equal(JsonSerializer.Serialize(styled.Record.Events, Json), JsonSerializer.Serialize(plain.Record.Events, Json));
    }

    [Theory]
    [InlineData(AiLevel.Easy)]
    [InlineData(AiLevel.Normal)]
    [InlineData(AiLevel.Hard)]
    [InlineData(AiLevel.Expert)]
    public void ReplacementChoicesRemainDeterministicForSingleAndDoubleReplacement(AiLevel level)
    {
        for (int index = 0; index < Ids.Length; index++)
        {
            var battle = Make(Pack(), Ids[index], (uint)(136100 + index));
            battle.Active(0).Hp = 0; battle.Active(0).Fainted = true;
            battle.Need[0] = true; battle.Phase = Phase.Replace;
            if (index % 2 == 1)
            {
                battle.Active(1).Hp = 0; battle.Active(1).Fainted = true; battle.Need[1] = true;
            }
            var same = battle.Clone();
            string before = Snapshot(battle);
            int selected = Ai.ChooseReplacement(battle, 0, level);
            int repeated = Ai.ChooseReplacement(same, 0, level);
            Assert.Equal(selected, repeated);
            Assert.InRange(selected, 0, battle.Sides[0].Team.Count - 1);
            Assert.NotEqual(battle.Sides[0].Active, selected);
            Assert.False(battle.Sides[0].Team[selected].Fainted);
            Assert.Equal(before, Snapshot(battle));
            Assert.Equal(before, Snapshot(same));
            Assert.Equal(battle.StateHash(), same.StateHash());
            Assert.Equal(battle.Random(), same.Random());
        }
    }

    [Theory]
    [MemberData(nameof(ExistingPackLevels))]
    public void UnstyledPacksKeepTheExplicitDefaultDecisionAndRandomStream(string pack, AiLevel level)
    {
        for (int position = 0; position < 3; position++)
        {
            var db = BattleTests.LoadPack(pack);
            Assert.All(db.Streamers, streamer => Assert.Null(streamer.AiStyle));
            var battle = Make(db, db.Streamers[position].Id, (uint)(136200 + position));
            Prepare(battle, position);
            var explicitDefault = battle.Clone();
            string before = Snapshot(battle);
            var actual = Ai.Choose(battle, 0, level);
            var expected = level == AiLevel.Expert
                ? Ai.ChooseExpert(explicitDefault, 0, ExpertBudget.Default).Action
                : Ai.ChooseWithWeights(explicitDefault, 0, level, level == AiLevel.Hard ? AiWeights.HardDefault : AiWeights.Default);
            AssertAction(battle, 0, actual);
            Assert.Equal(expected.ToString(), actual.ToString());
            Assert.Equal(before, Snapshot(battle));
            Assert.Equal(before, Snapshot(explicitDefault));
            Assert.Equal(battle.StateHash(), explicitDefault.StateHash());
            Assert.Equal(battle.Random(), explicitDefault.Random());
        }
    }

    [Theory]
    [InlineData(AiLevel.Easy)]
    [InlineData(AiLevel.Normal)]
    [InlineData(AiLevel.Hard)]
    [InlineData(AiLevel.Expert)]
    public void MissingAndExplicitNullStyleAreEquivalentForThirdWaveContent(AiLevel level)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "Content");
        string rules = File.ReadAllText(Path.Combine(directory, "StreamDuelRules.json"));
        var omitted = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "Packs", "original_wave3.json"))).AsObject();
        foreach (var entry in omitted["streamers"].AsArray()) entry.AsObject().Remove("aiStyle");
        var explicitNull = omitted.DeepClone();
        foreach (var entry in explicitNull["streamers"].AsArray()) entry["aiStyle"] = null;
        var first = ContentDb.FromJson(rules, omitted.ToJsonString());
        var second = ContentDb.FromJson(rules, explicitNull.ToJsonString());
        Assert.Equal(first.ContentHash(), second.ContentHash());
        Assert.Equal(Pack(false).ContentHash(), first.ContentHash());
        foreach (string id in Ids)
        {
            var a = Make(first, id, 136300); var b = Make(second, id, 136300);
            Assert.Equal(Ai.Choose(a, 0, level).ToString(), Ai.Choose(b, 0, level).ToString());
            Assert.Equal(a.StateHash(), b.StateHash());
            Assert.Equal(a.Record.ToJson(), b.Record.ToJson());
            Assert.Equal(a.Random(), b.Random());
        }
    }

    [Theory]
    [MemberData(nameof(Streamers))]
    public void TheSameExplicitActionsResolveIdenticallyWithAndWithoutStyles(string id)
    {
        var styled = Make(Pack(), id, 136400);
        var plain = Make(Pack(false), id, 136400);
        Assert.NotEqual(styled.Db.ContentHash(), plain.Db.ContentHash());
        for (int turn = 0; turn < 3; turn++)
        {
            Assert.Equal(Phase.Choose, styled.Phase);
            Assert.Equal(Phase.Choose, plain.Phase);
            var a = BattleAction.Skill(turn % 2);
            styled.ResolveTurn(a, BattleAction.Pass());
            plain.ResolveTurn(a, BattleAction.Pass());
            Assert.Equal(styled.StateHash(), plain.StateHash());
            Assert.Equal(JsonSerializer.Serialize(styled.Log, Json), JsonSerializer.Serialize(plain.Log, Json));
            Assert.Equal(JsonSerializer.Serialize(styled.Events, Json), JsonSerializer.Serialize(plain.Events, Json));
            Assert.Equal(JsonSerializer.Serialize(styled.Record.Events, Json), JsonSerializer.Serialize(plain.Record.Events, Json));
            Assert.Equal(BattleSummary.From(styled).ToText(), BattleSummary.From(plain).ToText());
            if (styled.Phase == Phase.Replace)
                for (int side = 0; side < 2; side++)
                    if (styled.Need[side])
                    {
                        int replacement = styled.Sides[side].Team.FindIndex(fighter => !fighter.Fainted && fighter.Slot - 1 != styled.Sides[side].Active);
                        styled.Replace(side, replacement); plain.Replace(side, replacement);
                    }
            if (styled.Phase == Phase.Over) break;
        }
        Assert.Equal(styled.Random(), plain.Random());
    }

    static AiWeights HistoricalPreset(string name)
    {
        // 固定 T-53 的公开预设，新增主播不能把旧三种预设切换成后来训练的 Default 基底。
        if (name == "aggressive") return new AiWeights
        {
            DamageScoreMultiplier = 1.7, KnockoutScore = 2.5, FirstKnockoutBonus = 0.9,
            ThreatenedFirstKnockoutBonus = 1.4, ControlledSwitchProbability = 0.1, SwitchProbability = 0.15,
            SwitchMinThreatRatio = 1.2, SwitchMaxActionScore = 0.35, HealMissingHpScore = 0.5,
            FieldSafeScore = 0.2, ControlStatusScore = 0.3, WeakStatusScore = 0.18
        };
        if (name == "defensive") return new AiWeights
        {
            HealMaxHpRatio = 0.8, HealMissingHpScore = 2, FieldSafeScore = 0.75, FieldThreatenedScore = 0.6,
            ReflectScoreCap = 1, ReflectDamageScoreMultiplier = 3, WeakImmunityScore = 0.7, CleanseScore = 0.7,
            ControlledSwitchProbability = 0.7, SwitchProbability = 0.9, SwitchMinThreatRatio = 0.65,
            SwitchMaxActionScore = 0.85, SwitchMinMatchupGain = 0.2
        };
        if (name == "tricky") return new AiWeights
        {
            ControlStatusScore = 1.1, WeakStatusScore = 0.8, DispelFieldScore = 0.5, DispelBoostScore = 0.35,
            StealBoostBaseScore = 0.25, StealBoostPerStageScore = 0.18, TrapAfflictedScore = 0.75,
            TrapScore = 0.45, PpReduceScore = 1.2
        };
        throw new ArgumentException("未知历史风格。");
    }

    [Fact]
    public void DedicatedPresetsCannotPolluteEachOtherOrTheThreeHistoricalPresets()
    {
        var snapshots = Ids.Concat(OldStyles).ToDictionary(id => id, id => AiStyles.Get(id).ToJson(), StringComparer.Ordinal);
        string normal = AiWeights.Default.ToJson(), hard = AiWeights.HardDefault.ToJson(), legacy = AiWeights.Legacy.ToJson();
        foreach (string id in Ids)
        {
            var modified = AiStyles.Get(id);
            foreach (string name in modified.Names()) modified.Set(name, -777);
            foreach (var pair in snapshots) Assert.Equal(pair.Value, AiStyles.Get(pair.Key).ToJson());
        }
        foreach (string name in OldStyles) Assert.Equal(HistoricalPreset(name).ToJson(), AiStyles.Get(name).ToJson());
        Assert.Equal(normal, AiWeights.Default.ToJson());
        Assert.Equal(hard, AiWeights.HardDefault.ToJson());
        Assert.Equal(legacy, AiWeights.Legacy.ToJson());
    }
}
