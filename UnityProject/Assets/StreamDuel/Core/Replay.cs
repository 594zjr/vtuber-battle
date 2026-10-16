using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace StreamDuel.Core
{
    public enum BattleRecordEventType { Turn, Replace, Lead, Ban, Surrender, DrawOffer, DrawResponse, Branch }

    public sealed class BattleRecordEvent
    {
        public BattleRecordEventType Type { get; set; }
        public BattleAction A0 { get; set; }
        public BattleAction A1 { get; set; }
        public int Side { get; set; }
        public int To { get; set; }
        public bool Accepted { get; set; }
        public ReplayBranchInfo Branch { get; set; }
        // AI 选择也消耗同一随机流，回放不调用 AI，所以必须保存结算前的位置。
        public uint? RngState { get; set; }
    }

    public sealed class BattleRecord
    {
        public const int CurrentVersion = 5;
        public const int DefaultVersion = 2;
        public const int TerminationVersion = 3;
        public const int BranchVersion = 4;
        public const int MultiBanVersion = 5;

        public int Version { get; set; } = DefaultVersion;
        public string ContentHash { get; set; }
        public uint Seed { get; set; }
        public string PackId { get; set; }
        public bool LeadPick { get; set; }
        public bool BanOne { get; set; }
        public int BanCount { get; set; }
        public List<string> Modifiers { get; set; }
        public string[] Names { get; set; }
        public IList<string>[] Teams { get; set; }
        public List<BattleRecordEvent> Events { get; } = new List<BattleRecordEvent>();
        public ReplayBranchInfo Branch
        {
            get
            {
                for (int i = Events.Count - 1; i >= 0; i--)
                    if (Events[i]?.Type == BattleRecordEventType.Branch) return Events[i].Branch;
                return null;
            }
        }
        public bool IsBranch { get { return Branch != null; } }

        public string ToJson()
        {
            int banCount = ValidateBanSettings();
            var sb = new StringBuilder();
            sb.Append("{\"seed\":").Append(Seed.ToString(CultureInfo.InvariantCulture)).Append(",\"packId\":");
            WriteString(sb, PackId);
            sb.Append(",\"version\":").Append(Version.ToString(CultureInfo.InvariantCulture));
            if (ContentHash != null)
            {
                sb.Append(",\"contentHash\":");
                WriteString(sb, ContentHash);
            }
            if (LeadPick) sb.Append(",\"leadPick\":true");
            if (banCount == 1) sb.Append(",\"banOne\":true");
            else if (banCount == 2) sb.Append(",\"banCount\":2");
            if (Modifiers != null && Modifiers.Count > 0)
            {
                sb.Append(",\"modifiers\":[");
                for (int i = 0; i < Modifiers.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    WriteString(sb, Modifiers[i]);
                }
                sb.Append(']');
            }
            if (Names != null)
            {
                if (Names.Length != 2) throw new FormatException("对战记录必须包含两个名称");
                sb.Append(",\"names\":[");
                WriteString(sb, Names[0]);
                sb.Append(',');
                WriteString(sb, Names[1]);
                sb.Append(']');
            }
            sb.Append(",\"teams\":[");
            for (int s = 0; s < Teams.Length; s++)
            {
                if (s > 0) sb.Append(',');
                sb.Append('[');
                for (int i = 0; i < Teams[s].Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    WriteString(sb, Teams[s][i]);
                }
                sb.Append(']');
            }
            sb.Append("],\"events\":[");
            for (int i = 0; i < Events.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var e = Events[i];
                ValidateEventVersion(Version, e);
                if (e.Type == BattleRecordEventType.Turn)
                {
                    sb.Append("{\"type\":\"turn\",\"a0\":");
                    WriteAction(sb, e.A0);
                    sb.Append(",\"a1\":");
                    WriteAction(sb, e.A1);
                }
                else if (e.Type == BattleRecordEventType.Replace || e.Type == BattleRecordEventType.Lead || e.Type == BattleRecordEventType.Ban)
                {
                    string type = e.Type == BattleRecordEventType.Ban ? "ban" : e.Type == BattleRecordEventType.Lead ? "lead" : "replace";
                    sb.Append("{\"type\":");
                    WriteString(sb, type);
                    sb.Append(",\"side\":");
                    sb.Append(e.Side.ToString(CultureInfo.InvariantCulture));
                    sb.Append(",\"to\":").Append(e.To.ToString(CultureInfo.InvariantCulture));
                }
                else if (e.Type == BattleRecordEventType.Surrender || e.Type == BattleRecordEventType.DrawOffer || e.Type == BattleRecordEventType.DrawResponse)
                {
                    sb.Append("{\"type\":");
                    WriteString(sb, e.Type == BattleRecordEventType.Surrender ? "surrender" : e.Type == BattleRecordEventType.DrawOffer ? "drawOffer" : "drawResponse");
                    sb.Append(",\"side\":").Append(e.Side.ToString(CultureInfo.InvariantCulture));
                    if (e.Type == BattleRecordEventType.DrawResponse) sb.Append(",\"accept\":").Append(e.Accepted ? "true" : "false");
                }
                else if (e.Type == BattleRecordEventType.Branch)
                {
                    sb.Append("{\"type\":\"branch\",\"sourceHash\":");
                    WriteString(sb, e.Branch.SourceHash);
                    sb.Append(",\"turn\":").Append(e.Branch.Turn.ToString(CultureInfo.InvariantCulture));
                    sb.Append(",\"humanSide\":").Append(e.Branch.HumanSide.ToString(CultureInfo.InvariantCulture));
                    sb.Append(",\"level\":");
                    WriteString(sb, e.Branch.Level.ToString());
                    sb.Append(",\"seed\":").Append(e.Branch.Seed.ToString(CultureInfo.InvariantCulture));
                }
                else throw new FormatException("未知回放事件类型：" + e.Type);
                if (e.RngState.HasValue) sb.Append(",\"rng\":").Append(e.RngState.Value.ToString(CultureInfo.InvariantCulture));
                sb.Append('}');
            }
            return sb.Append("]}").ToString();
        }

        public static BattleRecord FromJson(string json)
        {
            if (json == null) throw new ArgumentNullException(nameof(json));
            var root = ReadObject(MiniJson.Parse(json), "记录");
            var teams = ReadArray(Required(root, "teams"), "teams");
            if (teams.Count != 2) throw new FormatException("对战记录必须包含两支队伍");
            var record = new BattleRecord
            {
                Version = root.TryGetValue("version", out var version)
                    ? (int)ReadInteger(version, "version", 1, int.MaxValue) : 1,
                Seed = (uint)ReadInteger(Required(root, "seed"), "seed", 0, uint.MaxValue),
                PackId = ReadString(Required(root, "packId"), "packId"),
                Teams = new IList<string>[2]
            };
            if (root.TryGetValue("contentHash", out var contentHash))
                record.ContentHash = ReadString(contentHash, "contentHash");
            if (root.TryGetValue("leadPick", out var leadPick))
            {
                if (!(leadPick is bool enabled)) throw new FormatException("leadPick 必须是布尔值");
                record.LeadPick = enabled;
            }
            if (root.TryGetValue("banOne", out var banOne))
            {
                if (!(banOne is bool enabled)) throw new FormatException("banOne 必须是布尔值");
                record.BanOne = enabled;
            }
            if (root.TryGetValue("banCount", out var banCount))
                record.BanCount = (int)ReadInteger(banCount, "banCount", 0, 2);
            int effectiveBanCount = record.ValidateBanSettings();
            record.BanOne = effectiveBanCount == 1;
            record.BanCount = effectiveBanCount == 2 ? 2 : 0;
            if (root.TryGetValue("modifiers", out var modifiersValue))
            {
                var modifiers = ReadArray(modifiersValue, "modifiers");
                record.Modifiers = new List<string>();
                for (int i = 0; i < modifiers.Count; i++)
                    record.Modifiers.Add(ReadString(modifiers[i], "modifiers[" + i + "]"));
            }
            if (root.TryGetValue("names", out var namesValue))
            {
                var names = ReadArray(namesValue, "names");
                if (names.Count != 2) throw new FormatException("对战记录必须包含两个名称");
                record.Names = new[] { ReadString(names[0], "names[0]"), ReadString(names[1], "names[1]") };
            }
            for (int s = 0; s < teams.Count; s++)
            {
                var team = new List<string>();
                foreach (var id in ReadArray(teams[s], "teams[" + s + "]")) team.Add(ReadString(id, "主播 id"));
                record.Teams[s] = team;
            }
            foreach (var item in ReadArray(Required(root, "events"), "events"))
            {
                var obj = ReadObject(item, "事件");
                var e = new BattleRecordEvent();
                string type = ReadString(Required(obj, "type"), "type");
                if (type == "turn")
                {
                    e.Type = BattleRecordEventType.Turn;
                    e.A0 = ReadAction(Required(obj, "a0"));
                    e.A1 = ReadAction(Required(obj, "a1"));
                }
                else if (type == "replace" || type == "lead" || type == "ban")
                {
                    e.Type = type == "ban" ? BattleRecordEventType.Ban : type == "lead" ? BattleRecordEventType.Lead : BattleRecordEventType.Replace;
                    e.Side = (int)ReadInteger(Required(obj, "side"), "side", 0, 1);
                    e.To = (int)ReadInteger(Required(obj, "to"), "to", 0, int.MaxValue);
                }
                else if (type == "surrender" || type == "drawOffer" || type == "drawResponse")
                {
                    e.Type = type == "surrender" ? BattleRecordEventType.Surrender : type == "drawOffer" ? BattleRecordEventType.DrawOffer : BattleRecordEventType.DrawResponse;
                    e.Side = (int)ReadInteger(Required(obj, "side"), "side", type == "surrender" ? -1 : 0, 1);
                    if (e.Type == BattleRecordEventType.DrawResponse)
                    {
                        if (!(Required(obj, "accept") is bool accepted)) throw new FormatException("accept 必须是布尔值");
                        e.Accepted = accepted;
                    }
                }
                else if (type == "branch")
                {
                    e.Type = BattleRecordEventType.Branch;
                    string levelName = ReadString(Required(obj, "level"), "level");
                    if (!Enum.TryParse(levelName, out AiLevel level) || !Enum.IsDefined(typeof(AiLevel), level)
                        || levelName != level.ToString()) throw new FormatException("分支录像的电脑难度无效。");
                    e.Branch = new ReplayBranchInfo(ReadString(Required(obj, "sourceHash"), "sourceHash"),
                        (int)ReadInteger(Required(obj, "turn"), "turn", 1, int.MaxValue),
                        (int)ReadInteger(Required(obj, "humanSide"), "humanSide", 0, 1), level,
                        (uint)ReadInteger(Required(obj, "seed"), "seed", 0, uint.MaxValue));
                }
                else throw new FormatException("未知回放事件类型：" + type);
                ValidateEventVersion(record.Version, e);
                if (obj.TryGetValue("rng", out var state)) e.RngState = (uint)ReadInteger(state, "rng", 0, uint.MaxValue);
                record.Events.Add(e);
            }
            return record;
        }

        internal int ValidateBanSettings()
        {
            if (BanCount < 0 || BanCount > 2) throw new FormatException("录像每方禁用人数只能是 0、1 或 2。");
            int count = BanCount > 0 ? BanCount : BanOne ? 1 : 0;
            if (count == 2 && Version < MultiBanVersion) throw new FormatException("每方禁用两人需要录像版本 5。");
            return count;
        }

        internal static void ValidateEventVersion(int version, BattleRecordEvent entry)
        {
            if (entry == null) throw new FormatException("回放事件不能为空");
            if (entry.Type == BattleRecordEventType.Branch)
            {
                if (version < BranchVersion) throw new FormatException("接手分支事件需要录像版本 4。");
                ReplayBranch.ValidateInfo(entry.Branch);
            }
            else if (entry.Branch != null) throw new FormatException("分支来源只能用于 branch 录像事件。");
            bool termination = entry.Type == BattleRecordEventType.Surrender || entry.Type == BattleRecordEventType.DrawOffer
                || entry.Type == BattleRecordEventType.DrawResponse || entry.Type == BattleRecordEventType.Turn
                && (entry.A0.Type == ActionType.Surrender || entry.A1.Type == ActionType.Surrender);
            if (termination && version < TerminationVersion) throw new FormatException("投降或和局事件需要录像版本 3。");
            if (entry.Type == BattleRecordEventType.Turn
                && (entry.A0.Type == ActionType.Surrender || entry.A1.Type == ActionType.Surrender))
                throw new FormatException("投降必须使用独立 surrender 录像事件，不能记录为普通回合。");
            if (entry.Type == BattleRecordEventType.Surrender && (entry.Side < -1 || entry.Side > 1)
                || (entry.Type == BattleRecordEventType.DrawOffer || entry.Type == BattleRecordEventType.DrawResponse) && (entry.Side < 0 || entry.Side > 1))
                throw new FormatException("终局协商事件的对战方不合法。");
        }

        static void WriteAction(StringBuilder sb, BattleAction action)
        {
            sb.Append("{\"t\":");
            WriteString(sb, action.Type.ToString());
            sb.Append(",\"i\":").Append(action.Index.ToString(CultureInfo.InvariantCulture)).Append('}');
        }

        static void WriteString(StringBuilder sb, string value)
        {
            if (value == null) throw new FormatException("回放字符串不能为 null");
            sb.Append('"');
            foreach (char c in value)
            {
                if (c == '"') sb.Append("\\\"");
                else if (c == '\\') sb.Append("\\\\");
                else if (c < ' ' || char.IsSurrogate(c)) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                else sb.Append(c);
            }
            sb.Append('"');
        }

        static BattleAction ReadAction(object value)
        {
            var obj = ReadObject(value, "行动");
            string name = ReadString(Required(obj, "t"), "t");
            if (!Enum.TryParse(name, out ActionType type)) throw new FormatException("未知行动类型：" + name);
            return new BattleAction { Type = type, Index = (int)ReadInteger(Required(obj, "i"), "i", int.MinValue, int.MaxValue) };
        }

        static object Required(Dictionary<string, object> obj, string key)
        {
            if (!obj.TryGetValue(key, out var value)) throw new FormatException("对战记录缺少字段：" + key);
            return value;
        }

        static Dictionary<string, object> ReadObject(object value, string field)
        {
            return value as Dictionary<string, object> ?? throw new FormatException(field + " 必须是对象");
        }

        static List<object> ReadArray(object value, string field)
        {
            return value as List<object> ?? throw new FormatException(field + " 必须是数组");
        }

        static string ReadString(object value, string field)
        {
            return value as string ?? throw new FormatException(field + " 必须是字符串");
        }

        static double ReadInteger(object value, string field, double min, double max)
        {
            if (!(value is double n) || double.IsNaN(n) || n < min || n > max || n != Math.Truncate(n))
                throw new FormatException(field + " 必须是范围内的整数");
            return n;
        }
    }

    public static partial class Replay
    {
        public static Battle Run(ContentDb db, BattleRecord r)
        {
            if (db == null) throw new ArgumentNullException(nameof(db));
            if (r == null) throw new ArgumentNullException(nameof(r));
            if (r.ContentHash != null)
            {
                string contentHash = db.ContentHash();
                if (!string.Equals(r.ContentHash, contentHash, StringComparison.Ordinal))
                    throw new InvalidOperationException("内容不一致：记录指纹 " + r.ContentHash + "，当前指纹 " + contentHash);
            }
            if (r.PackId != db.PackId) throw new InvalidOperationException("回放内容包不匹配：" + r.PackId + " / " + db.PackId);
            return RunEvents(db, r);
        }

        static Battle RunEvents(ContentDb db, BattleRecord r)
        {
            r.ValidateBanSettings();
            var battle = new Battle(db, r.Teams, r.Names ?? new[] { "你", "对手" },
                new BattleOptions { Seed = r.Seed, LeadPick = r.LeadPick, BanOne = r.BanOne, BanCount = r.BanCount, Modifiers = r.Modifiers });
            foreach (var e in r.Events)
            {
                BattleRecord.ValidateEventVersion(r.Version, e);
                ApplyEvent(battle, e);
            }
            return battle;
        }

        internal static void ApplyEvent(Battle battle, BattleRecordEvent entry)
        {
            if (entry.RngState.HasValue) battle.RestoreReplayRng(entry.RngState.Value);
            switch (entry.Type)
            {
                case BattleRecordEventType.Turn: battle.ResolveTurn(entry.A0, entry.A1); break;
                case BattleRecordEventType.Replace: battle.Replace(entry.Side, entry.To); break;
                case BattleRecordEventType.Lead: battle.ChooseLead(entry.Side, entry.To); break;
                case BattleRecordEventType.Ban: battle.ChooseBan(entry.Side, entry.To); break;
                case BattleRecordEventType.Surrender:
                    battle.ResolveSurrenders(entry.Side == -1 || entry.Side == 0, entry.Side == -1 || entry.Side == 1); break;
                case BattleRecordEventType.DrawOffer: battle.OfferDraw(entry.Side); break;
                case BattleRecordEventType.DrawResponse: battle.RespondDraw(entry.Side, entry.Accepted); break;
                case BattleRecordEventType.Branch: battle.ApplyReplayBranch(entry.Branch); break;
                default: throw new FormatException("未知回放事件类型：" + entry.Type);
            }
        }
    }

    public sealed partial class Battle
    {
        public BattleRecord Record { get; }

        internal void RestoreReplayRng(uint state)
        {
            rng.State = state;
        }
    }
}
