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
        private sealed class PackEntry
        {
            public PackLoadResult Result;
            public string Path;
            public bool BuiltIn;
        }

        private static int Main(string[] args)
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            try
            {
                string packId = null;
                string packDirectory = null;
                bool writeDocs = false;
                bool listPacks = false;
                bool listCodes = false;
                for (int i = 0; i < args.Length; i++)
                {
                    switch (args[i])
                    {
                        case "--help":
                        case "-h":
                            Console.WriteLine("用法：dotnet run --project Tools/PackCheck -- [--pack <id>] [--pack-dir <文件夹>] [--list-packs | --write-docs | --codes]");
                            Console.WriteLine("默认检查全部内容包；--pack 按包 id 选择，兼容内置文件名（不含 .json）。错误返回 1，提醒不影响退出码。");
                            Console.WriteLine("文本检查逐条输出 Level、Where、Id、Message，并汇总 error/warn；任何文本 error 都会阻止名单生成。");
                            Console.WriteLine("--pack-dir 加载指定文件夹中的 JSON（不递归）；--list-packs 列出内置和外部包后退出，id 冲突时内置优先并提示。");
                            Console.WriteLine("--write-docs 在所选内容包全部通过检查后，更新对应 docs/roster/<packId>.md。");
                            Console.WriteLine("--codes 按已加载的全部技能和人设定义报告代码引用及未使用代码，可用 --pack 筛选；不能与 --list-packs 或 --write-docs 同用。");
                            return 0;
                        case "--pack":
                            if (++i >= args.Length || string.IsNullOrWhiteSpace(args[i]) || args[i].StartsWith("--", StringComparison.Ordinal) || args[i] == "-h")
                                throw new ArgumentException("--pack 需要非空内容包 id。");
                            packId = args[i];
                            break;
                        case "--pack-dir":
                            if (++i >= args.Length || string.IsNullOrWhiteSpace(args[i]) || args[i].StartsWith("--", StringComparison.Ordinal) || args[i] == "-h")
                                throw new ArgumentException("--pack-dir 需要非空文件夹路径。");
                            packDirectory = args[i];
                            break;
                        case "--list-packs":
                            listPacks = true;
                            break;
                        case "--write-docs":
                            writeDocs = true;
                            break;
                        case "--codes":
                            listCodes = true;
                            break;
                        default:
                            throw new ArgumentException("未知选项：" + args[i] + "；使用 --help 查看用法。");
                    }
                }

                if (listCodes && (listPacks || writeDocs))
                    throw new ArgumentException("--codes 不能与 --list-packs 或 --write-docs 同用。");
                if (listCodes && packDirectory != null && !Directory.Exists(packDirectory))
                    throw new IOException("代码使用报告的外部内容包目录不存在或无法访问：" + packDirectory);

                string root = FindRoot();
                string resources = Path.Combine(root, "UnityProject", "Assets", "StreamDuel", "Resources");
                string rulesPath = Path.Combine(resources, "StreamDuelRules.json");
                string rules = ReadJson(rulesPath);
                string packsDirectory = Path.Combine(resources, "Packs");
                var policy = PackPolicy.Load(Path.Combine(resources, "PackPolicy.json"));
                Console.WriteLine("下架策略：" + policy.DisabledPacks.Count + " 个内容包，" + policy.DisabledStreamers.Count + " 位主播。");
                foreach (string id in policy.DisabledPacks) Console.WriteLine("[下架] " + policy.PackReason(id));
                foreach (string id in policy.DisabledStreamers) Console.WriteLine("[下架] " + policy.StreamerReason(id));
                var documents = new SortedDictionary<string, string>(StringComparer.Ordinal);
                var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int errors = 0;
                int warnings = 0;
                int lintErrors = 0;
                int lintWarnings = 0;
                Action<bool, string, string> report = (error, location, message) =>
                {
                    if (error) errors++; else warnings++;
                    Console.WriteLine((error ? "[错误] " : "[提醒] ") + location + "：" + message);
                };

                var entries = LoadWithPolicyDiagnostics(rules, packsDirectory, policy)
                    .Select(result => new PackEntry { Result = result, Path = Path.Combine(packsDirectory, result.FileName), BuiltIn = true }).ToList();
                var builtInIds = new HashSet<string>(entries.Where(entry => entry.Result.Db != null)
                    .Select(entry => entry.Result.Db.PackId), StringComparer.Ordinal);
                if (packDirectory != null)
                {
                    foreach (var result in LoadWithPolicyDiagnostics(rules, packDirectory, policy))
                    {
                        string path = Path.Combine(packDirectory, result.FileName);
                        if (result.Db != null && builtInIds.Contains(result.Db.PackId))
                            report(false, path, "外部内容包 id「" + result.Db.PackId + "」与内置包重复，已使用内置包。");
                        else entries.Add(new PackEntry { Result = result, Path = path });
                    }
                }
                if (packId != null && !listPacks)
                {
                    var matches = entries.Where(entry => entry.Result.PackId == packId).ToList();
                    if (matches.Count == 0)
                        matches = entries.Where(entry => entry.BuiltIn &&
                            string.Equals(Path.GetFileNameWithoutExtension(entry.Result.FileName), packId, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (matches.Count == 0)
                        matches = entries.Where(entry => !entry.BuiltIn && entry.Result.Db == null &&
                            Path.GetFileNameWithoutExtension(entry.Result.FileName) == packId).ToList();
                    if (matches.Count == 0) throw new ArgumentException("找不到内容包「" + packId + "」；使用 --list-packs 查看可用内容包。");
                    entries = matches;
                }
                if (entries.Count == 0) throw new IOException("内容包目录没有 JSON 文件：" + packsDirectory);
                foreach (var entry in entries)
                {
                    if (entry.Result.Db == null) continue;
                    try
                    {
                        // 独立复核语法，保留检查工具原有的标准 JSON 验证。
                        ReadJson(entry.Path);
                    }
                    catch (Exception ex) when (IsContentError(ex))
                    {
                        entry.Result.Db = null;
                        entry.Result.Error = ex.Message;
                    }
                }
                if (listPacks)
                {
                    foreach (var entry in entries)
                    {
                        var result = entry.Result;
                        if (result.IsDisabled) Console.WriteLine("[下架] " + result.FileName + "：" + result.Error);
                        else if (result.Db == null) report(true, entry.Path, result.Error);
                        else Console.WriteLine(result.Db.PackId + "：" + result.Db.PackName + "〔" + (entry.BuiltIn ? "内置" : "外部") + "〕（" + result.FileName + "），内容指纹：" + result.Db.ContentHash());
                    }
                    return errors == 0 ? 0 : 1;
                }

                if (listCodes)
                {
                    foreach (var entry in entries)
                        if (entry.Result.Db == null) report(true, entry.Path, entry.Result.Error);
                    if (errors > 0)
                    {
                        Console.WriteLine("代码使用报告未生成：所选内容包加载失败，无法判断代码是否未使用。");
                        return 1;
                    }
                    Console.Write(CodeUsageReport.Render(entries.Select(entry => entry.Result.Db)));
                    return 0;
                }

                string[] sensitiveWords = File.ReadAllLines(Path.Combine(root, "Tools", "PackCheck", "sensitive_words.txt"))
                    .Select(line => line.Trim()).Where(line => line.Length > 0 && !line.StartsWith("#", StringComparison.Ordinal))
                    .Distinct(StringComparer.Ordinal).ToArray();
                string[] bannedWords = File.ReadAllLines(Path.Combine(resources, "Lint", "banned.txt"));
                foreach (var entry in entries)
                {
                    string location = Path.GetRelativePath(root, Path.GetFullPath(entry.Path)).Replace('\\', '/');
                    if (entry.Result.IsDisabled)
                    {
                        Console.WriteLine("[下架] " + location + "：" + entry.Result.Error);
                        continue;
                    }
                    var db = entry.Result.Db;
                    if (db == null)
                    {
                        report(true, location, entry.Result.Error);
                        continue;
                    }

                    int packErrors = errors;
                    Console.WriteLine("内容包 " + db.PackId + " 内容指纹：" + db.ContentHash());
                    if (writeDocs && !SafeId(db.PackId))
                        report(true, location + " $.id", "生成名单时内容包 id 只能包含英文字母、数字、下划线和连字符，且不能使用 Windows 保留文件名。");
                    else if (writeDocs && !seenIds.Add(db.PackId))
                        report(true, location + " $.id", "内容包 id 对应的名单文件重名（不区分大小写）：" + db.PackId);
                    PackValidator.Check(db, location, sensitiveWords, report);
                    foreach (var issue in TextLint.Check(db, bannedWords))
                    {
                        if (issue.Level == "error") { errors++; lintErrors++; }
                        else if (issue.Level == "warn") { warnings++; lintWarnings++; }
                        else throw new ArgumentException("文本检查返回了未知级别：" + issue.Level);
                        Console.WriteLine("[文本] Level=" + issue.Level + " Where=" + location + ":" + issue.Where
                            + " Id=" + issue.Id + " Message=" + issue.Message);
                    }
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
                Console.WriteLine("文本检查汇总：" + lintErrors + " 个 error，" + lintWarnings + " 个 warn。");
                Console.WriteLine("检查完成：" + entries.Count + " 个内容包（其中 "
                    + entries.Count(entry => entry.Result.IsDisabled) + " 个已下架），" + errors + " 个错误，" + warnings + " 个提醒。");
                return errors == 0 ? 0 : 1;
            }
            catch (Exception ex) when (IsContentError(ex))
            {
                Console.Error.WriteLine("[错误] PackCheck：" + ex.Message);
                return 1;
            }
        }

        private static List<PackLoadResult> LoadWithPolicyDiagnostics(string rules, string directory, PackPolicy policy)
        {
            // 普通列表省略下架包；检查工具补回诊断，但沿用目录加载器的重复 id 校验。
            var results = PackLoader.LoadDirectory(rules, directory, policy);
            if (!Directory.Exists(directory) || results.Any(result => result.FileName == directory)) return results;
            var visibleFiles = new HashSet<string>(results.Select(result => result.FileName), StringComparer.Ordinal);
            foreach (string path in Directory.GetFiles(directory, "*", SearchOption.TopDirectoryOnly)
                .Where(path => string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal))
            {
                string name = Path.GetFileName(path);
                if (visibleFiles.Contains(name)) continue;
                var result = PackLoader.LoadJson(rules, File.ReadAllText(path), name, policy);
                results.Add(result);
            }
            return results.OrderBy(result => result.FileName, StringComparer.Ordinal).ToList();
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
