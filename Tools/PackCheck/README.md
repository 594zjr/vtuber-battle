# 内容包检查工具

在仓库根目录运行：

```powershell
dotnet run --project Tools/PackCheck
dotnet run --project Tools/PackCheck -- --pack original
dotnet run --project Tools/PackCheck -- --pack-dir ./custom-packs --list-packs
dotnet run --project Tools/PackCheck -- --pack-dir ./custom-packs --pack my_pack
dotnet run --project Tools/PackCheck -- --write-docs
```

默认检查全部内置内容包；`--pack-dir` 额外加载指定文件夹中的 JSON，不递归，目录不存在时没有外部包。相对目录以当前工作目录为基准。`--pack` 按包 id 选择，仍兼容内置文件名（不含 `.json`）；外部文件名可以与包 id 不同。工具直接读取当前工作树的内置源 JSON；从子目录或使用工程绝对路径启动也使用该工程所在的仓库。

`--list-packs` 列出内置与外部包的 id、名称、来源和文件名，然后退出，不生成名单。外部包与成功加载的内置包 id 完全相同时，提示并保留内置包；每个目录中重复 id 和加载失败的文件分别报告错误。单个文件出错不会阻止加载其他文件；指定 `--pack` 时只检查所选包，未指定时检查全部。加载器返回的已验证内容用于检查和生成；工具另保留原有的严格 JSON 语法检查，拒绝非标准数字或转义。

`[错误]` 会使进程返回 1；`[提醒]` 不影响退出码。检查先由 `ContentDb.FromJson()` 执行引擎内建校验，再检查简介、同人声明、显示文字中的敏感词和未使用技能。简介按 Unicode 字符计数（UTF-16 代理对算一个字符），包含标点和空格，不超过 40 字。

内容包可在 `skills`、`personas`、`fields`、`statuses` 对象中新增定义，格式与共用规则文件中的同名对象一致，详见 [设计文档第 6.3 节](../../docs/design.md)。工具检查合并后的完整内容，包括本包新增技能是否被普通技能槽或第五技能使用；未使用时同样给出提醒。同类别的 id 与共用规则重复会报告错误，包内定义不会影响其他包。

敏感词在 `sensitive_words.txt` 中每行一个，空行和以 `#` 开头的行忽略。只对 `fan_` 内容包提醒；主播名不检查，其余显示文字采用内容包改名后的值，包含继承的规则说明。提醒只指出需要人工核对的文案，不会修改原始内容或认定存在真实事件。

`--write-docs` 仅在所选内容包全部无错误时更新 `docs/roster/<packId>.md`；提醒不会阻止生成。名单由当前内容包和规则数据生成，技能效果文案来自 `Describe.Effect()`。修改数据后请重新生成并提交文档，不手工改生成内容。

包 id 可用于精确选择，包括中文或点号；生成名单时，为保证输出路径安全，另要求 id 只含英文字母、数字、下划线和连字符，不能是 Windows 保留文件名，且名单文件名不区分大小写时不能重名。该文件名限制不影响加载、列出和普通检查。

名单在本包定义的技能和人设名称旁显示 `〔本包独有〕`；本包人设经 `personaName/personaDesc/personaStatus` 复制后仍保留标记。仅给共用技能或共用人设改名不会添加标记。省略四个扩展对象或提供空对象时，名单输出保持原样。
