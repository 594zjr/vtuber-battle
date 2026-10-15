using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using StreamDuel.Core;
using Xunit;

[Collection("Localization current")]
public class T60_TimelineEdgeTests
{
    static BattleEvent Event(BattleEventKind kind, int amount = 0, string id = "test_id", int side = 1, int slot = 3)
    {
        return new BattleEvent { Kind = kind, LogIndex = 37, Turn = 8, Node = Node.First, Side = side, Slot = slot, Amount = amount, Id = id };
    }

    static string Dump<T>(T value)
    {
        return JsonSerializer.Serialize(value, new JsonSerializerOptions { IncludeFields = true });
    }

    public static IEnumerable<object[]> Mappings()
    {
        yield return new object[] { BattleEventKind.Act, new[] { CueKind.Cast, CueKind.Projectile }, new[] { 0.0, 0.35 }, new[] { 0.35, 0.25 } };
        yield return new object[] { BattleEventKind.Damage, new[] { CueKind.Hit, CueKind.Flash, CueKind.FloatText, CueKind.HpBar }, new[] { 0.0, 0.0, 0.0, 0.0 }, new[] { 0.2, 0.12, 0.8, 0.4 } };
        yield return new object[] { BattleEventKind.Heal, new[] { CueKind.FloatText, CueKind.HpBar }, new[] { 0.0, 0.0 }, new[] { 0.8, 0.4 } };
        yield return new object[] { BattleEventKind.Faint, new[] { CueKind.Faint }, new[] { 0.0 }, new[] { 0.5 } };
        yield return new object[] { BattleEventKind.Enter, new[] { CueKind.Enter }, new[] { 0.0 }, new[] { 0.4 } };
        yield return new object[] { BattleEventKind.Switch, new[] { CueKind.SwitchOut, CueKind.SwitchIn }, new[] { 0.0, 0.3 }, new[] { 0.3, 0.35 } };
        yield return new object[] { BattleEventKind.StatusAdd, new[] { CueKind.StatusIcon }, new[] { 0.0 }, new[] { 0.3 } };
        yield return new object[] { BattleEventKind.StatusRemove, new[] { CueKind.StatusIcon }, new[] { 0.0 }, new[] { 0.3 } };
        yield return new object[] { BattleEventKind.FieldAdd, new[] { CueKind.FieldBanner }, new[] { 0.0 }, new[] { 0.6 } };
        yield return new object[] { BattleEventKind.FieldRemove, new[] { CueKind.FieldBanner }, new[] { 0.0 }, new[] { 0.6 } };
        yield return new object[] { BattleEventKind.StageChange, new[] { CueKind.FloatText }, new[] { 0.0 }, new[] { 0.8 } };
        yield return new object[] { BattleEventKind.Crit, new[] { CueKind.FloatText }, new[] { 0.0 }, new[] { 0.8 } };
        yield return new object[] { BattleEventKind.Miss, new[] { CueKind.Miss, CueKind.FloatText }, new[] { 0.0, 0.0 }, new[] { 0.3, 0.8 } };
        yield return new object[] { BattleEventKind.End, new[] { CueKind.End }, new[] { 0.0 }, new[] { 0.0 } };
        yield return new object[] { BattleEventKind.PpChange, Array.Empty<CueKind>(), Array.Empty<double>(), Array.Empty<double>() };
        yield return new object[] { BattleEventKind.Info, Array.Empty<CueKind>(), Array.Empty<double>(), Array.Empty<double>() };
    }

