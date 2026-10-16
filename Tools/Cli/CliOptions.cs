using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using StreamDuel.Core;

namespace StreamDuel.Cli
{
    internal sealed partial class CliOptions
    {
        public string Pack = "original";
        public bool PackSpecified;
        public string PackDirectory;
        public AiLevel Level = AiLevel.Normal;
        public uint Seed = 1;
        public string[] Team = null;
        public string[] TeamB;
        public string NameA = "玩家 A";
        public string NameB = "玩家 B";
        public bool Hotseat;
        public string TeamCode;
        public bool ExportTeam;
        public string SaveDir = "saves";
        public string ReplayFile;
        public string ReplayCode;
        public string BranchReplay;
        public int? BranchTurn;
        public int? BranchSide;
        public AiLevel BranchAi = AiLevel.Normal;
        public bool ReplayBestEffort;
        public string ReportFile;
        public string TournamentFile;
        public uint? TournamentSeed;
        public bool NoAutoTiebreak;
        public string TournamentFormat;
        public string TournamentReportFile;
        public bool Json;
        public int? HostPort;
        public string JoinHost;
        public int JoinPort;
        public string LanHostName;
        public bool LanList;
        public double? TurnSeconds;
        public double? BankSeconds;
        public bool Auto;
        public bool Ban;
        public bool Lead;
        public bool Advice;
        public bool Hash;
        public bool Help;
        public bool ListPacks;
        public bool ListReplays;
        public bool Share;
        public bool Glossary;
        public string GlossaryKeyword;
        public string CalcAttacker;
        public string CalcDefender;
        public int CalcSkillIndex;
        public string[] MatchupTeamA;
        public string[] MatchupTeamB;
        public bool Network { get { return HostPort.HasValue || JoinHost != null; } }
        public bool ReplayMode { get { return ReplayFile != null || ReplayCode != null; } }
        public bool AnalysisMode { get { return CalcAttacker != null || MatchupTeamA != null; } }

