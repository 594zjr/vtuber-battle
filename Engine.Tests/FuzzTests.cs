using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using StreamDuel.Core;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

public class FuzzTests
{
    readonly ITestOutputHelper output;

    public FuzzTests(ITestOutputHelper output)
    {
        this.output = output;
    }

    static string Resources([CallerFilePath] string source = null)
    {
        return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source), "..", "UnityProject", "Assets", "StreamDuel", "Resources"));
    }

    public static IEnumerable<object[]> Packs()
    {
        var files = Directory.GetFiles(Path.Combine(Resources(), "Packs"))
            .Where(path => string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.Ordinal).ToArray();
        Assert.NotEmpty(files);
        foreach (string path in files) yield return new object[] { Path.GetFileNameWithoutExtension(path) };
    }

    static int GameCount()
    {
        string value = Environment.GetEnvironmentVariable("SD_FUZZ");
        if (string.IsNullOrEmpty(value)) return 300;
        Assert.True(int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int count) && count >= 300,
            "SD_FUZZ 必须是不小于 300 的整数；完整检查使用 SD_FUZZ=2000。");
        return count;
    }

    static int Pick(Battle b, int count)
    {
        return (int)(b.Random() * count);
    }

    static IList<string> ShuffleTeam(Battle source, IList<string> ids)
    {
        var shuffled = ids.ToArray();
        for (int i = shuffled.Length - 1; i > 0; i--)
        {
            int other = Pick(source, i + 1);
            string saved = shuffled[i];
            shuffled[i] = shuffled[other];
            shuffled[other] = saved;
        }
        return shuffled.Take(6).ToList();
    }

    static BattleAction AnyAction(Battle b, int side, Coverage coverage)
    {
        var active = b.Active(side);
        BattleAction action;
        switch (Pick(b, 12))
        {
            case 0:
            case 1:
            case 2:
            case 3:
                // 不根据 LegalFor 过滤，才能触达 PP 耗尽和被控制时的非法出招。
                action = BattleAction.Skill(Pick(b, active.Skills.Count));
                break;
            case 4:
                action = BattleAction.Skill(Pick(b, 10) - 3);
                break;
            case 5:
                action = BattleAction.Skill(Pick(b, 2) == 0 ? int.MinValue : int.MaxValue);
                break;
            case 6:
                action = BattleAction.SwitchTo(Pick(b, b.Sides[side].Team.Count + 4) - 2);
                break;
            case 7:
                action = BattleAction.SwitchTo(b.Sides[side].Active);
                break;
            case 8:
                var fainted = Enumerable.Range(0, b.Sides[side].Team.Count).Where(i => b.Sides[side].Team[i].Fainted).ToArray();
                action = BattleAction.SwitchTo(fainted.Length == 0 ? Pick(b, b.Sides[side].Team.Count) : fainted[Pick(b, fainted.Length)]);
                break;
            case 9:
                action = BattleAction.Pass();
                break;
            case 10:
                action = new BattleAction { Type = Pick(b, 2) == 0 ? ActionType.Struggle : ActionType.PassControlled, Index = -1 };
                break;
            default:
                action = new BattleAction { Type = (ActionType)(Pick(b, 2) == 0 ? -1 : int.MaxValue), Index = int.MinValue };
                coverage.UnknownTypes++;
                break;
        }
        if (action.Type == ActionType.Skill)
        {
            if (action.Index < 0 || action.Index >= active.Skills.Count) coverage.InvalidSkills++;
            else if (active.Skills[action.Index].Pp == 0) coverage.EmptyPp++;
            if (b.ControlOf(active) != null) coverage.ControlledSkills++;
        }
        if (action.Type == ActionType.Switch)
        {
            if (action.Index < 0 || action.Index >= b.Sides[side].Team.Count || action.Index == b.Sides[side].Active)
                coverage.InvalidSwitches++;
            else if (b.Sides[side].Team[action.Index].Fainted) coverage.FaintedSwitches++;
        }
        return action;
    }

    static void CheckState(Battle b)
    {
        Assert.InRange(b.Turn, 1, Battle.TurnCap);
        Assert.True(Enum.IsDefined(typeof(Phase), b.Phase), "出现未定义的阶段");
        for (int side = 0; side < b.Sides.Length; side++)
        {
            var state = b.Sides[side];
            Assert.InRange(state.Active, 0, state.Team.Count - 1);
            for (int slot = 0; slot < state.Team.Count; slot++)
            {
                var fighter = state.Team[slot];
                Assert.Equal(side, fighter.Side);
                Assert.Equal(slot + 1, fighter.Slot);
                Assert.True(fighter.MaxHp > 0, "最大人气必须为正");
                Assert.InRange(fighter.Hp, 0, fighter.MaxHp);
                Assert.Equal(fighter.Hp == 0, fighter.Fainted);
                foreach (var skill in fighter.Skills) Assert.InRange(skill.Pp, 0, skill.Max);
                foreach (int stage in fighter.Stages.Values) Assert.InRange(stage, -6, 6);
                Assert.InRange(fighter.Statuses.Count(status => b.Db.Statuses[status.Id].IsControl), 0, 1);
            }
            if (b.Phase == Phase.Choose)
            {
                Assert.False(b.Need[side], "选择行动阶段不能仍等待补位");
                Assert.False(b.Active(side).Fainted, "下播者不能留在选择行动阶段");
            }
            if (b.Phase == Phase.Replace) Assert.Equal(b.Active(side).Fainted, b.Need[side]);
        }
        if (b.Phase == Phase.Replace) Assert.True(b.Need.Any(need => need), "补位阶段至少有一方需要补位");
        if (b.Phase == Phase.Over)
        {
            Assert.InRange(b.Winner, -1, 1);
            Assert.Equal(Node.Over, b.Log.Last().Node);
            Assert.Single(b.Log, entry => entry.Node == Node.Over);
        }
        else
        {
            Assert.Equal(-2, b.Winner);
            Assert.All(b.Sides, side => Assert.Contains(side.Team, fighter => !fighter.Fainted));
        }
    }

    static void Advance(Battle b, Coverage coverage, ref string operation)
    {
        Phase phase = b.Phase;
        int turn = b.Turn;
        int records = b.Record.Events.Count;
        if (phase == Phase.Ban || phase == Phase.Lead)
        {
            var pending = Enumerable.Range(0, 2).Where(side => phase == Phase.Ban ? !b.HasChosenBan(side) : !b.HasChosenLead(side)).ToArray();
            Assert.NotEmpty(pending);
            int side = pending[Pick(b, pending.Length)];
            int index = Pick(b, b.Sides[phase == Phase.Ban ? 1 - side : side].Team.Count);
            operation = phase + " side=" + side + " index=" + index;
            if (phase == Phase.Ban) b.ChooseBan(side, index);
            else b.ChooseLead(side, index);
            Phase expected = pending.Length == 2 ? phase : phase == Phase.Ban && b.Record.LeadPick ? Phase.Lead : Phase.Choose;
            Assert.Equal(expected, b.Phase);
            Assert.Equal(turn, b.Turn);
            int teamSize = phase == Phase.Ban && pending.Length == 2 ? 6 : b.Record.BanOne ? 5 : 6;
            Assert.All(b.Sides, state => Assert.Equal(teamSize, state.Team.Count));
        }
        else if (phase == Phase.Choose)
        {
            BattleAction a0 = AnyAction(b, 0, coverage), a1 = AnyAction(b, 1, coverage);
            operation = "Turn " + turn + ": " + a0 + " / " + a1;
            b.ResolveTurn(a0, a1);
            Assert.Contains(b.Phase, new[] { Phase.Choose, Phase.Replace, Phase.Over });
            Assert.Equal(b.Phase == Phase.Over ? turn : turn + 1, b.Turn);
            Assert.Equal(BattleRecordEventType.Turn, b.Record.Events.Last().Type);
            Assert.Equal(a0, b.Record.Events.Last().A0);
            Assert.Equal(a1, b.Record.Events.Last().A1);
        }
        else if (phase == Phase.Replace)
        {
            var pending = Enumerable.Range(0, 2).Where(side => b.Need[side]).ToArray();
            Assert.NotEmpty(pending);
            int side = pending[Pick(b, pending.Length)];
            // 补位 API 明确拒绝非法位置；非法换人通过任意回合行动交给 Normalize 检验。
            var choices = Enumerable.Range(0, b.Sides[side].Team.Count)
                .Where(index => index != b.Sides[side].Active && !b.Sides[side].Team[index].Fainted).ToArray();
            Assert.NotEmpty(choices);
            int index = choices[Pick(b, choices.Length)];
            operation = "Replace side=" + side + " index=" + index;
            b.Replace(side, index);
            Assert.Contains(b.Phase, new[] { Phase.Replace, Phase.Choose, Phase.Over });
            Assert.Equal(turn, b.Turn);
            Assert.Equal(index, b.Sides[side].Active);
            Assert.False(b.Need[side]);
        }
        else throw new XunitException("已结束或未知阶段不应继续派发操作：" + phase);
        Assert.Equal(records + 1, b.Record.Events.Count);
        CheckState(b);
    }

    [Theory]
    [MemberData(nameof(Packs))]
    public void ArbitraryActionsPreserveInvariantsAndReplay(string pack)
    {
        var db = ContentDb.FromJson(File.ReadAllText(Path.Combine(Resources(), "StreamDuelRules.json")),
            File.ReadAllText(Path.Combine(Resources(), "Packs", pack + ".json")));
        var ids = db.Streamers.Select(streamer => streamer.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        Assert.True(ids.Length >= 6, "模糊测试要求内容包至少能组成合法六人队伍：" + pack);
        int games = GameCount();
        var coverage = new Coverage();
        var modes = new HashSet<string>();
        for (int game = 0; game < games; game++)
        {
            uint seed = (uint)game;
            Battle b = null;
            string operation = "准备队伍";
            try
            {
                // 准备阶段也借用 Battle 的随机流，避免测试引入时间或平台相关随机源。
                var preparation = new Battle(db, new IList<string>[] { ids.Take(6).ToArray(), ids.Take(6).ToArray() },
                    new[] { "准备甲", "准备乙" }, new BattleOptions { Seed = seed, LeadPick = true });
                var teams = new[] { ShuffleTeam(preparation, ids), ShuffleTeam(preparation, ids) };
                foreach (var team in teams) Assert.Empty(TeamCheck.Validate(db, null, team));
                var options = new BattleOptions { Seed = seed, BanOne = Pick(preparation, 2) == 1, LeadPick = Pick(preparation, 2) == 1 };
                modes.Add(options.BanOne + "/" + options.LeadPick);
                b = new Battle(db, teams, new[] { "模糊甲", "模糊乙" }, options);
                CheckState(b);
                int steps = 0;
                while (b.Phase != Phase.Over && steps++ < Battle.TurnCap * 3 + 4) Advance(b, coverage, ref operation);
                Assert.Equal(Phase.Over, b.Phase);
                Assert.InRange(b.Record.Events.Count(e => e.Type == BattleRecordEventType.Turn), 1, Battle.TurnCap);
                operation = "序列化并重放";
                string json = b.Record.ToJson();
                var replay = Replay.Run(db, BattleRecord.FromJson(json));
                CheckState(replay);
                Assert.Equal(Phase.Over, replay.Phase);
                Assert.Equal(b.StateHash(), replay.StateHash());
                Assert.Equal(b.Winner, replay.Winner);
                Assert.Equal(json, replay.Record.ToJson());
            }
            catch (Exception error)
            {
                string record = b == null ? "尚未构造对战" : b.Record.ToJson();
                throw new XunitException("模糊测试失败：pack=" + pack + " seed=" + seed + " 操作=" + operation +
                    "\n" + error + "\n记录 JSON：\n" + record);
            }
        }
        Assert.Equal(4, modes.Count);
        Assert.True(coverage.InvalidSkills > 0 && coverage.InvalidSwitches > 0 && coverage.UnknownTypes > 0,
            "随机输入未覆盖非法技能、非法换人和未知行动类型");
        output.WriteLine("{0}: {1} 局 + {1} 次完整回放；非法技能 {2}，非法换人 {3}，换到下播者 {4}，受控出招 {5}，零 PP 出招 {6}，未知行动类型 {7}。",
            pack, games, coverage.InvalidSkills, coverage.InvalidSwitches, coverage.FaintedSwitches,
            coverage.ControlledSkills, coverage.EmptyPp, coverage.UnknownTypes);
    }

    sealed class Coverage
    {
        public int InvalidSkills, InvalidSwitches, FaintedSwitches, ControlledSkills, EmptyPp, UnknownTypes;
    }
}
