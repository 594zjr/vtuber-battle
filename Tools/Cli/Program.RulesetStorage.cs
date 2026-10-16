using System;
using System.IO;
using System.Linq;
using System.Text;
using StreamDuel.Core;

namespace StreamDuel.Cli
{
    internal static partial class Program
    {
        private static int RunRulesetStorageCommand(CliOptions options)
        {
            var db = LoadPack(Path.Combine(AppContext.BaseDirectory, "Content"), options);
            if (options.RulesetCommand == "--ruleset-save" || options.RulesetCommand == "--ruleset-import")
            {
                var ruleset = options.RulesetCommand == "--ruleset-import"
                    ? RulesetCode.Decode(options.RulesetSource, db) : ReadRulesetSource(options.RulesetSource, db);
                string name = options.RulesetCommand == "--ruleset-import" ? ruleset.Name : options.RulesetName;
                // 创建存档目录前完成校验；持锁保存时仍重读档案，防止覆盖另一进程的新结果。
                var preview = FileSaveStore.LoadProfileSnapshot(options.SaveDir, true);
                preview.AddRuleset(name, ruleset, db);
                var saved = new FileSaveStore(options.SaveDir).SaveRuleset(name, ruleset, db);
                PrintRulesetNameAdjustment(name);
                Console.WriteLine("已保存我的赛制：" + saved.Name);
                return 0;
            }

            var profile = FileSaveStore.LoadProfileSnapshot(options.SaveDir, true);
            if (options.RulesetCommand == "--ruleset-list")
            {
                Console.WriteLine("内置赛制（只读，可导出后另存为新名称）：");
                foreach (var ruleset in Rulesets.All) PrintStoredRuleset(ruleset);
                Console.WriteLine("我的赛制（" + profile.MyRulesets.Count + "/20）：");
                foreach (var ruleset in profile.MyRulesets) PrintStoredRuleset(ruleset);
                if (profile.MyRulesets.Count == 0) Console.WriteLine("暂无保存的赛制。");
            }
            else if (options.RulesetCommand == "--ruleset-code")
            {
                var ruleset = profile.GetRuleset(options.RulesetName);
                ruleset.Validate(db);
                Console.WriteLine(RulesetCode.Encode(ruleset));
            }
            else if (options.RulesetCommand == "--ruleset-rename")
            {
                profile.RenameRuleset(options.RulesetName, options.RulesetNewName);
                var renamed = new FileSaveStore(options.SaveDir).RenameRuleset(options.RulesetName, options.RulesetNewName);
                PrintRulesetNameAdjustment(options.RulesetNewName);
                Console.WriteLine("赛制已改名：" + renamed.Name);
            }
            else if (options.RulesetCommand == "--ruleset-delete")
            {
                profile.DeleteRuleset(options.RulesetName);
                new FileSaveStore(options.SaveDir).DeleteRuleset(options.RulesetName);
                Console.WriteLine("已删除我的赛制：" + TextInput.Clean(options.RulesetName, TextInputKind.RulesetName).Text);
            }
            return 0;
        }

        private static Ruleset ReadRulesetSource(string source, ContentDb db)
        {
            string json;
            if (source.TrimStart().StartsWith("{", StringComparison.Ordinal)) json = source;
            else if (File.Exists(source))
            {
                try
                {
                    using (var stream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
                    using (var reader = new StreamReader(stream, new UTF8Encoding(true, true), false))
                    {
                        var text = new StringBuilder();
                        var buffer = new char[4096];
                        int count;
                        while ((count = reader.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            if (text.Length + count > 65536) throw new FormatException("赛制 JSON 不能超过 64 KiB。");
                            text.Append(buffer, 0, count);
                        }
                        json = text.ToString();
                    }
                }
                catch (DecoderFallbackException ex)
                {
                    throw new FormatException("赛制文件不是有效的 UTF-8 JSON 文本。", ex);
                }
            }
            else return RulesetCode.Decode(source, db);
            if (json.Length > 65536 || Encoding.UTF8.GetByteCount(json) > 65536)
                throw new FormatException("赛制 JSON 不能超过 64 KiB。");
            return RulesetCode.FromJson(json, db);
        }

        private static Ruleset ResolveRuleset(CliOptions options)
        {
            var builtIn = Rulesets.All.FirstOrDefault(item => item.Id == options.RulesetId || item.Name == options.RulesetId);
            return builtIn ?? FileSaveStore.LoadProfileSnapshot(options.SaveDir, true).GetRuleset(options.RulesetId);
        }

        private static void PrintRulesetNameAdjustment(string name)
        {
            var cleaned = TextInput.Clean(name, TextInputKind.RulesetName);
            if (cleaned.Changed) Console.WriteLine("提示：" + cleaned.Message + "；保存名称：" + cleaned.Text);
        }

        private static void PrintStoredRuleset(Ruleset ruleset)
        {
            Console.WriteLine(TextInput.Clean(ruleset.Name, TextInputKind.RulesetName).Text
                + "（" + RulesetDisplayId(ruleset.Id) + "）：最多 " + ruleset.BestOf + " 局；"
                + (ruleset.BanOne ? "禁用" : "无禁用") + "；" + (ruleset.LeadPick ? "选择首发" : "默认首发")
                + "；修饰器 " + (ruleset.Modifiers == null || ruleset.Modifiers.Count == 0 ? "无" : string.Join("、", ruleset.Modifiers))
                + "；计时 " + ruleset.TurnSeconds + "/" + ruleset.BankSeconds + " 秒"
                + (ruleset.Season == null ? "" : "；赛季 " + TextInput.Clean(ruleset.Season, TextInputKind.Note).Text));
        }

        private static string RulesetDisplayId(string id)
        {
            return string.IsNullOrWhiteSpace(id) ? "自定义" : TextInput.Clean(id, TextInputKind.Note).Text;
        }
    }
}
