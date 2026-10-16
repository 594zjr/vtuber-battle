using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using StreamDuel.Core;
using Xunit;

public class T48_ShareCodeEdgeTests
{
    const string LegacyJson = "{\"seed\":0,\"packId\":\"original\",\"teams\":[[],[]],\"events\":[]}";

    [Theory]
    [InlineData(0u)]
    [InlineData(uint.MaxValue)]
    public void EveryEventAndActionPreservesMetadataUnsignedValuesAndUnicode(uint seed)
    {
        string text = "中文\"\\/\n\r\t\b\f\0\u0001\u2028\u2029\U0001f680e\u0301\ud800";
        var record = new BattleRecord
        {
            Seed = seed,
            PackId = text,
            Version = 2,
            ContentHash = "0123456789abcdef",
            LeadPick = true,
            BanOne = true,
            Names = new[] { text, "另一方" },
            Teams = new IList<string>[] { new[] { text, "主播一" }, new[] { "主播二" } }
        };
        record.Events.Add(new BattleRecordEvent { Type = BattleRecordEventType.Ban, Side = 1, To = 5 });
        record.Events.Add(new BattleRecordEvent { Type = BattleRecordEventType.Lead, Side = 0, To = 0, RngState = 0 });
        record.Events.Add(new BattleRecordEvent { Type = BattleRecordEventType.Replace, Side = 1, To = int.MaxValue, RngState = uint.MaxValue });
        var actions = new[]
        {
            BattleAction.Skill(int.MinValue), BattleAction.SwitchTo(int.MaxValue), BattleAction.Pass(),
            new BattleAction { Type = ActionType.Struggle, Index = -7 },
            new BattleAction { Type = ActionType.PassControlled, Index = 9 }
        };
        for (int i = 0; i < actions.Length; i++)
        {
            record.Events.Add(new BattleRecordEvent
            {
                Type = BattleRecordEventType.Turn,
                A0 = actions[i],
                A1 = actions[actions.Length - 1 - i],
                RngState = i % 3 == 0 ? (uint?)null : i % 3 == 1 ? 0u : uint.MaxValue
            });
        }

        string before = record.ToJson();
        string code = ShareCode.Encode(record);
        var decoded = ShareCode.Decode(code);
        Assert.Equal(before, record.ToJson());
        Assert.Equal(before, decoded.ToJson());
        Assert.Equal(seed, decoded.Seed);
        Assert.Equal(text, decoded.Names[0]);
        Assert.Null(decoded.Events[0].RngState);
        Assert.Equal(0u, decoded.Events[1].RngState);
        Assert.Equal(uint.MaxValue, decoded.Events[2].RngState);
        Assert.Equal(code, ShareCode.Encode(decoded));
    }

    [Fact]
    public void LegacyJsonWithoutOptionalFieldsKeepsVersionOneDefaults()
    {
        var record = ShareCode.Decode(Envelope(StoredBlock(Encoding.UTF8.GetBytes(LegacyJson))));
        Assert.Equal(1, record.Version);
        Assert.Equal(0u, record.Seed);
        Assert.Equal("original", record.PackId);
        Assert.False(record.LeadPick);
        Assert.False(record.BanOne);
        Assert.Null(record.ContentHash);
        Assert.Null(record.Names);
        Assert.Empty(record.Events);
        Assert.Equal(record.ToJson(), ShareCode.Decode(ShareCode.Encode(record)).ToJson());
    }

