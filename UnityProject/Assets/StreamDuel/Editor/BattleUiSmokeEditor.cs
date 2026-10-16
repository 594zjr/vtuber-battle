public static class BattleUiSmokeEditor
{
    public static void Run()
    {
        // 与路由验收共用尺寸与退出后 Console 检查，不能只采样停止 Play 前的日志。
        UiSmokeEditor.Run();
    }
}
