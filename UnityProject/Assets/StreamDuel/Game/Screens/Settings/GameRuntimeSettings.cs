using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using StreamDuel.Core;
using UnityEngine;

namespace StreamDuel.Game
{
    public static class GameRuntimeSettings
    {
        static GameSettings current = new GameSettings();
        static ISaveStore store;
        static bool initialized;
        static string notice;

        public static GameSettings Current
        {
            get { EnsureLoaded(); return current; }
        }

        public static string Notice
        {
            get { EnsureLoaded(); return notice; }
        }

        public static bool IsSmokeTest => Environment.GetCommandLineArgs().Any(argument =>
            argument == "--smoke-test" || argument == "--ui-smoke-test" || argument == "--menu-smoke-test"
            || argument == "--anim-smoke-test" || argument == "--t66-smoke-test" || argument == "--t71-smoke-test"
            || argument == "--battle-ui-smoke");

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Reset()
        {
            // 关闭域重载时也不能沿用上一轮 Play 的存档对象或失败标记。
            current = new GameSettings();
            store = null;
            initialized = false;
            notice = null;
        }

        static void EnsureLoaded()
        {
            if (initialized) return;
            initialized = true;
            if (IsSmokeTest)
            {
                // 验收默认只用内存，连玩家存档目录和 PlayerPrefs 也不访问。
                store = new MemorySaveStore();
                return;
            }
            try
            {
                string directory = Application.persistentDataPath;
                var source = new FileSaveStore(directory);
                bool migrate = SettingsFileMissing(Path.Combine(directory, "settings.json"));
                var loaded = source.LoadSettings();
                if (migrate)
                {
                    loaded.FastPlay = PlayerPrefs.GetInt("sd.fast", loaded.FastPlay ? 1 : 0) == 1;
                    loaded.TurnTimer = PlayerPrefs.GetInt("sd.timer", loaded.TurnTimer ? 1 : 0) == 1;
                    loaded.DefaultPack = PlayerPrefs.GetString("sd.pack", loaded.DefaultPack);
                }
                Copy(GameSettings.FromJson(loaded.ToJson()), current);
                store = source;
            }
            catch (Exception error) when (IsStorageError(error))
            {
                notice = "设置暂时无法读取，当前设置仅在本次运行中生效。";
                store = null;
            }
        }

        public static void UseStore(ISaveStore source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            initialized = true;
            store = null;
            try
            {
                var loaded = source.LoadSettings() ?? new GameSettings();
                Copy(GameSettings.FromJson(loaded.ToJson()), current);
                store = source;
                notice = null;
            }
            catch (Exception error) when (IsStorageError(error))
            {
                notice = "设置暂时无法读取，当前设置仅在本次运行中生效。";
            }
        }

        public static bool TrySave(GameSettings value, out string message)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            EnsureLoaded();
            // 共用对象保持身份不变，旧画面的引用不能在设置页保存后成为陈旧副本。
            Copy(GameSettings.FromJson(value.ToJson()), current);
            if (store == null)
            {
                message = notice = "设置暂时无法读取，当前设置仅在本次运行中生效，未覆盖原存档。";
                return false;
            }
            try
            {
                store.SaveSettings(GameSettings.FromJson(current.ToJson()));
                notice = null;
                message = store is MemorySaveStore ? "设置已应用（本次运行）。" : "设置已保存并应用。";
                return true;
            }
            catch (Exception error) when (IsStorageError(error))
            {
                // 写入失败仍保留内存选择，下一次明确保存时才重试。
                message = notice = "设置暂未保存，本次选择仍生效；下次保存时会重试。";
                return false;
            }
        }

        public static void ApplyDisplay()
        {
            EnsureLoaded();
            if (IsSmokeTest) return;
            var mode = current.Fullscreen ? FullScreenMode.FullScreenWindow : FullScreenMode.Windowed;
            if (Screen.width != current.Width || Screen.height != current.Height || Screen.fullScreenMode != mode)
                Screen.SetResolution(current.Width, current.Height, mode);
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

        static void Copy(GameSettings source, GameSettings target)
        {
            target.FastPlay = source.FastPlay;
            target.TurnTimer = source.TurnTimer;
            target.Animations = source.Animations;
            target.ColorblindMode = source.ColorblindMode;
            target.AnimationSpeed = source.AnimationSpeed;
            target.TurnSeconds = source.TurnSeconds;
            target.Fullscreen = source.Fullscreen;
            target.Width = source.Width;
            target.Height = source.Height;
            target.AiLevel = source.AiLevel;
            target.DefaultPack = source.DefaultPack;
            target.Language = source.Language;
            target.Version = source.Version;
        }

        sealed class MemorySaveStore : ISaveStore
        {
            string settings = new GameSettings().ToJson();
            string profile = new Profile().ToJson();
            readonly Dictionary<string, string> replays = new Dictionary<string, string>(StringComparer.Ordinal);

            public GameSettings LoadSettings() => GameSettings.FromJson(settings);
            public void SaveSettings(GameSettings value) { settings = value.ToJson(); }
            public Profile LoadProfile() => Profile.FromJson(profile);
            public void SaveProfile(Profile value) { profile = value.ToJson(); }
            public void SaveReplay(string name, string json) { replays[name] = json; }
            public List<string> ListReplays() => replays.Keys.OrderBy(name => name, StringComparer.Ordinal).ToList();
            public string LoadReplay(string name) => replays[name];
        }
    }
}
