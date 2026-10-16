using System;
using System.Collections.Generic;

namespace StreamDuel.Core
{
    public sealed partial class Battle
    {
        void SwapStages(Ctx ctx, Fighter target)
        {
            if (ctx.P.Fainted || target.Fainted) return;
            if (target == ctx.P)
            {
                Add(ctx.Node, "能力交换：目标是自身，能力等级不变", "stage", ctx.S);
                return;
            }

            var parts = new List<string>();
            var ownDeltas = new Dictionary<string, int>();
            var targetDeltas = new Dictionary<string, int>();
            // 同一项先保存双方原值，再赋值，避免把已交换的数值写回另一方。
            foreach (string stat in ContentDb.StatKeys)
            {
                int ownBefore = ctx.P.Stages[stat];
                int targetBefore = target.Stages[stat];
                int ownAfter = ClampStage(targetBefore);
                int targetAfter = ClampStage(ownBefore);
                ctx.P.Stages[stat] = ownAfter;
                target.Stages[stat] = targetAfter;
                ownDeltas[stat] = ownAfter - ownBefore;
                targetDeltas[stat] = targetAfter - targetBefore;
                parts.Add(Db.StatName(stat) + "：" + ctx.P.Name + " " + ownBefore + " → " + ownAfter
                    + "，" + target.Name + " " + targetBefore + " → " + targetAfter);
            }
            Add(ctx.Node, "能力交换：" + string.Join("；", parts), "stage", ctx.S);
            foreach (string stat in ContentDb.StatKeys)
            {
                if (ownDeltas[stat] != 0) Emit(BattleEventKind.StageChange, ctx.P, stat, ownDeltas[stat]);
                if (targetDeltas[stat] != 0) Emit(BattleEventKind.StageChange, target, stat, targetDeltas[stat]);
            }
        }

        void CopyStages(Ctx ctx, Fighter target)
        {
            if (ctx.P.Fainted || target.Fainted) return;
            if (target == ctx.P)
            {
                Add(ctx.Node, "复制强化：目标是自身，能力等级不变", "stage", ctx.S);
                return;
            }

            var parts = new List<string>();
            var deltas = new Dictionary<string, int>();
            foreach (string stat in ContentDb.StatKeys)
            {
                int copied = target.Stages[stat];
                if (copied <= 0) continue;
                int before = ctx.P.Stages[stat];
                int after = ClampStage(copied);
                ctx.P.Stages[stat] = after;
                deltas[stat] = after - before;
                parts.Add(Db.StatName(stat) + " " + before + " → " + after + "（目标 +" + copied + "）");
            }
            if (parts.Count == 0)
            {
                Add(ctx.Node, "复制强化：" + target.Name + " 没有正向能力等级，" + ctx.P.Name + " 的能力等级不变", "stage", ctx.S);
                return;
            }
            Add(ctx.Node, "复制强化：" + ctx.P.Name + " 复制 " + target.Name + " 的正向能力等级，"
                + string.Join("；", parts) + "（其余能力保留，目标不变）", "stage", ctx.S);
            foreach (string stat in ContentDb.StatKeys)
                if (deltas.TryGetValue(stat, out int delta) && delta != 0)
                    Emit(BattleEventKind.StageChange, ctx.P, stat, delta);
        }

        void RestorePp(Ctx ctx, int amount)
        {
            if (ctx.P.Fainted) return;
            amount = Math.Max(0, amount);
            bool changed = false;
            foreach (var slot in ctx.P.Skills)
            {
                if (slot.Fifth) continue;
                int before = slot.Pp;
                int after = (int)Math.Min(slot.Max, (long)before + amount);
                int restored = after - before;
                if (restored <= 0) continue;
                slot.Pp = after;
                changed = true;
                Add(ctx.Node, "恢复 PP：" + ctx.P.Name + " 的「" + Db.Skills[slot.Id].Name + "」PP +" + restored
                    + "（" + after + "/" + slot.Max + "）", "info", ctx.P.Side);
                Emit(BattleEventKind.PpChange, ctx.P, slot.Id, restored);
            }
            if (!changed)
                Add(ctx.Node, "恢复 PP：" + ctx.P.Name + " 的普通技能 PP 未变化（恢复量 " + amount
                    + "，不超过各技能上限，第五技能不恢复）", "info", ctx.P.Side);
        }
    }
}
