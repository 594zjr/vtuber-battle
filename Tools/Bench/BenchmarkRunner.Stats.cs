using System;
using System.Collections.Generic;
using System.IO;
using StreamDuel.Core;

namespace StreamDuel.Bench
{
    internal static partial class BenchmarkRunner
    {
        static void AddStatsMetrics(ContentDb db, bool quick, List<MetricResult> metrics)
        {
            string directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "obj",
                "stats-bench", Guid.NewGuid().ToString("N")));
            var moment = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            var library = new ReplayLibrary(directory, 200, () => moment);
            var profile = new Profile();
            for (int i = 0; i < 200; i++)
            {
                var battle = NewBattle(db, unchecked(Seed + (uint)i));
                PlayToEnd(battle);
                int side = i % 2;
                profile.Record(battle, side);
                library.Save(battle.Record, BattleSummary.From(battle), playerSide: side, aiLevel: AiLevel.Normal);
                moment = moment.AddSeconds(1);
            }

            Action<List<ReplayEntry>> validate = entries =>
            {
                if (entries.Count != 200 || entries.Exists(entry => entry.IsCorrupt || entry.Stats == null))
                    throw new InvalidOperationException("统计基准必须加载200局完整且含本方摘要的真实录像。");
            };
            metrics.Add(Measure("stats.library.first_read_200", "200局录像首次只读扫描（未清系统缓存）", 0, 1,
                index => library, value => value.ReadStatsSnapshot(), validate));
            metrics.Add(Measure("stats.library.read_200", "200局录像预热后读取与完整校验", 1, quick ? 3 : 10,
                index => library, value => value.ReadStatsSnapshot(), validate));
            var snapshot = library.ReadStatsSnapshot();
            metrics.Add(Measure("stats.compute_200", "200局统计重建及全部五项查询（目标<50ms）", 3, quick ? 30 : 200,
                index => snapshot, entries =>
                {
                    var stats = new ProfileStats(profile, entries, new[] { db });
                    return new object[] { stats.ByStreamer(), stats.TopTeams(10), stats.VsAi(), stats.Recent(10), stats.Streaks() };
                }));
        }
    }
}