    [Theory]
    [MemberData(nameof(Mappings))]
    public void EachEventMapsToItsSpecifiedCuesAndPreservesItsSourceLink(BattleEventKind kind, CueKind[] kinds, double[] starts, double[] durations)
    {
        int amount = kind == BattleEventKind.Damage || kind == BattleEventKind.Heal ? 73
            : kind == BattleEventKind.StatusAdd ? 2 : kind == BattleEventKind.StageChange ? -2 : 0;
        var source = Event(kind, amount, kind == BattleEventKind.StageChange ? "atk" : "test_id");
        var events = new[] { Event(BattleEventKind.Info), source, Event(BattleEventKind.Info) };

        var cues = BattleTimeline.Build(events, 1, 2);

        Assert.Equal(kinds.Length, cues.Count);
        for (int i = 0; i < kinds.Length; i++)
        {
            var cue = Assert.Single(cues, candidate => candidate.Kind == kinds[i]);
            Assert.Equal(starts[i], cue.Start, 9);
            Assert.Equal(durations[i], cue.Duration, 9);
            Assert.Equal(1, cue.EventIndex);
            Assert.Equal(source.LogIndex, cue.LogIndex);
            Assert.Equal(source.Side, cue.Side);
            Assert.Equal(source.Amount, cue.Amount);
            if (cue.Kind == CueKind.SwitchIn) continue;
            Assert.Equal(source.Slot, cue.Slot);
            Assert.Equal(source.Id, cue.Id);
        }
        if (kind == BattleEventKind.Damage || kind == BattleEventKind.Heal)
        {
            var text = Assert.Single(cues, cue => cue.Kind == CueKind.FloatText);
            Assert.Equal(kind == BattleEventKind.Damage ? "-73" : "+73", text.Text);
            Assert.Equal(kind == BattleEventKind.Damage ? "damage" : "heal", text.Style);
        }
    }

    [Fact]
    public void ConsecutiveEventsWaitForTheLongestCueWhileCriticalDamageRunsTogether()
    {
        var events = new[]
        {
            Event(BattleEventKind.Enter), Event(BattleEventKind.Act), Event(BattleEventKind.Crit, side: 0, slot: 1),
            Event(BattleEventKind.Damage, 90, side: 1, slot: 4), Event(BattleEventKind.Heal, 12),
            Event(BattleEventKind.StageChange, 1, "spd"), Event(BattleEventKind.Faint), Event(BattleEventKind.End)
        };
        var cues = BattleTimeline.Build(events, 0, events.Length);
        var expected = new[] { 0.0, 0.4, 1.0, 1.0, 1.8, 2.6, 3.4, 3.9 };

        for (int i = 0; i < expected.Length; i++)
            Assert.Equal(expected[i], cues.Where(cue => cue.EventIndex == i).Min(cue => cue.Start), 9);
        Assert.All(cues.Where(cue => cue.EventIndex == 3), cue => Assert.Equal(1.0, cue.Start, 9));
        var crit = Assert.Single(cues, cue => cue.EventIndex == 2);
        Assert.Equal("crit", crit.Style);
        Assert.Equal("暴击", crit.Text);
        Assert.Equal(0, crit.Side);
        Assert.Equal(1, crit.Slot);
        Assert.All(cues.Where(cue => cue.EventIndex == 3), cue => { Assert.Equal(1, cue.Side); Assert.Equal(4, cue.Slot); });
        for (int i = 1; i < cues.Count; i++) Assert.True(cues[i].Start >= cues[i - 1].Start);
        Assert.Equal(3.9, BattleTimeline.TotalDuration(cues), 9);
    }

    [Theory]
    [InlineData(BattleEventKind.Info)]
    [InlineData(BattleEventKind.PpChange)]
    public void InvisibleEventsDoNotAddTimeButBreakCriticalDamageAdjacency(BattleEventKind invisible)
    {
        var events = new[] { Event(BattleEventKind.Crit), Event(invisible), Event(BattleEventKind.Damage, 10), Event(BattleEventKind.End) };

        var cues = BattleTimeline.Build(events, 0, events.Length);

        Assert.DoesNotContain(cues, cue => cue.EventIndex == 1);
        Assert.Equal(0, Assert.Single(cues, cue => cue.EventIndex == 0).Start);
        Assert.All(cues.Where(cue => cue.EventIndex == 2), cue => Assert.Equal(0.8, cue.Start, 9));
        Assert.Equal(1.6, Assert.Single(cues, cue => cue.Kind == CueKind.End).Start, 9);
    }

