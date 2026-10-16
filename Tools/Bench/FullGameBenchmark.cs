using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StreamDuel.Core;

namespace StreamDuel.Bench
{
    internal static class FullGameBenchmark
    {
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
        static readonly JsonSerializerOptions Json = new JsonSerializerOptions { IncludeFields = true };
        const string Protocol = "T117-full-games-v1";

        internal sealed class Measurement
        {
            public int CaseId, Turns;
            public double Milliseconds, ConstructionMs, DecisionMs, ResolutionMs;
            public long AllocatedBytes, ConstructionBytes, DecisionBytes, ResolutionBytes;
            public long CloneCount, HardCopyCount, SimulationCloneCount;
        }

        internal sealed class Group
        {
            public string PackId, Level;
            public int Games, Turns;
            public double MeanMsPerGame, P95MsPerGame, MaxMsPerGame, AllocatedBytesPerTurn;
            public long AllocatedBytes, CloneCount, HardCopyCount, SimulationCloneCount;
            public double MeanConstructionMs, MeanDecisionMs, MeanResolutionMs;
            public long ConstructionBytes, DecisionBytes, ResolutionBytes;
        }

        internal sealed class Outcome
        {
            public int CaseId, Winner, Turns;
            public string StateHash, LogSha256, RecordSha256;
        }

        public static int Run(string[] args)
        {
            var options = FullGameOptions.Parse(args);
            if (options.Help)
            {
                Console.WriteLine("用法：dotnet run -c Release --project Tools/Bench -- --full-games --out bench_out/t117-before [--compare bench_out/基线]");
                Console.WriteLine("四包 × 普通/困难/专家各 200 局，单线程完整结算；--games 1..200 仅用于缩小 smoke，正式默认共 2400 局。");
                Console.WriteLine("分别保存 schedule/results、逐局完整 log/record/state 字节及 measurement.json；拒绝覆盖非空目录。--compare 对全部确定性文件逐字节核对。");
                Console.WriteLine("--verify-only --out 已完成优化后目录 --compare 已完成优化前目录：不重跑，严格核验 2400 局和全部 7200 个逐局文件。");
                return 0;
            }
            if (options.VerifyOnly) return FullGameVerification.Run(options.Output, options.Baseline);
#if DEBUG
            throw new InvalidOperationException("整局性能基准必须使用 Release 构建。");
#else
            string output = Path.GetFullPath(options.Output);
            string baseline = options.Baseline == null ? null : Path.GetFullPath(options.Baseline);
            if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
                throw new InvalidOperationException("整局基准拒绝覆盖非空目录：" + output);
            if (baseline != null && !File.Exists(Path.Combine(baseline, "results.json")))
                throw new ArgumentException("比较基线缺少完整 results.json：" + baseline);
            var packs = LoadPacks();
            var schedule = new List<FullGameCase>();
            for (int pack = 0; pack < packs.Count; pack++)
                for (int level = 0; level < FullGameWorkload.Levels.Length; level++)
                    for (int game = 0; game < options.Games; game++) schedule.Add(FullGameWorkload.Create(packs[pack], pack, level, game));
            Directory.CreateDirectory(Path.Combine(output, "games"));
            var scheduleBytes = JsonBytes(new { Protocol, GamesPerGroup = options.Games,
                Names = FullGameWorkload.Names, SelectionOrder = "side0-then-side1", ReplacementOrder = "side0-then-side1",
                ExpertMaxNodes = ExpertBudget.DefaultMaxNodes,
                Content = packs.Select(db => new { db.PackId, Hash = db.ContentHash() }).ToArray(), Cases = schedule });
            SaveAndCompare(output, baseline, "schedule.json", scheduleBytes);
            var measurements = new List<Measurement>();
            var groups = new List<Group>();
            var outcomes = new List<Outcome>();
            var elapsed = Stopwatch.StartNew();
            foreach (var group in schedule.GroupBy(item => new { item.PackId, item.Level }))
            {
                var db = packs.Single(pack => pack.PackId == group.Key.PackId);
                var level = (AiLevel)Enum.Parse(typeof(AiLevel), group.Key.Level);
                var first = group.First();
                Play(db, first, level, out _);
                var groupMeasurements = new List<Measurement>();
                foreach (var fixture in group)
                {
                    var battle = Play(db, fixture, level, out var measurement);
                    string prefix = "games/" + fixture.Id.ToString("D4", CultureInfo.InvariantCulture);
                    byte[] state = Utf8.GetBytes(battle.StateHash().ToString("x16", CultureInfo.InvariantCulture) + "\n");
                    byte[] log = JsonBytes(battle.Log);
                    byte[] record = Utf8.GetBytes(battle.Record.ToJson() + "\n");
                    SaveAndCompare(output, baseline, prefix + ".state.txt", state);
                    SaveAndCompare(output, baseline, prefix + ".log.json", log);
                    SaveAndCompare(output, baseline, prefix + ".record.json", record);
                    outcomes.Add(new Outcome { CaseId = fixture.Id, Winner = battle.Winner, Turns = measurement.Turns,
                        StateHash = battle.StateHash().ToString("x16", CultureInfo.InvariantCulture), LogSha256 = Hash(log), RecordSha256 = Hash(record) });
                    measurements.Add(measurement);
                    groupMeasurements.Add(measurement);
                    if ((fixture.GameIndex + 1) % 25 == 0 || fixture.GameIndex + 1 == options.Games)
                        Console.WriteLine(fixture.PackId + " / " + fixture.Level + "：" + (fixture.GameIndex + 1) + "/" + options.Games);
                }
                var summary = Summarize(group.Key.PackId, group.Key.Level, groupMeasurements);
                groups.Add(summary);
                Print(summary);
                WriteMeasurements(output, options, packs, measurements, groups, elapsed.Elapsed.TotalSeconds, false);
            }
            elapsed.Stop();
            SaveAndCompare(output, baseline, "results.json", JsonBytes(new { Protocol, Games = outcomes.Count, Outcomes = outcomes }));
            WriteMeasurements(output, options, packs, measurements, groups, elapsed.Elapsed.TotalSeconds, true);
            Console.WriteLine("完成 " + outcomes.Count + " 局；" + (baseline == null ? "已保存优化前后可比的完整字节。" : "全部 schedule/state/log/record/results 字节与基线一致。"));
            Console.WriteLine("输出：" + output);
            return 0;
#endif
        }

