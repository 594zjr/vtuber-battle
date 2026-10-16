using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using StreamDuel.Core;
using Xunit;

public class T90_SmartBanLeadTests
{
    static readonly string[] TeamA = { "bell", "enka", "shiori", "lumi", "hane", "momo" };
    static readonly string[] TeamB = { "kei", "yuan", "nyako", "buck", "kasumi", "bee" };

    [Theory]
    [InlineData(AiLevel.Hard, 0)]
    [InlineData(AiLevel.Hard, 1)]
    [InlineData(AiLevel.Expert, 0)]
    [InlineData(AiLevel.Expert, 1)]
    public void BansTheUniqueThreatToAtLeastFourAlliesWithoutCommittingOrMutating(AiLevel level, int side)
    {
        var battle = Make(true, BanContent(), side: side);
        var threat = battle.Sides[1 - side].Team[4];
        Assert.True(battle.Sides[side].Team.Count(ally => battle.Db.TypeMul(threat.Type, ally.Type) > 1) >= 4);
        string before = Snapshot(battle);
        ulong hash = battle.StateHash();
        string content = battle.Db.ContentHash();

        Assert.Equal(4, Ai.ChooseBan(battle, side, level));

        Assert.Equal(before, Snapshot(battle));
        Assert.Equal(hash, battle.StateHash());
        Assert.Equal(content, battle.Db.ContentHash());
        Assert.False(battle.HasChosenBan(side));
        Assert.Empty(battle.Log);
        Assert.Empty(battle.Events);
        Assert.Empty(battle.Record.Events);
    }

    [Theory]
    [InlineData(AiLevel.Hard, 0)]
    [InlineData(AiLevel.Hard, 1)]
    [InlineData(AiLevel.Expert, 0)]
    [InlineData(AiLevel.Expert, 1)]
    public void LeadUsesTheWholeRosterNotPrivateChoiceActiveOrEnemyOrder(AiLevel level, int side)
    {
        var original = Make(false, LeadContent(), side: side);
        string[] publicOrder = original.Record.Teams[1 - side].ToArray();
        var positions = new[] { original, original.Clone(), original.Clone(), original.Clone() };
        positions[1].ChooseLead(1 - side, 0);
        positions[2].ChooseLead(1 - side, 5);
        positions[2].Sides[1 - side].Active = 5;
        positions[3].Sides[1 - side].Team.Reverse();
        positions[3].Sides[1 - side].Active = 2;

        foreach (var battle in positions)
        {
            ulong hash = battle.StateHash();
            string before = Snapshot(battle);
            int choice = Ai.ChooseLead(battle, side, level);

            Assert.Equal(TeamA[0], battle.Sides[side].Team[choice].Def.Id);
            Assert.Equal(publicOrder, battle.Record.Teams[1 - side]);
            Assert.Equal(before, Snapshot(battle));
            Assert.Equal(hash, battle.StateHash());
            Assert.False(battle.HasChosenLead(side));
        }
    }

    [Theory]
    [InlineData(AiLevel.Hard, 0)]
    [InlineData(AiLevel.Hard, 1)]
    [InlineData(AiLevel.Expert, 0)]
    [InlineData(AiLevel.Expert, 1)]
    public void LeadPrefersTheUniqueBestNetMatchupOverTheHighestDamage(AiLevel level, int side)
    {
        var db = NeutralContent();
        db.Streamer(TeamA[0]).Stats["spa"] = 200;
        db.Streamer(TeamA[0]).Stats["sdef"] = 10;
        db.Streamer(TeamA[1]).Stats["spa"] = 150;
        db.Streamer(TeamA[1]).Stats["sdef"] = 200;
        var outgoing = DamageCalc.Matchup(db, TeamA, TeamB);
        var incoming = DamageCalc.Matchup(db, TeamB, TeamA);
        for (int foe = 0; foe < TeamB.Length; foe++)
        {
            Assert.True(incoming[foe, 1] > 0);
            Assert.True(incoming[foe, 0] > outgoing[0, foe]);
            for (int ally = 0; ally < TeamA.Length; ally++)
            {
                if (ally != 0) Assert.True(outgoing[0, foe] > outgoing[ally, foe]);
                if (ally != 1)
                    Assert.True(outgoing[1, foe] - incoming[foe, 1] > outgoing[ally, foe] - incoming[foe, ally]);
            }
        }
        var battle = Make(false, db, side: side);
        ulong hash = battle.StateHash();

        int choice = Ai.ChooseLead(battle, side, level);

        Assert.Equal(TeamA[1], battle.Sides[side].Team[choice].Def.Id);
        Assert.Equal(hash, battle.StateHash());
        Assert.False(battle.HasChosenLead(side));
    }

