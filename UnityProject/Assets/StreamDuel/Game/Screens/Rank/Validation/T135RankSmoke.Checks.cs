using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using StreamDuel.Core;
using UnityEngine;
using UnityEngine.UIElements;

namespace StreamDuel.Game
{
    public sealed partial class T135RankSmoke
    {
        sealed class MemoryProfile
        {
            public Profile Value = new Profile();
            public bool FailRead, FailWrite;
            public int Reads, Writes;
            public Profile Load()
            {
                Reads++;
                if (FailRead) throw new IOException("验收：档案读取失败。");
                return Value;
            }
            public Profile NewSeason()
            {
                Writes++;
                if (FailWrite) throw new IOException("验收：切季保存失败。");
                var next = Profile.FromJson(Value.ToJson());
                var rating = RatingOf(next);
                RatingSystem.NewSeason(rating);
                next.Ratings[Profile.DefaultRatingFormat] = rating;
                Value = next;
                return Value;
            }
        }

        IEnumerator Run()
        {
            if (!Application.isEditor)
            {
                float until = Time.realtimeSinceStartup + 30;
                while (!UnityEngine.Rendering.SplashScreen.isFinished)
                {
                    Require(Time.realtimeSinceStartup < until, "Unity 启动画面未结束。");
                    yield return null;
                }
            }
            yield return Settle();
            var routers = FindObjectsByType<ScreenRouter>(FindObjectsInactive.Include);
            Require(routers.Length == 1, "排位验收必须只有一个界面路由。");
            router = routers[0];
            legacy = router.GetComponent<BattleScreen>();
            Require(legacy != null && router.CurrentScreenId == ScreenIds.Menu && !legacy.enabled, "默认入口未进入正式主菜单。");
            var saveField = typeof(BattleScreen).GetField("saveStore", BindingFlags.Instance | BindingFlags.NonPublic);
            Require(saveField != null && saveField.GetValue(legacy) == null, "启动访问了玩家存档；SmokeRequested 必须在 Awake 之前生效。");
            Require(legacy.MenuPackErrors.Count == 0 && legacy.MenuPacks.Any(pack => pack.PackId == "original"), "正式内容加载异常。");

            foreach (int width in new[] { 1280, 1920 })
            {
                yield return Resize(width, width == 1280 ? 720 : 1080);
                router.Show(ScreenIds.Menu);
                yield return Settle();
                if (width == 1280) yield return WaitForFirstRenderedMenu();
                ValidateTextLayout(Element<VisualElement>("menu-screen"));
                Capture("menu");
                var development = router.Document.rootVisualElement.Q<Foldout>("developer-tools");
                if (development != null)
                {
                    development.value = true;
                    yield return Settle();
                    ValidateTextLayout(Element<VisualElement>("menu-screen"));
                    Capture("menu-development");
                    development.value = false;
                    yield return Settle();
                }
                Submit("open-rank");
                yield return Settle();
                Require(router.CurrentScreenId == ScreenIds.Rank && !legacy.enabled, "真实菜单排位按钮未打开正式页面。");
                Require(saveField.GetValue(legacy) == null, "排位入口访问了玩家设置存档。");
                ValidateLayout();
                Capture("rank-default");
                Submit("rank-back");
                yield return Settle();
                Require(router.CurrentScreenId == ScreenIds.Menu, "排位页返回按钮未回到菜单。");
                yield return CheckMemoryProfiles();
                yield return CheckFileProfiles();
                Submit("rank-back");
                yield return Settle();
                Require(router.CurrentScreenId == ScreenIds.Menu, "存档验收后无法返回菜单。");
                report.checks.Add(Size + "：菜单真实入口、默认内存路径和返回导航通过。");
            }
            yield return CompleteBattle();
            Require(saveField.GetValue(legacy) == null, "完整对局触及玩家设置存档。");
            report.completed = true;
        }

