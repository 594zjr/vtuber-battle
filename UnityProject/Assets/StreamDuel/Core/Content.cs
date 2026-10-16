// 内容定义：主播、技能、状态、直播间效果、人设。全部从 JSON 读入。
using System;
using System.Collections.Generic;
using System.Linq;

namespace StreamDuel.Core
{
    public sealed class TypeDef
    {
        public string Id, Name, Color;
        string symbol, colorAlt;

        public string Symbol
        {
            get { return string.IsNullOrWhiteSpace(symbol) ? "●" : symbol; }
            set { symbol = value; }
        }

        public string ColorAlt
        {
            get { return string.IsNullOrWhiteSpace(colorAlt) ? Color : colorAlt; }
            set { colorAlt = value; }
        }

        internal TypeDef Copy()
        {
            // 保留缺省值本身，让复制后的备用颜色仍能跟随原颜色回退。
            return (TypeDef)MemberwiseClone();
        }
    }

    public sealed class StatusDef
    {
        public string Id, Name, Cat, Desc; // Cat: "control" 控制类 / "weak" 弱化类
        public int Skip, Turns;
        public double Dot, InteractMul = 1;
        public bool Leech, Cleanse, WakeOnHit;
        public bool IsControl { get { return Cat == "control"; } }
    }

    public sealed class FieldDef
    {
        public string Id, Name, Kind, When, Desc; // Kind: "turn" 回合类 / "mark" 印记 / "protect" 保护
        public bool Dispel, Consume, ImmuneControl, ImmuneWeak, PreventSwitch;
        public double DmgTakenMul = 1, Heal, Dot, TalentMul = 1, ReflectDamage;
        public int Prio, MaxStacks;
    }

    public sealed class CondDef { public string Kind; public double Value; }

    public sealed class EffectDef
    {
        public string At, Code, Target, Status, Field;
        public int Chance = 100, Turns, N;
        public double Frac, Mul, Per;
        public bool Consume;
        public Dictionary<string, int> Changes = new Dictionary<string, int>();
        public CondDef Cond;
    }

    public sealed class SkillDef
    {
        public string Id, Name, Type, Cat; // Cat: "interact" 互动 / "talent" 才艺 / "status" 状态
        public bool PackDefined;
        public int Power, Pp, Acc = 100, Prio;
        public List<EffectDef> Effects = new List<EffectDef>();
    }

    public sealed class PersonaDef
    {
        public string Id, Name, Code, Node, Desc, Status, Field;
        public bool PackDefined;
        public Dictionary<string, double> Params = new Dictionary<string, double>();
        public double P(string k) { double v; return Params.TryGetValue(k, out v) ? v : 0; }
    }

    public sealed class StreamerDef
    {
        public string Id, Name, Type, Type2, Persona, Fifth, Blurb, Org, AiStyle;
        public Dictionary<string, int> Stats = new Dictionary<string, int>();
        public List<string> Skills = new List<string>();
    }

    public sealed class LimitedPoolDef
    {
        public int Max;
        public List<string> Ids = new List<string>();
    }

    public sealed class SeasonDef
    {
        public string Name;
        public List<string> Banned = new List<string>();
        public List<LimitedPoolDef> Limited = new List<LimitedPoolDef>();
    }

    public sealed partial class ContentDb
    {
        public static readonly string[] StatKeys = { "atk", "def", "spa", "sdef", "spd" };
        public Dictionary<string, string> StatNames = new Dictionary<string, string>();
        public Dictionary<string, string> CatNames = new Dictionary<string, string>();
        public Dictionary<string, TypeDef> Types = new Dictionary<string, TypeDef>();
        public Dictionary<string, Dictionary<string, double>> Chart = new Dictionary<string, Dictionary<string, double>>();
        public Dictionary<string, StatusDef> Statuses = new Dictionary<string, StatusDef>();
        public Dictionary<string, FieldDef> Fields = new Dictionary<string, FieldDef>();
        public Dictionary<string, PersonaDef> Personas = new Dictionary<string, PersonaDef>();
        public Dictionary<string, SkillDef> Skills = new Dictionary<string, SkillDef>();
        public List<StreamerDef> Streamers = new List<StreamerDef>();
        public Dictionary<string, SeasonDef> Seasons = new Dictionary<string, SeasonDef>();
        public SkillDef Struggle;
        public string PackId, PackName, PackNotice;
        ArtManifest art;
        public ArtManifest Art { get { return art ?? (art = new ArtManifest(this)); } }