    [Theory]
    [InlineData(AiLevel.Hard)]
    [InlineData(AiLevel.Expert)]
    public void BanDoesNotPeekAtTheOpponentsUnrevealedBan(AiLevel level)
    {
        var first = Make(true, BanContent());
        var second = first.Clone();
        first.ChooseBan(1, 0);
        second.ChooseBan(1, 5);

        Assert.Equal(4, Ai.ChooseBan(first, 0, level));
        Assert.Equal(4, Ai.ChooseBan(second, 0, level));
        Assert.Equal(first.Random(), second.Random());
        Assert.All(first.Sides, state => Assert.Equal(6, state.Team.Count));
        Assert.All(second.Sides, state => Assert.Equal(6, state.Team.Count));
    }

    [Theory]
    [InlineData(AiLevel.Easy)]
    [InlineData(AiLevel.Normal)]
    [InlineData(AiLevel.Hard)]
    [InlineData(AiLevel.Expert)]
    public void SameSeedReproducesTiedChoicesAndTheFollowingRandomStream(AiLevel level)
    {
        var db = NeutralContent();
        var bans = new HashSet<int>();
        var leads = new HashSet<int>();
        foreach (uint seed in new uint[] { 1, 2, 7, 42, 90, uint.MaxValue })
        {
            foreach (bool ban in new[] { true, false })
            {
                var first = Make(ban, db, seed);
                var second = first.Clone();
                var random = new Rng(seed);
                int expected = (int)(random.Next() * 6);
                string before = Snapshot(first);
                int choice = Select(first, ban, level);

                Assert.InRange(choice, 0, 5);
                Assert.Equal(expected, choice);
                Assert.Equal(choice, Select(second, ban, level));
                Assert.Equal(before, Snapshot(first));
                for (int i = 0; i < 3; i++)
                {
                    double next = first.Random();
                    Assert.Equal(random.Next(), next);
                    Assert.Equal(next, second.Random());
                }
                (ban ? bans : leads).Add(choice);
            }
        }
        Assert.True(bans.Count > 1, "完全同分的禁用应由对战种子打破平局");
        Assert.True(leads.Count > 1, "完全同分的首发应由对战种子打破平局");
    }

    [Fact]
    public void EasySamplesLegalChoicesInsteadOfAlwaysSelectingTheStrongest()
    {
        var db = BanContent();
        var choices = new HashSet<int>();
        for (uint seed = 1; seed <= 12; seed++)
        {
            var battle = Make(true, db, seed);
            for (int side = 0; side < 2; side++)
            {
                int choice = Ai.ChooseBan(battle, side, AiLevel.Easy);
                Assert.InRange(choice, 0, 5);
                choices.Add(choice);
                battle.ChooseBan(side, choice);
            }
            Assert.Equal(Phase.Lead, battle.Phase);
            for (int side = 0; side < 2; side++)
            {
                int choice = Ai.ChooseLead(battle, side, AiLevel.Easy);
                Assert.InRange(choice, 0, 4);
                Assert.False(battle.Sides[side].Team[choice].Fainted);
                battle.ChooseLead(side, choice);
            }
            Assert.Equal(Phase.Choose, battle.Phase);
        }
        Assert.True(choices.Count > 1);
        Assert.Contains(choices, choice => choice != 4);
    }

    [Fact]
    public void NormalOverloadsPreserveLegacyChoicesAndExactRandomConsumption()
    {
        foreach (var db in new[] { NeutralContent(), BattleTests.LoadPack("original") })
        {
            foreach (uint seed in new uint[] { 1, 90, uint.MaxValue })
            {
                foreach (bool ban in new[] { true, false })
                {
                    var legacy = Make(ban, db, seed);
                    var normal = legacy.Clone();
                    for (int side = 0; side < 2; side++)
                    {
                        int expected = ban ? Ai.ChooseBan(legacy, side) : Ai.ChooseLead(legacy, side);
                        Assert.Equal(expected, Select(normal, ban, AiLevel.Normal, side));
                        Assert.Equal(legacy.StateHash(), normal.StateHash());
                        Assert.Equal(Snapshot(legacy), Snapshot(normal));
                        Assert.Equal(legacy.Random(), normal.Random());
                    }
                }
            }
        }
    }

