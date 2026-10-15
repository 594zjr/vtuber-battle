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
                path = Path.GetFullPath(options.ReportFile);
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
            if (options.ReplayFile != null && string.Equals(path, Path.GetFullPath(options.ReplayFile), comparison))
                throw new ArgumentException("--report 不能与 --replay 指向同一文件，请为战报指定另一个路径。");
            if (options.ReplayFile == null && string.Equals(path,
                Path.GetFullPath(Path.Combine(options.SaveDir, "profile.json")), comparison))
                throw new ArgumentException("--report 不能与玩家档案 profile.json 指向同一文件，请为战报指定另一个路径。");
            return path;
        }

        private static void SaveReport(Battle battle, CliOptions options)
        {
            string path = ValidateReportPath(options);
            if (path == null) return;
            string markdown = BattleReport.Markdown(battle);
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
