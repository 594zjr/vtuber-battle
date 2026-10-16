using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using StreamDuel.Core;
using UnityEngine;
using UnityEngine.UIElements;

namespace StreamDuel.Game
{
    internal static partial class T66ReviewChecks
    {
        static readonly UTF8Encoding StorageUtf8 = new UTF8Encoding(false, true);

        public static IEnumerator RunStorage(ScreenRouter router, BattleScreen legacy, string outputDirectory)
        {
            StorageRequire(router != null && legacy != null, "存储回归需要已启动的真实界面。");
            StorageRequire(Environment.GetCommandLineArgs().Contains("--t66-smoke-test"), "存储回归仅允许 T66 隔离验收模式。");
            var smoke = typeof(BattleScreen).GetProperty("SmokeRequested", BindingFlags.Static | BindingFlags.NonPublic);
            var save = typeof(BattleScreen).GetField("saveStore", BindingFlags.Instance | BindingFlags.NonPublic);
            StorageRequire(smoke != null && (bool)smoke.GetValue(null)
                && save != null && save.GetValue(legacy) == null, "存储回归启动前必须已隔离玩家设置存档。");
            StorageCheckPath(outputDirectory);
            StorageRequire(Directory.Exists(outputDirectory), "存储回归输出父目录必须由 T66 验收创建。");
            string root = Path.Combine(Path.GetFullPath(outputDirectory), "storage-" + Guid.NewGuid().ToString("N"));
            StorageCheckPath(root);
            StorageRequire(!Directory.Exists(root) && !File.Exists(root), "存储回归必须使用新的独占目录。");
            Directory.CreateDirectory(root);
            StorageCheckPath(root);
            StorageEvidence(root, "开始；证据目录=" + root);

            var db = legacy.MenuPacks.FirstOrDefault(pack => pack.PackId == "original");
            StorageRequire(db != null, "存储回归缺少已加载的原创包。");
            var preset = new TeamPreset
            {
                Name = "  旧预设星标  ", PackId = db.PackId,
                Ids = TeamBuilder.Suggest(db, null, Array.Empty<string>(), 660066)
            };
            StorageRequire(preset.Check(db, null).Count == 0, "存储回归夹具必须是当前策略下的合法队伍。");
            string validJson = new TeamPresetList { Items = new List<TeamPreset> { preset } }.ToJson();
            byte[] validBytes = StorageUtf8.GetBytes(validJson);

            foreach (string kind in new[] { "bad-json", "bad-utf8" })
            {
                string directory = Path.Combine(root, kind);
                StorageCheckPath(directory);
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, "teams.json");
                byte[] badBytes = kind == "bad-json"
                    ? StorageUtf8.GetBytes("{\"items\":[") : StorageInvalidUtf8(validJson);
                StorageWrite(path, badBytes);
                DateTime badMtime = StorageSetTime(path, 3);
                StorageWrite(Path.Combine(directory, "initial-teams.bin"), badBytes);
                StorageEvidence(directory, "原始坏档：bytes=" + badBytes.Length + "；mtimeUtcTicks=" + badMtime.Ticks);

                StorageShowTeam(router, db, new FileTeamPresetStore(directory));
                yield return StorageSettle();
                StorageAssertBlocked(router);
                StorageAssertFile(path, badBytes, badMtime, "首次读取坏档");
                StorageSubmitDisabled(router, "team-preset-load");
                StorageSubmitDisabled(router, "team-preset-save");
                yield return StorageSettle();
                StorageAssertFile(path, badBytes, badMtime, "坏档禁用按钮");

                StorageSubmit(router, "team-auto");
                yield return StorageSettle();
                StorageRequire(TeamCheck.Validate(db, null, StoragePicked(router)).Count == 0,
                    "自动组队必须先产生合法队伍，才能独立验证仓库禁写。");
                StorageRequire(StorageElement<Button>(router, "team-start").enabledInHierarchy,
                    "合法队伍应能交接对战，不能用队伍非法掩盖预设禁写。");
                StorageAssertBlocked(router);
                StorageSubmitDisabled(router, "team-preset-save");
                yield return StorageSettle();
                StorageAssertFile(path, badBytes, badMtime, "合法队伍仍不得覆盖坏档");
                StorageWrite(Path.Combine(directory, "after-blocked-teams.bin"), File.ReadAllBytes(path));
                StorageEvidence(directory, "PASS 坏档提示中文、读取与保存禁用；合法组队及禁用按钮事件后字节和 mtime 均不变。");

                StorageWrite(path, validBytes);
                DateTime repairedMtime = StorageSetTime(path, 4);
                yield return StorageSettle();
                StorageAssertBlocked(router);
                StorageAssertFile(path, validBytes, repairedMtime, "仅修复磁盘还未刷新");
                StorageSubmit(router, "team-presets-refresh");
                yield return StorageSettle();
                StorageRequire(StorageElement<Button>(router, "team-preset-load").enabledInHierarchy
                    && StorageElement<Button>(router, "team-preset-save").enabledInHierarchy,
                    "明确刷新成功后应恢复预设读取和保存。");
                StorageRequire(string.IsNullOrEmpty(StorageElement<Label>(router, "team-storage-status").text),
                    "明确刷新成功后仍残留存储错误。");
                StorageAssertFile(path, validBytes, repairedMtime, "刷新只读修复后的预设");
                StorageSubmit(router, "team-preset-load");
                yield return StorageSettle();
                StorageRequire(StoragePicked(router).SequenceEqual(preset.Ids)
                    && StorageElement<TextField>(router, "team-preset-name").value == preset.Name,
                    "刷新后读取的原名或成员顺序被改写。");
                StorageAssertFile(path, validBytes, repairedMtime, "读取修复后的预设");

                string newName = kind + "恢复保存";
                StorageElement<TextField>(router, "team-preset-name").value = newName;
                StorageSubmit(router, "team-preset-save");
                yield return StorageSettle();
                var saved = new FileTeamPresetStore(directory).Load();
                StorageRequire(saved.Items.Count == 2
                    && saved.Items.Any(item => item.Name == preset.Name && item.PackId == preset.PackId && item.Ids.SequenceEqual(preset.Ids))
                    && saved.Items.Any(item => item.Name == newName && item.PackId == preset.PackId && item.Ids.SequenceEqual(preset.Ids)),
                    "刷新后真实保存未同时保留旧预设和新预设。");
                StorageRequire(string.IsNullOrEmpty(StorageElement<Label>(router, "team-storage-status").text)
                    && StorageElement<Label>(router, "team-status").text.Contains("预设已保存"), "正常保存未显示成功反馈。");
                StorageEvidence(directory, "PASS 磁盘修复不会自行解锁；真实刷新后读取、正常保存及原名/成员顺序往返通过。");
            }

