using System;
using System.Collections.Generic;
using System.Linq;

namespace StreamDuel.Cli
{
    internal sealed partial class CliOptions
    {
        public string BranchCompareSource;
        public string BranchCompareReplay;

        private bool ReadBranchCompareOption(string[] args, ref int index)
        {
            if (args[index] != "--branch-compare") return false;
            if (BranchCompareSource != null) throw new ArgumentException("--branch-compare 不能重复指定。");
            BranchCompareSource = ReadBranchCompareFile(args, ref index, "原录像");
            BranchCompareReplay = ReadBranchCompareFile(args, ref index, "接手录像");
            return true;
        }

        private static string ReadBranchCompareFile(string[] args, ref int index, string label)
        {
            if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index])
                || args[index].StartsWith("--", StringComparison.Ordinal) || args[index] == "-h")
                throw new ArgumentException("--branch-compare 缺少" + label + "文件路径；须依次提供原录像和接手录像两个文件。");
            if (args[index].Any(char.IsControl))
                throw new ArgumentException("--branch-compare 的文件路径不能包含换行或控制字符。");
            return args[index];
        }

        private static void ValidateBranchCompareOptions(CliOptions options, List<string> provided)
        {
            if (options.BranchCompareSource == null || options.Help) return;
            var unsupported = provided.Where(option => option != "--branch-compare" && option != "--pack-dir")
                .Distinct().ToArray();
            if (unsupported.Length > 0)
                throw new ArgumentException("--branch-compare 是独立只读对照，只可搭配 --pack-dir；不能与 "
                    + string.Join("、", unsupported) + " 同用。");
            string duplicate = provided.GroupBy(option => option).FirstOrDefault(group => group.Count() > 1)?.Key;
            if (duplicate != null) throw new ArgumentException(duplicate + " 不能重复指定。");
        }
    }
}
