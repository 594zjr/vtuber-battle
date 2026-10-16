using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace StreamDuel.Core
{
    public sealed class PackStats
    {
        public int Wins, Losses, Draws;
        public Dictionary<string, int> Picks = new Dictionary<string, int>(StringComparer.Ordinal);
        public Dictionary<string, int> PickWins = new Dictionary<string, int>(StringComparer.Ordinal);
    }

    public sealed class Profile
    {
        public const string DefaultRatingFormat = "默认";
        public Dictionary<string, PackStats> Packs = new Dictionary<string, PackStats>(StringComparer.Ordinal);
        public Dictionary<string, PlayerRating> Ratings = new Dictionary<string, PlayerRating>(StringComparer.Ordinal);

        public void Record(Battle b, int playerSide)
        {
            if (b == null) throw new ArgumentNullException(nameof(b));
            if (playerSide < 0 || playerSide >= b.Sides.Length) throw new ArgumentOutOfRangeException(nameof(playerSide));
            if (b.Phase != Phase.Over) throw new InvalidOperationException("对战尚未结束，不能记录战绩");
            if (b.Winner < -1 || b.Winner >= b.Sides.Length) throw new InvalidOperationException("对战没有有效的胜负结果");
            // 接手局允许保存录像，但不能把已知历史的练习结果累计为正式战绩。
            if (b.IsBranch) return;

            PackStats stats;
            if (!Packs.TryGetValue(b.Db.PackId, out stats)) stats = new PackStats();
            bool won = b.Winner == playerSide;
            int wins = checked(stats.Wins + (won ? 1 : 0));
            int losses = checked(stats.Losses + (b.Winner >= 0 && !won ? 1 : 0));
            int draws = checked(stats.Draws + (b.Winner == -1 ? 1 : 0));
            var picks = new Dictionary<string, int>(stats.Picks, StringComparer.Ordinal);
            var pickWins = new Dictionary<string, int>(stats.PickWins, StringComparer.Ordinal);
            // 最终队伍已移除被禁主播；下播和是否登场都不改变本局的选用名单。
            foreach (var fighter in b.Sides[playerSide].Team)
            {
                string id = fighter.Def.Id;
                int count;
                picks.TryGetValue(id, out count);
                picks[id] = checked(count + 1);
                pickWins.TryGetValue(id, out count);
                pickWins[id] = checked(count + (won ? 1 : 0));
            }
            // 先完成所有计数，溢出时不会留下只记了部分主播的档案。
            stats.Wins = wins;
            stats.Losses = losses;
            stats.Draws = draws;
            stats.Picks = picks;
            stats.PickWins = pickWins;
            Packs[b.Db.PackId] = stats;
        }

        public string ToJson()
        {
            if (Ratings == null) throw new FormatException("档案排位分记录不能为空。");
            var sb = new StringBuilder("{\"packs\":{");
            bool first = true;
            foreach (string id in Packs.Keys.OrderBy(key => key, StringComparer.Ordinal))
            {
                if (!first) sb.Append(',');
                first = false;
                WriteString(sb, id);
                var stats = Packs[id];
                sb.Append(":{\"draws\":").Append(stats.Draws.ToString(CultureInfo.InvariantCulture));
                sb.Append(",\"losses\":").Append(stats.Losses.ToString(CultureInfo.InvariantCulture));
                sb.Append(",\"pickWins\":");
                WriteCounts(sb, stats.PickWins);
                sb.Append(",\"picks\":");
                WriteCounts(sb, stats.Picks);
                sb.Append(",\"wins\":").Append(stats.Wins.ToString(CultureInfo.InvariantCulture));
                sb.Append('}');
            }
            sb.Append('}');
            // 没有排位记录的旧档案保持原输出，避免仅查看或单机对战就改写存档格式。
            if (Ratings.Count > 0)
            {
                sb.Append(",\"ratings\":{");
                first = true;
                foreach (string format in Ratings.Keys.OrderBy(key => key, StringComparer.Ordinal))
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteString(sb, format);
                    var rating = Ratings[format];
                    if (rating == null) throw new FormatException("档案排位分记录不能为空。");
                    sb.Append(':').Append(rating.ToJson());
                }
                sb.Append('}');
            }
            return sb.Append('}').ToString();
        }

        public static Profile FromJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new Profile();
            try
            {
                ValidateTokens(json);
                var root = ReadObject(MiniJson.Parse(json));
                var packs = ReadObject(Required(root, "packs"));
                var profile = new Profile();
                foreach (var pair in packs)
                {
                    var source = ReadObject(pair.Value);
                    profile.Packs.Add(pair.Key, new PackStats
                    {
                        Wins = ReadCount(Required(source, "wins")),
                        Losses = ReadCount(Required(source, "losses")),
                        Draws = ReadCount(Required(source, "draws")),
                        Picks = ReadCounts(Required(source, "picks")),
                        PickWins = ReadCounts(Required(source, "pickWins"))
                    });
                }
                if (root.TryGetValue("ratings", out var ratings))
                    foreach (var pair in ReadObject(ratings)) profile.Ratings.Add(pair.Key, PlayerRating.ReadValue(pair.Value));
                return profile;
            }
            catch (Exception e) when (e is FormatException || e is ArgumentException ||
                e is IndexOutOfRangeException || e is OverflowException)
            {
                // 存档损坏不能阻止开局；整份失败，避免部分导入被误当作完整战绩。
                return new Profile();
            }
        }

        static void WriteCounts(StringBuilder sb, Dictionary<string, int> counts)
        {
            sb.Append('{');
            bool first = true;
            foreach (string id in counts.Keys.OrderBy(key => key, StringComparer.Ordinal))
            {
                if (!first) sb.Append(',');
                first = false;
                WriteString(sb, id);
                sb.Append(':').Append(counts[id].ToString(CultureInfo.InvariantCulture));
            }
            sb.Append('}');
        }

        static void WriteString(StringBuilder sb, string value)
        {
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

        static Dictionary<string, object> ReadObject(object value)
        {
            return value as Dictionary<string, object> ?? throw new FormatException("档案字段必须是对象");
        }

        static object Required(Dictionary<string, object> source, string key)
        {
            object value;
            if (!source.TryGetValue(key, out value)) throw new FormatException("档案缺少字段：" + key);
            return value;
        }

        static int ReadCount(object value)
        {
            if (!(value is double count) || double.IsNaN(count) || count < 0 || count > int.MaxValue || count != Math.Truncate(count))
                throw new FormatException("档案计数必须是非负整数");
            return (int)count;
        }

        static Dictionary<string, int> ReadCounts(object value)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var pair in ReadObject(value)) counts.Add(pair.Key, ReadCount(pair.Value));
            return counts;
        }

        static void ValidateTokens(string json)
        {
            // 档案保留独立的词法检查，再交给通用解析器验证结构。
            int depth = 0;
            for (int i = 0; i < json.Length; i++)
            {
                char c = json[i];
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n' || c == ',' || c == ':') continue;
                if (c == '{' || c == '[')
                {
                    if (++depth > 64) throw new FormatException("档案嵌套过深");
                    continue;
                }
                if (c == '}' || c == ']')
                {
                    if (--depth < 0) throw new FormatException("档案括号不匹配");
                    continue;
                }
                if (c == '"')
                {
                    bool closed = false;
                    while (++i < json.Length)
                    {
                        c = json[i];
                        if (c == '"') { closed = true; break; }
                        if (c < ' ') throw new FormatException("档案字符串含未转义的控制字符");
                        if (c != '\\') continue;
                        if (++i >= json.Length) throw new FormatException("档案转义未结束");
                        c = json[i];
                        if (c == 'u')
                        {
                            for (int n = 0; n < 4; n++)
                            {
                                if (++i >= json.Length || !IsHex(json[i])) throw new FormatException("档案 Unicode 转义无效");
                            }
                        }
                        else if ("\"\\/bfnrt".IndexOf(c) < 0) throw new FormatException("档案转义无效");
                    }
                    if (!closed) throw new FormatException("档案字符串未结束");
                    continue;
                }
                if (c == '-' || IsDigit(c))
                {
                    if (c == '-' && ++i >= json.Length) throw new FormatException("档案数字未结束");
                    if (json[i] == '0') i++;
                    else
                    {
                        if (json[i] < '1' || json[i] > '9') throw new FormatException("档案数字无效");
                        while (i < json.Length && IsDigit(json[i])) i++;
                    }
                    if (i < json.Length && IsDigit(json[i])) throw new FormatException("档案数字含前导零");
                    if (i < json.Length && json[i] == '.')
                    {
                        if (++i >= json.Length || !IsDigit(json[i])) throw new FormatException("档案小数无效");
                        while (i < json.Length && IsDigit(json[i])) i++;
                    }
                    if (i < json.Length && (json[i] == 'e' || json[i] == 'E'))
                    {
                        i++;
                        if (i < json.Length && (json[i] == '+' || json[i] == '-')) i++;
                        if (i >= json.Length || !IsDigit(json[i])) throw new FormatException("档案指数无效");
                        while (i < json.Length && IsDigit(json[i])) i++;
                    }
                    i--;
                    continue;
                }
                string literal = c == 't' ? "true" : c == 'f' ? "false" : c == 'n' ? "null" : null;
                if (literal == null || i + literal.Length > json.Length || string.CompareOrdinal(json, i, literal, 0, literal.Length) != 0)
                    throw new FormatException("档案字符无效");
                i += literal.Length - 1;
            }
            if (depth != 0) throw new FormatException("档案括号未结束");
        }

        static bool IsDigit(char c) { return c >= '0' && c <= '9'; }
        static bool IsHex(char c) { return IsDigit(c) || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'); }
    }
}
