using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using StreamDuel.Core;

namespace StreamDuel.Bench
{
    internal sealed class BenchmarkResult
    {
        public string PackId { get; }
        public string ContentHash { get; }
        public uint Seed { get; }
        public bool Quick { get; }
        public string Workload { get; }
        public List<List<string>> Teams { get; }
        public List<SnapshotResult> Snapshots { get; }
        public int CompletedGames { get; }
        public string TerminalChecksum { get; }
        public List<MetricResult> Metrics { get; }
        public ExpertBenchmarkResult Expert { get; }

        internal BenchmarkResult(ContentDb db, uint seed, bool quick, string workload,
            IList<string>[] teams, List<SnapshotResult> snapshots, int completedGames,
            ulong terminalChecksum, List<MetricResult> metrics, ExpertBenchmarkResult expert)
        {
            PackId = db.PackId;
            ContentHash = db.ContentHash();
            Seed = seed;
            Quick = quick;
            Workload = workload;
            Teams = teams.Select(team => team.ToList()).ToList();
            Snapshots = snapshots;
            CompletedGames = completedGames;
            TerminalChecksum = terminalChecksum.ToString("x16", CultureInfo.InvariantCulture);
            Metrics = metrics;
            Expert = expert;
        }
    }

    internal sealed class SnapshotResult
    {
        public string Label { get; }
        public int Turn { get; }
        public int LogCount { get; }
        public int RecordEventCount { get; }
        public string StateHash { get; }

        internal SnapshotResult(string label, Battle battle)
        {
            Label = label;
            Turn = battle.Turn;
            LogCount = battle.Log.Count;
            RecordEventCount = battle.Record.Events.Count;
            StateHash = battle.StateHash().ToString("x16", CultureInfo.InvariantCulture);
        }
    }

    internal sealed class MetricResult
    {
        public string Id { get; }
        public string Label { get; }
        public int Count { get; }
        public double MeanMs { get; }
        public double P95Ms { get; }
        public double MaxMs { get; }
        public double TotalMs { get; }
        public double? OperationsPerSecond { get; }

        internal MetricResult(string id, string label, double[] samples, bool throughput)
        {
            Id = id;
            Label = label;
            Count = samples.Length;
            TotalMs = samples.Sum();
            MeanMs = TotalMs / Count;
            var sorted = samples.OrderBy(value => value).ToArray();
            P95Ms = sorted[(int)Math.Ceiling(0.95 * Count) - 1];
            MaxMs = sorted[Count - 1];
            OperationsPerSecond = throughput && TotalMs > 0 ? Count * 1000.0 / TotalMs : (double?)null;
        }
    }

    internal static class BenchmarkRunner
    {
        const uint Seed = 52001;
        const ulong ChecksumOffset = 14695981039346656037UL;
        const ulong ChecksumPrime = 1099511628211UL;
        static readonly IList<string>[] Teams =
        {
            new[] { "enka", "shiori", "lumi", "bell", "hane", "momo" },
            new[] { "kei", "nyako", "buck", "kasumi", "yuan", "bee" }
        };

