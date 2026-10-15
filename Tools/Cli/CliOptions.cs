using System;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using StreamDuel.Core;

namespace StreamDuel.Cli
{
    internal sealed class CliOptions
    {
        public string Pack = "original";
        public bool PackSpecified;
        public string PackDirectory;
        public AiLevel Level = AiLevel.Normal;
        public uint Seed = 1;
        public string[] Team = null;
        public string TeamCode;
        public bool ExportTeam;
        public string SaveDir = "saves";
        public string ReplayFile;
        public string ReportFile;
        public int? HostPort;
        public string JoinHost;
        public int JoinPort;
        public bool Auto;
        public bool Ban;
        public bool Lead;
        public bool Hash;
        public bool Help;
        public bool ListPacks;
        public bool Network { get { return HostPort.HasValue || JoinHost != null; } }

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
                if (option == "--export-team")
                {
                    options.ExportTeam = true;
                    continue;
                }
                if (option == "--list-packs")
                {
                    options.ListPacks = true;
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
                if (option != "--pack" && option != "--level" && option != "--seed" && option != "--team" && option != "--team-code"
                    && option != "--report"
                    && option != "--save-dir" && option != "--replay" && option != "--pack-dir"
                    && option != "--host" && option != "--join")
                    throw new ArgumentException("未知参数：" + (option ?? "空参数") + "；使用 --help 查看用法。");

                if (++i >= args.Length || args[i] == null || args[i].StartsWith("--", StringComparison.Ordinal) || args[i] == "-h")
                    throw new ArgumentException(option + " 缺少参数值。");
                string value = args[i];
                switch (option)
                {
                    case "--report":
                        if (string.IsNullOrWhiteSpace(value))
                            throw new ArgumentException("--report 需要非空的战报文件路径。");
                        options.ReportFile = value;
                        break;
                    case "--host":
                        if (options.HostPort.HasValue) throw new ArgumentException("--host 不能重复指定。");
                        options.HostPort = ParsePort(value, 0, "--host");
                        break;
                    case "--join":
                        if (options.JoinHost != null) throw new ArgumentException("--join 不能重复指定。");
                        ParseJoin(value, out options.JoinHost, out options.JoinPort);
                        break;
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
                        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("--pack 需要非空内容包 id。");
                        options.Pack = value;
                        options.PackSpecified = true;
                        break;
                    case "--pack-dir":
                        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("--pack-dir 需要非空文件夹路径。");
                        options.PackDirectory = value;
                        break;
                    case "--level":
                        if (value == "easy") options.Level = AiLevel.Easy;
                        else if (value == "normal") options.Level = AiLevel.Normal;
                        else if (value == "hard") options.Level = AiLevel.Hard;
                        else if (value == "expert") options.Level = AiLevel.Expert;
                        else throw new ArgumentException("--level 只能是 easy（简单）、normal（普通）、hard（困难）或 expert（专家）。");
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
                    case "--team-code":
                        if (options.TeamCode != null) throw new ArgumentException("--team-code 不能重复指定。");
                        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("--team-code 需要非空队伍码。");
                        options.TeamCode = value;
                        break;
                }
            }
            if (options.HostPort.HasValue && options.JoinHost != null)
                throw new ArgumentException("--host 与 --join 不能同时使用，请选择创建或加入一场对战。");
            if (!options.Help)
            {
                if (options.TeamCode != null && options.Team != null)
                    throw new ArgumentException("--team-code 与 --team 不能同时使用，请只指定一种本方队伍来源。");
                if (options.TeamCode != null || options.ExportTeam)
                {
                    if (options.ReplayFile != null)
                        throw new ArgumentException("--replay 不能与 --team-code 或 --export-team 同用；队伍导入导出仅用于新对局。");
                    if (options.ListPacks)
                        throw new ArgumentException("--list-packs 不能与 --team-code 或 --export-team 同用；列出内容包时不选择队伍。");
                }
            }
            if (options.Network && !options.Help)
            {
                var unsupported = new[]
                {
                    options.ReplayFile != null ? "--replay" : null,
                    options.Ban ? "--ban" : null,
                    options.Lead ? "--lead" : null,
                    options.ListPacks ? "--list-packs" : null
                }.Where(option => option != null).ToArray();
                if (unsupported.Length > 0)
                    throw new ArgumentException("联网会话目前仅支持基础对战，不支持 " + string.Join("、", unsupported) + "。");
            }
            if (options.ReplayFile != null && options.Hash && !options.Help)
                throw new ArgumentException("--replay 不支持 --hash；回看按保存的日志播放，不提供历史逐回合状态哈希。");
            return options;
        }

        private static int ParsePort(string value, int minimum, string option)
        {
            int port;
            if (value.Length == 0 || !value.All(c => c >= '0' && c <= '9')
                || !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out port)
                || port < minimum || port > 65535)
                throw new ArgumentException(option + " 的端口必须是 " + minimum + "..65535 的十进制整数，只允许数字。");
            return port;
        }

        private static void ParseJoin(string value, out string host, out int port)
        {
            const string formatError = "--join 必须是地址:端口，例如 127.0.0.1:7777、localhost:7777 或 [::1]:7777。";
            if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsWhiteSpace))
                throw new ArgumentException(formatError);

            string portText;
            if (value.StartsWith("[", StringComparison.Ordinal))
            {
                int closing = value.IndexOf(']');
                if (closing <= 1 || closing + 1 >= value.Length || value[closing + 1] != ':')
                    throw new ArgumentException(formatError);
                host = value.Substring(1, closing - 1);
                IPAddress address;
                if (!IPAddress.TryParse(host, out address) || address.AddressFamily != AddressFamily.InterNetworkV6)
                    throw new ArgumentException("--join 的方括号内必须是有效的 IPv6 地址。");
                portText = value.Substring(closing + 2);
            }
            else
            {
                int separator = value.IndexOf(':');
                if (separator <= 0 || separator != value.LastIndexOf(':'))
                    throw new ArgumentException(formatError + " IPv6 地址必须用方括号包围。");
                host = value.Substring(0, separator);
                var hostType = Uri.CheckHostName(host);
                if (hostType != UriHostNameType.Dns && hostType != UriHostNameType.IPv4)
                    throw new ArgumentException("--join 需要有效的 IPv4 地址或主机名；IPv6 地址请使用 [地址]:端口。");
                portText = value.Substring(separator + 1);
            }
            port = ParsePort(portText, 1, "--join");
        }
    }
}
