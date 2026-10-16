using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using StreamDuel.Core;
using Xunit;

public class T89_TournamentTests
{
    [Theory]
    [InlineData(4, 3, 6)]
    [InlineData(5, 5, 10)]
    public void RoundRobinPlaysEveryPairExactlyOnceAndGivesEachOddPlayerOneBye(int count, int rounds, int matches)
    {
        var tournament = Make("roundrobin", count);
        Assert.Equal(0, tournament.Round);
        Assert.Equal(rounds, tournament.Rounds);
        Assert.False(tournament.IsFinished);
        var meetings = new HashSet<string>();
        var byePlayers = new HashSet<int>();
        var ids = new HashSet<int>();
        var results = new List<Result>();
        for (int round = 1; round <= rounds; round++)
        {
            var pairings = tournament.NextRound();
            Assert.Equal(round, tournament.Round);
            AssertRound(pairings, count, round, meetings, byePlayers, ids);
            foreach (var pairing in pairings)
            {
                int outcome = pairing.IsBye ? 0 : (pairing.Player0 + pairing.Player1 + round) % 3 - 1;
                if (!pairing.IsBye) tournament.ReportResult(pairing, outcome);
                results.Add(new Result(pairing, outcome));
            }
            AssertStandings(tournament, results);
        }
        Assert.Equal(matches, meetings.Count);
        Assert.Equal(count % 2 == 0 ? 0 : count, byePlayers.Count);
        for (int left = 0; left < count; left++)
            for (int right = left + 1; right < count; right++)
                Assert.Contains(Key(left, right), meetings);
        Assert.True(tournament.IsFinished);
        Assert.Empty(tournament.NextRound());
        Assert.Equal(rounds, tournament.Round);
        Assert.Equal(Enumerable.Range(0, ids.Count), ids.OrderBy(id => id));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(2u)]
    [InlineData(3u)]
    [InlineData(17u)]
    [InlineData(89u)]
    [InlineData(901u)]
    [InlineData(65535u)]
    public void SevenPlayerSwissUsesThreeRoundsWithoutRepeatedOpponentsOrByesUnderDifferentScores(uint pattern)
    {
        var tournament = Make("swiss", 7);
        Assert.Equal(3, tournament.Rounds);
        var scores = new Rng(pattern);
        var meetings = new HashSet<string>();
        var byePlayers = new HashSet<int>();
        var ids = new HashSet<int>();
        var results = new List<Result>();
        for (int round = 1; round <= 3; round++)
        {
            var pairings = tournament.NextRound();
            AssertRound(pairings, 7, round, meetings, byePlayers, ids);
            foreach (var pairing in pairings)
            {
                int outcome = pairing.IsBye ? 0 : pattern == 0 ? 0 : pattern == 1 ? 1 : pattern == 2 ? -1 : (int)(scores.Next() * 3) - 1;
                if (!pairing.IsBye) tournament.ReportResult(pairing, outcome);
                results.Add(new Result(pairing, outcome));
            }
            AssertStandings(tournament, results);
        }
        Assert.True(tournament.IsFinished);
        Assert.Equal(9, meetings.Count);
        Assert.Equal(3, byePlayers.Count);
        Assert.Empty(tournament.NextRound());
        Assert.All(tournament.Standings(), row => Assert.InRange(row.Byes, 0, 1));
    }

