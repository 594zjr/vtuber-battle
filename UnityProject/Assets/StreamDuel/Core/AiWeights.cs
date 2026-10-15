using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;

namespace StreamDuel.Core
{
    public sealed class AiWeights
    {
        // 被控制时保留足够人气才考虑撤退，避免把残血队友反复送回后排。
        public double ControlledSwitchMinHpRatio = 0.3;
        public double ControlledSwitchProbability = 0.35;
        // 默认保留原伤害估值；风格可以单独增加非斩杀输出的吸引力。
        public double DamageScoreMultiplier = 1;
        // 对已经能击倒的招式单独加分，优先处理先手和当前受到的致命威胁。
        public double KnockoutScore = 1.6;
        public double FirstKnockoutBonus = 0.6;
        public double ThreatenedFirstKnockoutBonus = 1;
        // 控制与弱化的收益分别调节，不能因默认数值相近而合并。
        public double ControlStatusScore = 0.5;
        public double WeakStatusScore = 0.3;
        // 只有血量充足、威胁较低且能力还未达到目标等级时，才投入强化回合。
        public double StageBoostTarget = 2;
        public double StageMinHpRatio = 0.6;
        public double StageMaxThreatRatio = 0.5;
        public double StageScore = 0.22;
        public double FoeStageScore = 0.18;
        // 回复按缺失人气估值，阈值限制健康时浪费行动。
        public double HealMaxHpRatio = 0.5;
        public double HealMissingHpScore = 0.9;
        // 通用直播间效果在安全和受压局面下采用不同收益。
        public double FieldMaxThreatRatio = 0.6;
        public double FieldSafeScore = 0.32;
        public double FieldThreatenedScore = 0.12;
        // 反弹按可反弹的伤害计分，并限制单次估值上限。
        public double ReflectScoreCap = 0.7;
        public double ReflectDamageScoreMultiplier = 2;
        // 免疫与禁换依赖对手是否真的有对应行动，禁换已有异常状态的对手更有价值。
        public double WeakImmunityScore = 0.4;
        public double TrapAfflictedScore = 0.4;
        public double TrapScore = 0.22;
        // 清掉对手身上的禁换会帮到对方，因此它与其它可驱散效果分别估值。
        public double DispelFieldScore = 0.2;
        public double DispelTrapScore = -0.2;
        public double DispelBoostScore = 0.12;
        // 偷强化同时削弱对手、提升自身，分别为转移和实际获得的等级计分。
        public double StealBoostBaseScore = 0.12;
        public double StealBoostPerStageScore = 0.08;
        public double CleanseScore = 0.4;
        // 旧策略不为削 PP 额外加分，花招风格按实际可削除的占比估值。
        public double PpReduceScore = 0;
        // 扰动只改变决策分，不增加或减少原有的随机抽取次数。
        public double RandomScoreRange = 0.08;
        // 高威胁且现有行动价值较低时，才比较换人带来的对位改善。
        public double SwitchMinThreatRatio = 0.9;
        public double SwitchMaxActionScore = 0.5;
        public double SwitchMinMatchupGain = 0.5;
        public double SwitchProbability = 0.6;
        public double MatchupSlowerPenalty = 0.2;

        static readonly FieldInfo[] fields = typeof(AiWeights).GetFields(BindingFlags.Public | BindingFlags.Instance)
            .OrderBy(field => field.Name, StringComparer.Ordinal).ToArray();
        static readonly Dictionary<string, FieldInfo> fieldsByName = fields.ToDictionary(field => field.Name, StringComparer.Ordinal);

        // 每次取得独立值，调用者调整一组参数不会污染后续默认决策。
        public static AiWeights Default { get { return new AiWeights(); } }

        public AiWeights Copy() { return (AiWeights)MemberwiseClone(); }

        public IEnumerable<string> Names()
        {
            foreach (var field in fields) yield return field.Name;
        }

        public double Get(string name)
        {
            return (double)Find(name).GetValue(this);
        }

        public void Set(string name, double value)
        {
            var field = Find(name);
            if (double.IsNaN(value) || double.IsInfinity(value)) throw new ArgumentOutOfRangeException(nameof(value), "权重必须是有限数值");
            field.SetValue(this, value);
        }

        public string ToJson()
        {
            var sb = new StringBuilder("{");
            bool first = true;
            foreach (string name in Names())
            {
                double value = Get(name);
                if (double.IsNaN(value) || double.IsInfinity(value)) throw new InvalidOperationException("权重必须是有限数值：" + name);
                if (!first) sb.Append(',');
                first = false;
                sb.Append('"').Append(name).Append("\":").Append(value.ToString("R", CultureInfo.InvariantCulture));
            }
            return sb.Append('}').ToString();
        }

        public static AiWeights FromJson(string json)
        {
            if (json == null) throw new ArgumentNullException(nameof(json));
            var values = MiniJson.Parse(json) as Dictionary<string, object>;
            if (values == null) throw new FormatException("AI 权重必须是 JSON 对象");
            var result = Default;
            foreach (var pair in values)
            {
                if (!(pair.Value is double value)) throw new FormatException("AI 权重必须是数字：" + pair.Key);
                result.Set(pair.Key, value);
            }
            return result;
        }

        static FieldInfo Find(string name)
        {
            FieldInfo field;
            if (name == null || !fieldsByName.TryGetValue(name, out field)) throw new KeyNotFoundException("未知 AI 权重：" + name);
            return field;
        }
    }
}
