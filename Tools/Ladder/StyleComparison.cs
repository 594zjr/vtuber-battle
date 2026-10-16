using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StreamDuel.Core;

namespace StreamDuel.Ladder
{
    internal static class StyleComparison
    {
        const string PackId = "original_wave3";
        const string Protocol = "T136-style-comparison-v1";
        const string StyledArm = "styled", BaselineArm = "unstyled";
        static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions { IncludeFields = true, WriteIndented = true };

        sealed class Fixture
        {
            public int Id;
            public uint Seed;
            public string[] TargetTeam, OpponentTeam;
            public int[] LegTargetSides = { 0, 1 };
        }

        sealed class GameResult
        {
            public int Id, FixtureId, Leg, TargetSide, Winner, Turns;
            public uint Seed;
            public string Arm, TargetResult, ContentHash, FinalStateHash, RecordSha256;
            public string[][] Teams;
        }

        sealed class Score
        {
            public int Games, Side0Games, Side1Games, Wins, Draws, Losses;
            public double WinRate, ScoreRate;
        }

        public static int Run(string[] args, Func<string, ContentDb> loadPack)
        {
            var options = StyleComparisonOptions.Parse(args);
            if (options.Help)
            {
                Console.WriteLine("用法：dotnet run -c Release --project Tools/Ladder -- --compare-styles [--games 300] [--seed 1] [--level normal|hard] [--out ./ladder_styles_out]");
                Console.WriteLine("固定 original_wave3 的十二位主播，每次随机分成互不重叠的六人目标队与对手队；保持阵容和种子，各换边一次。");
                Console.WriteLine("有风格组仅目标队保留风格；无风格组双方均清空风格。每局独立载入内容，在创建战斗前屏蔽风格，不改输入文件或规则数值。");
                Console.WriteLine("--games 表示每组场数，必须为正偶数，默认各 300 场、总计 600 场；默认普通难度，双方使用相同难度。");
                Console.WriteLine("只统计目标队胜率（胜/全部场数）与得分率（胜+0.5平）/全部场数；门槛为无风格胜率减有风格胜率不超过 3 个百分点。");
                Console.WriteLine("输出 fixtures.json、comparison.json、games.csv；小样本仅用于检查，正式验收要求每组至少 300 场且十二位主播均作为有风格成员入队。");
                return 0;
            }

            var source = loadPack(PackId);
            source.Validate();
            var candidates = source.Streamers.OrderBy(s => s.Id, StringComparer.Ordinal).ToArray();
            if (source.PackId != PackId || candidates.Length != 12 || candidates.Any(s => !s.Id.StartsWith("og3_", StringComparison.Ordinal)))
                throw new InvalidOperationException("风格比较要求第三期原创包恰好包含十二位 og3_ 主播。");
            if (candidates.Any(s => s.AiStyle == null))
                throw new InvalidOperationException("风格比较要求第三期十二位主播均已配置有效 aiStyle。");
            string sourceHash = source.ContentHash();
            var fixtures = GenerateFixtures(source, candidates.Select(s => s.Id).ToArray(), options.Games / 2, options.Seed);
            var coverage = candidates.Select(s => new
            {
                Id = s.Id, AiStyle = s.AiStyle,
                StyledRosterGames = fixtures.Count(f => f.TargetTeam.Contains(s.Id)) * 2,
                UnstyledOpponentRosterGames = fixtures.Count(f => f.OpponentTeam.Contains(s.Id)) * 2
            }).ToArray();
            bool allStreamersStyled = coverage.All(row => row.StyledRosterGames > 0);
            if (options.Games >= 300 && !allStreamersStyled)
                throw new InvalidOperationException("冻结赛程未覆盖全部十二位有风格主播，请报告此种子的覆盖不足；不能把本次结果记为验收通过。");
            var games = new List<GameResult>(options.Games * 2);
            Console.WriteLine("风格比较：每组 " + options.Games + " 场；配对样本 " + fixtures.Count + "；种子 " + options.Seed
                + "；难度 " + (options.Level == AiLevel.Hard ? "困难" : "普通") + "。");
            foreach (var fixture in fixtures)
            {
                for (int leg = 0; leg < 2; leg++)
                {
                    Play(loadPack, sourceHash, fixture, leg, StyledArm, options.Level, games);
                    Play(loadPack, sourceHash, fixture, leg, BaselineArm, options.Level, games);
                }
                if (fixture.Id % 10 == 0 || fixture.Id == fixtures.Count)
                    Console.WriteLine("已完成 " + fixture.Id + "/" + fixtures.Count + " 个配对样本。");
            }

            var styled = Tally(games, StyledArm);
            var baseline = Tally(games, BaselineArm);
            double winRateDrop = 100 * (baseline.WinRate - styled.WinRate);
            double scoreRateDrop = 100 * (baseline.ScoreRate - styled.ScoreRate);
            bool withinThreshold = (long)(baseline.Wins - styled.Wins) * 100 <= (long)options.Games * 3;
            var fixtureReport = new
            {
                SchemaVersion = 1, Protocol, PackId, Seed = options.Seed, GamesPerArm = options.Games,
                Fixtures = fixtures
            };
            string fixtureJson = ReportJson(fixtureReport);
            var report = new
            {
                SchemaVersion = 1, Protocol, PackId, SourceContentHash = sourceHash,
                EngineAssemblySha256 = FileHash(typeof(Battle).Assembly.Location),
                Seed = options.Seed, Level = options.Level == AiLevel.Hard ? "hard" : "normal",
                GamesPerArm = options.Games, FixtureCount = fixtures.Count, GameCount = games.Count,
                SelectionOrder = "side0-then-side1", FixtureSha256 = Hash(fixtureJson),
                Styled = styled, Unstyled = baseline,
                WinRateDropPercentagePoints = winRateDrop, ScoreRateDropPercentagePoints = scoreRateDrop,
                WinRateDropAtMostThreePoints = withinThreshold,
                MeetsRequiredSampleSize = options.Games >= 300, AllStreamersStyled = allStreamersStyled,
                AcceptancePassed = options.Games >= 300 && allStreamersStyled && withinThreshold,
                Coverage = coverage, Games = games
            };
            // 先完成全部对局，再输出结果，异常对局不能留下看似完整的比较报告。
            Directory.CreateDirectory(options.OutputDirectory);
            File.WriteAllText(Path.Combine(options.OutputDirectory, "fixtures.json"), fixtureJson, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(options.OutputDirectory, "comparison.json"), ReportJson(report), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(options.OutputDirectory, "games.csv"), Csv(games), new UTF8Encoding(false));
            PrintScore("有风格目标队", styled);
            PrintScore("无风格目标队", baseline);
            Console.WriteLine("目标队胜率下降 " + Number(winRateDrop) + " 个百分点；得分率下降 " + Number(scoreRateDrop)
                + " 个百分点；胜率下降不超过 3 个百分点：" + (withinThreshold ? "是" : "否") + "。");
            Console.WriteLine("验收条件（每组至少 300 场、十二位有风格成员全覆盖、胜率门槛）满足："
                + (report.AcceptancePassed ? "是" : "否") + "；已输出 fixtures.json、comparison.json、games.csv。");
            return 0;
        }

