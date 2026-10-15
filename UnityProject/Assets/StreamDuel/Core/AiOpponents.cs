using System;
using System.Collections.Generic;
using System.IO;

namespace StreamDuel.Core
{
    public sealed class AiOpponent
    {
        public string Id, Name, Blurb, PackId;
        public List<string> Team = new List<string>();
        public AiLevel Level;
        public string Style;
    }

    public static class AiOpponents
    {
        static readonly Lazy<string> embeddedJson = new Lazy<string>(ReadEmbeddedJson);
        static volatile string suppliedJson;

        /// <summary>读取当前包的固定陪练名单，每次返回独立对象；未收录的包返回空列表。</summary>
        public static List<AiOpponent> For(ContentDb db)
        {
            if (db == null) throw new ArgumentNullException(nameof(db));
            return FromJson(db, suppliedJson ?? embeddedJson.Value);
        }

        /// <summary>由 Unity 等宿主注入同一份资源文本；传 null 恢复程序集内嵌资源。</summary>
        public static void SetJson(string json)
        {
            // 先验证再替换，损坏的资源不能覆盖上一份可用名单。
            if (json != null) ParseCatalog(json);
            suppliedJson = json;
        }

        /// <summary>校验全部名单结构、全局 id，再用指定内容包校验其阵容，不改全局来源。</summary>
        public static List<AiOpponent> FromJson(ContentDb db, string json)
        {
            if (db == null) throw new ArgumentNullException(nameof(db));
            var catalog = ParseCatalog(json);
            if (db.PackId == null || !catalog.TryGetValue(db.PackId, out var opponents))
                return new List<AiOpponent>();
            foreach (var opponent in opponents)
            {
                var errors = TeamCheck.Validate(db, null, opponent.Team);
                if (errors.Count > 0)
                    throw new FormatException("电脑对手「" + opponent.Name + "」阵容无效：" + string.Join("；", errors));
            }
            return opponents;
        }

        static string ReadEmbeddedJson()
        {
            // .NET 与 Unity 都读取 Resources 中同一文件，名单不复制进 C# 源码。
            using (var stream = typeof(AiOpponents).Assembly.GetManifestResourceStream("StreamDuel.Core.AiOpponents.json"))
            {
                if (stream == null)
                    throw new InvalidOperationException("尚未载入电脑对手名单，请由宿主用 AiOpponents.SetJson 载入 AiOpponents.json。");
                using (var reader = new StreamReader(stream)) return reader.ReadToEnd();
            }
        }

        static Dictionary<string, List<AiOpponent>> ParseCatalog(string json)
        {
            if (!(MiniJson.Parse(json) is Dictionary<string, object> root))
                throw new FormatException("电脑对手名单必须是按内容包 id 分组的 JSON 对象。");
            var catalog = new Dictionary<string, List<AiOpponent>>(StringComparer.Ordinal);
            var allIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var group in root)
            {
                if (string.IsNullOrWhiteSpace(group.Key))
                    throw new FormatException("电脑对手名单的内容包 id 不能为空。");
                if (!(group.Value is List<object> entries) || entries.Count < 6)
                    throw new FormatException("内容包「" + group.Key + "」必须提供至少 6 位电脑对手的数组。");
                var opponents = new List<AiOpponent>();
                var levels = new HashSet<AiLevel>();
                foreach (var entry in entries)
                {
                    if (!(entry is Dictionary<string, object> item))
                        throw new FormatException("电脑对手条目必须是 JSON 对象。");
                    var opponent = new AiOpponent
                    {
                        Id = RequiredText(item, "id"),
                        Name = RequiredText(item, "name"),
                        Blurb = RequiredText(item, "blurb"),
                        PackId = group.Key,
                        Level = ReadLevel(RequiredText(item, "level"))
                    };
                    if (!allIds.Add(opponent.Id))
                        throw new FormatException("电脑对手 id 重复：" + opponent.Id);
                    if (item.TryGetValue("style", out var style) && style != null)
                        throw new FormatException("当前版本的电脑对手 style 必须为 null，出招风格尚未接入。");
                    if (!item.TryGetValue("team", out var teamValue) || !(teamValue is List<object> team) || team.Count != 6)
                        throw new FormatException("电脑对手「" + opponent.Name + "」必须有恰好 6 位主播。");
                    var members = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var value in team)
                    {
                        if (!(value is string id) || string.IsNullOrWhiteSpace(id))
                            throw new FormatException("电脑对手「" + opponent.Name + "」的主播 id 必须是非空字符串。");
                        if (!members.Add(id))
                            throw new FormatException("电脑对手「" + opponent.Name + "」的阵容不能重复主播：" + id);
                        opponent.Team.Add(id);
                    }
                    levels.Add(opponent.Level);
                    opponents.Add(opponent);
                }
                if (!levels.Contains(AiLevel.Easy) || !levels.Contains(AiLevel.Normal)
                    || !levels.Contains(AiLevel.Hard) || !levels.Contains(AiLevel.Expert))
                    throw new FormatException("内容包「" + group.Key + "」的电脑对手必须覆盖简单、普通、困难、专家四种难度。");
                catalog.Add(group.Key, opponents);
            }
            return catalog;
        }

        static string RequiredText(Dictionary<string, object> item, string key)
        {
            if (!item.TryGetValue(key, out var value) || !(value is string text) || string.IsNullOrWhiteSpace(text))
                throw new FormatException("电脑对手的 " + key + " 必须是非空字符串。");
            return text;
        }

        static AiLevel ReadLevel(string value)
        {
            switch (value)
            {
                case "easy": return AiLevel.Easy;
                case "normal": return AiLevel.Normal;
                case "hard": return AiLevel.Hard;
                case "expert": return AiLevel.Expert;
                default: throw new FormatException("电脑对手 level 只能是 easy、normal、hard 或 expert。");
            }
        }
    }
}
