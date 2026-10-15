using System;
using System.Collections.Generic;
using System.Reflection;
using StreamDuel.Core;

namespace StreamDuel.Game
{
    // T-67 仅依赖 T-59；T-60 未合并时沿用空时间线，合并后直接委托 Core，画面不解释事件规则。
    public static class BattleAnimationTimeline
    {
        static readonly Func<IReadOnlyList<BattleEvent>, int, int, double, List<AnimCue>> build = FindBuilder();

        public static bool IsAvailable => build != null;

        public static IReadOnlyList<AnimCue> Build(Battle battle, int from, int to, double speed)
        {
            if (battle == null) throw new ArgumentNullException(nameof(battle));
            if (from < 0 || to < from || to > battle.Events.Count) throw new ArgumentOutOfRangeException(nameof(from));
            if (double.IsNaN(speed) || double.IsInfinity(speed)) throw new ArgumentOutOfRangeException(nameof(speed));
            return build == null ? (IReadOnlyList<AnimCue>)Array.Empty<AnimCue>() : build(battle.Events, from, to, speed);
        }

        static Func<IReadOnlyList<BattleEvent>, int, int, double, List<AnimCue>> FindBuilder()
        {
            var type = typeof(Battle).Assembly.GetType("StreamDuel.Core.BattleTimeline");
            var method = type?.GetMethod("Build", BindingFlags.Public | BindingFlags.Static, null,
                new[] { typeof(IReadOnlyList<BattleEvent>), typeof(int), typeof(int), typeof(double) }, null);
            return method == null ? null : (Func<IReadOnlyList<BattleEvent>, int, int, double, List<AnimCue>>)
                Delegate.CreateDelegate(typeof(Func<IReadOnlyList<BattleEvent>, int, int, double, List<AnimCue>>), method);
        }
    }
}
