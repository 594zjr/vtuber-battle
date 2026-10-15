using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using StreamDuel.Core;

namespace StreamDuel.Cli
{
    internal static partial class Program
    {
        private static int RunBackup(CliOptions options)
        {
            string root = BackupPath(options.SaveDir, "--save-dir");
            string archive = BackupPath(options.BackupFile ?? options.RestoreFile,
                options.BackupFile != null ? "--backup" : "--restore");
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            bool inRoot = ContainsPath(root, archive, comparison);
            bool automaticRestore = options.RestoreFile != null
                && ContainsPath(Path.Combine(root, "backups"), archive, comparison);
            if (ContainsPath(archive, root, comparison) || inRoot && !automaticRestore)
                throw new ArgumentException(options.BackupFile != null
                    ? "备份文件必须位于 --save-dir 存档目录之外，不能占用该目录或其父路径。"
                    : "恢复文件必须位于存档目录之外或其 backups 子目录内，不能读取即将被恢复替换的存档文件。");
            CheckBackupPath(root, directory: true);
            CheckBackupPath(archive, directory: false);
            string library = Path.Combine(root, "replay-library");

            if (options.BackupFile != null)
            {
                if (!Directory.Exists(root)) throw new IOException("备份失败：存档目录不存在。");
                if (File.Exists(archive)) throw new IOException("备份失败：目标文件已存在，请指定新的备份文件名。");
                var store = new FileSaveStore(root);
                string json = Backup.Export(store, library, DateTimeOffset.UtcNow);
                PublishBackup(archive, json);
                Console.WriteLine("备份已导出：" + archive);
                return 0;
            }

            string imported;
            try
            {
                if (new FileInfo(archive).Length > 64L * 1024 * 1024)
                    throw new IOException("备份文件超过 64 MiB 大小上限。");
                imported = File.ReadAllText(archive, new UTF8Encoding(false, true));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is DecoderFallbackException)
            {
                throw new IOException("读取备份失败：" + ex.Message, ex);
            }
            // 校验在构造 FileSaveStore 之前，坏备份不能顺带创建目标存档目录。
            Backup.Validate(imported);
            CheckBackupPath(root, directory: true);
            var result = Backup.Import(new FileSaveStore(root), library, imported, DateTimeOffset.UtcNow,
                options.Overwrite ? BackupImportMode.Overwrite : BackupImportMode.Merge);
            if (result.AutomaticBackupPath != null)
                Console.WriteLine("覆盖前备份已保存：" + result.AutomaticBackupPath);
            Console.WriteLine(options.Overwrite ? "存档已覆盖恢复。" : "存档已合并恢复。");
            Console.WriteLine("已导入队伍：" + result.ImportedTeams + "；回放：" + result.ImportedReplays
                + "；录像库录像：" + result.ImportedLibraryReplays + "。");
            return 0;
        }

        private static string BackupPath(string value, string option)
        {
            try
            {
                foreach (char character in value)
                    if (char.IsControl(character)) throw new ArgumentException("路径不能含控制字符。");
                if (OperatingSystem.IsWindows() && (value.StartsWith(@"\\?\", StringComparison.Ordinal)
                    || value.StartsWith(@"\\.\", StringComparison.Ordinal)))
                    throw new ArgumentException("路径必须指向普通文件或目录，不能使用设备路径。");
                // Windows 规范化会抹去尾点，必须在规范化之前拒绝文件名别名。
                if (OperatingSystem.IsWindows())
                    foreach (string segment in value.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries))
                        if (segment != "." && segment != ".."
                            && (segment.EndsWith(".", StringComparison.Ordinal) || segment.EndsWith(" ", StringComparison.Ordinal)))
                            throw new ArgumentException("文件名或目录名不能以点或空格结尾。");
                string full = Path.GetFullPath(value);
                string pathRoot = Path.GetPathRoot(full);
                foreach (string segment in full.Substring(pathRoot.Length).Split(new[]
                    { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                        throw new ArgumentException("文件名或目录名含无效字符。");
                    if (OperatingSystem.IsWindows())
                    {
                        if (segment.EndsWith(".", StringComparison.Ordinal) || segment.EndsWith(" ", StringComparison.Ordinal))
                            throw new ArgumentException("文件名或目录名不能以点或空格结尾。");
                        string stem = segment.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
                        if (stem == "CON" || stem == "PRN" || stem == "AUX" || stem == "NUL"
                            || stem == "CONIN$" || stem == "CONOUT$"
                            || stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal)
                                || stem.StartsWith("LPT", StringComparison.Ordinal))
                                && (stem[3] >= '0' && stem[3] <= '9' || stem[3] == '¹' || stem[3] == '²' || stem[3] == '³'))
                            throw new ArgumentException("不能使用系统设备名。");
                    }
                }
                if (option != "--save-dir" && (Path.EndsInDirectorySeparator(value)
                    || string.Equals(full, pathRoot, OperatingSystem.IsWindows()
                        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)))
                    throw new ArgumentException("需要文件路径，不能指定目录。");
                return Path.TrimEndingDirectorySeparator(full);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                throw new ArgumentException(option + " 路径无效：" + ex.Message, ex);
            }
        }

        private static void CheckBackupPath(string path, bool directory)
        {
            var paths = new Stack<string>();
            for (string current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
                paths.Push(current);
            while (paths.Count > 0)
            {
                string current = paths.Pop();
                FileAttributes attributes;
                try { attributes = File.GetAttributes(current); }
                catch (FileNotFoundException) { continue; }
                catch (DirectoryNotFoundException) { continue; }
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("备份相关路径不能经过符号链接或重解析点：" + current);
                bool mustBeDirectory = paths.Count > 0 || directory;
                if (((attributes & FileAttributes.Directory) != 0) != mustBeDirectory)
                    throw new IOException("备份相关路径的文件或目录类型不正确：" + current);
            }
        }

        private static void PublishBackup(string path, string json)
        {
            string directory = Path.GetDirectoryName(path);
            CheckBackupPath(directory, directory: true);
            Directory.CreateDirectory(directory);
            CheckBackupPath(path, directory: false);
            string temporary = Path.Combine(directory, ".backup-" + Guid.NewGuid().ToString("N") + ".tmp");
            bool created = false;
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    created = true;
                    using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, leaveOpen: true))
                    {
                        writer.Write(json);
                        writer.Flush();
                    }
                    stream.Flush(flushToDisk: true);
                }
                CheckBackupPath(temporary, directory: false);
                CheckBackupPath(path, directory: false);
                // 无覆盖移动让发布前出现的同名文件仍然获保留。
                File.Move(temporary, path);
                created = false;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                throw new IOException("写入备份失败：" + ex.Message, ex);
            }
            finally
            {
                if (created)
                {
                    CheckBackupPath(temporary, directory: false);
                    File.Delete(temporary);
                }
            }
        }
    }
}
