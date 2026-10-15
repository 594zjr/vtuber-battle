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
2. 本工程使用 **Unity 6（6000.6.5f1）**；用对应版本打开，Windows 打包需要编辑器具备 Windows 构建支持。
3. 第一次打开会花几分钟导入。打开 `Assets/Scenes/Main.unity` 后按顶部的 ▶ Play；空场景中的画面由 `GameBoot` 自动创建，无需摆放对象。
4. 组队页顶部可以切换内容包；选 6 位主播，点「开播！」，和电脑打一局。

顶部「主菜单」可进入新的 UI Toolkit 框架，组队、对战、结算、回看、设置、联网目前为独立占位页；「旧版对战」返回原有可玩界面，打开菜单期间原对战暂停。界面统一使用随工程提供的 Noto Sans SC 字体，来源与许可见 [THIRD_PARTY.md](THIRD_PARTY.md)。当前 Input System 1.20.1 使用 Both 兼容旧 IMGUI，待 T-67 替换旧画面后切为 New。Unity 6000.6.5f1 启动时仍会提示旧输入系统弃用；用户于 2026-10-15 批准仅此提示暂留至 T-67，其他错误和警告仍须修复。

编辑器 Play 中重编译脚本时，纯 C# 对战状态不能跨域重载恢复，画面会重新接线并回到旧版组队页；普通菜单切换或组件停用再启用仍保留当前页面参数。

## Windows 打包

在仓库根目录运行下面的命令；`Unity.exe` 使用 6000.6.5f1 编辑器的可执行文件，可将它所在目录加入 PATH，或替换为完整路径：

```powershell
Unity.exe -batchmode -quit -projectPath UnityProject -executeMethod UiProjectSetup.BuildWindows -logFile build.log
```

产物位于 `UnityProject/Build/Windows/StreamDuel.exe`，运行时需保留同目录的全部构建文件。构建目录已加入 Git 忽略；构建日志写入仓库根目录的 `build.log`。

`BuildScript.BuildWindows` 会先调用 `BuildScript.PrepareProject`：缺少主场景时创建合法空场景，并将它设为构建列表第 0 位；已有主场景和其他构建场景会保留。也可以在编辑器菜单「开播对决 → 准备主场景」或「开播对决 → 构建 Windows」执行。构建失败会抛出错误，让批处理命令返回失败状态。

`UiProjectSetup.BuildWindows` 先生成或更新 UI Toolkit 的面板和字体引用，再调用上述构建；已提交的资源可直接打开运行。编辑器菜单「开播对决 → 准备界面资源」可重新接线。

Windows 包的自动冒烟验收（需要正常图形桌面，不加 `-batchmode` / `-nographics`）：

```powershell
UnityProject/Build/Windows/StreamDuel.exe --smoke-test ./Build/Smoke -logFile ./Build/player.log
```

它通过现有界面入口切换内容包、选择首发并完成一局，截图和回放保存在指定目录新建的 `run-*` 子目录；`result.json` 记录结果，成功退出 0，失败或 180 秒超时退出 1。验收中的内容包切换和自动组队不覆盖玩家已保存的包选择与队伍。运行截图需要检查中文与排版；报告只统计 `BattleScreen.Start` 之后的日志，启动错误还须检查完整 Player 日志。此入口不替代鼠标、滚动和限时操作的人工验收；不传参数时正常进入组队页。

`--ui-smoke-test <绝对输出目录>` 单独验证新界面路由、返回、生命周期与 1280×720 / 1920×1080 布局，保存实际渲染截图和报告；使用程序触发的导航事件，不等同于人工鼠标验收。两种验收模式均只加载内置包，使用内存设置，不读写玩家档案或 PlayerPrefs 阵容。

编辑器验收可用 `-executeMethod UiSmokeEditor.Run --ui-smoke-test <绝对输出目录>`，整局对战则将参数改为 `--smoke-test <绝对输出目录>`，可另加 `--smoke-size 1920x1080`（默认 `1280x720`，不加 `-batchmode` / `-quit`）；它先在 Edit 模式初始化 Unity 搜索索引，再打开主场景进入 Play。完成后和停止 Play 清理后均采集 Console，写入 `UnityProject/Logs/T59/editor-console.json` 并退出。Console 红黄计数任一非零或清理等待超时都返回失败，不清除或过滤日志；更早的启动提示另查完整 Editor 日志，截图仍须复核。

