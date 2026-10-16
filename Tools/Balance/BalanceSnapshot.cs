using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using StreamDuel.Core;

namespace StreamDuel.Balance
{
    /// <summary>每包保存一组主播统计；集合可为子集，不据此推断完整名单。</summary>
    public sealed class BalanceSnapshot
    {
        public int Version { get; set; } = 1;
        public int GamesPerStreamer { get; set; }
        public uint Seed { get; set; }
        public string Level { get; set; }
        public string AiVersion { get; set; }
        public List<BalancePackSnapshot> Packs { get; set; } = new List<BalancePackSnapshot>();

        public void Validate()
        {
            Require(Version == 1, "不支持的版本；当前仅支持版本 1。");
            Require(GamesPerStreamer > 0, "每位主播的请求入队次数必须大于 0。");
            Require(Level == "easy" || Level == "normal" || Level == "hard" || Level == "expert", "电脑难度无效。");
            ValidateText(AiVersion, "电脑版本");
            Require(Packs != null && Packs.Count > 0, "内容包结果不能为空。");
            var packIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var pack in Packs)
            {
                Require(pack != null, "内容包结果不能为 null。");
                ValidateText(pack.PackId, "内容包编号");
                Require(packIds.Add(pack.PackId), "内容包编号重复：" + pack.PackId + "。");
                ValidateText(pack.ContentHash, "内容指纹");
                Require(pack.Battles > 0, "实际对战数必须大于 0。");
                Require(pack.Streamers != null && pack.Streamers.Count > 0, "主播结果集合不能为空。");
                var streamerIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (var streamer in pack.Streamers)
                {
                    Require(streamer != null, "主播结果不能为 null。");
                    streamer.Validate();
                    Require(streamerIds.Add(streamer.Id), "包内主播编号重复：" + pack.PackId + "/" + streamer.Id + "。");
                    Require(streamer.Games >= GamesPerStreamer, "实际入队次数不能低于请求次数：" + streamer.Id + "。");
                    // 镜像双方都计入队；减法比较避免实际对战数翻倍溢出。
                    Require(streamer.Games <= pack.Battles || streamer.Games - pack.Battles <= pack.Battles,
                        "实际入队次数不能超过对战数的两倍：" + streamer.Id + "。");
                }
            }
        }

