using System;
using System.IO;
using System.Security;
using StreamDuel.Core;
using UnityEngine;

namespace StreamDuel.Game
{
    public sealed partial class BattleScreen
    {
        ISaveStore saveStore;
        GameSettings settings;
        string settingsNotice;
        float settingsNoticeUntil;

        void LoadSettingsSafely()
        {
            if (settings == null) settings = new GameSettings();
            saveStore = null;
            // 验收只用内存设置，连存档目录也不因初始化而创建。
            if (SmokeRequested) return;
            try
            {
                var store = new FileSaveStore(Application.persistentDataPath);
                bool migrate = SettingsFileMissing(Path.Combine(Application.persistentDataPath, "settings.json"));
                var loaded = store.LoadSettings();
                if (migrate)
                {
                    // 只在确认文件不存在时迁移，不能把读取失败误当成首次运行。
                    loaded.FastPlay = PlayerPrefs.GetInt("sd.fast", loaded.FastPlay ? 1 : 0) == 1;
                    loaded.TurnTimer = PlayerPrefs.GetInt("sd.timer", loaded.TurnTimer ? 1 : 0) == 1;
                    loaded.DefaultPack = PlayerPrefs.GetString("sd.pack", loaded.DefaultPack);
                }
                settings = loaded;
                saveStore = store;
                settingsNotice = null;
            }
            catch (Exception ex) when (IsSettingsStorageError(ex))
            {
                // 未读到原设置时禁用本次持久化，防止 LoadPack 把默认值覆盖回去。
                ShowSettingsNotice("设置暂时无法读取，当前设置仅在本次运行中生效。");
            }
        }

        static bool SettingsFileMissing(string path)
        {
            try { File.GetAttributes(path); return false; }
            catch (FileNotFoundException) { return true; }
            catch (DirectoryNotFoundException) { return true; }
        }

        static bool IsSettingsStorageError(Exception error)
        {
            return error is IOException || error is UnauthorizedAccessException || error is SecurityException;
        }

        void SaveSettingsSafely()
        {
            if (SmokeRequested) return;
            if (saveStore == null)
            {
                ShowSettingsNotice("设置暂时无法读取，当前设置仅在本次运行中生效。");
                return;
            }
            try
            {
                saveStore.SaveSettings(settings);
                settingsNotice = null;
            }
            catch (Exception ex) when (IsSettingsStorageError(ex))
            {
                // 内存设置已生效；下次用户修改时再尝试保存，不在每帧重复读写。
                ShowSettingsNotice("设置暂未保存，本次操作仍生效；下次修改时会重试。");
            }
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
