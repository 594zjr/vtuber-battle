using UnityEngine;
using UnityEngine.UIElements;

namespace StreamDuel.Game
{
    // 打开工程后不用摆场景：按 Play 就会自动创建对战画面。
    public static class GameBoot
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            var existing = GameObject.Find("StreamDuel");
            if (existing != null)
            {
                existing.GetComponent<GameBootRecovery>()?.Restore();
                return;
            }
            if (Camera.main == null)
            {
                var cam = new GameObject("Main Camera").AddComponent<Camera>();
                cam.tag = "MainCamera";
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color32(12, 17, 36, 255);
                cam.gameObject.AddComponent<AudioListener>();
            }
            var go = new GameObject("StreamDuel");
            go.AddComponent<BattleScreen>();
            go.AddComponent<UIDocument>();
            go.AddComponent<ScreenRouter>();
            go.AddComponent<GameBootRecovery>();
            Object.DontDestroyOnLoad(go);
        }

        internal static void ConnectScreens(ScreenRouter router)
        {
            if (router.IsInitialized) return;
            var legacy = router.GetComponent<BattleScreen>();
            var document = router.GetComponent<UIDocument>();
            router.Initialize(document, Resources.Load<ScreenResources>("StreamDuelUI"), visible => legacy.enabled = visible);
            router.Register(ScreenIds.Menu, () => new MenuScreen());
            router.Register(ScreenIds.Team, () => new TeamScreen());
            router.Register(ScreenIds.Battle, () => new BattlePlaceholderScreen());
            router.Register(ScreenIds.Result, () => new ResultScreen());
            router.Register(ScreenIds.Replay, () => new ReplayScreen());
            router.Register(ScreenIds.Settings, () => new SettingsScreen());
            router.Register(ScreenIds.Online, () => new OnlineScreen());
            router.Register(AnimPreviewScreen.ScreenId, () => new AnimPreviewScreen());
            legacy.MainMenuRequested = () => router.Show(ScreenIds.Menu);
            // 纯 C# 对战数据无法跨脚本重载恢复，由旧画面的 OnEnable 安全回到组队。
            router.Show(ScreenIds.Legacy);
        }
    }
}