    [Fact]
    public void ShareRoundTripPreservesRawIllegalActionsReplayLogsAndRandomSequence()
    {
        var db = BattleTests.LoadPack("original");
        var ids = db.Streamers.Select(s => s.Id).ToArray();
        var battle = new Battle(db, new IList<string>[] { ids.Take(6).ToArray(), ids.Skip(6).Take(6).ToArray() },
            new[] { "甲\n\"方\"", "乙🚀方" }, 0);
        battle.Random();
        battle.Random();
        battle.ResolveTurn(BattleAction.Skill(-1), BattleAction.SwitchTo(999));
        string before = battle.Record.ToJson();
        ulong hash = battle.StateHash();

        var decoded = ShareCode.Decode(ShareCode.Encode(battle.Record));
        Assert.Equal(before, battle.Record.ToJson());
        Assert.Equal(hash, battle.StateHash());
        var turn = Assert.Single(decoded.Events);
        Assert.Equal(ActionType.Skill, turn.A0.Type);
        Assert.Equal(-1, turn.A0.Index);
        Assert.Equal(ActionType.Switch, turn.A1.Type);
        Assert.Equal(999, turn.A1.Index);
        var replayed = Replay.Run(db, decoded);
        Assert.Equal(hash, replayed.StateHash());
        Assert.Equal(battle.Log.Count, replayed.Log.Count);
        for (int i = 0; i < battle.Log.Count; i++)
        {
            Assert.Equal(battle.Log[i].Turn, replayed.Log[i].Turn);
            Assert.Equal(battle.Log[i].Side, replayed.Log[i].Side);
            Assert.Equal(battle.Log[i].Node, replayed.Log[i].Node);
            Assert.Equal(battle.Log[i].Text, replayed.Log[i].Text);
            Assert.Equal(battle.Log[i].Kind, replayed.Log[i].Kind);
            Assert.Equal(battle.Log[i].SnapActive, replayed.Log[i].SnapActive);
            Assert.Equal(battle.Log[i].SnapHp[0], replayed.Log[i].SnapHp[0]);
            Assert.Equal(battle.Log[i].SnapHp[1], replayed.Log[i].SnapHp[1]);
        }
        for (int i = 0; i < 8; i++) Assert.Equal(battle.Random(), replayed.Random());
    }

    [Fact]
    public void DecodingPreservesFingerprintSoReplayStillRejectsMismatchedContent()
    {
        var db = BattleTests.LoadPack("original");
        var record = BattleRecord.FromJson(LegacyJson);
        record.ContentHash = "different-content";
        var decoded = ShareCode.Decode(ShareCode.Encode(record));
        Assert.Equal(record.ContentHash, decoded.ContentHash);
        var error = Assert.Throws<InvalidOperationException>(() => Replay.Run(db, decoded));
        Assert.Contains("内容不一致", error.Message);
    }

