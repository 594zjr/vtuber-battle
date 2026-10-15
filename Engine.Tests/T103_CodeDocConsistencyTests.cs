using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using StreamDuel.Core;
using Xunit;

public class T103_CodeDocConsistencyTests
{
    static string Root => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    static string Core => Path.Combine(Root, "UnityProject", "Assets", "StreamDuel", "Core");
    static string Resources => Path.Combine(Root, "UnityProject", "Assets", "StreamDuel", "Resources");

    public static IEnumerable<object[]> Effects => EffectCodes.All.Select(code => new object[] { code.Code });
    public static IEnumerable<object[]> Personas => PersonaCodes.All.Select(code => new object[] { code.Code });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DesignTableExactlyMatchesRegistry(bool persona)
    {
        var registry = persona ? PersonaCodes.All : EffectCodes.All;
        string design = File.ReadAllText(Path.Combine(Root, "docs", "design.md"));
        var section = Regex.Match(design, persona ? @"(?ms)^### 6\.2\b.*?(?=^### 6\.3\b)" :
            @"(?ms)^### 6\.1\b.*?(?=^### 6\.2\b)");
        Assert.True(section.Success, "未找到设计文档中的代码表章节");
        Assert.Contains("| code | 判定时机 | 参数 | 驱散规则 | 说明来源 | 作用 |", section.Value);
        var rows = Regex.Matches(section.Value, @"(?m)^\|\s*`([A-Z][A-Z0-9_]*)`\s*\|.*$")
            .Cast<Match>().Select(match => match.Value.Split('|').Skip(1).Take(6)
                .Select(cell => cell.Trim().Trim('`')).ToArray()).ToArray();
        AssertSet(registry.Select(code => code.Code), rows.Select(row => row[0]));
        Assert.Equal(rows.Length, rows.Select(row => row[0]).Distinct().Count());
        foreach (var info in registry)
        {
            var row = Assert.Single(rows, value => value[0] == info.Code);
            Assert.Equal(info.Timing, row[1]);
            AssertSet(info.ParameterNames, row[2] == "—" ? Array.Empty<string>() : row[2].Split(',').Select(p => p.Trim()));
            Assert.Equal(info.DispelRule, row[3]);
            Assert.Equal(info.DescriptionSource, row[4]);
            Assert.False(string.IsNullOrWhiteSpace(row[5]), info.Code);
        }
    }

    [Fact]
    public void RegistryCodesMatchActualBattleHandlers()
    {
        // 从执行代码独立取集合，避免文档和注册表一起漏写；Validate 本身没有代码白名单。
        string battle = string.Join("\n", Directory.GetFiles(Core, "Battle*.cs").Select(File.ReadAllText));
        string persona = string.Join("\n", PersonaImplementationFiles().Select(File.ReadAllText));
        AssertSet(EffectCodes.All.Select(code => code.Code), Captures(battle, @"case\s+""([A-Z][A-Z0-9_]*)""\s*:"));
        AssertSet(PersonaCodes.All.Select(code => code.Code), Captures(persona, @"""(T_[A-Z0-9_]+)"""));
    }

    [Fact]
    public void EffectParametersMatchFieldsReadByEachHandler()
    {
        string source = File.ReadAllText(Path.Combine(Core, "Battle.cs"));
        string runEffect = source.Substring(source.IndexOf("void RunEffect(", StringComparison.Ordinal));
        runEffect = runEffect.Substring(0, runEffect.IndexOf("default:", StringComparison.Ordinal));
        var cases = Cases(runEffect);
        foreach (var info in EffectCodes.All)
        {
            string branch = cases[info.Code];
            var parameters = Captures(branch, @"\be\.([A-Z][A-Za-z0-9]*)\b")
                .Select(name => char.ToLowerInvariant(name[0]) + name.Substring(1)).ToList();
            if (Regex.IsMatch(branch, @"\btgt\b")) parameters.Add("target");
            AssertSet(info.ParameterNames, parameters);
        }
    }

