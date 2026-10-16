using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using StreamDuel.Core;

internal static class Program
{
    const string ContentBaselineCommit = "a5e3360147312ed7601f39e28dec51547e80f674";
    const string IntegrationBaseCommit = "72b76c923dd6a110d6faa0f23ad3cb04142594a5";
    const string FeatureCommit = "7ff081f45014babfe7ae61cfca5956befc491788";
    const string RulesSha = "75a259d99a4666c2b2699fddd316f327aa73eb7f1a7157ea5af442f01070e59d";
    const string PackSha = "770c2b6880cc72e3fe23dfe9e9a7c96082ee2ae26aec2f7be2ee6d58a5b54e8e";
    const uint Seed = 540001;
    const string RulesetJson = "{\"id\":\"ranked\",\"name\":\"排位\",\"season\":null,\"banOne\":true,\"leadPick\":true,\"bestOf\":3,\"modifiers\":[],\"turnSeconds\":10,\"bankSeconds\":30}";

    static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    static int Main(string[] args)
    {
        try
        {
            if (args.Length != 2 || args[0] != "--output")
                throw new ArgumentException("用法：dotnet run --project Tools/MatchGolden -- --output <新的目录>");
            string output = Path.GetFullPath(args[1]);
            if (Directory.Exists(output) || File.Exists(output))
                throw new IOException("输出目录已经存在；黄金录像不能自动覆盖，请指定新的审查目录。");
            string content = Path.Combine(SourceRoot(), "Engine.Tests", "Golden", "replays", "content");
            byte[] rulesBytes = File.ReadAllBytes(Path.Combine(content, "StreamDuelRules.json"));
            byte[] packBytes = File.ReadAllBytes(Path.Combine(content, "original.json"));
            if (Sha256(rulesBytes) != RulesSha || Sha256(packBytes) != PackSha)
                throw new InvalidOperationException("原有冻结内容的哈希不匹配，不能生成黄金录像。");
            var db = ContentDb.FromJson(Encoding.UTF8.GetString(rulesBytes), Encoding.UTF8.GetString(packBytes));
            db.Validate();
            var ruleset = Ruleset.FromJson(RulesetJson);
            var team = db.Streamers.Take(6).Select(streamer => streamer.Id).ToArray();
            var teams = new IList<string>[] { team.ToArray(), team.ToArray() };
            var names = new[] { "多局黄金甲", "多局黄金乙" };
            var match = new Match(db, ruleset, teams, names, Seed);
            var entries = new List<object>();
            Directory.CreateDirectory(output);
            // 字节哈希跨 Windows/Linux 一致，不能让 Git 的自动换行转换改写冻结文件。
            File.WriteAllText(Path.Combine(output, ".gitattributes"), "*.json text eol=lf\n", new UTF8Encoding(false));
            for (int game = 1; game <= 3; game++)
            {
                // 只通过 Match 开局并上报同一实例，不能把独立 Battle 的结果冒充多局流程。
                var battle = match.StartNextGame();
                string initialHash = StateHash(battle);
                int expectedWinner = game == 2 ? 1 : 0;
                Play(battle, expectedWinner);
                if (battle.Phase != Phase.Over || battle.Winner != expectedWinner)
                    throw new InvalidOperationException("真实多局样例未得到预定胜方：第 " + game + " 局。");
                string json = battle.Record.ToJson();
                var replay = Replay.Run(db, BattleRecord.FromJson(json));
                if (replay.StateHash() != battle.StateHash())
                    throw new InvalidOperationException("第 " + game + " 局独立回放与比赛当前局不一致。");
                match.ReportResult(battle);
                string file = "ranked-game-" + game + "-v2.json";
                byte[] bytes = new UTF8Encoding(false).GetBytes(json + "\n");
                File.WriteAllBytes(Path.Combine(output, file), bytes);
                entries.Add(new
                {
                    game, file, sha256 = Sha256(bytes), seed = battle.Seed,
                    recordVersion = battle.Record.Version, initialStateHash = initialHash,
                    finalStateHash = StateHash(battle), winner = battle.Winner,
                    eventCount = battle.Record.Events.Count,
                    turnCount = battle.Record.Events.Count(entry => entry.Type == BattleRecordEventType.Turn),
                    wins = match.Wins, draws = match.Draws, over = match.Over
                });
                Console.WriteLine(file + "：seed=" + battle.Seed + "，胜方=" + battle.Winner
                    + "，比分=" + string.Join(":", match.Wins) + "，hash=" + StateHash(battle));
            }
            if (!match.Over || !match.Wins.SequenceEqual(new[] { 2, 1 }) || match.Draws != 0)
                throw new InvalidOperationException("三局两胜没有正确结束。");
            using var rulesetDocument = JsonDocument.Parse(RulesetJson);
            WriteJson(Path.Combine(output, "manifest.json"), new
            {
                schemaVersion = 1,
                contentBaselineCommit = ContentBaselineCommit,
                integrationBaseCommit = IntegrationBaseCommit,
                featureCommit = FeatureCommit,
                coverage = "core-match-best-of-three",
                policy = "manual-one-side-attacks-no-state-edits",
                rules = new { file = "../replays/content/StreamDuelRules.json", sha256 = RulesSha },
                pack = new { file = "../replays/content/original.json", sha256 = PackSha },
                contentHash = db.ContentHash(), seed = Seed, ruleset = rulesetDocument.RootElement,
                teams, names, games = entries, finalWins = match.Wins, finalDraws = match.Draws, over = match.Over
            });
            Console.WriteLine("manifest SHA256=" + Sha256(File.ReadAllBytes(Path.Combine(output, "manifest.json"))));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    static void Play(Battle battle, int attackingSide)
    {
        battle.ChooseBan(1, 5);
        battle.ChooseBan(0, 5);
        battle.ChooseLead(1, 1);
        battle.ChooseLead(0, 1);
        for (int step = 0; step < 1000 && battle.Phase != Phase.Over; step++)
        {
            if (battle.Phase == Phase.Choose)
            {
                var choices = new[] { BattleAction.Pass(), BattleAction.Pass() };
                var legal = battle.LegalFor(attackingSide);
                if (legal.Controlled == null && !legal.Struggle)
                {
                    var skill = legal.Skills.Where(option => option.Usable)
                        .OrderByDescending(option => battle.Db.Skills[option.Id].Power)
                        .ThenBy(option => option.Index).FirstOrDefault();
                    if (skill != null) choices[attackingSide] = BattleAction.Skill(skill.Index);
                }
                battle.ResolveTurn(choices[0], choices[1]);
            }
            else if (battle.Phase == Phase.Replace)
            {
                for (int side = 0; side < 2 && battle.Phase == Phase.Replace; side++)
                    if (battle.Need[side])
                    {
                        int replacement = Enumerable.Range(0, battle.Sides[side].Team.Count)
                            .First(index => index != battle.Sides[side].Active
                                && !battle.Sides[side].Team[index].Fainted && battle.Sides[side].Team[index].Hp > 0);
                        battle.Replace(side, replacement);
                    }
            }
            else throw new InvalidOperationException("生成器遇到尚未处理的对战阶段：" + battle.Phase);
        }
    }

    static string SourceRoot([CallerFilePath] string file = "")
    {
        return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file), "..", ".."));
    }

    static string StateHash(Battle battle)
    {
        return battle.StateHash().ToString("x16", CultureInfo.InvariantCulture);
    }

    static string Sha256(byte[] bytes)
    {
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    static void WriteJson(string file, object value)
    {
        File.WriteAllText(file, JsonSerializer.Serialize(value, JsonOptions).Replace("\r\n", "\n") + "\n", new UTF8Encoding(false));
    }
}
