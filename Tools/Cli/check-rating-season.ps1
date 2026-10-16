#requires -Version 7.0
param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug')

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$prefix = $repository.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$cli = Join-Path $PSScriptRoot "bin/$Configuration/net8.0/Cli.dll"
if (-not (Test-Path -LiteralPath $cli -PathType Leaf)) { throw "请先构建 Tools/Cli -c $Configuration。" }
$run = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ('obj/rating-season-verification/' + [Guid]::NewGuid().ToString('N'))))
if (-not $run.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw '验收产物必须在当前工作树内。' }
[void][IO.Directory]::CreateDirectory($run)
$utf8 = [Text.UTF8Encoding]::new($false, $true)
$children = [Collections.Generic.List[object]]::new()
$checks = [Collections.Generic.List[string]]::new()
$calls = [Collections.Generic.List[object]]::new()
$failure = $null
$hostTeam = 'bell,enka,shiori,lumi,hane,momo'
$guestTeam = 'kei,yuan,nyako,buck,kasumi,bee'

function Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "$Message 证据目录：$run" }
}

function Write-Text([string]$Path, [string]$Text) {
    Check ([IO.Path]::GetFullPath($Path).StartsWith($run + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) '夹具写入路径越界。'
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path))
    [IO.File]::WriteAllText($Path, $Text, $utf8)
}

function Read-Log([string]$Path) {
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
    $reader = [IO.StreamReader]::new($stream, $utf8)
    try { return $reader.ReadToEnd() } finally { $reader.Dispose() }
}

function Start-Cli([string]$Name, [string[]]$Parameters) {
    $work = Join-Path $run "calls/$Name"
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
    $child = [pscustomobject]@{
        Name = $Name; Process = $process; Started = $false
        OutPath = (Join-Path $work 'stdout.log'); ErrPath = (Join-Path $work 'stderr.log')
        OutStream = $null; ErrStream = $null; OutTask = $null; ErrTask = $null
    }
    $children.Add($child)
    $child.OutStream = [IO.FileStream]::new($child.OutPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::ReadWrite, 1, [IO.FileOptions]::Asynchronous)
    $child.ErrStream = [IO.FileStream]::new($child.ErrPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::ReadWrite, 1, [IO.FileOptions]::Asynchronous)
    $child.Started = $process.Start()
    Check $child.Started "$Name 无法启动。"
    $child.OutTask = $process.StandardOutput.BaseStream.CopyToAsync($child.OutStream)
    $child.ErrTask = $process.StandardError.BaseStream.CopyToAsync($child.ErrStream)
    return $child
}

function Wait-Cli($Child, [int]$Exit = 0) {
    Check ($Child.Process.WaitForExit(30000)) "$($Child.Name) 超过 30 秒未退出。"
    Check ($Child.OutTask.Wait(5000) -and $Child.ErrTask.Wait(5000)) "$($Child.Name) 输出未结束。"
    $Child.OutStream.Flush(); $Child.ErrStream.Flush()
    $result = [pscustomobject]@{ Out = (Read-Log $Child.OutPath); Err = (Read-Log $Child.ErrPath); Exit = $Child.Process.ExitCode }
    $calls.Add([ordered]@{ name = $Child.Name; exit = $result.Exit; expected = $Exit })
    Check ($result.Exit -eq $Exit) "$($Child.Name) 退出码应为 $Exit，实际 $($result.Exit)。"
    if ($Exit -eq 0) { Check ([string]::IsNullOrWhiteSpace($result.Err)) "$($Child.Name) 成功时出现标准错误。" }
    else { Check ($result.Err.StartsWith('错误：')) "$($Child.Name) 未给出中文错误。" }
    return $result
}

function Invoke-Cli([string]$Name, [string[]]$Parameters, [int]$Exit = 0) {
    $child = Start-Cli $Name $Parameters
    $child.Process.StandardInput.Close()
    return Wait-Cli $child $Exit
}

