using System;
using System.Collections.Generic;
using System.Linq;

namespace StreamDuel.Cli
{
    internal sealed partial class CliOptions
    {
        public string RulesetCommand;
        public string RulesetName;
        public string RulesetSource;
        public string RulesetNewName;

        private bool ReadRulesetStorageOption(string[] args, ref int index)
        {
            string option = args[index];
            if (option != "--ruleset-save" && option != "--ruleset-list" && option != "--ruleset-code"
                && option != "--ruleset-import" && option != "--ruleset-rename" && option != "--ruleset-delete")
                return false;
            if (RulesetCommand != null)
                throw new ArgumentException("赛制保存、列表、导出、导入、改名和删除每次只能执行一种，不能重复指定。");
            RulesetCommand = option;
            if (option == "--ruleset-list") return true;
            if (option == "--ruleset-import")
            {
                RulesetSource = ReadAnalysisValue(args, ref index, option, "赛制分享码");
                return true;
            }
            RulesetName = ReadAnalysisValue(args, ref index, option, "赛制名称");
            if (option == "--ruleset-save")
                RulesetSource = ReadAnalysisValue(args, ref index, option, "JSON、JSON 文件路径或赛制分享码");
            else if (option == "--ruleset-rename")
                RulesetNewName = ReadAnalysisValue(args, ref index, option, "新名称");
            return true;
        }

        private static void ValidateRulesetStorageOptions(CliOptions options, List<string> provided)
        {
            if (options.RulesetCommand == null || options.Help) return;
            var allowed = new HashSet<string>(StringComparer.Ordinal)
            {
                options.RulesetCommand, "--save-dir", "--pack", "--packs", "--pack-dir"
            };
            var unsupported = provided.Where(option => !allowed.Contains(option)).Distinct().ToArray();
            if (unsupported.Length > 0)
                throw new ArgumentException("我的赛制管理是独立入口，只可搭配 --save-dir、--pack、--packs、--pack-dir；不能与 "
                    + string.Join("、", unsupported) + " 同用。");
            string duplicate = provided.GroupBy(option => option).FirstOrDefault(group => group.Count() > 1)?.Key;
            if (duplicate != null) throw new ArgumentException(duplicate + " 不能重复指定。");
        }
    }
}
