using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace StreamDuel.Core
{
    public sealed class StreamerCardTrack
    {
        public string Id, Name, Symbol;
    }

    public sealed class StreamerCardStat
    {
        public string Id, Name;
        public int Value, Rank, Total;
    }

    public sealed class StreamerCardSkill
    {
        public string Id, Name, Type, TypeName, TypeSymbol, Category, CategoryName;
        public int Power, Accuracy, Pp, Priority;
        public bool IsFifth;
        public List<EffectText> Effects = new List<EffectText>();
    }

    public sealed class StreamerCardMatchup
    {
        public string Id, Name, Symbol, AttackTypeId;
        public double Multiplier;
    }

    public sealed class StreamerCard
    {
        static readonly string[] StatOrder = { "hp", "atk", "def", "spa", "sdef", "spd" };

        public string Id, Name, PackId, PackName, Notice, PersonaId, PersonaName, PersonaText;
        public List<StreamerCardTrack> Tracks = new List<StreamerCardTrack>();
        public List<StreamerCardStat> Stats = new List<StreamerCardStat>();
        public List<StreamerCardSkill> Skills = new List<StreamerCardSkill>();
        public List<StreamerCardMatchup> Weaknesses = new List<StreamerCardMatchup>();
        public List<StreamerCardMatchup> Strengths = new List<StreamerCardMatchup>();

        public static StreamerCard Build(ContentDb db, string streamerId)
        {
            if (db == null) throw new ArgumentNullException(nameof(db), "内容库不能为空。");
            if (streamerId == null) throw new ArgumentNullException(nameof(streamerId), "主播编号不能为空。");
            if (string.IsNullOrWhiteSpace(streamerId)) throw new ArgumentException("主播编号不能为空。", nameof(streamerId));

            // 通过公开查询保留下架门禁，不能直接在名单中找到对象就允许显示。
            var streamer = db.Streamer(streamerId);
            var persona = db.Personas[streamer.Persona];
            var card = new StreamerCard
            {
                Id = streamer.Id,
                Name = streamer.Name,
                PackId = db.PackId,
                PackName = db.PackName,
                Notice = db.NoticeFor(streamerId),
                PersonaId = streamer.Persona,
                PersonaName = persona.Name,
                PersonaText = Glossary.Build(db).First(entry => entry.Category == "persona" && entry.Id == streamer.Persona).Text
            };
            card.Tracks.Add(Track(db, streamer.Type));
            if (streamer.Type2 != null) card.Tracks.Add(Track(db, streamer.Type2));

            foreach (string id in StatOrder)
            {
                int value = streamer.Stats[id];
                card.Stats.Add(new StreamerCardStat
                {
                    Id = id,
                    Name = db.StatName(id),
                    Value = value,
                    Rank = 1 + db.Streamers.Count(candidate => candidate.Stats[id] > value),
                    Total = db.Streamers.Count
                });
            }

            foreach (string skillId in streamer.Skills) card.Skills.Add(Skill(db, skillId, false));
            card.Skills.Add(Skill(db, streamer.Fifth, true));

            foreach (var target in db.Types.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                double incoming = db.TypeMul(target.Key, streamer.Type, streamer.Type2);
                if (incoming > 1) card.Weaknesses.Add(Matchup(target.Key, target.Value, incoming, target.Key));

                string attackType = streamer.Type;
                double outgoing = db.TypeMul(attackType, target.Key);
                if (streamer.Type2 != null)
                {
                    double secondary = db.TypeMul(streamer.Type2, target.Key);
                    if (secondary > outgoing) { outgoing = secondary; attackType = streamer.Type2; }
                }
                if (outgoing > 1) card.Strengths.Add(Matchup(target.Key, target.Value, outgoing, attackType));
            }
            return card;
        }

        public string ToText()
        {
            return ToText(this);
        }

        public static string ToText(StreamerCard card)
        {
            if (card == null) throw new ArgumentNullException(nameof(card), "主播资料卡不能为空。");
            var text = new StringBuilder();
            text.Append("主播资料卡：").Append(card.Name).Append('\n');
            text.Append("编号：").Append(card.Id).Append('\n');
            text.Append("内容包：").Append(card.PackName).Append("（").Append(card.PackId).Append("）\n");
            if (!string.IsNullOrEmpty(card.Notice)) text.Append(card.Notice).Append('\n');
            text.Append("赛道：").Append(string.Join(" / ", card.Tracks.Select(track => track.Symbol + " " + track.Name))).Append('\n');
            text.Append("六维（本包内降序竞争排名，并列同名次）：\n");
            foreach (var stat in card.Stats)
                text.Append("  ").Append(stat.Name).Append("：").Append(Number(stat.Value))
                    .Append("（第 ").Append(Number(stat.Rank)).Append(" / ").Append(Number(stat.Total)).Append(" 名）\n");

            text.Append("人设：").Append(card.PersonaName).Append('\n');
            text.Append(card.PersonaText).Append("\n\n技能：\n");
            for (int index = 0; index < card.Skills.Count; index++)
            {
                var skill = card.Skills[index];
                text.Append(Number(index + 1)).Append(". ").Append(skill.Name)
                    .Append(skill.IsFifth ? "〔必杀技能〕\n" : "〔常规技能〕\n");
                text.Append("  赛道：").Append(skill.TypeSymbol).Append(' ').Append(skill.TypeName)
                    .Append("；类别：").Append(skill.CategoryName).Append('\n');
                text.Append("  威力：").Append(Number(skill.Power)).Append("；命中：").Append(Number(skill.Accuracy))
                    .Append("%；PP：").Append(Number(skill.Pp)).Append("；先制：")
                    .Append(skill.Priority.ToString("+0;-0;0", CultureInfo.InvariantCulture)).Append('\n');
                if (skill.Effects.Count == 0) text.Append("  无附加效果\n");
                foreach (var effect in skill.Effects)
                {
                    text.Append("  〔").Append(effect.At).Append("〕").Append(effect.Text);
                    if (!string.IsNullOrEmpty(effect.Dispel)) text.Append("〔").Append(effect.Dispel).Append('〕');
                    text.Append('\n');
                }
            }

            text.Append("\n弱点（敌方赛道攻击自身，双赛道按倍率乘积）：\n");
            if (card.Weaknesses.Count == 0) text.Append("  无\n");
            foreach (var matchup in card.Weaknesses) AppendMatchup(text, matchup).Append('\n');

            text.Append("优势（自身主副赛道攻击单赛道，不含实际技能覆盖）：\n");
            if (card.Strengths.Count == 0) text.Append("  无\n");
            foreach (var matchup in card.Strengths)
            {
                var attack = card.Tracks.First(track => track.Id == matchup.AttackTypeId);
                AppendMatchup(text, matchup).Append("（由 ").Append(attack.Symbol).Append(' ').Append(attack.Name).Append(" 攻击）\n");
            }
            return text.ToString();
        }

        static StreamerCardTrack Track(ContentDb db, string id)
        {
            var type = db.Types[id];
            return new StreamerCardTrack { Id = id, Name = type.Name, Symbol = type.Symbol };
        }

        static StreamerCardSkill Skill(ContentDb db, string id, bool isFifth)
        {
            var skill = db.Skills[id];
            var type = db.Types[skill.Type];
            var result = new StreamerCardSkill
            {
                Id = id,
                Name = skill.Name,
                Type = skill.Type,
                TypeName = type.Name,
                TypeSymbol = type.Symbol,
                Category = skill.Cat,
                CategoryName = db.CatNames[skill.Cat],
                Power = skill.Power,
                Accuracy = skill.Acc,
                Pp = skill.Pp,
                Priority = skill.Prio,
                IsFifth = isFifth
            };
            foreach (var effect in skill.Effects)
            {
                var description = Describe.Effect(db, effect);
                result.Effects.Add(new EffectText { At = description.At, Text = description.Text, Dispel = description.Dispel });
            }
            return result;
        }

        static StreamerCardMatchup Matchup(string id, TypeDef type, double multiplier, string attackType)
        {
            return new StreamerCardMatchup
            {
                Id = id, Name = type.Name, Symbol = type.Symbol, Multiplier = multiplier, AttackTypeId = attackType
            };
        }

        static StringBuilder AppendMatchup(StringBuilder text, StreamerCardMatchup matchup)
        {
            return text.Append("  ").Append(matchup.Symbol).Append(' ').Append(matchup.Name)
                .Append(" ×").Append(matchup.Multiplier.ToString("G", CultureInfo.InvariantCulture));
        }

        static string Number(int value) { return value.ToString(CultureInfo.InvariantCulture); }
    }
}