        static List<Fixture> GenerateFixtures(ContentDb db, string[] candidates, int count, uint seed)
        {
            var rng = new Rng(seed);
            var fixtures = new List<Fixture>(count);
            // 抽样独立于对战随机流；两种策略必须收到同一份事先冻结的赛程。
            for (int index = 0; index < count; index++)
            {
                var shuffled = (string[])candidates.Clone();
                for (int i = shuffled.Length - 1; i > 0; i--)
                {
                    int j = (int)(rng.Next() * (i + 1));
                    string value = shuffled[i]; shuffled[i] = shuffled[j]; shuffled[j] = value;
                }
                var fixture = new Fixture
                {
                    Id = index + 1, TargetTeam = shuffled.Take(6).ToArray(), OpponentTeam = shuffled.Skip(6).ToArray(),
                    Seed = (uint)(rng.Next() * 4294967296.0)
                };
                foreach (var team in new[] { fixture.TargetTeam, fixture.OpponentTeam })
                {
                    var errors = TeamCheck.Validate(db, null, team);
                    if (errors.Count > 0) throw new InvalidOperationException("比较阵容不合法：" + string.Join("；", errors));
                }
                fixtures.Add(fixture);
            }
            return fixtures;
        }

        static void Play(Func<string, ContentDb> loadPack, string sourceHash, Fixture fixture, int leg,
            string arm, AiLevel level, List<GameResult> games)
        {
            var db = loadPack(PackId);
            if (db.ContentHash() != sourceHash)
                throw new InvalidOperationException("比较期间内容发生变化，拒绝混合不同内容版本的结果。");
            var targetIds = new HashSet<string>(fixture.TargetTeam, StringComparer.Ordinal);
            foreach (var streamer in db.Streamers)
                if (arm == BaselineArm || !targetIds.Contains(streamer.Id)) streamer.AiStyle = null;
            db.Validate();
            int targetSide = fixture.LegTargetSides[leg];
            var teams = targetSide == 0 ? new[] { fixture.TargetTeam, fixture.OpponentTeam }
                : new[] { fixture.OpponentTeam, fixture.TargetTeam };
            var battle = new Battle(db, new IList<string>[] { teams[0], teams[1] }, new[] { "甲方", "乙方" }, fixture.Seed);
            int guard = 0;
            while (battle.Phase != Phase.Over)
            {
                if (++guard > 1000) throw new InvalidOperationException("风格比较对局未在预期操作次数内结束，样本 " + fixture.Id + "。");
                if (battle.Phase == Phase.Choose)
                {
                    var first = Ai.Choose(battle, 0, level);
                    var second = Ai.Choose(battle, 1, level);
                    battle.ResolveTurn(first, second);
                }
                else if (battle.Phase == Phase.Replace)
                {
                    for (int side = 0; side < 2 && battle.Phase == Phase.Replace; side++)
                        if (battle.Need[side]) battle.Replace(side, Ai.ChooseReplacement(battle, side, level));
                }
                else throw new InvalidOperationException("风格比较不支持此对战阶段：" + battle.Phase);
            }
            games.Add(new GameResult
            {
                Id = games.Count + 1, FixtureId = fixture.Id, Leg = leg + 1, TargetSide = targetSide,
                Seed = fixture.Seed, Arm = arm, Teams = teams, Winner = battle.Winner,
                TargetResult = battle.Winner == -1 ? "draw" : battle.Winner == targetSide ? "win" : "loss",
                Turns = battle.Record.Events.Count(entry => entry.Type == BattleRecordEventType.Turn),
                ContentHash = db.ContentHash(), FinalStateHash = battle.StateHash().ToString("x16", CultureInfo.InvariantCulture),
                RecordSha256 = Hash(battle.Record.ToJson())
            });
        }

