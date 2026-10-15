using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace StreamDuel.Core
{
    public static class BattleReport
    {
        public static string Markdown(Battle b)
        {
            return Markdown(b, 0);
        }

        public static string Markdown(Battle b, int viewerSide)
        {
            if (b == null) throw new ArgumentNullException(nameof(b));
            if (viewerSide < 0 || viewerSide >= b.Sides.Length) throw new ArgumentOutOfRangeException(nameof(viewerSide));
            var text = new StringBuilder("# 对战战报\n\n");
            if (b.Record.IsOfflineContinuation)
                text.Append("> 离线续局（未经双方终局确认）。\n\n");
            text.Append("内容包：").Append(Inline(b.Db.PackName)).Append('\n').Append('\n');
            if (!string.IsNullOrEmpty(b.Db.PackNotice))
            {
                text.Append("内容包声明：\n\n");
                Literal(text, new[] { b.Db.PackNotice });
            }
            string result = "未结束";
            if (b.Phase == Phase.Over)
            {
                if (b.Winner == -1) result = "平局";
                else if (b.Winner >= 0 && b.Winner < b.Sides.Length) result = b.Sides[b.Winner].Name + " 获胜";
            }
            text.Append("胜负：").Append(Inline(result)).Append('\n').Append('\n');
            for (int side = 0; side < b.Sides.Length; side++)
            {
                var team = b.Sides[side].Team;
                bool choosingOpening = b.Phase == Phase.Ban || b.Phase == Phase.Lead;
                text.Append("### ").Append(SideName(side)).Append('：').Append(Inline(b.Sides[side].Name)).Append('\n').Append('\n');
                // 开局重排后号位不再改变；当前场上者可能已经换人，不能用来标记首发。
                text.Append("首发：").Append(choosingOpening ? "待双方确认" : team.Count == 0 ? "无" : Inline(team[0].Name)).Append('\n').Append('\n');
                text.Append(choosingOpening ? "当前候选阵容（按号位）：\n\n" : "首发阵容（按号位）：\n\n");
                for (int index = 0; index < team.Count; index++)
                    text.Append("- ").Append(Number(index + 1)).Append(" 号位：").Append(Inline(team[index].Name))
                        .Append("（").Append(Inline(team[index].Def.Id)).Append("）\n");
                text.Append('\n');
            }

            int position = 0;
            while (position < b.Log.Count)
            {
                int turn = b.Log[position].Turn;
                text.Append("## 第 ").Append(Number(turn)).Append(" 回合\n\n");
                while (position < b.Log.Count && b.Log[position].Turn == turn)
                {
                    string node = b.Log[position].Node;
                    text.Append("### ").Append(Inline(node)).Append('\n').Append('\n');
                    var lines = new List<string>();
                    // 同一节点在后面再次出现时另起一组，避免按节点汇总打乱实际结算顺序。
                    do { lines.Add(b.Log[position++].Text); }
                    while (position < b.Log.Count && b.Log[position].Turn == turn && b.Log[position].Node == node);
                    Literal(text, lines);
                }
            }

            AppendSummary(text, b);
            AppendReview(text, b, viewerSide);
            return text.ToString();
        }

        static void AppendReview(StringBuilder text, Battle b, int viewerSide)
        {
            text.Append("## ").Append(Inline(Loc.Current.T("review.heading"))).Append("\n\n");
            if (b.Phase != Phase.Over)
            {
                text.Append(Inline(Loc.Current.T("review.unfinished"))).Append("\n\n");
                return;
            }
            text.Append(Inline(Loc.Current.T("review.perspective", b.Sides[viewerSide].Name))).Append("\n\n");
            var moments = BattleReview.KeyMoments(b, viewerSide);
            if (moments.Count == 0) text.Append(Inline(Loc.Current.T("review.empty"))).Append("\n\n");
            else
            {
                foreach (var moment in moments) text.Append("- ").Append(Inline(moment.Text)).Append('\n');
                text.Append('\n');
            }
        }

        static void AppendSummary(StringBuilder text, Battle b)
        {
            var summary = BattleSummary.From(b);
            text.Append("## 对战统计\n\n");
            for (int side = 0; side < summary.Sides.Length; side++)
            {
                text.Append("### ").Append(SideName(side)).Append('：').Append(Inline(b.Sides[side].Name)).Append('\n').Append('\n');
                text.Append("| 号位 | 主播 | 造成伤害 | 承受伤害 | 击倒 | 在场回合 | 状态 | 技能使用 |\n")
                    .Append("| --- | --- | --- | --- | --- | --- | --- | --- |\n");
                for (int index = 0; index < summary.Sides[side].Count; index++)
                {
                    var stats = summary.Sides[side][index];
                    var uses = stats.SkillUses.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair =>
                    {
                        string name = pair.Key == "struggle" ? b.Db.Struggle.Name
                            : b.Db.Skills.TryGetValue(pair.Key, out var skill) ? skill.Name : pair.Key;
                        return name + "（" + pair.Key + "）×" + Number(pair.Value);
                    });
                    text.Append("| ").Append(Number(index + 1)).Append(" | ").Append(Inline(b.Sides[side].Team[index].Name))
                        .Append(" | ").Append(Number(stats.DamageDealt)).Append(" | ").Append(Number(stats.DamageTaken))
                        .Append(" | ").Append(Number(stats.Kos)).Append(" | ").Append(Number(stats.TurnsActive))
                        .Append(" | ").Append(stats.Fainted ? "已下播" : "在播")
                        .Append(" | ").Append(stats.SkillUses.Count == 0 ? "无" : Inline(string.Join("；", uses))).Append(" |\n");
                }
                text.Append('\n');
            }
        }

        static void Literal(StringBuilder text, IList<string> lines)
        {
            int longest = 0;
            foreach (string line in lines)
            {
                int run = 0;
                foreach (char c in line ?? "")
                {
                    run = c == '`' ? run + 1 : 0;
                    longest = Math.Max(longest, run);
                }
            }
            // 围栏长于原文中的反引号，既保留原文也避免内容被解释成 Markdown。
            string fence = new string('`', Math.Max(3, longest + 1));
            text.Append(fence).Append("text\n");
            foreach (string line in lines) text.Append(line).Append('\n');
            text.Append(fence).Append('\n').Append('\n');
        }

        static string Inline(string value)
        {
            var text = new StringBuilder();
            foreach (char c in value ?? "")
            {
                if (c == '\r' || c == '\n') { text.Append(' '); continue; }
                if ("\\`*_{}[]()#+-.!|<>&".IndexOf(c) >= 0) text.Append('\\');
                text.Append(c);
            }
            return text.ToString();
        }

        static string Number(int value) { return value.ToString(CultureInfo.InvariantCulture); }
        static string SideName(int side) { return side == 0 ? "甲方" : "乙方"; }
    }
}
