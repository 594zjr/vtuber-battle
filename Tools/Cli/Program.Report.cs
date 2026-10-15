using System;
using System.IO;
using System.Text;
using StreamDuel.Core;

namespace StreamDuel.Cli
{
    internal static partial class Program
    {
        private static string ValidateReportPath(CliOptions options)
        {
            if (options.ReportFile == null) return null;
            string path;
            try
            {
                path = ReportPath(options.ReportFile);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                throw new ArgumentException("--report 战报文件路径无效：" + ex.Message, ex);
            }
            string fileName = Path.GetFileName(path);
            if (Path.EndsInDirectorySeparator(options.ReportFile) || string.IsNullOrEmpty(fileName)
                || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || Directory.Exists(path))
                throw new ArgumentException("--report 需要有效的文件路径，不能指定目录。");

            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (options.ReplayFile != null && IsReplayFile(options.ReplayFile)
                && string.Equals(path, ReportPath(options.ReplayFile), comparison))
                throw new ArgumentException("--report 不能与 --replay 指向同一文件，请为战报指定另一个路径。");
            string libraryDirectory = ReportPath(ReplayLibraryDirectory(options));
            if (ContainsPath(libraryDirectory, path, comparison) || ContainsPath(path, libraryDirectory, comparison))
                throw new ArgumentException("--report 不能占用录像库 replay-library 目录、其父目录或其中的文件，请为战报指定另一个路径。");
            foreach (string reserved in new[] { "settings.json", ".settings.json.tmp", ".settings.lock",
                "teams.json", ".teams.json.tmp", ".teams.lock",
                Path.Combine("replays", ".replay-ownership.json"), Path.Combine("replays", ".replay-ownership.json.tmp") })
            {
                if (ContainsPath(ReportPath(Path.Combine(options.SaveDir, reserved)), path, comparison))
                    throw new ArgumentException("--report 不能占用存档保留路径「" + reserved + "」，请为战报指定另一个路径。");
            }
            if (options.ReplayFile == null)
            {
                string saveDirectory = ReportPath(options.SaveDir);
                string replayDirectory = ReportPath(Path.Combine(options.SaveDir, "replays"));
                if (ContainsPath(path, saveDirectory, comparison) || ContainsPath(path, replayDirectory, comparison))
                    throw new ArgumentException("--report 不能占用存档或回放目录及其父目录，请为战报指定另一个路径。");
                // 存档流程会重写临时文件并持有锁，战报不能占用这些路径或把它们创建成目录。
                foreach (string reserved in new[] { "profile.json", ".profile.json.tmp", ".profile.lock",
                    Path.Combine("replays", ".replay.json.tmp"), Path.Combine("replays", ".replays.lock") })
                {
                    if (ContainsPath(ReportPath(Path.Combine(options.SaveDir, reserved)), path, comparison))
                        throw new ArgumentException("--report 不能占用存档保留路径「" + reserved + "」，请为战报指定另一个路径。");
                }
            }
            return path;
        }

        private static string ReportPath(string value)
        {
            string path = Path.GetFullPath(value);
            if (OperatingSystem.IsWindows())
            {
                // 扩展前缀和普通路径可指向同一文件，比较前统一为普通绝对文件路径。
                if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)
                    || path.StartsWith(@"\\.\UNC\", StringComparison.OrdinalIgnoreCase)) path = @"\\" + path.Substring(8);
                else if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal))
                {
                    path = path.Substring(4);
                    if (path.Length < 3 || !char.IsLetter(path[0]) || path[1] != ':' || path[2] != '\\')
                        throw new ArgumentException("战报相关路径必须指向普通文件，不能使用设备路径。");
                }
                path = Path.GetFullPath(path);
                // 普通 Windows 路径会忽略段末尾的点或空格，拒绝这种别名才能可靠保护存档目录。
                string root = Path.GetPathRoot(path);
                foreach (string segment in path.Substring(root.Length).Split(Path.DirectorySeparatorChar))
                {
                    if (segment.EndsWith(".", StringComparison.Ordinal) || segment.EndsWith(" ", StringComparison.Ordinal))
                        throw new ArgumentException("战报相关路径的文件名或目录名不能以点或空格结尾。");
                }
            }
            return Path.TrimEndingDirectorySeparator(path);
        }

        private static bool ContainsPath(string parent, string child, StringComparison comparison)
        {
            return string.Equals(parent, child, comparison)
                || child.StartsWith(Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar, comparison);
        }

        private static void SaveReport(Battle battle, CliOptions options, int viewerSide = 0)
        {
            string path = ValidateReportPath(options);
            if (path == null) return;
            string markdown = BattleReport.Markdown(TextInput.ForDisplay(battle), viewerSide);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, markdown, new UTF8Encoding(false));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                throw new IOException("战报保存失败：" + ex.Message, ex);
            }
            Console.WriteLine("战报已保存：" + options.ReportFile);
        }
    }
}
