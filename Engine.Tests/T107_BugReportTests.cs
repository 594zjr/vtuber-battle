using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using StreamDuel.Core;
using Xunit;

public class T107_BugReportTests
{
    static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions { IncludeFields = true };

    [Theory]
    [InlineData(107u)]
    [InlineData(202u)]
    [InlineData(303u)]
    public void CompletedAiMatchReproducesStateLogsEventsAndNextRandomValues(uint seed)
    {
        var battle = Make(seed: seed, selections: true);
        FinishSelections(battle);
        for (int step = 0; step < 500 && battle.Phase != Phase.Over; step++) Step(battle);
        Assert.Equal(Phase.Over, battle.Phase);
        string record = battle.Record.ToJson();
        string logs = Json(battle.Log);
        string events = Json(battle.Events);
        ulong state = battle.StateHash();
        var untouched = battle.Clone();

        string code = BugReport.Create(battle, "这一局请协助检查");
        var report = BugReport.Parse(code);
        var result = BugReport.Reproduce(battle.Db, report);

        Assert.Matches("^SDBUG1-[A-Za-z0-9_-]+$", code);
        Assert.Equal(battle.Db.PackId, report.PackId);
        Assert.Equal(battle.Db.ContentHash(), report.ContentHash);
        Assert.Equal(battle.Record.Version, report.ReplayVersion);
        Assert.False(string.IsNullOrWhiteSpace(report.EngineVersion));
        Assert.Equal(state, report.StateHash);
        Assert.Matches("^[0-9a-f]{16}$", report.LogHash);
        Assert.Equal("这一局请协助检查", report.Note);
        Assert.Equal(record, report.Record.ToJson());
        AssertSuccessful(battle, result);
        Assert.Empty(result.Warnings);
        Assert.Equal(record, battle.Record.ToJson());
        Assert.Equal(logs, Json(battle.Log));
        Assert.Equal(events, Json(battle.Events));
        Assert.Equal(state, battle.StateHash());
        for (int i = 0; i < 4; i++)
        {
            double next = untouched.Random();
            Assert.Equal(next, battle.Random());
            Assert.Equal(next, result.Battle.Random());
        }
    }

    [Theory]
    [InlineData("ban")]
    [InlineData("lead")]
    [InlineData("choose")]
    [InlineData("replace")]
    public void IntermediatePhasesAndPendingSingleSideSelectionsAreReproduced(string phase)
    {
        var battle = Make(selections: phase == "ban" || phase == "lead");
        if (phase == "ban") battle.ChooseBan(1, 3);
        else if (phase == "lead")
        {
            battle.ChooseBan(1, 3);
            battle.ChooseBan(0, 4);
            battle.ChooseLead(1, 2);
        }
        else if (phase == "choose")
        {
            battle.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        }
        else
        {
            for (int step = 0; step < 400 && battle.Phase == Phase.Choose; step++) Step(battle);
            Assert.Equal(Phase.Replace, battle.Phase);
            Assert.Contains(true, battle.Need);
        }
        Assert.NotEqual(Phase.Over, battle.Phase);
        var result = BugReport.Reproduce(battle.Db, BugReport.Parse(BugReport.Create(battle, "中途")));

        AssertSuccessful(battle, result);
        Assert.Equal(battle.Need, result.Battle.Need);
        for (int side = 0; side < 2; side++)
        {
            Assert.Equal(battle.HasChosenBan(side), result.Battle.HasChosenBan(side));
            Assert.Equal(battle.HasChosenLead(side), result.Battle.HasChosenLead(side));
        }
    }

    [Fact]
    public void UnsettledAiSamplingIsRestoredWithoutInventingAReplayEvent()
    {
        var battle = Make();
        battle.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        string recorded = battle.Record.ToJson();
        Ai.Choose(battle, 0);
        Ai.Choose(battle, 1);
        for (int i = 0; i < 3; i++) battle.Random();
        Assert.Equal(recorded, battle.Record.ToJson());
        Assert.NotEqual(battle.StateHash(), Replay.Run(battle.Db, battle.Record).StateHash());
        var untouched = battle.Clone();

        var report = BugReport.Parse(BugReport.Create(battle, "选招后尚未提交"));
        var result = BugReport.Reproduce(battle.Db, report);

        AssertSuccessful(battle, result);
        Assert.Equal(recorded, report.Record.ToJson());
        Assert.Equal(recorded, battle.Record.ToJson());
        for (int i = 0; i < 4; i++)
        {
            double expected = untouched.Random();
            Assert.Equal(expected, battle.Random());
            Assert.Equal(expected, result.Battle.Random());
        }
    }

