# 内容包检查工具

在仓库根目录运行：

```powershell
dotnet run --project Tools/PackCheck
dotnet run --project Tools/PackCheck -- --pack original
dotnet run --project Tools/PackCheck -- --write-docs
```

默认检查全部内容包，`--pack` 只检查指定文件名，不包含 `.json`。工具直接读取当前工作树的源 JSON；从子目录或使用工程绝对路径启动也使用该工程所在的仓库。

`[错误]` 会使进程返回 1；`[提醒]` 不影响退出码。检查先由 `ContentDb.FromJson()` 执行引擎内建校验，再检查简介、同人声明、显示文字中的敏感词和未使用技能。简介按 Unicode 字符计数（UTF-16 代理对算一个字符），包含标点和空格，不超过 40 字。

敏感词在 `sensitive_words.txt` 中每行一个，空行和以 `#` 开头的行忽略。只对 `fan_` 内容包提醒；主播名不检查，其余显示文字采用内容包改名后的值，包含继承的规则说明。提醒只指出需要人工核对的文案，不会修改原始内容或认定存在真实事件。

`--write-docs` 仅在所选内容包全部无错误时更新 `docs/roster/<packId>.md`；提醒不会阻止生成。名单由当前内容包和规则数据生成，技能效果文案来自 `Describe.Effect()`。修改数据后请重新生成并提交文档，不手工改生成内容。
