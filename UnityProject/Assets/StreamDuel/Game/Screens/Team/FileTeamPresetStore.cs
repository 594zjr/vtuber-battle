using System;
using System.Collections.Generic;
using System.IO;
using StreamDuel.Core;

namespace StreamDuel.Game
{
    public sealed class FileTeamPresetStore : ITeamPresetStore
    {
        readonly string root;
        readonly string path;
        FileSaveStore store;

        public FileTeamPresetStore(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("预设存档目录不能为空。", nameof(directory));
            root = Path.GetFullPath(directory);
            path = Path.Combine(root, "teams.json");
        }

        public TeamPresetList Load()
        {
            CheckPath();
            string json;
            try { json = File.ReadAllText(path); }
            catch (FileNotFoundException) { return new TeamPresetList(); }
            catch (DirectoryNotFoundException) { return new TeamPresetList(); }
            // Core 的容错入口返回空列表；界面须区分损坏文件，不能下一次保存便覆盖原数据。
            var data = MiniJson.Parse(json) as Dictionary<string, object>;
            if (data == null || !data.TryGetValue("items", out object value) || !(value is List<object> items))
                throw new FormatException("队伍预设存档格式损坏，已保留原文件。");
            var presets = TeamPresetList.FromJson(json);
            if (presets.Items.Count != items.Count) throw new FormatException("队伍预设存档内容损坏，已保留原文件。");
            return Store.LoadTeams();
        }

        public void Save(TeamPresetList presets)
        {
            if (presets == null) throw new ArgumentNullException(nameof(presets));
            CheckPath();
            Store.SaveTeams(presets);
        }

        FileSaveStore Store => store ?? (store = new FileSaveStore(root));

        void CheckPath()
        {
            var parents = new Stack<string>();
            for (var current = new DirectoryInfo(root); current != null && current.Parent != null; current = current.Parent)
                parents.Push(current.FullName);
            while (parents.Count > 0) CheckFile(parents.Pop());
            CheckFile(path);
        }

        static void CheckFile(string name)
        {
            try
            {
                if ((File.GetAttributes(name) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("预设存档不能使用符号链接或重解析点。");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}
