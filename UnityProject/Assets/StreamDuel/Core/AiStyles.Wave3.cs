namespace StreamDuel.Core
{
    public static partial class AiStyles
    {
        static AiWeights Wave3(string name)
        {
            // 继承当前默认策略，只调整角色相关估值，避免重建权重时回退到旧策略。
            AiWeights weights;
            switch (name)
            {
                case "og3_liling":
                    // 首回合先制和先手增伤适合抢攻，残血时不再投入强化回合。
                    weights = AiWeights.Default;
                    weights.DamageScoreMultiplier = 1.04;
                    weights.FirstKnockoutBonus = 0.65;
                    weights.ThreatenedFirstKnockoutBonus = 0.30;
                    weights.StageMinHpRatio = 0.50;
                    weights.HealMaxHpRatio = 0.45;
                    weights.HealMissingHpScore = 0.53;
                    return weights;
                case "og3_yaye":
                    // 持续回复配合绒絮与减伤延长消耗，受压后仍保留防护收益。
                    weights = AiWeights.Default;
                    weights.WeakStatusScore = 0.10;
                    weights.FieldMaxThreatRatio = 0.08;
                    weights.FieldSafeScore = 0.43;
                    weights.FieldThreatenedScore = 0.15;
                    weights.HealMissingHpScore = 0.55;
                    return weights;
                case "og3_rongjiao":
                    // 首次免控提供布景机会；控制和禁换相配，低人气不再加双防。
                    weights = AiWeights.Default;
                    weights.ControlStatusScore = 0.56;
                    weights.TrapScore = 0.25;
                    weights.TrapAfflictedScore = 0.12;
                    weights.StageMinHpRatio = 0.55;
                    weights.SwitchMinThreatRatio = 0.38;
                    return weights;
                case "og3_paochi":
                    // 开场暖罩下安全调刻度，受伤后用净化回复重新取得展示机会。
                    weights = AiWeights.Default;
                    weights.StageScore = 0.39;
                    weights.StageMinHpRatio = 0.50;
                    weights.StageMaxThreatRatio = 0.16;
                    weights.HealMaxHpRatio = 0.48;
                    weights.HealMissingHpScore = 0.54;
                    weights.DispelFieldScore = 0.31;
                    return weights;
                case "og3_ruandian":
                    // 自带互动减伤适合反弹和后手反击，防御强化仍限于健康局面。
                    weights = AiWeights.Default;
                    weights.ReflectScoreCap = 1.40;
                    weights.ReflectDamageScoreMultiplier = 3.80;
                    weights.StageScore = 0.38;
                    weights.StageMinHpRatio = 0.55;
                    weights.DamageScoreMultiplier = 1.02;
                    weights.SwitchMinThreatRatio = 0.43;
                    return weights;
                case "og3_miantang":
                    // 回复和暖罩维持续航，受压时略早寻找好对位以保留后续登场接力。
                    weights = AiWeights.Default;
                    weights.HealMaxHpRatio = 0.55;
                    weights.HealMissingHpScore = 0.58;
                    weights.CleanseScore = 0.04;
                    weights.WeakImmunityScore = 0.32;
                    weights.SwitchMinThreatRatio = 0.38;
                    weights.SwitchMinMatchupGain = 0.94;
                    return weights;
                case "og3_zhiyuan":
                    // 健康时调盘强化纸星输出；自身每回合净化，不额外追逐净化分数。
                    weights = AiWeights.Default;
                    weights.StageScore = 0.385;
                    weights.StageMinHpRatio = 0.50;
                    weights.StageMaxThreatRatio = 0.15;
                    weights.DamageScoreMultiplier = 1.02;
                    weights.HealMaxHpRatio = 0.45;
                    weights.HealMissingHpScore = 0.53;
                    weights.DispelFieldScore = 0.31;
                    return weights;
                case "og3_guodong":
                    // 击倒后的回复鼓励连续过线，低人气优先收尾而非继续蓄势。
                    weights = AiWeights.Default;
                    weights.DamageScoreMultiplier = 1.05;
                    weights.KnockoutScore = 1.66;
                    weights.FirstKnockoutBonus = 0.64;
                    weights.StageMinHpRatio = 0.55;
                    weights.HealMaxHpRatio = 0.42;
                    weights.HealMissingHpScore = 0.52;
                    return weights;
                case "og3_xingguan":
                    // 后排减伤配合微光和储备保持场面，受压时仍可用带回复的收拍。
                    weights = AiWeights.Default;
                    weights.FieldMaxThreatRatio = 0.08;
                    weights.FieldSafeScore = 0.44;
                    weights.FieldThreatenedScore = 0.16;
                    weights.HealMaxHpRatio = 0.55;
                    weights.HealMissingHpScore = 0.55;
                    weights.SwitchMinThreatRatio = 0.43;
                    return weights;
                case "og3_yunkou":
                    // 受击提升定力后更适合留下整理对手强化，暖光防护兼顾受压局面。
                    weights = AiWeights.Default;
                    weights.DispelBoostScore = 0.14;
                    weights.CleanseScore = 0.04;
                    weights.FieldMaxThreatRatio = 0.06;
                    weights.FieldSafeScore = 0.43;
                    weights.FieldThreatenedScore = 0.15;
                    weights.SwitchMinThreatRatio = 0.42;
                    return weights;
                case "og3_modi":
                    // 停拍创造收尾机会，先制配合低人气对手增伤，谢幕顺带削 PP。
                    weights = AiWeights.Default;
                    weights.ControlStatusScore = 0.55;
                    weights.PpReduceScore = 0.08;
                    weights.FirstKnockoutBonus = 0.64;
                    weights.DamageScoreMultiplier = 1.03;
                    weights.HealMaxHpRatio = 0.45;
                    weights.HealMissingHpScore = 0.53;
                    return weights;
                case "og3_jufan":
                    // 保命提供再行动机会；健康时助跑，受压后抓先手收尾或补给。
                    weights = AiWeights.Default;
                    weights.StageScore = 0.38;
                    weights.StageMinHpRatio = 0.52;
                    weights.StageMaxThreatRatio = 0.15;
                    weights.FirstKnockoutBonus = 0.64;
                    weights.ThreatenedFirstKnockoutBonus = 0.30;
                    weights.HealMaxHpRatio = 0.45;
                    weights.HealMissingHpScore = 0.53;
                    return weights;
                default:
                    return null;
            }
        }
    }
}