        public StreamerDef Streamer(string id)
        {
            string disabled = Policy.PackReason(PackId) ?? Policy.StreamerReason(id);
            if (disabled != null) throw new KeyError(disabled);
            foreach (var s in Streamers) if (s.Id == id) return s;
            throw new KeyError("没有这个主播：" + id);
        }

        public double TypeMul(string atk, string def)
        {
            Dictionary<string, double> row; double v;
            return Chart.TryGetValue(atk, out row) && row.TryGetValue(def, out v) ? v : 1;
        }

        public double TypeMul(string atk, string def, string def2)
        {
            return TypeMul(atk, def) * (def2 == null ? 1 : TypeMul(atk, def2));
        }

        public string StatName(string k) { string v; return StatNames.TryGetValue(k, out v) ? v : k; }

        // ---------- 读取 ----------
        // rulesJson：赛道、状态、效果、技能、人设等规则数据；packJson：主播名单内容包（原创包或同人包），
        // 包内定义先合并再绑定主播，允许技能和人设引用同包的状态、效果。
        public static ContentDb FromJson(string rulesJson, string packJson)
        {
            var root = (Dictionary<string, object>)MiniJson.Parse(rulesJson);
            var db = new ContentDb();
            foreach (var kv in Obj(root, "stats")) db.StatNames[kv.Key] = (string)kv.Value;
            foreach (var kv in Obj(root, "categories")) db.CatNames[kv.Key] = (string)kv.Value;
            foreach (var kv in Obj(root, "types"))
            {
                var o = (Dictionary<string, object>)kv.Value;
                db.Types[kv.Key] = new TypeDef
                {
                    Id = kv.Key, Name = Str(o, "name"), Color = Str(o, "color"),
                    Symbol = Str(o, "symbol"), ColorAlt = Str(o, "colorAlt")
                };
            }
            foreach (var kv in Obj(root, "chart"))
            {
                var row = new Dictionary<string, double>();
                foreach (var c in (Dictionary<string, object>)kv.Value) row[c.Key] = (double)c.Value;
                db.Chart[kv.Key] = row;
            }
            db.ReadDefinitions(root, false);
            db.Struggle = ReadSkill("struggle", (Dictionary<string, object>)root["struggle"]);
            db.RememberMergeRules();

            var pack = (Dictionary<string, object>)MiniJson.Parse(packJson);
            db.PackId = Str(pack, "id"); db.PackName = Str(pack, "name"); db.PackNotice = Str(pack, "notice");
            db.ReadDefinitions(pack, true);
            db.EnsureDelayedDamageField();
            foreach (var kv in Obj(pack, "skillNames")) { if (!db.Skills.ContainsKey(kv.Key)) throw new KeyError("内容包改名的技能不存在：" + kv.Key); db.Skills[kv.Key].Name = (string)kv.Value; }
            foreach (var kv in Obj(pack, "fieldNames")) { if (!db.Fields.ContainsKey(kv.Key)) throw new KeyError("内容包改名的效果不存在：" + kv.Key); db.Fields[kv.Key].Name = (string)kv.Value; }
            foreach (var kv in Obj(pack, "statusNames")) { if (!db.Statuses.ContainsKey(kv.Key)) throw new KeyError("内容包改名的状态不存在：" + kv.Key); db.Statuses[kv.Key].Name = (string)kv.Value; }
            db.RememberMergeAliases(pack);
            foreach (var item in (List<object>)pack["streamers"])
            {
                var o = (Dictionary<string, object>)item;
                var s = new StreamerDef { Id = Str(o, "id"), Name = Str(o, "name"), Type = Str(o, "type"), Type2 = Str(o, "type2"), Persona = Str(o, "persona"), Fifth = Str(o, "fifth"), Blurb = Str(o, "blurb"), Org = Str(o, "org"), AiStyle = Str(o, "aiStyle") };
                object secondType;
                if (o.TryGetValue("type2", out secondType) && secondType != null && !(secondType is string))
                    throw new KeyError("主播 " + s.Id + " 的第二赛道 type2 必须是赛道 id 或 null");
                object aiStyle;
                if (o.TryGetValue("aiStyle", out aiStyle) && aiStyle != null && !(aiStyle is string))
                    throw new KeyError("主播 " + s.Id + " 的电脑风格 aiStyle 必须是字符串或 null");
                foreach (var st in Obj(o, "stats"))
                {
                    if (!(st.Value is double value) || double.IsNaN(value) || double.IsInfinity(value)
                        || value < int.MinValue || value > int.MaxValue || value != Math.Truncate(value))
                        throw new KeyError("主播 " + s.Id + " 的属性 " + st.Key + " 必须是 int 范围内的整数");
                    s.Stats[st.Key] = (int)value;
                }
                foreach (var sk in (List<object>)o["skills"]) s.Skills.Add((string)sk);
                // 每位主播可以有自己的人设名字和说明：复制一份同机制的人设，挂在这位主播名下
                string pName = Str(o, "personaName"), pDesc = Str(o, "personaDesc"), pStatus = Str(o, "personaStatus");
                PersonaDef basePersona;
                if ((pName != null || pDesc != null || pStatus != null) && db.Personas.TryGetValue(s.Persona, out basePersona))
                {
                    var copy = new PersonaDef { Id = s.Id + "#" + basePersona.Id, Name = pName ?? basePersona.Name, Code = basePersona.Code, Node = basePersona.Node, Desc = pDesc ?? basePersona.Desc, Status = pStatus ?? basePersona.Status, Field = basePersona.Field, Params = new Dictionary<string, double>(basePersona.Params), PackDefined = basePersona.PackDefined };
                    if (db.Personas.ContainsKey(copy.Id)) throw new KeyError("主播 " + s.Id + " 的人设副本 personas id 重复：" + copy.Id);
                    db.Personas[copy.Id] = copy;
                    s.Persona = copy.Id;
                }
                db.Streamers.Add(s);
            }
            foreach (var kv in Obj(pack, "seasons"))
            {
                var o = (Dictionary<string, object>)kv.Value;
                var season = new SeasonDef { Name = Str(o, "name") };
                object banned;
                if (o.TryGetValue("banned", out banned))
                {
                    foreach (var id in (List<object>)banned) season.Banned.Add((string)id);
                }
                object limited;
                if (o.TryGetValue("limited", out limited))
                {
                    foreach (var item in (List<object>)limited)
                    {
                        var poolData = (Dictionary<string, object>)item;
                        var pool = new LimitedPoolDef { Max = Int(poolData, "max") };
                        foreach (var id in (List<object>)poolData["ids"]) pool.Ids.Add((string)id);
                        season.Limited.Add(pool);
                    }
                }
                db.Seasons[kv.Key] = season;
            }
            db.art = ArtManifest.Read(db, pack);
            db.Validate();
            return db;
        }

