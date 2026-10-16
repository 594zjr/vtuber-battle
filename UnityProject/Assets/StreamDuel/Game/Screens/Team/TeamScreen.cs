using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using StreamDuel.Core;
using UnityEngine;
using UnityEngine.UIElements;

namespace StreamDuel.Game
{
    public sealed partial class TeamScreen : IScreen
    {
        readonly LocalBattleSession legacy;
        readonly List<Action> unbind = new List<Action>();
        readonly List<Action> rosterUnbind = new List<Action>();
        readonly Dictionary<string, Button> pickButtons = new Dictionary<string, Button>(StringComparer.Ordinal);
        readonly Dictionary<string, TeamPreset> selections = new Dictionary<string, TeamPreset>(StringComparer.Ordinal);
        readonly Dictionary<string, string> seasons = new Dictionary<string, string>(StringComparer.Ordinal);
        readonly Button[] slots = new Button[6];
        readonly List<string> seasonIds = new List<string>();
        readonly List<ContentDb> packs = new List<ContentDb>();
        TeamScreenArgs options;
        ScreenRouter router;
        ContentDb db;
        GameSettings settings;
        TeamPresetList presets;
        ITeamPresetStore store;
        IPortraitProvider portraits;
        IDisposable ownedPortraits;
        readonly List<string> selected = new List<string>();
        string seasonId, detailId;
        bool libraryReady;
        VisualElement root, grid;
        DropdownField packField, seasonField, levelField, presetsField;
        TextField presetName, codeField;
        Label errors, status, storageStatus, notice, detailName, detailText;
        Image detailPortrait;
        Button start, savePreset, loadPreset, copyCode;
        IVisualElementScheduledItem focus;
        public string Id => ScreenIds.Team;

        public TeamScreen(LocalBattleSession session = null) { legacy = session; }

        public VisualElement Build(ScreenRouter screenRouter, object args)
        {
            Dispose();
            router = screenRouter;
            options = args as TeamScreenArgs ?? legacy?.CreateTeamScreenArgs() ?? new TeamScreenArgs();
            settings = GameSettings.FromJson((options.Settings ?? new GameSettings()).ToJson());
            seasonId = options.SeasonId;
            store = options.Presets ?? new MemoryTeamPresetStore();
            if (options.Packs != null)
                packs.AddRange(options.Packs.Where(pack => pack != null && pack.Policy.PackReason(pack.PackId) == null)
                    .GroupBy(pack => pack.PackId, StringComparer.Ordinal).Select(group => group.First()));
            if (options.Content != null && !packs.Any(pack => pack.PackId == options.Content.PackId)
                && options.Content.Policy.PackReason(options.Content.PackId) == null) packs.Add(options.Content);
            db = packs.FirstOrDefault(pack => pack.PackId == options.Content?.PackId)
                ?? packs.FirstOrDefault(pack => pack.PackId == settings.DefaultPack) ?? packs.FirstOrDefault();
            portraits = options.Portraits;
            if (portraits == null)
            {
                var provider = new PortraitProvider(router.Resources.RegularFont);
                portraits = provider; ownedPortraits = provider;
            }
            root = new VisualElement { name = "team-screen", userData = this };
            root.AddToClassList("sd-team");
            var style = Resources.Load<StyleSheet>("T66Team");
            if (style == null) throw new InvalidOperationException("缺少组队页样式 T66Team。");
            root.styleSheets.Add(style);
            BuildHeader(args); BuildSelectors(); BuildRulesets(); BuildSlots(); BuildBody(); BuildLibrary(); BuildFooter();
            if (db != null)
            {
                var initial = options.Selection != null && options.Selection.PackId == db.PackId
                    ? options.Selection : options.LoadSelection?.Invoke(db);
                SetSelection(initial ?? new TeamPreset { Name = "我的队伍", PackId = db.PackId });
                PopulatePack();
            }
            else
            {
                detailName.text = "没有可用内容包";
                detailText.text = "请检查内容包和下架策略；返回主菜单后可查看加载错误。";
            }
            if (!string.IsNullOrEmpty(options.Notice)) status.text = options.Notice;
            ReloadPresets(); Refresh();
            focus = root.schedule.Execute(() => { if (root?.panel != null) packField.Focus(); });
            return root;
        }

