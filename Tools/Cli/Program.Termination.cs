using System;
using StreamDuel.Core;

namespace StreamDuel.Cli
{
    internal static partial class Program
    {
        private sealed class BattleFinishedException : Exception { }

        private static void PrintTerminationCommands(bool network = false)
        {
            Console.WriteLine("对战指令：/surrender 投降并结算；/draw 提议和局；q 退出未完成对局。");
            if (network)
                Console.WriteLine("收到求和后输入 /draw accept 同意，或 /draw reject 拒绝；等待对手时也可使用这些指令。");
        }

        private static bool HandleLocalCommand(Battle battle, int side, string line)
        {
            string command = line.Trim();
            if (!command.StartsWith("/", StringComparison.Ordinal)) return false;
            if (command.Equals("/surrender", StringComparison.OrdinalIgnoreCase))
            {
                battle.Surrender(side);
                throw new BattleFinishedException();
            }
            if (command.Equals("/draw", StringComparison.OrdinalIgnoreCase))
            {
                if (!battle.CanOfferDraw(side))
                {
                    Console.WriteLine("现在不能求和：已有待答提议，或距离上次提议不足 5 回合。");
                    return true;
                }
                battle.OfferDraw(side);
                bool accept = Ai.AcceptDraw(battle, 1 - side);
                battle.RespondDraw(1 - side, accept);
                Console.WriteLine(accept ? "电脑同意和局。" : "电脑拒绝和局，继续选择；每 5 回合最多提议一次。");
                if (battle.Phase == Phase.Over) throw new BattleFinishedException();
                return true;
            }
            Console.WriteLine("可用指令：/surrender 投降；/draw 向电脑求和（电脑自动回应）。");
            return true;
        }

        private static bool HandleHotseatCommand(HotseatSession session, string line)
        {
            string command = line.Trim();
            if (!command.StartsWith("/", StringComparison.Ordinal)) return false;
            if (command.Equals("/surrender", StringComparison.OrdinalIgnoreCase))
            {
                session.Surrender();
                throw new BattleFinishedException();
            }
            Console.WriteLine("同屏模式暂不支持求和；输入 /surrender 投降，或继续选择。");
            return true;
        }

        private static bool HandleNetworkCommand(NetSession session, string line, Action closing, Action offering)
        {
            string command = line.Trim();
            if (!command.StartsWith("/", StringComparison.Ordinal)) return false;
            if (command.Equals("/surrender", StringComparison.OrdinalIgnoreCase))
            {
                if (!session.CanSurrender)
                {
                    Console.WriteLine("正在同步，请稍候再输入 /surrender。");
                    return true;
                }
                session.Submit(BattleAction.Surrender());
                closing();
                Console.WriteLine("已提交投降，等待双方确认并保存结果……");
                return true;
            }
            if (command.Equals("/draw", StringComparison.OrdinalIgnoreCase))
            {
                if (!session.CanOfferDraw)
                    Console.WriteLine("现在不能求和：正在同步、已有待答提议，或距离上次提议不足 5 回合。");
                else
                {
                    session.OfferDraw();
                    offering();
                    Console.WriteLine("已提议和局，等待对方回应；拒绝后继续原阶段。");
                }
                return true;
            }
            bool accept = command.Equals("/draw accept", StringComparison.OrdinalIgnoreCase);
            if (accept || command.Equals("/draw reject", StringComparison.OrdinalIgnoreCase))
            {
                if (!session.DrawOffered) Console.WriteLine("当前没有对方的和局提议需要回应。");
                else if (!session.CanRespondDraw) Console.WriteLine("正在同步，请稍候再回应和局提议。");
                else
                {
                    session.RespondDraw(accept);
                    if (accept) closing();
                    Console.WriteLine(accept ? "已同意和局，等待双方确认并保存结果……" : "已拒绝和局，继续对战。");
                }
                return true;
            }
            Console.WriteLine("可用指令：/surrender、/draw、/draw accept、/draw reject。");
            return true;
        }

        private static void ShowNetworkDraw(NetSession session, bool automatic, ref bool notified)
        {
            if (!session.DrawOffered)
            {
                notified = false;
                return;
            }
            if (!notified && session.CanRespondDraw)
            {
                Console.WriteLine("对方提议和局：/draw accept 同意，/draw reject 拒绝。");
                notified = true;
            }
            if (automatic && session.CanRespondDraw)
            {
                bool accept = Ai.AcceptDraw(session.Battle, session.MySide);
                session.RespondDraw(accept);
                Console.WriteLine(accept ? "电脑同意和局。" : "电脑拒绝和局，继续对战。");
            }
        }
    }
}