        void ReadDefinitions(Dictionary<string, object> source, bool packDefined)
        {
            ReadSection(source, "statuses", Statuses, ReadStatus);
            ReadSection(source, "fields", Fields, ReadField);
            ReadSection(source, "personas", Personas, ReadPersona);
            ReadSection(source, "skills", Skills, ReadSkill);
            if (packDefined)
            {
                foreach (var kv in Obj(source, "skills")) Skills[kv.Key].PackDefined = true;
                foreach (var kv in Obj(source, "personas")) Personas[kv.Key].PackDefined = true;
            }
        }

        static void ReadSection<T>(Dictionary<string, object> source, string category,
            Dictionary<string, T> destination, Func<string, Dictionary<string, object>, T> read)
        {
            object value;
            if (!source.TryGetValue(category, out value)) return;
            var definitions = value as Dictionary<string, object>;
            if (definitions == null) throw new KeyError("内容类别 " + category + " 必须是对象");
            foreach (var kv in definitions)
            {
                if (destination.ContainsKey(kv.Key)) throw new KeyError("内容类别 " + category + " 的 id 重复：" + kv.Key);
                var definition = kv.Value as Dictionary<string, object>;
                if (definition == null) throw new KeyError("内容类别 " + category + " 的定义 " + kv.Key + " 必须是对象");
                destination.Add(kv.Key, read(kv.Key, definition));
            }
        }

        static StatusDef ReadStatus(string id, Dictionary<string, object> o)
        {
            return new StatusDef
            {
                Id = id, Name = Str(o, "name"), Cat = Str(o, "cat"), Desc = Str(o, "desc"),
                Skip = Int(o, "skip"), Turns = Int(o, "turns"), Dot = Num(o, "dot", 0), InteractMul = Num(o, "interactMul", 1),
                Leech = Bool(o, "leech"), Cleanse = Bool(o, "cleanse"), WakeOnHit = Bool(o, "wakeOnHit"),
            };
        }

