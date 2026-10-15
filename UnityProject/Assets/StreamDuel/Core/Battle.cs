// 对战引擎。不引用 UnityEngine，Unity、命令行测试、以后的服务器都能直接用。
//
// 每回合固定的结算顺序（日志每一行都标着它属于哪个节点）：
//   换人 -> 回合开始 -> 先手行动 -> 后手行动 -> 回合结束 -> 补位
// 每次行动内部再分两个判定点：命中前（伤害之前）和命中后（伤害之后）。
using System;
using System.Collections.Generic;
using System.Linq;

namespace StreamDuel.Core
{
    public static class Node
    {
        public const string Start = "开场", Switch = "换人", TurnStart = "回合开始", First = "先手行动", Second = "后手行动",
            TurnEnd = "回合结束", Replace = "补位", Over = "结束", TurnHeader = "回合";
    }

    public enum Phase { Choose, Replace, Over, Lead }
    public enum ActionType { Skill, Switch, Pass, Struggle, PassControlled }

    public struct BattleAction
    {
        public ActionType Type; public int Index;
        public static BattleAction Skill(int i) { return new BattleAction { Type = ActionType.Skill, Index = i }; }
        public static BattleAction SwitchTo(int i) { return new BattleAction { Type = ActionType.Switch, Index = i }; }
        public static BattleAction Pass() { return new BattleAction { Type = ActionType.Pass }; }
        public override string ToString() { return Type + ":" + Index; }
    }

    public sealed class SkillSlot { public string Id; public int Pp, Max; public bool Fifth; }
    public sealed class StatusInst { public string Id; public int Left; }
    public sealed class FieldInst { public string Id; public int Turns; public int Stacks; } // Turns = -1：直到下场

    public sealed class Fighter
    {
        public StreamerDef Def; public int Slot, Side, Hp, MaxHp, EnteredTurn = 1; public bool Fainted;
        public int LastSkillIndex = -1;
        public Dictionary<string, int> Stages = NewStages();
        public List<SkillSlot> Skills = new List<SkillSlot>();
        public List<StatusInst> Statuses = new List<StatusInst>();
        public List<FieldInst> Fields = new List<FieldInst>();
        public string Name { get { return Def.Name; } }
        public string Type { get { return Def.Type; } }
        public double HpRatio { get { return (double)Hp / MaxHp; } }
        public int Pct { get { return (int)Math.Round(HpRatio * 100); } }
        public static Dictionary<string, int> NewStages() { return ContentDb.StatKeys.ToDictionary(k => k, k => 0); }
        public FieldInst FieldOf(string id) { return Fields.FirstOrDefault(f => f.Id == id); }
        public bool HasField(string id) { return Fields.Any(f => f.Id == id); }
        public bool HasStatus(string id) { return Statuses.Any(s => s.Id == id); }
    }

    public sealed class SideState { public string Name; public List<Fighter> Team = new List<Fighter>(); public int Active; }

    public sealed class LogEntry
    {
        public int Turn, Side; public string Node, Text, Kind;
        public int[] SnapActive; public int[][] SnapHp; // 这一行发生后双方的上场位置和全队人气，界面逐行回放用
    }

    public sealed class PrioInfo { public int Value; public List<string> Parts = new List<string>(); }

    public sealed class SkillOption { public int Index; public string Id; public bool Usable, Fifth; public int Pp, Max; public PrioInfo Prio; }
    public sealed class Legal
    {
        public List<SkillOption> Skills = new List<SkillOption>(); public List<int> Switches = new List<int>();
        public string Controlled; public bool Struggle, FreePp;
    }

    /// <summary>可复现的随机数（mulberry32），同一个种子打出同一局。</summary>
    public sealed class Rng
    {
        uint a;
        internal uint State { get { return a; } set { a = value; } }
        public Rng(uint seed) { a = seed; }
        public double Next()
        {
            unchecked
            {
                a += 0x6D2B79F5;
                uint t = a;
                t = (t ^ (t >> 15)) * (1 | t);
                t = (t + ((t ^ (t >> 7)) * (61 | t))) ^ t;
                return (t ^ (t >> 14)) / 4294967296.0;
            }
        }
    }