    [Fact]
    public void OpponentPointsCountOnlyCompletedGamesUseCurrentScoresAndPrecedeHeadToHead()
    {
        var tournament = Make("roundrobin", 4);
        var first = tournament.NextRound();
        var opening = first[0];
        var other = first[1];
        int a = opening.Player0, b = opening.Player1;
        tournament.ReportResult(opening, 0);
        tournament.ReportResult(other, -1);
        var second = tournament.NextRound();
        var played = Assert.Single(second, pairing => Contains(pairing, b));
        int c = Opponent(played, b);
        int d = other.Player0 == c ? other.Player1 : other.Player0;
        tournament.ReportResult(played, SideOf(played, b));

        // b 曾负于 a，但 b 的已完成对手分更高；a 对 d 的待报对局不能提前计入。
        var rows = tournament.Standings();
        Assert.Equal(new[] { b, a, c, d }, rows.Select(row => row.Player));
        Assert.Equal(new[] { 3, 3, 1, 1 }, rows.Select(row => row.Points));
        Assert.Equal(new[] { 4, 3, 4, 1 }, rows.Select(row => row.OpponentPoints));
        Assert.All(rows, row => Assert.Equal(0, row.HeadToHeadPoints));
        var pending = Assert.Single(second, pairing => !pairing.WinnerSide.HasValue);
        tournament.ReportResult(pending, SideOf(pending, a));
        var updated = tournament.Standings();
        Assert.Equal(7, Assert.Single(updated, row => row.Player == b).OpponentPoints);
        Assert.Equal(new[] { a, b, d, c }, updated.Select(row => row.Player));
    }

    [Fact]
    public void HeadToHeadBreaksEqualPointsAndOpponentPointsBeforeRegistrationOrder()
    {
        var tournament = Make("roundrobin", 4);
        var winners = new Dictionary<string, int>
        {
            [Key(0, 1)] = 1, [Key(0, 2)] = 0, [Key(0, 3)] = 0,
            [Key(1, 2)] = 2, [Key(1, 3)] = 1, [Key(2, 3)] = -1
        };
        while (!tournament.IsFinished)
            foreach (var pairing in tournament.NextRound())
            {
                int winner = winners[Key(pairing.Player0, pairing.Player1)];
                tournament.ReportResult(pairing, winner < 0 ? -1 : SideOf(pairing, winner));
            }
        var rows = tournament.Standings();
        Assert.Equal(new[] { 1, 0, 2, 3 }, rows.Select(row => row.Player));
        Assert.Equal(new[] { 6, 6, 4, 1 }, rows.Select(row => row.Points));
        Assert.Equal(new[] { 11, 11, 13, 16 }, rows.Select(row => row.OpponentPoints));
        Assert.Equal(new[] { 3, 0, 0, 0 }, rows.Select(row => row.HeadToHeadPoints));
        Assert.Equal(new[] { 2, 2, 1, 0 }, rows.Select(row => row.Wins));
        Assert.Equal(new[] { 0, 0, 1, 1 }, rows.Select(row => row.Draws));
        Assert.Equal(new[] { 1, 1, 1, 2 }, rows.Select(row => row.Losses));
        Assert.Equal(new[] { 1, 2, 3, 4 }, rows.Select(row => row.Rank));
    }

    [Fact]
    public void AThreePlayerHeadToHeadCycleFallsBackToRegistrationOrder()
    {
        var tournament = Make("roundrobin", 4);
        while (!tournament.IsFinished)
            foreach (var pairing in tournament.NextRound())
            {
                int left = Math.Min(pairing.Player0, pairing.Player1), right = Math.Max(pairing.Player0, pairing.Player1);
                int winner = right == 3 ? left : left == 0 && right == 2 ? 2 : left;
                tournament.ReportResult(pairing, SideOf(pairing, winner));
            }
        var rows = tournament.Standings();
        Assert.Equal(new[] { 0, 1, 2, 3 }, rows.Select(row => row.Player));
        Assert.Equal(new[] { 6, 6, 6, 0 }, rows.Select(row => row.Points));
        Assert.Equal(new[] { 12, 12, 12, 18 }, rows.Select(row => row.OpponentPoints));
        Assert.Equal(new[] { 3, 3, 3, 0 }, rows.Select(row => row.HeadToHeadPoints));
    }

