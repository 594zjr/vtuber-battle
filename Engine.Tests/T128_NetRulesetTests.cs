using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using StreamDuel.Core;
using Xunit;

public class T128_NetRulesetTests
{
    static readonly string[] HostTeam = { "bell", "enka", "shiori", "lumi", "hane", "momo" };
    static readonly string[] GuestTeam = { "kei", "yuan", "nyako", "buck", "kasumi", "bee" };

    [Theory]
    [InlineData(128u)]
    [InlineData(uint.MaxValue)]
    public void BestOfThreeConfirmsEachGameSeedHashAndScoreBeforeConfirmingTheMatch(uint seed)
    {
        var pair = new Pair(Rule(3, true, true), seed: seed);
        pair.Start();
        int[] winners = { 0, 1, 0 };
        for (int game = 0; game < winners.Length; game++)
        {
            Assert.Equal(game + 1, pair.Host.GameNumber);
            Assert.Equal(unchecked(seed + (uint)game), pair.Host.GameSeed);
            Assert.Equal(pair.Host.GameSeed, pair.Guest.GameSeed);
            pair.Pregame();
            pair.PassTurn();
            pair.FinishGame(winners[game]);
            Assert.Equal(game + 1, pair.Host.GameResults.Count);
            Assert.Equal(pair.Host.Wins, pair.Guest.Wins);
            Assert.Equal(winners.Take(game + 1).Count(value => value == 0), pair.Host.Wins[0]);
            Assert.Equal(winners.Take(game + 1).Count(value => value == 1), pair.Host.Wins[1]);
            Assert.Equal(0, pair.Host.Draws);
            var host = pair.Host.GameResults[game];
            var guest = pair.Guest.GameResults[game];
            Assert.Equal(game + 1, host.Number);
            Assert.Equal(unchecked(seed + (uint)game), host.Seed);
            Assert.Equal(winners[game], host.Winner);
            Assert.Equal(host.StateHash, guest.StateHash);
            Assert.Equal(host.Record.ToJson(), guest.Record.ToJson());
            Assert.Equal(host.StateHash, Replay.Run(pair.HostDb, host.Record).StateHash());
            Assert.Equal(host.StateHash, host.Battle.StateHash());
            Assert.Equal(game == 2, pair.Host.MatchConfirmed);
            Assert.Equal(game == 2, pair.Guest.MatchConfirmed);
        }
        Assert.Equal(NetState.Over, pair.Host.State);
        Assert.Equal(NetState.Over, pair.Guest.State);
        Assert.Equal(new[] { 2, 1 }, pair.Host.Wins);
        Assert.Equal(0, pair.Host.MatchWinner);
        Assert.Equal(0, pair.Guest.MatchWinner);
        Assert.Throws<InvalidOperationException>(() => pair.Host.Submit(BattleAction.Pass()));
        pair.Pump();
        Assert.Equal(3, pair.Host.GameResults.Count);
    }

    [Fact]
    public void TwoStraightWinsEndBestOfThreeWithoutCreatingAThirdBattle()
    {
        var pair = new Pair(Rule(3));
        pair.Start();
        pair.FinishGame(1);
        Assert.False(pair.Host.MatchConfirmed);
        pair.FinishGame(1);
        Assert.True(pair.Host.MatchConfirmed);
        Assert.Equal(new[] { 0, 2 }, pair.Host.Wins);
        Assert.Equal(2, pair.Host.GameNumber);
        Assert.Equal(2, pair.Host.GameResults.Count);
        Assert.Equal(1, pair.Host.MatchWinner);
    }

    [Theory]
    [InlineData("ranked")]
    [InlineData("chaos")]
    public void RealAiGamesCompleteThroughNetworkSelectionsAndReplayAllConfirmedGames(string preset)
    {
        var pair = new Pair(Rulesets.All.Single(item => item.Id == preset));
        pair.Start();
        for (int step = 0; step < 12000 && !pair.Host.MatchConfirmed; step++)
        {
            pair.Pump(2);
            pair.AssertHealthy();
            foreach (var session in new[] { pair.Host, pair.Guest })
            {
                if (session.HasSubmitted) continue;
                if (session.State == NetState.Ban) session.SubmitBan(5);
                else if (session.State == NetState.Lead) session.SubmitLead(0);
                else if (session.State == NetState.Choose) session.Submit(Ai.Choose(session.Battle, session.MySide));
                else if (session.State == NetState.Replace) session.SubmitReplace(Ai.ChooseReplacement(session.Battle, session.MySide));
            }
        }
        pair.Pump();
        Assert.True(pair.Host.MatchConfirmed);
        Assert.True(pair.Guest.MatchConfirmed);
        Assert.NotEmpty(pair.Host.GameResults);
        Assert.Equal(pair.Host.GameResults.Count, pair.Guest.GameResults.Count);
        Assert.Contains(pair.Host.GameResults, game => game.Record.Events.Any(item => item.Type == BattleRecordEventType.Turn));
        for (int i = 0; i < pair.Host.GameResults.Count; i++)
        {
            var left = pair.Host.GameResults[i];
            var right = pair.Guest.GameResults[i];
            Assert.Equal(left.StateHash, right.StateHash);
            Assert.Equal(left.Record.ToJson(), right.Record.ToJson());
            Assert.Equal(left.StateHash, Replay.Run(pair.HostDb, left.Record).StateHash());
        }
    }

    [Theory]
    [InlineData("bestOf")]
    [InlineData("ban")]
    [InlineData("lead")]
    [InlineData("timer")]
    [InlineData("modifier")]
    [InlineData("season")]
    public void DifferentExpectedRulesAreRejectedBeforePlayingAnyGame(string difference)
    {
        var hostRule = Rule(3);
        var guestRule = Copy(hostRule);
        var hostDb = OpenSeasonDb();
        var guestDb = OpenSeasonDb();
        if (difference == "bestOf") guestRule.BestOf = 5;
        else if (difference == "ban") guestRule.BanOne = true;
        else if (difference == "lead") guestRule.LeadPick = true;
        else if (difference == "timer") guestRule.TurnSeconds = 10;
        else if (difference == "modifier") guestRule.Modifiers.Add("half_pp");
        else guestRule.Season = "open";
        var pair = new Pair(hostRule, guestRule, hostDb, guestDb);
        pair.Pump();
        AssertRejected(pair);
    }

