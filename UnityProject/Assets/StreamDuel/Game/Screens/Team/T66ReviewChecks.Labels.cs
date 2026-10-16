using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using StreamDuel.Core;
using UnityEngine;
using UnityEngine.UIElements;

namespace StreamDuel.Game
{
    internal static partial class T66ReviewChecks
    {
        public static IEnumerator RunLabels(ScreenRouter router, BattleScreen legacy)
        {
            var first = LabelContent("a〕〔b", "甲", "第一包声明：应可切换到另一包。");
            var second = LabelContent("b", "甲〔a〕", "第二包声明：当前选择必须对应此包。");
            const string firstSeason = "a〕〔b", secondSeason = "b";
            first.Seasons.Clear();
            second.Seasons.Clear();
            string[] selected = second.Streamers.Take(6).Select(item => item.Id).ToArray();
            second.Seasons.Add(firstSeason, new SeasonDef
            {
                Name = "甲", Banned = new List<string> { selected[0] }
            });
            second.Seasons.Add(secondSeason, new SeasonDef { Name = "甲〔a〕" });
            first.Validate(); second.Validate();
            LabelRequire(TeamCheck.Validate(second, firstSeason, selected).Count > 0
                && TeamCheck.Validate(second, secondSeason, selected).Count == 0,
                "赛季标签夹具必须在第一赛季非法、第二赛季合法。");
            TeamBattleRequest captured = null;
            router.Show(ScreenIds.Team, new TeamScreenArgs
            {
                Content = first, Packs = new[] { first, second }, Policy = PackPolicy.Empty,
                Settings = new GameSettings { DefaultPack = first.PackId, AiLevel = AiLevel.Normal },
                Presets = new MemoryTeamPresetStore(), Seed = 660066,
                StartBattle = request => captured = request, NavigateToBattle = false,
                Return = () => router.Show(ScreenIds.Menu)
            });
            yield return null;
            yield return new WaitForEndOfFrame();
            LabelRequire(router.CurrentScreenId == ScreenIds.Team && !legacy.enabled,
                "标签验收必须由正式路由构建实际组队页。");
            var pack = LabelElement<DropdownField>(router, "team-pack");
            LabelRequire(pack.choices.Count == 2, "包标签验收应保留两个独立包。");

            // 两个原始标签都是“甲〔a〕〔b〕”；直接选择第二项，不能只检查生成的字符串。
            pack.index = 1;
            yield return null;
            LabelRequire(pack.index == 1 && LabelElement<Label>(router, "team-pack-notice").text == second.PackNotice,
                "显示标签冲突使第二包无法选中，或实际包声明仍来自第一包。");
            LabelRequire(pack.choices.Distinct(StringComparer.Ordinal).Count() == 2,
                "内容包最终显示标签没有唯一化。");

            LabelSubmit(router, "team-clear");
            foreach (string id in selected) LabelSubmit(router, "team-pick-" + id);
            LabelSubmit(router, "team-start");
            LabelRequire(captured != null && ReferenceEquals(captured.Content, second)
                && captured.Team.PackId == second.PackId && captured.Team.Ids.SequenceEqual(selected),
                "第二包的真实开战按钮仍交接了错误内容库或阵容。");

            var season = LabelElement<DropdownField>(router, "team-season");
            LabelRequire(season.choices.Count == 3, "应显示不限赛季及两个独立赛季。");
            season.index = 1;
            yield return null;
            LabelRequire(!LabelElement<Button>(router, "team-start").enabledInHierarchy,
                "第一赛季的真实禁用规则没有生效。");
            foreach (string error in TeamCheck.Validate(second, firstSeason, selected))
                LabelRequire(LabelElement<Label>(router, "team-errors").text.Contains(error),
                    "第一赛季缺少 Core 的实际校验提示。");
            captured = null;
            season.index = 2;
            yield return null;
            LabelRequire(season.index == 2 && LabelElement<Button>(router, "team-start").enabledInHierarchy,
                "显示标签冲突使第二赛季没有切换，或仍使用第一赛季的禁用规则。");
            LabelRequire(season.choices.Distinct(StringComparer.Ordinal).Count() == 3,
                "赛季最终显示标签没有唯一化。");
            LabelSubmit(router, "team-start");
            LabelRequire(captured != null && ReferenceEquals(captured.Content, second)
                && captured.SeasonId == secondSeason && captured.Team.Ids.SequenceEqual(selected),
                "第二赛季的真实开战回调没有传递正确赛季和阵容。");
            yield return new WaitForEndOfFrame();
        }

        static ContentDb LabelContent(string id, string name, string notice)
        {
            var rules = Resources.Load<TextAsset>("StreamDuelRules");
            var pack = Resources.Load<TextAsset>("Packs/original");
            LabelRequire(rules != null && pack != null, "标签验收缺少现有规则或原创资源。");
            // 独立解析两份内存数据；不修改正式内容库或资源文件。
            var db = ContentDb.FromJson(rules.text, pack.text, PackPolicy.Empty);
            db.PackId = id; db.PackName = name; db.PackNotice = notice;
            return db;
        }

        static T LabelElement<T>(ScreenRouter router, string name) where T : VisualElement
        {
            var element = router.Document.rootVisualElement.Q<T>(name);
            LabelRequire(element != null, "标签验收缺少真实 UI 节点：" + name);
            return element;
        }

        static void LabelSubmit(ScreenRouter router, string name)
        {
            var button = LabelElement<Button>(router, name);
            LabelRequire(button.visible && button.enabledInHierarchy, "标签验收不能提交禁用按钮：" + name);
            button.Focus();
            using (var evt = NavigationSubmitEvent.GetPooled())
            {
                evt.target = button;
                button.SendEvent(evt);
            }
        }

        static void LabelRequire(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
