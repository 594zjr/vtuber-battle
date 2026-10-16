using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StreamDuel.Core;
using Xunit;

public class T79_NetProtocolGoldenTests
{
    const string GoldenNonce = "000102030405060708090a0b0c0d0e0f";
    const string HostName = "主\"机\\一\n😀";
    const string GuestName = "客机";
    static readonly string[] HostTeam = { "unit1", "unit2", "unit3", "unit4", "unit5", "unit6" };
    static readonly string[] GuestTeam = { "unit6", "unit5", "unit4", "unit3", "unit2", "unit1" };
    static readonly string[] Files =
    {
        "hello-host", "hello-guest", "seed-reveal-host", "seed-reveal-guest", "commit-action", "reveal-action", "hash-turn",
        "commit-replace", "reveal-replace", "hash-replace-first", "hash-replace-second", "hash-final", "error"
    };
    static readonly string GoldenDirectory = Path.Combine(SourceDirectory(), "Golden", "net");

    static string SourceDirectory([CallerFilePath] string sourceFile = "")
    {
        return Path.GetDirectoryName(sourceFile);
    }

    // 冻结数据使消息指纹不随正式内容包的平衡调整变化。
    static ContentDb FrozenContent()
    {
        var db = new ContentDb
        {
            PackId = "golden_net_v2", PackName = "协议固定样例", PackNotice = "仅测试用原创数据",
            Struggle = new SkillDef { Id = "struggle", Name = "测试硬撑", Type = "plain", Cat = "interact", Power = 1, Pp = 1, Acc = 100 }
        };
        db.Types.Add("plain", new TypeDef { Id = "plain", Name = "测试赛道", Color = "#808080" });
        db.CatNames.Add("status", "状态");
        db.CatNames.Add("interact", "互动");
        db.Personas.Add("plain", new PersonaDef
        {
            Id = "plain", Name = "测试人设", Code = "T_LOWHP_POWER", Node = "伤害计算", Desc = "固定测试参数",
            Params = new Dictionary<string, double> { ["below"] = 0, ["mul"] = 1 }
        });
        for (int i = 0; i < 5; i++)
        {
            string id = "idle" + i.ToString(CultureInfo.InvariantCulture);
            db.Skills.Add(id, new SkillDef { Id = id, Name = "测试技能" + i, Type = "plain", Cat = "status", Pp = 9, Acc = 100 });
        }
        for (int i = 1; i <= 6; i++)
        {
            db.Streamers.Add(new StreamerDef
            {
                Id = "unit" + i.ToString(CultureInfo.InvariantCulture), Name = "样例" + i,
                Type = "plain", Persona = "plain", Fifth = "idle4", Blurb = "原创测试单位", Org = "测试组",
                Stats = new Dictionary<string, int> { ["hp"] = 100, ["atk"] = 50, ["def"] = 50, ["spa"] = 50, ["sdef"] = 50, ["spd"] = 10 + i },
                Skills = new List<string> { "idle0", "idle1", "idle2", "idle3" }
            });
        }
        return db;
    }

    sealed class Transport : INetTransport
    {
        public readonly Queue<string> Incoming = new Queue<string>();
        public readonly List<string> Sent = new List<string>();
        public Transport Peer;
        public bool HoldAll, HoldHashes;

        public void Send(string message)
        {
            Sent.Add(message);
            if (!HoldAll && !(HoldHashes && Text(message, "type") == "hash")) Peer.Incoming.Enqueue(message);
        }

        public bool TryReceive(out string message)
        {
            if (Incoming.Count == 0) { message = null; return false; }
            message = Incoming.Dequeue();
            return true;
        }

        public string Single(string type) { return Sent.Single(message => Text(message, "type") == type); }
        public string[] All(string type) { return Sent.Where(message => Text(message, "type") == type).ToArray(); }
    }

    sealed class Pair
    {
        public readonly Transport A = new Transport(), B = new Transport();
        public readonly NetSession Host, Guest;

