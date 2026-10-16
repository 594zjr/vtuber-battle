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
        private static int CompareBranchFiles(CliOptions options)
        {
            var policy = PackPolicy.LoadDefault();
            var source = ReadBranchComparisonRecord(options.BranchCompareSource, "原录像", policy);
            var branch = ReadBranchComparisonRecord(options.BranchCompareReplay, "接手录像", policy);
            var db = LoadPack(Path.Combine(AppContext.BaseDirectory, "Content"),
                new CliOptions { Pack = source.PackId, PackDirectory = options.PackDirectory }, policy);
            // 原始记录先完成严格来源与回放校验；显示清理不能改变用于摘要的名字或事件。
            var result = BranchCompare.Compare(db, source, branch);

            Console.WriteLine("开播对决 · 接手结局对照");
            Console.WriteLine("接手回合：第 " + result.Turn + " 回合；接手方：第 " + result.HumanSide + " 方。");
            Console.WriteLine("胜负与关键回合均从接手方视角展示；第 0 / 1 方对应录像第一 / 第二方。");
            PrintBranchOutcome("原录像", result.Original, result.HumanSide);
            PrintBranchOutcome("接手录像", result.Branch, result.HumanSide);
            Console.WriteLine();
            Console.WriteLine("结论：" + result.Conclusion);
            return 0;
        }

        private static BattleRecord ReadBranchComparisonRecord(string value, string label, PackPolicy policy)
        {
            BattleRecord record;
            try
            {
                string path = ReportPath(value, "接手对照");
                byte[] bytes = File.ReadAllBytes(path);
                int offset = bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf ? 3 : 0;
                record = BattleRecord.FromJson(new UTF8Encoding(false, true).GetString(bytes, offset, bytes.Length - offset));
            }
            catch (DecoderFallbackException ex)
            {
                throw new FormatException(label + "不是有效的 UTF-8 文本。", ex);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                throw new IOException("无法读取" + label + "文件；请提供实际文件路径，不能使用录像库编号：" + ex.Message, ex);
            }
            catch (IndexOutOfRangeException ex)
            {
                throw new FormatException(label + " JSON 不完整。", ex);
            }
            catch (FormatException ex)
            {
                throw new FormatException(label + "格式无效：" + ex.Message, ex);
            }
            if (record.Version < 1 || record.Version > BattleRecord.CurrentVersion)
                throw new FormatException(label + "：" + Replay.Check(null, record, policy).Reason);
            if (string.IsNullOrWhiteSpace(record.PackId))
                throw new FormatException(label + "缺少内容包编号。");
            string disabled = policy.CheckTeam(record.PackId,
                record.Teams?.Where(team => team != null).SelectMany(team => team));
            if (disabled != null) throw new InvalidOperationException(label + "：" + disabled);
            return record;
        }

        private static void PrintBranchOutcome(string label, BranchCompareOutcome outcome, int humanSide)
        {
            Console.WriteLine();
            Console.WriteLine(label + "：" + (outcome.Winner < 0 ? "平局" : outcome.Winner == humanSide ? "获胜" : "落败"));
            Console.WriteLine("结束行动回合数：" + outcome.Turns + "；结束时回合号：" + outcome.EndTurn + "。");
            foreach (var side in outcome.Sides)
            {
                string name = TextInput.Clean(side.Name, TextInputKind.PlayerName).Text;
                Console.WriteLine("  第 " + side.Side + " 方（" + name + "）：剩余 " + side.Remaining
                    + " 人；总血量 " + side.Hp + " / " + side.MaxHp + "（"
                    + side.HpPercent.ToString("0.##", CultureInfo.InvariantCulture) + "%）。");
            }
            Console.WriteLine("接手后的关键回合：");
            if (outcome.KeyMoments.Count == 0) Console.WriteLine("  无关键回合。");
            else foreach (var moment in outcome.KeyMoments) Console.WriteLine("  " + moment.Text);
        }
    }
}