        IEnumerator WaitForFirstRenderedMenu()
        {
            // 新工作树首次进入 Play 时着色器可能尚未完成；只等待真实像素就绪，不替换验收截图。
            float until = Time.realtimeSinceStartup + 10;
            yield return new WaitForSecondsRealtime(2);
            yield return Settle();
            while (true)
            {
                bool ready = false;
                Texture2D texture = null;
                try
                {
                    texture = ScreenCapture.CaptureScreenshotAsTexture();
                    if (texture != null && texture.width == RequestedWidth && texture.height == RequestedHeight)
                    {
                        var pixels = texture.GetPixels32();
                        int step = Math.Max(1, pixels.Length / 65536), low = 255, high = 0, bright = 0;
                        for (int index = 0; index < pixels.Length; index += step)
                        {
                            var pixel = pixels[index];
                            int luminance = (299 * pixel.r + 587 * pixel.g + 114 * pixel.b) / 1000;
                            low = Math.Min(low, luminance);
                            high = Math.Max(high, luminance);
                            if (luminance >= 150) bright++;
                        }
                        ready = high - low >= 48 && bright >= 8;
                    }
                }
                finally { if (texture != null) Destroy(texture); }
                if (ready)
                {
                    report.checks.Add("首帧在 10 秒上限内完成真实渲染动态范围检查，均色候选帧未保存为验收截图。");
                    yield return Settle();
                    yield break;
                }
                Require(Time.realtimeSinceStartup < until, "首次菜单渲染在 10 秒内仍为均色或无效尺寸；未保存菜单验收截图。");
                yield return null;
                yield return new WaitForEndOfFrame();
            }
        }

        static PlayerRating RatingOf(Profile profile)
        {
            return profile.Ratings.TryGetValue(Profile.DefaultRatingFormat, out var value) ? value : new PlayerRating();
        }

        static Profile ProfileOf(PlayerRating rating)
        {
            var profile = new Profile();
            profile.Ratings[Profile.DefaultRatingFormat] = rating;
            return profile;
        }

        void Show(MemoryProfile memory)
        {
            router.Show(ScreenIds.Rank, new RankScreenArgs { LoadProfile = memory.Load, StartNewSeason = memory.NewSeason });
        }

        void ShowFile(string path)
        {
            Require(Within(path, report.outputDirectory), "验收存档超出本轮输出目录。");
            router.Show(ScreenIds.Rank, new RankScreenArgs
            {
                LoadProfile = () => FileSaveStore.LoadProfileSnapshot(path, true),
                StartNewSeason = () => new FileSaveStore(path).NewRatingSeason()
            });
        }

