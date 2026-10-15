using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace StreamDuel.Core
{
    public sealed class ReplayDiffResult
    {
        public bool SameOpening { get; internal set; }
        public IReadOnlyList<string> OpeningDifferences { get; internal set; } = Array.Empty<string>();
        public int? FirstActionDifferenceTurn { get; internal set; }
        public int? FirstStateDifferenceTurn { get; internal set; }
        public int? EndDifferenceTurn { get; internal set; }
        public int? ShorterSide { get; internal set; }
        public int TurnsA { get; internal set; }
        public int TurnsB { get; internal set; }
        public IReadOnlyList<string> Differences { get; internal set; } = Array.Empty<string>();
        public IReadOnlyList<string> Warnings { get; internal set; } = Array.Empty<string>();
        public ulong? StateHashA { get; internal set; }
        public ulong? StateHashB { get; internal set; }
    }

    /// <summary>在当前引擎与同一内容库下重放，不推断历史引擎的输出。</summary>
    public static class ReplayDiff
    {
        /// <summary>
        /// 缺失记录、版本及基本开局结构先校验；开局不同时直接返回，不执行后续事件。
        /// 开局相同后沿用 Replay.Check 门禁，只有内容指纹变化可尽力重放。
        /// 位置 0 包含首个 Turn 前的事件，位置 N 包含第 N 个 Turn 及下个 Turn 前的后缀。
        /// 缺失整回合只报告长度与行动差，不用较短录像的旧终态冒充缺失位置的状态。
        /// </summary>
        public static ReplayDiffResult Compare(ContentDb db, BattleRecord a, BattleRecord b)
        {
            CheckHeader(a);
            CheckHeader(b);
            var opening = CompareOpening(a, b);
            var result = new ReplayDiffResult
            {
                SameOpening = opening.Count == 0,
                OpeningDifferences = opening.AsReadOnly(),
                TurnsA = a.Events.Count(entry => entry != null && entry.Type == BattleRecordEventType.Turn),
                TurnsB = b.Events.Count(entry => entry != null && entry.Type == BattleRecordEventType.Turn)
            };
            if (!result.SameOpening) return result;

            var warnings = new List<string>
            {
                "对比使用当前引擎和当前内容重新执行，不代表历史引擎输出。"
            };
            CheckCompatibility(db, a, "录像1", warnings);
            CheckCompatibility(db, b, "录像2", warnings);
            var first = new Playback(db, a);
            var second = new Playback(db, b);
            var differences = new List<string>();
            SetEndDifference(first, second, result);

            for (int turn = 0; turn <= Math.Max(result.TurnsA, result.TurnsB); turn++)
            {
                bool hasFirst = turn <= result.TurnsA;
                bool hasSecond = turn <= result.TurnsB;
                if (hasFirst) first.Apply(turn, "录像1");
                if (hasSecond) second.Apply(turn, "录像2");
                if (!result.FirstActionDifferenceTurn.HasValue)
                {
                    string action = ActionDifference(first, second, turn, hasFirst, hasSecond);
                    if (action != null)
                    {
                        result.FirstActionDifferenceTurn = turn;
                        differences.Add(TurnLabel(turn) + action);
                    }
                }
                if (hasFirst && hasSecond && !result.FirstStateDifferenceTurn.HasValue)
                {
                    ulong hashA = first.Current.StateHash(), hashB = second.Current.StateHash();
                    if (hashA != hashB)
                    {
                        result.FirstStateDifferenceTurn = turn;
                        result.StateHashA = hashA;
                        result.StateHashB = hashB;
                        AddStateDifferences(differences, first.Current, second.Current, turn, hashA, hashB);
                    }
                }
            }
            if (result.EndDifferenceTurn.HasValue)
            {
                int side = result.ShorterSide.Value;
                int completed = side == 0 ? result.TurnsA : result.TurnsB;
                string missing = result.TurnsA == result.TurnsB ? "缺少本位置的末尾事件" : "缺少此回合及后续记录";
                differences.Add(TurnLabel(result.EndDifferenceTurn.Value) + "录像" + (side + 1).ToString(CultureInfo.InvariantCulture)
                    + " 已在完成 " + completed.ToString(CultureInfo.InvariantCulture) + " 个行动回合后结束，" + missing + "。");
            }
            result.Differences = differences.AsReadOnly();
            result.Warnings = warnings.AsReadOnly();
            return result;
        }

        static void CheckHeader(BattleRecord record)
        {
            if (record == null || record.Version < 1 || record.Version > BattleRecord.CurrentVersion)
                throw new InvalidOperationException(Replay.Check(null, record).Reason);
            if (string.IsNullOrWhiteSpace(record.PackId)) throw new InvalidOperationException("录像缺少内容包编号。");
            if (record.Teams == null || record.Teams.Length != 2) throw new InvalidOperationException("录像必须包含两支队伍。");
            foreach (var team in record.Teams)
            {
                if (team == null || team.Count == 0) throw new InvalidOperationException("录像队伍不能为空。");
                if (record.BanOne && team.Count < 2) throw new InvalidOperationException("录像启用了禁用，但队伍不足两位主播。");
                if (team.Any(string.IsNullOrEmpty)) throw new InvalidOperationException("录像队伍包含空的主播编号。");
            }
            if (record.Names != null && (record.Names.Length != 2 || record.Names.Any(name => name == null)))
                throw new InvalidOperationException("录像必须包含两个有效的玩家名称。");
            try { BattleModifiers.Normalize(record.Modifiers); }
            catch (KeyError error)
            {
                throw new InvalidOperationException("录像所需的内容或结构无效：" + error.Message, error);
            }
        }

        static List<string> CompareOpening(BattleRecord a, BattleRecord b)
        {
            var differences = new List<string>();
            if (!string.Equals(a.PackId, b.PackId, StringComparison.Ordinal)) differences.Add("开局不同：内容包。");
            if (a.Seed != b.Seed) differences.Add("开局不同：种子。");
            for (int side = 0; side < 2; side++)
                if (!a.Teams[side].SequenceEqual(b.Teams[side], StringComparer.Ordinal))
                    differences.Add("开局不同：第" + side.ToString(CultureInfo.InvariantCulture) + "方队伍或排列顺序。");
            if (a.LeadPick != b.LeadPick) differences.Add("开局不同：首发选择开关。");
            if (a.BanOne != b.BanOne) differences.Add("开局不同：开局禁用开关。");
            if (!BattleModifiers.Normalize(a.Modifiers).SequenceEqual(BattleModifiers.Normalize(b.Modifiers), StringComparer.Ordinal))
                differences.Add("开局不同：规则修饰器。");
            return differences;
        }

        static void CheckCompatibility(ContentDb db, BattleRecord record, string label, List<string> warnings)
        {
            var compatibility = Replay.Check(db, record);
            if (!compatibility.CanReplay) throw new InvalidOperationException(label + "：" + compatibility.Reason);
            if (!string.IsNullOrEmpty(compatibility.Reason)) warnings.Add(label + "：" + compatibility.Reason);
        }

        static void SetEndDifference(Playback a, Playback b, ReplayDiffResult result)
        {
            if (result.TurnsA != result.TurnsB)
            {
                result.EndDifferenceTurn = Math.Min(result.TurnsA, result.TurnsB) + 1;
                result.ShorterSide = result.TurnsA < result.TurnsB ? 0 : 1;
                return;
            }
            int turn = result.TurnsA;
            int countA = a.Count(turn), countB = b.Count(turn);
            if (countA == countB) return;
            for (int i = 0; i < Math.Min(countA, countB); i++)
                if (!SameAction(a.Entry(turn, i), b.Entry(turn, i))) return;
            result.EndDifferenceTurn = turn;
            result.ShorterSide = countA < countB ? 0 : 1;
        }

        static string ActionDifference(Playback a, Playback b, int turn, bool hasA, bool hasB)
        {
            if (!hasA || !hasB) return "行动记录不同：录像" + (hasA ? "2" : "1") + "缺少此回合。";
            int countA = a.Count(turn), countB = b.Count(turn);
            int i = 0;
            while (i < Math.Min(countA, countB) && SameAction(a.Entry(turn, i), b.Entry(turn, i))) i++;
            if (i == countA && i == countB) return null;
            return "行动记录第 " + (i + 1).ToString(CultureInfo.InvariantCulture) + " 项不同：录像1="
                + (i < countA ? ActionText(a.Entry(turn, i)) : "缺少记录") + "；录像2="
                + (i < countB ? ActionText(b.Entry(turn, i)) : "缺少记录") + "。";
        }

        static bool SameAction(BattleRecordEvent a, BattleRecordEvent b)
        {
            if (a.Type != b.Type) return false;
            switch (a.Type)
            {
                case BattleRecordEventType.Turn: return SameChoice(a.A0, b.A0) && SameChoice(a.A1, b.A1);
                case BattleRecordEventType.Replace:
                case BattleRecordEventType.Lead:
                case BattleRecordEventType.Ban: return a.Side == b.Side && a.To == b.To;
                case BattleRecordEventType.DrawResponse: return a.Side == b.Side && a.Accepted == b.Accepted;
                default: return a.Side == b.Side;
            }
        }

        static bool SameChoice(BattleAction a, BattleAction b)
        {
            return a.Type == b.Type && (a.Type != ActionType.Skill && a.Type != ActionType.Switch || a.Index == b.Index);
        }

        static string ActionText(BattleRecordEvent entry)
        {
            if (entry.Type == BattleRecordEventType.Turn) return "第0方 " + ChoiceText(entry.A0) + "，第1方 " + ChoiceText(entry.A1);
            string side = entry.Side == -1 ? "双方" : "第" + entry.Side.ToString(CultureInfo.InvariantCulture) + "方";
            switch (entry.Type)
            {
                case BattleRecordEventType.Replace: return side + "补位到下标 " + entry.To.ToString(CultureInfo.InvariantCulture);
                case BattleRecordEventType.Lead: return side + "首发下标 " + entry.To.ToString(CultureInfo.InvariantCulture);
                case BattleRecordEventType.Ban: return side + "禁用下标 " + entry.To.ToString(CultureInfo.InvariantCulture);
                case BattleRecordEventType.Surrender: return side + "投降";
                case BattleRecordEventType.DrawOffer: return side + "提议和局";
                case BattleRecordEventType.DrawResponse: return side + (entry.Accepted ? "接受和局" : "拒绝和局");
                default: return entry.Type.ToString();
            }
        }

        static string ChoiceText(BattleAction action)
        {
            switch (action.Type)
            {
                case ActionType.Skill: return "技能[" + action.Index.ToString(CultureInfo.InvariantCulture) + "]";
                case ActionType.Switch: return "换人[" + action.Index.ToString(CultureInfo.InvariantCulture) + "]";
                case ActionType.Pass: return "跳过";
                case ActionType.PassControlled: return "受控跳过";
                case ActionType.Struggle: return "硬撑";
                case ActionType.Surrender: return "投降";
                default: return "原始行动类型 " + ((int)action.Type).ToString(CultureInfo.InvariantCulture);
            }
        }

        static void AddStateDifferences(List<string> differences, Battle a, Battle b, int turn, ulong hashA, ulong hashB)
        {
            var stateA = a.ReplayDiffStateValues();
            var stateB = b.ReplayDiffStateValues();
            differences.Add(TurnLabel(turn) + "状态哈希不同：录像1=" + hashA.ToString("x16", CultureInfo.InvariantCulture)
                + "；录像2=" + hashB.ToString("x16", CultureInfo.InvariantCulture) + "。");
            foreach (string key in stateA.Keys.Union(stateB.Keys, StringComparer.Ordinal).OrderBy(key => key, StringComparer.Ordinal))
            {
                bool hasA = stateA.TryGetValue(key, out string valueA), hasB = stateB.TryGetValue(key, out string valueB);
                if (hasA == hasB && string.Equals(valueA, valueB, StringComparison.Ordinal)) continue;
                differences.Add(TurnLabel(turn) + key + "：录像1=" + (hasA ? valueA : "不存在") + "；录像2=" + (hasB ? valueB : "不存在") + "。");
            }
        }

        static string TurnLabel(int turn) { return "第 " + turn.ToString(CultureInfo.InvariantCulture) + " 回合："; }

        sealed class Playback
        {
            readonly BattleRecordEvent[] events;
            readonly int[] boundaries;
            public Battle Current { get; }

            public Playback(ContentDb db, BattleRecord record)
            {
                var header = new BattleRecord
                {
                    Version = record.Version, ContentHash = record.ContentHash, Seed = record.Seed, PackId = record.PackId,
                    LeadPick = record.LeadPick, BanOne = record.BanOne,
                    Names = record.Names == null ? null : (string[])record.Names.Clone(),
                    Teams = record.Teams.Select(team => (IList<string>)new List<string>(team)).ToArray(),
                    Modifiers = record.Modifiers == null ? null : new List<string>(record.Modifiers)
                };
                events = record.Events.Select(entry => new BattleRecordEvent
                {
                    Type = entry.Type, A0 = entry.A0, A1 = entry.A1, Side = entry.Side, To = entry.To,
                    Accepted = entry.Accepted, RngState = entry.RngState
                }).ToArray();
                var positions = new List<int> { 0 };
                for (int i = 0; i < events.Length; i++)
                    if (events[i].Type == BattleRecordEventType.Turn) positions.Add(i);
                positions.Add(events.Length);
                boundaries = positions.ToArray();
                // 只重放独立的空事件头部；完整记录的硬门禁已经在构造前通过。
                Current = Replay.RunBestEffort(db, header);
            }

            public int Count(int turn) { return boundaries[turn + 1] - boundaries[turn]; }
            public BattleRecordEvent Entry(int turn, int index) { return events[boundaries[turn] + index]; }

            public void Apply(int turn, string label)
            {
                for (int i = boundaries[turn]; i < boundaries[turn + 1]; i++)
                {
                    try { Replay.ApplyEvent(Current, events[i]); }
                    catch (Exception error) when (error is InvalidOperationException || error is ArgumentException || error is FormatException
                        || error is KeyError || error is KeyNotFoundException || error is IndexOutOfRangeException || error is OverflowException)
                    {
                        throw new InvalidOperationException(label + "在第 " + turn.ToString(CultureInfo.InvariantCulture)
                            + " 回合的事件 " + (i + 1).ToString(CultureInfo.InvariantCulture) + " 无法重放：" + error.Message, error);
                    }
                }
            }
        }
    }

    public sealed partial class Battle
    {
        // 和 StateHash 采用同一字段范围；私有选择与协商冷却也须解释，不能只展示血量。
        internal Dictionary<string, string> ReplayDiffStateValues()
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            void Add(string key, object value)
            {
                values.Add(key, value == null ? "null" : value is bool flag ? flag ? "是" : "否"
                    : value is IFormattable number ? number.ToString(null, CultureInfo.InvariantCulture) : value.ToString());
            }
            Add("待行动回合 Turn", Turn);
            Add("对战阶段 Phase", Phase);
            Add("胜方 Winner", Winner);
            Add("随机状态 RNG", rng.State);
            Add("内容包", Db.PackId);
            Add("禁用后选择首发", leadPickAfterBan);
            Add("规则修饰器数量", modifiers.Count);
            for (int i = 0; i < modifiers.Count; i++) Add("规则修饰器[" + i.ToString(CultureInfo.InvariantCulture) + "]", modifiers[i]);
            for (int side = 0; side < Sides.Length; side++)
            {
                string prefix = "第" + side.ToString(CultureInfo.InvariantCulture) + "方";
                Add(prefix + "场上位置 active", Sides[side].Active);
                Add(prefix + "需要补位", Need[side]);
                Add(prefix + "先手标记", ActedFirst[side]);
                Add(prefix + "首发选择", leadChoices[side]);
                Add(prefix + "禁用选择", banChoices[side]);
                Add(prefix + "待答和局提议", drawOffers[side]);
                Add(prefix + "上次和局提议回合", lastDrawOfferTurns[side]);
                Add(prefix + "队员数量", Sides[side].Team.Count);
                for (int i = 0; i < Sides[side].Team.Count; i++)
                {
                    var fighter = Sides[side].Team[i];
                    string position = prefix + "第" + (i + 1).ToString(CultureInfo.InvariantCulture) + "号位";
                    Add(position + "主播 id", fighter.Def.Id);
                    string member = position + " [" + fighter.Def.Id + "] ";
                    Add(member + "人气 HP", fighter.Hp);
                    Add(member + "最大人气", fighter.MaxHp);
                    Add(member + "下播", fighter.Fainted);
                    Add(member + "所属方", fighter.Side);
                    Add(member + "实际号位", fighter.Slot);
                    Add(member + "上次技能下标", fighter.LastSkillIndex);
                    Add(member + "登场回合", fighter.EnteredTurn);
                    Add(member + "首次控制免疫已用", fighter.FirstControlImmunityUsed);
                    Add(member + "保命已用", fighter.EndureUsed);
                    foreach (string stat in ContentDb.StatKeys) Add(member + "能力 " + stat, fighter.Stages[stat]);
                    Add(member + "技能数量", fighter.Skills.Count);
                    for (int k = 0; k < fighter.Skills.Count; k++)
                    {
                        var skill = fighter.Skills[k];
                        string key = member + "技能[" + k.ToString(CultureInfo.InvariantCulture) + "]";
                        Add(key + ".id", skill.Id);
                        Add(key + ".PP", skill.Pp);
                        Add(key + ".最大PP", skill.Max);
                        Add(key + ".第五技能", skill.Fifth);
                    }
                    Add(member + "状态数量", fighter.Statuses.Count);
                    for (int k = 0; k < fighter.Statuses.Count; k++)
                    {
                        var status = fighter.Statuses[k];
                        string key = member + "状态[" + k.ToString(CultureInfo.InvariantCulture) + "]";
                        Add(key + ".id", status.Id);
                        Add(key + ".剩余", status.Left);
                    }
                    Add(member + "直播间效果数量", fighter.Fields.Count);
                    for (int k = 0; k < fighter.Fields.Count; k++)
                    {
                        var field = fighter.Fields[k];
                        string key = member + "直播间效果[" + k.ToString(CultureInfo.InvariantCulture) + "]";
                        Add(key + ".id", field.Id);
                        Add(key + ".回合", field.Turns);
                        Add(key + ".层数", field.Stacks);
                    }
                }
            }
            return values;
        }
    }
}