    [Fact]
    public void PersonaParametersMatchFieldsReadByEachHook()
    {
        var parametersByCode = new Dictionary<string, IEnumerable<string>>(StringComparer.Ordinal);
        foreach (var file in PersonaImplementationFiles())
        {
            string source = File.ReadAllText(file);
            var methods = Regex.Matches(source, @"(?m)^        (?:bool|int|void) Persona\w+\(").Cast<Match>().ToArray();
            for (int i = 0; i < methods.Length; i++)
            {
                string method = source.Substring(methods[i].Index,
                    (i + 1 < methods.Length ? methods[i + 1].Index : source.Length) - methods[i].Index);
                var branches = Cases(method);
                if (branches.Count == 0)
                {
                    string code = Assert.Single(Captures(method, @"""(T_[A-Z0-9_]+)"""));
                    branches.Add(code, method);
                }
                foreach (var branch in branches)
                {
                    var parameters = Captures(branch.Value, @"\b(?:t|persona)\.P\(""([a-z]+)""\)").ToList();
                    parameters.AddRange(Captures(branch.Value, @"\b(?:t|persona)\.(Status|Field)\b").Select(name => name.ToLowerInvariant()));
                    if (branch.Value.Contains("persona.P(stat)"))
                    {
                        Assert.Contains("ContentDb.StatKeys", branch.Value);
                        parameters.AddRange(ContentDb.StatKeys);
                    }
                    parametersByCode.Add(branch.Key, parameters);
                }
            }
        }
        AssertSet(PersonaCodes.All.Select(code => code.Code), parametersByCode.Keys);
        foreach (var info in PersonaCodes.All) AssertSet(info.ParameterNames, parametersByCode[info.Code]);
    }

    [Theory]
    [MemberData(nameof(Effects))]
    public void EveryEffectHasGeneratedChineseDescriptionAtEveryAcceptedTiming(string code)
    {
        var db = BattleTests.LoadPack("original");
        var info = Assert.Single(EffectCodes.All, item => item.Code == code);
        AssertSet(new[] { "pre", "post" }, info.Timing.Split('/'));
        foreach (string at in info.Timing.Split('/'))
        {
            var effect = new EffectDef
            {
                Code = code, At = at, Target = "self", Status = db.Statuses.Keys.First(),
                Field = db.Fields.Keys.First(), Changes = { { "atk", 1 } },
                Frac = 0.25, Mul = 2, Per = 0.5, N = 2, Turns = 3, Consume = true
            };
            db.Skills["t103_description"] = new SkillDef
            {
                Id = "t103_description", Name = "说明校验", Type = db.Types.Keys.First(), Cat = "status", Pp = 5,
                Effects = { effect }
            };
            db.Validate();
            var description = Describe.Effect(db, effect);
            AssertChinese(description.Text);
            Assert.NotEqual(code, description.Text);
            Assert.Equal(Loc.Current.T("describe.at." + at), description.At);
        }
    }

    [Theory]
    [MemberData(nameof(Personas))]
    public void EveryPersonaHasTheRegisteredDescriptionSource(string code)
    {
        var db = BattleTests.LoadPack("original");
        var info = Assert.Single(PersonaCodes.All, item => item.Code == code);
        var persona = new PersonaDef
        {
            Id = "t103_persona", Code = code, Name = "说明校验人设", Node = "内容包自定义时机", Desc = "内容包自定义说明",
            Status = db.Statuses.Keys.First(), Field = db.Fields.Keys.First()
        };
        foreach (var parameter in info.ParameterNames) persona.Params[parameter] = parameter == "frac" ? 0.25 : 2;
        var description = Describe.Persona(db, persona);
        AssertChinese(description.At);
        AssertChinese(description.Text);
        AssertChinese(description.Dispel);
        if (info.DescriptionSource.Contains("PersonaDef.Node/Desc"))
        {
            Assert.Equal(persona.Node, description.At);
            Assert.Equal(persona.Desc, description.Text);
        }
        else
        {
            Assert.NotEqual(persona.Node, description.At);
            Assert.NotEqual(persona.Desc, description.Text);
        }
    }

    [Theory]
    [InlineData("pre", false)]
    [InlineData("pre", true)]
    [InlineData("post", false)]
    [InlineData("post", true)]
    public void MarkDescriptionUsesReferencedFieldDispelFlagWithoutChangingContent(string at, bool dispellable)
    {
        var db = BattleTests.LoadPack("original");
        db.Fields["t103_mark"] = new FieldDef
        {
            Id = "t103_mark", Name = "相同印记名称", Kind = "mark", Dispel = dispellable, MaxStacks = 5
        };
        string hash = db.ContentHash();
        var effect = new EffectDef { Code = "ADD_MARK", At = at, Target = "self", Field = "t103_mark", N = 2 };
        var description = Describe.Effect(db, effect);

        Assert.Equal(Loc.Current.T(dispellable ? "describe.dispel.turns" : "describe.dispel.mark"), description.Dispel);
        Assert.Contains("相同印记名称", description.Text);
        Assert.Equal(Loc.Current.T("describe.at." + at), description.At);
        Assert.Equal(hash, db.ContentHash());
        db.Fields["t103_mark"].Dispel = !dispellable;
        var changed = Describe.Effect(db, effect);
        Assert.Equal(description.Text, changed.Text);
        Assert.NotEqual(description.Dispel, changed.Dispel);
    }

