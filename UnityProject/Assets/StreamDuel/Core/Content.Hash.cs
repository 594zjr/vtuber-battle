using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace StreamDuel.Core
{
    public sealed partial class ContentDb
    {
        public string ContentHash()
        {
            var hash = new ContentHashWriter();
            hash.Text("StreamDuel.Content.v1");
            hash.Text(PackId);
            hash.Text(PackName);
            hash.Text(PackNotice);
            hash.Map(StatNames, hash.Text);
            hash.Map(CatNames, hash.Text);
            hash.Map(Types, hash.Type);
            hash.Map(Chart, row => hash.Map(row, hash.Double));
            hash.Map(Statuses, hash.Status);
            hash.Map(Fields, hash.Field);
            hash.Map(Personas, hash.Persona);
            hash.Map(Skills, hash.Skill);
            hash.Skill(Struggle);
            // 名单是按 id 引用的定义；技能槽和效果的列表顺序则会改变结算。
            hash.List(Streamers == null ? null : Streamers.OrderBy(s => s == null ? null : s.Id, StringComparer.Ordinal).ToList(), hash.Streamer);
            hash.Map(Seasons, hash.Season);
            // 未设置风格的旧内容保留原指纹，显式风格在独立尾段绑定主播 id。
            var styled = Streamers == null ? null : Streamers.Where(s => s != null && s.AiStyle != null)
                .OrderBy(s => s.Id, StringComparer.Ordinal).ToList();
            if (styled != null && styled.Count > 0)
            {
                hash.Text("StreamDuel.Content.AiStyles.v1");
                hash.List(styled, s => { hash.Text(s.Id); hash.Text(s.AiStyle); });
            }
            WriteMergeHash(hash);
            return hash.Value.ToString("x16", CultureInfo.InvariantCulture);
        }

        sealed class ContentHashWriter
        {
            public ulong Value { get; private set; } = 14695981039346656037UL;

            void Byte(byte value)
            {
                Value = unchecked((Value ^ value) * 1099511628211UL);
            }

            public void Bool(bool value) { Byte(value ? (byte)1 : (byte)0); }

            public void Int(int value)
            {
                uint bits = unchecked((uint)value);
                for (int shift = 0; shift < 32; shift += 8) Byte(unchecked((byte)(bits >> shift)));
            }

            public void Double(double value)
            {
                // 使用固定小端位表示，避免小数格式、精度和本机字节序改变指纹。
                ulong bits = unchecked((ulong)BitConverter.DoubleToInt64Bits(value));
                for (int shift = 0; shift < 64; shift += 8) Byte(unchecked((byte)(bits >> shift)));
            }

            public void Text(string value)
            {
                if (value == null) { Int(-1); return; }
                byte[] bytes = Encoding.UTF8.GetBytes(value);
                // 长度前缀使空值、空字符串及相邻字段的边界保持不同。
                Int(bytes.Length);
                foreach (byte item in bytes) Byte(item);
            }

            bool Present(object value)
            {
                Bool(value != null);
                return value != null;
            }

            public void Map<T>(IDictionary<string, T> values, Action<T> write)
            {
                if (values == null) { Int(-1); return; }
                Int(values.Count);
                foreach (var pair in values.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    Text(pair.Key);
                    write(pair.Value);
                }
            }

            public void List<T>(IList<T> values, Action<T> write)
            {
                if (values == null) { Int(-1); return; }
                Int(values.Count);
                foreach (var value in values) write(value);
            }

            public void Type(TypeDef value)
            {
                if (!Present(value)) return;
                Text(value.Id);
                Text(value.Name);
                Text(value.Color);
            }

            public void Status(StatusDef value)
            {
                if (!Present(value)) return;
                Text(value.Id);
                Text(value.Name);
                Text(value.Cat);
                Text(value.Desc);
                Int(value.Skip);
                Int(value.Turns);
                Double(value.Dot);
                Double(value.InteractMul);
                Bool(value.Leech);
                Bool(value.Cleanse);
                Bool(value.WakeOnHit);
            }

            public void Field(FieldDef value)
            {
                if (!Present(value)) return;
                Text(value.Id);
                Text(value.Name);
                Text(value.Kind);
                Text(value.When);
                Text(value.Desc);
                Bool(value.Dispel);
                Bool(value.Consume);
                Bool(value.ImmuneControl);
                Bool(value.ImmuneWeak);
                Bool(value.PreventSwitch);
                Double(value.DmgTakenMul);
                Double(value.Heal);
                Double(value.Dot);
                Double(value.TalentMul);
                Double(value.ReflectDamage);
                Int(value.Prio);
                Int(value.MaxStacks);
            }

            public void Condition(CondDef value)
            {
                if (!Present(value)) return;
                Text(value.Kind);
                Double(value.Value);
            }

            public void Effect(EffectDef value)
            {
                if (!Present(value)) return;
                Text(value.At);
                Text(value.Code);
                Text(value.Target);
                Text(value.Status);
                Text(value.Field);
                Int(value.Chance);
                Int(value.Turns);
                Int(value.N);
                Double(value.Frac);
                Double(value.Mul);
                Double(value.Per);
                Bool(value.Consume);
                Map(value.Changes, Int);
                Condition(value.Cond);
            }

            public void Skill(SkillDef value)
            {
                if (!Present(value)) return;
                Text(value.Id);
                Text(value.Name);
                Text(value.Type);
                Text(value.Cat);
                Bool(value.PackDefined);
                Int(value.Power);
                Int(value.Pp);
                Int(value.Acc);
                Int(value.Prio);
                List(value.Effects, Effect);
            }

            public void Persona(PersonaDef value)
            {
                if (!Present(value)) return;
                Text(value.Id);
                Text(value.Name);
                Text(value.Code);
                Text(value.Node);
                Text(value.Desc);
                Text(value.Status);
                Text(value.Field);
                Bool(value.PackDefined);
                Map(value.Params, Double);
            }

            public void Streamer(StreamerDef value)
            {
                if (!Present(value)) return;
                Text(value.Id);
                Text(value.Name);
                Text(value.Type);
                Text(value.Type2);
                Text(value.Persona);
                Text(value.Fifth);
                Text(value.Blurb);
                Text(value.Org);
                Map(value.Stats, Int);
                List(value.Skills, Text);
            }

            public void LimitedPool(LimitedPoolDef value)
            {
                if (!Present(value)) return;
                Int(value.Max);
                List(value.Ids, Text);
            }

            public void Season(SeasonDef value)
            {
                if (!Present(value)) return;
                Text(value.Name);
                List(value.Banned, Text);
                List(value.Limited, LimitedPool);
            }
        }
    }
}
