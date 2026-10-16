using System;
using System.Collections.Generic;
using StreamDuel.Core;
using UnityEngine;

namespace StreamDuel.Game
{
    public sealed partial class BattleScreen
    {
        HotseatSession tournamentSession;
        AiLevel?[] tournamentAiLevels;
        Action<Battle> tournamentCompleted;
        Action tournamentCancelled;
        ContentDb tournamentPreviousContent;
        int tournamentViewer;

        public bool TournamentBattleActive => tournamentSession != null;
        public bool TournamentNeedsHandover => TournamentBattleActive && tournamentSession.NeedsHandover
            && !tournamentAiLevels[0].HasValue && !tournamentAiLevels[1].HasValue;
        public int TournamentInputSide => TournamentBattleActive ? tournamentSession.WhoseTurn : -1;
        int BattleViewerSide => TournamentBattleActive ? tournamentViewer : 0;

        public void StartTournamentBattle(Battle battle, AiLevel?[] aiLevels, Action<Battle> completed, Action cancelled)
        {
            if (battle == null) throw new ArgumentNullException(nameof(battle));
            if (aiLevels == null || aiLevels.Length != 2)
                throw new ArgumentException("赛事对局需要双方的玩家或电脑配置。", nameof(aiLevels));
            if (completed == null) throw new ArgumentNullException(nameof(completed));
            if (cancelled == null) throw new ArgumentNullException(nameof(cancelled));
            if (TournamentBattleActive) throw new InvalidOperationException("请先结束或退出当前赛事对局。");
            foreach (var level in aiLevels)
                if (level.HasValue && !Enum.IsDefined(typeof(AiLevel), level.Value))
                    throw new ArgumentException("赛事电脑难度无效。", nameof(aiLevels));
            var session = new HotseatSession(battle);

            // 赛事只临时借用旧页，不写入普通组队、难度、设置或玩家档案。
            tournamentPreviousContent = db;
            ClearTeamMatch();
            teamBattleRequest = null;
            lastTeams = null;
            tournamentSession = session;
            tournamentAiLevels = (AiLevel?[])aiLevels.Clone();
            tournamentCompleted = completed;
            tournamentCancelled = cancelled;
            b = battle;
            db = battle.Db;
            view = View.Battle;
            shown = logCountDrawn = 0;
            playing = false;
            pending = null;
            tournamentViewer = !aiLevels[0].HasValue ? 0 : !aiLevels[1].HasValue ? 1 : 0;
            detailKind = null;
            actionScroll = logScroll = detailScroll = Vector2.zero;
            if (b.Phase != Phase.Ban && b.Phase != Phase.Lead && b.Log.Count > 0) Play();
            else PumpTournamentComputer();
        }

        public void ConfirmTournamentHandover()
        {
            if (!TournamentNeedsHandover) throw new InvalidOperationException("现在不需要赛事交接。");
            tournamentSession.ConfirmHandover();
            tournamentViewer = tournamentSession.WhoseTurn;
            detailKind = null;
            actionScroll = detailScroll = Vector2.zero;
        }

        public void CancelTournamentBattle()
        {
            if (!TournamentBattleActive) return;
            var callback = tournamentCancelled;
            CloseTournamentBattle();
            callback();
        }

        public void CompleteTournamentBattle()
        {
            if (!TournamentBattleActive || b.Phase != Phase.Over || playing)
                throw new InvalidOperationException("赛事对局尚未完成，不能登记结果。");
            var finished = b;
            var callback = tournamentCompleted;
            CloseTournamentBattle();
            callback(finished);
        }

        void CloseTournamentBattle()
        {
            tournamentSession = null;
            tournamentAiLevels = null;
            tournamentCompleted = null;
            tournamentCancelled = null;
            db = tournamentPreviousContent;
            tournamentPreviousContent = null;
            b = null;
            view = View.Team;
            pending = null;
            playing = false;
            shown = logCountDrawn = 0;
            detailKind = null;
            fast = settings != null && settings.FastPlay;
        }

        void UpdateTournamentBattle()
        {
            if (TournamentNeedsHandover) return;
            if (playing)
            {
                if (Time.unscaledTime < nextLineAt) return;
                shown++;
                if (shown >= b.Log.Count)
                {
                    shown = b.Log.Count;
                    playing = false;
                    PumpTournamentComputer();
                    return;
                }
                var entry = b.Log[shown - 1];
                nextLineAt = Time.unscaledTime + (fast ? 0.05f : entry.Kind == "turn" ? 0.4f : 0.25f);
                return;
            }
            PumpTournamentComputer();
        }

        void PumpTournamentComputer()
        {
            // 每次只推进一个操作者；播放或交接期间不抢走真人输入，也不计算战斗规则。
            if (!TournamentBattleActive || playing || b.Phase == Phase.Over || TournamentNeedsHandover) return;
            if (tournamentSession.NeedsHandover) tournamentSession.ConfirmHandover();
            int side = tournamentSession.WhoseTurn;
            if (side < 0) return;
            if (!tournamentAiLevels[side].HasValue)
            {
                tournamentViewer = side;
                return;
            }
            SubmitTournamentComputerChoice(tournamentAiLevels[side].Value);
        }

        void SubmitTournamentComputerChoice(AiLevel level)
        {
            int side = tournamentSession.WhoseTurn;
            int count = b.Log.Count;
            if (b.Phase == Phase.Ban) tournamentSession.SubmitBan(Ai.ChooseBan(b, side));
            else if (b.Phase == Phase.Lead) tournamentSession.SubmitLead(Ai.ChooseLead(b, side));
            else if (b.Phase == Phase.Replace) tournamentSession.SubmitReplace(Ai.ChooseReplacement(b, side, level));
            else if (b.Phase == Phase.Choose) tournamentSession.Submit(Ai.Choose(b, side, level));
            AfterTournamentSubmission(count);
        }