## 结构

| 位置 | 内容 |
|---|---|
| `UnityProject/Assets/StreamDuel/Core/` | 对战规则引擎，纯 C#，不依赖 Unity（以后也能放到服务器上） |
| `UnityProject/Assets/StreamDuel/Resources/StreamDuelRules.json` | 规则数据：技能、状态、直播间效果、人设、赛道克制 |
| `UnityProject/Assets/StreamDuel/Resources/Packs/*.json` | 内容包：主播名单，可给技能、状态、人设换显示名，机制不变 |
| `UnityProject/Assets/StreamDuel/Game/` | UI Toolkit 路由、分目录占位界面、舞台接口，以及迁移期间保留的 IMGUI 对战 |
| `Engine/`、`Engine.Tests/` | 不开 Unity 也能编译和测试规则：`dotnet test Engine.Tests` |

模糊测试：`dotnet test Engine.Tests --filter FullyQualifiedName~FuzzTests` 默认对每个内容包跑 300 局随机合法组队、随机禁用/首发、六项修饰器的随机组合和任意回合行动（包括非法选择），逐步检查人气、PP、能力、控制状态、阶段与结束节点，再完整回放并比较状态哈希。启用 `stage_cap_3` 时，能力等级还须满足 ±3 上限；每包必须覆盖每项修饰器的启用和关闭、无修饰器与多项组合，以及换到已下播者、受控出招和零 PP 出招，计数写入测试输出。设置环境变量 `SD_FUZZ=2000` 可跑每包 2000 局；变量必须是不小于 300 的整数，运行后清除即可恢复默认。失败断言包含内容包、固定种子、当时操作和记录 JSON。

确定性回归：每个内容包有 10 个固定种子的普通 AI 对战完整日志，`dotnet test Engine.Tests --filter FullyQualifiedName~GoldenTests` 会逐字比较 `Engine.Tests/Golden/` 基线，缺文件即失败。**只有有意修改规则、内容或日志行为，或新增内容包时才更新 golden，并在 PR 描述中说明原因**：设置 `SD_UPDATE_GOLDEN=1` 后运行该测试，检查差异，再清除变量并运行普通测试；完整命令见[基线说明](Engine.Tests/Golden/README.md)。

平衡模拟：`dotnet run --project Tools/Sim -- --pack original --games 5000 --seed 1`，标准输出为主播胜率及技能使用次数两段 CSV；胜率按入选队伍统计，详情见 `--help`。

模拟器可加 `--level easy|normal|hard|expert` 指定双方行动和补位的难度，默认 `normal`（普通）。例如 `dotnet run --project Tools/Sim -- --pack original --level expert --games 100 --seed 1` 使用专家难度。`--tune` 固定搜索普通难度的评分权重，不能与 `--level` 同用。

专家难度在独立对战副本上试算行动与补位，真实对局只在最高分相同时抽签。策略和实测见 [专家 AI 验证](docs/ai_expert.md)；`pwsh -File Tools/Sim/check-expert.ps1 -Mode acceptance -Games 200` 可复现对困难 AI 的验收分队，并统计专家决策耗时。

天梯模拟：`dotnet run --project Tools/Ladder -- --pack original --teams 64 --rounds 10 --seed 1 --out ./ladder_out`，输出阵容及主播出场加权 Elo 两份 CSV；可加 `--level hard`、`--season s1`，配对和统计口径见 [工具用法](Tools/Ladder/README.md)。

内容包检查及名单文档：`dotnet run --project Tools/PackCheck -- --write-docs`；可加 `--pack original` 只检查一个包，详情见 [工具用法](Tools/PackCheck/README.md)。

命令行对战：`dotnet run --project Tools/Cli -- --pack fan_asoul_vr --level normal --seed 1`；加 `--auto` 自动打完整局，`--team id1,id2,id3,id4,id5,id6` 指定我方阵容，数字选择技能/换人/补位，详情见 `--help`。