    [Fact]
    public void UnreportedRoundReturnsTheSamePairingsAndRejectsForeignDuplicateAndInvalidResultsAtomically()
    {
        var tournament = Make("roundrobin", 5);
        var round = tournament.NextRound();
        var again = tournament.NextRound();
        Assert.Equal(1, tournament.Round);
        Assert.Equal(round.Count, again.Count);
        for (int i = 0; i < round.Count; i++) Assert.Same(round[i], again[i]);
        var match = round.First(pairing => !pairing.IsBye);
        var bye = Assert.Single(round, pairing => pairing.IsBye);
        Assert.Equal(0, bye.WinnerSide);
        var foreign = Make("roundrobin", 5).NextRound().First(pairing => pairing.Id == match.Id);
        string before = tournament.ToJson();
        AssertChinese(() => tournament.ReportResult(foreign, 0));
        AssertChinese(() => tournament.ReportResult(null, 0));
        AssertChinese(() => tournament.ReportResult(match, -2));
        AssertChinese(() => tournament.ReportResult(match, 2));
        AssertChinese(() => tournament.ReportResult(bye, 0));
        AssertChinese(() => tournament.AddPlayer("迟到报名", Code(BattleTests.LoadPack("original"))));
        Assert.Equal(before, tournament.ToJson());
        tournament.ReportResult(match, -1);
        Assert.Equal(-1, match.WinnerSide);
        string reported = tournament.ToJson();
        AssertChinese(() => tournament.ReportResult(match, 1));
        Assert.Equal(reported, tournament.ToJson());
        Assert.Equal(1, tournament.Round);
        Assert.Same(match, Assert.Single(tournament.NextRound(), pairing => pairing.Id == match.Id));
    }

    [Theory]
    [InlineData("roundrobin", 5)]
    [InlineData("swiss", 7)]
    public void SavingAnUnfinishedRoundPreservesResultsAndContinuesWithTheSamePairings(string format, int players)
    {
        var db = BattleTests.LoadPack("original");
        var original = Make(format, players, db);
        var initial = original.NextRound();
        original.ReportResult(initial.First(pairing => !pairing.IsBye), 1);
        string json = original.ToJson();
        var restored = Tournament.FromJson(db, json);
        Assert.Equal(json, restored.ToJson());
        Assert.Equal(PlayerDump(original), PlayerDump(restored));
        Assert.Equal(StandingDump(original), StandingDump(restored));
        int guard = 20;
        while (!original.IsFinished && guard-- > 0)
        {
            var left = original.NextRound();
            var right = restored.NextRound();
            Assert.Equal(PairingDump(left), PairingDump(right));
            for (int i = left.Count - 1; i >= 0; i--)
            {
                if (left[i].WinnerSide.HasValue) continue;
                int result = (left[i].Round + left[i].Id) % 3 - 1;
                original.ReportResult(left[i], result);
                restored.ReportResult(right[i], result);
            }
            Assert.Equal(original.ToJson(), restored.ToJson());
            Assert.Equal(StandingDump(original), StandingDump(restored));
            // 也从已完成当前轮、尚未生成下一轮的存档继续。
            restored = Tournament.FromJson(db, restored.ToJson());
        }
        Assert.True(original.IsFinished);
        Assert.True(restored.IsFinished);
        Assert.Empty(restored.NextRound());
        Assert.Equal(original.ToJson(), restored.ToJson());
        Assert.Equal(StandingDump(original), StandingDump(Tournament.FromJson(db, original.ToJson())));
    }

    [Fact]
    public void SavedRegistrationCanContinueAndExplicitSwissRoundsArePreserved()
    {
        var db = BattleTests.LoadPack("original");
        var tournament = new Tournament(db, "swiss", 2);
        tournament.AddPlayer("甲\"\\雪", Code(db));
        var restored = Tournament.FromJson(db, tournament.ToJson());
        restored.AddPlayer("乙", Code(db, 1));
        restored.AddPlayer("丙", Code(db, 2));
        restored.AddPlayer("丁", Code(db, 3));
        Assert.Equal(2, restored.Rounds);
        Assert.Equal(0, restored.Round);
        Assert.Equal("甲\"\\雪", restored.Players[0].Name);
        for (int round = 1; round <= 2; round++)
            foreach (var pairing in restored.NextRound()) restored.ReportResult(pairing, -1);
        Assert.True(restored.IsFinished);
        Assert.Equal(2, restored.Round);
        Assert.Empty(restored.NextRound());
    }

