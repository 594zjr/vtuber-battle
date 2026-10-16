using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using StreamDuel.Core;
using UnityEngine;
using UnityEngine.UIElements;

namespace StreamDuel.Game
{
    public sealed class BattleScreenArgs
    {
        public LocalBattleSession Session;
        public TeamBattleRequest OriginalRequest;
        public Battle Battle;
        public ContentDb Db;
        public GameSettings Settings;
        public IList<string>[] Teams;
        public string[] Names;
        public uint Seed = 42;
        public bool BanOne;
        public bool LeadPick = true;
        public IBattleAnimator Animator;
        public IPortraitProvider Portraits;
        public bool Isolated;
        public float TimerRemaining = 10f;
        public List<string> Modifiers;
        internal TurnClock Clock;
        internal string SettingsNotice;
        internal Battle PendingBattle;
        internal BattleViewModel PendingBefore;
        internal int PendingFromEvent = -1, PendingFromLog;
    }

    // 独立参数入口保留的组队组件；正式导航使用 TeamScreen。
    public sealed class BattleSetupPanel : IDisposable
    {
        sealed class Pack
        {
            public ContentDb Db;
            public string Source;
            public string Label => (Db.PackName ?? Db.PackId) + "（" + Source + " · " + Db.PackId + "）";
        }

        readonly List<Pack> packs = new List<Pack>();
        readonly List<string> notices = new List<string>();
        readonly List<Action> unbind = new List<Action>();
        readonly List<Action> rosterUnbind = new List<Action>();
        readonly List<Action> slotUnbind = new List<Action>();
        readonly Dictionary<string, List<string>> selections = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        readonly SolidColorPortraitProvider portraits = new SolidColorPortraitProvider();
        readonly Action<BattleScreenArgs> start;
        readonly ScreenRouter router;
        readonly bool isolated;
        readonly BattleSetupSettings settingsStore;
        readonly GameSettings settings;
        ContentDb db;
        List<string> pick = new List<string>();
        VisualElement slots, roster;
        ScrollView details;
        Label notice, validation, settingsNotice;
        Button startButton;
        TextField seedField;
        bool banOne, leadPick = true, disposed;

        public VisualElement Root { get; }

        public static bool IsolatedRequested => Environment.GetCommandLineArgs().Any(argument =>
            argument == "--battle-ui-smoke" || argument == "--ui-smoke-test" || argument == "--smoke-test");

