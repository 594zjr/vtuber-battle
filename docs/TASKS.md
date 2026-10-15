# 任务单

规则：领任务和合并的流程见 `AGENTS.md`「分支、PR 与合并」。一个窗口一次做一个任务；标了依赖的必须等前置任务标 ✅。每个任务完成的标准是**验收标准全部满足 + `dotnet test Engine.Tests` 全绿**。规则以 `docs/design.md` 为准，工程约定见 `AGENTS.md`。

状态：✅ 已完成　⬜ 待做

## 已完成（现有代码，作为起点）

| 编号 | 内容 | 测试 |
|---|---|---|
| ✅ T-00 | 引擎核心：回合流程与节点、先制/反应排序、PP、能力等级、控制/弱化状态、直播间效果（回合类/印记/保护）、消回合、消强化、印记层数、12 个人设代码、伤害公式、电脑对手、内容包加载与校验 | `Engine.Tests/BattleTests.cs` |
| ✅ U-00 | Unity 工程：按 Play 自动启动，组队页 + 对战页（IMGUI），内容包切换，逐行回放，10 秒限时 | 只做过编译检查，**还没在真实 Unity 里打开过** |

---

## 第一批：引擎（纯 C#，`Assets/StreamDuel/Core/`）

### ✅ T-01 赛季规则：禁用池与限制池

- **目标**：内容包可定义赛季；组队时检查队伍是否合法。
- **数据**：内容包新增可选字段
  ```json
  "seasons": { "s1": { "name": "第一赛季", "banned": ["kei"], "limited": [ { "max": 1, "ids": ["bell", "bee"] } ] } }
  ```
  `original.json` 加上上面这个 `s1`（测试要用）。
- **代码**：新文件 `Core/TeamCheck.cs`：`public static List<string> Validate(ContentDb db, string seasonId, IList<string> team)`，返回中文错误列表，空表示合法。检查：正好 6 位、无重复、都存在、不在禁用池、每个限制池不超过 `max`。错误信息要带主播名字（不是 id）和池子说明。`seasonId == null` 时只做基础检查。`ContentDb` 增加 `Seasons` 字典，`Validate()` 校验赛季里引用的主播都存在。
- **验收**：把 `Engine.Tests/Pending/T01_SeasonRulesTests.cs` 移到 `Engine.Tests/`，全部通过。
- **不要做**：界面（在 T-11 做）。

### ✅ T-02 对战记录与回放

- **目标**：每局可以存成一段文本，以后原样重放（查 bug、做回看都靠它）。
- **代码**：
  - 新文件 `Core/Replay.cs`：`BattleRecord`（Seed、PackId、两队 id 列表、按顺序的事件列表：回合行动 `{a0, a1}` 或补位 `{side, to}`），`ToJson()` / `static FromJson(string)`（读用 `MiniJson`，写自己拼字符串，注意转义）。
  - `Battle` 增加只读属性 `Record`，在构造、`ResolveTurn`、`Replace` 里自动追加（记录**纠正前**的原始行动，重放时同样经过 `Normalize`）。
  - `Replay.Run(ContentDb db, BattleRecord r)`：PackId 不一致抛异常；否则新建同种子对战，按事件依次调用 `ResolveTurn` / `Replace`，返回对战。
- **验收**：移入 `Pending/T02_ReplayTests.cs`，通过。

### ✅ T-03 新效果码 PP_REDUCE、FIXED_DAMAGE

- **目标**：补上赛尔号式的“削 PP”和“固定伤害”。
- **代码**：
  - `Fighter` 增加 `LastSkillIndex`（-1 表示本次上场还没用过技能；下场清回 -1；挣扎不算）。
  - `PP_REDUCE`（参数 `n`，target）：目标最后用过的技能 PP 减 n（不低于 0）；没用过则写日志“……还没有用过技能，PP 削减无效”。
  - `FIXED_DAMAGE`（参数 `n`，target）：对目标造成正好 n 点伤害，不受能力、赛道、直播间效果、人设、暴击影响；算“受到伤害”（会打醒挂机），但不触发“被击中后”人设。
  - `Describe.cs` 加说明文字；`docs/design.md` 6.1 的效果码表加两行。
- **验收**：移入 `Pending/T03_NewEffectCodesTests.cs`，通过。

### ✅ T-04 平衡模拟工具

