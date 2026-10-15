using System;
using System.Collections.Generic;

namespace StreamDuel.Core
{
    public sealed class ReplayCompat
    {
        public bool CanReplay { get; }
        public string Reason { get; }

        internal ReplayCompat(bool canReplay, string reason)
        {
            CanReplay = canReplay;
            Reason = reason;
        }
    }

    public static partial class Replay
    {
        public static ReplayCompat Check(ContentDb db, BattleRecord r)
        {
            if (r == null) return Incompatible("录像记录缺失。");
            string versionError = VersionError(r);
            if (versionError != null) return Incompatible(versionError);
            if (db == null) return Incompatible("缺少录像所需的内容包：" + (r.PackId ?? "未指定"));
            if (string.IsNullOrWhiteSpace(r.PackId)) return Incompatible("录像缺少内容包编号。");
            if (!string.Equals(r.PackId, db.PackId, StringComparison.Ordinal))
                return Incompatible("回放内容包不匹配：需要 " + r.PackId + "，当前为 " + (db.PackId ?? "未加载"));

            try
            {
                string structureError = StructureError(db, r);
                if (structureError != null) return Incompatible(structureError);
                db.Validate();
                string currentHash = db.ContentHash();
                if (r.ContentHash != null && !string.Equals(r.ContentHash, currentHash, StringComparison.Ordinal))
                    return new ReplayCompat(true, "内容指纹不同：记录指纹 " + r.ContentHash + "，当前指纹 " + currentHash
                        + "。可以尽力重放，但规则或内容已改变，结果可能不同。");
                return new ReplayCompat(true, string.Empty);
            }
            catch (Exception error) when (error is KeyError || error is ArgumentException
                || error is InvalidOperationException || error is NullReferenceException
                || error is KeyNotFoundException || error is IndexOutOfRangeException)
            {
                return Incompatible("录像所需的内容或结构无效：" + error.Message);
            }
        }

        public static Battle RunBestEffort(ContentDb db, BattleRecord r)
        {
            if (db == null) throw new ArgumentNullException(nameof(db));
            if (r == null) throw new ArgumentNullException(nameof(r));
            var compatibility = Check(db, r);
            if (!compatibility.CanReplay) throw new InvalidOperationException(compatibility.Reason);
            return RunEvents(db, r);
        }

        static ReplayCompat Incompatible(string reason)
        {
            return new ReplayCompat(false, reason);
        }

        static string VersionError(BattleRecord r)
        {
            if (r.Version > BattleRecord.CurrentVersion)
                return "录像来自更新的版本：版本 " + r.Version + "，当前支持至版本 " + BattleRecord.CurrentVersion;
            return r.Version < 1 ? "录像版本无效：版本号必须至少为 1。" : null;
        }

        static string StructureError(ContentDb db, BattleRecord r)
        {
            try { BattleRecord.ValidateRulesVersion(r.Version, r.RulesVersion); }
            catch (FormatException error) { return error.Message; }
            if (r.Teams == null || r.Teams.Length != 2) return "录像必须包含两支队伍。";
            if (r.Names != null && (r.Names.Length != 2 || r.Names[0] == null || r.Names[1] == null))
                return "录像必须包含两个有效的玩家名称。";
            BattleModifiers.Normalize(r.Modifiers);
            bool doubleHp = r.Modifiers != null && r.Modifiers.Contains("hp_x2");
            for (int side = 0; side < 2; side++)
            {
                var team = r.Teams[side];
                if (team == null || team.Count == 0) return "录像队伍 " + (side + 1) + " 为空。";
                if (r.BanOne && team.Count < 2) return "录像启用了禁用，但队伍不足两位主播。";
                foreach (string id in team)
                {
                    if (string.IsNullOrEmpty(id)) return "录像队伍包含空的主播编号。";
                    var streamer = db.Streamer(id);
                    if (doubleHp && streamer.Stats["hp"] > int.MaxValue / 2)
                        return "录像规则修饰器 hp_x2：" + streamer.Name + " 的双倍人气超出整数范围。";
                }
            }

            // 兼容预检不模拟回合，阶段顺序仍由实际回放校验；原始行动保留给 Normalize 处理。
            foreach (var entry in r.Events)
            {
                if (entry == null) return "录像包含空事件。";
                try { BattleRecord.ValidateEventVersion(r.Version, entry); }
                catch (FormatException error) { return error.Message; }
                if (entry.Type == BattleRecordEventType.Turn) continue;
                if (entry.Type == BattleRecordEventType.Surrender || entry.Type == BattleRecordEventType.DrawOffer
                    || entry.Type == BattleRecordEventType.DrawResponse) continue;
                if (entry.Type == BattleRecordEventType.Replace || entry.Type == BattleRecordEventType.Lead
                    || entry.Type == BattleRecordEventType.Ban)
                {
                    if (entry.Side < 0 || entry.Side > 1) return "录像事件的对战方必须是 0 或 1。";
                    int teamSide = entry.Type == BattleRecordEventType.Ban ? 1 - entry.Side : entry.Side;
                    if (entry.To < 0 || entry.To >= r.Teams[teamSide].Count) return "录像事件的主播位置不在队伍中。";
                }
                else return "录像包含未知事件类型：" + entry.Type;
            }
            return null;
        }
    }
}
