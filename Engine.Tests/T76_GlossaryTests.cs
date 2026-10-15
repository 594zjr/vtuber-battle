using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using StreamDuel.Core;
using Xunit;

[Collection("Localization current")]
public class T76_GlossaryTests : IDisposable
{
    readonly Loc previousLocale;

    public T76_GlossaryTests()
    {
        previousLocale = Loc.Current;
        Loc.Current = null;
    }

    public void Dispose() { Loc.Current = previousLocale; }

    static GlossaryEntry Entry(List<GlossaryEntry> entries, string category, string id)
    {
        return Assert.Single(entries, entry => entry.Category == category && entry.Id == id);
    }

    static string Snapshot(List<GlossaryEntry> entries)
    {
        return JsonSerializer.Serialize(entries, new JsonSerializerOptions { IncludeFields = true });
    }

    static void AssertCoverage(List<GlossaryEntry> entries, string category, IEnumerable<string> ids)
    {
        Assert.Equal(ids.OrderBy(id => id, StringComparer.Ordinal),
            entries.Where(entry => entry.Category == category).Select(entry => entry.Id)
                .OrderBy(id => id, StringComparer.Ordinal));
    }

    static void AssertFraction(string text, params string[] equivalentForms)
    {
        Assert.True(equivalentForms.Any(text.Contains), "缺少实际比例：" + string.Join(" 或 ", equivalentForms) + "；实际：" + text);
    }

    [Theory]
    [InlineData("original")]
    [InlineData("original_wave2")]
    [InlineData("fan_holo_niji")]
    [InlineData("fan_asoul_vr")]
    public void EveryPackDefinitionHasExactlyOneReadableEntryAndEveryStatusNameIsSearchable(string pack)
    {
        var db = BattleTests.LoadPack(pack);
        string hash = db.ContentHash();
        var entries = Glossary.Build(db);

        AssertCoverage(entries, "track", db.Types.Keys);
        AssertCoverage(entries, "status", db.Statuses.Keys);
        AssertCoverage(entries, "field", db.Fields.Keys);
        AssertCoverage(entries, "persona", db.Personas.Keys);
        Assert.Equal(entries.Count, entries.Select(entry => (entry.Category, entry.Id)).Distinct().Count());
        Assert.All(entries, entry =>
        {
            Assert.Contains(entry.Category, new[] { "track", "status", "field", "persona", "rule" });
            Assert.False(string.IsNullOrWhiteSpace(entry.Id));
            Assert.False(string.IsNullOrWhiteSpace(entry.Title));
            Assert.False(string.IsNullOrWhiteSpace(entry.Text), entry.Category + "/" + entry.Id);
            Assert.DoesNotContain("_", entry.Text);
            Assert.DoesNotMatch("[A-Z]{3,}", entry.Text);
            Assert.DoesNotContain("[glossary.", entry.Title + entry.Text);
            Assert.NotNull(entry.Tags);
            Assert.All(entry.Tags, tag =>
            {
                Assert.False(string.IsNullOrWhiteSpace(tag));
                Assert.DoesNotContain("[glossary.", tag);
            });
        });
        foreach (var definition in db.Types.Values) Assert.Equal(definition.Name, Entry(entries, "track", definition.Id).Title);
        foreach (var definition in db.Fields.Values)
        {
            var entry = Entry(entries, "field", definition.Id);
            Assert.Equal(definition.Name, entry.Title);
            Assert.Contains(Describe.FieldKind(definition), entry.Text);
            Assert.Contains(definition.When, entry.Text);
            Assert.Contains(Loc.Current.T(definition.Dispel ? "describe.dispel.turns" : "describe.dispel.no.turns"), entry.Text);
        }
        foreach (var definition in db.Personas.Values) Assert.Equal(definition.Name, Entry(entries, "persona", definition.Id).Title);
        foreach (var definition in db.Statuses.Values)
        {
            var entry = Entry(entries, "status", definition.Id);
            Assert.Equal(definition.Name, entry.Title);
            Assert.Contains(Describe.StatusKind(definition), entry.Text);
            Assert.Contains(Describe.StatusDuration(definition, definition.IsControl ? definition.Skip : definition.Turns), entry.Text);
            Assert.Contains(Loc.Current.T(definition.Cleanse ? "describe.dispel.cleanse" : "describe.dispel.no.cleanse"), entry.Text);
            Assert.Contains(Glossary.Search(entries, definition.Name), entry => entry.Category == "status" && entry.Id == definition.Id);
        }
        Assert.Equal(hash, db.ContentHash());
    }