- **目标**：量化每位主播强弱，给调数值用。
- **代码**：新命令行工程 `Tools/Sim/Sim.csproj`（net8.0，引用 Engine），`dotnet run --project Tools/Sim -- --pack original --games 5000` 输出每位主播的出场胜率、平均回合数、各技能使用率，按胜率排序，CSV 打到标准输出。
- **验收**：能跑；同一参数两次输出一致（固定种子）；README 加一行用法。不需要单元测试。

### ✅ T-05 给界面用的预览：伤害范围和先手预测

- 新文件 `Core/Preview.cs`：`PreviewDamage(side, skillIndex)` 返回 Min/Max/Eff/Notes，`PreviewOrder(a0, a1)` 返回 First/Reason。**不得消耗随机数。**
- **验收**：移入 `Pending/T05_PreviewTests.cs`，通过。

### ✅ T-06 电脑难度：简单 / 普通

- `Ai.cs` 加 `AiLevel` 和 `Choose(b, s, level)`；普通 = 现有行为；简单 = 一半随机选技能、从不主动换人。
- **验收**：移入 `Pending/T06_AiLevelTests.cs`，通过（普通对简单 300 局胜率 ≥ 60%）。

每个任务的完整说明（可以直接贴给 Codex）在 `docs/codex-tasks/`。

---

## 第二批（T-07 起）

完整说明在 `docs/codex-tasks/T-xx.md`。标“需要本机有 Unity”的，电脑没装 Unity 就跳过。

| 编号 | 内容 | 依赖 |
|---|---|---|
| ✅ T-07 | GitHub 自动测试（CI） | 无 |
| ⬜ T-08 | 在 Unity 里打开工程、清零报错、出 Windows 包（需要本机有 Unity） | 无 |
| ⬜ T-09 | 画面改用 UI Toolkit + 头像图片（需要本机有 Unity） | T-08 |
| ⬜ T-10 | 打击表现（需要本机有 Unity） | T-09 |
| ⬜ T-11 | 组队页的赛季选择和对战回看（需要本机有 Unity） | T-01、T-02、T-09 |
| ⬜ T-12 | 电脑难度：困难 | T-05、T-06 |
| ✅ T-13 | 内容包检查工具与名单文档 | 无 |
| ✅ T-14 | 平衡调整 | T-04 |
| ✅ T-15 | 新机制：偷强化、反弹、免疫弱化、禁止换人 | T-03 |
| ✅ T-16 | 开场看阵容选首发 | 无 |
| ✅ T-17 | 命令行对战 | T-06 |

## 第三批（T-18 起，不需要 Unity）

完整说明在 `docs/codex-tasks/T-xx.md`；验收测试在 `Engine.Tests/Pending/`。

| 编号 | 内容 | 依赖 |
|---|---|---|
| ⬜ T-18 | 规则澄清：PP 被削空、自伤下播、镜面范围 | T-03、T-15 |
| ⬜ T-19 | 内容包可以自带技能、人设、效果和状态 | 无 |
| ⬜ T-20 | A-SOUL × VirtuaReal：每位主播独有的人设和招牌技能 | T-19、T-21 |
| ⬜ T-21 | 5 个新人设代码 | 无 |
| ⬜ T-22 | 双赛道 | 无 |
| ⬜ T-23 | 对战统计 | 无 |
| ⬜ T-24 | 状态哈希与行动编码（联网对战准备） | 无 |
| ⬜ T-25 | 开局禁用（各禁对方 1 位） | T-16 |
| ⬜ T-26 | 玩家档案（战绩统计） | 无 |
| ⬜ T-27 | 从外部文件夹加载内容包 | T-19 |
| ⬜ T-28 | 命令行：回看、禁用、首发、档案一体化 | T-17、T-23、T-25、T-26 |

## 内容（由 Claude 负责，不交给 Codex）

- 主播名单、二创规范核对、`FANWORK.md`（T-20 只写已收录主播的人设和技能文案，不改名单）。当前：`fan_asoul_vr`（A-SOUL 3 位 + VirtuaReal 4 位）、`fan_holo_niji`（占位）、`original`。

---

## 以后（先不做）

- 联网对战：服务器直接复用 `StreamDuel.Core`，客户端只发行动；对战记录用 T-02 的格式。
- 账号、排行、赛季轮换。
