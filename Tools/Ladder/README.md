# 天梯模拟

从仓库根目录运行（.NET 8）：

```powershell
dotnet run --project Tools/Ladder -- --pack original --teams 64 --rounds 10 --seed 1 --out ./ladder_out
dotnet run --project Tools/Ladder -- --pack original --teams 16 --rounds 3 --seed 1 --season s1 --level hard --out ./ladder_out
```

默认 64 支队伍、10 轮、种子 1、普通难度，输出目录为当前工作目录的 `ladder_out`。`--pack` 选择内置包 id，也兼容内置文件名；`--season` 可省略。`--help` 查看参数范围。`--level` 同时控制行动和补位策略。

## 阵容与配对

- 每队 6 位且不重复，最终通过 `TeamCheck.Validate`。前 `floor(teams / 2)` 队由 `TeamBuilder.Suggest` 自动组队，第 `i` 队（从 0 开始）的组队种子为 `unchecked(seed + i)`；其余队伍从按 id 排序的名单开始，用指定种子的独立 `System.Random` 打乱候选，再有限回溯满足赛季禁用和所有限制池。奇数队伍时随机阵容多一队。无合法六人阵容时报告中文错误。两种阵容随机数均与每场对战的 `Battle.Rng` 分离。
- 阵容是有序的六个号位，首发和第六位会影响对战。不同参赛队允许抽到同一阵容，仍保留独立的 `team_id`；不要求内容包提供足够多种不同组合。
- 每局胜 1 分、平 0.5 分、负 0 分。每轮开始按积分降序、队号升序排列；先为最高分未配对队伍选择交手次数最少、积分最接近的对手，同分按队号。整轮配对先冻结，再依次比赛。重复对局无法避免时仍可继续任意轮数。
- 奇数队伍时，优先让轮空次数最少、积分最低的队伍轮空，同分取队号较大的。轮空获得 2 分，但不计胜场、对局、出场或 Elo。
- 每对打两局，第二局交换双方位置，不改变队内号位，也不覆盖 Core 的先手规则。全赛事第 `g` 局（从 0 开始）的种子为 `unchecked(seed + g)`，两局使用不同种子。每场都通过 Core 结算至结束。

## Elo 和出场口径

初始 Elo 为 1500。每对两局共用赛前期望 `E = 1 / (1 + 10^((对手 Elo - 本方 Elo) / 400))`；每局 K=24，两局合计变化为 `24 × (两局总得分 - 2E)`。双方增减互为相反数；同实力各赢一局或两局均平不会因先处理哪局而产生净变化。全体参赛队的 Elo 总和保持 `1500 × 队伍数`。

主播的出场加权 Elo 为 `Σ(参赛阵容最终 Elo × 该主播在此阵容实际上场局数) / 总上场局数`。一局内多次换入只算一次；开场、主动换人和补位均算上场，立即被击倒也算。未上场的后排只记 `roster_games`，不参与加权；从未上场者的 `weighted_elo` 为空。该值反映抽样阵容及所选 AI 下的结果，不等同于主播单独贡献。

## 输出

输出为 UTF-8、无 BOM、LF 换行，数值用固定小数点。每次成功运行覆盖输出目录中的以下两个文件；不会修改规则或内容包。

- `teams.csv`：按 Elo 降序、积分降序、队号升序排序。`slot1`～`slot6` 为有序主播 id；`games/wins/draws/losses` 为真实对局统计，`byes` 为轮空次数。
- `streamers.csv`：按出场加权 Elo 降序、id 升序排序，未上场者在最后。`appearances` 是实际上场局数，`roster_games` 是入选阵容局数，`wins/draws/losses` 只统计实际上场对局。`damage_dealt/kos/turns_active` 累加自 `BattleSummary`；`turns_active` 仅统计到达回合结束节点时的在场次数，可能不包含提前终局的最后一回合。

控制台打印两份榜单的前十名。同一引擎与内容版本、同一组参数的两份 CSV 和控制台输出完全一致；输出不包含时间或绝对路径。CSV 排名使用未舍入值，展示的 Elo 保留六位小数。

