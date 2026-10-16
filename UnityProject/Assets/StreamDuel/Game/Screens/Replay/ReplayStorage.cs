using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using StreamDuel.Core;
using UnityEngine;

namespace StreamDuel.Game
{
    public static class ReplayStorage
    {
        public static ISaveStore OpenDefault()
        {
            string[] args = Environment.GetCommandLineArgs();
            int smoke = Array.IndexOf(args, "--ui-smoke-test");
            if (GameRuntimeSettings.IsSmokeTest)
            {
                if (smoke < 0) return new MemoryStore();
                if (smoke + 1 >= args.Length || !Path.IsPathFullyQualified(args[smoke + 1]))
                    throw new ArgumentException("界面验收需要绝对输出目录。");
                string project = Directory.GetParent(Application.dataPath).Parent.FullName;
                string output = Path.GetFullPath(args[smoke + 1]);
                if (!output.StartsWith(project + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("界面验收输出目录必须位于当前工作树内。");
                // 验收的空态和菜单入口也必须隔离，不能悄悄打开玩家的真实录像目录。
                return new FileSaveStore(Path.Combine(output, "saves"));
            }
            return new FileSaveStore(Application.persistentDataPath);
        }

        public static ContentDb ResolveBuiltIn(string packId)
        {
            var policy = UnityPackPolicy.Load();
            string reason = policy.PackReason(packId);
            if (reason != null) throw new PackDisabledException(reason);
            var rules = Resources.Load<TextAsset>("StreamDuelRules");
            if (rules == null) throw new InvalidOperationException("缺少对战规则，无法打开录像。");
            var loaded = new List<PackLoadResult>();
            foreach (var asset in Resources.LoadAll<TextAsset>("Packs").Where(asset => asset.name != "LICENSE"))
                loaded.Add(PackLoader.LoadJson(rules.text, asset.text, asset.name, policy));
            if (!GameRuntimeSettings.IsSmokeTest)
                loaded.AddRange(PackLoader.LoadDirectory(rules.text, Path.Combine(Application.persistentDataPath, "packs"), policy));
            if (packId != null && packId.StartsWith("mix:", StringComparison.Ordinal))
            {
                var ids = packId.Substring(4).Split(',').Select(Uri.UnescapeDataString).ToArray();
                var combined = ContentDb.Merge(ids.Select(id => FindPack(loaded, id)).ToArray());
                if (combined.PackId != packId) throw new FormatException("混合内容包编号不是规范格式。");
                return combined;
            }
            return FindPack(loaded, packId);
        }

        static ContentDb FindPack(IEnumerable<PackLoadResult> loaded, string packId)
        {
            var match = loaded.FirstOrDefault(item => item.PackId == packId);
            if (match?.Db != null) return match.Db;
            if (match?.IsDisabled == true) throw new PackDisabledException(match.Error);
            throw new InvalidOperationException(match?.Error
                ?? "未找到录像所需的内容包：" + packId + "。请先载入相同版本的内容包。");
        }

        public static void CheckPolicy(BattleRecord record)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));
            string reason = UnityPackPolicy.Load().CheckTeam(record.PackId,
                record.Teams?.Where(team => team != null).SelectMany(team => team));
            if (reason != null) throw new PackDisabledException(reason);
        }

        public static string Save(ISaveStore store, Battle battle)
        {
            if (store == null) throw new ArgumentNullException(nameof(store));
            if (battle == null) throw new ArgumentNullException(nameof(battle));
            if (battle.Phase != Phase.Over) throw new InvalidOperationException("对战结束后才能保存结算录像。");
            if (store is FileSaveStore files) return files.SaveReplay(battle);
            // 时间与唯一编号只生成文件名，不影响战斗随机流或记录内容。
            string name = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fffffff", CultureInfo.InvariantCulture)
                + "_" + Guid.NewGuid().ToString("N");
            store.SaveReplay(name, battle.Record.ToJson());
            return name;
        }

        sealed class MemoryStore : ISaveStore
        {
            string settings = new GameSettings().ToJson();
            string profile = new Profile().ToJson();
            readonly Dictionary<string, string> replays = new Dictionary<string, string>(StringComparer.Ordinal);
            public GameSettings LoadSettings() => GameSettings.FromJson(settings);
            public void SaveSettings(GameSettings value) { settings = value.ToJson(); }
            public Profile LoadProfile() => Profile.FromJson(profile);
            public void SaveProfile(Profile value) { profile = value.ToJson(); }
            public List<string> ListReplays() => replays.Keys.OrderBy(name => name, StringComparer.Ordinal).ToList();
            public string LoadReplay(string name) => replays[name];
            public void SaveReplay(string name, string json) { replays[name] = json; }
        }
    }
}
