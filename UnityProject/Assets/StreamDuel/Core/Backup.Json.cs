using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace StreamDuel.Core
{
    public static partial class Backup
    {
        const int BackupJsonMaxBytes = 64 * 1024 * 1024;
        static readonly UTF8Encoding BackupJsonUtf8 = new UTF8Encoding(false, true);

        internal sealed class Snapshot
        {
            public string Settings, Profile, Teams, Index;
            public bool IncludeReplays;
            public DateTimeOffset ExportedAt;
            public Dictionary<string, string> Replays = new Dictionary<string, string>(StringComparer.Ordinal);
            public Dictionary<string, string> Library = new Dictionary<string, string>(StringComparer.Ordinal);
        }

        public static void Validate(string json)
        {
            ParseSnapshot(json);
        }

        internal static Snapshot ParseSnapshot(string json)
        {
            var root = BackupJsonObject(BackupJsonParse(json), "备份");
            BackupJsonFields(root, "备份", "format", "version", "exportedAt", "includeReplays",
                "settings", "profile", "teams", "replays", "library", "libraryIndex");
            if (BackupJsonText(root, "format") != "StreamDuelBackup") throw new FormatException("不是开播对决备份文件。");
            BackupJsonVersion(root, "备份", 1, true);
            var snapshot = new Snapshot
            {
                ExportedAt = BackupJsonDate(root, "exportedAt"),
                IncludeReplays = BackupJsonBool(root, "includeReplays"),
                Settings = ValidateSettings(BackupJsonEncode(BackupJsonRequired(root, "settings"))),
                Profile = ValidateProfile(BackupJsonEncode(BackupJsonRequired(root, "profile"))),
                Teams = ValidateTeams(BackupJsonEncode(BackupJsonRequired(root, "teams"))),
                Replays = BackupJsonReplayList(BackupJsonRequired(root, "replays"), false),
                Library = BackupJsonReplayList(BackupJsonRequired(root, "library"), true)
            };
            int indexCount = BackupJsonValidateIndex(BackupJsonRequired(root, "libraryIndex"));
            if (!snapshot.IncludeReplays && (snapshot.Replays.Count != 0 || snapshot.Library.Count != 0 || indexCount != 0))
                throw new FormatException("备份声明不包含录像，但仍包含录像或录像索引条目。");
            // 索引仅是可重建缓存；即使形状正确，也不能用它决定访问哪些文件。
            snapshot.Index = BuildLibraryIndex(snapshot.Library);
            return snapshot;
        }

        internal static string SerializeSnapshot(Snapshot snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            string settings = ValidateSettings(snapshot.Settings);
            string profile = ValidateProfile(snapshot.Profile);
            string teams = ValidateTeams(snapshot.Teams);
            BackupJsonValidateReplayMap(snapshot.Replays, false);
            BackupJsonValidateReplayMap(snapshot.Library, true);
            if (!snapshot.IncludeReplays && (snapshot.Replays.Count != 0 || snapshot.Library.Count != 0))
                throw new FormatException("不包含录像的备份不能保存录像条目。");
            string index = BuildLibraryIndex(snapshot.Library);
            var text = new StringBuilder("{\"format\":\"StreamDuelBackup\",\"version\":1,\"exportedAt\":");
            text.Append(BackupJsonQuote(snapshot.ExportedAt.ToString("O", CultureInfo.InvariantCulture)));
            text.Append(",\"includeReplays\":").Append(snapshot.IncludeReplays ? "true" : "false");
            text.Append(",\"settings\":").Append(settings).Append(",\"profile\":").Append(profile);
            text.Append(",\"teams\":").Append(teams).Append(",\"replays\":");
            BackupJsonWriteReplays(text, snapshot.Replays);
            text.Append(",\"library\":");
            BackupJsonWriteReplays(text, snapshot.Library);
            string result = text.Append(",\"libraryIndex\":").Append(index).Append('}').ToString();
            BackupJsonCheckSize(result);
            return result;
        }

        internal static string ValidateSettings(string json)
        {
            var settings = BackupJsonObject(BackupJsonParse(json), "设置");
            BackupJsonFields(settings, "设置", "aiLevel", "animations", "defaultPack", "fastPlay", "language", "turnTimer", "version");
            BackupJsonVersion(settings, "设置", GameSettings.CurrentVersion, false);
            foreach (string field in new[] { "fastPlay", "turnTimer", "animations" })
                if (settings.ContainsKey(field)) BackupJsonBool(settings, field);
            foreach (string field in new[] { "defaultPack", "language" })
                if (settings.ContainsKey(field) && string.IsNullOrWhiteSpace(BackupJsonText(settings, field)))
                    throw new FormatException("设置字段「" + field + "」不能为空白。");
            if (settings.TryGetValue("aiLevel", out var raw))
            {
                bool valid = raw is string name && Enum.TryParse(name, true, out AiLevel level)
                    && Enum.IsDefined(typeof(AiLevel), level) && string.Equals(name, level.ToString(), StringComparison.OrdinalIgnoreCase);
                if (raw is double number && number >= 0 && number <= int.MaxValue && number == Math.Truncate(number))
                    valid = Enum.IsDefined(typeof(AiLevel), (int)number);
                if (!valid) throw new FormatException("设置的电脑难度无效。");
            }
            return GameSettings.FromJson(json).ToJson();
        }

        internal static string ValidateProfile(string json)
        {
            var profile = BackupJsonObject(BackupJsonParse(json), "本机统计");
            BackupJsonFields(profile, "本机统计", "packs");
            var packs = BackupJsonObject(BackupJsonRequired(profile, "packs"), "本机统计 packs");
            foreach (var pack in packs)
            {
                var stats = BackupJsonObject(pack.Value, "内容包统计");
                BackupJsonFields(stats, "内容包统计", "wins", "losses", "draws", "picks", "pickWins");
                foreach (string field in new[] { "wins", "losses", "draws" }) BackupJsonInteger(stats, field, 0, int.MaxValue);
                foreach (string field in new[] { "picks", "pickWins" })
                    foreach (var count in BackupJsonObject(BackupJsonRequired(stats, field), "主播统计"))
                        BackupJsonInteger(count.Value, "主播统计计数", 0, int.MaxValue);
            }
            return Profile.FromJson(json).ToJson();
        }

        internal static string ValidateTeams(string json)
        {
            var teams = BackupJsonObject(BackupJsonParse(json), "队伍预设");
            BackupJsonFields(teams, "队伍预设", "items");
            foreach (var value in BackupJsonArray(BackupJsonRequired(teams, "items"), "队伍预设 items"))
            {
                var item = BackupJsonObject(value, "队伍预设条目");
                BackupJsonFields(item, "队伍预设条目", "name", "packId", "ids");
                BackupJsonText(item, "name");
                BackupJsonText(item, "packId");
                BackupJsonStrings(BackupJsonRequired(item, "ids"), "队伍成员");
            }
            return TeamPresetList.FromJson(json).ToJson();
        }

        internal static string BuildLibraryIndex(Dictionary<string, string> library)
        {
            BackupJsonValidateReplayMap(library, true);
            var entries = new List<ReplayEntry>();
            foreach (var pair in library) entries.Add(BackupJsonLibraryEntry(pair.Key, pair.Value));
            var ordered = entries.OrderByDescending(entry => entry.Date).ThenByDescending(entry => entry.Id, StringComparer.Ordinal);
            var text = new StringBuilder("{\"version\":1,\"entries\":[");
            bool first = true;
            foreach (var entry in ordered)
            {
                if (!first) text.Append(',');
                first = false;
                text.Append("{\"version\":1,\"id\":").Append(BackupJsonQuote(entry.Id));
                text.Append(",\"date\":").Append(BackupJsonQuote(entry.Date.ToString("O", CultureInfo.InvariantCulture)));
                text.Append(",\"winner\":").Append(entry.Winner.ToString(CultureInfo.InvariantCulture));
                text.Append(",\"turns\":").Append(entry.Turns.ToString(CultureInfo.InvariantCulture));
                text.Append(",\"note\":").Append(BackupJsonQuote(entry.Note));
                text.Append(",\"packId\":").Append(BackupJsonQuote(entry.PackId));
                text.Append(",\"names\":[").Append(string.Join(",", entry.Names.Select(BackupJsonQuote))).Append(']');
                text.Append(",\"teams\":[").Append(string.Join(",", entry.Teams.Select(team => "[" + string.Join(",", team.Select(BackupJsonQuote)) + "]"))).Append(']');
                text.Append(",\"isCorrupt\":false,\"error\":null,\"isUnavailable\":false,\"unavailableReason\":null}");
            }
            string result = text.Append("]}").ToString();
            BackupJsonCheckSize(result);
            return result;
        }

        internal static void ValidateReplayId(string id, bool isLibrary)
        {
            if (string.IsNullOrEmpty(id) || id.Length > (isLibrary ? 160 : 250) || id.Any(c => !((c >= 'a' && c <= 'z')
                || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '-')))
                throw new FormatException("录像 id 只能包含 ASCII 字母、数字、下划线和连字符，且长度必须合法。");
            string upper = id.ToUpperInvariant();
            if (upper == "CON" || upper == "PRN" || upper == "AUX" || upper == "NUL" || isLibrary && upper == "INDEX"
                || upper.Length == 4 && (upper.StartsWith("COM", StringComparison.Ordinal) || upper.StartsWith("LPT", StringComparison.Ordinal))
                    && upper[3] >= '1' && upper[3] <= '9')
                throw new FormatException("录像 id 不能使用系统设备名或录像库管理文件名。");
        }

        static ReplayEntry BackupJsonLibraryEntry(string id, string json)
        {
            var root = BackupJsonObject(BackupJsonParse(json), "录像");
            BackupJsonVersion(root, "录像", BattleRecord.CurrentVersion, false);
            var metadata = BackupJsonObject(BackupJsonRequired(root, "library"), "录像库元数据");
            BackupJsonFields(metadata, "录像库元数据", "version", "id", "date", "winner", "turns", "note");
            BackupJsonVersion(metadata, "录像库元数据", 1, true);
            if (!string.Equals(BackupJsonText(metadata, "id"), id, StringComparison.Ordinal))
                throw new FormatException("录像库元数据 id 与条目 id 不一致。");
            DateTimeOffset date = BackupJsonDate(metadata, "date").ToUniversalTime();
            int winner = BackupJsonInteger(metadata, "winner", -1, 1);
            int turns = BackupJsonInteger(metadata, "turns", 0, int.MaxValue);
            string note = BackupJsonNullableText(BackupJsonRequired(metadata, "note"), "录像备注");
            var record = BattleRecord.FromJson(json);
            if (record.Version > BattleRecord.CurrentVersion) throw new FormatException("录像来自更新的版本，当前程序无法恢复。");
            if (turns != record.Events.Count(entry => entry.Type == BattleRecordEventType.Turn))
                throw new FormatException("录像库的回合数与录像记录不一致。");
            foreach (var entry in record.Events)
                if (entry.Type == BattleRecordEventType.Turn && (!Enum.IsDefined(typeof(ActionType), entry.A0.Type)
                    || !Enum.IsDefined(typeof(ActionType), entry.A1.Type))) throw new FormatException("录像包含未知行动类型。");
            return new ReplayEntry
            {
                Id = id, Date = date, PackId = record.PackId, Names = record.Names ?? new[] { "你", "对手" },
                Teams = record.Teams, Winner = winner, Turns = turns, Note = string.IsNullOrWhiteSpace(note) ? null : note
            };
        }

        static int BackupJsonValidateIndex(object value)
        {
            var index = BackupJsonObject(value, "录像库索引");
            BackupJsonFields(index, "录像库索引", "version", "entries");
            BackupJsonVersion(index, "录像库索引", 1, true);
            var entries = BackupJsonArray(BackupJsonRequired(index, "entries"), "录像库索引 entries");
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in entries)
            {
                var entry = BackupJsonObject(item, "录像库索引条目");
                BackupJsonFields(entry, "录像库索引条目", "version", "id", "date", "winner", "turns", "note",
                    "packId", "names", "teams", "isCorrupt", "error", "isUnavailable", "unavailableReason");
                BackupJsonVersion(entry, "录像库索引条目", 1, true);
                string id = BackupJsonText(entry, "id");
                ValidateReplayId(id, true);
                if (!ids.Add(id)) throw new FormatException("录像库索引包含重复 id。");
                BackupJsonDate(entry, "date");
                BackupJsonInteger(entry, "winner", -1, 1);
                BackupJsonInteger(entry, "turns", 0, int.MaxValue);
                BackupJsonNullableText(BackupJsonRequired(entry, "note"), "录像备注");
                BackupJsonNullableText(BackupJsonRequired(entry, "packId"), "录像内容包");
                if (BackupJsonStrings(BackupJsonRequired(entry, "names"), "录像名字").Count != 2)
                    throw new FormatException("录像索引必须包含两个名字。");
                var teams = BackupJsonArray(BackupJsonRequired(entry, "teams"), "录像队伍");
                if (teams.Count != 2) throw new FormatException("录像索引必须包含两支队伍。");
                foreach (var team in teams) BackupJsonStrings(team, "录像队伍成员");
                BackupJsonBool(entry, "isCorrupt");
                BackupJsonNullableText(BackupJsonRequired(entry, "error"), "录像错误");
                if (entry.ContainsKey("isUnavailable")) BackupJsonBool(entry, "isUnavailable");
                if (entry.TryGetValue("unavailableReason", out var reason)) BackupJsonNullableText(reason, "录像不可用原因");
            }
            return entries.Count;
        }

        static Dictionary<string, string> BackupJsonReplayList(object raw, bool isLibrary)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var value in BackupJsonArray(raw, "录像列表"))
            {
                var item = BackupJsonObject(value, "录像条目");
                BackupJsonFields(item, "录像条目", "id", "json");
                string id = BackupJsonText(item, "id");
                ValidateReplayId(id, isLibrary);
                if (!ids.Add(id)) throw new FormatException("备份包含重复或仅大小写不同的录像 id。");
                string json = BackupJsonText(item, "json");
                // 载荷无需是 JSON，但恢复成 UTF-8 文件前必须能无损编码。
                BackupJsonCheckSize(json);
                result.Add(id, json);
            }
            return result;
        }

        static void BackupJsonValidateReplayMap(Dictionary<string, string> replays, bool isLibrary)
        {
            if (replays == null) throw new FormatException("备份录像列表不能为空值。");
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in replays)
            {
                ValidateReplayId(pair.Key, isLibrary);
                if (!ids.Add(pair.Key)) throw new FormatException("备份包含仅大小写不同的录像 id。");
                BackupJsonCheckSize(pair.Value);
            }
        }

        static void BackupJsonWriteReplays(StringBuilder text, Dictionary<string, string> replays)
        {
            text.Append('[');
            bool first = true;
            foreach (var pair in replays.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                if (!first) text.Append(',');
                first = false;
                text.Append("{\"id\":").Append(BackupJsonQuote(pair.Key)).Append(",\"json\":").Append(BackupJsonQuote(pair.Value)).Append('}');
            }
            text.Append(']');
        }

        static object BackupJsonParse(string json)
        {
            BackupJsonCheckSize(json);
            object value = MiniJson.Parse(json);
            // MiniJson 为旧内容保留后值覆盖行为；备份拒绝歧义，避免部分数据被悄悄替换。
            var objects = new Stack<HashSet<string>>();
            for (int i = 0; i < json.Length; i++)
            {
                if (json[i] == '{') objects.Push(new HashSet<string>(StringComparer.Ordinal));
                else if (json[i] == '}') objects.Pop();
                else if (json[i] == '"')
                {
                    int start = i++;
                    while (i < json.Length && json[i] != '"')
                    {
                        if (json[i] == '\\') i++;
                        i++;
                    }
                    int next = i + 1;
                    while (next < json.Length && char.IsWhiteSpace(json[next])) next++;
                    if (next < json.Length && json[next] == ':')
                    {
                        string key = (string)MiniJson.Parse(json.Substring(start, i - start + 1));
                        if (!objects.Peek().Add(key)) throw new FormatException("备份 JSON 包含重复字段：" + key);
                    }
                }
            }
            return value;
        }

        static void BackupJsonCheckSize(string json)
        {
            if (json == null) throw new FormatException("备份 JSON 不能为空值。");
            try
            {
                if (json.Length > BackupJsonMaxBytes || BackupJsonUtf8.GetByteCount(json) > BackupJsonMaxBytes)
                    throw new FormatException("备份文件不能超过 64 MiB。");
            }
            catch (EncoderFallbackException ex) { throw new FormatException("备份不是有效的 Unicode 文本。", ex); }
        }

        static object BackupJsonRequired(Dictionary<string, object> source, string key)
        {
            if (!source.TryGetValue(key, out var value)) throw new FormatException("备份缺少字段「" + key + "」。");
            return value;
        }

        static Dictionary<string, object> BackupJsonObject(object value, string field)
        {
            return value as Dictionary<string, object> ?? throw new FormatException(field + "必须是 JSON 对象。");
        }

        static List<object> BackupJsonArray(object value, string field)
        {
            return value as List<object> ?? throw new FormatException(field + "必须是数组。");
        }

        static List<object> BackupJsonStrings(object value, string field)
        {
            var result = BackupJsonArray(value, field);
            foreach (var item in result) if (!(item is string)) throw new FormatException(field + "必须只包含字符串。");
            return result;
        }

        static string BackupJsonText(Dictionary<string, object> source, string key)
        {
            return BackupJsonRequired(source, key) as string ?? throw new FormatException("备份字段「" + key + "」必须是字符串。");
        }

        static string BackupJsonNullableText(object value, string field)
        {
            if (value == null || value is string) return (string)value;
            throw new FormatException(field + "必须是字符串或 null。");
        }

        static bool BackupJsonBool(Dictionary<string, object> source, string key)
        {
            if (!(BackupJsonRequired(source, key) is bool value)) throw new FormatException("备份字段「" + key + "」必须是布尔值。");
            return value;
        }

        static int BackupJsonInteger(Dictionary<string, object> source, string key, int min, int max)
        {
            return BackupJsonInteger(BackupJsonRequired(source, key), key, min, max);
        }

        static int BackupJsonInteger(object value, string field, int min, int max)
        {
            if (!(value is double number) || double.IsNaN(number) || double.IsInfinity(number)
                || number < min || number > max || number != Math.Truncate(number))
                throw new FormatException("备份字段「" + field + "」必须是合法范围内的整数。");
            return (int)number;
        }

        static void BackupJsonVersion(Dictionary<string, object> source, string field, int maximum, bool required)
        {
            if (!required && !source.ContainsKey("version")) return;
            int version = BackupJsonInteger(source, "version", 1, int.MaxValue);
            if (version > maximum) throw new FormatException(field + "来自更新的版本，当前程序无法恢复。");
        }

        static DateTimeOffset BackupJsonDate(Dictionary<string, object> source, string key)
        {
            if (!DateTimeOffset.TryParseExact(BackupJsonText(source, key), "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                throw new FormatException("备份日期字段「" + key + "」无效。");
            return date;
        }

        static void BackupJsonFields(Dictionary<string, object> source, string field, params string[] allowed)
        {
            foreach (string key in source.Keys)
                if (!allowed.Contains(key, StringComparer.Ordinal)) throw new FormatException(field + "包含未知字段「" + key + "」。");
        }

        static string BackupJsonEncode(object value)
        {
            if (value == null) return "null";
            if (value is string text) return BackupJsonQuote(text);
            if (value is bool flag) return flag ? "true" : "false";
            if (value is double number) return number.ToString("R", CultureInfo.InvariantCulture);
            if (value is List<object> list) return "[" + string.Join(",", list.Select(BackupJsonEncode)) + "]";
            var fields = BackupJsonObject(value, "备份字段");
            return "{" + string.Join(",", fields.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => BackupJsonQuote(pair.Key) + ":" + BackupJsonEncode(pair.Value))) + "}";
        }

        static string BackupJsonQuote(string value)
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
    }
}
