using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using StreamDuel.Core;
using Xunit;

public class T104_MixedPacksTests
{
    [Fact]
    public void OriginalAndAsoulMergeNineteenFightersWithoutChangingEitherInput()
    {
        var original = BattleTests.LoadPack("original");
        var fan = BattleTests.LoadPack("fan_asoul_vr");
        string originalHash = original.ContentHash();
        string fanHash = fan.ContentHash();

        var merged = ContentDb.Merge(original, fan);

        Assert.Equal(19, merged.Streamers.Count);
        Assert.Equal("mix:fan_asoul_vr,original", merged.PackId);
        Assert.Equal(original.Streamers.Concat(fan.Streamers).Select(s => s.Id).OrderBy(id => id, StringComparer.Ordinal),
            merged.Streamers.Select(s => s.Id).OrderBy(id => id, StringComparer.Ordinal));
        Assert.All(original.Streamers, s => Assert.Equal(original.PackNotice, merged.NoticeFor(s.Id)));
        Assert.All(fan.Streamers, s => Assert.Equal(fan.PackNotice, merged.NoticeFor(s.Id)));
        Assert.Equal(fan.Skills["stir_drama"].Name, merged.Skills["stir_drama"].Name);
        Assert.Equal(fan.Fields["blackhist"].Name, merged.Fields["blackhist"].Name);
        Assert.Equal(fan.Statuses["flame"].Name, merged.Statuses["flame"].Name);
        Assert.True(merged.Skills["av_bella_centerbeat"].PackDefined);
        Assert.True(merged.Personas["av_bella_rhythm"].PackDefined);
        Assert.Equal(originalHash, original.ContentHash());
        Assert.Equal(fanHash, fan.ContentHash());
        Assert.Equal(originalHash, BattleTests.LoadPack("original").ContentHash());
        Assert.Equal(fanHash, BattleTests.LoadPack("fan_asoul_vr").ContentHash());
    }

    [Fact]
    public void PackOrderDoesNotChangeContentHashRandomTeamsOrDisplayedNotices()
    {
        var original = BattleTests.LoadPack("original");
        var fan = BattleTests.LoadPack("fan_asoul_vr");
        var forward = ContentDb.Merge(original, fan);
        var reverse = ContentDb.Merge(fan, original);

        Assert.Equal(forward.PackId, reverse.PackId);
        Assert.Equal(forward.ContentHash(), reverse.ContentHash());
        Assert.Equal(forward.PackNotice, reverse.PackNotice);
        Assert.Equal(Flatten(MixedBattle.RandomTeams(forward, 104)), Flatten(MixedBattle.RandomTeams(reverse, 104)));
        foreach (var streamer in forward.Streamers) Assert.Equal(forward.NoticeFor(streamer.Id), reverse.NoticeFor(streamer.Id));
        var error = Assert.Throws<KeyError>(() => forward.NoticeFor("missing_streamer"));
        AssertChinese(error, "missing_streamer");
    }

    [Fact]
    public void MixedViewShowsCombinedNoticeAndEachFightersOwnSourceNotice()
    {
        var original = BattleTests.LoadPack("original");
        var fan = BattleTests.LoadPack("fan_asoul_vr");
        var db = ContentDb.Merge(original, fan);
        var battle = new Battle(db, ManualTeams(original, fan), new[] { "甲方", "乙方" }, 104);

        var view = BattleViewModel.From(battle, 0);

        Assert.Equal(db.PackNotice, view.Notice);
        Assert.Contains(fan.PackNotice, view.Notice);
        foreach (var side in view.Sides)
            foreach (var fighter in side.Team) Assert.Equal(db.NoticeFor(fighter.Id), fighter.Notice);
    }

    [Theory]
    [InlineData("skills")]
    [InlineData("personas")]
    [InlineData("statuses")]
    [InlineData("fields")]
    public void EveryCustomDefinitionCategoryRejectsDuplicateIds(string category)
    {
        var first = Load(CustomPack("first", "a_", category));
        var second = Load(CustomPack("second", "b_", category));
        string firstHash = first.ContentHash();
        string secondHash = second.ContentHash();

        var error = Assert.Throws<KeyError>(() => ContentDb.Merge(first, second));

        AssertChinese(error, "shared_definition");
        Assert.Equal(firstHash, first.ContentHash());
        Assert.Equal(secondHash, second.ContentHash());
    }

    [Fact]
    public void DuplicateStreamerIdsAreRejectedEvenWhenDefinitionsMatch()
    {
        var first = CustomPack("first", "a_");
        var second = CustomPack("second", "b_");
        string duplicate = first["streamers"][0]["id"].GetValue<string>();
        second["streamers"][0]["id"] = duplicate;

        var error = Assert.Throws<KeyError>(() => ContentDb.Merge(Load(first), Load(second)));

        AssertChinese(error, duplicate);
    }