        IEnumerator CheckMemoryProfiles()
        {
            foreach (int score in new[] { int.MinValue, -35, 0, 1199, 1200, 1499, 1500, 1799, 1800, 2099, 2100, int.MaxValue })
            {
                var source = new MemoryProfile { Value = ProfileOf(new PlayerRating { Rating = score, Games = 9 }) };
                string before = source.Value.ToJson();
                Show(source);
                yield return Settle();
                CheckRating(RatingOf(source.Value));
                ValidateLayout();
                Require(source.Value.ToJson() == before && source.Writes == 0, "浏览评级改写了源档案。");
                if (score == int.MinValue || score == 2100) Capture(score == int.MinValue ? "rank-negative-boundary" : "rank-top-tier");
            }

            var qualified = new PlayerRating { Rating = 1801, Games = 10 };
            var memory = new MemoryProfile { Value = ProfileOf(qualified) };
            Show(memory);
            yield return Settle();
            CheckRating(qualified);
            Submit("rank-new-season");
            yield return Settle();
            Require(Visible(Element<VisualElement>("rank-confirmation")) && memory.Writes == 0, "打开确认层已经切季。");
            var cancel = Element<Button>("rank-cancel");
            Require(ReferenceEquals(router.Document.rootVisualElement.focusController.focusedElement, cancel), "确认层初始焦点不在取消按钮。");
            int reads = memory.Reads;
            foreach (string name in new[] { "rank-back", "rank-refresh", "rank-new-season" })
            {
                var background = Element<Button>(name);
                Require(!background.enabledInHierarchy, "确认层打开后底层按钮仍可用：" + name);
                background.Focus();
                SendSubmit(background);
            }
            yield return Settle();
            var focused = router.Document.rootVisualElement.focusController.focusedElement as VisualElement;
            Require(focused != null && Element<VisualElement>("rank-confirmation").Contains(focused), "确认层焦点逸出到背景。");
            Require(memory.Reads == reads && memory.Writes == 0 && router.CurrentScreenId == ScreenIds.Rank,
                "程序导航事件绕过了确认层底层按钮保护。");
            ValidateLayout();
            Capture("rank-confirmation");
            Cancel();
            yield return Settle();
            Require(!Visible(Element<VisualElement>("rank-confirmation")) && memory.Writes == 0, "NavigationCancel 没有无副作用关闭确认。");
            Submit("rank-new-season");
            yield return Settle();
            Submit("rank-cancel");
            yield return Settle();
            Require(memory.Writes == 0, "取消按钮触发了切季。");
            Submit("rank-new-season");
            yield return Settle();
            Submit("rank-confirm");
            yield return Settle();
            Require(memory.Writes == 1 && RatingOf(memory.Value).Season == 2, "确认没有仅调用一次 Core 切季。");
            Require(RatingOf(memory.Value).IsPlaced && RatingOf(memory.Value).Games == 0, "已定级资格未跨季保留。");
            CheckRating(RatingOf(memory.Value));
            ValidateLayout();
            Capture("rank-new-season");

            var unplaced = new PlayerRating { Rating = 1480, Games = 8 };
            RatingSystem.NewSeason(unplaced);
            unplaced.Games = 8;
            Show(new MemoryProfile { Value = ProfileOf(unplaced) });
            yield return Settle();
            Require(!unplaced.IsPlaced, "夹具意外完成定级。");
            CheckRating(unplaced);

            memory = new MemoryProfile { Value = LongHistory(), FailWrite = true };
            Show(memory);
            yield return Settle();
            CheckRating(RatingOf(memory.Value));
            CheckHistory(RatingOf(memory.Value));
            ValidateLayout();
            Capture("rank-history");
            var scroll = Element<ScrollView>("rank-history");
            Require(scroll.verticalScroller.highValue > 0, "多赛季历史没有可滚动区域。");
            Submit("rank-history-down");
            yield return Settle();
            float advanced = scroll.scrollOffset.y;
            Require(advanced > 0, "历史下一页按钮的导航提交没有推进滚动位置。");
            Submit("rank-history-up");
            yield return Settle();
            Require(scroll.scrollOffset.y < advanced, "历史上一页按钮的导航提交没有回退滚动位置。");
            scroll.scrollOffset = new Vector2(0, scroll.verticalScroller.highValue);
            yield return Settle();
            Require(scroll.scrollOffset.y > 0, "历史列表无法滚动到底部。");
            ValidateLayout();
            Capture("rank-history-scrolled");
            scroll.scrollOffset = Vector2.zero;
            yield return Settle();
            string unchanged = memory.Value.ToJson();
            Submit("rank-new-season");
            yield return Settle();
            Submit("rank-confirm");
            yield return Settle();
            Require(memory.Writes == 1 && memory.Value.ToJson() == unchanged, "失败切季修改了档案。");
            Require(!string.IsNullOrWhiteSpace(Element<Label>("rank-notice").text), "切季失败没有可见错误。");
            Require(Visible(Element<Label>("rank-confirm-error")), "确认层没有保留切季错误。");
            ValidateLayout();
            Capture("rank-save-error");
            memory.FailWrite = false;
            Submit("rank-confirm");
            yield return Settle();
            Require(memory.Writes == 2, "失败后不能重试确认。");
            CheckRating(RatingOf(memory.Value));
            memory.FailRead = true;
            Submit("rank-refresh");
            yield return Settle();
            Require(!Element<Button>("rank-new-season").enabledInHierarchy, "刷新失败后仍允许按旧快照切季。");
            Require(!string.IsNullOrWhiteSpace(Element<Label>("rank-notice").text), "刷新失败没有提示。");
            memory.FailRead = false;
            Submit("rank-refresh");
            yield return Settle();
            Require(Element<Button>("rank-new-season").enabledInHierarchy, "重新读取成功后切季仍不可用。");
            CheckRating(RatingOf(memory.Value));

            Submit("rank-new-season");
            yield return Settle();
            var discardedConfirm = Element<Button>("rank-confirm");
            int writes = memory.Writes;
            router.Show(ScreenIds.Menu);
            yield return Settle();
            SendSubmit(discardedConfirm);
            yield return Settle();
            Require(memory.Writes == writes && router.CurrentScreenId == ScreenIds.Menu, "已释放页面的确认按钮仍可切季。");
            report.checks.Add(Size + "：全段位边界、负分/int 最值、定级 9/10 与跨季资格、历史字段/滚动、取消/确认/失败重试、刷新与 Dispose 通过。");
        }

        static Profile LongHistory()
        {
            var rating = PlayerRating.FromJson("{\"games\":17,\"rating\":1490,\"peakRating\":2130}");
            for (int season = 0; season < 24; season++)
            {
                RatingSystem.NewSeason(rating);
                for (int game = 0; game < 3; game++)
                    RatingSystem.Apply(rating, new PlayerRating { Rating = 1600, Games = 12 }, game % 2 == 0 ? 1 : 0);
            }
            return ProfileOf(rating);
        }

