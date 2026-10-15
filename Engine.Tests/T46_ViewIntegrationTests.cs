using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using StreamDuel.Core;
using Xunit;

[Collection("Localization current")]
public class T46_ViewIntegrationTests
{
    static ContentDb Db()
    {
        var db = BattleTests.LoadPack("fan_asoul_vr");
        db.Streamer("bella").Skills[0] = "gain_takeover";
        return db;
    }

    static Battle Make(ContentDb db, string[] modifiers = null, bool lead = false)
    {
        var ids = db.Streamers.Select(streamer => streamer.Id).ToArray();
        var teams = new IList<string>[]
        {
            new[] { "bella" }.Concat(ids.Where(id => id != "bella").Take(5)).ToArray(),
            new[] { "diana" }.Concat(ids.Where(id => id != "diana").Take(5)).ToArray()
        };
        return new Battle(db, teams, new[] { "你", "对手" }, new BattleOptions
        {
            Seed = 46, LeadPick = lead, Modifiers = modifiers?.ToList()
        });
    }

    static void EqualText(EffectText expected, EffectText actual)
    {
        Assert.Equal(expected.At, actual.At);
        Assert.Equal(expected.Text, actual.Text);
        Assert.Equal(expected.Dispel, actual.Dispel);
    }

    [Fact]
    public void DefaultDescriptionsAndRawPersonaSnapshotsRemainUnchanged()
    {
        var previous = Loc.Current;
        try
        {
            Loc.Current = null;
            var db = Db();
            var battle = Make(db);
            var steal = db.Skills["gain_takeover"].Effects[0];
            Assert.Equal("将对手所有大于 0 的能力等级加到自身（每项最高 +6），并清空目标的这些提升；目标下降等级不变",
                Describe.Effect(db, steal).Text);
            Assert.Equal("每次受到普通技能直接伤害，实际扣除人气且没有下播时，自身抗压+1（能力等级最高 +6）；固定伤害、持续伤害、反噬和反弹不触发。反弹使行动方下播后，不再触发",
                Describe.Persona(db, db.Personas["av_koichi_lane"]).Text);
            Assert.Equal(db.Personas["av_bella_rhythm"].Desc, Describe.Persona(db, db.Personas["av_bella_rhythm"]).Text);
            foreach (var effect in db.Skills.Values.SelectMany(skill => skill.Effects))
                EqualText(Describe.Effect(db, effect), Describe.Effect(db, effect, battle));
            foreach (var persona in db.Personas.Values)
            {
                EqualText(Describe.Persona(db, persona), Describe.Persona(db, persona, battle));
                Assert.Equal(persona.Desc, Describe.PersonaDescription(db, persona));
                Assert.Equal(persona.Desc, Describe.PersonaDescription(db, persona, battle));
            }
            var view = BattleViewModel.From(battle, 0);
            foreach (var fighter in view.Sides.SelectMany(side => side.Team))
                Assert.Equal(db.Personas[fighter.PersonaId].Desc, fighter.PersonaDescription);
            Assert.Equal(Describe.Effect(db, steal).Text,
                Assert.Single(view.Skills.Single(skill => skill.Id == "gain_takeover").Effects).Text);
        }
        finally { Loc.Current = previous; }
    }

