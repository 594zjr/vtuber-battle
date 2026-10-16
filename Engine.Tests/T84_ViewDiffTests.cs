using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using StreamDuel.Core;
using Xunit;

public class T84_ViewDiffTests
{
    static readonly JsonSerializerOptions Json = new JsonSerializerOptions
    {
        IncludeFields = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    static string Serialize(BattleViewModel view) { return JsonSerializer.Serialize(view, Json); }
    static BattleViewModel Copy(BattleViewModel view) { return JsonSerializer.Deserialize<BattleViewModel>(Serialize(view), Json); }
    static string Number(int value) { return value.ToString(CultureInfo.InvariantCulture); }

    // 应用器只依赖独立的标准 JSON 树，防止生产遍历和生产序列化的同一个遗漏互相掩盖。
    static JsonNode Apply(BattleViewModel before, IEnumerable<ViewChange> changes)
    {
        JsonNode root = before == null ? null : JsonNode.Parse(Serialize(before));
        bool rootExists = before != null;
        foreach (var change in changes)
        {
            Assert.NotNull(change);
            Assert.NotNull(change.Path);
            Assert.False(change.Path.StartsWith("/", StringComparison.Ordinal));
            Assert.Contains(change.Kind, new[] { "set", "add", "remove" });
            JsonNode oldValue = change.Old == null ? null : JsonNode.Parse(change.Old);
            JsonNode newValue = change.New == null ? null : JsonNode.Parse(change.New);
            if (change.Path.Length == 0)
            {
                Assert.Equal("set", change.Kind);
                AssertOld(change, rootExists, root, oldValue);
                Assert.NotNull(change.New);
                root = newValue;
                rootExists = true;
                continue;
            }

            Assert.True(rootExists, "子字段必须在根节点创建后设置。");
            string[] segments = change.Path.Split('/').Select(segment => segment.Replace("~1", "/").Replace("~0", "~")).ToArray();
            JsonNode parent = root;
            for (int i = 0; i < segments.Length - 1; i++)
            {
                if (parent is JsonObject obj)
                {
                    Assert.True(obj.TryGetPropertyValue(segments[i], out var child), "父对象路径不存在：" + change.Path);
                    parent = child;
                }
                else
                {
                    var array = Assert.IsType<JsonArray>(parent);
                    int index = Index(segments[i]);
                    Assert.InRange(index, 0, array.Count - 1);
                    parent = array[index];
                }
                Assert.NotNull(parent);
            }

            string last = segments[segments.Length - 1];
            if (parent is JsonObject properties)
            {
                bool exists = properties.TryGetPropertyValue(last, out var current);
                AssertOld(change, exists, current, oldValue);
                if (change.Kind == "remove")
                {
                    Assert.True(exists);
                    Assert.Null(change.New);
                    Assert.True(properties.Remove(last));
                }
                else
                {
                    if (change.Kind == "add") Assert.False(exists);
                    Assert.NotNull(change.New);
                    properties[last] = newValue;
                }
            }
            else
            {
                var array = Assert.IsType<JsonArray>(parent);
                int index = Index(last);
                Assert.InRange(index, 0, array.Count);
                bool exists = index < array.Count;
                AssertOld(change, exists, exists ? array[index] : null, oldValue);
                if (change.Kind == "remove")
                {
                    Assert.True(exists);
                    Assert.Null(change.New);
                    array.RemoveAt(index);
                }
                else
                {
                    Assert.NotNull(change.New);
                    if (change.Kind == "add") Assert.Equal(array.Count, index);
                    if (exists) array[index] = newValue;
                    else array.Add(newValue);
                }
            }
        }
        Assert.True(rootExists);
        return root;
    }

    static int Index(string segment)
    {
        Assert.True(int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out int index));
        Assert.Equal(Number(index), segment);
        return index;
    }

    static void AssertOld(ViewChange change, bool exists, JsonNode current, JsonNode oldValue)
    {
        if (change.Old == null) Assert.False(exists, "Old=null 表示该路径原先不存在：" + change.Path);
        else
        {
            Assert.True(exists, "Old 必须指向实际存在的值：" + change.Path);
            Assert.True(JsonNode.DeepEquals(current, oldValue), "Old 与补丁应用前的值不同：" + change.Path);
        }
    }

