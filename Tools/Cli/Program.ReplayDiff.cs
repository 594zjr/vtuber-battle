using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using StreamDuel.Core;

namespace StreamDuel.Cli
{
    internal static partial class Program
    {
        private static int CompareReplays(CliOptions options)
        {
            try
            {
                var policy = PackPolicy.LoadDefault();
                var records = new[]
                {
                    ReadDiffReplay(options.ReplayDiffA, options),
                    ReadDiffReplay(options.ReplayDiffB, options)
                };
                // 两份记录先完成政策和版本预检，开局不同也不能绕过下架与未来版本门禁。
                foreach (var record in records)
                {
                    string disabled = policy.CheckTeam(record.PackId, record.Teams?.SelectMany(team => team));
                    if (disabled != null) throw new InvalidOperationException(disabled);
                    if (record.Version > BattleRecord.CurrentVersion)
                        throw new InvalidOperationException(Replay.Check(null, record, policy).Reason);
                }
                var content = Path.Combine(AppContext.BaseDirectory, "Content");
                var databases = new ContentDb[2];
                for (int index = 0; index < records.Length; index++)
                {
                    var record = records[index];
                    databases[index] = index == 1 && records[0].PackId == record.PackId ? databases[0]
                        : LoadPack(content, new CliOptions { Pack = record.PackId, PackDirectory = options.PackDirectory }, policy);
                    var compatibility = Replay.Check(databases[index], record, policy);
                    if (!compatibility.CanReplay)
                        throw new InvalidOperationException("录像" + (index + 1) + "：" + compatibility.Reason);
                }
                var result = ReplayDiff.Compare(databases[0], records[0], records[1]);
                Console.WriteLine("开播对决 · 录像分歧对比");
                foreach (string warning in result.Warnings)
                    Console.WriteLine("提示：" + CleanReplayDiffText(warning));
                if (!result.SameOpening)
                {
                    foreach (string difference in result.OpeningDifferences)
                        Console.WriteLine(CleanReplayDiffText(difference));
                    Console.WriteLine("未继续逐回合比较。");
                    return 0;
                }
                Console.WriteLine("开局相同。");
                Console.WriteLine("已记录行动回合：录像1 " + result.TurnsA + "；录像2 " + result.TurnsB + "。");
                Console.WriteLine("首个行动分歧：" + DiffTurn(result.FirstActionDifferenceTurn));
                Console.WriteLine("首个状态分歧：" + DiffTurn(result.FirstStateDifferenceTurn));
                if (result.EndDifferenceTurn.HasValue)
                {
                    string shorter = result.ShorterSide.HasValue ? "录像" + (result.ShorterSide.Value + 1) : "较短录像";
                    if (result.TurnsA != result.TurnsB)
                        Console.WriteLine("记录长度不同：" + shorter + "已记录 " + Math.Min(result.TurnsA, result.TurnsB)
                            + " 个行动回合，从第 " + result.EndDifferenceTurn.Value + " 回合缺失。");
                    else Console.WriteLine("记录长度不同：" + shorter + "在第 " + result.EndDifferenceTurn.Value
                        + " 回合（0 为开场）末尾提前结束，缺少后续事件。");
                }
                if (result.StateHashA.HasValue)
                    Console.WriteLine("录像1 状态哈希：" + result.StateHashA.Value.ToString("x16", CultureInfo.InvariantCulture));
                if (result.StateHashB.HasValue)
                    Console.WriteLine("录像2 状态哈希：" + result.StateHashB.Value.ToString("x16", CultureInfo.InvariantCulture));
                foreach (string difference in result.Differences)
                    Console.WriteLine("- " + CleanReplayDiffText(difference));
                if (!result.FirstActionDifferenceTurn.HasValue && !result.FirstStateDifferenceTurn.HasValue
                    && !result.EndDifferenceTurn.HasValue && result.Differences.Count == 0)
                    Console.WriteLine("未发现差异。");
                return 0;
            }
            catch (Exception error) when (error is ArgumentException || error is IOException
                || error is UnauthorizedAccessException || error is KeyError || error is FormatException
                || error is InvalidOperationException || error is IndexOutOfRangeException)
            {
                string message = error is IndexOutOfRangeException ? "回放 JSON 不完整。" : error.Message;
                throw new ArgumentException(CleanReplayDiffText(message), error);
            }
        }

