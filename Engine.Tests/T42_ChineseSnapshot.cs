using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using StreamDuel.Core;

internal static class T42_ChineseSnapshot
{
    public static SortedDictionary<string, string> Collect(string resources, string fixtureDirectory)
    {
        var rows = new SortedDictionary<string, string>(StringComparer.Ordinal);
        string rules = File.ReadAllText(Path.Combine(resources, "StreamDuelRules.json"));
        foreach (string path in Directory.GetFiles(Path.Combine(resources, "Packs"), "*.json").OrderBy(p => p, StringComparer.Ordinal))
        {
            var db = ContentDb.FromJson(rules, File.ReadAllText(path));
            string prefix = Path.GetFileName(path) + "/";
            foreach (var skill in db.Skills.Values.Concat(new[] { db.Struggle }).OrderBy(s => s.Id, StringComparer.Ordinal))
                for (int i = 0; i < skill.Effects.Count; i++)
                    rows[prefix + "effect/" + skill.Id + "/" + i] = Text(Describe.Effect(db, skill.Effects[i]));
            foreach (var persona in db.Personas.OrderBy(p => p.Key, StringComparer.Ordinal))
                rows[prefix + "persona/" + persona.Key] = Text(Describe.Persona(db, persona.Value));
            foreach (var field in db.Fields.OrderBy(p => p.Key, StringComparer.Ordinal))
                rows[prefix + "field/" + field.Key] = Describe.FieldKind(field.Value);
            foreach (var streamer in db.Streamers.OrderBy(s => s.Id, StringComparer.Ordinal))
                rows[prefix + "tracks/" + streamer.Id] = Describe.Tracks(db, streamer);
        }

        string original = File.ReadAllText(Path.Combine(resources, "Packs", "original.json"));
        var source = ContentDb.FromJson(rules, original);
        foreach (var value in new[] { 0.5, 0.25, 0.125, 0.333, 0.1, 0.0, -0.05 })
            rows["extra/fraction/" + value.ToString("R", CultureInfo.InvariantCulture)] = Describe.Frac(value);
        rows["extra/condition/null"] = Describe.Cond(null);
        foreach (string condition in new[] { "foeHpAbove", "foeWeak", "actedFirst", "actedSecond", "foeBuffed", "unknown" })
            rows["extra/condition/" + condition] = Describe.Cond(new CondDef { Kind = condition, Value = 0.333 });
        rows["extra/field/unknown"] = Describe.FieldKind(new FieldDef { Kind = "unknown" });

        var effects = new[]
        {
            new EffectDef { Code = "STATUS", At = "pre", Target = "self", Status = "flame", Chance = 0 },
            new EffectDef { Code = "STATUS", At = "post", Target = "foe", Status = "stall", Chance = 50 },
            new EffectDef { Code = "STAGE", At = "pre", Target = "self", Chance = 30,
                Changes = new Dictionary<string, int> { { "atk", 1 }, { "def", -2 }, { "spa", 0 } } },
            new EffectDef { Code = "STAGE", At = "post", Target = "foe",
                Changes = new Dictionary<string, int> { { "spd", -1 } } },
            new EffectDef { Code = "FIELD", At = "pre", Target = "self", Field = "shield", Turns = -1, Chance = 50 },
            new EffectDef { Code = "FIELD", At = "post", Target = "foe", Field = "blackhist", Turns = 0 },
            new EffectDef { Code = "POWER_PER_MARK", At = "pre", Field = "hype", Per = 0.333, Consume = false },
            new EffectDef { Code = "POWER_PER_MARK", At = "pre", Field = "hype", Per = 0.25, Consume = true },
            new EffectDef { Code = "PP_REDUCE", At = "pre", Target = "self", N = 3, Chance = 40 },
            new EffectDef { Code = "FIXED_DAMAGE", At = "post", Target = "self", N = 0, Chance = 20 },
            new EffectDef { Code = "POWER_MUL", At = "pre", Mul = 1.2345,
                Cond = new CondDef { Kind = "foeHpAbove", Value = 0.333 } },
            new EffectDef { Code = "UNKNOWN", At = "unknown", Target = "foe" }
        };
        for (int i = 0; i < effects.Length; i++) rows["extra/effect/" + i] = Text(Describe.Effect(source, effects[i]));
        foreach (string code in new[] { "T_KO_HEAL", "T_FIRST_CONTROL_IMMUNE", "T_ENDURE", "T_ALLY_PP_ON_ENTER", "T_STAGE_ON_HIT", "UNKNOWN" })
            rows["extra/persona/" + code] = Text(Describe.Persona(source, new PersonaDef
            {
                Code = code, Node = "原始节点", Desc = code == "UNKNOWN" ? null : "原始说明",
                Params = new Dictionary<string, double> { { "frac", 0.25 }, { "n", 2 }, { "atk", 1 }, { "def", -1 }, { "spd", 2 } }
            }));

        rows["team/null"] = JsonSerializer.Serialize(TeamCheck.Validate(source, null, null));
        rows["team/missing-season"] = JsonSerializer.Serialize(TeamCheck.Validate(source, "missing", new string[0]));
        rows["team/duplicates-and-missing"] = JsonSerializer.Serialize(TeamCheck.Validate(source, null,
            new[] { "enka", "enka", null, null, "missing", "missing" }));
        rows["team/ban-and-limit"] = JsonSerializer.Serialize(TeamCheck.Validate(source, "s1",
            new[] { "kei", "bell", "bee", "hane", "lumi", "enka" }));

        var reason = typeof(PackLoader).GetMethod("Reason", BindingFlags.NonPublic | BindingFlags.Static);
        var failures = new Exception[]
        {
            new KeyError("自定义校验原因"), new UnauthorizedAccessException(), new IOException(),
            new FormatException(), new IndexOutOfRangeException(), new ArgumentOutOfRangeException(),
            new InvalidCastException(), new KeyNotFoundException(), new NullReferenceException(),
            new ArgumentException(), new OverflowException(), new Exception()
        };
        foreach (var failure in failures)
            rows["pack/reason/" + failure.GetType().Name] = (string)reason.Invoke(null, new object[] { failure });

        Directory.CreateDirectory(fixtureDirectory);
        try
        {
            var emptyId = JsonSerializer.Deserialize<Dictionary<string, object>>(original);
            emptyId["id"] = "";
            File.WriteAllText(Path.Combine(fixtureDirectory, "a-valid.json"), original, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(fixtureDirectory, "b-duplicate.json"), original, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(fixtureDirectory, "c-empty-id.json"), JsonSerializer.Serialize(emptyId), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(fixtureDirectory, "d-syntax.json"), "{", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(fixtureDirectory, "e-shape.json"), "[]", new UTF8Encoding(false));
            foreach (var result in PackLoader.LoadDirectory(rules, fixtureDirectory))
                rows["pack/load/" + result.FileName] = result.Error ?? "成功:" + result.Db.PackId;
        }
        finally
        {
            foreach (string file in Directory.GetFiles(fixtureDirectory, "*.json")) File.Delete(file);
            Directory.Delete(fixtureDirectory);
        }
        return rows;
    }

    static string Text(EffectText text)
    {
        return JsonSerializer.Serialize(new { text.At, text.Text, text.Dispel });
    }
}
