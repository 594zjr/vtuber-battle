using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using StreamDuel.Core;

namespace StreamDuel.Cli
{
    internal static partial class Program
    {
        private static int RunTournament(CliOptions options)
        {
            // 重定向输入按 UTF-8 读取，避免脚本中的中文昵称被本地控制台代码页改写。
            if (Console.IsInputRedirected) Console.InputEncoding = new UTF8Encoding(false, true);
            var file = new TournamentFileStore(options);
            string saved = file.Read();
            if (saved != null)
            {
                var root = MiniJson.Parse(saved) as Dictionary<string, object>;
                object pack;
                if (root == null || !root.TryGetValue("packId", out pack)
                    || !(pack is string packId) || string.IsNullOrWhiteSpace(packId))
                    throw new FormatException("赛事存档缺少有效的内容包编号；原文件未修改。");
                if (!options.PackSpecified) options.Pack = packId;
            }
            var db = LoadPack(Path.Combine(AppContext.BaseDirectory, "Content"), options);
            Tournament tournament;
            if (saved != null) tournament = Tournament.FromJson(db, saved);
            else
            {
                string format = ReadTournamentFormat();
                if (format == null) return 0;
                int? rounds = format == "swiss" ? ReadTournamentRounds() : 0;
                if (!rounds.HasValue) return 0;
                tournament = new Tournament(db, format, rounds.Value);
                file.Save(tournament);
            }

            Console.WriteLine("开播对决 · 本地赛事");
            Console.WriteLine("内容包：" + db.PackName);
            Console.WriteLine(db.PackNotice);
            Console.WriteLine("赛制：" + (tournament.Format == "swiss" ? "瑞士轮" : "循环赛")
                + "；模式：" + (options.Auto ? "普通 AI 自动代打" : "交互式记录结果"));
            Console.WriteLine("赛事存档：" + file.Path);
            if (tournament.Round == 0 && !RegisterTournamentPlayers(tournament, file))
                return QuitTournament(file);

            while (!tournament.IsFinished)
            {
                var pairings = tournament.NextRound();
                file.Save(tournament);
                if (pairings.Count == 0) break;
                Console.WriteLine();
                Console.WriteLine("===== 第 " + tournament.Round + " / " + tournament.Rounds + " 轮 =====");
                foreach (var pairing in pairings)
                {
                    var first = tournament.Players[pairing.Player0];
                    if (pairing.IsBye)
                    {
                        Console.WriteLine(TournamentPlayerName(first) + " 本轮轮空，记一胜。");
                        continue;
                    }
                    var second = tournament.Players[pairing.Player1];
                    string firstName = TournamentPlayerName(first), secondName = TournamentPlayerName(second);
                    Console.WriteLine("对局 " + (pairing.Id + 1) + "：" + firstName + " 对 " + secondName);
                    if (pairing.WinnerSide.HasValue)
                    {
                        PrintTournamentResult(firstName, secondName, pairing.WinnerSide.Value, "已记录：");
                        continue;
                    }
                    int? winner = options.Auto ? AutoTournamentBattle(db, first, second, pairing.Id)
                        : ReadTournamentResult(firstName, secondName);
                    if (!winner.HasValue) return QuitTournament(file);
                    tournament.ReportResult(pairing, winner.Value);
                    file.Save(tournament);
                    PrintTournamentResult(firstName, secondName, winner.Value, "已保存结果：");
                }
                PrintTournamentStandings(tournament);
            }
            Console.WriteLine("赛事已结束。");
            PrintTournamentStandings(tournament);
            return 0;
        }

        private static string ReadTournamentLine(string prompt)
        {
            Console.Write(prompt);
            string line = Console.ReadLine();
            return line == null || line.Trim().Equals("q", StringComparison.OrdinalIgnoreCase) ? null : line.Trim();
        }

        private static string ReadTournamentFormat()
        {
            while (true)
            {
                string value = ReadTournamentLine("新建赛事：1 循环赛，2 瑞士轮（q 退出）> ");
                if (value == null) return null;
                if (value == "1" || value == "roundrobin") return "roundrobin";
                if (value == "2" || value == "swiss") return "swiss";
                Console.WriteLine("请输入 1（循环赛）或 2（瑞士轮）。");
            }
        }

