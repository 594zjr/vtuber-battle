using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;

namespace StreamDuel.Core
{
    public sealed class ViewChange
    {
        public string Path;
        public string Kind;
        public string Old;
        public string New;
    }

    /// <summary>
    /// 对独立视图快照作只读比较。Path 为 camelCase 字段、字典键及从零开始的列表下标，
    /// 不带开头的斜杠；空串表示根，键中的 ~ 和 / 分别写成 ~0 和 ~1。
    /// Old/New 是 JSON 值文本，null 引用表示不存在，文本 "null" 表示 JSON null。
    /// 枚举写名称，数字用不受文化影响的格式；非有限数值和未定义枚举不支持序列化。
    /// set 替换或创建目标（列表下标等于长度时追加），add 插入，remove 删除。
    /// 按返回顺序应用：首次刷新先 set 空容器，再 set 所有子字段；数组删除从尾部开始。
    /// 这是本项目的差量格式，非 RFC 6902。对象键顺序无意义，列表顺序保持不变。
    /// </summary>
    public static class ViewDiff
    {
        sealed class Member
        {
            public FieldInfo Field;
            public string Name;
        }

        static readonly ConcurrentDictionary<Type, Member[]> Members = new ConcurrentDictionary<Type, Member[]>();

        public static List<ViewChange> Between(BattleViewModel before, BattleViewModel after)
        {
            if (after == null) throw new ArgumentNullException(nameof(after), "新视图不能为空。");
            var changes = new List<ViewChange>();
            if (before == null) Initialize(after, "", changes);
            else Compare(before, after, "", changes);
            return changes;
        }

        static void Compare(object before, object after, string path, List<ViewChange> changes)
        {
            if (ReferenceEquals(before, after)) return;
            if (before == null || after == null || before.GetType() != after.GetType())
            {
                Set(before, after, path, changes);
                return;
            }
            Type type = after.GetType();
            if (Scalar(type))
            {
                if (!before.Equals(after)) Set(before, after, path, changes);
                return;
            }
            if (before is IDictionary oldMap && after is IDictionary newMap)
            {
                // 按序比较键值而不是字典的枚举次序，使相同内容得到相同差量。
                var oldKeys = new HashSet<string>(Keys(oldMap), StringComparer.Ordinal);
                var newKeys = new HashSet<string>(Keys(newMap), StringComparer.Ordinal);
                var keys = oldKeys.Concat(newKeys).Distinct(StringComparer.Ordinal)
                    .OrderBy(key => key, StringComparer.Ordinal);
                foreach (string key in keys)
                {
                    string childPath = Child(path, key);
                    if (!oldKeys.Contains(key))
                        changes.Add(new ViewChange { Path = childPath, Kind = "add", New = Json(newMap[key]) });
                    else if (!newKeys.Contains(key))
                        changes.Add(new ViewChange { Path = childPath, Kind = "remove", Old = Json(oldMap[key]) });
                    else Compare(oldMap[key], newMap[key], childPath, changes);
                }
                return;
            }
            if (before is IList oldList && after is IList newList)
            {
                // 状态和直播间效果共用列表，Id 可能重名；下标保留原顺序且不会合并两个条目。
                int common = Math.Min(oldList.Count, newList.Count);
                for (int index = 0; index < common; index++)
                    Compare(oldList[index], newList[index], Index(path, index), changes);
                for (int index = oldList.Count - 1; index >= common; index--)
                    changes.Add(new ViewChange { Path = Index(path, index), Kind = "remove", Old = Json(oldList[index]) });
                for (int index = common; index < newList.Count; index++)
                    changes.Add(new ViewChange { Path = Index(path, index), Kind = "add", New = Json(newList[index]) });
                return;
            }
            foreach (var member in Fields(type))
                Compare(member.Field.GetValue(before), member.Field.GetValue(after), Child(path, member.Name), changes);
        }

        static void Set(object before, object after, string path, List<ViewChange> changes)
        {
            changes.Add(new ViewChange { Path = path, Kind = "set", Old = Json(before), New = Json(after) });
        }

        static void Initialize(object value, string path, List<ViewChange> changes)
        {
            if (value == null || Scalar(value.GetType()))
            {
                changes.Add(new ViewChange { Path = path, Kind = "set", New = Json(value) });
                return;
            }
            bool list = value is IList;
            changes.Add(new ViewChange { Path = path, Kind = "set", New = list ? "[]" : "{}" });
            if (value is IDictionary map)
            {
                foreach (string key in Keys(map)) Initialize(map[key], Child(path, key), changes);
            }
            else if (value is IList values)
            {
                for (int index = 0; index < values.Count; index++) Initialize(values[index], Index(path, index), changes);
            }
            else
            {
                foreach (var member in Fields(value.GetType()))
                    Initialize(member.Field.GetValue(value), Child(path, member.Name), changes);
            }
        }