队伍分享：`dotnet run --project Tools/Cli -- --pack original --team bell,enka,shiori,lumi,hane,momo --export-team` 在开局前打印 `SDT1-` 队伍码并继续对战；输入 `q` 可退出。用 `dotnet run --project Tools/Cli -- --team-code "SDT1-…" --auto` 导入并开打，省略 `--pack` 时自动加载码内内容包；显式指定的 `--pack` 必须匹配，外部包还需 `--pack-dir`。`--team-code` 与 `--team` 互斥，坏码、包不匹配和非法成员会在开局前报错。`--export-team` 导出的是禁用及首发调整前的本方六人顺序；与导入同用时保留预设名称，可配合手动、自动和联网对战。这两个参数都不能与 `--replay`、`--replay-code` 或 `--list-packs` 同用。

`--level` 支持 `easy`（简单）、`normal`（普通）、`hard`（困难）、`expert`（专家），默认普通；电脑行动和补位使用所选难度。专家对战示例：`dotnet run --project Tools/Cli -- --pack original --level expert --seed 1`；加 `--auto` 可观看双方专家电脑对战。

- `dotnet run --project Tools/Cli -- --glossary`：列出当前内容包的赛道克制、状态、直播间效果、人设和通用对战规则。可用 `--glossary "卡麦"` 按标题或正文搜索（不区分大小写），并配合 `--pack`、`--pack-dir` 查询其他包；此模式只查阅说明，不开始对战或写入存档。

- `dotnet run --project Tools/Cli -- --auto --seed 1 --ban --lead`：双方自动按“禁用 → 首发 → 对战”完成一局。去掉 `--auto` 即可手动选择；禁用后首发号位按剩余五人重新编号。
- `dotnet run --project Tools/Cli -- --seed 1 --ban --lead --advice`：在首次选择前显示建议禁用、两位首发候选、两位主要威胁及各自的应对人选。建议只基于双方公开的原始六人阵容和开场伤害预览，不预测对方选择或胜率；同时启用禁用与首发时只在禁用前显示一次。`--advice` 需要 `--ban` 或 `--lead`，仅用于新的本地对局，不能与同屏双人、赛事、回放、联网、分析或列表模式同用。启用建议不改变对局选择或随机数流。
- 结束时打印对战统计和本包累计战绩，并保存 `saves/profile.json`；`--save-dir 目录` 可指定其他本机目录。完整回放写入该目录下的 `replays/<UTC时间>_<packId>.json`，只保留本工具生成的最近 50 份，其他命名文件保留。外部包 id 含特殊字符或超过 80 字符时，文件名用 id 的 SHA256 摘要，JSON 内仍保存原 id。提前退出不记战绩、不保存回放。
- `dotnet run --project Tools/Cli -- --replay saves/replays/<文件名>.json`：自动加载记录对应的内容包；外部包需加 `--pack-dir 目录`。先显示第一个有日志的回合，再按回车逐回合播放；`q` 或输入结束退出。加 `--auto` 连续播放全部记录。回看不增加战绩，也不保存第二份回放；不完整记录会提示“未完成”。回放需要与原对局相同的规则和内容包版本。
- `--share`：完成对局后打印以 `SD1-` 开头的回放分享码，可直接复制给其他人；也可与 `--replay` 或 `--replay-code` 一起使用，在播放完毕后重新导出。提前退出不输出分享码。超大记录导出失败会报错并返回退出码 1，已完成对局仍会保存回放及战绩。回放时战报与分享分别导出，战报写入失败仍会输出可用的分享码。
- `dotnet run --project Tools/Cli -- --replay-code "SD1-这里粘贴分享码" --auto`：从分享码回看，允许码中带空白和换行；外部包仍需提供 `--pack-dir`。不能与 `--replay` 同用；不会新增战绩或回放文件，内容指纹校验与文件回放相同。
- `--report reports/battle.md`：对局结束或回放完整播放后导出 UTF-8 Markdown 战报，自动创建父目录；提前退出不导出，未结束记录完整播放后可导出未结束战报。输出路径不能与 `--replay` 输入相同，也不能占用新局的存档目录、档案、临时文件或锁路径；Windows 扩展前缀别名同样检查。
- `--hash` 可输出新对战每回合结算后的状态哈希，不能与 `--replay` 或 `--replay-code` 同用。回放中的阵容、种子及禁用/首发选项以记录为准；新对战参数不会修改它们。所有参数的中文说明见 `--help`。
- 相同规则、内容包、参数和种子的自动战斗日志一致。比较完整输出时还须有相同的初始档案和相同的存档路径文本；在同一目录连续完成两局，累计战绩会正常增长。回放时间仅用于文件名，不写入战斗日志或终端保存提示。

