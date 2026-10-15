# 开播对决（工作名）

虚拟主播 6 对 6 回合制对战。玩法机制借鉴赛尔号（人设式专属特质、先制、PP、控制类/弱化类状态、回合类效果和消回合、印记层数、第五技能、换人）；主播、技能、文案、数值、美术全部原创，不含任何真实主播或淘米的内容。

## 怎么试玩

1. 用 Unity Hub 选「添加 → 从磁盘添加项目」，选中 `UnityProject` 文件夹。
2. 如果 Hub 提示版本不一致，选你已经装好的 Unity 6 版本打开即可。
3. 第一次打开会花几分钟导入。打开后直接按顶部的 ▶ Play，不需要打开或摆放任何场景。
4. 组队页选 6 位主播，点「开播！」，和电脑打一局。

## 结构

| 位置 | 内容 |
|---|---|
| `UnityProject/Assets/StreamDuel/Core/` | 对战规则引擎，纯 C#，不依赖 Unity（以后也能放到服务器上） |
| `UnityProject/Assets/StreamDuel/Resources/StreamDuelContent.json` | 全部内容数据：主播、技能、状态、直播间效果、人设、赛道克制 |
| `UnityProject/Assets/StreamDuel/Game/` | 第一版画面（Unity 自带的即时 GUI，零美术资源） |
| `Engine/`、`Engine.Tests/` | 不开 Unity 也能编译和测试规则：`dotnet test Engine.Tests` |

## 规则要点

- 每回合固定顺序：换人 → 回合开始 → 先手行动 → 后手行动 → 回合结束 → 补位。战斗记录每一行都标着属于哪一步。
- 每次行动分两个判定点：命中前（伤害之前）和命中后（伤害之后）。「高音破麦」和「电流麦」描述几乎一样，但一个在命中前读对手人气，一个在命中后读。
- 控制类（卡麦、掉线、挂机）不能用技能但可以换人，按“跳过几次行动”计数，结束后获得一回合「重连保护」。
- 弱化类（炎上、黑粉缠身、被引流）在回合结束时结算，可以被净化。
- 直播间效果分三类：回合类（可被“消回合”清掉）、印记（清不掉，下场清空，比如「热度」层数、「黑历史」）、保护类（清不掉）。能力提升另由“消强化”归零。
- 人设永远生效、不能被消除。

## 加内容

只改 `StreamDuelContent.json`。技能效果用已有的效果代码（STATUS、STAGE、HEAL、RECOIL、DRAIN、FIELD、ADD_MARK、POWER_MUL、POWER_PER_MARK、DISPEL_TURNS、DISPEL_BOOST、CLEANSE），并写明 `at`（pre 命中前 / post 命中后）。读取时会检查引用是否存在，写错会直接报出是哪一条。
