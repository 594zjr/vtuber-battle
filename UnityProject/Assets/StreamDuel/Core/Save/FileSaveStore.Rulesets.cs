namespace StreamDuel.Core
{
    public sealed partial class FileSaveStore
    {
        public Ruleset SaveRuleset(string name, Ruleset ruleset, ContentDb db)
        {
            // 先验证独立副本，再打开档案锁；非法导入不能触碰档案或临时文件。
            var prepared = Profile.PrepareRuleset(name, ruleset, db);
            using (OpenLock(".profile.lock"))
            {
                var profile = LoadProfileStrict();
                var saved = profile.AddRuleset(prepared.Name, prepared, db);
                WriteAtomic("profile.json", ".profile.json.tmp", profile.ToJson());
                return saved;
            }
        }

        public Ruleset ImportRuleset(string code, ContentDb db)
        {
            var ruleset = RulesetCode.Decode(code, db);
            return SaveRuleset(ruleset.Name, ruleset, db);
        }

        public Ruleset RenameRuleset(string name, string newName)
        {
            using (OpenLock(".profile.lock"))
            {
                var profile = LoadProfileStrict();
                var renamed = profile.RenameRuleset(name, newName);
                WriteAtomic("profile.json", ".profile.json.tmp", profile.ToJson());
                return renamed;
            }
        }

        public void DeleteRuleset(string name)
        {
            using (OpenLock(".profile.lock"))
            {
                var profile = LoadProfileStrict();
                profile.DeleteRuleset(name);
                WriteAtomic("profile.json", ".profile.json.tmp", profile.ToJson());
            }
        }
    }
}
