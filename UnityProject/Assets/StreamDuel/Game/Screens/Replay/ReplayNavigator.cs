using System;
using System.Collections.Generic;
using System.Linq;
using StreamDuel.Core;

namespace StreamDuel.Game
{
    public sealed class ReplayNavigator
    {
        readonly ContentDb content;
        readonly string recordJson;
        readonly List<int> cuts = new List<int>();
        readonly List<int> turns = new List<int>();
        readonly List<int> logStarts = new List<int>();

        public int Count => cuts.Count;
        public int Index { get; private set; }
        public Battle Current { get; private set; }
        public IReadOnlyList<int> Turns => turns.AsReadOnly();
        public BattleRecord Record => BattleRecord.FromJson(recordJson);
        public IReadOnlyList<LogEntry> FrameLog => Current.Log.Skip(logStarts[Index]).ToList().AsReadOnly();

        public ReplayNavigator(ContentDb db, BattleRecord record)
        {
            if (db == null) throw new ArgumentNullException(nameof(db));
            if (record == null) throw new ArgumentNullException(nameof(record));
            content = db;
            recordJson = record.ToJson();
            var snapshot = Record;
            // 先由引擎验证完整录像，不能播放到中途才发现内容指纹或事件不合法。
            Replay.Run(content, snapshot);
            int firstTurn = snapshot.Events.FindIndex(e => e.Type == BattleRecordEventType.Turn);
            cuts.Add(firstTurn < 0 ? snapshot.Events.Count : firstTurn);
            turns.Add(0);
            logStarts.Add(0);
            int previousLogCount = Replay.Run(content, Prefix(snapshot, cuts[0])).Log.Count;
            for (int i = Math.Max(0, firstTurn); firstTurn >= 0 && i < snapshot.Events.Count; i++)
            {
                if (snapshot.Events[i].Type != BattleRecordEventType.Turn) continue;
                int end = i + 1;
                while (end < snapshot.Events.Count && snapshot.Events[end].Type != BattleRecordEventType.Turn) end++;
                cuts.Add(end);
                var state = Replay.Run(content, Prefix(snapshot, end));
                // 补位日志可能已标记下一回合，用真实回合标题命名；日志按范围保留，不能漏掉补位。
                turns.Add(state.Log.Last(entry => entry.Node == Node.TurnHeader).Turn);
                logStarts.Add(previousLogCount);
                previousLogCount = state.Log.Count;
            }
            Seek(0);
        }

        public void Seek(int index)
        {
            if (index < 0 || index >= Count) throw new ArgumentOutOfRangeException(nameof(index), "回看位置超出录像范围。");
            var next = Replay.Run(content, Prefix(Record, cuts[index]));
            Current = next;
            Index = index;
        }

        public void Next()
        {
            if (Index + 1 < Count) Seek(Index + 1);
        }

        public void Previous()
        {
            if (Index > 0) Seek(Index - 1);
        }

        static BattleRecord Prefix(BattleRecord source, int count)
        {
            var prefix = new BattleRecord
            {
                Version = source.Version,
                ContentHash = source.ContentHash,
                Seed = source.Seed,
                PackId = source.PackId,
                LeadPick = source.LeadPick,
                BanOne = source.BanOne,
                Modifiers = source.Modifiers,
                Names = source.Names,
                Teams = source.Teams
            };
            prefix.Events.AddRange(source.Events.Take(count));
            return prefix;
        }
    }
}
