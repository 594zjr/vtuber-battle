using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using StreamDuel.Core;
using UnityEngine;

namespace StreamDuel.Game
{
    public sealed partial class BattleScreen
    {
        // Awake 会先加载内容包；在 Start 前就识别验收模式，避免覆盖玩家偏好。
        static bool SmokeRequested => Array.IndexOf(Environment.GetCommandLineArgs(), "--smoke-test") >= 0
            || Array.IndexOf(Environment.GetCommandLineArgs(), "--ui-smoke-test") >= 0
            || Array.IndexOf(Environment.GetCommandLineArgs(), "--anim-smoke-test") >= 0;

        public static bool SmokeCompleted { get; private set; }
        public static int SmokeExitCode { get; private set; }
        public static string SmokeReportPath { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void ResetSmokeRun()
        {
            // 禁用域重载时也不能让本轮编辑器验收复用上一轮完成状态。
            SmokeCompleted = false;
            SmokeExitCode = 1;
            SmokeReportPath = null;
        }

        [Serializable]
        sealed class SmokeResult
        {
            public string unityVersion, pack, phase, stateHash, failure;
            public string logScope = "仅统计 BattleScreen.Start 之后的日志；启动和 Awake 错误须另查完整 Player 日志。";
            public string[] teamPacks, screenshots;
            public int turn, winner, warnings, errors;
            public bool completed;
        }

        SmokeResult smokeResult;
        string smokeDirectory;
        readonly List<string> smokeScreenshots = new List<string>();
        bool smokeRunning;
        float smokeDeadline;

        void Start()
        {
            var args = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(args, "--smoke-test");
            if (index < 0) return;
            smokeResult = new SmokeResult { unityVersion = Application.unityVersion };
            smokeRunning = true;
            smokeDeadline = Time.realtimeSinceStartup + 180f;
            Application.logMessageReceived += CollectSmokeLog;
            try
            {
                if (index + 1 >= args.Length)
                    throw new ArgumentException("--smoke-test 后需要截图和验收报告的输出目录。");
                // 每轮独立目录避免把上轮截图误认成本轮证据；目录编号不参与战斗。
                smokeDirectory = Path.Combine(Path.GetFullPath(args[index + 1]), "run-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(smokeDirectory);
                SmokeReportPath = Path.Combine(smokeDirectory, "result.json");
                Debug.Log("冒烟验收输出：" + smokeDirectory);
                StartCoroutine(GuardSmoke(RunSmoke()));
            }
            catch (Exception ex) { FinishSmoke(ex.ToString()); }
        }

        void LateUpdate()
        {
            // 即使帧末截图等待无法完成，也不能让验收进程一直挂起。
            if (smokeRunning && Time.realtimeSinceStartup >= smokeDeadline)
                FinishSmoke("冒烟验收超过 180 秒；截图需要正常渲染，不能使用无图形模式。");
        }

        void CollectSmokeLog(string message, string stack, LogType type)
        {
            if (type == LogType.Warning) smokeResult.warnings++;
            if (type == LogType.Error || type == LogType.Assert || type == LogType.Exception) smokeResult.errors++;
        }

        IEnumerator GuardSmoke(IEnumerator routine)
        {
            while (smokeRunning)
            {
                bool more = false;
                object wait = null;
                string failure = null;
                try
                {
                    more = routine.MoveNext();
                    if (more) wait = routine.Current;
                }
                catch (Exception ex) { failure = ex.ToString(); }
                if (failure != null || !more)
                {
                    FinishSmoke(failure);
                    yield break;
                }
                // RunSmoke 只返回 Unity 等待对象，所有实际操作都经过上面的异常出口。
                yield return wait;
            }
        }

        IEnumerator RunSmoke()
        {
            fast = true;
            timerOn = false;
            smokeResult.teamPacks = packs.Select(pack => pack.PackId).ToArray();
            for (int i = 0; i < packs.Count; i++)
            {
                LoadPack(packs[i].PackId);
                yield return new WaitForEndOfFrame();
                // 内容包 id 可以包含路径字符；序号与报告中的 teamPacks 一一对应。
                CaptureSmoke("team-" + i + ".png");
            }
            LoadPack("original");
            if (pick.Count != 6) RandomPick();
            yield return new WaitForEndOfFrame();
            CaptureSmoke("team.png");
            var mine = pick.ToList();
            var foe = db.Streamers.Select(streamer => streamer.Id).Where(id => !pick.Contains(id))
                .Concat(pick).Take(6).ToList();
            StartBattle(new[] { mine, foe });
            yield return new WaitForEndOfFrame();
            CaptureSmoke("lead.png");
            SubmitLead(Ai.ChooseLead(b, 0));

            bool capturedBattle = false;
            while (true)
            {
                if (playing) SkipPlayback();
                yield return new WaitForSecondsRealtime(0.1f);
                if (b.Phase == Phase.Over && !playing) break;
                if (playing) continue;
                if (!capturedBattle && b.Turn >= 2)
                {
                    yield return new WaitForEndOfFrame();
                    CaptureSmoke("battle.png");
                    capturedBattle = true;
                }
                if (b.Phase == Phase.Choose) Submit(Ai.Choose(b, 0));
                else if (b.Phase == Phase.Replace && b.Need[0]) DoReplace(Ai.ChooseReplacement(b, 0));
            }
            if (!capturedBattle) throw new InvalidOperationException("缺少对战中截图，不能完成验收。");
            yield return new WaitForEndOfFrame();
            CaptureSmoke("result.png");
            File.WriteAllText(Path.Combine(smokeDirectory, "record.json"), b.Record.ToJson());
            smokeResult.completed = true;
        }

        void CaptureSmoke(string fileName)
        {
            Texture2D texture = null;
            try
            {
                texture = ScreenCapture.CaptureScreenshotAsTexture();
                if (texture == null || texture.width == 0 || texture.height == 0)
                    throw new InvalidOperationException("截图没有有效画面：" + fileName);
                byte[] png = texture.EncodeToPNG();
                if (png == null || png.Length < 8) throw new InvalidOperationException("截图编码失败：" + fileName);
                string path = Path.Combine(smokeDirectory, fileName);
                File.WriteAllBytes(path, png);
                if (new FileInfo(path).Length != png.Length) throw new IOException("截图未完整写入：" + fileName);
                smokeScreenshots.Add(fileName);
            }
            finally { if (texture != null) Destroy(texture); }
        }

        void FinishSmoke(string failure)
        {
            if (!smokeRunning) return;
            smokeRunning = false;
            bool passed = false;
            try
            {
                if (failure != null)
                {
                    smokeResult.completed = false;
                    smokeResult.failure = failure;
                    Debug.LogError("冒烟验收失败：" + failure);
                }
                if (b != null)
                {
                    smokeResult.pack = db.PackId;
                    smokeResult.phase = b.Phase.ToString();
                    smokeResult.turn = b.Turn;
                    smokeResult.winner = b.Winner;
                    smokeResult.stateHash = b.StateHash().ToString("x16");
                }
                smokeResult.screenshots = smokeScreenshots.ToArray();
                if (SmokeReportPath != null)
                    File.WriteAllText(SmokeReportPath, JsonUtility.ToJson(smokeResult, true));
                passed = SmokeReportPath != null && smokeResult.completed && smokeResult.errors == 0 && smokeResult.warnings == 0;
            }
            catch (Exception ex) { Debug.LogError("无法写入冒烟验收报告：" + ex); }
            finally
            {
                Application.logMessageReceived -= CollectSmokeLog;
                SmokeExitCode = passed ? 0 : 1;
                SmokeCompleted = true;
                if (!Application.isEditor) Application.Quit(SmokeExitCode);
            }
        }
    }
}