    [Theory]
    [InlineData("missing_season")]
    [InlineData("missing_modifier")]
    [InlineData("illegal_season_team")]
    public void InvalidRulesOrSeasonTeamsFailWithoutSendingAHello(string problem)
    {
        var rule = Rule();
        if (problem == "missing_season") rule.Season = "not_a_season";
        else if (problem == "missing_modifier") rule.Modifiers.Add("not_a_modifier");
        else rule.Season = "s1";
        var pair = new Pair(rule);
        pair.Pump();
        AssertRejected(pair);
        var invalid = problem == "illegal_season_team" ? pair.GuestWire : pair.HostWire;
        Assert.DoesNotContain(invalid.Sent, message => TypeOf(message) == "hello");
    }

    [Theory]
    [InlineData("pack")]
    [InlineData("content")]
    [InlineData("season_content")]
    public void PackOrContentDifferencesRemainHandshakeErrorsInRulesetMode(string difference)
    {
        var a = OpenSeasonDb();
        var b = difference == "pack" ? BattleTests.LoadPack("fan_asoul_vr") : OpenSeasonDb();
        if (difference == "content") b.Skills["dig_history"].Power++;
        if (difference == "season_content") b.Seasons["open"].Name += "不同";
        var pair = new Pair(Rule(), hostDb: a, guestDb: b);
        pair.Pump();
        AssertRejected(pair);
    }

    [Fact]
    public void SourceRulesAndExposedSnapshotsCannotMutateTheOngoingMatch()
    {
        var rule = Rule(3, true, true);
        rule.Modifiers.Add("half_pp");
        var pair = new Pair(rule);
        string expected = rule.ToJson();
        rule.BestOf = 1;
        rule.BanOne = false;
        rule.Modifiers.Clear();
        pair.Start();
        var exposed = pair.Host.Ruleset;
        exposed.BestOf = 5;
        exposed.Modifiers.Add("hp_x2");
        Assert.Equal(expected, pair.Host.Ruleset.ToJson());
        Assert.Equal(expected, pair.Guest.Ruleset.ToJson());
        Assert.Equal(pair.Host.RulesetHash, pair.Guest.RulesetHash);
        pair.Pregame();
        pair.FinishGame(0);
        var result = pair.Host.GameResults[0];
        string saved = result.Record.ToJson();
        result.Record.Events.Clear();
        result.Battle.Active(0).Hp = 1;
        var wins = Assert.IsAssignableFrom<IList<int>>(pair.Host.Wins);
        Assert.Throws<NotSupportedException>(() => wins[0] = 99);
        Assert.Equal(saved, pair.Host.GameResults[0].Record.ToJson());
        Assert.Equal(result.StateHash, pair.Host.GameResults[0].Battle.StateHash());
        Assert.Equal(new[] { 1, 0 }, pair.Host.Wins);
        var results = Assert.IsAssignableFrom<IList<NetMatchGameResult>>(pair.Host.GameResults);
        Assert.Throws<NotSupportedException>(() => results.Clear());
    }

    [Fact]
    public void BanAndLeadSelectionsStayPrivateUntilBothCommitAndThenResolveInFixedSideOrder()
    {
        var pair = new Pair(Rule(1, true, true));
        pair.Start();
        Assert.Equal(NetState.Ban, pair.Host.State);
        Assert.Equal(NetState.Ban, pair.Guest.State);
        string before = pair.Host.Battle.Record.ToJson();
        ulong hash = pair.Host.Battle.StateHash();
        pair.Host.SubmitBan(2);
        pair.Pump();
        Assert.Equal(before, pair.Host.Battle.Record.ToJson());
        Assert.Equal(before, pair.Guest.Battle.Record.ToJson());
        Assert.Equal(hash, pair.Host.Battle.StateHash());
        Assert.False(pair.Host.Battle.HasChosenBan(0));
        Assert.False(pair.Guest.Battle.HasChosenBan(0));
        Assert.DoesNotContain(pair.HostWire.Sent, message => IsSelection(message, "reveal", "ban"));
        using (var commit = JsonDocument.Parse(pair.HostWire.Sent.Last(message => IsSelection(message, "commit", "ban"))))
        {
            Assert.False(commit.RootElement.TryGetProperty("action", out _));
            Assert.False(commit.RootElement.TryGetProperty("to", out _));
            Assert.False(commit.RootElement.TryGetProperty("nonce", out _));
        }
        pair.Guest.SubmitBan(4);
        pair.PumpUntil(() => pair.Host.State == NetState.Lead && pair.Guest.State == NetState.Lead);
        Assert.All(pair.Host.Battle.Sides, side => Assert.Equal(5, side.Team.Count));
        Assert.DoesNotContain(pair.Host.Battle.Sides[0].Team, fighter => fighter.Def.Id == HostTeam[4]);
        Assert.DoesNotContain(pair.Host.Battle.Sides[1].Team, fighter => fighter.Def.Id == GuestTeam[2]);
        Assert.Equal(new[] { 0, 1 }, pair.Host.Battle.Record.Events.Where(item => item.Type == BattleRecordEventType.Ban).Select(item => item.Side));
        string lead = pair.Host.Battle.Sides[0].Team[3].Def.Id;
        before = pair.Host.Battle.Record.ToJson();
        pair.Host.SubmitLead(3);
        pair.Pump();
        Assert.Equal(before, pair.Host.Battle.Record.ToJson());
        Assert.Equal(before, pair.Guest.Battle.Record.ToJson());
        Assert.False(pair.Guest.Battle.HasChosenLead(0));
        Assert.DoesNotContain(pair.HostWire.Sent, message => IsSelection(message, "reveal", "lead"));
        pair.Guest.SubmitLead(1);
        pair.PumpUntil(() => pair.Host.State == NetState.Choose && pair.Guest.State == NetState.Choose);
        Assert.Equal(lead, pair.Host.Battle.Active(0).Def.Id);
        Assert.Equal(lead, pair.Guest.Battle.Active(0).Def.Id);
        Assert.Equal(pair.Host.Battle.StateHash(), pair.Guest.Battle.StateHash());
        Assert.Equal(new[] { 0, 1 }, pair.Host.Battle.Record.Events.Where(item => item.Type == BattleRecordEventType.Lead).Select(item => item.Side));
    }

