using System.Text;

namespace StreamDuel.Core
{
    public sealed partial class Battle
    {
        public ulong StateHash()
        {
            var hash = new Fnv64Writer();
            hash.Int(Turn);
            hash.Int((int)Phase);
            foreach (var side in Sides) hash.Int(side.Active);
            foreach (var side in Sides)
            {
                hash.Int(side.Team.Count);
                foreach (var fighter in side.Team)
                {
                    hash.Int(fighter.Hp);
                    hash.Bool(fighter.Fainted);
                    hash.Int(fighter.LastSkillIndex);
                    hash.Int(fighter.Skills.Count);
                    foreach (var skill in fighter.Skills)
                    {
                        hash.Text(skill.Id);
                        hash.Int(skill.Pp);
                    }
                    foreach (var stat in ContentDb.StatKeys) hash.Int(fighter.Stages[stat]);
                    hash.Int(fighter.Statuses.Count);
                    foreach (var status in fighter.Statuses)
                    {
                        hash.Text(status.Id);
                        hash.Int(status.Left);
                    }
                    hash.Int(fighter.Fields.Count);
                    foreach (var field in fighter.Fields)
                    {
                        hash.Text(field.Id);
                        hash.Int(field.Turns);
                        hash.Int(field.Stacks);
                    }
                }
            }
            hash.UInt(rng.State);
            hash.Int(Winner);

            // 首发确认和登场回合也会影响后续结算，不能只比较当前人气与技能。
            hash.Text(Db.PackId);
            for (int s = 0; s < Sides.Length; s++)
            {
                hash.Bool(Need[s]);
                hash.Bool(ActedFirst[s]);
                hash.Int(leadChoices[s]);
                foreach (var fighter in Sides[s].Team)
                {
                    hash.Text(fighter.Def.Id);
                    hash.Int(fighter.Slot);
                    hash.Int(fighter.Side);
                    hash.Int(fighter.MaxHp);
                    hash.Int(fighter.EnteredTurn);
                    hash.Bool(fighter.FirstControlImmunityUsed);
                    hash.Bool(fighter.EndureUsed);
                    foreach (var skill in fighter.Skills)
                    {
                        hash.Int(skill.Max);
                        hash.Bool(skill.Fifth);
                    }
                }
            }
            return hash.Value;
        }

        sealed class Fnv64Writer
        {
            public ulong Value { get; private set; } = 14695981039346656037UL;

            void Byte(byte value)
            {
                Value = unchecked((Value ^ value) * 1099511628211UL);
            }

            public void Bool(bool value) { Byte(value ? (byte)1 : (byte)0); }
            public void Int(int value) { UInt(unchecked((uint)value)); }

            public void UInt(uint value)
            {
                // 固定小端序，避免不同平台的本机字节序影响哈希。
                for (int shift = 0; shift < 32; shift += 8) Byte(unchecked((byte)(value >> shift)));
            }

            public void Text(string value)
            {
                if (value == null) { Int(-1); return; }
                byte[] bytes = Encoding.UTF8.GetBytes(value);
                // 长度前缀避免相邻字符串或可变长度集合拼接出同一字节序列。
                Int(bytes.Length);
                foreach (byte item in bytes) Byte(item);
            }
        }
    }
}
