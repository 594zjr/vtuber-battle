using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace StreamDuel.Core
{
    public sealed class TournamentReport
    {
        sealed class Match
        {
            public Pairing Pairing;
            public string Name0, Name1, Status, ReplayId, Message;
            public readonly List<Game> Games = new List<Game>();
        }

        sealed class Game
        {
            public TournamentGame Source;
            public string Status, ReplayId, Message;
        }

        sealed class StreamerTotal
        {
            public string Id, Name;
            public long Kos, Appearances;
        }

        sealed class DamageBest
        {
            public int Amount, Pairing, Round, Turn, LogIndex, Side, Slot;
            public string Id, Name, ReplayId;
        }

        readonly string text;
        readonly string json;

        TournamentReport(string text, string json)
        {
            this.text = text;
            this.json = json;
        }

        public string ToText() { return text; }
        public string ToJson() { return json; }

        public static TournamentReport Build(Tournament tournament, ReplayLibrary library)
        {
            if (tournament == null) throw new ArgumentNullException(nameof(tournament));
            var db = tournament.ReportDb;
            if (db.ContentHash() != tournament.ReportContentHash)
                throw new InvalidOperationException("赛事内容已改变，不能按当前内容生成原赛事的数据之最。");
            var saved = library == null ? new List<TournamentReplaySnapshot>()
                : library.ReadTournamentReplays(tournament).ToList();
            var byPairing = saved.GroupBy(item => item.PairingId).ToDictionary(group => group.Key, group => group.ToList());
            var matches = new List<Match>();
            var totals = new Dictionary<string, StreamerTotal>(StringComparer.Ordinal);
            DamageBest damage = null;
            int completed = 0, archived = 0, missing = 0, invalid = 0;
            foreach (var pairing in tournament.Pairings.OrderBy(item => item.Id))
            {
                var first = tournament.Players[pairing.Player0];
                var second = pairing.IsBye ? null : tournament.Players[pairing.Player1];
                var match = new Match
                {
                    Pairing = pairing, Name0 = PlayerName(first.Name), Name1 = second == null ? null : PlayerName(second.Name)
                };
                matches.Add(match);
                if (pairing.IsBye) { SetStatus(match, "not_applicable", "轮空，无录像。"); continue; }
                byPairing.TryGetValue(pairing.Id, out var candidates);
                if (tournament.AutoTiebreak)
                {
                    foreach (var game in pairing.Games)
                    {
                        completed++;
                        var row = ReadGame(tournament, pairing, game, candidates, out var gameBattle);
                        match.Games.Add(row);
                        if (row.Status == "not_saved") missing++;
                        else if (gameBattle == null) invalid++;
                        else
                        {
                            archived++;
                            Accumulate(gameBattle, pairing, row.ReplayId, totals, ref damage);
                        }
                    }
                    if (match.Games.Count == 0) SetStatus(match, "pending", "待赛，尚无已确认单局结果。");
                    else if (match.Games.All(game => game.Status == "saved"))
                        SetStatus(match, "saved", "已登记的每局均已保存并严格复算。" + (pairing.WinnerSide.HasValue ? "" : "尚待加赛。"));
                    else if (match.Games.All(game => game.Status == "not_saved"))
                        SetStatus(match, "not_saved", "已登记的各局均未找到归档录像。");
                    else SetStatus(match, "partial", "部分单局录像缺失或不可用，详见逐局记录。");
                    continue;
                }
                if (candidates != null && candidates.Count == 1 && candidates[0].Entry != null)
                    match.ReplayId = candidates[0].Entry.Id;
                if (!pairing.WinnerSide.HasValue) { SetStatus(match, "pending", "待赛，尚无已确认结果。"); continue; }
                completed++;
                if (candidates == null || candidates.Count == 0)
                {
                    SetStatus(match, "not_saved", "未保存（没有找到本赛事此对阵的归档录像）。");
                    missing++;
                    continue;
                }
                if (candidates.Count != 1)
                {
                    SetStatus(match, "ambiguous", "同一对阵有多份关联录像，未选择其中任何一份。");
                    invalid++;
                    continue;
                }
                var candidate = candidates[0];
                if (candidate.Entry == null || candidate.Entry.IsCorrupt)
                {
                    SetStatus(match, "corrupt", "关联录像损坏或版本不受支持，未计入数据之最。");
                    invalid++;
                    continue;
                }
                if (candidate.Entry.IsUnavailable)
                {
                    SetStatus(match, "unavailable", "关联录像含已下架内容，不能重放或计入数据之最。");
                    invalid++;
                    continue;
                }
                if (candidate.Record == null || !string.IsNullOrEmpty(candidate.Error))
                {
                    SetStatus(match, "corrupt", "关联录像损坏或版本不受支持，未计入数据之最。");
                    invalid++;
                    continue;
                }
                var record = candidate.Record;
                if (!Matches(tournament, pairing, record) || candidate.Entry.Winner != pairing.WinnerSide.Value)
                {
                    SetStatus(match, "mismatch", "关联录像的内容、双方或结果与赛事不一致。");
                    invalid++;
                    continue;
                }
                Battle battle;
                try
                {
                    var compatible = Replay.Check(db, record);
                    if (!compatible.CanReplay || !string.IsNullOrEmpty(compatible.Reason))
                    {
                        SetStatus(match, "mismatch", "关联录像无法按原赛事内容严格复算。");
                        invalid++;
                        continue;
                    }
                    battle = Replay.Run(db, record);
                    if (battle.Phase != Phase.Over || battle.Winner != pairing.WinnerSide.Value)
                    {
                        SetStatus(match, "mismatch", "关联录像未完整结束或复算胜负与赛事不一致。");
                        invalid++;
                        continue;
                    }
                }
                catch (Exception error) when (error is FormatException || error is ArgumentException
                    || error is InvalidOperationException || error is KeyError || error is KeyNotFoundException
                    || error is IndexOutOfRangeException || error is NullReferenceException)
                {
                    SetStatus(match, "mismatch", "关联录像不能完整复算，未计入数据之最。");
                    invalid++;
                    continue;
                }
                SetStatus(match, "saved", "已保存并严格复算。");
                archived++;
                Accumulate(battle, pairing, match.ReplayId, totals, ref damage);
            }
            var standings = tournament.Standings().Select(row => new Standing(row.Player, PlayerName(row.Name), row.Points,
                row.OpponentPoints, row.HeadToHeadPoints, row.Wins, row.Draws, row.Losses, row.Byes, row.Rank)).ToList();
            var mostKos = totals.Values.Where(row => row.Kos > 0).OrderByDescending(row => row.Kos)
                .ThenBy(row => row.Id, StringComparer.Ordinal).FirstOrDefault();
            var mostAppearances = totals.Values.Where(row => row.Appearances > 0).OrderByDescending(row => row.Appearances)
                .ThenBy(row => row.Id, StringComparer.Ordinal).FirstOrDefault();
            return new TournamentReport(WriteText(tournament, standings, matches, completed, archived, missing, invalid,
                damage, mostKos, mostAppearances), WriteJson(tournament, standings, matches, completed, archived, missing,
                invalid, damage, mostKos, mostAppearances));
        }

        static void SetStatus(Match match, string status, string message)
        {
            match.Status = status;
            match.Message = message;
        }

        static Game ReadGame(Tournament tournament, Pairing pairing, TournamentGame source,
            List<TournamentReplaySnapshot> candidates, out Battle battle)
        {
            battle = null;
            var row = new Game { Source = source };
            var found = candidates?.Where(item => item.Attempt == source.Attempt).ToList();
            Game Failure(string status, string message)
            {
                row.Status = status; row.Message = message; return row;
            }
            if (found == null || found.Count == 0) return Failure("not_saved", "未保存（没有找到本赛事本局的归档录像）。");
            if (found.Count != 1) return Failure("ambiguous", "同一局有多份关联录像，未选择其中任何一份。");
            var candidate = found[0];
            row.ReplayId = candidate.Entry?.Id;
            if (candidate.Entry == null || candidate.Entry.IsCorrupt)
                return Failure("corrupt", "关联录像损坏或版本不受支持，未计入数据之最。");
            if (candidate.Entry.IsUnavailable)
                return Failure("unavailable", "关联录像含已下架内容，不能重放或计入数据之最。");
            if (candidate.Record == null || !string.IsNullOrEmpty(candidate.Error))
                return Failure("corrupt", "关联录像无法读取，未计入数据之最。");
            if (candidate.Entry.Winner != source.WinnerSide
                || candidate.Entry.Turns != candidate.Record.Events.Count(entry => entry.Type == BattleRecordEventType.Turn)
                || Tournament.TournamentRecordHash(candidate.Record.ToJson()) != source.RecordHash)
                return Failure("mismatch", "关联录像的统计或原始过程与登记的单局记录不一致。");
            try
            {
                var compatible = Replay.Check(tournament.ReportDb, candidate.Record);
                if (!compatible.CanReplay || !string.IsNullOrEmpty(compatible.Reason))
                    return Failure("mismatch", "关联录像无法按原赛事内容严格复算。");
                battle = tournament.ReplayGame(pairing, candidate.Record, source.Attempt);
            }
            catch (Exception error) when (error is FormatException || error is ArgumentException
                || error is InvalidOperationException || error is KeyError || error is KeyNotFoundException
                || error is IndexOutOfRangeException || error is NullReferenceException)
            {
                return Failure("mismatch", "关联录像的内容、种子、过程或结果与本局不一致，未计入数据之最。");
            }
            return Failure("saved", "已保存并严格复算。");
        }

        static bool Matches(Tournament tournament, Pairing pairing, BattleRecord record)
        {
            if (record.PackId != tournament.ReportPackId || record.ContentHash != tournament.ReportContentHash
                || record.Teams == null || record.Teams.Length != 2 || record.Names == null || record.Names.Length != 2)
                return false;
            var players = new[] { tournament.Players[pairing.Player0], tournament.Players[pairing.Player1] };
            for (int side = 0; side < 2; side++)
                if (record.Teams[side] == null || !record.Teams[side].SequenceEqual(players[side].Team)
                    || record.Names[side] != players[side].Name) return false;
            return true;
        }

        static void Accumulate(Battle battle, Pairing pairing, string replayId,
            Dictionary<string, StreamerTotal> totals, ref DamageBest damage)
        {
            StreamerTotal Total(string id)
            {
                if (!totals.TryGetValue(id, out var total))
                    totals[id] = total = new StreamerTotal { Id = id, Name = battle.Db.Streamer(id).Name };
                return total;
            }
            foreach (var side in BattleSummary.From(battle).Sides)
                foreach (var fighter in side)
                    Total(fighter.StreamerId).Kos = checked(Total(fighter.StreamerId).Kos + fighter.Kos);
            var appeared = new HashSet<(int Side, string Id)>();
            foreach (var entry in battle.Events.OrderBy(item => item.LogIndex))
            {
                if (entry.Kind == BattleEventKind.Enter && appeared.Add((entry.Side, entry.Id)))
                    Total(entry.Id).Appearances = checked(Total(entry.Id).Appearances + 1);
                // Damage 的位置指向受击者；不能把它误写为造成伤害的主播。
                if (entry.Kind != BattleEventKind.Damage || entry.Amount <= 0
                    || damage != null && entry.Amount <= damage.Amount) continue;
                var fighter = battle.Sides[entry.Side].Team[entry.Slot];
                damage = new DamageBest
                {
                    Amount = entry.Amount, Pairing = pairing.Id, Round = pairing.Round, Turn = entry.Turn,
                    LogIndex = entry.LogIndex, Side = entry.Side, Slot = entry.Slot,
                    Id = fighter.Def.Id, Name = fighter.Name, ReplayId = replayId
                };
            }
        }

        static string PlayerName(string name) { return TextInput.Clean(name, TextInputKind.PlayerName).Text; }
        static string Number(long value) { return value.ToString(CultureInfo.InvariantCulture); }
        static string Result(Pairing pairing)
        {
            if (pairing.IsBye) return "轮空";
            if (!pairing.WinnerSide.HasValue) return "待赛";
            if (pairing.AdvancedBySeed)
                return "加赛 3 次仍平，按种子顺位晋级：" + (pairing.WinnerSide.Value == 0 ? "甲方" : "乙方");
            return pairing.WinnerSide.Value == -1 ? "平局" : pairing.WinnerSide.Value == 0 ? "甲方获胜" : "乙方获胜";
        }

        static string WriteText(Tournament tournament, List<Standing> standings, List<Match> matches,
            int completed, int archived, int missing, int invalid, DamageBest damage, StreamerTotal kos, StreamerTotal appearances)
        {
            var output = new StringBuilder("本地赛事战报\n");
            output.Append("内容包：").Append(tournament.ReportPackId).Append("；赛制：")
                .Append(tournament.FormatName).Append("；已排轮数：")
                .Append(Number(tournament.Round)).Append('/').Append(Number(tournament.Rounds))
                .Append(tournament.IsElimination && !tournament.IsFinished ? "（后者为轮数上限）" : "")
                .Append("；状态：").Append(tournament.IsFinished ? "已结束" : "进行中").Append("。\n");
            if (tournament.IsElimination)
            {
                output.Append(tournament.AutoTiebreak ? "每场原局平局后最多加赛 3 次；重置总决赛：" : "每场一局；重置总决赛：").Append(tournament.Format == Tournament.DoubleElimination
                    ? tournament.ResetFinal ? "开启" : "关闭" : "不适用").Append("；冠军：");
                if (tournament.Champion.HasValue)
                    output.Append(PlayerName(tournament.Players[tournament.Champion.Value].Name))
                        .Append("（报名 #").Append(Number(tournament.Champion.Value + 1)).Append('）');
                else output.Append("尚未产生");
                output.Append("。\n");
            }
            output.Append(tournament.IsElimination
                ? "\n晋级排名（冠军 → 仍在赛 → 较晚淘汰；同阶段按种子顺序）\n"
                : "\n积分榜（积分 → 对手分 → 直接对话分 → 报名顺序）\n");
            if (standings.Count == 0) output.Append("暂无报名。\n");
            foreach (var row in standings)
                output.Append(Number(row.Rank)).Append(" · 报名 #").Append(Number(row.Player + 1)).Append(' ').Append(row.Name)
                    .Append("：").Append(Number(row.Points)).Append(" 分；对手分 ").Append(Number(row.OpponentPoints))
                    .Append("；直接对话分 ").Append(Number(row.HeadToHeadPoints)).Append("；胜 ").Append(Number(row.Wins))
                    .Append(" / 平 ").Append(Number(row.Draws)).Append(" / 负 ").Append(Number(row.Losses))
                    .Append("；轮空 ").Append(Number(row.Byes)).Append('\n');
            foreach (var round in matches.GroupBy(match => match.Pairing.Round))
            {
                output.Append("\n第 ").Append(Number(round.Key)).Append(" 轮\n");
                foreach (var match in round)
                {
                    if (tournament.IsElimination) output.Append(BracketName(match.Pairing.Bracket)).Append(" 第 ")
                        .Append(Number(match.Pairing.BracketRound)).Append(" 轮 · ");
                    output.Append("对局 #").Append(Number(match.Pairing.Id + 1)).Append("：报名 #")
                        .Append(Number(match.Pairing.Player0 + 1)).Append(' ').Append(match.Name0);
                    if (!match.Pairing.IsBye) output.Append(" 对 报名 #").Append(Number(match.Pairing.Player1 + 1)).Append(' ').Append(match.Name1);
                    output.Append("；").Append(Result(match.Pairing)).Append("；录像：");
                    if (match.ReplayId != null) output.Append(match.ReplayId).Append("；");
                    output.Append(match.Message).Append('\n');
                    foreach (var game in match.Games)
                    {
                        output.Append("  ").Append(game.Source.Attempt == 0 ? "原局" : "加赛 " + Number(game.Source.Attempt))
                            .Append("；种子：").Append(Number(game.Source.Seed)).Append("；")
                            .Append(game.Source.WinnerSide == -1 ? "平局" : game.Source.WinnerSide == 0 ? "甲方获胜" : "乙方获胜")
                            .Append("；录像：");
                        if (game.ReplayId != null) output.Append(game.ReplayId).Append("；");
                        output.Append(game.Message).Append('\n');
                    }
                }
            }
            output.Append("\n数据之最（仅含已保存且严格复算的录像）\n已完成实战 ").Append(Number(completed))
                .Append(" 局；可复算 ").Append(Number(archived)).Append(" 局；未保存 ").Append(Number(missing))
                .Append(" 局；不可用 ").Append(Number(invalid)).Append(" 局。缺失录像不按零值补齐。\n");
            output.Append("最高单次伤害：");
            if (damage == null) output.Append("暂无可复算伤害记录。\n");
            else output.Append(Number(damage.Amount)).Append(" 点；对局 #").Append(Number(damage.Pairing + 1))
                .Append("，第 ").Append(Number(damage.Turn)).Append(" 回合，日志 #").Append(Number(damage.LogIndex + 1))
                .Append("；受击方 ").Append(Number(damage.Side)).Append("，受击主播 ").Append(damage.Name)
                .Append("（").Append(damage.Id).Append("）；录像 ").Append(damage.ReplayId).Append("。\n");
            output.Append("最多击倒：").Append(kos == null ? "暂无可复算击倒记录。" : kos.Name + "（" + kos.Id + "）· " + Number(kos.Kos) + " 次。").Append('\n');
            output.Append("出场最多：").Append(appearances == null ? "暂无可复算登场记录。" : appearances.Name + "（" + appearances.Id + "）· " + Number(appearances.Appearances) + " 局次。").Append('\n');
            output.Append("单次伤害包含持续伤害、反弹和自伤的实际扣血；击倒归属沿用引擎；出场按每局每方实际登场去重。\n")
                .Append(tournament.AutoTiebreak
                    ? "并列时，击倒及出场按主播 id 顺序取首；伤害按对阵编号、原局/加赛序号、日志顺序取首。\n"
                    : "并列时，击倒及出场按主播 id 顺序取首；伤害按对阵编号、日志顺序取首。\n");
            return output.ToString();
        }

        static string WriteJson(Tournament tournament, List<Standing> standings, List<Match> matches,
            int completed, int archived, int missing, int invalid, DamageBest damage, StreamerTotal kos, StreamerTotal appearances)
        {
            var output = new StringBuilder(tournament.AutoTiebreak ? "{\"version\":2,\"packId\":" : "{\"version\":1,\"packId\":");
            String(output, tournament.ReportPackId);
            output.Append(",\"format\":"); String(output, tournament.Format);
            output.Append(",\"roundCount\":").Append(Number(tournament.Rounds)).Append(",\"playedRounds\":").Append(Number(tournament.Round))
                .Append(",\"isFinished\":").Append(tournament.IsFinished ? "true" : "false");
            if (tournament.IsElimination)
                output.Append(",\"resetFinal\":").Append(tournament.ResetFinal ? "true" : "false")
                    .Append(",\"bestOf\":1,\"champion\":").Append(tournament.Champion.HasValue ? Number(tournament.Champion.Value) : "null");
            if (tournament.AutoTiebreak) output.Append(",\"autoTiebreak\":true");
            output.Append(",\"standings\":[");
            for (int i = 0; i < standings.Count; i++)
            {
                if (i > 0) output.Append(',');
                var row = standings[i];
                output.Append("{\"rank\":").Append(Number(row.Rank)).Append(",\"player\":").Append(Number(row.Player)).Append(",\"name\":"); String(output, row.Name);
                output.Append(",\"points\":").Append(Number(row.Points)).Append(",\"opponentPoints\":").Append(Number(row.OpponentPoints))
                    .Append(",\"headToHeadPoints\":").Append(Number(row.HeadToHeadPoints)).Append(",\"wins\":").Append(Number(row.Wins))
                    .Append(",\"draws\":").Append(Number(row.Draws)).Append(",\"losses\":").Append(Number(row.Losses))
                    .Append(",\"byes\":").Append(Number(row.Byes)).Append('}');
            }
            output.Append("],\"rounds\":[");
            bool firstRound = true;
            foreach (var round in matches.GroupBy(match => match.Pairing.Round))
            {
                if (!firstRound) output.Append(',');
                firstRound = false;
                output.Append("{\"number\":").Append(Number(round.Key)).Append(",\"matches\":[");
                bool firstMatch = true;
                foreach (var match in round)
                {
                    if (!firstMatch) output.Append(',');
                    firstMatch = false;
                    var pairing = match.Pairing;
                    output.Append("{\"pairingId\":").Append(Number(pairing.Id)).Append(",\"player0\":").Append(Number(pairing.Player0))
                        .Append(",\"player1\":").Append(pairing.IsBye ? "null" : Number(pairing.Player1)).Append(",\"name0\":"); String(output, match.Name0);
                    output.Append(",\"name1\":"); String(output, match.Name1);
                    if (tournament.IsElimination)
                    {
                        output.Append(",\"bracket\":"); String(output, pairing.Bracket);
                        output.Append(",\"bracketRound\":").Append(Number(pairing.BracketRound));
                    }
                    output.Append(",\"winnerSide\":").Append(pairing.WinnerSide.HasValue ? Number(pairing.WinnerSide.Value) : "null").Append(",\"result\":"); String(output, Result(pairing));
                    output.Append(",\"replay\":{\"status\":"); String(output, match.Status);
                    output.Append(",\"id\":"); String(output, match.ReplayId);
                    output.Append(",\"message\":"); String(output, match.Message);
                    output.Append('}');
                    if (tournament.AutoTiebreak)
                    {
                        output.Append(",\"advancedBySeed\":").Append(pairing.AdvancedBySeed ? "true" : "false")
                            .Append(",\"games\":[");
                        for (int i = 0; i < match.Games.Count; i++)
                        {
                            if (i > 0) output.Append(',');
                            var game = match.Games[i];
                            output.Append("{\"attempt\":").Append(Number(game.Source.Attempt))
                                .Append(",\"seed\":").Append(Number(game.Source.Seed))
                                .Append(",\"winnerSide\":").Append(Number(game.Source.WinnerSide))
                                .Append(",\"recordHash\":"); String(output, game.Source.RecordHash);
                            output.Append(",\"replay\":{\"status\":"); String(output, game.Status);
                            output.Append(",\"id\":"); String(output, game.ReplayId);
                            output.Append(",\"message\":"); String(output, game.Message);
                            output.Append("}}");
                        }
                        output.Append(']');
                    }
                    output.Append('}');
                }
                output.Append("]}");
            }
            output.Append("],\"coverage\":{\"completedGames\":").Append(Number(completed)).Append(",\"archivedGames\":").Append(Number(archived))
                .Append(",\"missingGames\":").Append(Number(missing)).Append(",\"invalidGames\":").Append(Number(invalid)).Append("},\"maxDamage\":");
            if (damage == null) output.Append("null");
            else
            {
                output.Append("{\"amount\":").Append(Number(damage.Amount)).Append(",\"pairingId\":").Append(Number(damage.Pairing))
                    .Append(",\"round\":").Append(Number(damage.Round)).Append(",\"turn\":").Append(Number(damage.Turn))
                    .Append(",\"logIndex\":").Append(Number(damage.LogIndex)).Append(",\"side\":").Append(Number(damage.Side))
                    .Append(",\"slot\":").Append(Number(damage.Slot)).Append(",\"streamerId\":"); String(output, damage.Id);
                output.Append(",\"streamerName\":"); String(output, damage.Name);
                output.Append(",\"replayId\":"); String(output, damage.ReplayId); output.Append('}');
            }
            output.Append(",\"mostKos\":"); WriteLeader(output, kos, kos == null ? 0 : kos.Kos);
            output.Append(",\"mostAppearances\":"); WriteLeader(output, appearances, appearances == null ? 0 : appearances.Appearances);
            return output.Append('}').ToString();
        }

        static void String(StringBuilder output, string value)
        {
            if (value == null) output.Append("null");
            else TeamPresetJson.WriteString(output, value);
        }

        static string BracketName(string bracket)
        {
            if (bracket == "single") return "单败组";
            if (bracket == "winners") return "胜者组";
            if (bracket == "losers") return "败者组";
            if (bracket == "final") return "总决赛";
            if (bracket == "reset") return "重置总决赛";
            return "未分组";
        }
        static void WriteLeader(StringBuilder output, StreamerTotal row, long count)
        {
            if (row == null) { output.Append("null"); return; }
            output.Append("{\"streamerId\":"); String(output, row.Id);
            output.Append(",\"streamerName\":"); String(output, row.Name);
            output.Append(",\"count\":").Append(Number(count)).Append('}');
        }
    }
}