        public Pair(bool handshake = true, double? turnSeconds = null, double? bankSeconds = null)
        {
            A.Peer = B;
            B.Peer = A;
            var db = FrozenContent();
            Host = new NetSession(db, A, true, HostTeam, HostName, 79, turnSeconds, bankSeconds, seedContribution: T75ReviewProtocol.HostContribution);
            Guest = new NetSession(db, B, false, GuestTeam, GuestName, null, seedContribution: T75ReviewProtocol.GuestContribution);
            if (handshake)
            {
                Pump();
                Assert.Equal(NetState.Choose, Host.State);
                Assert.Equal(NetState.Choose, Guest.State);
            }
        }

        public void Pump()
        {
            for (int i = 0; i < 8; i++) { Host.Poll(); Guest.Poll(); }
        }

        public void Act(BattleAction action)
        {
            Host.Submit(action);
            Guest.Submit(action);
            Pump();
        }

        public void EnterReplacement()
        {
            foreach (var battle in new[] { Host.Battle, Guest.Battle })
                for (int side = 0; side < 2; side++)
                {
                    battle.Active(side).Hp = 0;
                    battle.Active(side).Fainted = true;
                }
            Act(BattleAction.Pass());
            Assert.Equal(NetState.Replace, Host.State);
            Assert.Equal(NetState.Replace, Guest.State);
            A.Sent.Clear();
            B.Sent.Clear();
        }
    }

    static string Text(string message, string field)
    {
        using var json = JsonDocument.Parse(message);
        return json.RootElement.GetProperty(field).GetString();
    }

