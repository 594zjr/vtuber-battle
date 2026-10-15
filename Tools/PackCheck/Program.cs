using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using StreamDuel.Core;

namespace StreamDuel.PackCheck
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            try
            {
                string packId = null;
                bool writeDocs = false;
                for (int i = 0; i < args.Length; i++)
                {
                    switch (args[i])
                    {
                        case "--help":
                        case "-h":
                            Console.WriteLine("用法：dotnet run --project Tools/PackCheck -- [--pack <id>] [--write-docs]");
                            Console.WriteLine("默认检查全部内容包；--pack 只检查指定文件名（不含 .json）。错误返回 1，提醒不影响退出码。");
                            Console.WriteLine("--write-docs 在所选内容包全部通过检查后，更新对应 docs/roster/<packId>.md。");
                            return 0;
                        case "--pack":
                            if (++i >= args.Length || !SafeId(args[i]))
                                throw new ArgumentException("--pack 需要内容包文件名（不含 .json），仅允许英文字母、数字、下划线和连字符。");
                            packId = args[i];
                            break;
                        case "--write-docs":
                            writeDocs = true;
                            break;
                        default:
                            throw new ArgumentException("未知选项：" + args[i] + "；使用 --help 查看用法。");
                    }
                }

                string root = FindRoot();
                string resources = Path.Combine(root, "UnityProject", "Assets", "StreamDuel", "Resources");
                string rulesPath = Path.Combine(resources, "StreamDuelRules.json");
                string rules = ReadJson(rulesPath);
                string packsDirectory = Path.Combine(resources, "Packs");
                string[] paths = packId == null
                    ? Directory.GetFiles(packsDirectory, "*.json").OrderBy(p => p, StringComparer.Ordinal).ToArray()
                    : new[] { Path.Combine(packsDirectory, packId + ".json") };
                if (paths.Length == 0) throw new IOException("内容包目录没有 JSON 文件：" + packsDirectory);

                string[] sensitiveWords = File.ReadAllLines(Path.Combine(root, "Tools", "PackCheck", "sensitive_words.txt"))
                    .Select(line => line.Trim()).Where(line => line.Length > 0 && !line.StartsWith("#", StringComparison.Ordinal))
                    .Distinct(StringComparer.Ordinal).ToArray();
                var documents = new SortedDictionary<string, string>(StringComparer.Ordinal);
                var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int errors = 0;
                int warnings = 0;
                Action<bool, string, string> report = (error, location, message) =>
                {
                    if (error) errors++; else warnings++;
                    Console.WriteLine((error ? "[错误] " : "[提醒] ") + location + "：" + message);
                };

                foreach (string path in paths)
                {
                    string location = Path.GetRelativePath(root, path).Replace('\\', '/');
                    ContentDb db;
                    try
                    {
                        string pack = ReadJson(path);
                        // FromJson 会调用引擎内建 Validate，工具不复制或放宽规则。
                        db = ContentDb.FromJson(rules, pack);
                    }
                    catch (Exception ex) when (IsContentError(ex))
                    {
                        report(true, location, ex.Message);
                        continue;
                    }

                    int packErrors = errors;
                    if (!SafeId(db.PackId))
                        report(true, location + " $.id", "内容包 id 只能包含英文字母、数字、下划线和连字符，且不能使用 Windows 保留文件名。");
                    else if (!seenIds.Add(db.PackId))
                        report(true, location + " $.id", "内容包 id 对应的名单文件重名（不区分大小写）：" + db.PackId);
                    PackValidator.Check(db, location, sensitiveWords, report);
                    if (writeDocs && errors == packErrors)
                    {
                        try
                        {
                            documents[db.PackId] = RosterWriter.Render(db);
                        }
                        catch (Exception ex) when (IsContentError(ex))
                        {
                            report(true, location, "名单说明生成失败：" + ex.Message);
                        }
                    }
                }

                // 先验证并渲染全部所选包，避免错误输入覆盖已有名单。
                if (writeDocs && errors == 0)
                {
                    string outputDirectory = Path.Combine(root, "docs", "roster");
                    Directory.CreateDirectory(outputDirectory);
                    foreach (var document in documents)
                    {
                        string path = Path.Combine(outputDirectory, document.Key + ".md");
                        File.WriteAllText(path, document.Value.Replace("\r\n", "\n"), new UTF8Encoding(false));
                        Console.WriteLine("已生成：docs/roster/" + document.Key + ".md");
                    }
                }
                Console.WriteLine("检查完成：" + paths.Length + " 个内容包，" + errors + " 个错误，" + warnings + " 个提醒。");
                return errors == 0 ? 0 : 1;
            }
            catch (Exception ex) when (IsContentError(ex))
            {
                Console.Error.WriteLine("[错误] PackCheck：" + ex.Message);
                return 1;
            }
        }

        private static bool SafeId(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            string upper = id.ToUpperInvariant();
            if (upper == "CON" || upper == "PRN" || upper == "AUX" || upper == "NUL" ||
                (upper.Length == 4 && (upper.StartsWith("COM", StringComparison.Ordinal) ||
                    upper.StartsWith("LPT", StringComparison.Ordinal)) && upper[3] >= '1' && upper[3] <= '9')) return false;
            return id.All(c =>
                (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
                (c >= '0' && c <= '9') || c == '_' || c == '-');
        }

        private static string FindRoot()
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "Engine", "StreamDuel.Core.csproj")) &&
                    File.Exists(Path.Combine(directory.FullName, "AGENTS.md"))) return directory.FullName;
            }
            throw new IOException("找不到仓库根目录，请在源码仓库中运行 Tools/PackCheck。");
        }

        private static string ReadJson(string path)
        {
            string text = File.ReadAllText(path);
            try
            {
                using (JsonDocument.Parse(text)) { }
            }
            catch (JsonException ex)
            {
                throw new FormatException(path + " 的 JSON 格式错误（行 " + (ex.LineNumber + 1) +
                    "，字节列 " + (ex.BytePositionInLine + 1) + "）：" + ex.Message, ex);
            }
            return text;
        }

        private static bool IsContentError(Exception ex)
        {
            return ex is KeyError || ex is IOException || ex is UnauthorizedAccessException ||
                ex is ArgumentException || ex is FormatException || ex is InvalidCastException ||
                ex is KeyNotFoundException || ex is NullReferenceException || ex is OverflowException;
        }
    }
}