    [Fact]
    public void LegacyTieBreakingStillUsesExactlyOneDrawAndNormalKeepsPendingLeadBehavior()
    {
        var db = NeutralContent();
        foreach (bool ban in new[] { true, false })
        {
            var legacy = Make(ban, db, 90);
            var expected = new Rng(90);
            int index = (int)(expected.Next() * 6);
            Assert.Equal(index, ban ? Ai.ChooseBan(legacy, 0) : Ai.ChooseLead(legacy, 0));
            Assert.Equal(expected.Next(), legacy.Random());
        }
        var pending = Make(false, db);
        pending.ChooseLead(0, 3);
        var normal = pending.Clone();
        Assert.Equal(Ai.ChooseLead(pending, 0), Ai.ChooseLead(normal, 0, AiLevel.Normal));
        Assert.Equal(pending.StateHash(), normal.StateHash());
    }

    [Theory]
    [InlineData(AiLevel.Hard, 0)]
    [InlineData(AiLevel.Hard, 1)]
    [InlineData(AiLevel.Expert, 0)]
    [InlineData(AiLevel.Expert, 1)]
    public void LeadReturnsTheRemainingRosterIndexAfterBothBans(AiLevel level, int side)
    {
        var battle = Make(true, LeadContent(), side: side);
        battle.ChooseBan(side, 5);
        battle.ChooseBan(1 - side, 0);
        Assert.Equal(Phase.Lead, battle.Phase);
        Assert.DoesNotContain(battle.Sides[side].Team, fighter => fighter.Def.Id == TeamA[0]);

        int choice = Ai.ChooseLead(battle, side, level);

        Assert.Equal(0, choice);
        Assert.Equal(TeamA[1], battle.Sides[side].Team[choice].Def.Id);
        battle.ChooseLead(side, choice);
        battle.ChooseLead(1 - side, 0);
        Assert.Equal(TeamA[1], battle.Active(side).Def.Id);
    }

    [Theory]
    [InlineData(AiLevel.Hard)]
    [InlineData(AiLevel.Expert)]
    public void OpeningEvaluatesActualModifiersRatherThanMutableRecordMetadata(AiLevel level)
    {
        var db = NeutralContent();
        AddSkill(db, "t90_mark_power", "art", 60);
        db.Skills["t90_mark_power"].Effects.Add(new EffectDef
        {
            At = "pre", Code = "POWER_PER_MARK", Field = "hype", Per = 1
        });
        AddSkill(db, "t90_heavy", "art", 80);
        foreach (var streamer in db.Streamers) SetSkills(streamer, "t90_idle");
        SetSkills(db.Streamer(TeamA[0]), "t90_mark_power");
        SetSkills(db.Streamer(TeamA[1]), "t90_heavy");
        var ordinary = Make(false, db);
        Assert.Equal(1, Ai.ChooseLead(ordinary, 0, level));
        var actual = new Battle(db, new IList<string>[] { TeamA, TeamB }, new[] { "甲方", "乙方" },
            new BattleOptions { Seed = 90, LeadPick = true, Modifiers = new List<string> { "hype_start" } });
        var changedRecord = actual.Clone();
        changedRecord.Record.Modifiers = new List<string> { "not_a_modifier" };
        var nullRecord = actual.Clone();
        nullRecord.Record.Modifiers = null;

        foreach (var battle in new[] { actual, changedRecord, nullRecord })
        {
            string before = Snapshot(battle);
            Assert.Equal(0, Ai.ChooseLead(battle, 0, level));
            Assert.Equal(new[] { "hype_start" }, battle.Modifiers);
            Assert.Equal(before, Snapshot(battle));
        }
    }