    static string Digest(string action, string nonce)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(action + "|" + nonce))).ToLowerInvariant();
    }

    static Dictionary<string, object> Object(string message)
    {
        return JsonSerializer.Deserialize<Dictionary<string, object>>(message);
    }

    static void CaptureCommitReveal(Dictionary<string, string> messages, string kind, Transport source)
    {
        string commit = source.Single("commit"), reveal = source.Single("reveal");
        foreach (string raw in new[] { commit, reveal })
        {
            using var json = JsonDocument.Parse(raw);
            var names = json.RootElement.EnumerateObject().Select(property => property.Name).ToArray();
            // 字典会折叠重复键，必须在规范化前拒绝这种发送格式变化。
            Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
        }
        string nonce = Text(reveal, "nonce"), action = Text(reveal, "action");
        Assert.Matches("^[0-9a-f]{32}$", nonce);
        Assert.Matches("^[0-9a-f]{64}$", Text(commit, "digest"));
        Assert.Equal(Digest(action, nonce), Text(commit, "digest"));
        var normalizedCommit = Object(commit);
        var normalizedReveal = Object(reveal);
        // 只消除密码学随机串及其绑定摘要；其他字段原样保留，新增发送字段也会使比较失败。
        normalizedCommit["digest"] = Digest(action, GoldenNonce);
        normalizedReveal["nonce"] = GoldenNonce;
        messages.Add("commit-" + kind, JsonSerializer.Serialize(normalizedCommit));
        messages.Add("reveal-" + kind, JsonSerializer.Serialize(normalizedReveal));
    }

    static Dictionary<string, string> CaptureCurrentMessages()
    {
        var messages = new Dictionary<string, string>();
        var action = new Pair();
        messages.Add("hello-host", action.A.Single("hello"));
        messages.Add("hello-guest", action.B.Single("hello"));
        messages.Add("seed-reveal-host", action.A.Single("seed_reveal"));
        messages.Add("seed-reveal-guest", action.B.Single("seed_reveal"));
        Assert.Equal(T75ReviewProtocol.FairSeed, action.Host.Battle.Seed);
        Assert.Equal(T75ReviewProtocol.FairSeed, action.Guest.Battle.Record.Seed);
        action.Act(BattleAction.Skill(0));
        Assert.Equal(NetState.Choose, action.Host.State);
        Assert.Equal(NetState.Choose, action.Guest.State);
        CaptureCommitReveal(messages, "action", action.B);
        messages.Add("hash-turn", action.B.Single("hash"));

        var replacement = new Pair();
        replacement.EnterReplacement();
        replacement.Host.SubmitReplace(1);
        replacement.Guest.SubmitReplace(1);
        replacement.Pump();
        Assert.Equal(NetState.Choose, replacement.Host.State);
        Assert.Equal(NetState.Choose, replacement.Guest.State);
        CaptureCommitReveal(messages, "replace", replacement.B);
        string[] hashes = replacement.B.All("hash");
        Assert.Equal(2, hashes.Length);
        messages.Add("hash-replace-first", hashes[0]);
        messages.Add("hash-replace-second", hashes[1]);

        var final = new Pair();
        final.Host.Battle.Turn = Battle.TurnCap;
        final.Guest.Battle.Turn = Battle.TurnCap;
        final.Act(BattleAction.Pass());
        Assert.Equal(NetState.Over, final.Host.State);
        Assert.Equal(NetState.Over, final.Guest.State);
        messages.Add("hash-final", final.B.Single("hash"));
        Assert.Equal(new[] { "hello", "seed_reveal", "commit", "reveal", "hash" }, final.B.Sent.Select(message => Text(message, "type")));

        var error = new Pair();
        error.A.Incoming.Enqueue("{\"type\":\"commit\",\"turn\":1,\"kind\":\"action\"}");
        error.Host.Poll();
        Assert.Equal(NetState.Error, error.Host.State);
        messages.Add("error", error.A.Single("error"));
        foreach (string message in action.A.Sent.Concat(action.B.Sent).Concat(replacement.B.Sent).Concat(final.B.Sent).Concat(error.A.Sent))
        {
            Assert.DoesNotContain("\n", message);
            Assert.DoesNotContain("\r", message);
            using var json = JsonDocument.Parse(message);
            Assert.Equal(JsonValueKind.Object, json.RootElement.ValueKind);
        }
        return messages;
    }

    static string Golden(string file)
    {
        bool fairSeed = file.StartsWith("hello-", StringComparison.Ordinal)
            || file.StartsWith("hash-", StringComparison.Ordinal) || file.StartsWith("seed-reveal-", StringComparison.Ordinal);
        return File.ReadAllText(Path.Combine(GoldenDirectory, (fairSeed ? "t87-v6-" : "") + file + ".json"), Encoding.UTF8);
    }

    static string HistoricalGolden(string file)
    {
        return File.ReadAllText(Path.Combine(GoldenDirectory, file + ".json"), Encoding.UTF8);
    }

    [Fact]
    public void CurrentMessagesMatchFrozenFieldsAndRealEntropyRemainsBoundToTheAction()
    {
        var actual = CaptureCurrentMessages();
        Assert.Equal(Files.OrderBy(file => file, StringComparer.Ordinal), actual.Keys.OrderBy(file => file, StringComparer.Ordinal));
        foreach (string file in Files)
        {
            using var expectedJson = JsonDocument.Parse(Golden(file));
            using var actualJson = JsonDocument.Parse(actual[file]);
            EqualJson(expectedJson.RootElement, actualJson.RootElement, file);
        }
    }

    static void EqualJson(JsonElement expected, JsonElement actual, string path)
    {
        Assert.True(expected.ValueKind == actual.ValueKind, path + " 的 JSON 类型发生变化。");
        if (expected.ValueKind == JsonValueKind.Object)
        {
            var left = expected.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal).ToArray();
            var right = actual.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal).ToArray();
            Assert.Equal(left.Select(property => property.Name), right.Select(property => property.Name));
            for (int i = 0; i < left.Length; i++) EqualJson(left[i].Value, right[i].Value, path + "." + left[i].Name);
        }
        else if (expected.ValueKind == JsonValueKind.Array)
        {
            var left = expected.EnumerateArray().ToArray();
            var right = actual.EnumerateArray().ToArray();
            Assert.Equal(left.Length, right.Length);
            for (int i = 0; i < left.Length; i++) EqualJson(left[i], right[i], path + "[" + i + "]");
        }
        else if (expected.ValueKind == JsonValueKind.String) Assert.Equal(expected.GetString(), actual.GetString());
        else if (expected.ValueKind == JsonValueKind.Number) Assert.Equal(expected.GetDecimal(), actual.GetDecimal());
        else Assert.Equal(expected.GetRawText(), actual.GetRawText());
    }

    sealed class Receiver
    {
        public NetSession Session;
        public Transport Transport;
    }

    static void Deliver(Receiver receiver, string message)
    {
        receiver.Transport.Incoming.Enqueue(message);
        receiver.Session.Poll();
    }

    static Receiver PrepareReceiver(string file)
    {
        bool isHello = file.StartsWith("hello-", StringComparison.Ordinal);
        bool isSeed = file.StartsWith("seed-reveal-", StringComparison.Ordinal);
        var pair = new Pair(handshake: !isHello && !isSeed);
        var result = new Receiver { Session = pair.Host, Transport = pair.A };
        if (isHello || isSeed)
        {
            pair.A.Incoming.Clear();
            pair.B.Incoming.Clear();
            if (file == "hello-host" || file == "seed-reveal-host") { result.Session = pair.Guest; result.Transport = pair.B; }
            if (isSeed) Deliver(result, Golden(file == "seed-reveal-host" ? "hello-host" : "hello-guest"));
            Assert.Equal(NetState.Handshake, result.Session.State);
            Assert.Null(result.Session.Battle);
            return result;
        }
        if (file == "error") return result;
        if (file == "hash-final")
        {
            pair.Host.Battle.Turn = Battle.TurnCap;
            pair.Guest.Battle.Turn = Battle.TurnCap;
            pair.B.HoldHashes = true;
            pair.Act(BattleAction.Pass());
            Assert.Equal(NetState.Waiting, pair.Host.State);
            Assert.Equal(Phase.Over, pair.Host.Battle.Phase);
            return result;
        }
        bool replacement = file.Contains("replace", StringComparison.Ordinal);
        if (replacement) pair.EnterReplacement();
        pair.B.HoldAll = true;
        if (replacement) pair.Host.SubmitReplace(1);
        else pair.Host.Submit(BattleAction.Skill(0));
        string kind = replacement ? "replace" : "action";
        if (!file.StartsWith("commit-", StringComparison.Ordinal)) Deliver(result, Golden("commit-" + kind));
        if (file.StartsWith("hash-", StringComparison.Ordinal)) Deliver(result, Golden("reveal-" + kind));
        if (file == "hash-replace-second") Deliver(result, Golden("hash-replace-first"));
        Assert.Equal(NetState.Waiting, result.Session.State);
        return result;
    }

    public static IEnumerable<object[]> ParsingCases()
    {
        foreach (string file in Files)
        {
            yield return new object[] { file, false };
            yield return new object[] { file, true };
        }
    }

    [Theory]
    [MemberData(nameof(ParsingCases))]
    public void FrozenMessagesAreParsedByRealSessionsIncludingUnknownFields(string file, bool extraFields)
    {
        var receiver = PrepareReceiver(file);
        string message = Golden(file);
        if (extraFields)
        {
            var obj = Object(message);
            obj["futureExtension"] = new { label = "额外字段", flags = new object[] { true, null, 7 } };
            message = JsonSerializer.Serialize(obj);
        }

        Deliver(receiver, message);

        var session = receiver.Session;
        if (file == "error")
        {
            Assert.Equal(NetState.Error, session.State);
            Assert.Equal("对方报错：" + Text(Golden(file), "message"), session.Error);
            Assert.DoesNotContain(receiver.Transport.Sent, sent => Text(sent, "type") == "error");
        }
        else if (file.StartsWith("hello-", StringComparison.Ordinal) || file.StartsWith("seed-reveal-", StringComparison.Ordinal))
        {
            if (file.StartsWith("hello-", StringComparison.Ordinal))
            {
                Assert.Equal(NetState.Handshake, session.State);
                Assert.Null(session.Battle);
                Assert.Single(receiver.Transport.All("seed_reveal"));
                Deliver(receiver, Golden(file == "hello-host" ? "seed-reveal-host" : "seed-reveal-guest"));
            }
            Assert.Equal(NetState.Choose, session.State);
            Assert.Equal(T75ReviewProtocol.FairSeed, session.Battle.Seed);
            Assert.Equal(T75ReviewProtocol.FairSeed, session.Battle.Record.Seed);
            Assert.Equal(HostName, session.Battle.Sides[0].Name);
            Assert.Equal(GuestName, session.Battle.Sides[1].Name);
            Assert.Equal(HostTeam, session.Battle.Sides[0].Team.Select(fighter => fighter.Def.Id));
            Assert.Equal(GuestTeam, session.Battle.Sides[1].Team.Select(fighter => fighter.Def.Id));
        }
        else if (file.StartsWith("commit-", StringComparison.Ordinal))
        {
            Assert.Equal(NetState.Waiting, session.State);
            Assert.Single(receiver.Transport.All("reveal"));
        }
        else if (file.StartsWith("reveal-", StringComparison.Ordinal))
        {
            Assert.Equal(NetState.Waiting, session.State);
            Assert.Equal(file == "reveal-action" ? 1 : 3, session.Battle.Record.Events.Count);
            Assert.Equal(file == "reveal-action" ? 1 : 2, receiver.Transport.All("hash").Length);
        }
        else if (file == "hash-final")
        {
            Assert.Equal(NetState.Over, session.State);
            Assert.Equal(Phase.Over, session.Battle.Phase);
            Assert.Equal(-1, session.Battle.Winner);
        }
        else
        {
            if (file == "hash-replace-first")
            {
                Assert.Equal(NetState.Waiting, session.State);
                Deliver(receiver, Golden("hash-replace-second"));
            }
            Assert.Equal(NetState.Choose, session.State);
            Assert.Equal(2, session.Battle.Turn);
        }
    }

    public static IEnumerable<object[]> MissingFields()
    {
        foreach (string file in Files)
        {
            string[] fields = file.StartsWith("hello-", StringComparison.Ordinal)
                ? new[] { "type", "version", "packId", "contentHash", "side", "seedCommit", "name", "team" }
                : file.StartsWith("seed-reveal-", StringComparison.Ordinal) ? new[] { "type", "value" }
                : file.StartsWith("commit-", StringComparison.Ordinal) ? new[] { "type", "turn", "kind", "digest" }
                : file.StartsWith("reveal-", StringComparison.Ordinal) ? new[] { "type", "turn", "kind", "action", "nonce" }
                : file.StartsWith("hash-", StringComparison.Ordinal) ? new[] { "type", "turn", "hash" }
                : new[] { "type", "message" };
            foreach (string field in fields) yield return new object[] { file, field };
        }
    }

    [Theory]
    [MemberData(nameof(MissingFields))]
    public void MissingRequiredFieldsAreRejectedAtTheCorrectPhaseWithAChineseReason(string file, string field)
    {
        var receiver = PrepareReceiver(file);
        var obj = Object(Golden(file));
        Assert.True(obj.Remove(field));

        Deliver(receiver, JsonSerializer.Serialize(obj));

        Assert.Equal(NetState.Error, receiver.Session.State);
        Assert.Matches("[\u4e00-\u9fff]", receiver.Session.Error);
        Assert.Contains(field, receiver.Session.Error);
        Assert.Contains(receiver.Transport.Sent, sent => Text(sent, "type") == "error");
    }

    static void AssertAdditionalGolden(string file, string message)
    {
        Assert.DoesNotContain("\n", message);
        Assert.DoesNotContain("\r", message);
        using var expectedJson = JsonDocument.Parse(Golden(file));
        using var actualJson = JsonDocument.Parse(message);
        EqualJson(expectedJson.RootElement, actualJson.RootElement, file);
    }

    static Receiver IsolatedReceiver(Pair pair, bool host)
    {
        // 隔离握手候选，避免另一个原始 hello 先把会话推进到已建战斗状态。
        pair.A.Incoming.Clear();
        pair.B.Incoming.Clear();
        return host
            ? new Receiver { Session = pair.Host, Transport = pair.A }
            : new Receiver { Session = pair.Guest, Transport = pair.B };
    }

    static void AssertHelloRejected(Receiver receiver, string field)
    {
        Assert.Equal(NetState.Error, receiver.Session.State);
        Assert.Null(receiver.Session.Battle);
        Assert.Matches("[\u4e00-\u9fff]", receiver.Session.Error);
        Assert.Contains(field, receiver.Session.Error);
        Assert.Single(receiver.Transport.All("error"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ChatGoldenMatchesRealSendAndPreservesBothBattleStates(bool hostSends)
    {
        var pair = new Pair();
        pair.Act(BattleAction.Skill(0));
        Assert.NotEmpty(pair.Host.Battle.Record.Events);
        var sessions = new[] { pair.Host, pair.Guest };
        var hashes = sessions.Select(session => session.Battle.StateHash()).ToArray();
        var records = sessions.Select(session => session.Battle.Record.ToJson()).ToArray();
        var nextRandom = sessions.Select(session => session.Battle.Clone().Random()).ToArray();
        var sender = hostSends ? pair.Host : pair.Guest;
        var receiver = hostSends ? pair.Guest : pair.Host;
        var transport = hostSends ? pair.A : pair.B;

        Assert.True(sender.SendQuickChat(0, 0));
        pair.Pump();

        AssertAdditionalGolden("chat", transport.Single("chat"));
        var chat = Assert.Single(receiver.ReceivedChats);
        Assert.Equal(0, chat.Index);
        Assert.Equal(sender.MySide, chat.Side);
        Assert.Equal(2, chat.Turn);
        Assert.Equal(receiver.Battle.Turn, chat.Turn);
        Assert.Empty(sender.ReceivedChats);
        for (int side = 0; side < sessions.Length; side++)
        {
            Assert.Equal(NetState.Choose, sessions[side].State);
            Assert.Null(sessions[side].Error);
            Assert.Equal(hashes[side], sessions[side].Battle.StateHash());
            Assert.Equal(records[side], sessions[side].Battle.Record.ToJson());
            Assert.Equal(nextRandom[side], sessions[side].Battle.Clone().Random());
        }
        Assert.Empty(pair.A.All("error"));
        Assert.Empty(pair.B.All("error"));
    }

    [Fact]
    public void TimedHostHelloGoldenMatchesEverySentFieldAndNegotiatesBothClocks()
    {
        var pair = new Pair(turnSeconds: 10, bankSeconds: 30);

        AssertAdditionalGolden("hello-host-timer", pair.A.Single("hello"));
        foreach (var session in new[] { pair.Host, pair.Guest })
        {
            Assert.Equal(NetState.Choose, session.State);
            Assert.NotNull(session.Clock);
            for (int side = 0; side < 2; side++)
            {
                Assert.Equal(10.0, session.Clock.TurnLeft(side));
                Assert.Equal(30.0, session.Clock.BankLeft(side));
            }
        }
        Assert.Equal(pair.Host.Battle.StateHash(), pair.Guest.Battle.StateHash());
    }

    [Theory]
    [InlineData("extra")]
    [InlineData("missingIndex")]
    public void InvalidChatShapeIsDroppedWithoutErrorOrConsumingReceiveQuota(string change)
    {
        var pair = new Pair();
        var receiver = new Receiver { Session = pair.Guest, Transport = pair.B };
        var message = Object(Golden("chat"));
        if (change == "extra") message["futureExtension"] = new { label = "额外字段" };
        else Assert.True(message.Remove("index"));

        Deliver(receiver, JsonSerializer.Serialize(message));

        Assert.Equal(NetState.Choose, receiver.Session.State);
        Assert.Null(receiver.Session.Error);
        Assert.Empty(receiver.Session.ReceivedChats);
        Assert.Empty(receiver.Transport.All("error"));
        for (int index = 0; index < 6; index++) Deliver(receiver, Golden("chat"));
        Assert.Equal(6, receiver.Session.ReceivedChats.Count);
        Assert.All(receiver.Session.ReceivedChats, chat =>
        {
            Assert.Equal(0, chat.Index);
            Assert.Equal(pair.Host.MySide, chat.Side);
            Assert.Equal(pair.Host.Battle.Turn, chat.Turn);
        });
        Deliver(receiver, Golden("chat"));
        Assert.Equal(6, receiver.Session.ReceivedChats.Count);
        Assert.Equal(NetState.Choose, receiver.Session.State);
        Assert.Null(receiver.Session.Error);
        Assert.Empty(receiver.Transport.All("error"));
    }

    [Fact]
    public void ChatWithoutTypeUsesTheNormalProtocolErrorPath()
    {
        var pair = new Pair();
        var receiver = new Receiver { Session = pair.Guest, Transport = pair.B };
        var message = Object(Golden("chat"));
        Assert.True(message.Remove("type"));

        Deliver(receiver, JsonSerializer.Serialize(message));

        Assert.Equal(NetState.Error, receiver.Session.State);
        Assert.Matches("[\u4e00-\u9fff]", receiver.Session.Error);
        Assert.Contains("type", receiver.Session.Error);
        Assert.Empty(receiver.Session.ReceivedChats);
        Assert.Single(receiver.Transport.All("error"));
    }

    [Fact]
    public void VersionSixWithoutTimerCapabilityRemainsCompatibleWhenBothTimersAreDisabled()
    {
        var pair = new Pair(handshake: false);
        IsolatedReceiver(pair, true);
        var hostHello = Object(pair.A.Single("hello"));
        var guestHello = Object(pair.B.Single("hello"));
        foreach (var message in new[] { hostHello, guestHello })
        {
            Assert.Equal(6, ((JsonElement)message["version"]).GetInt32());
            Assert.True(message.Remove("timerVersion"));
            Assert.False(message.ContainsKey("timer"));
        }
        pair.A.Incoming.Enqueue(JsonSerializer.Serialize(guestHello));
        pair.B.Incoming.Enqueue(JsonSerializer.Serialize(hostHello));

        pair.Pump();

        foreach (var session in new[] { pair.Host, pair.Guest })
        {
            Assert.Equal(NetState.Choose, session.State);
            Assert.NotNull(session.Battle);
            Assert.Null(session.Clock);
            Assert.Null(session.Error);
        }
        Assert.Equal(pair.Host.Battle.StateHash(), pair.Guest.Battle.StateHash());
        Assert.Empty(pair.A.All("error"));
        Assert.Empty(pair.B.All("error"));
    }

    [Fact]
    public void TimedHostRejectsGuestMissingTimerCapabilityBeforeCreatingBattle()
    {
        var pair = new Pair(handshake: false, turnSeconds: 10, bankSeconds: 30);
        var receiver = IsolatedReceiver(pair, true);
        var message = Object(pair.B.Single("hello"));
        Assert.True(message.Remove("timerVersion"));

        Deliver(receiver, JsonSerializer.Serialize(message));

        AssertHelloRejected(receiver, "timerVersion");
    }

    [Theory]
    [InlineData("timerVersion")]
    [InlineData("turnSeconds")]
    [InlineData("bankSeconds")]
    public void GuestRejectsTimedHelloMissingCapabilityOrEitherBudgetBeforeCreatingBattle(string field)
    {
        var pair = new Pair(handshake: false, turnSeconds: 10, bankSeconds: 30);
        var receiver = IsolatedReceiver(pair, false);
        var message = Object(Golden("hello-host-timer"));
        if (field == "timerVersion") Assert.True(message.Remove(field));
        else
        {
            var timer = Object(((JsonElement)message["timer"]).GetRawText());
            Assert.True(timer.Remove(field));
            message["timer"] = timer;
        }

        Deliver(receiver, JsonSerializer.Serialize(message));

        AssertHelloRejected(receiver, field);
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 1)]
    [InlineData(true, 2)]
    [InlineData(false, 2)]
    [InlineData(true, 3)]
    [InlineData(false, 3)]
    [InlineData(true, 4)]
    [InlineData(false, 4)]
    [InlineData(true, 5)]
    [InlineData(false, 5)]
    public void OlderHelloVersionsAreRejectedByEitherSideBeforeCreatingBattle(bool hostReceives, int version)
    {
        var pair = new Pair(handshake: false);
        var receiver = IsolatedReceiver(pair, hostReceives);
        var message = Object(hostReceives ? pair.B.Single("hello") : pair.A.Single("hello"));
        message["version"] = version;

        Deliver(receiver, JsonSerializer.Serialize(message));

        AssertHelloRejected(receiver, "协议版本");
        Assert.Contains(version.ToString(CultureInfo.InvariantCulture), receiver.Session.Error);
        Assert.Contains("6", receiver.Session.Error);
    }

    [Theory]
    [InlineData("hello-host", false)]
    [InlineData("hello-guest", true)]
    [InlineData("t75-hello-host", false)]
    [InlineData("t75-hello-guest", true)]
    public void UntouchedHistoricalHelloFilesAreRejectedBeforeCreatingBattle(string file, bool hostReceives)
    {
        var receiver = IsolatedReceiver(new Pair(handshake: false), hostReceives);
        Deliver(receiver, HistoricalGolden(file));
        AssertHelloRejected(receiver, "版本");
        Assert.Contains("不一致", receiver.Session.Error);
        Assert.Contains("6", receiver.Session.Error);
    }

    [Fact]
    public void HistoricalSeed79StateSamplesRemainUnchangedUnderTheSameRecordedActions()
    {
        Battle NewBattle() => new Battle(FrozenContent(), new IList<string>[] { HostTeam, GuestTeam },
            new[] { HostName, GuestName }, 79);
        void Check(string file, Battle battle, int operationTurn)
        {
            using var expected = JsonDocument.Parse(HistoricalGolden(file));
            Assert.Equal("hash", expected.RootElement.GetProperty("type").GetString());
            Assert.Equal(operationTurn, expected.RootElement.GetProperty("turn").GetInt32());
            Assert.Equal(expected.RootElement.GetProperty("hash").GetString(), battle.StateHash().ToString("x16"));
        }
        var action = NewBattle();
        action.ResolveTurn(BattleAction.Skill(0), BattleAction.Skill(0));
        Check("hash-turn", action, 1);
        var replacement = NewBattle();
        for (int side = 0; side < 2; side++)
        {
            replacement.Active(side).Hp = 0;
            replacement.Active(side).Fainted = true;
        }
        replacement.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        replacement.Replace(0, 1);
        Check("hash-replace-first", replacement, 2);
        replacement.Replace(1, 1);
        Check("hash-replace-second", replacement, 2);
        var final = NewBattle();
        final.Turn = Battle.TurnCap;
        final.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        Check("hash-final", final, Battle.TurnCap);
    }

}
