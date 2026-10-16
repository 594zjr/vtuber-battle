# 性能基准

原有快照基准入口和输出字段保持不变，运行方法见 `docs/performance.md`。T-117 的整局基准使用独立模式，必须以 Release 运行：

```powershell
dotnet run -c Release --project Tools/Bench -- --full-games --out bench_out/t117-before
# 完成优化后，在相同机器和运行配置下运行：
dotnet run -c Release --project Tools/Bench -- --full-games --out bench_out/t117-after --compare bench_out/t117-before
# 若 before 仍在采集，after 先独立运行，双方完成后再核验，不重跑对局：
dotnet run -c Release --project Tools/Bench -- --full-games --out bench_out/t117-after
dotnet run -c Release --project Tools/Bench -- --full-games --verify-only --out bench_out/t117-after --compare bench_out/t117-before
```

输出目录必须为空或不存在，工具不会覆盖此前基线。`--games 1..200` 可缩小调试规模；省略时每个组合 200 局，共 **2400 局**。缩小规模会明确标为非正式验收，两次比较必须使用相同规模。`--full-games --help` 查看用法。此模式不接受 `--quick`，也不修改旧 `bench_out/bench.json`。

## 固定赛程

内容包顺序为 `fan_asoul_vr`、`fan_holo_niji`、`original`、`original_wave2`，每包按 `Normal`、`Hard`、`Expert` 顺序进行同等级自战。每组 gameIndex 从 0 到 199；零基 caseId 为 `packIndex * 600 + levelIndex * 200 + gameIndex`，对战种子为 `1170001 + caseId`。即使缩小样本数也不压缩 caseId。

每局从按主播 id 的 ordinal 排序开始，用独立项目 `Rng(seed)` 依次为双方各复制完整候选数组、做 Fisher–Yates 打乱后取前六名。两队可以重叠，同队没有重复，所有阵容通过 `TeamCheck.Validate`。阵容生成 RNG 与对战 RNG 分开；对战重新以同一种子初始化。名字固定为“基准甲”“基准乙”，无赛季、禁用、首发选择或修饰器。每回合先调用 side 0 的 AI，再调用 side 1，随后真实结算；补位按 0、1 顺序处理。专家使用当时 Core 的默认预算，写入赛程以免默默更换工作量。

T-117 的 100 局固定验收子集为每个包/等级组合的 gameIndex 0..7（96 局），再加 `original/Normal` 的 8..11。`FullGameWorkload.cs` 定义全部赛程，生成基线和测试必须保持这个协议。

整局工具沿用进程的 `CultureInfo.CurrentCulture`，前后采集须使用相同区域设置；本轮为 `zh-CN`，与固定夹具的 InvariantCulture 使用相同的小数点格式。原有日志部分数字格式依赖区域设置，因此不保证不同区域设置之间的日志字节相同。

## 时间、分配与复制口径

串行执行且不跨异步线程。每组先完成一局不计入结果的预热，再逐局取 `Stopwatch` 和 `GC.GetAllocatedBytesForCurrentThread` 差值。整局包括 Battle 构造、双方 AI 行动/补位、真实结算及正常日志、结构化事件和录像的内存成本；内容加载、赛程生成、结果哈希、序列化、磁盘输出与比较不计入单局测量。工具不主动触发 GC，不使用计时决定行为或预算。

每回合分配采用 **全部计时对局的分配字节总和 / 实际 Turn 事件总数**，包含构造开销，按真实回合数加权。不是逐局比值的简单平均，也不是仍存活对象的内存大小。另给出每局均值、P95、最大时间，以及构造、AI 决策、结算三部分的时间和分配，后两部分包含补位阶段。

三个线程本地计数各自取每局前后差：`CloneCount` 仅计公共 `Battle.Clone()`，`HardCopyCount` 计 `CopyForHardAi()`，`SimulationCloneCount` 计优化后的内部模拟副本。不能把公共 Clone 数量减少理解为复制成本全部消失。统计钩子不参与状态、日志、录像或 RNG；优化前后需使用相同计数口径。

时间受到其他进程、GC 和调度影响。T-117 为缩短完整 2400 局的采集等待，冻结优化前和优化后引擎可以与验证任务在隔离进程并行；各进程内部仍串行执行，每局的当前线程分配不含其他进程，程序集、内容和输出目录均独立。wallclock 只作为并发负载参考，分配量是此次 20% 改善的主要验收指标。输出与比较在计时区间之外，但磁盘操作和额外序列化仍可能间接影响后续 GC、缓存和时间，不能宣称完全隔绝了机器噪声。冻结 before 的原始 measurement 数值保留；最终 verification 报告统一说明并发采集口径。

## 产物与严格比较

- `schedule.json`：协议、规模、内容指纹、默认专家预算、名字及全部种子/阵容，包含完整可重建输入。
- `games/NNNN.state.txt`、`.log.json`、`.record.json`：每局最终 StateHash、完整日志（包括快照数组）和 `BattleRecord.ToJson()` 原文。状态为小写 16 位十六进制；日志使用 `System.Text.Json` 的 `IncludeFields=true` 紧凑编码。文件均为 UTF-8 无 BOM，最后追加 LF。
- `results.json`：全部对局成功后才写入的确定性终局清单，包括胜方、实际回合数和日志/录像 SHA-256。
- `measurement.json`：环境、Core 程序集 SHA-256、逐组/逐局耗时、分配量及复制计数。每完成一组就更新，`Complete=true` 才表示全程完成；机器数据与优化会改变这个文件，它不参与结果字节一致性比较。

`--compare` 先比较赛程，再逐局比较 **完整原始字节**，最后比较确定性清单；不是只看摘要。首个差异写入 `failure.txt` 并以非零退出，保留本次已经写入的产物便于定位。不完整的基线（缺少 `results.json`）不会用于比较。读写或对局异常同样非零退出，不把部分对局当作完整验收。

`--verify-only` 使用两个已有且不同的完成目录，不载入内容库、不调用 AI，也不重新结算。仅允许正式规模：两份 measurement 均须 `Complete=true`、每组 200 局、12 个固定组合，schedule/results/measurement 的 case id 必须有序且完整覆盖 0..2399，逐局目录必须恰有 7200 个对应文件。核验每个 state 和日志/录像 SHA 与各自清单一致，再逐字节比较两份赛程、结果清单和全部原文。成功在 after 目录另写 `verification.json`，失败非零退出，不改写两边原文。

采集优化前数据时可以把原始 Core 源码和资源冻结在忽略目录，使用独立项目引用；只移植同样的三个诊断计数，不同步任何优化。这样长时间基线采集不受正在编辑的产品源码影响。T-117 本地冻结位于 `Tools/Bench/obj/t117-baseline`，原始 revision 和各文件 SHA 另存于基线清单。所有基准产物置于忽略目录，不提交 `bin`、`obj` 或 `bench_out`。
