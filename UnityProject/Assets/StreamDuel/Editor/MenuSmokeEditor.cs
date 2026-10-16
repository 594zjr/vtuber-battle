using System;
using System.IO;
using System.Linq;
using System.Reflection;
using StreamDuel.Game;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace StreamDuel.Editor
{
    [InitializeOnLoad]
    public static class MenuSmokeEditor
    {
        static EditorWindow gameView;
        static int width, height;
        static bool stopping, startPending;
        static double started, stoppedAt = -1;
        static int stoppedUpdates;
        static string ExitMarker => Path.Combine(Application.dataPath, "../Library/t81-editor-exit.txt");
        static string ConsolePath
        {
            get
            {
                string[] args = Environment.GetCommandLineArgs();
                int index = Array.IndexOf(args, "--menu-smoke-test");
                string directory = index >= 0 && index + 1 < args.Length && Path.IsPathFullyQualified(args[index + 1])
                    ? args[index + 1] : Path.GetFullPath(Path.Combine(Application.dataPath, "../Logs/T81"));
                return Path.Combine(directory, "editor-console.json");
            }
        }

        [Serializable]
        sealed class ConsoleResult
        {
            public string unityVersion, runtimeReport;
            public int errors, warnings, logs, exitErrors, exitWarnings, exitLogs;
            public bool exitSampled, exitWaitTimedOut, completed;
        }

        static MenuSmokeEditor()
        {
            if (!Environment.GetCommandLineArgs().Contains("StreamDuel.Editor.MenuSmokeEditor.Run")) return;
            started = EditorApplication.timeSinceStartup;
            EditorApplication.update += Poll;
        }

        public static void Run()
        {
            if (File.Exists(ExitMarker)) File.Delete(ExitMarker);
            string[] args = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(args, "--menu-smoke-test");
            if (index < 0 || index + 1 >= args.Length || !Path.IsPathFullyQualified(args[index + 1]))
                throw new ArgumentException("主菜单验收必须提供 --menu-smoke-test 和绝对输出目录。");
            EditorSceneManager.OpenScene("Assets/Scenes/Main.unity");
            SetSize(1280, 720);
            // 新工作树须先完成编辑器默认索引初始化，避免进入 Play 时访问尚未建立的索引。
            var searchDatabase = Type.GetType("UnityEditor.Search.SearchDatabase, UnityEditor.QuickSearchModule", true);
            var getDefaultIndex = searchDatabase.GetMethod("GetDefaultSearchDatabase", BindingFlags.Static | BindingFlags.Public);
            if (getDefaultIndex == null) throw new MissingMethodException("当前 Unity 不支持验收所需的搜索索引初始化。");
            getDefaultIndex.Invoke(null, null);
            startPending = true;
        }

        static void SetSize(int newWidth, int newHeight)
        {
            if (width == newWidth && height == newHeight && gameView != null) return;
            var assembly = typeof(UnityEditor.Editor).Assembly;
            var viewType = assembly.GetType("UnityEditor.GameView", true);
            gameView = EditorWindow.GetWindow(viewType);
            var sizesType = assembly.GetType("UnityEditor.GameViewSizes", true);
            var singleton = typeof(ScriptableSingleton<>).MakeGenericType(sizesType);
            object sizes = singleton.GetProperty("instance", BindingFlags.Public | BindingFlags.Static).GetValue(null);
            var groupType = assembly.GetType("UnityEditor.GameViewSizeGroupType", true);
            var group = sizesType.GetMethod("GetGroup").Invoke(sizes, new[] { Enum.Parse(groupType, "Standalone") });
            var sizeType = assembly.GetType("UnityEditor.GameViewSize", true);
            var sizeKind = assembly.GetType("UnityEditor.GameViewSizeType", true);
            object size = Activator.CreateInstance(sizeType, new[] { Enum.Parse(sizeKind, "FixedResolution"), (object)newWidth, newHeight, "T81 验收" });
            // 尺寸只在当前进程添加；不保存 Unity 的全局 GameView 配置。
            group.GetType().GetMethod("AddCustomSize").Invoke(group, new[] { size });
            int total = (int)group.GetType().GetMethod("GetTotalCount").Invoke(group, null);
            viewType.GetProperty("selectedSizeIndex", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).SetValue(gameView, total - 1);
            gameView.Show();
            gameView.Focus();
            width = newWidth;
            height = newHeight;
        }

        static void Poll()
        {
            try
            {
                if (!EditorApplication.isPlayingOrWillChangePlaymode && File.Exists(ExitMarker))
                {
                    if (stoppedAt < 0) stoppedAt = EditorApplication.timeSinceStartup;
                    stoppedUpdates++;
                    double elapsed = EditorApplication.timeSinceStartup - stoppedAt;
                    bool settled = elapsed >= 2 && stoppedUpdates >= 4 && !EditorApplication.isCompiling && !EditorApplication.isUpdating;
                    if (!settled && elapsed < 10) return;
                    int code = int.Parse(File.ReadAllText(ExitMarker));
                    // 停止 Play 后再次采样，清理阶段出现的问题仍会令本轮失败。
                    var result = JsonUtility.FromJson<ConsoleResult>(File.ReadAllText(ConsolePath));
                    object[] counts = ConsoleCounts();
                    result.exitErrors = (int)counts[0];
                    result.exitWarnings = (int)counts[1];
                    result.exitLogs = (int)counts[2];
                    result.exitSampled = true;
                    result.exitWaitTimedOut = !settled;
                    File.WriteAllText(ConsolePath, JsonUtility.ToJson(result, true));
                    if (!settled || result.exitErrors != 0 || result.exitWarnings != 0) code = 1;
                    File.Delete(ExitMarker);
                    EditorApplication.Exit(code);
                    return;
                }
                if (stopping) return;
                if (EditorApplication.timeSinceStartup - started > 360) { Finish(false, 1); return; }
                if (startPending)
                {
                    if (EditorApplication.isCompiling || EditorApplication.isUpdating
                        || !File.Exists(Path.Combine(Application.dataPath, "../UserSettings/Search.index"))) return;
                    startPending = false;
                    EditorApplication.isPlaying = true;
                    return;
                }
                if (!EditorApplication.isPlaying) return;
                if (MenuSmoke.RequestedWidth > 0) SetSize(MenuSmoke.RequestedWidth, MenuSmoke.RequestedHeight);
                if (MenuSmoke.Completed) Finish(MenuSmoke.ExitCode == 0, MenuSmoke.ExitCode);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                Finish(false, 1);
            }
        }

        static object[] ConsoleCounts()
        {
            var type = typeof(UnityEditor.Editor).Assembly.GetType("UnityEditor.LogEntries", true);
            object[] counts = { 0, 0, 0 };
            type.GetMethod("GetCountsByType", BindingFlags.Static | BindingFlags.Public).Invoke(null, counts);
            return counts;
        }

        static void Finish(bool completed, int requestedExitCode)
        {
            if (stopping) return;
            object[] counts = ConsoleCounts();
            var result = new ConsoleResult
            {
                unityVersion = Application.unityVersion, runtimeReport = MenuSmoke.ReportPath,
                errors = (int)counts[0], warnings = (int)counts[1], logs = (int)counts[2], completed = completed
            };
            Directory.CreateDirectory(Path.GetDirectoryName(ConsolePath));
            File.WriteAllText(ConsolePath, JsonUtility.ToJson(result, true));
            int exitCode = completed && result.errors == 0 && result.warnings == 0 ? requestedExitCode : 1;
            File.WriteAllText(ExitMarker, exitCode.ToString());
            stopping = true;
            EditorApplication.isPlaying = false;
        }
    }
}