    [Fact]
    public void FullOneHundredTwentyTurnRecordStaysBelowFourThousandCharactersWithoutDroppingEvents()
    {
        var battle = Make();
        for (int turn = 0; turn < 120; turn++)
        {
            Assert.Equal(Phase.Choose, battle.Phase);
            battle.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        }
        Assert.Equal(Phase.Over, battle.Phase);
        Assert.Equal(120, battle.Record.Events.Count(entry => entry.Type == BattleRecordEventType.Turn));

        string code = BugReport.Create(battle, "完整的一百二十回合");
        var report = BugReport.Parse(code);

        Assert.True(code.Length < 4000, "报告码长度为 " + code.Length);
        Assert.Equal(battle.Record.ToJson(), report.Record.ToJson());
        AssertSuccessful(battle, BugReport.Reproduce(battle.Db, report));
    }

    [Fact]
    public void OnlyTheLastThirtyLogEntriesContributeToTheLogHash()
    {
        var battle = Make();
        for (int i = 0; i < 12; i++) battle.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        Assert.True(battle.Log.Count > 30);
        string expected = BugReport.Parse(BugReport.Create(battle, "原始")).LogHash;
        var outside = battle.Clone();
        outside.Log[outside.Log.Count - 31].Text += "窗外变化";
        var inside = battle.Clone();
        inside.Log[inside.Log.Count - 30].Text += "窗内变化";

        Assert.Equal(expected, BugReport.Parse(BugReport.Create(outside, "不同备注")).LogHash);
        Assert.NotEqual(expected, BugReport.Parse(BugReport.Create(inside, "原始")).LogHash);
    }

    [Theory]
    [InlineData("turn")]
    [InlineData("side")]
    [InlineData("node")]
    [InlineData("kind")]
    [InlineData("text")]
    [InlineData("active")]
    [InlineData("hp")]
    public void LogHashCoversEachObservableLogFieldAndMismatchDoesNotPretendStateChanged(string field)
    {
        var battle = Make();
        battle.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        var original = BugReport.Parse(BugReport.Create(battle, "原始"));
        var changed = battle.Clone();
        var entry = changed.Log[changed.Log.Count - 1];
        switch (field)
        {
            case "turn": entry.Turn++; break;
            case "side": entry.Side = entry.Side == 0 ? 1 : 0; break;
            case "node": entry.Node += "变化"; break;
            case "kind": entry.Kind += "变化"; break;
            case "text": entry.Text += "变化"; break;
            case "active": entry.SnapActive[0] = (entry.SnapActive[0] + 1) % 6; break;
            case "hp": entry.SnapHp[0][0]--; break;
        }
        var report = BugReport.Parse(BugReport.Create(changed, "原始"));

        Assert.Equal(original.StateHash, report.StateHash);
        Assert.NotEqual(original.LogHash, report.LogHash);
        var result = BugReport.Reproduce(battle.Db, report);
        Assert.NotNull(result.Battle);
        Assert.True(result.StateMatches);
        Assert.False(result.LogMatches);
        Assert.False(result.Success);
        Assert.Contains(result.Warnings, warning => warning.Contains("日志"));
    }

    [Fact]
    public void ContentMismatchWarnsAndReplaysCurrentRulesInsteadOfChangingTheSourceFingerprint()
    {
        var battle = Make();
        battle.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        var report = BugReport.Parse(BugReport.Create(battle, "内容发生变化"));
        string originalRecord = report.Record.ToJson();
        var changed = BattleTests.LoadPack("original");
        changed.Streamer("enka").Stats["hp"] += 20;

        var result = BugReport.Reproduce(changed, report);

        Assert.NotNull(result.Battle);
        Assert.False(result.StateMatches);
        Assert.False(result.Success);
        Assert.Contains(result.Warnings, warning => warning.Contains("内容") && warning.Contains("不同"));
        Assert.Contains(result.Warnings, warning => warning.Contains("状态"));
        Assert.Equal(battle.Db.ContentHash(), report.ContentHash);
        Assert.Equal(originalRecord, report.Record.ToJson());
        Assert.Equal(changed.Streamer("enka").Stats["hp"], result.Battle.Sides[0].Team[0].MaxHp);
    }

