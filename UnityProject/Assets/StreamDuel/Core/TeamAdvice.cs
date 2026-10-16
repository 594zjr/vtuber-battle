using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace StreamDuel.Core
{
    public sealed class TeamAdviceEntry
    {
        public int Index { get; }
        public string Id { get; }
        public string Name { get; }
        public string Reason { get; }

        internal TeamAdviceEntry(int index, string id, string name, string reason)
        {
            Index = index;
            Id = id;
            Name = name;
            Reason = reason;
        }
    }

    public sealed class TeamAdviceAnswer
    {
        public int ThreatIndex { get; }
        public string ThreatId { get; }
        public string ThreatName { get; }
        public int AnswerIndex { get; }
        public string AnswerId { get; }
        public string AnswerName { get; }
        public string Reason { get; }

        internal TeamAdviceAnswer(int threatIndex, string threatId, string threatName,
            int answerIndex, string answerId, string answerName, string reason)
        {
            ThreatIndex = threatIndex;
            ThreatId = threatId;
            ThreatName = threatName;
            AnswerIndex = answerIndex;
            AnswerId = answerId;
            AnswerName = answerName;
            Reason = reason;
        }
    }

    public sealed class TeamAdviceResult
    {
        public TeamAdviceEntry BanSuggestion { get; }
        public IReadOnlyList<TeamAdviceEntry> LeadSuggestions { get; }
        public IReadOnlyList<TeamAdviceEntry> Threats { get; }
        public IReadOnlyList<TeamAdviceAnswer> Answers { get; }
        public string Scope { get; }

        internal TeamAdviceResult(TeamAdviceEntry banSuggestion, List<TeamAdviceEntry> leads,
            List<TeamAdviceEntry> threats, List<TeamAdviceAnswer> answers, string scope)
        {
            BanSuggestion = banSuggestion;
            LeadSuggestions = new List<TeamAdviceEntry>(leads).AsReadOnly();
            Threats = new List<TeamAdviceEntry>(threats).AsReadOnly();
            Answers = new List<TeamAdviceAnswer>(answers).AsReadOnly();
            Scope = scope;
        }
    }

    public static class TeamAdvice
    {
        sealed class Candidate
        {
            public int Index, Favorable, Coverage;
            public long Outgoing, Incoming;
            public long Net { get { return Outgoing - Incoming; } }
        }

        /// <summary>按双方公开的原始合法六人阵容生成开场建议；下标始终对应输入号位。</summary>
        public static TeamAdviceResult For(ContentDb db, IList<string> mine, IList<string> theirs)
        {
            var loc = Loc.Current;
            if (db == null) throw new ArgumentNullException(nameof(db), loc.T("advice.error.db"));
            var myIds = CopyTeam(db, mine, nameof(mine), loc.T("advice.side.mine"), loc);
            var theirIds = CopyTeam(db, theirs, nameof(theirs), loc.T("advice.side.theirs"), loc);
            var myDefs = myIds.Select(db.Streamer).ToArray();
            var theirDefs = theirIds.Select(db.Streamer).ToArray();

            // 双向均复用既有开场预览，不能用静态属性另算伤害或猜测尚未公开的选择。
            var outgoing = DamageCalc.Matchup(db, myIds, theirIds);
            var incoming = DamageCalc.Matchup(db, theirIds, myIds);
            var myCoverage = Coverage(db, myDefs, theirDefs, out var myHasAttacks);
            var theirCoverage = Coverage(db, theirDefs, myDefs, out _);
            var myRanking = Rank(outgoing, incoming, myCoverage);
            var theirRanking = Rank(incoming, outgoing, theirCoverage);

            var leads = myRanking.Take(2).Select(candidate => Entry(myDefs, candidate,
                loc.T("advice.reason.lead", Average(candidate.Outgoing), Average(candidate.Incoming),
                    candidate.Favorable, candidate.Coverage), loc)).ToList();
            var threats = new List<TeamAdviceEntry>();
            var answers = new List<TeamAdviceAnswer>();
            foreach (var threat in theirRanking.Take(2))
            {
                int j = threat.Index;
                int answerIndex = Enumerable.Range(0, myIds.Count)
                    .OrderByDescending(i => (long)outgoing[i, j] - incoming[j, i])
                    .ThenBy(i => incoming[j, i])
                    .ThenByDescending(i => myCoverage[i, j])
                    .ThenBy(i => i).First();
                string answerName = Name(myDefs[answerIndex], loc);
                threats.Add(Entry(theirDefs, threat,
                    loc.T("advice.reason.threat", Average(threat.Outgoing), Average(threat.Incoming),
                        threat.Favorable, threat.Coverage, answerName), loc));
                string coverageText = myHasAttacks[answerIndex]
                    ? loc.T("advice.coverage.value", myCoverage[answerIndex, j].ToString("G", CultureInfo.InvariantCulture))
                    : loc.T("advice.coverage.none");
                answers.Add(new TeamAdviceAnswer(j, theirIds[j], Name(theirDefs[j], loc),
                    answerIndex, myIds[answerIndex], answerName,
                    loc.T("advice.reason.answer", outgoing[answerIndex, j], incoming[j, answerIndex], coverageText)));
            }

            var ban = theirRanking[0];
            return new TeamAdviceResult(Entry(theirDefs, ban,
                loc.T("advice.reason.ban", Average(ban.Outgoing), Average(ban.Incoming),
                    ban.Favorable, ban.Coverage), loc), leads, threats, answers, loc.T("advice.scope"));
        }

        static List<string> CopyTeam(ContentDb db, IList<string> source, string parameter, string side, Loc loc)
        {
            if (source == null) throw new ArgumentNullException(parameter, loc.T("advice.error.team.null", side));
            var copy = new List<string>(source);
            if (TeamCheck.Validate(db, null, copy).Count > 0)
                throw new ArgumentException(loc.T("advice.error.team.invalid", side), parameter);
            foreach (string id in copy)
            {
                var streamer = db.Streamer(id);
                if (streamer.Stats["hp"] <= 0)
                    throw new ArgumentException(loc.T("advice.error.hp", Name(streamer, loc)), parameter);
            }
            return copy;
        }

        static double[,] Coverage(ContentDb db, StreamerDef[] attackers, StreamerDef[] defenders, out bool[] hasAttacks)
        {
            var result = new double[attackers.Length, defenders.Length];
            hasAttacks = new bool[attackers.Length];
            for (int i = 0; i < attackers.Length; i++)
            {
                var attacker = attackers[i];
                // 这里只解释初始技能的赛道潜力；现场可用性、人设与伤害由 Matchup 决定。
                var types = attacker.Skills.Concat(new[] { attacker.Fifth })
                    .Where(id => !string.IsNullOrEmpty(id)).Select(id => db.Skills[id])
                    .Where(skill => skill.Power > 0 && skill.Pp > 0).Select(skill => skill.Type).Distinct().ToArray();
                hasAttacks[i] = types.Length > 0;
                for (int j = 0; j < defenders.Length; j++)
                {
                    var defender = defenders[j];
                    foreach (string type in types)
                        result[i, j] = Math.Max(result[i, j], db.TypeMul(type, defender.Type, defender.Type2));
                }
            }
            return result;
        }

        static List<Candidate> Rank(int[,] own, int[,] opposing, double[,] coverage)
        {
            var result = new List<Candidate>();
            for (int i = 0; i < own.GetLength(0); i++)
            {
                var candidate = new Candidate { Index = i };
                for (int j = 0; j < own.GetLength(1); j++)
                {
                    // 六格百分比都可能达到 int 上限，先存入 long 再累加和比较。
                    candidate.Outgoing += own[i, j];
                    candidate.Incoming += opposing[j, i];
                    if (own[i, j] > opposing[j, i]) candidate.Favorable++;
                    if (coverage[i, j] > 1) candidate.Coverage++;
                }
                result.Add(candidate);
            }
            return result.OrderByDescending(candidate => candidate.Net)
                .ThenByDescending(candidate => candidate.Favorable)
                .ThenByDescending(candidate => candidate.Coverage)
                .ThenBy(candidate => candidate.Index).ToList();
        }

        static TeamAdviceEntry Entry(StreamerDef[] definitions, Candidate candidate, string reason, Loc loc)
        {
            var definition = definitions[candidate.Index];
            return new TeamAdviceEntry(candidate.Index, definition.Id, Name(definition, loc), reason);
        }

        static string Name(StreamerDef definition, Loc loc)
        {
            return string.IsNullOrWhiteSpace(definition.Name) ? loc.T("advice.name.unknown") : definition.Name;
        }

        static string Average(long sum)
        {
            return (sum / 6d).ToString("0.#", CultureInfo.InvariantCulture);
        }
    }
}
