using System;
using System.Collections.Generic;

namespace StreamDuel.Core
{
    public sealed partial class Battle
    {
        bool PersonaBlocksFirstControl(Fighter p, string node)
        {
            var persona = PersonaOf(p);
            if (persona.Code != "T_FIRST_CONTROL_IMMUNE" || p.FirstControlImmunityUsed) return false;
            p.FirstControlImmunityUsed = true;
            Add(node, "【人设·" + persona.Name + "】" + p.Name + " 人设生效，控制无效（本次上场的首次控制免疫已用完）", "trait", p.Side);
            return true;
        }

        bool PersonaEndure(Fighter p, ref int damage)
        {
            if (PersonaOf(p).Code != "T_ENDURE" || p.EndureUsed || p.Hp <= 0 || damage < p.Hp) return false;
            p.EndureUsed = true;
            damage = p.Hp - 1;
            return true;
        }

        void PersonaAfterKo(Fighter attacker, string node)
        {
            var persona = PersonaOf(attacker);
            if (persona.Code != "T_KO_HEAL") return;
            Heal(attacker, (int)Math.Floor(attacker.MaxHp * persona.P("frac")), node,
                "【人设·" + persona.Name + "】击倒对手后：");
        }

        void PersonaStageOnHit(Fighter p, string node)
        {
            if (p.Fainted) return;
            var persona = PersonaOf(p);
            if (persona.Code != "T_STAGE_ON_HIT") return;
            var changes = new Dictionary<string, int>();
            // 固定能力顺序让相同参数的日志不受 JSON 字段排列影响。
            foreach (var stat in ContentDb.StatKeys)
            {
                int amount = (int)persona.P(stat);
                if (amount > 0) changes[stat] = amount;
            }
            if (changes.Count == 0) return;
            Add(node, "【人设·" + persona.Name + "】" + p.Name + " 受到伤害且仍在播，提升能力", "trait", p.Side);
            ChangeStages(p, changes, node);
        }

        void PersonaNewOnEnter(Fighter p, string node)
        {
            if (p.FirstControlImmunityUsed)
            {
                p.FirstControlImmunityUsed = false;
                Add(node, "【人设·" + PersonaOf(p).Name + "】" + p.Name + " 重新上场，首次控制免疫已恢复", "trait", p.Side);
            }
            var persona = PersonaOf(p);
            if (persona.Code != "T_ALLY_PP_ON_ENTER") return;
            int amount = Math.Max(0, (int)persona.P("n"));
            foreach (var teammate in Sides[p.Side].Team)
            {
                if (teammate == p || teammate.Fainted) continue;
                foreach (var slot in teammate.Skills)
                {
                    int restored = Math.Min(amount, Math.Max(0, slot.Max - slot.Pp));
                    if (restored == 0) continue;
                    slot.Pp += restored;
                    Add(node, "【人设·" + persona.Name + "】" + teammate.Name + " 的「" + Db.Skills[slot.Id].Name +
                        "」PP +" + restored + "（" + slot.Pp + "/" + slot.Max + "）", "trait", teammate.Side);
                }
            }
        }
    }
}
