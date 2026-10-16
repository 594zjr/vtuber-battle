using System;
using System.Collections.Generic;
using System.Linq;

namespace StreamDuel.Core
{
    public sealed partial class ContentDb
    {
        public const string DelayedDamageFieldId = "delayed_damage";
        private const string DelayedDamageFieldName = "延迟伤害";
        private FieldDef delayedDamageDefinition;

        private void MergeDelayedDamageField(ContentDb source)
        {
            if (!source.Fields.TryGetValue(DelayedDamageFieldId, out var definition)) return;
            var candidate = CopyField(definition);
            // 自动定义不属于加载时的共用基线；只还原有声明来源的别名，再比较完整定义。
            if (source.mergeFieldAliases.ContainsKey(DelayedDamageFieldId)) candidate.Name = DelayedDamageFieldName;
            if (delayedDamageDefinition == null)
            {
                delayedDamageDefinition = candidate;
                Fields.Add(DelayedDamageFieldId, candidate);
                return;
            }

            var previousHash = new ContentHashWriter();
            var candidateHash = new ContentHashWriter();
            previousHash.Field(delayedDamageDefinition);
            candidateHash.Field(candidate);
            if (previousHash.Value != candidateHash.Value)
                throw new KeyError("合并内容包的共用规则不一致：" + source.PackId + "（" + DelayedDamageFieldId + "）");
        }

        private static int ReadDelayedDamageNumber(Dictionary<string, object> data, string key, int fallback)
        {
            if (!data.TryGetValue(key, out var raw)) return fallback;
            if (!(raw is double value) || double.IsNaN(value) || double.IsInfinity(value)
                || value < 0 || value > int.MaxValue || value != Math.Truncate(value))
                throw new KeyError("DELAYED_DAMAGE 的 " + key + " 必须是 int 范围内的非负整数");
            return (int)value;
        }

        private static void ValidateNewEffect(SkillDef skill, EffectDef effect)
        {
            if (effect.Field == DelayedDamageFieldId)
                throw new KeyError("技能 " + skill.Id + " 只能通过 DELAYED_DAMAGE 施加延迟伤害，不能通过 field 引用保留 id");
            if (effect.Code != "STAGE_SWAP" && effect.Code != "STAGE_COPY"
                && effect.Code != "PP_RESTORE" && effect.Code != "DELAYED_DAMAGE") return;
            if (effect.At != "pre" && effect.At != "post")
                throw new KeyError("技能 " + skill.Id + " 的 " + effect.Code + " 缺少判定时机 at");
            if (effect.Code == "DELAYED_DAMAGE" && (effect.Turns < 1 || effect.N < 0))
                throw new KeyError("技能 " + skill.Id + " 的 DELAYED_DAMAGE 要求 turns 至少为 1，n 不能为负数");
        }

        private void EnsureDelayedDamageField()
        {
            if (Fields.TryGetValue(DelayedDamageFieldId, out var existing))
            {
                if (!ReferenceEquals(existing, delayedDamageDefinition))
                    throw new KeyError("DELAYED_DAMAGE 使用保留的直播间效果 id：" + DelayedDamageFieldId);
                return;
            }
            if (!Skills.Values.Any(skill => skill.Effects.Any(effect => effect.Code == "DELAYED_DAMAGE"))
                && (Struggle == null || !Struggle.Effects.Any(effect => effect.Code == "DELAYED_DAMAGE"))) return;

            // 只为使用新效果的内容注册定义，旧包的规则指纹和显示文字保持不变。
            delayedDamageDefinition = new FieldDef
            {
                Id = DelayedDamageFieldId,
                Name = DelayedDamageFieldName,
                Kind = "turn",
                When = "回合结束时倒计时，到期造成固定伤害",
                Desc = "到期造成施加时指定的固定伤害；重复施加刷新时长与伤害；可被消回合，下场清除。",
                Dispel = true
            };
            Fields.Add(DelayedDamageFieldId, delayedDamageDefinition);
        }
    }
}
