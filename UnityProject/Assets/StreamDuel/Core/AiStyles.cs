using System;
using System.Linq;

namespace StreamDuel.Core
{
    public static class AiStyles
    {
        public static AiWeights Get(string name)
        {
            // 每次生成独立权重，避免调参或界面预览污染后续对局。
            switch (name)
            {
                case "aggressive":
                    return new AiWeights
                    {
                        DamageScoreMultiplier = 1.7, KnockoutScore = 2.5,
                        FirstKnockoutBonus = 0.9, ThreatenedFirstKnockoutBonus = 1.4,
                        ControlledSwitchProbability = 0.1, SwitchProbability = 0.15,
                        SwitchMinThreatRatio = 1.2, SwitchMaxActionScore = 0.35,
                        HealMissingHpScore = 0.5, FieldSafeScore = 0.2,
                        ControlStatusScore = 0.3, WeakStatusScore = 0.18
                    };
                case "defensive":
                    return new AiWeights
                    {
                        HealMaxHpRatio = 0.8, HealMissingHpScore = 2,
                        FieldSafeScore = 0.75, FieldThreatenedScore = 0.6,
                        ReflectScoreCap = 1, ReflectDamageScoreMultiplier = 3,
                        WeakImmunityScore = 0.7, CleanseScore = 0.7,
                        ControlledSwitchProbability = 0.7, SwitchProbability = 0.9,
                        SwitchMinThreatRatio = 0.65, SwitchMaxActionScore = 0.85,
                        SwitchMinMatchupGain = 0.2
                    };
                case "tricky":
                    return new AiWeights
                    {
                        ControlStatusScore = 1.1, WeakStatusScore = 0.8,
                        DispelFieldScore = 0.5, DispelBoostScore = 0.35,
                        StealBoostBaseScore = 0.25, StealBoostPerStageScore = 0.18,
                        TrapAfflictedScore = 0.75, TrapScore = 0.45,
                        PpReduceScore = 1.2
                    };
                default: return null;
            }
        }
    }

    public static partial class Ai
    {
        static AiWeights StyleWeights(Battle b, int s)
        {
            return AiStyles.Get(b.Active(s).Def.AiStyle);
        }

        static double StylePreference(Battle b, int s, BattleAction action)
        {
            var weights = StyleWeights(b, s);
            if (weights == null || action.Type != ActionType.Skill) return 0;
            var option = b.LegalFor(s).Skills.FirstOrDefault(o => o.Index == action.Index && o.Usable);
            if (option == null) return 0;
            var skill = b.Db.Skills[option.Id];
            bool first = option.Prio.Value > 0 || (option.Prio.Value == 0 && b.StatVal(b.Active(s), "spd") > b.StatVal(b.Active(1 - s), "spd"));
            double threat = Threat(b, s);
            // 搜索保留原战术分，只叠加风格相对默认估值的差额；不抽取随机数。
            return NormalSkillScore(b, s, skill, first, threat, weights)
                - NormalSkillScore(b, s, skill, first, threat, AiWeights.Default);
        }
    }
}
