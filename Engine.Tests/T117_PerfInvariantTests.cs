using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using StreamDuel.Core;
#if !T117_BASELINE
using Xunit;

public class T117_PerfInvariantTests
{
    const string SourceCommit = "751402cb67991d3131b20825d0adf9f842b7c984";
    static string SourceDirectory([CallerFilePath] string file = "") { return Path.GetDirectoryName(file); }
    static string BaselinePath { get { return Path.Combine(SourceDirectory(), "Fixtures", "t117-perf-baseline.zip"); } }
    static string Resources { get { return Path.GetFullPath(Path.Combine(SourceDirectory(), "..", "UnityProject", "Assets", "StreamDuel", "Resources")); } }

    public static IEnumerable<object[]> Cases()
    {
        return T117PerfScenario.Cases().Select(item => new object[] { item.CaseId });
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void OneHundredFullGamesKeepFrozenStateHashLogAndRecordBytes(int caseId)
    {
        var culture = CultureInfo.CurrentCulture;
        var uiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
            var item = T117PerfScenario.Cases().Single(entry => entry.CaseId == caseId);
            var db = T117PerfScenario.Load(Resources, item.Pack);
            var battle = T117PerfScenario.Play(db, item);
            Assert.Equal(Phase.Over, battle.Phase);
            using var archive = ZipFile.OpenRead(BaselinePath);
            Assert.Equal(ReadEntry(archive, item.Stem + ".state"), T117PerfScenario.StateBytes(battle));
            Assert.Equal(ReadEntry(archive, item.Stem + ".record.json"), T117PerfScenario.RecordBytes(battle));
            Assert.Equal(ReadEntry(archive, item.Stem + ".log.json"), T117PerfScenario.LogBytes(battle));
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = uiCulture;
        }
    }