        public string ToJson()
        {
            Validate();
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            {
                writer.WriteStartObject();
                writer.WriteNumber("version", Version);
                writer.WriteNumber("gamesPerStreamer", GamesPerStreamer);
                writer.WriteNumber("seed", Seed);
                writer.WriteString("level", Level);
                writer.WriteString("aiVersion", AiVersion);
                writer.WriteStartArray("packs");
                foreach (var pack in Packs.OrderBy(p => p.PackId, StringComparer.Ordinal))
                {
                    writer.WriteStartObject();
                    writer.WriteString("packId", pack.PackId);
                    writer.WriteString("contentHash", pack.ContentHash);
                    writer.WriteNumber("battles", pack.Battles);
                    writer.WriteStartArray("streamers");
                    foreach (var streamer in pack.Streamers.OrderBy(s => s.Id, StringComparer.Ordinal))
                    {
                        writer.WriteStartObject();
                        writer.WriteString("id", streamer.Id);
                        writer.WriteString("name", streamer.Name);
                        writer.WriteNumber("games", streamer.Games);
                        writer.WriteNumber("wins", streamer.Wins);
                        writer.WriteNumber("draws", streamer.Draws);
                        writer.WriteNumber("winRate", streamer.WinRate);
                        writer.WriteNumber("low95", streamer.Low95);
                        writer.WriteNumber("high95", streamer.High95);
                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            return Encoding.UTF8.GetString(stream.ToArray());
        }

        public static BalanceSnapshot FromJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) throw Error("JSON 不能为空。");
            try
            {
                using var document = JsonDocument.Parse(json);
                var root = Fields(document.RootElement, "根对象", "version", "gamesPerStreamer", "seed", "level", "aiVersion", "packs");
                var result = new BalanceSnapshot
                {
                    Version = Int(root["version"], "version"),
                    GamesPerStreamer = Int(root["gamesPerStreamer"], "gamesPerStreamer"),
                    Seed = UInt(root["seed"], "seed"),
                    Level = Text(root["level"], "level"),
                    AiVersion = Text(root["aiVersion"], "aiVersion")
                };
                Require(root["packs"].ValueKind == JsonValueKind.Array, "packs 必须是数组。");
                foreach (var packValue in root["packs"].EnumerateArray())
                {
                    var packFields = Fields(packValue, "内容包", "packId", "contentHash", "battles", "streamers");
                    var pack = new BalancePackSnapshot
                    {
                        PackId = Text(packFields["packId"], "packId"),
                        ContentHash = Text(packFields["contentHash"], "contentHash"),
                        Battles = Long(packFields["battles"], "battles")
                    };
                    Require(packFields["streamers"].ValueKind == JsonValueKind.Array, "streamers 必须是数组。");
                    foreach (var streamerValue in packFields["streamers"].EnumerateArray())
                    {
                        var fields = Fields(streamerValue, "主播", "id", "name", "games", "wins", "draws", "winRate", "low95", "high95");
                        pack.Streamers.Add(new BalanceStreamerSnapshot
                        {
                            Id = Text(fields["id"], "id"),
                            Name = Text(fields["name"], "name"),
                            Games = Long(fields["games"], "games"),
                            Wins = Long(fields["wins"], "wins"),
                            Draws = Long(fields["draws"], "draws"),
                            WinRate = Number(fields["winRate"], "winRate"),
                            Low95 = Number(fields["low95"], "low95"),
                            High95 = Number(fields["high95"], "high95")
                        });
                    }
                    pack.Streamers = pack.Streamers.OrderBy(s => s.Id, StringComparer.Ordinal).ToList();
                    result.Packs.Add(pack);
                }
                result.Packs = result.Packs.OrderBy(p => p.PackId, StringComparer.Ordinal).ToList();
                result.Validate();
                return result;
            }
            catch (JsonException error) { throw Error("JSON 格式损坏。", error); }
            catch (InvalidOperationException error) { throw Error("JSON 字段格式损坏。", error); }
        }

        public static BalanceSnapshot Load(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("平衡快照文件路径不能为空。", nameof(path));
            string json;
            try { json = File.ReadAllText(path, new UTF8Encoding(false, true)); }
            catch (DecoderFallbackException error) { throw Error("文件不是有效的 UTF-8 文本。", error); }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException
                || error is ArgumentException || error is NotSupportedException || error is System.Security.SecurityException)
            {
                throw new IOException("无法读取平衡快照文件，请检查路径、文件是否存在及读取权限。", error);
            }
            return FromJson(json);
        }

