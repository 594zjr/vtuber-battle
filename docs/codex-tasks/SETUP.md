# 窗口 0：一次性准备（把本地文件夹接到 GitHub 仓库）

（整段贴给一个 Codex 窗口，只需贴一次。）

---

你在这台 Windows 电脑上为开源项目《开播对决》写代码。仓库是 https://github.com/594zjr/vtuber-battle （默认分支 main）。这个窗口只做一次性准备，不写功能代码。

## 一、准备（只做一次）

1. 只在 `D:\vtuber-battle` 和 `D:\vtuber-battle-wt\` 里操作。**不要碰 D 盘的任何其他文件夹**（特别是 `D:\游戏`、`D:\seer`）。
2. 检查工具：`git --version`、`dotnet --list-sdks`。缺 git 就 `winget install --id Git.Git -e`；没有 8.x 的 SDK 就 `winget install --id Microsoft.DotNet.SDK.8 -e`。装完新开一个终端再检查。
3. 准备仓库文件夹：
   - 如果 `D:\vtuber-battle` 不存在：`git clone https://github.com/594zjr/vtuber-battle D:\vtuber-battle`。
   - 如果它已存在但不是 git 仓库（之前从压缩包解压的），或者 `git remote get-url origin` 不是上面这个地址：把它改名为 `D:\vtuber-battle.zip-old`（不要删除），然后执行上面的 clone。
   - 如果它已经是这个仓库：`git checkout main` 然后 `git pull origin main`。
4. 在 `D:\vtuber-battle` 里运行 `dotnet test Engine.Tests`，应该显示全部通过（Failed: 0）。不通过就停下来，把输出原样告诉我，不要改代码去迁就。
5. 检查这台电脑是否装了 Unity（看 Unity Hub 和 `C:\Program Files\Unity\Hub\Editor\` 下的版本文件夹），把结果和版本号告诉我。**不要自己安装 Unity。**
6. 读仓库根目录的 `AGENTS.md`、`docs/design.md`、`docs/TASKS.md`。之后所有工作都按 AGENTS.md 的规则来。

## 二、完成后

把第 4 步的测试结果和第 5 步的 Unity 情况告诉我，然后停下。其他窗口会各自领任务、开 PR，由审查窗口合并，流程见 `AGENTS.md`「分支、PR 与合并」。
