# T-117 优化前完整对局基线

`t117-perf-baseline.zip` 固定于优化前提交 `751402cb67991d3131b20825d0adf9f842b7c984`，SHA-256 为 `5bf88b4aae78b9229f0cb081c055edb44ccfe5c57561966c3710997b53519afd`。生成前复制该提交的 72 个 Core C# 源文件、规则及四个包，生成器只编译这一独立副本；后续 Core 优化不参与生成。临时生成器和逐文件源摘要保存在开发工作树的 `Engine.Tests/obj/t117-baseline/`，不提交。

冻结源清单 `source-sha256.tsv` 的 SHA-256 为 `7d6033fd5920ec350dacb6e99a27b6a91c11acba18fb7365d7761c3f665e57a3`；压缩包清单和测试均固定此值。

包含 100 局：包顺序 `fan_asoul_vr`、`fan_holo_niji`、`original`、`original_wave2`，每包 Normal、Hard、Expert 各取前 8 局，另外取 original / Normal 的 gameIndex 8～11（第 9～12 局）。对局编号为 `packIndex * 600 + levelIndex * 200 + gameIndex`，种子为 `1170001 + caseId`，对应整局基准 2400 局的固定子集。

组队先按 ordinal 排序主播 id，使用同一个独立 `Core.Rng(seed)` 连续对双方各一份数组进行 Fisher–Yates 洗牌，各取前六人。玩家名固定为“基准甲”“基准乙”，不启用禁用、首发或修饰器。同级对战每回合依次调用 0 方、1 方 AI，再结算；补位按 0、1 方顺序。所有对局真实运行至 Over，未提前截断。

压缩包内每局保留终局哈希、完整日志 JSON、完整录像 JSON 的 UTF-8 无 BOM 原字节，均带末尾 LF。日志使用 `System.Text.Json` 的 `IncludeFields=true`，包括每条 Turn、Node、Kind、Side、Text、SnapActive、SnapHp；录像使用 `BattleRecord.ToJson()`。`manifest.json` 列出每局种子、包、难度、回合数、胜方、内容指纹及原文 SHA-256。

测试逐字节比较所有 100 局的三个产物，并固定整个压缩包摘要。测试没有生成或更新基线的开关；性能优化不得重生成预期值来掩盖行为变化。后续有意规则迁移应保留此历史基线并按项目版本约定另行处理。
