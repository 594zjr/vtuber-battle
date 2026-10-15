using System.IO;
using System.Text;
using StreamDuel.Core;

namespace StreamDuel.Cli
{
    internal static class ProfileStore
    {
        public static Profile Record(Battle battle, string directory, int mySide = 0)
        {
            string path = Path.Combine(directory, "profile.json");
            Profile profile;
            Directory.CreateDirectory(directory);
            string temporary = Path.Combine(directory, ".profile.json.tmp");
            // 锁独立于临时文件，进程退出会释放锁，遗留临时文件不会阻止下一次保存。
            using (var profileLock = new FileStream(Path.Combine(directory, ".profile.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                bool created = false;
                try
                {
                    profile = File.Exists(path) ? Profile.FromJson(File.ReadAllText(path)) : new Profile();
                    profile.Record(battle, mySide);
                    using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        created = true;
                        using (var writer = new StreamWriter(stream, new UTF8Encoding(false))) writer.Write(profile.ToJson());
                    }
                    // 持锁到替换及清理完成，其他进程不能在读改写之间插入保存。
                    File.Move(temporary, path, true);
                    created = false;
                }
                finally
                {
                    if (created && File.Exists(temporary)) File.Delete(temporary);
                }
            }
            return profile;
        }
    }
}
