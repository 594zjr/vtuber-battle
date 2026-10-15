// 第一版画面：用 Unity 自带的即时模式 GUI 画出组队页和对战页，不需要任何场景、预制体或美术资源。
// 规则全部在 StreamDuel.Core 里，这里只负责显示和接收点击。以后换正式美术时只换这一层。
using System.Collections.Generic;
using System.Linq;
using StreamDuel.Core;
using UnityEngine;

namespace StreamDuel.Game
{
    public sealed class BattleScreen : MonoBehaviour
    {
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

        Vector2 rosterScroll, logScroll, detailScroll, teamDetailScroll;
        int logCountDrawn;

        // ---------- 颜色与样式 ----------
        static readonly Color Bg = Hex("#0c1124"), Panel = Hex("#151c38"), Panel2 = Hex("#1c2549"), Line = Hex("#2b3666");
        static readonly Color Fg = Hex("#e8ebfa"), Muted = Hex("#98a1c8"), Accent = Hex("#ffb547"), Mine = Hex("#5cc6ff"), Foe = Hex("#ff6b7f");
        static readonly Color Ok = Hex("#6fdc8c"), Ctrl = Hex("#ff8a5c"), Weak = Hex("#c58bff");

        Font font;
        Texture2D white, circle;
        GUIStyle sText, sSmall, sTitle, sBig, sHuge, sBtn, sChip, sPanel, sSkill, sCenter, sPortrait;
        bool stylesReady;

        static Color Hex(string h) { Color c; return ColorUtility.TryParseHtmlString(h, out c) ? c : Color.magenta; }
        static string HexOf(Color c) { return "#" + ColorUtility.ToHtmlStringRGB(c); }
        Color TypeColor(string type) { return Hex(db.Types[type].Color); }

        void Awake()
        {
            var ta = Resources.Load<TextAsset>("StreamDuelContent");
            db = ContentDb.FromJson(ta.text);
            fast = PlayerPrefs.GetInt("sd.fast", 0) == 1;
            timerOn = PlayerPrefs.GetInt("sd.timer", 0) == 1;
            var saved = PlayerPrefs.GetString("sd.pick", "");
            pick = saved.Split(',').Where(id => db.Streamers.Any(s => s.Id == id)).Distinct().Take(6).ToList();
            if (pick.Count == 0) RandomPick();
            teamDetail = pick.Count > 0 ? pick[0] : db.Streamers[0].Id;
        }

        void RandomPick()
        {
            pick = db.Streamers.Select(s => s.Id).OrderBy(_ => Random.value).Take(6).ToList();
            SavePick();
        }
        void SavePick() { PlayerPrefs.SetString("sd.pick", string.Join(",", pick)); }

        void EnsureStyles()
        {
            if (stylesReady) return;
            font = Font.CreateDynamicFontFromOSFont(new[] { "Microsoft YaHei UI", "Microsoft YaHei", "PingFang SC", "Noto Sans CJK SC", "Source Han Sans SC", "SimHei", "Arial" }, 18);
            white = Texture2D.whiteTexture;
            circle = MakeCircle(128);
            var panelTex = MakeTex(Panel);
            var panel2Tex = MakeTex(Panel2);
            var lineTex = MakeTex(Line);

            sText = new GUIStyle(GUI.skin.label) { font = font, fontSize = 17, wordWrap = true, richText = true };
            sText.normal.textColor = Fg;
            sSmall = new GUIStyle(sText) { fontSize = 14 };
            sSmall.normal.textColor = Muted;
            sTitle = new GUIStyle(sText) { fontSize = 22, fontStyle = FontStyle.Bold, wordWrap = false };
            sBig = new GUIStyle(sText) { fontSize = 30, fontStyle = FontStyle.Bold, wordWrap = false };
            sHuge = new GUIStyle(sText) { fontSize = 64, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, wordWrap = false };
            sHuge.normal.textColor = Accent;
            sCenter = new GUIStyle(sText) { alignment = TextAnchor.MiddleCenter };
            sPortrait = new GUIStyle(sText) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold, wordWrap = false };
            sPortrait.normal.textColor = Hex("#10142a");

