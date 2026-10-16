using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using StreamDuel.Core;

namespace StreamDuel.PackCheck
{
    internal static partial class Program
    {
        static int RunArt(string[] args)
        {
            bool json = args.Contains("--json");
            string directory = string.Empty;
            ArtCheckReport result;
            try
            {
                string packId = null, packDirectory = null;
                var seen = new HashSet<string>(StringComparer.Ordinal);
                for (int index = 0; index < args.Length; index++)
                {
                    string option = args[index];
                    if (!seen.Add(option)) throw new ArgumentException("选项不能重复：" + option);
                    switch (option)
                    {
                        case "--art": directory = ArtArgument(args, ref index, option); break;
                        case "--pack": packId = ArtArgument(args, ref index, option); break;
                        case "--pack-dir": packDirectory = ArtArgument(args, ref index, option); break;
                        case "--json": break;
                        case "--write-docs":
                        case "--list-packs":
                            throw new ArgumentException("--art 是只读资源检查，不能与 " + option + " 合用。");
                        default: throw new ArgumentException("美术检查不支持选项：" + option + "；使用 --help 查看用法。");
                    }
                }
                if (!seen.Contains("--art")) throw new ArgumentException("--json 必须与 --art <资源根目录> 合用。");
                directory = PlainArtDirectory(directory);
                string root = FindRoot();
                string resources = Path.Combine(root, "UnityProject", "Assets", "StreamDuel", "Resources");
                string rules = ReadJson(Path.Combine(resources, "StreamDuelRules.json"));
                var policy = PackPolicy.Load(Path.Combine(resources, "PackPolicy.json"));
                string source = packDirectory != null ? PlainArtDirectory(packDirectory)
                    : ArtPackFiles(directory).Count > 0 ? directory : PlainArtDirectory(Path.Combine(resources, "Packs"));
                var files = ArtPackFiles(source);
                if (files.Count == 0) throw new IOException("内容包目录没有 JSON 文件：" + source);

                var entries = new List<PackLoadResult>();
                foreach (string file in files)
                {
                    try
                    {
                        RejectArtLink(file);
                        entries.Add(PackLoader.LoadJson(rules, ReadJson(file), Path.GetFileName(file), policy));
                    }
                    catch (Exception error) when (IsContentError(error))
                    {
                        entries.Add(new PackLoadResult { FileName = Path.GetFileName(file), Error = "无法读取内容包：" + error.Message });
                    }
                }
                if (packId != null)
                {
                    var matches = entries.Where(entry => entry.PackId == packId).ToList();
                    if (matches.Count == 0)
                        matches = entries.Where(entry => string.Equals(Path.GetFileNameWithoutExtension(entry.FileName),
                            packId, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (matches.Count == 0) throw new ArgumentException("所选内容包目录中找不到「" + packId + "」。");
                    entries = matches;
                }
                var issues = new List<ArtCheckIssue>();
                var databases = new List<ContentDb>();
                var ids = new HashSet<string>(StringComparer.Ordinal);
                foreach (var entry in entries)
                {
                    if (entry.IsDisabled)
                        issues.Add(new ArtCheckIssue("disabled", "info", entry.FileName, entry.PackId,
                            "内容包已下架，不检查其美术文件：" + entry.Error));
                    else if (entry.Db == null)
                        issues.Add(new ArtCheckIssue("manifest", "error", entry.FileName, entry.PackId,
                            "内容包清单无效：" + entry.Error));
                    else if (!ids.Add(entry.Db.PackId))
                        issues.Add(new ArtCheckIssue("manifest", "error", entry.FileName, entry.Db.PackId,
                            "内容包 id 重复，无法确定资源清单。"));
                    else databases.Add(entry.Db);
                }
                if (databases.Count > 0)
                {
                    var inspected = ArtCheck.Check(databases, directory);
                    issues.AddRange(inspected.Issues);
                }
                result = new ArtCheckReport(directory, databases.Select(db => db.PackId).OrderBy(id => id, StringComparer.Ordinal).ToArray(),
                    issues.OrderBy(issue => issue.Path, StringComparer.Ordinal).ThenBy(issue => issue.Id, StringComparer.Ordinal)
                        .ThenBy(issue => issue.Code, StringComparer.Ordinal).ToArray());
            }
            catch (Exception error) when (IsContentError(error))
            {
                result = new ArtCheckReport(directory, Array.Empty<string>(), new[]
                {
                    new ArtCheckIssue("input", "error", string.Empty, string.Empty, "美术检查无法完成：" + error.Message)
                });
            }
            WriteArtReport(result, json);
            return result.Passed ? 0 : 1;
        }

        static string ArtArgument(string[] args, ref int index, string option)
        {
            if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]) || args[index].StartsWith("-", StringComparison.Ordinal))
                throw new ArgumentException(option + " 需要非空参数值。");
            return args[index];
        }

        static string PlainArtDirectory(string path)
        {
            if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal))
                throw new ArgumentException("美术检查目录不能使用设备路径。");
            string full = Path.GetFullPath(path);
            if (!Directory.Exists(full)) throw new IOException("资源或内容包目录不存在：" + full);
            for (var current = new DirectoryInfo(full); current != null; current = current.Parent)
                RejectArtLink(current.FullName);
            return full;
        }

        static void RejectArtLink(string path)
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("只读美术检查不跟随符号链接或目录联接：" + path);
        }

        static List<string> ArtPackFiles(string directory)
        {
            return Directory.GetFiles(directory, "*", SearchOption.TopDirectoryOnly)
                .Where(path => string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(Path.GetFileName(path), "StreamDuelRules.json", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(Path.GetFileName(path), "PackPolicy.json", StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path, StringComparer.Ordinal).ToList();
        }

        static void WriteArtReport(ArtCheckReport report, bool json)
        {
            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                    WriteIndented = true
                }));
                return;
            }
            Console.WriteLine("美术文件检查：" + report.Root);
            foreach (var issue in report.Issues)
            {
                string level = issue.Level == "error" ? "错误" : issue.Level == "warn" ? "提醒" : "说明";
                string location = string.IsNullOrEmpty(issue.Path) ? issue.Id : issue.Path;
                Console.WriteLine("[" + level + "] " + location + "：" + issue.Message);
            }
            Console.WriteLine((report.Passed ? "通过" : "未通过") + "：" + report.PackIds.Count + " 个内容包，"
                + report.ErrorCount + " 个错误，" + report.WarningCount + " 个提醒。");
            Console.WriteLine(report.Reminder);
        }
    }
}