        public BattleSetupPanel(ScreenRouter router, Action<BattleScreenArgs> start, Action menu)
        {
            this.router = router ?? throw new ArgumentNullException(nameof(router));
            this.start = start ?? throw new ArgumentNullException(nameof(start));
            if (menu == null) throw new ArgumentNullException(nameof(menu));
            isolated = IsolatedRequested;
            settingsStore = new BattleSetupSettings(isolated);
            settings = settingsStore.Settings;
            LoadPacks();
            Root = new VisualElement { name = "battle-setup" };
            Root.style.flexGrow = 1;
            Root.style.minHeight = 0;
            Root.style.paddingLeft = Root.style.paddingRight = 24;
            Root.style.paddingTop = Root.style.paddingBottom = 16;
            Root.style.backgroundColor = (Color)new Color32(12, 17, 36, 255);
            Root.style.fontSize = 24;

            var header = Row();
            var title = Text("组队开播", 36);
            title.style.unityFont = router.Resources.BoldFont;
            title.style.flexGrow = 1;
            header.Add(title);
            AddButton(header, "back-button", "主菜单", menu, unbind);
            Root.Add(header);
            settingsNotice = Text("", 20);
            settingsNotice.name = "setup-settings-notice";
            settingsNotice.style.color = (Color)new Color32(255, 181, 71, 255);
            Root.Add(settingsNotice);
            RefreshSettingsNotice();
            if (packs.Count == 0)
            {
                Root.Add(Text("没有可用的内容包。"));
                Root.Add(Text(string.Join("\n", notices), 20));
                return;
            }

            var chosen = packs.FindIndex(pack => pack.Db.PackId == settings.DefaultPack);
            if (chosen < 0) chosen = Math.Max(0, packs.FindIndex(pack => pack.Db.PackId == "original"));
            var packChoice = new DropdownField("内容包", packs.Select(pack => pack.Label).ToList(), chosen) { name = "setup-pack" };
            StyleInput(packChoice);
            packChoice.style.marginTop = packChoice.style.marginBottom = 8;
            Root.Add(packChoice);
            Bind(packChoice, value => SelectPack(packs.FindIndex(pack => pack.Label == value)));
            if (notices.Count > 0)
            {
                var warnings = new Foldout { text = "内容包与设置提示（" + notices.Count + "）", value = false };
                var errors = new ScrollView();
                errors.style.maxHeight = 120;
                errors.Add(Text(string.Join("\n", notices), 20));
                warnings.Add(errors);
                Root.Add(warnings);
            }
            BuildOptions();
            slots = Row();
            slots.name = "setup-slots";
            slots.style.flexWrap = Wrap.Wrap;
            Root.Add(slots);
            var actions = Row();
            seedField = new TextField("种子") { name = "setup-seed", value = "42", isDelayed = true };
            StyleInput(seedField);
            seedField.style.width = 310;
            seedField.style.marginRight = 12;
            Bind(seedField, _ => RefreshValidation());
            actions.Add(seedField);
            AddButton(actions, "setup-suggest", "随机建议", Suggest, unbind);
            AddButton(actions, "setup-clear", "清空", () => { pick.Clear(); RefreshTeam(); }, unbind);
            startButton = AddButton(actions, "setup-start", "开播（0/6）", StartBattle, unbind);
            Root.Add(actions);
            validation = Text("", 20);
            validation.name = "setup-validation";
            Root.Add(validation);
            notice = Text("", 20);
            notice.name = "setup-notice";
            Root.Add(notice);

            var body = Row();
            body.style.flexGrow = 1;
            body.style.flexBasis = 0;
            body.style.flexShrink = 1;
            body.style.minHeight = 0;
            body.style.alignItems = Align.Stretch;
            var rosterScroll = new ScrollView { name = "setup-roster-scroll" };
            rosterScroll.style.width = Length.Percent(62);
            rosterScroll.style.minWidth = 0;
            rosterScroll.style.minHeight = 0;
            rosterScroll.style.marginRight = 16;
            rosterScroll.style.flexShrink = 1;
            roster = rosterScroll.contentContainer;
            roster.style.flexDirection = FlexDirection.Row;
            roster.style.flexWrap = Wrap.Wrap;
            roster.style.alignContent = Align.FlexStart;
            body.Add(rosterScroll);
            details = new ScrollView { name = "setup-detail" };
            details.style.flexGrow = 1;
            details.style.flexBasis = 0;
            details.style.minWidth = 0;
            details.style.minHeight = 0;
            details.style.paddingLeft = details.style.paddingRight = 12;
            details.style.backgroundColor = (Color)new Color32(21, 28, 56, 255);
            body.Add(details);
            Root.Add(body);
            SelectPack(chosen);
        }

        void SaveSettings()
        {
            settingsStore.Save();
            RefreshSettingsNotice();
        }

        void RefreshSettingsNotice()
        {
            settingsNotice.text = settingsStore.Notice ?? "";
            settingsNotice.style.display = string.IsNullOrEmpty(settingsNotice.text) ? DisplayStyle.None : DisplayStyle.Flex;
        }

        void LoadPacks()
        {
            var rules = UnityEngine.Resources.Load<TextAsset>("StreamDuelRules");
            if (rules == null) { notices.Add("缺少内置规则文件 StreamDuelRules。"); return; }
            foreach (var asset in UnityEngine.Resources.LoadAll<TextAsset>("Packs")
                .Where(asset => asset.name != "LICENSE").OrderBy(asset => asset.name != "original")
                .ThenBy(asset => asset.name, StringComparer.Ordinal))
            {
                try { AddPack(ContentDb.FromJson(rules.text, asset.text), "内置", asset.name); }
                catch (Exception error) { notices.Add(asset.name + "：" + error.Message); }
            }
            if (isolated) return;
            foreach (var result in PackLoader.LoadDirectory(rules.text, Path.Combine(Application.persistentDataPath, "packs")))
            {
                if (result.Error != null) notices.Add(result.FileName + "：" + result.Error);
                else AddPack(result.Db, "外部", result.FileName);
            }
        }