        void BuildHeader(object args)
        {
            var row = Element(root, "team-header", "sd-team__header");
            var title = Label(row, "team-title", "组队 · 准备开播", "sd-team__title");
            title.style.unityFont = router.Resources.BoldFont;
            Label(row, "team-caption", "选好六位主播，查看技能，再迎接对手。", "sd-team__muted");
            var navigation = args as ScreenArgs;
            Button(row, "back-button", "返回主菜单", () =>
            {
                RememberView();
                if (options.Return != null) options.Return();
                else router.Show(navigation?.ReturnScreenId ?? ScreenIds.Menu, navigation?.ReturnArgs);
            });
        }

        void BuildSelectors()
        {
            var row = Element(root, "team-selectors", "sd-team__row");
            packField = Dropdown(row, "team-pack", "内容包", UniqueChoiceLabels(packs.Select(pack => (pack.PackName ?? pack.PackId) + "〔" + pack.PackId + "〕")));
            packField.style.flexGrow = 2;
            seasonField = Dropdown(row, "team-season", "赛季", new List<string> { "不限赛季" });
            levelField = Dropdown(row, "team-level", "电脑", new List<string> { "简单", "普通", "困难", "专家" });
            levelField.index = Mathf.Clamp((int)settings.AiLevel, 0, 3);
            Bind(packField, () => { if (packField.index >= 0 && packField.index < packs.Count) SwitchPack(packs[packField.index]); });
            Bind(seasonField, () =>
            {
                seasonId = seasonField.index >= 0 && seasonField.index < seasonIds.Count ? seasonIds[seasonField.index] : null;
                RememberView(); Refresh();
            });
            Bind(levelField, () => { settings.AiLevel = (AiLevel)levelField.index; RememberView(); Message("电脑难度已设为" + levelField.value + "。"); });
        }

        void BuildSlots()
        {
            var row = Element(root, "team-slots", "sd-team__slots");
            for (int i = 0; i < slots.Length; i++)
            {
                int slot = i;
                slots[i] = Button(row, "team-slot-" + i, "", () =>
                {
                    if (slot >= selected.Count) return;
                    string id = selected[slot]; selected.RemoveAt(slot);
                    ShowDetail(id); Remember(); Refresh();
                }, "sd-team__slot");
                slots[i].tooltip = "点击移出队伍";
            }
        }

        void BuildBody()
        {
            var body = Element(root, "team-body", "sd-team__body");
            var roster = new ScrollView(ScrollViewMode.Vertical) { name = "team-roster", horizontalScrollerVisibility = ScrollerVisibility.Hidden };
            roster.AddToClassList("sd-team__roster"); body.Add(roster);
            grid = Element(roster, "team-grid", "sd-team__grid");
            var detail = new ScrollView(ScrollViewMode.Vertical) { name = "team-detail", horizontalScrollerVisibility = ScrollerVisibility.Hidden };
            detail.AddToClassList("sd-team__detail"); body.Add(detail);
            var heading = Element(detail, "team-detail-heading", "sd-team__detail-heading");
            detailPortrait = new Image { name = "team-detail-portrait", scaleMode = ScaleMode.ScaleToFit };
            detailPortrait.AddToClassList("sd-team__detail-portrait"); heading.Add(detailPortrait);
            detailName = Label(heading, "team-detail-name", "主播详情", "sd-team__detail-name");
            detailName.style.unityFont = router.Resources.BoldFont;
            detailText = Label(detail, "team-detail-text", "选择主播卡查看能力、人设和全部技能。", "sd-team__detail-text");
        }