## 本地小型赛事（命令行）

```powershell
dotnet run --project Tools/Cli -- --tournament ./tournaments/weekend.json
dotnet run --project Tools/Cli -- --tournament ./tournaments/weekend.json --auto
```

文件不存在时，按提示选择循环赛或瑞士轮，并逐人录入姓名和 `SDT1-` 队伍码；至少两人后才能开始。已有文件会恢复报名或当前轮的未报对阵。手动模式录入对局结果；`--auto` 使用普通电脑完成未报结果的比赛，每场种子由对阵编号确定。报名、排轮和报分后自动保存，输入 `q` 或结束输入可退出，下次用同一命令继续。

循环赛每两人交手一次；瑞士轮默认进行 `ceil(log2(人数))` 轮，优先避免重复对手。奇数人数每轮一人轮空，计一胜且每人最多一次。胜、平、负分别得 3、1、0 分，排名依次比较积分、对手分、直接对话积分和报名顺序；直接对话只比较积分与对手分均相同的小组。已有赛事省略 `--pack` 时使用存档内容包；外部包仍需 `--pack-dir`。赛事模式只可搭配 `--auto`、`--pack` 和 `--pack-dir`，种子和难度由赛事模式固定。

赛事文件必须与玩家档案、旧回放目录 `replays` 和新录像库 `replay-library` 分开保存；这些目录及其子目录不能作为赛事文件位置。

## 同屏双人对战（命令行）

两位玩家共用一台电脑，用 `--hotseat` 轮流手动选择。A 方通过 `--team` 或 `--team-code` 指定阵容，B 方通过 `--team-b` 指定；省略时，A 方自动建议组队，B 方先选包内未入 A 队的主播，不足六人再从 A 队补齐。双方阵容都会在开局前校验。显示名可用 `--name-a` / `--name-b` 设置，默认“玩家 A” / “玩家 B”；`--team-b`、`--name-a`、`--name-b` 仅用于同屏模式。

```powershell
dotnet run --project Tools/Cli -- --hotseat --pack original --seed 7 --team bell,enka,shiori,lumi,hane,momo --team-b kei,yuan,nyako,buck,kasumi,bee --name-a "玩家 A" --name-b "玩家 B" --ban --lead
```

首次由 A 方选择，选项输入不回显。每个双人选择阶段的第一位提交后，程序清屏并提示交给另一位，对方按回车确认后继续；第二位提交后完成结算，并由这一位先选下一阶段。只有一方需要补位时直接由该方选择，不要求交接确认。交接只安排操作顺序，战斗先后与结算规则保持不变；待结算选择不会出现在另一方视图、日志或录像里。

同屏支持 `--seed`、`--ban`、`--lead`、`--report`、`--share`、`--save-dir` 和 `--export-team`；队伍码导入及导出均对应 A 方，导出保持禁用和首发调整前的六人顺序。不接受 `--auto`、`--level`、`--timer`、`--hash`、联网、回放、录像列表（`--replays`）、列包、伤害分析或图鉴（`--glossary`）参数。完整对局按 A 方视角累计一次档案，并分别保存到旧回放目录和新录像库各一份；`q` 或输入结束提前退出时不保存。

## 联网对战（命令行）

仅在可信的本机或局域网使用：TCP 消息没有加密，没有账号、服务器、匹配或 NAT 穿透。双方须使用相同版本的引擎、规则和内容包，握手会校验内容指纹；双方各自用 `--team` 或 `--team-code` 指定六人阵容，省略时按本端 `--seed` 自动组队。导入的队伍在监听或连接前校验，`--export-team` 打印的是本端队伍。房主的 `--seed` 决定对战随机种子；客机的 `--seed` 仅用于本地自动组队，不决定对战随机种子。

在两个命令行窗口分别运行：

```powershell
# 窗口一：房主
dotnet run --project Tools/Cli -- --host 7777 --pack original --seed 7 --team bell,enka,shiori,lumi,hane,momo --save-dir ./saves/host
# 窗口二：客机；同机用 127.0.0.1，另一台电脑换成房主的局域网地址
dotnet run --project Tools/Cli -- --join 127.0.0.1:7777 --pack original --team kei,yuan,nyako,buck,kasumi,bee --save-dir ./saves/guest
```