    [Fact]
    public void InvalidTeamCodesAreRejectedWithoutConsumingAPlayerId()
    {
        var db = BattleTests.LoadPack("original");
        var tournament = new Tournament(db, "swiss");
        string[] good = db.Streamers.Take(6).Select(streamer => streamer.Id).ToArray();
        var codes = new List<string>
        {
            null, "", "not-a-team-code", Code(db).Substring(0, Code(db).Length - 2),
            Encode("other_pack", good), Encode(db.PackId, good.Take(5)),
            Encode(db.PackId, good.Concat(new[] { db.Streamers[6].Id })),
            Encode(db.PackId, good.Take(5).Concat(new[] { good[0] })),
            Encode(db.PackId, good.Take(5).Concat(new[] { "missing_streamer" }))
        };
        foreach (string code in codes)
        {
            string before = tournament.ToJson();
            AssertChinese(() => tournament.AddPlayer("测试", code));
            Assert.Empty(tournament.Players);
            Assert.Equal(before, tournament.ToJson());
        }
        tournament.AddPlayer("有效报名", " \n" + Code(db) + "\t");
        Assert.Equal(0, Assert.Single(tournament.Players).Id);
        Assert.Equal(good, tournament.Players[0].Team);
    }

    [Fact]
    public void ReturnedCollectionsAndDecodedTeamEditsCannotChangeTournamentState()
    {
        var tournament = Make("roundrobin", 4);
        string before = tournament.ToJson();
        var players = tournament.Players;
        var team = players[0].Team;
        TryEdit(players, list => list[0] = list[1]);
        TryEdit(team, list => list[0] = "missing_streamer");
        var decoded = TeamCode.Decode(tournament.Players[0].TeamCode);
        decoded.Ids.Clear();
        tournament.Standings().Clear();
        Assert.Equal(before, tournament.ToJson());
        var first = tournament.NextRound();
        var original = first.ToArray();
        first.Reverse();
        first.Clear();
        var again = tournament.NextRound();
        Assert.Equal(original.Select(pairing => pairing.Id), again.Select(pairing => pairing.Id));
        for (int i = 0; i < original.Length; i++) Assert.Same(original[i], again[i]);
    }

    [Fact]
    public void InvalidConfigurationAndTooFewPlayersCannotStartARound()
    {
        var db = BattleTests.LoadPack("original");
        Assert.Throws<ArgumentNullException>(() => new Tournament(null, "swiss"));
        AssertChinese(() => new Tournament(db, "knockout"));
        AssertChinese(() => new Tournament(db, "swiss", -1));
        AssertChinese(() => new Tournament(db, "roundrobin", 2));
        var tournament = new Tournament(db, "swiss");
        AssertChinese(() => tournament.NextRound());
        Assert.Equal(0, tournament.Round);
        tournament.AddPlayer("甲", Code(db));
        AssertChinese(() => tournament.NextRound());
        Assert.Equal(0, tournament.Round);
        tournament.AddPlayer("乙", Code(db, 1));
        Assert.Equal(1, tournament.Rounds);
        tournament.ReportResult(Assert.Single(tournament.NextRound()), -1);
        Assert.True(tournament.IsFinished);
    }