    [Fact]
    public void ExpertReevaluatesLostSixthSlotSupportAfterBans()
    {
        var db = NeutralContent();
        AddSkill(db, "t90_empty_strong", "talk", 200, 0);
        foreach (string id in TeamA) SetSkills(db.Streamer(id), "t90_idle");
        SetSkills(db.Streamer(TeamA[0]), "t90_empty_strong");
        db.Streamer(TeamA[0]).Persona = "teamspirit";
        SetSkills(db.Streamer(TeamA[1]), "t90_art");
        var battle = Make(true, db);
        battle.ChooseBan(0, 5);
        battle.ChooseBan(1, 5);
        Assert.All(battle.Sides, state => Assert.Equal(5, state.Team.Count));
        var hard = battle.Clone();

        Assert.Equal(TeamA[0], hard.Sides[0].Team[Ai.ChooseLead(hard, 0, AiLevel.Hard)].Def.Id);
        int expert = Ai.ChooseLead(battle, 0, AiLevel.Expert);
        Assert.Equal(TeamA[1], battle.Sides[0].Team[expert].Def.Id);

        var unavailable = battle.Clone();
        unavailable.ChooseLead(0, 0);
        unavailable.ChooseLead(1, 0);
        Assert.False(unavailable.LegalFor(0).FreePp);
        Assert.True(unavailable.LegalFor(0).Struggle);
    }

    [Theory]
    [InlineData(AiLevel.Easy)]
    [InlineData(AiLevel.Hard)]
    [InlineData(AiLevel.Expert)]
    public void FaintedCandidatesAreSkippedEvenIfTheyWouldOtherwiseScoreBest(AiLevel level)
    {
        foreach (bool ban in new[] { true, false })
        {
            var battle = Make(ban, ban ? BanContent() : LeadContent());
            var candidates = battle.Sides[ban ? 1 : 0].Team;
            for (int i = 0; i < candidates.Count; i++)
            {
                if (i == 2) continue;
                candidates[i].Fainted = true;
                candidates[i].Hp = 0;
            }

            Assert.Equal(2, Select(battle, ban, level));
        }
    }

    [Theory]
    [InlineData(AiLevel.Easy)]
    [InlineData(AiLevel.Hard)]
    [InlineData(AiLevel.Expert)]
    public void EmptyOrEntirelyFaintedCandidateListsFailWithoutChangingState(AiLevel level)
    {
        foreach (bool ban in new[] { true, false })
        {
            foreach (bool empty in new[] { true, false })
            {
                var battle = Make(ban, NeutralContent());
                var candidates = battle.Sides[ban ? 1 : 0].Team;
                if (empty) candidates.Clear();
                else foreach (var candidate in candidates) { candidate.Fainted = true; candidate.Hp = 0; }
                string before = Snapshot(battle);
                ulong hash = battle.StateHash();

                Assert.Throws<InvalidOperationException>(() => Select(battle, ban, level));

                Assert.Equal(before, Snapshot(battle));
                Assert.Equal(hash, battle.StateHash());
            }
        }
    }

    [Theory]
    [InlineData(AiLevel.Easy)]
    [InlineData(AiLevel.Hard)]
    [InlineData(AiLevel.Expert)]
    public void InvalidArgumentsWrongPhaseAndRepeatedOwnChoicesAreRejected(AiLevel level)
    {
        foreach (bool ban in new[] { true, false })
        {
            Assert.Throws<ArgumentNullException>(() => Select(null, ban, level));
            var battle = Make(ban, NeutralContent());
            ulong hash = battle.StateHash();
            Assert.Throws<ArgumentOutOfRangeException>(() => Select(battle, ban, level, -1));
            Assert.Throws<ArgumentOutOfRangeException>(() => Select(battle, ban, level, 2));
            Assert.Throws<ArgumentOutOfRangeException>(() => Select(battle, ban, (AiLevel)(-1)));
            Assert.Throws<ArgumentOutOfRangeException>(() => Select(battle, ban, (AiLevel)99));
            Assert.Equal(hash, battle.StateHash());
            foreach (Phase phase in new[] { Phase.Choose, Phase.Replace, Phase.Over, ban ? Phase.Lead : Phase.Ban })
            {
                var wrongPhase = battle.Clone();
                wrongPhase.Phase = phase;
                ulong before = wrongPhase.StateHash();
                Assert.Throws<InvalidOperationException>(() => Select(wrongPhase, ban, level));
                Assert.Equal(before, wrongPhase.StateHash());
            }
            if (ban) battle.ChooseBan(0, 1);
            else battle.ChooseLead(0, 1);
            string pending = Snapshot(battle);
            hash = battle.StateHash();

            Assert.Throws<InvalidOperationException>(() => Select(battle, ban, level));

            Assert.Equal(pending, Snapshot(battle));
            Assert.Equal(hash, battle.StateHash());
        }
    }