    [Fact]
    public void ConflictingExplicitAliasesAreRejectedInsteadOfDependingOnPackOrder()
    {
        var first = CustomPack("first", "a_");
        var second = CustomPack("second", "b_");
        first["skillNames"] = new JsonObject { ["warm_up"] = "甲方开嗓" };
        second["skillNames"] = new JsonObject { ["warm_up"] = "乙方开嗓" };
        var a = Load(first);
        var b = Load(second);

        AssertChinese(Assert.Throws<KeyError>(() => ContentDb.Merge(a, b)), "warm_up");
        AssertChinese(Assert.Throws<KeyError>(() => ContentDb.Merge(b, a)), "warm_up");
    }

    [Theory]
    [InlineData("source_rules")]
    [InlineData("current_skill")]
    [InlineData("current_chart")]
    public void DifferentSharedRulesAreRejected(string change)
    {
        var first = Load(CustomPack("first", "a_"));
        var rules = JsonNode.Parse(Resource("StreamDuelRules.json")).AsObject();
        if (change == "source_rules") rules["skills"]["warm_up"]["power"] = 999;
        var second = ContentDb.FromJson(rules.ToJsonString(), CustomPack("second", "b_").ToJsonString());
        if (change == "current_skill") second.Skills["warm_up"].Power++;
        if (change == "current_chart") second.Chart["game"]["song"] = 7;

        var error = Assert.Throws<KeyError>(() => ContentDb.Merge(first, second));

        AssertChinese(error);
        Assert.Contains("规则", error.Message);
    }

    [Theory]
    [InlineData("null_array")]
    [InlineData("empty_array")]
    [InlineData("null_element")]
    [InlineData("duplicate_pack")]
    [InlineData("empty_id")]
    [InlineData("nested_mix")]
    [InlineData("missing_source")]
    public void InvalidMergeInputsHaveChineseErrors(string input)
    {
        var original = BattleTests.LoadPack("original");
        Action merge;
        switch (input)
        {
            case "null_array": merge = () => ContentDb.Merge((ContentDb[])null); break;
            case "empty_array": merge = () => ContentDb.Merge(); break;
            case "null_element": merge = () => ContentDb.Merge(original, null); break;
            case "duplicate_pack": merge = () => ContentDb.Merge(original, BattleTests.LoadPack("original")); break;
            case "empty_id": original.PackId = ""; merge = () => ContentDb.Merge(original); break;
            case "nested_mix": merge = () => ContentDb.Merge(ContentDb.Merge(original)); break;
            default: merge = () => ContentDb.Merge(new ContentDb { PackId = "manual" }); break;
        }

        var error = Assert.Throws<KeyError>(merge);
        AssertChinese(error);
        if (input == "missing_source") Assert.Contains("来源", error.Message);
    }

    [Theory]
    [InlineData("nul")]
    [InlineData("newline")]
    [InlineData("carriage_return")]
    [InlineData("tab")]
    [InlineData("unit_separator")]
    [InlineData("delete")]
    [InlineData("next_line")]
    [InlineData("high_surrogate")]
    [InlineData("low_surrogate")]
    [InlineData("high_surrogate_ascii")]
    public void InvalidPackIdCharactersFailBeforeEncodingWithoutEchoingUnsafeText(string input)
    {
        string invalid;
        switch (input)
        {
            case "nul": invalid = "\0"; break;
            case "newline": invalid = "\n"; break;
            case "carriage_return": invalid = "\r"; break;
            case "tab": invalid = "\t"; break;
            case "unit_separator": invalid = "\u001f"; break;
            case "delete": invalid = "\u007f"; break;
            case "next_line": invalid = "\u0085"; break;
            case "high_surrogate": invalid = new string((char)0xd83d, 1); break;
            case "low_surrogate": invalid = new string((char)0xde00, 1); break;
            default: invalid = new string((char)0xd83d, 1) + "A"; break;
        }
        var db = BattleTests.LoadPack("original");
        db.PackId = "unsafe_" + invalid + "_pack";
        string originalId = db.PackId;

        var error = Assert.Throws<KeyError>(() => ContentDb.Merge(db));

        AssertChinese(error);
        Assert.DoesNotContain(originalId, error.Message);
        Assert.DoesNotContain(error.Message, c => char.IsControl(c) || char.IsSurrogate(c));
        Assert.Equal(originalId, db.PackId);
    }

    [Fact]
    public void ValidEmojiPackIdIsPreservedAndEncodedAsItsFullUnicodeScalar()
    {
        var db = BattleTests.LoadPack("original");
        db.PackId = "emoji_\ud83d\ude00";

        var merged = ContentDb.Merge(db);

        Assert.Equal("mix:emoji_%F0%9F%98%80", merged.PackId);
        Assert.Equal(db.PackId, Uri.UnescapeDataString(merged.PackId.Substring(4)));
        Assert.Equal(12, merged.Streamers.Count);
        Assert.All(merged.Streamers, streamer => Assert.Equal(db.PackNotice, merged.NoticeFor(streamer.Id)));
    }

