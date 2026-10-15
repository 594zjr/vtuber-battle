#requires -Version 7.0
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$DotNet = 'dotnet'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$repositoryPrefix = $repository.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$project = Join-Path $PSScriptRoot 'Ladder.csproj'
$ladderDll = Join-Path $PSScriptRoot "bin/$Configuration/net8.0/Ladder.dll"
$runRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ('obj/verification/' + [Guid]::NewGuid().ToString('N'))))
if (-not $runRoot.StartsWith($repositoryPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw '验收目录必须位于当前仓库内。'
}
[void][IO.Directory]::CreateDirectory($runRoot)
$utf8 = [Text.UTF8Encoding]::new($false)
$invariant = [Globalization.CultureInfo]::InvariantCulture
$invocations = [Collections.Generic.List[object]]::new()

function Assert-Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "$Message 验收证据：$runRoot" }
}

function Invoke-ProcessCheck([string]$Name, [string[]]$Parameters, [int]$ExpectedExit = 0, [int]$TimeoutSeconds = 60) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $DotNet
    foreach ($parameter in $Parameters) { $start.ArgumentList.Add($parameter) }
    $start.WorkingDirectory = $repository
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = $utf8
    $start.StandardErrorEncoding = $utf8
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    $timer = [Diagnostics.Stopwatch]::StartNew()
    try {
        [void]$process.Start()
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $process.StandardInput.Close()
        $finished = $process.WaitForExit($TimeoutSeconds * 1000)
        if (-not $finished) {
            $process.Kill($true)
            $process.WaitForExit()
        }
        $timer.Stop()
        $outPath = Join-Path $runRoot "$Name.stdout.log"
        $errPath = Join-Path $runRoot "$Name.stderr.log"
        [IO.File]::WriteAllText($outPath, $stdout.Result, $utf8)
        [IO.File]::WriteAllText($errPath, $stderr.Result, $utf8)
        $invocations.Add([ordered]@{
            name = $Name; parameters = $Parameters; exit = $process.ExitCode
            elapsed_seconds = $timer.Elapsed.TotalSeconds; timeout = -not $finished
        })
        [IO.File]::WriteAllText((Join-Path $runRoot 'invocations.json'), ($invocations | ConvertTo-Json -Depth 8), $utf8)
        Assert-Check $finished "$Name 超过 $TimeoutSeconds 秒。"
        Assert-Check ($process.ExitCode -eq $ExpectedExit) "$Name 应退出 $ExpectedExit，实际为 $($process.ExitCode)。"
        return [pscustomobject]@{ Out = $stdout.Result; Err = $stderr.Result; OutPath = $outPath; ErrPath = $errPath; Seconds = $timer.Elapsed.TotalSeconds }
    }
    finally { $process.Dispose() }
}

function Invoke-LadderCheck([string]$Name, [string[]]$Parameters, [int]$ExpectedExit = 0) {
    $directory = Join-Path $runRoot $Name
    $arguments = @($ladderDll, '--out', $directory) + $Parameters
    $result = Invoke-ProcessCheck $Name $arguments $ExpectedExit
    if ($ExpectedExit -ne 0) {
        Assert-Check (-not (Test-Path -LiteralPath (Join-Path $directory 'teams.csv'))) "$Name 失败后不应生成 teams.csv。"
        Assert-Check (-not (Test-Path -LiteralPath (Join-Path $directory 'streamers.csv'))) "$Name 失败后不应生成 streamers.csv。"
    }
    return [pscustomobject]@{ Directory = $directory; Process = $result }
}

function Read-Number([string]$Value, [string]$Label) {
    $number = 0.0
    $parsed = [double]::TryParse($Value, [Globalization.NumberStyles]::Float, $invariant, [ref]$number)
    Assert-Check ($parsed -and -not [double]::IsNaN($number) -and -not [double]::IsInfinity($number)) "$Label 必须是有限数字。"
    return $number
}

function Read-Count([string]$Value, [string]$Label) {
    $number = 0L
    Assert-Check ([long]::TryParse($Value, [Globalization.NumberStyles]::None, $invariant, [ref]$number)) "$Label 必须是非负整数。"
    return $number
}

