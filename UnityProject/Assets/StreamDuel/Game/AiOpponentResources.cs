using StreamDuel.Core;
using UnityEngine;

namespace StreamDuel.Game
{
    internal static class AiOpponentResources
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Load()
        {
            // Unity 的 asmdef 不使用 .csproj 内嵌资源，由显示层把原始文本交给 Core 校验。
            var asset = Resources.Load<TextAsset>("AiOpponents");
            if (asset == null) throw new System.InvalidOperationException("缺少电脑对手名单 AiOpponents.json。");
            AiOpponents.SetJson(asset.text);
        }
    }
}
