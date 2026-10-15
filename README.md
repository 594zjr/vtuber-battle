# 开播对决（工作名）

[![Engine CI](https://github.com/594zjr/vtuber-battle/actions/workflows/test.yml/badge.svg)](https://github.com/594zjr/vtuber-battle/actions/workflows/test.yml)

虚拟主播 6 对 6 回合制对战。玩法机制借鉴赛尔号（人设式专属特质、先制、PP、控制类/弱化类状态、回合类效果和消回合、印记层数、第五技能、换人）；技能、文案、数值、美术全部原创，不含淘米的内容。

主播名单是可替换的**内容包**：

- `同人包：A-SOUL × VirtuaReal`：A-SOUL 的嘉然、贝拉、乃琳，VirtuaReal 的七海Nana7mi、阿萨Aza、中单光一、艾因Eine。
- `同人包：hololive × 彩虹社`：非官方同人作品，免费、非商业，只用公开艺名和公开人设，形象自制。收录依据见 [FANWORK.md](FANWORK.md)。
- `原创主播`：全部原创角色，规则完全相同，随时可以切回。

## 非官方同人作品声明

- 本项目**不盈利**：不收费、不内购、不接广告、不卖周边。开源发布，任何人可以自己下载、部署。
- 同人内容包与 A-SOUL 及其运营方、VirtuaReal、COVER（hololive）、ANYCOLOR（彩虹社）及各位主播本人无关，遵守它们公开的二创规范：[A-SOUL 二创规则说明5.0](https://www.bilibili.com/opus/481859656617637646)、[hololive](https://hololivepro.com/en/terms/)、[ANYCOLOR](https://www.anycolor.co.jp/guidelines/en/)；VirtuaReal 未见公开规范，公司或本人提出即下架。收录依据见 [FANWORK.md](FANWORK.md)。
- 不包含任何官方立绘、模型、音频、标志或他人图片。
- 许可分两部分：**代码**用 MIT（见 [LICENSE](LICENSE)）；**内容包**（`Resources/Packs/`）用 CC BY-NC-SA 4.0，禁止商业使用，fork 后同样受约束（见 [Packs/LICENSE.md](UnityProject/Assets/StreamDuel/Resources/Packs/LICENSE.md)）。
- 要下架某位主播：删掉内容包里对应的条目即可。

## 开发分工

- 设计、任务单、验收测试：[docs/design.md](docs/design.md)、[docs/TASKS.md](docs/TASKS.md)、`Engine.Tests/`。
- 写代码的 AI（GPT / Codex）先读 [AGENTS.md](AGENTS.md)。

## 怎么试玩

1. 用 Unity Hub 选「添加 → 从磁盘添加项目」，选中 `UnityProject` 文件夹。
2. 如果 Hub 提示版本不一致，选你已经装好的 Unity 6 版本打开即可。
3. 第一次打开会花几分钟导入。打开后直接按顶部的 ▶ Play，不需要打开或摆放任何场景。
4. 组队页顶部可以切换内容包；选 6 位主播，点「开播！」，和电脑打一局。

## 结构

| 位置 | 内容 |
|---|---|
| `UnityProject/Assets/StreamDuel/Core/` | 对战规则引擎，纯 C#，不依赖 Unity（以后也能放到服务器上） |
| `UnityProject/Assets/StreamDuel/Resources/StreamDuelRules.json` | 规则数据：技能、状态、直播间效果、人设、赛道克制 |
| `UnityProject/Assets/StreamDuel/Resources/Packs/*.json` | 内容包：主播名单，可给技能、状态、人设换显示名，机制不变 |
| `UnityProject/Assets/StreamDuel/Game/` | 第一版画面（Unity 自带的即时 GUI，零美术资源） |
| `Engine/`、`Engine.Tests/` | 不开 Unity 也能编译和测试规则：`dotnet test Engine.Tests` |

内容包检查及名单文档：`dotnet run --project Tools/PackCheck -- --write-docs`；可加 `--pack original` 只检查一个包，详情见 [工具用法](Tools/PackCheck/README.md)。

## 规则要点

- 每回合固定顺序：换人 → 回合开始 → 先手行动 → 后手行动 → 回合结束 → 补位。战斗记录每一行都标着属于哪一步。
- 每次行动分两个判定点：命中前（伤害之前）和命中后（伤害之后）。「高音破麦」和「电流麦」描述几乎一样，但一个在命中前读对手人气，一个在命中后读。
- 控制类（卡麦、掉线、挂机）不能用技能但可以换人，按“跳过几次行动”计数，结束后获得一回合「重连保护」。
- 弱化类（炎上、黑粉缠身、被引流）在回合结束时结算，可以被净化。
- 直播间效果分三类：回合类（可被“消回合”清掉）、印记（清不掉，下场清空，比如「热度」层数、「黑历史」）、保护类（清不掉）。能力提升另由“消强化”归零。
- 人设永远生效、不能被消除。

## 加内容

新主播只改 `Packs/` 里的内容包；新技能改 `StreamDuelRules.json`。技能效果用已有的效果代码（STATUS、STAGE、HEAL、RECOIL、DRAIN、FIELD、ADD_MARK、POWER_MUL、POWER_PER_MARK、DISPEL_TURNS、DISPEL_BOOST、CLEANSE），并写明 `at`（pre 命中前 / post 命中后）。读取时会检查引用是否存在，写错会直接报出是哪一条。