验收规模为 `--teams 16 --rounds 3`，共 48 局；可将输出写入 `Tools/Ladder/obj/` 下以免生成文件混入提交。

自动验收：`pwsh -NoProfile -File Tools/Ladder/verify.ps1`（PowerShell 7）。脚本构建 Release，检查两次 CSV 与控制台字节一致、耗时、赛季合法性、积分和 Elo 守恒、登场统计、奇数轮空、困难模式和无效参数，并将日志及报告保存在 `Tools/Ladder/obj/verification/` 的独立子目录，不删除已有结果。

## 专家预算比较（T-108）

`--compare-expert` 是独立模式，不修改前述天梯默认行为。使用 Release 构建，计时期间不要并行运行其它实验：

```powershell
dotnet run -c Release --project Tools/Ladder -- --compare-expert --compare-mode paired --games 300 --seed 1 --out Tools/Ladder/bin/expert-paired
dotnet run -c Release --project Tools/Ladder -- --compare-expert --compare-mode hard --games 300 --seed 1 --out Tools/Ladder/bin/expert-hard
dotnet run -c Release --project Tools/Ladder -- --compare-expert --compare-mode both --games 300 --budget 128 --workers 4 --seed 1 --out Tools/Ladder/bin/expert-both
```

默认模式为 `paired`、`games=300`、种子 1、预算为 Core 的 `ExpertBudget.DefaultMaxNodes`（当前 128），输出目录默认 `ladder_expert_out`。`--budget` 可取 0～2147483647；无限专家使用 `ExpertBudget.Unlimited`。比较固定使用原创包，显式 `--pack original` 也可；不接受 `--teams`、`--rounds`、`--level`、`--season`。双方均无禁用、首发选择或规则修饰器。

`--workers` 可取 1～16，默认 1。样本之间可以并行，每个样本内仍按下述顺序完成全部对局。各样本单独持有对战、节点计数和耗时，结束后按样本编号合并并重排全局 game id；改变工作线程数不会改变 `comparison.json` 或 `games.csv`。工作线程数只记在 `timings.json`，不参与确定性报告。

`--games` 始终表示配对样本（fixture）数量，而非总场数。每个样本包含有序的两支六人队伍和一个对战种子，先生成全部样本，再开始比赛。名单按 id 的 ordinal 顺序排列，使用独立的项目 `Rng(seed)` 逐队做 Fisher–Yates 打乱并取前六位，再从同一随机流抽取完整 uint 对战种子；每队都通过 `TeamCheck.Validate`。同队无重复、两方可以有相同成员。阵容生成不使用系统随机数或时间，也不受专家搜索消耗影响。所有比较模式使用完全相同的样本生成方式。

- `paired`：每个样本打两局，队伍、队内号位和种子保持原样；第一局预算专家在 side 0、无限专家在 side 1，第二局仅交换专家策略。默认总计 **600 场**，两种专家各在 side 0、side 1 打 **300 场**。各自得分率为 `(胜 + 0.5 × 平) / 600`，另列相对 50% 的百分点变化。这里的“各 300”指各侧场数，不能解读为总共只有 300 场。
- `hard`：每个样本让预算专家和无限专家分别对共同的困难电脑打一局，两局使用相同队伍、种子及专家所在位置。专家位置按样本交替，第一份在 side 0、第二份在 side 1；默认每种专家 **300 场**，各侧 150 场。直接报告各自胜率、得分率，以及 `无限专家胜率 − 预算专家胜率` 的下降百分点。负数表示预算专家更高；是否不超过 3 个百分点按胜场整数差判定，避免浮点边界误差。
- `both`：对每个样本先执行两场 `paired`，再执行两场 `hard`，默认总计 **1200 场**；两组结果分别统计。单独运行和合并运行的同一组样本及对局结果一致，合并运行中的全局 game id 不同。

胜率定义为 `胜 / 全部场数`（包含平局分母），得分率另计平局半分。两种指标分别报告，不把直接交手的相对 50% 得分变化等同于共同困难对手的胜率下降。双方始终先 side 0 后 side 1 选择行动，补位也按 0、1 顺序；使用 Core 的真实结算，不共享对手的待选行动。搜索和对战只使用 `Battle.Rng`，计时只用于实验报告，不影响预算、行动或停止条件。

