using System;
using System.Collections.Generic;
using System.Linq;
using StreamDuel.Core;

namespace StreamDuel.Ladder
{
    internal sealed class TeamResult
    {
        public int Id;
        public string[] Members;
        public double Elo = 1500;
        public double Points;
        public int Games, Wins, Draws, Losses, Byes;
        public readonly Dictionary<int, int> Opponents = new Dictionary<int, int>();
        public readonly Dictionary<string, long> Appearances = new Dictionary<string, long>(StringComparer.Ordinal);
    }

    internal sealed class StreamerResult
    {
        public StreamerDef Streamer;
        public long RosterGames, Appearances, Wins, Draws, Losses, DamageDealt, Kos, TurnsActive;
        public double WeightedElo;
    }

    internal sealed class Tournament
    {
        readonly ContentDb db;
        readonly Options options;
        public readonly List<TeamResult> Teams = new List<TeamResult>();
        public readonly Dictionary<string, StreamerResult> Streamers;
        public long Games { get; private set; }

        public Tournament(ContentDb db, Options options, List<string[]> rosters)
        {
            this.db = db;
            this.options = options;
            Streamers = db.Streamers.ToDictionary(s => s.Id, s => new StreamerResult { Streamer = s }, StringComparer.Ordinal);
            for (int i = 0; i < rosters.Count; i++)
            {
                var errors = TeamCheck.Validate(db, options.Season, rosters[i]);
                if (errors.Count > 0) throw new ArgumentException("阵容 " + (i + 1) + " 不合法：" + string.Join("；", errors));
                Teams.Add(new TeamResult { Id = i + 1, Members = rosters[i] });
            }
        }

        public void Run()
        {
            for (int round = 0; round < options.Rounds; round++)
            {
                var waiting = Teams.OrderByDescending(t => t.Points).ThenBy(t => t.Id).ToList();
                if (waiting.Count % 2 != 0)
                {
                    var bye = waiting.OrderBy(t => t.Byes).ThenBy(t => t.Points).ThenByDescending(t => t.Id).First();
                    bye.Byes++;
                    bye.Points += 2;
                    waiting.Remove(bye);
                }

                // 先冻结整轮配对，避免本轮早打完的成绩改变后面的配对。
                var pairs = new List<TeamResult[]>();
                while (waiting.Count > 0)
                {
                    var first = waiting[0];
                    waiting.RemoveAt(0);
                    var second = waiting.OrderBy(t => Meetings(first, t))
                        .ThenBy(t => Math.Abs(first.Points - t.Points)).ThenBy(t => t.Id).First();
                    waiting.Remove(second);
                    first.Opponents[second.Id] = Meetings(first, second) + 1;
                    second.Opponents[first.Id] = Meetings(second, first) + 1;
                    pairs.Add(new[] { first, second });
                }

                foreach (var pair in pairs)
                {
                    double expected = 1 / (1 + Math.Pow(10, (pair[1].Elo - pair[0].Elo) / 400));
                    double score = Play(pair[0], pair[1], unchecked(options.Seed + (uint)Games));
                    score += 1 - Play(pair[1], pair[0], unchecked(options.Seed + (uint)Games));
                    // 两局共用赛前期望，避免同实力各赢一局仍因处理顺序产生净涨跌。
                    double change = 24 * (score - 2 * expected);
                    pair[0].Elo += change;
                    pair[1].Elo -= change;
                }
            }

            foreach (var team in Teams)
                foreach (var appearance in team.Appearances)
                    Streamers[appearance.Key].WeightedElo += team.Elo * appearance.Value;
            foreach (var result in Streamers.Values)
                if (result.Appearances > 0) result.WeightedElo /= result.Appearances;
        }

        static int Meetings(TeamResult first, TeamResult second)
        {
            int count;
            return first.Opponents.TryGetValue(second.Id, out count) ? count : 0;
        }

        double Play(TeamResult first, TeamResult second, uint seed)
        {
            var teams = new[] { first, second };
            var battle = new Battle(db, new IList<string>[] { first.Members, second.Members }, new[] { "甲方", "乙方" }, seed);
            var appeared = new HashSet<Fighter>();
            RememberActive(battle, appeared);
            while (battle.Phase != Phase.Over)
            {
                if (battle.Phase == Phase.Replace)
                {
                    for (int side = 0; side < 2 && battle.Phase == Phase.Replace; side++)
                    {
                        if (!battle.Need[side]) continue;
                        battle.Replace(side, Ai.ChooseReplacement(battle, side, options.Level));
                        RememberActive(battle, appeared);
                    }
                }
                else
                {
                    var firstAction = Ai.Choose(battle, 0, options.Level);
                    var secondAction = Ai.Choose(battle, 1, options.Level);
                    battle.ResolveTurn(firstAction, secondAction);
                    RememberActive(battle, appeared);
                }
            }

            double firstScore = battle.Winner == -1 ? 0.5 : battle.Winner == 0 ? 1 : 0;
            var summary = BattleSummary.From(battle);
            for (int side = 0; side < 2; side++)
            {
                var team = teams[side];
                double score = side == 0 ? firstScore : 1 - firstScore;
                team.Points += score;
                team.Games++;
                if (score == 1) team.Wins++;
                else if (score == 0.5) team.Draws++;
                else team.Losses++;
                for (int index = 0; index < battle.Sides[side].Team.Count; index++)
                {
                    var fighter = battle.Sides[side].Team[index];
                    var result = Streamers[fighter.Def.Id];
                    var stats = summary.Sides[side][index];
                    result.RosterGames++;
                    result.DamageDealt += stats.DamageDealt;
                    result.Kos += stats.Kos;
                    result.TurnsActive += stats.TurnsActive;
                    if (!appeared.Contains(fighter)) continue;
                    result.Appearances++;
                    if (score == 1) result.Wins++;
                    else if (score == 0.5) result.Draws++;
                    else result.Losses++;
                    long count;
                    team.Appearances.TryGetValue(fighter.Def.Id, out count);
                    team.Appearances[fighter.Def.Id] = count + 1;
                }
            }
            Games++;
            return firstScore;
        }

        static void RememberActive(Battle battle, HashSet<Fighter> appeared)
        {
            // 回合结束计数可能尚未触发就终局，直接记录上场者才能包含立即下播的角色。
            for (int side = 0; side < 2; side++) appeared.Add(battle.Active(side));
        }
    }
}
