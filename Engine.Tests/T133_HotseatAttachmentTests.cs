using System;
using System.Collections.Generic;
using StreamDuel.Core;
using Xunit;

public class T133_HotseatAttachmentTests
{
    static Battle Battle(bool ban = false, bool lead = false)
    {
        return new Battle(BattleTests.LoadPack("original"), new IList<string>[]
        {
            new[] { "bell", "enka", "shiori", "lumi", "hane", "momo" },
            new[] { "kei", "yuan", "nyako", "buck", "kasumi", "bee" }
        }, new[] { "赛事甲", "赛事乙" }, new BattleOptions { Seed = 133, BanOne = ban, LeadPick = lead });
    }

    [Fact]
    public void AttachmentUsesTheOriginalBattleAndKeepsTheFirstChoicePrivate()
    {
        var battle = Battle();
        var session = new HotseatSession(battle);
        string before = battle.Record.ToJson();
        ulong hash = battle.StateHash();
        session.Submit(BattleAction.Pass());
        Assert.True(session.NeedsHandover);
        Assert.Equal(1, session.WhoseTurn);
        Assert.Equal(before, battle.Record.ToJson());
        Assert.Equal(hash, battle.StateHash());
        Assert.Throws<InvalidOperationException>(() => session.Submit(BattleAction.Pass()));
        session.ConfirmHandover();
        session.Submit(BattleAction.Pass());
        Assert.Equal(2, battle.Turn);
        Assert.Equal(battle.Record.ToJson(), session.Record.ToJson());
        Assert.Equal(battle.StateHash(), Replay.Run(battle.Db, session.Record).StateHash());
        session.Surrender();
        Assert.Equal(Phase.Over, battle.Phase);
        Assert.Equal(0, battle.Winner);
        Assert.Equal(battle.StateHash(), session.CloneCompletedBattle().StateHash());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExistingPartialOpeningChoiceIsRejectedWithoutChangingTheBattle(bool ban)
    {
        var battle = Battle(ban: ban, lead: !ban);
        if (ban) battle.ChooseBan(0, 5);
        else battle.ChooseLead(1, 2);
        string before = battle.Record.ToJson();
        ulong hash = battle.StateHash();
        Assert.Throws<InvalidOperationException>(() => new HotseatSession(battle));
        Assert.Equal(before, battle.Record.ToJson());
        Assert.Equal(hash, battle.StateHash());
    }

    [Fact]
    public void AttachedBanAndLeadCommitOnlyAfterBothPlayersConfirm()
    {
        var battle = Battle(ban: true, lead: true);
        var session = new HotseatSession(battle);
        session.SubmitBan(5);
        Assert.Empty(battle.Record.Events);
        session.ConfirmHandover();
        session.SubmitBan(4);
        Assert.Equal(Phase.Lead, battle.Phase);
        Assert.Equal(5, battle.Sides[0].Team.Count);
        Assert.Equal(5, battle.Sides[1].Team.Count);
        string banned = battle.Record.ToJson();
        session.SubmitLead(2);
        Assert.Equal(banned, battle.Record.ToJson());
        session.ConfirmHandover();
        session.SubmitLead(1);
        Assert.Equal(Phase.Choose, battle.Phase);
        Assert.Equal(battle.StateHash(), Replay.Run(battle.Db, battle.Record).StateHash());
    }

    [Fact]
    public void AttachmentAtALaterPublicTurnPreservesEarlierHistory()
    {
        var battle = Battle();
        battle.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        string prefix = battle.Record.ToJson();
        var session = new HotseatSession(battle);
        Assert.Equal(prefix, session.Record.ToJson());
        session.Submit(BattleAction.Pass());
        session.ConfirmHandover();
        session.Submit(BattleAction.Pass());
        Assert.Equal(3, battle.Turn);
        Assert.Equal(battle.StateHash(), Replay.Run(battle.Db, session.Record).StateHash());
    }

    [Fact]
    public void NullAndCompletedBattlesCannotAttach()
    {
        Assert.Throws<ArgumentNullException>(() => new HotseatSession((Battle)null));
        var battle = Battle();
        battle.Surrender(0);
        string before = battle.Record.ToJson();
        Assert.Throws<InvalidOperationException>(() => new HotseatSession(battle));
        Assert.Equal(before, battle.Record.ToJson());
    }
}
