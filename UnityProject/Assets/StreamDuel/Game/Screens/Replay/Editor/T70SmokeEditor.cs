#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using StreamDuel.Game;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

[InitializeOnLoad]
public static class T70SmokeEditor
{
    const string StatePrefix = "StreamDuel.T70SmokeEditor.";
    [Serializable]
    sealed class ConsoleResult
    {
        public string unityVersion;
        public bool completed, exitSampled, exitWaitTimedOut;
        public int errors, warnings, logs, exitErrors, exitWarnings, exitLogs;
    }

    [Serializable]
    sealed class ExitData
    {
        public string consolePath;
        public int code;
    }

    static EditorWindow gameView;
    static int width, height;
    static bool startPending
    {
        get => SessionState.GetBool(StatePrefix + "startPending", false);
        set => SessionState.SetBool(StatePrefix + "startPending", value);
    }
    static bool stopping
    {
        get => SessionState.GetBool(StatePrefix + "stopping", false);
        set => SessionState.SetBool(StatePrefix + "stopping", value);
    }
    static double stoppedAt = -1;
    static int stoppedUpdates;
    static string Marker => Path.Combine(Application.dataPath, "../Library/t70-editor-exit.json");

    static T70SmokeEditor()
    {
        if (!Environment.GetCommandLineArgs().Contains("T70SmokeEditor.Run")) return;
        EditorApplication.update += Poll;
    }

    public static void Run()
    {
        SessionState.SetFloat(StatePrefix + "started", (float)EditorApplication.timeSinceStartup);
        SessionState.SetBool(StatePrefix + "active", true);
        SessionState.SetInt(StatePrefix + "originalCustomCount", -1);
        startPending = stopping = false;
        OutputRoot();
        if (File.Exists(Marker)) File.Delete(Marker);
        EditorSceneManager.OpenScene("Assets/Scenes/Main.unity");
        SetSize(1280, 720);
        // 首导入的搜索索引须在 Edit 模式初始化，避免 Play 中访问尚未生成的索引。
        var searchType = Type.GetType("UnityEditor.Search.SearchDatabase, UnityEditor.QuickSearchModule", true);
        var initialize = searchType.GetMethod("GetDefaultSearchDatabase", BindingFlags.Static | BindingFlags.Public);
        if (initialize == null) throw new MissingMethodException("当前 Unity 缺少搜索索引初始化接口。");
        initialize.Invoke(null, null);
        startPending = true;
    }

