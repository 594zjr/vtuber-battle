using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using StreamDuel.Core;

namespace StreamDuel.PackCheck
{
    public sealed class ArtCheckIssue
    {
        public string Code { get; }
        public string Level { get; }
        public string Path { get; }
        public string Id { get; }
        public string Message { get; }
        public int? Width { get; }
        public int? Height { get; }
        public long? Bytes { get; }

        public ArtCheckIssue(string code, string level, string path, string id, string message,
            int? width = null, int? height = null, long? bytes = null)
        {
            Code = code ?? string.Empty;
            Level = level ?? string.Empty;
            Path = path ?? string.Empty;
            Id = id ?? string.Empty;
            Message = message ?? string.Empty;
            Width = width;
            Height = height;
            Bytes = bytes;
        }
    }

    public sealed class ArtCheckReport
    {
        public string Root { get; }
        public string PackId { get { return PackIds.Count == 1 ? PackIds[0] : string.Empty; } }
        public IReadOnlyList<string> PackIds { get; }
        public IReadOnlyList<ArtCheckIssue> Issues { get; }
        public int ErrorCount { get; }
        public int WarningCount { get; }
        public bool Passed { get { return ErrorCount == 0; } }
        public string Reminder { get { return ArtCheck.CopyrightReminder; } }

        public ArtCheckReport(string root, IReadOnlyList<string> packIds, IReadOnlyList<ArtCheckIssue> issues)
        {
            if (packIds == null) throw new ArgumentNullException(nameof(packIds));
            if (issues == null) throw new ArgumentNullException(nameof(issues));
            Root = root ?? string.Empty;
            PackIds = Array.AsReadOnly(packIds.Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToArray());
            var ordered = issues.OrderBy(issue => issue.Path, StringComparer.Ordinal)
                .ThenBy(issue => issue.Code, StringComparer.Ordinal)
                .ThenBy(issue => issue.Id, StringComparer.Ordinal)
                .ThenBy(issue => issue.Level, StringComparer.Ordinal)
                .ThenBy(issue => issue.Message, StringComparer.Ordinal).ToArray();
            Issues = Array.AsReadOnly(ordered);
            ErrorCount = ordered.Count(issue => issue.Level == "error");
            WarningCount = ordered.Count(issue => issue.Level == "warn");
        }
    }

    public static partial class ArtCheck
    {
        public const int MinDimension = 128;
        public const int MaxDimension = 4096;
        public const long MaxFileBytes = 8 * 1024 * 1024;
        public const string CopyrightReminder = "请只放自己画的或已取得授权的图片，不放官方立绘和截图。";
        static readonly StringComparer FileComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

        public static ArtCheckReport Check(ContentDb db, string directory)
        {
            if (db == null) throw new ArgumentNullException(nameof(db));
            return Check(new[] { db }, directory);
        }

