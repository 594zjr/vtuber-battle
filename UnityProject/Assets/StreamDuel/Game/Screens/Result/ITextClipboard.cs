using UnityEngine;

namespace StreamDuel.Game
{
    public interface ITextClipboard
    {
        string Text { get; set; }
    }

    public sealed class SystemTextClipboard : ITextClipboard
    {
        public string Text
        {
            get { return GUIUtility.systemCopyBuffer; }
            set { GUIUtility.systemCopyBuffer = value; }
        }
    }
}
