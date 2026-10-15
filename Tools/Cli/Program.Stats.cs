using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using StreamDuel.Core;

namespace StreamDuel.Cli
{
    internal static partial class Program
    {
        static int PrintStats(CliOptions options)
        {
            var policy = PackPolicy.LoadDefault();
            var profile = StatsDirectoryExists(options.SaveDir)
                ? new FileSaveStore(options.SaveDir).LoadProfile() : new Profile();
            string libraryPath = ReplayLibraryDirectory(options);
            var entries = StatsDirectoryExists(libraryPath)
                ? new ReplayLibrary(libraryPath, policy: policy).ReadStatsSnapshot() : new List<ReplayEntry>();
            var contents = LoadStatsContents(options, policy);
            string packId = null;
            if (options.PackSpecified)
            {
                // 历史查询可用已下架包的内部编号，不能借游戏加载入口绕过政策取回旧定义。
                bool knownId = policy.IsPackDisabled(options.Pack) || profile.Packs.ContainsKey(options.Pack)
                    || entries.Any(entry => !entry.IsCorrupt && entry.PackId == options.Pack)
                    || contents.Any(db => db.PackId == options.Pack);
                packId = knownId ? options.Pack
                    : LoadPack(Path.Combine(AppContext.BaseDirectory, "Content"), options, policy).PackId;
                var selectedProfile = new Profile();
                if (profile.Packs.TryGetValue(packId, out var pack)) selectedProfile.Packs.Add(packId, pack);
                profile = selectedProfile;
                entries = entries.Where(entry => entry.IsCorrupt || entry.PackId == packId).ToList();
            }
            var stats = new ProfileStats(profile, entries, contents, policy);
            Console.WriteLine("本机对战统计（只读）");
            Console.WriteLine("内容包范围：" + (packId == null ? "全部" : OneLine(packId)));
            Console.WriteLine("选用次数和胜率优先使用累计档案；伤害、击倒、阵容、电脑战绩和连胜只统计保留录像样本。");
            Console.WriteLine("选用包含本局队伍中未登场的成员；胜率为胜场除以选用局数，平局也计入局数。");
            Console.WriteLine("保留录像：" + entries.Count(entry => !entry.IsCorrupt) + " 局；含本方详细数据："
                + entries.Count(entry => !entry.IsCorrupt && entry.Stats != null) + " 局；损坏：" + entries.Count(entry => entry.IsCorrupt) + " 份。");
            Console.WriteLine("旧录像未保存视角或难度时显示未知，不推测本方或电脑等级；未知结果与平局均中断连胜/连败。");
            Console.WriteLine("含已下架内容的录像：" + entries.Count(entry => !entry.IsCorrupt && entry.IsUnavailable)
                + " 局；历史计数保留，相关主播显示为「已下架」，这些录像不可重放。");

            Console.WriteLine("\n主播选用：");
            var streamers = stats.ByStreamer();
            if (streamers.Count == 0) Console.WriteLine("  暂无数据。");
            foreach (var item in streamers)
                Console.WriteLine("  " + OneLine(item.PackId) + " / " + OneLine(item.Name) + "：选用 " + item.Games
                    + " 次；胜率 " + StatsPercent(item.WinRate) + "；平均造成伤害 "
                    + (item.SampleGames == 0 ? "未知" : item.AverageDamage.ToString("0.##", CultureInfo.InvariantCulture))
                    + "；击倒 " + (item.SampleGames == 0 ? "未知" : item.Kos.ToString(CultureInfo.InvariantCulture))
                    + "（伤害/击倒样本 " + item.SampleGames + " 局；选用来源：" + (item.CountsFromProfile ? "累计档案" : "保留录像") + "）。");

            Console.WriteLine("\n常用阵容（前 10）：");
            var teams = stats.TopTeams(10);
            if (teams.Count == 0) Console.WriteLine("  暂无已知本方阵容数据。");
            foreach (var item in teams)
                Console.WriteLine("  " + OneLine(item.PackId) + " / " + string.Join("、", item.Names.Select(OneLine))
                    + "：" + item.Games + " 局；" + item.Wins + " 胜 / " + item.Losses + " 负 / " + item.Draws
                    + " 平；胜率 " + StatsPercent(item.WinRate));

            Console.WriteLine("\n对电脑战绩：");
            var levels = stats.VsAi();
            if (levels.Count == 0) Console.WriteLine("  暂无已知电脑难度数据。");
            foreach (var item in levels)
                Console.WriteLine("  " + LevelName(item.Level) + "：" + item.Wins + " 胜 / " + item.Losses + " 负 / "
                    + item.Draws + " 平；胜率 " + StatsPercent(item.WinRate));

            Console.WriteLine("\n最近对局（前 10）：");
            var recent = stats.Recent(10);
            if (recent.Count == 0) Console.WriteLine("  暂无录像。");
            foreach (var item in recent)
                Console.WriteLine("  " + item.Date.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture)
                    + " · " + OneLine(item.PackId) + " · " + string.Join(" / ", item.Names.Select(name => TextInput.Clean(name, TextInputKind.PlayerName).Text))
                    + " · 本方结果：" + StatsOutcome(item.Outcome) + " · " + item.Turns + " 回合 · 对手电脑："
                    + (item.OpponentAiLevel.HasValue ? LevelName(item.OpponentAiLevel.Value) : "未知或玩家") + " · 编号：" + OneLine(item.Id));
            var streak = stats.Streaks();
            Console.WriteLine("\n保留录像中的连胜/连败：当前连胜 " + streak.CurrentWins + "；当前连败 "
                + streak.CurrentLosses + "；最长连胜 " + streak.LongestWins + "。");
            return 0;
        }