    [Fact]
    public void RuleEntriesExplainRequiredOrderLimitsAndSwitchPersistence()
    {
        var rules = Glossary.Build(BattleTests.LoadPack("original")).Where(entry => entry.Category == "rule").ToList();
        Assert.NotEmpty(rules);
        string[] nodes = { "换人", "回合开始", "先手行动", "后手行动", "回合结束", "补位" };
        Assert.Contains(rules, entry =>
        {
            int previous = -1;
            foreach (string node in nodes)
            {
                int found = entry.Text.IndexOf(node, previous + 1, StringComparison.Ordinal);
                if (found < 0) return false;
                previous = found;
            }
            return true;
        });
        Assert.Contains(rules, entry => entry.Text.Contains("先制") && entry.Text.Contains("反应") && entry.Text.Contains("随机"));
        Assert.Contains(rules, entry => entry.Text.Contains("PP") && entry.Text.Contains("0") && entry.Text.Contains("硬撑"));
        Assert.Contains(rules, entry => entry.Text.Contains("能力等级") &&
            (entry.Text.Contains("-6") || entry.Text.Contains("−6")) && entry.Text.Contains("+6"));
        Assert.Contains(rules, entry => entry.Text.Contains("状态") && entry.Text.Contains("保留") &&
            entry.Text.Contains("能力等级") && entry.Text.Contains("直播间效果") && entry.Text.Contains("清"));
    }

    [Fact]
    public void SearchUsesOnlyTitleAndBodyIgnoresCaseAndKeepsInputOrder()
    {
        var all = new List<GlossaryEntry>
        {
            new GlossaryEntry { Category = "rule", Id = "first", Title = "INPUT 入口", Text = "标题命中", Tags = new List<string>() },
            new GlossaryEntry { Category = "rule", Id = "second", Title = "正文入口", Text = "a mixed Input sentence", Tags = new List<string>() },
            new GlossaryEntry { Category = "input", Id = "input", Title = "未命中", Text = "无关正文", Tags = new List<string> { "input" } }
        };
        string before = Snapshot(all);
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            // 搜索不随宿主语言的特殊大小写规则改变。
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            var found = Glossary.Search(all, "  input  ");
            Assert.Equal(new[] { "first", "second" }, found.Select(entry => entry.Id));
            Assert.Empty(Glossary.Search(all, "完全不存在的关键词"));
            Assert.Empty(Glossary.Search(new List<GlossaryEntry>(), "input"));
        }
        finally { CultureInfo.CurrentCulture = previousCulture; }
        Assert.Equal(before, Snapshot(all));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n")]
    public void EmptySearchReturnsAllInAnIndependentList(string keyword)
    {
        var all = Glossary.Build(BattleTests.LoadPack("original"));
        var found = Glossary.Search(all, keyword);
        Assert.NotSame(all, found);
        Assert.Equal(all.Select(entry => (entry.Category, entry.Id)), found.Select(entry => (entry.Category, entry.Id)));
        int count = all.Count;
        found.Clear();
        Assert.Equal(count, all.Count);
    }

    [Fact]
    public void NullInputsAreRejectedWithoutAHiddenDefaultContentPack()
    {
        Assert.Throws<ArgumentNullException>(() => Glossary.Build(null));
        Assert.Throws<ArgumentNullException>(() => Glossary.Search(null, "状态"));
    }

