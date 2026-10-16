using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace StreamDuel.Balance
{
    internal static partial class Program
    {
        static int RunBalanceComparison(string[] args)
        {
            string oldPath = null, newPath = null;
            double threshold = 0.05;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int index = 0; index < args.Length; index++)
            {
                string option = args[index];
                if (!seen.Add(option)) throw new ArgumentException("选项不能重复：" + option + "。");
                switch (option)
                {
                    case "--compare-balance":
                        oldPath = ComparisonArgument(args, ref index, option);
                        newPath = ComparisonArgument(args, ref index, option);
                        break;
                    case "--change-threshold":
                        string text = ComparisonArgument(args, ref index, option);
                        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out threshold)
                            || !double.IsFinite(threshold) || threshold < 0 || threshold > 1)
                            throw new ArgumentException("--change-threshold 必须是 0～1 的有限比例，0.05 表示 5 个百分点。");
                        break;
                    default:
                        throw new ArgumentException("--compare-balance 只接受两份结果文件和可选 --change-threshold，不能合用：" + option + "。");
                }
            }
            var result = BalanceCompare.Compare(BalanceSnapshot.Load(oldPath), BalanceSnapshot.Load(newPath), threshold);
            Console.Write(result.ToText());
            return result.HasOutOfRange ? 1 : 0;
        }

        static string ComparisonArgument(string[] args, ref int index, string option)
        {
            if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index])
                || args[index].StartsWith("--", StringComparison.Ordinal) || args[index] == "-h")
                throw new ArgumentException(option + " 缺少参数值。");
            return args[index];
        }

        static string BalanceOutputPath(string path)
        {
            if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal))
                throw new ArgumentException("保存路径不能使用设备路径。");
            string full = Path.GetFullPath(path);
            string relative = full.Substring(Path.GetPathRoot(full).Length);
            foreach (string segment in relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (segment != segment.Trim() || segment.EndsWith(".", StringComparison.Ordinal)
                    || segment.IndexOfAny(new[] { ':', '<', '>', '"', '|', '?', '*' }) >= 0)
                    throw new ArgumentException("保存路径不能含备用数据流、非法字符或会被系统改写的路径段。");
                foreach (char character in segment)
                    if (char.IsControl(character)) throw new ArgumentException("保存路径不能含控制字符。");
                string device = segment.Split('.')[0].ToUpperInvariant();
                if (device == "CON" || device == "PRN" || device == "AUX" || device == "NUL"
                    || device == "CONIN$" || device == "CONOUT$"
                    || device.Length == 4 && (device.StartsWith("COM", StringComparison.Ordinal) || device.StartsWith("LPT", StringComparison.Ordinal))
                        && "123456789¹²³".IndexOf(device[3]) >= 0)
                    throw new ArgumentException("保存路径不能使用保留设备名称。");
            }
            if (File.Exists(full) || Directory.Exists(full))
                throw new IOException("结果文件已存在，未覆盖；请指定新的文件名：" + full);
            string parent = Path.GetDirectoryName(full);
            if (!Directory.Exists(parent)) throw new IOException("保存目录不存在，请先创建输出目录：" + parent);
            for (var current = new DirectoryInfo(parent); current != null; current = current.Parent)
                if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("保存路径不能经过符号链接或目录联接：" + current.FullName);
            if (IsBalancePathWithin(full, Path.Combine(AppContext.BaseDirectory, "Content")))
                throw new IOException("不能把平衡结果保存到工具的内容资源目录。");
            for (var current = new DirectoryInfo(AppContext.BaseDirectory); current != null; current = current.Parent)
            {
                if (!File.Exists(Path.Combine(current.FullName, "AGENTS.md"))
                    || !File.Exists(Path.Combine(current.FullName, "Engine", "StreamDuel.Core.csproj"))) continue;
                if (IsBalancePathWithin(full, Path.Combine(current.FullName, "UnityProject", "Assets", "StreamDuel", "Resources")))
                    throw new IOException("不能把平衡结果保存到工程的内容资源目录。");
                break;
            }
            return full;
        }

        static bool IsBalancePathWithin(string path, string root)
        {
            string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return string.Equals(path, fullRoot, comparison)
                || path.StartsWith(fullRoot + Path.DirectorySeparatorChar, comparison);
        }

        static void SaveBalanceSnapshot(BalanceSnapshot snapshot, string path)
        {
            string json = snapshot.ToJson();
            // 模拟耗时期间目录可能变化，写入前再检查，CreateNew 防止覆盖竞态中新出现的文件。
            path = BalanceOutputPath(path);
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                writer.Write(json);
            Console.Error.WriteLine("已保存平衡结果：" + path);
        }
    }
}