    public sealed partial class Battle
    {
        public const int TurnCap = 120;
        public readonly ContentDb Db;
        public readonly uint Seed;
        public readonly SideState[] Sides = new SideState[2];
        public readonly List<LogEntry> Log = new List<LogEntry>();
        public int Turn = 1;
        public Phase Phase = Phase.Choose;
        public int Winner = -2; // 0/1 胜方，-1 平局，-2 未结束
        public readonly bool[] Need = new bool[2];
        public readonly bool[] ActedFirst = new bool[2];
        readonly Rng rng;

        public Battle(ContentDb db, IList<string>[] teams, string[] names, uint? seed = null, bool leadPick = false)
        {
            Db = db;
            Seed = seed ?? (uint)Environment.TickCount;
            rng = new Rng(Seed);
            Record = new BattleRecord
            {
                Seed = Seed,
                PackId = db.PackId,
                LeadPick = leadPick,
                Teams = teams.Select(team => (IList<string>)new List<string>(team)).ToArray()
            };
            for (int s = 0; s < 2; s++)
            {
                Sides[s] = new SideState { Name = names[s] };
                for (int i = 0; i < teams[s].Count; i++) Sides[s].Team.Add(MakeFighter(teams[s][i], i + 1, s));
            }
            if (leadPick) Phase = Phase.Lead;
            else StartBattle();
        }

        Fighter MakeFighter(string id, int slot, int side)
        {
            var d = Db.Streamer(id);
            var f = new Fighter { Def = d, Slot = slot, Side = side, MaxHp = d.Stats["hp"], Hp = d.Stats["hp"] };
            foreach (var sid in d.Skills) f.Skills.Add(new SkillSlot { Id = sid, Pp = Db.Skills[sid].Pp, Max = Db.Skills[sid].Pp });
            f.Skills.Add(new SkillSlot { Id = d.Fifth, Pp = Db.Skills[d.Fifth].Pp, Max = Db.Skills[d.Fifth].Pp, Fifth = true });
            return f;
        }

        public double Random() { return rng.Next(); }
        public Fighter Active(int s) { return Sides[s].Team[Sides[s].Active]; }
        public PersonaDef PersonaOf(Fighter p) { return Db.Personas[p.Def.Persona]; }
        static double StageMul(int n) { return n >= 0 ? (2.0 + n) / 2.0 : 2.0 / (2.0 - n); }
        public int StatVal(Fighter p, string k) { return (int)Math.Floor(p.Def.Stats[k] * StageMul(p.Stages[k])); }
        public StatusInst ControlOf(Fighter p) { return p.Statuses.FirstOrDefault(x => Db.Statuses[x.Id].IsControl); }

        void Add(string node, string text, string kind = "info", int side = -1)
        {
            Log.Add(new LogEntry
            {
                Turn = Turn, Node = node, Text = text, Kind = kind, Side = side,
                SnapActive = new[] { Sides[0].Active, Sides[1].Active },
                SnapHp = new[] { Sides[0].Team.Select(p => p.Hp).ToArray(), Sides[1].Team.Select(p => p.Hp).ToArray() },
            });
        }

        int[] SpeedOrder()
        {
            int a = StatVal(Active(0), "spd"), b = StatVal(Active(1), "spd");
            if (a != b) return a > b ? new[] { 0, 1 } : new[] { 1, 0 };
            return rng.Next() < 0.5 ? new[] { 0, 1 } : new[] { 1, 0 };
        }

        // ================= 状态、效果、能力等级 =================
        bool ApplyStatus(Fighter p, string id, string node)
        {
            var d = Db.Statuses[id];
            if (p.Fainted) return false;
            if (d.IsControl)
            {
                if (p.Fields.Any(f => Db.Fields[f.Id].ImmuneControl)) { Add(node, p.Name + " 有「重连保护」，" + d.Name + "无效", "status", p.Side); return false; }
                var cur = ControlOf(p);
                if (cur != null) { Add(node, p.Name + " 已经处于「" + Db.Statuses[cur.Id].Name + "」，" + d.Name + "无效", "status", p.Side); return false; }
                p.Statuses.Add(new StatusInst { Id = id, Left = d.Skip });
                Add(node, p.Name + " 陷入「" + d.Name + "」（控制类，将跳过 " + d.Skip + " 次行动）", "status", p.Side);
                return true;
            }
            if (BlockWeakStatus(p, d, node)) return false;
            var same = p.Statuses.FirstOrDefault(x => x.Id == id);
            if (same != null) { same.Left = d.Turns; Add(node, p.Name + " 的「" + d.Name + "」刷新为 " + d.Turns + " 回合", "status", p.Side); return true; }
            p.Statuses.Add(new StatusInst { Id = id, Left = d.Turns });
            Add(node, p.Name + " 陷入「" + d.Name + "」（弱化类，" + d.Turns + " 回合）", "status", p.Side);
            return true;
        }

