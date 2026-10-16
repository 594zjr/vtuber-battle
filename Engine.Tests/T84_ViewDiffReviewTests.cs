using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using StreamDuel.Core;
using Xunit;

public class T84_ViewDiffReviewTests
{
    static readonly JsonSerializerOptions Json = new JsonSerializerOptions
    {
        IncludeFields = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void ConsecutiveRealPhasesRebuildBothViewersWithoutReplacingThePatchedDocument(int viewerSide)
    {
        var battle = Make(true);
        BattleViewModel previous = null;
        JsonNode document = null;
        var visited = new HashSet<Phase>();
        var kinds = new HashSet<string>();
        int batches = 0;

        void Check(Phase expected)
        {
            Assert.Equal(expected, battle.Phase);
            visited.Add(battle.Phase);
            var next = BattleViewModel.From(battle, viewerSide);
            var delta = ViewDiff.Between(previous, next);
            foreach (var change in delta) kinds.Add(change.Kind);
            // 沿用上批补丁应用后的文档，不能重新序列化 previous 来掩盖累计索引错误。
            document = Apply(document, delta);
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(Serialize(next)), document),
                "第 " + batches + " 批无法重建 " + expected + " 阶段、观看方 " + viewerSide + " 的完整视图。");
            previous = next;
            batches++;
        }