    static List<ViewChange> RoundTrip(BattleViewModel before, BattleViewModel after)
    {
        string beforeJson = Serialize(before), afterJson = Serialize(after);
        var changes = ViewDiff.Between(before, after);
        Assert.NotNull(changes);
        Assert.Equal(beforeJson, Serialize(before));
        Assert.Equal(afterJson, Serialize(after));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(afterJson), Apply(before, changes)), "独立应用差量后不能还原新视图。");
        return changes;
    }

    static FieldInfo[] Fields(Type type)
    {
        var fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance).OrderBy(field => field.Name, StringComparer.Ordinal).ToArray();
        if (fields.Length == 0) throw new InvalidOperationException("测试尚未支持字段类型：" + type.FullName);
        return fields;
    }

    static bool Scalar(Type type)
    {
        return type == typeof(string) || type == typeof(int) || type == typeof(bool) || type == typeof(double) || type.IsEnum;
    }

    static bool ListType(Type type) { return type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>); }
    static bool DictionaryType(Type type)
    {
        return type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>) && type.GetGenericArguments()[0] == typeof(string);
    }

    static object Seed(Type type, HashSet<Type> ancestors = null)
    {
        if (type == typeof(string)) return "初值\"\\\n😀";
        if (type == typeof(int)) return 3;
        if (type == typeof(bool)) return false;
        if (type == typeof(double)) return 1.25;
        if (type.IsEnum) return Enum.GetValues(type).GetValue(0);
        if (type.IsArray)
        {
            var array = Array.CreateInstance(type.GetElementType(), 1);
            array.SetValue(Seed(type.GetElementType(), ancestors), 0);
            return array;
        }
        if (ListType(type))
        {
            var list = (IList)Activator.CreateInstance(type);
            list.Add(Seed(type.GetGenericArguments()[0], ancestors));
            return list;
        }
        if (DictionaryType(type))
        {
            var dictionary = (IDictionary)Activator.CreateInstance(type);
            dictionary.Add("key", Seed(type.GetGenericArguments()[1], ancestors));
            return dictionary;
        }
        var stack = ancestors == null ? new HashSet<Type>() : new HashSet<Type>(ancestors);
        if (!stack.Add(type)) throw new InvalidOperationException("视图出现循环类型，测试需要显式支持：" + type.FullName);
        var value = Activator.CreateInstance(type);
        foreach (var field in Fields(type)) field.SetValue(value, Seed(field.FieldType, stack));
        return value;
    }

    static BattleViewModel RichView() { return (BattleViewModel)Seed(typeof(BattleViewModel)); }

    static IEnumerable<string> Routes(Type type, string prefix = "", HashSet<Type> ancestors = null)
    {
        var stack = ancestors == null ? new HashSet<Type>() : new HashSet<Type>(ancestors);
        if (!stack.Add(type)) throw new InvalidOperationException("视图出现循环类型：" + type.FullName);
        foreach (var field in Fields(type))
        {
            string path = prefix.Length == 0 ? field.Name : prefix + "/" + field.Name;
            yield return path;
            Type child = field.FieldType;
            while (child.IsArray || ListType(child) || DictionaryType(child))
            {
                if (child.IsArray) { path += "/0"; child = child.GetElementType(); }
                else if (ListType(child)) { path += "/0"; child = child.GetGenericArguments()[0]; }
                else { path += "/key"; child = child.GetGenericArguments()[1]; }
            }
            if (!Scalar(child))
                foreach (string descendant in Routes(child, path, stack)) yield return descendant;
        }
    }

    static (object Owner, FieldInfo Field) Locate(BattleViewModel view, string route)
    {
        string[] segments = route.Split('/');
        object owner = view;
        for (int i = 0; i < segments.Length - 1; i++)
        {
            if (owner is IDictionary dictionary) owner = dictionary[segments[i]];
            else if (owner is IList list) owner = list[int.Parse(segments[i], CultureInfo.InvariantCulture)];
            else owner = owner.GetType().GetField(segments[i]).GetValue(owner);
        }
        return (owner, owner.GetType().GetField(segments[segments.Length - 1]));
    }

    static object Different(Type type, object value)
    {
        if (type == typeof(string)) return (string)value + "变更";
        if (type == typeof(int)) return (int)value + 1;
        if (type == typeof(bool)) return !(bool)value;
        if (type == typeof(double)) return (double)value + 0.5;
        if (type.IsEnum)
        {
            var alternatives = Enum.GetValues(type).Cast<object>().Where(item => !item.Equals(value)).ToArray();
            Assert.NotEmpty(alternatives);
            return alternatives[0];
        }
        if (type.IsArray)
        {
            var old = (Array)value;
            var array = Array.CreateInstance(type.GetElementType(), old.Length + 1);
            Array.Copy(old, array, old.Length);
            array.SetValue(Seed(type.GetElementType()), old.Length);
            return array;
        }
        if (ListType(type)) { ((IList)value).Add(Seed(type.GetGenericArguments()[0])); return value; }
        if (DictionaryType(type)) { ((IDictionary)value).Add("added~/", Seed(type.GetGenericArguments()[1])); return value; }
        var field = Fields(type)[0];
        field.SetValue(value, Different(field.FieldType, field.GetValue(value)));
        return value;
    }

    public static IEnumerable<object[]> PublicFieldPaths()
    {
        foreach (string path in Routes(typeof(BattleViewModel))) yield return new object[] { path };
    }

    [Theory]
    [MemberData(nameof(PublicFieldPaths))]
    public void EveryPublicFieldAtEveryDepthProducesAnApplicableChange(string route)
    {
        var before = RichView();
        var after = Copy(before);
        var target = Locate(after, route);
        target.Field.SetValue(target.Owner, Different(target.Field.FieldType, target.Field.GetValue(target.Owner)));
        Assert.NotEqual(Serialize(before), Serialize(after));

        var changes = RoundTrip(before, after);

        string path = string.Join("/", route.Split('/').Select(JsonNamingPolicy.CamelCase.ConvertName));
        Assert.Contains(changes, change => change.Path == path || change.Path.StartsWith(path + "/", StringComparison.Ordinal));
    }

    [Fact]
    public void EqualViewsHaveNoChangesIncludingDistinctButEqualObjectGraphs()
    {
        var view = RichView();
        Assert.Empty(RoundTrip(view, view));
        Assert.Empty(RoundTrip(view, Copy(view)));
        Assert.Empty(RoundTrip(new BattleViewModel(), new BattleViewModel()));
    }

    [Fact]
    public void NullBeforeBuildsTheWholeViewUsingOnlySetsInParentBeforeChildOrder()
    {
        var after = RichView();
        after.Notice = null;

        var changes = RoundTrip(null, after);

        Assert.NotEmpty(changes);
        Assert.All(changes, change => { Assert.Equal("set", change.Kind); Assert.Null(change.Old); });
        Assert.Equal("", changes[0].Path);
        Assert.Equal("{}", changes[0].New);
        Assert.Equal("[]", Assert.Single(changes, change => change.Path == "sides").New);
        Assert.Equal("null", Assert.Single(changes, change => change.Path == "notice").New);
        foreach (string route in Routes(typeof(BattleViewModel)))
        {
            string path = string.Join("/", route.Split('/').Select(JsonNamingPolicy.CamelCase.ConvertName));
            Assert.Contains(changes, change => change.Path == path && change.Kind == "set");
        }
    }

    [Fact]
    public void NullAfterIsRejected()
    {
        Assert.Throws<ArgumentNullException>(() => ViewDiff.Between(new BattleViewModel(), null));
        Assert.Throws<ArgumentNullException>(() => ViewDiff.Between(null, null));
    }

    [Fact]
    public void ARealTurnReportsExactHpPpAndStatusValuesAndRestoresTheWholeView()
    {
        var db = BattleTests.LoadPack("original");
        var battle = new Battle(db, new IList<string>[]
        {
            new[] { "enka", "bell", "shiori", "lumi", "hane", "momo" },
            new[] { "kei", "yuan", "nyako", "buck", "kasumi", "bee" }
        }, new[] { "甲方", "乙方" }, 84);
        battle.Active(0).Statuses.Add(new StatusInst { Id = "flame", Left = 2 });
        var before = BattleViewModel.From(battle, 0);
        battle.ResolveTurn(BattleAction.Skill(1), BattleAction.Pass());
        var after = BattleViewModel.From(battle, 0);
        ulong hash = battle.StateHash();
        string record = battle.Record.ToJson();

        var changes = RoundTrip(before, after);

        Assert.True(after.Sides[0].Team[0].Hp < before.Sides[0].Team[0].Hp);
        Assert.True(after.Sides[1].Team[0].Hp < before.Sides[1].Team[0].Hp);
        foreach (int side in new[] { 0, 1 })
            AssertChange(changes, "sides/" + side + "/team/0/hp", "set", Number(before.Sides[side].Team[0].Hp), Number(after.Sides[side].Team[0].Hp));
        Assert.Equal(before.Skills[1].Pp - 1, after.Skills[1].Pp);
        AssertChange(changes, "skills/1/pp", "set", Number(before.Skills[1].Pp), Number(after.Skills[1].Pp));
        AssertChange(changes, "sides/0/team/0/statuses/0/turns", "set", "2", "1");
        Assert.Equal(hash, battle.StateHash());
        Assert.Equal(record, battle.Record.ToJson());
    }

    static void AssertChange(List<ViewChange> changes, string path, string kind, string oldValue, string newValue)
    {
        var change = Assert.Single(changes, item => item.Path == path);
        Assert.Equal(kind, change.Kind);
        Assert.Equal(oldValue, change.Old);
        Assert.Equal(newValue, change.New);
    }

    [Fact]
    public void DuplicateChipIdsAndMiddleInsertDeleteOrReorderKeepTheirListPositions()
    {
        var before = RichView();
        before.Sides[0].Team[0].Statuses = new List<ChipView>
        {
            new ChipView { Id = "shared", IsStatus = true, Turns = 3 },
            new ChipView { Id = "shared", IsStatus = false, Stacks = 2 }
        };
        var after = Copy(before);
        var chips = after.Sides[0].Team[0].Statuses;
        chips.Reverse();
        chips.Insert(1, new ChipView { Id = "inserted", IsStatus = true, Turns = 4 });

        var changes = RoundTrip(before, after);
        RoundTrip(after, before);

        AssertChange(changes, "sides/0/team/0/statuses/0/isStatus", "set", "true", "false");
        Assert.DoesNotContain(changes, change => change.Path.Contains("/shared", StringComparison.Ordinal));
    }

    [Fact]
    public void RemovedListTailsRunBackwardsAndAddedTailsRunForwards()
    {
        var before = new BattleViewModel { SwitchTargets = new List<int> { 0, 1, 2, 3 } };
        var after = new BattleViewModel { SwitchTargets = new List<int> { 9 } };

        var shrink = RoundTrip(before, after);
        var grow = RoundTrip(after, before);

        AssertChange(shrink, "switchTargets/0", "set", "0", "9");
        Assert.Equal(new[] { "switchTargets/3", "switchTargets/2", "switchTargets/1" }, shrink.Where(change => change.Kind == "remove").Select(change => change.Path));
        Assert.Equal(new[] { "switchTargets/1", "switchTargets/2", "switchTargets/3" }, grow.Where(change => change.Kind == "add").Select(change => change.Path));
    }

    [Fact]
    public void MissingArrayEntriesAreDifferentFromPresentJsonNull()
    {
        var before = new BattleViewModel { Sides = Array.Empty<SideView>() };
        var after = new BattleViewModel { Sides = new SideView[] { null } };

        var add = RoundTrip(before, after);
        var remove = RoundTrip(after, before);

        AssertChange(add, "sides/0", "add", null, "null");
        AssertChange(remove, "sides/0", "remove", "null", null);
    }

    public static IEnumerable<object[]> ContainerPaths()
    {
        var view = RichView();
        foreach (string route in Routes(typeof(BattleViewModel)))
        {
            Type type = Locate(view, route).Field.FieldType;
            if (type.IsArray || ListType(type) || DictionaryType(type)) yield return new object[] { route };
        }
    }

    [Theory]
    [MemberData(nameof(ContainerPaths))]
    public void NullAndEmptyContainersHaveDifferentJsonValues(string route)
    {
        var before = RichView();
        var target = Locate(before, route);
        target.Field.SetValue(target.Owner, null);
        var after = Copy(before);
        target = Locate(after, route);
        Type type = target.Field.FieldType;
        target.Field.SetValue(target.Owner, type.IsArray ? Array.CreateInstance(type.GetElementType(), 0) : Activator.CreateInstance(type));

        var changes = RoundTrip(before, after);
        RoundTrip(after, before);

        var change = Assert.Single(changes);
        Assert.Equal("set", change.Kind);
        Assert.Equal("null", change.Old);
        Assert.Equal(DictionaryType(type) ? "{}" : "[]", change.New);
    }

    [Fact]
    public void DictionaryKeysUseRfc6901EscapesAndOrdinalOrderIncludingTheEmptyKey()
    {
        var before = RichView();
        before.Sides[0].Team[0].Stages = new Dictionary<string, int>
        {
            ["~1"] = 4, ["a~b"] = 2, ["a/b"] = 1, [""] = 3
        };
        var after = Copy(before);
        var stages = after.Sides[0].Team[0].Stages;
        stages["a/b"] = 7;
        stages.Remove("a~b");
        stages["~1"] = 9;
        stages[""] = 4;
        stages["/~"] = 11;

        var changes = RoundTrip(before, after);

        const string path = "sides/0/team/0/stages/";
        Assert.Equal(new[] { path, path + "~1~0", path + "a~1b", path + "a~0b", path + "~01" }, changes.Select(change => change.Path));
        AssertChange(changes, path, "set", "3", "4");
        AssertChange(changes, path + "a~1b", "set", "1", "7");
        AssertChange(changes, path + "a~0b", "remove", "2", null);
        AssertChange(changes, path + "~1~0", "add", null, "11");
        AssertChange(changes, path + "~01", "set", "4", "9");
    }

    [Fact]
    public void CaseInsensitiveDictionaryComparersDoNotHideJsonKeyRenames()
    {
        var before = RichView();
        var after = Copy(before);
        before.Sides[0].Team[0].Stages = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["Atk"] = 1 };
        after.Sides[0].Team[0].Stages = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["atk"] = 1 };

        var changes = RoundTrip(before, after);

        Assert.Equal(2, changes.Count);
        AssertChange(changes, "sides/0/team/0/stages/Atk", "remove", "1", null);
        AssertChange(changes, "sides/0/team/0/stages/atk", "add", null, "1");
    }

    [Fact]
    public void JsonScalarsAndOrderingDoNotDependOnCulture()
    {
        var before = RichView();
        before.Phase = Phase.Choose;
        before.Notice = "null";
        var after = Copy(before);
        after.Phase = Phase.Over;
        after.Notice = null;
        after.ControlledReason = "引号\"、反斜杠\\、换行\n和😀";
        after.Skills[0].Effectiveness = 2.5;
        var previous = CultureInfo.CurrentCulture;
        string reference = null;
        try
        {
            foreach (string culture in new[] { "en-US", "fr-FR", "tr-TR" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                var changes = RoundTrip(before, after);
                string serialized = JsonSerializer.Serialize(changes, Json);
                if (reference == null) reference = serialized;
                else Assert.Equal(reference, serialized);
                AssertChange(changes, "phase", "set", "\"Choose\"", "\"Over\"");
                AssertChange(changes, "notice", "set", "\"null\"", "null");
                AssertChange(changes, "skills/0/effectiveness", "set", "1.25", "2.5");
            }
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void ChangedValuesThatCannotBeRepresentedByTheContractAreRejected()
    {
        var before = RichView();
        foreach (double number in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            var after = Copy(before);
            after.Skills[0].Effectiveness = number;
            Assert.ThrowsAny<ArgumentException>(() => ViewDiff.Between(before, after));
        }
        var invalidEnum = Copy(before);
        invalidEnum.Phase = (Phase)999;
        Assert.ThrowsAny<ArgumentException>(() => ViewDiff.Between(before, invalidEnum));
    }
}