        void AddField(Fighter p, string id, int turns, string node)
        {
            var d = Db.Fields[id];
            var cur = p.FieldOf(id);
            if (cur != null) cur.Turns = turns; else p.Fields.Add(new FieldInst { Id = id, Turns = turns });
            Add(node, p.Name + (cur != null ? " 刷新" : " 获得") + "「" + d.Name + "」（" + (turns < 0 ? "直到下场" : turns + " 回合") + "，" + (d.Dispel ? "可被消回合" : "消回合清不掉") + "）", "field", p.Side);
        }

        void AddMark(Fighter p, string id, int n, string node)
        {
            var d = Db.Fields[id];
            var cur = p.FieldOf(id);
            if (cur == null) { cur = new FieldInst { Id = id, Turns = -1 }; p.Fields.Add(cur); }
            int before = cur.Stacks;
            cur.Stacks = Math.Max(0, Math.Min(d.MaxStacks > 0 ? d.MaxStacks : 99, cur.Stacks + n));
            Add(node, p.Name + " 的「" + d.Name + "」" + before + " → " + cur.Stacks + " 层", "field", p.Side);
        }

        void ChangeStages(Fighter p, Dictionary<string, int> changes, string node)
        {
            if (p.Fainted) return;
            var parts = new List<string>();
            foreach (var kv in changes)
            {
                int before = p.Stages[kv.Key];
                p.Stages[kv.Key] = Math.Max(-6, Math.Min(6, before + kv.Value));
                int d = p.Stages[kv.Key] - before;
                parts.Add(d == 0 ? Db.StatName(kv.Key) + "已到极限" : Db.StatName(kv.Key) + (d > 0 ? "+" : "") + d);
            }
            Add(node, p.Name + " " + string.Join("，", parts), "stage", p.Side);
        }

        int Heal(Fighter p, int amt, string node, string prefix = "")
        {
            if (p.Fainted || amt <= 0) return 0;
            int real = Math.Min(amt, p.MaxHp - p.Hp);
            p.Hp += real;
            Add(node, prefix + p.Name + " 人气 +" + real, "heal", p.Side);
            return real;
        }

        int Damage(Fighter p, int amt, string node, string text, bool fromHit, bool triggerFaintPersona = true, Ctx action = null, Fighter source = null)
        {
            if (p.Fainted) return 0;
            int real = Math.Min(amt, p.Hp);
            p.Hp -= real;
            RecordDamage(p, source, real);
            Add(node, text.Replace("{n}", real.ToString()), "dmg", p.Side);
            var afk = p.Statuses.FirstOrDefault(x => Db.Statuses[x.Id].WakeOnHit);
            if (afk != null && real > 0 && fromHit) { p.Statuses.Remove(afk); Add(node, p.Name + " 被打得回过神来了", "status", p.Side); }
            if (p.Hp <= 0) Faint(p, node, triggerFaintPersona, action);
            return real;
        }

        void Faint(Fighter p, string node, bool triggerPersona = true, Ctx action = null)
        {
            p.Fainted = true; p.Hp = 0;
            StatsFor(Active(1 - p.Side)).Kos++;
            Add(node, p.Name + " 下播了", "faint", p.Side);
            if (action != null && action.P == p && !action.Interrupted)
            {
                action.Interrupted = true;
                // 在终局判定前记中断，结束节点仍是本局最后一条日志。
                Add(node, p.Name + " 下播，技能中断", "info", p.Side);
            }
            if (triggerPersona) PersonaOnFaint(p, node);
            CheckOver();
        }

        bool CheckOver()
        {
            if (Phase == Phase.Over) return true;
            bool d0 = Sides[0].Team.All(p => p.Fainted), d1 = Sides[1].Team.All(p => p.Fainted);
            if (!d0 && !d1) return false;
            Phase = Phase.Over;
            Winner = d0 && d1 ? -1 : d0 ? 1 : 0;
            Add(Node.Over, Winner == -1 ? "双方同时全员下播，平局" : Sides[Winner].Name + " 获胜", "turn");
            return true;
        }

        // ================= 技能效果 =================
        sealed class Ctx { public int S; public Fighter P, F; public SkillDef Skill; public string Node; public double PowerMul = 1; public int Dealt; public bool Interrupted; }

