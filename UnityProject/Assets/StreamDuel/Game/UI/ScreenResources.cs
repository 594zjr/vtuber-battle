using UnityEngine;
using UnityEngine.UIElements;

namespace StreamDuel.Game
{
    [CreateAssetMenu(fileName = "StreamDuelUI", menuName = "开播对决/界面资源")]
    public sealed class ScreenResources : ScriptableObject
    {
        public PanelSettings PanelSettings;
        public StyleSheet Theme;
        public Font RegularFont;
        public Font BoldFont;
    }
}