        internal static BenchmarkResult Run(bool quick)
        {
            string contentDirectory = Path.Combine(AppContext.BaseDirectory, "Content");
            string rules = File.ReadAllText(Path.Combine(contentDirectory, "StreamDuelRules.json"));
            var policy = PackPolicy.LoadDefault();
            var sources = Directory.GetFiles(Path.Combine(contentDirectory, "Packs"), "*.json")
                .OrderBy(path => path, StringComparer.Ordinal)
                .Select(path => File.ReadAllText(path)).ToList();
            var databases = new List<ContentDb>();
            var availableSources = new List<string>();
            foreach (string source in sources)
            {
                var loaded = PackLoader.LoadJson(rules, source, "基准内容包", policy);
                if (loaded.IsDisabled) continue;
                if (loaded.Db == null) throw new InvalidOperationException(loaded.Error);
                databases.Add(loaded.Db);
                availableSources.Add(source);
            }
            sources = availableSources;
            var db = databases.SingleOrDefault(pack => pack.PackId == "original")
                ?? throw new InvalidOperationException(policy.PackReason("original") ?? "性能基准缺少可用的 original 内容包。");
            foreach (var team in Teams)
            {
                var errors = TeamCheck.Validate(db, null, team);
                if (errors.Count > 0) throw new InvalidOperationException("基准固定阵容无效：" + string.Join("；", errors));
            }

            var positions = MakePositions(db);
            var snapshots = new List<SnapshotResult>
            {
                new SnapshotResult("开局", positions[0]),
                new SnapshotResult("中盘", positions[1]),
                new SnapshotResult("后半程", positions[2])
            };
            var metrics = new List<MetricResult>();
            int completedGames = 0;
            ulong terminalChecksum = ChecksumOffset;
            metrics.Add(Measure("games.normal", "普通对普通完整对局", 2, quick ? 12 : 120,
                index => unchecked(Seed + (uint)index),
                gameSeed =>
                {
                    var battle = NewBattle(db, gameSeed);
                    PlayToEnd(battle);
                    return battle;
                },
                battle =>
                {
                    completedGames++;
                    // 终局校验不属于对局吞吐，放在样本计时之外。
                    AddChecksum(ref terminalChecksum, battle.Seed);
                    AddChecksum(ref terminalChecksum, unchecked((ulong)battle.Winner));
                    AddChecksum(ref terminalChecksum, (ulong)battle.Turn);
                    AddChecksum(ref terminalChecksum, battle.StateHash());
                }, true));

            var levels = new[] { AiLevel.Easy, AiLevel.Normal, AiLevel.Hard, AiLevel.Expert };
            var levelNames = new[] { "简单", "普通", "困难", "专家" };
            for (int levelIndex = 0; levelIndex < levels.Length; levelIndex++)
            {
                AiLevel level = levels[levelIndex];
                int count = level == AiLevel.Expert ? (quick ? 24 : 240)
                    : level == AiLevel.Hard ? (quick ? 60 : 600) : (quick ? 240 : 2400);
                metrics.Add(Measure("ai." + level.ToString().ToLowerInvariant(), levelNames[levelIndex] + " AI 单次决策", 6, count,
                    // 每次恢复同一局面与随机流，避免准备成本或先前决策改变样本。
                    index => (Battle: positions[(index / 2) % positions.Count].Clone(), Side: index % 2),
                    input => Ai.Choose(input.Battle, input.Side, level)));
            }

            var expert = ExpertBenchmark.Run(positions, metrics, quick);

            var middle = positions[1];
            metrics.Add(Measure("battle.clone", "中盘 Battle.Clone", 20, quick ? 200 : 2000,
                index => middle, battle => battle.Clone()));
            metrics.Add(Measure("battle.state_hash", "中盘 StateHash", 20, quick ? 600 : 6000,
                index => middle, battle => battle.StateHash()));

            // 复用同一真实回合的两个独立快照，计时只包含差量计算及其结果分配。
            var viewBattle = middle.Clone();
            var beforeView = BattleViewModel.From(viewBattle, 0);
            var firstAction = Ai.Choose(viewBattle, 0, AiLevel.Normal);
            var secondAction = Ai.Choose(viewBattle, 1, AiLevel.Normal);
            viewBattle.ResolveTurn(firstAction, secondAction);
            var afterView = BattleViewModel.From(viewBattle, 0);
            snapshots.Add(new SnapshotResult("差量结算后", viewBattle));
            metrics.Add(Measure("view.diff", "一回合 ViewDiff.Between", 20, quick ? 200 : 2000,
                index => (Before: beforeView, After: afterView),
                input => ViewDiff.Between(input.Before, input.After)));

            for (int packIndex = 0; packIndex < sources.Count; packIndex++)
            {
                string json = sources[packIndex];
                string packId = databases[packIndex].PackId;
                metrics.Add(Measure("content.load." + packId, "加载 " + packId + "（内存 JSON）", 3, quick ? 24 : 240,
                    index => json, packJson => ContentDb.FromJson(rules, packJson, policy)));
            }

            string workload = "固定 original 六对六阵容，无禁用和首发选择；完整对局使用种子 52001 起连续递增，"
                + "计时包含 Battle 构造、双方普通 AI 行动/补位与完整结算，保留所有日志和回放历史；吞吐为样本数除以累计计时。"
                + "AI 局面由种子 52001 的普通对普通对局确定，取开局、行动回合数中点及四分之三处，轮换双方且等量采样；"
                + "每次在计时外 Clone 恢复，仅计时 Ai.Choose。Clone 和 StateHash 使用中盘完整历史。"
                + "ViewDiff 使用该中盘局面再结算一回合前后的 0 方视图；Clone、普通 AI 选招、ResolveTurn 和视图构造均在计时外，"
                + "所有样本复用这对独立快照，仅计时 ViewDiff.Between；快照表同时记录差量结算后的局面。"
                + expert.Workload
                + "所有内置内容包的 JSON 在计时外预读；加载仅计解析、合并和校验，不含磁盘 IO。"
                + "每项先预热：对局 2 次、每种 AI 6 次、Clone/StateHash/ViewDiff 各 20 次、每包加载 3 次；"
                + "不主动触发 GC，不扣计时器开销；P95 为最近秩 ceil(0.95*n)。"
                + "TerminalChecksum 为测量对局的 Seed/Winner/Turn/StateHash 按顺序各写 8 字节小端序计算的 FNV-1a 64 位摘要。";
            return new BenchmarkResult(db, Seed, quick, workload, Teams, snapshots, completedGames, terminalChecksum, metrics, expert);
        }

