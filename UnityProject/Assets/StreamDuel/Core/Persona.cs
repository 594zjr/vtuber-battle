// 人设（专属特质）。每个 code 只在它声明的时机被引擎调用；新人设只要复用这些 code 就不用改引擎。
// 人设本身永久生效、不能被消除，但它给出的直播间效果按那个效果自己的规则走。
using System.Linq;

namespace StreamDuel.Core
{
    public sealed partial class Battle
    {
        bool PersonaAtkMul(Fighter p, Fighter f, SkillDef sk, out double mul, out string why)
        {
            var t = PersonaOf(p); mul = 1; why = null;
            switch (t.Code)
            {
                case "T_LOWHP_POWER":
                    if (p.Hp < p.MaxHp * t.P("below")) { mul = t.P("mul"); why = "自身人气 " + p.Pct + "% 低于 1/3"; return true; }
                    break;
                case "T_EXECUTE":
                    if (f.Hp < f.MaxHp * t.P("below")) { mul = t.P("mul"); why = "对手人气 " + f.Pct + "% 低于 1/2"; return true; }
                    break;
            }
            return false;
        }

        bool PersonaDefMul(Fighter p, Fighter attacker, SkillDef sk, out double mul, out string why)
        {
            var t = PersonaOf(p); mul = 1; why = null;
            switch (t.Code)
            {
                case "T_BENCH_GUARD":
                {
                    int n = Sides[p.Side].Team.Count(x => x != p && !x.Fainted);
                    if (n > 0) { mul = 1 - t.P("per") * n; why = "后排在播 " + n + " 位"; return true; }
                    break;
                }
                case "T_INTERACT_REDUCE":
                    if (sk.Cat == "interact") { mul = t.P("mul"); why = "互动类伤害"; return true; }
                    break;
            }
            return false;
        }

        int PersonaPrio(Fighter p)
        {
            var t = PersonaOf(p);
            return t.Code == "T_FIRST_TURN_PRIO" && p.EnteredTurn == Turn ? (int)t.P("prio") : 0;
        }

        bool PersonaFreePp(Fighter p)
        {
            if (PersonaOf(p).Code != "T_FREE_PP_SLOT6") return false;
            var team = Sides[p.Side].Team;
            return team.Count >= 6 && team[5] != p && !team[5].Fainted;
        }

        void PersonaAfterHit(Fighter p, Fighter attacker, SkillDef sk, string node)
        {
            var t = PersonaOf(p);
            if (t.Code != "T_CONTACT_STATUS" || sk.Cat != "interact" || attacker.Fainted) return;
            bool ok = rng.Next() * 100 < t.P("chance");
            Add(node, "【人设·" + t.Name + "】" + p.Name + " 被互动类技能击中（" + t.P("chance") + "% 判定：" + (ok ? "成功" : "未触发") + "）", "trait", p.Side);
            if (ok) ApplyStatus(attacker, t.Status, node);
        }

        void PersonaOnEnter(Fighter p, string node)
        {
            var t = PersonaOf(p);
            if (t.Code != "T_ENTER_FIELD") return;
            Add(node, "【人设·" + t.Name + "】" + p.Name + " 开播", "trait", p.Side);
            AddField(p, t.Field, (int)t.P("turns"), node);
        }

        void PersonaOnFaint(Fighter p, string node)
        {
            var t = PersonaOf(p);
            if (t.Code != "T_FAINT_CURSE") return;
            var f = Active(1 - p.Side);
            if (f.Fainted) return;
            Add(node, "【人设·" + t.Name + "】" + p.Name + " 下播时发动", "trait", p.Side);
            int n = (int)t.P("n");
            ChangeStages(f, ContentDb.StatKeys.ToDictionary(k => k, k => n), node);
        }

        void PersonaTurnStart(int s, Fighter p)
        {
            var t = PersonaOf(p);
            if (t.Code != "T_TURNSTART_CLEANSE") return;
            var w = p.Statuses.FirstOrDefault(x => !Db.Statuses[x.Id].IsControl);
            if (w == null) return;
            p.Statuses.Remove(w);
            Add(Node.TurnStart, "【人设·" + t.Name + "】" + p.Name + " 移除了自身的「" + Db.Statuses[w.Id].Name + "」", "trait", s);
        }

        void PersonaTurnEnd(int s, Fighter p)
        {
            var t = PersonaOf(p);
            switch (t.Code)
            {
                case "T_REGEN":
                    Heal(p, (int)(p.MaxHp * t.P("frac")), Node.TurnEnd, "【人设·" + t.Name + "】");
                    break;
                case "T_SPEED_IF_FIRST":
                    if (ActedFirst[s])
                    {
                        Add(Node.TurnEnd, "【人设·" + t.Name + "】" + p.Name + " 本回合先手", "trait", s);
                        ChangeStages(p, new System.Collections.Generic.Dictionary<string, int> { { "spd", (int)t.P("n") } }, Node.TurnEnd);
                    }
                    break;
            }
        }
    }
}
