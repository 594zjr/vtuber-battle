using System;
using System.Collections.Generic;
using System.Linq;

namespace StreamDuel.Cli
{
    internal sealed partial class CliOptions
    {
        public string CompareA;
        public string CompareB;
        public bool CompareMode { get { return CompareA != null; } }

        private bool ReadCompareOption(string[] args, ref int index)
        {
            if (args[index] != "--compare") return false;
            if (CompareMode) throw new ArgumentException("--compare 不能重复指定。");
            CompareA = ReadCompareId(args, ref index, "A 方");
            CompareB = ReadCompareId(args, ref index, "B 方");
            return true;
        }

        private static string ReadCompareId(string[] args, ref int index, string side)
        {
            if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index])
                || args[index].StartsWith("--", StringComparison.Ordinal) || args[index] == "-h")
                throw new ArgumentException("--compare 缺少" + side + "主播 id，请提供两个主播 id。");
            if (args[index].Any(char.IsControl))
                throw new ArgumentException("--compare 的主播 id 不能含换行或控制字符。");
            return args[index];
        }

        private static void ValidateCompareOptions(CliOptions options, List<string> provided)
        {
            if (!options.CompareMode || options.Help) return;
            var allowed = new[] { "--compare", "--pack", "--packs", "--pack-dir" };
            var unsupported = provided.Where(option => !allowed.Contains(option)).Distinct().ToArray();
            if (unsupported.Length > 0)
                throw new ArgumentException("--compare 仅支持搭配 --pack、--packs、--pack-dir；不能与 "
                    + string.Join("、", unsupported) + " 同用。");
        }
    }
}
