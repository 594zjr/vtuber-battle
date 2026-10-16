using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using StreamDuel.Core;
using UnityEngine.UIElements;

namespace StreamDuel.Game
{
    public sealed partial class TeamScreen
    {
        IReadOnlyList<Ruleset> rulesetPresets;
        string selectedRulesetId;
        DropdownField rulesetField;

        void BuildRulesets()
        {
            rulesetPresets = Rulesets.All;
            selectedRulesetId = options.RulesetId;
            int selectedIndex = selectedRulesetId == null ? 0
                : rulesetPresets.ToList().FindIndex(item => item.Id == selectedRulesetId) + 1;
            if (selectedRulesetId != null && selectedIndex == 0)
                throw new ArgumentException("赛制不存在：" + selectedRulesetId);
            var row = root.Q<VisualElement>("team-selectors");
            var choices = new List<string> { "沿用单局设置" };
            choices.AddRange(rulesetPresets.Select(item => item.Name + "〔" + item.Id + "〕"));
            rulesetField = Dropdown(row, "team-ruleset", "赛制", UniqueChoiceLabels(choices));
            rulesetField.index = selectedIndex;
            Bind(rulesetField, () =>
            {
                selectedRulesetId = rulesetField.index > 0 ? rulesetPresets[rulesetField.index - 1].Id : null;
                try { options.RememberRuleset?.Invoke(selectedRulesetId); }
                catch (Exception ex) { storageStatus.text = "当前赛制已生效，但尚未保存：" + ex.Message; }
                Refresh();
            });
        }

        Ruleset SelectedRuleset()
        {
            if (selectedRulesetId == null) return null;
            var definition = rulesetPresets.First(item => item.Id == selectedRulesetId);
            // 赛季属于本次组队配置；不能改写内置预设或其它页面的副本。
            var result = Ruleset.FromJson(definition.ToJson());
            result.Season = seasonId;
            result.Validate(db);
            return result;
        }

        void RefreshRulesetHelp()
        {
            if (rulesetField == null) return;
            if (selectedRulesetId == null)
            {
                rulesetField.tooltip = "单局、选择首发；沿用原计时开关。";
                return;
            }
            var rule = rulesetPresets.First(item => item.Id == selectedRulesetId);
            var parts = new List<string>
            {
                "最多 " + rule.BestOf.ToString(CultureInfo.InvariantCulture) + " 局",
                rule.BanOne ? "每局禁用" : "无禁用",
                rule.LeadPick ? "选择首发" : "按队伍顺序首发"
            };
            if (rule.TurnSeconds > 0 || rule.BankSeconds > 0)
                parts.Add("每阶段 " + rule.TurnSeconds.ToString(CultureInfo.InvariantCulture) + " 秒，时间池 "
                    + rule.BankSeconds.ToString(CultureInfo.InvariantCulture) + " 秒/局");
            else parts.Add("不计时");
            if (rule.Modifiers != null && rule.Modifiers.Count > 0) parts.Add("启用 " + rule.Modifiers.Count + " 项修饰器");
            rulesetField.tooltip = string.Join(" · ", parts);
        }
    }
}
