using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using StreamDuel.Core;
using UnityEngine;
using UnityEngine.UIElements;

namespace StreamDuel.Game
{
    public sealed partial class T135RankSmoke : MonoBehaviour
    {
        [Serializable]
        sealed class Report
        {
            public string unityVersion, outputDirectory, failure, finalHash;
            public string inputScope = "NavigationSubmitEvent / NavigationCancelEvent 驱动正式 UI 回调；为自动 UI 验证，不是人工鼠标或物理键盘验收。";
            public string storageScope = "启动采用 SmokeRequested 内存档案；文件夹具和证据仅写当前工程 Logs/T-135 下本轮独占目录，不访问玩家存档或系统剪贴板。";
            public string layoutScope = "1280×720 与 1920×1080 检查可见文字测量、边界和非祖先重叠；历史列表允许滚动裁切。截图需独立视觉复核。";
            public bool completed;
            public int warnings, errors, turns, winner;
            public List<string> checks = new List<string>();
            public List<string> messages = new List<string>();
            public List<string> screenshots = new List<string>();
            public List<BoundsEntry> bounds = new List<BoundsEntry>();
        }

        [Serializable]
        sealed class BoundsEntry
        {
            public string name, text;
            public int width, height;
            public float x, y, w, h;
        }

        public static bool Completed { get; private set; }
        public static int ExitCode { get; private set; }
        public static int RequestedWidth { get; private set; }
        public static int RequestedHeight { get; private set; }
        public static string ReportPath { get; private set; }
        Report report;
        ScreenRouter router;
        BattleScreen legacy;
        PortraitProvider portraits;
        bool running;
        float deadline;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Install()
        {
            if (!Environment.GetCommandLineArgs().Contains("--t135-smoke-test")) return;
            Completed = false;
            ExitCode = 1;
            RequestedWidth = RequestedHeight = 0;
            ReportPath = null;
            var host = new GameObject("StreamDuel.T135RankSmoke");
            DontDestroyOnLoad(host);
            host.AddComponent<T135RankSmoke>().Configure();
        }
        public static string ReadOutputDirectory()
        {
            string[] args = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(args, "--t135-output-dir");
            if (args.Count(value => value == "--t135-smoke-test") != 1
                || args.Count(value => value == "--t135-output-dir") != 1 || index + 1 >= args.Length
                || index < 0 || !Path.IsPathFullyQualified(args[index + 1]))
                throw new ArgumentException("排位验收需要 --t135-smoke-test 与唯一的 --t135-output-dir 绝对目录。");
            string path = Path.GetFullPath(args[index + 1]);
            string project = ProjectDirectory();
            string allowed = Path.Combine(project, "Logs", "T-135");
            if (!Within(path, allowed)) throw new ArgumentException("排位验收输出必须位于当前工程 Logs/T-135 内。");
            return path;
        }

        public static string ProjectDirectory()
        {
            // Player 构建也放在本工程 Logs/T-135 中，向上只做路径运算，不搜索或读取用户目录。
            string path = Path.GetFullPath(Application.dataPath);
            while (!string.IsNullOrEmpty(path))
            {
                if (string.Equals(Path.GetFileName(path), "UnityProject", StringComparison.OrdinalIgnoreCase)) return path;
                path = Path.GetDirectoryName(path);
            }
            throw new InvalidOperationException("排位验收须在工作树 UnityProject 或其验收构建目录运行。");
        }

