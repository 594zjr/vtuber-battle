// 第一版画面：用 Unity 自带的即时模式 GUI 画出组队页和对战页，不需要任何场景、预制体或美术资源。
// 规则全部在 StreamDuel.Core 里，这里只负责显示和接收点击。以后换正式美术时只换这一层。
using System.Collections.Generic;
using System.IO;
using System.Linq;
using StreamDuel.Core;
using UnityEngine;

namespace StreamDuel.Game
{
    public sealed partial class BattleScreen : MonoBehaviour
    {
        public System.Action MainMenuRequested;
        public System.Action<Battle> ResultRequested;

        public void RematchFromResult() { StartBattle(lastTeams); }
        public void ReturnToTeamFromResult() { view = View.Team; b = null; }
        public ContentDb FindLoadedPack(string packId) { return packs.FirstOrDefault(pack => pack.PackId == packId); }
        const float RefW = 1600f, RefH = 900f;
        enum View { Team, Battle }

        ContentDb db;
        Battle b;
        View view = View.Team;
        List<string> pick = new List<string>();
        List<string>[] lastTeams;

        int shown;
        bool playing;
        float nextLineAt;
        bool fast, timerOn;
        float timerLeft = 10f;

        // 详情面板当前显示什么
        string detailKind; string detailId; int detailSide, detailIdx;
        string teamDetail;

        // 点击产生的状态变化推迟到本次 OnGUI 结束再执行，避免同一帧里自动布局前后不一致
        System.Action pending;
        void Do(System.Action a) { pending = a; }

        Vector2 rosterScroll, logScroll, detailScroll, teamDetailScroll, packScroll, packErrorScroll, actionScroll;
        int logCountDrawn;

        // ---------- 颜色与样式 ----------
        static readonly Color Bg = Hex("#0c1124"), Panel = Hex("#151c38"), Panel2 = Hex("#1c2549"), Line = Hex("#2b3666");
        static readonly Color Fg = Hex("#e8ebfa"), Muted = Hex("#98a1c8"), Accent = Hex("#ffb547"), Mine = Hex("#5cc6ff"), Foe = Hex("#ff6b7f");
        static readonly Color Ok = Hex("#6fdc8c"), Ctrl = Hex("#ff8a5c"), Weak = Hex("#c58bff");

        Font font;
        Texture2D white, circle;
        GUIStyle sText, sSmall, sTitle, sRosterTitle, sBig, sHuge, sBtn, sChip, sSkillInfo, sPanel, sSkill, sCenter, sPortrait, sPackError;
        bool stylesReady;

        static Color Hex(string h) { Color c; return ColorUtility.TryParseHtmlString(h, out c) ? c : Color.magenta; }
        static string HexOf(Color c) { return "#" + ColorUtility.ToHtmlStringRGB(c); }
        Color TypeColor(string type) { return Hex(db.Types[type].Color); }

        List<ContentDb> packs = new List<ContentDb>();
        List<string> packErrors = new List<string>();

        void OnEnable()
        {
            // Play 中脚本重载不会重跑 Awake，纯 C# 对战数据也不能由 Unity 序列化恢复。
            if (db != null) return;
            packs = new List<ContentDb>();
            packErrors = new List<string>();
            b = null;
            view = View.Team;
            playing = false;
            shown = 0;
            stylesReady = false;
            pending = null;
            Awake();
        }

        void Awake()
        {
            string rulesJson = Resources.Load<TextAsset>("StreamDuelRules").text;
            // Unity 也会把包目录的许可说明导入为 TextAsset，它不是内容包 JSON。
            foreach (var ta in Resources.LoadAll<TextAsset>("Packs").Where(t => t.name != "LICENSE")
                .OrderBy(t => t.name == "original" ? 1 : 0).ThenBy(t => t.name))
            {
                var probe = ContentDb.FromJson(rulesJson, ta.text);
                packs.Add(probe);
            }
            var externalPacks = SmokeRequested ? new List<PackLoadResult>()
                : PackLoader.LoadDirectory(rulesJson, Path.Combine(Application.persistentDataPath, "packs"));
            foreach (var result in externalPacks)
            {
                if (result.Error != null)
                {
                    packErrors.Add(result.FileName + "：" + result.Error);
                    continue;
                }
                if (packs.Any(p => p.PackId == result.Db.PackId))
                {
                    packErrors.Add(result.FileName + "：内容包 id " + result.Db.PackId + " 与内置包重复，已优先使用内置包");
                    continue;
                }
                packs.Add(result.Db);
            }
            LoadSettingsSafely();
            fast = settings.FastPlay;
            timerOn = settings.TurnTimer;
            string want = settings.DefaultPack;
            LoadPack(packs.Any(p => p.PackId == want) ? want : packs[0].PackId);
        }

        void LoadPack(string id)
        {
            db = packs.First(p => p.PackId == id);
            settings.DefaultPack = id;
            SaveSettingsSafely();
            var saved = SmokeRequested ? "" : PlayerPrefs.GetString("sd.pick." + id, "");
            pick = saved.Split(',').Where(x => db.Streamers.Any(s => s.Id == x)).Distinct().Take(6).ToList();
            if (pick.Count == 0) RandomPick();
            teamDetail = pick[0];
            rosterScroll = teamDetailScroll = Vector2.zero;
        }

        void RandomPick()
        {
            pick = TeamBuilder.Suggest(db, null, new string[0], unchecked((uint)System.Environment.TickCount));
            SavePick();
        }
        void SavePick()
        {
            if (!SmokeRequested) PlayerPrefs.SetString("sd.pick." + db.PackId, string.Join(",", pick));
        }