    [Fact]
    public void InvalidPrivateChoicesDoNotCommitOrChangeTheBattle()
    {
        var pair = new Pair(Rule(1, true, true));
        pair.Start();
        string before = pair.Host.Battle.Record.ToJson();
        int messages = pair.HostWire.Sent.Count;
        Assert.Throws<InvalidOperationException>(() => pair.Host.SubmitLead(0));
        Assert.Throws<InvalidOperationException>(() => pair.Host.Submit(BattleAction.Pass()));
        Assert.Throws<InvalidOperationException>(() => pair.Host.SubmitBan(6));
        Assert.False(pair.Host.HasSubmitted);
        Assert.Equal(messages, pair.HostWire.Sent.Count);
        Assert.Equal(before, pair.Host.Battle.Record.ToJson());
        pair.Host.SubmitBan(0);
        Assert.Throws<InvalidOperationException>(() => pair.Host.SubmitBan(1));
        pair.Guest.SubmitBan(0);
        pair.PumpUntil(() => pair.Host.State == NetState.Lead && pair.Guest.State == NetState.Lead);
        Assert.Throws<InvalidOperationException>(() => pair.Host.SubmitLead(5));
        Assert.False(pair.Host.HasSubmitted);
    }

    [Fact]
    public void AllModifiersAreAppliedToBothPeersAndEachNewGameStartsFresh()
    {
        var rule = Rule(3, true, true);
        rule.Modifiers = BattleModifiers.All.Select(item => item.Id).ToList();
        var pair = new Pair(rule);
        pair.Start();
        string content = pair.HostDb.ContentHash();
        for (int game = 0; game < 2; game++)
        {
            foreach (var session in new[] { pair.Host, pair.Guest })
            {
                Assert.Equal(rule.Modifiers.OrderBy(id => id, StringComparer.Ordinal), session.Battle.Modifiers);
                foreach (var fighter in session.Battle.Sides.SelectMany(side => side.Team))
                {
                    Assert.Equal(fighter.Def.Stats["hp"] * 2, fighter.MaxHp);
                    Assert.Equal(fighter.MaxHp, fighter.Hp);
                    foreach (var slot in fighter.Skills)
                    {
                        int original = session.Battle.Db.Skills[slot.Id].Pp;
                        Assert.Equal(original / 2 + original % 2, slot.Max);
                        Assert.Equal(slot.Max, slot.Pp);
                    }
                }
            }
            pair.Pregame();
            foreach (var session in new[] { pair.Host, pair.Guest })
                for (int side = 0; side < 2; side++)
                {
                    Assert.Empty(session.Battle.LegalFor(side).Switches);
                    Assert.Equal(1, session.Battle.Active(side).FieldOf("hype").Stacks);
                }
            pair.PassTurn();
            pair.FinishGame(game);
        }
        Assert.Equal(content, pair.HostDb.ContentHash());
        Assert.Equal(Phase.Ban, pair.Host.Battle.Phase);
        Assert.All(pair.Host.Battle.Sides, side => Assert.Equal(6, side.Team.Count));
    }

    [Fact]
    public void RulesetClockPausesPrivateChoicesAndRestoresTheWholeBudgetForTheNextGame()
    {
        var rule = Rule(3, true, true);
        rule.TurnSeconds = 10;
        rule.BankSeconds = 30;
        var pair = new Pair(rule);
        pair.Start();
        Assert.NotNull(pair.Host.Clock);
        pair.Host.Tick(100);
        pair.Guest.Tick(100);
        pair.AssertHealthy();
        Assert.False(pair.Host.HasSubmitted);
        Assert.False(pair.Guest.HasSubmitted);
        Assert.Equal(30, pair.Host.Clock.BankLeft(0));
        pair.Host.SubmitBan(5);
        pair.Guest.SubmitBan(5);
        pair.PumpUntil(() => pair.Host.State == NetState.Lead && pair.Guest.State == NetState.Lead);
        pair.Host.Tick(100);
        pair.Guest.Tick(100);
        pair.AssertHealthy();
        Assert.False(pair.Host.HasSubmitted);
        pair.Pregame();
        foreach (var session in new[] { pair.Host, pair.Guest })
        {
            Assert.Equal(10, session.Clock.TurnLeft(session.MySide));
            Assert.Equal(30, session.Clock.BankLeft(session.MySide));
            session.Tick(12);
            Assert.Equal(0, session.Clock.TurnLeft(session.MySide));
            Assert.Equal(28, session.Clock.BankLeft(session.MySide));
        }
        pair.PassTurn();
        Assert.Equal(10, pair.Host.Clock.TurnLeft(0));
        Assert.Equal(28, pair.Host.Clock.BankLeft(0));
        pair.FinishGame(0);
        pair.Pregame();
        Assert.Equal(10, pair.Host.Clock.TurnLeft(0));
        Assert.Equal(30, pair.Host.Clock.BankLeft(0));
        Assert.Equal(30, pair.Guest.Clock.BankLeft(1));
    }

    [Fact]
    public void ZeroClockConfigurationDoesNotImmediatelyAutoSubmit()
    {
        var pair = new Pair(Rule());
        pair.Start();
        pair.Host.Tick(1000000);
        pair.Guest.Tick(1000000);
        pair.AssertHealthy();
        Assert.Equal(NetState.Choose, pair.Host.State);
        Assert.False(pair.Host.HasSubmitted);
        Assert.Equal(1, pair.Host.Battle.Turn);
    }