        public static bool Within(string path, string directory)
        {
            string root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(full, root, StringComparison.OrdinalIgnoreCase)
                || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        void Configure()
        {
            report = new Report { unityVersion = Application.unityVersion };
            running = true;
            deadline = Time.realtimeSinceStartup + 840;
            Application.logMessageReceived += OnLog;
            try
            {
                report.outputDirectory = Path.Combine(ReadOutputDirectory(), "run-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(report.outputDirectory);
                ReportPath = Path.Combine(report.outputDirectory, "result.json");
                Debug.Log("T135_RANK_SMOKE_OUTPUT " + report.outputDirectory);
            }
            catch (Exception error) { Finish(error.ToString()); }
        }

        void Start() { if (running) StartCoroutine(Guard(Run())); }
        void LateUpdate()
        {
            if (running && Time.realtimeSinceStartup >= deadline) Finish("排位真实渲染验收超过 840 秒。");
        }

        void OnLog(string message, string stack, LogType type)
        {
            if (type == LogType.Warning) report.warnings++;
            if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert) report.errors++;
            if (type != LogType.Log) report.messages.Add(type + ": " + message + "\n" + stack);
        }

        IEnumerator Guard(IEnumerator root)
        {
            var pending = new Stack<IEnumerator>();
            pending.Push(root);
            while (running)
            {
                bool more = false;
                object current = null;
                string failure = null;
                try
                {
                    more = pending.Peek().MoveNext();
                    if (more) current = pending.Peek().Current;
                    else pending.Pop();
                }
                catch (Exception error) { failure = error.ToString(); }
                if (failure != null || pending.Count == 0) { Finish(failure); yield break; }
                if (!more) continue;
                if (current is IEnumerator nested) { pending.Push(nested); continue; }
                yield return current;
            }
        }

        IEnumerator Settle()
        {
            yield return null;
            yield return null;
            yield return new WaitForEndOfFrame();
        }

        IEnumerator Resize(int width, int height)
        {
            RequestedWidth = width;
            RequestedHeight = height;
            Screen.SetResolution(width, height, FullScreenMode.Windowed);
            float until = Time.realtimeSinceStartup + 20;
            while (Screen.width != width || Screen.height != height)
            {
                Require(Time.realtimeSinceStartup < until, "分辨率切换超时；Editor 需要 T135SmokeEditor 桥。");
                yield return null;
            }
            yield return Settle();
        }

        T Element<T>(string name) where T : VisualElement
        {
            var element = router.Document.rootVisualElement.Q<T>(name);
            Require(element != null, "缺少真实 UI 节点：" + name);
            return element;
        }

        void Submit(string name)
        {
            var button = Element<Button>(name);
            Require(button.enabledInHierarchy && button.visible, "不能提交不可用按钮：" + name);
            button.Focus();
            using (var evt = NavigationSubmitEvent.GetPooled()) { evt.target = button; button.SendEvent(evt); }
        }

        static bool Visible(VisualElement element)
        {
            for (var current = element; current != null; current = current.parent)
                if (!current.visible || current.resolvedStyle.display == DisplayStyle.None) return false;
            return true;
        }
        static bool InsideNamed(VisualElement element, string name)
        {
            for (var current = element; current != null; current = current.parent) if (current.name == name) return true;
            return false;
        }
        static Rect Intersection(Rect first, Rect second) => Rect.MinMaxRect(Mathf.Max(first.xMin, second.xMin), Mathf.Max(first.yMin, second.yMin), Mathf.Min(first.xMax, second.xMax), Mathf.Min(first.yMax, second.yMax));
        static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        static bool Contains(Rect outer, Rect inner) => inner.xMin >= outer.xMin - 2 && inner.yMin >= outer.yMin - 2 && inner.xMax <= outer.xMax + 2 && inner.yMax <= outer.yMax + 2;
        static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        string Size => RequestedWidth + "×" + RequestedHeight;

        void Capture(string name)
        {
            Texture2D texture = null;
            try
            {
                texture = ScreenCapture.CaptureScreenshotAsTexture();
                Require(texture != null && texture.width == RequestedWidth && texture.height == RequestedHeight, "截图分辨率不符。");
                byte[] png = texture.EncodeToPNG();
                Require(png != null && png.Length > 8, "截图编码失败。");
                string file = RequestedWidth + "x" + RequestedHeight + "-" + name + ".png";
                string path = Path.Combine(report.outputDirectory, file);
                File.WriteAllBytes(path, png);
                Require(new FileInfo(path).Length == png.Length, "截图没有完整写入。");
                var pixels = texture.GetPixels32();
                int step = Math.Max(1, pixels.Length / 65536), low = 255, high = 0, bright = 0;
                for (int index = 0; index < pixels.Length; index += step)
                {
                    var pixel = pixels[index];
                    int luminance = (299 * pixel.r + 587 * pixel.g + 114 * pixel.b) / 1000;
                    low = Math.Min(low, luminance); high = Math.Max(high, luminance);
                    if (luminance >= 150) bright++;
                }
                Require(high - low >= 48 && bright >= 8, "截图没有有效渲染内容：" + file);
                report.screenshots.Add(file);
            }
            finally { if (texture != null) Destroy(texture); }
        }

        void OnDestroy()
        {
            Application.logMessageReceived -= OnLog;
            portraits?.Dispose();
            portraits = null;
        }

        void Finish(string failure)
        {
            if (!running) return;
            running = false;
            try
            {
                report.failure = failure;
                if (failure != null) report.completed = false;
                ExitCode = report.completed && report.errors == 0 && report.warnings == 0 ? 0 : 1;
                if (ReportPath != null) File.WriteAllText(ReportPath, JsonUtility.ToJson(report, true));
                else ExitCode = 1;
            }
            catch (Exception error) { ExitCode = 1; Debug.LogError("排位验收报告写入失败：" + error); }
            finally
            {
                Application.logMessageReceived -= OnLog;
                Completed = true;
                if (ExitCode == 0) Debug.Log("T135_RANK_SMOKE_PASS " + ReportPath);
                else Debug.LogError("T135_RANK_SMOKE_FAIL " + (failure ?? "检测到警告或错误") + " " + ReportPath);
                if (!Application.isEditor) Application.Quit(ExitCode);
            }
        }
    }
}
