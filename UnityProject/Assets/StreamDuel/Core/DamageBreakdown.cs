using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace StreamDuel.Core
{
    public enum DamageOperation
    {
        Constant, Add, Subtract, Multiply, Divide, FloorToInt, TruncateToInt, Minimum, Maximum
    }

    /// <summary>常量读取 Value；运算只引用此前步骤的零起点下标，一元运算只读取 Left。</summary>
    public sealed class DamageStep
    {
        public string Name { get; }
        public DamageOperation Operation { get; }
        public double Value { get; }
        public int Left { get; }
        public int Right { get; }

        internal DamageStep(string name, DamageOperation operation, double value, int left, int right)
        {
            Name = name;
            Operation = operation;
            Value = value;
            Left = left;
            Right = right;
        }
    }

    /// <summary>结算当时的不可变数值快照；最后一步是实际扣除人气，不引用对战或可变内容定义。</summary>
    public sealed class DamageBreakdown
    {
        public string Kind { get; }
        public string Source { get; }
        public IReadOnlyList<DamageStep> Steps { get; }

        internal DamageBreakdown(string kind, string source, IEnumerable<DamageStep> steps)
        {
            Kind = kind;
            Source = source;
            Steps = Array.AsReadOnly(steps.ToArray());
        }

        public int Recalculate()
        {
            var values = Calculate();
            return (int)values[values.Length - 1];
        }

        double[] Calculate()
        {
            var values = new double[Steps.Count];
            for (int i = 0; i < Steps.Count; i++)
            {
                var step = Steps[i];
                double left = step.Left < 0 ? 0 : values[step.Left];
                double right = step.Right < 0 ? 0 : values[step.Right];
                switch (step.Operation)
                {
                    case DamageOperation.Constant: values[i] = step.Value; break;
                    case DamageOperation.Add: values[i] = left + right; break;
                    case DamageOperation.Subtract: values[i] = left - right; break;
                    case DamageOperation.Multiply: values[i] = left * right; break;
                    case DamageOperation.Divide: values[i] = left / right; break;
                    case DamageOperation.FloorToInt: values[i] = (int)Math.Floor(left); break;
                    case DamageOperation.TruncateToInt: values[i] = (int)left; break;
                    case DamageOperation.Minimum: values[i] = Math.Min(left, right); break;
                    case DamageOperation.Maximum: values[i] = Math.Max(left, right); break;
                    default: throw new InvalidOperationException("未知伤害拆解运算。");
                }
            }
            return values;
        }

        public string ToText()
        {
            var values = Calculate();
            var parts = new List<string>();
            for (int i = 0; i < Steps.Count; i++)
            {
                var step = Steps[i];
                string expression;
                string left = step.Left < 0 ? "" : Number(values[step.Left]);
                string right = step.Right < 0 ? "" : Number(values[step.Right]);
                switch (step.Operation)
                {
                    case DamageOperation.Add: expression = left + " + " + right; break;
                    case DamageOperation.Subtract: expression = left + " − " + right; break;
                    case DamageOperation.Multiply: expression = left + " × " + right; break;
                    case DamageOperation.Divide: expression = left + " ÷ " + right; break;
                    case DamageOperation.FloorToInt: expression = "向下取整(" + left + ")"; break;
                    case DamageOperation.TruncateToInt: expression = "向零取整(" + left + ")"; break;
                    case DamageOperation.Minimum: expression = "较小值(" + left + "，" + right + ")"; break;
                    case DamageOperation.Maximum: expression = "较大值(" + left + "，" + right + ")"; break;
                    default: expression = ""; break;
                }
                parts.Add(SingleLine(step.Name) + " " + expression
                    + (expression.Length == 0 ? "" : " = ") + Number(values[i]));
            }
            return SingleLine(Kind) + "「" + SingleLine(Source) + "」：" + string.Join("；", parts);
        }

        static string Number(double value) { return value.ToString("R", CultureInfo.InvariantCulture); }
        static string SingleLine(string value)
        {
            return new string((value ?? "").Select(c => char.IsControl(c) || c == '\u2028' || c == '\u2029' ? ' ' : c).ToArray());
        }
    }

    internal sealed class DamageBuilder
    {
        readonly string kind, source;
        readonly List<DamageStep> steps = new List<DamageStep>();
        internal int Result;
        internal int Multiplier;

        internal DamageBuilder(string kind, string source) { this.kind = kind; this.source = source; }
        internal int Constant(string name, double value)
        {
            steps.Add(new DamageStep(name, DamageOperation.Constant, value, -1, -1));
            return steps.Count - 1;
        }
        internal int Apply(string name, DamageOperation operation, int left, int right = -1)
        {
            steps.Add(new DamageStep(name, operation, 0, left, right));
            return steps.Count - 1;
        }
        internal void Factor(string name, double value)
        {
            Multiplier = Apply("累计伤害倍率", DamageOperation.Multiply, Multiplier, Constant(name, value));
        }
        internal DamageBreakdown Finish(int hp, bool endured, string persona)
        {
            int before = Constant("受击前人气", hp);
            Result = Apply("人气上限截断", DamageOperation.Minimum, Result, before);
            if (endured)
            {
                int limit = Apply("人设·" + persona + "保命上限", DamageOperation.Subtract,
                    before, Constant("保留人气", 1));
                Result = Apply("保命后实际伤害", DamageOperation.Minimum, Result, limit);
            }
            return new DamageBreakdown(kind, source, steps);
        }
    }

    internal sealed class DamagePowerChange
    {
        internal string Name;
        internal double Factor, Per;
        internal int Stacks;
        internal bool FromMark;
    }

    public sealed partial class Battle
    {
        /// <summary>只控制之后伤害的解释采集，不参与规则、录像或状态哈希。</summary>
        public bool CaptureDamageBreakdowns { get; set; } = true;

        DamageBuilder FixedBreakdown(string source, int amount)
        {
            if (!CaptureDamageBreakdowns) return null;
            var b = new DamageBuilder("固定伤害", source);
            b.Result = b.Apply("非负固定伤害", DamageOperation.Maximum,
                b.Constant("固定伤害点数", amount), b.Constant("固定伤害下限", 0));
            return b;
        }

        DamageBuilder FractionBreakdown(string kind, string source, string basisName, int basis,
            string factorName, double factor, bool floor)
        {
            if (!CaptureDamageBreakdowns) return null;
            var b = new DamageBuilder(kind, source);
            int product = b.Apply("比例伤害", DamageOperation.Multiply,
                b.Constant(basisName, basis), b.Constant(factorName, factor));
            int rounded = b.Apply(floor ? "伤害向下取整" : "伤害向零取整",
                floor ? DamageOperation.FloorToInt : DamageOperation.TruncateToInt, product);
            b.Result = b.Apply("至少一点伤害", DamageOperation.Maximum, rounded, b.Constant("伤害下限", 1));
            return b;
        }

        DamageBuilder SuddenDeathBreakdown(Fighter fighter)
        {
            if (!CaptureDamageBreakdowns) return null;
            var b = new DamageBuilder("规则持续伤害", "人气骤降");
            int ratio = b.Apply("最大人气的十六分之一", DamageOperation.Divide,
                b.Constant("最大人气", fighter.MaxHp), b.Constant("伤害除数", 16));
            int rounded = b.Apply("整数除法向零取整", DamageOperation.TruncateToInt, ratio);
            b.Result = b.Apply("至少一点伤害", DamageOperation.Maximum, rounded, b.Constant("伤害下限", 1));
            return b;
        }

        DamageBuilder NormalBreakdown(Fighter attacker, Fighter defender, SkillDef skill, double powerMul,
            List<DamagePowerChange> changes)
        {
            var b = new DamageBuilder("普通技能伤害", skill.Name);
            int skillPower = b.Constant("技能威力", skill.Power);
            int powerFactor = b.Constant("命中前威力倍率", changes == null ? powerMul : 1);
            if (changes != null)
                foreach (var change in changes)
                {
                    int factor;
                    if (change.FromMark)
                    {
                        int gain = b.Apply("印记威力加成", DamageOperation.Multiply,
                            b.Constant("每层威力加成·" + change.Name, change.Per),
                            b.Constant("威力印记层数·" + change.Name, change.Stacks));
                        factor = b.Apply("印记威力倍率", DamageOperation.Add, b.Constant("基础威力倍率", 1), gain);
                    }
                    else factor = b.Constant("技能效果威力倍率", change.Factor);
                    powerFactor = b.Apply("累计威力倍率", DamageOperation.Multiply, powerFactor, factor);
                }
            int power = b.Apply("实际威力", DamageOperation.Multiply, skillPower, powerFactor);
            bool interact = skill.Cat == "interact";
            int attack = DamageStat(b, attacker, interact ? "atk" : "spa");
            int defense = DamageStat(b, defender, interact ? "def" : "sdef");
            int first = b.Apply("基础伤害首项", DamageOperation.Multiply, b.Constant("公式常数五十", 50), power);
            int attackPart = b.Apply("乘攻击能力", DamageOperation.Multiply, first, attack);
            int defensePart = b.Apply("除防守能力", DamageOperation.Divide, attackPart, defense);
            int scaled = b.Apply("公式缩放", DamageOperation.Divide, defensePart, b.Constant("公式除数五十", 50));
            b.Result = b.Apply("基础伤害", DamageOperation.Add, scaled, b.Constant("基础伤害加数", 2));
            b.Multiplier = b.Constant("伤害倍率初值", 1);
            return b;
        }

        int DamageStat(DamageBuilder b, Fighter fighter, string key)
        {
            string name = Db.StatName(key);
            int raw = b.Constant("基础" + name, fighter.Def.Stats[key]);
            int stage = b.Constant(name + "等级", fighter.Stages[key]);
            int two = b.Constant("能力等级基数", 2);
            int ratio = fighter.Stages[key] >= 0
                ? b.Apply(name + "等级倍率", DamageOperation.Divide,
                    b.Apply("非负能力等级分子", DamageOperation.Add, two, stage), two)
                : b.Apply(name + "等级倍率", DamageOperation.Divide, two,
                    b.Apply("负能力等级分母", DamageOperation.Subtract, two, stage));
            int product = b.Apply(name + "修正数值", DamageOperation.Multiply, raw, ratio);
            return b.Apply("实际" + name, DamageOperation.FloorToInt, product);
        }
    }
}