    [Fact]
    public void SpeedPersonaUsesGeneratedDescriptionForNonDefaultStageCap()
    {
        var db = BattleTests.LoadPack("original");
        var team = db.Streamers.Take(6).Select(streamer => streamer.Id).ToArray();
        var battle = new Battle(db, new IList<string>[] { team, team }, new[] { "甲", "乙" },
            new BattleOptions { Seed = 7, Modifiers = new List<string> { "stage_cap_3" } });
        var persona = new PersonaDef { Code = "T_SPEED_IF_FIRST", Node = "旧时机", Desc = "旧说明", Params = { { "n", 1 } } };
        var text = Describe.Persona(db, persona, battle);
        Assert.Equal("回合结束", text.At);
        Assert.Contains("+3", text.Text);
        Assert.NotEqual(persona.Desc, text.Text);
        Assert.Contains("describe.persona.speed.first", PersonaCodes.All.Single(code => code.Code == persona.Code).DescriptionSource);
    }

    [Fact]
    public void RegisteredLocalizationSourcesExistAndAreUsedByDescribe()
    {
        var locale = Loc.FromJson(File.ReadAllText(Path.Combine(Resources, "Locale", "zh-CN.json")));
        string source = File.ReadAllText(Path.Combine(Core, "Describe.cs"));
        var branches = Cases(source);
        foreach (var info in EffectCodes.All.Concat(PersonaCodes.All))
        {
            foreach (string key in Regex.Matches(info.DescriptionSource, @"describe\.[a-z.]+").Cast<Match>().Select(match => match.Value))
            {
                Assert.True(locale.Has(key), info.Code + ": " + key);
                Assert.True(branches.ContainsKey(info.Code), "Describe 缺少代码分支：" + info.Code);
                Assert.Contains("Loc.T(\"" + key + "\"", branches[info.Code]);
            }
        }
    }

    [Fact]
    public void AllBuiltInAndExampleDefinitionsUseRegisteredCodes()
    {
        string rules = File.ReadAllText(Path.Combine(Resources, "StreamDuelRules.json"));
        var files = Directory.GetFiles(Path.Combine(Resources, "Packs"), "*.json")
            .Concat(Directory.GetFiles(Path.Combine(Root, "docs", "examples"), "*.json")).ToArray();
        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            var db = ContentDb.FromJson(rules, File.ReadAllText(file));
            foreach (var skill in db.Skills.Values.Concat(new[] { db.Struggle }))
                foreach (var effect in skill.Effects)
                    Assert.True(EffectCodes.All.Any(code => code.Code == effect.Code), file + " / 技能 " + skill.Id + " / " + effect.Code);
            foreach (var persona in db.Personas.Values)
                Assert.True(PersonaCodes.All.Any(code => code.Code == persona.Code), file + " / 人设 " + persona.Id + " / " + persona.Code);
        }
    }

    [Fact]
    public void RegistryMetadataCannotBeMutatedByCallers()
    {
        foreach (var registry in new[] { EffectCodes.All, PersonaCodes.All })
        {
            Assert.Equal(registry.Count, registry.Select(code => code.Code).Distinct().Count());
            Assert.Throws<NotSupportedException>(() => ((IList<CodeInfo>)registry)[0] = registry[0]);
            foreach (var info in registry)
            {
                Assert.False(string.IsNullOrWhiteSpace(info.Timing));
                AssertChinese(info.DispelRule);
                Assert.False(string.IsNullOrWhiteSpace(info.DescriptionSource));
                Assert.Throws<NotSupportedException>(() => ((IList<string>)info.ParameterNames).Add("changed"));
            }
        }
    }

    static IEnumerable<string> PersonaImplementationFiles()
    {
        return Directory.GetFiles(Core, "Persona*.cs").Where(file => Path.GetFileName(file) != "PersonaCodes.cs");
    }

    static Dictionary<string, string> Cases(string source)
    {
        var labels = Regex.Matches(source, @"case\s+""([A-Z][A-Z0-9_]*)""\s*:").Cast<Match>().ToArray();
        return labels.Select((match, index) => new
        {
            Code = match.Groups[1].Value,
            Body = source.Substring(match.Index, (index + 1 < labels.Length ? labels[index + 1].Index : source.Length) - match.Index)
        }).ToDictionary(item => item.Code, item => item.Body, StringComparer.Ordinal);
    }

    static IEnumerable<string> Captures(string source, string pattern)
    {
        return Regex.Matches(source, pattern).Cast<Match>().Select(match => match.Groups[1].Value).Distinct(StringComparer.Ordinal);
    }

    static void AssertSet(IEnumerable<string> expected, IEnumerable<string> actual)
    {
        Assert.Equal(expected.Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal),
            actual.Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal));
    }

    static void AssertChinese(string text)
    {
        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.Matches(@"[\u4e00-\u9fff]", text);
        Assert.DoesNotContain("[describe.", text);
    }
}