    [Fact]
    public void RebuildingIsStableAndEntryMutationsDoNotReachDefinitionsOrOtherSnapshots()
    {
        var db = BattleTests.LoadPack("original_wave2");
        string hash = db.ContentHash();
        var first = Glossary.Build(db);
        var second = Glossary.Build(db);
        string original = Snapshot(first);
        Assert.Equal(original, Snapshot(second));
        Assert.Equal(hash, db.ContentHash());
        for (int index = 0; index < first.Count; index++)
        {
            Assert.NotSame(first[index], second[index]);
            Assert.NotSame(first[index].Tags, second[index].Tags);
        }

        var saved = Entry(first, "status", "stall");
        string oldName = db.Statuses["stall"].Name;
        db.Statuses["stall"].Name = "新显示名";
        Assert.Equal(oldName, saved.Title);
        Assert.Equal(original, Snapshot(first));
        hash = db.ContentHash();
        saved.Title = "只改快照";
        saved.Text = "只改说明";
        saved.Tags.Add("只改标签");
        first.Clear();
        Assert.Equal("新显示名", db.Statuses["stall"].Name);
        Assert.Equal(original, Snapshot(second));
        Assert.Equal(hash, db.ContentHash());
        Assert.Equal("新显示名", Entry(Glossary.Build(db), "status", "stall").Title);
    }

    [Fact]
    public void DefinitionInsertionOrderDoesNotChangeTheGlossary()
    {
        var db = BattleTests.LoadPack("original_wave2");
        string before = Snapshot(Glossary.Build(db));
        db.Types = db.Types.Reverse().ToDictionary(pair => pair.Key, pair => pair.Value);
        db.Statuses = db.Statuses.Reverse().ToDictionary(pair => pair.Key, pair => pair.Value);
        db.Fields = db.Fields.Reverse().ToDictionary(pair => pair.Key, pair => pair.Value);
        db.Personas = db.Personas.Reverse().ToDictionary(pair => pair.Key, pair => pair.Value);
        Assert.Equal(before, Snapshot(Glossary.Build(db)));
    }

    [Fact]
    public void LocaleChangesApplyToNewBuildsWithoutChangingIdsContentOrEarlierSnapshots()
    {
        var db = BattleTests.LoadPack("original");
        string hash = db.ContentHash();
        var original = Glossary.Build(db);
        string originalJson = Snapshot(original);
        var chinese = Loc.Current;
        var texts = new Dictionary<string, string>
        {
            { "glossary.rule.order.title", "translated order title" },
            { "glossary.rule.order.text", "translated order body" },
            { "glossary.status.text", "translated status {0}; {1}; {2}; {3}; {4}" }
        };
        try
        {
            Loc.Current = Loc.FromJson(JsonSerializer.Serialize(new { language = "test", texts }), chinese);
            var translated = Glossary.Build(db);
            Assert.Contains(translated, entry => entry.Category == "rule" && entry.Title == "translated order title" && entry.Text == "translated order body");
            Assert.All(translated.Where(entry => entry.Category == "status"), entry => Assert.StartsWith("translated status ", entry.Text));
            Assert.Equal(original.Select(entry => (entry.Category, entry.Id)), translated.Select(entry => (entry.Category, entry.Id)));
            Assert.Equal(originalJson, Snapshot(original));
            Assert.Equal(hash, db.ContentHash());
        }
        finally { Loc.Current = chinese; }
        Assert.Equal(originalJson, Snapshot(Glossary.Build(db)));
    }

    [Fact]
    public void TrackTextUsesCurrentNamesAndBothDirectionsOfTheActualChart()
    {
        var db = BattleTests.LoadPack("original");
        db.Types["song"].Name = "试验主赛道";
        db.Types["game"].Name = "试验对面甲";
        db.Types["tech"].Name = "试验对面乙";
        db.Chart.Clear();
        db.Chart["song"] = new Dictionary<string, double> { { "game", 2.75 }, { "tech", 0.35 } };
        db.Chart["game"] = new Dictionary<string, double> { { "song", 1.6 } };
        db.Chart["tech"] = new Dictionary<string, double> { { "song", 0.2 } };
        string hash = db.ContentHash();
        var entry = Entry(Glossary.Build(db), "track", "song");

        Assert.Equal("试验主赛道", entry.Title);
        Assert.Contains("试验对面甲", entry.Text);
        Assert.Contains("试验对面乙", entry.Text);
        foreach (string multiplier in new[] { "2.75", "0.35", "1.6", "0.2" }) Assert.Contains(multiplier, entry.Text);
        Assert.DoesNotContain("game", entry.Text);
        Assert.DoesNotContain("tech", entry.Text);
        Assert.Equal(hash, db.ContentHash());
    }