    static string OutputRoot()
    {
        string[] args = Environment.GetCommandLineArgs();
        int index = Array.IndexOf(args, "--ui-smoke-test");
        if (index < 0 || index + 1 >= args.Length || args.Count(value => value == "--ui-smoke-test") != 1
            || !Path.IsPathFullyQualified(args[index + 1]))
            throw new ArgumentException("--ui-smoke-test 需要工程内的绝对输出目录。");
        if (!args.Contains("--t70-smoke")) throw new ArgumentException("T70 入口需要 --t70-smoke。");
        string project = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        string allowed = Path.Combine(project, "Logs", "T70").TrimEnd(Path.DirectorySeparatorChar);
        string output = Path.GetFullPath(args[index + 1]);
        if (!string.Equals(output.TrimEnd(Path.DirectorySeparatorChar), allowed, StringComparison.OrdinalIgnoreCase)
            && !output.StartsWith(allowed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("T70 验收目录必须位于当前 UnityProject/Logs/T70 内。");
        for (var current = new DirectoryInfo(output); current != null; current = current.Parent)
        {
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("T70 验收目录不能经过重解析点。");
            if (string.Equals(current.FullName.TrimEnd(Path.DirectorySeparatorChar),
                project.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) break;
        }
        return output;
    }

    static void SetSize(int newWidth, int newHeight)
    {
        if (!((newWidth == 1280 && newHeight == 720) || (newWidth == 1920 && newHeight == 1080)))
            throw new ArgumentException("T70 验收仅允许 1280×720 和 1920×1080。");
        if (width == newWidth && height == newHeight && gameView != null) return;
        var assembly = typeof(Editor).Assembly;
        var viewType = assembly.GetType("UnityEditor.GameView", true);
        gameView = EditorWindow.GetWindow(viewType);
        var sizesType = assembly.GetType("UnityEditor.GameViewSizes", true);
        var singleton = typeof(ScriptableSingleton<>).MakeGenericType(sizesType);
        object sizes = singleton.GetProperty("instance", BindingFlags.Public | BindingFlags.Static).GetValue(null);
        var groupType = assembly.GetType("UnityEditor.GameViewSizeGroupType", true);
        object group = sizesType.GetMethod("GetGroup").Invoke(sizes, new[] { Enum.Parse(groupType, "Standalone") });
        if (SessionState.GetInt(StatePrefix + "originalCustomCount", -1) < 0)
        {
            SessionState.SetInt(StatePrefix + "originalCustomCount",
                (int)group.GetType().GetMethod("GetCustomCount").Invoke(group, null));
            SessionState.SetInt(StatePrefix + "originalSelectedSize",
                (int)viewType.GetProperty("selectedSizeIndex", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .GetValue(gameView));
        }
        var sizeType = assembly.GetType("UnityEditor.GameViewSize", true);
        var sizeKind = assembly.GetType("UnityEditor.GameViewSizeType", true);
        object size = Activator.CreateInstance(sizeType,
            new[] { Enum.Parse(sizeKind, "FixedResolution"), (object)newWidth, newHeight, "T70 验收" });
        // 只添加本进程临时尺寸，不调用全局 GameView 配置保存接口。
        group.GetType().GetMethod("AddCustomSize").Invoke(group, new[] { size });
        int total = (int)group.GetType().GetMethod("GetTotalCount").Invoke(group, null);
        viewType.GetProperty("selectedSizeIndex", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .SetValue(gameView, total - 1);
        gameView.Show();
        gameView.Focus();
        width = newWidth;
        height = newHeight;
    }

    static void RestoreSizes()
    {
        int originalCount = SessionState.GetInt(StatePrefix + "originalCustomCount", -1);
        if (originalCount < 0) return;
        var assembly = typeof(Editor).Assembly;
        var sizesType = assembly.GetType("UnityEditor.GameViewSizes", true);
        var singleton = typeof(ScriptableSingleton<>).MakeGenericType(sizesType);
        object sizes = singleton.GetProperty("instance", BindingFlags.Public | BindingFlags.Static).GetValue(null);
        var groupType = assembly.GetType("UnityEditor.GameViewSizeGroupType", true);
        object group = sizesType.GetMethod("GetGroup").Invoke(sizes, new[] { Enum.Parse(groupType, "Standalone") });
        int count = (int)group.GetType().GetMethod("GetCustomCount").Invoke(group, null);
        int builtIn = (int)group.GetType().GetMethod("GetBuiltinCount").Invoke(group, null);
        if (gameView != null)
            gameView.GetType().GetProperty("selectedSizeIndex", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .SetValue(gameView, SessionState.GetInt(StatePrefix + "originalSelectedSize", 0));
        // 退出前撤回本进程添加的尺寸，域重载也不留下验收项；不调用全局保存接口。
        for (int index = count - 1; index >= originalCount; index--)
            group.GetType().GetMethod("RemoveCustomSize").Invoke(group, new object[] { builtIn + index });
        SessionState.SetInt(StatePrefix + "originalCustomCount", -1);
    }

    static int[] ConsoleCounts()
    {
        var type = typeof(Editor).Assembly.GetType("UnityEditor.LogEntries", true);
        object[] values = { 0, 0, 0 };
        type.GetMethod("GetCountsByType", BindingFlags.Static | BindingFlags.Public).Invoke(null, values);
        return new[] { (int)values[0], (int)values[1], (int)values[2] };
    }

    static void Poll()
    {
        if (!SessionState.GetBool(StatePrefix + "active", false)) return;
        try
        {
            if (!EditorApplication.isPlayingOrWillChangePlaymode && File.Exists(Marker))
            {
                if (stoppedAt < 0) stoppedAt = EditorApplication.timeSinceStartup;
                stoppedUpdates++;
                double elapsed = EditorApplication.timeSinceStartup - stoppedAt;
                bool settled = elapsed >= 2 && stoppedUpdates >= 4
                    && !EditorApplication.isCompiling && !EditorApplication.isUpdating;
                if (!settled && elapsed < 10) return;
                var data = JsonUtility.FromJson<ExitData>(File.ReadAllText(Marker));
                var result = JsonUtility.FromJson<ConsoleResult>(File.ReadAllText(data.consolePath));
                int[] counts = ConsoleCounts();
                result.exitErrors = counts[0];
                result.exitWarnings = counts[1];
                result.exitLogs = counts[2];
                result.exitSampled = true;
                result.exitWaitTimedOut = !settled;
                File.WriteAllText(data.consolePath, JsonUtility.ToJson(result, true));
                int code = settled && counts[0] == 0 && counts[1] == 0 ? data.code : 1;
                File.Delete(Marker);
                SessionState.SetBool(StatePrefix + "active", false);
                EditorApplication.Exit(code);
                return;
            }
            if (stopping) return;
            if (EditorApplication.timeSinceStartup - SessionState.GetFloat(StatePrefix + "started", 0) > 660)
            { Finish(false, 1); return; }
            if (startPending)
            {
                if (EditorApplication.isCompiling || EditorApplication.isUpdating
                    || !File.Exists(Path.Combine(Application.dataPath, "../UserSettings/Search.index"))) return;
                startPending = false;
                EditorApplication.isPlaying = true;
                return;
            }
            if (!EditorApplication.isPlaying) return;
            if (UiSmoke.RequestedWidth > 0) SetSize(UiSmoke.RequestedWidth, UiSmoke.RequestedHeight);
            if (UiSmoke.Completed) Finish(UiSmoke.ExitCode == 0, UiSmoke.ExitCode);
        }
        catch (Exception ex)
        {
            Debug.LogException(ex);
            try { Finish(false, 1); }
            catch (Exception finishError)
            {
                Debug.LogException(finishError);
                SessionState.SetBool(StatePrefix + "active", false);
                EditorApplication.Exit(1);
            }
        }
    }

    static void Finish(bool completed, int requestedCode)
    {
        if (stopping) return;
        RestoreSizes();
        int[] counts = ConsoleCounts();
        var result = new ConsoleResult
        {
            unityVersion = Application.unityVersion, completed = completed,
            errors = counts[0], warnings = counts[1], logs = counts[2]
        };
        string directory = string.IsNullOrEmpty(UiSmoke.ReportPath)
            ? Path.Combine(OutputRoot(), "editor-failure-" + Guid.NewGuid().ToString("N"))
            : Path.GetDirectoryName(UiSmoke.ReportPath);
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "editor-console.json");
        File.WriteAllText(path, JsonUtility.ToJson(result, true));
        var data = new ExitData
        {
            consolePath = path,
            code = completed && counts[0] == 0 && counts[1] == 0 ? requestedCode : 1
        };
        File.WriteAllText(Marker, JsonUtility.ToJson(data));
        stopping = true;
        EditorApplication.isPlaying = false;
    }
}
#endif
