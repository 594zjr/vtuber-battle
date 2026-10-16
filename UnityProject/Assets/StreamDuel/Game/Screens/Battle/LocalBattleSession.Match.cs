using System;
using System.Collections.Generic;
using System.Linq;
using StreamDuel.Core;

namespace StreamDuel.Game
{
    public sealed partial class LocalBattleSession
    {
        string teamSelectedRulesetId;
        Ruleset teamRuleset;
        Match teamMatch;
        readonly List<Battle> teamMatchGames = new List<Battle>();
        Battle reportedTeamGame;
        string teamMatchError;

        void ClearTeamMatch()
        {
            teamRuleset = null; teamMatch = null; reportedTeamGame = null;
            teamMatchGames.Clear(); teamMatchError = null;
            ResetTeamClock();
            ResetLocalClock();
        }

        void StartTeamRulesetBattle(TeamBattleRequest request)
        {
            var rule = Ruleset.FromJson(request.Ruleset.ToJson());
            rule.Validate(request.Content);
            if (!string.Equals(rule.Season, request.SeasonId, StringComparison.Ordinal))
                throw new ArgumentException("交接的赛制与已选择赛季不一致。");
            var errors = request.Team.Check(request.Content, rule.Season);
            if (errors.Count > 0) throw new ArgumentException(string.Join("；", errors));
            var opponent = TeamBuilder.Suggest(request.Content, rule.Season, Array.Empty<string>(), request.Seed ^ 0x66A50001u);
            var original = new[] { new List<string>(request.Team.Ids), new List<string>(opponent) };
            var selectedSettings = GameSettings.FromJson((request.Settings ?? new GameSettings()).ToJson());
            var nextMatch = new Match(request.Content, rule,
                new IList<string>[] { original[0], original[1] }, new[] { "你", "电脑" }, request.Seed);
            var nextBattle = nextMatch.StartNextGame();

            // 两队和赛制均构造成功后才替换当前会话；原六人名单不会被禁用或首发重排改写。
            db = request.Content; pick = new List<string>(request.Team.Ids);
            settings = selectedSettings; timerOn = settings.TurnTimer;
            teamSelectedRulesetId = rule.Id; teamRuleset = rule; teamMatch = nextMatch;
            teamMatchGames.Clear(); reportedTeamGame = null; teamMatchError = null;
            teamBattleRequest = new TeamBattleRequest
            {
                Content = db, Team = new TeamPreset { Name = request.Team.Name, PackId = db.PackId, Ids = new List<string>(pick) },
                SeasonId = rule.Season, Ruleset = Ruleset.FromJson(rule.ToJson()),
                Settings = GameSettings.FromJson(settings.ToJson()), Seed = request.Seed
            };
            lastTeams = original;
            LoadTeamMatchGame(nextBattle);
        }

        void LoadTeamMatchGame(Battle next)
        {
            b = next;
            playing = false;
            ResetLocalClock();
            teamMatchError = null;
            PrepareTeamMatchAiPhase();
            ResetTeamClock();
            if (b.Phase != Phase.Ban && b.Phase != Phase.Lead) Play();
        }

        void PrepareTeamMatchAiPhase()
        {
            if (b.Phase == Phase.Ban && !b.HasChosenBan(1)) b.ChooseBan(1, Ai.ChooseBan(b, 1));
            if (b.Phase == Phase.Lead && !b.HasChosenLead(1)) b.ChooseLead(1, Ai.ChooseLead(b, 1));
        }

        void SubmitTeamBan(int foeIndex)
        {
            if (teamMatch == null || b == null || b.Phase != Phase.Ban || b.HasChosenBan(0)) return;
            b.ChooseBan(0, foeIndex);
            PrepareTeamMatchAiPhase();
            if (b.Phase != Phase.Ban && b.Phase != Phase.Lead) Play();
        }

        void ReportTeamMatchCompletion()
        {
            if (teamMatch == null || b == null || b.Phase != Phase.Over || ReferenceEquals(reportedTeamGame, b)) return;
            teamMatch.ReportResult(b);
            reportedTeamGame = b;
            teamMatchGames.Add(b);
        }

        void ContinueTeamMatch()
        {
            if (teamMatch == null || b == null || b.Phase != Phase.Over || playing) return;
            ReportTeamMatchCompletion();
            try
            {
                if (teamMatch.Over) StartTeamRulesetBattle(teamBattleRequest);
                else LoadTeamMatchGame(teamMatch.StartNextGame());
            }
            catch (Exception ex) { teamMatchError = "无法开始下一局：" + ex.Message; }
        }

    }
}
