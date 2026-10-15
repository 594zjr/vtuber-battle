using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace StreamDuel.Core
{
    public sealed class PackLoadResult
    {
        public string FileName, Error;
        public ContentDb Db;
    }

    public static class PackLoader
    {
        private static StreamDuel.Core.Loc Loc { get { return StreamDuel.Core.Loc.Current; } }

        public static List<PackLoadResult> LoadDirectory(string rulesJson, string dir)
        {
            var results = new List<PackLoadResult>();
            if (!Directory.Exists(dir)) return results;

            string[] files;
            try
            {
                files = Directory.GetFiles(dir, "*", SearchOption.TopDirectoryOnly)
                    .Where(path => string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal).ToArray();
            }
            catch (Exception ex)
            {
                results.Add(new PackLoadResult { FileName = dir, Error = Loc.T("pack.err.directory", Reason(ex)) });
                return results;
            }

            var loadedIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var file in files)
            {
                var result = new PackLoadResult { FileName = Path.GetFileName(file) };
                try
                {
                    var db = ContentDb.FromJson(rulesJson, File.ReadAllText(file));
                    if (string.IsNullOrWhiteSpace(db.PackId)) throw new KeyError(Loc.T("pack.err.id.empty"));
                    if (!loadedIds.Add(db.PackId)) throw new KeyError(Loc.T("pack.err.id.duplicate", db.PackId));
                    result.Db = db;
                }
                catch (Exception ex)
                {
                    // 单个包损坏不能挡住同目录中其他可用内容包。
                    result.Error = Loc.T("pack.err.load", Reason(ex));
                }
                results.Add(result);
            }
            return results;
        }

        static string Reason(Exception ex)
        {
            if (ex is KeyError) return ex.Message;
            if (ex is UnauthorizedAccessException) return Loc.T("pack.err.permission");
            if (ex is IOException) return Loc.T("pack.err.io");
            if (ex is FormatException || ex is IndexOutOfRangeException || ex is ArgumentOutOfRangeException)
                return Loc.T("pack.err.json.format");
            if (ex is InvalidCastException || ex is KeyNotFoundException || ex is NullReferenceException || ex is ArgumentException)
                return Loc.T("pack.err.json.shape");
            if (ex is OverflowException) return Loc.T("pack.err.json.overflow");
            return Loc.T("pack.err.parse");
        }
    }
}
