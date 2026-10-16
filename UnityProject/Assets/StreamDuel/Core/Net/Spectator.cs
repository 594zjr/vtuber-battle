using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace StreamDuel.Core
{
    public sealed class SpectatorFeed
    {
        public const int ProtocolVersion = 2;
        readonly Battle source;
        readonly int delayTurns;
        readonly string opening;
        readonly List<Frame> frames = new List<Frame>();
        readonly List<Viewer> viewers = new List<Viewer>();
        Battle shadow;
        string ending;

        sealed class Frame
        {
            public string Message;
            public long ReadyTurn;
        }

        sealed class Viewer
        {
            public INetTransport Transport;
            public bool Started, Finished;
            public int Cursor;
        }

        public SpectatorFeed(Battle b, int delayTurns)
        {
            source = b ?? throw new ArgumentNullException(nameof(b));
            if (delayTurns < 0) throw new ArgumentOutOfRangeException(nameof(delayTurns), "观战延迟不能为负数。");
            this.delayTurns = delayTurns;
            var record = source.Record;
            SpectatorProtocol.ValidateRecord(source.Db, record);
            string contentHash = source.Db.ContentHash();
            if (record.PackId != source.Db.PackId || record.ContentHash != null && record.ContentHash != contentHash)
                throw new InvalidOperationException("内容不一致，不能重建观战开局。");
            var names = record.Names == null ? new[] { "你", "对手" } : record.Names.ToArray();
            var teams = record.Teams.Select(team => (IList<string>)team.ToList()).ToArray();
            shadow = SpectatorProtocol.CreateBattle(source.Db, record);
            opening = "{\"type\":\"spectator_start\",\"version\":2,\"seq\":0,\"packId\":" + NetJson.Quote(record.PackId)
                + ",\"contentHash\":" + NetJson.Quote(contentHash) + ",\"seed\":" + SpectatorProtocol.Number(record.Seed)
                + ",\"names\":[" + string.Join(",", names.Select(NetJson.Quote)) + "]"
                + ",\"teams\":[" + string.Join(",", teams.Select(team => "[" + string.Join(",", team.Select(NetJson.Quote)) + "]")) + "]"
                + ",\"leadPick\":" + (record.LeadPick ? "true" : "false")
                + ",\"banOne\":" + (record.BanOne ? "true" : "false")
                + ",\"recordVersion\":" + SpectatorProtocol.Number(record.Version)
                + ",\"modifiers\":[" + string.Join(",", shadow.Modifiers.Select(NetJson.Quote)) + "]"
                + ",\"hash\":" + NetJson.Quote(SpectatorProtocol.Hash(shadow)) + "}";
        }

        public void AddViewer(INetTransport t)
        {
            if (t == null) throw new ArgumentNullException(nameof(t));
            if (!viewers.Any(viewer => ReferenceEquals(viewer.Transport, t))) viewers.Add(new Viewer { Transport = t });
        }

        public void Pump()
        {
            if (ending == null) CaptureEvents();
            Deliver();
        }

        public void Finish()
        {
            if (source.Phase != Phase.Over) throw new InvalidOperationException("对战尚未结束，不能结束观战数据流。");
            if (ending == null)
            {
                CaptureEvents();
                if (shadow.Phase != Phase.Over || shadow.StateHash() != source.StateHash())
                    throw new InvalidOperationException("观战重演终局与真实对战不一致。");
                ending = "{\"type\":\"spectator_end\",\"version\":2,\"seq\":" + SpectatorProtocol.Number((long)frames.Count + 1)
                    + ",\"turn\":" + SpectatorProtocol.Number(shadow.Turn)
                    + ",\"hash\":" + NetJson.Quote(SpectatorProtocol.Hash(shadow)) + "}";
            }
            Deliver();
        }

        void CaptureEvents()
        {
            SpectatorProtocol.ValidateVersion(source.Record.Version);
            if (source.Record.Events.Count < frames.Count) throw new InvalidOperationException("对战记录不能删除已经发布的观战事件。");
            while (frames.Count < source.Record.Events.Count)
            {
                var entry = source.Record.Events[frames.Count];
                if (entry == null || !entry.RngState.HasValue) throw new InvalidOperationException("观战事件缺少结算前随机状态。");
                int beforeTurn = shadow.Turn;
                uint rng = entry.RngState.Value;
                var next = shadow.Clone();
                SpectatorProtocol.ApplyEvent(next, entry, source.Record.Version);
                string kind, actionFields;
                if (entry.Type == BattleRecordEventType.Turn)
                {
                    kind = "turn";
                    string a0 = SpectatorProtocol.Wire(entry.A0), a1 = SpectatorProtocol.Wire(entry.A1);
                    // 原始非法输入仍由 Core 纠正；线路只传标准行动，并验证两种输入等价。
                    if (!SpectatorProtocol.SameAction(entry.A0, BattleAction.FromWire(a0))
                        || !SpectatorProtocol.SameAction(entry.A1, BattleAction.FromWire(a1)))
                    {
                        var canonical = shadow.Clone();
                        canonical.RestoreReplayRng(rng);
                        canonical.ResolveTurn(BattleAction.FromWire(a0), BattleAction.FromWire(a1));
                        if (canonical.StateHash() != next.StateHash()) throw new InvalidOperationException("原始行动不能等价编码为观战消息。");
                    }
                    actionFields = ",\"a0\":" + NetJson.Quote(a0) + ",\"a1\":" + NetJson.Quote(a1);
                }
                else if (entry.Type == BattleRecordEventType.Surrender || entry.Type == BattleRecordEventType.DrawOffer
                    || entry.Type == BattleRecordEventType.DrawResponse)
                {
                    kind = entry.Type == BattleRecordEventType.Surrender ? "surrender"
                        : entry.Type == BattleRecordEventType.DrawOffer ? "drawOffer" : "drawResponse";
                    actionFields = ",\"side\":" + SpectatorProtocol.Number(entry.Side);
                    if (entry.Type == BattleRecordEventType.DrawResponse)
                        actionFields += ",\"accept\":" + (entry.Accepted ? "true" : "false");
                }
                else
                {
                    kind = entry.Type == BattleRecordEventType.Replace ? "replace"
                        : entry.Type == BattleRecordEventType.Lead ? "lead"
                        : entry.Type == BattleRecordEventType.Ban ? "ban"
                        : throw new InvalidOperationException("未知观战事件类型。");
                    actionFields = ",\"side\":" + SpectatorProtocol.Number(entry.Side) + ",\"to\":" + SpectatorProtocol.Number(entry.To);
                }
                string message = "{\"type\":\"spectator_event\",\"version\":2,\"seq\":" + SpectatorProtocol.Number((long)frames.Count + 1)
                    + ",\"turn\":" + SpectatorProtocol.Number(beforeTurn) + ",\"event\":" + NetJson.Quote(kind)
                    + ",\"rng\":" + SpectatorProtocol.Number(rng) + actionFields
                    + ",\"hash\":" + NetJson.Quote(SpectatorProtocol.Hash(next)) + "}";
                // 终局协商同样不能绕过延迟；选手确认完局后才由 Finish 公开。
                long readyTurn = entry.Type == BattleRecordEventType.Turn || next.Phase == Phase.Over
                    ? Math.Max((long)beforeTurn + 1, next.Turn) : next.Turn;
                frames.Add(new Frame { Message = message, ReadyTurn = readyTurn });
                shadow = next;
            }
        }

        void Deliver()
        {
            long threshold = (long)source.Turn - delayTurns;
            for (int index = viewers.Count - 1; index >= 0; index--)
            {
                var viewer = viewers[index];
                try
                {
                    if (!viewer.Started)
                    {
                        viewer.Transport.Send(opening);
                        viewer.Started = true;
                    }
                    while (viewer.Cursor < frames.Count && (ending != null || frames[viewer.Cursor].ReadyTurn <= threshold))
                    {
                        viewer.Transport.Send(frames[viewer.Cursor].Message);
                        viewer.Cursor++;
                    }
                    if (ending != null && !viewer.Finished)
                    {
                        viewer.Transport.Send(ending);
                        viewer.Finished = true;
                    }
                }
                catch (Exception)
                {
                    // 观众传输失败只影响这一名观众；连接的关闭权仍属于调用者。
                    viewers.RemoveAt(index);
                }
            }
        }
    }

    public sealed class SpectatorClient
    {
        readonly ContentDb db;
        readonly INetTransport transport;
        readonly string contentHash;
        long nextSequence;
        int protocolVersion;

        public Battle Battle { get; private set; }
        public string Error { get; private set; }
        public bool IsFinished { get; private set; }

        public SpectatorClient(ContentDb db, INetTransport t)
        {
            this.db = db ?? throw new ArgumentNullException(nameof(db));
            transport = t ?? throw new ArgumentNullException(nameof(t));
            contentHash = db.ContentHash();
        }

        public void Poll()
        {
            if (Error != null) return;
            try
            {
                string message;
                while (transport.TryReceive(out message)) Receive(message);
            }
            catch (TcpDisconnectedException ex)
            {
                if (!IsFinished) Error = "观战结束标记到达前连接中断：" + ex.Message;
            }
            catch (Exception ex)
            {
                Error = "观战消息无效或接收失败：" + ex.Message;
                IsFinished = false;
            }
        }

        void Receive(string message)
        {
            var obj = SpectatorProtocol.Object(message);
            int version = (int)NetJson.Integer(obj, "version", 1, int.MaxValue);
            if (version != 1 && version != SpectatorFeed.ProtocolVersion
                || protocolVersion != 0 && version != protocolVersion)
                throw new FormatException("观战协议版本不同。");
            long sequence = NetJson.Integer(obj, "seq", 0, int.MaxValue);
            if (sequence != nextSequence) throw new FormatException("观战消息顺序不匹配。");
            string type = NetJson.Text(obj, "type");
            if (IsFinished) throw new FormatException("观战已经结束，不能接收额外消息。");
            if (type == "spectator_start")
            {
                ReceiveOpening(obj, version);
                return;
            }
            if (Battle == null) throw new FormatException("开局信息到达前不能接收观战事件。");
            if ((int)NetJson.Integer(obj, "turn", 1, int.MaxValue) != Battle.Turn)
                throw new FormatException("观战消息回合不匹配。");
            if (type == "spectator_end")
            {
                SpectatorProtocol.Keys(obj, "type", "version", "seq", "turn", "hash");
                if (Battle.Phase != Phase.Over) throw new FormatException("对战尚未结束，不能接收观战结束标记。");
                SpectatorProtocol.VerifyHash(obj, Battle);
                IsFinished = true;
                nextSequence++;
                return;
            }
            if (type != "spectator_event") throw new FormatException("未知观战消息类型。");
            string kind = NetJson.Text(obj, "event");
            uint rng = (uint)NetJson.Integer(obj, "rng", 0, uint.MaxValue);
            // 先在副本上确认阶段、动作和结果，坏帧不能污染已经展示的对战。
            var next = Battle.Clone();
            var entry = new BattleRecordEvent { RngState = rng };
            if (kind == "turn")
            {
                SpectatorProtocol.Keys(obj, "type", "version", "seq", "turn", "event", "rng", "hash", "a0", "a1");
                entry.Type = BattleRecordEventType.Turn;
                entry.A0 = BattleAction.FromWire(NetJson.Text(obj, "a0"));
                entry.A1 = BattleAction.FromWire(NetJson.Text(obj, "a1"));
            }
            else if (version == 2 && (kind == "surrender" || kind == "drawOffer" || kind == "drawResponse"))
            {
                if (kind == "drawResponse")
                {
                    SpectatorProtocol.Keys(obj, "type", "version", "seq", "turn", "event", "rng", "hash", "side", "accept");
                    entry.Accepted = SpectatorProtocol.Boolean(obj, "accept");
                }
                else SpectatorProtocol.Keys(obj, "type", "version", "seq", "turn", "event", "rng", "hash", "side");
                entry.Type = kind == "surrender" ? BattleRecordEventType.Surrender
                    : kind == "drawOffer" ? BattleRecordEventType.DrawOffer : BattleRecordEventType.DrawResponse;
                entry.Side = (int)NetJson.Integer(obj, "side", kind == "surrender" ? -1 : 0, 1);
            }
            else
            {
                SpectatorProtocol.Keys(obj, "type", "version", "seq", "turn", "event", "rng", "hash", "side", "to");
                entry.Type = kind == "replace" ? BattleRecordEventType.Replace
                    : kind == "lead" ? BattleRecordEventType.Lead
                    : kind == "ban" ? BattleRecordEventType.Ban : throw new FormatException("未知观战事件类型。");
                entry.Side = (int)NetJson.Integer(obj, "side", 0, 1);
                entry.To = (int)NetJson.Integer(obj, "to", 0, 5);
            }
            // 协议 2 可在直播期间首次出现终止事件；由引擎同步升级该局录像，而非改写已确认的副本。
            SpectatorProtocol.ApplyEvent(next, entry, version == 1 ? BattleRecord.DefaultVersion : BattleRecord.CurrentVersion);
            SpectatorProtocol.VerifyHash(obj, next);
            Battle = next;
            nextSequence++;
        }

        void ReceiveOpening(Dictionary<string, object> obj, int version)
        {
            if (Battle != null || nextSequence != 0) throw new FormatException("不能重复发送观战开局信息。");
            if (version == 1)
                SpectatorProtocol.Keys(obj, "type", "version", "seq", "packId", "contentHash", "seed", "names", "teams", "leadPick", "banOne", "hash");
            else SpectatorProtocol.Keys(obj, "type", "version", "seq", "packId", "contentHash", "seed", "names", "teams", "leadPick", "banOne", "hash", "recordVersion", "modifiers");
            var names = NetJson.Array(obj, "names").Select(value => value as string
                ?? throw new FormatException("双方名字必须是字符串。")).ToArray();
            var teams = NetJson.Array(obj, "teams").Select(value =>
            {
                var members = value as List<object> ?? throw new FormatException("双方队伍必须是数组。");
                return (IList<string>)members.Select(member => member as string
                    ?? throw new FormatException("队伍成员必须是主播编号字符串。")).ToList();
            }).ToArray();
            var record = new BattleRecord
            {
                Version = version == 1 ? BattleRecord.DefaultVersion : (int)NetJson.Integer(obj, "recordVersion", 1, int.MaxValue),
                PackId = NetJson.Text(obj, "packId"),
                ContentHash = NetJson.Text(obj, "contentHash"),
                Names = names,
                Teams = teams,
                Seed = (uint)NetJson.Integer(obj, "seed", 0, uint.MaxValue),
                LeadPick = SpectatorProtocol.Boolean(obj, "leadPick"),
                BanOne = SpectatorProtocol.Boolean(obj, "banOne"),
                Modifiers = version == 1 ? null : NetJson.Array(obj, "modifiers").Select(value => value as string
                    ?? throw new FormatException("观战规则修饰器必须是字符串。")).ToList()
            };
            SpectatorProtocol.ValidateRecord(db, record);
            if (record.ContentHash != contentHash || db.ContentHash() != contentHash)
                throw new FormatException("观战内容指纹不一致。");
            var initial = SpectatorProtocol.CreateBattle(db, record);
            SpectatorProtocol.VerifyHash(obj, initial);
            Battle = initial;
            protocolVersion = version;
            nextSequence = 1;
        }
    }

    internal static class SpectatorProtocol
    {
        internal static string Number(long value) { return value.ToString(CultureInfo.InvariantCulture); }
        internal static string Hash(Battle battle) { return battle.StateHash().ToString("x16", CultureInfo.InvariantCulture); }

        internal static bool SameAction(BattleAction left, BattleAction right)
        {
            return left.Type == right.Type && left.Index == right.Index;
        }

        internal static string Wire(BattleAction action)
        {
            try { return action.ToWire(); }
            catch (FormatException) { return BattleAction.Pass().ToWire(); }
        }

        internal static void ValidateVersion(int version)
        {
            if (version < 1 || version > BattleRecord.CurrentVersion) throw new FormatException("观战录像版本不受支持。");
        }

        internal static void ApplyEvent(Battle battle, BattleRecordEvent entry, int version)
        {
            ValidateVersion(version);
            BattleRecord.ValidateEventVersion(version, entry);
            if (entry.Type == BattleRecordEventType.Replace || entry.Type == BattleRecordEventType.Lead || entry.Type == BattleRecordEventType.Ban)
                if (entry.Side < 0 || entry.Side > 1 || entry.To < 0 || entry.To > 5) throw new FormatException("观战选择位置无效。");
            Replay.ApplyEvent(battle, entry);
        }

        internal static void ValidateRecord(ContentDb db, BattleRecord record)
        {
            var compatibility = Replay.Check(db, record);
            if (!compatibility.CanReplay) throw new FormatException("观战开局无效：" + compatibility.Reason);
            foreach (var team in record.Teams)
            {
                var errors = TeamCheck.Validate(db, null, team);
                if (errors.Count > 0) throw new FormatException("观战队伍无效：" + string.Join("；", errors));
            }
        }

        internal static Battle CreateBattle(ContentDb db, BattleRecord record)
        {
            var battle = new Battle(db, record.Teams, record.Names ?? new[] { "你", "对手" }, new BattleOptions
            {
                Seed = record.Seed, LeadPick = record.LeadPick, BanOne = record.BanOne, Modifiers = record.Modifiers
            });
            battle.Record.Version = record.Version;
            return battle;
        }

        internal static bool Boolean(Dictionary<string, object> obj, string key)
        {
            if (!obj.TryGetValue(key, out var value) || !(value is bool flag))
                throw new FormatException("观战字段「" + key + "」必须是布尔值。");
            return flag;
        }

        internal static void Keys(Dictionary<string, object> obj, params string[] keys)
        {
            if (obj.Count != keys.Length || keys.Any(key => !obj.ContainsKey(key)))
                throw new FormatException("观战消息字段缺失或包含未知字段。");
        }

        internal static void VerifyHash(Dictionary<string, object> obj, Battle battle)
        {
            string hash = NetJson.Text(obj, "hash");
            if (hash.Length != 16 || hash.Any(c => !(c >= '0' && c <= '9') && !(c >= 'a' && c <= 'f')))
                throw new FormatException("观战状态哈希格式无效。");
            if (hash != Hash(battle)) throw new FormatException("观战状态哈希不一致。");
        }

        internal static Dictionary<string, object> Object(string message)
        {
            if (message != null && (message.IndexOf('\r') >= 0 || message.IndexOf('\n') >= 0))
                throw new FormatException("观战消息必须是单行 JSON。");
            var obj = NetJson.Object(message);
            // 通用解析器采用最后值；观战帧需要拒绝有歧义的重复顶层字段。
            var seen = new HashSet<string>(StringComparer.Ordinal);
            int depth = 0;
            for (int i = 0; i < message.Length; i++)
            {
                char c = message[i];
                if (c == '{' || c == '[') depth++;
                else if (c == '}' || c == ']') depth--;
                else if (c == '"')
                {
                    int start = i;
                    while (++i < message.Length)
                    {
                        if (message[i] == '\\') i++;
                        else if (message[i] == '"') break;
                    }
                    int after = i + 1;
                    while (after < message.Length && char.IsWhiteSpace(message[after])) after++;
                    if (depth == 1 && after < message.Length && message[after] == ':')
                    {
                        string key = (string)MiniJson.Parse(message.Substring(start, i - start + 1));
                        if (!seen.Add(key)) throw new FormatException("观战消息字段重复：" + key);
                    }
                }
            }
            return obj;
        }
    }
}