    [Fact]
    public void AllWhitespaceIsIgnoredEvenInsideThePrefixAndPayload()
    {
        var record = BattleRecord.FromJson(LegacyJson);
        string code = ShareCode.Encode(record);
        char[] whitespace = { ' ', '\t', '\r', '\n', '\v', '\f', '\u0085', '\u00a0', '\u2003', '\u2028', '\u2029', '\u3000' };
        var messy = new StringBuilder("\u3000");
        for (int i = 0; i < code.Length; i++) messy.Append(code[i]).Append(whitespace[i % whitespace.Length]);
        Assert.Equal(record.ToJson(), ShareCode.Decode(messy.ToString()).ToJson());
        AssertInvalid(code.Insert(5, "\u200b"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \r\n\u3000")]
    [InlineData("SD1-")]
    [InlineData("sd1-AAAA")]
    [InlineData("SD2-AAAA")]
    [InlineData("AAAA")]
    [InlineData("SD1-A")]
    [InlineData("SD1-AAAA+")]
    [InlineData("SD1-AAAA/")]
    [InlineData("SD1-AAAA=")]
    [InlineData("SD1-AA.AA")]
    [InlineData("SD1-AAAA\0")]
    [InlineData("SD1-AAAA")]
    [InlineData("SD1-AAAAAA")]
    public void MalformedPrefixBase64AndShortPayloadsHaveChineseFormatErrors(string code)
    {
        AssertInvalid(code);
    }

    [Fact]
    public void NonCanonicalBase64TailBitsAreRejectedEvenWhenDecodedBytesAreUnchanged()
    {
        string code = Envelope(StoredBlock(Encoding.UTF8.GetBytes(LegacyJson)));
        if (code.Length % 4 == 0) code = Envelope(StoredBlock(Encoding.UTF8.GetBytes(LegacyJson + " ")));
        Assert.NotEqual(0, code.Length % 4);
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
        int last = alphabet.IndexOf(code[code.Length - 1]);
        string malformed = code.Substring(0, code.Length - 1) + alphabet[last | 1];
        Assert.Equal(DecodeBase64(code), DecodeBase64(malformed));
        AssertInvalid(malformed);
    }

    [Fact]
    public void BothCompressedBytesAndChecksumAreProtected()
    {
        string code = Envelope(StoredBlock(Encoding.UTF8.GetBytes(LegacyJson)));
        byte[] payload = DecodeBase64(code);
        payload[6] ^= 1;
        AssertInvalid(Base64(payload));
        payload = DecodeBase64(code);
        payload[payload.Length - 1] ^= 1;
        AssertInvalid(Base64(payload));
    }

    [Fact]
    public void InvalidTruncatedAndTrailingDeflateDataFailEvenWithMatchingChecksum()
    {
        AssertInvalid(Envelope(new byte[] { 7 }));
        byte[] compressed = StoredBlock(Encoding.UTF8.GetBytes(LegacyJson));
        AssertInvalid(Envelope(compressed.Take(compressed.Length - 1).ToArray()));
        AssertInvalid(Envelope(compressed.Concat(new byte[] { 0 }).ToArray()));
        AssertInvalid(Envelope(compressed.Concat(compressed).ToArray()));
    }

    [Fact]
    public void CompleteJsonInANonFinalDeflateBlockStillRequiresTheStreamTerminator()
    {
        byte[] compressed = StoredBlock(Encoding.UTF8.GetBytes(LegacyJson));
        Assert.Equal(1, ShareCode.Decode(Envelope(compressed)).Version);
        compressed[0] = 0;
        AssertInvalid(Envelope(compressed));
    }

    [Fact]
    public void JsonByteLimitAcceptsOneMiBAndRejectsEncodingOrInflatingOneByteMore()
    {
        const int limit = 1024 * 1024;
        var record = BattleRecord.FromJson(LegacyJson);
        record.PackId = "";
        int room = limit - Encoding.UTF8.GetByteCount(record.ToJson());
        record.PackId = new string('中', room / 3) + new string('a', room % 3);
        string atLimit = record.ToJson();
        Assert.Equal(limit, Encoding.UTF8.GetByteCount(atLimit));
        Assert.Equal(atLimit, ShareCode.Decode(ShareCode.Encode(record)).ToJson());

        record.PackId += "a";
        string tooLarge = record.ToJson();
        Assert.True(tooLarge.Length < limit);
        Assert.Equal(limit + 1, Encoding.UTF8.GetByteCount(tooLarge));
        var encodeError = Assert.Throws<FormatException>(() => ShareCode.Encode(record));
        Assert.Contains("1 MiB", encodeError.Message);
        Assert.Matches("[\u4e00-\u9fff]", encodeError.Message);

        using (var output = new MemoryStream())
        {
            byte[] bytes = Encoding.UTF8.GetBytes(tooLarge);
            using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, true))
                deflate.Write(bytes, 0, bytes.Length);
            byte[] compressed = output.ToArray();
            Assert.True(compressed.Length < bytes.Length / 100);
            var decodeError = Assert.Throws<FormatException>(() => ShareCode.Decode(Envelope(compressed)));
            Assert.Contains("1 MiB", decodeError.Message);
            Assert.Matches("[\u4e00-\u9fff]", decodeError.Message);
        }
    }

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void UnknownNumericActionsArePreservedWithoutNormalization(int type)
    {
        var record = BattleRecord.FromJson(LegacyJson);
        record.Events.Add(new BattleRecordEvent
        {
            Type = BattleRecordEventType.Turn,
            A0 = new BattleAction { Type = (ActionType)type, Index = -7 },
            A1 = BattleAction.Pass()
        });
        var decoded = ShareCode.Decode(ShareCode.Encode(record));
        Assert.Equal(record.ToJson(), decoded.ToJson());
        Assert.Equal(type, (int)Assert.Single(decoded.Events).A0.Type);
        Assert.Equal(-7, decoded.Events[0].A0.Index);
    }

    [Theory]
    [InlineData(0xc3, 0x28)]
    [InlineData(0xc0, 0xaf)]
    [InlineData(0xed, 0xa0, 0x80)]
    [InlineData(0xf4, 0x90, 0x80, 0x80)]
    [InlineData(0xe4, 0xb8)]
    public void InvalidUtf8CannotBeSilentlyReplacedInsideAnOtherwiseValidRecord(params int[] invalid)
    {
        byte[] before = Encoding.UTF8.GetBytes("{\"seed\":0,\"packId\":\"");
        byte[] after = Encoding.UTF8.GetBytes("\",\"teams\":[[],[]],\"events\":[]}");
        byte[] json = before.Concat(invalid.Select(value => (byte)value)).Concat(after).ToArray();
        AssertInvalid(Envelope(StoredBlock(json)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"seed\":0,\"packId\":\"original\",\"teams\":[[],[]],\"events\":[],}")]
    [InlineData("{\"seed\":-1,\"packId\":\"original\",\"teams\":[[],[]],\"events\":[]}")]
    [InlineData("{\"seed\":0,\"packId\":\"original\",\"teams\":[[],[]],\"events\":[{\"type\":\"unknown\"}]}")]
    [InlineData("{\"seed\":0,\"packId\":\"original\",\"teams\":[[],[]],\"events\":[],\"names\":[\"one\"]}")]
    public void InvalidJsonAndRecordShapesFailAfterSuccessfulDecompression(string json)
    {
        AssertInvalid(Envelope(StoredBlock(Encoding.UTF8.GetBytes(json))));
    }

    [Fact]
    public void JsonTrailingContentIsRejected()
    {
        AssertInvalid(Envelope(StoredBlock(Encoding.UTF8.GetBytes(LegacyJson + "{}"))));
        AssertInvalid(Envelope(StoredBlock(Encoding.UTF8.GetBytes(LegacyJson + "\0"))));
    }

    static void AssertInvalid(string code)
    {
        var error = Assert.Throws<FormatException>(() => ShareCode.Decode(code));
        Assert.Matches("[\u4e00-\u9fff]", error.Message);
    }

    // 使用 DEFLATE 的原始存储块，让坏 UTF-8 / JSON 确实通过压缩层，不依赖待测编码器。
    static byte[] StoredBlock(byte[] bytes)
    {
        Assert.True(bytes.Length <= ushort.MaxValue);
        using (var output = new MemoryStream())
        using (var writer = new BinaryWriter(output, Encoding.UTF8, true))
        {
            writer.Write((byte)1);
            writer.Write((ushort)bytes.Length);
            writer.Write((ushort)~bytes.Length);
            writer.Write(bytes);
            writer.Flush();
            return output.ToArray();
        }
    }

    // 错误载荷重新附上有效校验，避免测试只碰到校验失败而漏掉后续校验层。
    static string Envelope(byte[] compressed)
    {
        uint checksum = 2166136261;
        foreach (byte value in compressed) checksum = unchecked((checksum ^ value) * 16777619);
        using (var output = new MemoryStream())
        using (var writer = new BinaryWriter(output, Encoding.UTF8, true))
        {
            writer.Write(compressed);
            writer.Write(checksum);
            writer.Flush();
            return Base64(output.ToArray());
        }
    }

    static string Base64(byte[] payload)
    {
        return "SD1-" + Convert.ToBase64String(payload).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    static byte[] DecodeBase64(string code)
    {
        string payload = code.Substring(4).Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(payload.PadRight((payload.Length + 3) / 4 * 4, '='));
    }
}
