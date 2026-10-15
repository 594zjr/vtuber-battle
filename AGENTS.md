# AGENTS.md — 给写代码的 AI（GPT / Codex 等）的工程说明

你在给《开播对决》（工作名）写代码。这是一个**非官方、非商业**的虚拟主播 6v6 回合制对战同人游戏，Unity 开发，开源。
先读这三份：

1. `docs/design.md` —— 规则和数据格式的唯一标准。和代码不一致时以它为准；你认为它有错，在 PR 里指出，不要自己改规则。
2. `docs/TASKS.md` —— 任务总表；`docs/codex-tasks/T-xx.md` 是每个任务的完整说明。一次只做一个任务。
3. 本文件。

## 目录

```
UnityProject/                         Unity 6 工程（打开这个文件夹）
  Assets/StreamDuel/Core/             规则引擎：纯 C#，禁止引用 UnityEngine（asmdef 设了 noEngineReferences）
  Assets/StreamDuel/Game/             画面层：只做显示和输入，不写任何规则
  Assets/StreamDuel/Resources/
    StreamDuelRules.json              规则数据：赛道、克制表、状态、直播间效果、人设、技能
    Packs/*.json                      内容包：主播名单（原创包、同人包）。单独许可 CC BY-NC-SA 4.0
Engine/StreamDuel.Core.csproj         把 Core 源码编成 netstandard2.1 类库（不复制源码，直接引用 Unity 工程里的文件）
Engine.Tests/                         xunit 测试；Pending/ 里是还没实现的任务的验收测试，默认不编译
docs/                                 设计文档、任务单
```

## 怎么跑测试

```
dotnet test Engine.Tests
```

需要 .NET 8 SDK。**每次提交前必须全部通过。** 做某个任务时，把 `Engine.Tests/Pending/` 里对应的测试文件移到 `Engine.Tests/` 下（见任务单），让它通过。不要修改已有测试的断言来迁就你的代码；确实需要改的，在 PR 里说明理由。

## 硬性规则

- **Core 不能依赖 Unity。** 不能 `using UnityEngine`，只用 .NET Standard 2.1 和 C# 9（不要用 record、init、file-scoped namespace、global using）。Unity 6 用的就是这个语言版本。
- **规则写在 Core，画面写在 Game。** Game 只能调用 Core 的公开接口，不能自己算伤害、判断先手。
- **数据驱动。** 新技能、新主播、新人设优先只改 JSON。只有出现新的“效果代码”或“人设代码”时才改引擎，并且同时更新 `docs/design.md` 的效果码表和 `Describe.cs` 的说明文字。
- **每个效果都必须有判定时机**（`at: pre/post`）和明确的可驱散规则。界面要能把它们显示出来。
- **同一局可复现。** 所有随机数只能来自 `Battle` 的 `Rng`，不能用 `System.Random`、`UnityEngine.Random` 或时间。
- **日志就是真相。** 每一次状态变化都要写进 `Battle.Log`，带上节点名（开场/换人/回合开始/先手行动/后手行动/回合结束/补位/结束）。

## 编码规范

- 命名空间：引擎 `StreamDuel.Core`，画面 `StreamDuel.Game`。类型、公开成员用 PascalCase，局部变量 camelCase。
- 4 空格缩进，花括号另起一行（与现有代码一致）。
- 给玩家看的文字（日志、说明）用简体中文；代码注释用中文，只写“为什么”。
- 新功能尽量放进新文件（`Battle` 是 partial class，可以新建 `Battle.Xxx.cs`），减少和别的任务冲突。
- 不引入任何 NuGet 包到 Core；测试工程只用 xunit。

## 禁止事项

- **不得提交任何官方素材**：官方立绘、Live2D/3D 模型、官方音频、标志，以及任何你没有权利使用的他人图片、字体、音频、代码。形象一律用程序生成的图形或本项目自己画的图。
- **不得出现淘米（赛尔号）的任何文本或数据**：精灵名、技能名、魂印描述、数值表、从其客户端提取的数据。只借鉴玩法机制。
- **不得写入真实主播的现实事件、中之人信息、负面传闻、擦边或贬损内容。** 同人包只写公开艺名和公开人设。
- **不得加入任何盈利功能**：收费、内购、广告、打赏、数据收集。
- **不得修改** `LICENSE`、`UnityProject/Assets/StreamDuel/Resources/Packs/LICENSE.md`、`FANWORK.md`，以及 README 里“非官方同人作品声明”一节。
- 不要提交 `Library/`、`Temp/`、`Logs/`、`UserSettings/`、`bin/`、`obj/`。

## 提交

- 仓库：https://github.com/594zjr/vtuber-battle 。每个任务从最新 `origin/main` 开分支 `task/T-xx`，推送后开 PR 到 main；不要直接推 main，不要自己合并。同时做多个任务时每个任务用单独的 `git worktree` 文件夹（见 `docs/codex-tasks/SETUP.md`）。
- 一个任务一个 PR，标题写任务编号，例如 `T-03 新效果码 PP_REDUCE / FIXED_DAMAGE`。
- PR 描述写：做了什么、改了哪些文件、测试结果。
