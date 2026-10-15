using UnityEngine;

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
            go.AddComponent<BattleScreen>();
            Object.DontDestroyOnLoad(go);
        }
    }
}
