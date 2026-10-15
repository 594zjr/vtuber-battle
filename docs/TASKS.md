# 任务单

规则：一次做一个任务，按编号顺序（标了“依赖”的必须等前置任务合并）。每个任务完成的标准是**验收标准全部满足 + `dotnet test Engine.Tests` 全绿**。规则以 `docs/design.md` 为准，工程约定见 `AGENTS.md`。

状态：✅ 已完成　⬜ 待做

## 已完成（现有代码，作为起点）

| 编号 | 内容 | 测试 |
|---|---|---|
| ✅ T-00 | 引擎核心：回合流程与节点、先制/反应排序、PP、能力等级、控制/弱化状态、直播间效果（回合类/印记/保护）、消回合、消强化、印记层数、12 个人设代码、伤害公式、电脑对手、内容包加载与校验 | `Engine.Tests/BattleTests.cs`（13 项） |
| ✅ U-00 | Unity 工程：按 Play 自动启动，组队页 + 对战页（IMGUI），内容包切换，逐行回放，10 秒限时 | 只做过编译检查，**还没在真实 Unity 里打开过** |

---

## 第一批：引擎（纯 C#，`Assets/StreamDuel/Core/`）

### ⬜ T-01 赛季规则：禁用池与限制池

- **目标**：内容包可定义赛季；组队时检查队伍是否合法。
- **数据**：内容包新增可选字段
  ```json
  "seasons": { "s1": { "name": "第一赛季", "banned": ["kei"], "limited": [ { "max": 1, "ids": ["bell", "bee"] } ] } }
  ```
  `original.json` 加上上面这个 `s1`（测试要用）。
- **代码**：新文件 `Core/TeamCheck.cs`：`public static List<string> Validate(ContentDb db, string seasonId, IList<string> team)`，返回中文错误列表，空表示合法。检查：正好 6 位、无重复、都存在、不在禁用池、每个限制池不超过 `max`。错误信息要带主播名字（不是 id）和池子说明。`seasonId == null` 时只做基础检查。`ContentDb` 增加 `Seasons` 字典，`Validate()` 校验赛季里引用的主播都存在。
- **验收**：把 `Engine.Tests/Pending/T01_SeasonRulesTests.cs` 移到 `Engine.Tests/`，全部通过。
- **不要做**：界面（在 U-05 做）。

### ⬜ T-02 对战记录与回放

- **目标**：每局可以存成一段文本，以后原样重放（查 bug、做回看都靠它）。
- **代码**：
  - 新文件 `Core/Replay.cs`：`BattleRecord`（Seed、PackId、两队 id 列表、按顺序的事件列表：回合行动 `{a0, a1}` 或补位 `{side, to}`），`ToJson()` / `static FromJson(string)`（读用 `MiniJson`，写自己拼字符串，注意转义）。
  - `Battle` 增加只读属性 `Record`，在构造、`ResolveTurn`、`Replace` 里自动追加（记录**纠正前**的原始行动，重放时同样经过 `Normalize`）。
  - `Replay.Run(ContentDb db, BattleRecord r)`：PackId 不一致抛异常；否则新建同种子对战，按事件依次调用 `ResolveTurn` / `Replace`，返回对战。
- **验收**：移入 `Pending/T02_ReplayTests.cs`，通过。

### ⬜ T-03 新效果码 PP_REDUCE、FIXED_DAMAGE

- **目标**：补上赛尔号式的“削 PP”和“固定伤害”。
- **代码**：
  - `Fighter` 增加 `LastSkillIndex`（-1 表示本次上场还没用过技能；下场清回 -1；挣扎不算）。
  - `PP_REDUCE`（参数 `n`，target）：目标最后用过的技能 PP 减 n（不低于 0）；没用过则写日志“……还没有用过技能，PP 削减无效”。
  - `FIXED_DAMAGE`（参数 `n`，target）：对目标造成正好 n 点伤害，不受能力、赛道、直播间效果、人设、暴击影响；算“受到伤害”（会打醒挂机），但不触发“被击中后”人设。
  - `Describe.cs` 加说明文字；`docs/design.md` 6.1 的效果码表加两行。
- **验收**：移入 `Pending/T03_NewEffectCodesTests.cs`，通过。

### ⬜ T-04 平衡模拟工具

- **目标**：量化每位主播强弱，给调数值用。
- **代码**：新命令行工程 `Tools/Sim/Sim.csproj`（net8.0，引用 Engine），`dotnet run --project Tools/Sim -- --pack original --games 5000` 输出每位主播的出场胜率、平均回合数、各技能使用率，按胜率排序，CSV 打到标准输出。
- **验收**：能跑；同一参数两次输出一致（固定种子）；README 加一行用法。不需要单元测试。

---

## 第二批：Unity 画面（`Assets/StreamDuel/Game/`）

### ⬜ U-01 在 Unity 6 里打开并清零报错（依赖：无）

- **目标**：工程在 Unity 6 LTS 首次打开无报错、无警告；按 Play 能完整打完一局。
- **做法**：新建 `Assets/Scenes/Main.unity`（空场景即可，`GameBoot` 会自动创建画面），加进 Build Settings；把 `ProjectVersion.txt` 对齐到实际使用的版本；能打出 Windows 包并运行。
- **验收**：附截图：组队页、对战中、结束弹窗；Console 截图无红黄。

### ⬜ U-02 换成 UI Toolkit（依赖：U-01）

- **目标**：画面改用 UI Toolkit（UXML + USS），为正式美术做准备，功能与 design.md 第 8 节一致。
- **字体**：放一份开源中文字体（如思源黑体 / Noto Sans SC，OFL 许可）到 `Assets/StreamDuel/Fonts/`，附许可文件。**不得使用来源不明的字体。**
- **验收**：第 8 节列出的每个功能都在；1280×720 和 1920×1080 下不重叠、不截字；附截图。旧 `BattleScreen.cs` 删除前确认功能全部迁移。

### ⬜ U-03 头像图片（依赖：U-01）

- **目标**：如果存在 `Resources/Portraits/<packId>/<streamerId>.png` 就用图片，否则继续用程序生成的圆形头像。
- **约束**：仓库里只能放本项目自己画的图；**不得放官方立绘或网上找来的图**。这个任务只做加载逻辑，图片先不放。
- **验收**：放一张自己做的测试图能显示，删掉后回落到圆形头像。

### ⬜ U-04 打击表现（依赖：U-02）

- **目标**：回放时人气条缓动、伤害/回复飘字、下播时头像变灰淡出、先手方高亮。
- **验收**：开“快速播放”时动画相应缩短；关闭动画选项后行为与现在一致。

### ⬜ U-05 组队页的赛季选择与回看（依赖：T-01、T-02、U-02）

- **目标**：组队页选择赛季，不合法的队伍不能开播并显示 `TeamCheck` 的错误；每局结束自动保存记录到 `Application.persistentDataPath/replays/`，组队页有“回看”列表，能重放。
- **验收**：附操作截图。

---

## 第三批：内容

### ⬜ C-01 换成用户指定的主播名单

- 由 Claude 先核对所属公司的二创规范、写进 `FANWORK.md`，再交给你按内容包格式填写。只写公开人设，按 `AGENTS.md` 的禁止事项。

### ⬜ C-02 数值调整（依赖：T-04）

- 用模拟工具把每位主播的胜率调到 40%～60% 之间，只改 JSON。附调整前后的胜率表。

---

## 以后（先不做）

- 联网对战：服务器直接复用 `StreamDuel.Core`，客户端只发行动；对战记录用 T-02 的格式。
- 账号、排行、赛季轮换。
