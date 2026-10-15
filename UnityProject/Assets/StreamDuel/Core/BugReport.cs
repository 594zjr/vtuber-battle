using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;

namespace StreamDuel.Core
{
    public sealed class BugReport
    {
        const string Prefix = "SDBUG1-";
        readonly string recordJson;

        public BattleRecord Record { get { return BattleRecord.FromJson(recordJson); } }
        public string PackId { get; }
        public string ContentHash { get; }
        public string EngineVersion { get; }
        public int ReplayVersion { get; }
        public ulong StateHash { get; }
        public string LogHash { get; }
        public string Note { get; }
        public uint RngState { get; }

        public static string CurrentEngineVersion
        {
            get
            {
                var assembly = typeof(Battle).Assembly;
                var information = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
                if (!string.IsNullOrWhiteSpace(information?.InformationalVersion)) return information.InformationalVersion;
                return assembly.GetName().Version?.ToString() ?? "未知版本";
            }
        }

        BugReport(string json, string packId, string contentHash, string engineVersion, int replayVersion,
            ulong stateHash, string logHash, string note, uint rngState)
        {
            recordJson = json;
            PackId = packId;
            ContentHash = contentHash;
            EngineVersion = engineVersion;
            ReplayVersion = replayVersion;
            StateHash = stateHash;
            LogHash = logHash;
            Note = note;
            RngState = rngState;
        }

        public static string Create(Battle b, string note)
        {
            if (b == null) throw new ArgumentNullException(nameof(b));
            string json = b.Record.ToJson();
            var record = BattleRecord.FromJson(json);
            var report = new BugReport(json, record.PackId, b.Db.ContentHash(), CurrentEngineVersion,
                record.Version, b.StateHash(), HashLog(b.Log), TextInput.Clean(note, TextInputKind.Note).Text,
                b.BugReportRngState);
            try { return ShareCode.EncodeText(report.ToJson(), Prefix); }
            catch (FormatException error) { throw FormatError(error); }
            catch (Exception error) when (error is ArgumentException || error is InvalidDataException
                || error is IOException || error is OverflowException)
            {
                throw new FormatException("问题报告码的压缩数据或文本内容无效。", error);
            }
        }

        public static BugReport Parse(string code)
        {
            try
            {
                var root = MiniJson.Parse(ShareCode.DecodeText(code, Prefix)) as Dictionary<string, object>;
                if (root == null) throw new FormatException("问题报告必须是 JSON 对象。");
                string json = ReadText(root, "record");
                var record = BattleRecord.FromJson(json);
                string packId = ReadText(root, "packId");
                string contentHash = ReadText(root, "contentHash");
                string engineVersion = ReadText(root, "engineVersion");
                int replayVersion = (int)ReadInteger(root, "replayVersion", 1, int.MaxValue);
                ulong stateHash = ReadHash(root, "stateHash");
                string logHash = ReadHash(root, "logHash").ToString("x16", CultureInfo.InvariantCulture);
                string note = TextInput.Clean(ReadText(root, "note", allowEmpty: true), TextInputKind.Note).Text;
                uint rngState = (uint)ReadInteger(root, "rngState", 0, uint.MaxValue);
                if (!string.Equals(packId, record.PackId, StringComparison.Ordinal))
                    throw new FormatException("问题报告的内容包编号与录像不一致。");
                if (replayVersion != record.Version)
                    throw new FormatException("问题报告的录像版本与录像内容不一致。");
                return new BugReport(json, packId, contentHash, engineVersion, replayVersion,
                    stateHash, logHash, note, rngState);
            }
            catch (FormatException error) { throw FormatError(error); }
            catch (Exception error) when (error is ArgumentException || error is InvalidDataException || error is IOException
                || error is OverflowException || error is IndexOutOfRangeException || error is InvalidCastException
                || error is KeyNotFoundException)
            {
                throw new FormatException("问题报告码的压缩数据或文本内容无效。", error);
            }
        }

        public static BugReportResult Reproduce(ContentDb db, BugReport report)
        {
            var warnings = new List<string>();
            if (report == null) return Failed("问题报告缺失。", warnings);
            try
            {
                var record = report.Record;
                var compatibility = Replay.Check(db, record);
                if (!compatibility.CanReplay) return Failed(compatibility.Reason, warnings);
                if (!string.IsNullOrEmpty(compatibility.Reason)) warnings.Add(compatibility.Reason);
                if (!string.Equals(report.EngineVersion, CurrentEngineVersion, StringComparison.Ordinal))
                    warnings.Add("引擎版本不同：报告为 " + report.EngineVersion + "，当前为 " + CurrentEngineVersion
                        + "。将按当前引擎尽力重放，结果可能不同。");
                if (report.ReplayVersion < BattleRecord.DefaultVersion)
                    warnings.Add("报告使用已支持的旧录像版本 " + report.ReplayVersion.ToString(CultureInfo.InvariantCulture)
                        + "，将按旧记录的兼容规则尽力重放。");
                string currentHash = db.ContentHash();
                if (!string.Equals(report.ContentHash, currentHash, StringComparison.Ordinal)
                    && !string.Equals(report.ContentHash, record.ContentHash, StringComparison.Ordinal))
                    warnings.Add("内容指纹不同：报告时为 " + report.ContentHash + "，当前为 " + currentHash
                        + "。将按当前内容尽力重放，结果可能不同。");

                var battle = Replay.RunBestEffort(db, record);
                // 最后一次 AI 选择可能尚未形成录像事件，独立保存终点位置才能保留真实随机流。
                battle.RestoreReplayRng(report.RngState);
                bool stateMatches = battle.StateHash() == report.StateHash;
                bool logMatches = string.Equals(HashLog(battle.Log), report.LogHash, StringComparison.Ordinal);
                if (!stateMatches) warnings.Add("最终状态哈希不一致，未能原样复现报告中的战斗状态。");
                if (!logMatches) warnings.Add("最后 30 行日志哈希不一致，结算记录或显示文字与报告不同。");
                return new BugReportResult(battle, stateMatches, logMatches, warnings,
                    stateMatches && logMatches ? "复现完成，状态与日志哈希一致。" : "重放完成，但报告中的状态或日志未能完全复现。");
            }
            catch (Exception error) when (error is FormatException || error is KeyError || error is ArgumentException
                || error is InvalidOperationException || error is KeyNotFoundException || error is NullReferenceException
                || error is IndexOutOfRangeException || error is OverflowException)
            {
                return Failed("无法重放问题报告：" + error.Message, warnings);
            }
        }

