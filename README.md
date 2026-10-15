# 开播对决（工作名）

[![Engine CI](https://github.com/594zjr/vtuber-battle/actions/workflows/test.yml/badge.svg)](https://github.com/594zjr/vtuber-battle/actions/workflows/test.yml)

虚拟主播 6 对 6 回合制对战。玩法机制借鉴赛尔号（人设式专属特质、先制、PP、控制类/弱化类状态、回合类效果和消回合、印记层数、第五技能、换人）；技能、文案、数值、美术全部原创，不含淘米的内容。

主播名单是可替换的**内容包**：

- `同人包：A-SOUL × VirtuaReal`：A-SOUL 的嘉然、贝拉、乃琳，VirtuaReal 的七海Nana7mi、阿萨Aza、中单光一、艾因Eine。
- `同人包：hololive × 彩虹社`：非官方同人作品，免费、非商业，只用公开艺名和公开人设，形象自制。收录依据见 [FANWORK.md](FANWORK.md)。
- `原创主播`：全部原创角色，规则完全相同，随时可以切回。
- `原创主播·第二期`（`original_wave2`）：12 位全新虚构主播，覆盖 8 个赛道，包含双赛道与各自专属的人设、第五技能。

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

平衡模拟：`dotnet run --project Tools/Sim -- --pack original --games 5000 --seed 1`，标准输出为主播胜率及技能使用次数两段 CSV；胜率按入选队伍统计，详情见 `--help`。

模拟器可加 `--level easy|normal|hard|expert` 指定双方行动和补位的难度，默认 `normal`（普通）。例如 `dotnet run --project Tools/Sim -- --pack original --level expert --games 100 --seed 1` 使用专家难度。`--tune` 固定搜索普通难度的评分权重，不能与 `--level` 同用。

专家难度在独立对战副本上试算行动与补位，真实对局只在最高分相同时抽签。策略和实测见 [专家 AI 验证](docs/ai_expert.md)；`pwsh -File Tools/Sim/check-expert.ps1 -Mode acceptance -Games 200` 可复现对困难 AI 的验收分队，并统计专家决策耗时。

天梯模拟：`dotnet run --project Tools/Ladder -- --pack original --teams 64 --rounds 10 --seed 1 --out ./ladder_out`，输出阵容及主播出场加权 Elo 两份 CSV；可加 `--level hard`、`--season s1`，配对和统计口径见 [工具用法](Tools/Ladder/README.md)。

内容包检查及名单文档：`dotnet run --project Tools/PackCheck -- --write-docs`；可加 `--pack original` 只检查一个包，详情见 [工具用法](Tools/PackCheck/README.md)。

命令行对战：`dotnet run --project Tools/Cli -- --pack fan_asoul_vr --level normal --seed 1`；加 `--auto` 自动打完整局，`--team id1,id2,id3,id4,id5,id6` 指定我方阵容，数字选择技能/换人/补位，详情见 `--help`。

`--level` 支持 `easy`（简单）、`normal`（普通）、`hard`（困难）、`expert`（专家），默认普通；电脑行动和补位使用所选难度。专家对战示例：`dotnet run --project Tools/Cli -- --pack original --level expert --seed 1`；加 `--auto` 可观看双方专家电脑对战。

- `dotnet run --project Tools/Cli -- --auto --seed 1 --ban --lead`：双方自动按“禁用 → 首发 → 对战”完成一局。去掉 `--auto` 即可手动选择；禁用后首发号位按剩余五人重新编号。
- 结束时打印对战统计和本包累计战绩，并保存 `saves/profile.json`；`--save-dir 目录` 可指定其他本机目录。完整回放写入该目录下的 `replays/<UTC时间>_<packId>.json`，只保留本工具生成的最近 50 份，其他命名文件保留。外部包 id 含特殊字符或超过 80 字符时，文件名用 id 的 SHA256 摘要，JSON 内仍保存原 id。提前退出不记战绩、不保存回放。
- `dotnet run --project Tools/Cli -- --replay saves/replays/<文件名>.json`：自动加载记录对应的内容包；外部包需加 `--pack-dir 目录`。先显示第一个有日志的回合，再按回车逐回合播放；`q` 或输入结束退出。加 `--auto` 连续播放全部记录。回看不增加战绩，也不保存第二份回放；不完整记录会提示“未完成”。回放需要与原对局相同的规则和内容包版本。
- `--report reports/battle.md`：对局结束或回放完整播放后导出 UTF-8 Markdown 战报，自动创建父目录；提前退出不导出，未结束记录完整播放后可导出未结束战报。输出路径不能与 `--replay` 输入相同，也不能占用新局的存档目录、档案、临时文件或锁路径；Windows 扩展前缀别名同样检查。
- `--hash` 可输出新对战每回合结算后的状态哈希，不能与 `--replay` 同用。回放中的阵容、种子及禁用/首发选项以记录为准；新对战参数不会修改它们。所有参数的中文说明见 `--help`。
- 相同规则、内容包、参数和种子的自动战斗日志一致。比较完整输出时还须有相同的初始档案和相同的存档路径文本；在同一目录连续完成两局，累计战绩会正常增长。回放时间仅用于文件名，不写入战斗日志或终端保存提示。

## 联网对战（命令行）

仅在可信的本机或局域网使用：TCP 消息没有加密，没有账号、服务器、匹配或 NAT 穿透。双方须使用相同版本的引擎、规则和内容包，握手会校验内容指纹；双方各自用 `--team` 指定六人阵容，省略时按本端 `--seed` 自动组队。房主的 `--seed` 决定对战随机种子；客机的 `--seed` 仅用于本地自动组队，不决定对战随机种子。

在两个命令行窗口分别运行：

```powershell
# 窗口一：房主
dotnet run --project Tools/Cli -- --host 7777 --pack original --seed 7 --team bell,enka,shiori,lumi,hane,momo --save-dir ./saves/host
# 窗口二：客机；同机用 127.0.0.1，另一台电脑换成房主的局域网地址
dotnet run --project Tools/Cli -- --join 127.0.0.1:7777 --pack original --team kei,yuan,nyako,buck,kasumi,bee --save-dir ./saves/guest
```

`--host 0` 由系统分配端口并显示实际值。房主等待连接时可按 Ctrl+C 退出。加入连接或握手等待超过 10 秒会报错；对战中的人工选招没有倒计时。连接建立后仍用原有数字菜单选技能、换人和补位，每次提交显示“等待对手……”。输入 `q` 或输入结束退出；另一端会提示“对方已断开”，未完成的对局不保存回放或战绩，等待输入时也能检测断线。

两端可分别加 `--auto` 让本方 AI 选择，便于回环测试；`--hash` 显示已确认阶段的状态哈希，结束时打印最终哈希。本端最终哈希校验通过后关闭发送方向，继续接收对端最后的错误或关闭通知；双方正常完成这一流程后，各端按自己的胜负保存档案和完整回放。终局收尾超过 10 秒会报错且不保存，也可用 `--report` 导出本局战报；同机两个窗口建议使用不同的 `--save-dir`，避免把两位玩家的战绩混到一个档案。联网暂不支持 `--ban`、`--lead`、`--replay`、`--list-packs`，不能同时使用 `--host` 和 `--join`。

### 可选对战计时

加 `--timer 10,30` 后，每次选招或补位有 10 秒基本时间，超出部分从本方全局 30 秒余额扣除；提交后停表，下一阶段重置基本时间但不恢复总时间。命令行显示倒计时，超时提交默认跳过，补位超时选择编号最小的可上场队友。默认跳过仍按当前状态结算：受控则跳过，全部技能无 PP 时按既有规则使用硬撑。

```powershell
dotnet run --project Tools/Cli -- --pack original --seed 1 --timer 10,30
dotnet run --project Tools/Cli -- --host 7777 --pack original --timer 10,30
dotnet run --project Tools/Cli -- --join 127.0.0.1:7777 --pack original
```

联网预算由房主声明，客机采用房主设置；双方都需要支持计时的版本。超时端自行提交默认行动，对方再宽限 10 秒仍未收到提交则报掉线；已承诺但不公开、结算后不确认哈希也会超时报错。计时不覆盖开局禁用和首发选择，不能与 `--replay` 同用。完整接口与等待规则见[设计文档 10.3 节](docs/design.md#103-对战计时)。

## 自制内容包

将自制内容包保存为 UTF-8 编码的 `.json` 文件，每个文件对应一个包。完整格式见[设计文档 6.3 节](docs/design.md)：设置唯一的 `id`、包名和主播名单；至少包含 6 位主播，每位有 4 个普通技能和 1 个第五技能。可以在包内的 `skills`、`personas`、`fields`、`statuses` 中定义专属内容，沿用已有的效果码、人设码和赛道；同类别定义的 id 不得与共用规则重复。

Unity 版在启动时额外读取 `Application.persistentDataPath/packs/`。把 JSON 直接放在这个目录第一层，例如 `Application.persistentDataPath/packs/my_pack.json`，重新启动后即可在组队页选择。`persistentDataPath` 是 Unity 为本游戏提供的用户数据目录，不是工程里的 `Resources/Packs/`；加载失败的文件会在组队页顶部显示原因，其他包仍可使用。

命令行工具通过 `--pack-dir <文件夹>` 指定外部目录。以下命令从仓库根目录运行，假设文件放在 `my-packs/`，包内 `id` 为 `my_pack`：

```powershell
dotnet run --project Tools/Cli -- --pack-dir ./my-packs --list-packs
dotnet run --project Tools/Cli -- --pack-dir ./my-packs --pack my_pack --level normal --seed 1
dotnet run --project Tools/Sim -- --pack-dir ./my-packs --pack my_pack --games 100 --seed 1
dotnet run --project Tools/PackCheck -- --pack-dir ./my-packs --pack my_pack --write-docs
```

三个工具都支持 `--list-packs`，列出可用包的 id、名称和来源；`--pack` 使用 JSON 中的 `id`，不要求与文件名一致。加载器只读取目录第一层的 JSON，按文件名排序，逐个解析并校验。坏文件会报告中文错误；外部包 id 重复时保留先成功加载的包，和内置包 id 重复时保留内置包并提示。目录不存在时不增加外部包。

同人内容包必须遵守 [FANWORK.md](FANWORK.md) 列出的二创规则，只使用公开艺名和公开人设、自己制作的形象；不得加入官方素材、现实事件、中之人信息或负面传闻，保持免费、非商业。

## 规则要点

- 每回合固定顺序：换人 → 回合开始 → 先手行动 → 后手行动 → 回合结束 → 补位。战斗记录每一行都标着属于哪一步。
- 每次行动分两个判定点：命中前（伤害之前）和命中后（伤害之后）。「高音破麦」和「电流麦」描述几乎一样，但一个在命中前读对手人气，一个在命中后读。
- 控制类（卡麦、掉线、挂机）不能用技能但可以换人，按“跳过几次行动”计数，结束后获得一回合「重连保护」。
- 弱化类（炎上、黑粉缠身、被引流）在回合结束时结算，可以被净化。
- 直播间效果分三类：回合类（可被“消回合”清掉）、印记（清不掉，下场清空，比如「热度」层数、「黑历史」）、保护类（清不掉）。能力提升另由“消强化”归零。
- 人设永远生效、不能被消除。

## 加内容

新主播、技能、人设、状态与直播间效果优先写进内容包，格式见上方“自制内容包”和[设计文档 6.3 节](docs/design.md)。使用已有的效果代码和人设代码；每个技能效果写明 `at`（pre 命中前 / post 命中后）及相应的驱散规则。读取时会检查引用是否存在，写错会直接报出是哪一条。