            sBtn = new GUIStyle(GUI.skin.button) { font = font, fontSize = 17, richText = true, padding = new RectOffset(12, 12, 6, 6) };
            sBtn.normal.background = panel2Tex; sBtn.hover.background = lineTex; sBtn.active.background = lineTex;
            sBtn.normal.textColor = sBtn.hover.textColor = sBtn.active.textColor = Fg;

            sChip = new GUIStyle(sBtn) { fontSize = 14, padding = new RectOffset(10, 10, 3, 3), wordWrap = false };
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
            b = new Battle(db, new IList<string>[] { teams[0], teams[1] }, new[] { "你", "对手" }, (uint)System.Environment.TickCount);
            view = View.Battle;
            shown = 0; logCountDrawn = 0; detailKind = null;
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
            float scale = Mathf.Min(Screen.width / RefW, Screen.height / RefH);
            float ox = (Screen.width - RefW * scale) / 2f, oy = (Screen.height - RefH * scale) / 2f;
            GUI.color = Bg;
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), white);
            GUI.color = Color.white;
            GUI.matrix = Matrix4x4.TRS(new Vector3(ox, oy, 0), Quaternion.identity, new Vector3(scale, scale, 1));
            if (view == View.Team) DrawTeam(); else DrawBattle();
            GUI.matrix = Matrix4x4.identity;
            if (pending != null) { var a = pending; pending = null; a(); }
        }

        void Fill(Rect r, Color c) { var old = GUI.color; GUI.color = c; GUI.DrawTexture(r, white); GUI.color = old; }
        void Box(Rect r) { GUI.Box(r, GUIContent.none, sPanel); }
        string C(string text, Color c) { return "<color=" + HexOf(c) + ">" + text + "</color>"; }
        string TypeTag(string type) { return C("[" + db.Types[type].Name + "]", TypeColor(type)); }

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
            GUI.Label(new Rect(24, 14, 600, 44), C("开播对决", Accent) + C("  工作名 · 原型 v0.1 · 全部原创角色", Muted), sBig);
            GUI.Label(new Rect(24, 62, 1000, 30), "选 6 位主播，选入的先后就是 1 到 6 号位，开局派出 1 号。没选的 6 位归电脑。点卡片看全部技能和判定时机。", sSmall);
            if (GUI.Button(new Rect(1150, 18, 120, 40), "随机挑选", sBtn)) Do(RandomPick);
            if (GUI.Button(new Rect(1280, 18, 90, 40), "清空", sBtn)) Do(() => { pick.Clear(); SavePick(); });
            GUI.enabled = pick.Count == 6;
            if (GUI.Button(new Rect(1380, 18, 196, 40), C("开播！（" + pick.Count + "/6）", pick.Count == 6 ? Accent : Muted), sBtn))
            {
                var foe = db.Streamers.Select(s => s.Id).Where(id => !pick.Contains(id)).OrderBy(_ => Random.value).ToList();
                var mine = pick.ToList();
                Do(() => StartBattle(new[] { mine, foe }));
            }
            GUI.enabled = true;

            for (int i = 0; i < 6; i++)
            {
                var r = new Rect(24 + i * 186, 100, 176, 56);
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

            var area = new Rect(24, 170, 1110, 712);
            int rows = Mathf.CeilToInt(db.Streamers.Count / 3f);
            rosterScroll = GUI.BeginScrollView(area, rosterScroll, new Rect(0, 0, 1090, rows * 232));
            for (int i = 0; i < db.Streamers.Count; i++)
            {
                var d = db.Streamers[i];
                var r = new Rect((i % 3) * 365, (i / 3) * 232, 350, 220);
                DrawRosterCard(r, d);
            }
            GUI.EndScrollView();

            DrawTeamDetail(new Rect(1150, 100, 426, 782));
        }

        void DrawRosterCard(Rect r, StreamerDef d)
        {
            int n = pick.IndexOf(d.Id);
            Box(r);
            if (n >= 0) { Fill(new Rect(r.x, r.y, r.width, 3), Accent); }
            if (teamDetail == d.Id) Fill(new Rect(r.x, r.yMax - 3, r.width, 3), Mine);
            if (GUI.Button(new Rect(r.x, r.y, r.width - 110, r.height), GUIContent.none, GUIStyle.none)) Do(() => teamDetail = d.Id);
            Portrait(new Rect(r.x + 14, r.y + 14, 64, 64), d, false);
            GUI.Label(new Rect(r.x + 90, r.y + 12, 160, 30), d.Name, sTitle);
            GUI.Label(new Rect(r.x + 90, r.y + 42, 250, 46), TypeTag(d.Type) + " " + C(d.Blurb, Muted), sSmall);
            string stats = string.Join("  ", new[] { "hp", "atk", "def", "spa", "sdef", "spd" }.Select(k => C(db.StatName(k), Muted) + " " + d.Stats[k]));
            GUI.Label(new Rect(r.x + 14, r.y + 92, 330, 26), stats, sSmall);
            var p = db.Personas[d.Persona];
            GUI.Label(new Rect(r.x + 14, r.y + 118, 330, 96), C("人设·" + p.Name, Accent) + " " + C("（" + p.Node + "，不可消除）", Muted) + "\n" + p.Desc, sSmall);
            if (GUI.Button(new Rect(r.xMax - 100, r.y + 14, 86, 36), n >= 0 ? C((n + 1) + " 号位", Accent) : "选入", sBtn))
                Do(() =>
                {
                    if (n >= 0) pick.RemoveAt(n); else if (pick.Count < 6) pick.Add(d.Id);
                    SavePick();
                    teamDetail = d.Id;
                });
        }

        void DrawTeamDetail(Rect r)
        {
            Box(r);
            var d = db.Streamer(teamDetail);
            GUILayout.BeginArea(new Rect(r.x + 14, r.y + 12, r.width - 28, r.height - 24));
            teamDetailScroll = GUILayout.BeginScrollView(teamDetailScroll);
            GUILayout.Label(d.Name + "  " + TypeTag(d.Type), sTitle);
            var p = db.Personas[d.Persona];
            GUILayout.Label(C("人设·" + p.Name, Accent) + C("  时机：" + p.Node + " · 不可消除", Muted) + "\n" + p.Desc, sSmall);
            GUILayout.Space(6);
            foreach (var id in d.Skills) SkillBlock(db.Skills[id], false, -1);
            SkillBlock(db.Skills[d.Fifth], true, -1);
            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        void SkillBlock(SkillDef sk, bool fifth, int ppLeft)
        {
            GUILayout.Space(8);
            GUILayout.Label((fifth ? C("第五技能 ", Accent) : "") + "<b>" + sk.Name + "</b>  " + SkillMeta(sk) + (ppLeft >= 0 ? C("  剩余 PP " + ppLeft, Muted) : ""), sText);
            if (sk.Effects.Count == 0) GUILayout.Label("没有附加效果", sSmall);
            foreach (var e in sk.Effects) GUILayout.Label(EffectLine(e), sSmall);
        }

        string SkillMeta(SkillDef sk)
        {
            var bits = new List<string> { TypeTag(sk.Type), C(db.CatNames[sk.Cat], Muted) };
            if (sk.Power > 0) bits.Add(C("威力 " + sk.Power, Muted));
            bits.Add(C("PP " + sk.Pp, Muted));
            if (sk.Acc < 100) bits.Add(C("命中 " + sk.Acc + "%", Muted));
            if (sk.Prio != 0) bits.Add(C("先制 " + (sk.Prio > 0 ? "+" : "") + sk.Prio, Accent));
            return string.Join(" ", bits);
        }

        string EffectLine(EffectDef e)
        {
            var t = Describe.Effect(db, e);
            string at = C("〔" + t.At + "〕", e.At == "pre" ? Hex("#7fd3ff") : Hex("#ffcf7a"));
            string disp = t.Dispel == null ? "" : " " + C("〔" + t.Dispel + "〕", t.Dispel.Contains("不") ? Foe : Ok);
            return at + " " + C(t.Text, Fg) + disp;
        }

        // ---------- 对战页 ----------
        int[] SnapActive() { return playing && shown > 0 ? b.Log[shown - 1].SnapActive : new[] { b.Sides[0].Active, b.Sides[1].Active }; }
        int Hp(int s, int i) { return playing && shown > 0 ? b.Log[shown - 1].SnapHp[s][i] : b.Sides[s].Team[i].Hp; }

        void DrawBattle()
        {
            int turnShown = shown > 0 ? b.Log[Mathf.Min(shown, b.Log.Count) - 1].Turn : 1;
            GUI.Label(new Rect(24, 12, 700, 44), C("开播对决", Accent) + C("   第 " + turnShown + " 回合", Fg), sBig);
            bool nf = GUI.Toggle(new Rect(860, 20, 150, 32), fast, " 快速播放", sBtn);
            if (nf != fast) { fast = nf; PlayerPrefs.SetInt("sd.fast", fast ? 1 : 0); }
            bool nt = GUI.Toggle(new Rect(1020, 20, 170, 32), timerOn, " 每回合 10 秒", sBtn);
            if (nt != timerOn) { timerOn = nt; timerLeft = 10f; PlayerPrefs.SetInt("sd.timer", timerOn ? 1 : 0); }
            if (GUI.Button(new Rect(1200, 18, 120, 36), "重新组队", sBtn)) Do(() => { view = View.Team; b = null; });
            if (timerOn && !playing && b.Phase != Phase.Over) GUI.Label(new Rect(1340, 18, 200, 40), C(Mathf.CeilToInt(Mathf.Max(0, timerLeft)) + " 秒", Accent), sBig);

            var act = SnapActive();
            GUI.Label(new Rect(24, 60, 600, 24), "对手队伍（点一下看技能和人设）", sSmall);
            DrawBench(1, new Rect(24, 84, 1090, 46), act);
            DrawFighter(1, new Rect(24, 138, 1090, 140), act);
            DrawFighter(0, new Rect(24, 288, 1090, 140), act);
            bool replacing = b.Phase == Phase.Replace && b.Need[0] && !playing;
            GUI.Label(new Rect(24, 434, 800, 24), replacing ? C("你的主播下播了，点下面一位补位", Mine) : "你的队伍（蓝边 = 可以换上，换人会用掉这回合）", sSmall);
            DrawBench(0, new Rect(24, 458, 1090, 46), act);
            DrawActions(new Rect(24, 514, 1090, 166));
            DrawDetail(new Rect(24, 690, 1090, 192));
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

        void DrawFighter(int s, Rect r, int[] act)
        {
            var p = b.Sides[s].Team[act[s]];
            int hp = Hp(s, act[s]);
            Box(r);
            Fill(new Rect(r.x, r.y, 5, r.height), s == 0 ? Mine : Foe);
            Portrait(new Rect(r.x + 20, r.y + 18, 100, 100), p.Def, hp <= 0);
            GUI.Label(new Rect(r.x + 136, r.y + 10, 600, 40), "<b>" + p.Name + "</b>  " + TypeTag(p.Type) + C("   " + p.Slot + " 号位 · " + db.StatName("spd") + " " + b.StatVal(p, "spd") + (s == 0 ? "" : "   对手"), Muted), sTitle);
            HpBar(new Rect(r.x + 136, r.y + 48, 520, 14), hp, p.MaxHp);
            GUI.Label(new Rect(r.x + 670, r.y + 40, 260, 28), C(db.StatName("hp") + " " + hp + " / " + p.MaxHp + "（" + Mathf.RoundToInt(100f * hp / p.MaxHp) + "%）", Muted), sSmall);

            float x = r.x + 136, y = r.y + 74, maxX = r.xMax - 12;
            var pd = db.Personas[p.Def.Persona];
            if (Chip(ref x, ref y, r.x + 136, maxX, C("人设·" + pd.Name, Accent))) Do(() => { detailKind = "persona"; detailId = pd.Id; });
            foreach (var st in p.Statuses)
            {
                var sd = db.Statuses[st.Id];
                string left = sd.IsControl ? "跳过 " + st.Left + " 次" : st.Left + " 回合";
                if (Chip(ref x, ref y, r.x + 136, maxX, C(sd.Name + " · " + left, sd.IsControl ? Ctrl : Weak))) { var id = st.Id; Do(() => { detailKind = "status"; detailId = id; }); }
            }
            foreach (var f in p.Fields)
            {
                var fd = db.Fields[f.Id];
                string dur = fd.MaxStacks > 0 ? f.Stacks + " 层" : f.Turns < 0 ? "直到下场" : f.Turns + " 回合";
                if (Chip(ref x, ref y, r.x + 136, maxX, fd.Name + " · " + dur + " · " + C(fd.Dispel ? "可消回合" : "消不掉", fd.Dispel ? Ok : Foe))) { var id = f.Id; Do(() => { detailKind = "field"; detailId = id; }); }
            }
            foreach (var kv in p.Stages)
                if (kv.Value != 0) Chip(ref x, ref y, r.x + 136, maxX, C(db.StatName(kv.Key) + " " + (kv.Value > 0 ? "+" : "") + kv.Value, kv.Value > 0 ? Ok : Foe));
        }

        bool Chip(ref float x, ref float y, float startX, float maxX, string text)
        {
            var gc = new GUIContent(text);
            var size = sChip.CalcSize(new GUIContent(StripTags(text)));
            if (x + size.x > maxX && x > startX) { x = startX; y += 32; }
            var rr = new Rect(x, y, size.x, 28);
            bool hit = GUI.Button(rr, gc, sChip);
            x += size.x + 6;
            return hit;
        }

        static string StripTags(string s) { return System.Text.RegularExpressions.Regex.Replace(s, "<.*?>", ""); }

        void DrawActions(Rect r)
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
            var L = b.LegalFor(0);
            var me = b.Active(0); var foe = b.Active(1);
            string head = "选择技能，或点上方蓝边的队友换人。";
            if (L.Controlled != null) head = "<b>" + me.Name + " 处于「" + L.Controlled + "」，本回合不能用技能。</b>可以换人，或直接跳过。";
            else if (L.Struggle) head = "<b>所有技能 PP 用完</b>，将自动使用「" + db.Struggle.Name + "」。";
            else if (L.FreePp) head = "人设·团魂生效中：6 号位队友在播，技能不消耗 PP。";
            GUI.Label(new Rect(inner.x, inner.y, 820, 30), head, sText);
            if (L.Controlled != null || L.Struggle)
                if (GUI.Button(new Rect(inner.xMax - 160, inner.y - 2, 160, 36), L.Struggle ? "使用" + db.Struggle.Name : "跳过本回合", sBtn)) { Do(() => Submit(BattleAction.Pass())); return; }

            float w = (inner.width - 4 * 10) / 5f;
            for (int i = 0; i < L.Skills.Count; i++)
            {
                var o = L.Skills[i];
                int si = i;
                var sk = db.Skills[o.Id];
                var br = new Rect(inner.x + i * (w + 10), inner.y + 38, w, 108);
                if (GUI.Button(new Rect(br.xMax - 30, br.y + 6, 24, 24), "?", sChip)) Do(() => { detailKind = "skill"; detailIdx = si; });
                if (br.Contains(Event.current.mousePosition)) { detailKind = "skill"; detailIdx = i; }
                var tc = TypeColor(sk.Type);
                Fill(new Rect(br.x, br.y, br.width, 4), o.Fifth ? Accent : tc);
                double m = sk.Power > 0 ? db.TypeMul(sk.Type, foe.Type) : 1;
                string hint = sk.Power == 0 ? C("状态技能", Muted) : C("对" + db.Types[foe.Type].Name + " ×" + m, m > 1 ? Ok : m < 1 ? Foe : Muted);
                string prio = o.Prio.Value != 0 ? C("先制 " + (o.Prio.Value > 0 ? "+" : "") + o.Prio.Value, Accent) : C("先制 0", Muted);
                string label = (o.Fifth ? C("第五技能 ", Accent) : "") + "<b>" + sk.Name + "</b>\n" +
                               C(db.Types[sk.Type].Name + " · " + db.CatNames[sk.Cat] + (sk.Power > 0 ? " · 威力 " + sk.Power : ""), Muted) + "\n" +
                               C("PP " + o.Pp + "/" + o.Max, Muted) + "  " + prio + "\n" + hint;
                GUI.enabled = o.Usable;
                if (GUI.Button(br, label, sSkill)) Do(() => Submit(BattleAction.Skill(si)));
                GUI.enabled = true;
            }
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
                    GUILayout.Label((slot.Fifth ? C("第五技能 ", Accent) : "") + "<b>" + sk.Name + "</b>  " + SkillMeta(sk), sText);
                    GUILayout.Label("当前实际先制 " + (pr.Value > 0 ? "+" : "") + pr.Value + (pr.Parts.Count > 0 ? "（技能本身 " + sk.Prio + "，" + string.Join("，", pr.Parts) + "）" : ""), sSmall);
                    if (sk.Effects.Count == 0) GUILayout.Label("没有附加效果", sSmall);
                    foreach (var e in sk.Effects) GUILayout.Label(EffectLine(e), sSmall);
                    break;
                }
                case "persona":
                {
                    var p = db.Personas[detailId];
                    GUILayout.Label("<b>人设·" + p.Name + "</b>  " + C("时机：" + p.Node + " · 不可消除", Muted), sText);
                    GUILayout.Label(p.Desc, sSmall);
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
                    GUILayout.Label("<b>" + p.Name + "</b>  " + TypeTag(p.Type) + C("  " + (detailSide == 0 ? "你的" : "对手") + " " + p.Slot + " 号位 · " + db.StatName("hp") + " " + p.Hp + "/" + p.MaxHp, Muted), sText);
                    GUILayout.Label(C("人设·" + pd.Name, Accent) + C("（" + pd.Node + "）", Muted) + pd.Desc, sSmall);
                    foreach (var sl in p.Skills) SkillBlock(db.Skills[sl.Id], sl.Fifth, sl.Pp);
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
            var r = new Rect(RefW / 2 - 260, RefH / 2 - 150, 520, 300);
            Box(r);
            string res = b.Winner == 0 ? "胜利" : b.Winner == 1 ? "失败" : "平局";
            GUI.Label(new Rect(r.x, r.y + 30, r.width, 90), res, sHuge);
            GUI.Label(new Rect(r.x, r.y + 130, r.width, 30), "共 " + b.Log[b.Log.Count - 1].Turn + " 回合", sCenter);
            if (GUI.Button(new Rect(r.x + 40, r.y + 200, 230, 50), "同样阵容再来一局", sBtn)) Do(() => StartBattle(lastTeams));
            if (GUI.Button(new Rect(r.x + 290, r.y + 200, 190, 50), "重新组队", sBtn)) Do(() => { view = View.Team; b = null; });
        }
    }
}
