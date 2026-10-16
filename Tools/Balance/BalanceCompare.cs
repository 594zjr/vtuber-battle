using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace StreamDuel.Balance
{
    public static class BalanceCompare
    {
        public static BalanceComparison Compare(BalanceSnapshot oldSnapshot, BalanceSnapshot newSnapshot, double threshold = 0.05)
        {
            if (oldSnapshot == null) throw new ArgumentNullException(nameof(oldSnapshot), "旧平衡快照不能为空。");
            if (newSnapshot == null) throw new ArgumentNullException(nameof(newSnapshot), "新平衡快照不能为空。");
            if (double.IsNaN(threshold) || double.IsInfinity(threshold) || threshold < 0 || threshold > 1)
                throw new ArgumentOutOfRangeException(nameof(threshold), "变化阈值必须是 0～1 的有限数值。");
            oldSnapshot.Validate();
            newSnapshot.Validate();
            var conditions = new List<string>();
            Changed(conditions, "每位请求入队次数", oldSnapshot.GamesPerStreamer, newSnapshot.GamesPerStreamer);
            Changed(conditions, "种子", oldSnapshot.Seed, newSnapshot.Seed);
            Changed(conditions, "电脑难度", LevelName(oldSnapshot.Level), LevelName(newSnapshot.Level));
            Changed(conditions, "电脑版本", oldSnapshot.AiVersion, newSnapshot.AiVersion);
            var oldPacks = oldSnapshot.Packs.ToDictionary(p => p.PackId, StringComparer.Ordinal);
            var newPacks = newSnapshot.Packs.ToDictionary(p => p.PackId, StringComparer.Ordinal);
            var rows = new List<BalanceComparisonRow>();
            var content = new List<string>();
            foreach (string packId in oldPacks.Keys.Union(newPacks.Keys, StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal))
            {
                oldPacks.TryGetValue(packId, out var oldPack);
                newPacks.TryGetValue(packId, out var newPack);
                content.Add("内容包 " + Cell(packId) + "：旧指纹 " + Cell(oldPack?.ContentHash ?? "无旧结果")
                    + "；新指纹 " + Cell(newPack?.ContentHash ?? "无新结果") + "。");
                if (oldPack != null && newPack != null)
                    Changed(conditions, "包 " + Cell(packId) + " 的实际对战数", oldPack.Battles, newPack.Battles);
                var oldStreamers = oldPack == null ? new Dictionary<string, BalanceStreamerSnapshot>(StringComparer.Ordinal)
                    : oldPack.Streamers.ToDictionary(s => s.Id, StringComparer.Ordinal);
                var newStreamers = newPack == null ? new Dictionary<string, BalanceStreamerSnapshot>(StringComparer.Ordinal)
                    : newPack.Streamers.ToDictionary(s => s.Id, StringComparer.Ordinal);
                foreach (string id in oldStreamers.Keys.Union(newStreamers.Keys, StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal))
                {
                    oldStreamers.TryGetValue(id, out var oldStreamer);
                    newStreamers.TryGetValue(id, out var newStreamer);
                    if (oldStreamer != null && newStreamer != null)
                        Changed(conditions, Cell(packId) + "/" + Cell(id) + " 的实际入队次数", oldStreamer.Games, newStreamer.Games);
                    rows.Add(new BalanceComparisonRow(packId, id, oldStreamer, newStreamer, threshold));
                }
            }
            return new BalanceComparison(rows, conditions, content, threshold);
        }

        static void Changed<T>(List<string> differences, string label, T oldValue, T newValue)
        {
            if (!EqualityComparer<T>.Default.Equals(oldValue, newValue))
                differences.Add(label + "：" + Cell(Convert.ToString(oldValue, CultureInfo.InvariantCulture)) + " → "
                    + Cell(Convert.ToString(newValue, CultureInfo.InvariantCulture)));
        }

        static string LevelName(string level)
        {
            switch (level)
            {
                case "easy": return "简单";
                case "normal": return "普通";
                case "hard": return "困难";
                default: return "专家";
            }
        }

        internal static string Cell(string value)
        {
            return (value ?? string.Empty).Replace('|', '｜').Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
        }
    }

    public sealed class BalanceComparisonRow
    {
        readonly BalanceStreamerSnapshot oldValue, newValue;
        public string PackId { get; }
        public string Id { get; }
        public BalanceStreamerSnapshot Old { get { return oldValue?.Copy(); } }
        public BalanceStreamerSnapshot New { get { return newValue?.Copy(); } }
        public double? Delta { get; }
        public bool IsSignificant { get; }
        public bool IsOutOfRange { get; }

        internal BalanceComparisonRow(string packId, string id, BalanceStreamerSnapshot oldStreamer,
            BalanceStreamerSnapshot newStreamer, double threshold)
        {
            PackId = packId;
            Id = id;
            oldValue = oldStreamer?.Copy();
            newValue = newStreamer?.Copy();
            if (newValue != null) IsOutOfRange = newValue.ExactRate() < 0.35m || newValue.ExactRate() > 0.65m;
            if (oldValue == null || newValue == null) return;
            // 从整数计数算净差，避免 55%-50% 的二进制误差误触发严格 5 个百分点门槛。
            decimal difference = newValue.ExactRate() - oldValue.ExactRate();
            Delta = (double)difference;
            IsSignificant = Math.Abs(difference) > (decimal)threshold
                && (oldValue.High95 < newValue.Low95 || newValue.High95 < oldValue.Low95);
        }
    }

    public sealed class BalanceComparison
    {
        readonly string[] conditions, content;
        readonly double threshold;
        public IReadOnlyList<BalanceComparisonRow> Rows { get; }
        public bool ConditionsDiffer { get { return conditions.Length > 0; } }
        public bool HasOutOfRange { get; }
        public bool HasSignificantChanges { get; }

        internal BalanceComparison(IEnumerable<BalanceComparisonRow> rows, IEnumerable<string> conditions,
            IEnumerable<string> content, double threshold)
        {
            var captured = rows.ToArray();
            Rows = Array.AsReadOnly(captured);
            this.conditions = conditions.ToArray();
            this.content = content.ToArray();
            this.threshold = threshold;
            HasOutOfRange = captured.Any(row => row.IsOutOfRange);
            HasSignificantChanges = captured.Any(row => row.IsSignificant);
        }

        public string ToText()
        {
            var text = new StringBuilder();
            text.Append(ConditionsDiffer ? "条件不同，对比仅供参考" : "平衡结果对比").Append('\n');
            if (ConditionsDiffer) text.Append("差异：").Append(string.Join("；", conditions)).Append("。\n");
            foreach (string line in content) text.Append(line).Append('\n');
            text.Append("内容包 | 主播 | 旧胜率〔95%区间〕 | 新胜率〔95%区间〕 | 差值（百分点） | 标记\n");
            foreach (var row in Rows)
            {
                var oldValue = row.Old;
                var newValue = row.New;
                var marks = new List<string>();
                if (oldValue == null) marks.Add("新增");
                else if (newValue == null) marks.Add("移除");
                else if (row.Delta == 0) marks.Add("无变化");
                if (row.IsSignificant) marks.Add("明显变化");
                if (row.IsOutOfRange) marks.Add("超出平衡范围");
                if (marks.Count == 0) marks.Add("未达到明显变化条件");
                string name = newValue?.Name ?? oldValue.Name;
                text.Append(BalanceCompare.Cell(row.PackId)).Append(" | ")
                    .Append(BalanceCompare.Cell(name)).Append("（").Append(BalanceCompare.Cell(row.Id)).Append("） | ")
                    .Append(Stats(oldValue)).Append(" | ").Append(Stats(newValue)).Append(" | ")
                    .Append(row.Delta.HasValue ? SignedPoints(row.Delta.Value) : "—")
                    .Append(" | ").Append(string.Join("、", marks)).Append('\n');
            }
            text.Append("明显变化：胜率绝对差严格大于 ").Append(Percent(threshold))
                .Append(" 个百分点，且双侧 95% 区间严格分离；区间端点相触不算分离。\n")
                .Append("平衡范围：新胜率低于 35% 或高于 65% 才告警，边界值不告警。\n")
                .Append("说明：按实际入队次数统计，平局计半胜；Wilson 区间仅作近似诊断，同局阵容共享结果，不能视为独立二项保证。\n");
            return text.ToString();
        }

        static string Stats(BalanceStreamerSnapshot value)
        {
            return value == null ? "—" : Percent(value.WinRate) + "%〔" + Percent(value.Low95) + "%～" + Percent(value.High95) + "%〕";
        }

        static string SignedPoints(double value)
        {
            return (value > 0 ? "+" : "") + Percent(value);
        }

        static string Percent(double value)
        {
            return (value * 100).ToString("0.####", CultureInfo.InvariantCulture);
        }
    }
}
