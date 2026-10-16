using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace StreamDuel.Core
{
    public sealed class Ruleset
    {
        public string Id, Name, Season;
        public bool BanOne, LeadPick;
        public int BestOf = 1;
        public List<string> Modifiers;
        public int TurnSeconds, BankSeconds;

        public void Validate(ContentDb db)
        {
            if (db == null) throw new ArgumentNullException(nameof(db));
            if (BestOf != 1 && BestOf != 3 && BestOf != 5) throw new KeyError("赛制 bestOf 只能是 1、3 或 5。");
            if (Season != null && !db.Seasons.ContainsKey(Season)) throw new KeyError("赛季不存在：" + Season);
            BattleModifiers.Normalize(Modifiers);
            if (TurnSeconds < 0 || BankSeconds < 0) throw new KeyError("赛制的回合时间和总时间不能为负数。");
        }

        public List<string> CheckTeam(ContentDb db, IList<string> team)
        {
            if (db == null) throw new ArgumentNullException(nameof(db));
            return TeamCheck.Validate(db, Season, team);
        }

        public string ToJson()
        {
            var text = new StringBuilder("{\"id\":");
            WriteText(text, Id);
            text.Append(",\"name\":");
            WriteText(text, Name);
            text.Append(",\"season\":");
            WriteText(text, Season);
            text.Append(",\"banOne\":").Append(BanOne ? "true" : "false");
            text.Append(",\"leadPick\":").Append(LeadPick ? "true" : "false");
            text.Append(",\"bestOf\":").Append(BestOf.ToString(CultureInfo.InvariantCulture));
            text.Append(",\"modifiers\":");
            if (Modifiers == null) text.Append("null");
            else
            {
                text.Append('[');
                for (int i = 0; i < Modifiers.Count; i++)
                {
                    if (Modifiers[i] == null) throw new FormatException("赛制 modifiers 必须只包含字符串。");
                    if (i > 0) text.Append(',');
                    WriteText(text, Modifiers[i]);
                }
                text.Append(']');
            }
            text.Append(",\"turnSeconds\":").Append(TurnSeconds.ToString(CultureInfo.InvariantCulture));
            return text.Append(",\"bankSeconds\":").Append(BankSeconds.ToString(CultureInfo.InvariantCulture)).Append('}').ToString();
        }

        public static Ruleset FromJson(string json)
        {
            var source = MiniJson.Parse(json) as Dictionary<string, object>
                ?? throw new FormatException("赛制必须是 JSON 对象。");
            var ruleset = new Ruleset
            {
                Id = ReadText(source, "id"),
                Name = ReadText(source, "name"),
                Season = ReadText(source, "season"),
                BanOne = ReadBool(source, "banOne"),
                LeadPick = ReadBool(source, "leadPick"),
                BestOf = ReadInt(source, "bestOf", 1),
                TurnSeconds = ReadInt(source, "turnSeconds", 0),
                BankSeconds = ReadInt(source, "bankSeconds", 0)
            };
            if (source.TryGetValue("modifiers", out var value) && value != null)
            {
                if (!(value is List<object> list)) throw new FormatException("赛制 modifiers 必须是字符串数组或 null。");
                ruleset.Modifiers = new List<string>();
                foreach (var item in list)
                {
                    if (!(item is string id)) throw new FormatException("赛制 modifiers 必须只包含字符串。");
                    ruleset.Modifiers.Add(id);
                }
            }
            return ruleset;
        }

        internal Ruleset Copy()
        {
            return new Ruleset
            {
                Id = Id, Name = Name, Season = Season, BanOne = BanOne, LeadPick = LeadPick,
                BestOf = BestOf, TurnSeconds = TurnSeconds, BankSeconds = BankSeconds,
                Modifiers = Modifiers == null ? null : new List<string>(Modifiers)
            };
        }

        private static string ReadText(Dictionary<string, object> source, string key)
        {
            if (!source.TryGetValue(key, out var value) || value == null) return null;
            return value as string ?? throw new FormatException("赛制 " + key + " 必须是字符串或 null。");
        }

        private static bool ReadBool(Dictionary<string, object> source, string key)
        {
            if (!source.TryGetValue(key, out var value)) return false;
            if (!(value is bool flag)) throw new FormatException("赛制 " + key + " 必须是布尔值。");
            return flag;
        }

        private static int ReadInt(Dictionary<string, object> source, string key, int fallback)
        {
            if (!source.TryGetValue(key, out var value)) return fallback;
            if (!(value is double number) || double.IsNaN(number) || double.IsInfinity(number)
                || number < int.MinValue || number > int.MaxValue || number != Math.Truncate(number))
                throw new FormatException("赛制 " + key + " 必须是整数范围内的数值。");
            return (int)number;
        }

        private static void WriteText(StringBuilder text, string value)
        {
            if (value == null) { text.Append("null"); return; }
            text.Append('"');
            foreach (char c in value)
            {
                if (c == '"') text.Append("\\\"");
                else if (c == '\\') text.Append("\\\\");
                else if (char.IsControl(c) || char.IsSurrogate(c) || c == '\u2028' || c == '\u2029')
                    text.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                else text.Append(c);
            }
            text.Append('"');
        }
    }

    public static class Rulesets
    {
        private static readonly Ruleset[] definitions =
        {
            new Ruleset { Id = "casual", Name = "休闲", Modifiers = new List<string>() },
            new Ruleset
            {
                Id = "ranked", Name = "排位", BanOne = true, LeadPick = true, BestOf = 3,
                TurnSeconds = 10, BankSeconds = 30, Modifiers = new List<string>()
            },
            new Ruleset { Id = "chaos", Name = "乱斗", Modifiers = new List<string> { "half_pp", "hype_start" } }
        };

        public static IReadOnlyList<Ruleset> All
        {
            get { return Array.AsReadOnly(definitions.Select(ruleset => ruleset.Copy()).ToArray()); }
        }
    }

    public sealed class Match
    {
        private readonly ContentDb db;
        private readonly string contentHash;
        private readonly Ruleset ruleset;
        private readonly IList<string>[] teams;
        private readonly string[] names;
        private readonly uint seed;
        private readonly int[] wins = new int[2];
        private int games;
        private Battle current;

        public int[] Wins { get { return (int[])wins.Clone(); } }
        public int Draws { get; private set; }
        public bool Over
        {
            get { return wins[0] >= ruleset.BestOf / 2 + 1 || wins[1] >= ruleset.BestOf / 2 + 1 || games >= ruleset.BestOf; }
        }

        public Match(ContentDb db, Ruleset r, IList<string>[] teams, string[] names, uint seed)
        {
            if (db == null) throw new ArgumentNullException(nameof(db));
            if (r == null) throw new ArgumentNullException(nameof(r));
            ruleset = r.Copy();
            ruleset.Validate(db);
            if (teams == null || teams.Length != 2) throw new ArgumentException("比赛必须包含两支队伍。", nameof(teams));
            if (names == null || names.Length != 2 || names[0] == null || names[1] == null)
                throw new ArgumentException("比赛必须包含两个非空值的名称。", nameof(names));
            this.teams = new IList<string>[2];
            for (int side = 0; side < 2; side++)
            {
                var errors = ruleset.CheckTeam(db, teams[side]);
                if (errors.Count > 0) throw new KeyError("第 " + (side + 1) + " 方队伍不合法：" + string.Join("；", errors));
                this.teams[side] = new List<string>(teams[side]);
            }
            this.db = db;
            this.names = (string[])names.Clone();
            this.seed = seed;
            contentHash = db.ContentHash();
        }

        public Battle StartNextGame()
        {
            if (Over) throw new InvalidOperationException("比赛已经结束，不能再开局。");
            if (current != null) throw new InvalidOperationException("上一局尚未上报结果，不能开启下一局。");
            if (!string.Equals(contentHash, db.ContentHash(), StringComparison.Ordinal))
                throw new KeyError("比赛期间内容不一致，不能开启下一局。");

            // 只按已上报局号推导种子，失败调用不会消耗局号或引入额外随机源。
            var battle = new Battle(db, teams, names, new BattleOptions
            {
                Seed = unchecked(seed + (uint)games), BanOne = ruleset.BanOne, LeadPick = ruleset.LeadPick,
                Modifiers = ruleset.Modifiers == null ? null : new List<string>(ruleset.Modifiers)
            });
            current = battle;
            return battle;
        }

        public void ReportResult(Battle finished)
        {
            if (current == null || !ReferenceEquals(current, finished))
                throw new InvalidOperationException("只能上报本场比赛当前一局的结果，不能重复上报。");
            if (finished.Phase != Phase.Over || finished.Winner < -1 || finished.Winner > 1)
                throw new InvalidOperationException("这一局尚未结束或胜负结果无效。");
            if (finished.Winner == -1) Draws++;
            else wins[finished.Winner]++;
            games++;
            current = null;
        }
    }
}
