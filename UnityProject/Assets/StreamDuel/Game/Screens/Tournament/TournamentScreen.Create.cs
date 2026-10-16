using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using StreamDuel.Core;
using UnityEngine.UIElements;

namespace StreamDuel.Game
{
    public sealed partial class TournamentScreen
    {
        static readonly string[] Formats = { "roundrobin", "swiss", "single", "double" };

        void BuildCreate()
        {
            Label(body, "tournament-create-title", "创建赛事", "subtitle");
            Label(body, "tournament-create-help", "选择每位选手的队伍；队伍预设与组队页共用。赛事结果不改变个人排位分。", "muted");
            var settings = Row(body, "tournament-create-settings");
            var format = Dropdown(settings, "tournament-format", "赛制", new[] { "循环赛", "瑞士轮", "单败淘汰", "双败淘汰" }, state.Format);
            format.RegisterValueChangedCallback(evt =>
            {
                state.Format = format.index;
                if (state.Format < 2) { if (state.Seeding == 1) state.Seeding = 0; state.AutoTiebreak = false; }
                Render();
            });
            var count = new IntegerField("人数（2～16）") { name = "tournament-count", value = state.Count };
            count.AddToClassList("field"); settings.Add(count);
            count.RegisterValueChangedCallback(evt =>
            {
                state.Count = evt.newValue;
                if (state.Count >= 2 && state.Count <= 16) Render();
                else Message("参赛人数必须为 2～16。");
            });
            var seeding = Dropdown(settings, "tournament-seeding", "种子方式", new[] { "报名顺序", "随机种子", "按排位分" }, state.Seeding);
            seeding.RegisterValueChangedCallback(evt =>
            {
                if (seeding.index == 1 && state.Format < 2)
                {
                    seeding.SetValueWithoutNotify(seeding.choices[state.Seeding]);
                    Message("循环赛和瑞士轮支持报名顺序或排位分；随机种子仅用于淘汰赛。");
                    return;
                }
                state.Seeding = seeding.index; Render();
            });
            var seed = new TextField("随机种子数值") { name = "tournament-seed", value = state.Seed, maxLength = 10 };
            seed.AddToClassList("field"); settings.Add(seed);
            seed.SetEnabled(state.Seeding == 1);
            seed.RegisterValueChangedCallback(evt => state.Seed = evt.newValue);
            var tie = new Toggle("整场平局时自动加赛（关闭后可手工报分）") { name = "tournament-tiebreak", value = state.AutoTiebreak };
            tie.AddToClassList("tiebreak"); body.Add(tie); tie.SetEnabled(state.Format >= 2);
            tie.RegisterValueChangedCallback(evt => state.AutoTiebreak = evt.newValue);
            Label(body, "tournament-seed-help", "随机种子仅用于淘汰赛。排位种子读取当前季分数；未选择档案的玩家使用初始分，电脑采用对应难度的参考分。", "muted");
            var entries = Scroll(body, "tournament-entries");
            while (state.Entries.Count < Math.Min(16, Math.Max(2, state.Count)))
                state.Entries.Add(new TournamentEntryDraft { Name = "选手 " + (state.Entries.Count + 1) });
            for (int i = 0; i < Math.Min(16, Math.Max(2, state.Count)); i++) BuildEntry(entries, i);
            var footer = Row(body, "tournament-create-footer");
            var create = Button(footer, "tournament-create", "创建并排第一轮", CreateTournament);
            create.AddToClassList("primary");
            create.SetEnabled(state.Content != null && state.Count >= 2 && state.Count <= 16);
        }

        void BuildEntry(VisualElement parent, int index)
        {
            var entry = state.Entries[index];
            string prefix = "tournament-player-" + index;
            var card = new VisualElement { name = prefix }; card.AddToClassList("entry"); parent.Add(card);
            var row = Row(card, prefix + "-row");
            Label(row, prefix + "-number", (index + 1).ToString(CultureInfo.InvariantCulture), "entry-number");
            var name = new TextField("姓名") { name = prefix + "-name", value = entry.Name, maxLength = 64 };
            name.AddToClassList("field"); row.Add(name);
            name.RegisterValueChangedCallback(evt => entry.Name = evt.newValue);
            var control = Dropdown(row, prefix + "-control", "操作", new[] { "玩家", "电脑·普通", "电脑·困难", "电脑·专家" }, entry.Control);
            control.RegisterValueChangedCallback(evt => entry.Control = control.index);
            Button(row, prefix + "-team", entry.Team == null ? "选择队伍" : "更换队伍", () => SelectTeam(index));
            var profile = new Toggle("采用本机当前排位") { name = prefix + "-profile", value = entry.UseLocalProfile };
            row.Add(profile); profile.SetEnabled(state.Seeding == 2);
            profile.RegisterValueChangedCallback(evt => entry.UseLocalProfile = evt.newValue);
            string text = entry.Team == null ? "尚未选择六人队伍" : string.Join(" · ", entry.Team.Ids.Select(id => state.Content.Streamers.FirstOrDefault(item => item.Id == id)?.Name ?? id));
            Label(card, prefix + "-summary", text, "muted");
        }

