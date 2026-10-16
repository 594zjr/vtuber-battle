using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using StreamDuel.Core;

namespace StreamDuel.Cli
{
    internal static partial class Program
    {
        private static int PrintTournamentReport(CliOptions options)
        {
            string path = ValidateTournamentPath(options, options.TournamentReportFile);
            string saved = File.ReadAllText(path, new UTF8Encoding(false, true));
            var source = MiniJson.Parse(saved) as Dictionary<string, object>;
            if (source == null || !source.TryGetValue("packId", out object value)
                || !(value is string packId) || string.IsNullOrWhiteSpace(packId))
                throw new FormatException("赛事存档缺少有效的内容包编号；原文件未修改。");
            if (!options.PackSpecified) options.Pack = packId;
            var policy = PackPolicy.LoadDefault();
            var db = LoadPack(Path.Combine(AppContext.BaseDirectory, "Content"), options, policy);
            var tournament = Tournament.FromJson(db, saved);
            string libraryPath = TournamentLibraryPath(path);
            var library = ReplayLibrary.OpenReadOnly(libraryPath, policy);
            var report = TournamentReport.Build(tournament, library);
            Console.Write(options.Json ? report.ToJson() : report.ToText());
            Console.WriteLine();
            return 0;
        }

        private static string TournamentLibraryPath(string tournamentPath)
        {
            return tournamentPath + ".replay-library";
        }

        private static bool ArchiveTournamentBattle(Tournament tournament, Pairing pairing, Battle battle, string path)
        {
            try
            {
                var library = new ReplayLibrary(TournamentLibraryPath(path), policy: battle.Db.Policy);
                string id = library.SaveTournament(tournament, pairing, battle);
                Console.WriteLine("赛事录像已保存：" + id);
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException
                || ex is ArgumentException || ex is FormatException || ex is InvalidOperationException || ex is KeyError)
            {
                // 录像维护失败不能抹掉已完成的比分；有回放的部分仍可在下次只读报告中找到。
                Console.Error.WriteLine("警告：赛事录像归档失败，比分仍会保存：" + ex.Message);
                return false;
            }
        }
    }
}
