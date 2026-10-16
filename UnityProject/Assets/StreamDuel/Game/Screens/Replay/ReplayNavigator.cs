using System;
using System.Collections.Generic;
using System.Linq;
using StreamDuel.Core;

namespace StreamDuel.Game
{
    public sealed class ReplayNavigator
    {
        readonly ReplayCursor cursor;
        readonly string recordJson;
        readonly List<int> turns = new List<int>();
        readonly List<int> logStarts = new List<int>();
        readonly List<int> eventStarts = new List<int>();

        public int Count => cursor.MaxTurn + 1;
        public int Index => cursor.Turn;
        // 回看调用方只能拿显示副本，不能污染游标保存的规则状态或随机流。
        public Battle Current => cursor.Current.Clone();
        public int FrameEventStart => eventStarts[Index];
        public IReadOnlyList<int> Turns => turns.AsReadOnly();
        public BattleRecord Record => BattleRecord.FromJson(recordJson);
        public string CompatibilityWarning { get; }
        public IReadOnlyList<LogEntry> FrameLog => Current.Log.Skip(logStarts[Index]).ToList().AsReadOnly();

        public ReplayNavigator(ContentDb db, BattleRecord record, bool bestEffort = false)
        {
            if (db == null) throw new ArgumentNullException(nameof(db));
            if (record == null) throw new ArgumentNullException(nameof(record));
            recordJson = record.ToJson();
            var snapshot = Record;
            // 先由引擎验证完整录像，不能播放到中途才发现内容指纹或事件不合法。
            var compatibility = Replay.Check(db, snapshot);
            if (!compatibility.CanReplay) throw new InvalidOperationException(compatibility.Reason);
            CompatibilityWarning = compatibility.Reason;
            if (bestEffort)
            {
                Replay.RunBestEffort(db, snapshot);
                // 原记录留给分享导出；兼容校验通过后，游标只需执行已获准的事件。
                snapshot.ContentHash = null;
            }
            else Replay.Run(db, snapshot);
            cursor = new ReplayCursor(db, snapshot);
            turns.Add(0);
            logStarts.Add(0);
            eventStarts.Add(0);
            int previousLogCount = Current.Log.Count;
            int previousEventCount = Current.Events.Count;
            while (cursor.Next())
            {
                var state = Current;
                // 补位日志可能已标记下一回合，用真实回合标题命名；日志按范围保留，不能漏掉补位。
                turns.Add(state.Log.Last(entry => entry.Node == Node.TurnHeader).Turn);
                logStarts.Add(previousLogCount);
                eventStarts.Add(previousEventCount);
                previousLogCount = state.Log.Count;
                previousEventCount = state.Events.Count;
            }
            Seek(0);
        }

        public void Seek(int index)
        {
            if (index < 0 || index >= Count) throw new ArgumentOutOfRangeException(nameof(index), "回看位置超出录像范围。");
            cursor.Seek(index);
        }

        public void Next()
        {
            cursor.Next();
        }

        public void Previous()
        {
            cursor.Prev();
        }
    }
}
