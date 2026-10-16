using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;

namespace StreamDuel.PackCheck
{
    public static partial class ArtCheck
    {
        sealed class ArtFile
        {
            public string FullPath, Relative;
        }

        static List<ArtFile> Scan(string root, Dictionary<string, SortedSet<string>> references,
            List<string> blocked, List<ArtCheckIssue> issues)
        {
            var result = new List<ArtFile>();
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                string directory = pending.Pop();
                string location = Relative(root, directory);
                string[] entries;
                try
                {
                    VerifyAncestors(directory);
                    entries = Directory.GetFileSystemEntries(directory);
                    Array.Sort(entries, StringComparer.Ordinal);
                }
                catch (Exception error) when (IsPathError(error))
                {
                    blocked.Add(location);
                    issues.Add(new ArtCheckIssue("io", "error", location, OwnersUnder(location, references),
                        "无法读取资源目录：" + PathErrorText(error)));
                    continue;
                }
                foreach (string path in entries)
                {
                    string relative = Relative(root, path);
                    try
                    {
                        FileAttributes attributes = File.GetAttributes(path);
                        if ((attributes & FileAttributes.ReparsePoint) != 0)
                        {
                            blocked.Add(relative);
                            issues.Add(new ArtCheckIssue("path", "error", relative, OwnersUnder(relative, references),
                                "不检查符号链接或重解析点，资源必须保留在资源根目录内。"));
                            continue;
                        }
                        if ((attributes & FileAttributes.Directory) != 0)
                        {
                            if (string.Equals(Path.GetExtension(relative), ".png", StringComparison.OrdinalIgnoreCase)
                                && references.TryGetValue(ResourceKey(relative), out var owners))
                            {
                                blocked.Add(relative);
                                issues.Add(new ArtCheckIssue("format", "error", relative, JoinIds(owners),
                                    "立绘路径对应文件夹，必须提供 PNG 文件。"));
                            }
                            pending.Push(path);
                        }
                        else result.Add(new ArtFile { FullPath = path, Relative = relative });
                    }
                    catch (Exception error) when (IsPathError(error))
                    {
                        blocked.Add(relative);
                        issues.Add(new ArtCheckIssue("io", "error", relative, OwnersUnder(relative, references),
                            "无法读取资源条目：" + PathErrorText(error)));
                    }
                }
            }
            result.Sort((left, right) => StringComparer.Ordinal.Compare(left.Relative, right.Relative));
            return result;
        }

        static void VerifyAncestors(string path)
        {
            var paths = new Stack<string>();
            string current = Path.GetFullPath(path);
            while (!string.IsNullOrEmpty(current))
            {
                paths.Push(current);
                current = Path.GetDirectoryName(current);
            }
            while (paths.Count > 0)
                if ((File.GetAttributes(paths.Pop()) & FileAttributes.ReparsePoint) != 0)
                    throw new ArgumentException("路径包含符号链接或重解析点。");
        }

        static void VerifyResourceKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key) || key.StartsWith("/", StringComparison.Ordinal)
                || key.IndexOf('\\') >= 0 || key.IndexOf(':') >= 0)
                throw new ArgumentException("资源键必须是根目录内的相对路径。");
            foreach (string segment in key.Split('/'))
            {
                if (segment.Length == 0 || segment == "." || segment == ".." || segment != segment.Trim()
                    || segment.EndsWith(".", StringComparison.Ordinal) || segment.Any(char.IsControl)
                    || segment.IndexOfAny(new[] { '<', '>', '"', '|', '?', '*' }) >= 0)
                    throw new ArgumentException("资源键包含不安全的路径段。");
                string device = segment.Split('.')[0].ToUpperInvariant();
                if (device == "CON" || device == "PRN" || device == "AUX" || device == "NUL"
                    || device.Length == 4 && (device.StartsWith("COM", StringComparison.Ordinal) || device.StartsWith("LPT", StringComparison.Ordinal))
                        && device[3] >= '1' && device[3] <= '9')
                    throw new ArgumentException("资源键不能使用保留设备名称。");
            }
        }

        static string Relative(string root, string path)
        {
            string relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            return relative == "." ? string.Empty : relative;
        }

        static bool IsWithin(string path, string parent)
        {
            return parent.Length == 0 || FileComparer.Equals(path, parent)
                || path.StartsWith(parent.TrimEnd('/') + "/", OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }

        static string ResourceKey(string relative)
        {
            string extension = Path.GetExtension(relative);
            return extension.Length == 0 ? relative : relative.Substring(0, relative.Length - extension.Length);
        }

        static string OwnersUnder(string relative, Dictionary<string, SortedSet<string>> references)
        {
            return JoinIds(references.Where(pair => IsWithin(pair.Key + ".png", relative)
                || FileComparer.Equals(pair.Key, ResourceKey(relative))).SelectMany(pair => pair.Value).Distinct(StringComparer.Ordinal));
        }

        static bool IgnoredFile(string relative)
        {
            string extension = Path.GetExtension(relative);
            if (extension.Equals(".meta", StringComparison.OrdinalIgnoreCase)) return true;
            if (relative.IndexOf('/') < 0 && extension.Equals(".json", StringComparison.OrdinalIgnoreCase)) return true;
            if (extension.Length > 0 && !new[] { ".md", ".txt", ".markdown", ".rst" }.Contains(extension, StringComparer.OrdinalIgnoreCase)) return false;
            string name = Path.GetFileNameWithoutExtension(relative);
            return new[] { "README", "LICENSE", "NOTICE", "CREDITS", "AUTHORS", "COPYING", "CHANGELOG",
                "制作说明", "授权说明", "资源说明", "使用说明" }.Contains(name, StringComparer.OrdinalIgnoreCase);
        }

        static bool IsPathError(Exception error)
        {
            return error is IOException || error is UnauthorizedAccessException || error is ArgumentException
                || error is NotSupportedException || error is SecurityException;
        }

        static string PathErrorText(Exception error)
        {
            if (error is FileNotFoundException || error is DirectoryNotFoundException) return "目录或文件不存在。";
            if (error is UnauthorizedAccessException || error is SecurityException) return "没有读取权限。";
            if (error is ArgumentException) return error.Message;
            if (error is NotSupportedException) return "路径格式不受支持。";
            return "文件系统读取失败，请检查访问权限或文件是否正在被占用。";
        }
    }
}
