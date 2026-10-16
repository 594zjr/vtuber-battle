using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace StreamDuel.Core
{
    public sealed partial class Tournament
    {
        void WriteEliminationConfiguration(StringBuilder text)
        {
            text.Append(",\"seed\":").Append(Seed.HasValue ? Seed.Value.ToString(CultureInfo.InvariantCulture) : "null")
                .Append(",\"seedOrder\":");
            if (SeedOrder == null) text.Append("null");
            else
            {
                text.Append('[');
                for (int i = 0; i < SeedOrder.Count; i++)
                {
                    if (i > 0) text.Append(',');
                    text.Append(Number(SeedOrder[i]));
                }
                text.Append(']');
            }
            text.Append(",\"resetFinal\":").Append(ResetFinal ? "true" : "false");
            if (AutoTiebreak) text.Append(",\"autoTiebreak\":true");
        }

        static TournamentOptions ReadEliminationConfiguration(Dictionary<string, object> root)
        {
            var options = new TournamentOptions();
            if (root["seed"] != null)
            {
                if (!(root["seed"] is double seed) || double.IsNaN(seed) || double.IsInfinity(seed)
                    || seed < 0 || seed > uint.MaxValue || seed != Math.Truncate(seed))
                    throw new FormatException("赛事种子必须为无符号 32 位整数或 null。");
                options.Seed = (uint)seed;
            }
            if (root["seedOrder"] != null)
            {
                var values = root["seedOrder"] as List<object> ?? throw new FormatException("种子顺序必须为编号数组或 null。");
                var order = new List<int>();
                foreach (object item in values)
                {
                    if (!(item is double number) || double.IsNaN(number) || double.IsInfinity(number)
                        || number < 0 || number > int.MaxValue || number != Math.Truncate(number))
                        throw new FormatException("种子顺序只能包含非负整数报名编号。");
                    order.Add((int)number);
                }
                options.SeedOrder = order;
            }
            if (!(root["resetFinal"] is bool reset)) throw new FormatException("重置总决赛选项必须为布尔值。");
            options.ResetFinal = reset;
            if (root.TryGetValue("autoTiebreak", out var autoTiebreak))
            {
                if (!(autoTiebreak is bool enabled)) throw new FormatException("自动加赛选项必须为布尔值。");
                options.AutoTiebreak = enabled;
            }
            return options;
        }
    }
}