    [Fact]
    public void FrozenBaselineHasExactlyTheAgreedHundredCasesAndAllTwelvePackLevelCombinations()
    {
        Assert.Equal("5bf88b4aae78b9229f0cb081c055edb44ccfe5c57561966c3710997b53519afd",
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(BaselinePath))).ToLowerInvariant());
        using var archive = ZipFile.OpenRead(BaselinePath);
        using var manifest = JsonDocument.Parse(ReadEntry(archive, "manifest.json"));
        Assert.Equal(SourceCommit, manifest.RootElement.GetProperty("sourceCommit").GetString());
        Assert.Equal("7d6033fd5920ec350dacb6e99a27b6a91c11acba18fb7365d7761c3f665e57a3",
            manifest.RootElement.GetProperty("sourceManifestSha256").GetString());
        var entries = manifest.RootElement.GetProperty("cases").EnumerateArray().ToArray();
        Assert.Equal(100, entries.Length);
        var cases = T117PerfScenario.Cases().ToArray();
        Assert.Equal(cases.Select(item => item.CaseId), entries.Select(item => item.GetProperty("caseId").GetInt32()));
        Assert.Equal(100, entries.Select(item => item.GetProperty("seed").GetUInt32()).Distinct().Count());
        Assert.Equal(12, cases.Select(item => item.Pack + "/" + item.Level).Distinct().Count());
        foreach (var entry in entries)
        {
            int id = entry.GetProperty("caseId").GetInt32();
            var expected = cases.Single(item => item.CaseId == id);
            Assert.Equal(expected.Seed, entry.GetProperty("seed").GetUInt32());
            Assert.Equal(expected.Pack, entry.GetProperty("pack").GetString());
            Assert.Equal(expected.Level.ToString(), entry.GetProperty("level").GetString());
            Assert.InRange(entry.GetProperty("winner").GetInt32(), -1, 1);
            Assert.InRange(entry.GetProperty("turns").GetInt32(), 1, Battle.TurnCap);
            foreach (string suffix in new[] { ".state", ".log.json", ".record.json" })
                Assert.NotEmpty(ReadEntry(archive, expected.Stem + suffix));
        }
        Assert.Equal(301, archive.Entries.Count);
    }

    static Battle CloneFixture()
    {
        var db = T117PerfScenario.Load(Resources, "original");
        db.Personas["t117_neutral"] = new PersonaDef
        {
            Id = "t117_neutral", Name = "稳定发挥", Code = "T_INTERACT_REDUCE", Node = Node.First,
            Desc = "互动伤害保持原值", Params = { { "mul", 1 } }
        };
        db.Skills["t117_hit"] = new SkillDef
        {
            Id = "t117_hit", Name = "固定输出", Type = "song", Cat = "status", Pp = 1000,
            Effects = new List<EffectDef> { new EffectDef { At = "post", Code = "FIXED_DAMAGE", Target = "foe", N = 10 } }
        };
        foreach (var streamer in db.Streamers)
        {
            streamer.Persona = "t117_neutral";
            streamer.Stats["hp"] = 1000;
            streamer.Stats["spd"] = 100;
            streamer.Skills[0] = "t117_hit";
        }
        db.Validate();
        var battle = T117PerfScenario.Start(db, T117PerfScenario.Cases().Single(item => item.CaseId == 1200));
        battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());
        battle.ResolveTurn(BattleAction.SwitchTo(1), BattleAction.Skill(0));
        return battle;
    }

    static string Snapshot(Battle battle)
    {
        return JsonSerializer.Serialize(battle, new JsonSerializerOptions { IncludeFields = true })
            + "\n" + BattleSummary.From(battle).ToText();
    }

    static Battle InternalCopy(Battle battle, string methodName)
    {
        var method = typeof(Battle).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return (Battle)method.Invoke(battle, null);
    }

    [Fact]
    public void PublicCloneDeeplyIsolatesEveryMutableFighterAndNestedHistoryObject()
    {
        var original = CloneFixture();
        foreach (var fighter in original.Sides.SelectMany(side => side.Team))
        {
            fighter.Stages = new Dictionary<string, int>(fighter.Stages, StringComparer.OrdinalIgnoreCase);
            fighter.Statuses.Add(new StatusInst { Id = "hater", Left = 3 });
            fighter.Fields.Add(new FieldInst { Id = "hype", Turns = -1, Stacks = 2 });
        }
        string before = Snapshot(original);
        ulong hash = original.StateHash();
        double nextRandom = original.Clone().Random();
        var copy = original.Clone();
        var sibling = original.Clone();
        Assert.Equal(before, Snapshot(copy));
        Assert.Equal(hash, copy.StateHash());
        Assert.Same(original.Db, copy.Db);
        for (int side = 0; side < 2; side++)
            for (int index = 0; index < 6; index++)
            {
                var fighter = copy.Sides[side].Team[index];
                var source = original.Sides[side].Team[index];
                Assert.NotSame(source, fighter);
                Assert.Same(source.Def, fighter.Def);
                Assert.Same(source.Stages.Comparer, fighter.Stages.Comparer);
                fighter.Hp--;
                fighter.MaxHp++;
                fighter.EnteredTurn++;
                fighter.LastSkillIndex = 4;
                fighter.FirstControlImmunityUsed = !fighter.FirstControlImmunityUsed;
                fighter.EndureUsed = !fighter.EndureUsed;
                fighter.Stages["ATK"] = 4;
                fighter.Skills[0].Pp = 0;
                fighter.Skills[0].Max++;
                fighter.Skills[0].Fifth = true;
                fighter.Skills.RemoveAt(4);
                fighter.Statuses[0].Left++;
                fighter.Statuses.Add(new StatusInst { Id = "disconnect", Left = 2 });
                fighter.Fields[0].Turns = 7;
                fighter.Fields[0].Stacks++;
                fighter.Fields.Add(new FieldInst { Id = "mirror", Turns = 2 });
            }
        copy.Sides[0].Team.Reverse();
        copy.Sides[1].Team.RemoveAt(5);
        copy.Sides[0].Name = "分支甲";
        copy.Need[0] = !copy.Need[0];
        copy.ActedFirst[1] = !copy.ActedFirst[1];
        copy.Record.Names[0] = "分支录像";
        copy.Record.Teams[0][0] = "分支成员";
        copy.Record.Modifiers.Add("hp_x2");
        copy.Record.Events[0].A0 = BattleAction.Pass();
        copy.Record.Events[0].RngState = 1;
        copy.Record.Events.RemoveAt(copy.Record.Events.Count - 1);
        copy.Log[0].Text = "分支日志";
        copy.Log[0].SnapActive[0] = 5;
        copy.Log[0].SnapHp[0][0] = 0;
        copy.Log[0].SnapHp[1] = new[] { 1 };
        copy.Events[0].Amount = 117;
        copy.Random();
        copy.Random();
        Assert.Equal(before, Snapshot(original));
        Assert.Equal(before, Snapshot(sibling));
        Assert.Equal(hash, original.StateHash());
        Assert.Equal(hash, sibling.StateHash());
        Assert.Equal(nextRandom, original.Clone().Random());
        Assert.Equal(nextRandom, sibling.Clone().Random());
    }

    [Fact]
    public void SimulationCopySkipsOnlyHistoryAndKeepsStateStatsRngAndIndependentFutureEvolution()
    {
        var original = CloneFixture();
        string before = Snapshot(original);
        ulong hash = original.StateHash();
        var simulation = InternalCopy(original, "CopyForSimulation");
        var sibling = InternalCopy(original, "CopyForSimulation");
        Assert.Equal(hash, simulation.StateHash());
        Assert.Empty(simulation.Log);
        Assert.Empty(simulation.Record.Events);
        Assert.Empty(simulation.Events);
        Assert.Equal(BattleSummary.From(original).ToText(), BattleSummary.From(simulation).ToText());
        AssertSameFighterStats(original, simulation);
        Assert.Equal(2, BattleSummary.From(original).Turns);
        Assert.Equal(0, BattleSummary.From(simulation).Turns);
        Assert.Equal(original.Clone().Random(), simulation.Clone().Random());
        Assert.Equal(original.Record.Version, simulation.Record.Version);
        Assert.Equal(original.Record.ContentHash, simulation.Record.ContentHash);
        Assert.Equal(original.Record.Names, simulation.Record.Names);
        Assert.Equal(original.Record.Modifiers, simulation.Record.Modifiers);
        for (int side = 0; side < 2; side++) Assert.Equal(original.Record.Teams[side], simulation.Record.Teams[side]);
        Assert.Equal(before, Snapshot(original));

        int logStart = original.Log.Count, recordStart = original.Record.Events.Count;
        simulation.ResolveTurn(BattleAction.Skill(0), BattleAction.Skill(0));
        Assert.Equal(before, Snapshot(original));
        Assert.Equal(hash, sibling.StateHash());
        Assert.Equal(10, BattleSummary.From(original).Sides[0].Sum(stats => stats.DamageDealt));
        Assert.Equal(20, BattleSummary.From(simulation).Sides[0].Sum(stats => stats.DamageDealt));
        original.ResolveTurn(BattleAction.Skill(0), BattleAction.Skill(0));
        Assert.Equal(original.StateHash(), simulation.StateHash());
        Assert.Equal(BattleSummary.From(original).ToText(), BattleSummary.From(simulation).ToText());
        AssertSameFighterStats(original, simulation);
        Assert.Equal(3, BattleSummary.From(original).Turns);
        Assert.Equal(1, BattleSummary.From(simulation).Turns);
        var json = new JsonSerializerOptions { IncludeFields = true };
        Assert.Equal(JsonSerializer.Serialize(original.Log.Skip(logStart), json), JsonSerializer.Serialize(simulation.Log, json));
        Assert.Equal(JsonSerializer.Serialize(original.Record.Events.Skip(recordStart), json), JsonSerializer.Serialize(simulation.Record.Events, json));
        Assert.Equal(original.Clone().Random(), simulation.Clone().Random());
        string advanced = Snapshot(original);
        simulation.Active(0).Hp = 1;
        simulation.Active(0).Stages["atk"] = 6;
        simulation.Active(0).Skills[0].Pp = 0;
        simulation.Record.Teams[0][0] = "模拟成员";
        simulation.Record.Names[0] = "模拟甲";
        Assert.Equal(advanced, Snapshot(original));
        Assert.Equal(hash, sibling.StateHash());
    }

    static void AssertSameFighterStats(Battle expected, Battle actual)
    {
        var first = BattleSummary.From(expected);
        var second = BattleSummary.From(actual);
        Assert.Equal(first.Winner, second.Winner);
        Assert.Equal(first.Sides.Length, second.Sides.Length);
        for (int side = 0; side < first.Sides.Length; side++)
        {
            Assert.Equal(first.Sides[side].Count, second.Sides[side].Count);
            for (int index = 0; index < first.Sides[side].Count; index++)
            {
                var source = first.Sides[side][index];
                var copy = second.Sides[side][index];
                Assert.Equal(source.StreamerId, copy.StreamerId);
                Assert.Equal(source.DamageDealt, copy.DamageDealt);
                Assert.Equal(source.DamageTaken, copy.DamageTaken);
                Assert.Equal(source.Kos, copy.Kos);
                Assert.Equal(source.TurnsActive, copy.TurnsActive);
                Assert.Equal(source.Fainted, copy.Fainted);
                Assert.Equal(source.SkillUses.OrderBy(pair => pair.Key, StringComparer.Ordinal),
                    copy.SkillUses.OrderBy(pair => pair.Key, StringComparer.Ordinal));
            }
        }
    }

    [Theory]
    [InlineData("interact", 80)]
    [InlineData("talent", 80)]
    [InlineData("status", 80)]
    [InlineData("interact", 0)]
    [InlineData("talent", 0)]
    [InlineData("status", 0)]
    public void InternalDamageEstimateMatchesPublicMathWithoutRemovingPublicNotes(string category, int power)
    {
        var battle = CloneFixture();
        var skill = new SkillDef { Id = "t117_damage", Name = "预览检查", Type = "song", Cat = category, Power = power, Pp = 20 };
        battle.Db.Skills[skill.Id] = skill;
        battle.Active(0).Skills[0].Id = skill.Id;
        battle.Active(0).Stages["atk"] = 2;
        battle.Active(0).Stages["spa"] = 2;
        battle.Active(0).Statuses.Add(new StatusInst { Id = "flame", Left = 3 });
        battle.Db.Fields["t117_shield"] = new FieldDef { Id = "t117_shield", Name = "观众护盾", Kind = "turn", DmgTakenMul = 0.75 };
        battle.Active(1).Fields.Add(new FieldInst { Id = "t117_shield", Turns = 3 });
        string before = Snapshot(battle);
        ulong hash = battle.StateHash();
        var estimate = typeof(Battle).GetMethod("EstimateDamageForAi", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(estimate);
        foreach (double powerMul in new[] { 0.0, 1.0, 1.5, 2.25 })
            foreach (double randomFactor in new[] { 0.85, 0.925, 1.0 })
            {
                var publicResult = battle.CalcDamage(0, skill, powerMul, true, randomFactor);
                int internalResult = (int)estimate.Invoke(battle, new object[] { 0, skill, powerMul, randomFactor });
                Assert.Equal(publicResult.Dmg, internalResult);
                Assert.NotNull(publicResult.Notes);
                Assert.False(publicResult.Crit);
                if (power > 0) Assert.Contains(publicResult.Notes, note => note.Contains("观众护盾"));
                else { Assert.Equal(0, internalResult); Assert.Empty(publicResult.Notes); }
            }
        var preview = battle.PreviewDamage(0, 0);
        Assert.NotNull(preview.Notes);
        if (power > 0 && category != "status")
        {
            Assert.Contains(preview.Notes, note => note.Contains("估算"));
            Assert.Contains(preview.Notes, note => note.Contains("观众护盾"));
            Assert.Equal(battle.CalcDamage(0, skill, 1, true, 0.85).Dmg, preview.Min);
            Assert.Equal(battle.CalcDamage(0, skill, 1, true, 1).Dmg, preview.Max);
        }
        else { Assert.Equal(0, preview.Min); Assert.Equal(0, preview.Max); Assert.Empty(preview.Notes); }
        Assert.Equal(hash, battle.StateHash());
        Assert.Equal(before, Snapshot(battle));
    }

    [Fact]
    public void DiagnosticCountersAreThreadLocalAndNeverChangeBattleHistoryOrRandomState()
    {
        var battle = CloneFixture();
        string before = Snapshot(battle);
        ulong hash = battle.StateHash();
        long[] counts = DiagnosticCounts();
        battle.Clone();
        InternalCopy(battle, "CopyForSimulation");
        InternalCopy(battle, "CopyForHardAi");
        long[] after = DiagnosticCounts();
        Assert.Equal(counts.Select(count => count + 1), after);
        long[] workerBefore = null, workerAfter = null;
        Exception workerError = null;
        var worker = new Thread(() =>
        {
            try
            {
                workerBefore = DiagnosticCounts();
                battle.Clone();
                InternalCopy(battle, "CopyForSimulation");
                InternalCopy(battle, "CopyForHardAi");
                workerAfter = DiagnosticCounts();
            }
            catch (Exception error) { workerError = error; }
        });
        worker.Start();
        worker.Join();
        Assert.Null(workerError);
        Assert.Equal(new long[] { 0, 0, 0 }, workerBefore);
        Assert.Equal(new long[] { 1, 1, 1 }, workerAfter);
        Assert.Equal(after, DiagnosticCounts());
        Assert.Equal(hash, battle.StateHash());
        Assert.Equal(before, Snapshot(battle));
    }

    static long[] DiagnosticCounts()
    {
        return new[] { BattleDiagnostics.CurrentThreadCloneCount, BattleDiagnostics.CurrentThreadSimulationCloneCount,
            BattleDiagnostics.CurrentThreadHardCopyCount };
    }

    public static IEnumerable<object[]> NormalizationCases()
    {
        foreach (string control in new[] { "none", "named", "null", "empty" })
            foreach (bool locked in new[] { false, true })
                foreach (string resource in new[] { "mixed", "depleted", "empty", "free", "free_empty", "free_sixth_down" })
                    yield return new object[] { control, locked, resource };
    }

    [Theory]
    [MemberData(nameof(NormalizationCases))]
    public void FastNormalizeMatchesPublicLegalReferenceForEveryActionAndBoundary(string control, bool locked, string resource)
    {
        var battle = CloneFixture();
        battle.Db.Fields["t117_lock"] = new FieldDef { Id = "t117_lock", Name = "锁麦", Kind = "turn", PreventSwitch = true };
        battle.Db.Statuses["t117_control"] = new StatusDef
        {
            Id = "t117_control", Name = control == "null" ? null : control == "empty" ? "" : "暂时离席", Cat = "control", Skip = 2
        };
        battle.Db.Personas["t117_free"] = new PersonaDef { Id = "t117_free", Name = "队友支持", Code = "T_FREE_PP_SLOT6" };
        if (resource.StartsWith("free", StringComparison.Ordinal))
            foreach (var streamer in battle.Db.Streamers) streamer.Persona = "t117_free";
        for (int side = 0; side < 2; side++)
        {
            var active = battle.Active(side);
            active.Statuses.Clear(); active.Fields.Clear();
            if (control != "none") active.Statuses.Add(new StatusInst { Id = "t117_control", Left = 2 });
            if (locked) active.Fields.Add(new FieldInst { Id = "t117_lock", Turns = 3 });
            foreach (var slot in active.Skills) slot.Pp = 0;
            if (resource == "mixed") active.Skills[1].Pp = 1;
            if (resource == "empty" || resource == "free_empty") active.Skills.Clear();
            battle.Sides[side].Team[2].Fainted = true;
            battle.Sides[side].Team[2].Hp = 0;
            if (resource == "free_sixth_down")
            {
                battle.Sides[side].Team[5].Fainted = true;
                battle.Sides[side].Team[5].Hp = 0;
            }
        }
        string before = Snapshot(battle);
        ulong hash = battle.StateHash();
        double nextRandom = battle.Clone().Random();
        var normalize = typeof(Battle).GetMethod("Normalize", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(normalize);
        var types = Enum.GetValues(typeof(ActionType)).Cast<ActionType>().Concat(new[] { (ActionType)(-1), (ActionType)999 });
        foreach (int side in new[] { 0, 1 })
        {
            var legal = battle.LegalFor(side);
            Assert.Equal(resource == "free" || resource == "free_empty", legal.FreePp);
            if (control != "none") Assert.All(legal.Skills, skill => Assert.False(skill.Usable));
            foreach (var type in types)
                foreach (int index in new[] { int.MinValue, -1, 0, 1, 2, 3, 4, 5, 6, int.MaxValue })
                {
                    var input = new BattleAction { Type = type, Index = index };
                    var expected = NormalizeByPublicLegal(battle, side, input);
                    var actual = (BattleAction)normalize.Invoke(battle, new object[] { side, input });
                    Assert.Equal(expected, actual);
                }
            int available = side == 0 ? 0 : 1;
            if (!locked)
            {
                var change = BattleAction.SwitchTo(available);
                Assert.Equal(change, (BattleAction)normalize.Invoke(battle, new object[] { side, change }));
            }
            var invalid = (BattleAction)normalize.Invoke(battle, new object[] { side, BattleAction.Skill(-1) });
            if (control == "named" || control == "empty") Assert.Equal(ActionType.PassControlled, invalid.Type);
            else if (control == "null") Assert.Equal(ActionType.Pass, invalid.Type);
            else if (resource == "mixed" || resource == "free") Assert.Equal(ActionType.Pass, invalid.Type);
            else Assert.Equal(ActionType.Struggle, invalid.Type);
        }
        Assert.Equal(hash, battle.StateHash());
        Assert.Equal(before, Snapshot(battle));
        Assert.Equal(nextRandom, battle.Clone().Random());
    }

    // 固定优化前通过公开 LegalFor 做归一化的契约，独立于待优化的内部快速路径。
    static BattleAction NormalizeByPublicLegal(Battle battle, int side, BattleAction action)
    {
        var legal = battle.LegalFor(side);
        if (action.Type == ActionType.Switch && legal.Switches.Contains(action.Index)) return action;
        if (legal.Controlled != null) return new BattleAction { Type = ActionType.PassControlled };
        if (legal.Struggle) return new BattleAction { Type = ActionType.Struggle };
        if (action.Type == ActionType.Skill && action.Index >= 0 && action.Index < legal.Skills.Count && legal.Skills[action.Index].Usable)
            return action;
        return BattleAction.Pass();
    }

    [Theory]
    [InlineData(false, -1)]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(true, -1)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    public void FastPriorityAndOrderPreviewKeepPublicNumbersPartsAndReasons(bool enteredThisTurn, int speedStage)
    {
        var battle = CloneFixture();
        battle.Db.Personas["t117_priority"] = new PersonaDef
        {
            Id = "t117_priority", Name = "登场节奏", Code = "T_FIRST_TURN_PRIO", Params = { { "prio", 3 } }
        };
        foreach (var streamer in battle.Db.Streamers) streamer.Persona = "t117_priority";
        battle.Db.Fields["t117_plus"] = new FieldDef { Id = "t117_plus", Name = "加速灯", Kind = "turn", Prio = 2 };
        battle.Db.Fields["t117_minus"] = new FieldDef { Id = "t117_minus", Name = "降速灯", Kind = "turn", Prio = -1 };
        battle.Db.Fields["t117_zero"] = new FieldDef { Id = "t117_zero", Name = "普通灯", Kind = "turn", Prio = 0 };
        battle.Db.Struggle.Prio = -4;
        int[] skillPriorities = { -2, 0, 3, 1, -1 };
        for (int side = 0; side < 2; side++)
        {
            var active = battle.Active(side);
            active.EnteredTurn = side == 0 && enteredThisTurn ? battle.Turn : battle.Turn - 1;
            active.Statuses.Clear(); active.Fields.Clear();
            active.Fields.Add(new FieldInst { Id = "t117_plus", Turns = 3 });
            active.Fields.Add(new FieldInst { Id = "t117_minus", Turns = 3 });
            active.Fields.Add(new FieldInst { Id = "t117_zero", Turns = 3 });
            active.Stages["spd"] = side == 0 ? speedStage : 0;
            for (int index = 0; index < active.Skills.Count; index++)
            {
                string id = "t117_priority_" + side + "_" + index;
                battle.Db.Skills[id] = new SkillDef { Id = id, Name = "先制测试", Cat = "status", Type = "song", Pp = 20, Prio = skillPriorities[index] };
                active.Skills[index].Id = id;
                active.Skills[index].Pp = 20;
            }
        }
        string before = Snapshot(battle);
        ulong hash = battle.StateHash();
        double nextRandom = battle.Clone().Random();
        var priority = typeof(Battle).GetMethod("ActPrio", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(priority);
        for (int side = 0; side < 2; side++)
        {
            var parts = new List<string> { "加速灯+2", "降速灯-1" };
            if (side == 0 && enteredThisTurn) parts.Add("人设·登场节奏+3");
            var legal = battle.LegalFor(side);
            for (int index = 0; index < battle.Active(side).Skills.Count; index++)
            {
                var skill = battle.Db.Skills[battle.Active(side).Skills[index].Id];
                var info = battle.PrioOf(side, skill);
                Assert.Equal(skillPriorities[index] + 1 + (side == 0 && enteredThisTurn ? 3 : 0), info.Value);
                Assert.Equal(parts, info.Parts);
                Assert.Equal(info.Value, legal.Skills[index].Prio.Value);
                Assert.Equal(parts, legal.Skills[index].Prio.Parts);
                var action = BattleAction.Skill(index);
                Assert.Equal(info.Value, (int)priority.Invoke(battle, new object[] { side, action }));
            }
            foreach (var type in Enum.GetValues(typeof(ActionType)).Cast<ActionType>().Where(type => type != ActionType.Skill))
            {
                var action = new BattleAction { Type = type, Index = -117 };
                Assert.Equal(PriorityByPublicInfo(battle, side, action), (int)priority.Invoke(battle, new object[] { side, action }));
            }
        }
        var inputs = new[] { BattleAction.Skill(0), BattleAction.Skill(1), BattleAction.Skill(2), BattleAction.Skill(-1),
            BattleAction.SwitchTo(0), BattleAction.SwitchTo(1), BattleAction.SwitchTo(6), BattleAction.Pass(),
            new BattleAction { Type = ActionType.PassControlled }, new BattleAction { Type = ActionType.Struggle }, BattleAction.Surrender() };
        foreach (var first in inputs)
            foreach (var second in inputs)
            {
                var expected = PreviewByPublicInfo(battle, first, second);
                var actual = battle.PreviewOrder(first, second);
                Assert.Equal(expected.First, actual.First);
                Assert.Equal(expected.Reason, actual.Reason);
            }
        Assert.Equal(hash, battle.StateHash());
        Assert.Equal(before, Snapshot(battle));
        Assert.Equal(nextRandom, battle.Clone().Random());
    }

    static int PriorityByPublicInfo(Battle battle, int side, BattleAction action)
    {
        if (action.Type == ActionType.Pass || action.Type == ActionType.PassControlled) return 0;
        var skill = action.Type == ActionType.Skill ? battle.Db.Skills[battle.Active(side).Skills[action.Index].Id] : battle.Db.Struggle;
        return battle.PrioOf(side, skill).Value;
    }

    static Battle.OrderPreview PreviewByPublicInfo(Battle battle, BattleAction firstAction, BattleAction secondAction)
    {
        firstAction = NormalizeByPublicLegal(battle, 0, firstAction);
        secondAction = NormalizeByPublicLegal(battle, 1, secondAction);
        if (firstAction.Type == ActionType.Switch && secondAction.Type == ActionType.Switch)
            return new Battle.OrderPreview { First = -1, Reason = "双方换人，没有行动" };
        if (firstAction.Type == ActionType.Switch)
            return new Battle.OrderPreview { First = 1, Reason = "对方换人，本方唯一行动，先手行动" };
        if (secondAction.Type == ActionType.Switch)
            return new Battle.OrderPreview { First = 0, Reason = "对方换人，本方唯一行动，先手行动" };
        int firstPriority = PriorityByPublicInfo(battle, 0, firstAction), secondPriority = PriorityByPublicInfo(battle, 1, secondAction);
        int firstSpeed = battle.StatVal(battle.Active(0), "spd"), secondSpeed = battle.StatVal(battle.Active(1), "spd");
        if (firstPriority == secondPriority && firstSpeed == secondSpeed)
            return new Battle.OrderPreview { First = -1, Reason = "随机" };
        int first = firstPriority != secondPriority ? (firstPriority > secondPriority ? 0 : 1) : (firstSpeed > secondSpeed ? 0 : 1);
        int highPriority = first == 0 ? firstPriority : secondPriority, lowPriority = first == 0 ? secondPriority : firstPriority;
        int highSpeed = first == 0 ? firstSpeed : secondSpeed, lowSpeed = first == 0 ? secondSpeed : firstSpeed;
        return new Battle.OrderPreview
        {
            First = first,
            Reason = firstPriority != secondPriority ? "先制 " + highPriority + " 对 " + lowPriority
                : "先制都是 " + highPriority + "，反应 " + highSpeed + " 对 " + lowSpeed
        };
    }

    static byte[] ReadEntry(ZipArchive archive, string name)
    {
        var entry = archive.GetEntry(name);
        Assert.NotNull(entry);
        using var input = entry.Open();
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }
}
#endif

internal sealed class T117PerfCase
{
    public int CaseId;
    public string Pack;
    public AiLevel Level;
    public uint Seed;
    public string Stem { get { return CaseId.ToString("D4", CultureInfo.InvariantCulture); } }
}

internal static class T117PerfScenario
{
    internal static readonly string[] Packs = { "fan_asoul_vr", "fan_holo_niji", "original", "original_wave2" };
    internal static readonly AiLevel[] Levels = { AiLevel.Normal, AiLevel.Hard, AiLevel.Expert };
    static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions { IncludeFields = true };

    internal static IEnumerable<T117PerfCase> Cases()
    {
        for (int pack = 0; pack < Packs.Length; pack++)
            for (int level = 0; level < Levels.Length; level++)
                for (int game = 0; game < (pack == 2 && level == 0 ? 12 : 8); game++)
                {
                    int id = pack * 600 + level * 200 + game;
                    yield return new T117PerfCase { CaseId = id, Pack = Packs[pack], Level = Levels[level], Seed = 1170001u + (uint)id };
                }
    }

    internal static ContentDb Load(string resources, string pack)
    {
        return ContentDb.FromJson(File.ReadAllText(Path.Combine(resources, "StreamDuelRules.json")),
            File.ReadAllText(Path.Combine(resources, "Packs", pack + ".json")));
    }

    internal static Battle Start(ContentDb db, T117PerfCase item)
    {
        var ids = db.Streamers.Select(streamer => streamer.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        if (ids.Length < 6) throw new InvalidOperationException("性能基线内容包不足六人。");
        var random = new Rng(item.Seed);
        var teams = new IList<string>[2];
        for (int side = 0; side < 2; side++)
        {
            var order = (string[])ids.Clone();
            for (int index = order.Length - 1; index > 0; index--)
            {
                int other = (int)(random.Next() * (index + 1));
                string swap = order[index]; order[index] = order[other]; order[other] = swap;
            }
            teams[side] = order.Take(6).ToArray();
        }
        return new Battle(db, teams, new[] { "基准甲", "基准乙" }, item.Seed);
    }

    internal static Battle Play(ContentDb db, T117PerfCase item)
    {
        var battle = Start(db, item);
        int steps = 0;
        while (battle.Phase != Phase.Over && steps++ < 600) Step(battle, item.Level);
        if (battle.Phase != Phase.Over) throw new InvalidOperationException("性能基线对局未在保护上限内结束：" + item.CaseId);
        return battle;
    }

    internal static void Step(Battle battle, AiLevel level)
    {
        if (battle.Phase == Phase.Choose)
        {
            var first = Ai.Choose(battle, 0, level);
            var second = Ai.Choose(battle, 1, level);
            battle.ResolveTurn(first, second);
        }
        else if (battle.Phase == Phase.Replace)
        {
            for (int side = 0; side < 2 && battle.Phase == Phase.Replace; side++)
                if (battle.Need[side]) battle.Replace(side, Ai.ChooseReplacement(battle, side, level));
        }
        else throw new InvalidOperationException("性能基线出现不支持的阶段：" + battle.Phase);
    }

    internal static byte[] StateBytes(Battle battle) { return Encoding.UTF8.GetBytes(battle.StateHash().ToString("x16", CultureInfo.InvariantCulture) + "\n"); }
    internal static byte[] RecordBytes(Battle battle) { return Encoding.UTF8.GetBytes(battle.Record.ToJson() + "\n"); }
    internal static byte[] LogBytes(Battle battle) { return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(battle.Log, JsonOptions) + "\n"); }
}