        void BuildLibrary()
        {
            var library = Element(root, "team-library", "sd-team__library");
            var row = Element(library, "team-presets-row", "sd-team__row");
            presetName = Text(row, "team-preset-name", "队伍名", "我的队伍");
            savePreset = Button(row, "team-preset-save", "保存预设", SavePreset);
            presetsField = Dropdown(row, "team-presets", "已保存", new List<string>());
            loadPreset = Button(row, "team-preset-load", "读取预设", LoadPreset);
            Button(row, "team-presets-refresh", "刷新", ReloadPresets);
            var share = Element(library, "team-code-row", "sd-team__row");
            codeField = Text(share, "team-code", "队伍码", ""); codeField.style.flexGrow = 3;
            copyCode = Button(share, "team-code-copy", "复制队伍码", CopyCode);
            Button(share, "team-code-paste", "粘贴并读取", PasteCode);
            Button(share, "team-code-open", "读取输入码", () => ImportPreset(TeamCode.Decode(codeField.value)));
            var storage = new ScrollView(ScrollViewMode.Vertical) { name = "team-storage-feedback", horizontalScrollerVisibility = ScrollerVisibility.Hidden };
            storage.AddToClassList("sd-team__storage-feedback"); library.Add(storage);
            storageStatus = Label(storage, "team-storage-status", "", "sd-team__error");
        }

        void BuildFooter()
        {
            var row = Element(root, "team-actions", "sd-team__row");
            Button(row, "team-auto", "自动组队", () =>
            {
                RequireContent();
                var next = TeamBuilder.Suggest(db, seasonId, Array.Empty<string>(), options.Seed);
                selected.Clear(); selected.AddRange(next);
                Remember(); Refresh(); Message("已按当前赛季组成六人队伍。");
            });
            Button(row, "team-clear", "清空队伍", () => { selected.Clear(); Remember(); Refresh(); Message("队伍已清空。"); });
            start = Button(row, "team-start", "开始对战（对电脑）", StartBattle, "sd-team__primary");
            var feedback = new ScrollView(ScrollViewMode.Vertical) { name = "team-feedback", horizontalScrollerVisibility = ScrollerVisibility.Hidden };
            feedback.AddToClassList("sd-team__feedback"); row.Add(feedback);
            errors = Label(feedback, "team-errors", "", "sd-team__error");
            status = Label(feedback, "team-status", "", "sd-team__status");
            var noticeScroll = new ScrollView(ScrollViewMode.Vertical) { name = "team-notice-scroll", horizontalScrollerVisibility = ScrollerVisibility.Hidden };
            noticeScroll.AddToClassList("sd-team__notice"); root.Add(noticeScroll);
            notice = Label(noticeScroll, "team-pack-notice", "", "sd-team__notice-text");
        }

        void PopulatePack()
        {
            foreach (var remove in rosterUnbind) remove(); rosterUnbind.Clear();
            pickButtons.Clear(); grid.Clear();
            packField.SetValueWithoutNotify(packField.choices[packs.IndexOf(db)]);
            seasonIds.Clear(); seasonIds.Add(null);
            var choices = new List<string> { "不限赛季" };
            foreach (var season in db.Seasons.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                seasonIds.Add(season.Key); choices.Add(season.Value.Name + "〔" + season.Key + "〕");
            }
            choices = UniqueChoiceLabels(choices);
            seasonField.choices = choices;
            if (!seasonIds.Contains(seasonId)) seasonId = null;
            seasonField.SetValueWithoutNotify(choices[Math.Max(0, seasonIds.IndexOf(seasonId))]);
            notice.text = string.IsNullOrWhiteSpace(db.PackNotice) ? "非官方同人作品 · 免费 · 非商业" : db.PackNotice;
            foreach (var streamer in db.Streamers)
            {
                string id = streamer.Id;
                var tile = Element(grid, "team-tile-" + id, "sd-team__tile");
                var card = Button(tile, "streamer-card-" + id, "", () => ShowDetail(id), "sd-team__card", rosterUnbind);
                var portrait = new Image { name = "portrait-" + id, image = portraits.Get(db, id), scaleMode = ScaleMode.ScaleToFit };
                portrait.AddToClassList("sd-team__portrait"); card.Add(portrait);
                Label(card, "streamer-name-" + id, streamer.Name, "sd-team__card-name");
                var tracks = Element(card, "streamer-tracks-" + id, "sd-team__tracks");
                foreach (string type in new[] { streamer.Type, streamer.Type2 }.Where(value => value != null))
                {
                    var definition = db.Types[type];
                    var chip = Label(tracks, "streamer-track-" + id + "-" + type,
                        definition.Symbol + " " + definition.Name, "sd-team__track");
                    if (ColorUtility.TryParseHtmlString(settings.ColorblindMode ? definition.ColorAlt : definition.Color, out Color color)) chip.style.backgroundColor = color;
                }
                Label(card, "streamer-persona-" + id, db.Personas[streamer.Persona].Name, "sd-team__muted");
                var statOrder = new[] { "hp", "atk", "def", "spa", "sdef", "spd" };
                var statRows = Enumerable.Range(0, 2).Select(row => string.Join("  ", statOrder.Skip(row * 3).Take(3)
                    .Select(stat => db.StatName(stat) + " " + streamer.Stats[stat].ToString(CultureInfo.InvariantCulture))));
                Label(card, "streamer-stats-" + id, string.Join("\n", statRows), "sd-team__card-stats");
                Label(card, "streamer-blurb-" + id, streamer.Blurb ?? "", "sd-team__card-blurb");
                pickButtons[id] = Button(tile, "team-pick-" + id, "选入队伍", () => Toggle(id), "sd-team__pick", rosterUnbind);
            }
            ShowDetail(db.Streamers.Any(streamer => streamer.Id == detailId) ? detailId : db.Streamers.FirstOrDefault()?.Id);
        }

