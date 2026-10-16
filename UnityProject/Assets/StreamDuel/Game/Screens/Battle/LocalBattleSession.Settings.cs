using StreamDuel.Core;

namespace StreamDuel.Game
{
    public sealed partial class LocalBattleSession
    {
        GameSettings settings;
        string settingsNotice;

        public bool CanChangeTurnTimer => teamRuleset == null;

        public void SetFastPlayback(bool value)
        {
            if (settings == null) throw new System.InvalidOperationException("尚未载入对战设置。");
            settings.FastPlay = value;
            settings.AnimationSpeed = value ? 2 : 1;
            settings.Animations = true;
            SaveBattleQuickSettings(true);
        }

        public void SetTurnTimer(bool value)
        {
            if (!CanChangeTurnTimer) throw new System.InvalidOperationException("显式赛制使用赛制规定的计时，不能在局中覆盖。");
            if (settings == null) throw new System.InvalidOperationException("尚未载入对战设置。");
            settings.TurnTimer = value;
            timerOn = value;
            ResetLocalClock();
            SaveBattleQuickSettings(false);
        }

        void LoadSettingsSafely()
        {
            settings = GameRuntimeSettings.Current;
            settingsNotice = GameRuntimeSettings.Notice;
        }

        void SaveSettingsSafely()
        {
            settingsNotice = GameRuntimeSettings.TrySave(settings, out string message) ? null : message;
        }

        void SaveBattleQuickSettings(bool playback)
        {
            // 本局持有独立快照，快捷开关只合并自己负责的设置，保留设置页后来保存的其它默认值。
            var latest = GameSettings.FromJson(GameRuntimeSettings.Current.ToJson());
            if (playback)
            {
                latest.FastPlay = settings.FastPlay;
                latest.AnimationSpeed = settings.AnimationSpeed;
                latest.Animations = settings.Animations;
            }
            else latest.TurnTimer = settings.TurnTimer;
            settingsNotice = GameRuntimeSettings.TrySave(latest, out string message) ? null : message;
        }
    }
}
