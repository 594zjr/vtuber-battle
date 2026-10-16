using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using StreamDuel.Core;
using Xunit;

public class T72_OriginalWave3Tests
{
    const string PackId = "original_wave3";
    static readonly string[] Roles = { "快攻", "消耗", "控制", "强化", "反击", "辅助" };

    static ContentDb Load() => BattleTests.LoadPack(PackId);

    static List<ContentDb> OtherPacks()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "Content", "Packs");
        return Directory.GetFiles(directory, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => name != PackId)
            .OrderBy(name => name, StringComparer.Ordinal)
            .Select(BattleTests.LoadPack)
            .ToList();
    }

    [Fact]
    public void PackLoadsWithTwelveDistinctOriginalStreamers()
    {
        var db = Load();
        db.Validate();
        Assert.Equal(PackId, db.PackId);
        Assert.Equal(12, db.Streamers.Count);
        Assert.Equal(12, db.Streamers.Select(s => s.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(12, db.Streamers.Select(s => s.Name).Distinct(StringComparer.Ordinal).Count());

        var others = OtherPacks();
        Assert.NotEmpty(others);
        var otherIds = others.SelectMany(pack => pack.Streamers.Select(s => s.Id)).ToHashSet(StringComparer.Ordinal);
        var otherNames = others.SelectMany(pack => pack.Streamers.Select(s => s.Name)).ToHashSet(StringComparer.Ordinal);
        foreach (var streamer in db.Streamers)
        {
            Assert.StartsWith("og3_", streamer.Id);
            Assert.False(string.IsNullOrWhiteSpace(streamer.Name), streamer.Id + " 缺少名字");
            Assert.DoesNotContain(streamer.Id, otherIds);
            Assert.DoesNotContain(streamer.Name, otherNames);
            Assert.True(string.IsNullOrEmpty(streamer.Org) || streamer.Org == "原创", streamer.Id + " 不应归属真实组织");
            foreach (string stat in new[] { "hp", "atk", "def", "spa", "sdef", "spd" })
            {
                Assert.True(streamer.Stats.TryGetValue(stat, out int value), streamer.Id + " 缺少属性 " + stat);
                Assert.True(value > 0, streamer.Id + " 的属性 " + stat + " 必须是正整数");
            }
        }
    }

    [Fact]
    public void PackLocalStatusesAndFieldsHaveTheWaveThreePrefix()
    {
        var db = Load();
        string path = Path.Combine(AppContext.BaseDirectory, "Content", "Packs", PackId + ".json");
        var pack = Assert.IsType<Dictionary<string, object>>(MiniJson.Parse(File.ReadAllText(path)));
        // 状态和直播间效果没有 PackDefined 标记，直接检查本包声明，避免把共用规则误当新增内容。
        foreach (string section in new[] { "statuses", "fields" })
        {
            if (!pack.TryGetValue(section, out object value)) continue;
            var definitions = Assert.IsType<Dictionary<string, object>>(value);
            foreach (string id in definitions.Keys)
            {
                Assert.StartsWith("og3_", id);
                Assert.True(section == "statuses" ? db.Statuses.ContainsKey(id) : db.Fields.ContainsKey(id),
                    "本包 " + section + " 定义没有载入：" + id);
            }
        }
    }

    [Fact]
    public void EveryLoadoutHasFourLegalSkillsAndAnOriginalFifthAndPersona()
    {
        var db = Load();
        var others = OtherPacks();
        var otherSkillNames = others.SelectMany(pack => pack.Skills.Values.Select(skill => skill.Name)).ToHashSet(StringComparer.Ordinal);
        var otherPersonaNames = others.SelectMany(pack => pack.Personas.Values.Select(persona => persona.Name)).ToHashSet(StringComparer.Ordinal);

        foreach (var streamer in db.Streamers)
        {
            Assert.Equal(4, streamer.Skills.Count);
            Assert.Equal(4, streamer.Skills.Distinct(StringComparer.Ordinal).Count());
            Assert.False(string.IsNullOrWhiteSpace(streamer.Fifth), streamer.Id + " 缺少第五技能");
            Assert.DoesNotContain(streamer.Fifth, streamer.Skills);
            foreach (string id in streamer.Skills.Concat(new[] { streamer.Fifth }))
            {
                Assert.True(db.Skills.TryGetValue(id, out var skill), streamer.Id + " 引用了不存在的技能 " + id);
                Assert.False(string.IsNullOrWhiteSpace(skill.Name), id + " 缺少原创技能名");
                Assert.DoesNotContain(skill.Name, otherSkillNames);
            }
            Assert.True(db.Personas.TryGetValue(streamer.Persona, out var persona), streamer.Id + " 引用了不存在的人设");
            Assert.False(string.IsNullOrWhiteSpace(persona.Name), streamer.Id + " 缺少原创人设名");
            Assert.False(string.IsNullOrWhiteSpace(persona.Desc), streamer.Id + " 缺少人设说明");
            Assert.DoesNotContain(persona.Name, otherPersonaNames);
        }

        foreach (var skill in db.Skills.Values.Where(skill => skill.PackDefined))
            Assert.StartsWith("og3_", skill.Id);
        foreach (var persona in db.Personas.Values.Where(persona => persona.PackDefined))
            Assert.StartsWith("og3_", persona.Id.Split('#').Last());
    }

    [Fact]
    public void RosterCoversAllEightTracksDualTracksAndEveryDocumentedRole()
    {
        var db = Load();
        string[] tracks = { "song", "game", "talk", "art", "meme", "asmr", "horror", "tech" };
        Assert.Equal(tracks.OrderBy(id => id), db.Streamers.Select(s => s.Type).Distinct().OrderBy(id => id));
        Assert.True(db.Streamers.Count(s => !string.IsNullOrEmpty(s.Type2)) >= 3, "第三期至少需要 3 位双赛道主播");
        foreach (var streamer in db.Streamers)
        {
            if (streamer.Type2 != null)
            {
                Assert.Contains(streamer.Type2, db.Types.Keys);
                Assert.NotEqual(streamer.Type, streamer.Type2);
            }
            Assert.False(string.IsNullOrWhiteSpace(streamer.Blurb), streamer.Id + " 缺少打法简介");
            Assert.True(Roles.Any(role => streamer.Blurb.Contains(role)), streamer.Id + " 的简介需要明确打法定位");
        }
        foreach (string role in Roles)
            Assert.Contains(db.Streamers, streamer => streamer.Blurb.Contains(role));
    }

    public static IEnumerable<object[]> BattleSeeds()
    {
        for (uint seed = 0; seed < 20; seed++) yield return new object[] { seed };
    }

    [Theory]
    [MemberData(nameof(BattleSeeds))]
    public void TwentySeededAiBattlesFinishAndReplayExactly(uint seed)
    {
        var db = Load();
        var teams = DrawTeams(db, seed);
        Assert.Empty(TeamCheck.Validate(db, null, teams[0]));
        Assert.Empty(TeamCheck.Validate(db, null, teams[1]));
        Assert.Equal(12, teams.SelectMany(team => team).Distinct(StringComparer.Ordinal).Count());

        var battle = new Battle(db, teams, new[] { "第三期甲队", "第三期乙队" }, new BattleOptions
        {
            Seed = seed,
            BanOne = seed % 5 == 0,
            LeadPick = seed % 3 == 0
        });
        PlayToEnd(battle);
        Assert.Equal(Phase.Over, battle.Phase);
        Assert.InRange(battle.Winner, -1, 1);
        Assert.InRange(battle.Turn, 1, Battle.TurnCap);
        Assert.Contains(battle.Log, entry => entry.Node == Node.Over);
        Assert.Contains(battle.Record.Events, entry => entry.Type == BattleRecordEventType.Turn);

        string json = battle.Record.ToJson();
        var replay = Replay.Run(db, BattleRecord.FromJson(json));
        Assert.Equal(battle.Phase, replay.Phase);
        Assert.Equal(battle.Winner, replay.Winner);
        Assert.Equal(battle.StateHash(), replay.StateHash());
        Assert.Equal(json, replay.Record.ToJson());
        AssertSameLogs(battle, replay);
    }

    static IList<string>[] DrawTeams(ContentDb db, uint seed)
    {
        var roster = db.Streamers.Select(s => s.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        // 抽阵容也使用引擎的固定种子随机流，不依赖运行时或测试执行顺序。
        var draw = new Battle(db, new IList<string>[] { roster.Take(6).ToArray(), roster.Skip(6).Take(6).ToArray() },
            new[] { "抽签甲", "抽签乙" }, seed, leadPick: true);
        for (int i = roster.Length - 1; i > 0; i--)
        {
            int selected = (int)(draw.Random() * (i + 1));
            string saved = roster[i];
            roster[i] = roster[selected];
            roster[selected] = saved;
        }
        return new IList<string>[] { roster.Take(6).ToArray(), roster.Skip(6).Take(6).ToArray() };
    }

    static void PlayToEnd(Battle battle)
    {
        for (int steps = 0; steps < Battle.TurnCap * 3 + 20 && battle.Phase != Phase.Over; steps++)
        {
            if (battle.Phase == Phase.Ban)
            {
                for (int side = 0; side < 2 && battle.Phase == Phase.Ban; side++)
                    if (!battle.HasChosenBan(side)) battle.ChooseBan(side, Ai.ChooseBan(battle, side));
            }
            else if (battle.Phase == Phase.Lead)
            {
                for (int side = 0; side < 2 && battle.Phase == Phase.Lead; side++)
                    if (!battle.HasChosenLead(side)) battle.ChooseLead(side, Ai.ChooseLead(battle, side));
            }
            else if (battle.Phase == Phase.Replace)
            {
                for (int side = 0; side < 2 && battle.Phase == Phase.Replace; side++)
                    if (battle.Need[side]) battle.Replace(side, Ai.ChooseReplacement(battle, side, AiLevel.Normal));
            }
            else if (battle.Phase == Phase.Choose)
            {
                battle.ResolveTurn(Ai.Choose(battle, 0, AiLevel.Normal), Ai.Choose(battle, 1, AiLevel.Normal));
            }
            else
            {
                throw new InvalidOperationException("意外对战阶段：" + battle.Phase);
            }
        }
    }

    static void AssertSameLogs(Battle expected, Battle actual)
    {
        Assert.Equal(expected.Log.Count, actual.Log.Count);
        for (int i = 0; i < expected.Log.Count; i++)
        {
            var left = expected.Log[i];
            var right = actual.Log[i];
            Assert.Equal(left.Turn, right.Turn);
            Assert.Equal(left.Node, right.Node);
            Assert.Equal(left.Side, right.Side);
            Assert.Equal(left.Kind, right.Kind);
            Assert.Equal(left.Text, right.Text);
            Assert.Equal(left.SnapActive, right.SnapActive);
            Assert.Equal(left.SnapHp[0], right.SnapHp[0]);
            Assert.Equal(left.SnapHp[1], right.SnapHp[1]);
        }
    }
}