        void ShowDetail(string id)
        {
            if (db == null || id == null || !db.Streamers.Any(streamer => streamer.Id == id)) return;
            var card = StreamerCard.Build(db, id);
            detailId = id; detailPortrait.image = portraits.Get(db, id);
            detailName.text = card.Name + "\n" + string.Join(" / ", card.Tracks.Select(track => track.Symbol + " " + track.Name));
            var text = new StringBuilder();
            foreach (var stat in card.Stats) text.Append(stat.Name).Append("：").Append(stat.Value.ToString(CultureInfo.InvariantCulture)).Append("  ");
            text.Append("\n\n人设：").Append(card.PersonaName).Append('\n').Append(card.PersonaText).Append("\n\n全部技能\n");
            for (int i = 0; i < card.Skills.Count; i++)
            {
                var skill = card.Skills[i];
                text.Append('\n').Append(i + 1).Append(". ").Append(skill.Name).Append(skill.IsFifth ? "〔必杀〕" : "").Append('\n');
                text.Append(skill.TypeSymbol).Append(' ').Append(skill.TypeName).Append(" · ").Append(skill.CategoryName).Append('\n');
                text.Append("威力 ").Append(skill.Power).Append(" · 命中 ").Append(skill.Accuracy).Append("% · PP ").Append(skill.Pp)
                    .Append(" · 先制 ").Append(skill.Priority.ToString("+0;-0;0", CultureInfo.InvariantCulture)).Append('\n');
                if (skill.Effects.Count == 0) text.Append("无附加效果\n");
                foreach (var effect in skill.Effects)
                    text.Append('〔').Append(effect.At).Append('〕').Append(effect.Text).Append('〔')
                        .Append(string.IsNullOrEmpty(effect.Dispel) ? "消除：参见下方能力与换人规则" : effect.Dispel).Append("〕\n");
            }
            foreach (var rule in Glossary.Build(db).Where(entry => entry.Category == "rule" && (entry.Id == "stages" || entry.Id == "switch")))
                text.Append('\n').Append(rule.Title).Append('\n').Append(rule.Text).Append('\n');
            detailText.text = text.ToString();
        }

