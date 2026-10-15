using StreamDuel.Core;

namespace StreamDuel.Cli
{
    internal static class ReplayStore
    {
        public static string Save(Battle battle, string directory)
        {
            try
            {
                new FileSaveStore(directory).SaveReplay(battle);
            }
            catch (ReplayRetentionException ex)
            {
                // 新回放已经落盘，清理旧文件失败不应漏记本局战绩。
                return ex.Message;
            }
            return null;
        }
    }
}
