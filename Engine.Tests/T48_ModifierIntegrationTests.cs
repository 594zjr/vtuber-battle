using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using StreamDuel.Core;
using Xunit;

public class T48_ModifierIntegrationTests
{
    [Theory]
    [InlineData("half_pp", false, false)]
    [InlineData("stage_cap_3", false, true)]
    [InlineData("sudden_death", true, false)]
    [InlineData("hype_start", true, true)]
    [InlineData("no_switch", true, true)]
    [InlineData("hp_x2", false, false)]
    [InlineData("half_pp,stage_cap_3,sudden_death,hype_start,no_switch,hp_x2", true, true)]
    public void CompletedModifiedBattleSharesAndReplaysAllStateAndHistory(string modifierIds, bool ban, bool lead)
    {
        var db = BattleTests.LoadPack("original");
        var modifiers = modifierIds.Split(',');
        var battle = new Battle(db, new IList<string>[]
        {
            new[] { "bell", "enka", "shiori", "lumi", "hane", "momo" },
            new[] { "kei", "yuan", "nyako", "buck", "kasumi", "bee" }
        }, new[] { "分享甲方", "分享乙方" }, new BattleOptions
        {
            Seed = 48, Modifiers = modifiers.ToList(), BanOne = ban, LeadPick = lead
        });
        if (ban)
            for (int side = 0; side < 2; side++) battle.ChooseBan(side, Ai.ChooseBan(battle, side));
        if (lead)
            for (int side = 0; side < 2; side++) battle.ChooseLead(side, Ai.ChooseLead(battle, side));

        int remaining = 500;
        while (battle.Phase != Phase.Over && remaining-- > 0)
        {
            if (battle.Phase == Phase.Choose)
                battle.ResolveTurn(Ai.Choose(battle, 0), Ai.Choose(battle, 1));
            else
            {
                Assert.Equal(Phase.Replace, battle.Phase);
                for (int side = 0; side < 2 && battle.Phase == Phase.Replace; side++)
                    if (battle.Need[side]) battle.Replace(side, Ai.ChooseReplacement(battle, side));
            }
        }

        Assert.Equal(Phase.Over, battle.Phase);
        Assert.NotEmpty(battle.Record.Events);
        Assert.NotEmpty(battle.Events);
        string json = battle.Record.ToJson();
        ulong hash = battle.StateHash();
        var jsonOptions = new JsonSerializerOptions { IncludeFields = true };
        string events = JsonSerializer.Serialize(battle.Events, jsonOptions);
        string log = JsonSerializer.Serialize(battle.Log, jsonOptions);
        string statistics = JsonSerializer.Serialize(BattleSummary.From(battle), jsonOptions);

        var decoded = ShareCode.Decode(ShareCode.Encode(battle.Record));
        Assert.Equal(json, decoded.ToJson());
        Assert.Equal(battle.Record.Modifiers, decoded.Modifiers);
        Assert.Equal(battle.Modifiers, decoded.Modifiers);
        Assert.Equal(ban, decoded.BanOne);
        Assert.Equal(lead, decoded.LeadPick);

        var replay = Replay.Run(db, decoded);
        Assert.Equal(Phase.Over, replay.Phase);
        Assert.Equal(battle.Winner, replay.Winner);
        Assert.Equal(hash, replay.StateHash());
        Assert.Equal(json, replay.Record.ToJson());
        Assert.Equal(battle.Modifiers, replay.Modifiers);
        Assert.Equal(events, JsonSerializer.Serialize(replay.Events, jsonOptions));
        Assert.Equal(log, JsonSerializer.Serialize(replay.Log, jsonOptions));
        Assert.Equal(statistics, JsonSerializer.Serialize(BattleSummary.From(replay), jsonOptions));

        Assert.Equal(hash, battle.StateHash());
        Assert.Equal(json, battle.Record.ToJson());
        Assert.Equal(events, JsonSerializer.Serialize(battle.Events, jsonOptions));
        Assert.Equal(log, JsonSerializer.Serialize(battle.Log, jsonOptions));
        Assert.Equal(statistics, JsonSerializer.Serialize(BattleSummary.From(battle), jsonOptions));
        for (int draw = 0; draw < 8; draw++) Assert.Equal(battle.Random(), replay.Random());
    }
}