        static List<ContentDb> LoadPacks()
        {
            string directory = Path.Combine(AppContext.BaseDirectory, "Content");
            string rules = File.ReadAllText(Path.Combine(directory, "StreamDuelRules.json"));
            var policy = PackPolicy.Load(Path.Combine(directory, "PackPolicy.json"));
            var result = new List<ContentDb>();
            foreach (string id in FullGameWorkload.PackIds)
            {
                var loaded = PackLoader.LoadJson(rules, File.ReadAllText(Path.Combine(directory, "Packs", id + ".json")), id, policy);
                if (loaded.Db == null) throw new InvalidOperationException("整局基准需要全部四包：" + loaded.Error);
                if (loaded.Db.PackId != id) throw new InvalidOperationException("内容包 id 与固定基准不符：" + id);
                result.Add(loaded.Db);
            }
            return result;
        }

        static Battle Play(ContentDb db, FullGameCase fixture, AiLevel level, out Measurement measurement)
        {
            measurement = new Measurement { CaseId = fixture.Id };
            long clones = BattleDiagnostics.CurrentThreadCloneCount;
            long hardCopies = BattleDiagnostics.CurrentThreadHardCopyCount;
            long simulationClones = BattleDiagnostics.CurrentThreadSimulationCloneCount;
            long bytes = GC.GetAllocatedBytesForCurrentThread();
            long start = Stopwatch.GetTimestamp();
            var battle = new Battle(db, new IList<string>[] { fixture.Teams[0], fixture.Teams[1] }, FullGameWorkload.Names, fixture.Seed);
            measurement.ConstructionMs = MillisecondsSince(start);
            measurement.ConstructionBytes = GC.GetAllocatedBytesForCurrentThread() - bytes;
            int steps = 0;
            while (battle.Phase != Phase.Over)
            {
                if (++steps > Battle.TurnCap * 4 + 32) throw new InvalidOperationException("整局基准未结束，case " + fixture.Id);
                if (battle.Phase == Phase.Choose)
                {
                    long operationBytes = GC.GetAllocatedBytesForCurrentThread();
                    long operationStart = Stopwatch.GetTimestamp();
                    var first = Ai.Choose(battle, 0, level);
                    var second = Ai.Choose(battle, 1, level);
                    measurement.DecisionMs += MillisecondsSince(operationStart);
                    measurement.DecisionBytes += GC.GetAllocatedBytesForCurrentThread() - operationBytes;
                    operationBytes = GC.GetAllocatedBytesForCurrentThread();
                    operationStart = Stopwatch.GetTimestamp();
                    battle.ResolveTurn(first, second);
                    measurement.ResolutionMs += MillisecondsSince(operationStart);
                    measurement.ResolutionBytes += GC.GetAllocatedBytesForCurrentThread() - operationBytes;
                    measurement.Turns++;
                }
                else if (battle.Phase == Phase.Replace)
                {
                    for (int side = 0; side < 2 && battle.Phase == Phase.Replace; side++)
                    {
                        if (!battle.Need[side]) continue;
                        long operationBytes = GC.GetAllocatedBytesForCurrentThread();
                        long operationStart = Stopwatch.GetTimestamp();
                        int replacement = Ai.ChooseReplacement(battle, side, level);
                        measurement.DecisionMs += MillisecondsSince(operationStart);
                        measurement.DecisionBytes += GC.GetAllocatedBytesForCurrentThread() - operationBytes;
                        operationBytes = GC.GetAllocatedBytesForCurrentThread();
                        operationStart = Stopwatch.GetTimestamp();
                        battle.Replace(side, replacement);
                        measurement.ResolutionMs += MillisecondsSince(operationStart);
                        measurement.ResolutionBytes += GC.GetAllocatedBytesForCurrentThread() - operationBytes;
                    }
                }
                else throw new InvalidOperationException("整局基准不支持阶段：" + battle.Phase);
            }
            measurement.Milliseconds = MillisecondsSince(start);
            measurement.AllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - bytes;
            measurement.CloneCount = BattleDiagnostics.CurrentThreadCloneCount - clones;
            measurement.HardCopyCount = BattleDiagnostics.CurrentThreadHardCopyCount - hardCopies;
            measurement.SimulationCloneCount = BattleDiagnostics.CurrentThreadSimulationCloneCount - simulationClones;
            if (measurement.Turns != battle.Record.Events.Count(entry => entry.Type == BattleRecordEventType.Turn))
                throw new InvalidOperationException("回合数与实际录像不符，case " + fixture.Id);
            return battle;
        }

