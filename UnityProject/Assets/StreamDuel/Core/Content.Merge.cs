using System;
using System.Collections.Generic;
using System.Linq;

namespace StreamDuel.Core
{
    public sealed partial class ContentDb
    {
        ContentDb mergeRules;
        Dictionary<string, string> mergeSkillAliases = new Dictionary<string, string>();
        Dictionary<string, string> mergeFieldAliases = new Dictionary<string, string>();
        Dictionary<string, string> mergeStatusAliases = new Dictionary<string, string>();
        Dictionary<string, string> mergedSources;
        Dictionary<string, string> mergedNotices;

        public bool IsMixed { get { return mergedSources != null; } }

        public string NoticeFor(string streamerId)
        {
            Streamer(streamerId);
            string notice;
            return mergedNotices != null && mergedNotices.TryGetValue(streamerId, out notice)
                ? notice : PackNotice ?? "";
        }

        void RememberMergeRules()
        {
            // 来源成员不能靠 PackDefined 判断：状态、效果及主播人设副本没有统一的来源标记。
            mergeRules = CopyContent();
        }

        void RememberMergeAliases(Dictionary<string, object> pack)
        {
            foreach (var item in Obj(pack, "skillNames"))
                if (mergeRules.Skills.ContainsKey(item.Key)) mergeSkillAliases.Add(item.Key, (string)item.Value);
            foreach (var item in Obj(pack, "fieldNames"))
                if (mergeRules.Fields.ContainsKey(item.Key)) mergeFieldAliases.Add(item.Key, (string)item.Value);
            foreach (var item in Obj(pack, "statusNames"))
                if (mergeRules.Statuses.ContainsKey(item.Key)) mergeStatusAliases.Add(item.Key, (string)item.Value);
        }

        public static ContentDb Merge(params ContentDb[] packs)
        {
            if (packs == null || packs.Length == 0) throw new KeyError("合并内容包至少需要一个包。");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var pack in packs)
            {
                if (pack == null) throw new KeyError("合并内容包不能为空。");
                if (string.IsNullOrWhiteSpace(pack.PackId)) throw new KeyError("合并内容包的 id 不能为空。");
                ValidateMergeId(pack.PackId);
                if (pack.IsMixed || pack.PackId.StartsWith("mix:", StringComparison.Ordinal))
                    throw new KeyError("请直接合并原始内容包，不能嵌套合并 mix: 内容包。");
                if (!ids.Add(pack.PackId)) throw new KeyError("内容包 id 重复：" + pack.PackId);
                if (pack.mergeRules == null) throw new KeyError("内容包缺少规则来源，请先通过 FromJson 加载：" + pack.PackId);
                string unavailable = pack.Policy.CheckTeam(pack.PackId, pack.Streamers.Select(s => s.Id));
                if (unavailable != null) throw new PackDisabledException(unavailable);
                if (!SamePolicy(packs[0].Policy, pack.Policy))
                    throw new KeyError("混合内容包的下架策略不一致，请使用同一策略重新加载全部来源包。");
                pack.Validate();
            }

            var ordered = packs.OrderBy(p => p.PackId, StringComparer.Ordinal).ToArray();
            var result = ordered[0].RulesForMerge();
            string baselineHash = ordered[0].mergeRules.ContentHash();
            string currentRulesHash = result.ContentHash();
            var skillAliases = new Dictionary<string, string>();
            var fieldAliases = new Dictionary<string, string>();
            var statusAliases = new Dictionary<string, string>();
            result.mergedSources = new Dictionary<string, string>();
            result.mergedNotices = new Dictionary<string, string>();
            var streamerIds = new HashSet<string>(StringComparer.Ordinal);