    [Fact]
    public void CriticalDamageAtRangeBoundariesNeverPullsEventsFromOutsideTheRange()
    {
        var events = new[] { Event(BattleEventKind.Enter), Event(BattleEventKind.Crit), Event(BattleEventKind.Damage, 15), Event(BattleEventKind.Heal, 4) };

        var critOnly = BattleTimeline.Build(events, 1, 2);
        var damageOnly = BattleTimeline.Build(events, 2, 3);
        var paired = BattleTimeline.Build(events, 1, 3);

        Assert.Equal(1, Assert.Single(critOnly).EventIndex);
        Assert.All(damageOnly, cue => { Assert.Equal(2, cue.EventIndex); Assert.Equal(0, cue.Start); });
        Assert.All(paired, cue => Assert.Equal(0, cue.Start));
        Assert.Equal(critOnly.Count + damageOnly.Count, paired.Count);
        Assert.Equal(0.8, BattleTimeline.TotalDuration(critOnly), 9);
        Assert.Equal(0.8, BattleTimeline.TotalDuration(damageOnly), 9);
        Assert.Equal(0.8, BattleTimeline.TotalDuration(paired), 9);
    }

    [Fact]
    public void EmptyAndEntirelyInvisibleRangesProduceNoCuesOrDuration()
    {
        var events = new[] { Event(BattleEventKind.Info), Event(BattleEventKind.PpChange, -1) };

        Assert.Empty(BattleTimeline.Build(events, 0, 0));
        Assert.Empty(BattleTimeline.Build(events, events.Length, events.Length));
        var hidden = BattleTimeline.Build(events, 0, events.Length);
        Assert.Empty(hidden);
        Assert.Equal(0, BattleTimeline.TotalDuration(hidden));
    }

