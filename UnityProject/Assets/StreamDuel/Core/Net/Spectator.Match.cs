using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace StreamDuel.Core
{
    public sealed class SpectatorMatchGameResult
    {
        readonly int[] wins;
        readonly Battle battle;
        public int GameNumber { get; }
        public int Winner { get; }
        public int[] Wins { get { return (int[])wins.Clone(); } }
        public int Draws { get; }
        public Battle Battle { get { return battle.Clone(); } }

        internal SpectatorMatchGameResult(int gameNumber, int[] wins, int draws, Battle battle)
        {
            GameNumber = gameNumber;
            this.wins = (int[])wins.Clone();
            Draws = draws;
            this.battle = battle.Clone();
            Winner = battle.Winner;
        }
    }

    public sealed class SpectatorMatchFeed
    {
        readonly Ruleset ruleset;
        readonly int delayTurns;
        readonly bool chatEnabled;
        readonly List<MatchViewer> viewers = new List<MatchViewer>();
        readonly int[] wins = new int[2];
        SpectatorFeed feed;
        Battle source;
        BattleRecord firstOpening;
        string contentHash;
        int[] openingWins;
        int openingDraws;
        int finishedWinner;
        string finishedHash;
        bool gameFinished;

        public const int ProtocolVersion = 1;
        public int GameNumber { get; private set; }
        public int[] ReleasedWins { get { return (int[])wins.Clone(); } }
        public int MatchDraws { get; private set; }
        public bool IsFinished { get; private set; }

        sealed class MatchViewer
        {
            internal INetTransport Transport;
            internal bool Joined, HeaderSent, EverAttached, InputClosed, Failed, ResultSent, Finished;
            internal readonly Queue<string> Chat = new Queue<string>();
            internal string ChatRegistration;
            internal Bridge Bridge;
        }

        sealed class Bridge : INetTransport
        {
            readonly SpectatorMatchFeed owner;
            readonly MatchViewer viewer;
            readonly int game;
            internal bool EndDelivered;

            internal Bridge(SpectatorMatchFeed owner, MatchViewer viewer)
            {
                this.owner = owner;
                this.viewer = viewer;
                game = owner.GameNumber;
            }

            public void Send(string message)
            {
                if (viewer.Failed || !ReferenceEquals(viewer.Bridge, this)) return;
                bool chat = message.StartsWith("{\"type\":\"spectator_chat\"", StringComparison.Ordinal);
                string outgoing = viewer.Joined && !chat
                    ? SpectatorMatchProtocol.Prefix("frame") + ",\"game\":" + SpectatorProtocol.Number(game)
                        + ",\"json\":" + NetJson.Quote(message) + "}"
                    : message;
                owner.Send(viewer, outgoing);
                if (message.StartsWith("{\"type\":\"spectator_end\"", StringComparison.Ordinal)) EndDelivered = true;
            }

            public bool TryReceive(out string message)
            {
                message = null;
                if (viewer.Failed || !ReferenceEquals(viewer.Bridge, this) || viewer.Chat.Count == 0) return false;
                message = viewer.Chat.Dequeue();
                return true;
            }
        }

        public SpectatorMatchFeed(Ruleset ruleset, int delayTurns, bool chatEnabled = false)
        {
            if (ruleset == null) throw new ArgumentNullException(nameof(ruleset));
            if (delayTurns < 0) throw new ArgumentOutOfRangeException(nameof(delayTurns), "观战延迟不能为负数。");
            if (ruleset.BestOf != 1 && ruleset.BestOf != 3 && ruleset.BestOf != 5)
                throw new ArgumentException("观战赛制局数只能是 1、3 或 5。", nameof(ruleset));
            this.ruleset = ruleset.Copy();
            this.delayTurns = delayTurns;
            this.chatEnabled = chatEnabled;
        }

        public void AddViewer(INetTransport transport)
        {
            if (transport == null) throw new ArgumentNullException(nameof(transport));
            if (!viewers.Any(viewer => ReferenceEquals(viewer.Transport, transport)))
                viewers.Add(new MatchViewer { Transport = transport });
        }

        public void StartGame(Battle battle)
        {
            if (battle == null) throw new ArgumentNullException(nameof(battle));
            if (IsFinished || SpectatorMatchProtocol.Over(wins, MatchDraws, ruleset.BestOf))
                throw new InvalidOperationException("比赛已经结束，不能发布下一局。");
            if (feed != null && !gameFinished) throw new InvalidOperationException("上一局尚未释放结果，不能发布下一局。");
            ruleset.Validate(battle.Db);
            var record = battle.Record;
            SpectatorProtocol.ValidateRecord(battle.Db, record);
            if (record.IsBranch || record.BanOne != ruleset.BanOne || record.LeadPick != ruleset.LeadPick
                || !BattleModifiers.Normalize(record.Modifiers).SequenceEqual(BattleModifiers.Normalize(ruleset.Modifiers)))
                throw new InvalidOperationException("观战对局与赛制配置不一致。");
            foreach (var team in record.Teams)
                if (ruleset.CheckTeam(battle.Db, team).Count != 0) throw new InvalidOperationException("观战队伍不符合赛制。");
            string hash = battle.Db.ContentHash();
            if (firstOpening != null && (hash != contentHash || record.PackId != firstOpening.PackId
                || record.Seed != unchecked(firstOpening.Seed + (uint)GameNumber)
                || !record.Names.SequenceEqual(firstOpening.Names)
                || !record.Teams[0].SequenceEqual(firstOpening.Teams[0]) || !record.Teams[1].SequenceEqual(firstOpening.Teams[1])))
                throw new InvalidOperationException("观战下一局的内容、队伍、名字或种子与比赛不一致。");
            var next = new SpectatorFeed(battle, delayTurns, chatEnabled);
            if (firstOpening == null)
            {
                firstOpening = BattleRecord.FromJson(record.ToJson());
                contentHash = hash;
            }
            source = battle;
            feed = next;
            GameNumber++;
            openingWins = (int[])wins.Clone();
            openingDraws = MatchDraws;
            gameFinished = false;
            foreach (var viewer in viewers)
                if (!viewer.Failed && (viewer.Joined || !viewer.EverAttached)) Attach(viewer);
        }

        public void Pump()
        {
            PollInputs();
            if (feed == null) return;
            foreach (var viewer in viewers)
                if (!viewer.Failed && !viewer.EverAttached) Attach(viewer);
            feed.Pump();
            PublishResults();
        }

        public void FinishGame()
        {
            if (feed == null) throw new InvalidOperationException("比赛尚未开局。");
            if (!gameFinished)
            {
                if (source.Phase != Phase.Over || source.Winner < -1 || source.Winner > 1)
                    throw new InvalidOperationException("对战尚未结束，不能公布比分。");
                // 原单局 Feed 先完整重演、核对终局并释放 end，比分随后才可公开。
                feed.Finish();
                finishedWinner = source.Winner;
                finishedHash = SpectatorProtocol.Hash(source);
                if (finishedWinner == -1) MatchDraws++;
                else wins[finishedWinner]++;
                gameFinished = true;
            }
            PublishResults();
        }

        public void Finish()
        {
            if (!gameFinished || !SpectatorMatchProtocol.Over(wins, MatchDraws, ruleset.BestOf))
                throw new InvalidOperationException("比赛尚未结束，不能发布最终比分。");
            IsFinished = true;
            PublishResults();
        }

        void Attach(MatchViewer viewer)
        {
            try
            {
                viewer.Chat.Clear();
                // 上传可在首局后半关闭；保留首次有效登记，下一局仍能只读接收聊天。
                if (viewer.ChatRegistration != null) viewer.Chat.Enqueue(viewer.ChatRegistration);
                if (viewer.Joined)
                {
                    if (!viewer.HeaderSent)
                    {
                        Send(viewer, SpectatorMatchProtocol.Prefix("start") + ",\"name\":" + NetJson.Quote(ruleset.Name ?? ruleset.Id ?? "比赛")
                            + ",\"bestOf\":" + SpectatorProtocol.Number(ruleset.BestOf) + ScoreFields(openingWins, openingDraws) + "}");
                        viewer.HeaderSent = true;
                    }
                    Send(viewer, SpectatorMatchProtocol.Prefix("game_start") + ScoreFields(openingWins, openingDraws) + "}");
                }
                viewer.Bridge = new Bridge(this, viewer);
                viewer.ResultSent = false;
                viewer.EverAttached = true;
                feed.AddViewer(viewer.Bridge);
            }
            catch (Exception) { viewer.Failed = true; }
        }

        void PollInputs()
        {
            foreach (var viewer in viewers)
            {
                if (viewer.Failed || viewer.InputClosed) continue;
                try
                {
                    for (int count = 0; count < SpectatorChatProtocol.PollLimit; count++)
                    {
                        if (!viewer.Transport.TryReceive(out var message)) break;
                        Dictionary<string, object> obj;
                        try { obj = SpectatorProtocol.Object(message); }
                        catch (FormatException) { continue; }
                        if (obj.TryGetValue("type", out var type) && type as string == SpectatorMatchProtocol.Type)
                        {
                            try
                            {
                                SpectatorProtocol.Keys(obj, "type", "version", "kind");
                                if (NetJson.Integer(obj, "version", 1, int.MaxValue) != ProtocolVersion
                                    || NetJson.Text(obj, "kind") != "join" || viewer.Joined) continue;
                                viewer.Joined = true;
                                if (feed != null) Attach(viewer);
                            }
                            catch (FormatException) { }
                        }
                        else if (type as string == SpectatorChatProtocol.Type && !IsFinished && viewer.Chat.Count < SpectatorChatProtocol.PollLimit)
                        {
                            RememberChatRegistration(viewer, obj);
                            viewer.Chat.Enqueue(message);
                        }
                    }
                }
                catch (TcpDisconnectedException) { viewer.InputClosed = true; }
                catch (Exception) { viewer.Failed = true; }
            }
        }

        string ScoreFields(int[] score, int draws)
        {
            return ",\"game\":" + SpectatorProtocol.Number(GameNumber) + ",\"wins\":[" + SpectatorProtocol.Number(score[0])
                + "," + SpectatorProtocol.Number(score[1]) + "],\"draws\":" + SpectatorProtocol.Number(draws);
        }

        static void RememberChatRegistration(MatchViewer viewer, Dictionary<string, object> obj)
        {
            if (viewer.ChatRegistration != null) return;
            try
            {
                SpectatorProtocol.Keys(obj, "type", "version", "kind", "name");
                if (NetJson.Integer(obj, "version", 1, int.MaxValue) != SpectatorFeed.ChatProtocolVersion
                    || NetJson.Text(obj, "kind") != "join") return;
                string name = TextInput.Clean(NetJson.Text(obj, "name"), TextInputKind.PlayerName).Text;
                viewer.ChatRegistration = SpectatorChatProtocol.Prefix("join") + ",\"name\":" + NetJson.Quote(name) + "}";
            }
            catch (FormatException) { }
        }

        void PublishResults()
        {
            if (!gameFinished) return;
            foreach (var viewer in viewers)
            {
                if (viewer.Failed || !viewer.Joined || viewer.Bridge == null || !viewer.Bridge.EndDelivered) continue;
                try
                {
                    if (!viewer.ResultSent)
                    {
                        Send(viewer, SpectatorMatchProtocol.Prefix("game_end") + ScoreFields(wins, MatchDraws)
                            + ",\"winner\":" + SpectatorProtocol.Number(finishedWinner)
                            + ",\"hash\":" + NetJson.Quote(finishedHash) + "}");
                        viewer.ResultSent = true;
                    }
                    if (IsFinished && !viewer.Finished)
                    {
                        Send(viewer, SpectatorMatchProtocol.Prefix("end") + ScoreFields(wins, MatchDraws) + "}");
                        viewer.Finished = true;
                    }
                }
                catch (Exception) { viewer.Failed = true; }
            }
        }

        void Send(MatchViewer viewer, string message)
        {
            try { viewer.Transport.Send(message); }
            catch (Exception) { viewer.Failed = true; throw; }
        }
    }

    public sealed partial class SpectatorClient
    {
        readonly List<SpectatorMatchGameResult> completedGames = new List<SpectatorMatchGameResult>();
        ReadOnlyCollection<SpectatorMatchGameResult> completedGamesView;
        int[] releasedWins = new int[2];
        bool gameEnded, matchNeedsStart, matchReported;
        BattleRecord matchOpening;
        int firstMatchGame;

        public bool MatchEnabled { get; }
        public bool HasMatch { get; private set; }
        public string MatchName { get; private set; }
        public int BestOf { get; private set; }
        public int GameNumber { get; private set; }
        public int[] ReleasedWins { get { return (int[])releasedWins.Clone(); } }
        public int MatchDraws { get; private set; }
        public bool IsMatchFinished { get; private set; }
        public IReadOnlyList<SpectatorMatchGameResult> CompletedGames
        {
            get { return completedGamesView ?? (completedGamesView = completedGames.AsReadOnly()); }
        }

        void JoinMatch()
        {
            if (!MatchEnabled) return;
            try { transport.Send(SpectatorMatchProtocol.Prefix("join") + "}"); }
            catch (Exception) { Error = "多局观战能力登记发送失败。"; }
        }

        void ReceiveMatch(Dictionary<string, object> obj)
        {
            if (!MatchEnabled || IsFinished) throw new FormatException("未启用多局观战或比赛已经结束。");
            if (NetJson.Integer(obj, "version", 1, int.MaxValue) != SpectatorMatchFeed.ProtocolVersion)
                throw new FormatException("多局观战扩展版本不同。");
            string kind = NetJson.Text(obj, "kind");
            if (kind == "start")
            {
                SpectatorProtocol.Keys(obj, "type", "version", "kind", "name", "bestOf", "game", "wins", "draws");
                if (HasMatch) throw new FormatException("不能重复发送多局观战开场。");
                string name = NetJson.Text(obj, "name");
                int bestOf = (int)NetJson.Integer(obj, "bestOf", 1, 5);
                if (bestOf != 1 && bestOf != 3 && bestOf != 5) throw new FormatException("多局观战局数无效。");
                int game = (int)NetJson.Integer(obj, "game", 1, bestOf);
                var score = SpectatorMatchProtocol.Score(obj, bestOf, out var draws);
                if (score[0] + score[1] + draws != game - 1 || SpectatorMatchProtocol.Over(score, draws, bestOf))
                    throw new FormatException("多局观战开场比分与局号不一致。");
                HasMatch = true;
                MatchName = name;
                BestOf = bestOf;
                GameNumber = game;
                releasedWins = score;
                MatchDraws = draws;
                matchNeedsStart = true;
                return;
            }
            if (!HasMatch) throw new FormatException("赛制信息到达前不能接收多局观战消息。");
            int number = (int)NetJson.Integer(obj, "game", 1, BestOf);
            if (kind == "game_start")
            {
                SpectatorProtocol.Keys(obj, "type", "version", "kind", "game", "wins", "draws");
                int expected = matchNeedsStart ? GameNumber : GameNumber + 1;
                if ((!matchNeedsStart && !matchReported) || number != expected || SpectatorMatchProtocol.Over(releasedWins, MatchDraws, BestOf))
                    throw new FormatException("多局观战开局顺序不匹配。");
                RequireScore(obj, releasedWins, MatchDraws);
                GameNumber = number;
                Battle = null;
                nextSequence = 0;
                protocolVersion = 0;
                gameEnded = false;
                matchReported = false;
                matchNeedsStart = false;
                chatJoined = false;
                chatSendTurn = -1;
                chatsSentThisTurn = 0;
                return;
            }
            if (matchNeedsStart || number != GameNumber) throw new FormatException("多局观战消息局号不匹配。");
            if (kind == "frame")
            {
                SpectatorProtocol.Keys(obj, "type", "version", "kind", "game", "json");
                if (matchReported) throw new FormatException("已公布结果的对局不能继续发送事件。");
                string json = NetJson.Text(obj, "json");
                var frame = SpectatorProtocol.Object(json);
                string type = NetJson.Text(frame, "type");
                if (type != "spectator_start" && type != "spectator_event" && type != "spectator_end")
                    throw new FormatException("多局观战封装只能包含单局观战帧。");
                if (NetJson.Integer(frame, "version", 1, int.MaxValue) != SpectatorFeed.ProtocolVersion)
                    throw new FormatException("多局观战封装的单局协议版本不同。");
                Receive(json, true);
                return;
            }
            if (kind == "game_end")
            {
                SpectatorProtocol.Keys(obj, "type", "version", "kind", "game", "wins", "draws", "winner", "hash");
                if (!gameEnded || matchReported || Battle == null || Battle.Phase != Phase.Over)
                    throw new FormatException("单局结束标记到达前不能公布结果，且结果不能重复。");
                int winner = (int)NetJson.Integer(obj, "winner", -1, 1);
                if (winner != Battle.Winner) throw new FormatException("多局观战胜负与重演结果不一致。");
                SpectatorProtocol.VerifyHash(obj, Battle);
                var expected = (int[])releasedWins.Clone();
                int draws = MatchDraws;
                if (winner == -1) draws++; else expected[winner]++;
                RequireScore(obj, expected, draws);
                var result = new SpectatorMatchGameResult(GameNumber, expected, draws, Battle);
                releasedWins = expected;
                MatchDraws = draws;
                completedGames.Add(result);
                matchReported = true;
                return;
            }
            if (kind == "end")
            {
                SpectatorProtocol.Keys(obj, "type", "version", "kind", "game", "wins", "draws");
                if (!matchReported || !SpectatorMatchProtocol.Over(releasedWins, MatchDraws, BestOf))
                    throw new FormatException("比赛尚未结束，不能接收最终比分。");
                RequireScore(obj, releasedWins, MatchDraws);
                IsMatchFinished = true;
                IsFinished = true;
                return;
            }
            throw new FormatException("未知多局观战消息类型。");
        }

        void RequireScore(Dictionary<string, object> obj, int[] expected, int expectedDraws)
        {
            var score = SpectatorMatchProtocol.Score(obj, BestOf, out var draws);
            if (!score.SequenceEqual(expected) || draws != expectedDraws) throw new FormatException("多局观战比分与已公开结果不一致。");
        }

        void ValidateMatchOpening(BattleRecord record)
        {
            if (!HasMatch) return;
            if (matchOpening != null && (record.Seed != unchecked(matchOpening.Seed + (uint)(GameNumber - firstMatchGame))
                || record.PackId != matchOpening.PackId || record.ContentHash != matchOpening.ContentHash
                || record.BanOne != matchOpening.BanOne || record.LeadPick != matchOpening.LeadPick
                || !record.Names.SequenceEqual(matchOpening.Names)
                || !record.Teams[0].SequenceEqual(matchOpening.Teams[0]) || !record.Teams[1].SequenceEqual(matchOpening.Teams[1])
                || !BattleModifiers.Normalize(record.Modifiers).SequenceEqual(BattleModifiers.Normalize(matchOpening.Modifiers))))
                throw new FormatException("多局观战的开局内容、名单或种子不连续。");
            if (matchOpening == null)
            {
                matchOpening = BattleRecord.FromJson(record.ToJson());
                firstMatchGame = GameNumber;
            }
        }
    }

    internal static class SpectatorMatchProtocol
    {
        internal const string Type = "spectator_match";
        internal static string Prefix(string kind)
        {
            return "{\"type\":\"spectator_match\",\"version\":" + SpectatorProtocol.Number(SpectatorMatchFeed.ProtocolVersion)
                + ",\"kind\":" + NetJson.Quote(kind);
        }

        internal static bool Over(int[] wins, int draws, int bestOf)
        {
            return wins[0] >= bestOf / 2 + 1 || wins[1] >= bestOf / 2 + 1 || wins[0] + wins[1] + draws >= bestOf;
        }

        internal static int[] Score(Dictionary<string, object> obj, int bestOf, out int draws)
        {
            var values = NetJson.Array(obj, "wins");
            if (values.Count != 2) throw new FormatException("多局观战比分必须包含双方胜局数。");
            var result = new int[2];
            for (int side = 0; side < 2; side++)
            {
                var entry = new Dictionary<string, object> { { "wins", values[side] } };
                result[side] = (int)NetJson.Integer(entry, "wins", 0, bestOf);
            }
            draws = (int)NetJson.Integer(obj, "draws", 0, bestOf);
            if (result[0] + result[1] + draws > bestOf) throw new FormatException("多局观战比分超过总局数。");
            return result;
        }
    }
}