            foreach (var source in ordered)
            {
                var sourceRules = source.RulesForMerge();
                if (source.mergeRules.ContentHash() != baselineHash || sourceRules.ContentHash() != currentRulesHash
                    || !SameTrackDisplay(ordered[0].mergeRules, source.mergeRules)
                    || !SameTrackDisplay(result, sourceRules))
                    throw new KeyError("合并内容包的共用规则不一致：" + source.PackId);
                CollectAliases(skillAliases, source.mergeSkillAliases, "技能");
                CollectAliases(fieldAliases, source.mergeFieldAliases, "直播间效果");
                CollectAliases(statusAliases, source.mergeStatusAliases, "状态");
                // 指纹冻结来源的全部显示内容；返回的定义仍独立可变，之后每次计算指纹都会读取它们。
                result.mergedSources.Add(source.PackId, source.ContentHash());
                var copy = source.CopyContent();
                AddPrivate(result.Statuses, copy.Statuses, source.mergeRules.Statuses, "状态");
                AddPrivate(result.Fields, copy.Fields, source.mergeRules.Fields, "直播间效果");
                AddPrivate(result.Personas, copy.Personas, source.mergeRules.Personas, "人设");
                AddPrivate(result.Skills, copy.Skills, source.mergeRules.Skills, "技能");
                AddPrivate(result.Seasons, copy.Seasons, source.mergeRules.Seasons, "赛季");
                foreach (var streamer in copy.Streamers)
                {
                    if (!streamerIds.Add(streamer.Id)) throw new KeyError("主播 id 冲突：" + streamer.Id);
                    result.Streamers.Add(streamer);
                    result.mergedNotices.Add(streamer.Id, source.NoticeFor(streamer.Id));
                }
            }
            foreach (var alias in skillAliases) result.Skills[alias.Key].Name = alias.Value;
            foreach (var alias in fieldAliases) result.Fields[alias.Key].Name = alias.Value;
            foreach (var alias in statusAliases) result.Statuses[alias.Key].Name = alias.Value;
            result.Streamers = result.Streamers.OrderBy(s => s.Id, StringComparer.Ordinal).ToList();
            result.PackId = "mix:" + string.Join(",", ordered.Select(p => Uri.EscapeDataString(p.PackId)));
            string disabledCombination = result.Policy.PackReason(result.PackId);
            if (disabledCombination != null) throw new PackDisabledException(disabledCombination);
            result.PackName = "混合内容包：" + string.Join("、", ordered.Select(p => p.PackName ?? p.PackId));
            result.PackNotice = string.Join("\n", ordered.Select(p => p.PackNotice)
                .Where(n => !string.IsNullOrEmpty(n)).Distinct(StringComparer.Ordinal));
            result.art = ArtManifest.Merge(result, ordered);
            result.Validate();
            return result;
        }

        static void ValidateMergeId(string id)
        {
            for (int i = 0; i < id.Length; i++)
            {
                if (char.IsControl(id[i])) throw new KeyError("合并内容包的 id 不能含控制字符。");
                if (char.IsHighSurrogate(id[i]))
                {
                    if (i + 1 >= id.Length || !char.IsLowSurrogate(id[i + 1]))
                        throw new KeyError("合并内容包的 id 必须是有效 Unicode。");
                    i++;
                }
                else if (char.IsLowSurrogate(id[i])) throw new KeyError("合并内容包的 id 必须是有效 Unicode。");
            }
        }

        static bool SamePolicy(PackPolicy left, PackPolicy right)
        {
            return left.DisabledPacks.SequenceEqual(right.DisabledPacks)
                && left.DisabledStreamers.SequenceEqual(right.DisabledStreamers)
                && left.Reasons.Count == right.Reasons.Count
                && left.Reasons.All(p => right.Reasons.TryGetValue(p.Key, out string value) && p.Value == value);
        }

        static bool SameTrackDisplay(ContentDb left, ContentDb right)
        {
            // 无障碍标记不改变旧录像指纹，但混合包不能静默采用某一来源的不同标记。
            return left.Types.Count == right.Types.Count && left.Types.All(p =>
                right.Types.TryGetValue(p.Key, out var type)
                && p.Value.Symbol == type.Symbol && p.Value.ColorAlt == type.ColorAlt);
        }

        static void CollectAliases(Dictionary<string, string> destination, Dictionary<string, string> source, string category)
        {
            foreach (var item in source)
            {
                string previous;
                if (destination.TryGetValue(item.Key, out previous) && !string.Equals(previous, item.Value, StringComparison.Ordinal))
                    throw new KeyError(category + "显示名冲突：" + item.Key);
                destination[item.Key] = item.Value;
            }
        }

        static void AddPrivate<T>(Dictionary<string, T> destination, Dictionary<string, T> source,
            Dictionary<string, T> rules, string category)
        {
            foreach (var item in source.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                if (rules.ContainsKey(item.Key)) continue;
                if (destination.ContainsKey(item.Key)) throw new KeyError(category + " id 冲突：" + item.Key);
                destination.Add(item.Key, item.Value);
            }
        }

        ContentDb RulesForMerge()
        {
            var copy = CopyContent();
            copy.PackId = copy.PackName = copy.PackNotice = null;
            copy.Streamers.Clear();
            copy.Seasons.Clear();
            copy.Statuses = RuleMembers(copy.Statuses, mergeRules.Statuses, "状态");
            copy.Fields = RuleMembers(copy.Fields, mergeRules.Fields, "直播间效果");
            copy.Personas = RuleMembers(copy.Personas, mergeRules.Personas, "人设");
            copy.Skills = RuleMembers(copy.Skills, mergeRules.Skills, "技能");
            foreach (var alias in mergeSkillAliases)
            {
                if (copy.Skills[alias.Key].Name != alias.Value) throw new KeyError("共用技能显示名已变更：" + alias.Key);
                copy.Skills[alias.Key].Name = mergeRules.Skills[alias.Key].Name;
            }
            foreach (var alias in mergeFieldAliases)
            {
                if (copy.Fields[alias.Key].Name != alias.Value) throw new KeyError("共用直播间效果显示名已变更：" + alias.Key);
                copy.Fields[alias.Key].Name = mergeRules.Fields[alias.Key].Name;
            }
            foreach (var alias in mergeStatusAliases)
            {
                if (copy.Statuses[alias.Key].Name != alias.Value) throw new KeyError("共用状态显示名已变更：" + alias.Key);
                copy.Statuses[alias.Key].Name = mergeRules.Statuses[alias.Key].Name;
            }
            return copy;
        }