    [Fact]
    public void DamagedArchivesAndArchivesWithMismatchedContentOrTeamsAreRejectedInChinese()
    {
        var db = BattleTests.LoadPack("original");
        var tournament = Make("roundrobin", 5, db);
        var first = tournament.NextRound();
        foreach (var pairing in first.Where(pairing => !pairing.IsBye)) tournament.ReportResult(pairing, -1);
        tournament.NextRound();
        string valid = tournament.ToJson();
        foreach (string invalid in new[]
        {
            null, "", "坏掉", "{}", "[]", valid.Substring(0, valid.Length - 2),
            valid.Replace("\"version\":1", "\"version\":1,\"version\":1"),
            valid.Replace("\"version\":1", "\"version\":1,\"\\u0076ersion\":1"),
            valid.Replace("\"winnerSide\":-1", "\"winnerSide\":-1,\"winnerSide\":0")
        })
            AssertChinese(() => Tournament.FromJson(db, invalid));
        var changes = new Action<Dictionary<string, object>>[]
        {
            root => root["version"] = 99,
            root => root["packId"] = "missing_pack",
            root => root["contentHash"] = "wrong_fingerprint",
            root => root["format"] = "knockout",
            root => root["rounds"] = -1,
            root => Players(root)[0]["id"] = 99,
            root => Players(root)[0]["teamCode"] = "invalid_code",
            root => Players(root)[0]["teamCode"] = Encode(db.PackId, new[] { "missing_streamer" }),
            root => Rounds(root)[0][0]["player0"] = 99,
            root => Rounds(root)[0][0]["player1"] = Rounds(root)[0][0]["player0"],
            root => Rounds(root)[0][1]["id"] = Rounds(root)[0][0]["id"],
            root => Rounds(root)[0][0]["round"] = 9,
            root => Rounds(root)[0][0]["winnerSide"] = 2,
            root => Rounds(root)[0].First(pairing => Convert.ToInt32(pairing["player1"]) >= 0)["winnerSide"] = null,
            root => Rounds(root)[0].First(pairing => Convert.ToInt32(pairing["player1"]) == -1)["winnerSide"] = null
        };
        foreach (var change in changes)
        {
            var root = (Dictionary<string, object>)MiniJson.Parse(valid);
            change(root);
            AssertChinese(() => Tournament.FromJson(db, JsonSerializer.Serialize(root)));
        }
        var changedDb = BattleTests.LoadPack("original");
        changedDb.Streamers[0].Name += "改名";
        AssertChinese(() => Tournament.FromJson(changedDb, valid));
        Assert.Equal(valid, tournament.ToJson());
    }

    static Tournament Make(string format, int count, ContentDb db = null)
    {
        db = db ?? BattleTests.LoadPack("original");
        var tournament = new Tournament(db, format);
        for (int i = 0; i < count; i++) tournament.AddPlayer("选手" + i, Code(db, i));
        return tournament;
    }

    static string Code(ContentDb db, int offset = 0)
    {
        return Encode(db.PackId, Enumerable.Range(0, 6).Select(i => db.Streamers[(i + offset) % db.Streamers.Count].Id));
    }

    static string Encode(string pack, IEnumerable<string> ids)
    {
        return TeamCode.Encode(new TeamPreset { Name = "赛事队伍", PackId = pack, Ids = ids.ToList() });
    }

    static string Key(int left, int right) => Math.Min(left, right) + ":" + Math.Max(left, right);
    static bool Contains(Pairing pairing, int player) => pairing.Player0 == player || pairing.Player1 == player;
    static int Opponent(Pairing pairing, int player) => pairing.Player0 == player ? pairing.Player1 : pairing.Player0;
    static int SideOf(Pairing pairing, int player)
    {
        Assert.True(Contains(pairing, player));
        return pairing.Player0 == player ? 0 : 1;
    }

    static void AssertRound(List<Pairing> pairings, int count, int round, HashSet<string> meetings,
        HashSet<int> byePlayers, HashSet<int> ids)
    {
        Assert.Equal((count + 1) / 2, pairings.Count);
        Assert.Equal(count % 2, pairings.Count(pairing => pairing.IsBye));
        var participants = new List<int>();
        foreach (var pairing in pairings)
        {
            Assert.Equal(round, pairing.Round);
            Assert.True(ids.Add(pairing.Id), "对局编号重复。");
            Assert.InRange(pairing.Player0, 0, count - 1);
            participants.Add(pairing.Player0);
            if (pairing.IsBye)
            {
                Assert.Equal(-1, pairing.Player1);
                Assert.Equal(0, pairing.WinnerSide);
                Assert.True(byePlayers.Add(pairing.Player0), "同一选手重复轮空。");
            }
            else
            {
                Assert.InRange(pairing.Player1, 0, count - 1);
                Assert.NotEqual(pairing.Player0, pairing.Player1);
                Assert.Null(pairing.WinnerSide);
                participants.Add(pairing.Player1);
                Assert.True(meetings.Add(Key(pairing.Player0, pairing.Player1)), "重复对阵。");
            }
        }
        Assert.Equal(Enumerable.Range(0, count), participants.OrderBy(player => player));
    }

