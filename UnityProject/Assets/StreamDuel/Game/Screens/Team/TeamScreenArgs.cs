using System;
using System.Collections.Generic;
using StreamDuel.Core;

namespace StreamDuel.Game
{
    public sealed class TeamScreenArgs
    {
        public IReadOnlyList<ContentDb> Packs;
        public ContentDb Content;
        public PackPolicy Policy;
        public TeamPreset Selection;
        public string SeasonId;
        public string RulesetId;
        public Action<string> RememberRuleset;
        public GameSettings Settings;
        public ITeamPresetStore Presets;
        public IPortraitProvider Portraits;
        public Action<TeamBattleRequest> StartBattle;
        public bool NavigateToBattle;
        public Action Return;
        public uint Seed = 1;
        public Func<string> ReadClipboard;
        public Action<string> WriteClipboard;
        public Func<ContentDb, TeamPreset> LoadSelection;
        public Func<ContentDb, string> LoadSeason;
        public Action<TeamPreset, GameSettings> Remember;
        public Action<TeamPreset, string, GameSettings> RememberView;
        public string Notice;
    }

    public sealed class TeamBattleRequest
    {
        public ContentDb Content;
        public TeamPreset Team;
        public string SeasonId;
        public Ruleset Ruleset;
        public GameSettings Settings;
        public uint Seed;
    }

    public interface ITeamPresetStore
    {
        TeamPresetList Load();
        void Save(TeamPresetList presets);
    }

    public sealed class MemoryTeamPresetStore : ITeamPresetStore
    {
        string json = new TeamPresetList().ToJson();
        public TeamPresetList Load() { return TeamPresetList.FromJson(json); }
        public void Save(TeamPresetList presets) { json = presets.ToJson(); }
    }
}
