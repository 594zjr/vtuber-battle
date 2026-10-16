using System;
using System.Collections.Generic;
using System.IO;
using System.Security;
using StreamDuel.Core;
using UnityEngine;

namespace StreamDuel.Game
{
    // 读取失败时保留内存默认值，但不获得写入权限，避免覆盖尚未成功读取的存档。
    internal sealed class BattleSetupSettings
    {
        static readonly Dictionary<string, string> isolatedPicks = new Dictionary<string, string>(StringComparer.Ordinal);
        static string isolatedPackId;
        readonly bool isolated;
        readonly HashSet<string> unreadablePicks = new HashSet<string>(StringComparer.Ordinal);
        ISaveStore store;
        string settingsNotice, pickNotice;

        public GameSettings Settings { get; private set; } = new GameSettings { DefaultPack = "original" };
        public string Notice => string.IsNullOrEmpty(settingsNotice) ? pickNotice
            : string.IsNullOrEmpty(pickNotice) ? settingsNotice : settingsNotice + " " + pickNotice;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetIsolatedPicks()
        {
            // 即使 Editor 关闭域重载，也不能继承上次验收会话的选择。
            isolatedPicks.Clear();
            isolatedPackId = null;
        }

        public BattleSetupSettings(bool isolated)
        {
            this.isolated = isolated;
            // 验收模式连存档路径和 PlayerPrefs 都不查询，也不会因初始化而创建目录。
            if (isolated)
            {
                Settings.DefaultPack = isolatedPackId ?? "original";
                return;
            }
            try
            {
                string directory = Application.persistentDataPath;
                var candidate = new FileSaveStore(directory);
                bool migrate = SettingsFileMissing(Path.Combine(directory, "settings.json"));
                var loaded = candidate.LoadSettings();
                if (migrate)
                {
                    loaded.FastPlay = PlayerPrefs.GetInt("sd.fast", loaded.FastPlay ? 1 : 0) == 1;
                    loaded.TurnTimer = PlayerPrefs.GetInt("sd.timer", loaded.TurnTimer ? 1 : 0) == 1;
                    loaded.DefaultPack = PlayerPrefs.GetString("sd.pack", loaded.DefaultPack);
                }
                Settings = loaded;
                store = candidate;
            }
            catch (Exception error) when (IsStorageError(error))
            {
                settingsNotice = "设置暂时无法读取，当前设置仅在本次运行中生效。";
            }
        }

        public void Save()
        {
            if (isolated) return;
            if (store == null)
            {
                settingsNotice = "设置暂时无法读取，当前设置仅在本次运行中生效。";
                return;
            }
            try
            {
                store.SaveSettings(Settings);
                settingsNotice = null;
            }
            catch (Exception error) when (IsStorageError(error))
            {
                // 保存失败不撤销本次选择；用户下一次修改时再次尝试，不按帧重复写入。
                settingsNotice = "设置暂未保存，本次操作仍生效；下次修改时会重试。";
            }
        }

        public string LoadPick(string packId)
        {
            if (isolated) return isolatedPicks.TryGetValue(packId, out var isolatedSaved) ? isolatedSaved : "";
            try
            {
                string saved = PlayerPrefs.GetString("sd.pick." + packId, "");
                unreadablePicks.Remove(packId);
                pickNotice = null;
                return saved;
            }
            catch (Exception error) when (IsStorageError(error) || error is UnityException)
            {
                unreadablePicks.Add(packId);
                pickNotice = "阵容暂时无法读取，本次选择不会覆盖原有阵容。";
                return "";
            }
        }

        public void SavePick(string packId, IList<string> ids)
        {
            string saved = string.Join(",", ids);
            if (isolated)
            {
                isolatedPicks[packId] = saved;
                isolatedPackId = packId;
                return;
            }
            if (unreadablePicks.Contains(packId)) return;
            try
            {
                PlayerPrefs.SetString("sd.pick." + packId, saved);
                pickNotice = null;
            }
            catch (Exception error) when (IsStorageError(error) || error is UnityException)
            {
                pickNotice = "阵容暂未保存，本次选择仍生效；下次修改时会重试。";
            }
        }

        static bool SettingsFileMissing(string path)
        {
            try { File.GetAttributes(path); return false; }
            catch (FileNotFoundException) { return true; }
            catch (DirectoryNotFoundException) { return true; }
        }

        static bool IsStorageError(Exception error)
        {
            return error is IOException || error is UnauthorizedAccessException || error is SecurityException;
        }
    }
}
