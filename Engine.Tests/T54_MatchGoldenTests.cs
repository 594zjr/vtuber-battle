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

public class T54_MatchGoldenTests
{
    const string ManifestSha256 = "64513330ce1e9aad23b634a4434c6099002697f4ff0ff5e97247345b23b30430";
    const string RulesetJson = "{\"id\":\"ranked\",\"name\":\"排位\",\"season\":null,\"banOne\":true,\"leadPick\":true,\"bestOf\":3,\"modifiers\":[],\"turnSeconds\":10,\"bankSeconds\":30}";

    static string DirectoryPath([CallerFilePath] string file = "")
    {
        return Path.Combine(Path.GetDirectoryName(file), "Golden", "match");
    }

    static string PathFor(string file)
    {
        return Path.Combine(DirectoryPath(), file.Replace('/', Path.DirectorySeparatorChar));
    }

    static string Sha256(byte[] bytes)
    {
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    static string StateHash(Battle battle)
    {
        return battle.StateHash().ToString("x16", CultureInfo.InvariantCulture);
    }

    static JsonDocument Manifest()
    {
        byte[] bytes = File.ReadAllBytes(PathFor("manifest.json"));
        Assert.Equal(ManifestSha256, Sha256(bytes));
        return JsonDocument.Parse(bytes);
    }

    static ContentDb Load(JsonElement manifest)
    {
        var rules = manifest.GetProperty("rules");
        var pack = manifest.GetProperty("pack");
        Assert.Equal("../replays/content/StreamDuelRules.json", rules.GetProperty("file").GetString());
        Assert.Equal("../replays/content/original.json", pack.GetProperty("file").GetString());
        byte[] rulesBytes = File.ReadAllBytes(PathFor(rules.GetProperty("file").GetString()));
        byte[] packBytes = File.ReadAllBytes(PathFor(pack.GetProperty("file").GetString()));
        Assert.Equal(rules.GetProperty("sha256").GetString(), Sha256(rulesBytes));
        Assert.Equal(pack.GetProperty("sha256").GetString(), Sha256(packBytes));
        var db = ContentDb.FromJson(Encoding.UTF8.GetString(rulesBytes), Encoding.UTF8.GetString(packBytes));
        db.Validate();
        Assert.Equal(manifest.GetProperty("contentHash").GetString(), db.ContentHash());
        return db;
    }

    static IList<string>[] Teams(JsonElement manifest)
    {
        return manifest.GetProperty("teams").EnumerateArray()
            .Select(team => (IList<string>)team.EnumerateArray().Select(id => id.GetString()).ToList()).ToArray();
    }

    static string[] Names(JsonElement manifest)
    {
        return manifest.GetProperty("names").EnumerateArray().Select(name => name.GetString()).ToArray();
    }

    [Fact]
    public void FrozenMatchManifestIdentifiesRealSeriesAndReusesUnchangedContent()
    {
        using var document = Manifest();
        var manifest = document.RootElement;
        Assert.Equal(1, manifest.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("a5e3360147312ed7601f39e28dec51547e80f674", manifest.GetProperty("contentBaselineCommit").GetString());
        Assert.Equal("72b76c923dd6a110d6faa0f23ad3cb04142594a5", manifest.GetProperty("integrationBaseCommit").GetString());
        Assert.Equal("7ff081f45014babfe7ae61cfca5956befc491788", manifest.GetProperty("featureCommit").GetString());
        Assert.Equal("core-match-best-of-three", manifest.GetProperty("coverage").GetString());
        Assert.Equal("manual-one-side-attacks-no-state-edits", manifest.GetProperty("policy").GetString());
        Assert.Equal(RulesetJson, Ruleset.FromJson(manifest.GetProperty("ruleset").GetRawText()).ToJson());
        Assert.Equal(RulesetJson, Rulesets.All.Single(rule => rule.Id == "ranked").ToJson());
        Assert.Equal(540001u, manifest.GetProperty("seed").GetUInt32());
        Assert.Equal(new[] { "manifest.json", "ranked-game-1-v2.json", "ranked-game-2-v2.json", "ranked-game-3-v2.json" },
            Directory.GetFiles(DirectoryPath(), "*.json").Select(Path.GetFileName).OrderBy(file => file, StringComparer.Ordinal));
        Assert.Empty(Directory.GetDirectories(DirectoryPath()));
        var entries = manifest.GetProperty("games").EnumerateArray().ToArray();
        Assert.Equal(new[] { 1, 2, 3 }, entries.Select(entry => entry.GetProperty("game").GetInt32()));
        Assert.Equal(new[] { 540001u, 540002u, 540003u }, entries.Select(entry => entry.GetProperty("seed").GetUInt32()));
        Assert.Equal(new[] { 0, 1, 0 }, entries.Select(entry => entry.GetProperty("winner").GetInt32()));
        Assert.Equal(new[] { false, false, true }, entries.Select(entry => entry.GetProperty("over").GetBoolean()));
        Assert.Equal(new[] { 2, 1 }, manifest.GetProperty("finalWins").EnumerateArray().Select(value => value.GetInt32()));
        Assert.Equal(0, manifest.GetProperty("finalDraws").GetInt32());
        Assert.True(manifest.GetProperty("over").GetBoolean());
        Load(manifest);
    }

    [Fact]
    public void FrozenInputsAdvanceTheCurrentMatchGamesAndRetainOriginalScoresSeedsAndHashes()
    {
        using var document = Manifest();
        var manifest = document.RootElement;
        var db = Load(manifest);
        var teams = Teams(manifest);
        var names = Names(manifest);
        var match = new Match(db, Ruleset.FromJson(RulesetJson), teams, names, manifest.GetProperty("seed").GetUInt32());
        Battle previous = null;
        Fighter[] previousFighters = null;
        int[][] scores = { new[] { 1, 0 }, new[] { 1, 1 }, new[] { 2, 1 } };
        foreach (var entry in manifest.GetProperty("games").EnumerateArray())
        {
            int game = entry.GetProperty("game").GetInt32();
            byte[] bytes = File.ReadAllBytes(PathFor(entry.GetProperty("file").GetString()));
            Assert.Equal(entry.GetProperty("sha256").GetString(), Sha256(bytes));
            var record = BattleRecord.FromJson(Encoding.UTF8.GetString(bytes));
            Assert.Equal(2, record.Version);
            Assert.Equal(entry.GetProperty("recordVersion").GetInt32(), record.Version);
            Assert.Equal(entry.GetProperty("seed").GetUInt32(), record.Seed);
            Assert.Equal(entry.GetProperty("eventCount").GetInt32(), record.Events.Count);
            Assert.Equal(entry.GetProperty("turnCount").GetInt32(), record.Events.Count(item => item.Type == BattleRecordEventType.Turn));
            Assert.InRange(entry.GetProperty("turnCount").GetInt32(), 1, Battle.TurnCap);
            Assert.Equal(db.ContentHash(), record.ContentHash);
            Assert.Equal(db.PackId, record.PackId);
            Assert.Equal(names, record.Names);
            Assert.True(record.BanOne);
            Assert.True(record.LeadPick);
            Assert.True(record.Modifiers == null || record.Modifiers.Count == 0);
            Assert.Equal(new[] { BattleRecordEventType.Ban, BattleRecordEventType.Ban, BattleRecordEventType.Lead, BattleRecordEventType.Lead },
                record.Events.Take(4).Select(item => item.Type));
            Assert.Equal(new[] { 1, 0, 1, 0 }, record.Events.Take(4).Select(item => item.Side));
            Assert.Equal(new[] { 5, 5, 1, 1 }, record.Events.Take(4).Select(item => item.To));
            Assert.All(record.Events, item => Assert.True(item.RngState.HasValue));

            Assert.False(match.Over);
            var battle = match.StartNextGame();
            Assert.NotSame(previous, battle);
            Assert.Equal(record.Seed, battle.Seed);
            Assert.Equal(entry.GetProperty("initialStateHash").GetString(), StateHash(battle));
            AssertFresh(battle, teams, names, previousFighters);
            var freshFighters = battle.Sides.SelectMany(side => side.Team).ToArray();
            Assert.Throws<InvalidOperationException>(() => match.StartNextGame());
            Assert.Throws<InvalidOperationException>(() => match.ReportResult(battle));
            foreach (var item in record.Events) ApplyInput(battle, item);
            Assert.Equal(Phase.Over, battle.Phase);
            Assert.Equal(entry.GetProperty("winner").GetInt32(), battle.Winner);
            Assert.Equal(entry.GetProperty("finalStateHash").GetString(), StateHash(battle));
            Assert.Equal(record.ToJson(), battle.Record.ToJson());
            Assert.All(battle.Sides, side => Assert.Equal(5, side.Team.Count));
            Assert.Contains(battle.Sides.SelectMany(side => side.Team), fighter => fighter.Fainted);
            Assert.Contains(battle.Sides.SelectMany(side => side.Team).SelectMany(fighter => fighter.Skills), skill => skill.Pp < skill.Max);

            var compatibility = Replay.Check(db, record);
            Assert.True(compatibility.CanReplay, compatibility.Reason);
            var replay = Replay.Run(db, record);
            Assert.Equal(battle.StateHash(), replay.StateHash());
            Assert.Equal(battle.Log.Select(line => line.Node + "|" + line.Text), replay.Log.Select(line => line.Node + "|" + line.Text));
            // 独立回放只作交叉核对，Match 必须拒绝外来实例，实际计分仍来自当前局。
            Assert.Throws<InvalidOperationException>(() => match.ReportResult(replay));
            match.ReportResult(battle);
            Assert.Equal(scores[game - 1], match.Wins);
            Assert.Equal(entry.GetProperty("wins").EnumerateArray().Select(value => value.GetInt32()), match.Wins);
            Assert.Equal(entry.GetProperty("draws").GetInt32(), match.Draws);
            Assert.Equal(entry.GetProperty("over").GetBoolean(), match.Over);
            Assert.Throws<InvalidOperationException>(() => match.ReportResult(battle));
            Assert.Equal(scores[game - 1], match.Wins);
            Assert.Equal(0, match.Draws);
            previous = battle;
            previousFighters = freshFighters;
        }
        Assert.True(match.Over);
        Assert.Equal(new[] { 2, 1 }, match.Wins);
        Assert.Equal(0, match.Draws);
        Assert.Throws<InvalidOperationException>(() => match.StartNextGame());
    }

    static void AssertFresh(Battle battle, IList<string>[] teams, string[] names, Fighter[] previous)
    {
        Assert.Equal(Phase.Ban, battle.Phase);
        Assert.Equal(1, battle.Turn);
        Assert.Equal(-2, battle.Winner);
        Assert.Empty(battle.Record.Events);
        Assert.Empty(battle.Log);
        Assert.All(battle.Need, needed => Assert.False(needed));
        for (int side = 0; side < 2; side++)
        {
            Assert.False(battle.HasChosenBan(side));
            Assert.False(battle.HasChosenLead(side));
            Assert.Equal(0, battle.Sides[side].Active);
            Assert.Equal(names[side], battle.Sides[side].Name);
            Assert.Equal(6, battle.Sides[side].Team.Count);
            Assert.Equal(teams[side], battle.Sides[side].Team.Select(fighter => fighter.Def.Id));
            Assert.Equal(teams[side], battle.Record.Teams[side]);
            foreach (var fighter in battle.Sides[side].Team)
            {
                if (previous != null) Assert.DoesNotContain(previous, old => ReferenceEquals(old, fighter));
                Assert.Equal(fighter.MaxHp, fighter.Hp);
                Assert.False(fighter.Fainted);
                Assert.False(fighter.FirstControlImmunityUsed);
                Assert.False(fighter.EndureUsed);
                Assert.Equal(-1, fighter.LastSkillIndex);
                Assert.All(fighter.Skills, skill => Assert.Equal(skill.Max, skill.Pp));
                Assert.Empty(fighter.Statuses);
                Assert.Empty(fighter.Fields);
                Assert.All(fighter.Stages.Values, stage => Assert.Equal(0, stage));
            }
        }
    }

    static void ApplyInput(Battle battle, BattleRecordEvent input)
    {
        int previousCount = battle.Record.Events.Count;
        // 手动行动不额外消耗随机数，逐事件核对 RNG 检查点即可；测试不恢复或篡改当前局随机状态。
        switch (input.Type)
        {
            case BattleRecordEventType.Ban: battle.ChooseBan(input.Side, input.To); break;
            case BattleRecordEventType.Lead: battle.ChooseLead(input.Side, input.To); break;
            case BattleRecordEventType.Turn: battle.ResolveTurn(input.A0, input.A1); break;
            case BattleRecordEventType.Replace: battle.Replace(input.Side, input.To); break;
            default: throw new InvalidOperationException("此黄金样例不得引入其他事件类型：" + input.Type);
        }
        Assert.Equal(previousCount + 1, battle.Record.Events.Count);
        Assert.Equal(input.RngState, battle.Record.Events.Last().RngState);
    }
}
