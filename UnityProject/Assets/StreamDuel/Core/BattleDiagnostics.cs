using System;

namespace StreamDuel.Core
{
    /// <summary>当前线程的复制次数，仅供性能工具取差；不属于对战状态，不参与录像和随机流。</summary>
    public static class BattleDiagnostics
    {
        [ThreadStatic] static long cloneCount;
        [ThreadStatic] static long hardCopyCount;
        [ThreadStatic] static long simulationCloneCount;

        public static long CurrentThreadCloneCount { get { return cloneCount; } }
        public static long CurrentThreadHardCopyCount { get { return hardCopyCount; } }
        public static long CurrentThreadSimulationCloneCount { get { return simulationCloneCount; } }

        internal static void OnClone() { cloneCount++; }
        internal static void OnHardCopy() { hardCopyCount++; }
        internal static void OnSimulationClone() { simulationCloneCount++; }
    }
}