    [Fact]
    public void StatusAndFieldMechanicsComeFromCurrentDataInsteadOfStaleDescriptions()
    {
        var db = BattleTests.LoadPack("original");
        var control = db.Statuses["stall"];
        control.Name = "临时控制";
        control.Skip = 7;
        control.Desc = "过时文案不能作为数值来源";
        var weak = db.Statuses["flame"];
        weak.Name = "临时弱化";
        weak.Turns = 9;
        weak.Dot = 0.19;
        weak.InteractMul = 0.43;
        weak.Cleanse = false;
        weak.Desc = control.Desc;
        var field = db.Fields["shield"];
        field.Name = "临时屏障";
        field.When = "测试节点";
        field.DmgTakenMul = 0.41;
        field.Dispel = false;
        field.Desc = control.Desc;
        string hash = db.ContentHash();
        var all = Glossary.Build(db);

        string controlText = Entry(all, "status", "stall").Text;
        Assert.Contains("控制", controlText);
        Assert.Contains("7", controlText);
        Assert.Contains("行动", controlText);
        string weakText = Entry(all, "status", "flame").Text;
        Assert.Contains("9", weakText);
        Assert.Contains("回合", weakText);
        AssertFraction(weakText, "19%", "0.19", "19/100");
        Assert.Contains("0.43", weakText);
        Assert.Contains("不可净化", weakText);
        var fieldEntry = Entry(all, "field", "shield");
        Assert.Equal("临时屏障", fieldEntry.Title);
        Assert.Contains("测试节点", fieldEntry.Text);
        Assert.Contains("0.41", fieldEntry.Text);
        Assert.Contains("消回合清不掉", fieldEntry.Text);
        Assert.DoesNotContain(control.Desc, controlText + weakText + fieldEntry.Text);
        Assert.Equal(hash, db.ContentHash());
    }

    [Fact]
    public void PersonasUseCurrentParametersAndRenamedReferencedDefinitions()
    {
        var db = BattleTests.LoadPack("original_wave2");
        db.Statuses["flame"].Name = "铃音标记";
        db.Fields["filter"].Name = "折叠护罩";
        db.Personas["trollback"].Params["chance"] = 17;
        db.Personas["beauty"].Params["turns"] = 9;
        db.Personas["healing"].Params["frac"] = 0.37;
        db.Personas["comeback"].Params["below"] = 0.27;
        db.Personas["comeback"].Params["mul"] = 1.73;
        db.Personas["og2_split_finish"].Params["frac"] = 0.42;
        foreach (string id in new[] { "trollback", "beauty", "healing", "comeback", "og2_split_finish" })
            db.Personas[id].Desc = "陈旧人设说明";
        string hash = db.ContentHash();
        var all = Glossary.Build(db);

        string contact = Entry(all, "persona", "trollback").Text;
        Assert.Contains("铃音标记", contact);
        Assert.Contains("17", contact);
        Assert.DoesNotContain("炎上", contact);
        string enter = Entry(all, "persona", "beauty").Text;
        Assert.Contains("折叠护罩", enter);
        Assert.Contains("9", enter);
        Assert.DoesNotContain("美颜滤镜", enter);
        AssertFraction(Entry(all, "persona", "healing").Text, "37%", "0.37", "37/100");
        string low = Entry(all, "persona", "comeback").Text;
        AssertFraction(low, "27%", "0.27", "27/100");
        Assert.Contains("1.73", low);
        var ko = Entry(all, "persona", "og2_split_finish");
        Assert.Contains(Describe.Persona(db, db.Personas["og2_split_finish"]).Text, ko.Text);
        AssertFraction(ko.Text, "42%", "0.42", "42/100");
        foreach (string id in new[] { "trollback", "beauty", "healing", "comeback", "og2_split_finish" })
            Assert.DoesNotContain("陈旧人设说明", Entry(all, "persona", id).Text);
        Assert.Equal(hash, db.ContentHash());
    }
}
