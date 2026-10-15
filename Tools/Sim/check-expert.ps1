<#
.SYNOPSIS
复现专家对困难的胜率与专家决策耗时，不含网络或额外依赖。
.EXAMPLE
pwsh -File Tools/Sim/check-expert.ps1 -Mode acceptance -Games 200
.EXAMPLE
pwsh -File Tools/Sim/check-expert.ps1 -Mode random -Games 200
.NOTES
acceptance 沿用 T-35 验收的分队和 seed；paired/random 每两局共用阵容及 seed、交换专家所在侧。
耗时只统计专家的行动和补位选择，首个冷启动决策也计入；进度写 stderr，最终指标为 JSON。
#>
[CmdletBinding()]
param(
    [ValidateSet('acceptance', 'paired', 'random')]
    [string]$Mode = 'acceptance',

    [ValidateRange(1, 2147483647)]
    [int]$Games = 200
)

$ErrorActionPreference = 'Stop'
$Mode = $Mode.ToLowerInvariant()
if ($Mode -ne 'acceptance' -and $Games % 2 -ne 0)
{
    throw 'paired 和 random 模式的 Games 必须是正偶数，以保证双方侧位配对。'
}

$repoRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::Combine($PSScriptRoot, '..', '..'))
$benchmarkDir = Join-Path $PSScriptRoot 'obj/t35-benchmark'
$projectPath = Join-Path $benchmarkDir 'ExpertBenchmark.csproj'
$programPath = Join-Path $benchmarkDir 'Program.cs'
$engineProject = [System.Security.SecurityElement]::Escape((Join-Path $repoRoot 'Engine/StreamDuel.Core.csproj'))
$contentGlob = [System.Security.SecurityElement]::Escape((Join-Path $repoRoot 'UnityProject/Assets/StreamDuel/Resources/**/*.json'))
$projectSource = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <LangVersion>9.0</LangVersion>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="$engineProject" />
    <None Include="$contentGlob" CopyToOutputDirectory="PreserveNewest" LinkBase="Content" />
  </ItemGroup>
</Project>
"@
$programSource = @'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using StreamDuel.Core;