        public bool CondMet(Fighter p, Fighter f, string node, CondDef c, out string why)
        {
            why = "";
            if (c == null) return true;
            switch (c.Kind)
            {
                case "foeHpAbove":
                {
                    bool ok = f.Hp > f.MaxHp * c.Value;
                    why = "对手人气 " + f.Pct + "% " + (ok ? "高于" : "不高于") + " " + Math.Round(c.Value * 100) + "%";
                    return ok;
                }
                case "foeWeak":
                {
                    bool ok = f.Statuses.Any(x => !Db.Statuses[x.Id].IsControl);
                    why = ok ? "对手处于弱化类状态" : "对手没有弱化类状态";
                    return ok;
                }
                case "actedFirst": why = node == Node.First ? "本方先手行动" : "本方不是先手"; return node == Node.First;
                case "actedSecond": why = node == Node.Second ? "本方后手行动" : "本方不是后手"; return node == Node.Second;
                case "foeBuffed":
                {
                    bool ok = f.Stages.Values.Any(v => v > 0);
                    why = ok ? "对手有能力提升" : "对手没有能力提升";
                    return ok;
                }
            }
            throw new KeyError("未知条件：" + c.Kind);
        }

        void RunEffect(Ctx ctx, EffectDef e)
        {
            if (Phase == Phase.Over) return;
            string why;
            bool ok = CondMet(ctx.P, ctx.F, ctx.Node, e.Cond, out why);
            string head = "〔" + (e.At == "pre" ? "命中前" : "命中后") + "判定〕";
            if (e.Cond != null) Add(ctx.Node, head + why + " → 条件" + (ok ? "满足" : "不满足"), "check", ctx.S);
            if (!ok) return;
            if (e.Chance < 100)
            {
                bool hit = rng.Next() * 100 < e.Chance;
                Add(ctx.Node, (e.Cond != null ? "" : head) + e.Chance + "% 概率判定：" + (hit ? "成功" : "未触发"), "check", ctx.S);
                if (!hit) return;
            }
            Fighter tgt = e.Target == "self" ? ctx.P : ctx.F;
            switch (e.Code)
            {
                case "STATUS": ApplyStatus(tgt, e.Status, ctx.Node); break;
                case "STAGE": ChangeStages(tgt, e.Changes, ctx.Node); break;
                case "HEAL": Heal(ctx.P, (int)(ctx.P.MaxHp * e.Frac), ctx.Node); break;
                case "RECOIL": if (ctx.Dealt > 0) Damage(ctx.P, Math.Max(1, (int)(ctx.Dealt * e.Frac)), ctx.Node, ctx.P.Name + " 受到反噬 {n}", false, action: ctx); break;
                case "DRAIN": if (ctx.Dealt > 0) Heal(ctx.P, (int)(ctx.Dealt * e.Frac), ctx.Node, "吸粉："); break;
                case "PP_REDUCE":
                {
                    if (tgt.Fainted) break;
                    if (tgt.LastSkillIndex < 0)
                    {
                        Add(ctx.Node, tgt.Name + " 还没有用过技能，PP 削减无效", "info", tgt.Side);
                        break;
                    }
                    var slot = tgt.Skills[tgt.LastSkillIndex];
                    int reduced = Math.Min(slot.Pp, Math.Max(0, e.N));
                    slot.Pp -= reduced;
                    Add(ctx.Node, tgt.Name + " 的「" + Db.Skills[slot.Id].Name + "」PP -" + reduced, "info", tgt.Side);
                    break;
                }
                case "FIXED_DAMAGE": DamageWithReflection(ctx, tgt, Math.Max(0, e.N), tgt.Name + " 受到固定伤害，人气 -{n}"); break;
                case "STEAL_BOOST": StealBoost(ctx, tgt); break;
                case "FIELD": if (!tgt.Fainted) AddField(tgt, e.Field, e.Turns, ctx.Node); break;
                case "ADD_MARK": if (!tgt.Fainted) AddMark(tgt, e.Field, e.N, ctx.Node); break;
                case "CLEANSE":
                {
                    int n = ctx.P.Statuses.RemoveAll(x => Db.Statuses[x.Id].Cleanse);
                    Add(ctx.Node, n > 0 ? ctx.P.Name + " 净化了自身的弱化类状态" : ctx.P.Name + " 没有可净化的状态", "status", ctx.S);
                    break;
                }
                case "DISPEL_TURNS": DispelTurns(ctx); break;
                case "DISPEL_BOOST": DispelBoost(ctx); break;
                case "POWER_MUL": ctx.PowerMul *= e.Mul; Add(ctx.Node, "威力 ×" + e.Mul, "info", ctx.S); break;
                case "POWER_PER_MARK":
                {
                    var m = ctx.P.FieldOf(e.Field);
                    int st = m == null ? 0 : m.Stacks;
                    double mul = 1 + e.Per * st;
                    ctx.PowerMul *= mul;
                    Add(ctx.Node, "〔命中前判定〕" + Db.Fields[e.Field].Name + " " + st + " 层 → 威力 ×" + mul.ToString("0.##"), "check", ctx.S);
                    if (e.Consume && m != null) { ctx.P.Fields.Remove(m); Add(ctx.Node, ctx.P.Name + " 的「" + Db.Fields[e.Field].Name + "」全部消耗", "field", ctx.S); }
                    break;
                }
                default: throw new KeyError("未知效果代码：" + e.Code);
            }
        }

