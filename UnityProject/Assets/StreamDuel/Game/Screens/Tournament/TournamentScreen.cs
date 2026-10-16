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
    public sealed class TournamentScreenArgs
    {
        public string StorageDirectory;
        public ContentDb Content;
        public ITeamPresetStore Presets;
        public Action<string> WriteClipboard;
    }

    public sealed class TournamentEntryDraft
    {
        public string Name;
        public int Control = 1;
        public bool UseLocalProfile;
        public TeamPreset Team;
    }

    public sealed class TournamentScreenState
    {
        public TournamentSession Session;
        public ContentDb Content;
        public readonly List<TournamentEntryDraft> Entries = new List<TournamentEntryDraft>();
        public int Format, Count = 2, Seeding, SelectedPairing = -1;
        public string Seed = "1", Notice, Report;
        public bool Creating = true, AutoTiebreak;
        public TournamentScreenArgs Options;
        public Battle PendingResult;
        public int PendingResultPairing;
    }

    public sealed partial class TournamentScreen : IScreen
    {
        readonly BattleScreen legacy;
        readonly TournamentScreenState state;
        readonly List<Action> unbind = new List<Action>();
        ScreenRouter router;
        VisualElement root, body;
        Label message;
        TournamentScreenArgs options;
        Battle automaticBattle;
        int automaticPairing;
        float elapsed;
        readonly List<string> savePaths = new List<string>();
        DropdownField saved;
        public string Id => ScreenIds.Tournament;
        public TournamentSession CurrentSession => state.Session;
        public TournamentScreenState State => state;
        public bool IsAutomaticRunning => automaticBattle != null;

        public TournamentScreen(BattleScreen battleScreen, TournamentScreenState screenState)
        {
            legacy = battleScreen ?? throw new ArgumentNullException(nameof(battleScreen));
            state = screenState ?? throw new ArgumentNullException(nameof(screenState));
        }

        public VisualElement Build(ScreenRouter owner, object args)
        {
            router = owner;
            options = args as TournamentScreenArgs ?? state.Options ?? new TournamentScreenArgs();
            if (options.StorageDirectory == null && Array.IndexOf(Environment.GetCommandLineArgs(), "--t133-smoke-test") >= 0)
                options.StorageDirectory = Path.Combine(T133TournamentSmoke.ReadOutputDirectory(), "menu-storage");
            state.Options = options;
            if (options.Content != null) SetDraftContent(options.Content);
            else if (state.Content == null) SetDraftContent(legacy.MenuPack);
            root = new VisualElement { name = "tournament-screen", userData = this };
            root.AddToClassList("sd-tournament");
            var style = Resources.Load<StyleSheet>("T133Tournament");
            if (style == null) throw new InvalidOperationException("缺少赛事界面样式。");
            root.styleSheets.Add(style);
            Render();
            return root;
        }

        string StorageDirectory => Path.GetFullPath(options.StorageDirectory ?? Application.persistentDataPath);
        string TournamentDirectory => Path.Combine(StorageDirectory, "tournaments");

        void Render()
        {
            foreach (var action in unbind) action();
            unbind.Clear();
            root.Clear();
            var header = Row(root, "tournament-header");
            Label(header, "tournament-title", "本地赛事", "title");
            Label(header, "tournament-pack", (state.Creating ? state.Content : state.Session?.Db)?.PackName ?? "无可用内容包", "muted");
            Button(header, "tournament-back", "返回主菜单", () => router.Show(ScreenIds.Menu));
            var toolbar = Row(root, "tournament-toolbar");
            Button(toolbar, "tournament-new", "新建赛事", () =>
            {
                state.Creating = true; state.Report = null; state.Notice = null;
                SetDraftContent(options.Content ?? legacy.MenuPack);
                Render();
            });
            saved = new DropdownField("继续存档") { name = "tournament-saves" };
            saved.AddToClassList("save-picker");
            toolbar.Add(saved);
            RefreshSaves();
            Button(toolbar, "tournament-load", "载入", LoadSelected);
            if (state.Session != null)
                Button(toolbar, "tournament-current", "当前赛事", () => { state.Creating = false; state.Content = state.Session.Db; state.Report = null; Render(); });
            if (state.PendingResult != null)
            {
                toolbar.Q<Button>("tournament-new").SetEnabled(false);
                toolbar.Q<Button>("tournament-load").SetEnabled(false);
            }
            message = Label(root, "tournament-status", state.Notice ?? "", "status");
            body = new VisualElement { name = "tournament-body" };
            body.AddToClassList("body"); root.Add(body);
            if (state.Creating) BuildCreate();
            else if (state.Report != null) BuildReport();
            else BuildPairings();
        }

        void RefreshSaves()
        {
            savePaths.Clear();
            var labels = new List<string>();
            try
            {
                if (Directory.Exists(TournamentDirectory))
                    foreach (string path in Directory.GetFiles(TournamentDirectory, "*.json").OrderByDescending(File.GetLastWriteTimeUtc))
                    {
                        savePaths.Add(path);
                        labels.Add(Path.GetFileNameWithoutExtension(path));
                    }
            }
            catch (Exception ex) { state.Notice = "无法列出赛事存档：" + ex.Message; }
            saved.choices = labels;
            saved.index = labels.Count > 0 ? 0 : -1;
            saved.SetEnabled(labels.Count > 0);
        }

        void SetDraftContent(ContentDb content)
        {
            if (content == null) return;
            if (state.Content != null && state.Content.PackId != content.PackId)
            {
                state.Entries.Clear(); state.Creating = true; state.Report = null;
            }
            state.Content = content;
        }

        void LoadSelected()
        {
            if (saved.index < 0 || saved.index >= savePaths.Count) throw new InvalidOperationException("尚无赛事存档。请先创建赛事。");
            if (state.Content == null) throw new InvalidOperationException("没有可用内容包。");
            var loaded = TournamentSession.Load(state.Content, savePaths[saved.index]);
            state.Session = loaded; state.Creating = false; state.Report = null; state.SelectedPairing = -1;
            state.PendingResult = null;
            state.Notice = "已载入赛事。未完成的对局可从开局重新进行。";
            Render();
        }

        void ShowReport()
        {
            state.Report = state.Session.BuildReport().ToText();
            Render();
        }

        void BuildReport()
        {
            Label(body, "tournament-report-title", state.Session.Tournament.IsFinished ? "赛事结束 · 战报" : "赛事战报", "subtitle");
            var controls = Row(body, "report-actions");
            Button(controls, "tournament-copy-report", "复制战报", () =>
            {
                if (options.WriteClipboard != null) options.WriteClipboard(state.Report);
                else GUIUtility.systemCopyBuffer = state.Report;
                Message("战报已复制。");
            });
            Button(controls, "tournament-report-back", "返回对阵", () => { state.Report = null; Render(); });
            var scroll = Scroll(body, "tournament-report-scroll");
            Label(scroll, "tournament-report-text", state.Report, "report-text");
        }

        void PlayPairing(bool automatic)
        {
            var session = state.Session;
            var pairing = session.Tournament.Pairings.First(item => item.Id == state.SelectedPairing);
            var levels = new[] { session.Controllers[pairing.Player0], session.Controllers[pairing.Player1] };
            if (automatic && levels.Any(level => !level.HasValue)) throw new InvalidOperationException("有玩家参赛，请选择进入对战。");
            Battle battle = session.CreateBattle(pairing.Id);
            if (automatic)
            {
                automaticBattle = battle; automaticPairing = pairing.Id; elapsed = 0;
                Message("电脑正在对战；离开此页只取消本局，已保存的赛程不会丢失。");
                body.SetEnabled(false);
                root.Q("tournament-toolbar").SetEnabled(false);
                return;
            }
            var activeRouter = router;
            int pairingId = pairing.Id;
            legacy.StartTournamentBattle(battle, levels, completed =>
            {
                state.PendingResult = completed;
                state.PendingResultPairing = pairingId;
                try
                {
                    SavePendingResult();
                }
                catch (Exception ex) { state.Notice = "登记失败，已保留本局，请修复存储后重试登记：" + ex.Message; }
                state.Report = null;
                activeRouter.Show(ScreenIds.Tournament, options);
            }, () =>
            {
                state.Notice = "已返回赛事，本场尚未登记结果。";
                activeRouter.Show(ScreenIds.Tournament, options);
            });
            activeRouter.Show(ScreenIds.Legacy);
        }

        public void Tick(float dt)
        {
            if (automaticBattle == null) return;
            elapsed += dt;
            if (elapsed < 0.01f) return;
            elapsed = 0;
            try
            {
                var session = state.Session;
                var pairing = session.Tournament.Pairings.First(item => item.Id == automaticPairing);
                var levels = new[] { session.Controllers[pairing.Player0].Value, session.Controllers[pairing.Player1].Value };
                if (automaticBattle.Phase == Phase.Choose)
                    automaticBattle.ResolveTurn(Ai.Choose(automaticBattle, 0, levels[0]), Ai.Choose(automaticBattle, 1, levels[1]));
                else if (automaticBattle.Phase == Phase.Replace)
                {
                    for (int side = 0; side < 2 && automaticBattle.Phase == Phase.Replace; side++)
                        if (automaticBattle.Need[side]) automaticBattle.Replace(side, Ai.ChooseReplacement(automaticBattle, side, levels[side]));
                }
                else if (automaticBattle.Phase != Phase.Over) throw new InvalidOperationException("当前赛事阶段无法自动推进。");
                if (automaticBattle.Phase == Phase.Over)
                {
                    state.PendingResult = automaticBattle;
                    state.PendingResultPairing = automaticPairing;
                    automaticBattle = null;
                    SavePendingResult();
                    Render();
                }
                else Message("电脑对战 · 第 " + automaticBattle.Turn + " 回合");
            }
            catch (Exception ex)
            {
                automaticBattle = null;
                state.Notice = state.PendingResult == null ? "自动对战停止，本场仍待赛：" + ex.Message
                    : "登记失败，已保留本局，请修复存储后重试登记：" + ex.Message;
                Render();
            }
        }

        void SavePendingResult()
        {
            if (state.PendingResult == null) throw new InvalidOperationException("没有等待登记的结束对局。");
            var tournament = state.Session.Tournament;
            if (tournament.IsElimination && !tournament.AutoTiebreak && state.PendingResult.Winner == -1)
            {
                // Core 不接受淘汰赛平局录像；保留待赛场次，让玩家重赛或录入明确胜负。
                state.PendingResult = null;
                state.Notice = "本局平局，淘汰赛需要明确胜负。本场保持待赛，请重赛或录入胜方。";
                return;
            }
            state.Session.CompleteBattle(state.PendingResultPairing, state.PendingResult);
            state.PendingResult = null;
            state.Notice = "对局已归档并登记。";
        }

        void Message(string text) { state.Notice = text; if (message != null) message.text = text; }

        Button Button(VisualElement parent, string name, string text, Action callback)
        {
            var button = new Button { name = name, text = text, enableRichText = false };
            button.AddToClassList("button");
            Action safe = () => { try { callback(); } catch (Exception ex) { Message(ex.Message); } };
            button.clicked += safe; unbind.Add(() => button.clicked -= safe);
            parent.Add(button); return button;
        }

        static Label Label(VisualElement parent, string name, string text, string className = null)
        {
            var label = new Label(text) { name = name, enableRichText = false };
            if (className != null) label.AddToClassList(className);
            parent.Add(label); return label;
        }

        static VisualElement Row(VisualElement parent, string name)
        {
            var row = new VisualElement { name = name }; row.AddToClassList("row"); parent.Add(row); return row;
        }

        static ScrollView Scroll(VisualElement parent, string name, bool horizontal = false)
        {
            var scroll = new ScrollView(horizontal ? ScrollViewMode.VerticalAndHorizontal : ScrollViewMode.Vertical)
            {
                name = name, horizontalScrollerVisibility = horizontal ? ScrollerVisibility.Auto : ScrollerVisibility.Hidden,
                verticalScrollerVisibility = ScrollerVisibility.Auto
            };
            scroll.AddToClassList("scroll"); parent.Add(scroll); return scroll;
        }

        public void Dispose()
        {
            automaticBattle = null;
            foreach (var action in unbind) action();
            unbind.Clear(); root = null; body = null; message = null;
        }
    }
}
