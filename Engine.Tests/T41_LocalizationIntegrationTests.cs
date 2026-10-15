using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using StreamDuel.Core;
using Xunit;

[Collection("Localization current")]
public class T41_LocalizationIntegrationTests
{
    static Battle Make()
    {
        var db = BattleTests.LoadPack("original");
        db.Statuses["view_control"] = new StatusDef
        {
            Id = "view_control", Name = "control", Cat = "control", Skip = 2, Desc = "control description"
        };
        db.Statuses["view_weak"] = new StatusDef
        {
            Id = "view_weak", Name = "weak", Cat = "weak", Turns = 3, Cleanse = true, Desc = "weak description"
        };
        db.Fields["view_turn"] = new FieldDef
        {
            Id = "view_turn", Name = "turn", Kind = "turn", Dispel = true, When = "after turn", Desc = "turn description"
        };
        db.Fields["view_mark"] = new FieldDef
        {
            Id = "view_mark", Name = "mark", Kind = "mark", MaxStacks = 5, When = "before hit", Desc = "mark description"
        };
        db.Fields["view_protect"] = new FieldDef
        {
            Id = "view_protect", Name = "protect", Kind = "protect", When = "on hit", Desc = "protect description"
        };
        db.Skills["view_timing"] = new SkillDef
        {
            Id = "view_timing", Name = "timing", Type = "talk", Cat = "status", Pp = 10,
            Effects = new List<EffectDef>
            {
                new EffectDef { Code = "HEAL", At = "pre", Target = "self", Frac = 0.25 },
                new EffectDef { Code = "HEAL", At = "post", Target = "self", Frac = 0.5 }
            }
        };
        db.Streamer("bell").Skills[0] = "view_timing";
        var ids = db.Streamers.Select(fighter => fighter.Id).ToArray();
        var teams = new IList<string>[]
        {
            new[] { "bell" }.Concat(ids.Where(id => id != "bell").Take(5)).ToArray(),
            new[] { "kei" }.Concat(ids.Where(id => id != "kei").Take(5)).ToArray()
        };
        var battle = new Battle(db, teams, new[] { "你", "对手" }, 41);
        battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());
        var active = battle.Active(0);
        active.Statuses.Add(new StatusInst { Id = "view_control", Left = 2 });
        active.Statuses.Add(new StatusInst { Id = "view_weak", Left = 3 });
        active.Fields.Add(new FieldInst { Id = "view_turn", Turns = 4 });
        active.Fields.Add(new FieldInst { Id = "view_mark", Turns = -1, Stacks = 3 });
        active.Fields.Add(new FieldInst { Id = "view_protect", Turns = -1 });
        return battle;
    }

    [Fact]
    public void DefaultChineseChipAndShortTimingTextRemainsUnchanged()
    {
        var previous = Loc.Current;
        try
        {
            Loc.Current = null;
            var view = BattleViewModel.From(Make(), 0);
            var chips = view.Sides[0].Team[0].Statuses.ToDictionary(chip => chip.Id);

            Assert.Equal("control · 控制类 · 剩余：跳过 2 次 · 时机：自身行动时 · 不可净化\ncontrol description", chips["view_control"].Tooltip);
            Assert.Equal("weak · 弱化类 · 剩余：3 回合 · 时机：回合结束 · 可净化\nweak description", chips["view_weak"].Tooltip);
            Assert.Equal("turn · 回合类 · 剩余：4 回合 · 时机：after turn · 可被消回合 · 下场清空\nturn description", chips["view_turn"].Tooltip);
            Assert.Equal("mark · 印记 · 3 层 · 剩余：直到下场 · 时机：before hit · 消回合清不掉 · 下场清空\nmark description", chips["view_mark"].Tooltip);
            Assert.Equal("protect · 保护 · 剩余：直到下场 · 时机：on hit · 消回合清不掉 · 下场清空\nprotect description", chips["view_protect"].Tooltip);
            Assert.Equal("跳过 2 次", chips["view_control"].Duration);
            Assert.Equal("3 回合", chips["view_weak"].Duration);
            Assert.Equal("4 回合", chips["view_turn"].Duration);
            Assert.Equal("3 层", chips["view_mark"].Duration);
            Assert.Equal("直到下场", chips["view_protect"].Duration);
            Assert.Equal(new[] { "命中前", "命中后" }, view.Skills[0].Effects.Select(effect => effect.Timing));
        }
        finally { Loc.Current = previous; }
    }

    [Fact]
    public void InjectedLocaleReachesSnapshotChipsAndTimingWithoutChangingBattleEvents()
    {
        var previous = Loc.Current;
        try
        {
            Loc.Current = null;
            var chinese = Loc.Current;
            var battle = Make();
            var original = BattleViewModel.From(battle, 0);
            var jsonOptions = new JsonSerializerOptions { IncludeFields = true };
            string events = JsonSerializer.Serialize(battle.Events, jsonOptions);
            ulong hash = battle.StateHash();
            Assert.Contains(battle.Events, entry => entry.Kind == BattleEventKind.Act);
            var texts = new Dictionary<string, string>
            {
                { "describe.at.pre.short", "before hit" }, { "describe.at.post.short", "after hit" },
                { "describe.at.self.action", "own action" }, { "describe.at.turn.end", "turn end" },
                { "describe.status.control", "control category" }, { "describe.status.weak", "weak category" },
                { "describe.duration.actions", "skip {0} actions" }, { "describe.duration.turns", "{0} turns" },
                { "describe.duration.stacks", "{0} stacks" }, { "describe.duration.until.leave", "until leaving" },
                { "describe.dispel.cleanse", "cleansable" }, { "describe.dispel.no.cleanse", "uncleansable" },
                { "describe.dispel.turns", "dispellable" }, { "describe.dispel.no.turns", "undispellable" },
                { "describe.field.turn", "timed" }, { "describe.field.mark", "mark category" },
                { "describe.field.protect", "protection" }, { "describe.field.layers", " [{0}]" },
                { "describe.status.tooltip", "{0}: {1}; remaining {2}; timing {3}; {4}\n{5}" },
                { "describe.field.tooltip", "{0}: {1}{2}; remaining {3}; timing {4}; {5}; cleared on leaving\n{6}" }
            };
            Loc.Current = Loc.FromJson(JsonSerializer.Serialize(new { language = "en", texts }), chinese);
            var translated = BattleViewModel.From(battle, 0);
            var chips = translated.Sides[0].Team[0].Statuses.ToDictionary(chip => chip.Id);

            Assert.Equal("control: control category; remaining skip 2 actions; timing own action; uncleansable\ncontrol description", chips["view_control"].Tooltip);
            Assert.Equal("weak: weak category; remaining 3 turns; timing turn end; cleansable\nweak description", chips["view_weak"].Tooltip);
            Assert.Equal("turn: timed; remaining 4 turns; timing after turn; dispellable; cleared on leaving\nturn description", chips["view_turn"].Tooltip);
            Assert.Equal("mark: mark category [3 stacks]; remaining until leaving; timing before hit; undispellable; cleared on leaving\nmark description", chips["view_mark"].Tooltip);
            Assert.Equal("protect: protection; remaining until leaving; timing on hit; undispellable; cleared on leaving\nprotect description", chips["view_protect"].Tooltip);
            Assert.Equal("own action", chips["view_control"].Timing);
            Assert.Equal("turn end", chips["view_weak"].Timing);
            Assert.Equal("skip 2 actions", chips["view_control"].Duration);
            Assert.Equal("3 turns", chips["view_weak"].Duration);
            Assert.Equal("4 turns", chips["view_turn"].Duration);
            Assert.Equal("3 stacks", chips["view_mark"].Duration);
            Assert.Equal("until leaving", chips["view_protect"].Duration);
            Assert.Equal(new[] { "before hit", "after hit" }, translated.Skills[0].Effects.Select(effect => effect.Timing));
            Assert.Equal(new[] { "命中前", "命中后" }, original.Skills[0].Effects.Select(effect => effect.Timing));
            Assert.Equal(events, JsonSerializer.Serialize(battle.Events, jsonOptions));
            Assert.Equal(hash, battle.StateHash());
        }
        finally { Loc.Current = previous; }
    }
}
