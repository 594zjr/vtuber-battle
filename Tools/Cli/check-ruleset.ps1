#requires -Version 7.0
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$repositoryPrefix = $repository.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$current = [IO.Path]::GetFullPath((Get-Location).Path)
if ($current -ne $repository -and -not $current.StartsWith($repositoryPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw '请在本仓库内运行此脚本。'
}
$cliDll = Join-Path $PSScriptRoot "bin/$Configuration/net8.0/Cli.dll"
if (-not (Test-Path -LiteralPath $cliDll -PathType Leaf)) {
    throw "请先运行 dotnet build Tools/Cli -c $Configuration。"
}
$runDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ('obj/ruleset-check/' + [Guid]::NewGuid().ToString('N'))))
if (-not $runDirectory.StartsWith($repositoryPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw '验收产物目录必须位于本仓库内。'
}
[void][IO.Directory]::CreateDirectory($runDirectory)
$utf8 = [Text.UTF8Encoding]::new($false)
$checks = [Collections.Generic.List[string]]::new()

function Assert-Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "$Message 证据目录：$runDirectory" }
    $checks.Add($Message)
}

function Invoke-CliCheck([string]$Name, [string[]]$Parameters, [int]$ExpectedExit = 0, [string]$InputText = '') {
    $caseDirectory = Join-Path $runDirectory $Name
    [void][IO.Directory]::CreateDirectory($caseDirectory)
    $saveDirectory = Join-Path $caseDirectory 'saves'
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = 'dotnet'
    $start.ArgumentList.Add($cliDll)
    $start.ArgumentList.Add('--save-dir')
    $start.ArgumentList.Add($saveDirectory)
    foreach ($parameter in $Parameters) { $start.ArgumentList.Add($parameter) }
    $start.WorkingDirectory = $caseDirectory
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = $utf8
    $start.StandardErrorEncoding = $utf8
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    try {
        [void]$process.Start()
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if ($InputText.Length -gt 0) { $process.StandardInput.Write($InputText) }
        $process.StandardInput.Close()
        if (-not $process.WaitForExit(30000)) {
            $process.Kill($true)
            throw "$Name 超过 30 秒未退出。"
        }
        $result = [pscustomobject]@{
            Out = $stdout.Result; Err = $stderr.Result; Exit = $process.ExitCode
            Directory = $caseDirectory; SaveDir = $saveDirectory
        }
        [IO.File]::WriteAllText((Join-Path $caseDirectory 'stdout.log'), $result.Out, $utf8)
        [IO.File]::WriteAllText((Join-Path $caseDirectory 'stderr.log'), $result.Err, $utf8)
        [IO.File]::WriteAllText((Join-Path $caseDirectory 'arguments.json'), (ConvertTo-Json -InputObject $Parameters), $utf8)
        Assert-Check ($result.Exit -eq $ExpectedExit) "$Name 退出码为 $ExpectedExit。"
        return $result
    }
    finally { $process.Dispose() }
}

function Replays([string]$SaveDirectory) {
    return @(Get-ChildItem -LiteralPath (Join-Path $SaveDirectory 'replays') -File -Filter '*.json' |
        Where-Object { -not $_.Name.StartsWith('.') } | ForEach-Object {
            $json = [IO.File]::ReadAllText($_.FullName)
            [pscustomobject]@{ Path = $_.FullName; Json = $json; Record = ($json | ConvertFrom-Json) }
        } | Sort-Object { $_.Record.seed })
}

function ProfileTotal([string]$SaveDirectory) {
    $stats = ([IO.File]::ReadAllText((Join-Path $SaveDirectory 'profile.json')) | ConvertFrom-Json).packs.original
    return $stats.wins + $stats.losses + $stats.draws
}