    sealed class Result
    {
        public readonly int Left, Right, Winner;
        public Result(Pairing pairing, int winner) { Left = pairing.Player0; Right = pairing.Player1; Winner = winner; }
    }

    static void AssertStandings(Tournament tournament, List<Result> results)
    {
        int count = tournament.Players.Count;
        var points = new int[count];
        var opponents = new int[count];
        var head = new int[count];
        var wins = new int[count];
        var draws = new int[count];
        var losses = new int[count];
        var byes = new int[count];
        foreach (var result in results)
        {
            if (result.Right < 0) { points[result.Left] += 3; wins[result.Left]++; byes[result.Left]++; }
            else if (result.Winner == -1)
            {
                points[result.Left]++; points[result.Right]++; draws[result.Left]++; draws[result.Right]++;
            }
            else
            {
                int winner = result.Winner == 0 ? result.Left : result.Right;
                int loser = result.Winner == 0 ? result.Right : result.Left;
                points[winner] += 3; wins[winner]++; losses[loser]++;
            }
        }
        foreach (var result in results.Where(result => result.Right >= 0))
        {
            opponents[result.Left] += points[result.Right];
            opponents[result.Right] += points[result.Left];
        }
        foreach (var result in results.Where(result => result.Right >= 0
            && points[result.Left] == points[result.Right] && opponents[result.Left] == opponents[result.Right]))
        {
            if (result.Winner == -1) { head[result.Left]++; head[result.Right]++; }
            else head[result.Winner == 0 ? result.Left : result.Right] += 3;
        }
        int[] order = Enumerable.Range(0, count).OrderByDescending(player => points[player])
            .ThenByDescending(player => opponents[player]).ThenByDescending(player => head[player]).ThenBy(player => player).ToArray();
        var rows = tournament.Standings();
        Assert.Equal(order, rows.Select(row => row.Player));
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            int player = order[i];
            Assert.Equal(i + 1, row.Rank);
            Assert.Equal(tournament.Players[player].Name, row.Name);
            Assert.Equal((points[player], opponents[player], head[player], wins[player], draws[player], losses[player], byes[player]),
                (row.Points, row.OpponentPoints, row.HeadToHeadPoints, row.Wins, row.Draws, row.Losses, row.Byes));
        }
    }

    static void TryEdit<T>(IReadOnlyList<T> source, Action<IList<T>> edit)
    {
        if (!(source is IList<T> list)) return;
        try { edit(list); }
        catch (NotSupportedException) { }
    }

    static void AssertChinese(Action action)
    {
        Exception error = Record.Exception(action);
        Assert.NotNull(error);
        Assert.Matches("[\u4e00-\u9fff]", error.Message);
    }

    static string[] PairingDump(IEnumerable<Pairing> pairings) => pairings.Select(pairing =>
        string.Join("|", pairing.Id, pairing.Round, pairing.Player0, pairing.Player1, pairing.IsBye, pairing.WinnerSide)).ToArray();
    static string[] PlayerDump(Tournament tournament) => tournament.Players.Select(player =>
        string.Join("|", player.Id, player.Name, player.TeamCode, string.Join(",", player.Team))).ToArray();
    static string[] StandingDump(Tournament tournament) => tournament.Standings().Select(row =>
        string.Join("|", row.Rank, row.Player, row.Name, row.Points, row.OpponentPoints, row.HeadToHeadPoints,
            row.Wins, row.Draws, row.Losses, row.Byes)).ToArray();
    static List<Dictionary<string, object>> Players(Dictionary<string, object> root) =>
        ((List<object>)root["players"]).Cast<Dictionary<string, object>>().ToList();
    static List<List<Dictionary<string, object>>> Rounds(Dictionary<string, object> root) =>
        ((List<object>)root["roundsPlayed"]).Select(round => ((List<object>)round).Cast<Dictionary<string, object>>().ToList()).ToList();
}
