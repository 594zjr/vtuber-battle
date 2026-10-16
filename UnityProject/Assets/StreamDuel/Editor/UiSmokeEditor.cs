using System;
using System.IO;
using System.Linq;
using System.Reflection;
using StreamDuel.Game;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

[InitializeOnLoad]
public static class UiSmokeEditor
{
    static EditorWindow gameView;
    static int width, height;
    static bool stopping;
    static bool startPending;
    static int exitCode;
    static double started;
    static double stoppedAt = -1;
    static int stoppedUpdates;
    static bool BattleMode => Environment.GetCommandLineArgs().Contains("--battle-ui-smoke")
        || Environment.GetCommandLineArgs().Contains("--smoke-test");
    static string TaskId => BattleMode ? "T67" : "T59";
    static string ExitMarker => Path.Combine(Application.dataPath, "../Library/" + TaskId.ToLowerInvariant() + "-editor-exit.txt");
    static string ConsolePath => Path.Combine(Application.dataPath, "../Logs/" + TaskId + "/editor-console.json");

    [Serializable]
    sealed class ConsoleResult
    {
        public string unityVersion;
        public int errors, warnings, logs;
        public int exitErrors, exitWarnings, exitLogs;
        public bool exitSampled, exitWaitTimedOut;
        public bool completed;
    }

    static UiSmokeEditor()
    {
        if (Environment.GetCommandLineArgs().Contains("UiSmokeEditor.Run")
            || Environment.GetCommandLineArgs().Contains("BattleUiSmokeEditor.Run"))
        {
            started = EditorApplication.timeSinceStartup;
            EditorApplication.update += Poll;
        }
    }

    public static void Run()
    {
        if (File.Exists(ExitMarker)) File.Delete(ExitMarker);
        var args = Environment.GetCommandLineArgs();
        if (!args.Contains("--ui-smoke-test") && !BattleMode)
            throw new InvalidOperationException("编辑器验收必须提供隔离的冒烟输出目录。");
        if (args.Contains("--ui-smoke-test") && BattleMode)
            throw new ArgumentException("路由验收与对战验收不能同时启动。");
        int sizeIndex = Array.IndexOf(args, "--smoke-size");
        int initialWidth = 1280, initialHeight = 720;
        if (sizeIndex >= 0)
        {
            string size = sizeIndex + 1 < args.Length ? args[sizeIndex + 1] : string.Empty;
            if (size == "1920x1080") { initialWidth = 1920; initialHeight = 1080; }
            else if (size != "1280x720") throw new ArgumentException("--smoke-size 必须为 1280x720 或 1920x1080。");
        }
        EditorSceneManager.OpenScene("Assets/Scenes/Main.unity");
        SetSize(initialWidth, initialHeight);
        // 新工作树的搜索索引由启动回调创建，过早进入 Play 会使 Unity 跳过创建后访问空索引。
        // 命令行打开的隐藏编辑器可能尚未执行该回调，先在 Edit 模式完成同一初始化。
        var searchDatabase = Type.GetType("UnityEditor.Search.SearchDatabase, UnityEditor.QuickSearchModule", true);
        var getDefaultIndex = searchDatabase.GetMethod("GetDefaultSearchDatabase", BindingFlags.Static | BindingFlags.Public);
        if (getDefaultIndex == null) throw new MissingMethodException("当前 Unity 不支持验收所需的搜索索引初始化。");
        getDefaultIndex.Invoke(null, null);
        startPending = true;
    }