function Assert-Csv([string]$Directory, [int]$TeamCount, [int]$Rounds, [string]$Season = '') {
    $teamPath = Join-Path $Directory 'teams.csv'
    $streamerPath = Join-Path $Directory 'streamers.csv'
    Assert-Check (Test-Path -LiteralPath $teamPath -PathType Leaf) '缺少 teams.csv。'
    Assert-Check (Test-Path -LiteralPath $streamerPath -PathType Leaf) '缺少 streamers.csv。'
    foreach ($path in @($teamPath, $streamerPath)) {
        $bytes = [IO.File]::ReadAllBytes($path)
        Assert-Check (-not ($bytes.Length -ge 3 -and $bytes[0] -eq 239 -and $bytes[1] -eq 187 -and $bytes[2] -eq 191)) 'CSV 不应包含 UTF-8 BOM。'
        Assert-Check (-not [IO.File]::ReadAllText($path).Contains("`r")) 'CSV 应使用 LF 换行。'
    }
    $teams = @(Import-Csv -LiteralPath $teamPath -Encoding utf8)
    $streamers = @(Import-Csv -LiteralPath $streamerPath -Encoding utf8)
    Assert-Check ($teams.Count -eq $TeamCount) '阵容行数不符合 --teams。'
    Assert-Check ($streamers.Count -eq @($pack.streamers).Count) '主播榜必须覆盖内容包完整名单。'
    Assert-Check (@($teams.team_id | Sort-Object -Unique).Count -eq $TeamCount) 'team_id 必须唯一。'
    Assert-Check (@($streamers.id | Sort-Object -Unique).Count -eq $streamers.Count) '主播 id 必须唯一。'

    $totalGames = 0L; $totalWins = 0L; $totalDraws = 0L; $totalLosses = 0L; $totalByes = 0L
    $totalElo = 0.0; $totalPoints = 0.0; $previousElo = [double]::PositiveInfinity
    $rosterGames = @{}; $memberElos = @{}
    foreach ($streamer in $pack.streamers) { $rosterGames[$streamer.id] = 0L; $memberElos[$streamer.id] = [Collections.Generic.List[double]]::new() }
    $byeCounts = [Collections.Generic.List[long]]::new()
    for ($i = 0; $i -lt $teams.Count; $i++) {
        $team = $teams[$i]
        Assert-Check ((Read-Count $team.rank 'rank') -eq $i + 1) '阵容排名必须从 1 连续编号。'
        $members = @($team.slot1, $team.slot2, $team.slot3, $team.slot4, $team.slot5, $team.slot6)
        Assert-Check (@($members | Sort-Object -Unique).Count -eq 6) '阵容内部不能重复主播。'
        foreach ($member in $members) { Assert-Check $rosterGames.ContainsKey($member) "未知主播：$member。" }
        if ($Season.Length -gt 0) {
            $seasonRules = $pack.seasons.$Season
            foreach ($member in $members) { Assert-Check (-not ($seasonRules.banned -contains $member)) "阵容包含赛季禁用主播 $member。" }
            foreach ($pool in $seasonRules.limited) {
                $selected = @($members | Where-Object { $pool.ids -contains $_ }).Count
                Assert-Check ($selected -le $pool.max) '阵容超过赛季限制池上限。'
            }
        }

        $elo = Read-Number $team.elo 'elo'; $points = Read-Number $team.points 'points'
        $games = Read-Count $team.games 'games'; $wins = Read-Count $team.wins 'wins'
        $draws = Read-Count $team.draws 'draws'; $losses = Read-Count $team.losses 'losses'; $byes = Read-Count $team.byes 'byes'
        Assert-Check ($elo -le $previousElo + 0.000001) '阵容榜应按 Elo 降序排列。'
        Assert-Check ($games -eq $wins + $draws + $losses) '阵容胜平负之和应等于真实对局数。'
        Assert-Check ($games + 2 * $byes -eq 2 * $Rounds) '每轮应打两局或轮空一次。'
        Assert-Check ([Math]::Abs($points - ($wins + 0.5 * $draws + 2 * $byes)) -lt 0.000001) '积分与胜平负、轮空不一致。'
        $previousElo = $elo
        $totalElo += $elo; $totalPoints += $points; $totalGames += $games
        $totalWins += $wins; $totalDraws += $draws; $totalLosses += $losses; $totalByes += $byes
        $byeCounts.Add($byes)
        foreach ($member in $members) { $rosterGames[$member] += $games; $memberElos[$member].Add($elo) }
    }
    $actualGames = ($TeamCount - ($TeamCount % 2)) * $Rounds
    Assert-Check ($totalGames -eq 2 * $actualGames) '全局对局数不符合两局换边赛制。'
    Assert-Check ($totalWins -eq $totalLosses -and $totalDraws % 2 -eq 0) '双方胜负或平局计数不守恒。'
    Assert-Check ($totalByes -eq ($TeamCount % 2) * $Rounds) '奇数队伍每轮应恰好有一个轮空。'
    Assert-Check ((($byeCounts | Measure-Object -Maximum).Maximum - ($byeCounts | Measure-Object -Minimum).Minimum) -le 1) '轮空次数分配应优先覆盖尚未轮空的队伍。'
    Assert-Check ([Math]::Abs($totalPoints - ($actualGames + 2 * $totalByes)) -lt 0.000001) '全局积分不守恒。'
    Assert-Check ([Math]::Abs($totalElo - 1500 * $TeamCount) -le $TeamCount * 0.0000006) '双方 Elo 更新必须零和（允许 CSV 舍入误差）。'

    $sumRoster = 0L; $sumAppearances = 0L; $sumDealt = 0L; $previousWeighted = [double]::PositiveInfinity; $unseenStarted = $false
    for ($i = 0; $i -lt $streamers.Count; $i++) {
        $streamer = $streamers[$i]
        Assert-Check ((Read-Count $streamer.rank 'streamer rank') -eq $i + 1) '主播排名必须从 1 连续编号。'
        Assert-Check $rosterGames.ContainsKey($streamer.id) '主播榜出现未知主播。'
        $appearances = Read-Count $streamer.appearances 'appearances'; $roster = Read-Count $streamer.roster_games 'roster_games'
        $wins = Read-Count $streamer.wins 'streamer wins'; $draws = Read-Count $streamer.draws 'streamer draws'; $losses = Read-Count $streamer.losses 'streamer losses'
        Assert-Check ($roster -eq $rosterGames[$streamer.id]) '主播入队局数与阵容榜不一致。'
        Assert-Check ($appearances -le $roster) '实际登场局数不能超过入队局数。'
        Assert-Check ($appearances -eq $wins + $draws + $losses) '主播胜平负应只累计实际登场对局。'
        $sumDealt += Read-Count $streamer.damage_dealt 'damage_dealt'
        [void](Read-Count $streamer.kos 'kos'); [void](Read-Count $streamer.turns_active 'turns_active')
        if ($appearances -eq 0) {
            $unseenStarted = $true
            Assert-Check ([string]::IsNullOrEmpty($streamer.weighted_elo)) '未登场主播的加权 Elo 应为空。'
        }
        else {
            Assert-Check (-not $unseenStarted) '未登场主播应排在有登场记录者之后。'
            $weighted = Read-Number $streamer.weighted_elo 'weighted_elo'
            Assert-Check ($weighted -le $previousWeighted + 0.000001) '主播榜应按加权 Elo 降序排列。'
            $minimum = ($memberElos[$streamer.id] | Measure-Object -Minimum).Minimum
            $maximum = ($memberElos[$streamer.id] | Measure-Object -Maximum).Maximum
            Assert-Check ($weighted -ge $minimum - 0.000001 -and $weighted -le $maximum + 0.000001) '加权 Elo 应位于包含该主播的阵容 Elo 范围内。'
            $previousWeighted = $weighted
        }
        $sumRoster += $roster; $sumAppearances += $appearances
    }
    Assert-Check ($sumRoster -eq 6 * $totalGames) '全榜入队局数应为各队真实对局数之和的六倍。'
    Assert-Check ($sumAppearances -ge 2 * $actualGames -and $sumAppearances -le $sumRoster) '每局双方至少各有一位主播实际登场。'
    Assert-Check ($sumDealt -gt 0) '正常模拟应产生伤害统计。'
}

