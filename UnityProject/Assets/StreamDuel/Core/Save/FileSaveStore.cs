using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

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
        readonly bool createRootOnAccess;
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
        static readonly string ReplayOwnershipPath = Path.Combine("replays", ".replay-ownership.json");
        static readonly string ReplayOwnershipTemporaryPath = Path.Combine("replays", ".replay-ownership.json.tmp");
        static readonly Regex AutomaticReplayName = new Regex(
            @"^[0-9]{8}_[0-9]{6}_[0-9]{7}_[A-Za-z0-9_-]+$", RegexOptions.CultureInvariant);

        public FileSaveStore(string rootDirectory) : this(rootDirectory, true)
        {
        }

        FileSaveStore(string rootDirectory, bool createRootOnAccess)
        {
            if (string.IsNullOrWhiteSpace(rootDirectory)) throw new ArgumentException("存档根目录不能为空。", nameof(rootDirectory));
            root = Path.GetFullPath(rootDirectory);
            rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            pathComparison = Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            this.createRootOnAccess = createRootOnAccess;
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

        public TeamPresetList LoadTeams()
        {
            return TeamPresetList.FromJson(ReadOptional("teams.json"));
        }

        public void SaveTeams(TeamPresetList teams)
        {
            if (teams == null) throw new ArgumentNullException(nameof(teams));
            string json = teams.ToJson();
            using (OpenLock(".teams.lock")) WriteAtomic("teams.json", ".teams.json.tmp", json);
        }

        public Profile LoadProfile()
        {
            return Profile.FromJson(ReadOptional("profile.json"));
        }

        // 联网开场只读完整快照，未完成的握手不能创建存档目录。
        public static Profile LoadProfileSnapshot(string rootDirectory)
        {
            return new FileSaveStore(rootDirectory, false).LoadProfile();
        }

        public void SaveProfile(Profile profile)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            string json = profile.ToJson();
            using (OpenLock(".profile.lock")) WriteAtomic("profile.json", ".profile.json.tmp", json);
        }

        // 读改写必须持有同一把锁，多个本机客户端才不会互相覆盖累计战绩。
        public Profile RecordProfile(Battle battle, int playerSide = 0, PlayerRating opponentRating = null)
        {
            using (OpenLock(".profile.lock"))
            {
                var profile = LoadProfile();
                profile.Record(battle, playerSide);
                // 接手局经过同样的终局校验，但不能触发排位结算或重写正式档案。
                if (battle.IsBranch) return profile;
                if (opponentRating != null)
                {
                    PlayerRating current;
                    var own = profile.Ratings.TryGetValue(Profile.DefaultRatingFormat, out current)
                        ? new PlayerRating { Rating = current.Rating, Games = current.Games } : new PlayerRating();
                    // 双方自报分只用于本机显示，不防作弊；对方快照不能作为本方档案保存。
                    var opponent = new PlayerRating { Rating = opponentRating.Rating, Games = opponentRating.Games };
                    double score = battle.Winner < 0 ? 0.5 : battle.Winner == playerSide ? 1.0 : 0.0;
                    RatingSystem.Apply(own, opponent, score);
                    profile.Ratings[Profile.DefaultRatingFormat] = own;
                }
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
            string relative = Path.Combine("replays", name + ".json");
            WriteAtomic(relative, Path.Combine("replays", ".replay.json.tmp"), json);
            try
            {
                // 登记或清理失败也不能让调用者误以为这局回放没有落盘。
                var ownership = ReadReplayOwnership();
                var files = ReplayFiles(ownership);
                DateTime timestamp = DateTime.UtcNow;
                // 覆盖、短间隔保存和时钟回拨都仍应把新保存的一份排在最前。
                if (files.Count > 0 && files[0].LastWriteTimeUtc >= timestamp)
                    timestamp = files[0].LastWriteTimeUtc.AddMilliseconds(1);
                File.SetLastWriteTimeUtc(CheckedPath(relative), timestamp);

                ownership = MatchingReplayOwnership(ownership);
                if (!AutomaticReplayName.IsMatch(name) && !IsBattleRecord(json))
                    ownership[name] = FileDigest(relative);
                else ownership.Remove(name);
                WriteReplayOwnership(ownership);

                bool removedOwnership = false;
                foreach (var file in ReplayFiles(ownership).Skip(50))
                {
                    File.Delete(CheckedPath(Path.Combine("replays", file.Name)));
                    removedOwnership |= ownership.Remove(Path.GetFileNameWithoutExtension(file.Name));
                }
                if (removedOwnership) WriteReplayOwnership(ownership);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
            {
                throw new ReplayRetentionException(name, ex);
            }
        }

        List<FileInfo> ReplayFiles()
        {
            return ReplayFiles(ReadReplayOwnership());
        }

        List<FileInfo> ReplayFiles(Dictionary<string, string> ownership)
        {
            return ReplayCandidates().Where(file => IsManagedReplay(file, ownership)).ToList();
        }

        List<FileInfo> ReplayCandidates()
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

        bool IsManagedReplay(FileInfo file, Dictionary<string, string> ownership)
        {
            string name = Path.GetFileNameWithoutExtension(file.Name);
            if (AutomaticReplayName.IsMatch(name)) return true;
            string relative = Path.Combine("replays", file.Name);
            try
            {
                if (IsBattleRecord(File.ReadAllText(CheckedPath(relative), Utf8))) return true;
                return ownership.TryGetValue(name, out var digest)
                    && string.Equals(digest, FileDigest(relative), StringComparison.Ordinal);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // 无法确认的外来文件不能阻塞存档，也不能授权本轮清理。
                return false;
            }
        }

        static bool IsBattleRecord(string json)
        {
            try { BattleRecord.FromJson(json); return true; }
            catch (FormatException) { return false; }
        }

        Dictionary<string, string> NewReplayOwnership()
        {
            return new Dictionary<string, string>(pathComparison == StringComparison.OrdinalIgnoreCase
                ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        }

        Dictionary<string, string> ReadReplayOwnership()
        {
            var ownership = NewReplayOwnership();
            string json = ReadOptional(ReplayOwnershipPath);
            if (json == null) return ownership;
            try
            {
                var entries = MiniJson.Parse(json) as Dictionary<string, object>;
                if (entries == null) return ownership;
                foreach (var entry in entries)
                {
                    // 损坏登记不能成为删除普通 JSON 的依据。
                    if (!IsValidName(entry.Key) || !(entry.Value is string digest) || digest.Length != 64
                        || !digest.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
                        return NewReplayOwnership();
                    ownership[entry.Key] = digest;
                }
            }
            catch (FormatException) { return NewReplayOwnership(); }
            return ownership;
        }

        Dictionary<string, string> MatchingReplayOwnership(Dictionary<string, string> ownership)
        {
            var matching = NewReplayOwnership();
            foreach (var file in ReplayCandidates())
            {
                string name = Path.GetFileNameWithoutExtension(file.Name);
                if (AutomaticReplayName.IsMatch(name) || !ownership.TryGetValue(name, out var digest)) continue;
                string relative = Path.Combine("replays", file.Name);
                try
                {
                    if (!IsBattleRecord(File.ReadAllText(CheckedPath(relative), Utf8))
                        && string.Equals(digest, FileDigest(relative), StringComparison.Ordinal)) matching[name] = digest;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    // 暂时占用不等于内容已变；保留摘要，解锁后仍须重新匹配才能列出或删除。
                    matching[name] = digest;
                }
            }
            return matching;
        }

        void WriteReplayOwnership(Dictionary<string, string> ownership)
        {
            if (ownership.Count == 0)
            {
                File.Delete(CheckedPath(ReplayOwnershipPath));
                return;
            }
            // 名称和摘要都已限制为 ASCII 安全字符，无需改变回放原文来附加元数据。
            string json = "{" + string.Join(",", ownership.OrderBy(entry => entry.Key, StringComparer.Ordinal)
                .Select(entry => "\"" + entry.Key + "\":\"" + entry.Value + "\"")) + "}";
            WriteAtomic(ReplayOwnershipPath, ReplayOwnershipTemporaryPath, json);
        }

        string FileDigest(string relative)
        {
            using (var sha = SHA256.Create())
                return string.Concat(sha.ComputeHash(File.ReadAllBytes(CheckedPath(relative)))
                    .Select(value => value.ToString("x2", CultureInfo.InvariantCulture)));
        }

        string ReadOptional(string relative)
        {
            string path = CheckedPath(relative);
            try
            {
                // 读者只看旧或新完整文件，不阻止另一局持锁后原子替换。
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
                using (var reader = new StreamReader(stream, Utf8)) return reader.ReadToEnd();
            }
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
            if (createRootOnAccess) Directory.CreateDirectory(root);
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