$plain = Invoke-CliCheck 'plain' @('--auto', '--seed', '54')
$casual = Invoke-CliCheck 'casual' @('--ruleset', 'casual', '--auto', '--seed', '54')
$plainReplays = @(Replays $plain.SaveDir)
$casualReplays = @(Replays $casual.SaveDir)
Assert-Check ($plainReplays.Count -eq 1 -and $casualReplays.Count -eq 1) '默认模式与休闲模式各保存一局。'
Assert-Check ($plainReplays[0].Json -ceq $casualReplays[0].Json) '休闲赛制保留默认单局的种子、队伍、行动和回放。'
Assert-Check ((ProfileTotal $casual.SaveDir) -eq 1) '休闲赛制只累计一次战绩。'

$ranked = Invoke-CliCheck 'ranked' @('--ruleset', 'ranked', '--auto', '--hash', '--seed', '54', '--report', 'match.md')
$games = @(Replays $ranked.SaveDir)
Assert-Check ($games.Count -ge 2 -and $games.Count -le 3) '排位赛完成两局或三局并分别保存回放。'
Assert-Check ((ProfileTotal $ranked.SaveDir) -eq $games.Count) '排位赛按已完成局数累计档案。'
Assert-Check ($ranked.Out.Contains('当前 CLI 尚未启用倒计时')) '计时配置明确显示尚未执行倒计时。'
Assert-Check ($ranked.Out.Contains('状态哈希：') -and $ranked.Out.Contains('赛制结束：')) '排位赛输出逐回合哈希及最终比分。'
$report = [IO.File]::ReadAllText((Join-Path $ranked.Directory 'match.md'))
Assert-Check ($report.StartsWith('# 赛制战报')) '多局只导出一份赛制战报。'
Assert-Check ([regex]::Matches($report, '(?m)^## 第 \d+ 局$').Count -eq $games.Count) '汇总战报保留全部已完成局而非仅最后一局。'
for ($index = 0; $index -lt $games.Count; $index++) {
    $record = $games[$index].Record
    Assert-Check ($record.seed -eq 54 + $index) "排位第 $($index + 1) 局使用确定性递增种子。"
    Assert-Check ($record.banOne -and $record.leadPick) "排位第 $($index + 1) 局启用禁用和首发。"
    Assert-Check (@($record.teams[0]).Count -eq 6 -and @($record.teams[1]).Count -eq 6) "排位第 $($index + 1) 局从原始六人阵容开始。"
}
$profileBefore = [IO.File]::ReadAllText((Join-Path $ranked.SaveDir 'profile.json'))
for ($index = 0; $index -lt $games.Count; $index++) {
    $replay = Invoke-CliCheck "replay-$index" @('--replay', $games[$index].Path, '--auto', '--save-dir', $ranked.SaveDir)
    Assert-Check ($replay.Out.Contains('回看结束：')) "排位第 $($index + 1) 局可以独立回看。"
}
Assert-Check ($profileBefore -ceq [IO.File]::ReadAllText((Join-Path $ranked.SaveDir 'profile.json'))) '回看排位各局不再次累计战绩。'
Assert-Check (@(Replays $ranked.SaveDir).Count -eq $games.Count) '回看排位各局不另存回放。'
$repeat = Invoke-CliCheck 'ranked-repeat' @('--ruleset', 'ranked', '--auto', '--seed', '54')
$repeatedGames = @(Replays $repeat.SaveDir)
Assert-Check ($repeatedGames.Count -eq $games.Count) '同种子排位赛局数可复现。'
for ($index = 0; $index -lt $games.Count; $index++) {
    Assert-Check ($games[$index].Json -ceq $repeatedGames[$index].Json) "排位第 $($index + 1) 局完整记录可复现。"
}

