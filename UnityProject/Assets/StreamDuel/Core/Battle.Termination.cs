using System;

namespace StreamDuel.Core
{
    public sealed partial class Battle
    {
        readonly bool[] drawOffers = new bool[2];
        readonly int[] lastDrawOfferTurns = { -5, -5 };

        public void Surrender(int side)
        {
            ValidateTerminationSide(side);
            ResolveSurrenders(side == 0, side == 1);
        }

        public void ResolveSurrenders(bool side0, bool side1)
        {
            RequireUnfinished();
            if (!side0 && !side1) throw new ArgumentException("至少一方须提交投降。");
            RecordTermination(new BattleRecordEvent
            {
                Type = BattleRecordEventType.Surrender,
                Side = side0 && side1 ? -1 : side0 ? 0 : 1
            });
            FinishByAgreement(side0 && side1 ? -1 : side0 ? 1 : 0, "投降",
                side0 && side1 ? "双方同时投降，平局" : Sides[side0 ? 0 : 1].Name + " 投降，" + Sides[side0 ? 1 : 0].Name + " 获胜");
        }

        public bool HasDrawOffer(int side)
        {
            ValidateTerminationSide(side);
            return Phase != Phase.Over && drawOffers[side];
        }

        public bool CanOfferDraw(int side)
        {
            ValidateTerminationSide(side);
            return Phase != Phase.Over && !drawOffers[side] && (long)Turn - lastDrawOfferTurns[side] >= 5;
        }

        public void OfferDraw(int side)
        {
            ValidateTerminationSide(side);
            if (!CanOfferDraw(side)) throw new InvalidOperationException("现在不能提议和局：对战已结束、已有待答提议或距上次不足 5 回合。");
            RecordTermination(new BattleRecordEvent { Type = BattleRecordEventType.DrawOffer, Side = side });
            drawOffers[side] = true;
            lastDrawOfferTurns[side] = Turn;
            Add(TerminationNode(), Sides[side].Name + " 提议和局", side: side);
        }

        public void RespondDraw(int side, bool accept)
        {
            ValidateTerminationSide(side);
            RequireUnfinished();
            if (!drawOffers[1 - side]) throw new InvalidOperationException("没有对方的和局提议需要回应。");
            RecordTermination(new BattleRecordEvent { Type = BattleRecordEventType.DrawResponse, Side = side, Accepted = accept });
            drawOffers[1 - side] = false;
            if (accept) FinishByAgreement(-1, "和局", Sides[side].Name + " 同意和局，双方平局");
            else Add(TerminationNode(), Sides[side].Name + " 拒绝和局，继续对战", side: side);
        }

        void RecordTermination(BattleRecordEvent entry)
        {
            // 终止事件升级的是录像格式，显式旧规则对局的结算不能随之改变。
            Record.Version = Math.Max(Record.Version, BattleRecord.TerminationVersion);
            if (RulesVersion < BattleRecord.TerminationVersion) Record.RulesVersion = RulesVersion;
            entry.RngState = rng.State;
            Record.Events.Add(entry);
        }

        void FinishByAgreement(int winner, string reason, string text)
        {
            Phase = Phase.Over;
            Winner = winner;
            EndReason = reason;
            Need[0] = Need[1] = false;
            drawOffers[0] = drawOffers[1] = false;
            Add(Node.Over, text, "turn");
            Emit(BattleEventKind.End, side: Winner);
        }

        string TerminationNode()
        {
            return Phase == Phase.Replace ? Node.Replace : Phase == Phase.Ban || Phase == Phase.Lead ? Node.Start : Node.TurnStart;
        }

        void RequireUnfinished()
        {
            if (Phase == Phase.Over) throw new InvalidOperationException("对战已经结束。");
        }

        static void ValidateTerminationSide(int side)
        {
            if (side < 0 || side > 1) throw new ArgumentOutOfRangeException(nameof(side), "对战方只能是 0 或 1。");
        }

        void CopyTerminationFrom(Battle source)
        {
            Array.Copy(source.drawOffers, drawOffers, 2);
            Array.Copy(source.lastDrawOfferTurns, lastDrawOfferTurns, 2);
        }

        void HashTermination(Fnv64Writer hash)
        {
            // 未发生过和局协商时不追加字节，冻结的旧录像哈希保持原样。
            if (lastDrawOfferTurns[0] == -5 && lastDrawOfferTurns[1] == -5) return;
            hash.Text("StreamDuel.Draw.v1");
            for (int side = 0; side < 2; side++)
            {
                hash.Bool(drawOffers[side]);
                hash.Int(lastDrawOfferTurns[side]);
            }
        }
    }

    public static partial class Ai
    {
        public static bool AcceptDraw(Battle battle, int side)
        {
            if (battle == null) throw new ArgumentNullException(nameof(battle));
            if (side < 0 || side > 1) throw new ArgumentOutOfRangeException(nameof(side));
            long own = 0, other = 0;
            foreach (var fighter in battle.Sides[side].Team) own += Math.Max(0, fighter.Hp);
            foreach (var fighter in battle.Sides[1 - side].Team) other += Math.Max(0, fighter.Hp);
            // 十进制比较保留恰好领先 30% 的边界，也避免累加后整数乘法溢出。
            return (decimal)own * 10 <= (decimal)other * 13;
        }
    }
}
