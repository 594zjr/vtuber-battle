using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace StreamDuel.Core
{
    public sealed class ReplayEntry
    {
        public string Id;
        public DateTimeOffset Date;
        public string PackId;
        public string[] Names;
        public IList<string>[] Teams;
        public int Winner;
        public int Turns;
        public string Note;
        public bool IsCorrupt;
        public string Error;
        public bool IsUnavailable;
        public string UnavailableReason;
        internal TournamentArchiveTag TournamentArchive;

        // 原始名字和备注保留旧录像的往返契约，界面不直接使用这些存储字段。
        public TextInputResult[] NameInputs
        {
            get
            {
                return (Names ?? Array.Empty<string>())
                    .Select(name => TextInput.Clean(name, TextInputKind.PlayerName)).ToArray();
            }
        }
        public string[] DisplayNames { get { return NameInputs.Select(input => input.Text).ToArray(); } }
        public TextInputResult NoteInput { get { return TextInput.Clean(Note, TextInputKind.Note); } }
        public string DisplayNote { get { return NoteInput.Text; } }
    }

    public sealed class ReplayFilter
    {
        public string StreamerId;
        public string PackId;
        public int? Winner;
        public DateTimeOffset? From;
        public DateTimeOffset? To;
    }

    public sealed class ReplayLibraryMaintenanceException : IOException
    {
        public string ReplayId { get; }

        internal ReplayLibraryMaintenanceException(string id, Exception inner)
            : base("录像已写入，但索引或旧录像清理失败：" + id, inner)
        {
            ReplayId = id;
        }
    }

    public sealed partial class ReplayLibrary
    {
        readonly string directory;
        readonly string prefix;
        readonly int maxCount;
        readonly Func<DateTimeOffset> clock;
        readonly PackPolicy policy;
        readonly bool readOnly;
        readonly StringComparison pathComparison;
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        public ReplayLibrary(string directory, int maxCount = 200, Func<DateTimeOffset> clock = null, PackPolicy policy = null)
            : this(directory, maxCount, clock, policy, false)
        {
        }

        ReplayLibrary(string directory, int maxCount, Func<DateTimeOffset> clock, PackPolicy policy, bool readOnly)
        {
            if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("录像目录不能为空。", nameof(directory));
            if (maxCount < 1) throw new ArgumentOutOfRangeException(nameof(maxCount), "录像上限必须至少为 1。");
            this.directory = Path.GetFullPath(directory);
            prefix = this.directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            this.maxCount = maxCount;
            // 时钟仅用于本地录像命名与筛选，不参与战斗或随机流。
            this.clock = clock ?? (() => DateTimeOffset.UtcNow);
            this.policy = policy ?? PackPolicy.LoadDefault();
            this.readOnly = readOnly;
            pathComparison = Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (readOnly) CheckAncestors();
            else EnsureDirectory();
        }

        public string Save(BattleRecord r, BattleSummary s, string note = null)
        {
            return SaveCore(r, s, note, null, null);
        }

        string SaveCore(BattleRecord r, BattleSummary s, string note, TournamentArchiveTag archive, Tournament tournament)
        {
            RequireWritable();
            if (r == null) throw new ArgumentNullException(nameof(r));
            if (s == null) throw new ArgumentNullException(nameof(s));
            RejectUnavailable(Replay.PolicyError(policy, r));
            if (r.Version > BattleRecord.CurrentVersion)
                throw new FormatException(Replay.Check(null, r).Reason);
            if (r.Teams == null || r.Teams.Length != 2 || r.Teams.Any(team => team == null))
                throw new ArgumentException("录像必须包含两支队伍。", nameof(r));
            string recordJson = r.ToJson();
            var record = BattleRecord.FromJson(recordJson);
            if (s.Winner < -1 || s.Winner > 1 || s.Turns < 0
                || s.Turns != record.Events.Count(entry => entry.Type == BattleRecordEventType.Turn))
                throw new ArgumentException("录像统计的胜方或回合数无效。", nameof(s));
            using (OpenLock())
            {
                if (archive != null)
                {
                    string existing = ExistingTournamentReplay(tournament, archive, recordJson, s);
                    if (existing != null)
                    {
                        // 逐局归档后、赛事存档前中断时，重试也补齐可能尚未写成的索引。
                        if (archive.Attempt.HasValue) WriteIndex(Scan());
                        return existing;
                    }
                }
                DateTimeOffset date = clock().ToUniversalTime();
                string stem = date.ToString("yyyyMMdd_HHmmss_fffffff", CultureInfo.InvariantCulture)
                    + "_" + record.Seed.ToString(CultureInfo.InvariantCulture);
                string id = NextId(stem);
                var entry = Entry(record, id, date, s.Winner, s.Turns, NormalizeNote(note));
                entry.TournamentArchive = archive;
                WriteAtomic(id + ".json", WithMetadata(recordJson, entry));
                try
                {
                    if (archive != null && archive.Attempt.HasValue) WriteIndex(Scan());
                    else RefreshAndPrune();
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    throw new ReplayLibraryMaintenanceException(id, ex);
                }
                return id;
            }
        }

        public List<ReplayEntry> List(ReplayFilter f = null)
        {
            RequireWritable();
            if (f != null && (f.Winner < -1 || f.Winner > 1 || f.From > f.To))
                throw new ArgumentException("录像筛选的胜方或日期范围无效。", nameof(f));
            using (OpenLock())
            {
                var entries = Scan();
                // 录像文件才是元数据来源，索引被删、损坏或过期时都可完整恢复。
                WriteIndex(entries);
                return entries.Where(entry => entry.IsCorrupt || Matches(entry, f)).ToList();
            }
        }

        public BattleRecord Load(string id)
        {
            ValidateId(id);
            if (readOnly) return LoadReadOnly(id);
            using (OpenLock())
            {
                var entry = ReadEntry(id, out var record);
                RejectUnavailable(entry.UnavailableReason);
                return record;
            }
        }

        public void Delete(string id)
        {
            RequireWritable();
            ValidateId(id);
            using (OpenLock())
            {
                File.Delete(CheckedPath(id + ".json"));
                WriteIndex(Scan());
            }
        }

        public void SetNote(string id, string note)
        {
            RequireWritable();
            ValidateId(id);
            using (OpenLock())
            {
                var entry = ReadEntry(id, out var record);
                RejectUnavailable(entry.UnavailableReason);
                entry.Note = NormalizeNote(note);
                WriteAtomic(id + ".json", WithMetadata(record.ToJson(), entry));
                RefreshAndPrune();
            }
        }

        static string NormalizeNote(string note) { return string.IsNullOrWhiteSpace(note) ? null : note; }

        static void RejectUnavailable(string reason)
        {
            if (reason != null) throw new InvalidOperationException(reason);
        }

        string NextId(string stem)
        {
            long largest = -1;
            foreach (string path in Directory.EnumerateFiles(directory, stem + "*.json", SearchOption.TopDirectoryOnly))
            {
                string id = Path.GetFileNameWithoutExtension(path);
                if (id == stem) largest = Math.Max(largest, 0);
                else if (id.StartsWith(stem + "_", StringComparison.Ordinal)
                    && long.TryParse(id.Substring(stem.Length + 1), NumberStyles.None, CultureInfo.InvariantCulture, out long suffix))
                    largest = Math.Max(largest, suffix);
            }
            // 即使最早的同刻录像已被清理，也不能复用其 id 把新录像排到最旧位置。
            if (largest == long.MaxValue) throw new IOException("同一时间与种子的录像编号已用尽。");
            return largest < 0 ? stem : stem + "_" + (largest + 1).ToString("D20", CultureInfo.InvariantCulture);
        }

        static bool Matches(ReplayEntry entry, ReplayFilter filter)
        {
            if (filter == null) return true;
            return (filter.PackId == null || filter.PackId == entry.PackId)
                && (filter.StreamerId == null || entry.Teams.Any(team => team.Contains(filter.StreamerId)))
                && (!filter.Winner.HasValue || filter.Winner.Value == entry.Winner)
                && (!filter.From.HasValue || entry.Date >= filter.From.Value)
                && (!filter.To.HasValue || entry.Date <= filter.To.Value);
        }

        void RefreshAndPrune()
        {
            var entries = Scan();
            // 下架不代表文件损坏，也不能成为自动删除用户原始录像的理由。
            int count = entries.Count(entry => !entry.IsCorrupt && !entry.IsUnavailable);
            foreach (var entry in entries.Where(entry => !entry.IsCorrupt && !entry.IsUnavailable && entry.Note == null
                && !(entry.TournamentArchive?.Attempt.HasValue ?? false))
                .OrderBy(entry => entry.Date).ThenBy(entry => entry.Id, StringComparer.Ordinal).ToList())
            {
                if (count <= maxCount) break;
                File.Delete(CheckedPath(entry.Id + ".json"));
                entries.Remove(entry);
                count--;
            }
            WriteIndex(entries);
        }

        List<ReplayEntry> Scan()
        {
            EnsureDirectory();
            var entries = new List<ReplayEntry>();
            foreach (string path in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly))
            {
                string id = Path.GetFileNameWithoutExtension(path);
                if (!IsValidId(id)) continue;
                try { entries.Add(ReadEntry(id, out _)); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is FormatException || ex is DecoderFallbackException)
                {
                    // 不把坏录像当成无备注录像删除，也不在索引内持久化含系统路径的异常文本。
                    entries.Add(new ReplayEntry
                    {
                        Id = id, Date = DateTimeOffset.MinValue, Winner = -1,
                        Names = new[] { "未知", "未知" }, Teams = new IList<string>[] { new List<string>(), new List<string>() },
                        IsCorrupt = true,
                        Error = ex is FormatException && ex.Message.StartsWith("录像来自更新的版本：", StringComparison.Ordinal)
                            ? ex.Message : "录像文件损坏或无法读取，已跳过。"
                    });
                }
            }
            return entries.OrderByDescending(entry => entry.Date).ThenByDescending(entry => entry.Id, StringComparer.Ordinal).ToList();
        }

        ReplayEntry ReadEntry(string id, out BattleRecord record)
        {
            string json;
            try { json = File.ReadAllText(CheckedPath(id + ".json"), Utf8); }
            catch (DecoderFallbackException ex) { throw new FormatException("录像不是有效的 UTF-8 文本：" + id, ex); }
            return ParseEntry(id, json, out record);
        }

        ReplayEntry ParseEntry(string id, string json, out BattleRecord record)
        {
            var root = MiniJson.Parse(json) as Dictionary<string, object>;
            if (root == null || !root.TryGetValue("library", out var value) || !(value is Dictionary<string, object> metadata))
                throw new FormatException("录像缺少本地录像库元数据：" + id);
            if (Integer(metadata, "version", 1, 1) != 1 || Text(metadata, "id") != id)
                throw new FormatException("录像标识与文件名不一致：" + id);
            if (!DateTimeOffset.TryParseExact(Text(metadata, "date"), "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                throw new FormatException("录像日期无效：" + id);
            int winner = Integer(metadata, "winner", -1, 1), turns = Integer(metadata, "turns", 0, int.MaxValue);
            if (!metadata.TryGetValue("note", out var note) || (note != null && !(note is string)))
                throw new FormatException("录像备注无效：" + id);
            record = BattleRecord.FromJson(json);
            // 解析保留旧接口的宽容行为，库层禁止重写或清理尚不支持的录像。
            if (record.Version > BattleRecord.CurrentVersion)
                throw new FormatException(Replay.Check(null, record).Reason);
            if (turns != record.Events.Count(entry => entry.Type == BattleRecordEventType.Turn))
                throw new FormatException("录像回合数与记录不一致：" + id);
            var entry = Entry(record, id, date.ToUniversalTime(), winner, turns, NormalizeNote((string)note));
            entry.TournamentArchive = ReadTournamentTag(metadata);
            return entry;
        }

        ReplayEntry Entry(BattleRecord record, string id, DateTimeOffset date, int winner, int turns, string note)
        {
            string reason = Replay.PolicyError(policy, record);
            return new ReplayEntry
            {
                Id = id, Date = date, PackId = record.PackId, Names = record.Names ?? new[] { "你", "对手" },
                Teams = record.Teams, Winner = winner, Turns = turns, Note = note,
                IsUnavailable = reason != null, UnavailableReason = reason
            };
        }

        static int Integer(Dictionary<string, object> data, string key, int min, int max)
        {
            if (!data.TryGetValue(key, out var raw) || !(raw is double value) || double.IsNaN(value)
                || double.IsInfinity(value) || value < min || value > max || value != Math.Truncate(value))
                throw new FormatException("录像元数据的 " + key + " 无效。");
            return (int)value;
        }

        static string Text(Dictionary<string, object> data, string key)
        {
            if (!data.TryGetValue(key, out var value) || !(value is string text))
                throw new FormatException("录像元数据的 " + key + " 无效。");
            return text;
        }

        static string Metadata(ReplayEntry entry)
        {
            return "{\"version\":1,\"id\":" + Quote(entry.Id) + ",\"date\":"
                + Quote(entry.Date.ToString("O", CultureInfo.InvariantCulture)) + ",\"winner\":"
                + entry.Winner.ToString(CultureInfo.InvariantCulture) + ",\"turns\":"
                + entry.Turns.ToString(CultureInfo.InvariantCulture) + ",\"note\":" + Quote(entry.Note)
                + TournamentMetadata(entry.TournamentArchive) + "}";
        }

        static string WithMetadata(string recordJson, ReplayEntry entry)
        {
            return recordJson.Substring(0, recordJson.Length - 1) + ",\"library\":" + Metadata(entry) + "}";
        }

        void WriteIndex(List<ReplayEntry> entries)
        {
            var json = new StringBuilder("{\"version\":1,\"entries\":[");
            for (int i = 0; i < entries.Count; i++)
            {
                if (i > 0) json.Append(',');
                var entry = entries[i];
                string metadata = Metadata(entry);
                json.Append(metadata, 0, metadata.Length - 1).Append(",\"packId\":").Append(Quote(entry.PackId));
                json.Append(",\"names\":[").Append(string.Join(",", entry.Names.Select(Quote))).Append(']');
                json.Append(",\"teams\":[").Append(string.Join(",", entry.Teams.Select(team => "[" + string.Join(",", team.Select(Quote)) + "]"))).Append(']');
                json.Append(",\"isCorrupt\":").Append(entry.IsCorrupt ? "true" : "false");
                json.Append(",\"error\":").Append(Quote(entry.Error));
                json.Append(",\"isUnavailable\":").Append(entry.IsUnavailable ? "true" : "false");
                json.Append(",\"unavailableReason\":").Append(Quote(entry.UnavailableReason)).Append('}');
            }
            WriteAtomic("index.json", json.Append("]}").ToString());
        }

        static string Quote(string value)
        {
            if (value == null) return "null";
            var text = new StringBuilder("\"");
            foreach (char c in value)
            {
                if (c == '\\' || c == '"') text.Append('\\').Append(c);
                else if (c < 32 || char.IsSurrogate(c)) text.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                else text.Append(c);
            }
            return text.Append('"').ToString();
        }

        FileStream OpenLock()
        {
            return new FileStream(CheckedPath(".library.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }

        void WriteAtomic(string name, string json)
        {
            string target = CheckedPath(name), temporary = CheckedPath(".library-write.tmp");
            bool created = false;
            try
            {
                // 持锁移除遗留临时目录项，再独占创建，避免复用旧文件的链接目标。
                File.Delete(temporary);
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    created = true;
                    using (var writer = new StreamWriter(stream, Utf8)) writer.Write(json);
                }
                CheckedPath(name);
                CheckedPath(".library-write.tmp");
                if (File.Exists(target)) File.Replace(temporary, target, null);
                else File.Move(temporary, target);
                created = false;
            }
            finally
            {
                if (created) File.Delete(CheckedPath(".library-write.tmp"));
            }
        }

        string CheckedPath(string name)
        {
            EnsureDirectory();
            string path = Path.GetFullPath(Path.Combine(directory, name));
            if (!path.StartsWith(prefix, pathComparison)) throw new ArgumentException("录像路径必须位于给定目录内。");
            CheckAttributes(path, false);
            return path;
        }

        void EnsureDirectory()
        {
            CheckAncestors();
            Directory.CreateDirectory(directory);
            CheckAncestors();
        }

        void CheckAncestors()
        {
            var ancestors = new Stack<string>();
            string current = directory, parent;
            while (!string.IsNullOrEmpty(parent = Path.GetDirectoryName(current)))
            {
                ancestors.Push(current);
                current = parent;
            }
            foreach (string path in ancestors) CheckAttributes(path, true);
        }

        static void CheckAttributes(string path, bool isDirectory)
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(path); }
            catch (FileNotFoundException) { return; }
            catch (DirectoryNotFoundException) { return; }
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("录像路径不能包含链接或重解析点。");
            if (((attributes & FileAttributes.Directory) != 0) != isDirectory) throw new IOException("录像路径的文件或目录类型不正确。");
        }

        static void ValidateId(string id)
        {
            if (!IsValidId(id)) throw new ArgumentException("录像 id 只能包含字母、数字、下划线和连字符，不能使用管理文件名或系统设备名。", nameof(id));
        }

        static bool IsValidId(string id)
        {
            if (string.IsNullOrEmpty(id) || id.Length > 160 || !id.All(c => (c >= 'A' && c <= 'Z')
                || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_' || c == '-')) return false;
            string upper = id.ToUpperInvariant();
            if (upper == "INDEX" || upper == "CON" || upper == "PRN" || upper == "AUX" || upper == "NUL") return false;
            return !(upper.Length == 4 && (upper.StartsWith("COM", StringComparison.Ordinal) || upper.StartsWith("LPT", StringComparison.Ordinal))
                && upper[3] >= '1' && upper[3] <= '9');
        }
    }
}
