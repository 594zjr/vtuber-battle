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
                results.Add(new PackLoadResult { FileName = dir, Error = "无法读取内容包目录：" + Reason(ex) });
                return results;
            }

            var loadedIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var file in files)
            {
                var result = new PackLoadResult { FileName = Path.GetFileName(file) };
                try
                {
                    var db = ContentDb.FromJson(rulesJson, File.ReadAllText(file));
                    if (string.IsNullOrWhiteSpace(db.PackId)) throw new KeyError("内容包缺少非空 id");
                    if (!loadedIds.Add(db.PackId)) throw new KeyError("内容包 id 重复：" + db.PackId);
                    result.Db = db;
                }
                catch (Exception ex)
                {
                    // 单个包损坏不能挡住同目录中其他可用内容包。
                    result.Error = "内容包加载失败：" + Reason(ex);
                }
                results.Add(result);
            }
            return results;
        }

        static string Reason(Exception ex)
        {
            if (ex is KeyError) return ex.Message;
            if (ex is UnauthorizedAccessException) return "没有读取权限";
            if (ex is IOException) return "文件或目录无法读取，可能已被移走或正被占用";
            if (ex is FormatException || ex is IndexOutOfRangeException || ex is ArgumentOutOfRangeException)
                return "JSON 格式错误或内容不完整";
            if (ex is InvalidCastException || ex is KeyNotFoundException || ex is NullReferenceException || ex is ArgumentException)
                return "JSON 字段缺失或类型错误";
            if (ex is OverflowException) return "JSON 数值超出支持范围";
            return "无法解析或校验内容包";
        }
    }
}
