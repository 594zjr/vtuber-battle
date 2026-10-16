using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using StreamDuel.Core;
using UnityEngine;
using UnityEngine.UIElements;

namespace StreamDuel.Game
{
    public sealed partial class TournamentScreen
    {
        void BuildPairings()
        {
            if (state.Session == null) { state.Creating = true; BuildCreate(); return; }
            var tournament = state.Session.Tournament;
            Label(body, "tournament-round-title", tournament.FormatName + " · " + tournament.Players.Count + " 人 · "
                + (tournament.IsFinished ? "已结束" : "第 " + tournament.Round + " 轮"), "subtitle");
            if (state.PendingResult != null)
            {
                var retry = Row(body, "tournament-retry");
                Label(retry, "tournament-retry-note", "有已结束对局尚未保存。重试成功前不能重新开打或录入结果。", "muted");
                Button(retry, "tournament-retry-save", "重试登记", () => { SavePendingResult(); Render(); });
            }
            var controls = Row(body, "tournament-round-actions");
            var next = Button(controls, "tournament-next", "排下一轮", () =>
            {
                state.Session.NextRound(); state.SelectedPairing = -1;
                state.Notice = "赛程已保存。"; Render();
            });
            next.SetEnabled(!tournament.IsFinished && state.PendingResult == null);
            Button(controls, "tournament-report", "查看战报", ShowReport);
            Label(controls, "tournament-round-note", tournament.AutoTiebreak ? "平局自动加赛；只接受真实对局，不可手工报分。" : "可对战或手工录入；淘汰赛平局仍待赛。", "muted");
            var current = tournament.Pairings.FirstOrDefault(pairing => pairing.Id == state.SelectedPairing)
                ?? tournament.Pairings.FirstOrDefault(pairing => !pairing.WinnerSide.HasValue)
                ?? tournament.Pairings.LastOrDefault();
            if (current != null) { state.SelectedPairing = current.Id; BuildPairingControls(current); }
            var scroll = Scroll(body, "tournament-pairings-scroll", tournament.IsElimination);
            BuildStandings(scroll);
            if (tournament.IsElimination) BuildBracket(scroll);
            else
            {
                var rounds = new VisualElement { name = "tournament-rounds" }; scroll.Add(rounds);
                foreach (var round in tournament.Pairings.GroupBy(item => item.Round))
                {
                    Label(rounds, "round-" + round.Key, "第 " + round.Key + " 轮", "subtitle");
                    var row = Row(rounds, "round-pairings-" + round.Key);
                    foreach (var pairing in round) PairingCard(row, pairing);
                }
            }
        }

        void BuildStandings(VisualElement parent)
        {
            var table = new VisualElement { name = "tournament-standings" }; table.AddToClassList("standings"); parent.Add(table);
            var header = Row(table, "standings-header");
            Label(header, "standings-header-rank", "名次", "rank-cell");
            Label(header, "standings-header-name", "选手", "name-cell");
            Label(header, "standings-header-score", "积分 / 对手分", "score-cell");
            Label(header, "standings-header-record", "胜 / 平 / 负 / 轮空", "score-cell");
            foreach (var standing in state.Session.Tournament.Standings())
            {
                var row = Row(table, "standing-" + standing.Player); row.AddToClassList("standing");
                Label(row, "standing-rank-" + standing.Player, standing.Rank.ToString(CultureInfo.InvariantCulture), "rank-cell");
                Label(row, "standing-name-" + standing.Player, CleanName(standing.Name), "name-cell");
                Label(row, "standing-score-" + standing.Player, standing.Points + " / " + standing.OpponentPoints, "score-cell");
                Label(row, "standing-record-" + standing.Player, standing.Wins + " / " + standing.Draws + " / " + standing.Losses + " / " + standing.Byes, "score-cell");
            }
        }

        static string CleanName(string text) => TextInput.Clean(text, TextInputKind.PlayerName).Text;
        string PlayerName(int id) => id < 0 ? "轮空" : CleanName(state.Session.Tournament.Players[id].Name);

        string PairingText(Pairing pairing)
        {
            string status = pairing.IsBye ? "轮空晋级" : !pairing.WinnerSide.HasValue ? "待赛"
                : pairing.WinnerSide == -1 ? "平局" : "胜方：" + PlayerName(pairing.WinnerSide == 0 ? pairing.Player0 : pairing.Player1);
            if (pairing.AdvancedBySeed) status += "（加赛后按种子晋级）";
            return "第 " + (pairing.Id + 1) + " 场 · " + status + "\n甲：" + PlayerName(pairing.Player0) + "\n乙：" + PlayerName(pairing.Player1);
        }

        Button PairingCard(VisualElement parent, Pairing pairing)
        {
            var card = Button(parent, "pairing-" + pairing.Id, PairingText(pairing), () => { state.SelectedPairing = pairing.Id; Render(); });
            card.AddToClassList("pairing");
            if (pairing.Id == state.SelectedPairing) card.AddToClassList("selected");
            return card;
        }