    [Fact]
    public void SpecialPackIdsAreEscapedSeparatelySoDifferentCombinationsRemainDistinct()
    {
        var first = ContentDb.Merge(Load(CustomPack("a,b", "a_")), Load(CustomPack("c", "b_")));
        var reverse = ContentDb.Merge(Load(CustomPack("c", "b_")), Load(CustomPack("a,b", "a_")));
        var other = ContentDb.Merge(Load(CustomPack("a", "a_")), Load(CustomPack("b,c", "b_")));

        Assert.Equal("mix:a%2Cb,c", first.PackId);
        Assert.Equal("mix:a,b%2Cc", other.PackId);
        Assert.NotEqual(first.PackId, other.PackId);
        Assert.Equal(first.ContentHash(), reverse.ContentHash());
        Assert.NotEqual(first.ContentHash(), other.ContentHash());
    }

    [Fact]
    public void MergeOwnsDeepCopiesAndContentHashReflectsSubsequentEdits()
    {
        var a = Load(CustomPack("first", "a_"));
        var b = Load(CustomPack("second", "b_"));
        var merged = ContentDb.Merge(a, b);
        string mergedHash = merged.ContentHash();
        a.Streamers[0].Stats["hp"]++;
        a.Skills["a_skill"].Effects[2].Changes["spa"] = 4;
        a.Skills["a_skill"].Effects[2].Cond.Value = 0.75;
        a.Personas["a_persona"].Params["turns"] = 99;
        a.Fields["a_field"].Heal = 0.9;
        a.Statuses["a_status"].Turns = 99;
        a.PackNotice = "源包修改后的声明";
        Assert.Equal(mergedHash, merged.ContentHash());
        Assert.Equal("声明：first", merged.NoticeFor("a_bell"));
        string aHash = a.ContentHash();
        string bHash = b.ContentHash();

        merged.Streamer("a_bell").Stats["atk"]++;
        merged.Streamer("a_bell").Skills[0] = "noise_cancel";
        merged.Skills["a_skill"].Effects[2].Changes["spa"] = 5;
        merged.Skills["a_skill"].Effects[2].Cond.Value = 0.6;
        merged.Personas["b_persona"].Params["turns"] = 80;
        merged.Chart["game"]["song"] = 9;

        Assert.NotEqual(mergedHash, merged.ContentHash());
        Assert.Equal(aHash, a.ContentHash());
        Assert.Equal(bHash, b.ContentHash());
    }

    [Fact]
    public void PackNoticeAndUnusedCustomContentContributeToTheMixedFingerprint()
    {
        var a = Load(CustomPack("first", "a_"));
        var b = Load(CustomPack("second", "b_"));
        string before = ContentDb.Merge(a, b).ContentHash();
        b.PackNotice = "不同的声明";
        string differentNotice = ContentDb.Merge(a, b).ContentHash();
        Assert.NotEqual(before, differentNotice);
        b.Skills["b_spare"].Power++;
        Assert.NotEqual(differentNotice, ContentDb.Merge(a, b).ContentHash());
    }

    [Fact]
    public void RandomTeamsAreDeterministicDisjointAndIndependentOfRosterOrderAndBattleRng()
    {
        var db = Mixed();
        var first = MixedBattle.RandomTeams(db, 104);
        var same = MixedBattle.RandomTeams(db, 104);
        Assert.Equal(Flatten(first), Flatten(same));
        Assert.NotSame(first, same);
        Assert.NotSame(first[0], same[0]);
        Assert.NotSame(first[1], same[1]);
        var battle = new Battle(db, first, new[] { "甲方", "乙方" }, 19);
        var untouched = battle.Clone();
        ulong battleHash = battle.StateHash();
        string contentHash = db.ContentHash();
        string[] order = db.Streamers.Select(s => s.Id).ToArray();
        var samples = new HashSet<string>();
        foreach (uint seed in new uint[] { 0, 1, 2, 19, 104, uint.MaxValue })
        {
            string[][] teams = MixedBattle.RandomTeams(db, seed);
            Assert.Equal(2, teams.Length);
            Assert.All(teams, team => { Assert.Equal(6, team.Length); Assert.Empty(TeamCheck.Validate(db, null, team)); });
            Assert.Equal(12, Flatten(teams).Distinct(StringComparer.Ordinal).Count());
            samples.Add(string.Join(",", Flatten(teams)));
        }
        Assert.True(samples.Count > 1);
        Assert.Equal(order, db.Streamers.Select(s => s.Id));
        Assert.Equal(contentHash, db.ContentHash());
        Assert.Equal(battleHash, battle.StateHash());
        Assert.Equal(untouched.Random(), battle.Random());
        db.Streamers.Reverse();
        Assert.Equal(Flatten(first), Flatten(MixedBattle.RandomTeams(db, 104)));
        first[0][0] = "caller_mutation";
        Assert.DoesNotContain("caller_mutation", Flatten(MixedBattle.RandomTeams(db, 104)));
    }