        static Member[] Fields(Type type)
        {
            // 只缓存字段描述，不缓存快照或值，避免跨局引用与陈旧数据。
            return Members.GetOrAdd(type, source =>
            {
                var fields = source.GetFields(BindingFlags.Public | BindingFlags.Instance)
                    .Select(field => new Member { Field = field, Name = CamelCase(field.Name) })
                    .OrderBy(member => member.Name, StringComparer.Ordinal).ToArray();
                if (fields.Length == 0) throw new NotSupportedException("视图字段类型不支持：" + source.FullName);
                if (fields.Select(member => member.Name).Distinct(StringComparer.Ordinal).Count() != fields.Length)
                    throw new NotSupportedException("视图字段名称转换后重复：" + source.FullName);
                return fields;
            });
        }

        static string CamelCase(string name)
        {
            var characters = name.ToCharArray();
            for (int index = 0; index < characters.Length && char.IsUpper(characters[index]); index++)
            {
                if (index > 0 && index + 1 < characters.Length && char.IsLower(characters[index + 1])) break;
                characters[index] = char.ToLowerInvariant(characters[index]);
            }
            return new string(characters);
        }

        static bool Scalar(Type type)
        {
            return type == typeof(string) || type == typeof(char) || type == typeof(bool) || type.IsEnum
                || type == typeof(byte) || type == typeof(sbyte) || type == typeof(short) || type == typeof(ushort)
                || type == typeof(int) || type == typeof(uint) || type == typeof(long) || type == typeof(ulong)
                || type == typeof(float) || type == typeof(double) || type == typeof(decimal);
        }

        static string[] Keys(IDictionary map)
        {
            var keys = new List<string>();
            foreach (object key in map.Keys)
            {
                if (!(key is string text)) throw new NotSupportedException("视图字典的键必须是字符串。");
                keys.Add(text);
            }
            keys.Sort(StringComparer.Ordinal);
            return keys.ToArray();
        }

        static string Child(string path, string key)
        {
            string escaped = key.Replace("~", "~0").Replace("/", "~1");
            return path.Length == 0 ? escaped : path + "/" + escaped;
        }

        static string Index(string path, int index)
        {
            return Child(path, index.ToString(CultureInfo.InvariantCulture));
        }

        static string Json(object value)
        {
            var text = new StringBuilder();
            WriteJson(text, value);
            return text.ToString();
        }

        static void WriteJson(StringBuilder text, object value)
        {
            if (value == null) { text.Append("null"); return; }
            Type type = value.GetType();
            if (value is string || value is char) { text.Append(NetJson.Quote(value.ToString())); return; }
            if (value is bool flag) { text.Append(flag ? "true" : "false"); return; }
            if (type.IsEnum)
            {
                string name = Enum.GetName(type, value);
                if (name == null) throw new ArgumentException("视图包含未定义的枚举值。");
                text.Append(NetJson.Quote(name));
                return;
            }
            if (value is double number)
            {
                if (double.IsNaN(number) || double.IsInfinity(number)) throw new ArgumentException("视图数值必须是有限数。");
                text.Append(number.ToString("R", CultureInfo.InvariantCulture));
                return;
            }
            if (value is float single)
            {
                if (float.IsNaN(single) || float.IsInfinity(single)) throw new ArgumentException("视图数值必须是有限数。");
                text.Append(single.ToString("R", CultureInfo.InvariantCulture));
                return;
            }
            if (Scalar(type)) { text.Append(Convert.ToString(value, CultureInfo.InvariantCulture)); return; }
            if (value is IDictionary map)
            {
                text.Append('{');
                bool first = true;
                foreach (string key in Keys(map))
                {
                    if (!first) text.Append(',');
                    first = false;
                    text.Append(NetJson.Quote(key)).Append(':');
                    WriteJson(text, map[key]);
                }
                text.Append('}');
                return;
            }
            if (value is IList list)
            {
                text.Append('[');
                for (int index = 0; index < list.Count; index++)
                {
                    if (index > 0) text.Append(',');
                    WriteJson(text, list[index]);
                }
                text.Append(']');
                return;
            }
            text.Append('{');
            var fields = Fields(type);
            for (int index = 0; index < fields.Length; index++)
            {
                if (index > 0) text.Append(',');
                text.Append(NetJson.Quote(fields[index].Name)).Append(':');
                WriteJson(text, fields[index].Field.GetValue(value));
            }
            text.Append('}');
        }
    }
}