    [Fact]
    public void ModifiedViewsUseCurrentCapsHpAndPpWithoutChangingPackDescriptions()
    {
        var previous = Loc.Current;
        try
        {
            Loc.Current = null;
            var db = Db();
            var descriptions = db.Personas.ToDictionary(pair => pair.Key, pair => pair.Value.Desc);
            string contentHash = db.ContentHash();
            var battle = Make(db, new[] { "stage_cap_3", "hp_x2", "half_pp" }, true);
            var lead = BattleViewModel.From(battle, 0);
            Assert.Equal(Phase.Lead, lead.Phase);
            foreach (var fighter in lead.Sides.SelectMany(side => side.Team))
            {
                Assert.Equal(db.Streamer(fighter.Id).Stats["hp"] * 2, fighter.MaxHp);
                Assert.Equal(fighter.MaxHp, fighter.Hp);
            }
            battle.ChooseLead(0, 0);
            battle.ChooseLead(1, 0);
            var view = BattleViewModel.From(battle, 0);
            string effectText = Assert.Single(view.Skills.Single(skill => skill.Id == "gain_takeover").Effects).Text;
            Assert.Contains("每项最高 +3", effectText);
            Assert.DoesNotContain("+6", effectText);
            foreach (var id in new[] { "av_koichi_lane", "av_bella_rhythm" })
            {
                string text = view.Sides[0].Team.Single(fighter => fighter.PersonaId == id).PersonaDescription;
                Assert.Contains("能力等级最高 +3", text);
                Assert.DoesNotContain("+6", text);
                Assert.Contains("能力提升可被消强化，下场清零", text);
                Assert.Equal(Describe.PersonaDescription(db, db.Personas[id], battle), text);
                Assert.Contains("最高 +6", db.Personas[id].Desc);
            }
            Assert.Contains("无需成功出招", view.Sides[0].Team[0].PersonaDescription);
            foreach (var skill in view.Skills)
            {
                Assert.Equal((db.Skills[skill.Id].Pp + 1) / 2, skill.MaxPp);
                Assert.Equal(skill.MaxPp, skill.Pp);
            }
            foreach (var persona in db.Personas.Values) Assert.Equal(descriptions[persona.Id], persona.Desc);
            Assert.Equal(contentHash, db.ContentHash());
        }
        finally { Loc.Current = previous; }
    }

    [Fact]
    public void InjectedCapTemplatesReachViewsWithoutChangingStateHistoryOrRandomSequence()
    {
        var previous = Loc.Current;
        try
        {
            Loc.Current = null;
            var db = Db();
            var modifiers = new[] { "stage_cap_3", "hp_x2", "half_pp" };
            var battle = Make(db, modifiers);
            var reference = Make(db, modifiers);
            battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());
            reference.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());
            Assert.Contains(battle.Events, entry => entry.Kind == BattleEventKind.Act);
            var jsonOptions = new JsonSerializerOptions { IncludeFields = true };
            ulong hash = battle.StateHash();
            string contentHash = db.ContentHash();
            string log = JsonSerializer.Serialize(battle.Log, jsonOptions);
            string events = JsonSerializer.Serialize(battle.Events, jsonOptions);
            string record = battle.Record.ToJson();
            var texts = new Dictionary<string, string>
            {
                { "describe.effect.steal.boost", "steal cap={2}" },
                { "describe.persona.stage.hit", "hit cap={1}" },
                { "describe.persona.speed.first", "first cap={2}; gain={1}; stat={0}" },
                { "describe.persona.summary", "{0} | {1}" },
                { "describe.dispel.persona", "permanent persona" },
                { "describe.dispel.persona.stage", "stages clear on leaving; {0}" }
            };
            Loc.Current = Loc.FromJson(JsonSerializer.Serialize(new { language = "en", texts }), Loc.Current);
            var view = BattleViewModel.From(battle, 0);
            BattleViewModel.From(battle, 1);
            Assert.Equal("steal cap=3", Assert.Single(view.Skills.Single(skill => skill.Id == "gain_takeover").Effects).Text);
            Assert.Equal("hit cap=3 | stages clear on leaving; permanent persona",
                view.Sides[0].Team.Single(fighter => fighter.PersonaId == "av_koichi_lane").PersonaDescription);
            Assert.Equal("first cap=3; gain=1; stat=反应 | stages clear on leaving; permanent persona",
                view.Sides[0].Team.Single(fighter => fighter.PersonaId == "av_bella_rhythm").PersonaDescription);
            Assert.Equal("hit cap=3", Describe.Persona(db, db.Personas["av_koichi_lane"], battle).Text);
            Assert.Equal(hash, battle.StateHash());
            Assert.Equal(contentHash, db.ContentHash());
            Assert.Equal(log, JsonSerializer.Serialize(battle.Log, jsonOptions));
            Assert.Equal(events, JsonSerializer.Serialize(battle.Events, jsonOptions));
            Assert.Equal(record, battle.Record.ToJson());
            Assert.Equal(reference.Random(), battle.Random());
        }
        finally { Loc.Current = previous; }
    }
}
