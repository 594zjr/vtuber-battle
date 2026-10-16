using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using StreamDuel.Core;
using Xunit;

// 终止事件与下架策略共享回放入口，恢复政策必须仍能保真重放。
public class T101_TerminationPolicyReviewTests
{
    static ContentDb Load(PackPolicy policy)
    {
        string content = Path.Combine(AppContext.BaseDirectory, "Content");
        return ContentDb.FromJson(File.ReadAllText(Path.Combine(content, "StreamDuelRules.json")),
            File.ReadAllText(Path.Combine(content, "Packs", "original.json")), policy);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void TerminationV3PolicyChecksOriginalRostersAndRestoringPolicyReplaysExactly(bool disablePack, bool surrender)
    {
        var raw = Load(PackPolicy.Empty);
        var ids = raw.Streamers.Select(streamer => streamer.Id).Take(12).ToArray();
        var battle = new Battle(raw, new IList<string>[] { ids.Take(6).ToArray(), ids.Skip(6).Take(6).ToArray() },
            new[] { "甲\u001b", "乙\u200b" }, new BattleOptions { Seed = 101, BanOne = true, LeadPick = true });
        Assert.Equal(BattleRecord.DefaultVersion, battle.Record.Version);
        battle.ChooseBan(0, 0);
        battle.ChooseBan(1, 0);
        battle.ChooseLead(0, 1);
        battle.ChooseLead(1, 2);
        if (surrender) battle.Surrender(0);
        else
        {
            battle.OfferDraw(0);
            battle.RespondDraw(1, false);
            battle.OfferDraw(1);
            battle.RespondDraw(0, true);
        }
        Assert.Equal(Phase.Over, battle.Phase);
        Assert.Equal(BattleRecord.TerminationVersion, battle.Record.Version);
        string disabled = ids[0];
        // 实际五人队中已被禁用的成员仍在原始录像队伍内，不能绕过运营下架门禁。
        Assert.DoesNotContain(battle.Sides[0].Team, fighter => fighter.Def.Id == disabled);
        Assert.Contains(disabled, battle.Record.Teams[0]);
        var policy = PackPolicy.FromJson(JsonSerializer.Serialize(new
        {
            disabledPacks = disablePack ? new[] { raw.PackId } : Array.Empty<string>(),
            disabledStreamers = disablePack ? Array.Empty<string>() : new[] { disabled },
            reason = new Dictionary<string, string> { { disablePack ? raw.PackId : disabled, "终止录像下架测试" } }
        }));
        var record = BattleRecord.FromJson(battle.Record.ToJson());
        string json = record.ToJson();
        string hash = raw.ContentHash();
        var before = battle.Clone();
        string reason = policy.CheckTeam(record.PackId, record.Teams.SelectMany(team => team));
        var blocked = Replay.Check(raw, record, policy);
        Assert.False(blocked.CanReplay);
        Assert.Equal(reason, blocked.Reason);
        if (!disablePack)
        {
            var filtered = Load(policy);
            Assert.False(Replay.Check(filtered, record).CanReplay);
            Assert.Equal(reason, Assert.Throws<InvalidOperationException>(() => Replay.RunBestEffort(filtered, record)).Message);
            Assert.False(Replay.Check(filtered, record, PackPolicy.Empty).CanReplay);
        }

        var restored = Load(PackPolicy.Empty);
        var accepted = Replay.Check(restored, record);
        Assert.True(accepted.CanReplay);
        Assert.Equal(string.Empty, accepted.Reason);
        foreach (var replay in new[] { Replay.Run(restored, record), Replay.RunBestEffort(restored, record) })
        {
            Assert.Equal(Phase.Over, replay.Phase);
            Assert.Equal(battle.Winner, replay.Winner);
            Assert.Equal(battle.StateHash(), replay.StateHash());
            Assert.Equal(battle.Log.Select(log => log.Turn + "|" + log.Node + "|" + log.Kind + "|" + log.Side + "|" + log.Text),
                replay.Log.Select(log => log.Turn + "|" + log.Node + "|" + log.Kind + "|" + log.Side + "|" + log.Text));
            Assert.Equal(json, replay.Record.ToJson());
            var expectedRandom = battle.Clone();
            for (int draw = 0; draw < 4; draw++) Assert.Equal(expectedRandom.Random(), replay.Random());
        }
        Assert.Equal(json, record.ToJson());
        Assert.Equal(json, battle.Record.ToJson());
        Assert.Equal(hash, raw.ContentHash());
        Assert.Equal(before.StateHash(), battle.StateHash());
        Assert.Equal(before.Random(), battle.Random());
    }
}
