using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace StreamDuel.Core
{
    /// <summary>新回放已经写入，但保留数量尚未收敛；调用者仍应记录本局战绩。</summary>
    public sealed class ReplayRetentionException : IOException
    {
        public string ReplayName { get; }

        internal ReplayRetentionException(string name, Exception inner)
            : base("回放已保存，但旧回放清理失败，暂未满足最多 50 份：" + inner.Message, inner)
        {
            ReplayName = name;
        }
    }

    public sealed class FileSaveStore : ISaveStore
    {
        readonly string root;
        readonly string rootPrefix;
        readonly StringComparison pathComparison;
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

        public FileSaveStore(string rootDirectory)
        {
            if (string.IsNullOrWhiteSpace(rootDirectory)) throw new ArgumentException("存档根目录不能为空。", nameof(rootDirectory));
            root = Path.GetFullPath(rootDirectory);
            rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            pathComparison = Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            EnsureRoot();
        }

        public GameSettings LoadSettings()
        {
            return GameSettings.FromJson(ReadOptional("settings.json"));
        }

        public void SaveSettings(GameSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            string json = settings.ToJson();
            using (OpenLock(".settings.lock")) WriteAtomic("settings.json", ".settings.json.tmp", json);
        }

        public Profile LoadProfile()
        {
            return Profile.FromJson(ReadOptional("profile.json"));
        }

        public void SaveProfile(Profile profile)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            string json = profile.ToJson();
            using (OpenLock(".profile.lock")) WriteAtomic("profile.json", ".profile.json.tmp", json);
        }

        // 读改写必须持有同一把锁，多个本机客户端才不会互相覆盖累计战绩。
        public Profile RecordProfile(Battle battle, int playerSide = 0)
        {
            using (OpenLock(".profile.lock"))
            {
                var profile = LoadProfile();
                profile.Record(battle, playerSide);
                WriteAtomic("profile.json", ".profile.json.tmp", profile.ToJson());
                return profile;
            }
        }

        public void SaveReplay(string name, string json)
        {
            ValidateName(name);
            if (json == null) throw new ArgumentNullException(nameof(json));
            EnsureReplayDirectory();
            using (OpenLock(Path.Combine("replays", ".replays.lock"))) SaveReplayLocked(name, json);
        }

        /// <summary>为已完成的对局生成不冲突的本地文件名，再按统一保留规则保存。</summary>
        public string SaveReplay(Battle battle)
        {
            if (battle == null) throw new ArgumentNullException(nameof(battle));
            if (battle.Phase != Phase.Over) throw new InvalidOperationException("对战尚未结束，不能自动保存回放。");
            string json = battle.Record.ToJson();
            string packId = FilePackId(battle.Db.PackId);
            EnsureReplayDirectory();
            using (OpenLock(Path.Combine("replays", ".replays.lock")))
            {
                // 时间只用于本地文件名与保留顺序，不参与战斗结算。
                DateTime timestamp = DateTime.UtcNow;
                string name;
                do
                {
                    name = timestamp.ToString("yyyyMMdd_HHmmss_fffffff", CultureInfo.InvariantCulture) + "_" + packId;
                    timestamp = timestamp.AddTicks(1);
                }
                while (File.Exists(CheckedPath(Path.Combine("replays", name + ".json"))));
                SaveReplayLocked(name, json);
                return name;
            }
        }

        public List<string> ListReplays()
        {
            return ReplayFiles().Select(file => Path.GetFileNameWithoutExtension(file.Name)).ToList();
        }

        public string LoadReplay(string name)
        {
            ValidateName(name);
            string path = CheckedPath(Path.Combine("replays", name + ".json"));
            try { return File.ReadAllText(path, Utf8); }
            catch (DirectoryNotFoundException ex) { throw new FileNotFoundException("找不到回放：" + name, path, ex); }
        }

        void SaveReplayLocked(string name, string json)
        {
            var files = ReplayFiles();
            DateTime timestamp = DateTime.UtcNow;
            // 覆盖、短间隔保存和时钟回拨都仍应把新保存的一份排在最前。
            if (files.Count > 0 && files[0].LastWriteTimeUtc >= timestamp)
                timestamp = files[0].LastWriteTimeUtc.AddMilliseconds(1);
            string relative = Path.Combine("replays", name + ".json");
            WriteAtomic(relative, Path.Combine("replays", ".replay.json.tmp"), json);
            try
            {
                File.SetLastWriteTimeUtc(CheckedPath(relative), timestamp);
                foreach (var file in ReplayFiles().Skip(50))
                    File.Delete(CheckedPath(Path.Combine("replays", file.Name)));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                throw new ReplayRetentionException(name, ex);
            }
        }

        List<FileInfo> ReplayFiles()
        {
            string directory = CheckedPath("replays", true);
            if (!Directory.Exists(directory)) return new List<FileInfo>();
            return new DirectoryInfo(directory).EnumerateFiles()
                .Where(file => file.Name.EndsWith(".json", StringComparison.Ordinal)
                    && IsValidName(Path.GetFileNameWithoutExtension(file.Name))
                    && (file.Attributes & FileAttributes.ReparsePoint) == 0)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ThenByDescending(file => file.Name, StringComparer.Ordinal).ToList();
        }

        string ReadOptional(string relative)
        {
            string path = CheckedPath(relative);
            try { return File.ReadAllText(path, Utf8); }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
        }

        void WriteAtomic(string relative, string temporaryRelative, string json)
        {
            string path = CheckedPath(relative), temporary = CheckedPath(temporaryRelative);
            bool created = false;
            try
            {
                using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    created = true;
                    using (var writer = new StreamWriter(stream, Utf8)) writer.Write(json);
                }
                CheckedPath(relative);
                CheckedPath(temporaryRelative);
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
                created = false;
            }
            finally
            {
                if (created) File.Delete(CheckedPath(temporaryRelative));
            }
        }

        FileStream OpenLock(string relative)
        {
            return new FileStream(CheckedPath(relative), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }

        void EnsureReplayDirectory()
        {
            string directory = CheckedPath("replays", true);
            Directory.CreateDirectory(directory);
            CheckAttributes(directory, true);
        }

        void EnsureRoot()
        {
            CheckRootAncestors();
            Directory.CreateDirectory(root);
            CheckRootAncestors();
        }

        void CheckRootAncestors()
        {
            // 先检查祖先再访问子目录，避免根目录本身经父级联接落到别处。
            var ancestors = new Stack<string>();
            string current = root;
            string parent;
            while (!string.IsNullOrEmpty(parent = Path.GetDirectoryName(current)))
            {
                ancestors.Push(current);
                current = parent;
            }
            foreach (string directory in ancestors) CheckAttributes(directory, true);
        }

        string CheckedPath(string relative, bool directory = false)
        {
            EnsureRoot();
            string path = Path.GetFullPath(Path.Combine(root, relative));
            if (!path.StartsWith(rootPrefix, pathComparison)) throw new ArgumentException("存档路径必须位于根目录内。");
            string current = root;
            string[] parts = relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                current = Path.Combine(current, parts[i]);
                CheckAttributes(current, i < parts.Length - 1 || directory);
            }
            return path;
        }

        static void CheckAttributes(string path, bool directory)
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(path); }
            catch (FileNotFoundException) { return; }
            catch (DirectoryNotFoundException) { return; }
            // 路径文本在根内还不够，符号链接或目录联接可能把访问导向根外。
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("存档路径不能包含链接或重解析点：" + path);
            if (((attributes & FileAttributes.Directory) != 0) != directory)
                throw new IOException("存档路径的文件或目录类型不正确：" + path);
        }

        static void ValidateName(string name)
        {
            if (!IsValidName(name)) throw new ArgumentException("回放名称只能包含字母、数字、下划线和连字符，且不能使用系统设备名。", nameof(name));
        }

        static bool IsValidName(string name)
        {
            if (string.IsNullOrEmpty(name) || !name.All(c =>
                (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '-')) return false;
            string upper = name.ToUpperInvariant();
            if (upper == "CON" || upper == "PRN" || upper == "AUX" || upper == "NUL") return false;
            return !(upper.Length == 4 && (upper.StartsWith("COM", StringComparison.Ordinal) || upper.StartsWith("LPT", StringComparison.Ordinal))
                && upper[3] >= '1' && upper[3] <= '9');
        }

        static string FilePackId(string id)
        {
            if (id != null && id.Length <= 80 && IsValidName(id)) return id;
            using (var sha = SHA256.Create())
                return "id-" + string.Concat(sha.ComputeHash(Utf8.GetBytes(id ?? "")).Select(value => value.ToString("x2", CultureInfo.InvariantCulture)));
        }
    }
}