    [Fact]
    public void UntimedPeerOnlyReplacementStillTimesOutWhenItsRevealNeverArrives()
    {
        var pair = new Pair(Rule());
        pair.Start();
        for (int step = 0; step < 2000 && !(pair.Host.State == NetState.Replace ^ pair.Guest.State == NetState.Replace); step++)
        {
            foreach (var session in new[] { pair.Host, pair.Guest })
            {
                if (session.HasSubmitted) continue;
                if (session.State == NetState.Choose) session.Submit(Ai.Choose(session.Battle, session.MySide));
                else if (session.State == NetState.Replace) session.SubmitReplace(Ai.ChooseReplacement(session.Battle, session.MySide));
            }
            pair.Pump();
            pair.AssertHealthy();
        }
        Assert.True(pair.Host.State == NetState.Replace ^ pair.Guest.State == NetState.Replace,
            "真实对局必须产生仅一方需要补位的阶段。");
        var replacing = pair.Host.State == NetState.Replace ? pair.Host : pair.Guest;
        var waiting = replacing == pair.Host ? pair.Guest : pair.Host;
        var replacingWire = replacing == pair.Host ? pair.HostWire : pair.GuestWire;
        var waitingWire = waiting == pair.Host ? pair.HostWire : pair.GuestWire;
        Assert.Null(waiting.Clock);
        Assert.Equal(NetState.Waiting, waiting.State);
        Assert.False(waiting.HasSubmitted);
        string record = waiting.Battle.Record.ToJson();
        ulong hash = waiting.Battle.StateHash();
        int sentBefore = waitingWire.Sent.Count;
        // 同时隔离后续终止屏障，确保超时来自补位公开等待本身。
        replacingWire.Hold = message => IsSelection(message, "reveal", "replace")
            || TypeOf(message).StartsWith("termination-", StringComparison.Ordinal);
        replacing.SubmitReplace(Ai.ChooseReplacement(replacing.Battle, replacing.MySide));
        pair.Pump();
        pair.AssertHealthy();
        Assert.Single(replacingWire.Held, message => IsSelection(message, "reveal", "replace"));
        Assert.DoesNotContain(waitingWire.Sent.Skip(sentBefore), message => IsSelection(message, "commit", "replace"));
        Assert.False(waiting.HasSubmitted);
        waiting.Tick(9.999);
        Assert.Equal(NetState.Waiting, waiting.State);
        waiting.Tick(0.001);
        Assert.Equal(NetState.Error, waiting.State);
        Assert.Contains("10 秒", waiting.Error);
        Assert.False(waiting.MatchConfirmed);
        Assert.Empty(waiting.GameResults);
        Assert.Equal(new[] { 0, 0 }, waiting.Wins);
        Assert.Equal(record, waiting.Battle.Record.ToJson());
        Assert.Equal(hash, waiting.Battle.StateHash());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExplicitCasualCompatibilityWorksWithAnUnmodifiedLegacyPeer(bool termination)
    {
        LoopbackPair.Create(out var a, out var b);
        var db = BattleTests.LoadPack("original");
        var host = new NetSession(db, a, true, HostTeam, "主机", 128, enableTermination: termination,
            ruleset: Rulesets.All.Single(item => item.Id == "casual"), legacyCasual: true);
        var guest = new NetSession(db, b, false, GuestTeam, "客机", null, enableTermination: termination);
        for (int step = 0; step < 5000 && (host.State != NetState.Over || guest.State != NetState.Over); step++)
        {
            host.Poll();
            guest.Poll();
            foreach (var session in new[] { host, guest })
            {
                Assert.DoesNotContain(session.State, new[] { NetState.Error, NetState.Desync });
                if (session.State == NetState.Choose && !session.HasSubmitted) session.Submit(Ai.Choose(session.Battle, session.MySide));
                else if (session.State == NetState.Replace && !session.HasSubmitted) session.SubmitReplace(Ai.ChooseReplacement(session.Battle, session.MySide));
            }
        }
        Assert.Equal(NetState.Over, host.State);
        Assert.Equal(NetState.Over, guest.State);
        Assert.True(host.MatchConfirmed);
        Assert.Single(host.GameResults);
        Assert.Equal(host.Battle.StateHash(), guest.Battle.StateHash());
        Assert.Equal(termination ? NetSession.TerminationProtocolVersion : NetSession.ProtocolVersion, host.SessionProtocolVersion);
    }

    [Theory]
    [InlineData("ranked")]
    [InlineData("chaos")]
    public void NonCasualRulesCannotBeSilentlyDowngradedToLegacy(string preset)
    {
        LoopbackPair.Create(out var a, out _);
        var host = new NetSession(BattleTests.LoadPack("original"), a, true, HostTeam, "主机", 128,
            ruleset: Rulesets.All.Single(item => item.Id == preset), legacyCasual: true);
        Assert.Equal(NetState.Error, host.State);
        Assert.Null(host.Battle);
        AssertChinese(host.Error);
    }

    [Fact]
    public void ADesyncInTheSecondGameInvalidatesTheEntireMatchRating()
    {
        using var directory = new TestDirectory();
        var store = new FileSaveStore(directory.Root);
        var pair = new Pair(Rule(3));
        pair.Start();
        Assert.Throws<InvalidOperationException>(() => store.RecordMatchProfile(pair.Host));
        pair.FinishGame(0);
        Assert.Equal(new[] { 1, 0 }, pair.Host.Wins);
        pair.Host.Battle.Active(0).Hp--;
        pair.Host.Submit(BattleAction.Pass());
        pair.Guest.Submit(BattleAction.Pass());
        pair.Pump();
        Assert.Contains(new[] { pair.Host.State, pair.Guest.State }, state => state == NetState.Desync);
        Assert.False(pair.Host.MatchConfirmed);
        Assert.False(pair.Guest.MatchConfirmed);
        Assert.Single(pair.Host.GameResults);
        Assert.Throws<InvalidOperationException>(() => store.RecordMatchProfile(pair.Host));
        Assert.Throws<InvalidOperationException>(() => store.RecordMatchProfile(pair.Guest));
        Assert.False(File.Exists(Path.Combine(directory.Root, "profile.json")));
    }

    [Fact]
    public void ACompletedMatchRecordsEachGameButAppliesRatingOnlyOnceAcrossAllStores()
    {
        using var directory = new TestDirectory();
        var store = new FileSaveStore(Path.Combine(directory.Root, "host"));
        var guestStore = new FileSaveStore(Path.Combine(directory.Root, "guest"));
        var pair = new Pair(Rule(3));
        pair.Start();
        pair.FinishGame(0);
        Assert.Throws<InvalidOperationException>(() => store.RecordMatchProfile(pair.Host));
        pair.FinishGame(1);
        Assert.Throws<InvalidOperationException>(() => store.RecordMatchProfile(pair.Host));
        pair.FinishGame(0);
        string opponent = pair.Host.OpponentRating.ToJson();
        var hostProfile = store.RecordMatchProfile(pair.Host);
        var guestProfile = guestStore.RecordMatchProfile(pair.Guest);
        Assert.Equal(1, hostProfile.Ratings[Profile.DefaultRatingFormat].Games);
        Assert.Equal(1520, hostProfile.Ratings[Profile.DefaultRatingFormat].Rating);
        Assert.Equal(1, guestProfile.Ratings[Profile.DefaultRatingFormat].Games);
        Assert.Equal(1480, guestProfile.Ratings[Profile.DefaultRatingFormat].Rating);
        Assert.Equal(2, hostProfile.Packs["original"].Wins);
        Assert.Equal(1, hostProfile.Packs["original"].Losses);
        Assert.Equal(1, guestProfile.Packs["original"].Wins);
        Assert.Equal(2, guestProfile.Packs["original"].Losses);
        Assert.All(HostTeam, id => Assert.Equal(3, hostProfile.Packs["original"].Picks[id]));
        Assert.Equal(opponent, pair.Host.OpponentRating.ToJson());
        string before = store.LoadProfile().ToJson();
        Assert.Throws<InvalidOperationException>(() => store.RecordMatchProfile(pair.Host));
        var other = new FileSaveStore(Path.Combine(directory.Root, "other"));
        Assert.Throws<InvalidOperationException>(() => other.RecordMatchProfile(pair.Host));
        Assert.Equal(before, store.LoadProfile().ToJson());
        Assert.Empty(other.LoadProfile().Ratings);
    }

    [Fact]
    public void MatchRatingUsesTheWholeMatchWinnerEvenWhenThatPlayerLosesTheLastGame()
    {
        using var directory = new TestDirectory();
        var pair = new Pair(Rule(5));
        pair.Start();
        foreach (int winner in new[] { 0, 0, -1, -1, 1 }) pair.FinishGame(winner);
        Assert.Equal(new[] { 2, 1 }, pair.Host.Wins);
        Assert.Equal(2, pair.Host.Draws);
        Assert.Equal(1, pair.Host.GameResults.Last().Winner);
        Assert.Equal(0, pair.Host.MatchWinner);
        var profile = new FileSaveStore(directory.Root).RecordMatchProfile(pair.Host);
        Assert.Equal(1520, profile.Ratings[Profile.DefaultRatingFormat].Rating);
        Assert.Equal(1, profile.Ratings[Profile.DefaultRatingFormat].Games);
        Assert.Equal(2, profile.Packs["original"].Draws);
    }

    [Fact]
    public void ADamagedProfileIsPreservedAndAFailedSaveCanBeRetriedExactlyOnce()
    {
        using var directory = new TestDirectory();
        var store = new FileSaveStore(directory.Root);
        var pair = new Pair(Rule());
        pair.Start();
        pair.FinishGame(0);
        string path = Path.Combine(directory.Root, "profile.json");
        const string damaged = "{\"packs\":{},\"ratings\":{\"默认\":{\"games\":-1}}}";
        File.WriteAllText(path, damaged);
        Assert.Throws<FormatException>(() => store.RecordMatchProfile(pair.Host));
        Assert.Equal(damaged, File.ReadAllText(path));
        store.SaveProfile(new Profile());
        using (var locked = new FileStream(Path.Combine(directory.Root, ".profile.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            Assert.ThrowsAny<IOException>(() => store.RecordMatchProfile(pair.Host));
        Assert.Empty(store.LoadProfile().Ratings);
        Assert.Empty(store.LoadProfile().Packs);
        var profile = store.RecordMatchProfile(pair.Host);
        Assert.Equal(1, profile.Ratings[Profile.DefaultRatingFormat].Games);
        Assert.Equal(1, profile.Packs["original"].Wins);
        Assert.Throws<InvalidOperationException>(() => store.RecordMatchProfile(pair.Host));
    }

    [Fact]
    public void NeitherPeerScoresAnEndedBattleUntilItsFinalHashHasBeenConfirmed()
    {
        using var directory = new TestDirectory();
        var pair = new Pair(Rule());
        pair.Start();
        pair.HostWire.Hold = message => TypeOf(message) == "hash";
        pair.GuestWire.Hold = message => TypeOf(message) == "hash";
        pair.Guest.Surrender();
        pair.Pump();
        AssertHealthyUnconfirmed(pair);
        Assert.Equal(Phase.Over, pair.Host.Battle.Phase);
        Assert.Equal(Phase.Over, pair.Guest.Battle.Phase);
        Assert.Single(pair.HostWire.Held);
        Assert.Single(pair.GuestWire.Held);
        Assert.Empty(pair.Host.GameResults);
        Assert.Empty(pair.Guest.GameResults);
        Assert.Equal(new[] { 0, 0 }, pair.Host.Wins);
        Assert.Equal(new[] { 0, 0 }, pair.Guest.Wins);
        Assert.Throws<InvalidOperationException>(() => new FileSaveStore(directory.Root).RecordMatchProfile(pair.Host));
        pair.HostWire.Hold = pair.GuestWire.Hold = null;
        pair.HostWire.Release();
        pair.GuestWire.Release();
        pair.PumpUntil(() => pair.Host.MatchConfirmed && pair.Guest.MatchConfirmed);
        Assert.Equal(new[] { 1, 0 }, pair.Host.Wins);
        Assert.Single(pair.Host.GameResults);
    }

    [Fact]
    public void ConfirmedIndividualGamesStillCannotBeRatedBeforeTheFinalMatchBarrier()
    {
        using var directory = new TestDirectory();
        var pair = new Pair(Rule());
        pair.Start();
        pair.HostWire.Hold = message => TypeOf(message) == "match-end";
        pair.GuestWire.Hold = message => TypeOf(message) == "match-end";
        pair.Guest.Surrender();
        pair.Pump();
        AssertHealthyUnconfirmed(pair);
        Assert.Single(pair.Host.GameResults);
        Assert.Single(pair.Guest.GameResults);
        Assert.Equal(new[] { 1, 0 }, pair.Host.Wins);
        Assert.Single(pair.HostWire.Held);
        Assert.Single(pair.GuestWire.Held);
        var store = new FileSaveStore(directory.Root);
        Assert.Throws<InvalidOperationException>(() => store.RecordMatchProfile(pair.Host));
        Assert.False(File.Exists(Path.Combine(directory.Root, "profile.json")));
        pair.HostWire.Hold = pair.GuestWire.Hold = null;
        pair.HostWire.Release();
        pair.GuestWire.Release();
        pair.PumpUntil(() => pair.Host.MatchConfirmed && pair.Guest.MatchConfirmed);
        Assert.Equal(1, store.RecordMatchProfile(pair.Host).Ratings[Profile.DefaultRatingFormat].Games);
    }

    [Theory]
    [InlineData("hello_hash")]
    [InlineData("hello_rules")]
    [InlineData("ready_number")]
    [InlineData("ready_seed")]
    [InlineData("ready_hash")]
    [InlineData("result_score")]
    [InlineData("result_winner")]
    [InlineData("match_digest")]
    public void CorruptedRuleGameOrFinalScoreMessagesCannotProduceARateableMatch(string corruption)
    {
        bool changed = false;
        var pair = new Pair(Rule(), configure: (host, guest) => host.Change = message =>
        {
            var packet = JsonNode.Parse(message).AsObject();
            string type = packet["type"].GetValue<string>();
            bool target = corruption.StartsWith("hello_", StringComparison.Ordinal) && type == "hello"
                || corruption.StartsWith("ready_", StringComparison.Ordinal) && type == "game-ready"
                || corruption.StartsWith("result_", StringComparison.Ordinal) && type == "game-result"
                || corruption == "match_digest" && type == "match-end";
            if (!target) return message;
            changed = true;
            if (corruption == "hello_hash") packet["rulesetHash"] = ChangedHex(packet["rulesetHash"].GetValue<string>());
            else if (corruption == "hello_rules") packet["ruleset"]["bestOf"] = 3;
            else if (corruption == "ready_number") packet["game"] = 2;
            else if (corruption == "ready_seed") packet["seed"] = 129;
            else if (corruption == "ready_hash") packet["hash"] = ChangedHex(packet["hash"].GetValue<string>());
            else if (corruption == "result_score") packet["wins"][0] = 4;
            else if (corruption == "result_winner") packet["winner"] = 1;
            else packet["digest"] = ChangedHex(packet["digest"].GetValue<string>());
            return packet.ToJsonString();
        });
        bool surrendered = false;
        for (int step = 0; step < 200 && !Faulted(pair.Host) && !Faulted(pair.Guest); step++)
        {
            pair.Pump(1);
            if (!surrendered && pair.Guest.CanSurrender)
            {
                pair.Guest.Surrender();
                surrendered = true;
            }
        }
        pair.Pump();
        Assert.True(changed);
        Assert.True(Faulted(pair.Host) || Faulted(pair.Guest));
        Assert.False(pair.Host.MatchConfirmed);
        Assert.False(pair.Guest.MatchConfirmed);
        AssertChinese(pair.Host.Error ?? pair.Guest.Error);
    }

    [Fact]
    public void APreviousGamesHashCannotConfirmOrScoreTheCurrentGame()
    {
        var pair = new Pair(Rule(3));
        pair.Start();
        pair.FinishGame(0);
        string old = pair.GuestWire.Sent.First(message => TypeOf(message) == "hash");
        string current = pair.Host.Battle.Record.ToJson();
        pair.GuestWire.InjectToPeer(old);
        pair.Pump();
        Assert.Equal(NetState.Error, pair.Host.State);
        Assert.False(pair.Host.MatchConfirmed);
        Assert.Equal(current, pair.Host.Battle.Record.ToJson());
        Assert.Equal(new[] { 1, 0 }, pair.Host.Wins);
        Assert.Single(pair.Host.GameResults);
    }

    [Fact]
    public void WaitingForTheNextGameHandshakeTimesOutWithoutChargingGameTime()
    {
        var pair = new Pair(Rule(), configure: (host, guest) =>
        {
            host.Hold = message => TypeOf(message) == "game-ready";
            guest.Hold = message => TypeOf(message) == "game-ready";
        });
        pair.Pump();
        AssertHealthyUnconfirmed(pair);
        Assert.Equal(NetState.Waiting, pair.Host.State);
        Assert.Throws<InvalidOperationException>(() => pair.Host.Submit(BattleAction.Pass()));
        pair.Host.Tick(9.999);
        Assert.Equal(NetState.Waiting, pair.Host.State);
        pair.Host.Tick(0.001);
        Assert.Equal(NetState.Error, pair.Host.State);
        Assert.Empty(pair.Host.GameResults);
        Assert.Equal(1, pair.Host.Battle.Turn);
        Assert.False(pair.Host.MatchConfirmed);
    }

    [Fact]
    public void ALatePeerErrorRevokesConfirmationBeforeRatingIsSaved()
    {
        using var directory = new TestDirectory();
        var pair = new Pair(Rule());
        pair.Start();
        pair.FinishGame(0);
        Assert.True(pair.Host.MatchConfirmed);
        pair.GuestWire.InjectToPeer("{\"type\":\"error\",\"message\":\"终局复核失败\"}");
        pair.Host.Poll();
        Assert.Equal(NetState.Error, pair.Host.State);
        Assert.False(pair.Host.MatchConfirmed);
        Assert.Throws<InvalidOperationException>(() => new FileSaveStore(directory.Root).RecordMatchProfile(pair.Host));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnEmptyTerminationBarrierRestoresAnUnsubmittedBanOrLeadPhase(bool lead)
    {
        var pair = new Pair(Rule(1, true, true));
        pair.Start();
        if (lead)
        {
            pair.Host.SubmitBan(5);
            pair.Guest.SubmitBan(5);
            pair.PumpUntil(() => pair.Host.State == NetState.Lead && pair.Guest.State == NetState.Lead);
        }
        var expected = lead ? NetState.Lead : NetState.Ban;
        string record = pair.Host.Battle.Record.ToJson();
        ulong hash = pair.Host.Battle.StateHash();
        pair.GuestWire.InjectToPeer("{\"type\":\"termination-wake\",\"phase\":" + (lead ? "2" : "1") + ",\"turn\":1,\"game\":1}");
        pair.Pump();
        pair.AssertHealthy();
        Assert.Equal(expected, pair.Host.State);
        Assert.Equal(expected, pair.Guest.State);
        Assert.False(pair.Host.HasSubmitted);
        Assert.False(pair.Guest.HasSubmitted);
        Assert.Equal(record, pair.Host.Battle.Record.ToJson());
        Assert.Equal(record, pair.Guest.Battle.Record.ToJson());
        Assert.Equal(hash, pair.Host.Battle.StateHash());
        pair.Pregame();
        Assert.Equal(NetState.Choose, pair.Host.State);
        Assert.Equal(NetState.Choose, pair.Guest.State);
    }

    [Fact]
    public void ProtocolSamplesMatchRealMessagesAgainstFrozenContentAndFixedCommitmentNonces()
    {
        string directory = Path.Combine(SourceDirectory(), "Golden");
        ContentDb LoadFrozen()
        {
            return ContentDb.FromJson(File.ReadAllText(Path.Combine(directory, "replays", "content", "StreamDuelRules.json")),
                File.ReadAllText(Path.Combine(directory, "replays", "content", "original.json")));
        }
        var rule = Rule(1, true, true);
        rule.Id = "t128-fixed";
        rule.Name = "协议固定样例";
        rule.Modifiers.Add("half_pp");
        rule.TurnSeconds = 10;
        rule.BankSeconds = 30;
        var pair = new Pair(rule, hostDb: LoadFrozen(), guestDb: LoadFrozen());
        pair.Start();
        pair.Pregame();
        pair.FinishGame(0);
        var samples = new Dictionary<string, string>
        {
            ["hello-host"] = Assert.Single(pair.HostWire.Sent, message => TypeOf(message) == "hello"),
            ["hello-guest"] = Assert.Single(pair.GuestWire.Sent, message => TypeOf(message) == "hello")
        };
        foreach (string type in new[] { "ruleset-ready", "game-ready", "game-result", "match-end" })
            samples[type] = Assert.Single(pair.HostWire.Sent, message => TypeOf(message) == type);
        foreach (string kind in new[] { "ban", "lead" })
            foreach (string type in new[] { "commit", "reveal" })
                samples[type + "-" + kind] = Assert.Single(pair.HostWire.Sent, message => IsSelection(message, type, kind));
        foreach (string kind in new[] { "ban", "lead" })
        {
            var commit = JsonNode.Parse(samples["commit-" + kind]);
            var reveal = JsonNode.Parse(samples["reveal-" + kind]);
            string action = reveal["action"].GetValue<string>();
            Assert.Equal(kind == "ban" ? "W5" : "W0", action);
            string digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(action + "|" + reveal["nonce"].GetValue<string>()))).ToLowerInvariant();
            Assert.Equal(digest, commit["digest"].GetValue<string>());
        }
        const string nonce = "000102030405060708090a0b0c0d0e0f";
        foreach (var sample in samples)
        {
            var actual = JsonNode.Parse(sample.Value).AsObject();
            string type = actual["type"].GetValue<string>();
            if (type == "reveal") actual["nonce"] = nonce;
            if (type == "commit")
            {
                string action = actual["kind"].GetValue<string>() == "ban" ? "W5" : "W0";
                actual["digest"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(action + "|" + nonce))).ToLowerInvariant();
            }
            string file = Path.Combine(directory, "net", "t128-" + sample.Key + ".json");
            var expected = JsonNode.Parse(File.ReadAllText(file));
            Assert.True(JsonNode.DeepEquals(expected, actual), "固定协议样例不一致：" + sample.Key);
        }
        Assert.Equal(10, samples.Count);
        Assert.Equal("63a758a4fa663be9", pair.HostDb.ContentHash());
    }

    static string SourceDirectory([CallerFilePath] string file = "") { return Path.GetDirectoryName(file); }

    static string ChangedHex(string text) { return (text[0] == '0' ? "1" : "0") + text.Substring(1); }

    static bool Faulted(NetSession session) { return session.State == NetState.Error || session.State == NetState.Desync; }

    static void AssertHealthyUnconfirmed(Pair pair)
    {
        pair.AssertHealthy();
        Assert.False(pair.Host.MatchConfirmed);
        Assert.False(pair.Guest.MatchConfirmed);
    }

    static Ruleset Rule(int bestOf = 1, bool ban = false, bool lead = false)
    {
        return new Ruleset { Id = "net-test", Name = "联网测试赛制", BestOf = bestOf, BanOne = ban, LeadPick = lead, Modifiers = new List<string>() };
    }

    static Ruleset Copy(Ruleset source) { return Ruleset.FromJson(source.ToJson()); }

    static ContentDb OpenSeasonDb()
    {
        var db = BattleTests.LoadPack("original");
        db.Seasons["open"] = new SeasonDef { Name = "开放赛季" };
        return db;
    }

    static void AssertChinese(string text)
    {
        Assert.False(string.IsNullOrEmpty(text));
        Assert.Contains(text, character => character >= '\u4e00' && character <= '\u9fff');
    }

    static void AssertRejected(Pair pair)
    {
        Assert.Contains(new[] { pair.Host.State, pair.Guest.State }, state => state == NetState.Error);
        Assert.False(pair.Host.MatchConfirmed);
        Assert.False(pair.Guest.MatchConfirmed);
        Assert.Empty(pair.Host.GameResults);
        Assert.Empty(pair.Guest.GameResults);
        AssertChinese(pair.Host.Error ?? pair.Guest.Error);
    }

    static string TypeOf(string message)
    {
        using var document = JsonDocument.Parse(message);
        return document.RootElement.GetProperty("type").GetString();
    }

    static bool IsSelection(string message, string type, string kind)
    {
        using var document = JsonDocument.Parse(message);
        return document.RootElement.GetProperty("type").GetString() == type
            && document.RootElement.TryGetProperty("kind", out var value) && value.GetString() == kind;
    }

    sealed class Wire : INetTransport
    {
        readonly INetTransport inner;
        public readonly List<string> Sent = new List<string>();
        public readonly List<string> Held = new List<string>();
        public Func<string, bool> Hold;
        public Func<string, string> Change;

        public Wire(INetTransport inner) { this.inner = inner; }

        public void Send(string message)
        {
            Sent.Add(message);
            if (Hold != null && Hold(message)) { Held.Add(message); return; }
            inner.Send(Change == null ? message : Change(message));
        }

        public bool TryReceive(out string message) { return inner.TryReceive(out message); }
        public void InjectToPeer(string message) { inner.Send(message); }
        public void Release()
        {
            var messages = Held.ToArray();
            Held.Clear();
            foreach (string message in messages) inner.Send(message);
        }
    }

    sealed class Pair
    {
        public readonly ContentDb HostDb, GuestDb;
        public readonly Wire HostWire, GuestWire;
        public readonly NetSession Host, Guest;

        public Pair(Ruleset hostRule, Ruleset guestRule = null, ContentDb hostDb = null, ContentDb guestDb = null,
            uint seed = 128, Action<Wire, Wire> configure = null)
        {
            HostDb = hostDb ?? BattleTests.LoadPack("original");
            GuestDb = guestDb ?? BattleTests.LoadPack("original");
            LoopbackPair.Create(out var a, out var b);
            HostWire = new Wire(a);
            GuestWire = new Wire(b);
            configure?.Invoke(HostWire, GuestWire);
            Host = new NetSession(HostDb, HostWire, true, HostTeam, "主机", seed, ruleset: hostRule);
            var team = GuestDb.PackId == "original" ? GuestTeam : GuestDb.Streamers.Take(6).Select(item => item.Id).ToArray();
            Guest = new NetSession(GuestDb, GuestWire, false, team, "客机", null, ruleset: guestRule ?? Copy(hostRule));
        }

        public void Pump(int count = 30)
        {
            for (int i = 0; i < count; i++) { Host.Poll(); Guest.Poll(); }
        }

        public void AssertHealthy()
        {
            Assert.True(Host.State != NetState.Error && Host.State != NetState.Desync, "主机：" + Host.Error);
            Assert.True(Guest.State != NetState.Error && Guest.State != NetState.Desync, "客机：" + Guest.Error);
        }

        public void PumpUntil(Func<bool> complete)
        {
            for (int i = 0; i < 300 && !complete(); i++) { Pump(1); AssertHealthy(); }
            Assert.True(complete(), "联网流程应在有限次消息传递后完成。");
        }

        public void Start()
        {
            PumpUntil(() => Host.Battle != null && Guest.Battle != null
                && Host.State != NetState.Handshake && Guest.State != NetState.Handshake
                && Host.State != NetState.Waiting && Guest.State != NetState.Waiting);
            AssertHealthy();
        }

        public void Pregame()
        {
            for (int step = 0; step < 300; step++)
            {
                Pump(1);
                AssertHealthy();
                if (Host.State == NetState.Choose && Guest.State == NetState.Choose) return;
                foreach (var session in new[] { Host, Guest })
                {
                    if (session.HasSubmitted) continue;
                    if (session.State == NetState.Ban) session.SubmitBan(5);
                    else if (session.State == NetState.Lead) session.SubmitLead(0);
                }
            }
            Assert.Fail("禁用和首发阶段未结束。");
        }

        public void PassTurn()
        {
            Pregame();
            int turn = Host.Battle.Turn;
            Host.Submit(BattleAction.Pass());
            Guest.Submit(BattleAction.Pass());
            PumpUntil(() => Host.Battle.Turn > turn && Guest.Battle.Turn > turn
                && Host.State == NetState.Choose && Guest.State == NetState.Choose);
            Assert.Equal(Host.Battle.StateHash(), Guest.Battle.StateHash());
        }

        public void FinishGame(int winner)
        {
            Pregame();
            int completed = Host.GameResults.Count;
            if (winner >= 0) (winner == 0 ? Guest : Host).Surrender();
            else
            {
                Host.OfferDraw();
                PumpUntil(() => Guest.CanRespondDraw);
                Guest.RespondDraw(true);
            }
            PumpUntil(() => Host.GameResults.Count == completed + 1 && Guest.GameResults.Count == completed + 1);
            Pump();
            AssertHealthy();
            Assert.Equal(winner, Host.GameResults[completed].Winner);
            Assert.Equal(Host.GameResults[completed].StateHash, Guest.GameResults[completed].StateHash);
        }
    }

    sealed class TestDirectory : IDisposable
    {
        readonly string parent;
        readonly string name = "T128-" + Guid.NewGuid().ToString("N");
        public string Root { get; }

        public TestDirectory([CallerFilePath] string source = "")
        {
            parent = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source), "obj", "test-temp"));
            Root = Path.Combine(parent, name);
            Directory.CreateDirectory(Root);
        }

        public void Dispose()
        {
            string target = Path.GetFullPath(Root);
            if (Path.GetDirectoryName(target) != parent || Path.GetFileName(target) != name)
                throw new InvalidOperationException("只能清理当前测试创建的目录。");
            if (Directory.Exists(target)) Directory.Delete(target, true);
        }
    }
}
