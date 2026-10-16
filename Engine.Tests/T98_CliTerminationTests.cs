using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using StreamDuel.Core;
using Xunit;

public sealed class T98_CliTerminationTests
{
    static readonly string TeamA = "enka,shiori,lumi,bell,hane,momo";
    static readonly string TeamB = "kei,nyako,buck,kasumi,yuan,bee";
    static readonly Lazy<string> CliDll = new Lazy<string>(BuildCli);

    [Theory]
    [InlineData("")]
    [InlineData("--ban")]
    [InlineData("--lead")]
    [InlineData("--ban --lead")]
    [InlineData("--timer 30,30")]
    public void SurrenderAtLocalSelectionSavesOneLossAndReplay(string mode)
    {
        using var cli = Start(mode.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        cli.Send("/surrender");
        cli.CloseInput();
        cli.Finish();
        Assert.Contains("对局结束：电脑 获胜", cli.Output);
        Assert.DoesNotContain("已退出，对局未完成", cli.Output);
        var replay = ReadCompleted(cli, 1);
        Assert.Equal(BattleRecordEventType.Surrender, Assert.Single(replay.Record.Events).Type);
        AssertProfile(cli, "original", 0, 1, 0);
    }

    [Theory]
    [InlineData("")]
    [InlineData("--timer 30,30")]
    public void LocalDrawUsesAiAndSavesOneDraw(string mode)
    {
        using var cli = Start(mode.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        cli.Send("/draw");
        cli.CloseInput();
        cli.Finish();
        Assert.Contains("电脑同意和局", cli.Output);
        var replay = ReadCompleted(cli, -1);
        Assert.Equal(new[] { BattleRecordEventType.DrawOffer, BattleRecordEventType.DrawResponse },
            replay.Record.Events.Select(entry => entry.Type));
        AssertProfile(cli, "original", 0, 0, 1);
    }

    [Fact]
    public void RejectedLocalDrawCannotBeRepeatedBeforeFiveTurnsAndCanStillSurrender()
    {
        string directory = NewDirectory();
        var fixture = MakeFixture(directory, 100);
        using var cli = StartAt(directory, "--pack", fixture.Db.PackId, "--pack-dir", fixture.Directory);
        cli.Send("/draw", "/draw", "/surrender");
        cli.CloseInput();
        cli.Finish();
        Assert.Contains("电脑拒绝和局", cli.Output);
        Assert.Contains("距离上次提议不足 5 回合", cli.Output);
        var replay = ReadCompleted(cli, 1, fixture.Db);
        Assert.Equal(new[] { BattleRecordEventType.DrawOffer, BattleRecordEventType.DrawResponse, BattleRecordEventType.Surrender },
            replay.Record.Events.Select(entry => entry.Type));
        Assert.False(replay.Record.Events[1].Accepted);
        AssertProfile(cli, fixture.Db.PackId, 0, 1, 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SurrenderFromReplacementDoesNotSubmitAnInvalidReplacement(bool timer)
    {
        string directory = NewDirectory();
        var fixture = MakeFixture(directory, 1);
        var args = new List<string> { "--pack", fixture.Db.PackId, "--pack-dir", fixture.Directory };
        if (timer) args.AddRange(new[] { "--timer", "30,30" });
        using var cli = StartAt(directory, args.ToArray());
        // 极低人气队伍用真实 Core 结算生成抵达补位的合法输入，不能修改 CLI 进程中的 Battle。
        var battle = new Battle(fixture.Db, new IList<string>[] { TeamA.Split(','), TeamB.Split(',') },
            new[] { "你", "电脑" }, new BattleOptions { Seed = 1 });
        for (int step = 0; battle.Phase == Phase.Choose && step < 20; step++)
        {
            var action = BattleAction.Skill(0);
            var foe = Ai.Choose(battle, 1, AiLevel.Normal);
            battle.ResolveTurn(action, foe);
            cli.WaitFor("选择行动（q 退出）> ", step + 1);
            cli.Send("1");
        }
        Assert.Equal(Phase.Replace, battle.Phase);
        Assert.True(battle.Need[0]);
        cli.WaitFor("请选择补位号位（q 退出）> ");
        cli.Send("/surrender");
        cli.CloseInput();
        cli.Finish();
        var replay = ReadCompleted(cli, 1, fixture.Db);
        Assert.Equal(BattleRecordEventType.Surrender, replay.Record.Events.Last().Type);
        Assert.DoesNotContain(replay.Record.Events, entry => entry.Type == BattleRecordEventType.Replace);
    }

    [Theory]
    [InlineData("")]
    [InlineData("--ban")]
    [InlineData("--lead")]
    public void HotseatSupportsSurrenderWithoutPretendingToSupportDraw(string mode)
    {
        using var cli = Start(new[] { "--hotseat" }.Concat(mode.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToArray());
        cli.Send("/draw", "/surrender");
        cli.CloseInput();
        cli.Finish();
        Assert.Contains("同屏模式暂不支持求和", cli.Output);
        Assert.Contains("对局结束：玩家 B 获胜", cli.Output);
        var replay = ReadCompleted(cli, 1);
        Assert.Equal(BattleRecordEventType.Surrender, Assert.Single(replay.Record.Events).Type);
        AssertProfile(cli, "original", 0, 1, 0);
    }

    [Fact]
    public void HotseatSecondPlayerCanSurrenderAfterPrivateHandover()
    {
        using var cli = Start("--hotseat");
        cli.Send("1", "", "/surrender");
        cli.CloseInput();
        cli.Finish();
        Assert.Contains("请把电脑交给「玩家 B」", cli.Output);
        Assert.Contains("对局结束：玩家 A 获胜", cli.Output);
        var replay = ReadCompleted(cli, 0);
        Assert.Equal(1, Assert.Single(replay.Record.Events).Side);
        AssertProfile(cli, "original", 1, 0, 0);
    }

    [Fact]
    public void QuitStillLeavesNoProfileOrReplay()
    {
        using var cli = Start();
        cli.Send("q");
        cli.CloseInput();
        cli.Finish();
        Assert.Contains("已退出，对局未完成", cli.Output);
        Assert.False(Directory.Exists(cli.SaveDirectory));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NetworkAcceptedDrawProducesMatchingFinalHashesAndOneDrawEach(bool automaticGuest)
    {
        using var host = Start("--host", "0", "--hash");
        int port = host.WaitForPort();
        using var guest = Start(new[] { "--join", "127.0.0.1:" + port, "--hash" }
            .Concat(automaticGuest ? new[] { "--auto" } : Array.Empty<string>()).ToArray());
        host.WaitFor("选择行动（q 退出）> ");
        if (!automaticGuest) guest.WaitFor("选择行动（q 退出）> ");
        host.Send("/draw");
        host.CloseInput();
        guest.WaitFor("对方提议和局：");
        if (!automaticGuest)
        {
            guest.Send("/draw accept");
            guest.CloseInput();
        }
        host.Finish();
        guest.Finish();
        AssertSameNetworkResult(host, guest, -1);
        AssertProfile(host, "original", 0, 0, 1);
        AssertProfile(guest, "original", 0, 0, 1);
    }

    [Fact]
    public void NetworkRejectionPreservesCommittedActionAndAllowsSurrenderWhileWaiting()
    {
        using var host = Start("--host", "0", "--hash");
        int port = host.WaitForPort();
        using var guest = Start("--join", "127.0.0.1:" + port, "--hash");
        host.WaitFor("选择行动（q 退出）> ");
        guest.WaitFor("选择行动（q 退出）> ");
        host.Send("1");
        host.WaitFor("等待对手……");
        host.Send("/draw");
        guest.WaitFor("对方提议和局：");
        guest.Send("/draw reject");
        host.WaitFor("拒绝和局，继续对战");
        host.Send("/draw");
        host.WaitFor("距离上次提议不足 5 回合");
        host.Send("/surrender");
        host.CloseInput();
        host.Finish();
        guest.Finish();
        AssertSameNetworkResult(host, guest, 1);
        var record = ReadCompleted(host, 1).Record;
        Assert.Equal(new[] { BattleRecordEventType.DrawOffer, BattleRecordEventType.DrawResponse, BattleRecordEventType.Surrender },
            record.Events.Select(entry => entry.Type));
        AssertProfile(host, "original", 0, 1, 0);
        AssertProfile(guest, "original", 1, 0, 0);
    }

    static void AssertSameNetworkResult(CliProcess first, CliProcess second, int winner)
    {
        string firstHash = Regex.Match(first.Output, @"最终状态哈希：([0-9a-f]{16})").Groups[1].Value;
        string secondHash = Regex.Match(second.Output, @"最终状态哈希：([0-9a-f]{16})").Groups[1].Value;
        Assert.Equal(16, firstHash.Length);
        Assert.Equal(firstHash, secondHash);
        var left = ReadCompleted(first, winner);
        var right = ReadCompleted(second, winner);
        Assert.Equal(firstHash, left.StateHash().ToString("x16"));
        Assert.Equal(left.Record.ToJson(), right.Record.ToJson());
    }

    static Battle ReadCompleted(CliProcess cli, int winner, ContentDb db = null)
    {
        string path = Assert.Single(Directory.GetFiles(Path.Combine(cli.SaveDirectory, "replays"), "*.json"));
        var record = BattleRecord.FromJson(File.ReadAllText(path));
        var replay = Replay.Run(db ?? BattleTests.LoadPack("original"), record);
        Assert.Equal(Phase.Over, replay.Phase);
        Assert.Equal(winner, replay.Winner);
        Assert.Single(Directory.GetFiles(Path.Combine(cli.SaveDirectory, "replay-library"), "*.json"),
            path => Path.GetFileName(path) != "index.json");
        return replay;
    }

    static void AssertProfile(CliProcess cli, string pack, int wins, int losses, int draws)
    {
        var profile = Profile.FromJson(File.ReadAllText(Path.Combine(cli.SaveDirectory, "profile.json")));
        Assert.Equal(wins, profile.Packs[pack].Wins);
        Assert.Equal(losses, profile.Packs[pack].Losses);
        Assert.Equal(draws, profile.Packs[pack].Draws);
    }

    static (ContentDb Db, string Directory) MakeFixture(string directory, int playerHp)
    {
        string content = Path.Combine(Repository(), "UnityProject", "Assets", "StreamDuel", "Resources");
        var pack = JsonNode.Parse(File.ReadAllText(Path.Combine(content, "Packs", "original.json")));
        pack["id"] = "t98_cli_fixture";
        pack["name"] = "命令行验收专用原创包";
        foreach (var streamer in pack["streamers"].AsArray())
        {
            bool player = TeamA.Split(',').Contains(streamer["id"].GetValue<string>());
            streamer["stats"]["hp"] = player ? playerHp : 500;
            streamer["persona"] = "pure";
            streamer["skills"] = new JsonArray("aim_burst", "quick_mic", "yolo_rush", "pregame");
            streamer["fifth"] = "endgame";
        }
        string path = Path.Combine(directory, "packs");
        Directory.CreateDirectory(path);
        string json = pack.ToJsonString();
        File.WriteAllText(Path.Combine(path, "fixture.json"), json, new UTF8Encoding(false));
        return (ContentDb.FromJson(File.ReadAllText(Path.Combine(content, "StreamDuelRules.json")), json), path);
    }

    static CliProcess Start(params string[] args) { return StartAt(NewDirectory(), args); }

    static CliProcess StartAt(string directory, params string[] args)
    {
        return new CliProcess(CliDll.Value, directory, new[] { "--seed", "1", "--team", TeamA,
            "--save-dir", Path.Combine(directory, "saves") }.Concat(args).ToArray());
    }

    static string NewDirectory()
    {
        string path = Path.Combine(Repository(), "Tools", "Cli", "obj", "t98-verification", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    static string Repository()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Tools", "Cli", "Cli.csproj"))) return directory.FullName;
        throw new InvalidOperationException("找不到命令行验收所在仓库。");
    }

    static string BuildCli()
    {
        var start = StartInfo(Repository());
        foreach (string argument in new[] { "build", "Tools/Cli", "-warnaserror", "--nologo" }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start);
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(120000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("CLI 构建超过两分钟。");
        }
        Assert.True(process.ExitCode == 0, output.GetAwaiter().GetResult() + error.GetAwaiter().GetResult());
        return Path.Combine(Repository(), "Tools", "Cli", "bin", "Debug", "net8.0", "Cli.dll");
    }

    static ProcessStartInfo StartInfo(string directory)
    {
        return new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
    }

    sealed class CliProcess : IDisposable
    {
        readonly Process process;
        readonly StringBuilder output = new StringBuilder(), error = new StringBuilder();
        readonly Task outputTask, errorTask;
        readonly string directory;
        public string SaveDirectory { get { return Path.Combine(directory, "saves"); } }
        public string Output { get { lock (output) return output.ToString(); } }

        public CliProcess(string dll, string directory, string[] args)
        {
            this.directory = directory;
            var start = StartInfo(directory);
            start.ArgumentList.Add(dll);
            foreach (string argument in args) start.ArgumentList.Add(argument);
            process = Process.Start(start);
            outputTask = Read(process.StandardOutput, output);
            errorTask = Read(process.StandardError, error);
        }

        static Task Read(StreamReader reader, StringBuilder destination)
        {
            // 独立读取线程不依赖测试上下文或线程池续体，避免并行计算拖延管道收尾。
            return Task.Factory.StartNew(() =>
            {
                // 批量字符读取可能等下一段 UTF-8 字节而扣留提示；无换行提示必须立即可见。
                int character;
                while ((character = reader.Read()) != -1)
                    lock (destination) destination.Append((char)character);
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        public void Send(params string[] lines)
        {
            foreach (string line in lines) process.StandardInput.WriteLine(line);
            process.StandardInput.Flush();
        }

        public void CloseInput() { process.StandardInput.Close(); }

        public int WaitForPort()
        {
            var elapsed = Stopwatch.StartNew();
            while (elapsed.ElapsedMilliseconds < 15000)
            {
                // 管道可能拆分任何一次 WriteLine，必须等完整行后再解析端口。
                var match = Regex.Match(Output, @"(?m)^监听端口：([0-9]+)\r?\n");
                if (match.Success) return int.Parse(match.Groups[1].Value);
                if (process.HasExited) break;
                Thread.Sleep(10);
            }
            throw new TimeoutException("等待完整监听端口行失败：" + directory + "\n" + Output);
        }

        public void WaitFor(string text, int occurrence = 1)
        {
            var elapsed = Stopwatch.StartNew();
            while (elapsed.ElapsedMilliseconds < 15000)
            {
                if (Regex.Matches(Output, Regex.Escape(text)).Count >= occurrence) return;
                if (process.HasExited) break;
                Thread.Sleep(10);
            }
            Assert.Fail("等待 CLI 输出失败：" + text + "\n证据：" + directory + "\n" + Output);
        }

        public void Finish()
        {
            Assert.True(process.WaitForExit(20000), "CLI 未在 20 秒内结束：" + directory + "\n" + Output);
            Assert.True(Task.WaitAll(new[] { outputTask, errorTask }, 5000), "CLI 输出没有结束。");
            Persist();
            Assert.True(process.ExitCode == 0, "CLI 退出码 " + process.ExitCode + "：" + directory + "\n" + error + "\n" + Output);
            Assert.Equal("", error.ToString());
        }

        void Persist()
        {
            File.WriteAllText(Path.Combine(directory, "stdout.log"), Output, new UTF8Encoding(false));
            lock (error) File.WriteAllText(Path.Combine(directory, "stderr.log"), error.ToString(), new UTF8Encoding(false));
        }

        public void Dispose()
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
            Task.WaitAll(new[] { outputTask, errorTask }, 5000);
            Persist();
            process.Dispose();
        }
    }
}
