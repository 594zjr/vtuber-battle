using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using StreamDuel.Core;
using UnityEngine;

namespace StreamDuel.Game
{
    public sealed partial class BattleScreen
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
            settings = selectedSettings; fast = settings.FastPlay; timerOn = settings.TurnTimer;
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
            b = next; view = View.Battle;
            shown = 0; logCountDrawn = 0; playing = false; timerLeft = 10f;
            pending = null; actionScroll = Vector2.zero; logScroll = Vector2.zero;
            detailKind = "pet"; detailSide = 1; detailIdx = 0; detailScroll = Vector2.zero;
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
            if (TournamentBattleActive) { SubmitTournamentPosition(foeIndex); return; }
            if (teamMatch == null || b == null || b.Phase != Phase.Ban || b.HasChosenBan(0)) return;
            b.ChooseBan(0, foeIndex);
            // 删除队员后详情索引须重新定位；名单变化与下一阶段均由 Core 决定。
            detailKind = "pet"; detailSide = 1; detailIdx = 0; detailScroll = Vector2.zero;
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

        bool DrawTeamMatchResult()
        {
            if (teamMatch == null) return false;
            Fill(new Rect(0, 0, RefW, RefH), new Color(Bg.r, Bg.g, Bg.b, 0.75f));
            DrawTeamMatchFanNotice(true);
            var r = new Rect(RefW / 2 - 300, RefH / 2 - 170, 600, 340);
            Box(r);
            string result = b.Winner == 0 ? "本局胜利" : b.Winner == 1 ? "本局失败" : "本局平局";
            GUI.Label(new Rect(r.x, r.y + 18, r.width, 80), result, sHuge);
            var wins = teamMatch.Wins;
            GUI.Label(new Rect(r.x + 20, r.y + 110, r.width - 40, 30), teamRuleset.Name + "：你 " + wins[0]
                + " : " + wins[1] + " 电脑；平局 " + teamMatch.Draws, sCenter);
            GUI.Label(new Rect(r.x + 20, r.y + 148, r.width - 40, 30),
                (teamMatch.Over ? "整场已结束" : "本局已记录，可继续下一局") + "；已完成 "
                + teamMatchGames.Count.ToString(CultureInfo.InvariantCulture) + " 局", sCenter);
            if (!string.IsNullOrEmpty(teamMatchError))
                GUI.Label(new Rect(r.x + 20, r.y + 184, r.width - 40, 54), teamMatchError, sSmall);
            if (GUI.Button(new Rect(r.x + 30, r.y + 260, 290, 50), teamMatch.Over ? "同样阵容重新比赛" : "继续下一局", sBtn))
                Do(ContinueTeamMatch);
            if (GUI.Button(new Rect(r.x + 340, r.y + 260, 230, 50), "重新组队", sBtn)) Do(RequestTeamScreen);
            return true;
        }

        void DrawTeamBan()
        {
            GUI.Label(new Rect(24, 12, 1000, 44), C("开播对决", Accent) + "  选择禁用", sBig);
            DrawTeamMatchFanNotice();
            if (GUI.Button(new Rect(1400, 18, 176, 36), TournamentBattleActive ? "返回赛事" : "返回组队", sBtn)) Do(RequestTeamScreen);
            GUI.Label(new Rect(24, 62, 1500, 32), "从对手六人中禁用一位；双方确认后由规则引擎统一移除。此时不计时。", sText);
            DrawTeamBanCards(1 - BattleViewerSide, 104, "对手阵容 · 选择一位禁用");
            DrawTeamBanCards(BattleViewerSide, 388, "你的阵容 · 点卡片查看详情");
            DrawDetail(new Rect(24, 690, 1552, 192));
        }

        void DrawTeamMatchFanNotice(bool battleHeader = false)
        {
            if (db.IsMixed || (db.PackId != null && db.PackId.StartsWith("fan_", StringComparison.Ordinal)))
                GUI.Label(new Rect(battleHeader ? 560 : 650, 24, battleHeader ? 290 : 720, 28),
                    "非官方同人作品 · 免费 · 非商业", sSmall);
        }

        void DrawTeamBanCards(int side, float y, string title)
        {
            GUI.Label(new Rect(24, y, 1500, 28), C(title, side == BattleViewerSide ? Mine : Foe), sTitle);
            float width = (1552f - 5 * 12) / 6;
            for (int i = 0; i < b.Sides[side].Team.Count; i++)
            {
                var fighter = b.Sides[side].Team[i];
                var r = new Rect(24 + i * (width + 12), y + 36, width, 230);
                Box(r);
                Portrait(new Rect(r.x + 12, r.y + 14, 52, 52), fighter.Def, false);
                GUI.Label(new Rect(r.x + 74, r.y + 14, width - 86, 30), fighter.Name, sTitle);
                GUI.Label(new Rect(r.x + 74, r.y + 44, width - 86, 26), TypeTag(fighter.Def), sSmall);
                GUI.Label(new Rect(r.x + 12, r.y + 80, width - 24, 58), "人设·" + db.Personas[fighter.Def.Persona].Name, sSmall);
                int index = i;
                if (GUI.Button(new Rect(r.x + 12, r.y + 176, width - 24, 40), side != BattleViewerSide ? "禁用这位主播" : "查看详情", sBtn))
                {
                    if (side != BattleViewerSide) Do(() => SubmitTeamBan(index));
                    else Do(() => { detailKind = "pet"; detailSide = side; detailIdx = index; detailScroll = Vector2.zero; });
                }
                if (GUI.Button(new Rect(r.x, r.y, width, 166), GUIContent.none, GUIStyle.none))
                    Do(() => { detailKind = "pet"; detailSide = side; detailIdx = index; detailScroll = Vector2.zero; });
            }
        }
    }
}
