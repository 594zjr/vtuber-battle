using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace StreamDuel.Core
{
    public sealed class TournamentPlayer
    {
        public int Id { get; }
        public string Name { get; }
        public string TeamCode { get; }
        public IReadOnlyList<string> Team { get; }

        internal TournamentPlayer(int id, string name, string code, IList<string> team)
        {
            Id = id;
            Name = name;
            TeamCode = code;
            Team = new List<string>(team).AsReadOnly();
        }
    }

    public sealed class Pairing
    {
        public int Id { get; }
        public int Round { get; }
        public int Player0 { get; }
        public int Player1 { get; }
        public string Bracket { get; }
        public int BracketRound { get; }
        public bool IsBye { get { return Player1 == -1; } }
        public int? WinnerSide { get; private set; }

        internal Pairing(int id, int round, int player0, int player1, string bracket = null, int bracketRound = 0)
        {
            Id = id;
            Round = round;
            Player0 = player0;
            Player1 = player1;
            Bracket = bracket;
            BracketRound = bracketRound;
            if (IsBye) WinnerSide = 0;
        }

        internal void SetResult(int winnerSide) { WinnerSide = winnerSide; }
    }

    public sealed class Standing
    {
        public int Player { get; }
        public string Name { get; }
        public int Points { get; }
        public int OpponentPoints { get; }
        public int HeadToHeadPoints { get; }
        public int Wins { get; }
        public int Draws { get; }
        public int Losses { get; }
        public int Byes { get; }
        public int Rank { get; }

        internal Standing(int player, string name, int points, int opponentPoints, int headToHeadPoints,
            int wins, int draws, int losses, int byes, int rank)
        {
            Player = player;
            Name = name;
            Points = points;
            OpponentPoints = opponentPoints;
            HeadToHeadPoints = headToHeadPoints;
            Wins = wins;
            Draws = draws;
            Losses = losses;
            Byes = byes;
            Rank = rank;
        }
    }

    public sealed partial class Tournament
    {
        readonly ContentDb db;
        readonly string packId, contentHash;
        readonly int requestedRounds;
        readonly List<TournamentPlayer> players = new List<TournamentPlayer>();
        readonly List<List<Pairing>> roundsPlayed = new List<List<Pairing>>();
        readonly List<Pairing> pairings = new List<Pairing>();

        public IReadOnlyList<TournamentPlayer> Players { get; }
        public string Format { get; }
        public int Round { get { return roundsPlayed.Count; } }
        public int Rounds
        {
            get
            {
                if (IsElimination) return EliminationRounds();
                if (players.Count < 2) return requestedRounds;
                if (Format == "roundrobin") return players.Count % 2 == 0 ? players.Count - 1 : players.Count;
                if (requestedRounds > 0) return requestedRounds;
                int count = players.Count - 1, result = 0;
                while (count > 0) { count >>= 1; result++; }
                return result;
            }
        }
        public bool IsFinished
        {
            get
            {
                if (IsElimination) return EliminationFinished();
                return Round > 0 && Round == Rounds && roundsPlayed[Round - 1].All(item => item.WinnerSide.HasValue);
            }
        }

        public Tournament(ContentDb db, string format, int rounds = 0, TournamentOptions options = null)
        {
            this.db = db ?? throw new ArgumentNullException(nameof(db), "内容数据不能为空。");
            if (format != "roundrobin" && format != "swiss" && format != SingleElimination && format != DoubleElimination)
                throw new ArgumentException("赛制必须为循环赛、瑞士轮、单败或双败淘汰赛。", nameof(format));
            if (rounds < 0) throw new ArgumentOutOfRangeException(nameof(rounds), "赛事轮数不能为负数。");
            if (format != "swiss" && rounds != 0)
                throw new ArgumentException("循环赛与淘汰赛轮数由人数和赛程决定，不能指定轮数。", nameof(rounds));
            Format = format;
            requestedRounds = rounds;
            packId = db.PackId;
            contentHash = db.ContentHash();
            Players = players.AsReadOnly();
            InitializeElimination(options);
        }

        public void AddPlayer(string name, string teamCode)
        {
            if (Round != 0) throw new InvalidOperationException("赛事已开始，不能再报名。");
            if (string.IsNullOrWhiteSpace(name) || name.Any(char.IsControl))
                throw new ArgumentException("参赛昵称不能为空或包含控制字符。", nameof(name));
            TournamentJson.ValidateUnicode(name);
            TeamPreset preset;
            try { preset = StreamDuel.Core.TeamCode.Decode(teamCode); }
            catch (FormatException error) { throw new FormatException("报名队伍码无效：" + error.Message, error); }
            var errors = preset.Check(db, null);
            if (errors.Count > 0) throw new ArgumentException("报名队伍不合法：" + string.Join("；", errors), nameof(teamCode));
            players.Add(new TournamentPlayer(players.Count, name.Trim(), StreamDuel.Core.TeamCode.Encode(preset), preset.Ids));
        }

        public List<Pairing> NextRound()
        {
            if (players.Count < 2) throw new InvalidOperationException("至少需要两名选手才能排赛程。");
            if (Round > 0 && roundsPlayed[Round - 1].Any(item => !item.WinnerSide.HasValue))
                return new List<Pairing>(roundsPlayed[Round - 1]);
            if (IsFinished) return new List<Pairing>();
            if (IsElimination)
            {
                var eliminationRound = new List<Pairing>();
                foreach (var choice in EliminationRound())
                {
                    var pairing = new Pairing(pairings.Count, Round + 1, choice.Player0, choice.Player1,
                        choice.Bracket, choice.BracketRound);
                    pairings.Add(pairing);
                    eliminationRound.Add(pairing);
                }
                if (eliminationRound.Count == 0) throw new InvalidOperationException("淘汰赛没有可推进的对阵。");
                roundsPlayed.Add(eliminationRound);
                return new List<Pairing>(eliminationRound);
            }
            if (Format == "swiss" && players.Count % 2 != 0 && Rounds > players.Count)
                throw new InvalidOperationException("奇数人数的瑞士轮不能多于参赛人数，否则无法保证每人最多轮空一次。");

            var choices = Format == "roundrobin" ? RoundRobin() : Swiss();
            var round = new List<Pairing>();
            foreach (var choice in choices)
            {
                var pairing = new Pairing(pairings.Count, Round + 1, choice[0], choice[1]);
                pairings.Add(pairing);
                round.Add(pairing);
            }
            roundsPlayed.Add(round);
            return new List<Pairing>(round);
        }

        public void ReportResult(Pairing pairing, int winnerSide)
        {
            if (pairing == null) throw new ArgumentNullException(nameof(pairing), "对阵不能为空。");
            if (pairing.Id < 0 || pairing.Id >= pairings.Count || !ReferenceEquals(pairings[pairing.Id], pairing))
                throw new ArgumentException("这场对阵不属于本赛事。", nameof(pairing));
            if (pairing.IsBye) throw new InvalidOperationException("轮空已自动记胜，不能手工改写。");
            if (pairing.WinnerSide.HasValue) throw new InvalidOperationException("这场对阵已经登记结果。");
            if (winnerSide < -1 || winnerSide > 1)
                throw new ArgumentOutOfRangeException(nameof(winnerSide), "胜方只能是 0、1，或以 -1 表示平局。");
            if (IsElimination && winnerSide == -1)
                throw new ArgumentException("淘汰赛需要明确胜负；平局不能决定晋级，本场仍待赛。", nameof(winnerSide));
            pairing.SetResult(winnerSide);
        }

        public List<Standing> Standings()
        {
            int count = players.Count;
            var points = new int[count];
            var opponents = new int[count];
            var direct = new int[count];
            var wins = new int[count];
            var draws = new int[count];
            var losses = new int[count];
            var byes = new int[count];
            foreach (var pairing in pairings.Where(item => item.WinnerSide.HasValue))
            {
                int first = pairing.Player0, second = pairing.Player1, winner = pairing.WinnerSide.Value;
                if (pairing.IsBye) { points[first] += 3; wins[first]++; byes[first]++; }
                else if (winner == -1) { points[first]++; points[second]++; draws[first]++; draws[second]++; }
                else
                {
                    int won = winner == 0 ? first : second, lost = winner == 0 ? second : first;
                    points[won] += 3;
                    wins[won]++;
                    losses[lost]++;
                }
            }
            foreach (var pairing in pairings.Where(item => item.WinnerSide.HasValue && !item.IsBye))
            {
                // 每次已完成交手都计一次对手当前总分；轮空不虚构一个对手。
                opponents[pairing.Player0] += points[pairing.Player1];
                opponents[pairing.Player1] += points[pairing.Player0];
            }
            foreach (var pairing in pairings.Where(item => item.WinnerSide.HasValue && !item.IsBye))
            {
                int first = pairing.Player0, second = pairing.Player1;
                if (points[first] != points[second] || opponents[first] != opponents[second]) continue;
                // 直接对话只在同积分且同对手分的小组累计，避免两两比较遇到循环胜负时破坏排序。
                if (pairing.WinnerSide.Value == -1) { direct[first]++; direct[second]++; }
                else direct[pairing.WinnerSide.Value == 0 ? first : second] += 3;
            }
            var order = Enumerable.Range(0, count).OrderByDescending(id => points[id])
                .ThenByDescending(id => opponents[id]).ThenByDescending(id => direct[id]).ThenBy(id => id).ToList();
            if (IsElimination) order = EliminationRankOrder().ToList();
            var result = new List<Standing>();
            foreach (int id in order)
                result.Add(new Standing(id, players[id].Name, points[id], opponents[id], direct[id],
                    wins[id], draws[id], losses[id], byes[id], result.Count + 1));
            return result;
        }

        List<int[]> RoundRobin()
        {
            var order = Enumerable.Range(0, players.Count).ToList();
            if (order.Count % 2 != 0) order.Add(-1);
            for (int round = 0; round < Round; round++)
            {
                int last = order[order.Count - 1];
                order.RemoveAt(order.Count - 1);
                order.Insert(1, last);
            }
            var result = new List<int[]>();
            for (int i = 0; i < order.Count / 2; i++)
            {
                int first = order[i], second = order[order.Count - 1 - i];
                result.Add(first == -1 ? new[] { second, first } : new[] { first, second });
            }
            return result;
        }

        List<int[]> Swiss()
        {
            var standings = Standings();
            var ranked = standings.Select(item => item.Player).ToList();
            var met = new bool[players.Count, players.Count];
            foreach (var pairing in pairings.Where(item => !item.IsBye))
                met[pairing.Player0, pairing.Player1] = met[pairing.Player1, pairing.Player0] = true;
            var candidates = players.Count % 2 == 0 ? new List<int> { -1 }
                : standings.Where(item => item.Byes == 0).Reverse().Select(item => item.Player).ToList();
            List<int[]> best = null;
            int bestCost = int.MaxValue;
            foreach (int bye in candidates)
            {
                var remaining = ranked.Where(id => id != bye).ToList();
                var matching = Match(remaining, met, out int cost);
                if (cost < bestCost)
                {
                    best = matching;
                    bestCost = cost;
                    if (bye >= 0) best.Add(new[] { bye, -1 });
                }
                // 未轮空且排名靠后的候选先试；只有它无法避免重复时才改选其他轮空者。
                if (bestCost == 0) break;
            }
            return best ?? throw new InvalidOperationException("无法排出符合轮空限制的下一轮。");
        }

        static List<int[]> Match(List<int> players, bool[,] met, out int cost)
        {
            List<int[]> best = null;
            int bestCost = int.MaxValue;
            var path = new List<int[]>();
            void Search(List<int> remaining, int repeats)
            {
                if (remaining.Count == 0)
                {
                    if (repeats < bestCost) { bestCost = repeats; best = new List<int[]>(path); }
                    return;
                }
                int forced = remaining.Count(id => !remaining.Any(other => id != other && !met[id, other]));
                int lowerBound = repeats + (forced + 1) / 2;
                if (lowerBound >= bestCost) return;
                int first = remaining[0];
                // 同等重复代价下按现有排名相邻优先，候选全部有限枚举，不以搜索预算误判无解。
                foreach (int second in remaining.Skip(1).OrderBy(id => met[first, id] ? 1 : 0))
                {
                    int nextRepeats = repeats + (met[first, second] ? 1 : 0);
                    if (nextRepeats >= bestCost) continue;
                    path.Add(new[] { first, second });
                    Search(remaining.Where(id => id != first && id != second).ToList(), nextRepeats);
                    path.RemoveAt(path.Count - 1);
                    if (bestCost == lowerBound) return;
                }
            }
            Search(players, 0);
            cost = bestCost;
            return best;
        }

        public string ToJson()
        {
            var text = new StringBuilder(IsElimination ? "{\"version\":2,\"packId\":" : "{\"version\":1,\"packId\":");
            TeamPresetJson.WriteString(text, packId);
            text.Append(",\"contentHash\":");
            TeamPresetJson.WriteString(text, contentHash);
            text.Append(",\"format\":");
            TeamPresetJson.WriteString(text, Format);
            if (IsElimination) WriteEliminationConfiguration(text);
            text.Append(",\"rounds\":").Append(Number(requestedRounds)).Append(",\"players\":[");
            for (int i = 0; i < players.Count; i++)
            {
                if (i > 0) text.Append(',');
                var player = players[i];
                text.Append("{\"id\":").Append(Number(player.Id)).Append(",\"name\":");
                TeamPresetJson.WriteString(text, player.Name);
                text.Append(",\"teamCode\":");
                TeamPresetJson.WriteString(text, player.TeamCode);
                text.Append('}');
            }
            text.Append("],\"roundsPlayed\":[");
            for (int i = 0; i < roundsPlayed.Count; i++)
            {
                if (i > 0) text.Append(',');
                text.Append('[');
                for (int j = 0; j < roundsPlayed[i].Count; j++)
                {
                    if (j > 0) text.Append(',');
                    var pairing = roundsPlayed[i][j];
                    text.Append("{\"id\":").Append(Number(pairing.Id)).Append(",\"round\":").Append(Number(pairing.Round))
                        .Append(",\"player0\":").Append(Number(pairing.Player0)).Append(",\"player1\":").Append(Number(pairing.Player1));
                    if (IsElimination)
                    {
                        text.Append(",\"bracket\":"); TeamPresetJson.WriteString(text, pairing.Bracket);
                        text.Append(",\"bracketRound\":").Append(Number(pairing.BracketRound));
                    }
                    text.Append(",\"winnerSide\":").Append(pairing.WinnerSide.HasValue ? Number(pairing.WinnerSide.Value) : "null").Append('}');
                }
                text.Append(']');
            }
            return text.Append("]}").ToString();
        }

        public static Tournament FromJson(ContentDb db, string json)
        {
            if (db == null) throw new ArgumentNullException(nameof(db), "内容数据不能为空。");
            try
            {
                var root = TournamentJson.Parse(json);
                if (!root.ContainsKey("version")) throw new FormatException("赛事存档缺少版本。");
                int version = TournamentJson.Integer(root, "version", 1, int.MaxValue);
                if (version != 1 && version != 2)
                    throw new FormatException("不支持此赛事存档版本。");
                if (version == 1) TournamentJson.Keys(root, "version", "packId", "contentHash", "format", "rounds", "players", "roundsPlayed");
                else TournamentJson.Keys(root, "version", "packId", "contentHash", "format", "seed", "seedOrder", "resetFinal", "rounds", "players", "roundsPlayed");
                if (TournamentJson.Text(root, "packId") != db.PackId) throw new FormatException("赛事内容包不匹配。");
                if (TournamentJson.Text(root, "contentHash") != db.ContentHash()) throw new FormatException("赛事内容不一致，请使用相同规则与内容包。");
                string format = TournamentJson.Text(root, "format");
                bool elimination = format == SingleElimination || format == DoubleElimination;
                if (elimination != (version == 2)) throw new FormatException("赛事版本与赛制不匹配。");
                var result = new Tournament(db, format, TournamentJson.Integer(root, "rounds", 0, int.MaxValue),
                    elimination ? ReadEliminationConfiguration(root) : null);
                foreach (var value in TournamentJson.Array(root, "players"))
                {
                    var player = TournamentJson.Object(value);
                    TournamentJson.Keys(player, "id", "name", "teamCode");
                    if (TournamentJson.Integer(player, "id", 0, int.MaxValue) != result.players.Count)
                        throw new FormatException("参赛报名编号顺序无效。");
                    result.AddPlayer(TournamentJson.Text(player, "name"), TournamentJson.Text(player, "teamCode"));
                }
                foreach (var value in TournamentJson.Array(root, "roundsPlayed"))
                {
                    var savedRound = value as List<object> ?? throw new FormatException("每轮对阵必须是数组。");
                    if (result.Round > 0 && result.roundsPlayed[result.Round - 1].Any(item => !item.WinnerSide.HasValue))
                        throw new FormatException("上一轮尚未报完结果，不能保存后续轮次。");
                    var round = result.NextRound();
                    if (round.Count == 0 || savedRound.Count != round.Count) throw new FormatException("赛事轮次或对阵数量无效。");
                    for (int i = 0; i < round.Count; i++)
                    {
                        var pairing = TournamentJson.Object(savedRound[i]);
                        if (elimination) TournamentJson.Keys(pairing, "id", "round", "player0", "player1", "bracket", "bracketRound", "winnerSide");
                        else TournamentJson.Keys(pairing, "id", "round", "player0", "player1", "winnerSide");
                        var expected = round[i];
                        if (TournamentJson.Integer(pairing, "id", 0, int.MaxValue) != expected.Id
                            || TournamentJson.Integer(pairing, "round", 1, int.MaxValue) != expected.Round
                            || TournamentJson.Integer(pairing, "player0", 0, int.MaxValue) != expected.Player0
                            || TournamentJson.Integer(pairing, "player1", -1, int.MaxValue) != expected.Player1)
                            throw new FormatException("存档对阵不符合当前赛制的排轮顺序。");
                        if (elimination && (TournamentJson.Text(pairing, "bracket") != expected.Bracket
                            || TournamentJson.Integer(pairing, "bracketRound", 1, int.MaxValue) != expected.BracketRound))
                            throw new FormatException("存档对阵的胜败组或组内轮次不一致。");
                        object winner = pairing["winnerSide"];
                        if (expected.IsBye)
                        {
                            if (winner == null || TournamentJson.Integer(pairing, "winnerSide", -1, 1) != 0)
                                throw new FormatException("轮空必须自动记为胜利。");
                        }
                        else if (winner != null) result.ReportResult(expected, TournamentJson.Integer(pairing, "winnerSide", -1, 1));
                    }
                }
                return result;
            }
            catch (Exception error) when (error is FormatException || error is ArgumentException || error is InvalidOperationException)
            {
                throw new FormatException("赛事存档无效：" + error.Message, error);
            }
        }

        static string Number(int number) { return number.ToString(CultureInfo.InvariantCulture); }
    }

    internal static class TournamentJson
    {
        static readonly Encoding Utf8 = new UTF8Encoding(false, true);

        internal static Dictionary<string, object> Parse(string json)
        {
            var result = Object(MiniJson.Parse(json));
            // 通用解析器会覆盖重复键；在语法验证后扫描键，拒绝含糊的赛事比分。
            var objects = new Stack<HashSet<string>>();
            for (int i = 0; i < json.Length; i++)
            {
                if (json[i] == '{') objects.Push(new HashSet<string>(StringComparer.Ordinal));
                else if (json[i] == '[') objects.Push(null);
                else if (json[i] == '}' || json[i] == ']') objects.Pop();
                else if (json[i] == '"')
                {
                    int start = i++;
                    while (json[i] != '"') { if (json[i] == '\\') i++; i++; }
                    string value = (string)MiniJson.Parse(json.Substring(start, i - start + 1));
                    ValidateUnicode(value);
                    int next = i + 1;
                    while (next < json.Length && (json[next] == ' ' || json[next] == '\t' || json[next] == '\r' || json[next] == '\n')) next++;
                    if (next < json.Length && json[next] == ':' && !objects.Peek().Add(value))
                        throw new FormatException("赛事存档包含重复字段。");
                }
            }
            return result;
        }

        internal static void ValidateUnicode(string text)
        {
            try { Utf8.GetByteCount(text); }
            catch (EncoderFallbackException error) { throw new FormatException("文字包含无效的 Unicode 字符。", error); }
        }

        internal static Dictionary<string, object> Object(object value)
        {
            return value as Dictionary<string, object> ?? throw new FormatException("赛事存档需要对象。");
        }

        internal static void Keys(Dictionary<string, object> value, params string[] keys)
        {
            if (value.Count != keys.Length || keys.Any(key => !value.ContainsKey(key)))
                throw new FormatException("赛事存档字段缺失或包含不支持的字段。");
        }

        internal static string Text(Dictionary<string, object> value, string key)
        {
            return value[key] as string ?? throw new FormatException("赛事文字字段必须是字符串。");
        }

        internal static List<object> Array(Dictionary<string, object> value, string key)
        {
            return value[key] as List<object> ?? throw new FormatException("赛事列表字段必须是数组。");
        }

        internal static int Integer(Dictionary<string, object> value, string key, int min, int max)
        {
            if (!(value[key] is double number) || double.IsNaN(number) || double.IsInfinity(number)
                || number < min || number > max || number != Math.Truncate(number))
                throw new FormatException("赛事数字字段必须是范围内的整数。");
            return (int)number;
        }
    }
}
