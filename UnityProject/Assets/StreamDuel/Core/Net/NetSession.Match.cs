using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace StreamDuel.Core
{
    public sealed class NetMatchGameResult
    {
        readonly Battle snapshot;
        readonly string recordJson;
        public int Number { get; }
        public uint Seed { get; }
        public int Winner { get; }
        public ulong StateHash { get; }
        public Battle Battle { get { return snapshot.Clone(); } }
        public BattleRecord Record { get { return BattleRecord.FromJson(recordJson); } }

        internal NetMatchGameResult(int number, Battle battle)
        {
            Number = number;
            Seed = battle.Seed;
            Winner = battle.Winner;
            StateHash = battle.StateHash();
            snapshot = battle.Clone();
            recordJson = battle.Record.ToJson();
        }
    }

    public sealed partial class NetSession
    {
        public const int RulesetProtocolVersion = 5;
        enum MatchSyncStage { Hello, Ruleset, Game, Playing, Result, End, Finished }

        Ruleset matchRuleset;
        Match match;
        bool rulesetProtocol, matchConfirmed;
        uint matchSeed;
        MatchSyncStage matchStage;
        double matchWaitSeconds;
        readonly List<NetMatchGameResult> gameResults = new List<NetMatchGameResult>();
        readonly int?[] openingChoices = new int?[2];

        public bool IsRulesetMatch { get { return matchRuleset != null; } }
        public Ruleset Ruleset { get { return matchRuleset == null ? null : matchRuleset.Copy(); } }
        public string RulesetHash { get; private set; }
        public int GameNumber { get; private set; }
        public uint GameSeed { get { return unchecked(matchSeed + (uint)Math.Max(0, GameNumber - 1)); } }
        public IReadOnlyList<int> Wins { get { return Array.AsReadOnly(match == null ? new[] { 0, 0 } : match.Wins); } }
        public int Draws { get { return match == null ? 0 : match.Draws; } }
        public IReadOnlyList<NetMatchGameResult> GameResults { get { return gameResults.AsReadOnly(); } }
        public bool MatchConfirmed { get { return IsRulesetMatch && matchConfirmed && State == NetState.Over; } }
        public int MatchWinner
        {
            get
            {
                if (match == null || !match.Over) return -1;
                var scores = match.Wins;
                return scores[0] == scores[1] ? -1 : scores[0] > scores[1] ? 0 : 1;
            }
        }

        bool ConfigureRuleset(Ruleset ruleset, bool legacyCasual, double? turnSeconds, double? bankSeconds)
        {
            if (ruleset == null)
            {
                if (legacyCasual) { Fault(NetState.Error, "旧协议休闲兼容需要明确提供休闲赛制。"); return false; }
                return true;
            }
            try
            {
                var copy = ruleset.Copy();
                copy.Validate(db);
                copy.Modifiers = BattleModifiers.Normalize(copy.Modifiers).ToList();
                if (turnSeconds.HasValue || bankSeconds.HasValue)
                    throw new ArgumentException("联网赛制的计时由赛制统一决定，不能额外指定计时参数。");
                if (legacyCasual && (copy.BestOf != 1 || copy.BanOne || copy.LeadPick || copy.Season != null
                    || copy.Modifiers.Count != 0 || copy.TurnSeconds != 0 || copy.BankSeconds != 0))
                    throw new ArgumentException("旧协议只能使用无禁用、首发、赛季、修饰器和计时的休闲单局，不能降级其他赛制。");
                matchRuleset = copy;
                RulesetHash = FingerprintRuleset(copy);
                rulesetProtocol = !legacyCasual;
                matchStage = MatchSyncStage.Hello;
                return true;
            }
            catch (Exception error) when (error is ArgumentException || error is FormatException || error is KeyError)
            {
                Fault(NetState.Error, "联网赛制配置无效：" + error.Message);
                return false;
            }
        }

        static string FingerprintRuleset(Ruleset ruleset)
        {
            using (var sha = SHA256.Create())
                return Hex(sha.ComputeHash(Encoding.UTF8.GetBytes("StreamDuel.NetRuleset.v1\n" + ruleset.ToJson())));
        }

        string RulesetHello()
        {
            if (!rulesetProtocol) return string.Empty;
            return ",\"ruleset\":" + (MySide == 0 ? matchRuleset.ToJson() : "null")
                + ",\"rulesetHash\":" + NetJson.Quote(RulesetHash);
        }

        void ReceiveRulesetHello(Dictionary<string, object> obj)
        {
            if (!rulesetProtocol) return;
            string peerHash = NetJson.Text(obj, "rulesetHash");
            if (!LowerHex(peerHash, 64)) throw new FormatException("赛制指纹必须是 64 位小写十六进制字符串。");
            if (!obj.TryGetValue("ruleset", out var value)) throw new FormatException("握手缺少完整赛制字段。");
            if (MySide == 0)
            {
                if (value != null) throw new FormatException("赛制由主机声明，客机只能确认预期赛制指纹。");
            }
            else
            {
                var received = ReadNetworkRuleset(value);
                received.Validate(db);
                received.Modifiers = BattleModifiers.Normalize(received.Modifiers).ToList();
                if (FingerprintRuleset(received) != peerHash) throw new FormatException("主机赛制内容与赛制指纹不一致。");
            }
            if (peerHash != RulesetHash) throw new FormatException("双方赛制不一致，请使用相同的完整赛制配置。");
        }

        static Ruleset ReadNetworkRuleset(object value)
        {
            if (!(value is Dictionary<string, object> source)) throw new FormatException("主机赛制必须是完整 JSON 对象。");
            var fields = new[] { "id", "name", "season", "banOne", "leadPick", "bestOf", "modifiers", "turnSeconds", "bankSeconds" };
            if (source.Count != fields.Length || fields.Any(key => !source.ContainsKey(key)))
                throw new FormatException("主机赛制字段缺失或包含未知字段。");
            string Text(string key) { return source[key] == null ? null : NetJson.Text(source, key); }
            bool Flag(string key)
            {
                if (!(source[key] is bool flag)) throw new FormatException("赛制「" + key + "」必须是布尔值。");
                return flag;
            }
            var modifiers = new List<string>();
            if (source["modifiers"] != null)
                foreach (object item in NetJson.Array(source, "modifiers"))
                    modifiers.Add(item as string ?? throw new FormatException("赛制修饰器必须是字符串数组。"));
            return new Ruleset
            {
                Id = Text("id"), Name = Text("name"), Season = Text("season"), BanOne = Flag("banOne"), LeadPick = Flag("leadPick"),
                BestOf = (int)NetJson.Integer(source, "bestOf", 1, 5), Modifiers = modifiers,
                TurnSeconds = (int)NetJson.Integer(source, "turnSeconds", 0, int.MaxValue),
                BankSeconds = (int)NetJson.Integer(source, "bankSeconds", 0, int.MaxValue)
            };
        }

        void StartRulesetMatch(IList<string>[] teams, string[] names, uint seed)
        {
            match = new Match(db, matchRuleset, teams, names, seed);
            matchSeed = seed;
            if (rulesetProtocol)
            {
                ChangeMatchStage(MatchSyncStage.Ruleset);
                Send("{\"type\":\"ruleset-ready\",\"rulesetHash\":" + NetJson.Quote(RulesetHash) + "}");
            }
            else StartNextMatchGame();
        }

        void StartNextMatchGame()
        {
            Battle = match.StartNextGame();
            GameNumber = gameResults.Count + 1;
            rngCheckpoint = Battle.NetRngState;
            Clock = matchRuleset.TurnSeconds == 0 && matchRuleset.BankSeconds == 0 ? null
                : new TurnClock(matchRuleset.TurnSeconds, matchRuleset.BankSeconds);
            HasSubmitted = false;
            if (rulesetProtocol)
            {
                ChangeMatchStage(MatchSyncStage.Game);
                Send("{\"type\":\"game-ready\",\"game\":" + Number(GameNumber)
                    + ",\"seed\":" + GameSeed.ToString(CultureInfo.InvariantCulture)
                    + ",\"hash\":" + NetJson.Quote(Battle.StateHash().ToString("x16", CultureInfo.InvariantCulture)) + "}");
            }
            else BeginPhase();
        }

        bool ReceiveMatchControl(string type, Dictionary<string, object> obj)
        {
            if (type != "ruleset-ready" && type != "game-ready" && type != "game-result" && type != "match-end") return false;
            if (!rulesetProtocol) throw new FormatException("旧协议不能接收多局赛制同步消息。");
            if (type == "ruleset-ready")
            {
                if (matchStage != MatchSyncStage.Ruleset || match == null) throw new FormatException("赛制确认重复或阶段不匹配。");
                if (NetJson.Text(obj, "rulesetHash") != RulesetHash) throw new FormatException("对方确认的赛制指纹不一致。");
                StartNextMatchGame();
                return true;
            }
            RequireGameNumber(obj);
            if (type == "game-ready")
            {
                if (matchStage != MatchSyncStage.Game) throw new FormatException("开局确认重复或阶段不匹配。");
                if (NetJson.Integer(obj, "seed", 0, uint.MaxValue) != GameSeed)
                    throw new FormatException("对方确认的局号种子不一致。");
                Battle.RestoreReplayRng(rngCheckpoint);
                if (NetJson.Text(obj, "hash") != Battle.StateHash().ToString("x16", CultureInfo.InvariantCulture))
                { Fault(NetState.Desync, "开局状态不同步，整场不计分。"); return true; }
                ChangeMatchStage(MatchSyncStage.Playing);
                BeginPhase();
                return true;
            }
            if (type == "game-result")
            {
                if (matchStage != MatchSyncStage.Result) throw new FormatException("单局比分确认重复或尚未核对终局哈希。");
                var result = gameResults[gameResults.Count - 1];
                if (NetJson.Integer(obj, "winner", -1, 1) != result.Winner
                    || NetJson.Text(obj, "hash") != result.StateHash.ToString("x16", CultureInfo.InvariantCulture))
                { Fault(NetState.Desync, "单局胜负或终局哈希不同步，整场不计分。"); return true; }
                RequireMatchScore(obj);
                if (!match.Over) StartNextMatchGame();
                else
                {
                    ChangeMatchStage(MatchSyncStage.End);
                    Send("{\"type\":\"match-end\",\"game\":" + Number(GameNumber)
                        + ",\"winner\":" + Number(MatchWinner) + ScoreFields()
                        + ",\"digest\":" + NetJson.Quote(MatchResultDigest()) + "}");
                }
                return true;
            }
            if (matchStage != MatchSyncStage.End) throw new FormatException("整场确认重复或比赛尚未完成。");
            RequireMatchScore(obj);
            if (NetJson.Integer(obj, "winner", -1, 1) != MatchWinner || NetJson.Text(obj, "digest") != MatchResultDigest())
            { Fault(NetState.Desync, "整场结果不同步，不能结算排位。"); return true; }
            ChangeMatchStage(MatchSyncStage.Finished);
            matchConfirmed = true;
            State = NetState.Over;
            return true;
        }

        void ConfirmMatchGame()
        {
            if (gameResults.Count != GameNumber - 1) throw new InvalidOperationException("本局结果已经确认，不能重复上报。");
            var result = new NetMatchGameResult(GameNumber, Battle);
            match.ReportResult(Battle);
            gameResults.Add(result);
            if (!rulesetProtocol)
            {
                matchConfirmed = true;
                State = NetState.Over;
                return;
            }
            ChangeMatchStage(MatchSyncStage.Result);
            Send("{\"type\":\"game-result\",\"game\":" + Number(GameNumber) + ",\"winner\":" + Number(result.Winner)
                + ",\"hash\":" + NetJson.Quote(result.StateHash.ToString("x16", CultureInfo.InvariantCulture)) + ScoreFields() + "}");
        }

        string ScoreFields()
        {
            var scores = match.Wins;
            return ",\"wins\":[" + Number(scores[0]) + "," + Number(scores[1]) + "],\"draws\":" + Number(match.Draws);
        }

        void RequireMatchScore(Dictionary<string, object> obj)
        {
            var values = NetJson.Array(obj, "wins");
            var scores = match.Wins;
            if (values.Count != 2 || !(values[0] is double first) || !(values[1] is double second)
                || first != scores[0] || second != scores[1] || NetJson.Integer(obj, "draws", 0, 5) != match.Draws)
                throw new FormatException("双方确认的整场比分不一致，不能计分。");
        }

        string MatchResultDigest()
        {
            var text = new StringBuilder("StreamDuel.NetMatch.v1\n").Append(RulesetHash).Append('\n');
            foreach (var result in gameResults)
                text.Append(Number(result.Number)).Append(':').Append(result.Seed.ToString(CultureInfo.InvariantCulture))
                    .Append(':').Append(Number(result.Winner)).Append(':').Append(result.StateHash.ToString("x16", CultureInfo.InvariantCulture)).Append('\n');
            using (var sha = SHA256.Create()) return Hex(sha.ComputeHash(Encoding.UTF8.GetBytes(text.ToString())));
        }

        void RequireGameNumber(Dictionary<string, object> obj)
        {
            if (GameNumber < 1 || NetJson.Integer(obj, "game", 1, 5) != GameNumber)
                throw new FormatException("消息局号不匹配，不能将上一局或未来局的操作用于当前局。");
        }

        string GameField { get { return rulesetProtocol ? ",\"game\":" + Number(GameNumber) : string.Empty; } }

        void ChangeMatchStage(MatchSyncStage stage)
        {
            matchStage = stage;
            matchWaitSeconds = 0;
            TimerRevision++;
            State = NetState.Waiting;
        }

        bool TickMatchSynchronization(double seconds)
        {
            if (!rulesetProtocol || Terminal) return false;
            if (matchStage == MatchSyncStage.Playing)
            {
                if (Clock != null && phase != Phase.Ban && phase != Phase.Lead) return false;
                int peer = 1 - MySide;
                bool peerChoosing = phase == Phase.Choose || phase == Phase.Ban || phase == Phase.Lead || needsReplacement[peer];
                bool waitingReveal = peerChoosing && AllCommitmentsReady() && !revealed[peer];
                // 无计时赛制仍限制协议确认等待；普通选招、禁用和首发的人工思考时间不受此限制。
                if (hashes.Count == 0 && !terminationFrozen && !waitingReveal) return false;
            }
            matchWaitSeconds += seconds;
            if (matchWaitSeconds >= TimerGraceSeconds)
                Fault(NetState.Error, "等待对方赛制、开局或整场结果确认超过 10 秒，整场不计分。");
            return true;
        }

        void ResetMatchWait()
        {
            if (!rulesetProtocol) return;
            matchWaitSeconds = 0;
            TimerRevision++;
        }

        public void SubmitBan(int foeIndex) { SubmitOpening(Phase.Ban, foeIndex); }
        public void SubmitLead(int index) { SubmitOpening(Phase.Lead, index); }

        void SubmitOpening(Phase expected, int index)
        {
            var required = expected == Phase.Ban ? NetState.Ban : NetState.Lead;
            if (State != required || phase != expected || HasSubmitted || terminationFrozen || !ValidOpening(MySide, index))
                throw new InvalidOperationException("现在不能提交该禁用或首发位置，或已经提交。");
            CommitSelection(expected == Phase.Ban ? "ban" : "lead", BattleAction.SwitchTo(index).ToWire());
        }

        bool ValidOpening(int side, int index)
        {
            if (!rulesetProtocol || Battle == null || (phase != Phase.Ban && phase != Phase.Lead)) return false;
            var team = Battle.Sides[phase == Phase.Ban ? 1 - side : side].Team;
            return index >= 0 && index < team.Count;
        }
    }
}