        IEnumerator CheckFileProfiles()
        {
            string absent = Path.Combine(report.outputDirectory, "absent-" + RequestedWidth);
            Require(!Directory.Exists(absent), "缺省档案夹具路径已存在。");
            ShowFile(absent);
            yield return Settle();
            CheckRating(new PlayerRating());
            Require(!Directory.Exists(absent), "只读浏览创建了档案目录。");
            Require(!Element<ScrollView>("rank-history").Query<VisualElement>().ToList().Any(row => row.userData is RatingSeasonHistory), "空档案凭空显示历史赛季。");
            Submit("rank-refresh");
            yield return Settle();
            Submit("rank-new-season");
            yield return Settle();
            Submit("rank-cancel");
            yield return Settle();
            Require(!Directory.Exists(absent), "刷新或取消创建了档案目录/锁文件。");
            ValidateLayout();
            Capture("rank-no-profile");

            foreach (bool badUtf8 in new[] { false, true })
            {
                string folder = Path.Combine(report.outputDirectory, (badUtf8 ? "bad-utf8-" : "bad-json-") + RequestedWidth);
                Directory.CreateDirectory(folder);
                string file = Path.Combine(folder, "profile.json");
                byte[] bytes = badUtf8 ? new byte[] { 0xff, 0xfe, 0x80 } : System.Text.Encoding.UTF8.GetBytes("{\"packs\":{},\"ratings\":");
                File.WriteAllBytes(file, bytes);
                ShowFile(folder);
                yield return Settle();
                Require(!Element<Button>("rank-new-season").enabledInHierarchy, "损坏档案仍能切季。");
                Require(!string.IsNullOrWhiteSpace(Element<Label>("rank-notice").text), "严格读取失败未显示原因。");
                Submit("rank-refresh");
                yield return Settle();
                Require(File.ReadAllBytes(file).SequenceEqual(bytes), "损坏档案字节被覆盖。");
                ValidateLayout();
                Capture(badUtf8 ? "rank-utf8-error" : "rank-json-error");
                new FileSaveStore(folder).SaveProfile(ProfileOf(new PlayerRating { Rating = 1710, Games = 11 }));
                Submit("rank-refresh");
                yield return Settle();
                Require(Element<Button>("rank-new-season").enabledInHierarchy, "修复夹具后刷新未恢复。");
                CheckRating(new PlayerRating { Rating = 1710, Games = 11 });
            }

            string concurrent = Path.Combine(report.outputDirectory, "concurrent-" + RequestedWidth);
            var first = new FileSaveStore(concurrent);
            first.SaveProfile(ProfileOf(new PlayerRating { Rating = 1801, Games = 12 }));
            ShowFile(concurrent);
            yield return Settle();
            Submit("rank-new-season");
            yield return Settle();
            var latest = new FileSaveStore(concurrent).NewRatingSeason();
            var expected = Profile.FromJson(latest.ToJson());
            RatingSystem.NewSeason(RatingOf(expected));
            Submit("rank-confirm");
            yield return Settle();
            var actual = FileSaveStore.LoadProfileSnapshot(concurrent, true);
            Require(actual.ToJson() == expected.ToJson(), "确认切季覆盖了打开页面后的并发更新。");
            CheckRating(RatingOf(expected));
            CheckHistory(RatingOf(expected));
            ValidateLayout();
            Capture("rank-concurrent-update");
            report.checks.Add(Size + "：真实 FileSaveStore 缺档只读不建目录、坏 JSON/UTF-8 保留、刷新恢复及并发切季读取最新档案通过。");
        }

        void CheckRating(PlayerRating rating)
        {
            Require(Visible(Element<Label>("rank-season")) && HasNumber(Element<Label>("rank-season").text, rating.Season), "当前赛季显示与 Core 不符。");
            Require(HasNumber(Element<Label>("rank-rating").text, rating.Rating), "当前分数显示与 Core 不符。");
            Require(Element<Label>("rank-tier").text.Contains(RatingSystem.TierOf(rating.Rating).Name), "段位显示与 Core 不符。");
            Require(HasNumber(Element<Label>("rank-games").text, rating.Games), "本赛季场数显示错误。");
            Require(Element<Label>("rank-placement").text.Contains(rating.IsPlaced ? "已定级" : "定级中"), "定级提示未使用跨季 IsPlaced。");
            var tiers = RatingSystem.Tiers;
            int index = tiers.ToList().FindIndex(tier => tier.Name == RatingSystem.TierOf(rating.Rating).Name);
            string next = Element<Label>("rank-next").text;
            Require(!string.IsNullOrWhiteSpace(next), "下一段位提示为空。");
            if (index + 1 < tiers.Count)
                Require(HasNumber(next, (long)tiers[index + 1].MinRating - rating.Rating), "距下一段位分差错误或发生 int 溢出。");
            else Require(!next.Contains("还差"), "最高段位仍显示下一段位差额。");
        }

