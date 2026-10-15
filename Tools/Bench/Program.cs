using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace StreamDuel.Bench
{
    internal static class Program
    {
        static int Main(string[] args)
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            Console.Out.NewLine = "\n";
            try
            {
                bool quick = false, help = false;
                foreach (string argument in args)
                {
                    if (argument == "--quick") quick = true;
                    else if (argument == "--help" || argument == "-h") help = true;
                    else throw new ArgumentException("未知选项：" + argument + "；使用 --help 查看用法。");
                }
                if (help)
                {
                    Console.WriteLine("用法：dotnet run -c Release --project Tools/Bench -- [--quick]");
                    Console.WriteLine("固定种子，先预热再计时；--quick 使用较小的固定样本数。");
                    Console.WriteLine("输出均值、95 分位、最大耗时及普通对普通完整对局吞吐，结果写入当前目录的 bench_out/bench.json。");
                    Console.WriteLine("计时结果受机器负载影响，请在相同环境下比较；参考上限见 docs/performance.md。");
                    return 0;
                }
#if DEBUG
                throw new InvalidOperationException("性能基准需要 Release 构建，请使用 dotnet run -c Release --project Tools/Bench --" + (quick ? " --quick" : "") + "。");
#else
                var elapsed = Stopwatch.StartNew();
                var result = BenchmarkRunner.Run(quick);
                elapsed.Stop();
                var report = new
                {
                    SchemaVersion = 1,
                    MeasuredAtUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                    Runtime = RuntimeInformation.FrameworkDescription,
                    OperatingSystem = RuntimeInformation.OSDescription,
                    Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                    LogicalProcessorCount = Environment.ProcessorCount,
                    ProcessorIdentifier = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER"),
                    Configuration = "Release",
                    TieredCompilationConfigured = AppContext.TryGetSwitch("System.Runtime.TieredCompilation", out bool tiered) ? (bool?)tiered : null,
                    StopwatchFrequency = Stopwatch.Frequency,
                    ElapsedSecondsIncludingWarmup = elapsed.Elapsed.TotalSeconds,
                    Benchmark = result
                };
                string output = Path.GetFullPath(Path.Combine("bench_out", "bench.json"));
                Directory.CreateDirectory(Path.GetDirectoryName(output));
                File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                }) + "\n", new UTF8Encoding(false));
                Console.WriteLine("开播对决性能基准（Release / " + (quick ? "快速" : "完整") + "）");
                Console.WriteLine("运行时：" + RuntimeInformation.FrameworkDescription + "；架构：" + RuntimeInformation.ProcessArchitecture);
                Console.WriteLine("内容包：" + result.PackId + "；固定种子：" + result.Seed.ToString(CultureInfo.InvariantCulture));
                Console.WriteLine();
                Console.WriteLine("| 项目 | 样本数 | 平均 ms | P95 ms | 最大 ms | 次/秒 |");
                Console.WriteLine("|---|---:|---:|---:|---:|---:|");
                foreach (var metric in result.Metrics)
                {
                    Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "| {0} | {1} | {2:F4} | {3:F4} | {4:F4} | {5} |",
                        metric.Label, metric.Count, metric.MeanMs, metric.P95Ms, metric.MaxMs,
                        metric.OperationsPerSecond.HasValue ? metric.OperationsPerSecond.Value.ToString("F2", CultureInfo.InvariantCulture) : "—"));
                }
                Console.WriteLine();
                Console.WriteLine("含预热的总耗时：" + elapsed.Elapsed.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture) + " 秒");
                Console.WriteLine("结果已保存：" + output);
                return 0;
#endif
            }
            catch (Exception ex) when (ex is ArgumentException || ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException || ex is FormatException || ex is StreamDuel.Core.KeyError)
            {
                Console.Error.WriteLine("错误：" + ex.Message);
                return 1;
            }
        }
    }
}