        static Score Tally(IEnumerable<GameResult> games, string arm)
        {
            var score = new Score();
            foreach (var game in games.Where(game => game.Arm == arm))
            {
                score.Games++;
                if (game.TargetSide == 0) score.Side0Games++; else score.Side1Games++;
                if (game.Winner == -1) score.Draws++;
                else if (game.Winner == game.TargetSide) score.Wins++;
                else score.Losses++;
            }
            score.WinRate = (double)score.Wins / score.Games;
            score.ScoreRate = (score.Wins + 0.5 * score.Draws) / score.Games;
            return score;
        }

        static void PrintScore(string label, Score score)
        {
            Console.WriteLine(label + "：" + score.Games + " 场（side0 " + score.Side0Games + "/side1 " + score.Side1Games
                + "），胜 " + score.Wins + "、平 " + score.Draws + "、负 " + score.Losses + "；胜率 "
                + Number(100 * score.WinRate) + "%；得分率 " + Number(100 * score.ScoreRate) + "%。");
        }

        static string Csv(IEnumerable<GameResult> games)
        {
            var output = new StringBuilder("game_id,fixture_id,leg,arm,seed,target_side,team0_json,team1_json,winner,target_result,turns,content_hash,final_state_hash,record_sha256\n");
            foreach (var game in games)
            {
                var cells = new[]
                {
                    Number(game.Id), Number(game.FixtureId), Number(game.Leg), game.Arm, Number(game.Seed), Number(game.TargetSide),
                    JsonSerializer.Serialize(game.Teams[0]), JsonSerializer.Serialize(game.Teams[1]), Number(game.Winner), game.TargetResult,
                    Number(game.Turns), game.ContentHash, game.FinalStateHash, game.RecordSha256
                };
                output.Append(string.Join(",", cells.Select(value => value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0
                    ? value : "\"" + value.Replace("\"", "\"\"") + "\""))).Append('\n');
            }
            return output.ToString();
        }

        static string ReportJson(object value)
        {
            // 缩进序列化在不同系统使用不同换行，规范化后再计算摘要和保存。
            return JsonSerializer.Serialize(value, JsonOptions).Replace("\r\n", "\n") + "\n";
        }

        static string Hash(string text)
        {
            using (var sha = SHA256.Create()) return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
        }

        static string FileHash(string path)
        {
            using (var input = File.OpenRead(path))
            using (var sha = SHA256.Create()) return Convert.ToHexString(sha.ComputeHash(input)).ToLowerInvariant();
        }

        static string Number(long value) { return value.ToString(CultureInfo.InvariantCulture); }
        static string Number(double value) { return value.ToString("F6", CultureInfo.InvariantCulture); }
    }
}