    [Fact]
    public void OneOriginalPackProvidesExactlyTwelveUniqueRandomFighters()
    {
        var original = BattleTests.LoadPack("original");
        string before = original.ContentHash();
        var db = ContentDb.Merge(original);

        var teams = MixedBattle.RandomTeams(db, 104);

        Assert.Equal("mix:original", db.PackId);
        Assert.Equal(12, Flatten(teams).Distinct().Count());
        Assert.Equal(original.Streamers.Select(s => s.Id).OrderBy(id => id, StringComparer.Ordinal),
            Flatten(teams).OrderBy(id => id, StringComparer.Ordinal));
        Assert.Equal(before, original.ContentHash());
    }

    [Theory]
    [InlineData("too_few")]
    [InlineData("duplicate")]
    [InlineData("null_fighter")]
    [InlineData("blank_id")]
    public void RandomTeamsRejectInvalidRostersWithoutRepairingThem(string input)
    {
        var db = Mixed();
        if (input == "too_few") db.Streamers.RemoveRange(11, db.Streamers.Count - 11);
        if (input == "duplicate") db.Streamers.Add(db.Streamers[0]);
        if (input == "null_fighter") db.Streamers.Add(null);
        if (input == "blank_id") db.Streamers[0].Id = " ";
        var before = db.Streamers.ToArray();

        var error = Assert.Throws<KeyError>(() => MixedBattle.RandomTeams(db, 104));

        AssertChinese(error);
        Assert.Equal(before, db.Streamers);
        if (input == "too_few") Assert.Contains("12", error.Message);
        if (input == "duplicate") Assert.Contains(db.Streamers[0].Id, error.Message);
    }