            string bomDirectory = Path.Combine(root, "valid-utf8-bom");
            StorageCheckPath(bomDirectory);
            Directory.CreateDirectory(bomDirectory);
            string bomPath = Path.Combine(bomDirectory, "teams.json");
            byte[] bomBytes = new byte[] { 0xef, 0xbb, 0xbf }.Concat(validBytes).ToArray();
            StorageWrite(bomPath, bomBytes);
            DateTime bomMtime = StorageSetTime(bomPath, 5);
            StorageWrite(Path.Combine(bomDirectory, "initial-teams.bin"), bomBytes);
            StorageEvidence(bomDirectory, "原始 BOM 档：bytes=" + bomBytes.Length + "；mtimeUtcTicks=" + bomMtime.Ticks);
            var bomStore = new FileTeamPresetStore(bomDirectory);
            var loaded = bomStore.Load();
            StorageRequire(loaded.Items.Count == 1 && loaded.Items[0].Name == preset.Name
                && loaded.Items[0].PackId == preset.PackId && loaded.Items[0].Ids.SequenceEqual(preset.Ids),
                "真实存储未兼容带 UTF-8 BOM 的旧预设。");
            StorageAssertFile(bomPath, bomBytes, bomMtime, "直接读取 BOM 档");
            StorageShowTeam(router, db, bomStore);
            yield return StorageSettle();
            StorageRequire(StorageElement<Button>(router, "team-preset-load").enabledInHierarchy
                && string.IsNullOrEmpty(StorageElement<Label>(router, "team-storage-status").text), "BOM 旧预设被 UI 误判为坏档。");
            StorageSubmit(router, "team-preset-load");
            yield return StorageSettle();
            StorageRequire(StoragePicked(router).SequenceEqual(preset.Ids)
                && StorageElement<TextField>(router, "team-preset-name").value == preset.Name, "BOM 预设经真实读取按钮后未保留原名及成员顺序。");
            StorageSubmit(router, "team-presets-refresh");
            yield return StorageSettle();
            StorageAssertFile(bomPath, bomBytes, bomMtime, "UI 读取及刷新 BOM 档");
            StorageWrite(Path.Combine(bomDirectory, "after-load-teams.bin"), File.ReadAllBytes(bomPath));
            StorageEvidence(bomDirectory, "PASS BOM 档直接读取、真实 UI 读取及刷新均保留原字节和 mtime。");
            StorageEvidence(root, "PASS 全部存储回归通过；保留全部夹具与证据，不清理文件。");
            router.Show(ScreenIds.Menu);
            yield return StorageSettle();
        }

        static byte[] StorageInvalidUtf8(string validJson)
        {
            const string marker = "\"name\":\"";
            int index = validJson.IndexOf(marker, StringComparison.Ordinal);
            StorageRequire(index >= 0, "坏编码夹具缺少 name 字段。");
            index += marker.Length;
            return StorageUtf8.GetBytes(validJson.Substring(0, index)).Concat(new byte[] { 0xff })
                .Concat(StorageUtf8.GetBytes(validJson.Substring(index))).ToArray();
        }