        static Group Summarize(string packId, string level, List<Measurement> samples)
        {
            var times = samples.Select(item => item.Milliseconds).OrderBy(value => value).ToArray();
            long bytes = samples.Sum(item => item.AllocatedBytes);
            int turns = samples.Sum(item => item.Turns);
            return new Group { PackId = packId, Level = level, Games = samples.Count, Turns = turns,
                MeanMsPerGame = samples.Average(item => item.Milliseconds), P95MsPerGame = times[(int)Math.Ceiling(times.Length * 0.95) - 1],
                MaxMsPerGame = times[times.Length - 1], AllocatedBytes = bytes, AllocatedBytesPerTurn = (double)bytes / turns,
                CloneCount = samples.Sum(item => item.CloneCount), HardCopyCount = samples.Sum(item => item.HardCopyCount),
                SimulationCloneCount = samples.Sum(item => item.SimulationCloneCount),
                MeanConstructionMs = samples.Average(item => item.ConstructionMs), MeanDecisionMs = samples.Average(item => item.DecisionMs),
                MeanResolutionMs = samples.Average(item => item.ResolutionMs), ConstructionBytes = samples.Sum(item => item.ConstructionBytes),
                DecisionBytes = samples.Sum(item => item.DecisionBytes), ResolutionBytes = samples.Sum(item => item.ResolutionBytes) };
        }

