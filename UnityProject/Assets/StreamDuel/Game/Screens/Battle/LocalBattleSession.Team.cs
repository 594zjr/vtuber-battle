using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using StreamDuel.Core;
using UnityEngine;

namespace StreamDuel.Game
{
    public sealed partial class LocalBattleSession
    {
        TeamBattleRequest teamBattleRequest;
        ITeamPresetStore teamPresetStore;
        PackPolicy teamPolicySnapshot = PackPolicy.Empty;
        readonly Dictionary<string, TeamPreset> teamSelections = new Dictionary<string, TeamPreset>(StringComparer.Ordinal);
        readonly Dictionary<string, string> teamSeasons = new Dictionary<string, string>(StringComparer.Ordinal);

        public TeamScreenArgs CreateTeamScreenArgs()
        {
            EnsureMenuContentLoaded();
            if (db != null)
            {
                string name = teamSelections.TryGetValue(db.PackId, out var remembered) ? remembered.Name : "";
                teamSelections[db.PackId] = new TeamPreset { Name = name, PackId = db.PackId, Ids = new List<string>(pick) };
            }
            // 每次进入组队都取设置页刚保存的默认值，本局开始后再复制快照。
            settings = GameRuntimeSettings.Current;
            timerOn = settings.TurnTimer;
            var preferred = packs.FirstOrDefault(content => content.PackId == settings.DefaultPack);
            if (preferred != null && !ReferenceEquals(preferred, db))
            {
                db = preferred;
                pick = new List<string>(RestoreTeamSelection(db).Ids);
                teamPolicyNotice = db.Policy.CheckTeam(db.PackId, pick);
            }
            b = null;
            teamBattleRequest = null;
            ClearTeamMatch();
            playing = false;
            if (teamPresetStore == null) teamPresetStore = SmokeRequested
                ? (ITeamPresetStore)new MemoryTeamPresetStore() : new FileTeamPresetStore(Application.persistentDataPath);
            return new TeamScreenArgs
            {
                Packs = MenuPacks, Content = db, Policy = teamPolicySnapshot, Selection = db == null ? null : RestoreTeamSelection(db),
                Settings = GameSettings.FromJson((settings ?? new GameSettings()).ToJson()),
                RulesetId = teamSelectedRulesetId, RememberRuleset = id => teamSelectedRulesetId = id,
                Presets = teamPresetStore, StartBattle = StartTeamBattle, NavigateToBattle = true, LoadSelection = RestoreTeamSelection,
                LoadSeason = content => teamSeasons.TryGetValue(content.PackId, out string savedSeason) ? savedSeason : null,
                Remember = RememberTeamSelection, RememberView = RememberTeamView,
                SeasonId = db != null && teamSeasons.TryGetValue(db.PackId, out string season) ? season : null,
                Notice = string.Join("\n", packErrors.Concat(new[] { settingsNotice, teamPolicyNotice }).Where(x => !string.IsNullOrEmpty(x)))
            };
        }

        TeamPreset RestoreTeamSelection(ContentDb content)
        {
            if (teamSelections.TryGetValue(content.PackId, out var preset))
                return new TeamPreset { Name = preset.Name, PackId = preset.PackId, Ids = new List<string>(preset.Ids) };
            string saved = SmokeRequested ? "" : PlayerPrefs.GetString("sd.pick." + content.PackId, "");
            return new TeamPreset { PackId = content.PackId,
                Ids = string.IsNullOrEmpty(saved) ? new List<string>() : saved.Split(',').ToList() };
        }

        void RememberTeamSelection(TeamPreset selected, GameSettings chosenSettings)
        {
            RememberTeamView(selected, teamSeasons.TryGetValue(selected.PackId, out string season) ? season : null, chosenSettings);
            if (!SmokeRequested)
            {
                // 只有明确选人、自动组队或读取预设时写回，加载页面本身不覆盖旧队伍。
                SavePick();
            }
        }

        void RememberTeamView(TeamPreset selected, string season, GameSettings chosenSettings)
        {
            var content = packs.FirstOrDefault(item => item.PackId == selected.PackId);
            if (content == null) throw new ArgumentException("找不到内容包：" + selected.PackId);
            db = content; pick = new List<string>(selected.Ids);
            teamSelections[db.PackId] = new TeamPreset { Name = selected.Name, PackId = selected.PackId, Ids = new List<string>(pick) };
            teamSeasons[db.PackId] = season;
            var next = GameSettings.FromJson(chosenSettings.ToJson());
            next.DefaultPack = db.PackId;
            bool changed = settings == null || settings.ToJson() != next.ToJson();
            settings = next; timerOn = settings.TurnTimer;
            if (changed || !string.IsNullOrEmpty(settingsNotice))
            {
                SaveSettingsSafely();
                if (!string.IsNullOrEmpty(settingsNotice)) throw new IOException(settingsNotice);
            }
        }

        public void StartTeamBattle(TeamBattleRequest request)
        {
            if (request == null || request.Content == null || request.Team == null)
                throw new ArgumentException("开局需要内容包和六人队伍。");
            if (request.Ruleset != null) { StartTeamRulesetBattle(request); return; }
            var errors = request.Team.Check(request.Content, request.SeasonId);
            if (errors.Count > 0) throw new ArgumentException(string.Join("；", errors));
            var opponent = TeamBuilder.Suggest(request.Content, request.SeasonId, Array.Empty<string>(), request.Seed ^ 0x66A50001u);
            var selectedSettings = GameSettings.FromJson((request.Settings ?? new GameSettings()).ToJson());
            var teams = new[] { new List<string>(request.Team.Ids), opponent };
            var nextBattle = new Battle(request.Content, new IList<string>[] { teams[0], teams[1] }, new[] { "你", "电脑" },
                request.Seed, leadPick: true);
            nextBattle.ChooseLead(1, Ai.ChooseLead(nextBattle, 1));
            ClearTeamMatch(); teamSelectedRulesetId = null;
            db = request.Content;
            pick = new List<string>(request.Team.Ids);
            settings = selectedSettings;
            timerOn = settings.TurnTimer;
            teamBattleRequest = new TeamBattleRequest
            {
                Content = db, Team = new TeamPreset { Name = request.Team.Name, PackId = db.PackId, Ids = new List<string>(pick) },
                SeasonId = request.SeasonId, Settings = GameSettings.FromJson(settings.ToJson()), Seed = request.Seed
            };
            lastTeams = teams;
            b = nextBattle;
            playing = false;
            ResetLocalClock();
        }

        void RequestTeamScreen()
        {
            b = null;
            teamBattleRequest = null;
            ClearTeamMatch();
            playing = false;
            if (TeamScreenRequested != null) TeamScreenRequested();
        }

    }
}
