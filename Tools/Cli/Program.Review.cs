using System;
using StreamDuel.Core;

namespace StreamDuel.Cli
{
    internal static partial class Program
    {
        private static void PrintReview(Battle battle, int viewerSide)
        {
            battle = TextInput.ForDisplay(battle);
            Console.WriteLine();
            Console.WriteLine(Loc.Current.T("review.heading"));
            Console.WriteLine(Loc.Current.T("review.perspective", battle.Sides[viewerSide].Name));
            var moments = BattleReview.KeyMoments(battle, viewerSide);
            if (moments.Count == 0) Console.WriteLine(Loc.Current.T("review.empty"));
            else foreach (var moment in moments) Console.WriteLine("  " + moment.Text);
        }
    }
}
