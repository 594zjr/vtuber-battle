using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace StreamDuel.Core
{
    public sealed class TournamentGame
    {
        public int Attempt { get; }
        public uint Seed { get; }
        public int WinnerSide { get; }
        public string RecordHash { get; }

        internal TournamentGame(int attempt, uint seed, int winnerSide, string recordHash)
        {
            Attempt = attempt;
            Seed = seed;
            WinnerSide = winnerSide;
            RecordHash = recordHash;
        }
    }

    public sealed partial class Tournament
    {
        public uint GameSeed(Pairing pairing, int attempt)
        {
            RequireGamePairing(pairing);
            if (attempt < 0 || attempt > 3) throw new ArgumentOutOfRangeException(nameof(attempt), "对局序号只能是原局 0 或加赛 1～3。");
            unchecked
            {
                uint value = (Seed ?? 0) ^ (uint)pairing.Id * 0x9E3779B9u ^ (uint)attempt * 0x85EBCA6Bu ^ 0x54423138u;
                value ^= value >> 16;
                value *= 0x7FEB352Du;
                value ^= value >> 15;
                value *= 0x846CA68Bu;
                return value ^ (value >> 16);
            }
        }

        public Battle CreateGame(Pairing pairing)
        {
            RequirePendingGame(pairing);
            if (db.PackId != packId || db.ContentHash() != contentHash)
                throw new InvalidOperationException("赛事内容已改变，不能创建后续对局。");
            var first = players[pairing.Player0];
            var second = players[pairing.Player1];
            return new Battle(db, new IList<string>[] { first.Team.ToList(), second.Team.ToList() },
                new[] { first.Name, second.Name }, GameSeed(pairing, pairing.Games.Count));
        }

        public void ReportGame(Pairing pairing, Battle battle)
        {
            RequirePendingGame(pairing);
            int attempt = pairing.Games.Count;
            string recordHash = ValidateGame(pairing, battle, attempt);
            ApplyTournamentGame(pairing, new TournamentGame(attempt, GameSeed(pairing, attempt), battle.Winner, recordHash));
        }

        internal string ValidateGame(Pairing pairing, Battle battle, int attempt)
        {
            if (battle == null) throw new ArgumentNullException(nameof(battle), "对战不能为空。");
            if (battle.Phase != Phase.Over || battle.Winner < -1 || battle.Winner > 1)
                throw new InvalidOperationException("只有完整结束且结果有效的对局才能登记。");
            var replayed = ReplayGame(pairing, battle.Record, attempt);
            if (battle.Db.PackId != packId || battle.Db.ContentHash() != contentHash || battle.Seed != replayed.Seed
                || replayed.Winner != battle.Winner || replayed.StateHash() != battle.StateHash())
                throw new InvalidOperationException("对局内容、种子、状态或胜负与完整录像重放不一致，不能登记。");
            return TournamentRecordHash(battle.Record.ToJson());
        }

        internal Battle ReplayGame(Pairing pairing, BattleRecord record, int attempt)
        {
            RequireGamePairing(pairing);
            if (!AutoTiebreak) throw new InvalidOperationException("此赛事未开启自动加赛。");
            uint seed = GameSeed(pairing, attempt);
            if (attempt > pairing.Games.Count || attempt == pairing.Games.Count && pairing.WinnerSide.HasValue)
                throw new InvalidOperationException("只能登记下一局，或核对已经登记的赛事对局。");
            var first = players[pairing.Player0];
            var second = players[pairing.Player1];
            if (record == null || record.PackId != packId || record.ContentHash != contentHash
                || db.PackId != packId || db.ContentHash() != contentHash)
                throw new InvalidOperationException("对局内容包或指纹与赛事不一致。");
            if (record.Seed != seed)
                throw new InvalidOperationException("对局种子与本场次及加赛序号不一致。");
            if (record.IsBranch)
                throw new InvalidOperationException("从录像接手的对局不能登记为正式赛事原局或加赛。");
            if (record.Teams == null || record.Teams.Length != 2 || record.Teams[0] == null || record.Teams[1] == null
                || !record.Teams[0].SequenceEqual(first.Team, StringComparer.Ordinal)
                || !record.Teams[1].SequenceEqual(second.Team, StringComparer.Ordinal)
                || record.Names == null || record.Names.Length != 2 || record.Names[0] != first.Name || record.Names[1] != second.Name)
                throw new InvalidOperationException("对局的原始队伍、顺序或选手姓名与本场对阵不一致。");
            if (record.LeadPick || record.BanOne || record.Modifiers != null && record.Modifiers.Count != 0)
                throw new InvalidOperationException("赛事对局不能更改默认首发、禁用或规则修饰器配置。");
            var compatibility = Replay.Check(db, record);
            if (!compatibility.CanReplay) throw new InvalidOperationException(compatibility.Reason);
            string json = record.ToJson();
            var replayed = Replay.Run(db, BattleRecord.FromJson(json));
            if (replayed.Phase != Phase.Over || replayed.Winner < -1 || replayed.Winner > 1)
                throw new InvalidOperationException("录像尚未完整结束或胜负无效，不能登记。");
            // 历史摘要绑定实际保存的记录；重放会补齐可选检查点，不能改用重生成的 Record。
            string recordHash = TournamentRecordHash(json);
            if (attempt < pairing.Games.Count)
            {
                var game = pairing.Games[attempt];
                if (game.Seed != seed || game.WinnerSide != replayed.Winner || game.RecordHash != recordHash)
                    throw new InvalidOperationException("录像与该场已经登记的单局记录不一致。");
            }
            return replayed;
        }

        void RequireGamePairing(Pairing pairing)
        {
            if (pairing == null) throw new ArgumentNullException(nameof(pairing), "对阵不能为空。");
            if (pairing.Id < 0 || pairing.Id >= pairings.Count || !ReferenceEquals(pairings[pairing.Id], pairing))
                throw new ArgumentException("这场对阵不属于本赛事。", nameof(pairing));
            if (pairing.IsBye) throw new InvalidOperationException("轮空没有实际对局或加赛。");
        }

        void RequirePendingGame(Pairing pairing)
        {
            RequireGamePairing(pairing);
            if (!AutoTiebreak) throw new InvalidOperationException("此赛事未开启自动加赛，请沿用手工报分方式。");
            if (pairing.WinnerSide.HasValue) throw new InvalidOperationException("这场对阵已经决定晋级，不能再登记对局。");
        }

        void ApplyTournamentGame(Pairing pairing, TournamentGame game)
        {
            RequirePendingGame(pairing);
            if (game.Attempt != pairing.Games.Count || game.Seed != GameSeed(pairing, game.Attempt)
                || game.WinnerSide < -1 || game.WinnerSide > 1 || !ValidTournamentRecordHash(game.RecordHash))
                throw new FormatException("赛事单局的序号、种子、胜负或录像摘要无效。");
            int? winner = game.WinnerSide == -1 ? (int?)null : game.WinnerSide;
            bool advancedBySeed = game.WinnerSide == -1 && game.Attempt == 3;
            if (advancedBySeed)
            {
                var seeds = EffectiveEliminationSeeds();
                winner = Array.IndexOf(seeds, pairing.Player0) < Array.IndexOf(seeds, pairing.Player1) ? 0 : 1;
            }
            pairing.AddGame(game, winner, advancedBySeed);
        }

        static bool ValidTournamentRecordHash(string hash)
        {
            return hash != null && hash.Length == 64 && hash.All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f');
        }

        internal static string TournamentRecordHash(string json)
        {
            using (var sha = SHA256.Create())
            {
                var result = new StringBuilder(64);
                foreach (byte value in sha.ComputeHash(Encoding.UTF8.GetBytes(json)))
                    result.Append(value.ToString("x2", CultureInfo.InvariantCulture));
                return result.ToString();
            }
        }

        static void WriteTiebreakGames(StringBuilder text, Pairing pairing)
        {
            text.Append(",\"games\":[");
            for (int i = 0; i < pairing.Games.Count; i++)
            {
                if (i > 0) text.Append(',');
                var game = pairing.Games[i];
                text.Append("{\"attempt\":").Append(Number(game.Attempt))
                    .Append(",\"seed\":").Append(game.Seed.ToString(CultureInfo.InvariantCulture))
                    .Append(",\"winnerSide\":").Append(Number(game.WinnerSide)).Append(",\"recordHash\":");
                TeamPresetJson.WriteString(text, game.RecordHash);
                text.Append('}');
            }
            text.Append("],\"advancedBySeed\":").Append(pairing.AdvancedBySeed ? "true" : "false");
        }

        void ReadTiebreakGames(Dictionary<string, object> saved, Pairing pairing)
        {
            foreach (var value in TournamentJson.Array(saved, "games"))
            {
                var game = TournamentJson.Object(value);
                TournamentJson.Keys(game, "attempt", "seed", "winnerSide", "recordHash");
                if (!(game["seed"] is double seed) || double.IsNaN(seed) || double.IsInfinity(seed)
                    || seed < 0 || seed > uint.MaxValue || seed != Math.Truncate(seed))
                    throw new FormatException("单局种子必须为无符号 32 位整数。");
                ApplyTournamentGame(pairing, new TournamentGame(TournamentJson.Integer(game, "attempt", 0, 3), (uint)seed,
                    TournamentJson.Integer(game, "winnerSide", -1, 1), TournamentJson.Text(game, "recordHash")));
            }
            if (!(saved["advancedBySeed"] is bool advanced) || advanced != pairing.AdvancedBySeed)
                throw new FormatException("按种子晋级标记与单局历史不一致。");
            int? winner = saved["winnerSide"] == null ? (int?)null : TournamentJson.Integer(saved, "winnerSide", -1, 1);
            if (winner != pairing.WinnerSide) throw new FormatException("存档晋级结果与单局历史不一致。");
        }
    }
}
