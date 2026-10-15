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
            if (GameObject.Find("StreamDuel") != null) return;
            if (Camera.main == null)
            {
                var cam = new GameObject("Main Camera").AddComponent<Camera>();
                cam.tag = "MainCamera";
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color32(12, 17, 36, 255);
                cam.gameObject.AddComponent<AudioListener>();
            }
            var go = new GameObject("StreamDuel");
            var legacy = go.AddComponent<BattleScreen>();
            var document = go.AddComponent<UIDocument>();
            var router = go.AddComponent<ScreenRouter>();
            router.Initialize(document, Resources.Load<ScreenResources>("StreamDuelUI"), visible => legacy.enabled = visible);
            router.Register(ScreenIds.Menu, () => new MenuScreen());
            router.Register(ScreenIds.Team, () => new TeamScreen());
            router.Register(ScreenIds.Battle, () => new BattlePlaceholderScreen());
            router.Register(ScreenIds.Result, () => new ResultScreen());
            router.Register(ScreenIds.Replay, () => new ReplayScreen());
            router.Register(ScreenIds.Settings, () => new SettingsScreen());
            router.Register(ScreenIds.Online, () => new OnlineScreen());
            legacy.MainMenuRequested = () => router.Show(ScreenIds.Menu);
            router.Show(ScreenIds.Legacy);
            Object.DontDestroyOnLoad(go);
        }
    }
}