[void](Invoke-ProcessCheck '00-build' @('build', $project, '-c', $Configuration, '--nologo'))
$pack = [IO.File]::ReadAllText((Join-Path $PSScriptRoot "bin/$Configuration/net8.0/Content/Packs/original.json")) | ConvertFrom-Json
$baselineArguments = @('--pack', 'original', '--teams', '16', '--rounds', '3', '--seed', '1', '--level', 'normal')
$first = Invoke-LadderCheck '01-repeat-a' $baselineArguments
$second = Invoke-LadderCheck '02-repeat-b' $baselineArguments
Assert-Csv $first.Directory 16 3
Assert-Csv $second.Directory 16 3
Assert-Check ($first.Process.Seconds -lt 60 -and $second.Process.Seconds -lt 60) '16 队 3 轮应在一分钟内完成（不含构建）。'
foreach ($name in @('teams.csv', 'streamers.csv')) {
    $firstHash = (Get-FileHash -LiteralPath (Join-Path $first.Directory $name) -Algorithm SHA256).Hash
    $secondHash = (Get-FileHash -LiteralPath (Join-Path $second.Directory $name) -Algorithm SHA256).Hash
    Assert-Check ($firstHash -eq $secondHash) "同参数两次运行的 $name 字节不一致。"
}
Assert-Check ((Get-FileHash -LiteralPath $first.Process.OutPath -Algorithm SHA256).Hash -eq (Get-FileHash -LiteralPath $second.Process.OutPath -Algorithm SHA256).Hash) '同参数两次控制台标准输出不一致。'
Assert-Check ((Get-FileHash -LiteralPath $first.Process.ErrPath -Algorithm SHA256).Hash -eq (Get-FileHash -LiteralPath $second.Process.ErrPath -Algorithm SHA256).Hash) '同参数两次控制台标准错误不一致。'