    [Fact]
    public void DifferentEngineVersionWarnsButStillAllowsACompatibleReplay()
    {
        var battle = Make();
        var payload = Decode(BugReport.Create(battle, "版本检查"));
        payload["engineVersion"] = "historical-test-engine";
        var report = BugReport.Parse(Encode(payload.ToJsonString()));

        var result = BugReport.Reproduce(battle.Db, report);

        AssertSuccessful(battle, result);
        Assert.Contains(result.Warnings, warning => warning.Contains("引擎") && warning.Contains("版本"));
        Assert.Equal("historical-test-engine", report.EngineVersion);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void SupportedOlderReplayVersionsRemainReproducibleWithoutAutomaticUpgrade(int version)
    {
        var battle = Make();
        battle.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        battle.Record.Version = version;

        var report = BugReport.Parse(BugReport.Create(battle, "旧录像"));

        Assert.Equal(version, report.ReplayVersion);
        Assert.Equal(version, report.Record.Version);
        AssertSuccessful(battle, BugReport.Reproduce(battle.Db, report));
        Assert.Equal(version, report.Record.Version);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(int.MaxValue)]
    public void ParseMayPreserveFutureReplayVersionsButReproduceCannotExecuteThem(int version)
    {
        var battle = Make();
        var payload = Decode(BugReport.Create(battle, "未来版本"));
        var record = JsonNode.Parse(payload["record"].GetValue<string>()).AsObject();
        record["version"] = version;
        payload["record"] = record.ToJsonString();
        payload["replayVersion"] = version;
        var report = BugReport.Parse(Encode(payload.ToJsonString()));
        Assert.Equal(version, report.Record.Version);

        var result = BugReport.Reproduce(battle.Db, report);

        AssertRejected(result, "录像来自更新的版本");
        Assert.Equal(version, report.ReplayVersion);
    }

    [Fact]
    public void SurrenderUsesCurrentTerminationRecordVersionAndReproducesTheActualWinner()
    {
        var battle = Make();
        battle.Surrender(0);
        Assert.Equal(Phase.Over, battle.Phase);
        Assert.Equal(1, battle.Winner);
        var report = BugReport.Parse(BugReport.Create(battle, "投降后"));

        Assert.Equal(BattleRecord.TerminationVersion, report.ReplayVersion);
        AssertSuccessful(battle, BugReport.Reproduce(battle.Db, report));
    }

    [Fact]
    public void DisabledOriginalTeamMemberBlocksReproductionBeforeFingerprintWarnings()
    {
        var battle = Make();
        var report = BugReport.Parse(BugReport.Create(battle, "已下架"));
        var policy = PackPolicy.FromJson("{\"disabledStreamers\":[\"bell\"],\"reason\":{\"bell\":\"申请下架\"}}");
        string content = Path.Combine(AppContext.BaseDirectory, "Content");
        var filtered = ContentDb.FromJson(File.ReadAllText(Path.Combine(content, "StreamDuelRules.json")),
            File.ReadAllText(Path.Combine(content, "Packs", "original.json")), policy);

        var result = BugReport.Reproduce(filtered, report);

        AssertRejected(result, "该主播已下架");
        Assert.Contains("申请下架", result.Reason);
        Assert.Equal(11, filtered.Streamers.Count);
        Assert.Contains("bell", report.Record.Teams[0]);
    }

    [Fact]
    public void DifferentPackCannotBeReplayedByIgnoringItsIdentity()
    {
        var report = BugReport.Parse(BugReport.Create(Make(), "包不匹配"));

        AssertRejected(BugReport.Reproduce(BattleTests.LoadPack("fan_holo_niji"), report), "内容包");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \r\n\t\u3000")]
    [InlineData("SD1-AAAA")]
    [InlineData("sdbug1-AAAA")]
    [InlineData("SDBUG1-")]
    [InlineData("SDBUG1-A")]
    [InlineData("SDBUG1-AAAA=")]
    [InlineData("SDBUG1-AAA+")]
    [InlineData("SDBUG1-AAA\u200b")]
    public void InvalidCodesAlwaysFailWithChineseFormatErrors(string code)
    {
        AssertChinese(Assert.Throws<FormatException>(() => BugReport.Parse(code)));
    }

    [Fact]
    public void WhitespaceCanBeInsertedAnywhereWithoutChangingTheReport()
    {
        string code = BugReport.Create(Make(), "空白容忍");
        var expected = BugReport.Parse(code);
        string spaced = "\u3000" + string.Join(" \r\n\t\u00a0", code.Select(character => character.ToString())) + "\u2003";

        var actual = BugReport.Parse(spaced);

        Assert.Equal(expected.Record.ToJson(), actual.Record.ToJson());
        Assert.Equal(expected.StateHash, actual.StateHash);
        Assert.Equal(expected.LogHash, actual.LogHash);
        Assert.Equal(expected.Note, actual.Note);
    }

    [Fact]
    public void ChangingASinglePayloadCharacterFailsChecksumValidation()
    {
        string code = BugReport.Create(Make(), "复制损坏");
        int index = "SDBUG1-".Length + 10;
        string tampered = code.Substring(0, index) + (code[index] == 'A' ? "B" : "A") + code.Substring(index + 1);

        var error = Assert.Throws<FormatException>(() => BugReport.Parse(tampered));

        AssertChinese(error);
        Assert.Contains("校验", error.Message);
    }

    [Fact]
    public void DecompressionAndEncodedInputLimitsRejectOversizedReports()
    {
        string bomb = Encode(new string(' ', 1024 * 1024 + 1));
        Assert.True(bomb.Length < 4000);
        var decompressionError = Assert.Throws<FormatException>(() => BugReport.Parse(bomb));
        AssertChinese(decompressionError);
        Assert.Contains("1 MiB", decompressionError.Message);
        AssertChinese(Assert.Throws<FormatException>(() => BugReport.Parse("SDBUG1-" + new string('A', 1500000))));
        var battle = Make();
        battle.Record.Names[0] = new string('长', 400000);
        AssertChinese(Assert.Throws<FormatException>(() => BugReport.Create(battle, "过大原始录像")));
    }

    [Theory]
    [InlineData("trailing")]
    [InlineData("truncated")]
    [InlineData("invalid_utf8")]
    public void ValidChecksumsDoNotBypassDeflateOrStrictUtf8Validation(string input)
    {
        byte[] compressed;
        if (input == "invalid_utf8") compressed = Compress(new byte[] { 0xc3, 0x28 });
        else
        {
            compressed = CompressedBytes(BugReport.Create(Make(), "压缩数据"));
            compressed = input == "trailing" ? compressed.Concat(new byte[] { 0x42 }).ToArray()
                : compressed.Take(compressed.Length - 1).ToArray();
        }

        AssertChinese(Assert.Throws<FormatException>(() => BugReport.Parse(Wrap(compressed))));
    }

    [Theory]
    [InlineData("[")]
    [InlineData("{")]
    [InlineData("{\"record\":\"unterminated}")]
    public void MalformedJsonWithAValidTransportChecksumStillProducesAChineseFormatError(string json)
    {
        AssertChinese(Assert.Throws<FormatException>(() => BugReport.Parse(Encode(json))));
    }

    [Theory]
    [InlineData("record_type")]
    [InlineData("pack_mismatch")]
    [InlineData("version_mismatch")]
    [InlineData("fractional_rng")]
    [InlineData("overflow_rng")]
    [InlineData("invalid_state_hash")]
    [InlineData("missing_log_hash")]
    public void InvalidReportFieldsCannotBeAcceptedByRecalculatingTheTransportChecksum(string input)
    {
        var payload = Decode(BugReport.Create(Make(), "字段检查"));
        switch (input)
        {
            case "record_type": payload["record"] = new JsonObject(); break;
            case "pack_mismatch": payload["packId"] = "other_pack"; break;
            case "version_mismatch": payload["replayVersion"] = 1; break;
            case "fractional_rng": payload["rngState"] = 1.5; break;
            case "overflow_rng": payload["rngState"] = (long)uint.MaxValue + 1; break;
            case "invalid_state_hash": payload["stateHash"] = "not-a-hex-hash!!!"; break;
            case "missing_log_hash": payload.Remove("logHash"); break;
        }

        AssertChinese(Assert.Throws<FormatException>(() => BugReport.Parse(Encode(payload.ToJsonString()))));
    }

    [Fact]
    public void NotesUseTheExistingTextFilterAndUnicodeLengthLimit()
    {
        string banned = TextLint.DefaultBannedWords.First();
        string raw = "  甲\u200b\n" + banned + "  " + string.Concat(Enumerable.Repeat("🦊", 70));
        var report = BugReport.Parse(BugReport.Create(Make(), raw));

        Assert.Equal(TextInput.Clean(raw, TextInputKind.Note).Text, report.Note);
        Assert.DoesNotContain(banned, report.Note);
        Assert.DoesNotContain('\n', report.Note);
        Assert.DoesNotContain('\u200b', report.Note);
        Assert.Equal("无备注", BugReport.Parse(BugReport.Create(Make(), null)).Note);
    }

    [Fact]
    public void PayloadContainsOnlyBattleDiagnosticFieldsAndParsedRecordIsAnIndependentSnapshot()
    {
        var battle = Make();
        string original = battle.Record.ToJson();
        string code = BugReport.Create(battle, "公开诊断");
        Assert.Equal(code, BugReport.Create(battle, "公开诊断"));
        var payload = Decode(code);
        var allowed = new[] { "record", "packId", "contentHash", "engineVersion", "replayVersion", "stateHash", "logHash", "note", "rngState" };
        Assert.Equal(allowed.OrderBy(key => key, StringComparer.Ordinal), payload.Select(pair => pair.Key).OrderBy(key => key, StringComparer.Ordinal));
        Assert.Equal(original, payload["record"].GetValue<string>());
        var report = BugReport.Parse(code);
        var exposed = report.Record;
        exposed.Names[0] = "调用者修改";
        exposed.Teams[0].Clear();
        exposed.Events.Clear();
        battle.Record.Names[0] = "战斗随后修改";

        Assert.Equal(original, report.Record.ToJson());
        Assert.Equal(original, BugReport.Parse(code).Record.ToJson());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HotseatReportKeepsPendingActionPrivateAndDoesNotChangeTheHandover(bool handedOver)
    {
        var db = BattleTests.LoadPack("original");
        var ids = db.Streamers.Select(streamer => streamer.Id).ToArray();
        var session = new HotseatSession(db, ids.Take(6).ToArray(), ids.Skip(6).Take(6).ToArray(),
            "同屏甲", "同屏乙", new BattleOptions { Seed = 107 });
        session.Submit(BattleAction.Pass());
        session.ConfirmHandover();
        session.Submit(BattleAction.Pass());
        Assert.Single(session.Record.Events);
        int firstPlayer = session.WhoseTurn;
        string settledCode = session.CreateBugReport("交接诊断");
        session.Submit(BattleAction.Skill(0));
        Assert.True(session.NeedsHandover);
        if (handedOver) session.ConfirmHandover();
        string record = session.Record.ToJson();
        string log = Json(session.Log);
        int whoseTurn = session.WhoseTurn;
        bool needsHandover = session.NeedsHandover;

        string code = session.CreateBugReport("交接诊断");
        var report = BugReport.Parse(code);
        var result = BugReport.Reproduce(db, report);

        Assert.Equal(settledCode, code);
        Assert.Equal(record, report.Record.ToJson());
        Assert.Single(report.Record.Events);
        AssertSuccessful(Replay.Run(db, report.Record), result);
        Assert.Equal(whoseTurn, session.WhoseTurn);
        Assert.Equal(needsHandover, session.NeedsHandover);
        Assert.Equal(record, session.Record.ToJson());
        Assert.Equal(log, Json(session.Log));
        if (session.NeedsHandover) session.ConfirmHandover();
        session.Submit(BattleAction.Pass());
        Assert.Equal(2, session.Record.Events.Count);
        var nextTurn = session.Record.Events[1];
        var retained = firstPlayer == 0 ? nextTurn.A0 : nextTurn.A1;
        Assert.Equal(ActionType.Skill, retained.Type);
        Assert.Equal(0, retained.Index);
    }

    static Battle Make(uint seed = 107, bool selections = false)
    {
        var db = BattleTests.LoadPack("original");
        var ids = db.Streamers.Select(streamer => streamer.Id).ToArray();
        return new Battle(db, new IList<string>[] { ids.Take(6).ToArray(), ids.Skip(6).Take(6).ToArray() },
            new[] { "报告甲", "报告乙" }, new BattleOptions { Seed = seed, BanOne = selections, LeadPick = selections });
    }

    static void FinishSelections(Battle battle)
    {
        if (battle.Phase == Phase.Ban)
            for (int side = 0; side < 2; side++) battle.ChooseBan(side, Ai.ChooseBan(battle, side));
        if (battle.Phase == Phase.Lead)
            for (int side = 0; side < 2; side++) battle.ChooseLead(side, Ai.ChooseLead(battle, side));
    }

    static void Step(Battle battle)
    {
        if (battle.Phase == Phase.Choose) battle.ResolveTurn(Ai.Choose(battle, 0), Ai.Choose(battle, 1));
        else if (battle.Phase == Phase.Replace)
            for (int side = 0; side < 2; side++)
                if (battle.Need[side]) battle.Replace(side, Ai.ChooseReplacement(battle, side));
    }

    static void AssertSuccessful(Battle expected, BugReportResult actual)
    {
        Assert.NotNull(actual.Battle);
        Assert.True(actual.StateMatches, actual.Reason);
        Assert.True(actual.LogMatches, actual.Reason);
        Assert.True(actual.Success, actual.Reason);
        Assert.Equal(expected.StateHash(), actual.Battle.StateHash());
        Assert.Equal(expected.Phase, actual.Battle.Phase);
        Assert.Equal(expected.Winner, actual.Battle.Winner);
        Assert.Equal(Json(expected.Log), Json(actual.Battle.Log));
        Assert.Equal(Json(expected.Events), Json(actual.Battle.Events));
    }

    static void AssertRejected(BugReportResult result, string reason)
    {
        Assert.Null(result.Battle);
        Assert.False(result.StateMatches);
        Assert.False(result.LogMatches);
        Assert.False(result.Success);
        Assert.Contains(reason, result.Reason);
    }

    static void AssertChinese(Exception error) => Assert.Matches("[\u3400-\u9fff]", error.Message);
    static string Json<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);

    static JsonObject Decode(string code)
    {
        using var input = new MemoryStream(CompressedBytes(code));
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        using var reader = new StreamReader(deflate, new UTF8Encoding(false, true));
        return JsonNode.Parse(reader.ReadToEnd()).AsObject();
    }

    static byte[] CompressedBytes(string code)
    {
        string payload = code.Substring("SDBUG1-".Length).Replace('-', '+').Replace('_', '/');
        byte[] bytes = Convert.FromBase64String(payload + new string('=', (4 - payload.Length % 4) % 4));
        return bytes.Take(bytes.Length - 4).ToArray();
    }

    static string Encode(string json) => Wrap(Compress(Encoding.UTF8.GetBytes(json)));

    static byte[] Compress(byte[] bytes)
    {
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, true)) deflate.Write(bytes, 0, bytes.Length);
        return output.ToArray();
    }

    static string Wrap(byte[] compressed)
    {
        uint checksum = 2166136261;
        foreach (byte value in compressed) checksum = unchecked((checksum ^ value) * 16777619);
        using var output = new MemoryStream();
        output.Write(compressed, 0, compressed.Length);
        for (int i = 0; i < 4; i++) output.WriteByte((byte)(checksum >> (8 * i)));
        return "SDBUG1-" + Convert.ToBase64String(output.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
