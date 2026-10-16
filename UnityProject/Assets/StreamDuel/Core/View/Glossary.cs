using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace StreamDuel.Core
{
    public sealed class GlossaryEntry
    {
        public string Category, Id, Title, Text;
        public List<string> Tags = new List<string>();
    }

    public static class Glossary
    {
        private static Loc Loc { get { return StreamDuel.Core.Loc.Current; } }

        public static List<GlossaryEntry> Build(ContentDb db)
        {
            if (db == null) throw new ArgumentNullException(nameof(db), Loc.T("glossary.error.db"));
            var entries = new List<GlossaryEntry>();
            foreach (var pair in db.Types.OrderBy(item => item.Key, StringComparer.Ordinal))
                entries.Add(Entry("track", pair.Key, pair.Value.Name, TrackText(db, pair.Key)));
            foreach (var pair in db.Statuses.OrderBy(item => item.Key, StringComparer.Ordinal))
                entries.Add(Entry("status", pair.Key, pair.Value.Name, StatusText(db, pair.Value), Describe.StatusKind(pair.Value)));
            foreach (var pair in db.Fields.OrderBy(item => item.Key, StringComparer.Ordinal))
                entries.Add(Entry("field", pair.Key, pair.Value.Name, FieldText(pair.Value), FieldKind(pair.Value)));
            foreach (var pair in db.Personas.OrderBy(item => item.Key, StringComparer.Ordinal))
                entries.Add(Entry("persona", pair.Key, pair.Value.Name, PersonaText(db, pair.Value)));

            entries.Add(Rule("order", Loc.T("glossary.rule.order.text")));
            entries.Add(Rule("priority", Loc.T("glossary.rule.priority.text", StatName(db, "spd"))));
            entries.Add(Rule("pp", Loc.T("glossary.rule.pp.text", DisplayName(db.Struggle?.Name))));
            entries.Add(Rule("stages", Loc.T("glossary.rule.stages.text", StatNames(db))));
            entries.Add(Rule("switch", Loc.T("glossary.rule.switch.text")));
            return entries;
        }

        public static List<GlossaryEntry> Search(List<GlossaryEntry> all, string keyword)
        {
            if (all == null) throw new ArgumentNullException(nameof(all), Loc.T("glossary.error.entries"));
            if (string.IsNullOrWhiteSpace(keyword)) return new List<GlossaryEntry>(all);
            string query = keyword.Trim();
            return all.Where(entry => entry != null
                && ((entry.Title?.IndexOf(query, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0
                    || (entry.Text?.IndexOf(query, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0)).ToList();
        }

        private static GlossaryEntry Entry(string category, string id, string title, string text, string tag = null)
        {
            string categoryKey = "glossary.category." + category;
            var entry = new GlossaryEntry
            {
                Category = category,
                Id = id,
                Title = DisplayName(title),
                Text = text,
                Tags = new List<string> { Loc.T(categoryKey) }
            };
            if (!string.IsNullOrWhiteSpace(tag)) entry.Tags.Add(tag);
            return entry;
        }

        private static GlossaryEntry Rule(string id, string text)
        {
            string titleKey = "glossary.rule." + id + ".title";
            return Entry("rule", id, Loc.T(titleKey), text);
        }

        private static string TrackText(ContentDb db, string id)
        {
            var strong = new List<string>();
            var resisted = new List<string>();
            var weak = new List<string>();
            var resistant = new List<string>();
            foreach (var other in db.Types.OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                double outgoing = db.TypeMul(id, other.Key);
                double incoming = db.TypeMul(other.Key, id);
                string name = DisplayName(other.Value.Name);
                if (outgoing > 1) strong.Add(Loc.T("glossary.track.matchup", name, Number(outgoing)));
                else if (outgoing < 1) resisted.Add(Loc.T("glossary.track.matchup", name, Number(outgoing)));
                if (incoming > 1) weak.Add(Loc.T("glossary.track.matchup", name, Number(incoming)));
                else if (incoming < 1) resistant.Add(Loc.T("glossary.track.matchup", name, Number(incoming)));
            }
            return Loc.T("glossary.track.text", Matchups(strong), Matchups(resisted), Matchups(weak), Matchups(resistant));
        }

        private static string Matchups(List<string> values)
        {
            return values.Count == 0 ? Loc.T("glossary.none") : string.Join(Loc.T("describe.list.separator"), values);
        }

        private static string StatusText(ContentDb db, StatusDef status)
        {
            var effects = new List<string>
            {
                Loc.T(status.IsControl ? "glossary.status.control" : "glossary.status.weak")
            };
            if (status.IsControl)
                effects.Add(Loc.T("glossary.status.recovery", DisplayName(db.Fields["reconnect"].Name)));
            if (!status.IsControl && status.Dot > 0)
            {
                effects.Add(Loc.T("glossary.effect.dot", Percent(status.Dot)));
                if (status.Leech) effects.Add(Loc.T("glossary.status.leech"));
            }
            if (status.InteractMul != 1)
                effects.Add(Loc.T("glossary.status.interact", Number(status.InteractMul)));
            if (status.WakeOnHit) effects.Add(Loc.T("glossary.status.wake"));
            return Loc.T("glossary.status.text", Describe.StatusKind(status),
                Describe.StatusDuration(status, status.IsControl ? status.Skip : status.Turns),
                Describe.StatusTiming(status),
                Loc.T(status.Cleanse ? "describe.dispel.cleanse" : "describe.dispel.no.cleanse"), Join(effects));
        }

        private static string FieldText(FieldDef field)
        {
            var effects = new List<string>();
            if (field.DmgTakenMul != 1) effects.Add(Loc.T("glossary.field.damage", Number(field.DmgTakenMul)));
            if (field.TalentMul != 1)
            {
                effects.Add(Loc.T("glossary.field.talent", Number(field.TalentMul)));
                if (field.Consume) effects.Add(Loc.T("glossary.field.consume"));
            }
            if (field.Prio != 0) effects.Add(Loc.T("glossary.field.priority", Signed(field.Prio)));
            if (field.Heal > 0) effects.Add(Loc.T("glossary.effect.heal", Percent(field.Heal)));
            if (field.Dot > 0) effects.Add(Loc.T("glossary.effect.dot", Percent(field.Dot)));
            if (field.ImmuneControl) effects.Add(Loc.T("glossary.field.immune.control"));
            if (field.ImmuneWeak) effects.Add(Loc.T("glossary.field.immune.weak"));
            if (field.PreventSwitch) effects.Add(Loc.T("glossary.field.prevent.switch"));
            if (field.ReflectDamage > 0) effects.Add(Loc.T("glossary.field.reflect", Percent(field.ReflectDamage)));
            if (field.MaxStacks > 0) effects.Add(Loc.T("glossary.field.stacks", field.MaxStacks));
            if (effects.Count == 0) effects.Add(Loc.T("glossary.field.source.effect"));
            // 定义本身没有持续回合，不能拿某一个施加来源的时间冒充统一规则。
            return Loc.T("glossary.field.text", FieldKind(field),
                string.IsNullOrWhiteSpace(field.When) ? Loc.T("glossary.field.source.timing") : field.When,
                Loc.T(field.Dispel ? "describe.dispel.turns" : "describe.dispel.no.turns"),
                Loc.T("glossary.field.duration"), Join(effects));
        }

        private static string PersonaText(ContentDb db, PersonaDef persona)
        {
            var description = Describe.Persona(db, persona);
            string at = description.At;
            string text = description.Text;
            switch (persona.Code)
            {
                case "T_LOWHP_POWER":
                    at = Loc.T("glossary.at.deal.damage");
                    text = Loc.T("glossary.persona.low.hp", Percent(persona.P("below")), Number(persona.P("mul")));
                    break;
                case "T_EXECUTE":
                    at = Loc.T("glossary.at.deal.damage");
                    text = Loc.T("glossary.persona.execute", Percent(persona.P("below")), Number(persona.P("mul")));
                    break;
                case "T_REGEN":
                    at = Loc.T("describe.at.turn.end");
                    text = Loc.T("glossary.effect.heal", Percent(persona.P("frac")));
                    break;
                case "T_BENCH_GUARD":
                    at = Loc.T("describe.at.damage");
                    text = Loc.T("glossary.persona.bench.guard", Percent(persona.P("per")));
                    break;
                case "T_INTERACT_REDUCE":
                    at = Loc.T("describe.at.damage");
                    text = Loc.T("glossary.persona.interact.reduce", Number(persona.P("mul")));
                    break;
                case "T_FIRST_TURN_PRIO":
                    at = Loc.T("glossary.at.priority");
                    text = Loc.T("glossary.persona.first.priority", Signed((int)persona.P("prio")));
                    break;
                case "T_TURNSTART_CLEANSE":
                    at = Loc.T("glossary.at.turn.start");
                    text = Loc.T("glossary.persona.cleanse");
                    break;
                case "T_FREE_PP_SLOT6":
                    at = Loc.T("glossary.at.use.skill");
                    text = Loc.T("glossary.persona.free.pp");
                    break;
                case "T_CONTACT_STATUS":
                    at = Loc.T("describe.at.hit");
                    text = Loc.T("glossary.persona.contact.status", Number(persona.P("chance")),
                        DisplayName(db.Statuses[persona.Status].Name));
                    break;
                case "T_ENTER_FIELD":
                    at = Loc.T("describe.at.enter");
                    var field = db.Fields[persona.Field];
                    int turns = (int)persona.P("turns");
                    text = Loc.T("glossary.persona.enter.field", DisplayName(field.Name),
                        turns < 0 ? Loc.T("describe.duration.until.leave") : Loc.T("describe.duration.turns", turns),
                        Loc.T(field.Dispel ? "describe.dispel.turns" : "describe.dispel.no.turns"));
                    break;
                case "T_FAINT_CURSE":
                    at = Loc.T("glossary.at.faint");
                    text = Loc.T("glossary.persona.faint.curse", StatNames(db), Signed((int)persona.P("n")));
                    break;
                case "T_SPEED_IF_FIRST":
                    at = Loc.T("describe.at.turn.end");
                    text = Loc.T("glossary.persona.speed.first", StatName(db, "spd"), Signed((int)persona.P("n")));
                    break;
            }
            return Loc.T("glossary.persona.text", at, description.Dispel, text);
        }

        private static string FieldKind(FieldDef field)
        {
            return field.Kind == "turn" || field.Kind == "mark" || field.Kind == "protect"
                ? Describe.FieldKind(field) : Loc.T("glossary.category.field");
        }

        private static string StatName(ContentDb db, string id)
        {
            string fallbackKey = "glossary.stat." + id;
            return db.StatNames.TryGetValue(id, out string name) && !string.IsNullOrWhiteSpace(name)
                ? name : Loc.T(fallbackKey);
        }

        private static string StatNames(ContentDb db)
        {
            return string.Join(Loc.T("describe.list.separator"), ContentDb.StatKeys.Select(id => StatName(db, id)));
        }

        private static string DisplayName(string name)
        {
            return string.IsNullOrWhiteSpace(name) ? Loc.T("glossary.unnamed") : name;
        }

        private static string Join(List<string> text) { return string.Join(Loc.T("glossary.list.separator"), text); }
        private static string Number(double value) { return value.ToString("G", CultureInfo.InvariantCulture); }
        private static string Percent(double value) { return Number(value * 100); }
        private static string Signed(int value) { return value.ToString("+0;-0;0", CultureInfo.InvariantCulture); }
    }
}