`--host 0` 由系统分配端口并显示实际值。房主等待连接时可按 Ctrl+C 退出。加入连接或握手等待超过 10 秒会报错；对战中的人工选招没有倒计时。连接建立后仍用原有数字菜单选技能、换人和补位，每次提交显示“等待对手……”。输入 `q` 或输入结束退出；另一端会提示“对方已断开”，未完成的对局不保存回放或战绩，等待输入时也能检测断线。

两端可分别加 `--auto` 让本方 AI 选择，便于回环测试；`--hash` 显示已确认阶段的状态哈希，结束时打印最终哈希。本端最终哈希校验通过后关闭发送方向，继续接收对端最后的错误或关闭通知；双方正常完成这一流程后，各端按自己的胜负保存档案和完整回放。终局收尾超过 10 秒会报错且不保存，也可用 `--report` 导出本局战报、`--share` 打印分享码；同机两个窗口建议使用不同的 `--save-dir`，避免把两位玩家的战绩混到一个档案。联网暂不支持 `--ban`、`--lead`、`--replay`、`--replay-code`、`--list-packs`，不能同时使用 `--host` 和 `--join`。

### 可选对战计时

加 `--timer 10,30` 后，每次选招或补位有 10 秒基本时间，超出部分从本方全局 30 秒余额扣除；提交后停表，下一阶段重置基本时间但不恢复总时间。命令行显示倒计时，超时提交默认跳过，补位超时选择编号最小的可上场队友。默认跳过仍按当前状态结算：受控则跳过，全部技能无 PP 时按既有规则使用硬撑。

```powershell
dotnet run --project Tools/Cli -- --pack original --seed 1 --timer 10,30
dotnet run --project Tools/Cli -- --host 7777 --pack original --timer 10,30
dotnet run --project Tools/Cli -- --join 127.0.0.1:7777 --pack original
```

联网预算由房主声明，客机采用房主设置；双方都需要支持计时的版本。超时端自行提交默认行动，对方再宽限 10 秒仍未收到提交则报掉线；已承诺但不公开、结算后不确认哈希也会超时报错。计时不覆盖开局禁用和首发选择，不能与 `--replay` 或 `--replay-code` 同用。完整接口与等待规则见[设计文档 10.3 节](docs/design.md#103-对战计时)。

也可以通过局域网广播发现房间：

```powershell
# 房主：自动分配 TCP 端口；可加 --host 7777 指定端口
dotnet run --project Tools/Cli -- --lan-host "周末对战" --pack original --save-dir ./saves/host
# 另一窗口：搜索 5 秒后，复制所选房间显示的 --join 参数加入
dotnet run --project Tools/Cli -- --lan-list
```

房主等待连接期间每秒向 IPv4 局域网的 UDP 47777 端口广播一次，接到对手后停止；房名最多保留 24 个 UTF-16 字符单位，末尾不会截断一个代理对。搜索命令独立运行，不加载内容包、不保存档案，也不与其他参数合用。结果包括本次 5 秒内见过的房间、地址、协议、规则及内容指纹；超过 3 秒未更新的房间标为失联，协议不兼容的房间也不提供加入命令。加入仍需选择相同内容包，并由原有握手核对指纹；当前广播规则为 `casual`（基础对战）。同机只能有一个搜索监听器，端口被占用时会显示中文错误。广播不能跨越阻止广播的路由器或隔离网络，未发现时仍可使用原来的 `--join 地址:端口`。

## 自制内容包

第一次制作请从[自制内容包教程](docs/modding.md)和[完整示例包](docs/examples/example_pack.json)开始，按步骤检查、运行并试测平衡。

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
- 弱化类（过热、黑粉缠身、被引流）在回合结束时结算，可以被净化。
- 直播间效果分三类：回合类（可被“消回合”清掉）、印记（清不掉，下场清空，比如「热度」层数、「循环回放」）、保护类（清不掉）。能力提升另由“消强化”归零。
- 人设永远生效、不能被消除。

## 加内容

新主播、技能、人设、状态与直播间效果优先写进内容包，格式见上方“自制内容包”和[设计文档 6.3 节](docs/design.md)。使用已有的效果代码和人设代码；每个技能效果写明 `at`（pre 命中前 / post 命中后）及相应的驱散规则。读取时会检查引用是否存在，写错会直接报出是哪一条。
