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

## 禁用与首发策略配对比较

```powershell
dotnet run --project Tools/Ladder -c Release -- --compare-openers --blocks 250 --pack original --seed 1 --out Tools/Ladder/obj/t90-comparison
```

`--compare-openers` 只改变禁用和首发策略：新策略调用带 `Hard` 的 `Ai.ChooseBan/ChooseLead`，旧策略调用 `Normal`；双方每回合出招与补位始终使用 `Hard`。每场同时开启禁用和首发。可设置 `--pack`、`--season`、`--seed`、`--out`，不能混用普通赛事的 `--teams`、`--rounds`、`--level`；`--blocks` 只用于比较模式，默认 250，至少 1。

先用原有 `TeamGenerator.Generate(db, season, 2 * blocks, seed)` 生成阵容。第 `i` 组从前半取得建议阵容 A，从后半取得随机阵容 B。每组依次运行四局：

| 局内顺序 | 0 方 | 1 方 |
|---|---|---|
| 1 | A / 新策略 | B / 旧策略 |
| 2 | B / 旧策略 | A / 新策略 |
| 3 | A / 旧策略 | B / 新策略 |
| 4 | B / 新策略 | A / 旧策略 |

第 `i` 组（从 0 开始）的四场共用 `unchecked(seed + i)`，不改变队内原始顺序。两种策略都使用两套阵容、都处于两侧；默认 1000 局中新策略在 0 方、1 方各 500 局。每个阶段按 0 方、1 方顺序调用 AI；禁用/首发先收集双方决定再提交，不向第二方泄露第一方选择。不新增随机源、不回退或重置 `Battle.Rng`，阵容随机性只来自原有生成器。默认预先固定 `seed=1`，不能按结果挑选种子或删除失败场次。

输出 UTF-8、无 BOM，不含时间：

- `opener-games.csv`：每场的组号、方向、种子、新策略所在方、双方原始有序阵容、双方禁用的对手 id、双方首发 id、胜方、新策略结果、回合和终局哈希。
- `opener-records.jsonl`：与 CSV 数据行顺序一致，每行是一场完整 `BattleRecord`，含原始阵容、种子和全部决定。每场写入前经过 JSON 往返与 `Replay.Run`，核对终局、哈希及逐行日志。
- `opener-summary.json`：完整配置、内容指纹、计划/已完成局数、回放验证数，以及新策略处于 0 方、1 方和总体的胜平负、胜率及目标是否达到。

严格胜率为 `胜 / 总局数`，平局仍计入分母；同时单列 `(胜 + 0.5 × 平) / 总局数`，不把两者混用。目标为总体严格胜率至少 50%，结果不足照实输出，不改变战斗规则，不把低胜率当作工具运行异常。Core 按 120 回合上限正常裁决的终局计入结果；超出安全上限但仍未终局则报 `unfinished`、退出失败并保存 `status=failed` 的已完成汇总，未完成对局不算平局或完成场次。