        // 消回合：清掉对手所有“回合类”直播间效果；印记和保护类清不掉
        void DispelTurns(Ctx ctx)
        {
            var f = ctx.F; if (f.Fainted) return;
            var gone = f.Fields.Where(x => Db.Fields[x.Id].Dispel).Select(x => Db.Fields[x.Id].Name).ToList();
            var kept = f.Fields.Where(x => !Db.Fields[x.Id].Dispel).Select(x => Db.Fields[x.Id].Name).ToList();
            f.Fields.RemoveAll(x => Db.Fields[x.Id].Dispel);
            string txt = gone.Count > 0 ? "消回合：清掉了 " + f.Name + " 的 " + string.Join("、", gone) : "消回合：" + f.Name + " 身上没有回合类效果";
            if (kept.Count > 0) txt += "；" + string.Join("、", kept) + " 清不掉，保留";
            Add(ctx.Node, txt, "field", 1 - ctx.S);
        }

        // 消强化：把对手所有提升过的能力等级归零，下降的保留
        void DispelBoost(Ctx ctx)
        {
            var f = ctx.F; if (f.Fainted) return;
            var ups = f.Stages.Where(kv => kv.Value > 0).Select(kv => kv.Key).ToList();
            foreach (var k in ups) f.Stages[k] = 0;
            Add(ctx.Node, ups.Count > 0 ? "消强化：" + f.Name + " 的 " + string.Join("、", ups.Select(Db.StatName)) + " 提升归零" : "消强化：" + f.Name + " 没有能力提升", "field", 1 - ctx.S);
        }

        // ================= 伤害 =================
        public sealed class DamageResult { public int Dmg; public double Eff = 1; public bool Crit; public List<string> Notes = new List<string>(); }

        /// <summary>纯计算。estimate=true 时不掷骰、不暴击，可指定随机系数供预览使用。</summary>
        public DamageResult CalcDamage(int s, SkillDef skill, double powerMul, bool estimate, double estimateRandomFactor = 0.925)
        {
            var r = new DamageResult();
            var p = Active(s); var f = Active(1 - s);
            if (skill.Power <= 0) return r;
            bool inter = skill.Cat == "interact";
            double A = StatVal(p, inter ? "atk" : "spa"), D = StatVal(f, inter ? "def" : "sdef");
            double power = skill.Power * powerMul, mul = 1;
            if (skill.Type == p.Type) mul *= 1.5;
            r.Eff = Db.TypeMul(skill.Type, f.Type); mul *= r.Eff;
            foreach (var st in p.Statuses)
            {
                var sd = Db.Statuses[st.Id];
                if (inter && sd.InteractMul != 1) { mul *= sd.InteractMul; r.Notes.Add(sd.Name + " ×" + sd.InteractMul); }
            }
            double am; string an;
            if (PersonaAtkMul(p, f, skill, out am, out an)) { mul *= am; r.Notes.Add("人设·" + PersonaOf(p).Name + "（" + an + "）×" + am); }
            if (!inter) foreach (var fl in p.Fields) { var fd = Db.Fields[fl.Id]; if (fd.TalentMul != 1) { mul *= fd.TalentMul; r.Notes.Add(fd.Name + " ×" + fd.TalentMul); } }
            foreach (var fl in f.Fields) { var fd = Db.Fields[fl.Id]; if (fd.DmgTakenMul != 1) { mul *= fd.DmgTakenMul; r.Notes.Add(fd.Name + " ×" + fd.DmgTakenMul); } }
            double dm; string dn;
            if (PersonaDefMul(f, p, skill, out dm, out dn)) { mul *= dm; r.Notes.Add("人设·" + PersonaOf(f).Name + "（" + dn + "）×" + dm.ToString("0.##")); }
            if (!estimate && rng.Next() < 1.0 / 16) { r.Crit = true; mul *= 1.5; r.Notes.Add("暴击 ×1.5"); }
            double rand = estimate ? estimateRandomFactor : 0.85 + rng.Next() * 0.15;
            r.Dmg = Math.Max(1, (int)Math.Floor((50 * power * A / D / 50 + 2) * mul * rand));
            return r;
        }

