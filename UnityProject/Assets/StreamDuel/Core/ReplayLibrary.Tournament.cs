using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace StreamDuel.Core
{
    internal sealed class TournamentArchiveTag
    {
        internal string Key;
        internal int PairingId;
    }

    internal sealed class TournamentReplaySnapshot
    {
        internal int PairingId { get; }
        internal ReplayEntry Entry { get; }
        internal BattleRecord Record { get; }
        internal string Error { get; }

        internal TournamentReplaySnapshot(int pairingId, ReplayEntry entry, BattleRecord record, string error)
        {
            PairingId = pairingId; Entry = entry; Record = record; Error = error;
        }
    }

    public sealed partial class ReplayLibrary
    {
        public static ReplayLibrary OpenReadOnly(string directory, PackPolicy policy = null)
        {
            return new ReplayLibrary(directory, 200, null, policy, true);
        }

        public string SaveTournament(Tournament tournament, Pairing pairing, Battle battle)
        {
            RequireWritable();
            if (tournament == null) throw new ArgumentNullException(nameof(tournament), "赛事不能为空。");
            if (pairing == null) throw new ArgumentNullException(nameof(pairing), "对阵不能为空。");
            if (battle == null) throw new ArgumentNullException(nameof(battle), "对战不能为空。");
            var history = tournament.Pairings;
            if (pairing.Id < 0 || pairing.Id >= history.Count || !ReferenceEquals(history[pairing.Id], pairing))
                throw new ArgumentException("这场对阵不属于本赛事。", nameof(pairing));
            if (pairing.IsBye) throw new InvalidOperationException("轮空没有实际对战，不能归档录像。");
            if (battle.Phase != Phase.Over || battle.Winner < -1 || battle.Winner > 1)
                throw new InvalidOperationException("只有已结束且结果有效的对战才能归档赛事录像。");
            if (pairing.WinnerSide.HasValue && pairing.WinnerSide.Value != battle.Winner)
                throw new InvalidOperationException("录像胜负与已登记的赛事结果不一致。");

            var record = battle.Record;
            var first = tournament.Players[pairing.Player0];
            var second = tournament.Players[pairing.Player1];
            if (record == null || record.PackId != tournament.ReportPackId || record.ContentHash != tournament.ReportContentHash
                || battle.Db.PackId != tournament.ReportPackId || battle.Db.ContentHash() != tournament.ReportContentHash)
                throw new InvalidOperationException("录像内容包或内容指纹与赛事不一致。");
            if (record.Teams == null || record.Teams.Length != 2 || record.Teams[0] == null || record.Teams[1] == null
                || !record.Teams[0].SequenceEqual(first.Team, StringComparer.Ordinal)
                || !record.Teams[1].SequenceEqual(second.Team, StringComparer.Ordinal)
                || record.Names == null || record.Names.Length != 2 || record.Names[0] != first.Name || record.Names[1] != second.Name)
                throw new InvalidOperationException("录像的原始队伍、顺序或选手姓名与本场对阵不一致。");

            var archive = new TournamentArchiveTag { Key = tournament.ReportKey, PairingId = pairing.Id };
            string note = "赛事录像：第 " + pairing.Round.ToString(CultureInfo.InvariantCulture)
                + " 轮，对局 " + (pairing.Id + 1).ToString(CultureInfo.InvariantCulture);
            return SaveCore(record, BattleSummary.From(battle), note, archive, tournament);
        }

        internal IReadOnlyList<TournamentReplaySnapshot> ReadTournamentReplays(Tournament tournament)
        {
            if (tournament == null) throw new ArgumentNullException(nameof(tournament), "赛事不能为空。");
            if (!ReadOnlyDirectoryExists()) return new List<TournamentReplaySnapshot>().AsReadOnly();
            using (OpenExistingReadLock()) return ScanTournamentReplays(tournament);
        }

        string ExistingTournamentReplay(Tournament tournament, int pairingId, string recordJson, BattleSummary summary)
        {
            // 调用方已持写锁，把查重和写入放在同一临界区，重试不产生第二份关联。
            string existingId = null;
            foreach (var snapshot in ScanTournamentReplays(tournament).Where(item => item.PairingId == pairingId))
            {
                if (snapshot.Record == null || snapshot.Entry.IsCorrupt || snapshot.Entry.IsUnavailable
                    || snapshot.Record.ToJson() != recordJson || snapshot.Entry.Winner != summary.Winner
                    || snapshot.Entry.Turns != summary.Turns)
                    throw new InvalidOperationException("本场对阵已有不同或不可用的归档录像，不能覆盖或重复关联。");
                if (existingId == null) existingId = snapshot.Entry.Id;
            }
            return existingId;
        }

        IReadOnlyList<TournamentReplaySnapshot> ScanTournamentReplays(Tournament tournament)
        {
            var result = new List<TournamentReplaySnapshot>();
            if (!ReadOnlyDirectoryExists()) return result.AsReadOnly();
            string key = tournament.ReportKey;
            foreach (string path in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
                .OrderBy(value => Path.GetFileName(value), StringComparer.Ordinal))
            {
                string id = Path.GetFileNameWithoutExtension(path);
                if (!IsValidId(id)) continue;
                string json;
                try { json = File.ReadAllText(ReadOnlyPath(id + ".json"), Utf8); }
                catch (FileNotFoundException) { continue; }
                catch (DecoderFallbackException) { continue; }

                TournamentArchiveTag tag;
                try
                {
                    var root = MiniJson.Parse(json) as Dictionary<string, object>;
                    if (root == null || !root.TryGetValue("library", out var rawMetadata)
                        || !(rawMetadata is Dictionary<string, object> metadata)) continue;
                    tag = ReadTournamentTag(metadata);
                }
                catch (FormatException)
                {
                    // 不从坏 JSON、未知版本或不完整标签猜测赛事归属。
                    continue;
                }
                if (tag == null || tag.Key != key) continue;
                try
                {
                    var entry = ParseEntry(id, json, out var record);
                    result.Add(new TournamentReplaySnapshot(tag.PairingId, entry,
                        entry.IsUnavailable ? null : record, entry.IsUnavailable ? "录像内容已下架，不能用于战报统计。" : null));
                }
                catch (FormatException)
                {
                    const string error = "关联录像损坏或版本不支持，不能用于战报统计。";
                    result.Add(new TournamentReplaySnapshot(tag.PairingId, new ReplayEntry
                    {
                        Id = id, PackId = tournament.ReportPackId, Date = DateTimeOffset.MinValue, Winner = -1,
                        Names = new[] { "未知", "未知" },
                        Teams = new IList<string>[] { new List<string>(), new List<string>() },
                        IsCorrupt = true, Error = error, TournamentArchive = tag
                    }, null, error));
                }
            }
            return result.OrderBy(item => item.PairingId).ThenBy(item => item.Entry.Id, StringComparer.Ordinal).ToList().AsReadOnly();
        }

        static TournamentArchiveTag ReadTournamentTag(Dictionary<string, object> metadata)
        {
            if (!metadata.TryGetValue("tournament", out var value)) return null;
            if (!(value is Dictionary<string, object> source)) throw new FormatException("赛事录像关联必须为对象。");
            Integer(source, "version", 1, 1);
            string key = Text(source, "key");
            if (key.Length != 64 || !key.All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f'))
                throw new FormatException("赛事录像关联键无效。");
            return new TournamentArchiveTag { Key = key, PairingId = Integer(source, "pairingId", 0, int.MaxValue) };
        }

        static string TournamentMetadata(TournamentArchiveTag archive)
        {
            if (archive == null) return "";
            return ",\"tournament\":{\"version\":1,\"key\":" + Quote(archive.Key)
                + ",\"pairingId\":" + archive.PairingId.ToString(CultureInfo.InvariantCulture) + "}";
        }

        void RequireWritable()
        {
            if (readOnly) throw new InvalidOperationException("此录像库以只读方式打开，不能保存、修改、删除或维护索引。");
        }

        bool ReadOnlyDirectoryExists()
        {
            CheckAncestors();
            try
            {
                var attributes = File.GetAttributes(directory);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("录像路径不能包含链接或重解析点。");
                if ((attributes & FileAttributes.Directory) == 0) throw new IOException("录像路径必须是目录。");
                return true;
            }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
        }

        string ReadOnlyPath(string name)
        {
            CheckAncestors();
            string path = Path.GetFullPath(Path.Combine(directory, name));
            if (!path.StartsWith(prefix, pathComparison)) throw new ArgumentException("录像路径必须位于给定目录内。");
            CheckAttributes(path, false);
            return path;
        }

        FileStream OpenExistingReadLock()
        {
            try { return new FileStream(ReadOnlyPath(".library.lock"), FileMode.Open, FileAccess.Read, FileShare.Read); }
            // 未保存过的库没有锁文件；读取不能为了互斥新建文件，也不承诺跨文件事务快照。
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
        }

        BattleRecord LoadReadOnly(string id)
        {
            using (OpenExistingReadLock())
            {
                string json;
                try { json = File.ReadAllText(ReadOnlyPath(id + ".json"), Utf8); }
                catch (DecoderFallbackException ex) { throw new FormatException("录像不是有效的 UTF-8 文本：" + id, ex); }
                var entry = ParseEntry(id, json, out var record);
                RejectUnavailable(entry.UnavailableReason);
                return record;
            }
        }
    }
}
