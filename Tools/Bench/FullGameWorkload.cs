using System;
using System.Collections.Generic;
using System.Linq;
using StreamDuel.Core;

namespace StreamDuel.Bench
{
    internal static class FullGameWorkload
    {
        public const int GamesPerGroup = 200;
        public const uint FirstSeed = 1170001;
        public static readonly string[] PackIds = { "fan_asoul_vr", "fan_holo_niji", "original", "original_wave2" };
        public static readonly AiLevel[] Levels = { AiLevel.Normal, AiLevel.Hard, AiLevel.Expert };
        public static readonly string[] Names = { "基准甲", "基准乙" };

        public static FullGameCase Create(ContentDb db, int packIndex, int levelIndex, int gameIndex)
        {
            int id = packIndex * Levels.Length * GamesPerGroup + levelIndex * GamesPerGroup + gameIndex;
            uint seed = FirstSeed + (uint)id;
            var candidates = db.Streamers.Select(streamer => streamer.Id).OrderBy(value => value, StringComparer.Ordinal).ToArray();
            if (candidates.Length < 6) throw new InvalidOperationException("整局基准内容包不足六位主播：" + db.PackId);
            var rng = new Rng(seed);
            var teams = new string[2][];
            for (int side = 0; side < 2; side++)
            {
                var shuffled = (string[])candidates.Clone();
                for (int index = shuffled.Length - 1; index > 0; index--)
                {
                    int other = (int)(rng.Next() * (index + 1));
                    string value = shuffled[index]; shuffled[index] = shuffled[other]; shuffled[other] = value;
                }
                teams[side] = shuffled.Take(6).ToArray();
                var errors = TeamCheck.Validate(db, null, teams[side]);
                if (errors.Count > 0) throw new InvalidOperationException("整局基准阵容无效：" + string.Join("；", errors));
            }
            return new FullGameCase { Id = id, PackId = db.PackId, Level = Levels[levelIndex].ToString(),
                GameIndex = gameIndex, Seed = seed, Teams = teams };
        }
    }

    internal sealed class FullGameCase
    {
        public int Id;
        public string PackId, Level;
        public int GameIndex;
        public uint Seed;
        public string[][] Teams;
    }
}
