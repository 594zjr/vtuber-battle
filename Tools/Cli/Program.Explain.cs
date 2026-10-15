using System;
using System.Globalization;
using System.Linq;
using StreamDuel.Core;

namespace StreamDuel.Cli
{
    internal static partial class Program
    {
        static BattleAction ChooseAiAction(Battle battle, int side, CliOptions options)
        {
            var chosen = ChooseAiAction(battle, side, options, out var explanation);
            if (explanation != null) PrintAiExplanation(battle, side, options.Level, explanation, chosen);
            return chosen;
        }

        static BattleAction ChooseAiAction(Battle battle, int side, CliOptions options, out AiExplanation explanation)
        {
            // Explain 只读预测当前随机流；真正的选择仍由 Choose 消费本局随机数。
            explanation = options.Explain ? Ai.Explain(battle, side, options.Level) : null;
            return Ai.Choose(battle, side, options.Level);
        }

        static void PrintAiExplanation(Battle battle, int side, AiLevel level, AiExplanation explanation,
            BattleAction chosen, BattleAction? submitted = null)
        {
            Console.WriteLine();
            Console.WriteLine("【电脑出招说明 · 第 " + battle.Turn + " 回合 · " + battle.Sides[side].Name
                + " · " + LevelName(level) + "】");
            Console.WriteLine("评分用于比较选项；随机选择或换人阈值可能使已选行动不是最高分。");
            var leading = explanation.Options.Take(3).ToList();
            for (int i = 0; i < leading.Count; i++)
            {
                var option = leading[i];
                Console.WriteLine("  " + (i + 1) + ". " + (SameAction(option.Action, chosen) ? "【已选】" : "")
                    + ExplainActionName(battle, side, option.Action) + "；得分 "
                    + option.Score.ToString("0.###", CultureInfo.InvariantCulture));
                Console.WriteLine("     理由：" + string.Join("；", option.Reasons));
            }
            if (!leading.Any(option => SameAction(option.Action, chosen)))
                Console.WriteLine("已选：" + ExplainActionName(battle, side, chosen) + "（不在前三项内）。");
            if (submitted.HasValue && !SameAction(chosen, submitted.Value))
                Console.WriteLine("计时到期，本次实际提交：" + ExplainActionName(battle, side, submitted.Value)
                    + "；上面的已选标记为超时前的电脑选择。");
        }

        static bool SameAction(BattleAction first, BattleAction second)
        {
            return first.Type == second.Type && first.Index == second.Index;
        }

        static string ExplainActionName(Battle battle, int side, BattleAction action)
        {
            switch (action.Type)
            {
                case ActionType.Skill:
                    var skill = battle.Active(side).Skills[action.Index];
                    return "技能 " + (action.Index + 1) + "「" + battle.Db.Skills[skill.Id].Name + "」";
                case ActionType.Switch:
                    return "换上 " + (action.Index + 1) + " 号「" + battle.Sides[side].Team[action.Index].Name + "」";
                case ActionType.Struggle:
                    return "使用「" + battle.Db.Struggle.Name + "」";
                case ActionType.PassControlled:
                    return "受控跳过";
                default:
                    return "跳过";
            }
        }
    }
}
