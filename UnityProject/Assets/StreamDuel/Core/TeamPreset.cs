using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace StreamDuel.Core
{
    public sealed class TeamPreset
    {
        // 原名用于存档和队伍码保真；显示时统一走过滤后的属性。
        public string Name = "";
        public string PackId = "";
        public List<string> Ids = new List<string>();

        public TextInputResult NameInput { get { return TextInput.Clean(Name, TextInputKind.TeamName); } }
        public string DisplayName { get { return NameInput.Text; } }

        public List<string> Check(ContentDb db, string seasonId)
        {
            if (db == null) throw new ArgumentNullException(nameof(db));
            var errors = new List<string>();
            if (!string.Equals(PackId, db.PackId, StringComparison.Ordinal))
                errors.Add("这套队伍属于其他内容包");
            errors.AddRange(TeamCheck.Validate(db, seasonId, Ids));
            return errors;
        }
    }

    public sealed class TeamPresetList
    {
        public List<TeamPreset> Items = new List<TeamPreset>();

        public string ToJson()
        {
            if (Items == null) throw new FormatException("队伍预设列表不能为空值");
            var text = new StringBuilder("{\"items\":[");
            for (int i = 0; i < Items.Count; i++)
            {
                if (i > 0) text.Append(',');
                var item = Items[i];
                if (item == null) throw new FormatException("队伍预设不能为空值");
                text.Append("{\"name\":");
                TeamPresetJson.WriteString(text, item.Name);
                text.Append(",\"packId\":");
                TeamPresetJson.WriteString(text, item.PackId);
                text.Append(",\"ids\":");
                TeamPresetJson.WriteIds(text, item.Ids);
                text.Append('}');
            }
            return text.Append("]}").ToString();
        }

        public static TeamPresetList FromJson(string json)
        {
            try
            {
                var root = MiniJson.Parse(json) as Dictionary<string, object>
                    ?? throw new FormatException("队伍预设存档必须是对象");
                var items = TeamPresetJson.Required(root, "items") as List<object>
                    ?? throw new FormatException("队伍预设列表必须是数组");
                var result = new TeamPresetList();
                foreach (var value in items)
                {
                    var item = value as Dictionary<string, object>
                        ?? throw new FormatException("队伍预设必须是对象");
                    result.Items.Add(new TeamPreset
                    {
                        Name = TeamPresetJson.ReadString(TeamPresetJson.Required(item, "name")),
                        PackId = TeamPresetJson.ReadString(TeamPresetJson.Required(item, "packId")),
                        Ids = TeamPresetJson.ReadIds(TeamPresetJson.Required(item, "ids"))
                    });
                }
                return result;
            }
            catch (FormatException)
            {
                // 整份存档成功后才返回，损坏的后续条目不能留下看似完整的部分队伍。
                return new TeamPresetList();
            }
        }
    }

    internal static class TeamPresetJson
    {
        internal static object Required(Dictionary<string, object> source, string key)
        {
            if (!source.TryGetValue(key, out var value))
                throw new FormatException("队伍预设缺少字段：" + key);
            return value;
        }

        internal static string ReadString(object value)
        {
            return value as string ?? throw new FormatException("队伍预设文字字段必须是字符串");
        }

        internal static List<string> ReadIds(object value)
        {
            var source = value as List<object> ?? throw new FormatException("队伍成员必须是数组");
            var result = new List<string>(source.Count);
            foreach (var id in source) result.Add(ReadString(id));
            return result;
        }

        internal static void WriteString(StringBuilder text, string value)
        {
            if (value == null) throw new FormatException("队伍预设文字字段不能为空值");
            text.Append('"');
            foreach (char c in value)
            {
                if (c == '"') text.Append("\\\"");
                else if (c == '\\') text.Append("\\\\");
                else if (c < ' ' || char.IsSurrogate(c))
                    text.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                else text.Append(c);
            }
            text.Append('"');
        }

        internal static void WriteIds(StringBuilder text, IList<string> ids)
        {
            if (ids == null) throw new FormatException("队伍成员数组不能为空值");
            text.Append('[');
            for (int i = 0; i < ids.Count; i++)
            {
                if (i > 0) text.Append(',');
                WriteString(text, ids[i]);
            }
            text.Append(']');
        }
    }
}
