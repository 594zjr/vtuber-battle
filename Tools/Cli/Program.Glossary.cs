using System;
using StreamDuel.Core;

namespace StreamDuel.Cli
{
    internal static partial class Program
    {
        static int PrintGlossary(ContentDb db, string keyword)
        {
            var loc = Loc.Current;
            var entries = Glossary.Search(Glossary.Build(db), keyword);
            Console.WriteLine(loc.T("glossary.cli.title", db.PackName));
            Console.WriteLine(db.PackNotice);
            Console.WriteLine(loc.T("glossary.cli.count", entries.Count));
            if (entries.Count == 0)
                Console.WriteLine(loc.T("glossary.cli.no.matches", keyword == null ? "" : keyword.Trim()));
            foreach (var entry in entries)
            {
                Console.WriteLine();
                Console.WriteLine(loc.T("glossary.cli.entry", loc.T("glossary.category." + entry.Category), entry.Title));
                Console.WriteLine(entry.Text);
            }
            return 0;
        }
    }
}