        void AddPack(ContentDb content, string source, string file)
        {
            if (packs.Any(pack => pack.Db.PackId == content.PackId))
                notices.Add(file + "：内容包 id " + content.PackId + " 重复，保留先加载的内容包。");
            else packs.Add(new Pack { Db = content, Source = source });
        }

        void BuildOptions()
        {
            var options = Row();
            options.style.flexWrap = Wrap.Wrap;
            var levelNames = new List<string> { "简单", "普通", "困难", "专家" };
            var levels = new[] { AiLevel.Easy, AiLevel.Normal, AiLevel.Hard, AiLevel.Expert };
            var difficulty = new DropdownField("电脑难度", levelNames, Math.Max(0, Array.IndexOf(levels, settings.AiLevel))) { name = "setup-difficulty" };
            StyleInput(difficulty);
            difficulty.style.width = 320;
            Bind(difficulty, value => { settings.AiLevel = levels[levelNames.IndexOf(value)]; SaveSettings(); });
            options.Add(difficulty);
            var speedNames = new List<string> { "1 倍", "2 倍" };
            var speed = new DropdownField("播放速度", speedNames, settings.FastPlay ? 1 : 0) { name = "setup-speed" };
            StyleInput(speed);
            speed.style.width = 280;
            Bind(speed, value => { settings.FastPlay = value == speedNames[1]; SaveSettings(); });
            options.Add(speed);
            AddToggle(options, "setup-lead", "选择首发", true, value => leadPick = value);
            AddToggle(options, "setup-ban", "各禁用一位", false, value => banOne = value);
            AddToggle(options, "setup-timer", "10 秒计时", settings.TurnTimer, value => { settings.TurnTimer = value; SaveSettings(); });
            AddToggle(options, "setup-animations", "播放动画", settings.Animations, value => { settings.Animations = value; SaveSettings(); });
            Root.Add(options);
        }

        void SelectPack(int index)
        {
            if (index < 0 || index >= packs.Count || disposed) return;
            db = packs[index].Db;
            settings.DefaultPack = db.PackId;
            SaveSettings();
            if (!selections.TryGetValue(db.PackId, out pick))
            {
                string saved = settingsStore.LoadPick(db.PackId);
                pick = saved.Split(',').Where(id => db.Streamers.Any(streamer => streamer.Id == id)).Distinct().Take(6).ToList();
                selections.Add(db.PackId, pick);
                if (pick.Count == 0)
                {
                    try { pick.AddRange(TeamBuilder.Suggest(db, null, Array.Empty<string>(), 42)); }
                    catch (InvalidOperationException) { }
                }
            }
            notice.text = (db.PackNotice ?? "") + "  选满 6 位；电脑优先使用你未选的主播。相同种子得到相同建议。";
            RefreshTeam();
            if (db.Streamers.Count > 0) ShowDetails(db.Streamers[0]);
            else details.Clear();
        }

        void Suggest()
        {
            if (!TrySeed(out uint seed)) { RefreshValidation(); return; }
            try
            {
                var suggested = TeamBuilder.Suggest(db, null, Array.Empty<string>(), seed);
                pick.Clear();
                pick.AddRange(suggested);
                RefreshTeam();
                ShowDetails(db.Streamer(pick[0]));
            }
            catch (InvalidOperationException error) { validation.text = error.Message; }
        }

