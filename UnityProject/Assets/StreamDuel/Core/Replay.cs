using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace StreamDuel.Core
{
    public enum BattleRecordEventType { Turn, Replace, Lead }

    public sealed class BattleRecordEvent
    {
        public BattleRecordEventType Type { get; set; }
        public BattleAction A0 { get; set; }
        public BattleAction A1 { get; set; }
        public int Side { get; set; }
        public int To { get; set; }
        // AI 选择也消耗同一随机流，回放不调用 AI，所以必须保存结算前的位置。
        public uint? RngState { get; set; }
    }

    public sealed class BattleRecord
    {
        public uint Seed { get; set; }
        public string PackId { get; set; }
        public bool LeadPick { get; set; }
        public IList<string>[] Teams { get; set; }
        public List<BattleRecordEvent> Events { get; } = new List<BattleRecordEvent>();

        public string ToJson()
        {
            var sb = new StringBuilder();
            sb.Append("{\"seed\":").Append(Seed.ToString(CultureInfo.InvariantCulture)).Append(",\"packId\":");
            WriteString(sb, PackId);
            if (LeadPick) sb.Append(",\"leadPick\":true");
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
                if (e.Type == BattleRecordEventType.Turn)
                {
                    sb.Append("{\"type\":\"turn\",\"a0\":");
                    WriteAction(sb, e.A0);
                    sb.Append(",\"a1\":");
                    WriteAction(sb, e.A1);
                }
                else if (e.Type == BattleRecordEventType.Replace || e.Type == BattleRecordEventType.Lead)
                {
                    sb.Append(e.Type == BattleRecordEventType.Lead ? "{\"type\":\"lead\",\"side\":" : "{\"type\":\"replace\",\"side\":");
                    sb.Append(e.Side.ToString(CultureInfo.InvariantCulture));
                    sb.Append(",\"to\":").Append(e.To.ToString(CultureInfo.InvariantCulture));
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
                Seed = (uint)ReadInteger(Required(root, "seed"), "seed", 0, uint.MaxValue),
                PackId = ReadString(Required(root, "packId"), "packId"),
                Teams = new IList<string>[2]
            };
            if (root.TryGetValue("leadPick", out var leadPick))
            {
                if (!(leadPick is bool enabled)) throw new FormatException("leadPick 必须是布尔值");
                record.LeadPick = enabled;
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
                else if (type == "replace" || type == "lead")
                {
                    e.Type = type == "lead" ? BattleRecordEventType.Lead : BattleRecordEventType.Replace;
                    e.Side = (int)ReadInteger(Required(obj, "side"), "side", 0, 1);
                    e.To = (int)ReadInteger(Required(obj, "to"), "to", 0, int.MaxValue);
                }
                else throw new FormatException("未知回放事件类型：" + type);
                if (obj.TryGetValue("rng", out var state)) e.RngState = (uint)ReadInteger(state, "rng", 0, uint.MaxValue);
                record.Events.Add(e);
            }
            return record;
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

    public static class Replay
    {
        public static Battle Run(ContentDb db, BattleRecord r)
        {
            if (db == null) throw new ArgumentNullException(nameof(db));
            if (r == null) throw new ArgumentNullException(nameof(r));
            if (r.PackId != db.PackId) throw new InvalidOperationException("回放内容包不匹配：" + r.PackId + " / " + db.PackId);
            var battle = new Battle(db, r.Teams, new[] { "你", "对手" }, r.Seed, r.LeadPick);
            foreach (var e in r.Events)
            {
                if (e.RngState.HasValue) battle.RestoreReplayRng(e.RngState.Value);
                if (e.Type == BattleRecordEventType.Turn) battle.ResolveTurn(e.A0, e.A1);
                else if (e.Type == BattleRecordEventType.Replace) battle.Replace(e.Side, e.To);
                else if (e.Type == BattleRecordEventType.Lead) battle.ChooseLead(e.Side, e.To);
                else throw new FormatException("未知回放事件类型：" + e.Type);
            }
            return battle;
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