    static void SetSize(int newWidth, int newHeight)
    {
        if (width == newWidth && height == newHeight && gameView != null) return;
        var assembly = typeof(Editor).Assembly;
        var viewType = assembly.GetType("UnityEditor.GameView", true);
        gameView = EditorWindow.GetWindow(viewType);
        var sizesType = assembly.GetType("UnityEditor.GameViewSizes", true);
        var singleton = typeof(ScriptableSingleton<>).MakeGenericType(sizesType);
        object sizes = singleton.GetProperty("instance", BindingFlags.Public | BindingFlags.Static).GetValue(null);
        var groupType = assembly.GetType("UnityEditor.GameViewSizeGroupType", true);
        var group = sizesType.GetMethod("GetGroup").Invoke(sizes, new[] { Enum.Parse(groupType, "Standalone") });
        var sizeType = assembly.GetType("UnityEditor.GameViewSize", true);
        var sizeKind = assembly.GetType("UnityEditor.GameViewSizeType", true);
        object size = Activator.CreateInstance(sizeType, new[] { Enum.Parse(sizeKind, "FixedResolution"), (object)newWidth, newHeight, TaskId + " 验收" });
        // 尺寸仅在本次编辑器进程中添加，不调用全局尺寸配置的保存接口。
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
                bool settled = elapsed >= 2 && stoppedUpdates >= 4
                    && !EditorApplication.isCompiling && !EditorApplication.isUpdating;
                if (!settled && elapsed < 10) return;
                int code = int.Parse(File.ReadAllText(ExitMarker));
                // 停止 Play 后仍要观察清理阶段的 Console，不能只凭停止前的零计数退出。
                string consolePath = ConsolePath;
                var result = JsonUtility.FromJson<ConsoleResult>(File.ReadAllText(consolePath));
                var type = typeof(Editor).Assembly.GetType("UnityEditor.LogEntries", true);
                object[] counts = { 0, 0, 0 };
                type.GetMethod("GetCountsByType", BindingFlags.Static | BindingFlags.Public).Invoke(null, counts);
                result.exitErrors = (int)counts[0];
                result.exitWarnings = (int)counts[1];
                result.exitLogs = (int)counts[2];
                result.exitSampled = true;
                result.exitWaitTimedOut = !settled;
                File.WriteAllText(consolePath, JsonUtility.ToJson(result, true));
                if (!settled || result.exitErrors != 0 || result.exitWarnings != 0) code = 1;
                File.Delete(ExitMarker);
                EditorApplication.Exit(code);
                return;
            }
            if (stopping)
            {
                return;
            }
            if (EditorApplication.timeSinceStartup - started > (BattleMode ? 600 : 360))
            {
                Finish(false, 1);
                return;
            }
            if (startPending)
            {
                if (EditorApplication.isCompiling || EditorApplication.isUpdating
                    || !File.Exists(Path.Combine(Application.dataPath, "../UserSettings/Search.index"))) return;
                startPending = false;
                EditorApplication.isPlaying = true;
                return;
            }
            if (!EditorApplication.isPlaying) return;
            var args = Environment.GetCommandLineArgs();
            int uiIndex = Array.IndexOf(args, "--ui-smoke-test");
            if (uiIndex >= 0)
            {
                if (UiSmoke.RequestedWidth > 0) SetSize(UiSmoke.RequestedWidth, UiSmoke.RequestedHeight);
                if (UiSmoke.Completed) Finish(true, UiSmoke.ExitCode);
            }
            else
            {
                if (BattleUiSmoke.RequestedWidth > 0) SetSize(BattleUiSmoke.RequestedWidth, BattleUiSmoke.RequestedHeight);
                if (BattleMode && BattleUiSmoke.Completed)
                {
                    // 只读取本轮运行公开的精确路径，不扫描可能含旧 run-* 结果的输出目录。
                    string path = BattleUiSmoke.ReportPath;
                    if (string.IsNullOrEmpty(path) || !File.Exists(path))
                        throw new InvalidOperationException("本轮对战界面验收没有写出报告。");
                    Finish(true, BattleUiSmoke.ExitCode);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogException(ex);
            Finish(false, 1);
        }
    }

    static void Finish(bool completed, int requestedExitCode)
    {
        var type = typeof(Editor).Assembly.GetType("UnityEditor.LogEntries", true);
        object[] counts = { 0, 0, 0 };
        type.GetMethod("GetCountsByType", BindingFlags.Static | BindingFlags.Public).Invoke(null, counts);
        var result = new ConsoleResult
        {
            unityVersion = Application.unityVersion,
            errors = (int)counts[0], warnings = (int)counts[1], logs = (int)counts[2], completed = completed
        };
        string path = ConsolePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, JsonUtility.ToJson(result, true));
        exitCode = completed && result.errors == 0 && result.warnings == 0 ? requestedExitCode : 1;
        File.WriteAllText(ExitMarker, exitCode.ToString());
        stopping = true;
        EditorApplication.isPlaying = false;
    }
}
