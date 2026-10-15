using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace StreamDuel.Core
{
    public sealed class AiExplanation
    {
        public BattleAction Chosen;
        public List<AiOption> Options = new List<AiOption>();
    }

    public sealed class AiOption
    {
        public BattleAction Action;
        public double Score;
        public List<string> Reasons = new List<string>();
    }

    public static partial class Ai
    {
        sealed class AiTrace
        {
            public readonly List<AiOption> Options = new List<AiOption>();

            public void Add(BattleAction action, double score, params string[] reasons)
            {
                Add(action, score, (IEnumerable<string>)reasons);
            }

            public void Add(BattleAction action, double score, IEnumerable<string> reasons)
            {
                Options.Add(new AiOption { Action = action, Score = score, Reasons = new List<string>(reasons) });
            }

            public void Note(string reason)
            {
                foreach (var option in Options) option.Reasons.Add(reason);
            }
        }

        /// <summary>在独立克隆上执行同一次选招，保留原对战及其随机流。</summary>
        public static AiExplanation Explain(Battle b, int s, AiLevel level)
        {
            ExpertValidate(b, s);
            if (b.Phase != Phase.Choose) throw new InvalidOperationException("现在不是选择行动的阶段");
            if (level < AiLevel.Easy || level > AiLevel.Expert) throw new ArgumentOutOfRangeException(nameof(level));
            var trace = new AiTrace();
            var chosen = ChooseWithTrace(b.Clone(), s, level, trace);
            return new AiExplanation
            {
                Chosen = chosen,
                Options = trace.Options.OrderByDescending(option => option.Score).ToList()
            };
        }

        static string AiNumber(double value)
        {
            return value.ToString("0.######", CultureInfo.InvariantCulture);
        }

        static void ExplainDamage(List<string> reasons, Battle b, int s, SkillDef skill, double estimate, double score)
        {
            if (reasons == null) return;
            reasons.Add("主体伤害估值占对方当前人气 " + AiNumber(estimate * 100) + "%；伤害评分 " + AiNumber(score));
            if (skill.Effects.Any(effect => effect.Code == "FIXED_DAMAGE"))
                reasons.Add("固定伤害效果未单独计入此处主体伤害估值，不能据此判断技能总伤害");
            if (skill.Power > 0)
            {
                var foe = b.Active(1 - s);
                reasons.Add("技能赛道对位倍率 ×" + AiNumber(b.Db.TypeMul(skill.Type, foe.Type, foe.Type2)));
            }
            if (estimate >= 1) reasons.Add("预估伤害达到对方剩余人气，计入击倒收益；不保证实际击倒");
        }

        static string ExplainEffect(EffectDef effect)
        {
            switch (effect.Code)
            {
                case "STATUS": return "施加状态";
                case "STAGE": return "能力变化与防护";
                case "HEAL": return "回复人气";
                case "DRAIN": return "吸取人气";
                case "RECOIL": return "反噬风险";
                case "FIELD": return "直播间效果与防护";
                case "DISPEL_TURNS": return "消除回合效果";
                case "DISPEL_BOOST": return "清除对方强化";
                case "STEAL_BOOST": return "转移对方强化";
                case "CLEANSE": return "净化弱化状态";
                case "PP_REDUCE": return "削减对方 PP";
                case "ADD_MARK": return "增加印记";
                default: return "技能效果估值";
            }
        }

        static string ExplainPass(Battle b, int s)
        {
            return b.ControlOf(b.Active(s)) != null ? "受到控制，本次等待解除控制" : "没有可用技能，本次跳过";
        }
    }
}