        private static int? ReadTournamentRounds()
        {
            while (true)
            {
                string value = ReadTournamentLine("瑞士轮轮数（回车或 0 按人数自动确定，q 退出）> ");
                if (value == null) return null;
                int rounds;
                if (value.Length == 0) return 0;
                if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out rounds)) return rounds;
                Console.WriteLine("请输入非负整数，或直接按回车。");
            }
        }

        private static bool RegisterTournamentPlayers(Tournament tournament, TournamentFileStore file)
        {
            foreach (var player in tournament.Players)
                Console.WriteLine("已报名：" + TournamentPlayerName(player));
            while (true)
            {
                string name = ReadTournamentLine("报名姓名（空行结束报名，q 保存退出）> ");
                if (name == null) return false;
                if (name.Length == 0)
                {
                    if (tournament.Players.Count >= 2) return true;
                    Console.WriteLine("至少需要 2 名选手，请继续报名。");
                    continue;
                }
                string code = ReadTournamentLine("请输入报名 #" + (tournament.Players.Count + 1) + " " + name + " 的队伍码（q 保存退出）> ");
                if (code == null) return false;
                try
                {
                    tournament.AddPlayer(name, code);
                }
                catch (Exception ex) when (ex is ArgumentException || ex is FormatException || ex is InvalidOperationException)
                {
                    Console.WriteLine("报名失败：" + ex.Message);
                    continue;
                }
                file.Save(tournament);
                Console.WriteLine("已保存报名：" + TournamentPlayerName(tournament.Players[tournament.Players.Count - 1])
                    + "（共 " + tournament.Players.Count + " 人）");
            }
        }

        private static int? ReadTournamentResult(string first, string second)
        {
            while (true)
            {
                string value = ReadTournamentLine("输入结果：0 = " + first + " 获胜；1 = " + second
                    + " 获胜；-1 = 平局（q 保存退出）> ");
                if (value == null) return null;
                if (value == "0") return 0;
                if (value == "1") return 1;
                if (value == "-1" || value == "平" || value == "平局") return -1;
                Console.WriteLine("请输入 0、1 或 -1；0 与 1 对应上方两位选手，-1 表示平局。");
            }
        }

        private static int AutoTournamentBattle(ContentDb db, TournamentPlayer first, TournamentPlayer second, int pairingId)
        {
            uint seed = unchecked(0x9E3779B9u + (uint)pairingId);
            var battle = new Battle(db, new IList<string>[] { first.Team.ToArray(), second.Team.ToArray() },
                new[] { first.Name, second.Name }, new BattleOptions { Seed = seed });
            while (battle.Phase != Phase.Over)
            {
                if (battle.Phase == Phase.Replace)
                {
                    for (int side = 0; side < 2 && battle.Phase == Phase.Replace; side++)
                        if (battle.Need[side]) battle.Replace(side, Ai.ChooseReplacement(battle, side, AiLevel.Normal));
                }
                else
                {
                    var firstAction = Ai.Choose(battle, 0, AiLevel.Normal);
                    var secondAction = Ai.Choose(battle, 1, AiLevel.Normal);
                    battle.ResolveTurn(firstAction, secondAction);
                }
            }
            Console.WriteLine("自动对局种子：" + seed + "；回合：" + battle.Turn
                + "；最终状态哈希：" + battle.StateHash().ToString("x16", CultureInfo.InvariantCulture));
            return battle.Winner;
        }

        private static void PrintTournamentResult(string first, string second, int winner, string prefix)
        {
            Console.WriteLine(prefix + (winner == -1 ? "平局" : (winner == 0 ? first : second) + " 获胜"));
        }

        private static string TournamentPlayerName(TournamentPlayer player)
        {
            return "报名 #" + (player.Id + 1) + " " + player.Name;
        }

        private static void PrintTournamentStandings(Tournament tournament)
        {
            Console.WriteLine("排名（积分 → 对手分 → 直接对话分 → 报名顺序）：");
            foreach (var standing in tournament.Standings())
                Console.WriteLine(standing.Rank + " · 报名 #" + (standing.Player + 1) + " " + standing.Name + "：" + standing.Points
                    + " 分；对手分 " + standing.OpponentPoints + "；直接对话分 " + standing.HeadToHeadPoints
                    + "；胜 " + standing.Wins + " / 平 " + standing.Draws + " / 负 " + standing.Losses
                    + "；轮空 " + standing.Byes);
        }

        private static int QuitTournament(TournamentFileStore file)
        {
            Console.WriteLine("已保存赛事进度并退出：" + file.Path + "；未报结果的对局保持待赛。");
            return 0;
        }

        private sealed class TournamentFileStore
        {
            static readonly Encoding Utf8 = new UTF8Encoding(false, true);
            readonly CliOptions options;
            string expected;
            public string Path { get; }

            public TournamentFileStore(CliOptions options)
            {
                this.options = options;
                Path = ValidateTournamentPath(options);
            }

            public string Read()
            {
                if (File.Exists(Path)) expected = File.ReadAllText(Path, Utf8);
                return expected;
            }

            public void Save(Tournament tournament)
            {
                string json = tournament.ToJson();
                ValidateTournamentPath(options);
                string directory = System.IO.Path.GetDirectoryName(Path);
                Directory.CreateDirectory(directory);
                string lockPath = Path + ".tournament.lock";
                RejectTournamentLinks(lockPath);
                // 持锁比较上次读取的内容，另一实例修改后不能用旧的内存赛事覆盖新结果。
                using (new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                {
                    string actual = File.Exists(Path) ? File.ReadAllText(Path, Utf8) : null;
                    if (!string.Equals(expected, actual, StringComparison.Ordinal))
                        throw new IOException("赛事文件已被其他程序修改，请退出后重新打开；没有覆盖现有文件。");
                    bool ownsTemporary = false;
                    string temporary = null;
                    try
                    {
                        using (var stream = CreateTemporary(out temporary))
                        {
                            ownsTemporary = true;
                            byte[] bytes = Utf8.GetBytes(json);
                            stream.Write(bytes, 0, bytes.Length);
                            stream.Flush(true);
                        }
                        File.Move(temporary, Path, expected != null);
                        ownsTemporary = false;
                        expected = json;
                    }
                    finally
                    {
                        if (ownsTemporary) File.Delete(temporary);
                    }
                }
            }

            private FileStream CreateTemporary(out string path)
            {
                for (int suffix = 0; ; suffix = checked(suffix + 1))
                {
                    path = Path + ".tournament.tmp" + (suffix == 0 ? "" : "." + suffix.ToString(CultureInfo.InvariantCulture));
                    // 上次中断留下的文件可能不属于本进程，另取序号，不覆盖或删除它。
                    if (File.Exists(path) || Directory.Exists(path)) continue;
                    RejectTournamentLinks(path);
                    try { return new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); }
                    catch (IOException) when (File.Exists(path) || Directory.Exists(path)) { }
                }
            }
        }

        private static string ValidateTournamentPath(CliOptions options)
        {
            string path;
            try { path = System.IO.Path.GetFullPath(options.TournamentFile); }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException)
            {
                throw new ArgumentException("赛事文件路径无效：" + ex.Message, ex);
            }
            if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal)
                || System.IO.Path.EndsInDirectorySeparator(options.TournamentFile) || Directory.Exists(path)
                || !string.Equals(System.IO.Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("赛事存档必须是普通的 .json 文件路径，不能是目录或设备路径。");
            string name = System.IO.Path.GetFileName(path);
            if (name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0 || name.StartsWith(".", StringComparison.Ordinal)
                || new[] { "profile.json", "settings.json", "teams.json", "StreamDuelRules.json", "manifest.json", "packages-lock.json" }
                    .Contains(name, StringComparer.OrdinalIgnoreCase)
                || name.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("赛事文件不能占用程序、规则、玩家存档或隐藏管理文件，请另选文件名。");
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (ContainsPath(System.IO.Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory), path, comparison)
                || (options.PackDirectory != null && ContainsPath(System.IO.Path.GetFullPath(options.PackDirectory), path, comparison)))
                throw new ArgumentException("赛事文件不能放在程序目录或内容包目录中，请另选保存位置。");
            var parts = path.Split(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
            foreach (string part in parts)
            {
                if (OperatingSystem.IsWindows() && IsTournamentDeviceName(part))
                    throw new ArgumentException("赛事路径不能使用 Windows 保留设备名：" + part);
                if (new[] { ".git", "replays", "Resources", "Packs", "Content", "ProjectSettings", "Packages" }
                    .Contains(part, StringComparer.OrdinalIgnoreCase))
                    throw new ArgumentException("赛事文件不能放在程序资源、内容包或玩家回放目录中。");
                if (OperatingSystem.IsWindows() && part.Length > 0 && (part.EndsWith(".", StringComparison.Ordinal)
                    || part.EndsWith(" ", StringComparison.Ordinal)))
                    throw new ArgumentException("赛事路径的每一段都不能以空格或句点结尾。");
            }
            RejectTournamentLinks(path);
            return path;
        }

        private static bool IsTournamentDeviceName(string part)
        {
            string stem = part.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
            return stem == "CON" || stem == "PRN" || stem == "AUX" || stem == "NUL"
                || stem == "CONIN$" || stem == "CONOUT$"
                || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal)
                    || stem.StartsWith("LPT", StringComparison.Ordinal)) && "123456789¹²³".IndexOf(stem[3]) >= 0);
        }

        private static void RejectTournamentLinks(string path)
        {
            string current = path;
            while (!string.IsNullOrEmpty(current))
            {
                try
                {
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("赛事路径不能经过符号链接或重解析点：" + current);
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
                current = System.IO.Path.GetDirectoryName(current);
            }
        }
    }
}