        static Dictionary<string, T> RuleMembers<T>(Dictionary<string, T> current, Dictionary<string, T> baseline, string category)
        {
            var result = new Dictionary<string, T>();
            foreach (var id in baseline.Keys)
            {
                T value;
                if (!current.TryGetValue(id, out value)) throw new KeyError("共用规则缺少" + category + "：" + id);
                result.Add(id, value);
            }
            return result;
        }

        void WriteMergeHash(ContentHashWriter hash)
        {
            if (mergedSources == null) return;
            hash.Text("StreamDuel.Content.Mixed.v1");
            hash.Map(mergedSources, hash.Text);
            hash.Map(mergedNotices, hash.Text);
        }

        ContentDb CopyContent()
        {
            var copy = new ContentDb
            {
                PackId = PackId, PackName = PackName, PackNotice = PackNotice, Policy = Policy,
                StatNames = new Dictionary<string, string>(StatNames), CatNames = new Dictionary<string, string>(CatNames),
                Types = Types.ToDictionary(p => p.Key, p => p.Value.Copy()),
                Chart = Chart.ToDictionary(p => p.Key, p => new Dictionary<string, double>(p.Value)),
                Statuses = Statuses.ToDictionary(p => p.Key, p => CopyStatus(p.Value)),
                Fields = Fields.ToDictionary(p => p.Key, p => CopyField(p.Value)),
                Personas = Personas.ToDictionary(p => p.Key, p => CopyPersona(p.Value)),
                Skills = Skills.ToDictionary(p => p.Key, p => CopySkill(p.Value)), Struggle = CopySkill(Struggle),
                Streamers = Streamers.Select(s => new StreamerDef
                {
                    Id = s.Id, Name = s.Name, Type = s.Type, Type2 = s.Type2, Persona = s.Persona, Fifth = s.Fifth,
                    Blurb = s.Blurb, Org = s.Org, AiStyle = s.AiStyle,
                    Stats = new Dictionary<string, int>(s.Stats), Skills = new List<string>(s.Skills)
                }).ToList(),
                Seasons = Seasons.ToDictionary(p => p.Key, p => new SeasonDef
                {
                    Name = p.Value.Name, Banned = new List<string>(p.Value.Banned),
                    Limited = p.Value.Limited.Select(l => new LimitedPoolDef { Max = l.Max, Ids = new List<string>(l.Ids) }).ToList()
                })
            };
            copy.art = art?.CopyTo(copy);
            return copy;
        }

        static StatusDef CopyStatus(StatusDef s)
        {
            return new StatusDef
            {
                Id = s.Id, Name = s.Name, Cat = s.Cat, Desc = s.Desc, Skip = s.Skip, Turns = s.Turns,
                Dot = s.Dot, InteractMul = s.InteractMul, Leech = s.Leech, Cleanse = s.Cleanse, WakeOnHit = s.WakeOnHit
            };
        }

        static FieldDef CopyField(FieldDef f)
        {
            return new FieldDef
            {
                Id = f.Id, Name = f.Name, Kind = f.Kind, When = f.When, Desc = f.Desc,
                Dispel = f.Dispel, Consume = f.Consume, ImmuneControl = f.ImmuneControl, ImmuneWeak = f.ImmuneWeak,
                PreventSwitch = f.PreventSwitch, DmgTakenMul = f.DmgTakenMul, Heal = f.Heal, Dot = f.Dot,
                TalentMul = f.TalentMul, ReflectDamage = f.ReflectDamage, Prio = f.Prio, MaxStacks = f.MaxStacks
            };
        }

        static PersonaDef CopyPersona(PersonaDef p)
        {
            return new PersonaDef
            {
                Id = p.Id, Name = p.Name, Code = p.Code, Node = p.Node, Desc = p.Desc, Status = p.Status,
                Field = p.Field, PackDefined = p.PackDefined, Params = new Dictionary<string, double>(p.Params)
            };
        }

        static SkillDef CopySkill(SkillDef s)
        {
            if (s == null) return null;
            return new SkillDef
            {
                Id = s.Id, Name = s.Name, Type = s.Type, Cat = s.Cat, PackDefined = s.PackDefined,
                Power = s.Power, Pp = s.Pp, Acc = s.Acc, Prio = s.Prio,
                Effects = s.Effects.Select(e => new EffectDef
                {
                    At = e.At, Code = e.Code, Target = e.Target, Status = e.Status, Field = e.Field,
                    Chance = e.Chance, Turns = e.Turns, N = e.N, Frac = e.Frac, Mul = e.Mul, Per = e.Per,
                    Consume = e.Consume, Changes = new Dictionary<string, int>(e.Changes),
                    Cond = e.Cond == null ? null : new CondDef { Kind = e.Cond.Kind, Value = e.Cond.Value }
                }).ToList()
            };
        }
    }
}