        // ================= 先制 =================
        public PrioInfo PrioOf(int s, SkillDef skill)
        {
            var p = Active(s);
            var info = new PrioInfo { Value = skill.Prio };
            foreach (var fl in p.Fields)
            {
                var fd = Db.Fields[fl.Id];
                if (fd.Prio != 0) { info.Value += fd.Prio; info.Parts.Add(fd.Name + (fd.Prio > 0 ? "+" : "") + fd.Prio); }
            }
            int tp = PersonaPrio(p);
            if (tp != 0) { info.Value += tp; info.Parts.Add("人设·" + PersonaOf(p).Name + "+" + tp); }
            return info;
        }

        // ================= 合法行动 =================
        public Legal LegalFor(int s)
        {
            var sd = Sides[s]; var p = Active(s);
            var ctrl = ControlOf(p);
            var L = new Legal { Controlled = ctrl == null ? null : Db.Statuses[ctrl.Id].Name, FreePp = PersonaFreePp(p) };
            for (int i = 0; i < p.Skills.Count; i++)
            {
                var sl = p.Skills[i];
                L.Skills.Add(new SkillOption { Index = i, Id = sl.Id, Fifth = sl.Fifth, Pp = sl.Pp, Max = sl.Max, Usable = ctrl == null && (sl.Pp > 0 || L.FreePp), Prio = PrioOf(s, Db.Skills[sl.Id]) });
            }
            if (!SwitchLocked(p))
                for (int i = 0; i < sd.Team.Count; i++) if (i != sd.Active && !sd.Team[i].Fainted) L.Switches.Add(i);
            L.Struggle = ctrl == null && !L.Skills.Any(o => o.Usable);
            return L;
        }

        // ================= 换人 =================
        void Enter(int s, string node)
        {
            var p = Active(s);
            p.EnteredTurn = node == Node.Switch ? Turn + 1 : Turn;
            Add(node, Sides[s].Name + "：" + p.Name + " 开播", "enter", s);
            PersonaOnEnter(p, node);
        }

        void SwitchTo(int s, int to, string node)
        {
            var old = Active(s);
            old.LastSkillIndex = -1;
            if (!old.Fainted)
            {
                old.Stages = Fighter.NewStages();
                old.Fields.Clear();
                Add(node, old.Name + " 下场（能力变化、直播间效果、印记和最后使用技能记录清空，状态保留）", "info", s);
            }
            else Add(node, old.Name + " 离场，最后使用技能记录清空", "info", s);
            Sides[s].Active = to;
            Enter(s, node);
        }

