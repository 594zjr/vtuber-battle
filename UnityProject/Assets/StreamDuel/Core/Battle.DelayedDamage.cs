namespace StreamDuel.Core
{
    public sealed partial class Battle
    {
        void AddDelayedDamage(Ctx ctx, Fighter target, int turns, int amount)
        {
            if (target.Fainted) return;
            if (turns < 1 || amount < 0)
                throw new KeyError("DELAYED_DAMAGE 的 turns 必须至少为 1，n 不能为负数");
            if (!Db.Fields.ContainsKey(ContentDb.DelayedDamageFieldId))
                throw new KeyError("DELAYED_DAMAGE 缺少效果定义；新增技能后须先校验内容库");

            var field = target.FieldOf(ContentDb.DelayedDamageFieldId);
            bool refresh = field != null;
            if (field == null)
            {
                field = new FieldInst { Id = ContentDb.DelayedDamageFieldId };
                target.Fields.Add(field);
            }
            field.Turns = turns;
            field.PendingDamage = amount;
            Add(ctx.Node, target.Name + (refresh ? " 刷新" : " 获得") + "「" + Db.Fields[field.Id].Name + "」（"
                + turns + " 回合，含本回合；到期固定伤害 " + amount + "，可被消回合，下场清除）", "field", target.Side);
            Emit(BattleEventKind.FieldAdd, target, field.Id, turns);
        }

        void TickDelayedDamage(Fighter target, FieldInst field)
        {
            field.Turns--;
            if (field.Turns > 0)
            {
                Add(Node.TurnEnd, target.Name + " 的「" + Db.Fields[field.Id].Name + "」剩余 " + field.Turns
                    + " 回合，到期固定伤害 " + field.PendingDamage, "field", target.Side);
                return;
            }

            // 先移除再伤害，避免下播或终局后仍保留可再次结算的待办效果。
            target.Fields.Remove(field);
            Add(Node.TurnEnd, target.Name + " 的「" + Db.Fields[field.Id].Name + "」到期（固定伤害 " + field.PendingDamage + "）", "field", target.Side);
            Emit(BattleEventKind.FieldRemove, target, field.Id);
            Damage(target, field.PendingDamage, Node.TurnEnd, target.Name + " 因「" + Db.Fields[field.Id].Name + "」人气 -{n}", true);
        }

        void ClearDelayedDamageOnFaint(Fighter target, string node)
        {
            var field = target.FieldOf(ContentDb.DelayedDamageFieldId);
            if (field == null) return;
            target.Fields.Remove(field);
            Add(node, target.Name + " 下播，清除「" + Db.Fields[field.Id].Name + "」（待结算固定伤害 " + field.PendingDamage + "）", "field", target.Side);
            Emit(BattleEventKind.FieldRemove, target, field.Id);
        }
    }
}