成功运行覆盖输出目录中的三个文件，均为 UTF-8、无 BOM：

- `comparison.json`：比较协议编号、参数、内容指纹、Core 程序集 SHA-256、样本 SHA-256、全部队伍/种子、逐局胜方/回合数/最终状态哈希/录像 SHA-256，以及各组胜负平和百分点统计。节点摘要按双方记录模拟节点总数、单次最大节点、行动/补位决策数及分别耗尽次数；汇总也保留相同口径。困难电脑不走预算接口，其专家节点与决策统计为零。
- `games.csv`：每局一行，保留模式、样本编号、两队 JSON、种子、策略所在侧、胜方、实际 Turn 事件数、双方节点及耗尽摘要、状态哈希和录像 SHA-256。队伍 JSON 放在标准 CSV 引号单元格内。
- `timings.json`：独立保存工作线程数、总耗时和逐局耗时。单局包括构造对战与选招/补位/结算，总耗时还包括样本生成、逐局摘要计算和结果合并，不含写文件。并行时单局耗时之和不等于总耗时，也不能作为独占运行的 Bench 延迟；耗时受机器负载影响，不属于确定性结果。

同一 Core 程序集、内容和比较参数下（允许 `--workers` 不同），`comparison.json` 与 `games.csv` 可逐字比较；控制台进度、末行和 `timings.json` 的耗时不能用于字节一致性断言。该工具记录实验结果，胜率下降超过阈值时仍保存结果并正常退出，不能把退出码当作强度验收结论。

### T-108 验收记录

2026-10-16 在 `668e52e` 基线上运行最终实现，原创内容指纹 `336820d77647481b`：

```powershell
dotnet run -c Release --project Tools/Ladder -- --compare-expert --compare-mode both --games 300 --workers 4 --seed 1082026 --out bench_out/t108-ladder-final
```

| 对照 | 默认 128 节点专家 | 无限预算专家 | 变化 |
|---|---|---|---|
| 直接配对，各侧 300 场 | 300 胜 / 300 负 / 0 平，得分率 50% | 300 胜 / 300 负 / 0 平，得分率 50% | 距 50% 为 0 个百分点 |
| 同一困难对手，各 300 场 | 282 胜 / 18 负 / 0 平，胜率 94% | 282 胜 / 18 负 / 0 平，胜率 94% | 胜率下降 **0 个百分点**，满足 ≤3 |

共结束 1200 场，600 对同样本/同模式的最终状态哈希与录像 SHA-256 全部相同。直接配对单次最多 123 节点，对困难电脑最多 99 节点；此样本没有触及默认 128 上限，不能据此推断所有自定义内容的截断强度。真正耗尽默认预算的路径由 `T108_ExpertBudgetTests` 的双方持续伤害、并列候选压力夹具覆盖：无限搜索 180 节点、默认恰好 128 节点，返回合法行动且不改真实局面。旧 `Ai.Expert.cs` 与新 Unlimited 另在 60 个合法推进局面（36 次行动、24 次补位）核对行动、决策后哈希和下一次 RNG，输出逐字相同。

两样本的 `--workers 1` 与 `4` 输出 JSON/CSV 字节一致；既有 `verify.ps1` 及原断言通过，新增七类无效参数均拒绝，零预算完整对局验证通过。正式对照耗时约 608.30 秒，期间并行运行 Engine 测试；该耗时只作实验记录，单步延迟使用独立 Bench 报告。

正式样本 SHA-256：`8a889a874274d0df9467912f70848cdf646d5afe72071f73b72158360bd22df9`。本地结果 `bench_out/t108-ladder-final/comparison.json` 的 SHA-256 为 `662ba329a8dd29ba394ef68ccf7902f0363d81df498584d79b2b5ad2a84fc4d0`，`games.csv` 为 `965d86c058af4ab5d6acd25eeac278d29befe2fd1fd6e1a43113544f452e7389`；机器报告不随源码提交。
