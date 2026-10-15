using System;
using UnityEngine;

namespace StreamDuel.Game
{
    [DisallowMultipleComponent]
    public sealed class GameBootRecovery : MonoBehaviour
    {
        [NonSerialized] ScreenRouter router;

        void OnEnable()
        {
            router = GetComponent<ScreenRouter>();
            Restore();
        }

        void Update()
        {
            // 路由可能在重载时被单独停用，等它重新启用再接线；正常启停不重置页面。
            if (router != null && !router.IsInitialized) Restore();
        }

        internal void Restore()
        {
            if (router == null) router = GetComponent<ScreenRouter>();
            if (router != null && router.isActiveAndEnabled && !router.IsInitialized)
                GameBoot.ConnectScreens(router);
        }
    }
}