        void EnsureStyles()
        {
            if (stylesReady) return;
            var resources = Resources.Load<ScreenResources>("StreamDuelUI");
            if (resources == null || resources.RegularFont == null || resources.BoldFont == null)
                throw new System.InvalidOperationException("旧版画面缺少 StreamDuelUI 的中文字体资源。");
            font = resources.RegularFont;
            white = Texture2D.whiteTexture;
            circle = MakeCircle(128);
            var panelTex = MakeTex(Panel);
            var panel2Tex = MakeTex(Panel2);
            var lineTex = MakeTex(Line);

            sText = new GUIStyle(GUI.skin.label) { font = font, fontSize = 17, wordWrap = true, richText = true };
            sText.normal.textColor = Fg;
            sSmall = new GUIStyle(sText) { fontSize = 14 };
            sSmall.normal.textColor = Muted;
            sPackError = new GUIStyle(sSmall) { wordWrap = false, richText = false };
            sPackError.normal.textColor = Foe;
            sTitle = new GUIStyle(sText) { font = resources.BoldFont, fontSize = 22, fontStyle = FontStyle.Bold, wordWrap = false };
            sRosterTitle = new GUIStyle(sTitle) { wordWrap = true };
            sBig = new GUIStyle(sText) { font = resources.BoldFont, fontSize = 30, fontStyle = FontStyle.Bold, wordWrap = false };
            sHuge = new GUIStyle(sText) { font = resources.BoldFont, fontSize = 64, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, wordWrap = false };
            sHuge.normal.textColor = Accent;
            sCenter = new GUIStyle(sText) { alignment = TextAnchor.MiddleCenter };
            sPortrait = new GUIStyle(sText) { font = resources.BoldFont, alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold, wordWrap = false };
            sPortrait.normal.textColor = Hex("#10142a");

            sBtn = new GUIStyle(GUI.skin.button) { font = font, fontSize = 17, richText = true, padding = new RectOffset(12, 12, 6, 6) };
            sBtn.normal.background = panel2Tex; sBtn.hover.background = lineTex; sBtn.active.background = lineTex;
            sBtn.normal.textColor = sBtn.hover.textColor = sBtn.active.textColor = Fg;

            sChip = new GUIStyle(sBtn) { fontSize = 14, padding = new RectOffset(10, 10, 3, 3), wordWrap = false };
            sSkillInfo = new GUIStyle(sChip) { padding = new RectOffset(), alignment = TextAnchor.MiddleCenter };
            sPanel = new GUIStyle(GUI.skin.box);
            sPanel.normal.background = panelTex;
            sSkill = new GUIStyle(sBtn) { alignment = TextAnchor.UpperLeft, wordWrap = true, padding = new RectOffset(12, 30, 10, 8) };
            stylesReady = true;
        }

        static Texture2D MakeTex(Color c)
        {
            var t = new Texture2D(1, 1) { hideFlags = HideFlags.DontSave };
            t.SetPixel(0, 0, c); t.Apply();
            return t;
        }

        static Texture2D MakeCircle(int n)
        {
            var t = new Texture2D(n, n, TextureFormat.RGBA32, false) { hideFlags = HideFlags.DontSave, filterMode = FilterMode.Bilinear };
            float r = n / 2f;
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float dx = x + 0.5f - r, dy = y + 0.5f - r, d = Mathf.Sqrt(dx * dx + dy * dy);
                    float a = Mathf.Clamp01(r - d);
                    float shade = Mathf.Lerp(1.15f, 0.7f, (d / r) * 0.6f + (y < r ? 0f : 0.25f));
                    t.SetPixel(x, y, new Color(shade, shade, shade, a));
                }
            t.Apply();
            return t;
        }

        // ================= 每帧逻辑：逐行回放、限时 =================
        void Update()
        {
            if (view != View.Battle || b == null) return;
            if (b.Phase == Phase.Lead) return;
            if (playing)
            {
                if (Time.unscaledTime >= nextLineAt)
                {
                    shown++;
                    if (shown >= b.Log.Count) { shown = b.Log.Count; playing = false; AfterPlayback(); return; }
                    var e = b.Log[shown - 1];
                    float d = fast ? 0.05f : e.Kind == "turn" ? 0.4f : e.Kind == "check" || e.Kind == "order" ? 0.25f : 0.32f;
                    nextLineAt = Time.unscaledTime + d;
                }
                return;
            }
            if (!timerOn || b.Phase == Phase.Over) return;
            timerLeft -= Time.unscaledDeltaTime;
            if (timerLeft > 0) return;
            if (b.Phase == Phase.Choose) Submit(BattleAction.Pass());
            else if (b.Phase == Phase.Replace && b.Need[0]) DoReplace(Ai.ChooseReplacement(b, 0));
        }

        void StartBattle(List<string>[] teams)
        {
            lastTeams = teams;
            b = new Battle(db, new IList<string>[] { teams[0], teams[1] }, new[] { "你", "对手" }, (uint)System.Environment.TickCount, leadPick: true);
            view = View.Battle;
            shown = 0; logCountDrawn = 0; playing = false; timerLeft = 10f;
            detailKind = "pet"; detailSide = 1; detailIdx = 0; detailScroll = Vector2.zero;
            b.ChooseLead(1, Ai.ChooseLead(b, 1));
        }

        void SubmitLead(int index)
        {
            if (b == null || b.Phase != Phase.Lead || b.HasChosenLead(0)) return;
            b.ChooseLead(0, index);
            detailKind = null;
            Play();
        }

        void Play() { playing = true; nextLineAt = Time.unscaledTime; }

        void AfterPlayback()
        {
            if (b.Phase == Phase.Replace && b.Need[1])
            {
                b.Replace(1, Ai.ChooseReplacement(b, 1));
                if (!b.Need[0]) { Play(); return; }
            }
            timerLeft = 10f;
        }

        void Submit(BattleAction a)
        {
            if (b == null || playing || b.Phase != Phase.Choose) return;
            b.ResolveTurn(a, Ai.Choose(b, 1));
            detailKind = null;
            Play();
        }

        void DoReplace(int i)
        {
            if (playing || b.Phase != Phase.Replace || !b.Need[0]) return;
            b.Replace(0, i);
            if (b.Phase == Phase.Replace && b.Need[1]) b.Replace(1, Ai.ChooseReplacement(b, 1));
            Play();
        }

        void SkipPlayback() { shown = b.Log.Count; playing = false; AfterPlayback(); }

