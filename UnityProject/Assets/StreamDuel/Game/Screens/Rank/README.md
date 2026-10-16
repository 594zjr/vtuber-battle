# 排位与赛季页面

主菜单「排位与赛季」显示本机默认赛制的当前评级与已归档赛季。段位、峰值、定级资格和切季都读取 Core；首次定级完成后跨季保留资格。历史按最近赛季优先排列，可滚动及用翻页按钮浏览。

浏览使用 `FileSaveStore.LoadProfileSnapshot(path, true)`，缺档案显示初始记录但不创建目录、锁或文件。读档失败显示中文提示，并禁用切季；已有快照只作上次读取结果展示。点击「开始新赛季」再确认后，调用 `NewRatingSeason()` 锁内重新读取最新档案并原子保存，避免用页面旧快照覆盖其它进程刚保存的对局。取消与返回不写档。

T-125 在本任务基线尚未合并，暂不显示赛季总结。页面不把普通胜负统计当作排位赛季胜负，不增加账号、匹配或排行榜。

## 运行验收

使用本机已有 Unity 6000.6.5f1。所有输出目录必须位于当前工程 `Logs/T-135` 内，不提交截图或构建产物。验收参数从 Awake 前启用内存存档隔离；真实文件测试也只操作本轮输出目录，正常玩家存档与剪贴板不会被访问。

- Editor：`-batchmode -projectPath <本工作树/UnityProject> -executeMethod StreamDuel.Game.Editor.T135SmokeEditor.Run --t135-smoke-test --t135-output-dir <绝对输出目录> -logFile <工程内日志>`。不要传 `-quit` 或 `-nographics`；桥会进入/退出 Play 并关闭自己的编辑器进程。
- Windows 构建：`-batchmode -quit -projectPath <本工作树/UnityProject> -executeMethod StreamDuel.Game.Editor.T135SmokeEditor.BuildWindows --t135-build-dir <绝对构建目录> -logFile <工程内日志>`。
- 发布包：`StreamDuel.exe -screen-fullscreen 0 --t135-smoke-test --t135-output-dir <绝对输出目录> -logFile <工程内日志>`。截图需要真实渲染窗口，完成后自动退出。
- 引擎回归：`dotnet test Engine.Tests`。

自动验收通过正式 UI Toolkit 的导航提交/取消事件驱动按钮，覆盖两个分辨率、只读浏览、错误恢复、切季及完整对战，并保存 Console 计数、截图和布局边界。自动布局检查不能代替视觉复核；提交前逐图检查文字、徽章和历史列表。
