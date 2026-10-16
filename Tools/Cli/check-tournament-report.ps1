#requires -Version 7.0
param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug')

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$binary = Join-Path $PSScriptRoot "bin/$Configuration/net8.0"
$cli = Join-Path $binary 'Cli.dll'
$core = Join-Path $binary 'StreamDuel.Core.dll'
foreach ($path in @($cli, $core)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "请先构建 Tools/Cli -c $Configuration。" }
}
$run = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ('obj/tournament-report-verification/' + [Guid]::NewGuid().ToString('N'))))
if (-not $run.StartsWith($repository + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw '验收目录必须位于当前工作树内。'
}
[void][IO.Directory]::CreateDirectory($run)
$utf8 = [Text.UTF8Encoding]::new($false, $true)
$checks = [Collections.Generic.List[string]]::new()
$calls = [Collections.Generic.List[object]]::new()
$failure = $null

function Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "$Message 证据目录：$run" }
}

function Invoke-Cli([string]$Name, [string[]]$Parameters, [string[]]$Lines = @(), [int]$Exit = 0, [string]$Executable = $cli) {
    $work = Join-Path $run $Name
    [void][IO.Directory]::CreateDirectory($work)
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = 'dotnet'
    $start.ArgumentList.Add($Executable)
    foreach ($argument in $Parameters) { $start.ArgumentList.Add($argument) }
    $start.WorkingDirectory = $work
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardInputEncoding = $utf8
    $start.StandardOutputEncoding = $utf8
    $start.StandardErrorEncoding = $utf8
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    $started = $false
    try {
        $started = $process.Start()
        Check $started "$Name 无法启动。"
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if ($Lines.Count -gt 0) { $process.StandardInput.Write(($Lines -join "`n") + "`n") }
        $process.StandardInput.Close()
        Check ($process.WaitForExit(60000)) "$Name 超时。"
        Check ($stdout.Wait(5000) -and $stderr.Wait(5000)) "$Name 输出未结束。"
        $result = [pscustomobject]@{ Out = $stdout.Result; Err = $stderr.Result; Exit = $process.ExitCode }
        [IO.File]::WriteAllText((Join-Path $work 'stdout.log'), $result.Out, $utf8)
        [IO.File]::WriteAllText((Join-Path $work 'stderr.log'), $result.Err, $utf8)
        $calls.Add([ordered]@{ name = $Name; exit = $result.Exit; expected = $Exit })
        Check ($result.Exit -eq $Exit) "$Name 退出码应为 $Exit，实际 $($result.Exit)。"
        Check (-not (Test-Path -LiteralPath (Join-Path $work 'saves'))) "$Name 产生了普通玩家存档。"
        if ($Exit -eq 0) { Check ([string]::IsNullOrWhiteSpace($result.Err)) "$Name 成功时出现标准错误。" }
        else { Check ($result.Err -match '错误：|警告：') "$Name 未给出中文错误。" }
        return $result
    }
    finally {
        if ($started -and -not $process.HasExited) { $process.Kill($true); [void]$process.WaitForExit(5000) }
        $process.Dispose()
    }
}

