using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace StreamDuel.Core
{
    public sealed partial class Profile
    {
        public const int MaxMyRulesets = 20;
        readonly List<Ruleset> myRulesets = new List<Ruleset>();

        public IReadOnlyList<Ruleset> MyRulesets
        {
            get { return Array.AsReadOnly(myRulesets.Select(ruleset => ruleset.Copy()).ToArray()); }
        }

        public Ruleset GetRuleset(string name)
        {
            var builtin = Rulesets.All.FirstOrDefault(ruleset => ruleset.Id == name || ruleset.Name == name);
            if (builtin != null) return builtin;
            return myRulesets[FindRuleset(name)].Copy();
        }

        public Ruleset AddRuleset(string name, Ruleset ruleset, ContentDb db)
        {
            var copy = PrepareRuleset(name, ruleset, db);
            RequireFreeName(copy.Name);
            if (myRulesets.Count >= MaxMyRulesets) throw new KeyError("我的赛制最多保存 20 个，请先删除不需要的赛制。");
            myRulesets.Add(copy);
            return copy.Copy();
        }

        public Ruleset RenameRuleset(string name, string newName)
        {
            RequireCustomName(name);
            int index = FindRuleset(name);
            string cleaned = TextInput.Clean(newName, TextInputKind.RulesetName).Text;
            RequireCustomName(cleaned);
            if (string.Equals(name, cleaned, StringComparison.Ordinal)) return myRulesets[index].Copy();
            RequireFreeName(cleaned);
            var copy = myRulesets[index].Copy();
            copy.Name = cleaned;
            RulesetCode.CheckJsonSize(copy.ToJson());
            myRulesets[index] = copy;
            return copy.Copy();
        }

        public void DeleteRuleset(string name)
        {
            RequireCustomName(name);
            myRulesets.RemoveAt(FindRuleset(name));
        }

        internal static Ruleset PrepareRuleset(string name, Ruleset ruleset, ContentDb db)
        {
            if (ruleset == null) throw new ArgumentNullException(nameof(ruleset));
            var copy = ruleset.Copy();
            copy.Name = TextInput.Clean(name, TextInputKind.RulesetName).Text;
            RequireCustomName(copy.Name);
            copy.Validate(db);
            RulesetCode.CheckJsonSize(copy.ToJson());
            return copy;
        }

        int FindRuleset(string name)
        {
            int index = myRulesets.FindIndex(ruleset => string.Equals(ruleset.Name, name, StringComparison.Ordinal));
            if (index < 0) throw new KeyError("找不到我的赛制，请使用列表中的完整名称。");
            return index;
        }

        void RequireFreeName(string name)
        {
            RequireCustomName(name);
            if (myRulesets.Any(ruleset => string.Equals(ruleset.Name, name, StringComparison.Ordinal)))
                throw new KeyError("赛制名称已存在，请改用其他名称。");
        }

        static void RequireCustomName(string name)
        {
            if (Rulesets.All.Any(ruleset => ruleset.Id == name || ruleset.Name == name))
                throw new KeyError("内置预设不能覆盖、改名或删除；请复制后使用新的名称保存。");
        }

        void WriteRulesets(StringBuilder text)
        {
            if (myRulesets.Count == 0) return;
            text.Append(",\"myRulesets\":[");
            for (int i = 0; i < myRulesets.Count; i++)
            {
                if (i > 0) text.Append(',');
                text.Append(myRulesets[i].ToJson());
            }
            text.Append(']');
        }

        void ReadRulesets(Dictionary<string, object> root)
        {
            if (!root.TryGetValue("myRulesets", out var value)) return;
            if (!(value is List<object> list)) throw new FormatException("档案 myRulesets 必须是数组。");
            if (list.Count > MaxMyRulesets) throw new FormatException("档案中的我的赛制超过 20 个。");
            foreach (var item in list)
            {
                var ruleset = RulesetJson.ReadValue(item);
                if (!string.Equals(ruleset.Name, TextInput.Clean(ruleset.Name, TextInputKind.RulesetName).Text, StringComparison.Ordinal))
                    throw new FormatException("档案赛制名称不是清理后的有效名称。");
                RulesetCode.CheckJsonSize(ruleset.ToJson());
                try
                {
                    RequireFreeName(ruleset.Name);
                    // 存档没有绑定内容库；季节可能属于另一个包，启用时再由 Validate(db) 检查。
                    if (ruleset.BestOf != 1 && ruleset.BestOf != 3 && ruleset.BestOf != 5)
                        throw new KeyError("赛制 bestOf 只能是 1、3 或 5。");
                    if (ruleset.TurnSeconds < 0 || ruleset.BankSeconds < 0)
                        throw new KeyError("赛制的回合时间和总时间不能为负数。");
                    BattleModifiers.Normalize(ruleset.Modifiers);
                }
                catch (KeyError ex) { throw new FormatException("档案中的赛制无效：" + ex.Message, ex); }
                myRulesets.Add(ruleset);
            }
        }
    }
}
