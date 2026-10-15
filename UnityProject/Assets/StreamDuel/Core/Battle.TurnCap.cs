using System;
using System.Linq;
using System.Numerics;

namespace StreamDuel.Core
{
    public sealed partial class Battle
    {
        // 判胜状态不从可编辑的统计快照或录像历史反推，AI 的局部副本也需要保留它。
        readonly long[] totalDamageDealt = new long[2];

        public int EffectiveTurnCap { get { return HasModifier("turn_cap_60") ? 60 : TurnCap; } }
        public string EndReason { get; private set; } = "";

        void FinishAtTurnCap()
        {
            Phase = Phase.Over;
            EndReason = "回合上限";
            if (!HasModifier("turn_cap_60"))
            {
                // 默认规则的迁移需要升级录像与联网协议，先保留旧记录的结算和日志。
                double r0 = Sides[0].Team.Sum(p => p.HpRatio), r1 = Sides[1].Team.Sum(p => p.HpRatio);
                Winner = r0 == r1 ? -1 : r0 > r1 ? 0 : 1;
                Add(Node.Over, "达到 " + TurnCap + " 回合上限，按剩余人气判定", "turn");
            }
            else
            {
                int comparison = Sides[0].Team.Count(p => !p.Fainted).CompareTo(Sides[1].Team.Count(p => !p.Fainted));
                string reason = "按剩余人数判定";
                if (comparison == 0)
                {
                    comparison = CompareRemainingHp();
                    reason = "双方剩余人数相同，按剩余人气判定";
                    if (comparison == 0)
                    {
                        comparison = totalDamageDealt[0].CompareTo(totalDamageDealt[1]);
                        reason = comparison == 0 ? "双方剩余人数、剩余人气比例总和和累计伤害均相同" :
                            "双方剩余人数和剩余人气比例总和相同，按累计伤害判定";
                    }
                }
                Winner = comparison == 0 ? -1 : comparison > 0 ? 0 : 1;
                Add(Node.Over, "回合上限（" + EffectiveTurnCap + " 回合）：" + reason + "，" +
                    (Winner == -1 ? "平局" : Sides[Winner].Name + " 获胜"), "turn");
            }
            Emit(BattleEventKind.End, side: Winner);
        }

        int CompareRemainingHp()
        {
            // 有理数精确求差，避免 1/10+2/10 与 3/10 的舍入误差绕过累计伤害判定。
            BigInteger numerator = BigInteger.Zero, denominator = BigInteger.One;
            for (int side = 0; side < Sides.Length; side++)
                foreach (var fighter in Sides[side].Team)
                {
                    if (fighter.Fainted || fighter.Hp <= 0 || fighter.MaxHp <= 0) continue;
                    numerator = numerator * fighter.MaxHp + (side == 0 ? 1 : -1) * (BigInteger)fighter.Hp * denominator;
                    denominator *= fighter.MaxHp;
                    BigInteger divisor = BigInteger.GreatestCommonDivisor(BigInteger.Abs(numerator), denominator);
                    numerator /= divisor;
                    denominator /= divisor;
                }
            return numerator.Sign;
        }

        void CopyTurnCapState(Battle source)
        {
            EndReason = source.EndReason;
            Array.Copy(source.totalDamageDealt, totalDamageDealt, totalDamageDealt.Length);
        }
    }
}