        void RefreshTeam()
        {
            settingsStore.SavePick(db.PackId, pick);
            RefreshSettingsNotice();
            Release(slotUnbind);
            Release(rosterUnbind);
            slots.Clear();
            roster.Clear();
            for (int index = 0; index < 6; index++)
            {
                int slot = index;
                string name = slot < pick.Count ? db.Streamer(pick[slot]).Name + " ×" : "空位";
                var button = AddButton(slots, "setup-slot-" + slot, (slot + 1) + " · " + name, () =>
                {
                    if (slot >= pick.Count) return;
                    pick.RemoveAt(slot);
                    RefreshTeam();
                }, slotUnbind);
                button.userData = slot < pick.Count ? pick[slot] : null;
                button.style.flexGrow = 1;
                button.style.flexBasis = 0;
                button.style.minWidth = 0;
                button.SetEnabled(slot < pick.Count);
                button.tooltip = "点击移出队伍，剩余成员保持顺序";
            }
            foreach (var streamer in db.Streamers)
            {
                var card = new VisualElement { name = "setup-card-" + streamer.Id };
                card.style.width = Length.Percent(48);
                card.style.marginRight = Length.Percent(2);
                card.style.marginBottom = 12;
                card.style.paddingLeft = card.style.paddingRight = 12;
                card.style.paddingTop = card.style.paddingBottom = 10;
                card.style.backgroundColor = (Color)new Color32(21, 28, 56, 255);
                var top = Row();
                var portrait = new Image { image = portraits.Get(db, streamer.Id), scaleMode = ScaleMode.ScaleToFit };
                portrait.style.width = portrait.style.height = 48;
                portrait.style.marginRight = 10;
                top.Add(portrait);
                var name = Text(streamer.Name + " · " + Describe.Tracks(db, streamer));
                name.style.flexGrow = 1;
                name.style.flexBasis = 0;
                name.style.minWidth = 0;
                top.Add(name);
                card.Add(top);
                card.Add(Text(streamer.Blurb ?? "", 20));
                card.Add(Text(Stats(streamer), 20));
                card.Add(Text("人设 · " + db.Personas[streamer.Persona].Name, 20));
                var buttons = Row();
                bool selected = pick.Contains(streamer.Id);
                var choose = AddButton(buttons, "setup-pick-" + streamer.Id, selected ? "移出队伍" : "加入队伍", () =>
                {
                    if (pick.Contains(streamer.Id)) pick.Remove(streamer.Id);
                    else if (pick.Count < 6) pick.Add(streamer.Id);
                    RefreshTeam();
                    ShowDetails(streamer);
                }, rosterUnbind);
                choose.SetEnabled(selected || pick.Count < 6);
                AddButton(buttons, "setup-details-" + streamer.Id, "详情", () => ShowDetails(streamer), rosterUnbind);
                card.Add(buttons);
                roster.Add(card);
            }
            RefreshValidation();
        }

        string Stats(StreamerDef streamer)
        {
            return string.Join("  ", new[] { "hp", "atk", "def", "spa", "sdef", "spd" }
                .Select(stat => db.StatName(stat) + " " + streamer.Stats[stat]));
        }

        void ShowDetails(StreamerDef streamer)
        {
            details.Clear();
            details.scrollOffset = Vector2.zero;
            var title = Text(streamer.Name + " · " + Describe.Tracks(db, streamer), 30);
            title.style.unityFont = router.Resources.BoldFont;
            details.Add(title);
            details.Add(Text(streamer.Blurb ?? "", 20));
            details.Add(Text(Stats(streamer), 20));
            var persona = db.Personas[streamer.Persona];
            details.Add(Text("人设 · " + persona.Name));
            AddEffect(details, Describe.Persona(db, persona));
            var skills = streamer.Skills.Concat(new[] { streamer.Fifth }).ToList();
            for (int index = 0; index < skills.Count; index++)
            {
                var skill = db.Skills[skills[index]];
                var heading = Text((index == 4 ? "第五技能 · " : (index + 1) + " · ") + skill.Name);
                heading.style.marginTop = 14;
                if (index == 4) heading.style.color = (Color)new Color32(255, 181, 71, 255);
                details.Add(heading);
                details.Add(Text(db.Types[skill.Type].Name + " · " + db.CatNames[skill.Cat] + " · 威力 " + skill.Power
                    + " · PP " + skill.Pp + " · 命中 " + skill.Acc + "% · 先制 " + skill.Prio, 20));
                foreach (var effect in skill.Effects) AddEffect(details, Describe.Effect(db, effect));
            }
        }