        static void StorageShowTeam(ScreenRouter router, ContentDb db, ITeamPresetStore store)
        {
            router.Show(ScreenIds.Team, new TeamScreenArgs
            {
                Content = db, Packs = new[] { db }, Policy = db.Policy, Presets = store,
                Settings = new GameSettings { DefaultPack = db.PackId, AiLevel = AiLevel.Normal, FastPlay = true, TurnTimer = false },
                Seed = 660066, ReadClipboard = () => "", WriteClipboard = value => { },
                StartBattle = request => { throw new InvalidOperationException("存储回归不应开始对战。"); },
                Return = () => router.Show(ScreenIds.Menu)
            });
        }

        static IEnumerator StorageSettle()
        {
            yield return null;
            yield return null;
            yield return new WaitForEndOfFrame();
        }

        static T StorageElement<T>(ScreenRouter router, string name) where T : VisualElement
        {
            var element = router.Document.rootVisualElement.Q<T>(name);
            StorageRequire(element != null, "存储回归缺少真实 UI 节点：" + name);
            return element;
        }

        static List<string> StoragePicked(ScreenRouter router)
        {
            var ids = new List<string>();
            for (int i = 0; i < 6; i++)
                if (StorageElement<Button>(router, "team-slot-" + i).userData is string id && !string.IsNullOrEmpty(id)) ids.Add(id);
            return ids;
        }

        static void StorageSubmit(ScreenRouter router, string name)
        {
            var button = StorageElement<Button>(router, name);
            StorageRequire(button.enabledInHierarchy && button.visible, "存储回归按钮不可提交：" + name);
            button.Focus();
            using (var evt = NavigationSubmitEvent.GetPooled()) { evt.target = button; button.SendEvent(evt); }
        }

        static void StorageSubmitDisabled(ScreenRouter router, string name)
        {
            var button = StorageElement<Button>(router, name);
            StorageRequire(!button.enabledInHierarchy, "坏档保护期间按钮必须禁用：" + name);
            using (var evt = NavigationSubmitEvent.GetPooled()) { evt.target = button; button.SendEvent(evt); }
        }

        static void StorageAssertBlocked(ScreenRouter router)
        {
            StorageRequire(!StorageElement<Button>(router, "team-preset-load").enabledInHierarchy
                && !StorageElement<Button>(router, "team-preset-save").enabledInHierarchy, "坏档必须禁用预设读取和保存。");
            string message = StorageElement<Label>(router, "team-storage-status").text;
            StorageRequire(message.Contains("无法读取预设") && message.Contains("保留原数据") && message.Contains("刷新"),
                "坏档须显示保留原数据和刷新重试的中文提示。");
        }

        static void StorageWrite(string path, byte[] bytes)
        {
            StorageCheckPath(path);
            File.WriteAllBytes(path, bytes);
        }

        static DateTime StorageSetTime(string path, int day)
        {
            StorageCheckPath(path);
            File.SetLastWriteTimeUtc(path, new DateTime(2001, 2, day, 4, 5, 6, DateTimeKind.Utc));
            return File.GetLastWriteTimeUtc(path);
        }

        static void StorageAssertFile(string path, byte[] bytes, DateTime mtime, string phase)
        {
            StorageCheckPath(path);
            StorageRequire(File.ReadAllBytes(path).SequenceEqual(bytes), phase + " 改写了预设原字节。");
            StorageRequire(File.GetLastWriteTimeUtc(path) == mtime, phase + " 改变了预设修改时间。");
        }

        static void StorageEvidence(string directory, string message)
        {
            string path = Path.Combine(directory, "storage-evidence.txt");
            StorageCheckPath(path);
            File.AppendAllText(path, message + "\n", StorageUtf8);
        }

        static void StorageCheckPath(string path)
        {
            StorageRequire(!string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path), "存储回归路径必须是绝对路径。");
            string project = Path.GetFullPath(T66TeamSmoke.ProjectDirectory()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string allowed = Path.Combine(project, "Logs", "T-66");
            string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            StringComparison comparison = Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            StorageRequire(full.StartsWith(allowed + Path.DirectorySeparatorChar, comparison), "存储回归只能访问本工程 Logs/T-66 的子目录。");
            for (string current = full; ; current = Path.GetDirectoryName(current))
            {
                try
                {
                    StorageRequire((File.GetAttributes(current) & FileAttributes.ReparsePoint) == 0,
                        "存储回归路径不能包含链接或重解析点。");
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
                if (string.Equals(current, project, comparison)) break;
                StorageRequire(!string.IsNullOrEmpty(current) && current.StartsWith(project + Path.DirectorySeparatorChar, comparison),
                    "存储回归路径祖先越过当前工程。");
            }
        }

        static void StorageRequire(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
