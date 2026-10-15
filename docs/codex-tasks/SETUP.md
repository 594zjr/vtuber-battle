# 给 Codex 的第一条消息：把本地文件夹接到 GitHub 仓库

（整段贴给 Codex 桌面版，只需贴一次。）

---

你在这台 Windows 电脑上为开源项目《开播对决》写代码。仓库是 https://github.com/594zjr/vtuber-battle （默认分支 main）。我们的分工是：你写代码、推分支、开 PR；另一位审核者会拉下来跑测试、修正并合并。你**不要**自己合并 PR，也不要直接往 main 推提交。

## 一、准备（只做一次）

1. 只在 `D:\vtuber-battle` 以及下面第三节提到的 `D:\vtuber-battle-wt\` 里操作。**不要碰 D 盘的任何其他文件夹**（特别是 `D:\游戏`、`D:\seer`）。
2. 检查工具：`git --version`、`dotnet --list-sdks`。缺 git 就 `winget install --id Git.Git -e`；没有 8.x 的 SDK 就 `winget install --id Microsoft.DotNet.SDK.8 -e`。装完新开一个终端再检查。
3. 准备仓库文件夹：
   - 如果 `D:\vtuber-battle` 不存在：`git clone https://github.com/594zjr/vtuber-battle D:\vtuber-battle`。
   - 如果它已存在但不是 git 仓库（之前从压缩包解压的），或者 `git remote get-url origin` 不是上面这个地址：把它改名为 `D:\vtuber-battle.zip-old`（不要删除），然后执行上面的 clone。
   - 如果它已经是这个仓库：`git checkout main` 然后 `git pull origin main`。
4. 在 `D:\vtuber-battle` 里运行 `dotnet test Engine.Tests`，应该显示 13 项全部通过。不通过就停下来，把输出原样告诉我，不要改代码去迁就。
5. 读仓库根目录的 `AGENTS.md`、`docs/design.md`、`docs/TASKS.md`。之后所有工作都按 AGENTS.md 的规则来。

## 二、每个任务的固定流程

我会说“做 T-xx”。你就：

1. `git fetch origin` → 从最新的 `origin/main` 开新分支：`git switch -c task/T-xx origin/main`。
2. 按 `docs/codex-tasks/T-xx.md` 做。把 `Engine.Tests/Pending/` 里对应的测试文件移到 `Engine.Tests/`。
3. `dotnet test Engine.Tests` 全部通过后提交。提交信息第一行以任务编号开头，例如 `T-03 新效果码 PP_REDUCE / FIXED_DAMAGE`。
4. `git push -u origin task/T-xx`。
5. 开 PR（base 是 main）：用 GitHub 插件或 `gh pr create`。标题同提交第一行，描述写：做了什么、改了哪些文件、`dotnet test` 输出的最后一行。如果开不了 PR，只推分支也行，告诉我分支名。
6. 告诉我 PR 链接，然后停下，等下一个任务。不要在同一个分支上接着做别的任务。

审核者可能会往你的 PR 分支上推修正。之后如果我让你改同一个任务，先 `git pull` 那个分支再改。

## 三、同时做几个任务（git worktree）

T-01 到 T-06 互相独立，可以同时做。每个任务用一个单独的文件夹，互不干扰：

```
cd D:\vtuber-battle
git fetch origin
git worktree add D:\vtuber-battle-wt\T-02 -b task/T-02 origin/main
```

然后在 `D:\vtuber-battle-wt\T-02` 里按第二节的第 2～6 步做（第 1 步已经由上面的命令完成）。每个文件夹各自跑 `dotnet test Engine.Tests`。

规则：
- 一个文件夹只做一个任务；一个 Codex 对话只负责一个文件夹。
- 不要在两个文件夹里改同一个任务。
- PR 合并后可以清理：`git worktree remove D:\vtuber-battle-wt\T-02`。
- 合并冲突不用你处理，审核者合并时会解决。
