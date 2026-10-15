using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using StreamDuel.Core;
using Xunit;

public class T56_TeamPresetEdgeTests
{
    static TeamPreset Ordinary(string pack = "original")
    {
        var db = BattleTests.LoadPack(pack);
        return new TeamPreset
        {
            Name = "常用队伍",
            PackId = db.PackId,
            Ids = db.Streamers.Take(6).Select(s => s.Id).ToList()
        };
    }

    static TeamPreset Unusual()
    {
        return new TeamPreset
        {
            Name = "中文队伍 🦊\n\"引号\"\\斜杠\t\u0000",
            PackId = "自制包/中文 🦊",
            Ids = new List<string> { "主播 一", "双引号\"", "反斜杠\\", "换行\n制表\t", "emoji🦊", "空字符\u0000" }
        };
    }

    static void EqualPreset(TeamPreset expected, TeamPreset actual)
    {
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(expected.PackId, actual.PackId);
        Assert.Equal(expected.Ids, actual.Ids);
    }

    static IEnumerable<string> JsonStrings(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String) yield return element.GetString();
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
                foreach (string value in JsonStrings(child)) yield return value;
        }
        else if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
                foreach (string value in JsonStrings(property.Value)) yield return value;
        }
    }

    [Fact]
    public void PresetJsonPreservesUnicodeEscapingOrderAndRepeatedNames()
    {
        var first = Unusual();
        var second = Ordinary();
        second.Name = first.Name;
        var source = new TeamPresetList { Items = new List<TeamPreset> { first, second } };
        string json = source.ToJson();

        // 独立解析器检查转义，避免写入和读取共享错误而掩盖坏 JSON。
        using var document = JsonDocument.Parse(json);
        var strings = JsonStrings(document.RootElement).ToArray();
        foreach (string value in new[] { first.Name, first.PackId }.Concat(first.Ids)) Assert.Contains(value, strings);
        var restored = TeamPresetList.FromJson(json);
        Assert.Equal(2, restored.Items.Count);
        EqualPreset(first, restored.Items[0]);
        EqualPreset(second, restored.Items[1]);
        restored.Items[0].Ids[0] = "独立副本";
        Assert.Equal("主播 一", first.Ids[0]);
        Assert.Equal(json, source.ToJson());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \r\n\t ")]
    [InlineData("坏 JSON")]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("42")]
    [InlineData("\"不是预设列表\"")]
    [InlineData("[null]")]
    [InlineData("{")]
    public void InvalidPresetInputReturnsAnEmptyList(string json)
    {
        var restored = TeamPresetList.FromJson(json);
        Assert.NotNull(restored);
        Assert.NotNull(restored.Items);
        Assert.Empty(restored.Items);
    }

    [Fact]
    public void DamagedJsonNeverReturnsOnlyTheValidPrefixOfThePresetList()
    {
        var list = new TeamPresetList { Items = new List<TeamPreset> { Ordinary(), Unusual() } };
        string json = list.ToJson().TrimEnd();
        var damaged = new[]
        {
            json.Substring(0, json.Length - 1),
            json + " trailing",
            json + "{}",
            json.Insert(json.LastIndexOf('"'), "\\q"),
            json.Substring(0, json.Length - 1) + "," + json[json.Length - 1]
        };

        foreach (string input in damaged) Assert.Empty(TeamPresetList.FromJson(input).Items);
        Assert.Equal(2, TeamPresetList.FromJson(json).Items.Count);
        var empty = TeamPresetList.FromJson("坏");
        empty.Items.Add(Ordinary());
        Assert.Empty(TeamPresetList.FromJson("坏").Items);
    }

    [Fact]
    public void WrongPackAddsItsErrorWithoutHidingRosterErrorsOrChangingThePreset()
    {
        var db = BattleTests.LoadPack("original");
        var preset = new TeamPreset
        {
            Name = "仍需修正",
            PackId = "other_pack",
            Ids = new List<string> { "enka", "enka", "shiori", "missing_streamer", "lumi" }
        };
        var before = preset.Ids.ToArray();
        var rosterErrors = TeamCheck.Validate(db, null, preset.Ids);

        var errors = preset.Check(db, null);

        Assert.Equal(new[] { "这套队伍属于其他内容包" }.Concat(rosterErrors), errors);
        Assert.Contains(errors, e => e.Contains("6"));
        Assert.Contains(errors, e => e.Contains(db.Streamer("enka").Name) && e.Contains("重复"));
        Assert.Contains(errors, e => e.Contains("missing_streamer"));
        Assert.Equal(before, preset.Ids);
        Assert.Equal("other_pack", preset.PackId);
        Assert.Equal("仍需修正", preset.Name);
    }

    [Fact]
    public void PresetCheckUsesTheSelectedSeasonAndAllowsNullSeason()
    {
        var db = BattleTests.LoadPack("original");
        var preset = new TeamPreset
        {
            Name = "赛季队伍",
            PackId = db.PackId,
            Ids = new List<string> { "kei", "bell", "bee", "enka", "shiori", "lumi" }
        };

        Assert.Empty(preset.Check(db, null));
        var seasonal = preset.Check(db, "s1");
        Assert.Equal(TeamCheck.Validate(db, "s1", preset.Ids), seasonal);
        Assert.Contains(seasonal, e => e.Contains(db.Streamer("kei").Name) && e.Contains("禁用"));
        Assert.Contains(seasonal, e => e.Contains("限制池"));
        Assert.Contains("missing-season", Assert.Single(preset.Check(db, "missing-season")));
        preset.Ids = new List<string> { "enka", "shiori", "lumi", "bell", "hane", "momo" };
        Assert.Empty(preset.Check(db, "s1"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(7)]
    public void CodePreservesStructurallyValidDataWhileCheckOwnsTeamLegality(int count)
    {
        var db = BattleTests.LoadPack("original");
        var preset = new TeamPreset
        {
            Name = "尚未完成",
            PackId = db.PackId,
            Ids = Enumerable.Repeat("unknown_id", count).ToList()
        };

        var decoded = TeamCode.Decode(TeamCode.Encode(preset));

        EqualPreset(preset, decoded);
        Assert.NotEmpty(decoded.Check(db, null));
        var list = new TeamPresetList { Items = new List<TeamPreset> { preset } };
        EqualPreset(preset, Assert.Single(TeamPresetList.FromJson(list.ToJson()).Items));
    }

    [Fact]
    public void CodeRoundTripsUnicodeAndIgnoresWhitespaceThroughoutTheCode()
    {
        var preset = Unusual();
        string code = TeamCode.Encode(preset);
        string wrapped = "\u3000" + string.Join(" \t\r\n\u2003\u00a0", code.ToCharArray()) + "\u3000";

        EqualPreset(preset, TeamCode.Decode(wrapped));
        EqualPreset(preset, TeamCode.Decode(code));
        Assert.Equal(code, TeamCode.Encode(TeamCode.Decode(wrapped)));
        var decoded = TeamCode.Decode(code);
        decoded.Ids.Reverse();
        EqualPreset(preset, TeamCode.Decode(code));
    }

    [Fact]
    public void EncodingIsDeterministicAcrossCultures()
    {
        var preset = Unusual();
        string expected = TeamCode.Encode(preset);
        var previous = CultureInfo.CurrentCulture;
        try
        {
            foreach (string name in new[] { "tr-TR", "ar-SA" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                Assert.Equal(expected, TeamCode.Encode(preset));
                EqualPreset(preset, TeamCode.Decode(expected));
            }
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Theory]
    [InlineData("original")]
    [InlineData("original_wave2")]
    [InlineData("fan_asoul_vr")]
    [InlineData("fan_holo_niji")]
    public void OrdinaryTeamsUseShortUrlSafeCodesWithoutPadding(string pack)
    {
        var preset = Ordinary(pack);
        string code = TeamCode.Encode(preset);

        Assert.True(code.Length < 200, "普通队伍的码过长：" + pack + "，" + code.Length);
        Assert.Matches("^SDT1-[A-Za-z0-9_-]+$", code);
        Assert.DoesNotContain("=", code);
        EqualPreset(preset, TeamCode.Decode(code));
    }

    [Fact]
    public void EveryTruncatedCodeIsRejectedAsAFormatError()
    {
        string code = TeamCode.Encode(Ordinary());
        for (int length = 0; length < code.Length; length++)
            Assert.Throws<FormatException>(() => TeamCode.Decode(code.Substring(0, length)));
        Assert.Throws<FormatException>(() => TeamCode.Decode(code + "A"));
    }

    [Fact]
    public void TamperedPayloadAndWrongPrefixesAreRejected()
    {
        string code = TeamCode.Encode(Ordinary());
        foreach (int index in new[] { 5, (code.Length + 5) / 2, code.Length - 5 })
        {
            var chars = code.ToCharArray();
            chars[index] = chars[index] == 'A' ? 'B' : 'A';
            Assert.Throws<FormatException>(() => TeamCode.Decode(new string(chars)));
        }
        foreach (string prefix in new[] { "SDT2-", "sdt1-", "SDT1+", "" })
            Assert.Throws<FormatException>(() => TeamCode.Decode(prefix + code.Substring(5)));
        Assert.Throws<FormatException>(() => TeamCode.Decode(code.Insert(8, "%")));
        Assert.Throws<FormatException>(() => TeamCode.Decode(code.Insert(8, "中")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(" \r\n\t\u2003")]
    [InlineData("SDT1-")]
    [InlineData("SDT1-%%%%")]
    public void MissingOrMalformedCodeAlwaysThrowsFormatException(string code)
    {
        Assert.Throws<FormatException>(() => TeamCode.Decode(code));
    }
}