        static void AddEffect(VisualElement parent, EffectText effect)
        {
            parent.Add(Text("〔" + effect.At + "〕" + effect.Text
                + (string.IsNullOrEmpty(effect.Dispel) ? "" : " 〔" + effect.Dispel + "〕"), 20));
        }

        void RefreshValidation()
        {
            if (db == null || startButton == null || validation == null) return;
            var errors = TeamCheck.Validate(db, null, pick);
            if (!TrySeed(out _)) errors.Add("种子须为 0～4294967295 的整数。");
            validation.text = errors.Count == 0 ? "阵容已就绪；点击号位可移出成员，技能详情见右侧。" : string.Join("；", errors);
            startButton.text = "开播（" + pick.Count + "/6）";
            startButton.SetEnabled(errors.Count == 0);
        }

        bool TrySeed(out uint seed)
        {
            return uint.TryParse(seedField.value, NumberStyles.None, CultureInfo.InvariantCulture, out seed);
        }

        void StartBattle()
        {
            if (disposed || !TrySeed(out uint seed)) return;
            var errors = TeamCheck.Validate(db, null, pick);
            if (errors.Count > 0) { validation.text = string.Join("；", errors); return; }
            var foe = db.Streamers.Select(streamer => streamer.Id).Where(id => !pick.Contains(id))
                .Concat(pick).Take(6).ToList();
            start(new BattleScreenArgs
            {
                Db = db, Settings = GameSettings.FromJson(settings.ToJson()),
                Teams = new IList<string>[] { pick.ToList(), foe }, Names = new[] { "你", "对手" },
                Seed = seed, BanOne = banOne, LeadPick = leadPick, Isolated = isolated
            });
        }

        static Label Text(string value, int size = 24)
        {
            var label = new Label(value) { enableRichText = false };
            label.style.fontSize = size;
            label.style.whiteSpace = WhiteSpace.Normal;
            label.style.marginTop = label.style.marginBottom = 4;
            label.style.flexShrink = 0;
            return label;
        }

        static VisualElement Row()
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.flexShrink = 0;
            return row;
        }

        Button AddButton(VisualElement parent, string name, string label, Action clicked, List<Action> callbacks)
        {
            var button = new Button { name = name, text = label, enableRichText = false };
            button.style.fontSize = 22;
            button.style.minHeight = 44;
            button.style.paddingLeft = button.style.paddingRight = 12;
            button.style.marginRight = 8;
            button.style.marginTop = button.style.marginBottom = 5;
            button.style.whiteSpace = WhiteSpace.Normal;
            button.style.backgroundColor = (Color)new Color32(28, 37, 73, 255);
            button.style.color = (Color)new Color32(232, 235, 250, 255);
            button.clicked += clicked;
            callbacks.Add(() => button.clicked -= clicked);
            parent.Add(button);
            return button;
        }

        void AddToggle(VisualElement parent, string name, string label, bool initial, Action<bool> changed)
        {
            var toggle = new Toggle(label) { name = name, value = initial };
            toggle.style.marginLeft = 12;
            toggle.style.marginTop = toggle.style.marginBottom = 8;
            Bind(toggle, changed);
            parent.Add(toggle);
        }

        static void StyleInput(BaseField<string> field)
        {
            // 输入区使用浅底深字，避免继承页面的浅色文字；保留原生焦点边框和文本选区。
            var input = field.Q<VisualElement>(className: "unity-base-field__input");
            if (input == null) return;
            Color ink = new Color32(12, 17, 36, 255);
            input.style.backgroundColor = (Color)new Color32(232, 235, 250, 255);
            input.style.color = ink;
            input.Query<TextElement>().ForEach(element => element.style.color = ink);
        }

        void Bind<T>(BaseField<T> field, Action<T> changed)
        {
            EventCallback<ChangeEvent<T>> callback = evt => changed(evt.newValue);
            field.RegisterValueChangedCallback(callback);
            unbind.Add(() => field.UnregisterValueChangedCallback(callback));
        }

        static void Release(List<Action> callbacks)
        {
            foreach (var callback in callbacks) callback();
            callbacks.Clear();
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            Release(unbind);
            Release(rosterUnbind);
            Release(slotUnbind);
            portraits.Dispose();
        }
    }
}