        static string StatsPercent(double value) { return (value * 100).ToString("0.##", CultureInfo.InvariantCulture) + "%"; }

        static string StatsOutcome(ProfileBattleOutcome? value)
        {
            if (!value.HasValue) return "未知";
            if (value.Value == ProfileBattleOutcome.Win) return "胜";
            return value.Value == ProfileBattleOutcome.Loss ? "负" : "平";
        }

        static List<ContentDb> LoadStatsContents(CliOptions options, PackPolicy policy)
        {
            string directory = Path.Combine(AppContext.BaseDirectory, "Content");
            string rules = File.ReadAllText(Path.Combine(directory, "StreamDuelRules.json"));
            var result = new List<ContentDb>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var folders = new List<string> { Path.Combine(directory, "Packs") };
            if (options.PackDirectory != null) folders.Add(options.PackDirectory);
            foreach (string folder in folders)
                foreach (var loaded in PackLoader.LoadDirectory(rules, folder, policy))
                {
                    if (loaded.Db == null) Console.Error.WriteLine("[提醒] 统计显示名未载入内容包 " + OneLine(loaded.FileName) + "：" + OneLine(loaded.Error));
                    else if (ids.Add(loaded.Db.PackId)) result.Add(loaded.Db);
                }
            return result;
        }

        static bool StatsDirectoryExists(string directory)
        {
            string path = Path.GetFullPath(directory);
            bool exists = true, first = true;
            // Directory.Exists 会吞掉权限错误；逐级核对也不能遗漏不存在目录的重解析父路径。
            while (path != null)
            {
                try
                {
                    var attributes = File.GetAttributes(path);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("统计存档路径不能经过重解析点。");
                    if ((attributes & FileAttributes.Directory) == 0) throw new IOException("统计存档路径必须是目录。");
                }
                catch (FileNotFoundException) { if (first) exists = false; }
                catch (DirectoryNotFoundException) { if (first) exists = false; }
                first = false;
                path = Path.GetDirectoryName(path);
            }
            return exists;
        }
    }
}