internal static class Program
{
    static int Main(string[] args)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        string mode = args.Length > 0 ? args[0] : "acceptance";
        int games = args.Length > 1 ? int.Parse(args[1]) : 200;
        if (mode != "acceptance" && mode != "paired" && mode != "random") throw new ArgumentException("mode: acceptance / paired / random");
        string content = Path.Combine(AppContext.BaseDirectory, "Content");
        var db = ContentDb.FromJson(File.ReadAllText(Path.Combine(content, "StreamDuelRules.json")),
            File.ReadAllText(Path.Combine(content, "Packs", "original.json")));
        var ids = db.Streamers.Select(s => s.Id).ToArray();
        int wins = 0, losses = 0, draws = 0;
        int actionCount = 0, replacementCount = 0, turns = 0;
        double actionMs = 0, replacementMs = 0, slowestMs = 0;
        var seatGames = new int[2];
        var seatWins = new int[2];
        var overall = Stopwatch.StartNew();
        for (int g = 0; g < games; g++)
        {
            int caseIndex = mode == "acceptance" ? g : g / 2;
            uint seed = (uint)((mode == "random" ? 5000 : 1000) + caseIndex);
            int expertSide = g % 2;
            var order = ids.Skip(caseIndex % ids.Length).Concat(ids.Take(caseIndex % ids.Length)).ToArray();
            if (mode == "random")
            {
                var setup = NewBattle(db, ids, seed);
                order = ids.ToArray();
                for (int i = order.Length - 1; i > 0; i--)
                {
                    int j = (int)(setup.Random() * (i + 1));
                    string value = order[i]; order[i] = order[j]; order[j] = value;
                }
            }
            var battle = NewBattle(db, order, seed);
            int guard = 0;
            while (battle.Phase != Phase.Over && guard++ < 500)
            {
                if (battle.Phase == Phase.Choose)
                {
                    var actions = new BattleAction[2];
                    for (int side = 0; side < 2; side++)
                    {
                        var watch = Stopwatch.StartNew();
                        actions[side] = Ai.Choose(battle, side, side == expertSide ? AiLevel.Expert : AiLevel.Hard);
                        watch.Stop();
                        if (side == expertSide)
                        {
                            actionCount++;
                            actionMs += watch.Elapsed.TotalMilliseconds;
                            slowestMs = Math.Max(slowestMs, watch.Elapsed.TotalMilliseconds);
                        }
                    }
                    battle.ResolveTurn(actions[0], actions[1]);
                }
                else
                {
                    for (int side = 0; side < 2; side++)
                    {
                        if (!battle.Need[side]) continue;
                        var watch = Stopwatch.StartNew();
                        int replacement = Ai.ChooseReplacement(battle, side, side == expertSide ? AiLevel.Expert : AiLevel.Hard);
                        watch.Stop();
                        if (side == expertSide)
                        {
                            replacementCount++;
                            replacementMs += watch.Elapsed.TotalMilliseconds;
                            slowestMs = Math.Max(slowestMs, watch.Elapsed.TotalMilliseconds);
                        }
                        battle.Replace(side, replacement);
                    }
                }
            }
            if (battle.Phase != Phase.Over) throw new InvalidOperationException("未结束：game=" + g + ", seed=" + seed);
            turns += battle.Turn;
            seatGames[expertSide]++;
            if (battle.Winner == expertSide) { wins++; seatWins[expertSide]++; }
            else if (battle.Winner == -1) draws++;
            else losses++;
            if ((g + 1) % 20 == 0) Console.Error.WriteLine(mode + " " + (g + 1) + "/" + games + ": " + wins + "胜 " + losses + "负 " + draws + "平");
        }
        overall.Stop();
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            mode, games, wins, losses, draws,
            decisiveWinRate = wins / (double)Math.Max(1, wins + losses),
            seatGames, seatWins,
            averageTurns = turns / (double)games,
            actionCount, replacementCount,
            averageActionMs = actionMs / Math.Max(1, actionCount),
            averageReplacementMs = replacementMs / Math.Max(1, replacementCount),
            averageDecisionMs = (actionMs + replacementMs) / Math.Max(1, actionCount + replacementCount),
            slowestMs,
            elapsedSeconds = overall.Elapsed.TotalSeconds
        }));
        return 0;
    }

    static Battle NewBattle(ContentDb db, string[] order, uint seed)
    {
        return new Battle(db, new IList<string>[] { order.Take(6).ToList(), order.Skip(6).Take(6).ToList() }, new[] { "A", "B" }, seed);
    }
}
'@

$previousTemp = [System.Environment]::GetEnvironmentVariable('TEMP', 'Process')
$previousTmp = [System.Environment]::GetEnvironmentVariable('TMP', 'Process')
try
{
    New-Item -ItemType Directory -Path $benchmarkDir -Force | Out-Null
    [System.Environment]::SetEnvironmentVariable('TEMP', $benchmarkDir, 'Process')
    [System.Environment]::SetEnvironmentVariable('TMP', $benchmarkDir, 'Process')
    $utf8 = [System.Text.UTF8Encoding]::new($false)
    [System.IO.File]::WriteAllText($projectPath, $projectSource, $utf8)
    [System.IO.File]::WriteAllText($programPath, $programSource, $utf8)

    & dotnet build $projectPath --nologo --verbosity quiet | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "专家基准构建失败，退出码 $LASTEXITCODE。" }
    $assemblyPath = Join-Path $benchmarkDir 'bin/Debug/net8.0/ExpertBenchmark.dll'
    & dotnet $assemblyPath $Mode $Games
    if ($LASTEXITCODE -ne 0) { throw "专家基准运行失败，退出码 $LASTEXITCODE。" }
}
finally
{
    [System.Environment]::SetEnvironmentVariable('TEMP', $previousTemp, 'Process')
    [System.Environment]::SetEnvironmentVariable('TMP', $previousTmp, 'Process')
}
