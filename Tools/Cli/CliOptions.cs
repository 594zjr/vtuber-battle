using System;
using System.Globalization;
using System.Linq;
using StreamDuel.Core;

namespace StreamDuel.Cli
{
    internal sealed class CliOptions
    {
        public string Pack = "original";
        public AiLevel Level = AiLevel.Normal;
        public uint Seed = 1;
        public string[] Team = null;
        public string SaveDir = "saves";
        public string ReplayFile;
        public bool Auto;
        public bool Ban;
        public bool Lead;
        public bool Hash;
        public bool Help;

        public static CliOptions Parse(string[] args)
        {
            if (args == null) throw new ArgumentException("参数列表不能为空。");

            var options = new CliOptions();
            for (int i = 0; i < args.Length; i++)
            {
                string option = args[i];
                if (option == "--auto")
                {
                    options.Auto = true;
                    continue;
                }
                if (option == "--ban")
                {
                    options.Ban = true;
                    continue;
                }
                if (option == "--lead")
                {
                    options.Lead = true;
                    continue;
                }
                if (option == "--hash")
                {
                    options.Hash = true;
                    continue;
                }
                if (option == "--help" || option == "-h")
                {
                    options.Help = true;
                    continue;
                }
                if (option != "--pack" && option != "--level" && option != "--seed" && option != "--team"
                    && option != "--save-dir" && option != "--replay")
                    throw new ArgumentException("未知参数：" + (option ?? "空参数") + "；使用 --help 查看用法。");

                if (++i >= args.Length || args[i] == null || args[i].StartsWith("--", StringComparison.Ordinal) || args[i] == "-h")
                    throw new ArgumentException(option + " 缺少参数值。");
                string value = args[i];
                switch (option)
                {
                    case "--replay":
                        if (string.IsNullOrWhiteSpace(value))
                            throw new ArgumentException("--replay 需要非空的回放文件路径。");
                        options.ReplayFile = value;
                        break;
                    case "--save-dir":
                        if (string.IsNullOrWhiteSpace(value))
                            throw new ArgumentException("--save-dir 需要非空的存档目录。");
                        options.SaveDir = value;
                        break;
                    case "--pack":
                        if (!IsPackId(value))
                            throw new ArgumentException("--pack 应为内容包文件名（不含 .json），只允许英文字母、数字、下划线和连字符。");
                        options.Pack = value;
                        break;
                    case "--level":
                        if (value == "easy") options.Level = AiLevel.Easy;
                        else if (value == "normal") options.Level = AiLevel.Normal;
                        else throw new ArgumentException("--level 只能是 easy（简单）或 normal（普通）。");
                        break;
                    case "--seed":
                        if (value.Length == 0 || !value.All(c => c >= '0' && c <= '9') ||
                            !uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out options.Seed))
                            throw new ArgumentException("--seed 必须是 0..4294967295 的十进制整数，只允许数字。");
                        break;
                    case "--team":
                        var team = value.Split(',');
                        for (int j = 0; j < team.Length; j++)
                        {
                            team[j] = team[j].Trim();
                            if (team[j].Length == 0)
                                throw new ArgumentException("--team 的第 " + (j + 1) + " 个主播编号为空，请用逗号分隔非空编号。");
                        }
                        options.Team = team;
                        break;
                }
            }
            if (options.ReplayFile != null && options.Hash && !options.Help)
                throw new ArgumentException("--replay 不支持 --hash；回看按保存的日志播放，不提供历史逐回合状态哈希。");
            return options;
        }

        internal static bool IsPackId(string value)
        {
            return !string.IsNullOrEmpty(value) && value.All(c =>
                (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
                (c >= '0' && c <= '9') || c == '_' || c == '-');
        }
    }
}