        public static CliOptions Parse(string[] args)
        {
            if (args == null) throw new ArgumentException("参数列表不能为空。");

            var options = new CliOptions();
            var provided = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                string option = args[i];
                provided.Add(option);
                if (options.ReadTournamentSeedOption(args, ref i)) continue;
                if (option == "--no-auto-tiebreak")
                {
                    if (options.NoAutoTiebreak) throw new ArgumentException("--no-auto-tiebreak 不能重复指定。");
                    options.NoAutoTiebreak = true;
                    continue;
                }
                if (options.ReadRatingOption(option)) continue;
                if (options.ReadCardOption(args, ref i)) continue;
                if (option == "--json")
                {
                    if (options.Json) throw new ArgumentException("--json 不能重复指定。");
                    options.Json = true;
                    continue;
                }
                if (option == "--hotseat")
                {
                    if (options.Hotseat) throw new ArgumentException("--hotseat 不能重复指定。");
                    options.Hotseat = true;
                    continue;
                }
                if (option == "--glossary")
                {
                    if (options.Glossary) throw new ArgumentException(Loc.Current.T("glossary.cli.duplicate"));
                    options.Glossary = true;
                    if (i + 1 < args.Length && args[i + 1] != null
                        && !args[i + 1].StartsWith("--", StringComparison.Ordinal) && args[i + 1] != "-h")
                        options.GlossaryKeyword = args[++i];
                    continue;
                }
                if (option == "--calc")
                {
                    if (options.CalcAttacker != null) throw new ArgumentException("--calc 不能重复指定。");
                    options.CalcAttacker = ReadAnalysisValue(args, ref i, "--calc", "攻击方 id");
                    string skill = ReadAnalysisValue(args, ref i, "--calc", "技能序号 1～5");
                    int number;
                    if (!int.TryParse(skill, NumberStyles.None, CultureInfo.InvariantCulture, out number)
                        || number < 1 || number > 5)
                        throw new ArgumentException("--calc 技能序号必须是 1～5 的整数，5 为第五技能。");
                    options.CalcSkillIndex = number - 1;
                    options.CalcDefender = ReadAnalysisValue(args, ref i, "--calc", "防守方 id");
                    continue;
                }
                if (option == "--matchup")
                {
                    if (options.MatchupTeamA != null) throw new ArgumentException("--matchup 不能重复指定。");
                    options.MatchupTeamA = ReadMatchupTeam(args, ref i, "A");
                    if (ReadAnalysisValue(args, ref i, "--matchup", "分隔符 vs") != "vs")
                        throw new ArgumentException("--matchup 两队之间必须用小写 vs 分隔。");
                    options.MatchupTeamB = ReadMatchupTeam(args, ref i, "B");
                    continue;
                }
                if (option == "--lan-list")
                {
                    if (options.LanList) throw new ArgumentException("--lan-list 不能重复指定。");
                    options.LanList = true;
                    continue;
                }
                if (option == "--random-teams")
                {
                    if (options.RandomTeams) throw new ArgumentException("--random-teams 不能重复指定。");
                    options.RandomTeams = true;
                    continue;
                }
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
                if (option == "--replays")
                {
                    options.ListReplays = true;
                    continue;
                }
                if (option == "--replay-best-effort")
                {
                    if (options.ReplayBestEffort) throw new ArgumentException("--replay-best-effort 不能重复指定。");
                    options.ReplayBestEffort = true;
                    continue;
                }
                if (option == "--ban")
                {
                    options.Ban = true;
                    continue;
                }
                if (option == "--advice")
                {
                    if (options.Advice) throw new ArgumentException(Loc.Current.T("advice.cli.duplicate"));
                    options.Advice = true;
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
                if (option == "--share")
                {
                    options.Share = true;
                    continue;
                }
                if (option == "--help" || option == "-h")
                {
                    options.Help = true;
                    continue;
                }
                if (option != "--packs" && option != "--pack" && option != "--level" && option != "--seed" && option != "--team" && option != "--team-code"
                    && option != "--ruleset"
                    && option != "--team-b" && option != "--name-a" && option != "--name-b"
                    && option != "--report" && option != "--tournament" && option != "--tournament-report" && option != "--format"
                    && option != "--save-dir" && option != "--replay" && option != "--replay-code" && option != "--pack-dir"
                    && option != "--host" && option != "--join" && option != "--timer" && option != "--lan-host"
                    && option != "--branch" && option != "--turn" && option != "--side" && option != "--ai"
                    && option != "--spectate-port" && option != "--watch" && option != "--spectator-chat" && option != "--watch-name"
                    && option != "--tournament-seed")
                    throw new ArgumentException("未知参数：" + (option ?? "空参数") + "；使用 --help 查看用法。");

                if (++i >= args.Length || args[i] == null || args[i].StartsWith("--", StringComparison.Ordinal) || args[i] == "-h")
                    throw new ArgumentException(option + " 缺少参数值。");
                string value = args[i];
                switch (option)
                {
                    case "--ruleset":
                        if (options.RulesetId != null) throw new ArgumentException("--ruleset 不能重复指定。");
                        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("--ruleset 需要非空赛制编号。");
                        options.RulesetId = value;
                        break;
                    case "--branch":
                    case "--turn":
                    case "--side":
                    case "--ai":
                        if (provided.Count(item => item == option) > 1)
                            throw new ArgumentException(option + " 不能重复指定。");
                        if (option == "--branch")
                        {
                            if (string.IsNullOrWhiteSpace(value))
                                throw new ArgumentException("--branch 需要非空的录像文件路径或录像库编号。");
                            options.BranchReplay = value;
                        }
                        else if (option == "--turn")
                        {
                            int turn;
                            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out turn) || turn < 1)
                                throw new ArgumentException("--turn 必须是从 1 开始的十进制整数回合号。");
                            options.BranchTurn = turn;
                        }
                        else if (option == "--side")
                        {
                            if (value != "0" && value != "1")
                                throw new ArgumentException("--side 只能是 0（录像第一方）或 1（录像第二方）。");
                            options.BranchSide = value == "0" ? 0 : 1;
                        }
                        else
                        {
                            if (value == "normal") options.BranchAi = AiLevel.Normal;
                            else if (value == "hard") options.BranchAi = AiLevel.Hard;
                            else if (value == "expert") options.BranchAi = AiLevel.Expert;
                            else throw new ArgumentException("--ai 只能是 normal（普通）、hard（困难）或 expert（专家）。");
                        }
                        break;
                    case "--tournament-seed":
                        if (options.TournamentSeed.HasValue) throw new ArgumentException("--tournament-seed 不能重复指定。");
                        uint tournamentSeed;
                        if (value.Length == 0 || !value.All(c => c >= '0' && c <= '9')
                            || !uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out tournamentSeed))
                            throw new ArgumentException("--tournament-seed 必须是 0..4294967295 的十进制整数，只允许数字。");
                        options.TournamentSeed = tournamentSeed;
                        break;
                    case "--format":
                        if (options.TournamentFormat != null) throw new ArgumentException("--format 不能重复指定。");
                        if (value != "single" && value != "double")
                            throw new ArgumentException("--format 只接受 single（单败淘汰）或 double（双败淘汰）。");
                        options.TournamentFormat = value;
                        break;
                    case "--spectate-port":
                        if (options.SpectatePort.HasValue) throw new ArgumentException("--spectate-port 不能重复指定。");
                        options.SpectatePort = ParsePort(value, 0, "--spectate-port");
                        break;
                    case "--watch":
                        if (options.WatchHost != null) throw new ArgumentException("--watch 不能重复指定。");
                        ParseEndpoint(value, "--watch", out options.WatchHost, out options.WatchPort);
                        break;
                    case "--watch-name":
                        if (provided.Count(item => item == option) > 1) throw new ArgumentException("--watch-name 不能重复指定。");
                        if (value.Length > 4096) throw new ArgumentException("--watch-name 输入过长，请缩短后重试。");
                        options.WatchName = value;
                        break;
                    case "--spectator-chat":
                        if (provided.Count(item => item == option) > 1) throw new ArgumentException("--spectator-chat 不能重复指定。");
                        if (value != "on" && value != "off") throw new ArgumentException("--spectator-chat 只能是 on 或 off。");
                        options.SpectatorChat = value == "on";
                        break;
                    case "--tournament-report":
                        if (options.TournamentReportFile != null) throw new ArgumentException("--tournament-report 不能重复指定。");
                        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("--tournament-report 需要非空的赛事 JSON 文件路径。");
                        options.TournamentReportFile = value;
                        break;
                    case "--tournament":
                        if (options.TournamentFile != null) throw new ArgumentException("--tournament 不能重复指定。");
                        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("--tournament 需要非空的赛事 JSON 文件路径。");
                        options.TournamentFile = value;
                        break;
                    case "--team-b":
                        if (options.TeamB != null) throw new ArgumentException("--team-b 不能重复指定。");
                        options.TeamB = value.Split(',').Select(id => id.Trim()).ToArray();
                        if (options.TeamB.Any(id => id.Length == 0))
                            throw new ArgumentException("--team-b 需要用逗号分隔的非空主播编号。");
                        break;
                    case "--name-a":
                    case "--name-b":
                        if (provided.Count(item => item == option) > 1) throw new ArgumentException(option + " 不能重复指定。");
                        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl))
                            throw new ArgumentException(option + " 需要非空玩家名称，不能含换行或控制字符。");
                        if (option == "--name-a") options.NameA = value.Trim();
                        else options.NameB = value.Trim();
                        break;
                    case "--lan-host":
                        if (options.LanHostName != null) throw new ArgumentException("--lan-host 不能重复指定。");
                        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl))
                            throw new ArgumentException("--lan-host 需要非空房名，不能含换行或控制字符。");
                        options.LanHostName = value;
                        break;
                    case "--timer":
                        if (options.TurnSeconds.HasValue) throw new ArgumentException("--timer 不能重复指定。");
                        var timer = value.Split(',');
                        double turnSeconds, bankSeconds;
                        if (timer.Length != 2 || value.Any(char.IsWhiteSpace)
                            || !double.TryParse(timer[0], NumberStyles.Float, CultureInfo.InvariantCulture, out turnSeconds)
                            || !double.TryParse(timer[1], NumberStyles.Float, CultureInfo.InvariantCulture, out bankSeconds)
                            || double.IsNaN(turnSeconds) || double.IsInfinity(turnSeconds) || turnSeconds < 0
                            || double.IsNaN(bankSeconds) || double.IsInfinity(bankSeconds) || bankSeconds < 0)
                            throw new ArgumentException("--timer 必须是逗号分隔的两个有限非负秒数，例如 10,30；不允许空项或空格。");
                        options.TurnSeconds = turnSeconds;
                        options.BankSeconds = bankSeconds;
                        break;
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
                        ParseEndpoint(value, "--join", out options.JoinHost, out options.JoinPort);
                        break;
                    case "--replay":
                        if (string.IsNullOrWhiteSpace(value))
                            throw new ArgumentException("--replay 需要非空的录像库编号或回放文件路径。");
                        options.ReplayFile = value;
                        break;
                    case "--replay-code":
                        if (string.IsNullOrWhiteSpace(value))
                            throw new ArgumentException("--replay-code 需要非空的回放分享码。");
                        options.ReplayCode = value;
                        break;
                    case "--save-dir":
                        if (string.IsNullOrWhiteSpace(value))
                            throw new ArgumentException("--save-dir 需要非空的存档目录。");
                        options.SaveDir = value;
                        break;
                    case "--packs":
                        if (options.Packs != null) throw new ArgumentException("--packs 不能重复指定。");
                        options.Packs = ParseMixedPackIds(value);
                        options.PackSpecified = true;
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
                        options.SeedSpecified = true;
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
            ValidateTournamentSeedOptions(options);
            ValidateRatingOptions(options, provided);
            ValidateSpectatorOptions(options, provided);
            ValidateCardOptions(options, provided);
            ValidateRulesetOptions(options, provided);
            if (!options.Help)
            {
                if (options.BranchReplay == null && (options.BranchTurn.HasValue || options.BranchSide.HasValue || provided.Contains("--ai")))
                    throw new ArgumentException("--turn、--side 和 --ai 只能与 --branch 同用。");
                if (options.BranchReplay != null)
                {
                    if (!options.BranchTurn.HasValue || !options.BranchSide.HasValue)
                        throw new ArgumentException("--branch 必须同时指定 --turn 回合 和 --side 0或1。");
                    var allowed = new HashSet<string>(StringComparer.Ordinal)
                    { "--branch", "--turn", "--side", "--ai", "--save-dir", "--pack-dir" };
                    var unsupported = provided.Where(option => !allowed.Contains(option)).Distinct().ToArray();
                    if (unsupported.Length > 0)
                        throw new ArgumentException("--branch 是独立的手动接手模式，只可搭配 --turn、--side、--ai、--save-dir、--pack-dir；不能与 "
                            + string.Join("、", unsupported) + " 同用。");
                    string duplicate = provided.GroupBy(option => option).FirstOrDefault(group => group.Count() > 1)?.Key;
                    if (duplicate != null) throw new ArgumentException(duplicate + " 不能重复指定。");
                }
                if (options.TournamentFormat != null && options.TournamentFile == null)
                    throw new ArgumentException("--format 只能与 --tournament 同用。");
                if ((options.TournamentSeed.HasValue || options.NoAutoTiebreak) && options.TournamentFile == null)
                    throw new ArgumentException("--tournament-seed 与 --no-auto-tiebreak 只能与 --tournament 的淘汰赛同用。");
                if (options.Json && options.TournamentReportFile == null)
                    throw new ArgumentException("--json 只能与 --tournament-report 同用。");
                if (options.TournamentReportFile != null)
                {
                    var unsupported = provided.Where(option => option != "--tournament-report" && option != "--json"
                        && option != "--pack" && option != "--pack-dir").Distinct().ToArray();
                    if (unsupported.Length > 0)
                        throw new ArgumentException("--tournament-report 是独立只读查询，只可搭配 --json、--pack、--pack-dir；不能与 "
                            + string.Join("、", unsupported) + " 同用。");
                }
            }
            if (options.Advice && !options.Help)
            {
                var compatible = new HashSet<string>(StringComparer.Ordinal)
                {
                    "--advice", "--ban", "--lead", "--ruleset", "--packs", "--random-teams", "--team-b", "--pack", "--pack-dir", "--level", "--seed", "--team",
                    "--team-code", "--export-team", "--save-dir", "--auto", "--timer", "--hash", "--share", "--report"
                };
                var unsupported = provided.Where(option => !compatible.Contains(option)).Distinct().ToArray();
                if (unsupported.Length > 0)
                    throw new ArgumentException(Loc.Current.T("advice.cli.conflict", string.Join("、", unsupported)));
                if (!options.Ban && !options.Lead && options.RulesetId == null)
                    throw new ArgumentException(Loc.Current.T("advice.cli.phase.required"));
            }
            if (options.TournamentFile != null && !options.Help)
            {
                var unsupported = provided.Where(option => option != "--tournament" && option != "--format" && option != "--seed-by" && option != "--pack"
                    && option != "--pack-dir" && option != "--auto" && option != "--help" && option != "-h"
                    && option != "--tournament-seed" && option != "--no-auto-tiebreak")
                    .Distinct().ToArray();
                if (unsupported.Length > 0)
                    throw new ArgumentException("赛事模式仅支持搭配 --format、--seed-by、--pack、--pack-dir、--auto、--tournament-seed、--no-auto-tiebreak；不能与 "
                        + string.Join("、", unsupported) + " 同用。自动代打固定使用普通 AI；--seed 不用于赛事。");
            }
            if (!options.Help)
            {
                if (!options.Hotseat && provided.Any(option => option == "--name-a" || option == "--name-b"
                    || (option == "--team-b" && !options.MixedPack && options.TeamCode == null)))
                    throw new ArgumentException("--team-b、--name-a 和 --name-b 仅用于 --hotseat 同屏双人对战。");
                if (options.Hotseat)
                {
                    var allowed = new HashSet<string>(new[] { "--hotseat", "--packs", "--random-teams", "--pack", "--pack-dir", "--seed", "--team",
                        "--team-code", "--team-b", "--name-a", "--name-b", "--ban", "--lead", "--save-dir",
                        "--report", "--share", "--export-team", "--help", "-h" }, StringComparer.Ordinal);
                    var unsupported = provided.Where(option => !allowed.Contains(option)).Distinct().ToArray();
                    if (unsupported.Length > 0)
                        throw new ArgumentException("--hotseat 不支持 " + string.Join("、", unsupported)
                            + "；双方请轮流手动操作，并在交接后按回车确认。");
                }
            }
            if (options.CalcAttacker != null && options.MatchupTeamA != null)
                throw new ArgumentException("--calc 与 --matchup 不能同时使用，请选择单次伤害或阵容对位分析。");
            if (options.Glossary && !options.Help)
            {
                var incompatible = provided.Where(option => option != "--glossary"
                    && option != "--pack" && option != "--packs" && option != "--pack-dir").ToArray();
                if (incompatible.Length > 0)
                    throw new ArgumentException(Loc.Current.T("glossary.cli.conflict",
                        string.Join(Loc.Current.T("describe.list.separator"), incompatible)));
            }
            if (options.AnalysisMode && !options.Help)
            {
                var unsupported = provided.Where(option => option != "--calc" && option != "--matchup"
                    && option != "--pack" && option != "--packs" && option != "--pack-dir" && option != "--help" && option != "-h")
                    .Distinct().ToArray();
                if (unsupported.Length > 0)
                    throw new ArgumentException("伤害分析仅支持搭配 --pack、--pack-dir；不能与 "
                        + string.Join("、", unsupported) + " 同用。");
            }
            if (options.LanList && !options.Help && args.Any(value => value != "--lan-list"))
                throw new ArgumentException("--lan-list 是独立的 5 秒搜索操作，不能与其他对战或内容包参数同时使用。");
            if (options.LanHostName != null && options.JoinHost != null)
                throw new ArgumentException("--lan-host 与 --join 不能同时使用，请选择开房或加入房间。");
            if (options.LanHostName != null && !options.HostPort.HasValue) options.HostPort = 0;
            if (options.HostPort.HasValue && options.JoinHost != null)
                throw new ArgumentException("--host 与 --join 不能同时使用，请选择创建或加入一场对战。");
            if (!options.Help)
            {
                if (options.TeamCode != null && options.Team != null)
                    throw new ArgumentException("--team-code 与 --team 不能同时使用，请只指定一种本方队伍来源。");
                if (options.TeamCode != null || options.ExportTeam)
                {
                    if (options.ReplayMode)
                        throw new ArgumentException("--replay 或 --replay-code 不能与 --team-code 或 --export-team 同用；队伍导入导出仅用于新对局。");
                    if (options.ListPacks)
                        throw new ArgumentException("--list-packs 不能与 --team-code 或 --export-team 同用；列出内容包时不选择队伍。");
                }
            }
            if (options.ReplayFile != null && options.ReplayCode != null)
                throw new ArgumentException("--replay 与 --replay-code 不能同时使用，请选择回放文件或分享码。");
            if (options.ReplayBestEffort && !options.Help && (!options.ReplayMode || options.ListPacks))
                throw new ArgumentException("--replay-best-effort 只能与 --replay 或 --replay-code 同用，不能用于新对局或内容包列表。");
            if (options.ListReplays && !options.Help && (options.ReplayMode || options.ListPacks
                || options.Network || options.ReportFile != null || options.Share
                || options.TeamCode != null || options.ExportTeam || options.TurnSeconds.HasValue))
                throw new ArgumentException("--replays 只列出录像库，不能与 --replay、--replay-code、--list-packs、--host、--join、--lan-host、--report、--share、--team-code、--export-team 或 --timer 同用。");
            if (options.Network && !options.Help)
            {
                var unsupported = new[]
                {
                    options.ReplayFile != null ? "--replay" : null,
                    options.ReplayCode != null ? "--replay-code" : null,
                    options.Ban ? "--ban" : null,
                    options.Lead ? "--lead" : null,
                    options.ListPacks ? "--list-packs" : null
                }.Where(option => option != null).ToArray();
                if (unsupported.Length > 0)
                    throw new ArgumentException("联网会话目前仅支持基础对战，不支持 " + string.Join("、", unsupported) + "。");
            }
            if (options.ReplayMode && options.Hash && !options.Help)
                throw new ArgumentException((options.ReplayCode != null ? "--replay-code" : "--replay")
                    + " 不支持 --hash；回看按保存的日志播放，不提供历史逐回合状态哈希。");
            if (options.ReplayMode && options.TurnSeconds.HasValue && !options.Help)
                throw new ArgumentException((options.ReplayCode != null ? "--replay-code" : "--replay")
                    + " 不支持 --timer；回放使用已保存的选择，不重新计时。");
            ValidateMixedOptions(options, provided);
            return options;
        }

        private static string ReadAnalysisValue(string[] args, ref int index, string option, string label)
        {
            if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index])
                || args[index].StartsWith("--", StringComparison.Ordinal) || args[index] == "-h")
                throw new ArgumentException(option + " 缺少" + label + "。");
            return args[index].Trim();
        }

        private static string[] ReadMatchupTeam(string[] args, ref int index, string side)
        {
            var team = new List<string>();
            while (team.Count < 6)
            {
                string value = ReadAnalysisValue(args, ref index, "--matchup", side + " 队的 6 个主播 id");
                foreach (string part in value.Split(','))
                {
                    string id = part.Trim();
                    if (id.Length == 0 || id == "vs" || team.Count >= 6)
                        throw new ArgumentException("--matchup 的 " + side
                            + " 队必须恰有 6 个非空主播 id，可用空格或逗号分隔；两队之间用 vs 分隔。");
                    team.Add(id);
                }
            }
            return team.ToArray();
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

        private static void ParseEndpoint(string value, string option, out string host, out int port)
        {
            string formatError = option + " 必须是地址:端口，例如 127.0.0.1:7777、localhost:7777 或 [::1]:7777。";
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
                    throw new ArgumentException(option + " 的方括号内必须是有效的 IPv6 地址。");
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
                    throw new ArgumentException(option + " 需要有效的 IPv4 地址或主机名；IPv6 地址请使用 [地址]:端口。");
                portText = value.Substring(separator + 1);
            }
            port = ParsePort(portText, 1, option);
        }
    }
}
