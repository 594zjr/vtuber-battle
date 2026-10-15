using System;
using System.Collections.Generic;

namespace StreamDuel.Core
{
    public sealed class ArtManifest
    {
        public static readonly string[] FxIds = { "slash", "burst", "wave", "sparkle", "beam", "ring", "shield", "heal" };
        public static readonly string[] IconIds = { "lock", "down", "up", "shield", "mark", "clock", "heart", "star" };
        static readonly HashSet<string> allowedFx = new HashSet<string>(FxIds, StringComparer.Ordinal);
        static readonly HashSet<string> allowedIcons = new HashSet<string>(IconIds, StringComparer.Ordinal);

        sealed class StreamerArt
        {
            internal string Accent, Portrait;
        }

        readonly ContentDb db;
        readonly Dictionary<string, StreamerArt> streamers = new Dictionary<string, StreamerArt>(StringComparer.Ordinal);
        readonly Dictionary<string, string> skillFx = new Dictionary<string, string>(StringComparer.Ordinal);
        readonly Dictionary<string, string> statusIcons = new Dictionary<string, string>(StringComparer.Ordinal);
        string portraitDir;

        internal ArtManifest(ContentDb db)
        {
            this.db = db;
        }

        public string PortraitPath(string streamerId)
        {
            var streamer = db.Streamer(streamerId);
            streamers.TryGetValue(streamerId, out var entry);
            string directory = portraitDir ?? DefaultPortraitDirectory();
            string portrait = entry?.Portrait ?? streamer.Id;
            ValidatePath(directory, "portraitDir");
            ValidatePath(portrait, "streamers." + streamerId + ".portrait", true);
            return directory + "/" + portrait;
        }

        public string Accent(string streamerId)
        {
            var streamer = db.Streamer(streamerId);
            streamers.TryGetValue(streamerId, out var entry);
            string accent = entry?.Accent ?? db.Types[streamer.Type].Color;
            ValidateAccent(accent, streamerId);
            return accent;
        }

        public string SkillFx(string skillId)
        {
            var skill = Skill(skillId);
            if (skillFx.TryGetValue(skillId, out var value)) return value;
            if (skill.Cat == "interact") return "slash";
            if (skill.Cat == "talent") return "beam";
            // 回复与保护并存时先展示回复；攻击技能仍按攻击分类展示。
            foreach (var effect in skill.Effects)
            {
                if (effect.Code == "HEAL" || effect.Code == "DRAIN") return "heal";
                if (effect.Code == "FIELD" && effect.Field != null
                    && db.Fields.TryGetValue(effect.Field, out var field) && field.Heal > 0) return "heal";
            }
            foreach (var effect in skill.Effects)
            {
                if (effect.Code == "FIELD" && effect.Field != null
                    && db.Fields.TryGetValue(effect.Field, out var field) && field.Kind == "protect") return "shield";
            }
            return "sparkle";
        }

        public string StatusIcon(string id)
        {
            if (id == null) throw Error("statusIcons", "状态或直播间效果 id 不能为空");
            bool isStatus = db.Statuses.TryGetValue(id, out var status);
            bool isField = db.Fields.TryGetValue(id, out var field);
            if (!isStatus && !isField) throw Error("statusIcons." + id, "状态或直播间效果不存在：" + id);
            if (statusIcons.TryGetValue(id, out var value)) return value;
            // 两类定义各有独立的 id 空间；同名时固定优先显示状态。
            if (isStatus) return status.IsControl ? "lock" : "down";
            if (field.Kind == "protect") return "shield";
            return field.Kind == "mark" ? "mark" : "clock";
        }

        SkillDef Skill(string id)
        {
            if (id != null && db.Skills.TryGetValue(id, out var skill)) return skill;
            if (id != null && db.Struggle != null && db.Struggle.Id == id) return db.Struggle;
            throw Error("skillFx." + id, "技能不存在：" + (id ?? "null"));
        }

        string DefaultPortraitDirectory()
        {
            // 保留 PackLoader 已有的空包 id 错误及本地化，避免被资源路径错误遮盖。
            if (string.IsNullOrWhiteSpace(db.PackId)) throw new KeyError(Loc.Current.T("pack.err.id.empty"));
            return "Portraits/" + db.PackId;
        }