    static Battle Make(bool ban, ContentDb db, uint seed = 90, int side = 0)
    {
        IList<string>[] teams = { TeamA.ToArray(), TeamB.ToArray() };
        if (side == 1) Array.Reverse(teams);
        return new Battle(db, teams, new[] { "甲方", "乙方" },
            new BattleOptions { Seed = seed, BanOne = ban, LeadPick = true });
    }

    static int Select(Battle battle, bool ban, AiLevel level, int side = 0)
    {
        return ban ? Ai.ChooseBan(battle, side, level) : Ai.ChooseLead(battle, side, level);
    }

    static string Snapshot(Battle battle)
    {
        return JsonSerializer.Serialize(battle, new JsonSerializerOptions { IncludeFields = true });
    }

    static ContentDb NeutralContent()
    {
        var db = BattleTests.LoadPack("original");
        db.Chart.Clear();
        db.Personas["t90_neutral"] = new PersonaDef { Id = "t90_neutral", Name = "测试人设", Code = "T_LOWHP_POWER" };
        AddSkill(db, "t90_art", "art", 40);
        AddSkill(db, "t90_game", "game", 40);
        AddSkill(db, "t90_song", "song", 40);
        AddSkill(db, "t90_idle", "talk", 0);
        foreach (var streamer in db.Streamers)
        {
            streamer.Type = "talk";
            streamer.Type2 = null;
            streamer.Persona = "t90_neutral";
            streamer.AiStyle = null;
            streamer.Stats["hp"] = 1000;
            foreach (string stat in ContentDb.StatKeys) streamer.Stats[stat] = 100;
            SetSkills(streamer, "t90_art");
        }
        return db;
    }

    static ContentDb BanContent()
    {
        var db = NeutralContent();
        db.Chart["game"] = new Dictionary<string, double> { { "talk", 2 }, { "art", 0.5 } };
        db.Chart["song"] = new Dictionary<string, double> { { "talk", 0.5 }, { "art", 4 } };
        db.Streamer(TeamA[0]).Type = "art";
        db.Streamer(TeamB[0]).Type = "song";
        SetSkills(db.Streamer(TeamB[0]), "t90_song");
        db.Streamer(TeamB[4]).Type = "game";
        SetSkills(db.Streamer(TeamB[4]), "t90_game");
        return db;
    }

    static ContentDb LeadContent()
    {
        var db = NeutralContent();
        db.Chart["game"] = new Dictionary<string, double> { { "talk", 2 }, { "song", 0.5 } };
        db.Chart["art"] = new Dictionary<string, double> { { "talk", 0.5 }, { "song", 2 } };
        foreach (string id in TeamA) SetSkills(db.Streamer(id), "t90_idle");
        SetSkills(db.Streamer(TeamA[0]), "t90_game");
        db.Streamer(TeamA[0]).Type = "game";
        db.Streamer(TeamA[0]).Stats["spa"] = 120;
        SetSkills(db.Streamer(TeamA[1]), "t90_art");
        db.Streamer(TeamA[1]).Type = "art";
        for (int i = 0; i < TeamB.Length; i++)
        {
            db.Streamer(TeamB[i]).Type = i < 3 ? "talk" : "song";
            SetSkills(db.Streamer(TeamB[i]), "t90_idle");
        }
        return db;
    }

    static void SetSkills(StreamerDef streamer, string skill)
    {
        streamer.Skills = Enumerable.Repeat(skill, 4).ToList();
        streamer.Fifth = skill;
    }

    static void AddSkill(ContentDb db, string id, string type, int power, int pp = 10)
    {
        db.Skills[id] = new SkillDef
        {
            Id = id, Name = "测试技能", Type = type, Cat = power == 0 ? "status" : "talent", Power = power, Pp = pp
        };
    }
}
