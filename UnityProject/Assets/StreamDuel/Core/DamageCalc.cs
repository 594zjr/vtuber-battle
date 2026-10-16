using System;
using System.Collections.Generic;

namespace StreamDuel.Core
{
    public static class DamageCalc
    {
        /// <summary>
        /// 双方各一位、均为首发的开场预览，不含后排支援；技能下标从 0 开始。
        /// 可选字典覆盖登场后的绝对能力等级（-6～+6），未指定的能力保留原值。
        /// 预览范围与限制沿用 Battle.PreviewDamage，不修改内容库或输入字典。
        /// </summary>
        public static Battle.DamagePreview Range(ContentDb db, string attackerId, int skillIndex,
            string defenderId, IDictionary<string, int> attackerStages = null,
            IDictionary<string, int> defenderStages = null)
        {
            if (db == null) throw new ArgumentNullException(nameof(db));
            if (string.IsNullOrWhiteSpace(attackerId))
                throw new ArgumentException("攻击方主播 id 不能为空", nameof(attackerId));
            if (string.IsNullOrWhiteSpace(defenderId))
                throw new ArgumentException("防守方主播 id 不能为空", nameof(defenderId));
            var attacker = db.Streamer(attackerId);
            db.Streamer(defenderId);
            if (skillIndex < 0 || skillIndex > attacker.Skills.Count)
                throw new ArgumentOutOfRangeException(nameof(skillIndex), "技能下标不在主播技能列表中");
            ValidateStages(attackerStages, nameof(attackerStages));
            ValidateStages(defenderStages, nameof(defenderStages));

            // 两位 id 无法确定后排；只创建指定单位，避免凭空加入队友触发人设。
            var battle = new Battle(db, new IList<string>[] { new[] { attackerId }, new[] { defenderId } },
                new[] { "你", "对手" }, 0);
            SetStages(battle.Active(0), attackerStages);
            SetStages(battle.Active(1), defenderStages);
            return battle.PreviewDamage(0, skillIndex);
        }

        /// <summary>
        /// 两支合法六人队伍的伤害百分比矩阵；每格重新选择双方首发，保留完整后排。
        /// 取可用技能（含第五技能）最高的预览均值 / 对手最大人气 × 100，四舍五入。
        /// 没有可用技能时为 0；允许超过 100，超出 int 表示范围时取 int.MaxValue。
        /// </summary>
        public static int[,] Matchup(ContentDb db, IList<string> teamA, IList<string> teamB)
        {
            if (db == null) throw new ArgumentNullException(nameof(db));
            ValidateTeam(db, teamA, nameof(teamA));
            ValidateTeam(db, teamB, nameof(teamB));
            var result = new int[teamA.Count, teamB.Count];
            for (int i = 0; i < teamA.Count; i++)
            {
                for (int j = 0; j < teamB.Count; j++)
                {
                    // 复用首发重排，使号位、后排支援和登场效果与真实对战一致。
                    var battle = new Battle(db, new[] { teamA, teamB }, new[] { "你", "对手" }, 0, true);
                    battle.ChooseLead(0, i);
                    battle.ChooseLead(1, j);
                    double bestAverage = 0;
                    foreach (var skill in battle.LegalFor(0).Skills)
                    {
                        if (!skill.Usable) continue;
                        var preview = battle.PreviewDamage(0, skill.Index);
                        double average = ((double)preview.Min + preview.Max) / 2;
                        bestAverage = Math.Max(bestAverage, average);
                    }
                    double percent = bestAverage / battle.Active(1).MaxHp * 100;
                    result[i, j] = (int)Math.Min(int.MaxValue, Math.Round(percent, MidpointRounding.AwayFromZero));
                }
            }
            return result;
        }

        static void ValidateTeam(ContentDb db, IList<string> team, string parameter)
        {
            if (team == null) throw new ArgumentNullException(parameter);
            var errors = TeamCheck.Validate(db, null, team);
            if (errors.Count > 0) throw new ArgumentException(string.Join("；", errors), parameter);
            foreach (string id in team)
            {
                var streamer = db.Streamer(id);
                // 内容包基础校验允许非正人气，百分比分析必须先拒绝无效分母。
                if (streamer.Stats["hp"] <= 0)
                    throw new ArgumentException(streamer.Name + "的最大人气必须大于 0", parameter);
            }
        }

        static void ValidateStages(IDictionary<string, int> stages, string parameter)
        {
            if (stages == null) return;
            foreach (var stage in stages)
            {
                if (Array.IndexOf(ContentDb.StatKeys, stage.Key) < 0)
                    throw new ArgumentException("未知能力：" + stage.Key, parameter);
                if (stage.Value < -6 || stage.Value > 6)
                    throw new ArgumentException("能力等级必须在 -6～+6 之间：" + stage.Key, parameter);
            }
        }

        static void SetStages(Fighter fighter, IDictionary<string, int> stages)
        {
            if (stages == null) return;
            foreach (var stage in stages) fighter.Stages[stage.Key] = stage.Value;
        }
    }
}