        void BuildPairingControls(Pairing pairing)
        {
            var box = new VisualElement { name = "tournament-pairing-detail" }; box.AddToClassList("detail"); body.Add(box);
            Label(box, "tournament-selected", PairingText(pairing).Replace("\n", "   "), "selected-title");
            var row = Row(box, "tournament-pairing-actions");
            bool pending = !pairing.IsBye && !pairing.WinnerSide.HasValue && state.PendingResult == null;
            bool computers = !pairing.IsBye && state.Session.Controllers[pairing.Player0].HasValue && state.Session.Controllers[pairing.Player1].HasValue;
            Button(row, "tournament-auto", "电脑自动打完", () => PlayPairing(true)).SetEnabled(pending && computers);
            Button(row, "tournament-play", "进入对战", () => PlayPairing(false)).SetEnabled(pending && !computers);
            var result = Dropdown(row, "tournament-result", "录入结果", new[] { "甲方胜", "乙方胜", "平局" }, 0);
            result.SetEnabled(pending && !state.Session.Tournament.AutoTiebreak);
            Button(row, "tournament-record", "登记结果", () =>
            {
                int winner = result.index == 2 ? -1 : result.index;
                state.Session.ReportResult(pairing.Id, winner);
                state.Notice = "结果已登记并保存。手工结果不生成录像。";
                Render();
            }).SetEnabled(pending && !state.Session.Tournament.AutoTiebreak);
        }

        void BuildBracket(VisualElement parent)
        {
            var tree = new VisualElement { name = "tournament-bracket" }; tree.AddToClassList("bracket"); parent.Add(tree);
            Label(tree, "bracket-help", "对阵树显示已生成的场次；后续对阵在本轮完成后生成。可横向、纵向滚动查看。", "muted");
            var tournament = state.Session.Tournament;
            string[] groups = tournament.Format == Tournament.SingleElimination ? new[] { "single" } : new[] { "winners", "losers", "final" };
            foreach (string group in groups)
            {
                string title = group == "losers" ? "败者组" : group == "final" ? "总决赛 / 重置总决赛" : "胜者组";
                Label(tree, "bracket-title-" + group, title, "subtitle");
                var pairings = tournament.Pairings.Where(item => item.Bracket == group || group == "final" && item.Bracket == "reset").ToList();
                if (pairings.Count == 0) { Label(tree, "bracket-empty-" + group, "等待前序对阵完成。", "muted"); continue; }
                var lane = Row(tree, "bracket-lane-" + group); lane.AddToClassList("bracket-lane");
                var cards = new Dictionary<int, VisualElement>();
                var edges = new BracketLines(); lane.Add(edges);
                foreach (var round in pairings.GroupBy(item => Tuple.Create(item.Bracket, item.BracketRound)))
                {
                    var column = new VisualElement { name = "bracket-column-" + round.Key.Item1 + "-" + round.Key.Item2 };
                    column.AddToClassList("bracket-column"); lane.Add(column);
                    Label(column, "bracket-round-" + round.Key.Item1 + "-" + round.Key.Item2,
                        round.Key.Item1 == "reset" ? "重置总决赛" : "第 " + round.Key.Item2 + " 轮", "muted");
                    foreach (var pairing in round) cards.Add(pairing.Id, PairingCard(column, pairing));
                }
                // 连线仅连接已排出场次里相同的选手，不推算赛程或晋级资格。
                foreach (var current in pairings)
                    foreach (int player in new[] { current.Player0, current.Player1 }.Where(id => id >= 0))
                    {
                        var previous = pairings.Where(item => item.Id < current.Id && (item.BracketRound < current.BracketRound
                            || item.Bracket == "final" && current.Bracket == "reset")
                            && (item.Player0 == player || item.Player1 == player)).OrderByDescending(item => item.Id).FirstOrDefault();
                        if (previous != null) edges.Connections.Add(Tuple.Create(cards[previous.Id], cards[current.Id]));
                    }
                lane.RegisterCallback<GeometryChangedEvent>(evt => edges.MarkDirtyRepaint());
            }
        }

        sealed class BracketLines : VisualElement
        {
            public readonly List<Tuple<VisualElement, VisualElement>> Connections = new List<Tuple<VisualElement, VisualElement>>();
            public BracketLines()
            {
                pickingMode = PickingMode.Ignore;
                style.position = Position.Absolute;
                style.left = style.top = style.right = style.bottom = 0;
                generateVisualContent += context =>
                {
                    var painter = context.painter2D; painter.lineWidth = 2; painter.strokeColor = new Color32(100, 143, 190, 255);
                    foreach (var edge in Connections)
                    {
                        var a = this.WorldToLocal(new Vector2(edge.Item1.worldBound.xMax, edge.Item1.worldBound.center.y));
                        var b = this.WorldToLocal(new Vector2(edge.Item2.worldBound.xMin, edge.Item2.worldBound.center.y));
                        if (!float.IsFinite(a.x) || !float.IsFinite(b.x)) continue;
                        float middle = (a.x + b.x) / 2;
                        painter.BeginPath(); painter.MoveTo(a); painter.LineTo(new Vector2(middle, a.y));
                        painter.LineTo(new Vector2(middle, b.y)); painter.LineTo(b); painter.Stroke();
                    }
                };
            }
        }
    }
}
