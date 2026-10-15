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

## 电脑强度回归

```powershell
dotnet run --project Tools/Ladder -c Release -- --ai-regression
dotnet run --project Tools/Ladder -c Release -- --ai-regression --pack original --games 100 --seed 1 --baseline docs/ai-baseline.json
dotnet run --project Tools/Ladder -c Release -- --ai-regression --games 20 --seed 1 --write-baseline Tools/Ladder/obj/ai-baseline-candidate.json
```

`--ai-regression` 调用 Core 的 `AiRegression.Run`，比较简单、普通、困难、专家四档难度的全部六种配对。默认原创包、每对 100 局、种子 1；`--games` 必须为大于零的偶数。每组先从按 id 排序的名单随机抽取两支六人阵容，保持队内号位与对战种子不变，再交换双方难度打第二局；六种难度配对使用相同的阵容样本。随机数来自 `Battle.Rng`，技能先手顺序仍由 Core 正常结算，换边不会强制某档难度先行动。无赛季限制、禁用或选首发流程；每局使用引擎现有回合上限，达到上限也按 Core 的既有规则结算胜负。

控制台输出行对列的胜率矩阵与每对难度的胜、负、平、未完成数量。胜率为胜场除以总局数，平局不折算半胜；同档难度没有实际对战，以 `—` 显示。未完成指保护性步数上限结束时仍未终局的异常情况，数量包含在平局中；任何未完成对局均判回归失败。

默认基准由 `docs/ai-baseline.json` 复制到构建输出目录，运行时与当前工作目录无关；也可用 `--baseline` 指定已有文件。基准记录内容包、内容指纹、局数、种子、回合上限、采样版本、六组实测计数以及允许下降幅度。比较要求内容包、局数、种子、回合上限、采样版本一致；内容指纹变化时提示后继续比较，以便发现修改规则或内容后的退步。参数或文件错误、任一高难度对低难度胜率不大于 50%、任一高难度胜率比基准下降超过允许幅度（默认 5 个百分点）、任一未完成对局，均返回退出码 `1`。全部通过才返回 `0`，所有失败项逐条输出，不因其中一项失败而隐藏其余结果。

`--write-baseline` 明确指定并覆盖一个输出文件，创建其父目录，写入本次实际测得的数据；不读取旧基准，也不比较历史下降。难度顺序或完整对局要求未达标时仍保存实际结果并返回 `1`，避免把测得的失败改成成功。建议先写到 `obj/` 下作为候选，审查测量结果后再决定是否替换仓库基准；生成命令本身不代表接受这套难度强度。该选项与 `--baseline` 互斥。

回归模式只接受 `--pack`、`--games`、`--seed`、`--baseline` 或 `--write-baseline`，不能混用普通天梯的 `--teams`、`--rounds`、`--level`、`--season`、`--out`。回归专用参数也不能单独用于普通天梯。完整每对 100 局检查较慢，不在 CI 中运行；单元测试使用每对 20 局的小样本。

`pwsh -NoProfile -File Tools/Ladder/verify-ai-regression.ps1` 检查帮助、参数互斥、损坏或不匹配的基准、默认基准加载、每对两局的小样本实际失败退出码，以及旧天梯模式仍可输出 CSV；它不会重复执行完整强度测量。已经构建 Release 时可加 `-SkipBuild`，日志保存在 `Tools/Ladder/obj/ai-regression-verification/` 的独立子目录。