        // ================= 行动 =================
        void DoAction(int s, BattleAction act, string node)
        {
            var p = Active(s); var f = Active(1 - s);
            if (p.Fainted) return;
            ActedFirst[s] = node == Node.First;
            if (act.Type == ActionType.Pass) { Add(node, p.Name + " 放弃了行动", "info", s); return; }
            var ctrl = ControlOf(p);
            if (ctrl != null)
            {
                var d = Db.Statuses[ctrl.Id];
                ctrl.Left--;
                Add(node, p.Name + " 处于「" + d.Name + "」，无法行动" + (ctrl.Left > 0 ? "（还要跳过 " + ctrl.Left + " 次）" : ""), "status", s);
                if (ctrl.Left <= 0)
                {
                    p.Statuses.Remove(ctrl);
                    Add(node, p.Name + " 的「" + d.Name + "」解除", "status", s);
                    AddField(p, "reconnect", 2, node);
                }
                return;
            }
            if (act.Type == ActionType.PassControlled) { Add(node, p.Name + " 刚回过神来，本回合来不及行动", "status", s); return; }

            SkillDef skill;
            if (act.Type == ActionType.Struggle) skill = Db.Struggle;
            else
            {
                var slot = p.Skills[act.Index];
                skill = Db.Skills[slot.Id];
                bool freePp = PersonaFreePp(p);
                if (slot.Pp <= 0 && !freePp)
                {
                    Add(node, p.Name + " 的「" + skill.Name + "」PP 已耗尽，行动失败", "info", s);
                    return;
                }
                if (freePp) Add(node, "【人设·" + PersonaOf(p).Name + "】6 号位队友在播，本次不消耗 PP", "trait", s);
                else slot.Pp = Math.Max(0, slot.Pp - 1);
                p.LastSkillIndex = act.Index;
            }
            Add(node, p.Name + " 使用「" + skill.Name + "」", "act", s);
            RecordSkillUse(p, skill.Id);
            if (f.Fainted) { Add(node, "没有目标", "info", s); return; }
            if (skill.Acc < 100 && rng.Next() * 100 >= skill.Acc) { Add(node, "没有命中（命中率 " + skill.Acc + "%）", "miss", s); return; }

            var ctx = new Ctx { S = s, P = p, F = f, Skill = skill, Node = node };
            foreach (var e in skill.Effects) if (e.At == "pre" && !ctx.Interrupted && Phase != Phase.Over) RunEffect(ctx, e);
            if (Phase == Phase.Over || ctx.Interrupted) return;
            if (skill.Power > 0 && !f.Fainted)
            {
                var r = CalcDamage(s, skill, ctx.PowerMul, false);
                if (skill.Cat == "talent") p.Fields.RemoveAll(x => Db.Fields[x.Id].Consume && Db.Fields[x.Id].TalentMul != 1);
                string effTxt = r.Eff > 1 ? "，赛道克制" : r.Eff < 1 ? "，赛道被克" : "";
                ctx.Dealt = DamageWithReflection(ctx, f, r.Dmg, f.Name + " 人气 -{n}" + effTxt + (r.Notes.Count > 0 ? "（" + string.Join("，", r.Notes) + "）" : ""));
                if (Phase != Phase.Over) PersonaAfterHit(f, p, skill, node);
            }
            foreach (var e in skill.Effects) if (e.At == "post" && !ctx.Interrupted && Phase != Phase.Over) RunEffect(ctx, e);
        }

        // ================= 回合结束 =================
        void TurnEndFor(int s)
        {
            var p = Active(s);
            if (p.Fainted || Phase == Phase.Over) return;
            foreach (var x in p.Statuses.ToList())
            {
                var d = Db.Statuses[x.Id];
                if (d.IsControl) continue;
                if (d.Dot > 0)
                {
                    int amt = Math.Max(1, (int)(p.MaxHp * d.Dot));
                    Damage(p, amt, Node.TurnEnd, p.Name + " 因「" + d.Name + "」人气 -{n}", false);
                    if (d.Leech) { var f = Active(1 - s); if (!f.Fainted) Heal(f, amt, Node.TurnEnd, "引流："); }
                }
                if (p.Fainted || Phase == Phase.Over) return;
                if (--x.Left <= 0) { p.Statuses.Remove(x); Add(Node.TurnEnd, p.Name + " 的「" + d.Name + "」结束", "status", s); }
            }
            foreach (var fl in p.Fields.ToList())
            {
                var fd = Db.Fields[fl.Id];
                if (fd.Heal > 0) Heal(p, (int)(p.MaxHp * fd.Heal), Node.TurnEnd, "「" + fd.Name + "」：");
                if (fd.Dot > 0) Damage(p, Math.Max(1, (int)(p.MaxHp * fd.Dot)), Node.TurnEnd, p.Name + " 因「" + fd.Name + "」人气 -{n}", false);
                if (p.Fainted || Phase == Phase.Over) return;
                if (fl.Turns >= 0 && --fl.Turns <= 0) { p.Fields.Remove(fl); Add(Node.TurnEnd, p.Name + " 的「" + fd.Name + "」结束", "field", s); }
            }
            PersonaTurnEnd(s, p);
        }

