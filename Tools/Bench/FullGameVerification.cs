using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace StreamDuel.Bench
{
    internal static class FullGameVerification
    {
        const int Cases = 2400;
        const int FilesPerCase = 3;
        const string Protocol = "T117-full-games-v1";

        internal static int Run(string output, string baseline)
        {
            output = Path.GetFullPath(output);
            baseline = Path.GetFullPath(baseline);
            if (string.Equals(output.TrimEnd(Path.DirectorySeparatorChar), baseline.TrimEnd(Path.DirectorySeparatorChar),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new ArgumentException("核验必须使用两个独立完成目录，不能与自身比较。");
            try
            {
                ValidateComplete(baseline);
                ValidateComplete(output);
            }
            catch (KeyNotFoundException error)
            {
                throw new FormatException("核验文件缺少必需字段。", error);
            }
            Compare(baseline, output, "schedule.json");
            Compare(baseline, output, "results.json");
            for (int id = 0; id < Cases; id++)
            {
                string prefix = "games/" + id.ToString("D4", CultureInfo.InvariantCulture);
                Compare(baseline, output, prefix + ".state.txt");
                Compare(baseline, output, prefix + ".log.json");
                Compare(baseline, output, prefix + ".record.json");
            }
            var report = new { Protocol, Success = true, Cases, Groups = 12, GamesPerGroup = 200,
                ComparedGameFiles = Cases * FilesPerCase, ComparedTotalFiles = Cases * FilesPerCase + 2,
                BaselineDirectory = baseline, AfterDirectory = output,
                VerifiedAtUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                ScheduleSha256 = Hash(File.ReadAllBytes(Path.Combine(output, "schedule.json"))),
                ResultsSha256 = Hash(File.ReadAllBytes(Path.Combine(output, "results.json"))),
                BaselineMeasurementSha256 = Hash(File.ReadAllBytes(Path.Combine(baseline, "measurement.json"))),
                AfterMeasurementSha256 = Hash(File.ReadAllBytes(Path.Combine(output, "measurement.json"))),
                Comparison = "全部 schedule/results 和 2400×3 个 state/log/record 文件均经完整字节 SequenceEqual；每目录独立验证Complete、数量、id覆盖和清单摘要，未重跑对局。",
                LoadContext = "两份隔离程序可与验证任务并行采集；各进程内部串行测CurrentThread分配，wallclock仅为并发机器负载参考。分配改善是主要20%验收指标。冻结before measurement原始计量值保留。" };
            string path = Path.Combine(output, "verification.json");
            File.WriteAllText(path, JsonSerializer.Serialize(report) + "\n", new UTF8Encoding(false));
            Console.WriteLine("核验通过：12组各200局，2400 case完整，7200逐局文件和schedule/results全部逐字节一致。");
            Console.WriteLine("核验报告：" + path);
            return 0;
        }

        static void ValidateComplete(string directory)
        {
            using var measurement = Read(directory, "measurement.json");
            var root = measurement.RootElement;
            Require(root.GetProperty("Protocol").GetString() == Protocol, directory, "measurement协议");
            Require(root.GetProperty("Complete").GetBoolean(), directory, "measurement未完成");
            Require(root.GetProperty("IsFullAcceptanceRun").GetBoolean(), directory, "不是正式规模");
            Require(root.GetProperty("GamesPerGroup").GetInt32() == 200, directory, "每组并非200局");
            var measured = root.GetProperty("Games");
            Require(measured.GetArrayLength() == Cases, directory, "测量缺少2400局");
            for (int id = 0; id < Cases; id++)
            {
                Require(measured[id].GetProperty("CaseId").GetInt32() == id, directory, "测量case id缺失、重复或乱序");
                Require(measured[id].GetProperty("Turns").GetInt32() > 0, directory, "测量回合数无效");
            }
            var groups = root.GetProperty("Groups");
            Require(groups.GetArrayLength() == 12, directory, "组合并非12组");
            for (int index = 0; index < 12; index++)
            {
                var group = groups[index];
                Require(group.GetProperty("PackId").GetString() == FullGameWorkload.PackIds[index / 3]
                    && group.GetProperty("Level").GetString() == FullGameWorkload.Levels[index % 3].ToString()
                    && group.GetProperty("Games").GetInt32() == 200, directory, "组合覆盖或组规模错误");
                int turns = 0;
                for (int id = index * 200; id < (index + 1) * 200; id++) turns += measured[id].GetProperty("Turns").GetInt32();
                Require(group.GetProperty("Turns").GetInt32() == turns, directory, "组回合数与逐局不一致");
            }
            using var schedule = Read(directory, "schedule.json");
            var scheduled = schedule.RootElement.GetProperty("Cases");
            Require(schedule.RootElement.GetProperty("Protocol").GetString() == Protocol
                && schedule.RootElement.GetProperty("GamesPerGroup").GetInt32() == 200
                && scheduled.GetArrayLength() == Cases, directory, "赛程协议或规模错误");
            using var results = Read(directory, "results.json");
            var outcomes = results.RootElement.GetProperty("Outcomes");
            Require(results.RootElement.GetProperty("Protocol").GetString() == Protocol
                && results.RootElement.GetProperty("Games").GetInt32() == Cases
                && outcomes.GetArrayLength() == Cases, directory, "终局清单不完整");
            string games = Path.Combine(directory, "games");
            Require(Directory.Exists(games) && Directory.GetFiles(games).Length == Cases * FilesPerCase
                && !Directory.EnumerateDirectories(games).Any(), directory, "逐局文件数量不是7200或含额外子目录");
            for (int id = 0; id < Cases; id++)
            {
                var input = scheduled[id];
                Require(input.GetProperty("Id").GetInt32() == id && input.GetProperty("GameIndex").GetInt32() == id % 200
                    && input.GetProperty("Seed").GetUInt32() == FullGameWorkload.FirstSeed + (uint)id
                    && input.GetProperty("PackId").GetString() == FullGameWorkload.PackIds[id / 600]
                    && input.GetProperty("Level").GetString() == FullGameWorkload.Levels[id / 200 % 3].ToString(),
                    directory, "赛程case id/种子/组合覆盖错误");
                var outcome = outcomes[id];
                Require(outcome.GetProperty("CaseId").GetInt32() == id, directory, "终局case id缺失、重复或乱序");
                Require(outcome.GetProperty("Turns").GetInt32() == measured[id].GetProperty("Turns").GetInt32(),
                    directory, "终局回合数与测量不符");
                int winner = outcome.GetProperty("Winner").GetInt32();
                Require(winner >= -1 && winner <= 1, directory, "胜方无效");
                string prefix = Path.Combine(games, id.ToString("D4", CultureInfo.InvariantCulture));
                string state = outcome.GetProperty("StateHash").GetString();
                Require(state != null && state.Length == 16 && state.All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f'),
                    directory, "StateHash格式无效");
                Require(File.ReadAllBytes(prefix + ".state.txt").AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(state + "\n")),
                    directory, "state文件与清单不一致，case " + id);
                Require(Hash(File.ReadAllBytes(prefix + ".log.json")) == outcome.GetProperty("LogSha256").GetString(),
                    directory, "log文件与清单摘要不一致，case " + id);
                Require(Hash(File.ReadAllBytes(prefix + ".record.json")) == outcome.GetProperty("RecordSha256").GetString(),
                    directory, "record文件与清单摘要不一致，case " + id);
            }
        }

        static JsonDocument Read(string directory, string file)
        {
            try { return JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, file))); }
            catch (JsonException error) { throw new FormatException("核验文件JSON无效：" + Path.Combine(directory, file), error); }
        }

        static void Compare(string baseline, string after, string relative)
        {
            byte[] left = File.ReadAllBytes(Path.Combine(baseline, relative));
            byte[] right = File.ReadAllBytes(Path.Combine(after, relative));
            if (left.AsSpan().SequenceEqual(right)) return;
            int index = 0;
            while (index < left.Length && index < right.Length && left[index] == right[index]) index++;
            throw new InvalidOperationException("完整字节比较失败：" + relative + "；首差offset=" + index
                + "；长度=" + left.Length + "/" + right.Length);
        }

        static void Require(bool condition, string directory, string detail)
        {
            if (!condition) throw new InvalidOperationException("不完整或不一致的正式产物：" + directory + "；" + detail);
        }

        static string Hash(byte[] bytes) { return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(); }
    }
}
