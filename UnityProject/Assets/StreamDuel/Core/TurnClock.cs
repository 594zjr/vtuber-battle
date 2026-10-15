using System;

namespace StreamDuel.Core
{
    public sealed class TurnClock
    {
        readonly double turnSeconds;
        readonly double[] turnLeft = new double[2];
        readonly double[] bankLeft = new double[2];
        readonly bool[] submitted = new bool[2];
        readonly bool[] timedOut = new bool[2];

        public TurnClock(double turnSeconds, double bankSeconds)
        {
            ValidateSeconds(turnSeconds, nameof(turnSeconds));
            ValidateSeconds(bankSeconds, nameof(bankSeconds));
            this.turnSeconds = turnSeconds;
            bankLeft[0] = bankSeconds;
            bankLeft[1] = bankSeconds;
            StartTurn();
        }

        public void StartTurn()
        {
            for (int side = 0; side < 2; side++)
            {
                turnLeft[side] = turnSeconds;
                submitted[side] = false;
                timedOut[side] = turnLeft[side] == 0 && bankLeft[side] == 0;
            }
        }

        public bool Tick(int side, double seconds)
        {
            ValidateSide(side);
            ValidateSeconds(seconds, nameof(seconds));
            if (submitted[side] || timedOut[side]) return timedOut[side];

            // 分段扣除，避免两个合法的大预算相加时溢出为无穷大。
            double fromTurn = Math.Min(turnLeft[side], seconds);
            turnLeft[side] -= fromTurn;
            seconds -= fromTurn;
            bankLeft[side] = Math.Max(0, bankLeft[side] - seconds);
            timedOut[side] = turnLeft[side] == 0 && bankLeft[side] == 0;
            return timedOut[side];
        }

        public void Submit(int side)
        {
            ValidateSide(side);
            submitted[side] = true;
        }

        public double TurnLeft(int side)
        {
            ValidateSide(side);
            return turnLeft[side];
        }

        public double BankLeft(int side)
        {
            ValidateSide(side);
            return bankLeft[side];
        }

        public bool TimedOut(int side)
        {
            ValidateSide(side);
            return timedOut[side];
        }

        public static BattleAction TimeoutAction(Battle b, int side)
        {
            ValidateBattleSide(b, side);
            return BattleAction.Pass();
        }

        public static int TimeoutReplacement(Battle b, int side)
        {
            ValidateBattleSide(b, side);
            var state = b.Sides[side];
            for (int index = 0; index < state.Team.Count; index++)
                if (index != state.Active && !state.Team[index].Fainted && state.Team[index].Hp > 0)
                    return index;
            return -1;
        }

        static void ValidateBattleSide(Battle b, int side)
        {
            if (b == null) throw new ArgumentNullException(nameof(b), "对战不能为空");
            ValidateSide(side);
        }

        static void ValidateSide(int side)
        {
            if (side < 0 || side >= 2)
                throw new ArgumentOutOfRangeException(nameof(side), "对战方下标必须是 0 或 1");
        }

        static void ValidateSeconds(double seconds, string parameter)
        {
            if (seconds < 0 || double.IsNaN(seconds) || double.IsInfinity(seconds))
                throw new ArgumentOutOfRangeException(parameter, "时间必须是有限的非负秒数");
        }
    }
}
