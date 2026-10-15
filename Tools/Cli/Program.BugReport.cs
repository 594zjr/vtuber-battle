using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using StreamDuel.Core;

namespace StreamDuel.Cli
{
    internal static partial class Program
    {
        private static bool IsBugCommand(string command, out string note)
        {
            note = null;
            if (!command.Equals("/bug", StringComparison.OrdinalIgnoreCase)
                && !(command.Length > 4 && command.StartsWith("/bug", StringComparison.OrdinalIgnoreCase)
                    && char.IsWhiteSpace(command[4]))) return false;
            note = command.Length > 4 ? command.Substring(4).Trim() : "";
            return true;
        }

        private static bool PrintBugReport(Battle battle, string note)
        {
            return PrintBugReport(() => BugReport.Create(battle, note));
        }

        private static bool PrintBugReport(Func<string> create)
        {
            try
            {
                Console.WriteLine("问题报告码：" + create());
                return true;
            }
            catch (Exception ex) when (ex is FormatException || ex is ArgumentException
                || ex is InvalidOperationException || ex is KeyError || ex is OverflowException)
            {
                // 导出失败不能中断正在选择的对局，终局时也不能漏存已有结果。
                Console.Error.WriteLine("错误：问题报告导出失败：" + BugDisplay(ex.Message));
                return false;
            }
        }

        private static int ReproduceBugReport(CliOptions options)
        {
            try { return ReproduceBugReportCore(options); }
            catch (Exception ex) when (ex is ArgumentException || ex is IOException
                || ex is UnauthorizedAccessException || ex is KeyError || ex is OverflowException
                || ex is FormatException || ex is InvalidOperationException)
            {
                Console.Error.WriteLine("错误：" + BugDisplay(ex.Message));
                return 1;
            }
        }

        private static int ReproduceBugReportCore(CliOptions options)
        {
            var report = BugReport.Parse(options.ReproduceCode);
            string content = Path.Combine(AppContext.BaseDirectory, "Content");
            var policy = PackPolicy.Load(Path.Combine(content, "PackPolicy.json"));
            string disabled = policy.CheckTeam(report.PackId,
                report.Record.Teams?.Where(team => team != null).SelectMany(team => team));
            if (disabled != null) throw new InvalidOperationException(disabled);
            var db = LoadPack(content, new CliOptions
            {
                Pack = options.PackSpecified ? options.Pack : report.PackId,
                PackDirectory = options.PackDirectory
            }, policy);
            var result = BugReport.Reproduce(db, report);
            Console.WriteLine("开播对决 · 问题报告复现");
            Console.WriteLine("报告内容包：" + BugDisplay(report.PackId) + "；当前内容包：" + BugDisplay(db.PackId));
            Console.WriteLine(BugDisplay(db.PackNotice));
            Console.WriteLine("报告引擎版本：" + BugDisplay(report.EngineVersion) + "；录像版本：" + report.ReplayVersion);
            Console.WriteLine("报告内容指纹：" + BugDisplay(report.ContentHash));
            if (!string.IsNullOrEmpty(report.Note)) Console.WriteLine("备注：" + BugDisplay(report.Note));
            foreach (string warning in result.Warnings) Console.WriteLine("警告：" + BugDisplay(warning));
            if (result.Battle == null)
            {
                Console.Error.WriteLine("复现失败：" + BugDisplay(result.Reason));
                return 1;
            }
            Console.WriteLine("复现阶段：" + BugPhaseName(result.Battle.Phase) + "；第 " + result.Battle.Turn + " 回合");
            Console.WriteLine("报告状态哈希：" + report.StateHash.ToString("x16", CultureInfo.InvariantCulture));
            Console.WriteLine("复现状态哈希：" + result.Battle.StateHash().ToString("x16", CultureInfo.InvariantCulture));
            Console.WriteLine("状态哈希：" + (result.StateMatches ? "一致" : "不一致"));
            Console.WriteLine("最近日志哈希：" + (result.LogMatches ? "一致" : "不一致"));
            Console.WriteLine("复现结果：" + (result.Success ? "一致" : "存在差异") + "；" + BugDisplay(result.Reason));
            return result.Success ? 0 : 1;
        }

        private static string BugPhaseName(Phase phase)
        {
            switch (phase)
            {
                case Phase.Ban: return "禁用";
                case Phase.Lead: return "选择首发";
                case Phase.Replace: return "补位";
                case Phase.Over: return "结束";
                default: return "选择行动";
            }
        }

        private static string BugDisplay(string value)
        {
            // 元数据参与诊断时仍保留原值，只有终端输出剔除控制和格式字符，不截断包编号或哈希。
            var text = new StringBuilder();
            for (int i = 0; i < (value?.Length ?? 0); i++)
            {
                char ch = value[i];
                int width = char.IsHighSurrogate(ch) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]) ? 2 : 1;
                if (char.IsSurrogate(ch) && width == 1) continue;
                var category = CharUnicodeInfo.GetUnicodeCategory(value, i);
                if (category != UnicodeCategory.Control && category != UnicodeCategory.Format
                    && category != UnicodeCategory.LineSeparator && category != UnicodeCategory.ParagraphSeparator && ch != '\u034f')
                    text.Append(value, i, width);
                i += width - 1;
            }
            return text.ToString();
        }
    }
}