        // ================= 绘制 =================
        void OnGUI()
        {
            EnsureStyles();
            const float navigationHeight = 44f;
            float availableHeight = Mathf.Max(1, Screen.height - navigationHeight);
            float scale = Mathf.Min(Screen.width / RefW, availableHeight / RefH);
            float ox = (Screen.width - RefW * scale) / 2f, oy = navigationHeight + (availableHeight - RefH * scale) / 2f;
            GUI.color = Bg;
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), white);
            GUI.color = Color.white;
            GUI.matrix = Matrix4x4.TRS(new Vector3(ox, oy, 0), Quaternion.identity, new Vector3(scale, scale, 1));
            if (view == View.Team) DrawTeam();
            else if (b.Phase == Phase.Lead) DrawLead();
            else DrawBattle();
            DrawSettingsNotice();
            GUI.matrix = Matrix4x4.identity;
            if (MainMenuRequested != null && GUI.Button(new Rect(12, 6, 120, 32), "主菜单", sBtn)) Do(MainMenuRequested);
            if (pending != null) { var a = pending; pending = null; a(); }
        }

        void Fill(Rect r, Color c) { var old = GUI.color; GUI.color = c; GUI.DrawTexture(r, white); GUI.color = old; }
        void Box(Rect r) { GUI.Box(r, GUIContent.none, sPanel); }
        string C(string text, Color c) { return "<color=" + HexOf(c) + ">" + text + "</color>"; }
        string TypeTag(string type) { return C("[" + db.Types[type].Name + "]", TypeColor(type)); }
        string TypeTag(StreamerDef streamer) { return C("[" + Describe.Tracks(db, streamer) + "]", TypeColor(streamer.Type)); }

        void Portrait(Rect r, StreamerDef d, bool dead)
        {
            var old = GUI.color;
            GUI.color = dead ? new Color(0.3f, 0.3f, 0.35f) : TypeColor(d.Type);
            GUI.DrawTexture(r, circle);
            GUI.color = old;
            sPortrait.fontSize = Mathf.RoundToInt(r.height * 0.48f);
            GUI.Label(r, d.Name.Substring(0, 1), sPortrait);
        }

        void HpBar(Rect r, int hp, int max)
        {
            float ratio = max > 0 ? (float)hp / max : 0;
            Fill(r, new Color(0, 0, 0, 0.5f));
            var c = ratio <= 0.25f ? Hex("#e2504f") : ratio <= 0.5f ? Hex("#e2b23a") : Hex("#4fd47a");
            Fill(new Rect(r.x, r.y, r.width * ratio, r.height), c);
        }

        // ---------- 组队页 ----------
        void DrawTeam()
        {
            GUI.Label(new Rect(24, 14, 600, 44), C("开播对决", Accent) + C("  工作名 · 原型 v0.1", Muted), sBig);
            if (db.PackId != null && db.PackId.StartsWith("fan_", System.StringComparison.Ordinal))
                GUI.Label(new Rect(650, 24, 480, 28), "非官方同人作品 · 免费 · 非商业", sSmall);
            float errorHeight = packErrors.Count > 0 ? 34 : 0;
            if (packErrors.Count > 0)
            {
                var errorText = new GUIContent(string.Join("；", packErrors).Replace('\r', ' ').Replace('\n', ' '));
                float errorWidth = Mathf.Max(1532, sPackError.CalcSize(errorText).x);
                packErrorScroll = GUI.BeginScrollView(new Rect(24, 60, 1552, errorHeight), packErrorScroll, new Rect(0, 0, errorWidth, 18));
                GUI.Label(new Rect(0, 0, errorWidth, 18), errorText, sPackError);
                GUI.EndScrollView();
            }
            float packWidth = packs.Sum(p => sChip.CalcSize(new GUIContent(p.PackName ?? p.PackId)).x + 16);
            packScroll = GUI.BeginScrollView(new Rect(24, 62 + errorHeight, 1552, 36), packScroll, new Rect(0, 0, Mathf.Max(1532, packWidth), 20));
            float px = 0;
            foreach (var pack in packs)
            {
                string label = pack.PackName ?? pack.PackId;
                var size = sChip.CalcSize(new GUIContent(label));
                bool on = pack.PackId == db.PackId;
                if (GUI.Button(new Rect(px, 0, size.x + 8, 20), on ? C(label, Accent) : C(label, Muted), sChip) && !on) { var id = pack.PackId; Do(() => LoadPack(id)); }
                px += size.x + 16;
            }
            GUI.EndScrollView();
            if (GUI.Button(new Rect(1150, 18, 120, 40), "随机挑选", sBtn)) Do(RandomPick);
            if (GUI.Button(new Rect(1280, 18, 90, 40), "清空", sBtn)) Do(() => { pick.Clear(); SavePick(); });
            GUI.enabled = pick.Count == 6;
            if (GUI.Button(new Rect(1380, 18, 196, 40), C("开播！（" + pick.Count + "/6）", pick.Count == 6 ? Accent : Muted), sBtn))
            {
                // 名单不足 12 位时，电脑先用你没选的，不够再从你选的里补（同一位主播可以双方都有）
                var foe = db.Streamers.Select(s => s.Id).Where(id => !pick.Contains(id))
                    .Concat(pick).Take(6).ToList();
                var mine = pick.ToList();
                Do(() => StartBattle(new[] { mine, foe }));
            }
            GUI.enabled = true;

            for (int i = 0; i < 6; i++)
            {
                var r = new Rect(24 + i * 186, 100 + errorHeight, 176, 56);
                Box(r);
                GUI.Label(new Rect(r.x + 10, r.y + 12, 30, 32), C((i + 1).ToString(), Accent), sTitle);
                if (i < pick.Count)
                {
                    var d = db.Streamer(pick[i]);
                    Portrait(new Rect(r.x + 36, r.y + 8, 40, 40), d, false);
                    GUI.Label(new Rect(r.x + 84, r.y + 15, 90, 28), d.Name, sText);
                }
                else GUI.Label(new Rect(r.x + 40, r.y + 15, 120, 28), "空位", sSmall);
            }

            GUI.Label(new Rect(1150, 104 + errorHeight, 426, 48), "选 6 位，先后顺序就是 1 到 6 号位；电脑优先用你没选的主播。", sSmall);
            GUI.Label(new Rect(24, 162 + errorHeight, 1552, 44), C(db.PackNotice ?? "", Muted), sSmall);
            var area = new Rect(24, 210 + errorHeight, 1110, 672 - errorHeight);
            int rows = Mathf.CeilToInt(db.Streamers.Count / 3f);
            var layouts = new RosterCardLayout[db.Streamers.Count];
            var rowHeights = new float[rows];
            for (int i = 0; i < db.Streamers.Count; i++)
            {
                layouts[i] = MeasureRosterCard(db.Streamers[i], 350);
                rowHeights[i / 3] = Mathf.Max(rowHeights[i / 3], layouts[i].Height);
            }
            rosterScroll = GUI.BeginScrollView(area, rosterScroll, new Rect(0, 0, 1090, rowHeights.Sum() + rows * 12));
            float rowY = 0;
            for (int row = 0; row < rows; row++)
            {
                for (int col = 0; col < 3; col++)
                {
                    int i = row * 3 + col;
                    if (i >= db.Streamers.Count) break;
                    DrawRosterCard(new Rect(col * 365, rowY, 350, rowHeights[row]), db.Streamers[i], layouts[i]);
                }
                rowY += rowHeights[row] + 12;
            }
            GUI.EndScrollView();

            DrawTeamDetail(new Rect(1150, 210 + errorHeight, 426, 672 - errorHeight));
        }

        sealed class RosterCardLayout
        {
            public GUIContent Name, Intro, Persona;
            public GUIContent[] Stats = new GUIContent[2];
            public Rect NameRect, IntroRect, PersonaRect;
            public Rect[] StatRects = new Rect[2];
            public float Height;
        }

        static Rect MeasureRosterText(GUIContent text, GUIStyle style, float x, float y, float width, float minimumHeight)
        {
            return new Rect(x, y, width, Mathf.Max(minimumHeight, Mathf.Ceil(style.CalcHeight(text, width))));
        }

        RosterCardLayout MeasureRosterCard(StreamerDef d, float width)
        {
            var layout = new RosterCardLayout();
            layout.Name = new GUIContent(d.Name);
            layout.Intro = new GUIContent(TypeTag(d) + (d.Org != null ? C(" " + d.Org, Muted) : "") + " " + C(d.Blurb, Muted));
            // 姓名避开右上按钮，简介从按钮下方开始；测量和绘制共用矩形，避免更换字体后裁字。
            layout.NameRect = MeasureRosterText(layout.Name, sRosterTitle, 90, 12, width - 200, 30);
            layout.IntroRect = MeasureRosterText(layout.Intro, sSmall, 90, Mathf.Max(54, layout.NameRect.yMax), width - 104, 46);
            float y = Mathf.Max(78, layout.IntroRect.yMax) + 8;
            var statKeys = new[] { "hp", "atk", "def", "spa", "sdef", "spd" };
            for (int row = 0; row < 2; row++)
            {
                layout.Stats[row] = new GUIContent(string.Join("  ", statKeys.Skip(row * 3).Take(3)
                    .Select(k => C(db.StatName(k), Muted) + " " + d.Stats[k])));
                layout.StatRects[row] = MeasureRosterText(layout.Stats[row], sSmall, 14, y, width - 28, 24);
                y = layout.StatRects[row].yMax;
            }
            var p = db.Personas[d.Persona];
            layout.Persona = new GUIContent(C("人设·" + p.Name, Accent) + " " + C("（" + p.Node + "，不可消除）", Muted) + "\n" + p.Desc);
            layout.PersonaRect = MeasureRosterText(layout.Persona, sSmall, 14, y + 4, width - 28, 0);
            layout.Height = layout.PersonaRect.yMax + 14;
            return layout;
        }

        void DrawRosterCard(Rect r, StreamerDef d, RosterCardLayout layout)
        {
            int n = pick.IndexOf(d.Id);
            Box(r);
            if (n >= 0) { Fill(new Rect(r.x, r.y, r.width, 3), Accent); }
            if (teamDetail == d.Id) Fill(new Rect(r.x, r.yMax - 3, r.width, 3), Mine);
            GUI.BeginGroup(r);
            if (GUI.Button(new Rect(0, 0, r.width - 110, r.height), GUIContent.none, GUIStyle.none)) Do(() => teamDetail = d.Id);
            Portrait(new Rect(14, 14, 64, 64), d, false);
            GUI.Label(layout.NameRect, layout.Name, sRosterTitle);
            GUI.Label(layout.IntroRect, layout.Intro, sSmall);
            for (int row = 0; row < 2; row++)
                GUI.Label(layout.StatRects[row], layout.Stats[row], sSmall);
            GUI.Label(layout.PersonaRect, layout.Persona, sSmall);
            if (GUI.Button(new Rect(r.width - 100, 14, 86, 36), n >= 0 ? C((n + 1) + " 号位", Accent) : "选入", sBtn))
                Do(() =>
                {
                    if (n >= 0) pick.RemoveAt(n); else if (pick.Count < 6) pick.Add(d.Id);
                    SavePick();
                    teamDetail = d.Id;
                });
            GUI.EndGroup();
        }

        void DrawTeamDetail(Rect r)
        {
            Box(r);
            var d = db.Streamer(teamDetail);
            GUILayout.BeginArea(new Rect(r.x + 14, r.y + 12, r.width - 28, r.height - 24));
            teamDetailScroll = GUILayout.BeginScrollView(teamDetailScroll);
            GUILayout.Label(d.Name + "  " + TypeTag(d), sTitle);
            var p = db.Personas[d.Persona];
            GUILayout.Label(C("人设·" + p.Name, Accent) + C("  时机：" + p.Node + " · 不可消除", Muted) + "\n" + p.Desc, sSmall);
            GUILayout.Space(6);
            foreach (var id in d.Skills) SkillBlock(db.Skills[id], false, -1);
            SkillBlock(db.Skills[d.Fifth], true, -1);
            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        void SkillBlock(SkillDef sk, bool fifth, int ppLeft, int? maxPp = null, Battle battle = null)
        {
            GUILayout.Space(8);
            GUILayout.Label((fifth ? C("第五技能 ", Accent) : "") + "<b>" + sk.Name + "</b>  " + SkillMeta(sk, maxPp) + (ppLeft >= 0 ? C("  剩余 PP " + ppLeft, Muted) : ""), sText);
            if (sk.Effects.Count == 0) GUILayout.Label("没有附加效果", sSmall);
            foreach (var e in sk.Effects) GUILayout.Label(EffectLine(e, battle), sSmall);
        }

        string SkillMeta(SkillDef sk, int? maxPp = null)
        {
            var bits = new List<string> { TypeTag(sk.Type), C(db.CatNames[sk.Cat], Muted) };
            if (sk.Power > 0) bits.Add(C("威力 " + sk.Power, Muted));
            bits.Add(C("PP " + (maxPp ?? sk.Pp), Muted));
            if (sk.Acc < 100) bits.Add(C("命中 " + sk.Acc + "%", Muted));
            if (sk.Prio != 0) bits.Add(C("先制 " + (sk.Prio > 0 ? "+" : "") + sk.Prio, Accent));
            return string.Join(" ", bits);
        }

        string EffectLine(EffectDef e, Battle battle = null)
        {
            var t = Describe.Effect(db, e, battle);
            string at = C("〔" + t.At + "〕", e.At == "pre" ? Hex("#7fd3ff") : Hex("#ffcf7a"));
            string disp = t.Dispel == null ? "" : " " + C("〔" + t.Dispel + "〕", t.Dispel.Contains("不") ? Foe : Ok);
            return at + " " + C(t.Text, Fg) + disp;
        }

        // ---------- 首发选择页 ----------
        void DrawLead()
        {
            GUI.Label(new Rect(24, 12, 620, 44), C("开播对决", Accent) + "  选择首发", sBig);
            if (db.PackId != null && db.PackId.StartsWith("fan_", System.StringComparison.Ordinal)) GUI.Label(new Rect(650, 24, 360, 28), "非官方同人作品 · 免费 · 非商业", sSmall);
            if (GUI.Button(new Rect(1400, 18, 176, 36), "返回组队", sBtn)) Do(() => { view = View.Team; b = null; });
            GUI.Label(new Rect(24, 62, 1500, 32), "先查看对手阵容，再选择你的首发。首发移到 1 号位，其余队友保持相对顺序；此时不计回合限时。", sText);

            DrawLeadTeam(1, 104, "对手阵容 · 点卡片查看技能和人设");
            DrawLeadTeam(0, 388, "你的阵容 · 选择一位首发后开始对战");
            DrawDetail(new Rect(24, 690, 1552, 192));
        }

        void DrawLeadTeam(int side, float y, string title)
        {
            GUI.Label(new Rect(24, y, 1500, 28), C(title, side == 0 ? Mine : Foe), sTitle);
            float width = (1552f - 5 * 12) / 6;
            for (int i = 0; i < b.Sides[side].Team.Count; i++)
            {
                var fighter = b.Sides[side].Team[i];
                var def = fighter.Def;
                var persona = db.Personas[def.Persona];
                var r = new Rect(24 + i * (width + 12), y + 36, width, 230);
                Box(r);
                if (detailKind == "pet" && detailSide == side && detailIdx == i) Fill(new Rect(r.x, r.y, r.width, 3), Accent);
                Portrait(new Rect(r.x + 12, r.y + 14, 52, 52), def, false);
                GUI.Label(new Rect(r.x + 74, r.y + 14, width - 86, 30), def.Name, sTitle);
                GUI.Label(new Rect(r.x + 74, r.y + 44, width - 86, 26), TypeTag(def), sSmall);
                GUI.Label(new Rect(r.x + 12, r.y + 80, width - 24, 28), "人设·" + persona.Name, sSmall);
                string[] stats = { "hp", "atk", "def", "spa", "sdef", "spd" };
                for (int row = 0; row < 2; row++)
                    GUI.Label(new Rect(r.x + 12, r.y + 114 + row * 26, width - 24, 26),
                        string.Join("  ", stats.Skip(row * 3).Take(3).Select(k => db.StatName(k) + " " + (k == "hp" ? fighter.MaxHp : def.Stats[k]))), sSmall);
                int index = i;
                if (GUI.Button(new Rect(r.x + 12, r.y + 176, width - 24, 40), side == 0 ? "选为首发" : "查看详情", sBtn))
                {
                    if (side == 0) Do(() => SubmitLead(index));
                    else Do(() => { detailKind = "pet"; detailSide = side; detailIdx = index; detailScroll = Vector2.zero; });
                }
                if (GUI.Button(new Rect(r.x, r.y, width, 166), GUIContent.none, GUIStyle.none))
                    Do(() => { detailKind = "pet"; detailSide = side; detailIdx = index; detailScroll = Vector2.zero; });
            }
        }

        // ---------- 对战页 ----------
        int[] SnapActive() { return playing && shown > 0 ? b.Log[shown - 1].SnapActive : new[] { b.Sides[0].Active, b.Sides[1].Active }; }
        int Hp(int s, int i) { return playing && shown > 0 ? b.Log[shown - 1].SnapHp[s][i] : b.Sides[s].Team[i].Hp; }

        void DrawBattle()
        {
            var battleView = BattleViewModel.From(b, 0);
            int turnShown = shown > 0 ? b.Log[Mathf.Min(shown, b.Log.Count) - 1].Turn : 1;
            GUI.Label(new Rect(24, 12, 560, 44), C("开播对决", Accent) + C("   第 " + turnShown + " 回合", Fg), sBig);
            if (db.PackId != null && db.PackId.StartsWith("fan_", System.StringComparison.Ordinal)) GUI.Label(new Rect(560, 24, 290, 28), C("非官方同人作品 · 免费 · 非商业", Muted), sSmall);
            bool nf = GUI.Toggle(new Rect(860, 20, 150, 32), fast, " 快速播放", sBtn);
            if (nf != fast) { fast = nf; settings.FastPlay = fast; SaveSettingsSafely(); }
            bool nt = GUI.Toggle(new Rect(1020, 20, 170, 32), timerOn, " 每回合 10 秒", sBtn);
            if (nt != timerOn) { timerOn = nt; timerLeft = 10f; settings.TurnTimer = timerOn; SaveSettingsSafely(); }
            if (GUI.Button(new Rect(1200, 18, 120, 36), "重新组队", sBtn)) Do(() => { view = View.Team; b = null; });
            if (timerOn && !playing && b.Phase != Phase.Over) GUI.Label(new Rect(1340, 18, 200, 40), C(Mathf.CeilToInt(Mathf.Max(0, timerLeft)) + " 秒", Accent), sBig);

            var act = SnapActive();
            GUI.Label(new Rect(24, 60, 600, 24), "对手队伍（点一下看技能和人设）", sSmall);
            DrawBench(1, new Rect(24, 84, 1090, 46), act);
            DrawFighter(1, new Rect(24, 138, 1090, 140), act, battleView.Sides[1].Team[act[1]]);
            DrawFighter(0, new Rect(24, 288, 1090, 140), act, battleView.Sides[0].Team[act[0]]);
            bool replacing = b.Phase == Phase.Replace && b.Need[0] && !playing;
            GUI.Label(new Rect(24, 434, 800, 24), replacing ? C("你的主播下播了，点下面一位补位", Mine) : "你的队伍（蓝边 = 可以换上，换人会用掉这回合）", sSmall);
            DrawBench(0, new Rect(24, 458, 1090, 46), act);
            var actions = MeasureActions(battleView, 1090);
            var actionsRect = new Rect(24, 514, 1090, actions.Height);
            DrawActions(actionsRect, battleView, actions);
            float detailY = actionsRect.yMax + 10;
            DrawDetail(new Rect(24, detailY, 1090, 882 - detailY));
            DrawLog(new Rect(1130, 60, 446, 822));
            if (b.Phase == Phase.Over && !playing) DrawResult();
        }

        void DrawBench(int s, Rect r, int[] act)
        {
            var sd = b.Sides[s];
            float w = (r.width - 5 * 8) / 6f;
            var legal = s == 0 && b.Phase == Phase.Choose && !playing ? b.LegalFor(0) : null;
            for (int i = 0; i < sd.Team.Count; i++)
            {
                var p = sd.Team[i];
                var cr = new Rect(r.x + i * (w + 8), r.y, w, r.height);
                int hp = Hp(s, i);
                bool canSwitch = s == 0 && !playing && ((legal != null && legal.Switches.Contains(i)) || (b.Phase == Phase.Replace && b.Need[0] && !p.Fainted && i != sd.Active));
                Box(cr);
                if (act[s] == i) Fill(new Rect(cr.x, cr.y, cr.width, 3), Accent);
                if (canSwitch) { Fill(new Rect(cr.x, cr.y, 3, cr.height), Mine); Fill(new Rect(cr.xMax - 3, cr.y, 3, cr.height), Mine); }
                Portrait(new Rect(cr.x + 6, cr.y + 6, 34, 34), p.Def, hp <= 0);
                GUI.Label(new Rect(cr.x + 46, cr.y + 3, w - 50, 24), C((i + 1) + ". " + p.Name, hp <= 0 ? Muted : Fg), sSmall);
                HpBar(new Rect(cr.x + 46, cr.y + 31, w - 56, 6), hp, p.MaxHp);
                int idx = i;
                if (GUI.Button(cr, GUIContent.none, GUIStyle.none))
                {
                    if (canSwitch) Do(() => { if (b.Phase == Phase.Replace) DoReplace(idx); else Submit(BattleAction.SwitchTo(idx)); });
                    else Do(() => { detailKind = "pet"; detailSide = s; detailIdx = idx; });
                }
            }
        }

        void DrawFighter(int s, Rect r, int[] act, FighterView fighter)
        {
            var p = b.Sides[s].Team[act[s]];
            int hp = Hp(s, act[s]);
            Box(r);
            Fill(new Rect(r.x, r.y, 5, r.height), s == 0 ? Mine : Foe);
            Portrait(new Rect(r.x + 20, r.y + 18, 100, 100), p.Def, hp <= 0);
            GUI.Label(new Rect(r.x + 136, r.y + 10, 600, 40), "<b>" + p.Name + "</b>  " + TypeTag(p.Def) + C("   " + p.Slot + " 号位 · " + db.StatName("spd") + " " + b.StatVal(p, "spd") + (s == 0 ? "" : "   对手"), Muted), sTitle);
            HpBar(new Rect(r.x + 136, r.y + 48, 520, 14), hp, p.MaxHp);
            GUI.Label(new Rect(r.x + 670, r.y + 40, 260, 28), C(db.StatName("hp") + " " + hp + " / " + p.MaxHp + "（" + Mathf.RoundToInt(100f * hp / p.MaxHp) + "%）", Muted), sSmall);

            float x = r.x + 136, y = r.y + 74, maxX = r.xMax - 12;
            if (Chip(ref x, ref y, r.x + 136, maxX, C("人设·" + fighter.PersonaName, Accent))) Do(() => { detailKind = "persona"; detailId = fighter.PersonaId; });
            foreach (var chip in fighter.Statuses)
            {
                string label = chip.IsStatus
                    ? C(chip.Name + " · " + chip.Duration, chip.IsControl ? Ctrl : Weak)
                    : chip.Name + " · " + chip.Duration + " · " + C(chip.CanDispel ? "可消回合" : "消不掉", chip.CanDispel ? Ok : Foe);
                if (Chip(ref x, ref y, r.x + 136, maxX, label, chip.Tooltip))
                {
                    var id = chip.Id;
                    var kind = chip.IsStatus ? "status" : "field";
                    Do(() => { detailKind = kind; detailId = id; });
                }
            }
            foreach (var kv in fighter.Stages)
                if (kv.Value != 0) Chip(ref x, ref y, r.x + 136, maxX, C(db.StatName(kv.Key) + " " + (kv.Value > 0 ? "+" : "") + kv.Value, kv.Value > 0 ? Ok : Foe));
        }

        bool Chip(ref float x, ref float y, float startX, float maxX, string text, string tooltip = null)
        {
            var gc = new GUIContent(text, tooltip);
            var size = sChip.CalcSize(new GUIContent(StripTags(text)));
            if (x + size.x > maxX && x > startX) { x = startX; y += 32; }
            var rr = new Rect(x, y, size.x, 28);
            bool hit = GUI.Button(rr, gc, sChip);
            x += size.x + 6;
            return hit;
        }

        static string StripTags(string s) { return System.Text.RegularExpressions.Regex.Replace(s, "<.*?>", ""); }

        sealed class ActionLayout
        {
            public GUIContent Head;
            public GUIContent[] Labels;
            public float Height = 166, ContentWidth, ContentHeight, HeaderHeight, ButtonWidth, ButtonHeight;
        }

        string ActionSkillLabel(SkillView skill, FighterView foe)
        {
            double m = skill.Effectiveness;
            string hint = skill.Power == 0 ? C("状态技能", Muted) : C("对" + foe.Tracks + " ×" + m, m > 1 ? Ok : m < 1 ? Foe : Muted);
            string prio = skill.Priority != 0 ? C("先制 " + (skill.Priority > 0 ? "+" : "") + skill.Priority, Accent) : C("先制 0", Muted);
            return (skill.Fifth ? C("第五技能 ", Accent) : "") + "<b>" + skill.Name + "</b>\n" +
                   C(skill.Track + " · " + skill.Category + (skill.Power > 0 ? " · 威力 " + skill.Power : ""), Muted) + "\n" +
                   C("PP " + skill.Pp + "/" + skill.MaxPp, Muted) + "  " + prio + "\n" + hint;
        }

        ActionLayout MeasureActions(BattleViewModel battleView, float width)
        {
            var layout = new ActionLayout();
            if (playing || b.Phase != Phase.Choose) return layout;
            var me = battleView.Sides[0].Team[battleView.Sides[0].Active];
            var foe = battleView.Sides[1].Team[battleView.Sides[1].Active];
            string head = "选择技能，或点上方蓝边的队友换人。";
            if (battleView.ControlledReason != null) head = "<b>" + me.Name + " 处于「" + battleView.ControlledReason + "」，本回合不能用技能。</b>可以换人，或直接跳过。";
            else if (battleView.Struggle) head = "<b>所有技能 PP 用完</b>，将自动使用「" + battleView.StruggleName + "」。";
            else if (battleView.FreePp) head = "人设·团魂生效中：6 号位队友在播，技能不消耗 PP。";
            layout.Head = new GUIContent(head);
            // 预留滚动条宽度，让极长文本滚动时仍与测量使用同一宽度。
            layout.ContentWidth = width - 48;
            bool hasPass = battleView.ControlledReason != null || battleView.Struggle;
            float headWidth = layout.ContentWidth - (hasPass ? 174 : 0);
            layout.HeaderHeight = Mathf.Max(hasPass ? 36 : 30, Mathf.Ceil(sText.CalcHeight(layout.Head, headWidth)));
            layout.ButtonWidth = (layout.ContentWidth - 4 * 10) / 5f;
            layout.ButtonHeight = 108;
            layout.Labels = new GUIContent[battleView.Skills.Count];
            for (int i = 0; i < battleView.Skills.Count; i++)
            {
                layout.Labels[i] = new GUIContent(ActionSkillLabel(battleView.Skills[i], foe));
                layout.ButtonHeight = Mathf.Max(layout.ButtonHeight, Mathf.Ceil(sSkill.CalcHeight(layout.Labels[i], layout.ButtonWidth)));
            }
            layout.ContentHeight = layout.HeaderHeight + 8 + layout.ButtonHeight;
            // 底部详情至少保留 96 高，额外长的技能文本在行动区内滚动查看。
            layout.Height = Mathf.Clamp(layout.ContentHeight + 20, 166, 262);
            return layout;
        }

        void DrawActions(Rect r, BattleViewModel battleView, ActionLayout layout)
        {
            Box(r);
            var inner = new Rect(r.x + 14, r.y + 10, r.width - 28, r.height - 20);
            if (playing)
            {
                GUI.Label(new Rect(inner.x, inner.y, 600, 30), "正在结算……", sText);
                if (GUI.Button(new Rect(inner.xMax - 140, inner.y, 140, 36), "跳过动画", sBtn)) Do(SkipPlayback);
                return;
            }
            if (b.Phase == Phase.Over) return;
            if (b.Phase == Phase.Replace)
            {
                GUI.Label(new Rect(inner.x, inner.y, 900, 30), "<b>你的主播下播了。</b>点上方蓝边的一位队友补位。", sText);
                return;
            }
            bool mouseInActions = inner.Contains(Event.current.mousePosition);
            actionScroll = GUI.BeginScrollView(inner, actionScroll, new Rect(0, 0, layout.ContentWidth, layout.ContentHeight));
            bool hasPass = battleView.ControlledReason != null || battleView.Struggle;
            GUI.Label(new Rect(0, 0, layout.ContentWidth - (hasPass ? 174 : 0), layout.HeaderHeight), layout.Head, sText);
            if (hasPass)
                if (GUI.Button(new Rect(layout.ContentWidth - 160, 0, 160, 36), battleView.Struggle ? "使用" + battleView.StruggleName : "跳过本回合", sBtn)) Do(() => Submit(BattleAction.Pass()));

            float w = layout.ButtonWidth;
            for (int i = 0; i < battleView.Skills.Count; i++)
            {
                var skill = battleView.Skills[i];
                int si = skill.Index;
                var br = new Rect(i * (w + 10), layout.HeaderHeight + 8, w, layout.ButtonHeight);
                var infoRect = new Rect(br.xMax - 30, br.y + 6, 24, 24);
                if (mouseInActions && br.Contains(Event.current.mousePosition)) { detailKind = "skill"; detailIdx = si; }
                var tc = TypeColor(skill.TrackId);
                // 问号后绘制以保持可见，按下时不让下层技能按钮抢走输入。
                bool pressingInfo = Event.current.type == EventType.MouseDown && infoRect.Contains(Event.current.mousePosition);
                GUI.enabled = skill.Usable && !pressingInfo;
                if (GUI.Button(br, layout.Labels[i], sSkill)) Do(() => Submit(BattleAction.Skill(si)));
                GUI.enabled = true;
                Fill(new Rect(br.x, br.y, br.width, 4), skill.Fifth ? Accent : tc);
                if (GUI.Button(infoRect, "?", sSkillInfo)) Do(() => { detailKind = "skill"; detailIdx = si; });
            }
            GUI.EndScrollView();
        }

        void DrawDetail(Rect r)
        {
            Box(r);
            GUILayout.BeginArea(new Rect(r.x + 14, r.y + 10, r.width - 28, r.height - 20));
            detailScroll = GUILayout.BeginScrollView(detailScroll);
            switch (detailKind)
            {
                case "skill":
                {
                    var me = b.Active(0);
                    if (detailIdx >= me.Skills.Count) break;
                    var slot = me.Skills[detailIdx]; var sk = db.Skills[slot.Id];
                    var pr = b.PrioOf(0, sk);
                    GUILayout.Label((slot.Fifth ? C("第五技能 ", Accent) : "") + "<b>" + sk.Name + "</b>  " + SkillMeta(sk, slot.Max), sText);
                    GUILayout.Label("当前实际先制 " + (pr.Value > 0 ? "+" : "") + pr.Value + (pr.Parts.Count > 0 ? "（技能本身 " + sk.Prio + "，" + string.Join("，", pr.Parts) + "）" : ""), sSmall);
                    if (sk.Effects.Count == 0) GUILayout.Label("没有附加效果", sSmall);
                    foreach (var e in sk.Effects) GUILayout.Label(EffectLine(e, b), sSmall);
                    break;
                }
                case "persona":
                {
                    var p = db.Personas[detailId];
                    GUILayout.Label("<b>人设·" + p.Name + "</b>  " + C("时机：" + p.Node + " · 不可消除", Muted), sText);
                    GUILayout.Label(Describe.PersonaDescription(db, p, b), sSmall);
                    break;
                }
                case "status":
                {
                    var s = db.Statuses[detailId];
                    GUILayout.Label("<b>" + s.Name + "</b>  " + C((s.IsControl ? "控制类" : "弱化类") + " · " + (s.Cleanse ? "可净化" : "不可净化，只能等它结束"), Muted), sText);
                    GUILayout.Label(s.Desc, sSmall);
                    break;
                }
                case "field":
                {
                    var f = db.Fields[detailId];
                    GUILayout.Label("<b>" + f.Name + "</b>  " + C(Describe.FieldKind(f) + " · 时机：" + f.When + " · " + (f.Dispel ? "可被消回合" : "消回合清不掉"), Muted), sText);
                    GUILayout.Label(f.Desc, sSmall);
                    break;
                }
                case "pet":
                {
                    var p = b.Sides[detailSide].Team[detailIdx];
                    var pd = db.Personas[p.Def.Persona];
                    GUILayout.Label("<b>" + p.Name + "</b>  " + TypeTag(p.Def) + C("  " + (detailSide == 0 ? "你的" : "对手") + " " + p.Slot + " 号位 · " + db.StatName("hp") + " " + p.Hp + "/" + p.MaxHp, Muted), sText);
                    GUILayout.Label(C("人设·" + pd.Name, Accent) + C("（" + pd.Node + "）", Muted) + Describe.PersonaDescription(db, pd, b), sSmall);
                    foreach (var sl in p.Skills) SkillBlock(db.Skills[sl.Id], sl.Fifth, sl.Pp, sl.Max, b);
                    break;
                }
                default:
                    GUILayout.Label("把鼠标移到技能上、点身上的状态或人设、或点对手队伍里的主播，这里会显示完整说明。每个效果都标着〔命中前判定〕或〔命中后判定〕，以及能不能被消除。", sSmall);
                    break;
            }
            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        static readonly string[] Steps = { Node.Switch, Node.TurnStart, Node.First, Node.Second, Node.TurnEnd, Node.Replace };

        void DrawLog(Rect r)
        {
            Box(r);
            GUI.Label(new Rect(r.x + 14, r.y + 8, 400, 24), "每回合的结算顺序", sSmall);
            string cur = playing && shown > 0 ? b.Log[shown - 1].Node : null;
            float x = r.x + 14;
            foreach (var s in Steps)
            {
                var size = sChip.CalcSize(new GUIContent(s));
                var rr = new Rect(x, r.y + 34, size.x, 28);
                if (s == cur) Fill(rr, Accent);
                GUI.Label(rr, C(s, s == cur ? Hex("#2a1a00") : Muted), sCenter);
                x += size.x + 2;
            }
            Fill(new Rect(r.x + 10, r.y + 70, r.width - 20, 1), Line);

            int count = Mathf.Min(shown, b.Log.Count);
            if (count != logCountDrawn) { logCountDrawn = count; logScroll.y = float.MaxValue; }
            GUILayout.BeginArea(new Rect(r.x + 10, r.y + 78, r.width - 20, r.height - 88));
            logScroll = GUILayout.BeginScrollView(logScroll);
            for (int i = 0; i < count; i++)
            {
                var e = b.Log[i];
                if (e.Kind == "turn") { GUILayout.Space(6); GUILayout.Label(C("<b>" + e.Text + "</b>", Accent), sText); continue; }
                Color c = e.Kind == "check" || e.Kind == "order" ? Muted : e.Kind == "trait" ? Accent : e.Side == 0 ? Hex("#cfeeff") : e.Side == 1 ? Hex("#ffd6dc") : Fg;
                GUILayout.BeginHorizontal();
                GUILayout.Label(C(e.Node, Muted), sSmall, GUILayout.Width(72));
                GUILayout.Label(C(e.Kind == "faint" ? "<b>" + e.Text + "</b>" : e.Text, c), sSmall);
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        void DrawResult()
        {
            Fill(new Rect(0, 0, RefW, RefH), new Color(Bg.r, Bg.g, Bg.b, 0.75f));
            var r = new Rect(RefW / 2 - 260, RefH / 2 - 185, 520, 370);
            Box(r);
            string res = b.Winner == 0 ? "胜利" : b.Winner == 1 ? "失败" : "平局";
            GUI.Label(new Rect(r.x, r.y + 30, r.width, 90), res, sHuge);
            GUI.Label(new Rect(r.x, r.y + 130, r.width, 30), "共 " + b.Log[b.Log.Count - 1].Turn + " 回合", sCenter);
            if (GUI.Button(new Rect(r.x + 40, r.y + 200, 230, 50), "同样阵容再来一局", sBtn)) Do(() => StartBattle(lastTeams));
            if (GUI.Button(new Rect(r.x + 290, r.y + 200, 190, 50), "重新组队", sBtn)) Do(() => { view = View.Team; b = null; });
            if (ResultRequested != null && GUI.Button(new Rect(r.x + 40, r.y + 270, 440, 50), "查看结算、保存与回看", sBtn))
                Do(() => ResultRequested(b));
        }
    }
}