        static FieldDef ReadField(string id, Dictionary<string, object> o)
        {
            return new FieldDef
            {
                Id = id, Name = Str(o, "name"), Kind = Str(o, "kind"), When = Str(o, "when"), Desc = Str(o, "desc"),
                Dispel = Bool(o, "dispel"), Consume = Bool(o, "consume"), ImmuneControl = Bool(o, "immuneControl"),
                ImmuneWeak = Bool(o, "immuneWeak"), PreventSwitch = Bool(o, "preventSwitch"),
                DmgTakenMul = Num(o, "dmgTakenMul", 1), Heal = Num(o, "heal", 0), Dot = Num(o, "dot", 0), TalentMul = Num(o, "talentMul", 1),
                ReflectDamage = Num(o, "reflectDamage", 0), Prio = Int(o, "prio"), MaxStacks = Int(o, "maxStacks"),
            };
        }

        static PersonaDef ReadPersona(string id, Dictionary<string, object> o)
        {
            var persona = new PersonaDef { Id = id, Name = Str(o, "name"), Code = Str(o, "code"), Node = Str(o, "node"), Desc = Str(o, "desc"), Status = Str(o, "status"), Field = Str(o, "field") };
            foreach (var kv in Obj(o, "params")) persona.Params[kv.Key] = (double)kv.Value;
            return persona;
        }

        static SkillDef ReadSkill(string id, Dictionary<string, object> o)
        {
            var s = new SkillDef { Id = id, Name = Str(o, "name"), Type = Str(o, "type"), Cat = Str(o, "cat"), Power = Int(o, "power"), Pp = Int(o, "pp"), Acc = o.ContainsKey("acc") ? Int(o, "acc") : 100, Prio = Int(o, "prio") };
            object effs;
            if (o.TryGetValue("effects", out effs))
                foreach (var e in (List<object>)effs)
                {
                    var eo = (Dictionary<string, object>)e;
                    var ef = new EffectDef
                    {
                        At = Str(eo, "at"), Code = Str(eo, "code"), Target = Str(eo, "target") ?? "foe", Status = Str(eo, "status"), Field = Str(eo, "field"),
                        Chance = eo.ContainsKey("chance") ? Int(eo, "chance") : 100, Turns = eo.ContainsKey("turns") ? Int(eo, "turns") : -1, N = Int(eo, "n"),
                        Frac = Num(eo, "frac", 0), Mul = Num(eo, "mul", 1), Per = Num(eo, "per", 0), Consume = Bool(eo, "consume"),
                    };
                    if (ef.Code == "DELAYED_DAMAGE")
                    {
                        ef.Turns = ReadDelayedDamageNumber(eo, "turns", -1);
                        ef.N = ReadDelayedDamageNumber(eo, "n", 0);
                    }
                    foreach (var c in Obj(eo, "changes")) ef.Changes[c.Key] = (int)(double)c.Value;
                    object cond;
                    if (eo.TryGetValue("cond", out cond))
                    {
                        var co = (Dictionary<string, object>)cond;
                        ef.Cond = new CondDef { Kind = Str(co, "kind"), Value = Num(co, "value", 0) };
                    }
                    s.Effects.Add(ef);
                }
            return s;
        }

