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
$run = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ('obj/elimination-verification/' + [Guid]::NewGuid().ToString('N'))))
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

function Invoke-Cli([string]$Name, [string[]]$Parameters, [string[]]$Lines = @(), [int]$Exit = 0) {
    $work = Join-Path $run $Name
    [void][IO.Directory]::CreateDirectory($work)
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = 'dotnet'
    $start.ArgumentList.Add($cli)
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

function Fingerprint([string]$Path) {
    $files = [Collections.Generic.List[string]]::new()
    if (Test-Path -LiteralPath $Path -PathType Leaf) { $files.Add($Path) }
    foreach ($item in Get-ChildItem -LiteralPath ([IO.Path]::GetDirectoryName($Path)) -File -Force) {
        if ($item.FullName.StartsWith($Path + '.tournament.', [StringComparison]::OrdinalIgnoreCase)) { $files.Add($item.FullName) }
    }
    if (Test-Path -LiteralPath ($Path + '.replay-library') -PathType Container) {
        foreach ($item in Get-ChildItem -LiteralPath ($Path + '.replay-library') -File -Recurse -Force) { $files.Add($item.FullName) }
    }
    return (($files | Sort-Object -Unique | ForEach-Object {
        $item = Get-Item -LiteralPath $_ -Force
        $_ + ':' + $item.LastWriteTimeUtc.Ticks + ':' + (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash
    }) -join "`n")
}

function New-Code([string]$PackId, [string[]]$Ids) {
    $preset = [StreamDuel.Core.TeamPreset]::new()
    $preset.PackId = $PackId
    $preset.Name = '淘汰赛验收队伍'
    foreach ($id in $Ids) { $preset.Ids.Add($id) }
    return [StreamDuel.Core.TeamCode]::Encode($preset)
}

function Registration([int]$Count, [string]$FirstCode, [string]$SecondCode) {
    $lines = [Collections.Generic.List[string]]::new()
    for ($i = 0; $i -lt $Count; $i++) {
        $lines.Add("选手$($i + 1)")
        $lines.Add($(if ($i % 2 -eq 0) { $FirstCode } else { $SecondCode }))
    }
    $lines.Add('')
    return ,$lines.ToArray()
}

function Read-Tournament([string]$Path, $Content = $db) {
    return [StreamDuel.Core.Tournament]::FromJson($Content, [IO.File]::ReadAllText($Path, $utf8))
}

function Report([string]$Name, [string]$Path, [string[]]$Extra = @()) {
    $before = Fingerprint $Path
    $result = Invoke-Cli $Name (@('--tournament-report', $Path, '--json') + $Extra)
    Check ((Fingerprint $Path) -ceq $before) "$Name 改写了赛事或录像库文件。"
    return ($result.Out | ConvertFrom-Json -AsHashtable)
}

function Matches($Json) { return ,@($Json.rounds | ForEach-Object { $_.matches }) }

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

    $missing = Join-Path $run 'not-created/event.json'
    $invalid = @(
        @('--format'), @('--format', ''), @('--format', 'roundrobin'), @('--format', 'Single'),
        @('--format', 'single'), @('--tournament', $missing, '--format', 'unknown'),
        @('--tournament', $missing, '--format', 'single', '--format', 'double'),
        @('--tournament', $missing, '--format', 'single', '--seed', '5'),
        @('--tournament-report', $missing, '--format', 'single'),
        @('--tournament', $missing, '--format', 'double', '--replays'),
        @('--tournament', $missing, '--format', 'single', '--json'),
        @('--watch', '127.0.0.1:1', '--format', 'single'),
        @('--format', 'single', '--watch', '127.0.0.1:1'),
        @('--watch', '127.0.0.1:1', '--tournament', $missing, '--format', 'single'),
        @('--tournament', $missing, '--format', 'single', '--watch', '127.0.0.1:1'),
        @('--host', '0', '--spectate-port', '0', '--tournament', $missing, '--format', 'double'),
        @('--tournament', $missing, '--format', 'double', '--host', '0', '--spectate-port', '0'),
        @('--lan-host', '淘汰互斥验收', '--spectate-port', '0', '--tournament', $missing, '--format', 'double'),
        @('--tournament', $missing, '--format', 'double', '--lan-host', '淘汰互斥验收', '--spectate-port', '0')
    )
    for ($i = 0; $i -lt $invalid.Count; $i++) {
        $result = Invoke-Cli "invalid-$i" $invalid[$i] @() 1
        Check ($result.Out.Length -eq 0) '无效参数在拒绝前进入了赛事流程。'
    }
    Check (-not (Test-Path -LiteralPath (Split-Path $missing -Parent))) '无效参数创建了赛事目录。'
    $checks.Add('PASS format_argument_isolation_no_write')

    $singlePath = Join-Path $run 'single-pending.json'
    $null = Invoke-Cli 'single-create' @('--tournament', $singlePath, '--format', 'single') ((Registration 4 $codeA $codeB) + @('q'))
    $single = Read-Tournament $singlePath
    Check ($single.Format -eq 'single' -and $single.Round -eq 1 -and $single.Players.Count -eq 4) '--format single 没有直接创建四人单败赛事。'
    $before = Fingerprint $singlePath
    $mismatch = Invoke-Cli 'resume-mismatched-format' @('--tournament', $singlePath, '--format', 'double', '--auto') @() 1
    Check ($mismatch.Err.Contains('赛制不一致') -and (Fingerprint $singlePath) -ceq $before) '续赛事制冲突没有在写入前拒绝。'
    $bytesBefore = (Get-FileHash -LiteralPath $singlePath -Algorithm SHA256).Hash
    $draw = Invoke-Cli 'manual-draw-rejected' @('--tournament', $singlePath, '--format', 'single') @('-1', '平局', 'q')
    Check ($draw.Out.Contains('不能登记平局') -and (Get-FileHash -LiteralPath $singlePath -Algorithm SHA256).Hash -ceq $bytesBefore) '手工平局改变了待赛数据或没有明确拒绝。'
    $null = Invoke-Cli 'resume-manual-result' @('--tournament', $singlePath, '--format', 'single') @('0', 'q')
    $single = Read-Tournament $singlePath
    $pending = @($single.NextRound())
    Check ($pending[0].WinnerSide -eq 0 -and $null -eq $pending[1].WinnerSide) '续赛未保留已报比分和未报对阵。'
    $done = Invoke-Cli 'single-finish-manual' @('--tournament', $singlePath) @('1', '0')
    $single = Read-Tournament $singlePath
    Check ($single.IsFinished -and $null -ne $single.Champion -and $done.Out.Contains('冠军：') -and
        $done.Out.Contains('按晋级与淘汰进度') -and -not $done.Out.Contains('积分 →')) '单败结束或排名展示不正确。'
    $checks.Add('PASS single_manual_draw_rejection_and_resume')

    $autoPath = Join-Path $run 'single-auto-five.json'
    $auto = Invoke-Cli 'single-five-auto' @('--tournament', $autoPath, '--format', 'single', '--auto') (Registration 5 $codeA $codeB)
    $automatic = Read-Tournament $autoPath
    $autoReport = Report 'single-five-report' $autoPath
    $allMatches = Matches $autoReport
    $games = @($allMatches | Where-Object { $null -ne $_.player1 })
    $byes = @($allMatches | Where-Object { $null -eq $_.player1 })
    Check ($automatic.IsFinished -and $automatic.Round -eq 3 -and $games.Count -eq 4 -and $byes.Count -eq 3) '五人单败局数或首轮补足轮空不正确。'
    Check (@($byes | Where-Object { $_.player0 -notin @(0, 1, 2) }).Count -eq 0) 'CLI 默认种子没有优先让前三报名者轮空。'
    Check ($autoReport.format -eq 'single' -and $autoReport.coverage.archivedGames -eq 4 -and
        $autoReport.coverage.missingGames -eq 0 -and $autoReport.coverage.invalidGames -eq 0) '自动单败决胜局没有完整归档。'
    Check ($autoReport.bestOf -eq 1 -and $autoReport.champion -eq $automatic.Champion -and
        @($allMatches | Where-Object { $_.bracket -ne 'single' -or $_.bracketRound -lt 1 }).Count -eq 0) '单败报告缺少正确的局制、冠军或分组轮次。'
    Check ($auto.Out.Contains('单败淘汰组') -and $auto.Out.Contains('轮空，直接晋级')) '自动单败未显示分组或轮空晋级。'
    $library = [StreamDuel.Core.ReplayLibrary]::OpenReadOnly($autoPath + '.replay-library', $policy)
    foreach ($game in $games) {
        Check ($game.replay.status -eq 'saved') '决胜局报告未关联保存的录像。'
        $replayed = [StreamDuel.Core.Replay]::Run($db, $library.Load($game.replay.id))
        Check ($replayed.Winner -ne -1 -and $replayed.Winner -eq $game.winnerSide) '真实录像结果与淘汰赛晋级结果不一致。'
    }
    $checks.Add('PASS five_player_single_auto_byes_and_real_archives')

    $doublePath = Join-Path $run 'double-reset.json'
    $created = Invoke-Cli 'double-create' @('--tournament', $doublePath, '--format', 'double') ((Registration 2 $codeA $codeB) + @('q'))
    $double = Read-Tournament $doublePath
    Check ($double.ResetFinal -and $created.Out.Contains('总决赛重置：开启')) 'CLI 新建双败没有默认开启重置。'
    $brackets = [Collections.Generic.List[string]]::new()
    $lastOutput = ''
    for ($step = 0; -not $double.IsFinished -and $step -lt 8; $step++) {
        $match = @($double.NextRound() | Where-Object { -not $_.IsBye -and $null -eq $_.WinnerSide })[0]
        $brackets.Add($match.Bracket)
        $winner = 0
        if ($match.Bracket -eq 'final') {
            $first = @($double.Standings() | Where-Object { $_.Player -eq $match.Player0 })[0]
            $winner = $(if ($first.Losses -eq 1) { 0 } else { 1 })
        }
        $advanced = Invoke-Cli "double-step-$step" @('--tournament', $doublePath, '--format', 'double') @([string]$winner, 'q')
        $lastOutput = $advanced.Out
        $double = Read-Tournament $doublePath
    }
    Check ($double.IsFinished -and ($brackets -join ',') -ceq 'winners,final,reset') '双败总决赛挑战者获胜后没有正确进入重置赛。'
    $ranking = @($double.Standings())
    Check ($ranking[0].Player -eq $double.Champion -and $ranking[0].Losses -eq 1 -and $ranking[1].Losses -eq 2) '重置决赛没有按第二败淘汰并确定冠军。'
    Check ($lastOutput.Contains('重置总决赛') -and $lastOutput.Contains('冠军：')) 'CLI 未显示重置总决赛和冠军。'
    $doubleReport = Report 'double-reset-report' $doublePath
    Check ($doubleReport.format -eq 'double' -and $doubleReport.coverage.completedGames -eq 3 -and
        $doubleReport.coverage.missingGames -eq 3) '手工双败战报未保留三场比分或误称存在录像。'
    Check ($doubleReport.resetFinal -and $doubleReport.bestOf -eq 1 -and $doubleReport.champion -eq $double.Champion -and
        (((Matches $doubleReport) | ForEach-Object { $_.bracket }) -join ',') -ceq 'winners,final,reset') '双败报告没有保留重置规则、冠军或赛程分组。'
    $checks.Add('PASS double_final_reset_resume_and_report')

    # 只增加测试内容包：双方无伤害且 PP 足够，真实对战到回合上限后必为平局。
    $external = Join-Path $run 'draw-fixture'
    [void][IO.Directory]::CreateDirectory($external)
    $skills = [ordered]@{}
    $skillIds = @(1..5 | ForEach-Object { "t115_wait_$_" })
    foreach ($id in $skillIds) { $skills[$id] = @{ name = '安静等待'; type = 'talk'; cat = 'status'; power = 0; pp = 999; effects = @() } }
    $streamers = @(0..5 | ForEach-Object {
        @{ id = "t115_actor_$_"; name = "平局演员$_"; type = 'talk'; persona = 'punctual';
            stats = @{ hp = 400; atk = 200; def = 200; spa = 200; sdef = 200; spd = 200 };
            skills = @($skillIds[0..3]); fifth = $skillIds[4]; blurb = '无伤害验收角色。' }
    })
    $drawPack = @{ id = 't115_draw'; name = '淘汰赛平局验收'; notice = '原创临时验收包。'; skills = $skills; streamers = $streamers } | ConvertTo-Json -Depth 20
    [IO.File]::WriteAllText((Join-Path $external 'fixture.json'), $drawPack, $utf8)
    $drawDb = [StreamDuel.Core.ContentDb]::FromJson($rules, $drawPack, $policy)
    $drawCode = New-Code $drawDb.PackId @($streamers | ForEach-Object { $_.id })
    foreach ($format in @('single', 'double')) {
        $drawPath = Join-Path $run "auto-draw-$format.json"
        $drawRun = Invoke-Cli "forced-draw-$format" @('--tournament', $drawPath, '--format', $format, '--auto', '--pack', 't115_draw', '--pack-dir', $external) (Registration 2 $drawCode $drawCode) 1
        $drawTournament = Read-Tournament $drawPath $drawDb
        $drawMatches = @($drawTournament.NextRound())
        Check (-not $drawTournament.IsFinished -and $drawTournament.Round -eq 1 -and $null -eq $drawMatches[0].WinnerSide) '自动平局被任意判胜或推进了赛程。'
        Check ($drawRun.Err.Contains('本场保持待赛') -and $drawRun.Err.Contains('手动录入') -and
            [regex]::Matches($drawRun.Out, '自动对局种子：').Count -eq 1) '自动平局没有明确提示重赛，或偷偷进行了重复自动对局。'
        Check (-not (Test-Path -LiteralPath ($drawPath + '.replay-library'))) '自动平局被归档为该对阵的决胜录像。'
        $drawReport = Report "forced-draw-report-$format" $drawPath @('--pack-dir', $external)
        Check ($drawReport.coverage.completedGames -eq 0 -and (Matches $drawReport)[0].replay.status -eq 'pending') '自动平局战报错误标记已完成。'
        $null = Invoke-Cli "forced-draw-manual-resume-$format" @('--tournament', $drawPath, '--pack-dir', $external) @('0', 'q')
        $afterResume = Read-Tournament $drawPath $drawDb
        Check (@($afterResume.Standings() | Where-Object { $_.Losses -eq 1 }).Count -eq 1) '自动平局后无法按真实重赛结果手动续赛。'
    }
    $checks.Add('PASS automatic_draw_keeps_pending_no_archive_no_retry')

    foreach ($choice in @(@('3', 'single'), @('4', 'double'))) {
        $menuPath = Join-Path $run ("menu-$($choice[1]).json")
        $null = Invoke-Cli "menu-$($choice[1])" @('--tournament', $menuPath) @($choice[0], 'q')
        Check ((Read-Tournament $menuPath).Format -eq $choice[1]) '交互菜单无法创建新增赛制。'
    }
    $checks.Add('PASS interactive_elimination_menu')
}
catch { $failure = $_.ToString() }
finally {
    $result = [ordered]@{ status = $(if ($null -eq $failure) { 'PASS' } else { 'FAIL' }); checks = @($checks.ToArray()); calls = @($calls.ToArray()); error = $failure }
    [IO.File]::WriteAllText((Join-Path $run 'report.json'), ($result | ConvertTo-Json -Depth 10), $utf8)
}
foreach ($check in $checks) { Write-Host $check }
Write-Host "证据目录：$run"
if ($null -ne $failure) { throw $failure }
