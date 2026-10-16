using System;
using System.Collections.Generic;
using System.Linq;
using StreamDuel.Core;

namespace StreamDuel.Game
{
    public sealed partial class BattleScreen
    {
        // 菜单与旧版组队共用加载和保存入口，避免外部包及玩家选择出现两份状态。
        public IReadOnlyList<ContentDb> MenuPacks => packs.AsReadOnly();
        public IReadOnlyList<string> MenuPackErrors => packErrors.AsReadOnly();
        public string MenuSettingsNotice => settingsNotice;
        public ContentDb MenuPack => db;
        public bool MenuPackLocked => view == View.Battle && b != null;

        internal void EnsureMenuContentLoaded()
        {
            // 主菜单让旧组件保持禁用；脚本重载后仍须恢复它提供的内容包列表。
            if (db == null) OnEnable();
        }

        public void SelectMenuPack(string id)
        {
            if (db != null && db.PackId == id) return;
            if (MenuPackLocked)
                throw new InvalidOperationException("请先返回旧版组队，再切换内容包。");
            if (!packs.Any(pack => pack.PackId == id))
                throw new ArgumentException("找不到内容包：" + id, nameof(id));
            LoadPack(id);
        }
    }
}