        // 内容写错时尽早报出来，而不是打到一半才崩
        public void Validate()
        {
            EnsureDelayedDamageField();
            if (Struggle != null)
                foreach (var effect in Struggle.Effects) ValidateNewEffect(Struggle, effect);
            foreach (var sk in Skills.Values)
            {
                if (sk.Type == null || !Types.ContainsKey(sk.Type)) throw new KeyError("技能 " + sk.Id + " 的赛道不存在：" + sk.Type);
                foreach (var e in sk.Effects)
                {
                    if (e.At != "pre" && e.At != "post") throw new KeyError("技能 " + sk.Id + " 的效果缺少判定时机 at");
                    ValidateNewEffect(sk, e);
                    if (e.Code == "STATUS" && e.Status == null) throw new KeyError("技能 " + sk.Id + " 的 STATUS 缺少状态 status");
                    if ((e.Code == "FIELD" || e.Code == "ADD_MARK" || e.Code == "POWER_PER_MARK") && e.Field == null)
                        throw new KeyError("技能 " + sk.Id + " 的 " + e.Code + " 缺少效果 field");
                    if (e.Status != null && !Statuses.ContainsKey(e.Status)) throw new KeyError("技能 " + sk.Id + " 用到不存在的状态：" + e.Status);
                    if (e.Field != null && !Fields.ContainsKey(e.Field)) throw new KeyError("技能 " + sk.Id + " 用到不存在的效果：" + e.Field);
                }
            }
            foreach (var persona in Personas.Values)
            {
                if (persona.Field == DelayedDamageFieldId) throw new KeyError("人设 " + persona.Id + " 不能通过 field 引用延迟伤害的保留 id");
                if (persona.Code == "T_CONTACT_STATUS" && persona.Status == null) throw new KeyError("人设 " + persona.Id + " 缺少状态 status");
                if (persona.Code == "T_ENTER_FIELD" && persona.Field == null) throw new KeyError("人设 " + persona.Id + " 缺少效果 field");
                if (persona.Status != null && !Statuses.ContainsKey(persona.Status)) throw new KeyError("人设 " + persona.Id + " 用到不存在的状态：" + persona.Status);
                if (persona.Field != null && !Fields.ContainsKey(persona.Field)) throw new KeyError("人设 " + persona.Id + " 用到不存在的效果：" + persona.Field);
            }
            if (Streamers.Count < 6) throw new KeyError("内容包至少需要 6 位主播（一队 6 位），现在只有 " + Streamers.Count + " 位");
            foreach (var s in Streamers)
            {
                foreach (string stat in new[] { "hp" }.Concat(StatKeys))
                    if (!s.Stats.ContainsKey(stat)) throw new KeyError("主播 " + s.Id + " 缺少属性 " + stat);
                if (!Types.ContainsKey(s.Type)) throw new KeyError("主播 " + s.Id + " 的赛道不存在");
                if (s.Type2 != null)
                {
                    if (!Types.ContainsKey(s.Type2)) throw new KeyError("主播 " + s.Id + " 的第二赛道不存在：" + s.Type2);
                    if (s.Type2 == s.Type) throw new KeyError("主播 " + s.Id + " 的第二赛道不能与主赛道相同：" + s.Type2);
                }
                if (s.AiStyle != null && AiStyles.Get(s.AiStyle) == null)
                    throw new KeyError("主播 " + s.Id + " 的电脑风格 aiStyle 不存在：" + s.AiStyle);
                if (!Personas.ContainsKey(s.Persona)) throw new KeyError("主播 " + s.Id + " 的人设不存在：" + s.Persona);
                if (s.Skills.Count != 4) throw new KeyError("主播 " + s.Id + " 需要正好 4 个普通技能");
                foreach (var k in s.Skills) if (!Skills.ContainsKey(k)) throw new KeyError("主播 " + s.Id + " 的技能不存在：" + k);
                if (!Skills.ContainsKey(s.Fifth)) throw new KeyError("主播 " + s.Id + " 的第五技能不存在：" + s.Fifth);
                if (Streamers.Count(x => x.Id == s.Id) > 1) throw new KeyError("主播 id 重复：" + s.Id);
                var pd = Personas[s.Persona];
                if (pd.Status != null && !Statuses.ContainsKey(pd.Status)) throw new KeyError("主播 " + s.Id + " 的人设用到不存在的状态：" + pd.Status);
            }
            var streamerIds = new HashSet<string>(Streamers.Select(s => s.Id));
            foreach (var kv in Seasons)
            {
                foreach (var id in kv.Value.Banned)
                {
                    if (!streamerIds.Contains(id)) throw new KeyError("赛季 " + kv.Key + "（" + kv.Value.Name + "）禁用池的主播不存在：" + id);
                }
                for (int i = 0; i < kv.Value.Limited.Count; i++)
                {
                    foreach (var id in kv.Value.Limited[i].Ids)
                    {
                        if (!streamerIds.Contains(id)) throw new KeyError("赛季 " + kv.Key + "（" + kv.Value.Name + "）限制池 " + (i + 1) + " 的主播不存在：" + id);
                    }
                }
            }
            Art.Validate();
        }

        static IEnumerable<KeyValuePair<string, object>> Obj(Dictionary<string, object> o, string k)
        {
            object v;
            return o.TryGetValue(k, out v) && v is Dictionary<string, object> ? (Dictionary<string, object>)v : new Dictionary<string, object>();
        }
        static string Str(Dictionary<string, object> o, string k) { object v; return o.TryGetValue(k, out v) ? v as string : null; }
        static int Int(Dictionary<string, object> o, string k) { object v; return o.TryGetValue(k, out v) && v is double ? (int)Math.Round((double)v) : 0; }
        static double Num(Dictionary<string, object> o, string k, double d) { object v; return o.TryGetValue(k, out v) && v is double ? (double)v : d; }
        static bool Bool(Dictionary<string, object> o, string k) { object v; return o.TryGetValue(k, out v) && v is bool && (bool)v; }
    }

    public sealed class KeyError : Exception { public KeyError(string m) : base(m) { } }
}