        // ================= 主流程 =================
        public void ResolveTurn(BattleAction a0, BattleAction a1)
        {
            if (Phase != Phase.Choose) throw new InvalidOperationException("现在不是选择行动的阶段");
            Record.Events.Add(new BattleRecordEvent { Type = BattleRecordEventType.Turn, A0 = a0, A1 = a1, RngState = rng.State });
            var acts = new[] { Normalize(0, a0), Normalize(1, a1) };
            ActedFirst[0] = ActedFirst[1] = false;
            Add(Node.TurnHeader, "第 " + Turn + " 回合", "turn");

            // 1 换人：永远最先
            foreach (int s in SpeedOrder()) if (acts[s].Type == ActionType.Switch) SwitchTo(s, acts[s].Index, Node.Switch);

            // 2 回合开始
            foreach (int s in SpeedOrder()) { var p = Active(s); if (!p.Fainted) PersonaTurnStart(s, p); }

            // 3 行动：先制高者先；先制相同比反应；再相同随机
            var actors = new List<int>();
            for (int s = 0; s < 2; s++) if (acts[s].Type != ActionType.Switch) actors.Add(s);
            if (actors.Count == 2)
            {
                int p0 = ActPrio(0, acts[0]), p1 = ActPrio(1, acts[1]);
                int s0 = StatVal(Active(0), "spd"), s1 = StatVal(Active(1), "spd");
                int first = p0 != p1 ? (p0 > p1 ? 0 : 1) : s0 != s1 ? (s0 > s1 ? 0 : 1) : (rng.Next() < 0.5 ? 0 : 1);
                int ap = first == 0 ? p0 : p1, bp = first == 0 ? p1 : p0, asp = first == 0 ? s0 : s1, bsp = first == 0 ? s1 : s0;
                string why = ap != bp ? "先制 " + ap + " 对 " + bp : asp != bsp ? "先制都是 " + ap + "，反应 " + asp + " 对 " + bsp : "先制和反应都相同，随机决定";
                Add(Node.First, Active(first).Name + " 先手（" + why + "）", "order");
                actors = new List<int> { first, 1 - first };
            }
            for (int i = 0; i < actors.Count && Phase != Phase.Over; i++) DoAction(actors[i], acts[actors[i]], i == 0 ? Node.First : Node.Second);

            // 4 回合结束
            if (Phase != Phase.Over)
            {
                RecordTurnEnd();
                foreach (int s in SpeedOrder()) TurnEndFor(s);
            }

            if (Phase != Phase.Over && Turn >= TurnCap)
            {
                double r0 = Sides[0].Team.Sum(p => p.HpRatio), r1 = Sides[1].Team.Sum(p => p.HpRatio);
                Phase = Phase.Over; Winner = r0 == r1 ? -1 : r0 > r1 ? 0 : 1;
                Add(Node.Over, "达到 " + TurnCap + " 回合上限，按剩余人气判定", "turn");
                return;
            }
            if (Phase == Phase.Over) return;

            // 5 补位
            Turn++;
            Need[0] = Active(0).Fainted; Need[1] = Active(1).Fainted;
            Phase = Need[0] || Need[1] ? Phase.Replace : Phase.Choose;
        }

        int ActPrio(int s, BattleAction a)
        {
            if (a.Type == ActionType.Pass || a.Type == ActionType.PassControlled) return 0;
            var sk = a.Type == ActionType.Skill ? Db.Skills[Active(s).Skills[a.Index].Id] : Db.Struggle;
            return PrioOf(s, sk).Value;
        }

        BattleAction Normalize(int s, BattleAction a)
        {
            var L = LegalFor(s);
            if (a.Type == ActionType.Switch && L.Switches.Contains(a.Index)) return a;
            if (L.Controlled != null) return new BattleAction { Type = ActionType.PassControlled };
            if (L.Struggle) return new BattleAction { Type = ActionType.Struggle };
            if (a.Type == ActionType.Skill && a.Index >= 0 && a.Index < L.Skills.Count && L.Skills[a.Index].Usable) return a;
            return BattleAction.Pass();
        }

        public void Replace(int s, int to)
        {
            if (Phase != Phase.Replace || !Need[s]) throw new InvalidOperationException("这一方现在不需要补位");
            var sd = Sides[s];
            if (to == sd.Active || to < 0 || to >= sd.Team.Count || sd.Team[to].Fainted) throw new InvalidOperationException("不能派出这位主播");
            Record.Events.Add(new BattleRecordEvent { Type = BattleRecordEventType.Replace, Side = s, To = to, RngState = rng.State });
            Need[s] = false;
            SwitchTo(s, to, Node.Replace);
            if (!Need[0] && !Need[1] && Phase != Phase.Over) Phase = Phase.Choose;
        }
    }
}