        static void WriteMeasurements(string output, FullGameOptions options, List<ContentDb> packs, List<Measurement> games,
            List<Group> groups, double seconds, bool complete)
        {
            var report = new { Protocol, Complete = complete, MeasuredAtUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                Runtime = RuntimeInformation.FrameworkDescription, OperatingSystem = RuntimeInformation.OSDescription,
                Architecture = RuntimeInformation.ProcessArchitecture.ToString(), Configuration = "Release",
                TieredCompilationConfigured = AppContext.TryGetSwitch("System.Runtime.TieredCompilation", out bool tiered) ? (bool?)tiered : null,
                EngineAssemblySha256 = Hash(File.ReadAllBytes(typeof(Battle).Assembly.Location)),
                GamesPerGroup = options.Games, IsFullAcceptanceRun = options.Games == FullGameWorkload.GamesPerGroup,
                ElapsedSecondsIncludingWarmupAndArtifacts = seconds, ComparedWithBaseline = options.Baseline != null,
                LoadContext = "本进程内部串行，当前线程分配不含其它进程；T117采集期间优化前、优化后及验证程序可在隔离进程并行，wallclock只作并发负载参考，不能当成独占延迟或因果加速。主要验收指标为同赛程每回合分配下降。",
                Workload = "四包×Normal/Hard/Expert同级自战；每组预热1局后，单线程测量" + options.Games + "局。含Battle构造、双方决策、补位、结算与完整日志/录像；不含加载内容、赛程生成、哈希/序列化/输出。每回合分配为整局当前线程分配总和/实际Turn事件总数（含构造）。无人工GC，无时间驱动选择。",
                CloneDefinition = "CloneCount仅public Battle.Clone；HardCopyCount为CopyForHardAi；SimulationCloneCount为内部搜索专用复制。均使用ThreadStatic计数前后差，不能将公共Clone减少等同全部复制消失。",
                Determinism = "固定赛程；日志System.Text.Json IncludeFields=true紧凑编码；录像Record.ToJson原文；hash为小写16位hex。文件UTF8无BOM并追加LF。比较模式逐字节核对全部完整产物，不以摘要相等替代字节比较。",
                Content = packs.Select(db => new { db.PackId, Hash = db.ContentHash() }).ToArray(), Groups = groups, Games = games };
            File.WriteAllBytes(Path.Combine(output, "measurement.json"), JsonBytes(report));
        }

        static void Print(Group group)
        {
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "{0}/{1}：{2}局，平均 {3:F3}ms/局，P95 {4:F3}ms，{5:F0} bytes/回合；Clone {6} / HardCopy {7} / SimulationClone {8}",
                group.PackId, group.Level, group.Games, group.MeanMsPerGame, group.P95MsPerGame,
                group.AllocatedBytesPerTurn, group.CloneCount, group.HardCopyCount, group.SimulationCloneCount));
        }

        static void SaveAndCompare(string output, string baseline, string relative, byte[] bytes)
        {
            File.WriteAllBytes(Path.Combine(output, relative), bytes);
            if (baseline == null) return;
            string path = Path.Combine(baseline, relative);
            if (!File.Exists(path)) throw new InvalidOperationException("基线缺少文件：" + relative);
            byte[] expected = File.ReadAllBytes(path);
            if (expected.AsSpan().SequenceEqual(bytes)) return;
            int index = 0;
            while (index < expected.Length && index < bytes.Length && expected[index] == bytes[index]) index++;
            string message = "优化前后字节不一致：" + relative + "；首个差异offset=" + index + "；长度 " + expected.Length + " / " + bytes.Length;
            File.WriteAllText(Path.Combine(output, "failure.txt"), message + "\n", Utf8);
            throw new InvalidOperationException(message);
        }

        static byte[] JsonBytes<T>(T value) { return Utf8.GetBytes(JsonSerializer.Serialize(value, Json) + "\n"); }
        static string Hash(byte[] bytes) { return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(); }
        static double MillisecondsSince(long started) { return (Stopwatch.GetTimestamp() - started) * (1000.0 / Stopwatch.Frequency); }
    }
}