        static bool HasNumber(string text, long value)
        {
            return (text ?? "").Replace(",", "").Replace("，", "").Contains(value.ToString(CultureInfo.InvariantCulture));
        }

        void CheckHistory(PlayerRating rating)
        {
            var scroll = Element<ScrollView>("rank-history");
            var rows = new List<VisualElement>();
            scroll.Query<VisualElement>().ForEach(row => { if (row.userData is RatingSeasonHistory) rows.Add(row); });
            Require(rows.Count == rating.History.Count, "历史赛季数量不符。");
            Require(rows.Select(row => ((RatingSeasonHistory)row.userData).Season).SequenceEqual(rating.History.Reverse().Select(item => item.Season)), "历史赛季没有按最近在前排列。");
            foreach (var item in rating.History)
            {
                var row = Element<VisualElement>("rank-history-season-" + item.Season);
                var values = new List<string>();
                row.Query<TextElement>().ForEach(text => values.Add(text.text));
                string joined = string.Join(" ", values);
                Require(HasNumber(joined, item.FinalRating) && HasNumber(joined, item.PeakRating) && HasNumber(joined, item.Games)
                    && joined.Contains(item.Tier), "历史行遗漏最终分、最高分、最终段位或场数：" + item.Season);
                Require(!ReferenceEquals(row.userData, item), "历史 UI 直接暴露原档案记录。");
            }
        }

        static void SendSubmit(Button button)
        {
            using (var evt = NavigationSubmitEvent.GetPooled()) { evt.target = button; button.SendEvent(evt); }
        }

        void Cancel()
        {
            var button = Element<Button>("rank-cancel");
            button.Focus();
            using (var evt = NavigationCancelEvent.GetPooled()) { evt.target = button; button.SendEvent(evt); }
        }

        IEnumerator CompleteBattle()
        {
            Submit("open-team");
            yield return Settle();
            Require(router.CurrentScreenId == ScreenIds.Team, "返回菜单后不能进入组队。");
            var db = legacy.MenuPacks.First(pack => pack.PackId == "original");
            portraits = new PortraitProvider(router.Resources.RegularFont);
            router.Show(ScreenIds.Team, new TeamScreenArgs
            {
                Content = db, Packs = legacy.MenuPacks, Presets = new MemoryTeamPresetStore(), Portraits = portraits,
                Settings = new GameSettings { DefaultPack = db.PackId, AiLevel = AiLevel.Normal, FastPlay = true, TurnTimer = false },
                ReadClipboard = () => "", WriteClipboard = value => { }, Seed = 1350135,
                StartBattle = legacy.StartTeamBattle, NavigateToBattle = true, Return = () => router.Show(ScreenIds.Menu)
            });
            yield return Settle();
            Submit("team-auto");
            Submit("team-start");
            yield return Settle();
            Require(router.CurrentScreenId == ScreenIds.Legacy && legacy.enabled && legacy.CurrentTeamBattle != null, "组队开战按钮未进入实际对局。");
            var battle = legacy.CurrentTeamBattle;
            int steps = 0;
            while (battle.Phase != Phase.Over)
            {
                Require(++steps < 5000, "正式桥接未能结束完整对局。");
                legacy.AdvanceTeamSmoke();
                yield return null;
            }
            legacy.AdvanceTeamSmoke();
            yield return Settle();
            Capture("battle-finished");
            yield return Resize(1280, 720);
            Capture("battle-finished");
            yield return Resize(1920, 1080);
            File.WriteAllText(Path.Combine(report.outputDirectory, "battle-record.json"), battle.Record.ToJson());
            var replay = Replay.Run(db, BattleRecord.FromJson(battle.Record.ToJson()));
            Require(replay.StateHash() == battle.StateHash() && replay.Log.Select(entry => entry.Text).SequenceEqual(battle.Log.Select(entry => entry.Text)), "完整对局记录不能复现。");
            report.turns = battle.Turn;
            report.winner = battle.Winner;
            report.finalHash = battle.StateHash().ToString("x16");
            report.checks.Add("排位返回菜单后，通过实际 Team 按钮开战并用公开桥接完成整局；录像的最终 hash 和日志重放一致。");
        }
    }
}
