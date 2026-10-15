using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace StreamDuel.Core
{
    public enum NetState { Handshake, Choose, Replace, Waiting, Over, Desync, Error }

    public sealed partial class NetSession
    {
        public const int ProtocolVersion = 2;
        public NetState State { get; private set; } = NetState.Handshake;
        public Battle Battle { get; private set; }
        public int MySide { get; }
        public bool HasSubmitted { get; private set; }
        public string Error { get; private set; }

        private readonly ContentDb db;
        private INetTransport transport;
        private readonly IList<string> myTeam;
        private readonly string myName;
        private readonly uint? hostSeed;
        private readonly string contentHash;
        private readonly BattleAction?[] actions = new BattleAction?[2];
        private readonly int?[] replacements = new int?[2];
        private readonly bool[] needsReplacement = new bool[2];
        private readonly Queue<HashStamp> hashes = new Queue<HashStamp>();
        private Phase phase;
        private int turn;
        private uint rngCheckpoint;

        private sealed class HashStamp
        {
            public int Turn;
            public string Hash;
        }

        public NetSession(ContentDb db, INetTransport t, bool host, IList<string> myTeam, string myName, uint? seed, bool enableReconnect = false)
        {
            this.db = db ?? throw new ArgumentNullException(nameof(db));
            transport = t ?? throw new ArgumentNullException(nameof(t));
            MySide = host ? 0 : 1;
            this.enableReconnect = enableReconnect;
            if (enableReconnect && host) SessionId = NewSessionId();
            this.myTeam = myTeam == null ? null : new List<string>(myTeam);
            this.myName = myName;
            // 只由主机在建立会话时生成一次种子，之后不再读取时间。
            hostSeed = host ? seed ?? unchecked((uint)Environment.TickCount) : (uint?)null;
            var errors = TeamCheck.Validate(db, null, this.myTeam);
            if (errors.Count > 0 || myName == null)
            {
                Fault(NetState.Error, errors.Count > 0 ? "本方队伍不合法：" + string.Join("；", errors) : "本方名字不能为 null。");
                return;
            }
            contentHash = db.ContentHash();
            Send("{\"type\":\"hello\",\"version\":" + Number(enableReconnect ? ReconnectProtocolVersion : ProtocolVersion)
                + (enableReconnect ? ",\"sessionId\":" + (SessionId == null ? "null" : NetJson.Quote(SessionId)) : "")
                + ",\"packId\":" + NetJson.Quote(db.PackId)
                + ",\"contentHash\":" + NetJson.Quote(contentHash)
                + ",\"seed\":" + (hostSeed.HasValue ? hostSeed.Value.ToString(CultureInfo.InvariantCulture) : "null")
                + ",\"name\":" + NetJson.Quote(myName)
                + ",\"team\":[" + string.Join(",", this.myTeam.Select(NetJson.Quote)) + "]}");
        }

        public void Poll()
        {
            Poll(0);
        }

        public void Poll(double elapsedSeconds)
        {
            TickReconnect(elapsedSeconds);
            if (Faulted) return;
            if (IsDisconnected && !IsReconnecting) return;
            try
            {
                string message;
                while (!Faulted && transport.TryReceive(out message))
                {
                    if (enableReconnect && Battle != null) ReceiveReconnectMessage(message);
                    else Receive(message);
                    Advance();
                    TryFinishReconnect();
                    if (IsDisconnected && !IsReconnecting) break;
                }
            }
            catch (TcpDisconnectedException ex)
            {
                // 终局仍接收迟到的协议报错，只有排队消息处理完后的纯断线可以忽略。
                if (State == NetState.Over && (!enableReconnect || peerCompleted)) return;
                if (!PauseForReconnect(ex)) Fault(NetState.Error, ex.Message, false);
            }
            catch (IOException ex)
            {
                if (!PauseForReconnect(ex)) Fault(NetState.Error, "接收消息失败：" + ex.Message, false);
            }
            catch (ObjectDisposedException ex)
            {
                if (!PauseForReconnect(ex)) Fault(NetState.Error, "连接已经关闭：" + ex.Message, false);
            }
            catch (Exception ex)
            {
                Fault(NetState.Error, "接收或处理消息失败：" + ex.Message);
            }
        }

        public void Submit(BattleAction action)
        {
            if (State != NetState.Choose || HasSubmitted)
                throw new InvalidOperationException("现在不能提交行动，或本阶段已经提交。");
            var legal = Battle.LegalFor(MySide);
            // 硬撑与受控跳过属于 Core 派生动作，线路上仍只发送标准的 P。
            if (action.Type == ActionType.Struggle && legal.Struggle
                || action.Type == ActionType.PassControlled && legal.Controlled != null) action = BattleAction.Pass();
            string wire;
            try { wire = action.ToWire(); }
            catch (FormatException ex) { throw new InvalidOperationException("不能提交该行动编码。", ex); }
            CommitSelection("action", wire);
        }

        public void SubmitReplace(int to)
        {
            if (State != NetState.Replace || HasSubmitted || !ValidReplacement(MySide, to))
                throw new InvalidOperationException("现在不能提交该补位，或本阶段已经提交。");
            CommitSelection("replace", BattleAction.SwitchTo(to).ToWire());
        }

        private bool Terminal
        {
            get { return State == NetState.Over || State == NetState.Desync || State == NetState.Error; }
        }

        private bool Faulted
        {
            get { return State == NetState.Desync || State == NetState.Error; }
        }

        private void Receive(string message)
        {
            var obj = NetJson.Object(message);
            string type = NetJson.Text(obj, "type");
            if (type == "error")
            {
                Fault(NetState.Error, "对方报错：" + NetJson.Text(obj, "message"), false);
                return;
            }
            if (enableReconnect && type == "goodbye")
            {
                if (Battle.Phase != Phase.Over || hashes.Count != 0)
                    throw new FormatException("对战尚未完成终局校验，不能结束连接。");
                peerCompleted = true;
                return;
            }
            if (State == NetState.Over) throw new FormatException("对战已经结束，不能再接收新的协议操作。");
            if (type == "hello")
            {
                ReceiveHello(obj);
                return;
            }
            if (Battle == null) throw new FormatException("握手完成前不能发送行动、补位或哈希。");
            if (type == "hash")
            {
                ReceiveHash(obj);
                return;
            }
            if (hashes.Count > 0) throw new FormatException("上一阶段哈希尚未确认，不能提前提交下一阶段。");
            int messageTurn = (int)NetJson.Integer(obj, "turn", 1, int.MaxValue);
            if (messageTurn != turn) throw new FormatException("消息回合不匹配：当前 " + Number(turn) + "，收到 " + Number(messageTurn) + "。");
            if (type == "commit") ReceiveCommit(obj);
            else if (type == "reveal") ReceiveReveal(obj);
            else throw new FormatException("未知协议消息类型：" + type);
        }

        private void ReceiveHello(Dictionary<string, object> obj)
        {
            if (Battle != null) throw new FormatException("对方重复发送握手。");
            long version = NetJson.Integer(obj, "version", 0, int.MaxValue);
            int expectedVersion = enableReconnect ? ReconnectProtocolVersion : ProtocolVersion;
            if (version != expectedVersion) throw new FormatException("协议版本不同：本端 " + Number(expectedVersion) + "，对方 " + version + "。");
            if (enableReconnect)
            {
                if (MySide == 0)
                {
                    if (!obj.TryGetValue("sessionId", out var id) || id != null)
                        throw new FormatException("客机不能指定新的会话标识。");
                }
                else
                {
                    SessionId = NetJson.Text(obj, "sessionId");
                    if (!LowerHex(SessionId, 32)) throw new FormatException("主机会话标识无效。");
                }
            }
            string packId = NetJson.Text(obj, "packId");
            if (packId != db.PackId) throw new FormatException("内容包不同：本端「" + db.PackId + "」，对方「" + packId + "」。");
            string peerContentHash = NetJson.Text(obj, "contentHash");
            string currentContentHash = db.ContentHash();
            // 发出的指纹也必须仍对应本地内容，避免握手途中修改规则后误以为双方一致。
            if (!string.Equals(contentHash, currentContentHash, StringComparison.Ordinal))
                throw new FormatException("内容不一致：握手期间本端内容已变化，已发送指纹 " + contentHash
                    + "，当前指纹 " + currentContentHash + "，对方指纹 " + peerContentHash + "。");
            if (!string.Equals(contentHash, peerContentHash, StringComparison.Ordinal))
                throw new FormatException("内容不一致：本端指纹 " + contentHash + "，对方指纹 " + peerContentHash + "。");
            string name = NetJson.Text(obj, "name");
            var team = new List<string>();
            foreach (var item in NetJson.Array(obj, "team"))
            {
                if (!(item is string id)) throw new FormatException("握手队伍必须包含主播编号字符串。");
                team.Add(id);
            }
            var errors = TeamCheck.Validate(db, null, team);
            if (errors.Count > 0) throw new FormatException("对方队伍不合法：" + string.Join("；", errors));
            uint sharedSeed;
            if (MySide == 0)
            {
                if (!obj.TryGetValue("seed", out var peerSeed) || peerSeed != null)
                    throw new FormatException("角色或种子不匹配：客机握手的 seed 必须为 null。");
                sharedSeed = hostSeed.Value;
            }
            else sharedSeed = (uint)NetJson.Integer(obj, "seed", 0, uint.MaxValue);
            var teams = new IList<string>[2];
            var names = new string[2];
            teams[MySide] = myTeam;
            teams[1 - MySide] = team;
            names[MySide] = myName;
            names[1 - MySide] = name;
            Battle = new Battle(db, teams, names, sharedSeed);
            rngCheckpoint = Battle.NetRngState;
            RememberCheckpoint();
            BeginPhase();
        }

        private void Advance()
        {
            if (Terminal || Battle == null || hashes.Count > 0 || IsDisconnected && !IsReconnecting) return;
            RevealIfReady();
            if (Faulted || IsDisconnected && !IsReconnecting) return;
            if (phase == Phase.Choose && actions[0].HasValue && actions[1].HasValue)
            {
                State = NetState.Waiting;
                // 本端 AI 建议可能消费 RNG；只以双方共享的结算检查点推进对战随机流。
                Battle.RestoreReplayRng(rngCheckpoint);
                int resolvedTurn = Battle.Turn;
                Battle.ResolveTurn(actions[0].Value, actions[1].Value);
                CaptureHash(resolvedTurn);
            }
            else if (phase == Phase.Replace
                && (!needsReplacement[0] || replacements[0].HasValue)
                && (!needsReplacement[1] || replacements[1].HasValue))
            {
                State = NetState.Waiting;
                Battle.RestoreReplayRng(rngCheckpoint);
                // 双方同时补位也按主机、客机顺序执行，消息到达顺序不改变登场效果。
                for (int side = 0; side < 2 && !Terminal; side++)
                {
                    if (!needsReplacement[side]) continue;
                    int replacedTurn = Battle.Turn;
                    Battle.Replace(side, replacements[side].Value);
                    CaptureHash(replacedTurn);
                }
            }
        }

        private bool ValidReplacement(int side, int to)
        {
            if (Battle == null || phase != Phase.Replace || !needsReplacement[side]) return false;
            var team = Battle.Sides[side];
            return to >= 0 && to < team.Team.Count && to != team.Active && !team.Team[to].Fainted;
        }

        private void CaptureHash(int operationTurn)
        {
            rngCheckpoint = Battle.NetRngState;
            string hash = Battle.StateHash().ToString("x16", CultureInfo.InvariantCulture);
            hashes.Enqueue(new HashStamp { Turn = operationTurn, Hash = hash });
            RememberCheckpoint();
            Send("{\"type\":\"hash\",\"turn\":" + Number(operationTurn) + ",\"hash\":" + NetJson.Quote(hash) + "}");
        }

        private void ReceiveHash(Dictionary<string, object> obj)
        {
            if (hashes.Count == 0) throw new FormatException("尚无对应结算，不能接收哈希或重复确认。");
            int messageTurn = (int)NetJson.Integer(obj, "turn", 1, int.MaxValue);
            string peerHash = NetJson.Text(obj, "hash");
            var local = hashes.Peek();
            if (messageTurn != local.Turn) throw new FormatException("哈希回合不匹配：本端 " + Number(local.Turn) + "，对方 " + Number(messageTurn) + "。");
            // 同一回合可能有两次补位，按操作顺序逐条比较，不能用回合号覆盖旧哈希。
            if (!string.Equals(local.Hash, peerHash, StringComparison.Ordinal))
            {
                Fault(NetState.Desync, "第 " + Number(messageTurn) + " 回合状态不同步：本端哈希 " + local.Hash + "，对方哈希 " + peerHash + "。");
                return;
            }
            hashes.Dequeue();
            if (hashes.Count == 0) BeginPhase();
        }

        private void BeginPhase()
        {
            phase = Battle.Phase;
            turn = Battle.Turn;
            // 等哈希期间也可能预先请求 AI 建议，下一阶段仍从已保存的结算点开始。
            Battle.RestoreReplayRng(rngCheckpoint);
            HasSubmitted = false;
            ResetCommitReveal();
            for (int side = 0; side < 2; side++)
            {
                actions[side] = null;
                replacements[side] = null;
                needsReplacement[side] = Battle.Need[side];
            }
            if (phase == Phase.Over) State = NetState.Over;
            else if (phase == Phase.Choose) State = NetState.Choose;
            else if (phase == Phase.Replace) State = needsReplacement[MySide] ? NetState.Replace : NetState.Waiting;
            else Fault(NetState.Error, "同步协议暂不支持禁用或首发选择阶段。");
            if (IsDisconnected && !Faulted) State = NetState.Waiting;
        }

        private bool Send(string message)
        {
            if (enableReconnect && Battle != null) return SendJournaled(message);
            try { transport.Send(message); return true; }
            catch (Exception ex)
            {
                Fault(NetState.Error, "发送消息失败：" + ex.Message, false);
                return false;
            }
        }

        private void Fault(NetState state, string reason, bool notify = true)
        {
            if (Faulted) return;
            State = state;
            Error = reason;
            if (!notify) return;
            try { Send("{\"type\":\"error\",\"message\":" + NetJson.Quote(reason) + "}"); }
            catch (Exception) { }
        }

        private static string Number(int value) { return value.ToString(CultureInfo.InvariantCulture); }
    }

    public sealed partial class Battle
    {
        // 会话在本地选招前后保留共享随机流，不开放新的玩家操作接口。
        internal uint NetRngState { get { return rng.State; } }
    }
}