    [Fact]
    public void RandomTeamsRejectNullDatabase()
    {
        Assert.Throws<ArgumentNullException>(() => MixedBattle.RandomTeams(null, 104));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MixedManualAndRandomMatchesFinishAndReplayWithReversedPackOrder(bool randomTeams)
    {
        var original = BattleTests.LoadPack("original");
        var fan = BattleTests.LoadPack("fan_asoul_vr");
        var db = ContentDb.Merge(original, fan);
        IList<string>[] teams = randomTeams ? MixedBattle.RandomTeams(db, 104) : ManualTeams(original, fan);
        var battle = new Battle(db, teams, new[] { "原创与同人甲", "原创与同人乙" },
            new BattleOptions { Seed = 104, BanOne = true, LeadPick = true });
        for (int side = 0; side < 2; side++) battle.ChooseBan(side, Ai.ChooseBan(battle, side));
        for (int side = 0; side < 2; side++) battle.ChooseLead(side, Ai.ChooseLead(battle, side));
        Finish(battle);
        string json = battle.Record.ToJson();

        var replay = Replay.Run(ContentDb.Merge(fan, original), BattleRecord.FromJson(json));

        Assert.Equal(Phase.Over, replay.Phase);
        Assert.Equal(battle.StateHash(), replay.StateHash());
        Assert.Equal(battle.Winner, replay.Winner);
        Assert.Equal(Log(battle), Log(replay));
        Assert.Equal(json, replay.Record.ToJson());
        for (int i = 0; i < 3; i++) Assert.Equal(battle.Random(), replay.Random());
        var changed = ContentDb.Merge(fan, original);
        changed.Skills["av_bella_centerbeat"].Power++;
        Assert.Contains("内容不一致", Assert.Throws<InvalidOperationException>(() =>
            Replay.Run(changed, BattleRecord.FromJson(json))).Message);
    }

    [Fact]
    public void LoopbackAcceptsTheSameMixedPacksInReverseOrderAndKeepsTurnsInSync()
    {
        var original = BattleTests.LoadPack("original");
        var fan = BattleTests.LoadPack("fan_asoul_vr");
        var hostDb = ContentDb.Merge(original, fan);
        var guestDb = ContentDb.Merge(fan, original);
        var teams = MixedBattle.RandomTeams(hostDb, 104);
        LoopbackPair.Create(out var a, out var b);
        var host = new NetSession(hostDb, a, true, teams[0], "主机", 104);
        var guest = new NetSession(guestDb, b, false, teams[1], "客机", null);
        Pump(host, guest);
        for (int turn = 0; turn < 3; turn++)
        {
            Assert.Equal(NetState.Choose, host.State);
            Assert.Equal(NetState.Choose, guest.State);
            host.Submit(BattleAction.Pass());
            guest.Submit(BattleAction.Pass());
            Pump(host, guest);
            Assert.Equal(host.Battle.StateHash(), guest.Battle.StateHash());
        }
        Assert.Equal(4, host.Battle.Turn);
        Assert.Equal(host.Battle.Record.ToJson(), guest.Battle.Record.ToJson());
        Assert.Null(host.Error);
        Assert.Null(guest.Error);
    }

    [Fact]
    public void LoopbackRejectsDifferentContentEvenWithTheSameMixedPackIds()
    {
        var original = BattleTests.LoadPack("original");
        var fan = BattleTests.LoadPack("fan_asoul_vr");
        var hostDb = ContentDb.Merge(original, fan);
        fan.Skills["av_bella_centerbeat"].Power++;
        var guestDb = ContentDb.Merge(fan, original);
        Assert.Equal(hostDb.PackId, guestDb.PackId);
        Assert.NotEqual(hostDb.ContentHash(), guestDb.ContentHash());
        var teams = MixedBattle.RandomTeams(hostDb, 104);
        LoopbackPair.Create(out var a, out var b);
        var host = new NetSession(hostDb, a, true, teams[0], "主机", 104);
        var guest = new NetSession(guestDb, b, false, teams[1], "客机", null);

        Pump(host, guest);

        Assert.Equal(NetState.Error, host.State);
        Assert.Equal(NetState.Error, guest.State);
        Assert.Contains("内容不一致", host.Error);
        Assert.Contains("内容不一致", guest.Error);
        Assert.Null(host.Battle);
        Assert.Null(guest.Battle);
    }

    [Fact]
    public void EquivalentIndependentPoliciesPreserveFilteringReasonsAndImmutabilityAfterMerge()
    {
        var firstPolicy = Policy(new[] { "unused_b", "unused_a" }, new[] { "unknown", "bell" },
            new Dictionary<string, string> { ["bell"] = "申请下架", ["unused_a"] = "暂时撤下" });
        var secondPolicy = Policy(new[] { "unused_a", "unused_b" }, new[] { "bell", "unknown" },
            new Dictionary<string, string> { ["unused_a"] = "暂时撤下", ["bell"] = "申请下架" });
        Assert.NotSame(firstPolicy, secondPolicy);
        var original = LoadWithPolicy("original", firstPolicy);
        var fan = LoadWithPolicy("fan_asoul_vr", secondPolicy);

        var merged = ContentDb.Merge(original, fan);
        var reverse = ContentDb.Merge(fan, original);

        Assert.Equal(11, original.Streamers.Count);
        Assert.Equal(7, fan.Streamers.Count);
        Assert.Equal(18, merged.Streamers.Count);
        Assert.DoesNotContain(merged.Streamers, streamer => streamer.Id == "bell");
        Assert.Equal(firstPolicy.DisabledPacks, merged.Policy.DisabledPacks);
        Assert.Equal(firstPolicy.DisabledStreamers, merged.Policy.DisabledStreamers);
        Assert.Equal("申请下架", merged.Policy.Reasons["bell"]);
        Assert.Equal(firstPolicy.StreamerReason("bell"), Assert.Throws<KeyError>(() => merged.NoticeFor("bell")).Message);
        Assert.Equal(merged.ContentHash(), reverse.ContentHash());
        var teams = MixedBattle.RandomTeams(merged, 104);
        Assert.Equal(12, Flatten(teams).Distinct(StringComparer.Ordinal).Count());
        Assert.DoesNotContain("bell", Flatten(teams));
        Assert.Equal(Flatten(teams), Flatten(MixedBattle.RandomTeams(reverse, 104)));
        Assert.Throws<NotSupportedException>(() => ((IList<string>)merged.Policy.DisabledPacks).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<string>)merged.Policy.DisabledStreamers).Clear());
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, string>)merged.Policy.Reasons)["bell"] = "已恢复");
        Assert.Equal("申请下架", merged.Policy.Reasons["bell"]);
    }

    [Theory]
    [InlineData("packs")]
    [InlineData("streamers")]
    [InlineData("reasons")]
    public void DifferentPolicySemanticsCannotBeCombinedByChangingInputOrder(string difference)
    {
        var firstPolicy = Policy(new[] { "unrelated_pack" }, new[] { "unrelated_streamer" },
            new Dictionary<string, string> { ["unrelated_pack"] = "原因甲" });
        var secondPolicy = Policy(new[] { difference == "packs" ? "other_pack" : "unrelated_pack" },
            new[] { difference == "streamers" ? "other_streamer" : "unrelated_streamer" },
            new Dictionary<string, string> { ["unrelated_pack"] = difference == "reasons" ? "原因乙" : "原因甲" });
        var original = LoadWithPolicy("original", firstPolicy);
        var fan = LoadWithPolicy("fan_asoul_vr", secondPolicy);

        Assert.Contains("下架策略不一致", Assert.Throws<KeyError>(() => ContentDb.Merge(original, fan)).Message);
        Assert.Contains("下架策略不一致", Assert.Throws<KeyError>(() => ContentDb.Merge(fan, original)).Message);
        Assert.Equal(12, original.Streamers.Count);
        Assert.Equal(7, fan.Streamers.Count);
    }

    [Fact]
    public void ExplicitEmptyPoliciesKeepExistingMixedHashAndRandomAssignments()
    {
        var expected = Mixed();
        var actual = ContentDb.Merge(LoadWithPolicy("original", Policy()), LoadWithPolicy("fan_asoul_vr", Policy()));

        Assert.Equal(expected.ContentHash(), actual.ContentHash());
        Assert.Equal(Flatten(MixedBattle.RandomTeams(expected, 104)), Flatten(MixedBattle.RandomTeams(actual, 104)));
        Assert.Empty(actual.Policy.DisabledPacks);
        Assert.Empty(actual.Policy.DisabledStreamers);
    }

    [Fact]
    public void ManuallyRestoringADisabledStreamerCannotBypassMergeRandomTeamsOrTeamValidation()
    {
        var policy = Policy(disabledStreamers: new[] { "bell" }, reasons: new Dictionary<string, string> { ["bell"] = "申请下架" });
        var original = LoadWithPolicy("original", policy);
        var fan = LoadWithPolicy("fan_asoul_vr", policy);
        var merged = ContentDb.Merge(original, fan);
        var restored = BattleTests.LoadPack("original").Streamer("bell");
        original.Streamers.Add(restored);
        merged.Streamers.Add(restored);
        string reason = policy.StreamerReason("bell");
        string before = merged.ContentHash();

        Assert.Equal(reason, Assert.Throws<PackDisabledException>(() => ContentDb.Merge(original, fan)).Message);
        Assert.Equal(reason, Assert.Throws<PackDisabledException>(() => MixedBattle.RandomTeams(merged, 104)).Message);
        Assert.Equal(reason, Assert.Throws<KeyError>(() => merged.NoticeFor("bell")).Message);
        var teams = ManualTeams(original, fan);
        teams[0][0] = "bell";
        var preset = new TeamPreset { PackId = merged.PackId, Ids = teams[0].ToList() };
        Assert.Contains(reason, preset.Check(merged, null));
        Assert.Equal(reason, Assert.Throws<KeyError>(() => new Battle(merged, teams, new[] { "甲", "乙" }, 104)).Message);
        Assert.Equal(before, merged.ContentHash());
        Assert.Equal(19, merged.Streamers.Count);
    }

    [Fact]
    public void DirectlyDisabledCombinationIsRejectedEvenWhenBothSourcePacksAreAvailable()
    {
        const string combinedId = "mix:fan_asoul_vr,original";
        var policy = Policy(new[] { combinedId }, reasons: new Dictionary<string, string> { [combinedId] = "组合暂停开放" });
        var original = LoadWithPolicy("original", policy);
        var fan = LoadWithPolicy("fan_asoul_vr", policy);

        Assert.Null(policy.PackReason(original.PackId));
        Assert.Null(policy.PackReason(fan.PackId));
        Assert.Equal(policy.PackReason(combinedId), Assert.Throws<PackDisabledException>(() => ContentDb.Merge(original, fan)).Message);
        Assert.Equal(12, original.Streamers.Count);
        Assert.Equal(7, fan.Streamers.Count);
    }

    [Fact]
    public void FilteredMixedContentRejectsOldTeamsAndBothReplayModesWithoutChangingTheRecord()
    {
        var original = BattleTests.LoadPack("original");
        var fan = BattleTests.LoadPack("fan_asoul_vr");
        var record = new Battle(ContentDb.Merge(original, fan), ManualTeams(original, fan), new[] { "甲", "乙" }, 104).Record;
        Assert.Contains("bell", record.Teams[0]);
        string saved = record.ToJson();
        var policy = Policy(disabledStreamers: new[] { "bell" });
        var filtered = ContentDb.Merge(LoadWithPolicy("original", policy), LoadWithPolicy("fan_asoul_vr", policy));
        string reason = policy.StreamerReason("bell");

        Assert.Equal(18, filtered.Streamers.Count);
        Assert.False(Replay.Check(filtered, record).CanReplay);
        Assert.Equal(reason, Replay.Check(filtered, record).Reason);
        Assert.Throws<InvalidOperationException>(() => Replay.Run(filtered, record));
        Assert.Equal(reason, Assert.Throws<InvalidOperationException>(() => Replay.RunBestEffort(filtered, record)).Message);
        Assert.Equal(reason, Assert.Throws<KeyError>(() => new Battle(filtered, record.Teams, record.Names, record.Seed)).Message);
        Assert.Contains(reason, new TeamPreset { PackId = filtered.PackId, Ids = record.Teams[0].ToList() }.Check(filtered, null));
        Assert.Equal(saved, record.ToJson());
    }

    [Theory]
    [InlineData("original")]
    [InlineData("fan_asoul_vr")]
    public void ExplicitSourcePackPolicyRejectsMixedReplayEvenWithoutLoadedContent(string disabledSource)
    {
        var db = Mixed();
        var record = MixedRecord(db);
        string saved = record.ToJson();
        var policy = Policy(new[] { disabledSource }, reasons: new Dictionary<string, string> { [disabledSource] = "来源包已撤下" });
        string reason = policy.PackReason(disabledSource);

        Assert.Equal(reason, policy.PackReason(db.PackId));
        foreach (var content in new[] { db, (ContentDb)null })
        {
            var result = Replay.Check(content, record, policy);
            Assert.False(result.CanReplay);
            Assert.Equal(reason, result.Reason);
        }
        Assert.Equal(saved, record.ToJson());
    }

    [Theory]
    [InlineData("a,b")]
    [InlineData("a%2Cb")]
    [InlineData("a:b")]
    public void SourcePackPolicyDecodesEachMixedIdExactlyOnce(string sourceId)
    {
        var db = ContentDb.Merge(Load(CustomPack(sourceId, "a_")), Load(CustomPack("z", "z_")));
        var record = MixedRecord(db);
        var policy = Policy(new[] { sourceId }, reasons: new Dictionary<string, string> { [sourceId] = "编码来源撤下" });

        Assert.Equal("mix:" + Uri.EscapeDataString(sourceId) + ",z", db.PackId);
        Assert.Equal(policy.PackReason(sourceId), policy.PackReason(db.PackId));
        Assert.False(Replay.Check(null, record, policy).CanReplay);
        Assert.Equal(policy.PackReason(sourceId), Replay.Check(null, record, policy).Reason);
        Assert.Null(Policy(new[] { "a" }).PackReason(db.PackId));
        if (sourceId == "a%2Cb") Assert.Null(Policy(new[] { "a,b" }).PackReason(db.PackId));
    }

    [Fact]
    public void ReplayLibraryMarksMixedRecordsUnavailableFromSourcePolicyWithoutRewritingThem()
    {
        using var directory = new PolicyReplayDirectory();
        var record = MixedRecord(Mixed());
        var initial = new ReplayLibrary(directory.Root, policy: PackPolicy.Empty);
        string id = initial.Save(record, new BattleSummary { Winner = -1, Turns = 0 }, "保留原组合录像");
        string path = Path.Combine(directory.Root, id + ".json");
        byte[] before = File.ReadAllBytes(path);
        var policy = Policy(new[] { "original" }, reasons: new Dictionary<string, string> { ["original"] = "来源包下架" });
        var restricted = new ReplayLibrary(directory.Root, policy: policy);

        var entry = Assert.Single(restricted.List());

        Assert.True(entry.IsUnavailable);
        Assert.False(entry.IsCorrupt);
        Assert.Equal(record.PackId, entry.PackId);
        Assert.Equal(policy.PackReason("original"), entry.UnavailableReason);
        Assert.Equal(entry.UnavailableReason, Assert.Throws<InvalidOperationException>(() => restricted.Load(id)).Message);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    static PackPolicy Policy(string[] disabledPacks = null, string[] disabledStreamers = null, Dictionary<string, string> reasons = null)
    {
        return PackPolicy.FromJson(JsonSerializer.Serialize(new
        {
            disabledPacks = disabledPacks ?? Array.Empty<string>(),
            disabledStreamers = disabledStreamers ?? Array.Empty<string>(),
            reason = reasons ?? new Dictionary<string, string>()
        }));
    }

    static ContentDb LoadWithPolicy(string pack, PackPolicy policy) => ContentDb.FromJson(Resource("StreamDuelRules.json"),
        Resource(Path.Combine("Packs", pack + ".json")), policy);
    static BattleRecord MixedRecord(ContentDb db) => new Battle(db, MixedBattle.RandomTeams(db, 104), new[] { "甲", "乙" }, 104).Record;

    sealed class PolicyReplayDirectory : IDisposable
    {
        readonly string parent;
        readonly string name = Guid.NewGuid().ToString("N");
        public string Root { get; }

        public PolicyReplayDirectory([CallerFilePath] string sourceFile = "")
        {
            string tests = Path.GetDirectoryName(Path.GetFullPath(sourceFile));
            if (Path.GetFileName(tests) != "Engine.Tests") throw new InvalidOperationException("测试目录必须位于 Engine.Tests。");
            parent = Path.Combine(tests, "obj", "T104-policy-replay");
            Root = Path.Combine(parent, name);
            Directory.CreateDirectory(Root);
        }

        public void Dispose()
        {
            string resolved = Path.GetFullPath(Root);
            if (!string.Equals(Path.GetDirectoryName(resolved), parent, StringComparison.Ordinal)
                || Path.GetFileName(resolved) != name || (File.GetAttributes(resolved) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("只能清理本轮创建的测试目录。");
            foreach (string file in Directory.GetFiles(resolved, "*", SearchOption.TopDirectoryOnly)) File.Delete(file);
            Directory.Delete(resolved);
        }
    }

    static ContentDb Mixed() => ContentDb.Merge(BattleTests.LoadPack("original"), BattleTests.LoadPack("fan_asoul_vr"));
    static string[] Flatten(string[][] teams) => teams.SelectMany(team => team).ToArray();
    static string Resource(string path) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Content", path));
    static ContentDb Load(JsonObject pack) => ContentDb.FromJson(Resource("StreamDuelRules.json"), pack.ToJsonString());
    static string Log(Battle battle) => JsonSerializer.Serialize(battle.Log, new JsonSerializerOptions { IncludeFields = true });

    static IList<string>[] ManualTeams(ContentDb original, ContentDb fan)
    {
        return new IList<string>[]
        {
            original.Streamers.Take(6).Select(s => s.Id).ToArray(), fan.Streamers.Take(6).Select(s => s.Id).ToArray()
        };
    }

    static void AssertChinese(KeyError error, string id = null)
    {
        Assert.Matches("[\\u4e00-\\u9fff]", error.Message);
        if (id != null) Assert.Contains(id, error.Message);
    }

    static void Finish(Battle battle)
    {
        for (int step = 0; step < 500 && battle.Phase != Phase.Over; step++)
        {
            if (battle.Phase == Phase.Choose) battle.ResolveTurn(Ai.Choose(battle, 0), Ai.Choose(battle, 1));
            else
            {
                Assert.Equal(Phase.Replace, battle.Phase);
                for (int side = 0; side < 2; side++)
                    if (battle.Need[side]) battle.Replace(side, Ai.ChooseReplacement(battle, side));
            }
        }
        Assert.Equal(Phase.Over, battle.Phase);
    }

    static void Pump(NetSession host, NetSession guest)
    {
        for (int i = 0; i < 8; i++) { host.Poll(); guest.Poll(); }
    }

    static JsonObject CustomPack(string packId, string prefix, string sharedCategory = null)
    {
        var pack = JsonNode.Parse(Resource(Path.Combine("Packs", "original.json"))).AsObject();
        pack["id"] = packId;
        pack["name"] = packId;
        pack["notice"] = "声明：" + packId;
        pack.Remove("seasons");
        foreach (var streamer in pack["streamers"].AsArray()) streamer["id"] = prefix + streamer["id"].GetValue<string>();
        string definitions = Definitions.Replace("t104_", prefix);
        if (sharedCategory != null)
        {
            string singular = sharedCategory == "statuses" ? "status" : sharedCategory.Substring(0, sharedCategory.Length - 1);
            definitions = definitions.Replace(prefix + singular, "shared_definition");
        }
        var parsed = JsonNode.Parse(definitions).AsObject();
        foreach (var definition in parsed) pack[definition.Key] = definition.Value.DeepClone();
        string skill = sharedCategory == "skills" ? "shared_definition" : prefix + "skill";
        string persona = sharedCategory == "personas" ? "shared_definition" : prefix + "persona";
        pack["streamers"][0]["fifth"] = skill;
        pack["streamers"][0]["persona"] = persona;
        return pack;
    }

    const string Definitions = @"{
      ""statuses"": { ""t104_status"": { ""name"": ""测试弱化"", ""cat"": ""weak"", ""turns"": 2, ""dot"": 0.05, ""cleanse"": true } },
      ""fields"": { ""t104_field"": { ""name"": ""测试效果"", ""kind"": ""turn"", ""dispel"": true, ""heal"": 0.05 } },
      ""personas"": { ""t104_persona"": { ""name"": ""测试人设"", ""code"": ""T_ENTER_FIELD"", ""node"": ""登场时"", ""field"": ""t104_field"", ""params"": { ""turns"": 2 } } },
      ""skills"": {
        ""t104_spare"": { ""name"": ""未被选用的测试招式"", ""type"": ""song"", ""cat"": ""talent"", ""power"": 30, ""pp"": 10 },
        ""t104_skill"": { ""name"": ""测试招式"", ""type"": ""song"", ""cat"": ""talent"", ""power"": 40, ""pp"": 10,
        ""effects"": [
          { ""at"": ""post"", ""code"": ""STATUS"", ""target"": ""foe"", ""status"": ""t104_status"" },
          { ""at"": ""post"", ""code"": ""FIELD"", ""target"": ""self"", ""field"": ""t104_field"", ""turns"": 2 },
          { ""at"": ""post"", ""code"": ""STAGE"", ""target"": ""self"", ""changes"": { ""spa"": 1 }, ""cond"": { ""kind"": ""foeHpAbove"", ""value"": 0 } }
        ] } }
    }";
}
