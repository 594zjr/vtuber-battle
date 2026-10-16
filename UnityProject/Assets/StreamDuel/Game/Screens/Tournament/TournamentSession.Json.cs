using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using StreamDuel.Core;

namespace StreamDuel.Game
{
    public sealed partial class TournamentSession
    {
        static string SerializeSave(Tournament tournament, AiLevel?[] controls)
        {
            var text = new StringBuilder("{\"version\":1,\"tournament\":");
            WriteString(text, tournament.ToJson());
            text.Append(",\"controllers\":[");
            for (int i = 0; i < controls.Length; i++)
            {
                if (i > 0) text.Append(',');
                if (!controls[i].HasValue) text.Append("null");
                else WriteString(text, controls[i] == AiLevel.Normal ? "normal" : controls[i] == AiLevel.Hard ? "hard" : "expert");
            }
            return text.Append("]}").ToString();
        }

        static void ParseSave(ContentDb db, string text, out Tournament tournament, out AiLevel?[] controls)
        {
            var root = MiniJson.Parse(text) as Dictionary<string, object>;
            if (root == null || root.Count != 3 || !root.TryGetValue("version", out var version)
                || !(version is double number) || number != 1
                || !root.TryGetValue("tournament", out var value) || !(value is string coreJson)
                || !root.TryGetValue("controllers", out var entries) || !(entries is List<object> list))
                throw new FormatException("本地赛事存档格式或版本无效，原文件已保留。");
            RejectDuplicateKeys(text);
            tournament = StreamDuel.Core.Tournament.FromJson(db, coreJson);
            if (tournament.Players.Count < 2 || tournament.Players.Count > 16 || list.Count != tournament.Players.Count)
                throw new FormatException("本地赛事选手必须为 2～16 人，并逐位保存操作者类型。");
            controls = new AiLevel?[list.Count];
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] == null) controls[i] = null;
                else if (list[i] is string level && level == "normal") controls[i] = AiLevel.Normal;
                else if (list[i] is string hard && hard == "hard") controls[i] = AiLevel.Hard;
                else if (list[i] is string expert && expert == "expert") controls[i] = AiLevel.Expert;
                else throw new FormatException("赛事电脑难度必须为 normal、hard、expert，真人须为 null。");
            }
            if (tournament.SeedMode == TournamentSeedMode.ByRating)
                for (int i = 0; i < controls.Length; i++)
                    if (tournament.RatingSeeds[i].ComputerLevel != controls[i])
                        throw new FormatException("赛事操作者类型与已冻结的排位种子来源不一致。");
        }

        static void RejectDuplicateKeys(string text)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] != '"') continue;
                int start = i++;
                while (text[i] != '"') { if (text[i] == '\\') i++; i++; }
                int next = i + 1;
                while (next < text.Length && char.IsWhiteSpace(text[next])) next++;
                if (next < text.Length && text[next] == ':'
                    && !keys.Add((string)MiniJson.Parse(text.Substring(start, i - start + 1))))
                    throw new FormatException("本地赛事存档含重复字段，原文件已保留。");
            }
        }

        static void WriteString(StringBuilder text, string value)
        {
            Utf8.GetByteCount(value);
            text.Append('"');
            foreach (char character in value)
            {
                if (character == '"' || character == '\\') text.Append('\\').Append(character);
                else if (character < 32) text.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                else text.Append(character);
            }
            text.Append('"');
        }
    }
}
