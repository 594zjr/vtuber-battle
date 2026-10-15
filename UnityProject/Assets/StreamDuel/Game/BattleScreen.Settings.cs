using StreamDuel.Core;
using UnityEngine;

namespace StreamDuel.Game
{
    public sealed partial class BattleScreen
    {
        GameSettings settings;
        string settingsNotice;
        float settingsNoticeUntil;

        void LoadSettingsSafely()
        {
            settings = GameRuntimeSettings.Current;
            settingsNotice = null;
            if (!string.IsNullOrEmpty(GameRuntimeSettings.Notice)) ShowSettingsNotice(GameRuntimeSettings.Notice);
        }

        void SaveSettingsSafely()
        {
            if (SmokeRequested) return;
            if (GameRuntimeSettings.TrySave(settings, out string message)) settingsNotice = null;
            else ShowSettingsNotice(message);
        }

        void ShowSettingsNotice(string text)
        {
            settingsNotice = text;
            settingsNoticeUntil = Time.realtimeSinceStartup + 8f;
        }

        void DrawSettingsNotice()
        {
            if (string.IsNullOrEmpty(settingsNotice) || Time.realtimeSinceStartup >= settingsNoticeUntil) return;
            var rect = new Rect((RefW - 840) / 2, RefH - 58, 840, 34);
            Box(rect);
            GUI.Label(rect, settingsNotice, sCenter);
        }
    }
}