        static BugReportResult Failed(string reason, List<string> warnings)
        {
            warnings.Add(reason);
            return new BugReportResult(null, false, false, warnings, reason);
        }

        string ToJson()
        {
            var text = new StringBuilder("{\"record\":");
            TeamPresetJson.WriteString(text, recordJson);
            text.Append(",\"packId\":");
            TeamPresetJson.WriteString(text, PackId);
            text.Append(",\"contentHash\":");
            TeamPresetJson.WriteString(text, ContentHash);
            text.Append(",\"engineVersion\":");
            TeamPresetJson.WriteString(text, EngineVersion);
            text.Append(",\"replayVersion\":").Append(ReplayVersion.ToString(CultureInfo.InvariantCulture));
            text.Append(",\"stateHash\":");
            TeamPresetJson.WriteString(text, StateHash.ToString("x16", CultureInfo.InvariantCulture));
            text.Append(",\"logHash\":");
            TeamPresetJson.WriteString(text, LogHash);
            text.Append(",\"note\":");
            TeamPresetJson.WriteString(text, Note);
            text.Append(",\"rngState\":").Append(RngState.ToString(CultureInfo.InvariantCulture));
            return text.Append('}').ToString();
        }

        static string ReadText(Dictionary<string, object> root, string key, bool allowEmpty = false)
        {
            if (!root.TryGetValue(key, out object value) || !(value is string text)
                || (!allowEmpty && string.IsNullOrWhiteSpace(text)))
                throw new FormatException("问题报告的 " + key + " 必须是" + (allowEmpty ? "字符串。" : "非空字符串。"));
            return text;
        }

        static long ReadInteger(Dictionary<string, object> root, string key, long min, long max)
        {
            if (!root.TryGetValue(key, out object value) || !(value is double number)
                || double.IsNaN(number) || double.IsInfinity(number) || number < min || number > max || Math.Truncate(number) != number)
                throw new FormatException("问题报告的 " + key + " 必须是范围内的整数。");
            return (long)number;
        }

        static ulong ReadHash(Dictionary<string, object> root, string key)
        {
            string text = ReadText(root, key);
            if (text.Length != 16 || !ulong.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong hash))
                throw new FormatException("问题报告的 " + key + " 必须是 16 位十六进制哈希。");
            return hash;
        }

        static FormatException FormatError(FormatException error)
        {
            return new FormatException("问题报告码格式错误：" + error.Message.Replace("分享码", "问题报告码"), error);
        }

        static string HashLog(IList<LogEntry> log)
        {
            var hash = new LogHashWriter();
            hash.Text("StreamDuel.BugReport.Log.v1");
            int start = Math.Max(0, log.Count - 30);
            hash.Int(log.Count - start);
            for (int i = start; i < log.Count; i++)
            {
                var entry = log[i];
                // 空值也有边界，异常现场不能因缺少显示快照而无法导出诊断码。
                hash.Int(entry == null ? 0 : 1);
                if (entry == null) continue;
                hash.Int(entry.Turn);
                hash.Int(entry.Side);
                hash.Text(entry.Node);
                hash.Text(entry.Kind);
                hash.Text(entry.Text);
                hash.Array(entry.SnapActive);
                hash.Int(entry.SnapHp == null ? -1 : entry.SnapHp.Length);
                if (entry.SnapHp != null)
                    foreach (var hp in entry.SnapHp) hash.Array(hp);
            }
            return hash.Value.ToString("x16", CultureInfo.InvariantCulture);
        }

        sealed class LogHashWriter
        {
            public ulong Value { get; private set; } = 14695981039346656037UL;

            void Byte(byte value) { Value = unchecked((Value ^ value) * 1099511628211UL); }

            public void Int(int value)
            {
                for (int shift = 0; shift < 32; shift += 8) Byte(unchecked((byte)((uint)value >> shift)));
            }

            public void Text(string value)
            {
                if (value == null) { Int(-1); return; }
                byte[] bytes = Encoding.UTF8.GetBytes(value);
                Int(bytes.Length);
                foreach (byte item in bytes) Byte(item);
            }

            public void Array(int[] values)
            {
                Int(values == null ? -1 : values.Length);
                if (values != null) foreach (int value in values) Int(value);
            }
        }
    }

    public sealed class BugReportResult
    {
        public Battle Battle { get; }
        public bool StateMatches { get; }
        public bool LogMatches { get; }
        public IReadOnlyList<string> Warnings { get; }
        public bool Success { get { return Battle != null && StateMatches && LogMatches; } }
        public string Reason { get; }

        internal BugReportResult(Battle battle, bool stateMatches, bool logMatches, List<string> warnings, string reason)
        {
            Battle = battle;
            StateMatches = stateMatches;
            LogMatches = logMatches;
            Warnings = new List<string>(warnings).AsReadOnly();
            Reason = reason;
        }
    }

    public sealed partial class Battle
    {
        internal uint BugReportRngState { get { return rng.State; } }
    }
}