        void Refresh()
        {
            RefreshRulesetHelp();
            var issues = db == null ? new List<string> { "没有可用内容包。" } : CurrentPreset().Check(db, seasonId);
            errors.text = issues.Count == 0 ? "六人队伍符合当前赛季规则。" : string.Join("\n", issues);
            start.SetEnabled(issues.Count == 0 && options.StartBattle != null);
            copyCode.SetEnabled(issues.Count == 0); savePreset.SetEnabled(issues.Count == 0 && libraryReady);
            loadPreset.SetEnabled(libraryReady && presets != null && presets.Items.Count > 0);
            packField.SetEnabled(packs.Count > 0); seasonField.SetEnabled(db != null);
            for (int i = 0; i < slots.Length; i++)
            {
                string id = i < selected.Count ? selected[i] : null;
                slots[i].userData = id;
                string name = db?.Streamers.FirstOrDefault(streamer => streamer.Id == id)?.Name ?? id;
                slots[i].text = (i + 1) + "  " + (id == null ? "空位" : name);
                slots[i].EnableInClassList("sd-team__slot--filled", id != null);
            }
            foreach (var item in pickButtons)
            {
                bool picked = selected.Contains(item.Key);
                item.Value.text = picked ? "移出队伍" : "选入队伍";
                item.Value.EnableInClassList("sd-team__pick--selected", picked);
            }
        }

        static List<string> UniqueChoiceLabels(IEnumerable<string> labels)
        {
            // 下拉框按显示值定位；合法名称与编号中的分隔符也可能组合成同一标签。
            var originals = labels.ToList();
            var reserved = new HashSet<string>(originals, StringComparer.Ordinal);
            var used = new HashSet<string>(StringComparer.Ordinal);
            var result = new List<string>(originals.Count);
            foreach (string label in originals)
            {
                string choice = label;
                int suffix = 2;
                if (used.Contains(choice))
                {
                    do { choice = label + "（" + suffix.ToString(CultureInfo.InvariantCulture) + "）"; suffix++; }
                    while (reserved.Contains(choice) || used.Contains(choice));
                }
                used.Add(choice);
                result.Add(choice);
            }
            return result;
        }

        static VisualElement Element(VisualElement parent, string name, string style)
        {
            var element = new VisualElement { name = name }; element.AddToClassList(style); parent.Add(element); return element;
        }
        static Label Label(VisualElement parent, string name, string text, string style)
        {
            var label = new Label(text) { name = name, enableRichText = false }; label.AddToClassList(style); parent.Add(label); return label;
        }
        Button Button(VisualElement parent, string name, string text, Action action, string style = "sd-team__button", List<Action> bindings = null)
        {
            var button = new Button { name = name, text = text, enableRichText = false, focusable = true };
            button.AddToClassList(style); Action callback = () => Attempt(action);
            button.clicked += callback; (bindings ?? unbind).Add(() => button.clicked -= callback);
            parent.Add(button); return button;
        }
        static DropdownField Dropdown(VisualElement parent, string name, string label, List<string> choices)
        {
            var field = new DropdownField(label, choices, choices.Count > 0 ? 0 : -1) { name = name };
            field.AddToClassList("sd-team__field"); field.Query<TextElement>().ForEach(text => text.enableRichText = false);
            parent.Add(field); return field;
        }
        static TextField Text(VisualElement parent, string name, string label, string value)
        {
            var field = new TextField(label) { name = name, value = value };
            field.AddToClassList("sd-team__field"); field.Query<TextElement>().ForEach(text => text.enableRichText = false);
            parent.Add(field); return field;
        }
        void Bind(DropdownField field, Action action)
        {
            EventCallback<ChangeEvent<string>> callback = evt => Attempt(action);
            field.RegisterValueChangedCallback(callback); unbind.Add(() => field.UnregisterValueChangedCallback(callback));
        }
        void Attempt(Action action)
        {
            if (root == null) return;
            try { action(); } catch (Exception ex) { if (status != null) status.text = "操作失败：" + ex.Message; }
        }
        void Message(string text) { if (status != null) status.text = text; }
        public void Tick(float dt) { }
        public void Dispose()
        {
            focus?.Pause(); focus = null;
            foreach (var remove in rosterUnbind) remove(); rosterUnbind.Clear();
            foreach (var remove in unbind) remove(); unbind.Clear();
            ownedPortraits?.Dispose(); ownedPortraits = null; portraits = null;
            root = null; router = null; options = null; db = null; selected.Clear(); packs.Clear(); selections.Clear(); pickButtons.Clear();
            seasons.Clear();
            seasonId = detailId = null; presets = null; libraryReady = false;
        }
    }
}