        public static ArtCheckReport Check(IReadOnlyList<ContentDb> dbs, string directory)
        {
            if (dbs == null) throw new ArgumentNullException(nameof(dbs));
            if (dbs.Any(db => db == null)) throw new ArgumentException("待检查的内容包不能为空。", nameof(dbs));
            var issues = new List<ArtCheckIssue>();
            var packIds = dbs.Select(db => db.PackId).Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToArray();
            var references = CollectReferences(dbs, issues);
            issues.Add(new ArtCheckIssue("procedural", "info", "", "",
                "程序特效和状态图标只验证清单编号；外部资源检查等待后续路径规范，本次不推定特效或图标图片路径。"));
            string root = directory ?? string.Empty;
            try
            {
                if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("资源根目录不能为空。");
                if (directory.StartsWith(@"\\?\", StringComparison.Ordinal) || directory.StartsWith(@"\\.\", StringComparison.Ordinal))
                    throw new ArgumentException("资源根目录不能使用设备路径。");
                root = Path.GetFullPath(directory);
                VerifyAncestors(root);
                if ((File.GetAttributes(root) & FileAttributes.Directory) == 0)
                    throw new ArgumentException("资源根路径必须是文件夹。");
            }
            catch (Exception error) when (IsPathError(error))
            {
                issues.Add(new ArtCheckIssue("path", "error", "", "", "无法检查资源根目录：" + PathErrorText(error)));
                return new ArtCheckReport(root, packIds, issues);
            }

            var blocked = new List<string>();
            var files = Scan(root, references, blocked, issues);
            var byKey = new Dictionary<string, List<ArtFile>>(FileComparer);
            foreach (var file in files)
            {
                if (string.Equals(Path.GetExtension(file.Relative), ".meta", StringComparison.OrdinalIgnoreCase)) continue;
                string key = ResourceKey(file.Relative);
                if (!byKey.TryGetValue(key, out var matches)) byKey.Add(key, matches = new List<ArtFile>());
                matches.Add(file);
            }
            var used = new HashSet<string>(FileComparer);
            foreach (var reference in references.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                string expected = reference.Key + ".png";
                string id = JoinIds(reference.Value);
                if (blocked.Any(path => IsWithin(expected, path))) continue;
                if (!byKey.TryGetValue(reference.Key, out var matches))
                {
                    issues.Add(new ArtCheckIssue("missing", "warn", expected, id, "缺少立绘，将使用占位。"));
                    continue;
                }
                bool hasPng = false;
                foreach (var file in matches)
                {
                    used.Add(file.Relative);
                    if (!string.Equals(Path.GetExtension(file.Relative), ".png", StringComparison.OrdinalIgnoreCase))
                    {
                        issues.Add(new ArtCheckIssue("format", "error", file.Relative, id, "立绘格式必须是 PNG，请使用 .png 扩展名。"));
                        continue;
                    }
                    if (hasPng)
                        issues.Add(new ArtCheckIssue("format", "error", file.Relative, id, "同一立绘资源键对应多份 PNG，无法唯一定位。"));
                    hasPng = true;
                    CheckPng(file, id, issues);
                }
                if (!hasPng)
                    issues.Add(new ArtCheckIssue("missing", "warn", expected, id, "缺少 PNG 立绘，将使用占位。"));
            }
            foreach (var file in files)
                if (!used.Contains(file.Relative) && !IgnoredFile(file.Relative))
                    issues.Add(new ArtCheckIssue("unused", "warn", file.Relative, "", "文件未被所选内容包的立绘清单引用。"));
            return new ArtCheckReport(root, packIds, issues);
        }

        static Dictionary<string, SortedSet<string>> CollectReferences(IReadOnlyList<ContentDb> dbs, List<ArtCheckIssue> issues)
        {
            var references = new Dictionary<string, SortedSet<string>>(FileComparer);
            foreach (var db in dbs.OrderBy(db => db.PackId, StringComparer.Ordinal))
            {
                foreach (var streamer in db.Streamers.OrderBy(streamer => streamer.Id, StringComparer.Ordinal))
                {
                    string owner = db.PackId + "/" + streamer.Id;
                    try
                    {
                        string key = db.Art.PortraitPath(streamer.Id);
                        VerifyResourceKey(key);
                        if (!references.TryGetValue(key, out var ids))
                            references.Add(key, ids = new SortedSet<string>(StringComparer.Ordinal));
                        else
                        {
                            string existing = references.Keys.First(path => FileComparer.Equals(path, key));
                            if (!string.Equals(existing, key, StringComparison.Ordinal))
                                issues.Add(new ArtCheckIssue("manifest", "error", key + ".png", owner,
                                    "立绘资源键与另一引用仅大小写不同，在本平台上产生路径别名；请统一清单拼写。"));
                        }
                        ids.Add(owner);
                    }
                    catch (Exception error) when (error is KeyError || IsPathError(error))
                    {
                        issues.Add(new ArtCheckIssue("manifest", "error", "", owner, "立绘清单路径无效：" + error.Message));
                    }
                }
                foreach (string skill in db.Skills.Keys.Concat(db.Struggle == null ? Array.Empty<string>() : new[] { db.Struggle.Id })
                    .Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal))
                {
                    try
                    {
                        string fx = db.Art.SkillFx(skill);
                        if (Array.IndexOf(ArtManifest.FxIds, fx) < 0) throw new ArgumentException("不支持的程序特效编号。");
                    }
                    catch (Exception error) when (error is KeyError || error is ArgumentException)
                    {
                        issues.Add(new ArtCheckIssue("manifest", "error", "", db.PackId + "/" + skill, "特效清单无效：" + error.Message));
                    }
                }
                foreach (string effect in db.Statuses.Keys.Concat(db.Fields.Keys).Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal))
                {
                    try
                    {
                        string icon = db.Art.StatusIcon(effect);
                        if (Array.IndexOf(ArtManifest.IconIds, icon) < 0) throw new ArgumentException("不支持的程序图标编号。");
                    }
                    catch (Exception error) when (error is KeyError || error is ArgumentException)
                    {
                        issues.Add(new ArtCheckIssue("manifest", "error", "", db.PackId + "/" + effect, "图标清单无效：" + error.Message));
                    }
                }
            }
            return references;
        }

        static string JoinIds(IEnumerable<string> ids) { return string.Join(",", ids.OrderBy(id => id, StringComparer.Ordinal)); }
    }
}
