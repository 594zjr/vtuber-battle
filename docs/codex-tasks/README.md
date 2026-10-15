# 给 Codex 的任务

第一次使用先把 [SETUP.md](SETUP.md) 整段贴给 Codex（克隆仓库、装工具、分支和 PR 流程、并行做法）。之后每次对它说“做 T-xx”即可，或者把对应的 T-xx.md 整段贴进去。一条任务一个分支、一个 PR。

这 6 条互相独立，可以同时开（每条一个 git worktree 文件夹、一个 Codex 对话，见 SETUP.md 第三节）；T-02、T-03、T-05 都会动 `Battle`，后合并的那个可能要解决冲突，我来处理。

- 窗口 0 一次性准备：SETUP.md
- 任务窗口：按 AGENTS.md「分支、PR 与合并」自己领任务（T-01 起，做完自动领下一个）
- 审查窗口：REVIEWER.md
- 第一批：T-01～T-06（互相独立）
- 第二批：T-07～T-17（依赖见 docs/TASKS.md）