        static Dictionary<string, JsonElement> Fields(JsonElement element, string location, params string[] names)
        {
            Require(element.ValueKind == JsonValueKind.Object, location + "必须是对象。");
            var allowed = new HashSet<string>(names, StringComparer.Ordinal);
            var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                Require(allowed.Contains(property.Name), location + "含未知字段：" + property.Name + "。");
                Require(fields.TryAdd(property.Name, property.Value), location + "字段重复：" + property.Name + "。");
            }
            foreach (string name in names) Require(fields.ContainsKey(name), location + "缺少字段：" + name + "。");
            return fields;
        }

        static string Text(JsonElement value, string name)
        {
            Require(value.ValueKind == JsonValueKind.String, name + "必须是字符串。");
            return value.GetString();
        }

        static int Int(JsonElement value, string name)
        {
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number)) return number;
            throw Error(name + "必须是有效整数。");
        }

        static uint UInt(JsonElement value, string name)
        {
            if (value.ValueKind == JsonValueKind.Number && value.TryGetUInt32(out uint number)) return number;
            throw Error(name + "必须是 0～4294967295 的整数。");
        }

        static long Long(JsonElement value, string name)
        {
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number)) return number;
            throw Error(name + "必须是有效整数。");
        }

        static double Number(JsonElement value, string name)
        {
            if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number)) return number;
            throw Error(name + "必须是有效数值。");
        }

        internal static void ValidateText(string value, string name)
        {
            Require(!string.IsNullOrWhiteSpace(value), name + "不能为空。");
            Require(!value.Any(c => char.IsControl(c) || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format
                || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.LineSeparator
                || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.ParagraphSeparator),
                name + "不能含控制字符或不可见格式字符。");
        }

        internal static void Require(bool condition, string message)
        {
            if (!condition) throw Error(message);
        }

        internal static FormatException Error(string message, Exception inner = null)
        {
            return new FormatException("平衡快照无效：" + message, inner);
        }
    }

    public sealed class BalancePackSnapshot
    {
        public string PackId { get; set; }
        public string ContentHash { get; set; }
        public long Battles { get; set; }
        public List<BalanceStreamerSnapshot> Streamers { get; set; } = new List<BalanceStreamerSnapshot>();
    }

    public sealed class BalanceStreamerSnapshot
    {
        const double WilsonZ = 1.959963984540054;
        const double ValidationTolerance = 1e-12;
        public string Id { get; set; }
        public string Name { get; set; }
        public long Games { get; set; }
        public long Wins { get; set; }
        public long Draws { get; set; }
        public double WinRate { get; set; }
        public double Low95 { get; set; }
        public double High95 { get; set; }

        public static BalanceStreamerSnapshot FromStats(BalanceStreamerStats stats, string name)
        {
            if (stats == null) throw new ArgumentNullException(nameof(stats), "主播统计不能为空。");
            var result = new BalanceStreamerSnapshot
            {
                Id = stats.Id, Name = name, Games = stats.Games, Wins = stats.Wins,
                Draws = stats.Draws, WinRate = stats.WinRate
            };
            Interval(result.Wins, result.Draws, result.Games, out double low, out double high);
            result.Low95 = low;
            result.High95 = high;
            result.Validate();
            return result;
        }

        internal void Validate()
        {
            BalanceSnapshot.ValidateText(Id, "主播编号");
            BalanceSnapshot.ValidateText(Name, "主播名称");
            BalanceSnapshot.Require(Games > 0, "主播实际入队次数必须大于 0。");
            BalanceSnapshot.Require(Wins >= 0 && Draws >= 0 && Draws <= Games && Wins <= Games - Draws,
                "胜场、平局计数必须非负，且总数不能超过入队次数。");
            BalanceSnapshot.Require(Probability(WinRate) && Probability(Low95) && Probability(High95),
                "胜率和区间必须是 0～1 的有限数值。");
            BalanceSnapshot.Require(Low95 <= High95 && Low95 <= WinRate + ValidationTolerance && WinRate <= High95 + ValidationTolerance,
                "置信区间必须包含胜率且下界不高于上界。");
            double expectedRate = (Wins + Draws * 0.5) / Games;
            BalanceSnapshot.Require(Math.Abs(WinRate - expectedRate) <= ValidationTolerance, "胜率与胜场、平局及入队次数不一致。");
            Interval(Wins, Draws, Games, out double low, out double high);
            BalanceSnapshot.Require(Math.Abs(Low95 - low) <= ValidationTolerance && Math.Abs(High95 - high) <= ValidationTolerance,
                "置信区间与胜平计数重算的双侧 95% Wilson 区间不一致。");
        }

        internal BalanceStreamerSnapshot Copy()
        {
            return new BalanceStreamerSnapshot
            {
                Id = Id, Name = Name, Games = Games, Wins = Wins, Draws = Draws,
                WinRate = WinRate, Low95 = Low95, High95 = High95
            };
        }

        internal decimal ExactRate()
        {
            return (Wins + Draws / 2m) / Games;
        }

        static bool Probability(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value) && value >= 0 && value <= 1;
        }

        static void Interval(long wins, long draws, long games, out double low, out double high)
        {
            BalanceSnapshot.Require(games > 0, "区间计算需要正的入队次数。");
            double n = games, p = (wins + draws * 0.5) / n, z2 = WilsonZ * WilsonZ;
            double center = p + z2 / (2 * n);
            double radius = WilsonZ * Math.Sqrt(p * (1 - p) / n + z2 / (4 * n * n));
            double denominator = 1 + z2 / n;
            // 平局半胜和同局阵容共享结果都只支持近似诊断，不作独立二项保证。
            low = p == 0 ? 0 : Math.Max(0, (center - radius) / denominator);
            high = p == 1 ? 1 : Math.Min(1, (center + radius) / denominator);
        }
    }
}
