using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace StreamDuel.Core
{
    public sealed class TournamentBan
    {
        public int Side { get; }
        public int TargetIndex { get; }
        public string StreamerId { get; }
        public uint RngState { get; }

        internal TournamentBan(int side, int targetIndex, string streamerId, uint rngState)
        {
            Side = side;
            TargetIndex = targetIndex;
            StreamerId = streamerId;
            RngState = rngState;
        }
    }

    public sealed partial class Pairing
    {
        readonly List<TournamentBan> bans = new List<TournamentBan>();
        public IReadOnlyList<TournamentBan> Bans { get { return bans.AsReadOnly(); } }
        internal void AddBan(TournamentBan ban) { bans.Add(ban); }
    }

    public sealed partial class Tournament
    {
        public int BanCount { get; }

        public int NextBanSide(Pairing pairing)
        {
            RequirePairing(pairing);
            if (pairing.IsBye || pairing.WinnerSide.HasValue || pairing.Bans.Count == BanCount * 2) return -1;
            int first = pairing.Player0 < pairing.Player1 ? 0 : 1;
            if (IsElimination)
            {
                var seeds = EffectiveEliminationSeeds();
                first = Array.IndexOf(seeds, pairing.Player0) < Array.IndexOf(seeds, pairing.Player1) ? 0 : 1;
            }
            return pairing.Bans.Count % 2 == 0 ? first : 1 - first;
        }

        public Battle CreateBattle(Pairing pairing)
        {
            RequirePairing(pairing);
            if (pairing.IsBye) throw new InvalidOperationException("轮空不创建对战。");
            if (db.ContentHash() != contentHash)
                throw new InvalidOperationException("赛事内容已改变，不能创建原赛事对战。");
            var first = players[pairing.Player0];
            var second = players[pairing.Player1];
            var battle = new Battle(db, new IList<string>[] { first.Team.ToArray(), second.Team.ToArray() },
                new[] { first.Name, second.Name }, new BattleOptions { Seed = PairingSeed(pairing), BanCount = BanCount });
            if (pairing.Bans.Count == 0) return battle;
            var record = battle.Record;
            foreach (var ban in pairing.Bans) record.Events.Add(BanEvent(ban));
            // AI 的选人也使用实战随机流，恢复其结算前位置才能与连续运行相同。
            return Replay.Run(db, record);
        }

        public void ChooseBan(Pairing pairing, int side, int foeIndex)
        {
            if (side < 0 || side > 1) throw new ArgumentOutOfRangeException(nameof(side), "禁用方必须为 0 或 1。");
            RequireBanTurn(pairing, side);
            var battle = CreateBattle(pairing);
            battle.ChooseBan(side, foeIndex);
            CaptureBan(pairing, battle);
        }

        public void AutoChooseBan(Pairing pairing)
        {
            int side = NextBanSide(pairing);
            RequireBanTurn(pairing, side);
            var battle = CreateBattle(pairing);
            battle.ChooseBan(side, Ai.ChooseBan(battle, side));
            CaptureBan(pairing, battle);
        }

        void RequirePairing(Pairing pairing)
        {
            if (pairing == null) throw new ArgumentNullException(nameof(pairing), "对阵不能为空。");
            if (pairing.Id < 0 || pairing.Id >= pairings.Count || !ReferenceEquals(pairings[pairing.Id], pairing))
                throw new ArgumentException("这场对阵不属于本赛事。", nameof(pairing));
        }

        void RequireBanTurn(Pairing pairing, int side)
        {
            int expected = NextBanSide(pairing);
            if (expected == -1) throw new InvalidOperationException("本场没有待确认的禁用选择。");
            if (side != expected) throw new InvalidOperationException("尚未轮到这一方禁用；按固定种子编号顺序交替确认。");
        }

        void CaptureBan(Pairing pairing, Battle battle)
        {
            var entry = battle.Record.Events.Last();
            string id = players[entry.Side == 0 ? pairing.Player1 : pairing.Player0].Team[entry.To];
            pairing.AddBan(new TournamentBan(entry.Side, entry.To, id, entry.RngState.Value));
        }

        static uint PairingSeed(Pairing pairing) { return unchecked(0x9E3779B9u + (uint)pairing.Id); }

        static BattleRecordEvent BanEvent(TournamentBan ban)
        {
            return new BattleRecordEvent { Type = BattleRecordEventType.Ban, Side = ban.Side, To = ban.TargetIndex, RngState = ban.RngState };
        }

        internal bool MatchesBanRecord(Pairing pairing, BattleRecord record)
        {
            if (record == null || record.Events.Any(entry => entry == null)) return false;
            int count = record.BanCount > 0 ? record.BanCount : record.BanOne ? 1 : 0;
            if (count != BanCount || record.Events.Count(entry => entry.Type == BattleRecordEventType.Ban) != BanCount * 2)
                return false;
            if (BanCount == 0) return true;
            if (pairing.Bans.Count != BanCount * 2 || record.Seed != PairingSeed(pairing) || record.LeadPick
                || record.Modifiers != null && record.Modifiers.Count > 0 || record.Events.Count < pairing.Bans.Count)
                return false;
            for (int i = 0; i < pairing.Bans.Count; i++)
            {
                var saved = pairing.Bans[i];
                var entry = record.Events[i];
                if (entry.Type != BattleRecordEventType.Ban || entry.Side != saved.Side || entry.To != saved.TargetIndex
                    || entry.RngState != saved.RngState) return false;
            }
            return true;
        }

        internal static void WriteBans(StringBuilder text, Pairing pairing, ContentDb names = null)
        {
            text.Append(",\"bans\":[");
            for (int i = 0; i < pairing.Bans.Count; i++)
            {
                if (i > 0) text.Append(',');
                var ban = pairing.Bans[i];
                text.Append("{\"side\":").Append(Number(ban.Side)).Append(",\"targetIndex\":").Append(Number(ban.TargetIndex))
                    .Append(",\"streamerId\":");
                TeamPresetJson.WriteString(text, ban.StreamerId);
                if (names != null)
                {
                    text.Append(",\"streamerName\":");
                    TeamPresetJson.WriteString(text, names.Streamer(ban.StreamerId).Name);
                }
                text.Append(",\"rng\":").Append(ban.RngState.ToString(CultureInfo.InvariantCulture)).Append('}');
            }
            text.Append(']');
        }

        void ReadBans(Pairing pairing, List<object> saved)
        {
            if (saved.Count > BanCount * 2 || pairing.IsBye && saved.Count != 0)
                throw new FormatException("本场禁用人数与赛制不符，轮空不能禁用。");
            foreach (var raw in saved)
            {
                var value = TournamentJson.Object(raw);
                TournamentJson.Keys(value, "side", "targetIndex", "streamerId", "rng");
                int side = TournamentJson.Integer(value, "side", 0, 1);
                int target = TournamentJson.Integer(value, "targetIndex", 0, 5);
                string id = TournamentJson.Text(value, "streamerId");
                if (NextBanSide(pairing) != side || id != players[side == 0 ? pairing.Player1 : pairing.Player0].Team[target])
                    throw new FormatException("本场禁用顺序或主播与原始报名名单不符。");
                if (!(value["rng"] is double rng) || double.IsNaN(rng) || double.IsInfinity(rng)
                    || rng < 0 || rng > uint.MaxValue || rng != Math.Truncate(rng))
                    throw new FormatException("禁用随机流位置必须为无符号整数。");
                var ban = new TournamentBan(side, target, id, (uint)rng);
                var prefix = CreateBattle(pairing).Record;
                prefix.Events.Add(BanEvent(ban));
                Replay.Run(db, prefix);
                pairing.AddBan(ban);
            }
        }
    }
}
