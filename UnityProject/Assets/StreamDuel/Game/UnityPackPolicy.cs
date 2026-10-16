using System.IO;
using StreamDuel.Core;
using UnityEngine;

namespace StreamDuel.Game
{
    internal static class UnityPackPolicy
    {
        internal static PackPolicy Load()
        {
            string path = Path.Combine(Application.streamingAssetsPath, "PackPolicy.json");
            bool deployed = true;
            // 只有确认文件不存在才回退，访问失败不能使已下架的内容重新开放。
            try { File.GetAttributes(path); }
            catch (FileNotFoundException) { deployed = false; }
            catch (DirectoryNotFoundException) { deployed = false; }
            if (deployed) return PackPolicy.FromJson(File.ReadAllText(path));
            var fallback = Resources.Load<TextAsset>("PackPolicy");
            return fallback == null ? PackPolicy.Empty : PackPolicy.FromJson(fallback.text);
        }
    }
}