    [Fact]
    public void InvalidArgumentsAreRejectedInsteadOfReturningPartialTimelines()
    {
        var events = new[] { Event(BattleEventKind.Enter) };
        Assert.Throws<ArgumentNullException>(() => BattleTimeline.Build(null, 0, 0));
        foreach (var range in new[] { (-1, 0), (0, 2), (1, 0), (2, 2) })
            Assert.Throws<ArgumentOutOfRangeException>(() => BattleTimeline.Build(events, range.Item1, range.Item2));
        foreach (double speed in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            Assert.Throws<ArgumentOutOfRangeException>(() => BattleTimeline.Build(events, 0, 1, speed));
        Assert.Throws<ArgumentException>(() => BattleTimeline.Build(new BattleEvent[] { null }, 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => BattleTimeline.Build(new[] { Event((BattleEventKind)999) }, 0, 1));
        Assert.Throws<ArgumentNullException>(() => BattleTimeline.TotalDuration(null));
        Assert.Throws<ArgumentException>(() => BattleTimeline.TotalDuration(new AnimCue[] { null }));
    }

    [Fact]
    public void EventsOutsideTheRequestedRangeAreNotValidatedOrConsumed()
    {
        var events = new[] { null, Event(BattleEventKind.Crit), Event((BattleEventKind)999) };

        var cue = Assert.Single(BattleTimeline.Build(events, 1, 2));

        Assert.Equal(1, cue.EventIndex);
        Assert.Equal(CueKind.FloatText, cue.Kind);
        Assert.Equal(0, cue.Start);
        Assert.Equal(0.8, cue.Duration, 9);
    }

    [Theory]
    [InlineData(0.25)]
    [InlineData(2.0)]
    [InlineData(3.5)]
    [InlineData(0.0)]
    [InlineData(-2.0)]
    public void SpeedChangesOnlyCueTimesAndKeepsAllFinalStateInformation(double speed)
    {
        var events = new[] { Event(BattleEventKind.Act), Event(BattleEventKind.Crit), Event(BattleEventKind.Damage, 13), Event(BattleEventKind.Heal, 9), Event(BattleEventKind.End) };
        var normal = BattleTimeline.Build(events, 0, events.Length);
        var scaled = BattleTimeline.Build(events, 0, events.Length, speed);

        Assert.Equal(normal.Count, scaled.Count);
        for (int i = 0; i < normal.Count; i++)
        {
            Assert.Equal(speed <= 0 ? 0 : normal[i].Start / speed, scaled[i].Start, 9);
            Assert.Equal(speed <= 0 ? 0 : normal[i].Duration / speed, scaled[i].Duration, 9);
            scaled[i].Start = normal[i].Start;
            scaled[i].Duration = normal[i].Duration;
            Assert.Equal(Dump(normal[i]), Dump(scaled[i]));
        }
    }

    [Fact]
    public void TotalDurationUsesTheLatestEndingCueWithoutReorderingItsInput()
    {
        var cues = new List<AnimCue>
        {
            new AnimCue { Start = 3, Duration = 8 }, new AnimCue { Start = 8, Duration = 0.2 },
            new AnimCue { Start = 0, Duration = 0.4 }
        };
        string before = Dump(cues);

        Assert.Equal(11, BattleTimeline.TotalDuration(cues));
        Assert.Equal(before, Dump(cues));
    }

    [Fact]
    public void AllFloatTextUsesInjectedLocalizationIncludingEveryStatAndUnknownStats()
    {
        var previous = Loc.Current;
        try
        {
            Loc.Current = Loc.FromJson(JsonSerializer.Serialize(new
            {
                language = "test",
                texts = new Dictionary<string, string>
                {
                    { "timeline.damage", "D[{0}]" }, { "timeline.heal", "H[{0}]" },
                    { "timeline.crit", "CRITICAL" }, { "timeline.miss", "MISSED" },
                    { "timeline.stage.up", "UP[{0}:{1}]" }, { "timeline.stage.down", "DOWN[{0}:{1}]" },
                    { "timeline.stat.atk", "ATTACK" }, { "timeline.stat.def", "DEFENSE" },
                    { "timeline.stat.spa", "TALENT" }, { "timeline.stat.sdef", "FOCUS" },
                    { "timeline.stat.spd", "SPEED" }, { "timeline.stat.unknown", "UNKNOWN" }
                }
            }));
            var events = new List<BattleEvent>
            {
                Event(BattleEventKind.Damage, 7), Event(BattleEventKind.Heal, 3), Event(BattleEventKind.Crit), Event(BattleEventKind.Miss)
            };
            events.AddRange(new[] { "atk", "def", "spa", "sdef", "spd", "custom_stat" }
                .Select((stat, index) => Event(BattleEventKind.StageChange, index % 2 == 0 ? 1 : -2, stat)));

            var cues = BattleTimeline.Build(events, 0, events.Count);

            Assert.Equal(new[] { "D[7]", "H[3]", "CRITICAL", "MISSED", "UP[ATTACK:1]", "DOWN[DEFENSE:-2]",
                "UP[TALENT:1]", "DOWN[FOCUS:-2]", "UP[SPEED:1]", "DOWN[UNKNOWN:-2]" },
                cues.Where(cue => cue.Kind == CueKind.FloatText).Select(cue => cue.Text));
            Assert.Equal(new[] { "damage", "heal", "crit", "miss", "stage-up", "stage-down", "stage-up", "stage-down", "stage-up", "stage-down" },
                cues.Where(cue => cue.Kind == CueKind.FloatText).Select(cue => cue.Style));
        }
        finally { Loc.Current = previous; }
    }

    [Fact]
    public void BuildIsDeterministicDoesNotMutateReadOnlyEventsAndReturnsIndependentCues()
    {
        var events = Array.AsReadOnly(new[] { Event(BattleEventKind.Act), Event(BattleEventKind.Damage, 21), Event(BattleEventKind.StatusAdd, 2, "disconnect") });
        string before = Dump(events);
        var first = BattleTimeline.Build(events, 0, events.Count);
        var second = BattleTimeline.Build(events, 0, events.Count);
        string expected = Dump(second);

        Assert.Equal(expected, Dump(first));
        first[0].Text = "修改返回值";
        first[0].Start = 999;
        Assert.Equal(expected, Dump(second));
        Assert.Equal(expected, Dump(BattleTimeline.Build(events, 0, events.Count)));
        Assert.Equal(before, Dump(events));
    }

    [Fact]
    public void RealSwitchUsesTheIncomingEnterTargetWhileKeepingTheSwitchSourceIndex()
    {
        var db = BattleTests.LoadPack("original");
        var ids = db.Streamers.Select(streamer => streamer.Id).ToArray();
        var battle = new Battle(db, new IList<string>[] { ids.Take(6).ToArray(), ids.Skip(6).Take(6).ToArray() }, new[] { "甲方", "乙方" }, 60);
        battle.ResolveTurn(BattleAction.SwitchTo(1), BattleAction.Pass());
        int index = Enumerable.Range(0, battle.Events.Count).Single(i => battle.Events[i].Kind == BattleEventKind.Switch);
        var outgoing = battle.Events[index];
        int incomingIndex = Enumerable.Range(index + 1, battle.Events.Count - index - 1)
            .First(i => battle.Events[i].Kind != BattleEventKind.Info && battle.Events[i].Kind != BattleEventKind.PpChange);
        var incoming = battle.Events[incomingIndex];
        Assert.Equal(BattleEventKind.Enter, incoming.Kind);
        var unchanged = battle.Clone();
        string before = Dump(battle.Events);
        ulong hash = battle.StateHash();

        var cues = BattleTimeline.Build(battle.Events, index, incomingIndex + 1);

        var leave = Assert.Single(cues, cue => cue.Kind == CueKind.SwitchOut);
        var arrive = Assert.Single(cues, cue => cue.Kind == CueKind.SwitchIn);
        Assert.Equal(outgoing.Slot, leave.Slot);
        Assert.Equal(outgoing.Id, leave.Id);
        Assert.Equal(incoming.Slot, arrive.Slot);
        Assert.Equal(incoming.Id, arrive.Id);
        Assert.Equal(outgoing.LogIndex, arrive.LogIndex);
        Assert.Equal(index, arrive.EventIndex);
        Assert.Equal(0.3, arrive.Start, 9);
        var enter = Assert.Single(cues, cue => cue.Kind == CueKind.Enter);
        Assert.Equal(incomingIndex, enter.EventIndex);
        Assert.Equal(incoming.LogIndex, enter.LogIndex);
        Assert.Equal(0.65, enter.Start, 9);
        Assert.Equal(1.05, BattleTimeline.TotalDuration(cues), 9);
        Assert.Equal(before, Dump(battle.Events));
        Assert.Equal(hash, battle.StateHash());
        Assert.Equal(unchanged.Random(), battle.Random());
    }

    [Theory]
    [InlineData(BattleEventKind.Info)]
    [InlineData(BattleEventKind.PpChange)]
    public void SwitchInCanFindTheNextVisibleEnterPastInvisibleEvents(BattleEventKind invisible)
    {
        var events = new[]
        {
            Event(BattleEventKind.Switch, id: "old", side: 0, slot: 1), Event(invisible),
            Event(BattleEventKind.Enter, id: "new", side: 0, slot: 4)
        };

        var cues = BattleTimeline.Build(events, 0, events.Length);

        var arrive = Assert.Single(cues, cue => cue.Kind == CueKind.SwitchIn);
        Assert.Equal(0, arrive.Side);
        Assert.Equal(4, arrive.Slot);
        Assert.Equal("new", arrive.Id);
        Assert.Equal(0, arrive.EventIndex);
        Assert.Equal(events[0].LogIndex, arrive.LogIndex);
        Assert.DoesNotContain(cues, cue => cue.EventIndex == 1);
        Assert.Equal(0.65, Assert.Single(cues, cue => cue.Kind == CueKind.Enter).Start, 9);
    }

    [Theory]
    [InlineData("truncated")]
    [InlineData("side")]
    [InlineData("turn")]
    [InlineData("node")]
    [InlineData("not_adjacent")]
    public void SwitchInNeverUsesAnUnmatchedOrOutOfRangeEnter(string mismatch)
    {
        var outgoing = Event(BattleEventKind.Switch, id: "old", side: 0, slot: 1);
        var incoming = Event(BattleEventKind.Enter, id: "new", side: 0, slot: 4);
        if (mismatch == "side") incoming.Side = 1;
        if (mismatch == "turn") incoming.Turn++;
        if (mismatch == "node") incoming.Node = Node.Replace;
        var events = new List<BattleEvent> { outgoing };
        if (mismatch == "not_adjacent") events.Add(Event(BattleEventKind.FieldAdd));
        events.Add(incoming);

        var cues = BattleTimeline.Build(events, 0, mismatch == "truncated" ? 1 : events.Count);

        var arrive = Assert.Single(cues, cue => cue.Kind == CueKind.SwitchIn);
        Assert.Equal(0, arrive.Side);
        Assert.Equal(-1, arrive.Slot);
        Assert.Null(arrive.Id);
        Assert.Equal(0, arrive.EventIndex);
        Assert.Equal(outgoing.LogIndex, arrive.LogIndex);
    }
}
