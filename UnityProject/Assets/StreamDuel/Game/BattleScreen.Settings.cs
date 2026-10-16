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
            if (GameRuntimeSettings.TrySave(settings, out string message)) settingsNotice = null;
            else ShowSettingsNotice(message);
        }

        void SaveBattleQuickSettings(bool playback)
        {
            // 本局可能保留较早的开局快照，快捷开关不能覆盖设置页后来保存的其它默认值。
            var latest = GameSettings.FromJson(GameRuntimeSettings.Current.ToJson());
            if (playback)
            {
                latest.FastPlay = settings.FastPlay;
                latest.AnimationSpeed = settings.AnimationSpeed;
                latest.Animations = settings.Animations;
            }
            else latest.TurnTimer = settings.TurnTimer;
            if (GameRuntimeSettings.TrySave(latest, out string message)) settingsNotice = null;
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