$season = Invoke-LadderCheck '03-season' ($baselineArguments + @('--season', 's1'))
Assert-Csv $season.Directory 16 3 's1'
$odd = Invoke-LadderCheck '04-odd' @('--pack', 'original', '--teams', '5', '--rounds', '6', '--seed', '7')
Assert-Csv $odd.Directory 5 6
$hard = Invoke-LadderCheck '05-hard' @('--pack', 'original', '--teams', '2', '--rounds', '1', '--seed', '1', '--level', 'hard')
Assert-Csv $hard.Directory 2 1
[void](Invoke-LadderCheck '06-unknown-season' @('--pack', 'original', '--teams', '2', '--rounds', '1', '--season', 'missing_season') 1)
[void](Invoke-LadderCheck '07-invalid-team-count' @('--teams', '1') 1)
[void](Invoke-LadderCheck '08-invalid-level' @('--level', 'easy') 1)
[void](Invoke-LadderCheck '09-missing-value' @('--seed') 1)
[void](Invoke-LadderCheck '10-unknown-option' @('--unknown') 1)

$report = [ordered]@{
    result = 'PASS'
    first_16x3_seconds = $first.Process.Seconds
    second_16x3_seconds = $second.Process.Seconds
    checks = @('CSV与控制台字节可复现', '16x3耗时', '合法阵容与赛季限制', '对局积分胜负守恒', 'Elo零和', '实际登场及累计统计', '加权Elo范围与零登场', '奇数轮空', '困难难度', '无效参数不输出CSV')
    weighted_elo_limit = 'CSV未提供各阵容逐主播登场权重；脚本检查分母、数值范围和空值，精确加权公式由独立源码审查确认。'
    invocations = $invocations
}
[IO.File]::WriteAllText((Join-Path $runRoot 'report.json'), ($report | ConvertTo-Json -Depth 10), $utf8)
Write-Output "PASS：可复现、性能、合法阵容、积分/Elo、登场统计、轮空、困难模式与无效输入。证据：$runRoot"
