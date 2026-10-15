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
using System.Text.Json.Nodes;
using StreamDuel.Core;

internal static class Program
{
    const string BaselineCommit = "a5e3360147312ed7601f39e28dec51547e80f674";
    static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    static readonly string[] PackIds = { "original", "fan_asoul_vr", "original_wave2" };
    static readonly string[] Names = { "黄金甲\"\\一", "黄金乙" };
    static readonly List<object> Entries = new List<object>();
    static string outputDirectory;
    static string snapshotDirectory;

    static int Main(string[] args)
    {
        try
        {
            if (args.Length != 2 || args[0] != "--output")
                throw new ArgumentException("用法：dotnet run --project Tools/ReplayGolden -- --output <新的目录>");
            outputDirectory = Path.GetFullPath(args[1]);
            if (Directory.Exists(outputDirectory) || File.Exists(outputDirectory))
                throw new IOException("输出目录已经存在；黄金录像不能自动覆盖，请指定新的审查目录。");
            snapshotDirectory = Path.Combine(SourceRoot(), "Engine.Tests", "Golden", "replays", "content");
            Directory.CreateDirectory(Path.Combine(outputDirectory, "content"));
            var rules = CopySnapshot("StreamDuelRules.json");
            var packs = PackIds.Select(id => CopySnapshot(id + ".json")).ToArray();

            Save("ordinary-v2", "ordinary", Play("original", 9301), "normal-ai");
            Save("lead-v2", "lead", Play("original", 9302, lead: true), "normal-ai");
            Save("ban-lead-v2", "ban", Play("original", 9303, lead: true, ban: true), "normal-ai");
            Save("modifiers-v2", "modifiers", Play("original", 9304, lead: true, ban: true,
                modifiers: new[] { "half_pp", "hype_start", "sudden_death" }), "normal-ai");

            // 这里只生成三个独立完整对局，不替尚未合并的 Series 定义规则或容器。
            for (int game = 1; game <= 3; game++)
            {
                int attackingSide = game == 2 ? 1 : 0;
                var battle = Play("original", (uint)(9310 + game), attackingSide: attackingSide, mirror: true);
                if (battle.Winner != attackingSide) throw new InvalidOperationException("系列单局样例没有得到预定的交替胜方。");
                Save("series-game-" + game + "-v2", "series-game", battle, "manual-one-side-attacks",
                    seriesId: "same-teams-three-games", game: game);
            }

            Save("network-v2", "network", PlayNetwork(), "loopback-net-session");
            Save("fan-v2", "fan-pack", Play("fan_asoul_vr", 9321), "normal-ai");
            Save("wave2-v2", "wave2-pack", Play("original_wave2", 9322), "normal-ai");
            Save("legacy-v1", "legacy-v1", Play("original", 9323, manual: true,
                names: new[] { "你", "对手" }), "manual-no-extra-rng", legacy: true);

            WriteJson("manifest.json", new
            {
                schemaVersion = 1,
                baselineCommit = BaselineCommit,
                rules,
                packs,
                replays = Entries,
                series = new[]
                {
                    new
                    {
                        id = "same-teams-three-games",
                        coverage = "individual-games-only",
                        replayIds = new[] { "series-game-1-v2", "series-game-2-v2", "series-game-3-v2" },
                        winners = new[] { 0, 1, 0 },
                        limitation = "T-54 尚未合并；仅验证关联的三份独立对局记录，未验证 Series 容器、比分或赛制流程。"
                    }
                }
            });
            Console.WriteLine("已生成 " + Entries.Count + " 份完整黄金录像：" + outputDirectory);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    static string SourceRoot([CallerFilePath] string file = "")
    {
        return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file), "..", ".."));
    }

    static object CopySnapshot(string file)
    {
        byte[] bytes = File.ReadAllBytes(Path.Combine(snapshotDirectory, file));
        File.WriteAllBytes(Path.Combine(outputDirectory, "content", file), bytes);
        return new { file = "content/" + file, sha256 = Hash(bytes) };
    }

    static ContentDb Load(string pack)
    {
        return ContentDb.FromJson(File.ReadAllText(Path.Combine(snapshotDirectory, "StreamDuelRules.json")),
            File.ReadAllText(Path.Combine(snapshotDirectory, pack + ".json")));
    }

    static IList<string>[] Teams(ContentDb db, bool mirror = false)
    {
        var ids = db.Streamers.Select(streamer => streamer.Id).ToArray();
        return new IList<string>[]
        {
            ids.Take(6).ToArray(),
            mirror ? ids.Take(6).ToArray() : ids.Reverse().Take(6).ToArray()
        };
    }

    static Battle Play(string pack, uint seed, bool lead = false, bool ban = false,
        string[] modifiers = null, bool manual = false, int attackingSide = -1, bool mirror = false,
        string[] names = null)
    {
        var db = Load(pack);
        var battle = new Battle(db, Teams(db, mirror), names ?? Names, new BattleOptions
        {
            Seed = seed, LeadPick = lead, BanOne = ban, Modifiers = modifiers?.ToList()
        });
        if (ban)
        {
            battle.ChooseBan(1, 0);
            battle.ChooseBan(0, 5);
        }
        if (lead)
        {
            battle.ChooseLead(1, 2);
            battle.ChooseLead(0, 1);
        }
        for (int step = 0; step < 1000 && battle.Phase != Phase.Over; step++)
        {
            if (battle.Phase == Phase.Choose)
            {
                var choices = new BattleAction[2];
                // 冻结样例的策略输入，避免当前默认调参改变历史录像的生成结果。
                for (int side = 0; side < 2; side++)
                    choices[side] = attackingSide >= 0 && side != attackingSide ? BattleAction.Pass()
                        : manual || attackingSide >= 0 ? ManualAction(battle, side)
                        : Ai.ChooseWithWeights(battle, side, AiLevel.Normal, AiWeights.Legacy);
                battle.ResolveTurn(choices[0], choices[1]);
            }
            else if (battle.Phase == Phase.Replace)
            {
                for (int side = 0; side < 2 && battle.Phase == Phase.Replace; side++)
                    if (battle.Need[side]) battle.Replace(side,
                        manual || attackingSide >= 0 ? ManualReplacement(battle, side)
                        : Ai.ChooseReplacementWithWeights(battle, side, AiLevel.Normal, AiWeights.Legacy));
            }
            else throw new InvalidOperationException("生成器遇到尚未处理的对战阶段：" + battle.Phase);
        }
        RequireFinished(battle);
        return battle;
    }

    static BattleAction ManualAction(Battle battle, int side)
    {
        var legal = battle.LegalFor(side);
        if (legal.Controlled != null || legal.Struggle) return BattleAction.Pass();
        var skill = legal.Skills.Where(option => option.Usable)
            .OrderByDescending(option => battle.Db.Skills[option.Id].Power).ThenBy(option => option.Index).FirstOrDefault();
        return skill == null ? BattleAction.Pass() : BattleAction.Skill(skill.Index);
    }

    static int ManualReplacement(Battle battle, int side)
    {
        for (int i = 0; i < battle.Sides[side].Team.Count; i++)
            if (i != battle.Sides[side].Active && !battle.Sides[side].Team[i].Fainted && battle.Sides[side].Team[i].Hp > 0)
                return i;
        throw new InvalidOperationException("没有合法的补位候选。");
    }

    static Battle PlayNetwork()
    {
        var db = Load("original");
        var teams = Teams(db);
        LoopbackPair.Create(out var hostTransport, out var guestTransport);
        var host = new NetSession(db, hostTransport, true, teams[0], Names[0], 9320);
        var guest = new NetSession(Load("original"), guestTransport, false, teams[1], Names[1], null);
        for (int step = 0; step < 5000; step++)
        {
            host.Poll();
            guest.Poll();
            foreach (var peer in new[] { host, guest })
            {
                if (peer.State == NetState.Error || peer.State == NetState.Desync)
                    throw new InvalidOperationException("联网黄金录像生成失败：" + peer.Error);
                if (peer.State == NetState.Choose && !peer.HasSubmitted)
                    peer.Submit(ManualAction(peer.Battle, peer.MySide));
                else if (peer.State == NetState.Replace && !peer.HasSubmitted)
                    peer.SubmitReplace(ManualReplacement(peer.Battle, peer.MySide));
            }
            if (host.State == NetState.Over && guest.State == NetState.Over)
            {
                RequireFinished(host.Battle);
                RequireFinished(guest.Battle);
                if (host.Battle.StateHash() != guest.Battle.StateHash()
                    || host.Battle.Record.ToJson() != guest.Battle.Record.ToJson())
                    throw new InvalidOperationException("联网双方的最终状态或录像不同。");
                return host.Battle;
            }
        }
        throw new InvalidOperationException("联网黄金录像未在限制内完成。");
    }

    static void Save(string id, string category, Battle battle, string policy, bool legacy = false,
        string seriesId = null, int? game = null)
    {
        string json = battle.Record.ToJson();
        if (legacy)
        {
            // 用真实手动行动对局去掉后来增加的字段，不修改任何行动、结果或随机流。
            var root = JsonNode.Parse(json).AsObject();
            foreach (string key in new[] { "version", "contentHash", "names" }) root.Remove(key);
            foreach (var item in root["events"].AsArray()) item.AsObject().Remove("rng");
            json = root.ToJsonString(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        }
        var record = BattleRecord.FromJson(json);
        var replay = Replay.Run(battle.Db, record);
        RequireFinished(replay);
        if (replay.StateHash() != battle.StateHash()) throw new InvalidOperationException("生成后立即回放不一致：" + id);
        string file = id + ".json";
        byte[] bytes = new UTF8Encoding(false).GetBytes(json + "\n");
        File.WriteAllBytes(Path.Combine(outputDirectory, file), bytes);
        Entries.Add(new
        {
            id, file, category, policy, seriesId, game,
            sha256 = Hash(bytes),
            recordVersion = record.Version,
            packId = record.PackId,
            contentHash = battle.Db.ContentHash(),
            seed = record.Seed,
            finalStateHash = battle.StateHash().ToString("x16", CultureInfo.InvariantCulture),
            winner = battle.Winner,
            eventCount = record.Events.Count,
            turnCount = record.Events.Count(item => item.Type == BattleRecordEventType.Turn),
            networkProtocolVersion = category == "network" ? (int?)NetSession.ProtocolVersion : null,
            peerHashesVerified = category == "network"
        });
        Console.WriteLine(id + "：seed=" + record.Seed + "，胜方=" + battle.Winner + "，hash="
            + battle.StateHash().ToString("x16", CultureInfo.InvariantCulture));
    }

    static void RequireFinished(Battle battle)
    {
        if (battle.Phase != Phase.Over || battle.Record.Events.Count == 0)
            throw new InvalidOperationException("黄金录像必须来自真实完成的对局。");
    }

    static string Hash(byte[] bytes)
    {
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    static void WriteJson(string file, object value)
    {
        File.WriteAllText(Path.Combine(outputDirectory, file), JsonSerializer.Serialize(value, JsonOptions).Replace("\r\n", "\n") + "\n",
            new UTF8Encoding(false));
    }
}