        static MetricResult Measure<TInput, TOutput>(string id, string label, int warmup, int count,
            Func<int, TInput> prepare, Func<TInput, TOutput> operation, Action<TOutput> consume = null, bool throughput = false)
        {
            for (int index = 0; index < warmup; index++)
            {
                var result = operation(prepare(index));
                GC.KeepAlive(result);
            }
            var samples = new double[count];
            double millisecondsPerTick = 1000.0 / Stopwatch.Frequency;
            for (int index = 0; index < count; index++)
            {
                var input = prepare(index);
                long started = Stopwatch.GetTimestamp();
                var result = operation(input);
                long finished = Stopwatch.GetTimestamp();
                samples[index] = (finished - started) * millisecondsPerTick;
                if (consume != null) consume(result);
                GC.KeepAlive(result);
            }
            return new MetricResult(id, label, samples, throughput);
        }

        static Battle NewBattle(ContentDb db, uint seed)
        {
            return new Battle(db, Teams, new[] { "基准甲", "基准乙" }, seed);
        }

        static List<Battle> MakePositions(ContentDb db)
        {
            int rounds = PlayToEnd(NewBattle(db, Seed));
            if (rounds < 4) throw new InvalidOperationException("基准对局过短，无法提供独立的开局、中盘及后半程局面。");
            var wanted = new HashSet<int> { 0, rounds / 2, rounds * 3 / 4 };
            var positions = new List<Battle>();
            PlayToEnd(NewBattle(db, Seed), (battle, round) =>
            {
                if (wanted.Contains(round)) positions.Add(battle.Clone());
            });
            if (positions.Count != 3 || positions[1].Record.Events.Count == 0)
                throw new InvalidOperationException("未能准备具有真实中盘历史的基准局面。");
            return positions;
        }

        static int PlayToEnd(Battle battle, Action<Battle, int> beforeChoice = null)
        {
            int rounds = 0, steps = 0;
            while (battle.Phase != Phase.Over)
            {
                if (++steps > Battle.TurnCap * 4 + 32)
                    throw new InvalidOperationException("基准对局超过预期步骤上限，未完成的对局不会计入吞吐。");
                if (battle.Phase == Phase.Replace)
                {
                    for (int side = 0; side < 2 && battle.Phase == Phase.Replace; side++)
                        if (battle.Need[side]) battle.Replace(side, Ai.ChooseReplacement(battle, side, AiLevel.Normal));
                    continue;
                }
                if (battle.Phase != Phase.Choose)
                    throw new InvalidOperationException("基准对局出现不支持的阶段：" + battle.Phase);
                if (beforeChoice != null) beforeChoice(battle, rounds);
                var first = Ai.Choose(battle, 0, AiLevel.Normal);
                var second = Ai.Choose(battle, 1, AiLevel.Normal);
                battle.ResolveTurn(first, second);
                rounds++;
            }
            return rounds;
        }

        static void AddChecksum(ref ulong checksum, ulong value)
        {
            for (int index = 0; index < 8; index++)
            {
                checksum = unchecked((checksum ^ (byte)value) * ChecksumPrime);
                value >>= 8;
            }
        }
    }
}