        Check(Phase.Ban);
        battle.ChooseBan(0, 5);
        Check(Phase.Ban);
        battle.ChooseBan(1, 5);
        Check(Phase.Lead);
        Assert.All(battle.Sides, side => Assert.Equal(5, side.Team.Count));
        battle.ChooseLead(0, 2);
        Check(Phase.Lead);
        battle.ChooseLead(1, 3);
        Check(Phase.Choose);
        battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Skill(0));
        Check(Phase.Choose);

        for (int defeated = 0; defeated < 5; defeated++)
        {
            battle.ResolveTurn(BattleAction.Skill(1), BattleAction.Pass());
            if (defeated == 4)
            {
                Check(Phase.Over);
                break;
            }
            Check(Phase.Replace);
            Assert.True(battle.Need[1]);
            Assert.False(battle.Need[0]);
            battle.Replace(1, TurnClock.TimeoutReplacement(battle, 1));
            Check(Phase.Choose);
        }

        Assert.Equal(0, battle.Winner);
        Assert.Equal(15, batches);
        Assert.True(visited.SetEquals(new[] { Phase.Ban, Phase.Lead, Phase.Choose, Phase.Replace, Phase.Over }));
        Assert.True(kinds.SetEquals(new[] { "set", "add", "remove" }));
    }

    [Fact]
    public void ExistingInitialAndIncrementalPatchesSurviveDeepSourceCollectionMutationAndLaterCalls()
    {
        var battle = Make(false);
        var before = BattleViewModel.From(battle, 0);
        battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Skill(0));
        var after = BattleViewModel.From(battle, 0);
        // 整个新增队员与删去的技能携带嵌套集合，覆盖 Old/New 的复合 JSON 值快照。
        after.Sides[0].Team.Add(new FighterView
        {
            Id = "added", Name = "新增队员",
            Stages = new Dictionary<string, int> { { "atk", 2 } },
            Statuses = new List<ChipView> { new ChipView { Id = "added-status", Turns = 4 } },
            TrackViews = new List<TrackView> { new TrackView { Id = "talk", Name = "聊天" } }
        });
        before.Skills.Add(new SkillView
        {
            Id = "removed", PriorityParts = new List<string> { "旧先制来源" },
            Effects = new List<SkillEffectView> { new SkillEffectView { Timing = "命中后", Text = "旧效果" } }
        });
        string beforeJson = Serialize(before), afterJson = Serialize(after);
        var initial = ViewDiff.Between(null, after);
        var incremental = ViewDiff.Between(before, after);
        Assert.Contains(incremental, change => change.Path == "sides/0/team/6" && change.Kind == "add");
        Assert.Contains(incremental, change => change.Path == "skills/5" && change.Kind == "remove");
        string initialJson = Serialize(initial), incrementalJson = Serialize(incremental);

        CorruptSourceCollections(before);
        CorruptSourceCollections(after);
        Assert.NotEqual(beforeJson, Serialize(before));
        Assert.NotEqual(afterJson, Serialize(after));
        Assert.NotEmpty(ViewDiff.Between(null, after));

        Assert.Equal(initialJson, Serialize(initial));
        Assert.Equal(incrementalJson, Serialize(incremental));
        var expected = JsonNode.Parse(afterJson);
        Assert.True(JsonNode.DeepEquals(expected, Apply(null, initial)));
        Assert.True(JsonNode.DeepEquals(expected, Apply(JsonNode.Parse(beforeJson), incremental)));
    }

    static void CorruptSourceCollections(BattleViewModel view)
    {
        foreach (var side in view.Sides)
        {
            foreach (var fighter in side.Team)
            {
                fighter.Name = "已修改";
                fighter.Stages.Clear();
                foreach (var chip in fighter.Statuses) chip.Turns = 999;
                fighter.Statuses.Clear();
                fighter.TrackViews.Clear();
            }
            side.Team.Clear();
        }
        Array.Clear(view.Sides, 0, view.Sides.Length);
        foreach (var skill in view.Skills)
        {
            skill.PriorityParts.Clear();
            foreach (var effect in skill.Effects) effect.Text = "已修改";
            skill.Effects.Clear();
        }
        view.Skills.Clear();
        view.SwitchTargets.Clear();
        view.NodeStrip.Clear();
    }

    // 只解释公开补丁协议；字段命名、值序列化、树比较均交给标准 JSON 库。
    static JsonNode Apply(JsonNode document, IEnumerable<ViewChange> delta)
    {
        foreach (var change in delta)
        {
            Assert.Contains(change.Kind, new[] { "set", "add", "remove" });
            JsonNode value = change.New == null ? null : JsonNode.Parse(change.New);
            if (change.Path.Length == 0)
            {
                Assert.Equal("set", change.Kind);
                Assert.Null(document);
                Assert.Null(change.Old);
                Assert.NotNull(change.New);
                document = value;
                continue;
            }

            var path = change.Path.Split('/').Select(part => part.Replace("~1", "/").Replace("~0", "~")).ToArray();
            JsonNode parent = document;
            foreach (string part in path.Take(path.Length - 1))
                parent = parent is JsonArray array ? array[int.Parse(part, CultureInfo.InvariantCulture)] : parent[part];
            string key = path[path.Length - 1];
            JsonNode old;
            bool exists;
            int index = -1;
            if (parent is JsonArray items)
            {
                index = int.Parse(key, CultureInfo.InvariantCulture);
                Assert.InRange(index, 0, items.Count);
                exists = index < items.Count;
                old = exists ? items[index] : null;
            }
            else exists = Assert.IsType<JsonObject>(parent).TryGetPropertyValue(key, out old);
            Assert.Equal(exists, change.Old != null);
            if (exists) Assert.True(JsonNode.DeepEquals(old, JsonNode.Parse(change.Old)), "旧值不匹配：" + change.Path);

            if (change.Kind == "remove")
            {
                Assert.True(exists);
                Assert.Null(change.New);
                if (parent is JsonArray list) list.RemoveAt(index);
                else Assert.True(((JsonObject)parent).Remove(key));
            }
            else
            {
                Assert.NotNull(change.New);
                if (change.Kind == "add") Assert.False(exists);
                if (parent is JsonArray list)
                {
                    if (index == list.Count) list.Add(value);
                    else list[index] = value;
                }
                else ((JsonObject)parent)[key] = value;
            }
        }
        return document;
    }

    static Battle Make(bool openingChoices)
    {
        // 固定合法夹具让各阶段由公开行动到达，不依赖正式包平衡或 AI 选招的后续变化。
        var db = new ContentDb { PackId = "t84_review", PackName = "差量阶段验收" };
        db.CatNames["status"] = "状态";
        db.CatNames["interact"] = "互动";
        db.Types["talk"] = new TypeDef { Id = "talk", Name = "聊天", Color = "#808080" };
        db.Chart["talk"] = new Dictionary<string, double> { { "talk", 1 } };
        db.Personas["neutral"] = new PersonaDef { Id = "neutral", Name = "测试人设", Code = "T_LOWHP_POWER" };
        db.Statuses["weak"] = new StatusDef { Id = "weak", Name = "测试弱化", Cat = "weak", Turns = 3, Cleanse = true };
        db.Fields["room"] = new FieldDef { Id = "room", Name = "测试直播间", Kind = "turn", Dispel = true };
        db.Skills["prepare"] = new SkillDef
        {
            Id = "prepare", Name = "测试准备", Type = "talk", Cat = "status", Pp = 20,
            Effects = new List<EffectDef>
            {
                new EffectDef { At = "post", Code = "STAGE", Target = "self", Changes = new Dictionary<string, int> { { "atk", 1 } } },
                new EffectDef { At = "post", Code = "STATUS", Target = "self", Status = "weak" },
                new EffectDef { At = "post", Code = "FIELD", Target = "self", Field = "room", Turns = 3 }
            }
        };
        db.Skills["finish"] = new SkillDef
        {
            Id = "finish", Name = "测试下播", Type = "talk", Cat = "status", Pp = 20,
            Effects = new List<EffectDef> { new EffectDef { At = "post", Code = "FIXED_DAMAGE", Target = "foe", N = 99999 } }
        };
        foreach (string id in new[] { "wait_a", "wait_b", "wait_fifth" })
            db.Skills[id] = new SkillDef { Id = id, Name = "测试等待", Type = "talk", Cat = "status", Pp = 20 };
        db.Struggle = new SkillDef { Id = "struggle", Name = "测试硬撑", Type = "talk", Cat = "interact", Power = 20, Pp = 1 };
        for (int index = 0; index < 12; index++)
            db.Streamers.Add(new StreamerDef
            {
                Id = "u" + index, Name = "测试主播" + index, Type = "talk", Persona = "neutral",
                Stats = new Dictionary<string, int>
                {
                    { "hp", 300 }, { "atk", 100 }, { "def", 100 }, { "spa", 100 }, { "sdef", 100 }, { "spd", 200 - index }
                },
                Skills = new List<string> { "prepare", "finish", "wait_a", "wait_b" }, Fifth = "wait_fifth"
            });
        db.Validate();
        return new Battle(db, new IList<string>[]
        {
            db.Streamers.Take(6).Select(streamer => streamer.Id).ToArray(),
            db.Streamers.Skip(6).Take(6).Select(streamer => streamer.Id).ToArray()
        }, new[] { "甲方", "乙方" }, new BattleOptions { Seed = 84, BanOne = openingChoices, LeadPick = openingChoices });
    }
}
