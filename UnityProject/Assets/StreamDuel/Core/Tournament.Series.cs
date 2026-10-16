using System;
using System.Collections.Generic;
using System.Linq;

namespace StreamDuel.Core
{
    public sealed partial class Tournament
    {
        Ruleset seriesRuleset;

        public bool HasRuleset { get { return seriesRuleset != null; } }
        public Ruleset Ruleset { get { return seriesRuleset?.Copy(); } }
        int MaximumGameIndex { get { return HasRuleset ? BestOf + 2 : 3; } }

        void InitializeSeries(Ruleset ruleset)
        {
            if (ruleset == null) return;
            if (!IsElimination && Format != "roundrobin")
                throw new ArgumentException("逐局赛制目前只用于单败、双败淘汰赛或循环赛。", nameof(ruleset));
            var copy = ruleset.Copy();
            copy.Validate(db);
            foreach (string value in new[] { copy.Id, copy.Name, copy.Season })
                if (value != null) TournamentJson.ValidateUnicode(value);
            seriesRuleset = copy;
            AutoTiebreak = true;
        }

        public string PendingDecision(Pairing pairing)
        {
            if (pairing == null) throw new ArgumentNullException(nameof(pairing), "对阵不能为空。");
            if (pairing.Id < 0 || pairing.Id >= pairings.Count || !ReferenceEquals(pairings[pairing.Id], pairing))
                throw new ArgumentException("这场对阵不属于本赛事。", nameof(pairing));
            if (!HasRuleset || pairing.IsBye || pairing.WinnerSide.HasValue || pairing.Games.Count != BestOf) return null;
            if (!IsElimination)
                return "需要决定：循环赛已打满规定局数但无人达到胜场门槛，平局或加赛的整场处理规则尚未确定；比分及各局记录已保留。";
            int first = pairing.Games.Count(game => game.WinnerSide == 0);
            int second = pairing.Games.Count(game => game.WinnerSide == 1);
            return first == second ? null
                : "需要决定：已打满规定局数，双方胜场不等但均未达到门槛，尚未明确按领先比分结束还是继续加赛；比分及各局记录已保留。";
        }

        void ApplySeriesGame(Pairing pairing, TournamentGame game)
        {
            int? winner = null;
            bool advancedBySeed = false;
            if (game.Attempt < BestOf)
            {
                int first = pairing.Games.Count(item => item.WinnerSide == 0) + (game.WinnerSide == 0 ? 1 : 0);
                int second = pairing.Games.Count(item => item.WinnerSide == 1) + (game.WinnerSide == 1 ? 1 : 0);
                if (first >= BestOf / 2 + 1) winner = 0;
                else if (second >= BestOf / 2 + 1) winner = 1;
            }
            else
            {
                // 规定局数同分后的决胜局沿用 T-118；不把顺位裁决写成某一局获胜。
                if (game.WinnerSide != -1) winner = game.WinnerSide;
                else if (game.Attempt == BestOf + 2)
                {
                    var seeds = EffectiveEliminationSeeds();
                    winner = Array.IndexOf(seeds, pairing.Player0) < Array.IndexOf(seeds, pairing.Player1) ? 0 : 1;
                    advancedBySeed = true;
                }
            }
            pairing.AddGame(game, winner, advancedBySeed);
        }

        void ValidateSeriesRecord(BattleRecord record)
        {
            if (record.BanOne != seriesRuleset.BanOne || record.LeadPick != seriesRuleset.LeadPick
                || !BattleModifiers.Normalize(record.Modifiers).SequenceEqual(BattleModifiers.Normalize(seriesRuleset.Modifiers)))
                throw new InvalidOperationException("对局的禁用、首发或修饰器与赛事赛制快照不一致。");
        }

        static Ruleset ReadSeriesRuleset(Dictionary<string, object> root)
        {
            var source = TournamentJson.Object(root["ruleset"]);
            TournamentJson.Keys(source, "id", "name", "season", "banOne", "leadPick", "bestOf", "modifiers", "turnSeconds", "bankSeconds");
            if (!(source["banOne"] is bool banOne) || !(source["leadPick"] is bool leadPick))
                throw new FormatException("赛制的禁用和首发字段必须为布尔值。");
            var ruleset = new Ruleset
            {
                Id = NullableRulesetText(source, "id"), Name = NullableRulesetText(source, "name"),
                Season = NullableRulesetText(source, "season"), BanOne = banOne, LeadPick = leadPick,
                BestOf = TournamentJson.Integer(source, "bestOf", 1, 5),
                TurnSeconds = TournamentJson.Integer(source, "turnSeconds", 0, int.MaxValue),
                BankSeconds = TournamentJson.Integer(source, "bankSeconds", 0, int.MaxValue)
            };
            if (source["modifiers"] != null)
            {
                ruleset.Modifiers = new List<string>();
                foreach (object value in TournamentJson.Array(source, "modifiers"))
                {
                    if (!(value is string id)) throw new FormatException("赛制修饰器必须是字符串数组。");
                    ruleset.Modifiers.Add(id);
                }
            }
            return ruleset;
        }

        static string NullableRulesetText(Dictionary<string, object> source, string key)
        {
            return source[key] == null ? null : TournamentJson.Text(source, key);
        }
    }
}
