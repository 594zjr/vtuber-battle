using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using StreamDuel.Core;
using UnityEngine;

namespace StreamDuel.Game
{
    public sealed partial class LocalBattleSession
    {
        List<ContentDb> packs = new List<ContentDb>();
        List<string> packErrors = new List<string>();
        string teamPolicyNotice;
        bool contentLoaded, contentLoadSucceeded;

        public IReadOnlyList<ContentDb> MenuPacks => packs.AsReadOnly();
        public IReadOnlyList<string> MenuPackErrors => packErrors.AsReadOnly();
        public string MenuSettingsNotice => settingsNotice;
        public ContentDb MenuPack => db;
        public bool MenuPackLocked => b != null;

        internal void EnsureMenuContentLoaded()
        {
            // 只恢复曾成功加载但后来遗失的内容；无包或失败状态不能在每次打开菜单时反复重试。
            if (contentLoaded && (db != null || !contentLoadSucceeded)) return;
            contentLoaded = true;
            contentLoadSucceeded = false;
            // 旧菜单可能仍持有列表的只读包装，重建时不能清空它观察到的原快照。
            packs = new List<ContentDb>();
            packErrors = new List<string>();
            db = null;
            b = null;
            teamBattleRequest = null;
            ClearTeamMatch();
            playing = false;
            teamPolicyNotice = null;
            teamPolicySnapshot = PackPolicy.Empty;
            LoadSettingsSafely();
            timerOn = settings.TurnTimer;
            PackPolicy policy;
            try { policy = UnityPackPolicy.Load(); teamPolicySnapshot = policy; }
            catch (Exception error)
            {
                packErrors.Add("下架策略无法读取，已停止加载内容包：" + error.Message);
                return;
            }
            var rules = Resources.Load<TextAsset>("StreamDuelRules");
            if (rules == null) { packErrors.Add("缺少规则数据。"); return; }
            foreach (var asset in Resources.LoadAll<TextAsset>("Packs").Where(item => item.name != "LICENSE")
                .OrderBy(item => item.name == "original" ? 1 : 0).ThenBy(item => item.name))
            {
                var result = PackLoader.LoadJson(rules.text, asset.text, asset.name + ".json", policy);
                if (result.IsDisabled) continue;
                if (result.Db == null) packErrors.Add(result.FileName + "：" + result.Error);
                else if (packs.Any(pack => pack.PackId == result.Db.PackId))
                    packErrors.Add(result.FileName + "：内容包 id " + result.Db.PackId + " 重复，已使用先加载的内置包");
                else packs.Add(result.Db);
            }
            var external = SmokeRequested ? new List<PackLoadResult>()
                : PackLoader.LoadDirectory(rules.text, Path.Combine(Application.persistentDataPath, "packs"), policy);
            foreach (var result in external)
            {
                if (result.Error != null) { packErrors.Add(result.FileName + "：" + result.Error); continue; }
                if (packs.Any(pack => pack.PackId == result.Db.PackId))
                {
                    packErrors.Add(result.FileName + "：内容包 id " + result.Db.PackId + " 与内置包重复，已优先使用内置包");
                    continue;
                }
                packs.Add(result.Db);
            }
            if (packs.Count == 0) return;
            string preferred = settings.DefaultPack;
            LoadPack(packs.Any(pack => pack.PackId == preferred) ? preferred : packs[0].PackId);
            contentLoadSucceeded = true;
        }

        public void SelectMenuPack(string id)
        {
            EnsureMenuContentLoaded();
            if (db != null && db.PackId == id) return;
            if (MenuPackLocked) throw new InvalidOperationException("请先返回组队，再切换内容包。");
            if (!packs.Any(pack => pack.PackId == id)) throw new ArgumentException("找不到内容包：" + id, nameof(id));
            LoadPack(id);
        }

        void LoadPack(string id)
        {
            db = packs.First(pack => pack.PackId == id);
            settings.DefaultPack = id;
            SaveSettingsSafely();
            string saved = SmokeRequested ? "" : PlayerPrefs.GetString("sd.pick." + id, "");
            var savedIds = saved.Split(',');
            teamPolicyNotice = db.Policy.CheckTeam(id, savedIds);
            if (teamPolicyNotice != null)
            {
                // 下架不能静默换掉队友或覆盖存档，保留原名单待玩家明确重选。
                teamPolicyNotice += "；已停止使用原队伍，请重新选择六位主播。";
                pick = new List<string>();
            }
            else
            {
                pick = savedIds.Where(value => db.Streamers.Any(streamer => streamer.Id == value)).Distinct().Take(6).ToList();
                if (pick.Count == 0)
                {
                    pick = TeamBuilder.Suggest(db, null, Array.Empty<string>(), unchecked((uint)Environment.TickCount));
                    SavePick();
                }
            }
        }

        void SavePick()
        {
            teamPolicyNotice = null;
            if (!SmokeRequested) PlayerPrefs.SetString("sd.pick." + db.PackId, string.Join(",", pick));
        }
    }
}
