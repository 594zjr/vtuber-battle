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
    static int exitCode;
    static double started;
    static string ExitMarker => Path.Combine(Application.dataPath, "../Library/t59-editor-exit.txt");

    [Serializable]
    sealed class ConsoleResult
    {
        public string unityVersion;
        public int errors, warnings, logs;
        public bool completed;
    }

    [Serializable]
    sealed class LegacySmokeResult
    {
        public bool completed = false;
        public int errors = -1, warnings = -1;
    }

    static UiSmokeEditor()
    {
        if (Environment.GetCommandLineArgs().Contains("UiSmokeEditor.Run"))
        {
            started = EditorApplication.timeSinceStartup;
            EditorApplication.update += Poll;
        }
    }

    public static void Run()
    {
        if (File.Exists(ExitMarker)) File.Delete(ExitMarker);
        var args = Environment.GetCommandLineArgs();
        if (!args.Contains("--ui-smoke-test") && !args.Contains("--smoke-test"))
            throw new InvalidOperationException("编辑器验收必须提供隔离的冒烟输出目录。");
        EditorSceneManager.OpenScene("Assets/Scenes/Main.unity");
        SetSize(1280, 720);
        EditorApplication.isPlaying = true;
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
        object size = Activator.CreateInstance(sizeType, new[] { Enum.Parse(sizeKind, "FixedResolution"), (object)newWidth, newHeight, "T59 验收" });
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
                int code = int.Parse(File.ReadAllText(ExitMarker));
                File.Delete(ExitMarker);
                EditorApplication.Exit(code);
                return;
            }
            if (stopping)
            {
                if (!EditorApplication.isPlayingOrWillChangePlaymode) EditorApplication.Exit(exitCode);
                return;
            }
            if (EditorApplication.timeSinceStartup - started > 360)
            {
                Finish(false, 1);
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
                int index = Array.IndexOf(args, "--smoke-test");
                if (index >= 0 && BattleScreen.SmokeCompleted)
                {
                    // 只读取本轮运行公开的精确路径，不扫描可能含旧 run-* 结果的输出目录。
                    string path = BattleScreen.SmokeReportPath;
                    if (string.IsNullOrEmpty(path) || !File.Exists(path))
                        throw new InvalidOperationException("本轮旧版冒烟验收没有写出报告。");
                    var result = new LegacySmokeResult();
                    JsonUtility.FromJsonOverwrite(File.ReadAllText(path), result);
                    if (result.errors < 0 || result.warnings < 0)
                        throw new InvalidOperationException("本轮旧版冒烟报告缺少有效的错误或警告计数。");
                    bool passed = result.completed && result.errors == 0 && result.warnings == 0
                        && BattleScreen.SmokeExitCode == 0;
                    Finish(result.completed, passed ? 0 : 1);
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
        string path = Path.Combine(Application.dataPath, "../Logs/T59/editor-console.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, JsonUtility.ToJson(result, true));
        exitCode = completed && result.errors == 0 && result.warnings == 0 ? requestedExitCode : 1;
        File.WriteAllText(ExitMarker, exitCode.ToString());
        stopping = true;
        EditorApplication.isPlaying = false;
    }
}