        void SubmitTournamentAction(BattleAction action)
        {
            RequireTournamentInput();
            int count = b.Log.Count;
            tournamentSession.Submit(action);
            AfterTournamentSubmission(count);
        }

        void SubmitTournamentPosition(int index)
        {
            RequireTournamentInput();
            int count = b.Log.Count;
            if (b.Phase == Phase.Ban) tournamentSession.SubmitBan(index);
            else if (b.Phase == Phase.Lead) tournamentSession.SubmitLead(index);
            else if (b.Phase == Phase.Replace) tournamentSession.SubmitReplace(index);
            else throw new InvalidOperationException("当前阶段不能选择赛事位置。");
            AfterTournamentSubmission(count);
        }

        void RequireTournamentInput()
        {
            if (!TournamentBattleActive || playing || TournamentNeedsHandover || tournamentSession.WhoseTurn < 0
                || tournamentAiLevels[tournamentSession.WhoseTurn].HasValue)
                throw new InvalidOperationException("请等待赛事播放或确认交接后再选择。");
            if (tournamentSession.NeedsHandover) tournamentSession.ConfirmHandover();
        }

        void AfterTournamentSubmission(int previousLogs)
        {
            detailKind = null;
            actionScroll = detailScroll = Vector2.zero;
            if (b.Log.Count != previousLogs)
            {
                if (b.Phase == Phase.Ban || b.Phase == Phase.Lead) shown = b.Log.Count;
                else Play();
            }
            if (!TournamentNeedsHandover && tournamentSession.NeedsHandover) tournamentSession.ConfirmHandover();
            if (!playing && tournamentSession.WhoseTurn >= 0 && !tournamentAiLevels[tournamentSession.WhoseTurn].HasValue)
                tournamentViewer = tournamentSession.WhoseTurn;
        }

        bool AfterTournamentPlayback()
        {
            if (!TournamentBattleActive) return false;
            PumpTournamentComputer();
            return true;
        }

        bool DrawTournamentHandover()
        {
            if (!TournamentBattleActive) return false;
            bool computer = !playing && tournamentSession.WhoseTurn >= 0
                && tournamentAiLevels[tournamentSession.WhoseTurn].HasValue;
            if (!TournamentNeedsHandover && !computer) return false;
            // 不绘制战斗页再覆盖：交接期间没有技能详情、日志或上一位玩家的鼠标提示。
            Box(new Rect(420, 270, 760, 300));
            var plain = new GUIStyle(sCenter) { richText = false, wordWrap = true, fontSize = 24 };
            string name = TextInput.Clean(b.Sides[tournamentSession.WhoseTurn].Name, TextInputKind.PlayerName).Text;
            GUI.Label(new Rect(450, 300, 700, 90), computer ? "电脑「" + name + "」正在选择…"
                : "请将设备交给「" + name + "」\n上一位玩家的选择已隐藏", plain);
            if (!computer && GUI.Button(new Rect(590, 430, 420, 60), "我已接手，继续选择", sBtn)) Do(ConfirmTournamentHandover);
            return true;
        }

        bool DrawTournamentHeader()
        {
            if (!TournamentBattleActive) return false;
            var plain = new GUIStyle(sSmall) { richText = false, wordWrap = true };
            string name = TextInput.Clean(b.Sides[BattleViewerSide].Name, TextInputKind.PlayerName).Text;
            GUI.Label(new Rect(1020, 8, 170, 48), "赛事对局\n" + name + "的视角", plain);
            return true;
        }

        bool DrawTournamentResult()
        {
            if (!TournamentBattleActive) return false;
            Fill(new Rect(0, 0, RefW, RefH), new Color(Bg.r, Bg.g, Bg.b, 0.85f));
            var r = new Rect(430, 270, 740, 340);
            Box(r);
            var plain = new GUIStyle(sCenter) { richText = false, wordWrap = true, fontSize = 28 };
            string result = b.Winner < 0 ? "本局平局" : TextInput.Clean(b.Sides[b.Winner].Name, TextInputKind.PlayerName).Text + " 获胜";
            GUI.Label(new Rect(r.x + 30, r.y + 35, r.width - 60, 80), result, plain);
            GUI.Label(new Rect(r.x + 30, r.y + 130, r.width - 60, 60), "返回赛事后归档本局并登记结果。\n本页不累计普通战绩或排位分。", sCenter);
            if (GUI.Button(new Rect(r.x + 140, r.y + 235, r.width - 280, 60), "返回赛事并登记", sBtn)) Do(CompleteTournamentBattle);
            return true;
        }

        public bool AdvanceTournamentSmoke()
        {
            if (!SmokeRequested) throw new InvalidOperationException("自动推进仅供隔离验收使用。");
            if (!TournamentBattleActive) return false;
            if (TournamentNeedsHandover) ConfirmTournamentHandover();
            else if (playing) SkipPlayback();
            else if (b.Phase == Phase.Over) CompleteTournamentBattle();
            else
            {
                if (tournamentSession.NeedsHandover) tournamentSession.ConfirmHandover();
                int side = tournamentSession.WhoseTurn;
                if (tournamentAiLevels[side].HasValue) PumpTournamentComputer();
                else SubmitTournamentComputerChoice(AiLevel.Normal);
            }
            return TournamentBattleActive;
        }
    }
}
