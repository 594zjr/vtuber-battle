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
$run = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ('obj/tournament-bans-verification/' + [Guid]::NewGuid().ToString('N'))))
if (-not $run.StartsWith($repository + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw '验收产物必须位于本工作树内。'
}
[void][IO.Directory]::CreateDirectory($run)
$temporary = Join-Path $run 'temp'
[void][IO.Directory]::CreateDirectory($temporary)
$utf8 = [Text.UTF8Encoding]::new($false, $true)
$checks = [Collections.Generic.List[string]]::new()
$calls = [Collections.Generic.List[object]]::new()
$failure = $null

function Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "$Message 证据目录：$run" }
}

function Invoke-Cli([string]$Name, [string[]]$Arguments, [string[]]$Lines = @(), [int]$Exit = 0) {
    Check ($Name -cmatch '^[A-Za-z0-9_-]+$') '用例名不可包含路径字符。'
    $work = Join-Path $run ('calls/' + $Name)
    [void][IO.Directory]::CreateDirectory($work)
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = 'dotnet'
    $start.ArgumentList.Add($cli)
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $start.WorkingDirectory = $work
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardInputEncoding = $utf8
    $start.StandardOutputEncoding = $utf8
    $start.StandardErrorEncoding = $utf8
    $start.Environment['TEMP'] = $temporary
    $start.Environment['TMP'] = $temporary
    $start.Environment['DOTNET_CLI_HOME'] = $temporary
    $start.Environment['DOTNET_SKIP_FIRST_TIME_EXPERIENCE'] = '1'
    $start.Environment['DOTNET_CLI_TELEMETRY_OPTOUT'] = '1'
    $start.Environment['DOTNET_GENERATE_ASPNET_CERTIFICATE'] = 'false'
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
        $calls.Add([ordered]@{ name = $Name; arguments = $Arguments; exit = $result.Exit; expected = $Exit })
        Check ($result.Exit -eq $Exit) "$Name 应退出 $Exit，实际 $($result.Exit)。"
        Check (-not (Test-Path -LiteralPath (Join-Path $work 'saves'))) "$Name 写入了普通玩家存档。"
        if ($Exit -eq 0) { Check ([string]::IsNullOrWhiteSpace($result.Err)) "$Name 成功时出现错误。" }
        else { Check ($result.Err.Contains('错误：')) "$Name 缺少中文错误。" }
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
    $parent = [IO.Path]::GetDirectoryName($Path)
    if (Test-Path -LiteralPath $parent -PathType Container) {
        foreach ($item in Get-ChildItem -LiteralPath $parent -File -Force) {
            if ($item.FullName.StartsWith($Path + '.tournament.', [StringComparison]::OrdinalIgnoreCase)) { $files.Add($item.FullName) }
        }
    }
    $library = $Path + '.replay-library'
    if (Test-Path -LiteralPath $library -PathType Container) {
        foreach ($item in Get-ChildItem -LiteralPath $library -File -Recurse -Force) { $files.Add($item.FullName) }
    }
    elseif (Test-Path -LiteralPath $library -PathType Leaf) { $files.Add($library) }
    return (($files | Sort-Object -Unique | ForEach-Object {
        $item = Get-Item -LiteralPath $_ -Force
        $_ + ':' + $item.LastWriteTimeUtc.Ticks + ':' + (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash
    }) -join "`n")
}

function Read-Tournament([string]$Path) {
    return [StreamDuel.Core.Tournament]::FromJson($db, [IO.File]::ReadAllText($Path, $utf8))
}

function New-Code([string[]]$Ids) {
    $preset = [StreamDuel.Core.TeamPreset]::new()
    $preset.PackId = $db.PackId
    $preset.Name = '禁用验收队伍'
    foreach ($id in $Ids) { $preset.Ids.Add($id) }
    return [StreamDuel.Core.TeamCode]::Encode($preset)
}

function Bans-Text($Pairing) {
    return (($Pairing.Bans | ForEach-Object { "$($_.Side):$($_.TargetIndex):$($_.StreamerId):$($_.RngState)" }) -join '|')
}

function Seed-Order($Tournament) {
    [int[]]$order = @(0..($Tournament.Players.Count - 1))
    if ($Tournament.IsElimination) {
        if ($null -ne $Tournament.SeedOrder) { return ,([int[]]@($Tournament.SeedOrder)) }
        if ($null -ne $Tournament.Seed) {
            $rng = [StreamDuel.Core.Rng]::new([uint32]$Tournament.Seed)
            for ($i = $order.Length - 1; $i -gt 0; $i--) {
                $j = [int][Math]::Floor($rng.Next() * ($i + 1))
                $swap = $order[$i]; $order[$i] = $order[$j]; $order[$j] = $swap
            }
        }
    }
    return ,$order
}

function Assert-Report([string]$Name, [string]$Path, [bool]$Archived) {
    $before = Fingerprint $Path
    $tournament = Read-Tournament $Path
    $text = Invoke-Cli ($Name + '-text') @('--tournament-report', $Path)
    $json = Invoke-Cli ($Name + '-json') @('--tournament-report', $Path, '--json')
    $report = $json.Out | ConvertFrom-Json -AsHashtable
    $library = [StreamDuel.Core.ReplayLibrary]::OpenReadOnly($Path + '.replay-library', $policy)
    $expected = [StreamDuel.Core.TournamentReport]::Build($tournament, $library)
    Check ($text.Out -ceq ($expected.ToText() + "`n") -and $json.Out -ceq ($expected.ToJson() + "`n")) "$Name CLI 战报未完整转发 Core。"
    if ($tournament.BanCount -gt 0) {
        Check ($report.Contains('banCount') -and $report.banCount -eq $tournament.BanCount) '战报缺少禁用配置。'
    }
    else { Check (-not $report.Contains('banCount')) '零禁用改变了旧战报JSON字段。' }
    $savedJson = [IO.File]::ReadAllText($Path, $utf8) | ConvertFrom-Json -AsHashtable
    $expectedVersion = if ($tournament.BanCount -gt 0) { 3 } elseif ($tournament.IsElimination) { 2 } else { 1 }
    Check ($savedJson.version -eq $expectedVersion) '赛事存档未遵守非零禁用v3、原零禁用v1/v2的版本隔离。'
    $seeds = Seed-Order $tournament
    $matches = @($report.rounds | ForEach-Object { $_.matches })
    $firstSideOne = $false
    foreach ($pairing in $tournament.Pairings) {
        $match = @($matches | Where-Object { $_.pairingId -eq $pairing.Id })
        Check ($match.Count -eq 1) "$Name 战报遗漏或重复对阵。"
        $match = $match[0]
        if ($tournament.BanCount -gt 0) {
            Check ($match.Contains('bans') -and $match.bans.Count -eq $pairing.Bans.Count) '战报禁用记录数量与赛事不同。'
        }
        else { Check (-not $match.Contains('bans')) '零禁用对阵增加了新战报字段。' }
        if ($pairing.IsBye) { Check ($pairing.Bans.Count -eq 0) '轮空不能禁用。'; continue }
        $first = if ([Array]::IndexOf($seeds, [int]$pairing.Player0) -lt [Array]::IndexOf($seeds, [int]$pairing.Player1)) { 0 } else { 1 }
        if ($first -eq 1) { $firstSideOne = $true }
        if ($null -ne $pairing.WinnerSide) {
            Check ($pairing.Bans.Count -eq 2 * $tournament.BanCount) '已完成对阵缺少禁用。'
        }
        $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        for ($i = 0; $i -lt $pairing.Bans.Count; $i++) {
            $ban = $pairing.Bans[$i]
            $expectedSide = if (($i % 2) -eq 0) { $first } else { 1 - $first }
            Check ($ban.Side -eq $expectedSide) '禁用未按赛事种子顺位逐轮交替。'
            $foe = if ($ban.Side -eq 0) { $pairing.Player1 } else { $pairing.Player0 }
            Check ($ban.TargetIndex -ge 0 -and $ban.TargetIndex -lt 6 -and
                $tournament.Players[$foe].Team[$ban.TargetIndex] -ceq $ban.StreamerId) '禁用未保留原始目标下标。'
            Check ($seen.Add("$($ban.Side):$($ban.TargetIndex)")) '同一方重复禁用了同一个原始目标。'
            Check ($text.Out.Contains($db.Streamer($ban.StreamerId).Name)) '文字战报遗漏被禁主播。'
            $reported = $match.bans[$i]
            Check ($reported.side -eq $ban.Side -and $reported.targetIndex -eq $ban.TargetIndex -and
                $reported.streamerId -ceq $ban.StreamerId -and $reported.streamerName -ceq $db.Streamer($ban.StreamerId).Name -and
                $reported.rng -eq $ban.RngState) 'JSON战报中的禁用来源、顺序、名字或RNG与赛事不同。'
        }
        if ($pairing.Bans.Count -gt 0) { Check ($text.Out.Contains('本场禁用：')) '战报缺少本场禁用段。' }
        if (-not $Archived -or $null -eq $pairing.WinnerSide) { continue }
        Check ($match.replay.status -ceq 'saved') '实际自动对局没有可重放归档。'
        $record = $library.Load($match.replay.id)
        if ($tournament.BanCount -eq 1) { Check ($record.BanOne -and $record.BanCount -eq 0) '单禁用没有沿用旧BanOne规范。' }
        if ($tournament.BanCount -eq 2) { Check (-not $record.BanOne -and $record.BanCount -eq 2 -and $record.Version -eq 4) '双禁用未采用版本4录像。' }
        $events = @($record.Events | Where-Object { $_.Type -eq [StreamDuel.Core.BattleRecordEventType]::Ban })
        Check ($events.Count -eq $pairing.Bans.Count) '录像与赛事禁用数量不同。'
        for ($i = 0; $i -lt $events.Count; $i++) {
            $event = $events[$i]; $ban = $pairing.Bans[$i]
            Check ($event.Side -eq $ban.Side -and $event.To -eq $ban.TargetIndex -and $event.RngState -eq $ban.RngState) '录像禁用次序/原下标/RNG 与赛事不同。'
        }
        $battle = [StreamDuel.Core.Replay]::Run($db, $record)
        Check ($battle.Phase -eq [StreamDuel.Core.Phase]::Over -and $battle.Winner -eq $pairing.WinnerSide) '录像终局与赛事比分不符。'
        Check ($battle.Sides[0].Team.Count -eq 6 - $tournament.BanCount -and
            $battle.Sides[1].Team.Count -eq 6 - $tournament.BanCount) '禁用后阵容人数不正确。'
        foreach ($ban in $pairing.Bans) {
            $foe = 1 - $ban.Side
            Check (@($battle.Events | Where-Object { $_.Kind -eq [StreamDuel.Core.BattleEventKind]::Enter -and $_.Side -eq $foe -and $_.Id -ceq $ban.StreamerId }).Count -eq 0) '被禁主播仍然出场。'
        }
    }
    Check ((Fingerprint $Path) -ceq $before) "$Name 只读战报/重放改写来源。"
    return [pscustomobject]@{ Json = $report; FirstSideOne = $firstSideOne; Text = $text.Out }
}

try {
    $assembly = [Reflection.Assembly]::LoadFrom($core)
    Check ([StringComparer]::OrdinalIgnoreCase.Equals([IO.Path]::GetFullPath($assembly.Location), [IO.Path]::GetFullPath($core))) '请使用未加载其他 Core 的新 PowerShell 进程。'
    $content = Join-Path $binary 'Content'
    $policy = [StreamDuel.Core.PackPolicy]::Load((Join-Path $content 'PackPolicy.json'))
    $db = [StreamDuel.Core.ContentDb]::FromJson([IO.File]::ReadAllText((Join-Path $content 'StreamDuelRules.json'), $utf8),
        [IO.File]::ReadAllText((Join-Path $content 'Packs/original.json'), $utf8), $policy)
    $codeA = New-Code @('bell', 'enka', 'shiori', 'lumi', 'hane', 'momo')
    $codeB = New-Code @('kei', 'yuan', 'nyako', 'buck', 'kasumi', 'bee')
    $two = @('1', '甲', $codeA, '乙', $codeB, '')

    $implicit = Join-Path $run 'implicit-zero.json'
    $explicit = Join-Path $run 'explicit-zero.json'
    $a = Invoke-Cli 'zero-default' @('--tournament', $implicit, '--auto') $two
    $b = Invoke-Cli 'zero-explicit' @('--tournament', $explicit, '--auto', '--bans', '0') $two
    Check ([IO.File]::ReadAllText($implicit, $utf8) -ceq [IO.File]::ReadAllText($explicit, $utf8)) '显式0改变了赛事存档。'
    # 录像库编号含归档时间，只归一化编号及赛事路径，其余旧输出必须完全一致。
    $outputA = [regex]::Replace($a.Out.Replace($implicit, '<赛事>'), '(?m)^赛事录像已保存：[^\r\n]+', '赛事录像已保存：<编号>')
    $outputB = [regex]::Replace($b.Out.Replace($explicit, '<赛事>'), '(?m)^赛事录像已保存：[^\r\n]+', '赛事录像已保存：<编号>')
    Check ($outputA -ceq $outputB) '显式0改变了提示、输入、种子、结果或哈希。'
    $zeroA = Assert-Report 'zero-default-report' $implicit $true
    $zeroB = Assert-Report 'zero-explicit-report' $explicit $true
    $libA = [StreamDuel.Core.ReplayLibrary]::OpenReadOnly($implicit + '.replay-library', $policy)
    $libB = [StreamDuel.Core.ReplayLibrary]::OpenReadOnly($explicit + '.replay-library', $policy)
    $replayA = $zeroA.Json.rounds[0].matches[0].replay.id
    $replayB = $zeroB.Json.rounds[0].matches[0].replay.id
    Check ($libA.Load($replayA).ToJson() -ceq $libB.Load($replayB).ToJson()) '显式0改变了旧录像。'
    $checks.Add('PASS 默认与显式0的提示/种子/哈希/赛事/录像一致')

    foreach ($count in @(1, 2)) {
        $path = Join-Path $run "auto-$count.json"
        $four = @('1', '甲', $codeA, '乙', $codeB, '丙', $codeA, '丁', $codeB, '')
        $auto = Invoke-Cli "auto-$count" @('--tournament', $path, '--bans', [string]$count, '--auto') $four
        Check ((Read-Tournament $path).IsFinished) "禁用$count 的四人赛事未结束。"
        $report = Assert-Report "auto-$count-report" $path $true
        Check ($report.FirstSideOne) '样本没有覆盖Player1报名顺位更小的对阵。'
        Check ($report.Json.coverage.archivedGames -eq 6) '六场自动对局录像不完整。'
        $before = Fingerprint $path
        $again = Invoke-Cli "auto-$count-finished" @('--tournament', $path, '--auto', '--bans', [string]$count)
        Check ((Fingerprint $path) -ceq $before -and -not $again.Out.Contains('自动对局种子：')) '已结束赛事重跑禁用/对局或改写存档。'
    }
    $checks.Add('PASS 禁用1/2六局录像可重放、原始号位与RNG一致、交替顺序、被禁不出场、已完不重打')

    $manual = Join-Path $run 'manual-two.json'
    $null = Invoke-Cli 'manual-first-pause' @('--tournament', $manual, '--bans', '2') ($two + @('1', 'q'))
    $saved = Read-Tournament $manual
    Check ($saved.BanCount -eq 2 -and $saved.Pairings[0].Bans.Count -eq 1 -and -not $saved.IsFinished) '第一次禁用未即时保存。'
    $prefix = Bans-Text $saved.Pairings[0]
    $null = Invoke-Cli 'manual-eof' @('--tournament', $manual)
    Check ((Bans-Text (Read-Tournament $manual).Pairings[0]) -ceq $prefix) '禁用输入EOF改变了已有选择。'
    foreach ($value in @('0', '1')) {
        $snapshot = Fingerprint $manual
        $bad = Invoke-Cli "resume-mismatch-$value" @('--tournament', $manual, '--bans', $value, '--auto') @() 1
        Check ($bad.Err.Contains('不一致') -and $bad.Out.Length -eq 0 -and (Fingerprint $manual) -ceq $snapshot) '不同禁用配置未在写入前拒绝。'
    }
    $null = Invoke-Cli 'manual-second-eof' @('--tournament', $manual, '--bans', '2') @('1')
    $saved = Read-Tournament $manual
    Check ($saved.Pairings[0].Bans.Count -eq 2 -and (Bans-Text $saved.Pairings[0]).StartsWith($prefix, [StringComparison]::Ordinal)) '续赛覆盖已有禁用或重复选择。'
    $invalid = Invoke-Cli 'manual-duplicate-eof' @('--tournament', $manual) @('1', '7', 'bad')
    Check ($invalid.Out.Contains('已禁用的主播不能重复选择') -and (Read-Tournament $manual).Pairings[0].Bans.Count -eq 2) '重复/越界禁用没有拒绝。'
    $pending = Assert-Report 'manual-pending-report' $manual $false
    Check ($pending.Json.rounds[0].matches[0].replay.status -ceq 'pending') '部分禁用被当作已完成对局。'
    $null = Invoke-Cli 'manual-finish' @('--tournament', $manual) @('2', '2', '1')
    $saved = Read-Tournament $manual
    Check ($saved.IsFinished -and $saved.Pairings[0].Bans.Count -eq 4 -and $saved.Pairings[0].WinnerSide -eq 1) '续赛后禁用或手动比分丢失。'
    Check (-not (Test-Path -LiteralPath ($manual + '.replay-library'))) '手动赛事虚构了实际录像。'
    $finished = Assert-Report 'manual-finished-report' $manual $false
    Check ($finished.Json.coverage.missingGames -eq 1) '手动禁用报告未保留缺少录像事实。'
    $checks.Add('PASS 每次人工禁用即保存、q/EOF/非法输入恢复、配置不能改、无录像仍有禁用战报')

    $partialAuto = Join-Path $run 'partial-auto.json'
    $null = Invoke-Cli 'partial-auto-create' @('--tournament', $partialAuto, '--bans', '2') ($two + @('1', 'q'))
    $prefix = Bans-Text (Read-Tournament $partialAuto).Pairings[0]
    $null = Invoke-Cli 'partial-auto-resume' @('--tournament', $partialAuto, '--auto')
    Check ((Bans-Text (Read-Tournament $partialAuto).Pairings[0]).StartsWith($prefix, [StringComparison]::Ordinal)) '自动续赛重选了人工禁用。'
    $null = Assert-Report 'partial-auto-report' $partialAuto $true
    $checks.Add('PASS 人工禁用前缀转自动完成仍保留原选择并进入实际录像')

    $single = Join-Path $run 'single-bans-one.json'
    $five = @('甲', $codeA, '乙', $codeB, '丙', $codeA, '丁', $codeB, '戊', $codeA, '')
    $singleRun = Invoke-Cli 'single-one-auto' @('--tournament', $single, '--format', 'single', '--bans', '1', '--auto') $five
    $singleReport = Assert-Report 'single-one-report' $single $true
    $singleMatches = @($singleReport.Json.rounds | ForEach-Object { $_.matches })
    Check ((Read-Tournament $single).IsFinished -and $singleReport.Json.coverage.archivedGames -eq 4 -and
        @($singleMatches | Where-Object { $null -eq $_.player1 }).Count -eq 3) '单败+禁用没有保留五人四场实战与三次轮空。'
    Check ($singleRun.Out.Contains('冠军：') -and $singleRun.Out.Contains('赛事种子顺位') -and
        $singleReport.Json.bestOf -eq 1 -and $null -ne $singleReport.Json.champion) '单败禁用丢失冠军、局制或顺位说明。'
    foreach ($conflict in @(@('--format', 'double', '--bans', '1'), @('--format', 'single', '--bans', '2'))) {
        $before = Fingerprint $single
        $bad = Invoke-Cli ("single-config-conflict-" + $conflict[1] + $conflict[3]) (@('--tournament', $single) + $conflict) @() 1
        Check ($bad.Out.Length -eq 0 -and $bad.Err.Contains('不一致') -and (Fingerprint $single) -ceq $before) '淘汰赛续赛冲突没有在写入前拒绝。'
    }
    $checks.Add('PASS 单败+禁用1真实四局归档、轮空无禁用、冠军战报与两项续赛配置门禁')

    $doublePath = Join-Path $run 'double-bans-two.json'
    $null = Invoke-Cli 'double-two-create' @('--tournament', $doublePath, '--format', 'double', '--bans', '2') ($two[1..($two.Length - 1)] + @('1', 'q'))
    $double = Read-Tournament $doublePath
    $brackets = [Collections.Generic.List[string]]::new()
    for ($step = 0; -not $double.IsFinished -and $step -lt 8; $step++) {
        $pairing = @($double.Pairings | Where-Object { -not $_.IsBye -and $null -eq $_.WinnerSide })[0]
        $brackets.Add($pairing.Bracket)
        if ($pairing.Bans.Count -eq 0) {
            $null = Invoke-Cli "double-two-pause-$step" @('--tournament', $doublePath) @('1', 'q')
            $double = Read-Tournament $doublePath
            $pairing = @($double.Pairings | Where-Object { -not $_.IsBye -and $null -eq $_.WinnerSide })[0]
        }
        Check ($pairing.Bans.Count -eq 1) '双败每场首次禁用未保留为可续赛前缀。'
        $pairingId = $pairing.Id
        $prefix = Bans-Text $pairing
        $winner = 0
        if ($pairing.Bracket -eq 'final') {
            $first = @($double.Standings() | Where-Object { $_.Player -eq $pairing.Player0 })[0]
            $winner = if ($first.Losses -eq 1) { 0 } else { 1 }
        }
        $stepRun = Invoke-Cli "double-two-finish-$step" @('--tournament', $doublePath, '--format', 'double', '--bans', '2') @('1', '2', '2', '-1', [string]$winner, 'q')
        $double = Read-Tournament $doublePath
        $finishedPair = @($double.Pairings | Where-Object { $_.Id -eq $pairingId })[0]
        Check ($finishedPair.Bans.Count -eq 4 -and (Bans-Text $finishedPair).StartsWith($prefix, [StringComparison]::Ordinal) -and
            $stepRun.Out.Contains('不能登记平局')) '双败续赛重选禁用、遗漏选择或放行了平局。'
    }
    Check ($double.IsFinished -and ($brackets -join ',') -ceq 'winners,final,reset') '双败+禁用没有保留胜者组、总决赛和重置赛。'
    $doubleReport = Assert-Report 'double-two-report' $doublePath $false
    Check ($doubleReport.Json.coverage.missingGames -eq 3 -and $doubleReport.Json.resetFinal -and
        $doubleReport.Json.champion -eq $double.Champion -and -not (Test-Path -LiteralPath ($doublePath + '.replay-library'))) '人工双败重置报告丢失禁用/冠军或虚构录像。'
    $checks.Add('PASS 双败+禁用2三场逐次中断续赛、总决赛重置、禁平局和无录像禁用战报')

    # Core 生成自定义种子顺序的真实存档，CLI 续赛不能改回报名顺序。
    $orderedPath = Join-Path $run 'ordered-single-bans.json'
    $settings = [StreamDuel.Core.TournamentOptions]::new()
    $settings.SeedOrder = [int[]]@(1, 0)
    $ordered = [StreamDuel.Core.Tournament]::new($db, 'single', 0, $settings, 1)
    $ordered.AddPlayer('甲', $codeA); $ordered.AddPlayer('乙', $codeB)
    [void]$ordered.NextRound()
    [IO.File]::WriteAllText($orderedPath, $ordered.ToJson(), $utf8)
    $null = Invoke-Cli 'ordered-single-first-ban' @('--tournament', $orderedPath) @('1', 'q')
    $ordered = Read-Tournament $orderedPath
    $chosen = $ordered.Pairings[0].Bans[0]
    $chooser = if ($chosen.Side -eq 0) { $ordered.Pairings[0].Player0 } else { $ordered.Pairings[0].Player1 }
    Check ($chooser -eq 1) '自定义种子顺序续赛仍由报名编号较小者先禁用。'
    $null = Invoke-Cli 'ordered-single-auto-finish' @('--tournament', $orderedPath, '--auto', '--bans', '1')
    $null = Assert-Report 'ordered-single-report' $orderedPath $true
    $checks.Add('PASS 自定义种子顺序存档续赛按有效种子选择并保存完整录像')

    $fresh = Join-Path $run 'must-not-create/fresh.json'
    $invalidOptions = @(@('--bans'), @('--bans', ''), @('--bans', '-1'), @('--bans', '3'),
        @('--bans', '01'), @('--bans', '+1'), @('--bans', '1.0'), @('--bans', ' 1'), @('--bans', '一'),
        @('--bans', '0', '--bans', '0'), @('--bans', '1', '--ban'))
    for ($i = 0; $i -lt $invalidOptions.Count; $i++) {
        $bad = Invoke-Cli "invalid-bans-$i" (@('--tournament', $fresh) + $invalidOptions[$i]) @() 1
        Check ($bad.Out.Length -eq 0 -and -not $bad.Err.Contains('未知参数')) '无效参数未由bans/互斥校验拒绝。'
    }
    $conflicts = @(@(), @('--auto'), @('--list-packs'), @('--replays'), @('--replay', 'missing.json'),
        @('--replay-code', 'SD1-invalid'), @('--glossary'), @('--card', 'bell'), @('--cards', 'original'),
        @('--hotseat'), @('--host', '0'), @('--join', '127.0.0.1:1'), @('--watch', '127.0.0.1:1'),
        @('--lan-list'), @('--calc', 'bell', '1', 'kei'), @('--tournament-report', $manual))
    for ($i = 0; $i -lt $conflicts.Count; $i++) {
        foreach ($first in @($true, $false)) {
            $arguments = if ($first) { @('--bans', '1') + $conflicts[$i] } else { $conflicts[$i] + @('--bans', '1') }
            $bad = Invoke-Cli "conflict-$i-$first" $arguments @() 1
            Check ($bad.Err.Contains('--bans 只能与 --tournament 同用') -and $bad.Out.Length -eq 0) 'bans被其他入口忽略或因其它附加参数假绿。'
        }
    }
    Check (-not (Test-Path -LiteralPath (Split-Path $fresh -Parent))) '参数拒绝前创建了赛事目录。'
    $help = Invoke-Cli 'help' @('--help', '--bans', '2')
    Check ($help.Out.Contains('--bans 0|1|2') -and $help.Out.Contains('显式值必须相同')) '帮助未说明配置和续赛限制。'
    $checks.Add('PASS 参数严格取值/重复/与旧ban互斥、其它模式双顺序拒绝、帮助完整')
}
catch { $failure = $_.Exception.Message }
finally {
    $result = [ordered]@{ status = $(if ($null -eq $failure) { 'PASS' } else { 'FAIL' }); checks = @($checks.ToArray()); calls = @($calls.ToArray()); error = $failure }
    [IO.File]::WriteAllText((Join-Path $run 'result.json'), ($result | ConvertTo-Json -Depth 16), $utf8)
}
foreach ($check in $checks) { Write-Host $check }
Write-Host "证据目录：$run"
if ($null -ne $failure) { throw $failure }
