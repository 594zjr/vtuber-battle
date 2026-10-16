using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace StreamDuel.Core
{
    public sealed class AiMatchupResult
    {
        public AiLevel Lower { get; }
        public AiLevel Higher { get; }
        public int LowerWins { get; }
        public int HigherWins { get; }
        public int Draws { get; }
        public int Truncated { get; }
        public int Games { get { return LowerWins + HigherWins + Draws; } }
        public double HigherWinRate { get { return (double)HigherWins / Games; } }

        internal AiMatchupResult(AiLevel lower, AiLevel higher, int lowerWins, int higherWins, int draws, int truncated)
        {
            Lower = lower;
            Higher = higher;
            LowerWins = lowerWins;
            HigherWins = higherWins;
            Draws = draws;
            Truncated = truncated;
        }
    }

    public sealed class AiLadderResult
    {
        public string PackId { get; }
        public string ContentHash { get; }
        public int GamesPerPair { get; }
        public uint Seed { get; }
        public int MaxTurns { get; }
        public int SamplingVersion { get; }
        public IReadOnlyList<AiMatchupResult> Matchups { get; }

        internal AiLadderResult(string packId, string contentHash, int games, uint seed, int maxTurns,
            int samplingVersion, IEnumerable<AiMatchupResult> matchups)
        {
            PackId = packId;
            ContentHash = contentHash;
            GamesPerPair = games;
            Seed = seed;
            MaxTurns = maxTurns;
            SamplingVersion = samplingVersion;
            Matchups = Array.AsReadOnly(matchups.ToArray());
        }

        public double WinRate(AiLevel row, AiLevel opponent)
        {
            if ((int)row < 0 || (int)row > 3) throw new ArgumentOutOfRangeException(nameof(row));
            if ((int)opponent < 0 || (int)opponent > 3) throw new ArgumentOutOfRangeException(nameof(opponent));
            if (row == opponent) return 0.5;
            var pair = Matchups.Single(p => (p.Lower == row && p.Higher == opponent) || (p.Higher == row && p.Lower == opponent));
            return (double)(pair.Lower == row ? pair.LowerWins : pair.HigherWins) / pair.Games;
        }
    }

    public static class AiRegression
    {
        public const int SamplingVersion = 1;

        public static AiLadderResult Run(ContentDb db, int games, uint seed)
        {
            if (db == null) throw new ArgumentNullException(nameof(db));
            if (games <= 0 || games % 2 != 0)
                throw new ArgumentOutOfRangeException(nameof(games), "每对局数必须是正偶数，确保两种难度都使用两边阵容和位置。");
            db.Validate();
            var ids = db.Streamers.Select(s => s.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray();
            if (ids.Length < 6) throw new ArgumentException("内容包至少需要六位主播。", nameof(db));
            string contentHash = db.ContentHash();
            var initialTeam = ids.Take(6).ToArray();
            // 抽样也使用 Battle 的随机源；停在首发选择，避免开场效果影响抽样序列。
            var sampler = new Battle(db, new IList<string>[] { initialTeam, initialTeam }, new[] { "抽样甲", "抽样乙" }, seed, true);
            var fixtures = new List<Fixture>();
            for (int game = 0; game < games; game += 2)
            {
                fixtures.Add(new Fixture
                {
                    Teams = new IList<string>[] { SampleTeam(ids, sampler), SampleTeam(ids, sampler) },
                    Seed = (uint)(sampler.Random() * 4294967296.0)
                });
            }

            var matchups = new List<AiMatchupResult>();
            for (int lower = 0; lower < 3; lower++)
            {
                for (int higher = lower + 1; higher < 4; higher++)
                {
                    int lowerWins = 0, higherWins = 0, draws = 0, truncated = 0;
                    foreach (var fixture in fixtures)
                    {
                        // 同一组阵容、种子打两局，只交换驾驶阵容的难度，平衡阵容和位置偏差。
                        for (int higherSide = 0; higherSide < 2; higherSide++)
                        {
                            var levels = higherSide == 0
                                ? new[] { (AiLevel)higher, (AiLevel)lower }
                                : new[] { (AiLevel)lower, (AiLevel)higher };
                            var battle = new Battle(db, fixture.Teams, new[] { "甲方", "乙方" }, fixture.Seed);
                            int steps = 0;
                            while (battle.Phase != Phase.Over && steps++ < Battle.TurnCap * 3)
                            {
                                if (battle.Phase == Phase.Choose)
                                {
                                    var a0 = Ai.Choose(battle, 0, levels[0]);
                                    var a1 = Ai.Choose(battle, 1, levels[1]);
                                    battle.ResolveTurn(a0, a1);
                                }
                                else if (battle.Phase == Phase.Replace)
                                {
                                    for (int side = 0; side < 2 && battle.Phase == Phase.Replace; side++)
                                        if (battle.Need[side]) battle.Replace(side, Ai.ChooseReplacement(battle, side, levels[side]));
                                }
                                else throw new InvalidOperationException("强度回归遇到未预期的对战阶段：" + battle.Phase);
                            }
                            if (battle.Phase != Phase.Over) { truncated++; draws++; }
                            else if (battle.Winner == -1) draws++;
                            else if (battle.Winner == higherSide) higherWins++;
                            else lowerWins++;
                        }
                    }
                    matchups.Add(new AiMatchupResult((AiLevel)lower, (AiLevel)higher, lowerWins, higherWins, draws, truncated));
                }
            }
            return new AiLadderResult(db.PackId, contentHash, games, seed, Battle.TurnCap, SamplingVersion, matchups);
        }

        static string[] SampleTeam(string[] ids, Battle sampler)
        {
            var shuffled = (string[])ids.Clone();
            for (int i = 0; i < 6; i++)
            {
                int j = i + (int)(sampler.Random() * (shuffled.Length - i));
                string value = shuffled[i];
                shuffled[i] = shuffled[j];
                shuffled[j] = value;
            }
            return shuffled.Take(6).ToArray();
        }

        sealed class Fixture
        {
            public IList<string>[] Teams;
            public uint Seed;
        }
    }

    public sealed class AiRegressionBaseline
    {
        public AiLadderResult Result { get; }
        public double AllowedDrop { get; }

        AiRegressionBaseline(AiLadderResult result, double allowedDrop)
        {
            Result = result;
            AllowedDrop = allowedDrop;
        }

        public static AiRegressionBaseline FromResult(AiLadderResult result, double allowedDrop = 0.05)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));
            if (double.IsNaN(allowedDrop) || double.IsInfinity(allowedDrop) || allowedDrop < 0 || allowedDrop > 1)
                throw new ArgumentOutOfRangeException(nameof(allowedDrop), "允许下降幅度必须在 0 到 1 之间。");
            return new AiRegressionBaseline(result, allowedDrop);
        }

        public IReadOnlyList<string> Check(AiLadderResult current)
        {
            if (current == null) throw new ArgumentNullException(nameof(current));
            var errors = new List<string>();
            if (Result.PackId != current.PackId || Result.GamesPerPair != current.GamesPerPair || Result.Seed != current.Seed
                || Result.MaxTurns != current.MaxTurns || Result.SamplingVersion != current.SamplingVersion)
            {
                errors.Add("基准与当前结果的内容包、局数、种子、回合上限或抽样版本不一致，不能比较。");
                return errors.AsReadOnly();
            }
            // 内容指纹只保留来源；规则或数值改变正是本检查需要比较的情况。
            foreach (var pair in current.Matchups)
            {
                string name = pair.Higher + " 对 " + pair.Lower;
                if (pair.Truncated != 0) errors.Add(name + " 存在未正常结束的对局。");
                if (pair.HigherWinRate <= 0.5) errors.Add(name + " 胜率未超过 50%。");
                double before = Result.WinRate(pair.Higher, pair.Lower);
                if (before - pair.HigherWinRate > AllowedDrop + 1e-12)
                    errors.Add(name + " 胜率下降超过允许幅度 " + (AllowedDrop * 100).ToString("0.######", CultureInfo.InvariantCulture) + " 个百分点。");
            }
            return errors.AsReadOnly();
        }

        public string ToJson()
        {
            var text = new StringBuilder();
            text.Append("{\n  \"schemaVersion\": 1,\n  \"samplingVersion\": ").Append(Result.SamplingVersion)
                .Append(",\n  \"packId\": ").Append(Quote(Result.PackId))
                .Append(",\n  \"contentHash\": ").Append(Quote(Result.ContentHash))
                .Append(",\n  \"gamesPerPair\": ").Append(Result.GamesPerPair)
                .Append(",\n  \"seed\": ").Append(Result.Seed)
                .Append(",\n  \"maxTurns\": ").Append(Result.MaxTurns)
                .Append(",\n  \"allowedDrop\": ").Append(AllowedDrop.ToString("R", CultureInfo.InvariantCulture))
                .Append(",\n  \"matchups\": [\n");
            for (int i = 0; i < Result.Matchups.Count; i++)
            {
                var pair = Result.Matchups[i];
                text.Append("    {\"lower\": ").Append(Quote(pair.Lower.ToString()))
                    .Append(", \"higher\": ").Append(Quote(pair.Higher.ToString()))
                    .Append(", \"lowerWins\": ").Append(pair.LowerWins)
                    .Append(", \"higherWins\": ").Append(pair.HigherWins)
                    .Append(", \"draws\": ").Append(pair.Draws)
                    .Append(", \"truncated\": ").Append(pair.Truncated).Append('}');
                if (i + 1 != Result.Matchups.Count) text.Append(',');
                text.Append('\n');
            }
            return text.Append("  ]\n}\n").ToString();
        }

        public static AiRegressionBaseline FromJson(string json)
        {
            var root = Object(MiniJson.Parse(json), "根对象");
            if (Integer(root, "schemaVersion", 1) != 1) throw new FormatException("不支持的基准格式版本。");
            int samplingVersion = (int)Integer(root, "samplingVersion", int.MaxValue);
            if (samplingVersion != AiRegression.SamplingVersion) throw new FormatException("不支持的基准抽样版本。");
            string packId = Text(root, "packId");
            string contentHash = Text(root, "contentHash");
            if (contentHash.Length != 16 || contentHash.Any(c => !(c >= '0' && c <= '9') && !(c >= 'a' && c <= 'f')))
                throw new FormatException("内容指纹必须是 16 位小写十六进制。");
            int games = (int)Integer(root, "gamesPerPair", int.MaxValue);
            if (games == 0 || games % 2 != 0) throw new FormatException("基准每对局数必须是正偶数。");
            uint seed = (uint)Integer(root, "seed", uint.MaxValue);
            int maxTurns = (int)Integer(root, "maxTurns", int.MaxValue);
            if (maxTurns == 0) throw new FormatException("回合上限必须为正数。");
            double allowedDrop = Number(root, "allowedDrop");
            if (allowedDrop < 0 || allowedDrop > 1) throw new FormatException("允许下降幅度必须在 0 到 1 之间。");
            object value;
            if (!root.TryGetValue("matchups", out value) || !(value is List<object> pairs) || pairs.Count != 6)
                throw new FormatException("基准必须包含六组难度对局。");
            var matchups = new List<AiMatchupResult>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (object item in pairs)
            {
                var pair = Object(item, "对局");
                AiLevel lower = Level(Text(pair, "lower")), higher = Level(Text(pair, "higher"));
                if (lower >= higher || !seen.Add(lower + "/" + higher)) throw new FormatException("难度对局重复或顺序错误。");
                int lowerWins = (int)Integer(pair, "lowerWins", games), higherWins = (int)Integer(pair, "higherWins", games);
                int draws = (int)Integer(pair, "draws", games), truncated = (int)Integer(pair, "truncated", games);
                if ((long)lowerWins + higherWins + draws != games || truncated > draws)
                    throw new FormatException("胜负、平局或未结束局数与总局数不一致。");
                matchups.Add(new AiMatchupResult(lower, higher, lowerWins, higherWins, draws, truncated));
            }
            var result = new AiLadderResult(packId, contentHash, games, seed, maxTurns, samplingVersion,
                matchups.OrderBy(p => p.Lower).ThenBy(p => p.Higher));
            return new AiRegressionBaseline(result, allowedDrop);
        }

        static Dictionary<string, object> Object(object value, string label)
        {
            return value as Dictionary<string, object> ?? throw new FormatException(label + " 必须是 JSON 对象。");
        }

        static string Text(Dictionary<string, object> obj, string key)
        {
            object value;
            if (!obj.TryGetValue(key, out value) || !(value is string text) || string.IsNullOrWhiteSpace(text))
                throw new FormatException(key + " 必须是非空字符串。");
            return text;
        }

        static double Number(Dictionary<string, object> obj, string key)
        {
            object value;
            if (!obj.TryGetValue(key, out value) || !(value is double number) || double.IsNaN(number) || double.IsInfinity(number))
                throw new FormatException(key + " 必须是有限数值。");
            return number;
        }

        static long Integer(Dictionary<string, object> obj, string key, long maximum)
        {
            double number = Number(obj, key);
            if (number < 0 || number > maximum || number != Math.Truncate(number)) throw new FormatException(key + " 不是范围内的非负整数。");
            return (long)number;
        }

        static AiLevel Level(string name)
        {
            for (int i = 0; i < 4; i++) if (((AiLevel)i).ToString() == name) return (AiLevel)i;
            throw new FormatException("未知电脑难度：" + name);
        }

        static string Quote(string value)
        {
            var text = new StringBuilder("\"");
            foreach (char c in value)
            {
                if (c == '\\' || c == '"') text.Append('\\').Append(c);
                else if (c < 32) text.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                else text.Append(c);
            }
            return text.Append('"').ToString();
        }
    }
}
