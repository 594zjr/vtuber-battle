using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace StreamDuel.Game.Editor
{
    [InitializeOnLoad]
    public static class T68SmokeEditor
    {
        const string EntryPoint = "StreamDuel.Game.Editor.T68SmokeEditor.Run";
        const string StatePrefix = "StreamDuel.T68SmokeEditor.";
        const double TimeoutSeconds = 900;
        static EditorWindow gameView;
        static int width, height, stoppedUpdates;
        static double stoppedAt = -1;
        static string outputDirectory;

        [Serializable]
        sealed class ConsoleResult
        {
            public string unityVersion, reportPath, failure;
            public int errors, warnings, logs;
            public int exitErrors, exitWarnings, exitLogs;
            public int runtimeExitCode, editorExitCode;
            public bool completed, exitSampled, exitWaitTimedOut;
            public int width, height;
            public double elapsedSeconds;
        }

        static T68SmokeEditor()
        {
            if (Environment.GetCommandLineArgs().Contains(EntryPoint)) EditorApplication.update += Poll;
        }

        public static void Run()
        {
            try
            {
                outputDirectory = ReadOutputDirectory();
                Directory.CreateDirectory(outputDirectory);
                // 会话状态跨 Play 的域重载保留，只属于本次编辑器进程，不写全局设置。
                SessionState.SetBool(StatePrefix + "active", true);
                SessionState.SetBool(StatePrefix + "stopping", false);
                SessionState.SetBool(StatePrefix + "startPending", false);
                SaveTime("started", EditorApplication.timeSinceStartup);
                EditorSceneManager.OpenScene("Assets/Scenes/Main.unity");
                SetSize(1280, 720);
                // 新工作树先在 Edit 模式初始化本工程索引，避免 Unity 进入 Play 后访问空索引。
                var searchDatabase = Type.GetType("UnityEditor.Search.SearchDatabase, UnityEditor.QuickSearchModule", true);
                var initialize = searchDatabase.GetMethod("GetDefaultSearchDatabase", BindingFlags.Static | BindingFlags.Public);
                if (initialize == null) throw new MissingMethodException("当前 Unity 不支持验收所需的搜索索引初始化。");
                initialize.Invoke(null, null);
                SessionState.SetBool(StatePrefix + "startPending", true);
            }
            catch (Exception ex) { Fail(ex); }
        }

        static string ReadOutputDirectory()
        {
            string[] args = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(args, "--anim-smoke-test");
            if (index < 0 || index + 1 >= args.Length || args.Count(arg => arg == "--anim-smoke-test") != 1)
                throw new ArgumentException("动画验收必须提供唯一的 --anim-smoke-test 输出目录。");
            string path = args[index + 1];
            if (!Path.IsPathRooted(path)) throw new ArgumentException("动画验收输出目录必须为绝对路径。");
            path = Path.GetFullPath(path);
            string allowed = Path.GetFullPath(Path.Combine(Application.dataPath, "../Logs/T68"));
            if (!Within(path, allowed)) throw new ArgumentException("动画验收输出必须位于本工程 Logs/T68 内。");
            return path;
        }

        static bool Within(string path, string directory)
        {
            string root = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), root, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        static string OutputDirectory
        {
            get { return outputDirectory ?? (outputDirectory = ReadOutputDirectory()); }
        }

        static string ConsolePath { get { return Path.Combine(OutputDirectory, "editor-console.json"); } }

        static void SaveTime(string key, double value)
        {
            SessionState.SetString(StatePrefix + key, value.ToString("R", CultureInfo.InvariantCulture));
        }

        static double Time(string key)
        {
            return double.Parse(SessionState.GetString(StatePrefix + key, "0"), CultureInfo.InvariantCulture);
        }

        static void SetSize(int newWidth, int newHeight)
        {
            if (!((newWidth == 1280 && newHeight == 720) || (newWidth == 1920 && newHeight == 1080)))
                throw new ArgumentException("动画验收只支持 1280×720 和 1920×1080。");
            if (width == newWidth && height == newHeight && gameView != null) return;
            var assembly = typeof(UnityEditor.Editor).Assembly;
            var viewType = assembly.GetType("UnityEditor.GameView", true);
            gameView = EditorWindow.GetWindow(viewType);
            var sizesType = assembly.GetType("UnityEditor.GameViewSizes", true);
            var singleton = typeof(ScriptableSingleton<>).MakeGenericType(sizesType);
            object sizes = singleton.GetProperty("instance", BindingFlags.Public | BindingFlags.Static).GetValue(null);
            var groupType = assembly.GetType("UnityEditor.GameViewSizeGroupType", true);
            object group = sizesType.GetMethod("GetGroup").Invoke(sizes, new[] { Enum.Parse(groupType, "Standalone") });
            var sizeType = assembly.GetType("UnityEditor.GameViewSize", true);
            var sizeKind = assembly.GetType("UnityEditor.GameViewSizeType", true);
            object size = Activator.CreateInstance(sizeType,
                new[] { Enum.Parse(sizeKind, "FixedResolution"), (object)newWidth, newHeight, "T68 动画验收" });
            // 只在本次进程添加尺寸，不调用全局 GameView 配置的保存接口。
            group.GetType().GetMethod("AddCustomSize").Invoke(group, new[] { size });
            int total = (int)group.GetType().GetMethod("GetTotalCount").Invoke(group, null);
            viewType.GetProperty("selectedSizeIndex", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .SetValue(gameView, total - 1);
            gameView.Show();
            gameView.Focus();
            width = newWidth;
            height = newHeight;
            SessionState.SetInt(StatePrefix + "width", width);
            SessionState.SetInt(StatePrefix + "height", height);
        }

        static void Poll()
        {
            if (!SessionState.GetBool(StatePrefix + "active", false)) return;
            try
            {
                if (SessionState.GetBool(StatePrefix + "stopping", false))
                {
                    FinishAfterStop();
                    return;
                }
                if (EditorApplication.timeSinceStartup - Time("started") >= TimeoutSeconds)
                {
                    Finish(false, 1, "动画验收超过 900 秒。");
                    return;
                }
                if (SessionState.GetBool(StatePrefix + "startPending", false))
                {
                    if (EditorApplication.isCompiling || EditorApplication.isUpdating
                        || !File.Exists(Path.Combine(Application.dataPath, "../UserSettings/Search.index"))) return;
                    SessionState.SetBool(StatePrefix + "startPending", false);
                    EditorApplication.isPlaying = true;
                    return;
                }
                if (!EditorApplication.isPlaying) return;
                if (T68AnimSmoke.RequestedWidth > 0) SetSize(T68AnimSmoke.RequestedWidth, T68AnimSmoke.RequestedHeight);
                if (T68AnimSmoke.Completed) Finish(true, T68AnimSmoke.ExitCode, null);
            }
            catch (Exception ex) { Fail(ex); }
        }

        static int[] ConsoleCounts()
        {
            var type = typeof(UnityEditor.Editor).Assembly.GetType("UnityEditor.LogEntries", true);
            var method = type.GetMethod("GetCountsByType", BindingFlags.Static | BindingFlags.Public);
            if (method == null) throw new MissingMethodException("当前 Unity 无法读取 Console 计数。");
            object[] values = { 0, 0, 0 };
            method.Invoke(null, values);
            return new[] { (int)values[0], (int)values[1], (int)values[2] };
        }

        static void Finish(bool completed, int requestedExitCode, string failure)
        {
            if (SessionState.GetBool(StatePrefix + "stopping", false)) return;
            string reportPath = T68AnimSmoke.ReportPath;
            if (completed && (string.IsNullOrEmpty(reportPath) || !Path.IsPathRooted(reportPath)
                || !Within(Path.GetFullPath(reportPath), OutputDirectory) || !File.Exists(reportPath)))
            {
                completed = false;
                failure = "本轮动画验收未在指定输出目录写出报告。";
            }
            int[] counts = ConsoleCounts();
            var result = new ConsoleResult
            {
                unityVersion = Application.unityVersion,
                reportPath = reportPath,
                failure = failure,
                errors = counts[0], warnings = counts[1], logs = counts[2],
                completed = completed,
                runtimeExitCode = requestedExitCode,
                editorExitCode = completed && requestedExitCode == 0 && counts[0] == 0 && counts[1] == 0 ? 0 : 1,
                width = SessionState.GetInt(StatePrefix + "width", 0),
                height = SessionState.GetInt(StatePrefix + "height", 0),
                elapsedSeconds = EditorApplication.timeSinceStartup - Time("started")
            };
            Directory.CreateDirectory(OutputDirectory);
            File.WriteAllText(ConsolePath, JsonUtility.ToJson(result, true));
            SessionState.SetBool(StatePrefix + "stopping", true);
            SaveTime("stopRequested", EditorApplication.timeSinceStartup);
            EditorApplication.isPlaying = false;
        }

        static void FinishAfterStop()
        {
            double now = EditorApplication.timeSinceStartup;
            bool stopped = !EditorApplication.isPlayingOrWillChangePlaymode;
            if (stopped)
            {
                if (stoppedAt < 0) stoppedAt = now;
                stoppedUpdates++;
            }
            bool settled = stopped && now - stoppedAt >= 2 && stoppedUpdates >= 4
                && !EditorApplication.isCompiling && !EditorApplication.isUpdating;
            if (!settled && now - Time("stopRequested") < 10) return;
            var result = JsonUtility.FromJson<ConsoleResult>(File.ReadAllText(ConsolePath));
            if (result == null) throw new InvalidOperationException("动画验收 Console 报告无效。");
            int[] counts = ConsoleCounts();
            result.exitErrors = counts[0];
            result.exitWarnings = counts[1];
            result.exitLogs = counts[2];
            result.exitSampled = stopped;
            result.exitWaitTimedOut = !settled;
            result.elapsedSeconds = now - Time("started");
            if (!settled || counts[0] != 0 || counts[1] != 0) result.editorExitCode = 1;
            File.WriteAllText(ConsolePath, JsonUtility.ToJson(result, true));
            Exit(result.editorExitCode);
        }

        static void Fail(Exception ex)
        {
            Debug.LogException(ex);
            try
            {
                if (SessionState.GetBool(StatePrefix + "stopping", false))
                {
                    Exit(1);
                    return;
                }
                Finish(false, 1, ex.ToString());
            }
            catch (Exception reportFailure)
            {
                Debug.LogException(reportFailure);
                Exit(1);
            }
        }

        static void Exit(int code)
        {
            SessionState.SetBool(StatePrefix + "active", false);
            EditorApplication.update -= Poll;
            EditorApplication.Exit(code);
        }
    }
}
