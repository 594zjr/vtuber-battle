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

function Read-AvailableOutput($State) {
    if ($State.Closed) { return }
    if ($null -eq $State.Pending) { $State.Pending = $State.Reader.ReadAsync($State.Buffer, 0, $State.Buffer.Length) }
    if (-not $State.Pending.IsCompleted) { return }
    $count = $State.Pending.GetAwaiter().GetResult()
    $State.Pending = $null
    if ($count -eq 0) { $State.Closed = $true }
    else { [void]$State.Text.Append($State.Buffer, 0, $count) }
}

function Invoke-CliCheck([string]$Name, [string[]]$Parameters, [int]$ExpectedExit = 0,
    [string]$InputText = '', [object[]]$InputSteps = @()) {
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
    $started = $false
    try {
        [void]$process.Start()
        $started = $true
        $stderr = $process.StandardError.ReadToEndAsync()
        if ($InputSteps.Count -eq 0) {
            $stdout = $process.StandardOutput.ReadToEndAsync()
            if ($InputText.Length -gt 0) { $process.StandardInput.Write($InputText) }
            $process.StandardInput.Close()
            if (-not $process.WaitForExit(30000)) {
                $process.Kill($true)
                throw "$Name 超过 30 秒未退出。"
            }
            $output = $stdout.GetAwaiter().GetResult()
        }
        else {
            $state = [pscustomobject]@{
                Reader = $process.StandardOutput; Buffer = [char[]]::new(4096)
                Text = [Text.StringBuilder]::new(); Pending = $null; Closed = $false
            }
            $elapsed = [Diagnostics.Stopwatch]::StartNew()
            $offset = 0
            foreach ($step in $InputSteps) {
                $found = -1
                while ($found -lt 0) {
                    Read-AvailableOutput $state
                    $found = $state.Text.ToString().IndexOf($step.Expect, $offset, [StringComparison]::Ordinal)
                    if ($found -ge 0) { break }
                    if ($state.Closed -or $elapsed.ElapsedMilliseconds -ge 30000) {
                        [IO.File]::WriteAllText((Join-Path $caseDirectory 'stdout.log'), $state.Text.ToString(), $utf8)
                        throw "$Name 未等到提示：$($step.Expect)"
                    }
                    Start-Sleep -Milliseconds 10
                }
                $offset = $found + $step.Expect.Length
                if ($step.ContainsKey('DelayMs')) {
                    $until = $elapsed.ElapsedMilliseconds + $step.DelayMs
                    while ($elapsed.ElapsedMilliseconds -lt $until) {
                        Read-AvailableOutput $state
                        Start-Sleep -Milliseconds 10
                    }
                }
                $process.StandardInput.Write($step.Input)
                $process.StandardInput.Flush()
            }
            $process.StandardInput.Close()
            while (-not $process.HasExited -or -not $state.Closed) {
                Read-AvailableOutput $state
                if ($elapsed.ElapsedMilliseconds -ge 30000) { throw "$Name 超过 30 秒未退出。" }
                Start-Sleep -Milliseconds 10
            }
            $output = $state.Text.ToString()
        }
        $result = [pscustomobject]@{
            Out = $output; Err = $stderr.GetAwaiter().GetResult(); Exit = $process.ExitCode
            Directory = $caseDirectory; SaveDir = $saveDirectory
        }
        [IO.File]::WriteAllText((Join-Path $caseDirectory 'stdout.log'), $result.Out, $utf8)
        [IO.File]::WriteAllText((Join-Path $caseDirectory 'stderr.log'), $result.Err, $utf8)
        [IO.File]::WriteAllText((Join-Path $caseDirectory 'arguments.json'), (ConvertTo-Json -InputObject $Parameters), $utf8)
        Assert-Check ($result.Exit -eq $ExpectedExit) "$Name 退出码为 $ExpectedExit。"
        return $result
    }
    finally {
        if ($started -and -not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
        $process.Dispose()
    }
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

function LibraryFiles([string]$SaveDirectory) {
    return @(Get-ChildItem -LiteralPath (Join-Path $SaveDirectory 'replay-library') -File -Filter '*.json' |
        Where-Object { $_.Name -ne 'index.json' -and -not $_.Name.StartsWith('.') })
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
Assert-Check ($ranked.Out.Contains('计时（第 ') -and -not $ranked.Out.Contains('尚未启用倒计时')) '排位赛实际启用阶段计时，不再显示过期的占位提示。'
Assert-Check (@(LibraryFiles $ranked.SaveDir).Count -eq $games.Count) '排位每个完成局同时进入独立录像库。'
$rankedProfile = [IO.File]::ReadAllText((Join-Path $ranked.SaveDir 'profile.json')) | ConvertFrom-Json
Assert-Check ($null -eq $rankedProfile.PSObject.Properties['ratings'] -and -not $ranked.Out.Contains('本机排位（')) '本地 ranked 赛制不创建或累计联网排位分。'
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
Assert-Check (@(LibraryFiles $ranked.SaveDir).Count -eq $games.Count) '回看排位各局不另存录像库条目。'
$ratedDirectory = Join-Path $runDirectory 'ranked-repeat/saves'
[void][IO.Directory]::CreateDirectory($ratedDirectory)
$ratedFixture = '{"packs":{},"ratings":{"默认":{"rating":1320,"games":4,"season":2,"peakRating":1400,"isPlaced":true,"history":[{"season":1,"finalRating":1640,"peakRating":1700,"tier":"人气聚光","games":12}]},"保留赛制":{"rating":1601,"games":8}}}'
[IO.File]::WriteAllText((Join-Path $ratedDirectory 'profile.json'), $ratedFixture, $utf8)
$repeat = Invoke-CliCheck 'ranked-repeat' @('--ruleset', 'ranked', '--auto', '--seed', '54')
$repeatedGames = @(Replays $repeat.SaveDir)
Assert-Check ($repeatedGames.Count -eq $games.Count) '同种子排位赛局数可复现。'
for ($index = 0; $index -lt $games.Count; $index++) {
    Assert-Check ($games[$index].Json -ceq $repeatedGames[$index].Json) "排位第 $($index + 1) 局完整记录可复现。"
}
$retained = ([IO.File]::ReadAllText((Join-Path $ratedDirectory 'profile.json')) | ConvertFrom-Json).ratings
Assert-Check ($retained.'默认'.rating -eq 1320 -and $retained.'默认'.games -eq 4 -and $retained.'默认'.season -eq 2 -and
    $retained.'默认'.peakRating -eq 1400 -and $retained.'默认'.isPlaced -and @($retained.'默认'.history).Count -eq 1 -and
    ($retained.'默认'.history | ConvertTo-Json -Compress) -ceq (($ratedFixture | ConvertFrom-Json).ratings.'默认'.history | ConvertTo-Json -Compress) -and
    $retained.'保留赛制'.rating -eq 1601 -and $retained.'保留赛制'.games -eq 8 -and @($retained.PSObject.Properties).Count -eq 2) '本地多局保留已有评级、赛季、历史、定级资格及其他赛制。'

$failedProfileDirectory = Join-Path $runDirectory 'profile-failure/saves/profile.json'
[void][IO.Directory]::CreateDirectory($failedProfileDirectory)
$profileFailure = Invoke-CliCheck 'profile-failure' @('--ruleset', 'ranked', '--auto', '--seed', '54', '--report', 'match.md') -ExpectedExit 1
$failureGames = @(Replays $profileFailure.SaveDir)
$failureReport = [IO.File]::ReadAllText((Join-Path $profileFailure.Directory 'match.md'))
Assert-Check ($failureGames.Count -eq $games.Count -and $profileFailure.Out.Contains('赛制结束：')) '档案保存失败仍完成后续对局并分别保留回放。'
Assert-Check ([regex]::Matches($profileFailure.Err, '档案保存失败').Count -eq $games.Count) '每局档案保存失败均给出明确错误。'
Assert-Check ($failureReport -ceq $report) '档案保存失败不妨碍生成完整汇总战报。'
Assert-Check (Test-Path -LiteralPath $failedProfileDirectory -PathType Container) '存档失败不覆盖占据档案路径的目录。'
Assert-Check (@(LibraryFiles $profileFailure.SaveDir).Count -eq $games.Count) '档案失败时各局仍进入独立录像库。'

$overflowSaveDirectory = Join-Path $runDirectory 'profile-overflow/saves'
[void][IO.Directory]::CreateDirectory($overflowSaveDirectory)
$overflowProfilePath = Join-Path $overflowSaveDirectory 'profile.json'
$overflowFixture = '{"packs":{"original":{"wins":0,"losses":2147483647,"draws":0,"picks":{},"pickWins":{}}}}'
[IO.File]::WriteAllText($overflowProfilePath, $overflowFixture, $utf8)
$overflowBefore = [Convert]::ToBase64String([IO.File]::ReadAllBytes($overflowProfilePath))
$overflow = Invoke-CliCheck 'profile-overflow' @('--ruleset', 'ranked', '--seed', '54', '--report', 'overflow.md') `
    -ExpectedExit 1 -InputText "/surrender`n/surrender`n"
Assert-Check ($overflow.Out.Contains('赛制结束：共 2 局；你 0 : 2 电脑')) '合法战绩计数达到上限时仍完成两局投降结算。'
Assert-Check (@(Replays $overflow.SaveDir).Count -eq 2 -and @(LibraryFiles $overflow.SaveDir).Count -eq 2) '两局档案计数溢出仍分别保存两份回放和两份录像库条目。'
$overflowReport = [IO.File]::ReadAllText((Join-Path $overflow.Directory 'overflow.md'))
Assert-Check ($overflowReport.StartsWith('# 赛制战报') -and $overflowReport.Contains('比分：你 0 : 2 电脑') -and
    [regex]::Matches($overflowReport, '(?m)^## 第 \d+ 局$').Count -eq 2) '档案计数溢出后仍生成包含两局及最终比分的汇总战报。'
Assert-Check ([regex]::Matches($overflow.Err, '档案保存失败').Count -eq 2 -and
    $overflow.Err.Contains('错误：第 1 局档案保存失败：') -and $overflow.Err.Contains('错误：第 2 局档案保存失败：')) '每局计数溢出均给出明确档案保存错误并最终返回失败。'
Assert-Check ($overflowBefore -ceq [Convert]::ToBase64String([IO.File]::ReadAllBytes($overflowProfilePath))) '两次计数溢出均不改变原档案的任何字节。'

$manual = Invoke-CliCheck 'manual-timed-series' @('--ruleset', 'ranked', '--seed', '54', '--report', 'manual.md') -InputSteps @(
    @{ Expect = '请选择要禁用的对手号位（q 退出）> '; Input = "1`n" },
    @{ Expect = '请选择首发号位（q 退出）> '; Input = "1`n" },
    @{ Expect = '选择行动（q 退出）> '; Input = "/surrender`n"; DelayMs = 11000 },
    @{ Expect = '===== 赛制第 2 局'; Input = '' },
    @{ Expect = '请选择要禁用的对手号位（q 退出）> '; Input = "1`n" },
    @{ Expect = '请选择首发号位（q 退出）> '; Input = "1`n" },
    @{ Expect = '选择行动（q 退出）> '; Input = "/surrender`n" }
)
$manualGames = @(Replays $manual.SaveDir)
Assert-Check ($manualGames.Count -eq 2 -and (ProfileTotal $manual.SaveDir) -eq 2 -and
    $manual.Out.Contains('赛制结束：共 2 局；你 0 : 2 电脑')) '手动两局依次禁用、首发、投降并按三局两胜结算。'
$secondGame = $manual.Out.IndexOf('===== 赛制第 2 局', [StringComparison]::Ordinal)
Assert-Check ($manual.Out.Substring(0, $secondGame).Contains('本方基本 0 秒 / 总时间 29 秒') -and
    $manual.Out.Substring($secondGame).Contains('本方基本 10 秒 / 总时间 30 秒')) '第一局真实等待消耗总时间，第二局恢复完整计时预算。'
foreach ($game in $manualGames) {
    Assert-Check (@($game.Record.events | Where-Object { $_.type -eq 'surrender' }).Count -eq 1) '每局投降通过终止事件保存，可独立回放。'
    $view = Invoke-CliCheck "manual-replay-$($game.Record.seed)" @('--replay', $game.Path, '--auto')
    Assert-Check ($view.Out.Contains('回看结束：')) '手动赛制投降录像可以独立回看。'
}

$partial = Invoke-CliCheck 'partial-series' @('--ruleset', 'ranked', '--report', 'partial.md') -InputText "/surrender`nq`n"
Assert-Check ((ProfileTotal $partial.SaveDir) -eq 1 -and @(Replays $partial.SaveDir).Count -eq 1 -and
    @(LibraryFiles $partial.SaveDir).Count -eq 1 -and -not (Test-Path -LiteralPath (Join-Path $partial.Directory 'partial.md')) -and
    $partial.Out.Contains('赛制未完成；已完成 1 局')) '跨局 q 退出只保留第一局，不保存未完成局或汇总报告。'

$chaos = Invoke-CliCheck 'chaos' @('--ruleset', 'chaos', '--auto', '--seed', '54')
$chaosGames = @(Replays $chaos.SaveDir)
Assert-Check ($chaosGames.Count -eq 1 -and (ProfileTotal $chaos.SaveDir) -eq 1) '乱斗赛制只打一局。'
Assert-Check (($chaosGames[0].Record.modifiers -join ',') -ceq 'half_pp,hype_start') '乱斗回放携带两项赛制修饰器。'

$mixed = Invoke-CliCheck 'mixed-random' @('--ruleset', 'casual', '--packs', 'original,fan_asoul_vr', '--random-teams', '--export-team', '--auto', '--seed', '54')
$mixedGames = @(Replays $mixed.SaveDir)
$mixedRecord = $mixedGames[0].Record
Assert-Check ($mixedGames.Count -eq 1 -and @(@($mixedRecord.teams[0]) + @($mixedRecord.teams[1]) | Select-Object -Unique).Count -eq 12 -and
    $mixed.Out.Contains('阵容来源声明：')) '赛制混合包随机阵容保留十二人不重复及来源声明。'
$codeMatch = [regex]::Match($mixed.Out, '(?m)^本方队伍码：(SDT1-[A-Za-z0-9_-]+)\r?$')
Assert-Check ($codeMatch.Success) '赛制支持导出原始六人队伍码。'
$mixedImported = Invoke-CliCheck 'mixed-imported' @('--ruleset', 'casual', '--team-code', $codeMatch.Groups[1].Value,
    '--team-b', ($mixedRecord.teams[1] -join ','), '--auto', '--seed', '54')
$importedGames = @(Replays $mixedImported.SaveDir)
Assert-Check ($importedGames.Count -eq 1 -and $importedGames[0].Json -ceq $mixedGames[0].Json) '导入混合队伍码与显式电脑队伍保留双方原阵容和可复现行动。'

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
    @{ Name = 'timer'; Args = @('--ruleset', 'ranked', '--timer', '0,0') },
    @{ Name = 'host'; Args = @('--ruleset', 'ranked', '--host', '0') },
    @{ Name = 'join'; Args = @('--ruleset', 'ranked', '--join', 'localhost:1') },
    @{ Name = 'replay'; Args = @('--ruleset', 'ranked', '--replay', 'missing.json') },
    @{ Name = 'replay-code'; Args = @('--ruleset', 'ranked', '--replay-code', 'SD1-invalid') },
    @{ Name = 'replays'; Args = @('--ruleset', 'ranked', '--replays') },
    @{ Name = 'watch'; Args = @('--ruleset', 'ranked', '--watch', 'localhost:1') },
    @{ Name = 'lan-host'; Args = @('--ruleset', 'ranked', '--lan-host', 'room') },
    @{ Name = 'hotseat-timer'; Args = @('--ruleset', 'ranked', '--hotseat', '--timer', '10,30') },
    @{ Name = 'new-season'; Args = @('--ruleset', 'ranked', '--new-season') },
    @{ Name = 'rating-history'; Args = @('--ruleset', 'ranked', '--rating-history') },
    @{ Name = 'tournament'; Args = @('--ruleset', 'ranked', '--tournament', 'event.json') },
    @{ Name = 'tournament-report'; Args = @('--ruleset', 'ranked', '--tournament-report', 'event.json') },
    @{ Name = 'branch'; Args = @('--ruleset', 'ranked', '--branch', 'missing.json', '--turn', '1', '--side', '0') },
    @{ Name = 'glossary'; Args = @('--ruleset', 'ranked', '--glossary') },
    @{ Name = 'cards'; Args = @('--ruleset', 'ranked', '--cards', 'original') },
    @{ Name = 'advice-without-phase'; Args = @('--ruleset', 'casual', '--advice') },
    @{ Name = 'report-reserved'; Args = @('--ruleset', 'ranked', '--auto', '--report', 'saves/profile.json') }
)
foreach ($case in $invalidCases) {
    $result = Invoke-CliCheck $case.Name $case.Args -ExpectedExit 1
    Assert-Check ($result.Err.Contains('错误：') -and -not (Test-Path -LiteralPath $result.SaveDir)) "$($case.Name) 在开局和保存前明确拒绝。"
}

[IO.File]::WriteAllText((Join-Path $runDirectory 'checks.json'), (ConvertTo-Json -InputObject $checks.ToArray()), $utf8)
Write-Output "赛制 CLI 检查通过：$($checks.Count) 项；证据目录：$runDirectory"
