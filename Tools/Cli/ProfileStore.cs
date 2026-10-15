using System.IO;
using System.Text;
using StreamDuel.Core;

namespace StreamDuel.Cli
{
    internal static class ProfileStore
    {
        public static Profile Record(Battle battle, string directory)
        {
            string path = Path.Combine(directory, "profile.json");
            Profile profile;
            Directory.CreateDirectory(directory);
            string temporary = Path.Combine(directory, ".profile.json.tmp");
            bool created = false;
            try
            {
                // 先独占临时文件再读旧档，避免同时结束的两局都按同一份旧计数写回。
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    created = true;
                    profile = File.Exists(path) ? Profile.FromJson(File.ReadAllText(path)) : new Profile();
                    profile.Record(battle, 0);
                    using (var writer = new StreamWriter(stream, new UTF8Encoding(false))) writer.Write(profile.ToJson());
                }
                // 写完临时文件才替换旧档案，写入失败时仍保留上一次战绩。
                File.Move(temporary, path, true);
                created = false;
            }
            finally
            {
                if (created && File.Exists(temporary)) File.Delete(temporary);
            }
            return profile;
        }
    }
}
