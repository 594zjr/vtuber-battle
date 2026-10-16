using UnityEngine;
using UnityEngine.UIElements;

namespace StreamDuel.Game
{
    // 打开工程后不用摆场景：按 Play 就会自动创建主菜单。
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
            GameRuntimeSettings.ApplyDisplay();
            var go = new GameObject("StreamDuel");
            go.AddComponent<LocalBattleSession>().enabled = false;
            go.AddComponent<UIDocument>();
            go.AddComponent<ScreenRouter>();
            go.AddComponent<GameBootRecovery>();
            Object.DontDestroyOnLoad(go);
        }

        internal static void ConnectScreens(ScreenRouter router)
        {
            if (router.IsInitialized) return;
            var session = router.GetComponent<LocalBattleSession>();
            if (session == null) session = router.gameObject.AddComponent<LocalBattleSession>();
            session.enabled = false;
            session.EnsureMenuContentLoaded();
            router.Initialize(router.GetComponent<UIDocument>(), Resources.Load<ScreenResources>("StreamDuelUI"));
            router.Register(ScreenIds.Menu, () => new MenuScreen(session));
            router.Register(ScreenIds.About, () => new AboutScreen());
            router.Register(ScreenIds.AnimationPreview, () => new AnimPreviewScreen());
            router.Register(ScreenIds.EffectPreview, () => new MenuPreviewScreen(ScreenIds.EffectPreview, "特效预览"));
            router.Register(ScreenIds.Team, () => new TeamScreen(session));
            router.Register(ScreenIds.Battle, () => new BattleScreen());
            router.Register(ScreenIds.Result, () => new ResultScreen());
            router.Register(ScreenIds.Replay, () => new ReplayScreen(session.FindLoadedPack));
            router.Register(ScreenIds.Settings, () => new SettingsScreen());
            router.Register(ScreenIds.Online, () => new OnlineScreen());
            session.MainMenuRequested = () => router.Show(ScreenIds.Menu);
            session.TeamScreenRequested = () => router.Show(ScreenIds.Team);
            session.ResultRequested = battle => router.Show(ScreenIds.Result, new ResultScreenArgs
            {
                Battle = battle,
                ResolvePack = session.FindLoadedPack,
                RematchLabel = session.ResultRematchLabel,
                Rematch = () => { session.RematchFromResult(); router.Show(ScreenIds.Battle, session.CreateBattleScreenArgs()); },
                ReturnToTeam = session.ReturnToTeamFromResult
            });
            // 域重载不能恢复纯 C# 会话，重新接线后从菜单开始。
            router.Show(ScreenIds.Menu);
        }
    }
}