function Fingerprint([string]$TournamentPath) {
    $files = [Collections.Generic.List[string]]::new()
    if (Test-Path -LiteralPath $TournamentPath -PathType Leaf) { $files.Add($TournamentPath) }
    foreach ($item in Get-ChildItem -LiteralPath ([IO.Path]::GetDirectoryName($TournamentPath)) -File -Force) {
        if ($item.FullName.StartsWith($TournamentPath + '.tournament.', [StringComparison]::OrdinalIgnoreCase)) { $files.Add($item.FullName) }
    }
    $library = $TournamentPath + '.replay-library'
    if (Test-Path -LiteralPath $library -PathType Container) {
        foreach ($item in Get-ChildItem -LiteralPath $library -File -Recurse -Force) { $files.Add($item.FullName) }
    }
    elseif (Test-Path -LiteralPath $library -PathType Leaf) { $files.Add($library) }
    return (($files | Sort-Object -Unique | ForEach-Object {
        $item = Get-Item -LiteralPath $_ -Force
        $_ + ':' + $item.LastWriteTimeUtc.Ticks + ':' + (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash
    }) -join "`n")
}

function Report([string]$Name, [string]$Path, [switch]$Json, [string[]]$Extra = @()) {
    $before = Fingerprint $Path
    $arguments = @('--tournament-report', $Path) + $Extra
    if ($Json) { $arguments += '--json' }
    $result = Invoke-Cli $Name $arguments
    Check ((Fingerprint $Path) -ceq $before) "$Name 改写了赛事、锁、库文件或文件时间。"
    Check (-not $result.Out.Contains($repository)) "$Name 报告泄露了绝对文件路径。"
    return $result.Out
}

function New-Code([string]$PackId, [string[]]$Ids) {
    $preset = [StreamDuel.Core.TeamPreset]::new()
    $preset.PackId = $PackId
    $preset.Name = '赛事验收队伍'
    foreach ($id in $Ids) { $preset.Ids.Add($id) }
    return [StreamDuel.Core.TeamCode]::Encode($preset)
}

function Read-Tournament([string]$Path) {
    return [StreamDuel.Core.Tournament]::FromJson($db, [IO.File]::ReadAllText($Path, $utf8))
}

function Matches($Json) { return ,@($Json.rounds | ForEach-Object { $_.matches }) }

function Assert-Ranking($Json, $Tournament) {
    $expected = @($Tournament.Standings())
    Check ($Json.standings.Count -eq $expected.Count) '报告积分榜人数不符。'
    for ($i = 0; $i -lt $expected.Count; $i++) {
        foreach ($property in @('Player', 'Rank', 'Points', 'OpponentPoints', 'HeadToHeadPoints', 'Wins', 'Draws', 'Losses', 'Byes')) {
            $key = [char]::ToLowerInvariant($property[0]) + $property.Substring(1)
            Check ($Json.standings[$i].Contains($key) -and $Json.standings[$i][$key] -eq $expected[$i].$property) "积分榜 $property 与 Tournament 不符。"
        }
        $name = [StreamDuel.Core.TextInput]::Clean($expected[$i].Name, [StreamDuel.Core.TextInputKind]::PlayerName).Text
        Check ($Json.standings[$i].name -ceq $name) '积分榜昵称没有使用公开文字过滤。'
    }
}

function Assert-Highlights($Json, [string]$Path) {
    $before = Fingerprint $Path
    $library = [StreamDuel.Core.ReplayLibrary]::OpenReadOnly($Path + '.replay-library', $policy)
    $largest = 0
    $kos = @{}
    $appearances = @{}
    foreach ($match in (Matches $Json)) {
        if ($match.replay.status -ne 'saved') { continue }
        $record = $library.Load($match.replay.id)
        $battle = [StreamDuel.Core.Replay]::Run($db, $record)
        Check ($battle.Winner -eq $match.winnerSide) '归档录像胜方与赛事对阵不一致。'
        $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($event in $battle.Events) {
            if ($event.Kind -eq [StreamDuel.Core.BattleEventKind]::Damage) { $largest = [Math]::Max($largest, $event.Amount) }
            if ($event.Kind -eq [StreamDuel.Core.BattleEventKind]::Enter -and $seen.Add("$($event.Side):$($event.Id)")) {
                if (-not $appearances.ContainsKey($event.Id)) { $appearances[$event.Id] = 0 }
                $appearances[$event.Id]++
            }
        }
        $summary = [StreamDuel.Core.BattleSummary]::From($battle)
        foreach ($side in $summary.Sides) {
            foreach ($fighter in $side) {
                if (-not $kos.ContainsKey($fighter.StreamerId)) { $kos[$fighter.StreamerId] = 0 }
                $kos[$fighter.StreamerId] += $fighter.Kos
            }
        }
    }
    Check ($Json.maxDamage.amount -eq $largest) '全场最高单次伤害与真实录像事件不符。'
    Check ($kos[$Json.mostKos.streamerId] -eq $Json.mostKos.count -and
        $Json.mostKos.count -eq ($kos.Values | Measure-Object -Maximum).Maximum) '最多击倒与录像 Summary 独立求和不符。'
    Check ($appearances[$Json.mostAppearances.streamerId] -eq $Json.mostAppearances.count -and
        $Json.mostAppearances.count -eq ($appearances.Values | Measure-Object -Maximum).Maximum) '最多出场与录像 Enter 独立计数不符。'
    Check ((Fingerprint $Path) -ceq $before) '只读加载录像进行独立复算改动了原文件。'
}

try {
    $assembly = [Reflection.Assembly]::LoadFrom($core)
    Check ([StringComparer]::OrdinalIgnoreCase.Equals([IO.Path]::GetFullPath($assembly.Location), [IO.Path]::GetFullPath($core))) '请在未加载其他 Core 的新 PowerShell 进程运行。'
    $content = Join-Path $binary 'Content'
    $rules = [IO.File]::ReadAllText((Join-Path $content 'StreamDuelRules.json'), $utf8)
    $pack = [IO.File]::ReadAllText((Join-Path $content 'Packs/original.json'), $utf8)
    $policy = [StreamDuel.Core.PackPolicy]::Load((Join-Path $content 'PackPolicy.json'))
    $db = [StreamDuel.Core.ContentDb]::FromJson($rules, $pack, $policy)
    $codeA = New-Code $db.PackId @('bell', 'enka', 'shiori', 'lumi', 'hane', 'momo')
    $codeB = New-Code $db.PackId @('kei', 'yuan', 'nyako', 'buck', 'kasumi', 'bee')

    $missing = Join-Path $run 'never-created/missing.json'
    $conflicts = @(@('--auto'), @('--tournament', 'other.json'), @('--json', '--json'),
        @('--tournament-report', 'other.json'), @('--host', '0'), @('--hotseat'), @('--replays'),
        @('--replay', 'missing.json'), @('--glossary'), @('--list-packs'), @('--save-dir', 'other'),
        @('--timer', '1,2'), @('--random-teams'), @('--packs', 'original,fan_asoul_vr'))
    for ($i = 0; $i -lt $conflicts.Count; $i++) {
        $result = Invoke-Cli "invalid-$i" (@('--tournament-report', $missing) + $conflicts[$i]) @() 1
        Check ($result.Out.Length -eq 0) '无效参数在拒绝前输出了报告或开始对战。'
    }
    $null = Invoke-Cli 'json-alone' @('--json') @() 1
    $null = Invoke-Cli 'missing-argument' @('--tournament-report') @() 1
    $null = Invoke-Cli 'empty-argument' @('--tournament-report', '') @() 1
    $null = Invoke-Cli 'missing-file' @('--tournament-report', $missing) @() 1
    Check (-not (Test-Path -LiteralPath (Split-Path $missing -Parent))) '拒绝参数或缺失文件时创建了目录。'
    $checks.Add('PASS argument_isolation_and_missing_file_no_write')

    $rrPath = Join-Path $run 'roundrobin.json'
    $rawName = "甲$([char]0x200b)队"
    $rrLines = @('1', $rawName, $codeA, '乙队', $codeB, '丙队', $codeA, '丁队', $codeB, '')
    $null = Invoke-Cli 'rr-auto' @('--tournament', $rrPath, '--auto') $rrLines
    $rr = Read-Tournament $rrPath
    Check ($rr.IsFinished -and $rr.Round -eq 3) '四人循环赛没有完成。'
    $textA = Report 'rr-text-first' $rrPath
    $textB = Report 'rr-text-second' $rrPath
    $jsonA = Report 'rr-json-first' $rrPath -Json
    $jsonB = Report 'rr-json-second' $rrPath -Json
    Check ($textA -ceq $textB -and $jsonA -ceq $jsonB) '同一赛事重复导出不是逐字节稳定。'
    Check (-not $textA.Contains($rawName) -and -not $jsonA.Contains($rawName)) '报告保留了未经清洗的玩家名。'
    $rrReport = $jsonA | ConvertFrom-Json -AsHashtable
    Assert-Ranking $rrReport $rr
    Check ($rrReport.coverage.completedGames -eq 6 -and $rrReport.coverage.archivedGames -eq 6 -and
        $rrReport.coverage.missingGames -eq 0 -and $rrReport.coverage.invalidGames -eq 0) '循环赛六局未全部归档。'
    Check ((Matches $rrReport).Count -eq 6 -and @((Matches $rrReport) | Where-Object { $_.replay.status -ne 'saved' }).Count -eq 0) '报告遗漏对阵或录像链接。'
    Assert-Highlights $rrReport $rrPath
    $rrBefore = Fingerprint $rrPath
    $null = Invoke-Cli 'rr-already-over' @('--tournament', $rrPath, '--auto')
    Check ((Fingerprint $rrPath) -ceq $rrBefore) '已完成赛事重新打开产生了重复归档或改写。'
    $checks.Add('PASS_rr_complete_report_determinism_ranking_and_independent_highlights')

    $swissPath = Join-Path $run 'swiss.json'
    $swissLines = [Collections.Generic.List[string]]::new()
    $swissLines.Add('2'); $swissLines.Add('3')
    for ($i = 0; $i -lt 6; $i++) {
        $swissLines.Add("选手$i"); $swissLines.Add($(if ($i % 2 -eq 0) { $codeA } else { $codeB }))
    }
    $swissLines.Add('')
    $null = Invoke-Cli 'swiss-auto' @('--tournament', $swissPath, '--auto') $swissLines.ToArray()
    $swiss = Read-Tournament $swissPath
    $swissJson = Report 'swiss-json' $swissPath -Json | ConvertFrom-Json -AsHashtable
    Check ($swiss.IsFinished -and $swissJson.roundCount -eq 3 -and $swissJson.coverage.archivedGames -eq 9) '六人三轮瑞士轮报告没有覆盖九局。'
    Assert-Ranking $swissJson $swiss
    Assert-Highlights $swissJson $swissPath
    $checks.Add('PASS_swiss_nine_games_and_independent_highlights')

    $partialPath = Join-Path $run 'missing-one.json'
    [IO.File]::Copy($rrPath, $partialPath)
    $partialLibrary = $partialPath + '.replay-library'
    [void][IO.Directory]::CreateDirectory($partialLibrary)
    $removedId = (Matches $rrReport)[0].replay.id
    foreach ($file in Get-ChildItem -LiteralPath ($rrPath + '.replay-library') -File -Force) {
        if ($file.Name -ne ($removedId + '.json')) { [IO.File]::Copy($file.FullName, (Join-Path $partialLibrary $file.Name)) }
    }
    $partial = Report 'missing-one-report' $partialPath -Json | ConvertFrom-Json -AsHashtable
    Check ($partial.coverage.archivedGames -eq 5 -and $partial.coverage.missingGames -eq 1 -and
        @((Matches $partial) | Where-Object { $_.replay.status -eq 'not_saved' }).Count -eq 1) '缺失一局录像没有明确标注未保存，或错误信任旧索引。'
    $checks.Add('PASS_missing_replay_is_not_saved_without_index_repair')

    $manualPath = Join-Path $run 'manual.json'
    $null = Invoke-Cli 'manual-result' @('--tournament', $manualPath) @('1', '甲', $codeA, '乙', $codeB, '', '-1')
    $manual = Report 'manual-report' $manualPath -Json | ConvertFrom-Json -AsHashtable
    Check ($manual.coverage.completedGames -eq 1 -and $manual.coverage.missingGames -eq 1 -and
        $null -eq $manual.maxDamage -and $null -eq $manual.mostKos -and $null -eq $manual.mostAppearances) '手工结果被编造成有录像的统计。'
    Check (-not (Test-Path -LiteralPath ($manualPath + '.replay-library'))) '缺库查询创建了录像目录。'
    $pendingPath = Join-Path $run 'pending.json'
    $null = Invoke-Cli 'pending-create' @('--tournament', $pendingPath) @('1', '甲', $codeA, '乙', $codeB, '', 'q')
    $pending = Report 'pending-report' $pendingPath -Json | ConvertFrom-Json -AsHashtable
    Check (-not $pending.isFinished -and (Matches $pending)[0].replay.status -eq 'pending') '未报结果未显示待赛，或报告推进了赛事。'
    $byePath = Join-Path $run 'bye.json'
    $null = Invoke-Cli 'bye-create' @('--tournament', $byePath) @('1', '甲', $codeA, '乙', $codeB, '丙', $codeA, '', '-1', '-1', '-1')
    $bye = Report 'bye-report' $byePath -Json | ConvertFrom-Json -AsHashtable
    Check (@((Matches $bye) | Where-Object { $_.replay.status -eq 'not_applicable' }).Count -eq 3 -and
        $bye.coverage.completedGames -eq 3) '轮空被算作缺失录像或实际比赛。'
    $checks.Add('PASS_manual_pending_bye_and_absent_library')

    $failedPath = Join-Path $run 'archive-failure.json'
    [IO.File]::WriteAllText(($failedPath + '.replay-library'), '故意占用旁库路径', $utf8)
    $failed = Invoke-Cli 'archive-failure-auto' @('--tournament', $failedPath, '--auto') @('1', '甲', $codeA, '乙', $codeB, '') 1
    Check ((Read-Tournament $failedPath).IsFinished -and $failed.Err.Contains('比分仍会保存')) '归档失败导致完整比分丢失，或未返回明确警告。'
    Check ([IO.File]::ReadAllText(($failedPath + '.replay-library'), $utf8) -ceq '故意占用旁库路径') '归档失败覆盖了已有文件。'
    $badPath = Join-Path $run 'broken.json'
    [IO.File]::WriteAllText($badPath, '{bad', $utf8)
    $badBefore = Fingerprint $badPath
    $null = Invoke-Cli 'broken-report' @('--tournament-report', $badPath, '--json') @() 1
    Check ((Fingerprint $badPath) -ceq $badBefore) '损坏赛事报告入口改写了来源。'
    $checks.Add('PASS_archive_failure_keeps_score_and_corrupt_input_unchanged')

    $external = Join-Path $run 'external-fixture'
    [void][IO.Directory]::CreateDirectory($external)
    $externalPack = $pack | ConvertFrom-Json -AsHashtable
    $externalPack.id = 't110_cli_fixture'
    [IO.File]::WriteAllText((Join-Path $external 'fixture.json'), ($externalPack | ConvertTo-Json -Depth 64), $utf8)
    $externalCodeA = New-Code 't110_cli_fixture' @('bell', 'enka', 'shiori', 'lumi', 'hane', 'momo')
    $externalCodeB = New-Code 't110_cli_fixture' @('kei', 'yuan', 'nyako', 'buck', 'kasumi', 'bee')
    $externalPath = Join-Path $run 'external-event.json'
    $null = Invoke-Cli 'external-create' @('--tournament', $externalPath, '--pack', 't110_cli_fixture', '--pack-dir', $external) @('1', '甲', $externalCodeA, '乙', $externalCodeB, '', '-1')
    $externalReport = Report 'external-report-other-cwd' $externalPath -Json -Extra @('--pack-dir', $external) | ConvertFrom-Json -AsHashtable
    Check ($externalReport.packId -ceq 't110_cli_fixture' -and $externalReport.coverage.completedGames -eq 1) '报告未从赛事自动读取外部包 id。'

    $deployment = Join-Path $run 'isolated-deployment'
    $sourceFiles = @(Get-ChildItem -LiteralPath $binary -Recurse -Force)
    Check (@($sourceFiles | Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 }).Count -eq 0) '不能复制含重解析点的发布目录。'
    Copy-Item -LiteralPath $binary -Destination $deployment -Recurse
    $sourcePolicyPath = Join-Path $content 'PackPolicy.json'
    $sourcePolicyHash = (Get-FileHash -LiteralPath $sourcePolicyPath -Algorithm SHA256).Hash
    [IO.File]::WriteAllText((Join-Path $deployment 'Content/PackPolicy.json'), '{"disabledPacks":["original"],"disabledStreamers":[],"reason":{}}', $utf8)
    $policyBefore = Fingerprint $rrPath
    $blocked = Invoke-Cli 'policy-disabled-report' @('--tournament-report', $rrPath, '--json') @() 1 (Join-Path $deployment 'Cli.dll')
    Check ($blocked.Out.Length -eq 0 -and $blocked.Err.Contains('该内容包已下架')) '报告绕过下架政策或在拒绝前泄露部分报告。'
    Check ((Fingerprint $rrPath) -ceq $policyBefore -and
        (Get-FileHash -LiteralPath $sourcePolicyPath -Algorithm SHA256).Hash -ceq $sourcePolicyHash) '政策拒绝改动了赛事、录像或真实发布配置。'
    $checks.Add('PASS_external_pack_and_isolated_policy_gate')
}
catch { $failure = $_.ToString() }
finally {
    $result = [ordered]@{ status = $(if ($null -eq $failure) { 'PASS' } else { 'FAIL' }); checks = @($checks.ToArray()); calls = @($calls.ToArray()); error = $failure }
    [IO.File]::WriteAllText((Join-Path $run 'report.json'), ($result | ConvertTo-Json -Depth 10), $utf8)
}
foreach ($check in $checks) { Write-Host $check }
Write-Host "证据目录：$run"
if ($null -ne $failure) { throw $failure }