        internal static ArtManifest Read(ContentDb db, Dictionary<string, object> pack)
        {
            var art = new ArtManifest(db);
            if (!pack.TryGetValue("art", out var raw)) return art;
            var root = art.Object(raw, "art");
            if (root.TryGetValue("portraitDir", out raw)) art.portraitDir = art.Text(raw, "portraitDir");
            if (root.TryGetValue("streamers", out raw))
            {
                foreach (var pair in art.Object(raw, "streamers"))
                {
                    string location = "streamers." + pair.Key;
                    var definition = art.Object(pair.Value, location);
                    var entry = new StreamerArt();
                    if (definition.TryGetValue("accent", out raw)) entry.Accent = art.Text(raw, location + ".accent");
                    if (definition.TryGetValue("portrait", out raw)) entry.Portrait = art.Text(raw, location + ".portrait");
                    art.streamers.Add(pair.Key, entry);
                }
            }
            art.ReadMap(root, "skillFx", art.skillFx);
            art.ReadMap(root, "statusIcons", art.statusIcons);
            return art;
        }

        internal void Validate()
        {
            if (portraitDir != null) ValidatePath(portraitDir, "portraitDir");
            foreach (var pair in streamers)
            {
                if (!db.Streamers.Exists(s => s.Id == pair.Key))
                    throw Error("streamers." + pair.Key, "主播不存在：" + pair.Key);
                if (pair.Value.Accent != null) ValidateAccent(pair.Value.Accent, pair.Key);
                if (pair.Value.Portrait != null) ValidatePath(pair.Value.Portrait, "streamers." + pair.Key + ".portrait", true);
            }
            foreach (var pair in skillFx)
            {
                Skill(pair.Key);
                if (!allowedFx.Contains(pair.Value)) throw Error("skillFx." + pair.Key, "不支持的特效：" + pair.Value);
            }
            foreach (var pair in statusIcons)
            {
                if (!db.Statuses.ContainsKey(pair.Key) && !db.Fields.ContainsKey(pair.Key))
                    throw Error("statusIcons." + pair.Key, "状态或直播间效果不存在：" + pair.Key);
                if (!allowedIcons.Contains(pair.Value)) throw Error("statusIcons." + pair.Key, "不支持的图标：" + pair.Value);
            }
            // 默认目录和文件名也来自内容包，不能只检查显式覆盖。
            foreach (var streamer in db.Streamers)
            {
                PortraitPath(streamer.Id);
                Accent(streamer.Id);
            }
        }

        void ReadMap(Dictionary<string, object> root, string key, Dictionary<string, string> destination)
        {
            if (!root.TryGetValue(key, out var raw)) return;
            foreach (var pair in Object(raw, key)) destination.Add(pair.Key, Text(pair.Value, key + "." + pair.Key));
        }

        Dictionary<string, object> Object(object raw, string location)
        {
            if (raw is Dictionary<string, object> result) return result;
            throw Error(location, "必须是对象");
        }

        string Text(object raw, string location)
        {
            if (raw is string result) return result;
            throw Error(location, "必须是字符串");
        }

        void ValidateAccent(string accent, string streamerId)
        {
            bool valid = accent != null && accent.Length == 7 && accent[0] == '#';
            if (valid)
                for (int i = 1; i < accent.Length; i++)
                {
                    char c = accent[i];
                    if (!(c >= '0' && c <= '9') && !(c >= 'a' && c <= 'f') && !(c >= 'A' && c <= 'F')) valid = false;
                }
            if (!valid) throw Error("streamers." + streamerId + ".accent", "颜色必须是 #RRGGBB：" + accent);
        }

        void ValidatePath(string path, string location, bool portrait = false)
        {
            if (string.IsNullOrWhiteSpace(path) || path.IndexOf("..", StringComparison.Ordinal) >= 0
                || path.IndexOf("http", StringComparison.OrdinalIgnoreCase) >= 0)
                throw Error(location, "资源路径必须是相对路径，不得为空或含 ..、http：" + path);
            foreach (char c in path)
            {
                if (char.IsControl(c) || ":\\<>\"|?*".IndexOf(c) >= 0)
                    throw Error(location, "资源路径含非法字符：" + path);
            }
            string[] segments = path.Split('/');
            foreach (string segment in segments)
            {
                if (string.IsNullOrWhiteSpace(segment) || segment == "." || segment != segment.Trim())
                    throw Error(location, "资源路径不得为绝对路径或包含空段、点段、首尾空格：" + path);
            }
            if (portrait && segments[segments.Length - 1].IndexOf('.') >= 0)
                throw Error(location, "立绘资源文件名必须不带扩展名和点号：" + path);
        }

        KeyError Error(string location, string message)
        {
            return new KeyError("内容包 " + db.PackId + " 的 art." + location + "：" + message);
        }
    }
}
