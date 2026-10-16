using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace StreamDuel.Core
{
    public sealed class RankedReplayTag
    {
        public string Format { get; }
        public int Season { get; }
        public int GameNumber { get; }
        public int PlayerSide { get; }
        public int RatingBefore { get; }
        public int RatingAfter { get; }
        internal string RecordHash { get; }

        internal RankedReplayTag(string format, int season, int gameNumber, int playerSide,
            int ratingBefore, int ratingAfter, string recordHash)
        {
            Format = format;
            Season = season;
            GameNumber = gameNumber;
            PlayerSide = playerSide;
            RatingBefore = ratingBefore;
            RatingAfter = ratingAfter;
            RecordHash = recordHash;
        }

        internal static string Hash(string recordJson, int winner)
        {
            using (var sha = SHA256.Create())
                return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(recordJson + "\n"
                    + winner.ToString(CultureInfo.InvariantCulture))).Select(value => value.ToString("x2", CultureInfo.InvariantCulture)));
        }

        internal static bool IsOffline(string json)
        {
            var root = MiniJson.Parse(json) as Dictionary<string, object>;
            if (root == null) return true;
            // 尚未合入的离线续局也不能因旧解析器忽略扩展而混入正式排位。
            return root.TryGetValue("offlineContinuation", out var value) && (!(value is bool flag) || flag);
        }
    }

    internal sealed class RankedReplaySnapshot
    {
        internal RankedReplayTag Tag;
        internal ReplayEntry Entry;
        internal BattleRecord Record;
        internal string Error;
    }

    public sealed partial class ReplayLibrary
    {
        public string SaveRanked(Battle battle, RankedReplayTag tag, string note = null)
        {
            RequireWritable();
            if (battle == null) throw new ArgumentNullException(nameof(battle));
            if (tag == null) throw new ArgumentNullException(nameof(tag));
            string json = battle.Record.ToJson();
            if (battle.Phase != Phase.Over || battle.IsBranch || RankedReplayTag.IsOffline(json))
                throw new InvalidOperationException("只有已完成的正式排位对局才能保存排位录像。");
            if (tag.RecordHash != RankedReplayTag.Hash(json, battle.Winner))
                throw new InvalidOperationException("排位结算凭条与本局录像不一致。");
            return SaveCore(battle.Record, BattleSummary.From(battle), note, null, null, tag);
        }

        internal IReadOnlyList<RankedReplaySnapshot> ReadRankedReplays(string format, int season, List<string> warnings)
        {
            var result = new List<RankedReplaySnapshot>();
            if (!ReadOnlyDirectoryExists()) return result.AsReadOnly();
            using (OpenExistingReadLock())
            {
                foreach (string path in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
                    .OrderBy(value => Path.GetFileName(value), StringComparer.Ordinal))
                {
                    string id = Path.GetFileNameWithoutExtension(path);
                    if (!IsValidId(id)) continue;
                    RankedReplayTag tag = null;
                    try
                    {
                        string json = File.ReadAllText(ReadOnlyPath(id + ".json"), Utf8);
                        var root = MiniJson.Parse(json) as Dictionary<string, object>;
                        if (root == null || !root.TryGetValue("library", out var raw)
                            || !(raw is Dictionary<string, object> metadata)) continue;
                        tag = ReadRankedTag(metadata);
                        if (tag == null || tag.Format != format || tag.Season != season) continue;
                        ValidateRankedJson(json);
                        var entry = ParseEntry(id, json, out var record);
                        result.Add(new RankedReplaySnapshot
                        {
                            Tag = tag, Entry = entry, Record = record,
                            Error = entry.IsUnavailable ? "排位录像内容已下架，未计入统计。" : null
                        });
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException
                        || ex is FormatException || ex is DecoderFallbackException)
                    {
                        const string error = "有录像损坏、无法读取或排位标签无效，已跳过。";
                        if (!warnings.Contains(error)) warnings.Add(error);
                        if (tag != null && tag.Format == format && tag.Season == season)
                            result.Add(new RankedReplaySnapshot { Tag = tag, Error = error });
                    }
                }
            }
            return result.AsReadOnly();
        }

        static RankedReplayTag ReadRankedTag(Dictionary<string, object> metadata)
        {
            if (!metadata.TryGetValue("ranked", out var raw)) return null;
            if (!(raw is Dictionary<string, object> data)) throw new FormatException("排位录像标签必须是对象。");
            string[] keys = { "version", "format", "season", "gameNumber", "playerSide", "ratingBefore", "ratingAfter", "recordHash" };
            if (data.Count != keys.Length || keys.Any(key => !data.ContainsKey(key)))
                throw new FormatException("排位录像标签字段缺失或含有未知字段。");
            Integer(data, "version", 1, 1);
            string format = Text(data, "format"), hash = Text(data, "recordHash");
            if (string.IsNullOrWhiteSpace(format) || hash.Length != 64
                || !hash.All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f'))
                throw new FormatException("排位录像赛制或内容摘要无效。");
            int before = Integer(data, "ratingBefore", int.MinValue, int.MaxValue);
            int after = Integer(data, "ratingAfter", int.MinValue, int.MaxValue);
            if (Math.Abs((long)after - before) > 40) throw new FormatException("排位录像分数变化超出单局范围。");
            return new RankedReplayTag(format, Integer(data, "season", 1, int.MaxValue),
                Integer(data, "gameNumber", 1, int.MaxValue), Integer(data, "playerSide", 0, 1), before, after, hash);
        }

        static void ValidateRankedJson(string json)
        {
            MiniJson.Parse(json);
            // 只加强新标签文件的字段唯一性，原始昵称仍遵守录像逐字往返的兼容契约。
            var containers = new Stack<HashSet<string>>();
            for (int i = 0; i < json.Length; i++)
            {
                if (json[i] == '{') containers.Push(new HashSet<string>(StringComparer.Ordinal));
                else if (json[i] == '[') containers.Push(null);
                else if (json[i] == '}' || json[i] == ']') containers.Pop();
                else if (json[i] == '"')
                {
                    int start = i++;
                    while (json[i] != '"') { if (json[i] == '\\') i++; i++; }
                    int next = i + 1;
                    while (next < json.Length && (json[next] == ' ' || json[next] == '\t'
                        || json[next] == '\r' || json[next] == '\n')) next++;
                    if (next < json.Length && json[next] == ':')
                    {
                        string key = (string)MiniJson.Parse(json.Substring(start, i - start + 1));
                        if (!containers.Peek().Add(key)) throw new FormatException("排位录像包含重复字段。");
                    }
                }
            }
        }

        static void ValidateRankedEntry(ReplayEntry entry, BattleRecord record, string rawJson)
        {
            var tag = entry.RankedReplay;
            if (record.IsBranch || RankedReplayTag.IsOffline(rawJson) || entry.TournamentArchive != null)
                throw new FormatException("接手、离线续局或赛事录像不能同时标为排位录像。");
            if (tag.RecordHash != RankedReplayTag.Hash(record.ToJson(), entry.Winner))
                throw new FormatException("排位录像内容与结算摘要不一致。");
            int deltaSign = Math.Sign((long)tag.RatingAfter - tag.RatingBefore);
            if (entry.Winner == tag.PlayerSide && deltaSign < 0
                || entry.Winner >= 0 && entry.Winner != tag.PlayerSide && deltaSign > 0)
                throw new FormatException("排位录像分数变化与胜负不一致。");
        }

        static string RankedMetadata(RankedReplayTag tag)
        {
            if (tag == null) return "";
            return ",\"ranked\":{\"version\":1,\"format\":" + Quote(tag.Format)
                + ",\"season\":" + tag.Season.ToString(CultureInfo.InvariantCulture)
                + ",\"gameNumber\":" + tag.GameNumber.ToString(CultureInfo.InvariantCulture)
                + ",\"playerSide\":" + tag.PlayerSide.ToString(CultureInfo.InvariantCulture)
                + ",\"ratingBefore\":" + tag.RatingBefore.ToString(CultureInfo.InvariantCulture)
                + ",\"ratingAfter\":" + tag.RatingAfter.ToString(CultureInfo.InvariantCulture)
                + ",\"recordHash\":" + Quote(tag.RecordHash) + "}";
        }
    }
}
