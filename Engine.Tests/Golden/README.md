# 对战日志基线

`GoldenTests` 自动读取当前工作树 `Resources/Packs/` 的全部 JSON 内容包，每包使用固定种子 0～9，双方行动和补位都使用普通难度。主播按 ordinal id 排序，再按种子轮换；每队六位，队内不重复，不足十二位时允许两队重叠。不启用禁用或首发选择。

每局保存为 `<内容包文件名>/seed-<十位种子>.txt`。第一行是包 id、种子、难度和双方阵容；后续每行对应一个完整 `Battle.Log` 条目，包括 `Turn`、`Side`、`Node`、`Text`、`Kind`、`SnapActive` 和 `SnapHp`。文件使用 UTF-8 无 BOM 和 LF 换行；生成对局时固定 invariant culture。测试逐字比较完整文本，不忽略空白或日志快照。

正常执行 `dotnet test Engine.Tests --filter FullyQualifiedName~GoldenTests` 只读取基线；缺文件即失败，不会自动建立或修复。新增内容包会自动加入测试，因此也必须提交它的十份基线。基线始终读写编译本测试的源码工作树，不写入 `bin/` 副本。

仅在有意改变规则、内容或日志行为，或新增内容包时更新；必须在 PR 描述中说明原因并审阅基线差异。从仓库根目录执行：

```powershell
$env:SD_UPDATE_GOLDEN = '1'
try {
    dotnet test Engine.Tests --filter FullyQualifiedName~GoldenTests
} finally {
    Remove-Item Env:SD_UPDATE_GOLDEN -ErrorAction SilentlyContinue
}
git diff -- Engine.Tests/Golden
dotnet test Engine.Tests --filter FullyQualifiedName~GoldenTests
```

只有环境变量的值精确等于 `1` 时才会写文件。更新后必须清除该变量，再跑普通测试验证；不要用自动更新掩盖意外回归。