$failedProfileDirectory = Join-Path $runDirectory 'profile-failure/saves/profile.json'
[void][IO.Directory]::CreateDirectory($failedProfileDirectory)
$profileFailure = Invoke-CliCheck 'profile-failure' @('--ruleset', 'ranked', '--auto', '--seed', '54', '--report', 'match.md') -ExpectedExit 1
$failureGames = @(Replays $profileFailure.SaveDir)
$failureReport = [IO.File]::ReadAllText((Join-Path $profileFailure.Directory 'match.md'))
Assert-Check ($failureGames.Count -eq $games.Count -and $profileFailure.Out.Contains('赛制结束：')) '档案保存失败仍完成后续对局并分别保留回放。'
Assert-Check ([regex]::Matches($profileFailure.Err, '档案保存失败').Count -eq $games.Count) '每局档案保存失败均给出明确错误。'
Assert-Check ($failureReport -ceq $report) '档案保存失败不妨碍生成完整汇总战报。'
Assert-Check (Test-Path -LiteralPath $failedProfileDirectory -PathType Container) '存档失败不覆盖占据档案路径的目录。'

$chaos = Invoke-CliCheck 'chaos' @('--ruleset', 'chaos', '--auto', '--seed', '54')
$chaosGames = @(Replays $chaos.SaveDir)
Assert-Check ($chaosGames.Count -eq 1 -and (ProfileTotal $chaos.SaveDir) -eq 1) '乱斗赛制只打一局。'
Assert-Check (($chaosGames[0].Record.modifiers -join ',') -ceq 'half_pp,hype_start') '乱斗回放携带两项赛制修饰器。'

$quit = Invoke-CliCheck 'quit' @('--ruleset', 'ranked', '--report', 'unfinished.md') -InputText "q`n"
Assert-Check (-not (Test-Path -LiteralPath $quit.SaveDir) -and -not (Test-Path -LiteralPath (Join-Path $quit.Directory 'unfinished.md'))) '首局提前退出不保存档案、回放或汇总战报。'
$listed = Invoke-CliCheck 'list' @('--list-packs', '--ruleset', 'unknown', '--report', 'saves/profile.json')
Assert-Check ($listed.Out.Contains('original：') -and -not (Test-Path -LiteralPath $listed.SaveDir)) '列包模式不启动赛制或写存档。'
$help = Invoke-CliCheck 'help' @('--help', '--ruleset', 'ranked', '--ban')
Assert-Check ($help.Out.Contains('--ruleset') -and -not (Test-Path -LiteralPath $help.SaveDir)) '帮助列出赛制用法且不受模式冲突影响。'

$invalidCases = @(
    @{ Name = 'missing'; Args = @('--ruleset') },
    @{ Name = 'blank'; Args = @('--ruleset', ' ') },
    @{ Name = 'duplicate'; Args = @('--ruleset', 'casual', '--ruleset', 'ranked') },
    @{ Name = 'unknown'; Args = @('--ruleset', 'no_such') },
    @{ Name = 'case'; Args = @('--ruleset', 'Ranked') },
    @{ Name = 'ban'; Args = @('--ruleset', 'ranked', '--ban') },
    @{ Name = 'lead'; Args = @('--ruleset', 'ranked', '--lead') },
    @{ Name = 'host'; Args = @('--ruleset', 'ranked', '--host', '0') },
    @{ Name = 'join'; Args = @('--ruleset', 'ranked', '--join', 'localhost:1') },
    @{ Name = 'replay'; Args = @('--ruleset', 'ranked', '--replay', 'missing.json') },
    @{ Name = 'report-reserved'; Args = @('--ruleset', 'ranked', '--auto', '--report', 'saves/profile.json') }
)
foreach ($case in $invalidCases) {
    $result = Invoke-CliCheck $case.Name $case.Args -ExpectedExit 1
    Assert-Check ($result.Err.Contains('错误：') -and -not (Test-Path -LiteralPath $result.SaveDir)) "$($case.Name) 在开局和保存前明确拒绝。"
}

[IO.File]::WriteAllText((Join-Path $runDirectory 'checks.json'), (ConvertTo-Json -InputObject $checks.ToArray()), $utf8)
Write-Output "赛制 CLI 检查通过：$($checks.Count) 项；证据目录：$runDirectory"
