using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

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

        /// <summary>查找固定对手；未知编号返回 null，已下架队伍给出政策原因。</summary>
        public static AiOpponent Find(ContentDb db, string id)
        {
            if (db == null) throw new ArgumentNullException(nameof(db));
            if (id == null) throw new ArgumentNullException(nameof(id));
            var catalog = ParseCatalog(suppliedJson ?? embeddedJson.Value);
            string reason = db.Policy.PackReason(db.PackId);
            if (reason != null) throw new PackDisabledException(reason);
            if (db.PackId == null || !catalog.TryGetValue(db.PackId, out var opponents)) return null;
            var opponent = opponents.FirstOrDefault(candidate => candidate.Id == id);
            if (opponent == null) return null;
            reason = db.Policy.CheckTeam(db.PackId, opponent.Team);
            if (reason != null) throw new PackDisabledException(reason);
            var errors = TeamCheck.Validate(db, null, opponent.Team);
            if (errors.Count > 0)
                throw new FormatException("电脑对手「" + opponent.Name + "」阵容无效：" + string.Join("；", errors));
            return opponent;
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
            string packReason = db.Policy.PackReason(db.PackId);
            if (packReason != null) throw new PackDisabledException(packReason);
            if (db.PackId == null || !catalog.TryGetValue(db.PackId, out var opponents))
                return new List<AiOpponent>();
            var available = new List<AiOpponent>();
            foreach (var opponent in opponents)
            {
                // 政策过滤不能掩盖其它未知编号，也不能让一支下架阵容隐藏整包合法陪练。
                foreach (string id in opponent.Team)
                    if (!db.Policy.IsStreamerDisabled(id) && !db.Streamers.Any(streamer => streamer.Id == id))
                        throw new FormatException("电脑对手「" + opponent.Name + "」阵容包含不存在的主播：" + id);
                if (db.Policy.CheckTeam(db.PackId, opponent.Team) != null) continue;
                var errors = TeamCheck.Validate(db, null, opponent.Team);
                if (errors.Count > 0)
                    throw new FormatException("电脑对手「" + opponent.Name + "」阵容无效：" + string.Join("；", errors));
                available.Add(opponent);
            }
            return available;
        }

        /// <summary>在这一局独立的主播定义上应用陪练风格；null 保留现有风格，不改共享内容或随机流。</summary>
        public static void ApplyStyle(Battle battle, int side, AiOpponent opponent)
        {
            if (battle == null) throw new ArgumentNullException(nameof(battle));
            if (opponent == null) throw new ArgumentNullException(nameof(opponent));
            if (side < 0 || side > 1) throw new ArgumentOutOfRangeException(nameof(side));
            if (opponent.PackId != battle.Db.PackId || opponent.PackId != battle.Record.PackId)
                throw new FormatException("电脑对手的内容包与当前对战不一致。");
            if (!Enum.IsDefined(typeof(AiLevel), opponent.Level)) throw new FormatException("电脑对手难度无效。");
            ValidateStyle(opponent.Style);
            string reason = battle.Db.Policy.CheckTeam(opponent.PackId, opponent.Team);
            if (reason != null) throw new PackDisabledException(reason);
            var errors = TeamCheck.Validate(battle.Db, null, opponent.Team);
            if (errors.Count > 0) throw new FormatException("电脑对手阵容无效：" + string.Join("；", errors));
            if (!battle.Record.Teams[side].SequenceEqual(opponent.Team)
                || battle.Sides[side].Team.Any(fighter => !opponent.Team.Contains(fighter.Def.Id)))
                throw new FormatException("电脑对手的固定阵容与当前对战方不一致。");
            if (opponent.Style == null) return;

            // 镜像队伍也可能共享同一份定义；先准备完整副本，避免污染对侧或留下半更新。
            var fighters = battle.Sides[side].Team;
            var definitions = fighters.Select(fighter => CopyDefinition(fighter.Def, opponent.Style)).ToArray();
            for (int index = 0; index < fighters.Count; index++) fighters[index].Def = definitions[index];
        }

        static StreamerDef CopyDefinition(StreamerDef source, string style)
        {
            return new StreamerDef
            {
                Id = source.Id, Name = source.Name, Type = source.Type, Type2 = source.Type2,
                Persona = source.Persona, Fifth = source.Fifth, Blurb = source.Blurb, Org = source.Org,
                AiStyle = style, Stats = new Dictionary<string, int>(source.Stats), Skills = new List<string>(source.Skills)
            };
        }

        static void ValidateStyle(string style)
        {
            if (style != null && AiStyles.Get(style) == null)
                throw new FormatException("电脑对手 style 只能是 null、aggressive、defensive 或 tricky。");
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
            RejectDuplicateKeys(json);
            var catalog = new Dictionary<string, List<AiOpponent>>(StringComparer.Ordinal);
            var allIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var group in root)
            {
                ValidateId(group.Key);
                if (!(group.Value is List<object> entries) || entries.Count < 6)
                    throw new FormatException("内容包「" + group.Key + "」必须提供至少 6 位电脑对手的数组。");
                var opponents = new List<AiOpponent>();
                var levels = new HashSet<AiLevel>();
                foreach (var entry in entries)
                {
                    if (!(entry is Dictionary<string, object> item))
                        throw new FormatException("电脑对手条目必须是 JSON 对象。");
                    foreach (string key in item.Keys)
                        if (key != "id" && key != "name" && key != "blurb" && key != "team" && key != "level" && key != "style")
                            throw new FormatException("电脑对手包含未知字段：" + key);
                    var opponent = new AiOpponent
                    {
                        Id = RequiredText(item, "id"),
                        Name = RequiredText(item, "name"),
                        Blurb = RequiredText(item, "blurb"),
                        PackId = group.Key,
                        Level = ReadLevel(RequiredText(item, "level"))
                    };
                    ValidateId(opponent.Id);
                    if (!allIds.Add(opponent.Id))
                        throw new FormatException("电脑对手 id 重复：" + opponent.Id);
                    if (item.TryGetValue("style", out var style) && style != null)
                    {
                        if (!(style is string text)) throw new FormatException("电脑对手 style 必须是字符串或 null。");
                        ValidateStyle(text);
                        opponent.Style = text;
                    }
                    if (!item.TryGetValue("team", out var teamValue) || !(teamValue is List<object> team) || team.Count != 6)
                        throw new FormatException("电脑对手「" + opponent.Name + "」必须有恰好 6 位主播。");
                    var members = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var value in team)
                    {
                        if (!(value is string id) || string.IsNullOrWhiteSpace(id))
                            throw new FormatException("电脑对手「" + opponent.Name + "」的主播 id 必须是非空字符串。");
                        ValidateId(id);
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

        static void ValidateId(string id)
        {
            if (string.IsNullOrWhiteSpace(id) || id.Trim() != id || id.Any(char.IsControl))
                throw new FormatException("电脑对手名单的编号不能为空、带首尾空白或控制字符。");
            for (int index = 0; index < id.Length; index++)
            {
                if (char.IsHighSurrogate(id[index]))
                {
                    if (++index == id.Length || !char.IsLowSurrogate(id[index]))
                        throw new FormatException("电脑对手名单的编号必须是有效 Unicode。");
                }
                else if (char.IsLowSurrogate(id[index])) throw new FormatException("电脑对手名单的编号必须是有效 Unicode。");
            }
        }

        static void RejectDuplicateKeys(string json)
        {
            // 不改变旧内容解析器的兼容行为，名单独立拒绝重复键，防止后一条覆盖固定阵容。
            var objects = new Stack<HashSet<string>>();
            for (int index = 0; index < json.Length; index++)
            {
                if (json[index] == '{') objects.Push(new HashSet<string>(StringComparer.Ordinal));
                else if (json[index] == '}') objects.Pop();
                else if (json[index] == '"')
                {
                    int start = index++;
                    while (index < json.Length && json[index] != '"')
                    {
                        if (json[index] == '\\') index++;
                        index++;
                    }
                    int next = index + 1;
                    while (next < json.Length && char.IsWhiteSpace(json[next])) next++;
                    if (next < json.Length && json[next] == ':')
                    {
                        string key = (string)MiniJson.Parse(json.Substring(start, index - start + 1));
                        if (!objects.Peek().Add(key)) throw new FormatException("电脑对手名单的字段重复：" + key);
                    }
                }
            }
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