        void SelectTeam(int index)
        {
            if (state.Content == null) throw new InvalidOperationException("没有可用内容包。");
            var target = state.Entries[index];
            var activeRouter = router;
            var returnOptions = options;
            activeRouter.Show(ScreenIds.Team, new TeamScreenArgs
            {
                Content = state.Content, Packs = new[] { state.Content }, Policy = state.Content.Policy,
                Selection = target.Team, SelectionOnly = true, Settings = new GameSettings(),
                Presets = options.Presets ?? new FileTeamPresetStore(StorageDirectory),
                Notice = "为「" + target.Name + "」选择队伍；可读取已保存的预设。确认后返回赛事。",
                Return = () => activeRouter.Show(ScreenIds.Tournament, returnOptions),
                StartBattle = request =>
                {
                    var errors = request.Team.Check(state.Content, null);
                    if (errors.Count > 0) throw new ArgumentException(string.Join("；", errors));
                    target.Team = new TeamPreset { Name = request.Team.Name, PackId = request.Team.PackId, Ids = new List<string>(request.Team.Ids) };
                    state.Notice = "已为「" + target.Name + "」选择队伍。";
                    activeRouter.Show(ScreenIds.Tournament, returnOptions);
                }
            });
        }

        void CreateTournament()
        {
            if (state.Count < 2 || state.Count > 16) throw new ArgumentException("参赛人数必须为 2～16。");
            if (state.Content == null) throw new InvalidOperationException("没有可用内容包。");
            var entrants = new List<TournamentEntrant>();
            for (int i = 0; i < state.Count; i++)
            {
                var entry = state.Entries[i];
                if (entry.Team == null) throw new ArgumentException("请先为第 " + (i + 1) + " 位选手选择队伍。");
                var errors = entry.Team.Check(state.Content, null);
                if (errors.Count > 0) throw new ArgumentException(string.Join("；", errors));
                entrants.Add(new TournamentEntrant
                {
                    Name = entry.Name, TeamCode = TeamCode.Encode(entry.Team),
                    ComputerLevel = entry.Control == 0 ? (AiLevel?)null : (AiLevel)entry.Control,
                    Profile = state.Seeding == 2 && entry.UseLocalProfile ? FileSaveStore.LoadProfileSnapshot(StorageDirectory, true) : null
                });
            }
            var setup = new TournamentOptions { AutoTiebreak = state.AutoTiebreak };
            if (state.Seeding == 1)
            {
                if (state.Format < 2) throw new ArgumentException("随机种子仅用于淘汰赛。");
                if (!uint.TryParse(state.Seed, NumberStyles.None, CultureInfo.InvariantCulture, out uint value))
                    throw new ArgumentException("随机种子必须为 0～4294967295 的整数。");
                setup.Seed = value;
            }
            else if (state.Seeding == 2) setup.SeedMode = TournamentSeedMode.ByRating;
            string path = Path.Combine(TournamentDirectory, "赛事-" + Formats[state.Format] + "-" + Guid.NewGuid().ToString("N") + ".json");
            state.Session = TournamentSession.Create(state.Content, path, Formats[state.Format], entrants,
                options: state.Format < 2 && state.Seeding != 2 ? null : setup);
            state.PendingResult = null;
            state.Session.NextRound();
            state.Creating = false; state.Report = null; state.SelectedPairing = -1;
            state.Notice = "赛事已保存。选择一场开始对战，或录入已完成的结果。";
            Render();
        }

        static DropdownField Dropdown(VisualElement parent, string name, string title, IEnumerable<string> choices, int index)
        {
            var result = new DropdownField(title, choices.ToList(), index) { name = name };
            result.AddToClassList("field"); parent.Add(result); return result;
        }
    }
}
