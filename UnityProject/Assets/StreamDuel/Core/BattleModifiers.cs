using System;
using System.Collections.Generic;
using System.Linq;

namespace StreamDuel.Core
{
    public sealed class ModifierDef
    {
        public string Id, Name, Desc;
    }

    public static class BattleModifiers
    {
        static readonly ModifierDef[] definitions =
        {
            new ModifierDef { Id = "half_pp", Name = "精简弹药", Desc = "双方所有技能的 PP 上限减半并向上取整，开局当前 PP 同步；整场有效，不可驱散。" },
            new ModifierDef { Id = "stage_cap_3", Name = "三阶封顶", Desc = "双方能力等级限制在 -3 至 +3，包含技能、人设和偷强化；整场有效，不可驱散。" },
            new ModifierDef { Id = "sudden_death", Name = "人气骤降", Desc = "从第 10 回合起，按回合结束顺序，在各自人设结算后失去最大人气的 1/16，向下取整且至少 1；一方全灭立即结束，不可驱散。" },
            new ModifierDef { Id = "hype_start", Name = "自带热场", Desc = "禁用与首发选择结束后，开场登场人设结算完毕时，双方实际首发各获得 1 层热度；印记不能消回合，下场清除，之后登场不再授予。" },
            new ModifierDef { Id = "no_switch", Name = "站定开播", Desc = "整场禁止主动换人，已下播后的补位不受影响；不可驱散。" },
            new ModifierDef { Id = "hp_x2", Name = "双倍人气", Desc = "开局双方所有主播的最大人气与当前人气翻倍，治疗与比例伤害使用翻倍后的上限；整场有效，不可驱散。" }
        };

        public static IReadOnlyList<ModifierDef> All
        {
            get
            {
                // 公开定义可编辑，返回副本以免界面修改全局规则目录。
                return Array.AsReadOnly(definitions.Select(d => new ModifierDef { Id = d.Id, Name = d.Name, Desc = d.Desc }).ToArray());
            }
        }

        internal static ModifierDef Find(string id)
        {
            foreach (var definition in definitions)
                if (string.Equals(definition.Id, id, StringComparison.Ordinal)) return definition;
            throw new KeyError("未知规则修饰器：" + (id ?? "空编号"));
        }

        internal static IReadOnlyList<string> Normalize(IEnumerable<string> ids)
        {
            var unique = new SortedSet<string>(StringComparer.Ordinal);
            if (ids != null)
                foreach (string id in ids)
                {
                    Find(id);
                    unique.Add(id);
                }
            return Array.AsReadOnly(unique.ToArray());
        }
    }

    public sealed partial class Battle
    {
        IReadOnlyList<string> modifiers = Array.AsReadOnly(new string[0]);

        public IReadOnlyList<string> Modifiers { get { return modifiers; } }
        internal int StageLimit { get { return HasModifier("stage_cap_3") ? 3 : 6; } }

        bool HasModifier(string id) { return modifiers.Contains(id); }

        int ClampStage(long stage)
        {
            int limit = StageLimit;
            return (int)Math.Max(-limit, Math.Min(limit, stage));
        }

        void InitializeModifiers(Fighter fighter)
        {
            if (HasModifier("half_pp"))
                foreach (var skill in fighter.Skills)
                {
                    skill.Max = skill.Max / 2 + skill.Max % 2;
                    skill.Pp = skill.Max;
                }
            if (HasModifier("hp_x2"))
            {
                if (fighter.MaxHp > int.MaxValue / 2)
                    throw new KeyError("规则修饰器 hp_x2：" + fighter.Name + " 的双倍人气超出整数范围");
                fighter.MaxHp *= 2;
                fighter.Hp = fighter.MaxHp;
            }
        }

        void LogModifiers()
        {
            foreach (string id in modifiers)
            {
                var definition = BattleModifiers.Find(id);
                Add(Node.Start, "启用规则修饰器「" + definition.Name + "」（" + id + "）：" + definition.Desc, "rule");
            }
        }

        void StartModifiers()
        {
            if (HasModifier("hype_start"))
                for (int side = 0; side < Sides.Length; side++) AddMark(Active(side), "hype", 1, Node.Start);
        }

        void TurnEndModifiers(Fighter fighter)
        {
            if (!HasModifier("sudden_death") || Turn < 10 || fighter.Fainted || Phase == Phase.Over) return;
            Damage(fighter, Math.Max(1, fighter.MaxHp / 16), Node.TurnEnd,
                fighter.Name + " 因规则修饰器「人气骤降」人气 -{n}", false);
        }
    }
}
