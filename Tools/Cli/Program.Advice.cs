using System;
using System.Collections.Generic;
using StreamDuel.Core;

namespace StreamDuel.Cli
{
    internal static partial class Program
    {
        private static void PrintTeamAdvice(ContentDb db, IList<string> mine, IList<string> theirs)
        {
            var advice = TeamAdvice.For(db, mine, theirs);
            var loc = Loc.Current;
            Console.WriteLine();
            Console.WriteLine(loc.T("advice.cli.title"));
            Console.WriteLine(loc.T("advice.cli.scope"));
            Console.WriteLine(advice.Scope);
            Console.WriteLine(loc.T("advice.cli.ban", AdviceFighterName(advice.BanSuggestion.Index, advice.BanSuggestion.Name),
                advice.BanSuggestion.Reason));
            for (int i = 0; i < advice.LeadSuggestions.Count; i++)
            {
                var suggestion = advice.LeadSuggestions[i];
                Console.WriteLine(loc.T("advice.cli.lead", i + 1, AdviceFighterName(suggestion.Index, suggestion.Name), suggestion.Reason));
            }
            for (int i = 0; i < advice.Threats.Count; i++)
            {
                var threat = advice.Threats[i];
                Console.WriteLine(loc.T("advice.cli.threat", i + 1, AdviceFighterName(threat.Index, threat.Name), threat.Reason));
            }
            foreach (var answer in advice.Answers)
                Console.WriteLine(loc.T("advice.cli.answer", AdviceFighterName(answer.ThreatIndex, answer.ThreatName),
                    AdviceFighterName(answer.AnswerIndex, answer.AnswerName), answer.Reason));
        }

        private static string AdviceFighterName(int index, string name)
        {
            return Loc.Current.T("advice.cli.fighter", index + 1, name);
        }
    }
}
