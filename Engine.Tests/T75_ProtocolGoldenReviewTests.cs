using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StreamDuel.Core;
using Xunit;

public class T75_ProtocolGoldenReviewTests
{
    const string GoldenSessionId = "f0f1f2f3f4f5f6f7f8f9fafbfcfdfeff";
    const string GoldenNonce = "000102030405060708090a0b0c0d0e0f";
    static readonly string[] Files =
    {
        "hello-host", "hello-guest", "seed-reveal-host", "seed-reveal-guest", "hello-host-timer", "hello-host-termination", "hello-guest-termination",
        "packet-commit", "packet-reveal", "packet-hash", "resume", "resumed", "packet-goodbye", "packet-termination-freeze"
    };

    static string Golden(string name, [CallerFilePath] string source = "")
    {
        string prefix = name.StartsWith("seed-reveal-", StringComparison.Ordinal) ? "t87-v6-" : "t87-v6-reconnect-";
        return File.ReadAllText(Path.Combine(Path.GetDirectoryName(source), "Golden", "net", prefix + name + ".json"), Encoding.UTF8);
    }

    static string Digest(string wire, string nonce)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(wire + "|" + nonce))).ToLowerInvariant();
    }

    static string Single(T75ReviewProtocol.Transport source, string type)
    {
        return source.Sent.Single(message => T75ReviewProtocol.PayloadType(message) == type);
    }

    static string Normalize(string message)
    {
        using var original = JsonDocument.Parse(message);
        AssertUniqueKeys(original.RootElement);
        Assert.DoesNotContain("\n", message);
        Assert.DoesNotContain("\r", message);
        var fields = T75ReviewProtocol.Object(message);
        if (original.RootElement.TryGetProperty("sessionId", out var sessionId) && sessionId.ValueKind != JsonValueKind.Null)
        {
            Assert.Matches("^[0-9a-f]{32}$", sessionId.GetString());
            fields["sessionId"] = GoldenSessionId;
        }
        if (T75ReviewProtocol.Text(message, "type") == "packet")
        {
            string rawPayload = T75ReviewProtocol.Payload(message);
            using var parsed = JsonDocument.Parse(rawPayload);
            AssertUniqueKeys(parsed.RootElement);
            var payload = T75ReviewProtocol.Object(rawPayload);
            string type = T75ReviewProtocol.Text(rawPayload, "type");
            // 仅规范化密码学熵及绑定摘要；序号、能力、内容指纹和状态哈希全部冻结。
            if (type == "commit") payload["digest"] = Digest("S0", GoldenNonce);
            if (type == "reveal") payload["nonce"] = GoldenNonce;
            fields["payload"] = JsonSerializer.Serialize(payload);
        }
        return JsonSerializer.Serialize(fields);
    }

    static void AssertUniqueKeys(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var properties = element.EnumerateObject().ToArray();
            Assert.Equal(properties.Length, properties.Select(property => property.Name).Distinct(StringComparer.Ordinal).Count());
            foreach (var property in properties) AssertUniqueKeys(property.Value);
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) AssertUniqueKeys(child);
    }

    static void EqualJson(JsonElement expected, JsonElement actual)
    {
        Assert.Equal(expected.ValueKind, actual.ValueKind);
        if (expected.ValueKind == JsonValueKind.Object)
        {
            var left = expected.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal).ToArray();
            var right = actual.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal).ToArray();
            Assert.Equal(left.Select(property => property.Name), right.Select(property => property.Name));
            for (int index = 0; index < left.Length; index++)
            {
                if (left[index].Name == "payload")
                {
                    using var expectedPayload = JsonDocument.Parse(left[index].Value.GetString());
                    using var actualPayload = JsonDocument.Parse(right[index].Value.GetString());
                    EqualJson(expectedPayload.RootElement, actualPayload.RootElement);
                }
                else EqualJson(left[index].Value, right[index].Value);
            }
        }
        else if (expected.ValueKind == JsonValueKind.Array)
        {
            var left = expected.EnumerateArray().ToArray();
            var right = actual.EnumerateArray().ToArray();
            Assert.Equal(left.Length, right.Length);
            for (int index = 0; index < left.Length; index++) EqualJson(left[index], right[index]);
        }
        else if (expected.ValueKind == JsonValueKind.String) Assert.Equal(expected.GetString(), actual.GetString());
        else if (expected.ValueKind == JsonValueKind.Number) Assert.Equal(expected.GetDecimal(), actual.GetDecimal());
        else Assert.Equal(expected.GetRawText(), actual.GetRawText());
    }

    [Fact]
    public void VersionSixReconnectMessagesMatchNewFrozenSamplesWithoutChangingLegacySamples()
    {
        var messages = new Dictionary<string, string>();
        var pair = new T75ReviewProtocol.Pair();
        Assert.Equal(6, pair.Host.SessionProtocolVersion);
        Assert.Equal(6, pair.Guest.SessionProtocolVersion);
        Assert.Equal("06cab9cf6b123697", pair.Host.Battle.Db.ContentHash());
        messages.Add("hello-host", Single(pair.A, "hello"));
        messages.Add("hello-guest", Single(pair.B, "hello"));
        messages.Add("seed-reveal-host", Single(pair.A, "seed_reveal"));
        messages.Add("seed-reveal-guest", Single(pair.B, "seed_reveal"));
        Assert.Equal(T75ReviewProtocol.FairSeed, pair.Host.Battle.Seed);
        Assert.Equal(T75ReviewProtocol.FairSeed, pair.Guest.Battle.Record.Seed);
        pair.Act(BattleAction.Skill(0));
        string commit = Single(pair.B, "commit"), reveal = Single(pair.B, "reveal");
        string nonce = T75ReviewProtocol.Text(T75ReviewProtocol.Payload(reveal), "nonce");
        Assert.Matches("^[0-9a-f]{32}$", nonce);
        Assert.Equal(Digest("S0", nonce), T75ReviewProtocol.Text(T75ReviewProtocol.Payload(commit), "digest"));
        messages.Add("packet-commit", commit);
        messages.Add("packet-reveal", reveal);
        messages.Add("packet-hash", Single(pair.B, "hash"));
        Assert.Equal("42cc2dfa4dfd0972", pair.Host.Battle.StateHash().ToString("x16"));
        pair.Disconnect();
        pair.Attach();
        messages.Add("resume", Single(pair.A, "resume"));
        pair.Pump();
        T75ReviewProtocol.Healthy(pair.Host);
        T75ReviewProtocol.Healthy(pair.Guest);
        Assert.False(pair.Host.IsDisconnected || pair.Guest.IsDisconnected);
        messages.Add("resumed", Single(pair.B, "resumed"));

        var timed = new T75ReviewProtocol.Pair(turnSeconds: 10, bankSeconds: 30);
        messages.Add("hello-host-timer", Single(timed.A, "hello"));
        var termination = new T75ReviewProtocol.Pair(hostTermination: true, guestTermination: true);
        messages.Add("hello-host-termination", Single(termination.A, "hello"));
        messages.Add("hello-guest-termination", Single(termination.B, "hello"));
        termination.Host.OfferDraw();
        messages.Add("packet-termination-freeze", Single(termination.A, "termination-freeze"));
        var completed = new T75ReviewProtocol.Pair();
        completed.Host.Battle.Turn = Battle.TurnCap;
        completed.Guest.Battle.Turn = Battle.TurnCap;
        completed.Act(BattleAction.Pass());
        Assert.Equal(NetState.Over, completed.Guest.State);
        completed.Guest.PrepareClose();
        messages.Add("packet-goodbye", Single(completed.B, "goodbye"));

        Assert.Equal(Files.OrderBy(name => name, StringComparer.Ordinal), messages.Keys.OrderBy(name => name, StringComparer.Ordinal));
        foreach (string name in Files)
        {
            using var expected = JsonDocument.Parse(Golden(name));
            using var actual = JsonDocument.Parse(Normalize(messages[name]));
            EqualJson(expected.RootElement, actual.RootElement);
        }
    }

    [Fact]
    public void FrozenGuestPacketsAreAcceptedAndResolveExactlyTheFrozenRuleState()
    {
        T75ReviewProtocol.NewTransport(out var input, out _);
        var host = new NetSession(T75ReviewProtocol.FrozenContent(), input, true,
            T75ReviewProtocol.HostTeam, T75ReviewProtocol.HostName, 79, enableReconnect: true,
            seedContribution: T75ReviewProtocol.HostContribution);
        input.Incoming.Enqueue(Golden("hello-guest"));
        host.Poll();
        Assert.Equal(NetState.Handshake, host.State);
        Assert.Null(host.Battle);
        input.Incoming.Enqueue(Golden("seed-reveal-guest"));
        host.Poll();
        Assert.Equal(NetState.Choose, host.State);
        Assert.Equal(T75ReviewProtocol.FairSeed, host.Battle.Seed);
        host.Submit(BattleAction.Skill(0));
        foreach (string name in new[] { "packet-commit", "packet-reveal", "packet-hash" })
        {
            var envelope = T75ReviewProtocol.Object(Golden(name));
            envelope["sessionId"] = host.SessionId;
            input.Incoming.Enqueue(JsonSerializer.Serialize(envelope));
            host.Poll();
            T75ReviewProtocol.Healthy(host);
        }
        Assert.Equal(NetState.Choose, host.State);
        Assert.Equal("42cc2dfa4dfd0972", host.Battle.StateHash().ToString("x16"));
        var turn = Assert.Single(host.Battle.Record.Events);
        Assert.Equal("S0", turn.A0.ToWire());
        Assert.Equal("S0", turn.A1.ToWire());
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void SameVersionSixWithDifferentTerminationCapabilitiesCannotCreateABattle(bool hostTermination, bool guestTermination)
    {
        var pair = new T75ReviewProtocol.Pair(handshake: false,
            hostTermination: hostTermination, guestTermination: guestTermination);
        Assert.Equal(6, pair.Host.SessionProtocolVersion);
        Assert.Equal(6, pair.Guest.SessionProtocolVersion);

        pair.Pump();

        Assert.Equal(NetState.Error, pair.Host.State);
        Assert.Equal(NetState.Error, pair.Guest.State);
        Assert.Null(pair.Host.Battle);
        Assert.Null(pair.Guest.Battle);
        Assert.Contains("终止协议", pair.Host.Error);
        Assert.Contains("终止协议", pair.Guest.Error);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void SameVersionSixWithDifferentReconnectCapabilitiesCannotCreateABattle(bool hostReconnect, bool guestReconnect)
    {
        T75ReviewProtocol.NewTransport(out var a, out var b);
        var db = T75ReviewProtocol.FrozenContent();
        var host = new NetSession(db, a, true, T75ReviewProtocol.HostTeam, T75ReviewProtocol.HostName, 79,
            enableReconnect: hostReconnect, seedContribution: T75ReviewProtocol.HostContribution);
        var guest = new NetSession(db, b, false, T75ReviewProtocol.GuestTeam, "客机", null,
            enableReconnect: guestReconnect, seedContribution: T75ReviewProtocol.GuestContribution);
        Assert.Equal(6, host.SessionProtocolVersion);
        Assert.Equal(6, guest.SessionProtocolVersion);
        for (int index = 0; index < 12; index++) { host.Poll(); guest.Poll(); }
        foreach (var session in new[] { host, guest })
        {
            Assert.Equal(NetState.Error, session.State);
            Assert.Null(session.Battle);
            Assert.Contains("重连协议", session.Error);
        }
    }

}