function Wait-Output($Child, [string]$Pattern) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    while ($timer.ElapsedMilliseconds -lt 15000) {
        $match = [regex]::Match((Read-Log $Child.OutPath), $Pattern)
        if ($match.Success) { return $match }
        Check (-not $Child.Process.HasExited) "$($Child.Name) 在预期输出前退出。"
        Start-Sleep -Milliseconds 25
    }
    throw "$($Child.Name) 等待输出超时。"
}

function Fingerprint([string]$Directory) {
    if (-not (Test-Path -LiteralPath $Directory)) { return '<missing>' }
    return (@(Get-ChildItem -LiteralPath $Directory -Recurse -Force | Sort-Object FullName | ForEach-Object {
        if ($_.PSIsContainer) { 'directory:' + $_.FullName }
        else { $_.FullName + ':' + $_.LastWriteTimeUtc.Ticks + ':' + (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    }) -join "`n")
}

function Read-Profile([string]$Directory) {
    return ([IO.File]::ReadAllText((Join-Path $Directory 'profile.json'), $utf8) | ConvertFrom-Json -AsHashtable)
}

function Json($Value) { return ($Value | ConvertTo-Json -Depth 20 -Compress) }

function Tier([int]$Rating) {
    if ($Rating -ge 2100) { return '巅峰主场' }
    if ($Rating -ge 1800) { return '全场焦点' }
    if ($Rating -ge 1500) { return '人气聚光' }
    if ($Rating -ge 1200) { return '暖场新星' }
    return '初声开播'
}

function Placed($Rating) {
    return ($Rating.games -ge 10 -or ($Rating.Contains('isPlaced') -and $Rating.isPlaced))
}

function Peak($Rating) {
    if ($Rating.Contains('peakRating')) { return $Rating.peakRating }
    return $Rating.rating
}

function Write-Fixture([string]$Directory, [int]$Rating, [int]$Games, [Nullable[int]]$Peak = $null) {
    $current = [ordered]@{ games = $Games; rating = $Rating }
    if ($null -ne $Peak) { $current['peakRating'] = [int]$Peak }
    $profile = [ordered]@{
        packs = [ordered]@{ original = [ordered]@{ draws = 1; losses = 1; pickWins = @{ bell = 2 }; picks = @{ bell = 3 }; wins = 2 } }
        ratings = [ordered]@{ '默认' = $current; 'Z保留赛制' = [ordered]@{ games = 8; rating = 1601 }; 'A保留赛制' = [ordered]@{ games = 12; rating = 1801 } }
    }
    Write-Text (Join-Path $Directory 'profile.json') (Json $profile)
    Write-Text (Join-Path $Directory 'settings.json') '{"version":1,"defaultPack":"original","fastPlay":true}'
    Write-Text (Join-Path $Directory 'teams.json') '{"version":1,"teams":[]}'
}

function Assert-History($Rating, [int]$Index, [int]$Final, [int]$Peak, [int]$Games) {
    $entry = $Rating.history[$Index]
    Check ($entry.season -eq $Index + 1 -and $entry.finalRating -eq $Final -and $entry.peakRating -eq $Peak -and
        $entry.games -eq $Games -and $entry.tier -ceq (Tier $Final)) '历史赛季的最终分、峰值、段位或场数不完整。'
}

function Assert-NoBattle([string]$Directory, $Result) {
    Check (-not $Result.Out.Contains('监听端口：') -and -not $Result.Out.Contains('选择行动') -and
        -not $Result.Out.Contains('对局结束：') -and -not $Result.Out.Contains('回放已保存')) '独立赛季命令启动了对战或联网。'
    foreach ($name in @('replays', 'replay-library')) {
        Check (-not (Test-Path -LiteralPath (Join-Path $Directory $name))) '独立赛季命令生成了回放。'
    }
}

function Invoke-History([string]$Name, [string]$Directory) {
    $before = Fingerprint $Directory
    $result = Invoke-Cli $Name @('--rating-history', '--save-dir', $Directory)
    Check ((Fingerprint $Directory) -ceq $before) '查询历史改写了文件字节、时间或目录内容。'
    Assert-NoBattle $Directory $result
    return $result
}

try {
    $missing = Join-Path $run 'missing-parent/saves'
    $empty = Invoke-History 'history-missing' $missing
    Check (-not (Test-Path -LiteralPath (Split-Path $missing -Parent)) -and
        $empty.Out.Contains('第 1 赛季') -and $empty.Out.Contains('1500 分') -and $empty.Out.Contains('0 局')) '缺失档案查询没有显示默认 S1，或创建了目录。'
    $fresh = Join-Path $run 'fresh-saves'
    $freshResult = Invoke-Cli 'new-season-empty' @('--new-season', '--save-dir', $fresh)
    Assert-NoBattle $fresh $freshResult
    $freshRating = (Read-Profile $fresh).ratings.'默认'
    Check ($freshRating.season -eq 2 -and $freshRating.rating -eq 1500 -and $freshRating.games -eq 0 -and
        -not (Placed $freshRating) -and $freshRating.history.Count -eq 1) '空档案不能从 S1 正确切换至 S2。'
    Assert-History $freshRating 0 1500 1500 0
    $firstHistory = Invoke-History 'history-fresh-first' $fresh
    $secondHistory = Invoke-History 'history-fresh-second' $fresh
    Check ($firstHistory.Out -ceq $secondHistory.Out) '同档案跨进程重复查询输出不稳定。'
    $checks.Add('PASS missing_history_read_only_and_empty_new_season')

    $cases = @(
        @{ Name = 'high'; Rating = 1701; Games = 20; Peak = 2200; Expected = 1601 },
        @{ Name = 'low'; Rating = 1299; Games = 9; Peak = $null; Expected = 1400 },
        @{ Name = 'equal'; Rating = 1500; Games = 0; Peak = $null; Expected = 1500 },
        @{ Name = 'below'; Rating = 1499; Games = 10; Peak = $null; Expected = 1500 },
        @{ Name = 'above'; Rating = 1501; Games = 1; Peak = $null; Expected = 1501 },
        @{ Name = 'negative'; Rating = -1501; Games = 10; Peak = $null; Expected = -1 }
    )
    foreach ($case in $cases) {
        $directory = Join-Path $run ($case.Name + '-saves')
        Write-Fixture $directory $case.Rating $case.Games $case.Peak
        $old = Read-Profile $directory
        $sideFiles = @('settings.json', 'teams.json') | ForEach-Object { (Get-FileHash -LiteralPath (Join-Path $directory $_)).Hash }
        $changed = Invoke-Cli ('new-' + $case.Name) @('--new-season', '--save-dir', $directory)
        Assert-NoBattle $directory $changed
        $profile = Read-Profile $directory
        $rating = $profile.ratings.'默认'
        Check ($rating.season -eq 2 -and $rating.rating -eq $case.Expected -and $rating.games -eq 0 -and
            (Peak $rating) -eq $case.Expected -and (Placed $rating) -eq ($case.Games -ge 10) -and $rating.history.Count -eq 1) '软重置数值、峰值、定级资格或场数不符合规则。'
        Assert-History $rating 0 $case.Rating $(if ($null -eq $case.Peak) { $case.Rating } else { $case.Peak }) $case.Games
        Check ((Json $profile.packs) -ceq (Json $old.packs) -and
            (Json $profile.ratings.'A保留赛制') -ceq (Json $old.ratings.'A保留赛制') -and
            (Json $profile.ratings.'Z保留赛制') -ceq (Json $old.ratings.'Z保留赛制')) '切季覆盖了包统计或其他赛制记录。'
        $afterSideFiles = @('settings.json', 'teams.json') | ForEach-Object { (Get-FileHash -LiteralPath (Join-Path $directory $_)).Hash }
        Check (($sideFiles -join ',') -ceq ($afterSideFiles -join ',')) '切季改写了设置或队伍预设。'
        $statusShown = if ($case.Games -ge 10) { $changed.Out.Contains('已定级') }
            else { $changed.Out.Contains('未定级') -or $changed.Out.Contains('定级中') }
        Check ($changed.Out.Contains('第 2 赛季') -and $changed.Out.Contains("$($case.Expected) 分") -and
            $changed.Out.Contains('最高') -and $changed.Out.Contains((Tier $case.Expected)) -and $changed.Out.Contains('0 局') -and
            $statusShown) '切季没有完整显示当前赛季、分数、峰值、段位、场数和定级状态。'
        $history = Invoke-History ('history-' + $case.Name) $directory
        Check ($history.Out.IndexOf('赛制：A保留赛制') -lt $history.Out.IndexOf('赛制：Z保留赛制') -and
            $history.Out.IndexOf('赛制：Z保留赛制') -lt $history.Out.IndexOf('赛制：默认') -and
            $history.Out.Contains('已结束赛季：') -and $history.Out.Contains("$($case.Rating) 分")) '历史查询遗漏赛制/历史或未使用 ordinal 排序。'
    }
    $highDirectory = Join-Path $run 'high-saves'
    $null = Invoke-Cli 'new-high-second-time' @('--new-season', '--save-dir', $highDirectory)
    $high = (Read-Profile $highDirectory).ratings.'默认'
    Check ($high.season -eq 3 -and $high.rating -eq 1551 -and (Placed $high) -and $high.history.Count -eq 2) '再次跨进程切季丢失定级资格或没有延续历史。'
    Assert-History $high 0 1701 2200 20
    Assert-History $high 1 1601 1601 0
    $checks.Add('PASS_rounding_old_profiles_history_and_other_data_preserved')

    $unexpected = Join-Path $run 'invalid-created/saves'
    $conflicts = @(
        @('--auto'), @('--pack', 'original'), @('--pack-dir', 'unused'), @('--packs', 'original,fan_asoul_vr'),
        @('--host', '0'), @('--join', '127.0.0.1:1'), @('--lan-list'), @('--lan-host', '验收'),
        @('--spectate-port', '0'), @('--watch', '127.0.0.1:1'), @('--hotseat'), @('--seed', '1'),
        @('--level', 'normal'), @('--team', $hostTeam), @('--team-b', $guestTeam), @('--team-code', 'unused'),
        @('--name-a', '甲'), @('--name-b', '乙'), @('--export-team'), @('--random-teams'),
        @('--ban'), @('--lead'), @('--advice'), @('--hash'), @('--timer', '10,30'), @('--share'),
        @('--replay', 'unused.json'), @('--replay-code', 'unused'), @('--replays'), @('--replay-best-effort'),
        @('--list-packs'), @('--glossary'), @('--card', 'bell'), @('--cards', 'original'),
        @('--report', 'unused.md'), @('--tournament', 'unused.json'), @('--tournament-report', 'unused.json'), @('--json'),
        @('--format', 'single'), @('--branch', 'unused.json', '--turn', '1', '--side', '0'),
        @('--calc', 'bell', '1', 'enka'), @('--matchup', $hostTeam, 'vs', $guestTeam)
    )
    foreach ($mode in @('--new-season', '--rating-history')) {
        $label = $mode.Substring(2)
        foreach ($reverse in @($false, $true)) {
            for ($i = 0; $i -lt $conflicts.Count; $i++) {
                $selection = @($mode, '--save-dir', $unexpected)
                $arguments = if ($reverse) { $conflicts[$i] + $selection } else { $selection + $conflicts[$i] }
                $rejected = Invoke-Cli "reject-$label-$reverse-$i" $arguments 1
                Check ($rejected.Out.Length -eq 0 -and -not (Test-Path -LiteralPath (Split-Path $unexpected -Parent))) '冲突参数没有在输出或修改档案前拒绝。'
            }
        }
        $null = Invoke-Cli "duplicate-$label" @($mode, $mode, '--save-dir', $unexpected) 1
        $null = Invoke-Cli "value-$label" @($mode, '1', '--save-dir', $unexpected) 1
        $help = Invoke-Cli "help-$label" @($mode, '--save-dir', $unexpected, '--help')
        Check ($help.Out.Contains('--new-season') -and $help.Out.Contains('--rating-history')) '帮助没有列出两个赛季命令。'
    }
    $null = Invoke-Cli 'both-modes' @('--new-season', '--rating-history', '--save-dir', $unexpected) 1
    $null = Invoke-Cli 'both-modes-reverse' @('--rating-history', '--new-season', '--save-dir', $unexpected) 1
    Check (-not (Test-Path -LiteralPath (Split-Path $unexpected -Parent))) '拒绝或帮助创建了档案目录。'
    $beforeConflict = Fingerprint $highDirectory
    $null = Invoke-Cli 'existing-conflict' @('--new-season', '--save-dir', $highDirectory, '--auto') 1
    Check ((Fingerprint $highDirectory) -ceq $beforeConflict) '无效切季参数改写了已有档案。'
    $checks.Add('PASS_independent_mode_conflicts_both_orders_without_side_effects')

    $badProfiles = @(
        @{ Name = 'broken-json'; Text = '{broken' },
        @{ Name = 'empty-file'; Text = '' },
        @{ Name = 'broken-season'; Text = '{"packs":{},"ratings":{"默认":{"games":0,"rating":1500,"season":2,"history":[]}}}' }
    )
    foreach ($bad in $badProfiles) {
        $directory = Join-Path $run $bad.Name
        Write-Text (Join-Path $directory 'profile.json') $bad.Text
        Write-Text (Join-Path $directory '.profile.lock') ''
        $before = Fingerprint $directory
        foreach ($mode in @('--rating-history', '--new-season')) {
            $rejected = Invoke-Cli ($bad.Name + '-' + $mode.Substring(2)) @($mode, '--save-dir', $directory) 1
            Check ($rejected.Out.Length -eq 0 -and (Fingerprint $directory) -ceq $before) '损坏或空档案被赛季命令当作新档案覆盖。'
        }
    }
    $checks.Add('PASS_corrupt_or_empty_profile_rejected_without_overwrite')

    $invalidUtf8 = Join-Path $run 'invalid-utf8'
    $invalidPath = Join-Path $invalidUtf8 'profile.json'
    Write-Text $invalidPath ''
    Write-Text (Join-Path $invalidUtf8 '.profile.lock') ''
    $bytes = $utf8.GetBytes('{"packs":{},"ratings":{"X":{"games":12,"rating":1510}}}')
    $bytes[[Array]::IndexOf($bytes, [byte][char]'X')] = 0xff
    [IO.File]::WriteAllBytes($invalidPath, $bytes)
    $before = Fingerprint $invalidUtf8
    foreach ($mode in '--rating-history', '--new-season') {
        $rejected = Invoke-Cli ('invalid-utf8-' + $mode.Substring(2)) @($mode, '--save-dir', $invalidUtf8) 1
        Check ($rejected.Out.Length -eq 0 -and $rejected.Err.Contains('UTF-8') -and (Fingerprint $invalidUtf8) -ceq $before) '损坏 UTF-8 没有明确拒绝，或改写了原文件字节、mtime、目录。'
    }
    $checks.Add('PASS_invalid_utf8_rejected_without_lossy_rewrite')

    $bomSave = Join-Path $run 'utf8-bom'
    Write-Fixture $bomSave 1700 20
    $bomPath = Join-Path $bomSave 'profile.json'
    [IO.File]::WriteAllBytes($bomPath, [byte[]](@(0xef, 0xbb, 0xbf) + [IO.File]::ReadAllBytes($bomPath)))
    $bomHistory = Invoke-History 'history-utf8-bom' $bomSave
    Check ($bomHistory.Out.Contains('1700 分')) 'UTF-8 BOM 合法档案无法查询。'
    $null = Invoke-Cli 'new-season-utf8-bom' @('--new-season', '--save-dir', $bomSave)
    $bomRating = (Read-Profile $bomSave).ratings.'默认'
    Check ($bomRating.season -eq 2 -and $bomRating.rating -eq 1600 -and (Placed $bomRating)) 'UTF-8 BOM 合法档案无法切季。'
    Assert-History $bomRating 0 1700 1700 20
    $checks.Add('PASS_valid_utf8_bom_history_and_new_season')

    $beforeLock = Fingerprint $highDirectory
    $held = [IO.File]::Open((Join-Path $highDirectory 'profile.json'), [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
    try { $null = Invoke-Cli 'history-io-locked' @('--rating-history', '--save-dir', $highDirectory) 1 }
    finally { $held.Dispose() }
    Check ((Fingerprint $highDirectory) -ceq $beforeLock) '查询读取失败改写了档案。'
    $held = [IO.File]::Open((Join-Path $highDirectory '.profile.lock'), [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try { $null = Invoke-Cli 'new-season-locked' @('--new-season', '--save-dir', $highDirectory) 1 }
    finally { $held.Dispose() }
    Check ((Fingerprint $highDirectory) -ceq $beforeLock) '切季持锁失败改写了档案。'
    $checks.Add('PASS_read_and_write_lock_errors_keep_files_unchanged')

    $hostSave = Join-Path $run 'network-host'
    $guestSave = Join-Path $run 'network-guest'
    Write-Fixture $hostSave 1700 20
    Write-Fixture $guestSave 1300 12
    $null = Invoke-Cli 'network-host-new-season' @('--new-season', '--save-dir', $hostSave)
    $null = Invoke-Cli 'network-guest-new-season' @('--new-season', '--save-dir', $guestSave)
    $beforeWire = Fingerprint $hostSave
    $wireHost = Start-Cli 'wire-hello' @('--host', '0', '--team', $hostTeam, '--save-dir', $hostSave)
    $wirePort = [int](Wait-Output $wireHost '(?m)^监听端口：([0-9]+)\r?$').Groups[1].Value
    $socket = [Net.Sockets.TcpClient]::new()
    $reader = $null
    try {
        $socket.Connect([Net.IPAddress]::Loopback, $wirePort)
        $stream = $socket.GetStream()
        $stream.ReadTimeout = 5000
        $reader = [IO.StreamReader]::new($stream, $utf8)
        $helloLine = $reader.ReadLine()
        Write-Text (Join-Path $run 'captured-hello.json') $helloLine
        $hello = $helloLine | ConvertFrom-Json -AsHashtable
        Check ((($hello.rating.Keys | Sort-Object) -join ',') -ceq 'games,rating') '真实 TCP hello 的评级包含赛季历史或其他新增字段。'
        Check ($hello.rating.games -eq 0 -and $hello.rating.rating -eq 1600) '真实 TCP hello 不是当前赛季分数与场数。'
    }
    finally { if ($null -ne $reader) { $reader.Dispose() }; $socket.Dispose() }
    $null = Wait-Cli $wireHost 1
    Check ((Fingerprint $hostSave) -ceq $beforeWire) '未完成的握手改变了赛季档案。'
    $checks.Add('PASS_real_tcp_hello_uses_only_legacy_rating_fields')

    $hostChild = Start-Cli 'network-game-host' @('--host', '0', '--auto', '--seed', '7', '--hash', '--team', $hostTeam, '--save-dir', $hostSave)
    $port = (Wait-Output $hostChild '(?m)^监听端口：([0-9]+)\r?$').Groups[1].Value
    $guestChild = Start-Cli 'network-game-guest' @('--join', "127.0.0.1:$port", '--auto', '--hash', '--team', $guestTeam, '--save-dir', $guestSave)
    $hostResult = Wait-Cli $hostChild
    $guestResult = Wait-Cli $guestChild
    foreach ($case in @(
        @{ Name = '房主'; Directory = $hostSave; Result = $hostResult; Before = 1600; Opponent = 1400; Old = 1700; OldGames = 20 },
        @{ Name = '客机'; Directory = $guestSave; Result = $guestResult; Before = 1400; Opponent = 1600; Old = 1300; OldGames = 12 }
    )) {
        $winner = [regex]::Match($case.Result.Out, '(?m)^对局结束：(房主 获胜|客机 获胜|平局)\r?$')
        Check $winner.Success '真实 TCP 完局没有胜负输出。'
        $score = if ($winner.Groups[1].Value -eq '平局') { 0.5 } elseif ($winner.Groups[1].Value -eq ($case.Name + ' 获胜')) { 1.0 } else { 0.0 }
        $expectedScore = 1.0 / (1.0 + [Math]::Pow(10.0, ($case.Opponent - $case.Before) / 400.0))
        $expected = $case.Before + [int][Math]::Round(24 * ($score - $expectedScore), [MidpointRounding]::AwayFromZero)
        $wrongK40 = $case.Before + [int][Math]::Round(40 * ($score - $expectedScore), [MidpointRounding]::AwayFromZero)
        $profile = Read-Profile $case.Directory
        $rating = $profile.ratings.'默认'
        Check ($expected -ne $wrongK40 -and $rating.rating -eq $expected -and $rating.games -eq 1 -and
            $rating.season -eq 2 -and (Placed $rating) -and (Peak $rating) -eq [Math]::Max($case.Before, $expected)) '已定级玩家的新赛季首局未使用 K24 或丢失赛季/峰值。'
        Check ($rating.history.Count -eq 1) '真实联网结算丢失或重复增加历史赛季。'
        Assert-History $rating 0 $case.Old $case.Old $case.OldGames
        Check ($profile.ratings.'A保留赛制'.rating -eq 1801 -and $profile.ratings.'Z保留赛制'.rating -eq 1601 -and
            ($profile.packs.original.wins + $profile.packs.original.losses + $profile.packs.original.draws) -eq 5) '联网赛季结算覆盖其他赛制或重复累计普通战绩。'
        Check (@(Get-ChildItem -LiteralPath (Join-Path $case.Directory 'replays') -File -Filter '*.json').Count -eq 1) '联网首局没有恰好保存一个回放。'
    }
    $hostHash = [regex]::Match($hostResult.Out, '(?m)^最终状态哈希：([0-9a-f]{16})\r?$').Groups[1].Value
    $guestHash = [regex]::Match($guestResult.Out, '(?m)^最终状态哈希：([0-9a-f]{16})\r?$').Groups[1].Value
    Check ($hostHash.Length -eq 16 -and $hostHash -ceq $guestHash) '评级赛季改变了双方对战一致性。'
    $checks.Add('PASS_real_tcp_new_season_first_game_k24_and_history_retained')
}
catch { $failure = $_.ToString() }
finally {
    foreach ($child in $children) {
        if ($child.Started -and -not $child.Process.HasExited) { $child.Process.Kill($true); [void]$child.Process.WaitForExit(5000) }
        if ($null -ne $child.OutTask) { [void]$child.OutTask.Wait(5000) }
        if ($null -ne $child.ErrTask) { [void]$child.ErrTask.Wait(5000) }
        if ($null -ne $child.OutStream) { $child.OutStream.Dispose() }
        if ($null -ne $child.ErrStream) { $child.ErrStream.Dispose() }
        $child.Process.Dispose()
    }
    $result = [ordered]@{ status = $(if ($null -eq $failure) { 'PASS' } else { 'FAIL' }); checks = @($checks.ToArray()); calls = @($calls.ToArray()); error = $failure }
    Write-Text (Join-Path $run 'report.json') ($result | ConvertTo-Json -Depth 10)
}
foreach ($check in $checks) { Write-Host $check }
Write-Host "证据目录：$run"
if ($null -ne $failure) { throw $failure }
