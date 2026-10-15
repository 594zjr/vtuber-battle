using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace StreamDuel.Core
{
    /// <summary>提供整体备份所需的文件存档根目录，不扩充已有存档接口。</summary>
    public interface IBackupFileStore : ISaveStore
    {
        string RootDirectory { get; }
    }

    public enum BackupImportMode { Merge, Overwrite }

    public sealed class BackupImportResult
    {
        public string AutomaticBackupPath;
        public int ImportedTeams, ImportedReplays, ImportedLibraryReplays;
    }

    public static partial class Backup
    {
        static readonly UTF8Encoding BackupUtf8 = new UTF8Encoding(false, true);

        public static string Export(ISaveStore store, string replayDir, DateTimeOffset exportedAt, bool includeReplays = true)
        {
            var paths = new BackupPaths(store, replayDir);
            using (var locks = new BackupLocks(paths, includeReplays, true))
                return SerializeSnapshot(ReadSnapshot(store, paths, exportedAt, includeReplays));
        }

        public static BackupImportResult Import(ISaveStore store, string replayDir, string json,
            DateTimeOffset importedAt, BackupImportMode mode = BackupImportMode.Merge)
        {
            // 输入的最后一个条目也必须先校验，不能边读边覆盖有效存档。
            Snapshot incoming = ParseSnapshot(json);
            if (mode != BackupImportMode.Merge && mode != BackupImportMode.Overwrite)
                throw new ArgumentException("备份导入方式无效。", nameof(mode));
            var paths = new BackupPaths(store, replayDir);
            bool readReplays = incoming.IncludeReplays || mode == BackupImportMode.Overwrite;
            Snapshot current = ReadSnapshot(store, paths, importedAt, readReplays);
            var result = new BackupImportResult();
            Snapshot merged = Combine(paths, current, incoming, mode, result);
            List<BackupChange> changes = PlanChanges(paths, current, merged, incoming.IncludeReplays);
            if (mode == BackupImportMode.Overwrite) paths.CheckDirectory(paths.Backups);
            Preflight(paths, changes);

            using (var locks = new BackupLocks(paths, readReplays))
            {
                // 与普通设置、统计、预设及录像写入共用锁，再读一次以消除等待期间的旧快照。
                current = ReadSnapshot(store, paths, importedAt, readReplays);
                merged = Combine(paths, current, incoming, mode, result);
                changes = PlanChanges(paths, current, merged, incoming.IncludeReplays);
                Preflight(paths, changes);
                string stage = paths.NewStageDirectory();
                bool preserveStage = false;
                try
                {
                    StageChanges(paths, stage, changes);
                    if (mode == BackupImportMode.Overwrite)
                        result.AutomaticBackupPath = WriteAutomaticBackup(paths, SerializeSnapshot(current), importedAt);
                    ApplyChanges(paths, changes, ref preserveStage);
                }
                finally
                {
                    // 回滚失败的旧文件留在暂存目录，不能把最后的恢复依据删除。
                    if (!preserveStage) CleanupStage(paths, stage, changes);
                }
            }
            return result;
        }

        static Snapshot ReadSnapshot(ISaveStore store, BackupPaths paths, DateTimeOffset time, bool replays)
        {
            var snapshot = new Snapshot
            {
                ExportedAt = time,
                IncludeReplays = replays,
                Settings = ValidateSettings(paths.ReadOptional(paths.Settings) ?? new GameSettings().ToJson()),
                Profile = ValidateProfile(paths.ReadOptional(paths.Profile) ?? new Profile().ToJson()),
                Teams = ValidateTeams(paths.ReadOptional(paths.Teams) ?? new TeamPresetList().ToJson())
            };
            if (replays)
            {
                int textSize = snapshot.Settings.Length + snapshot.Profile.Length + snapshot.Teams.Length;
                paths.CheckDirectory(paths.Legacy);
                paths.CheckDirectory(paths.Library);
                CheckLegacyFiles(paths);
                foreach (string id in store.ListReplays())
                {
                    ValidateReplayId(id, false);
                    string text = paths.ReadOptional(paths.ReplayPath(id, false));
                    if (text == null) throw new IOException("备份期间旧录像发生变化，请重试。");
                    textSize = checked(textSize + BackupUtf8.GetByteCount(text));
                    if (textSize > BackupJsonMaxBytes) throw new FormatException("存档内容超过 64 MiB 备份大小上限。");
                    snapshot.Replays.Add(id, text);
                }
                if (Directory.Exists(paths.Library))
                {
                    foreach (string file in Directory.EnumerateFiles(paths.Library).OrderBy(p => p, StringComparer.Ordinal))
                    {
                        if (!file.EndsWith(".json", StringComparison.Ordinal)) continue;
                        string id = Path.GetFileNameWithoutExtension(file);
                        if (string.Equals(id, "index", StringComparison.OrdinalIgnoreCase)) continue;
                        try { ValidateReplayId(id, true); }
                        catch (ArgumentException) { continue; }
                        catch (FormatException) { continue; }
                        string text = paths.ReadOptional(paths.ReplayPath(id, true))
                            ?? throw new IOException("备份期间录像库发生变化，请重试。");
                        textSize = checked(textSize + BackupUtf8.GetByteCount(text));
                        if (textSize > BackupJsonMaxBytes) throw new FormatException("存档内容超过 64 MiB 备份大小上限。");
                        snapshot.Library.Add(id, text);
                    }
                }
            }
            snapshot.Index = BuildLibraryIndex(snapshot.Library);
            return snapshot;
        }

        static void CheckLegacyFiles(BackupPaths paths)
        {
            string ownership = paths.ReadOptional(Path.Combine(paths.Legacy, ".replay-ownership.json"));
            if (ownership != null)
            {
                var entries = BackupJsonObject(BackupJsonParse(ownership), "旧录像管理登记");
                foreach (var entry in entries)
                {
                    ValidateReplayId(entry.Key, false);
                    if (!(entry.Value is string hash) || hash.Length != 64
                        || hash.Any(c => !(c >= '0' && c <= '9' || c >= 'a' && c <= 'f')))
                        throw new FormatException("旧录像管理登记已损坏，无法确认完整备份范围。");
                }
            }
            if (!Directory.Exists(paths.Legacy)) return;
            foreach (string file in Directory.EnumerateFiles(paths.Legacy))
            {
                if (!file.EndsWith(".json", StringComparison.Ordinal)) continue;
                string id = Path.GetFileNameWithoutExtension(file);
                try { ValidateReplayId(id, false); }
                catch (FormatException) { continue; }
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) continue;
                paths.CheckFile(file);
                try
                {
                    // 旧 ListReplays 会忽略不可读文件；整体备份不能把临时占用当成没有录像。
                    using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read)) { }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    throw new IOException("旧录像暂不可读，无法生成完整备份。", ex);
                }
            }
        }

        static Snapshot Combine(BackupPaths paths, Snapshot current, Snapshot incoming, BackupImportMode mode, BackupImportResult result)
        {
            bool overwrite = mode == BackupImportMode.Overwrite;
            var merged = new Snapshot
            {
                ExportedAt = incoming.ExportedAt,
                IncludeReplays = incoming.IncludeReplays,
                Settings = overwrite ? incoming.Settings : paths.ReadOptional(paths.Settings) ?? incoming.Settings,
                Profile = incoming.Profile,
                Teams = incoming.Teams
            };
            var teams = TeamPresetList.FromJson(incoming.Teams);
            result.ImportedTeams = teams.Items.Count;
            if (!overwrite)
            {
                var oldTeams = TeamPresetList.FromJson(current.Teams);
                var names = new HashSet<string>(oldTeams.Items.Select(t => t.Name), StringComparer.Ordinal);
                foreach (var team in teams.Items)
                {
                    string original = team.Name;
                    int suffix = 2;
                    while (!names.Add(team.Name)) team.Name = original + " (" + (suffix++).ToString(CultureInfo.InvariantCulture) + ")";
                    oldTeams.Items.Add(team);
                }
                merged.Teams = oldTeams.ToJson();
                var profile = Profile.FromJson(current.Profile);
                foreach (var pair in Profile.FromJson(incoming.Profile).Packs)
                    if (!profile.Packs.ContainsKey(pair.Key)) profile.Packs.Add(pair.Key, pair.Value);
                merged.Profile = profile.ToJson();
            }
            merged.Replays = MergeReplays(paths, current.Replays, incoming.Replays, overwrite, out int replayCount);
            merged.Library = MergeReplays(paths, current.Library, incoming.Library, overwrite, out int libraryCount);
            result.ImportedReplays = incoming.IncludeReplays ? replayCount : 0;
            result.ImportedLibraryReplays = incoming.IncludeReplays ? libraryCount : 0;
            merged.Index = incoming.IncludeReplays ? BuildLibraryIndex(merged.Library) : BuildLibraryIndex(new Dictionary<string, string>());
            return merged;
        }

        static Dictionary<string, string> MergeReplays(BackupPaths paths, Dictionary<string, string> current,
            Dictionary<string, string> incoming, bool overwrite, out int count)
        {
            var merged = new Dictionary<string, string>(paths.Comparer);
            if (!overwrite) foreach (var pair in current) merged.Add(pair.Key, pair.Value);
            count = 0;
            foreach (var pair in incoming)
            {
                if (merged.ContainsKey(pair.Key)) continue;
                merged.Add(pair.Key, pair.Value);
                count++;
            }
            return merged;
        }

        sealed class BackupChange
        {
            public string Path, Text, After, Before;
            public bool Existed, Applied;
        }

        static List<BackupChange> PlanChanges(BackupPaths paths, Snapshot current, Snapshot merged, bool replays)
        {
            var changes = new List<BackupChange>();
            AddChange(paths, changes, paths.Settings, merged.Settings);
            AddChange(paths, changes, paths.Profile, merged.Profile);
            AddChange(paths, changes, paths.Teams, merged.Teams);
            if (!replays) return changes;
            PlanReplayChanges(paths, changes, current.Replays, merged.Replays, false);
            PlanReplayChanges(paths, changes, current.Library, merged.Library, true);
            AddChange(paths, changes, Path.Combine(paths.Library, "index.json"), merged.Index);

            // 恢复旧版任意载荷时同步登记实际文件摘要，不调用有保留上限的 SaveReplay。
            var ownership = new StringBuilder("{");
            var writes = changes.ToDictionary(c => c.Path, paths.Comparer);
            foreach (var pair in merged.Replays.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                if (ownership.Length > 1) ownership.Append(',');
                string path = paths.ReplayPath(pair.Key, false);
                byte[] bytes = writes.ContainsKey(path) ? BackupUtf8.GetBytes(pair.Value) : File.ReadAllBytes(path);
                string hash;
                using (var sha = SHA256.Create()) hash = string.Concat(sha.ComputeHash(bytes).Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
                ownership.Append('"').Append(pair.Key).Append("\":\"").Append(hash).Append('"');
            }
            ownership.Append('}');
            AddChange(paths, changes, Path.Combine(paths.Legacy, ".replay-ownership.json"), ownership.ToString());
            return changes;
        }

        static void PlanReplayChanges(BackupPaths paths, List<BackupChange> changes, Dictionary<string, string> current,
            Dictionary<string, string> merged, bool library)
        {
            var old = new Dictionary<string, string>(current, paths.Comparer);
            foreach (var pair in merged)
            {
                string path = paths.ReplayPath(pair.Key, library);
                if (!old.ContainsKey(pair.Key) && File.Exists(path))
                    throw new IOException("恢复目标存在未受管理的同名录像，已取消导入：" + pair.Key);
                AddChange(paths, changes, path, pair.Value);
            }
            foreach (var pair in current)
                if (!merged.ContainsKey(pair.Key)) AddChange(paths, changes, paths.ReplayPath(pair.Key, library), null);
        }

        static void AddChange(BackupPaths paths, List<BackupChange> changes, string path, string text)
        {
            string old = paths.ReadOptional(path);
            if (old == text) return;
            changes.Add(new BackupChange { Path = path, Text = text, Existed = old != null });
        }

        static void Preflight(BackupPaths paths, List<BackupChange> changes)
        {
            foreach (var change in changes)
            {
                paths.CheckFile(change.Path);
                if (!File.Exists(change.Path)) continue;
                if ((File.GetAttributes(change.Path) & FileAttributes.ReadOnly) != 0)
                    throw new IOException("存档文件只读，已取消恢复。");
                using (new FileStream(change.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            }
        }

        static void StageChanges(BackupPaths paths, string stage, List<BackupChange> changes)
        {
            for (int i = 0; i < changes.Count; i++)
            {
                BackupChange change = changes[i];
                change.Before = Path.Combine(stage, i.ToString(CultureInfo.InvariantCulture) + ".before");
                change.After = Path.Combine(stage, i.ToString(CultureInfo.InvariantCulture) + ".after");
                if (change.Text != null) paths.WriteNew(change.After, change.Text);
            }
        }

        static void ApplyChanges(BackupPaths paths, List<BackupChange> changes, ref bool preserveStage)
        {
            try
            {
                foreach (var change in changes)
                {
                    paths.CheckFile(change.Path);
                    paths.CheckFile(change.After);
                    paths.CheckFile(change.Before);
                    if (change.Text == null) File.Move(change.Path, change.Before);
                    else if (change.Existed) File.Replace(change.After, change.Path, change.Before);
                    else File.Move(change.After, change.Path);
                    change.Applied = true;
                }
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                try
                {
                    for (int i = changes.Count - 1; i >= 0; i--)
                    {
                        var change = changes[i];
                        if (!change.Applied) continue;
                        paths.CheckFile(change.Path);
                        paths.CheckFile(change.Before);
                        if (!change.Existed) File.Delete(change.Path);
                        else if (File.Exists(change.Path)) File.Replace(change.Before, change.Path, null);
                        else File.Move(change.Before, change.Path);
                    }
                }
                catch (Exception rollback) when (rollback is IOException || rollback is UnauthorizedAccessException)
                {
                    preserveStage = true;
                    throw new IOException("恢复失败且回滚未完成，旧文件已保留在暂存目录；请从覆盖前自动备份恢复。", new AggregateException(error, rollback));
                }
                throw new IOException("恢复写入失败，已回滚原有存档。", error);
            }
        }

        static string WriteAutomaticBackup(BackupPaths paths, string json, DateTimeOffset time)
        {
            paths.CheckDirectory(paths.Backups);
            Directory.CreateDirectory(paths.Backups);
            string prefix = "before-restore-" + time.ToUniversalTime().ToString("yyyyMMdd-HHmmss-fffffff", CultureInfo.InvariantCulture) + "-";
            for (int i = 1; ; i++)
            {
                string path = Path.Combine(paths.Backups, prefix + i.ToString(CultureInfo.InvariantCulture) + ".json");
                paths.CheckFile(path);
                if (File.Exists(path)) continue;
                paths.WriteNew(path, json);
                return path;
            }
        }

        static void CleanupStage(BackupPaths paths, string stage, List<BackupChange> changes)
        {
            foreach (var change in changes)
            {
                if (change.After != null) { paths.CheckFile(change.After); File.Delete(change.After); }
                if (change.Before != null) { paths.CheckFile(change.Before); File.Delete(change.Before); }
            }
            paths.CheckDirectory(stage);
            Directory.Delete(stage, false);
        }

        sealed class BackupPaths
        {
            public readonly string Root, Settings, Profile, Teams, Legacy, Library, Backups;
            public readonly StringComparer Comparer = Path.DirectorySeparatorChar == '\\' ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            public readonly StringComparison Comparison = Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            readonly string prefix;

            public BackupPaths(ISaveStore store, string replayDir)
            {
                if (!(store is IBackupFileStore files)) throw new ArgumentException("整体备份需要支持根目录的文件存档。", nameof(store));
                if (string.IsNullOrWhiteSpace(files.RootDirectory) || string.IsNullOrWhiteSpace(replayDir))
                    throw new ArgumentException("备份存档及录像目录不能为空。");
                CheckRawDirectory(files.RootDirectory);
                CheckRawDirectory(replayDir);
                Root = Path.GetFullPath(files.RootDirectory);
                if (Root.Length > Path.GetPathRoot(Root).Length)
                    Root = Root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                prefix = Root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                Library = Path.GetFullPath(replayDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                Settings = Path.Combine(Root, "settings.json");
                Profile = Path.Combine(Root, "profile.json");
                Teams = Path.Combine(Root, "teams.json");
                Legacy = Path.Combine(Root, "replays");
                Backups = Path.Combine(Root, "backups");
                if (!Library.StartsWith(prefix, Comparison) || Overlaps(Library, Legacy) || Overlaps(Library, Backups)
                    || Overlaps(Library, Settings) || Overlaps(Library, Profile) || Overlaps(Library, Teams)
                    || Path.GetRelativePath(Root, Library).Split(Path.DirectorySeparatorChar)[0].StartsWith(".", StringComparison.Ordinal))
                    throw new ArgumentException("录像库必须是存档根目录内的独立子目录，不能与旧录像或备份目录重叠。");
                CheckDirectory(Root);
                CheckDirectory(Library);
                CheckDirectory(Legacy);
                CheckFile(Settings); CheckFile(Profile); CheckFile(Teams);
                if (!Directory.Exists(Root)) throw new DirectoryNotFoundException("存档根目录不存在。");
            }

            static void CheckRawDirectory(string value)
            {
                if (value.StartsWith(@"\\?\", StringComparison.Ordinal) || value.StartsWith(@"\\.\", StringComparison.Ordinal))
                    throw new ArgumentException("备份目录不能使用设备路径。");
                foreach (string part in value.Substring(Path.GetPathRoot(value).Length).Split(
                    new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries))
                    if (part.EndsWith(".", StringComparison.Ordinal) || part.EndsWith(" ", StringComparison.Ordinal)
                        || part.Any(char.IsControl) || part.IndexOf(':') >= 0)
                        throw new ArgumentException("备份目录不能包含别名、控制字符或不安全的路径分段。");
            }

            bool Overlaps(string left, string right)
            {
                return string.Equals(left, right, Comparison) || left.StartsWith(right + Path.DirectorySeparatorChar, Comparison)
                    || right.StartsWith(left + Path.DirectorySeparatorChar, Comparison);
            }

            public string ReplayPath(string id, bool library)
            {
                ValidateReplayId(id, library);
                string path = Path.Combine(library ? Library : Legacy, id + ".json");
                CheckFile(path);
                return path;
            }

            public string ReadOptional(string path)
            {
                CheckFile(path);
                try
                {
                    if (new FileInfo(path).Length > 64 * 1024 * 1024) throw new FormatException("存档文件超过备份大小上限。");
                    return File.ReadAllText(path, BackupUtf8);
                }
                catch (FileNotFoundException) { return null; }
                catch (DirectoryNotFoundException) { return null; }
                catch (DecoderFallbackException ex) { throw new FormatException("存档文件不是有效的 UTF-8 文本。", ex); }
            }

            public void CheckDirectory(string path) { CheckPath(path, true); }
            public void CheckFile(string path) { CheckPath(path, false); }

            void CheckPath(string path, bool directory)
            {
                string full = Path.GetFullPath(path);
                if (!string.Equals(full, Root, Comparison) && !full.StartsWith(prefix, Comparison))
                    throw new IOException("备份路径越过存档根目录。");
                for (string current = full; current != null; current = Path.GetDirectoryName(current))
                {
                    string segment = Path.GetFileName(current);
                    if (segment.Length > 0 && (segment.EndsWith(".", StringComparison.Ordinal) || segment.EndsWith(" ", StringComparison.Ordinal)
                        || segment.IndexOf(':') >= 0)) throw new IOException("备份路径包含不安全的目录或文件名。");
                    try
                    {
                        var attributes = File.GetAttributes(current);
                        if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("备份路径不能经过链接或重解析点。");
                        bool expectedDirectory = !string.Equals(current, full, Comparison) || directory;
                        if (((attributes & FileAttributes.Directory) != 0) != expectedDirectory)
                            throw new IOException("备份路径的文件或目录类型不正确。");
                    }
                    catch (FileNotFoundException) { }
                    catch (DirectoryNotFoundException) { }
                    if (string.Equals(current, Path.GetPathRoot(current), Comparison)) break;
                }
            }

            public string NewStageDirectory()
            {
                for (int i = 1; ; i++)
                {
                    string path = Path.Combine(Root, ".backup-restore-" + i.ToString(CultureInfo.InvariantCulture));
                    CheckDirectory(path);
                    if (Directory.Exists(path)) continue;
                    Directory.CreateDirectory(path);
                    return path;
                }
            }

            public void WriteNew(string path, string text)
            {
                CheckFile(path);
                bool created = false;
                try
                {
                    using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        created = true;
                        byte[] bytes = BackupUtf8.GetBytes(text);
                        stream.Write(bytes, 0, bytes.Length);
                        stream.Flush(true);
                    }
                }
                catch
                {
                    if (created) { CheckFile(path); File.Delete(path); }
                    throw;
                }
            }
        }

        sealed class BackupLocks : IDisposable
        {
            readonly BackupPaths paths;
            readonly List<FileStream> streams = new List<FileStream>();
            readonly bool readOnly;

            public BackupLocks(BackupPaths paths, bool replays, bool readOnly = false)
            {
                this.paths = paths;
                this.readOnly = readOnly;
                try
                {
                    Lock(Path.Combine(paths.Root, ".backup.lock"));
                    Lock(Path.Combine(paths.Root, ".settings.lock"));
                    Lock(Path.Combine(paths.Root, ".profile.lock"));
                    Lock(Path.Combine(paths.Root, ".teams.lock"));
                    if (replays)
                    {
                        if (!readOnly)
                        {
                            CreateDirectory(paths.Legacy);
                            CreateDirectory(paths.Library);
                        }
                        Lock(Path.Combine(paths.Legacy, ".replays.lock"));
                        Lock(Path.Combine(paths.Library, ".library.lock"));
                    }
                }
                catch { Dispose(); throw; }
            }

            void CreateDirectory(string directory)
            {
                paths.CheckDirectory(directory);
                Directory.CreateDirectory(directory);
            }

            void Lock(string path)
            {
                paths.CheckFile(path);
                if (readOnly && !File.Exists(path)) return;
                streams.Add(new FileStream(path, readOnly ? FileMode.Open : FileMode.OpenOrCreate,
                    readOnly ? FileAccess.Read : FileAccess.ReadWrite, FileShare.None));
            }

            public void Dispose()
            {
                for (int i = streams.Count - 1; i >= 0; i--) streams[i].Dispose();
                // 锁文件保持稳定：关闭后删同名文件会破坏其它写入者刚取得的互斥。
            }
        }
    }
}
