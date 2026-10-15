using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using StreamDuel.Core;

namespace StreamDuel.Cli
{
    internal static class ReplayStore
    {
        private static readonly Regex ReplayName = new Regex(
            @"^\d{8}_\d{6}_\d{7}_[A-Za-z0-9_-]+\.json$", RegexOptions.CultureInvariant);

        public static string Save(Battle battle, string directory)
        {
            if (battle.Phase != Phase.Over) throw new InvalidOperationException("对战尚未结束，不能自动保存回放。");
            if (!CliOptions.IsPackId(battle.Db.PackId)) throw new ArgumentException("回放内容包编号不是有效文件名。");
            string replayDirectory = Path.Combine(directory, "replays");
            Directory.CreateDirectory(replayDirectory);
            using (var replayLock = new FileStream(Path.Combine(replayDirectory, ".replays.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                // 时间只用来命名文件，不参与战斗或终端的可复现日志。
                DateTime timestamp = DateTime.UtcNow;
                string path;
                do
                {
                    path = Path.Combine(replayDirectory, timestamp.ToString("yyyyMMdd_HHmmss_fffffff", CultureInfo.InvariantCulture)
                        + "_" + battle.Db.PackId + ".json");
                    timestamp = timestamp.AddTicks(1);
                }
                while (File.Exists(path));

                string temporary = Path.Combine(replayDirectory, ".replay.json.tmp");
                bool created = false;
                try
                {
                    using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        created = true;
                        using (var writer = new StreamWriter(stream, new UTF8Encoding(false))) writer.Write(battle.Record.ToJson());
                    }
                    File.Move(temporary, path);
                    created = false;
                }
                finally
                {
                    if (created && File.Exists(temporary)) File.Delete(temporary);
                }

                // 只清理本工具命名的回放；新保存的这一局始终保留，即使系统时钟被回拨。
                try
                {
                    var older = new DirectoryInfo(replayDirectory).EnumerateFiles("*.json")
                        .Where(file => ReplayName.IsMatch(file.Name)
                            && (file.Attributes & FileAttributes.ReparsePoint) == 0
                            && !string.Equals(file.FullName, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(file => file.Name, StringComparer.Ordinal).ToList();
                    foreach (var file in older.Skip(49)) file.Delete();
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    // 新回放已经落盘，清理旧文件失败不应漏记本局战绩。
                    return "回放已保存，但旧回放清理失败，暂未满足最多 50 份：" + ex.Message;
                }
            }
            return null;
        }
    }
}