        private static string DiffTurn(int? turn)
        {
            return turn.HasValue ? "第 " + turn.Value + " 回合" + (turn.Value == 0 ? "（开场）" : "") : "无";
        }

        private static BattleRecord ReadDiffReplay(string reference, CliOptions options)
        {
            string path = reference;
            if (!IsReplayFile(reference))
            {
                string upper = reference.ToUpperInvariant();
                if (reference.Length > 160 || !reference.All(c => c >= 'A' && c <= 'Z' || c >= 'a' && c <= 'z'
                    || c >= '0' && c <= '9' || c == '_' || c == '-')
                    || upper == "INDEX" || upper == "CON" || upper == "PRN" || upper == "AUX" || upper == "NUL"
                    || upper.Length == 4 && (upper.StartsWith("COM", StringComparison.Ordinal) || upper.StartsWith("LPT", StringComparison.Ordinal))
                        && upper[3] >= '1' && upper[3] <= '9')
                    throw new ArgumentException("录像 id 只能包含字母、数字、下划线和连字符，不能使用管理文件名或系统设备名。");
                path = Path.GetFullPath(Path.Combine(ReplayLibraryDirectory(options), reference + ".json"));
                // 普通录像库构造和 Load 会创建目录及锁文件；诊断入口只打开现有文件。
                var ancestors = new Stack<string>();
                for (string current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
                    ancestors.Push(current);
                while (ancestors.Count > 0)
                {
                    string current = ancestors.Pop();
                    FileAttributes attributes;
                    try { attributes = File.GetAttributes(current); }
                    catch (FileNotFoundException) { continue; }
                    catch (DirectoryNotFoundException) { continue; }
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("录像路径不能包含链接或重解析点。");
                    if (((attributes & FileAttributes.Directory) != 0) != (ancestors.Count > 0))
                        throw new IOException("录像路径的文件或目录类型不正确。");
                }
            }
            try { return BattleRecord.FromJson(File.ReadAllText(path, new UTF8Encoding(false, true))); }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is DecoderFallbackException)
            {
                throw new IOException("读取录像失败：" + error.Message, error);
            }
        }

        private static string CleanReplayDiffText(string value)
        {
            // Note 的 60 字上限用于用户备注；分段清理诊断文本以保留完整哈希和差异字段。
            var text = new StringBuilder();
            string source = value ?? "";
            for (int start = 0; start < source.Length;)
            {
                int end = start, count = 0;
                while (end < source.Length && count++ < 60)
                {
                    end += char.IsHighSurrogate(source[end]) && end + 1 < source.Length && char.IsLowSurrogate(source[end + 1]) ? 2 : 1;
                }
                string piece = source.Substring(start, end - start);
                var cleaned = TextInput.Clean(piece, TextInputKind.Note);
                if (!cleaned.UsedDefault)
                {
                    if (text.Length > 0 && char.IsWhiteSpace(piece[0])) text.Append(' ');
                    text.Append(cleaned.Text);
                    if (char.IsWhiteSpace(piece[piece.Length - 1])) text.Append(' ');
                }
                start = end;
            }
            string result = text.ToString();
            // 跨分段边界的词也使用相同词表处理，不能借诊断长行绕过显示过滤。
            foreach (string word in TextLint.DefaultBannedWords.OrderByDescending(word => word.Length))
                result = result.Replace(word, new string('*', word.Length));
            return result;
        }
    }
}
