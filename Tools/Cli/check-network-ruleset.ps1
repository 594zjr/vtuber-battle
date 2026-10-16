#requires -Version 7.0
param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug')

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$binary = Join-Path $PSScriptRoot "bin/$Configuration/net8.0"
$cli = Join-Path $binary 'Cli.dll'
$core = Join-Path $binary 'StreamDuel.Core.dll'
foreach ($path in @($cli, $core)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "请先构建 Tools/Cli -c $Configuration；脚本不自动构建。" }
}
$run = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ('obj/network-ruleset-check/' + [Guid]::NewGuid().ToString('N'))))
if (-not $run.StartsWith($repository + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw '产物必须位于当前项目内。' }
$temporary = Join-Path $run 'temp'
[void][IO.Directory]::CreateDirectory($temporary)
$utf8 = [Text.UTF8Encoding]::new($false, $true)
$children = [Collections.Generic.List[object]]::new()
$checks = [Collections.Generic.List[string]]::new()
$failure = $null
$hostTeam = 'bell,enka,shiori,lumi,hane,momo'
$guestTeam = 'kei,yuan,nyako,buck,kasumi,bee'

function Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "$Message 证据目录：$run" }
}

function Passed([string]$Name) { $checks.Add($Name); Write-Output "PASS $Name" }

function Read-Log([string]$Path) {
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
    $reader = [IO.StreamReader]::new($stream, $utf8)
    try { return $reader.ReadToEnd() } finally { $reader.Dispose() }
}

function Start-Cli([string]$Name, [string[]]$Parameters) {
    $work = Join-Path $run $Name
    [void][IO.Directory]::CreateDirectory($work)
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = 'dotnet'
    $start.ArgumentList.Add($cli)
    foreach ($parameter in $Parameters) { $start.ArgumentList.Add($parameter) }
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
    $start.Environment['DOTNET_CLI_HOME'] = 'D:\vtuber-battle\.dotnet-home'
    $start.Environment['NUGET_PACKAGES'] = 'D:\vtuber-battle\.nuget\packages'
    $start.Environment['DOTNET_SKIP_FIRST_TIME_EXPERIENCE'] = '1'
    $start.Environment['DOTNET_CLI_TELEMETRY_OPTOUT'] = '1'
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    $child = [pscustomobject]@{
        Name = $Name; Process = $process; Started = $false; Result = $null; Killed = $false
        OutPath = (Join-Path $work 'stdout.log'); ErrPath = (Join-Path $work 'stderr.log')
        OutStream = $null; ErrStream = $null; OutTask = $null; ErrTask = $null
    }
    $children.Add($child)
    $child.OutStream = [IO.FileStream]::new($child.OutPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::ReadWrite, 1, [IO.FileOptions]::Asynchronous)
    $child.ErrStream = [IO.FileStream]::new($child.ErrPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::ReadWrite, 1, [IO.FileOptions]::Asynchronous)
    $child.Started = $process.Start()
    Check $child.Started "$Name 启动失败。"
    $child.OutTask = $process.StandardOutput.BaseStream.CopyToAsync($child.OutStream)
    $child.ErrTask = $process.StandardError.BaseStream.CopyToAsync($child.ErrStream)
    return $child
}

function Wait-Output($Child, [string]$Pattern, [int]$TimeoutMs = 15000) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    while ($timer.ElapsedMilliseconds -lt $TimeoutMs) {
        $match = [regex]::Match((Read-Log $Child.OutPath), $Pattern)
        if ($match.Success) { return $match }
        Check (-not $Child.Process.HasExited) "$($Child.Name) 在预期输出前退出。"
        Start-Sleep -Milliseconds 25
    }
    throw "$($Child.Name) 等待输出超时：$Pattern；证据目录：$run"
}

function Wait-Cli($Child, [int]$ExpectedExit = 0, [int]$TimeoutMs = 90000) {
    if ($null -eq $Child.Result) {
        $timer = [Diagnostics.Stopwatch]::StartNew()
        while (-not $Child.Process.HasExited -and $timer.ElapsedMilliseconds -lt $TimeoutMs) { Start-Sleep -Milliseconds 25 }
        Check $Child.Process.HasExited "$($Child.Name) 超时。"
        Check ($Child.OutTask.Wait(5000) -and $Child.ErrTask.Wait(5000)) "$($Child.Name) 输出未完成。"
        $Child.OutStream.Flush(); $Child.ErrStream.Flush()
        $Child.Result = [pscustomobject]@{ Exit = $Child.Process.ExitCode; Out = (Read-Log $Child.OutPath); Err = (Read-Log $Child.ErrPath) }
    }
    Check ($Child.Result.Exit -eq $ExpectedExit) "$($Child.Name) 退出码应为 $ExpectedExit，实际 $($Child.Result.Exit)。"
    if ($ExpectedExit -eq 0) { Check ([string]::IsNullOrWhiteSpace($Child.Result.Err)) "$($Child.Name) 成功时有错误输出。" }
    else { Check ($Child.Result.Err.Contains('错误：')) "$($Child.Name) 没有中文错误。" }
    return $Child.Result
}

function Send-Line($Child, [string]$Text) { $Child.Process.StandardInput.WriteLine($Text); $Child.Process.StandardInput.Flush() }

function Start-Pair([string]$Name, [string[]]$HostExtra, [string[]]$GuestExtra, [bool]$GuestAutomatic = $true) {
    $hostSave = Join-Path $run "$Name-host-save"
    $guestSave = Join-Path $run "$Name-guest-save"
    $hostArgs = @('--host', '0', '--seed', '128', '--team', $hostTeam, '--save-dir', $hostSave, '--auto', '--hash', '--report', 'match.md') + $HostExtra
    $hostChild = Start-Cli "$Name-host" $hostArgs
    $port = [int](Wait-Output $hostChild '(?m)^监听端口：([0-9]+)\r?$').Groups[1].Value
    $guestArgs = @('--join', "127.0.0.1:$port", '--seed', '999', '--team', $guestTeam, '--save-dir', $guestSave, '--hash', '--report', 'match.md') + $GuestExtra
    if ($GuestAutomatic) { $guestArgs += '--auto' }
    $guestChild = Start-Cli "$Name-guest" $guestArgs
    return [pscustomobject]@{ Host = $hostChild; Guest = $guestChild; HostSave = $hostSave; GuestSave = $guestSave; Port = $port }
}

function No-Save([string]$Directory) {
    Check (-not (Test-Path -LiteralPath $Directory)) '未确认整场创建了存档目录。'
}

function Games([string]$SaveDirectory) {
    $files = @(Get-ChildItem -LiteralPath (Join-Path $SaveDirectory 'replays') -File -Filter '*.json')
    return ,@($files | ForEach-Object {
        $record = [StreamDuel.Core.BattleRecord]::FromJson([IO.File]::ReadAllText($_.FullName, $utf8))
        [pscustomobject]@{ Record = $record; Battle = [StreamDuel.Core.Replay]::Run($db, $record) }
    } | Sort-Object { $_.Record.Seed })
}

function Assert-Series($Pair, [string]$Ruleset, [bool]$BothRuleset = $true) {
    $hostResult = Wait-Cli $Pair.Host
    $guestResult = Wait-Cli $Pair.Guest
    $hostGames = Games $Pair.HostSave
    $guestGames = Games $Pair.GuestSave
    $expectedCount = if ($Ruleset -eq 'ranked') { @(2, 3) } else { @(1) }
    Check ($hostGames.Count -in $expectedCount -and $hostGames.Count -eq $guestGames.Count) '两端保存的完整局数不一致。'
    $wins = @(0, 0); $draws = 0
    for ($i = 0; $i -lt $hostGames.Count; $i++) {
        $record = $hostGames[$i].Record
        $battle = $hostGames[$i].Battle
        Check ($record.ToJson() -ceq $guestGames[$i].Record.ToJson()) "第 $($i + 1) 局两端录像不同。"
        Check ($record.Seed -eq 128 + $i) '局种子不是房主初始种子加零起点局号。'
        $hash = $battle.StateHash().ToString('x16')
        Check ($battle.Phase.ToString() -eq 'Over' -and $hash -ceq $guestGames[$i].Battle.StateHash().ToString('x16')) '各局录像不能独立复算至相同终局。'
        Check ($hostResult.Out.Contains($hash) -and $guestResult.Out.Contains($hash)) '双方没有显示独立复算一致的终局哈希。'
        if ($Ruleset -eq 'ranked') {
            Check ($record.BanOne -and $record.LeadPick) '排位录像缺失禁用或首发。'
            Check (@($record.Events | Where-Object { $_.Type.ToString() -eq 'Ban' }).Count -eq 2) '每局必须各禁一人。'
            Check (@($record.Events | Where-Object { $_.Type.ToString() -eq 'Lead' }).Count -eq 2) '每局必须各选首发。'
        }
        elseif ($Ruleset -eq 'chaos') { Check (($battle.Modifiers -join ',') -ceq 'half_pp,hype_start') '乱斗修饰器没有进入录像复算。' }
        else { Check (-not $record.BanOne -and -not $record.LeadPick -and $battle.Modifiers.Count -eq 0) '旧协议休闲发生了规则变化。' }
        if ($battle.Winner -lt 0) { $draws++ } else { $wins[$battle.Winner]++ }
    }
    $winner = if ($wins[0] -eq $wins[1]) { -1 } elseif ($wins[0] -gt $wins[1]) { 0 } else { 1 }
    for ($side = 0; $side -lt 2; $side++) {
        $save = if ($side -eq 0) { $Pair.HostSave } else { $Pair.GuestSave }
        $expected = [StreamDuel.Core.Profile]::new()
        foreach ($game in $hostGames) { $expected.Record($game.Battle, $side) }
        $own = [StreamDuel.Core.PlayerRating]::new()
        $opponent = [StreamDuel.Core.PlayerRating]::new()
        $score = if ($winner -lt 0) { 0.5 } elseif ($winner -eq $side) { 1.0 } else { 0.0 }
        [StreamDuel.Core.RatingSystem]::Apply($own, $opponent, $score)
        $expected.Ratings[[StreamDuel.Core.Profile]::DefaultRatingFormat] = $own
        $actual = [StreamDuel.Core.Profile]::FromJson([IO.File]::ReadAllText((Join-Path $save 'profile.json'), $utf8))
        Check ($actual.ToJson() -ceq $expected.ToJson()) '普通统计未逐局累计或排位不是按整场结果仅结算一次。'
    }
    if ($BothRuleset) {
        foreach ($child in @($Pair.Host, $Pair.Guest)) {
            $out = $child.Result.Out
            Check ([regex]::Matches($out, '整场排位已结算一次：').Count -eq 1) '整场结算提示不是恰好一次。'
            Check ([regex]::Matches($out, '联网赛制第 [0-9]+ 局').Count -eq $hostGames.Count) '同连接局次展示缺失或重复。'
            $report = [IO.File]::ReadAllText((Join-Path (Split-Path $child.OutPath -Parent) 'match.md'), $utf8)
            Check ([regex]::Matches($report, '(?m)^## 第 [0-9]+ 局$').Count -eq $hostGames.Count) '汇总战报覆盖前局或漏局。'
            if ($Ruleset -eq 'ranked') { Check ([regex]::Matches($out, '计时已按赛制重置').Count -eq $hostGames.Count) '每局没有显示赛制计时重置。' }
        }
    }
}

try {
    $assembly = [Reflection.Assembly]::LoadFrom($core)
    Check ([StringComparer]::OrdinalIgnoreCase.Equals([IO.Path]::GetFullPath($assembly.Location), [IO.Path]::GetFullPath($core))) '请在未加载其他 Core 的新 PowerShell 进程运行。'
    $content = Join-Path $binary 'Content'
    $db = [StreamDuel.Core.ContentDb]::FromJson([IO.File]::ReadAllText((Join-Path $content 'StreamDuelRules.json'), $utf8),
        [IO.File]::ReadAllText((Join-Path $content 'Packs/original.json'), $utf8), [StreamDuel.Core.PackPolicy]::Load((Join-Path $content 'PackPolicy.json')))

    $invalid = @(
        @('--host', '0', '--ruleset', 'unknown'),
        @('--host', '0', '--ruleset', 'ranked', '--timer', '1,1'),
        @('--host', '0', '--ruleset', 'ranked', '--ban'),
        @('--join', '127.0.0.1:1', '--ruleset', 'ranked', '--lead'),
        @('--lan-host', '禁观战', '--ruleset', 'ranked', '--spectate-port', '0'),
        @('--host', '0', '--ruleset', 'ranked', '--spectate-port', '0', '--spectator-chat', 'on'),
        @('--host', '0', '--ruleset', 'casual', '--team-b', $guestTeam),
        @('--watch', '127.0.0.1:1', '--ruleset', 'ranked')
    )
    for ($i = 0; $i -lt $invalid.Count; $i++) {
        $save = Join-Path $run "invalid-save-$i"
        $child = Start-Cli "invalid-$i" ($invalid[$i] + @('--save-dir', $save))
        $child.Process.StandardInput.Close()
        $result = Wait-Cli $child 1 10000
        Check (-not $result.Out.Contains('监听端口：')) '不支持的组合在拒绝前打开了监听。'
        No-Save $save
    }
    Passed 'unsupported_options_rejected_before_connect_or_save'

    $pair = Start-Pair 'ranked' @('--ruleset', 'ranked') @('--ruleset', 'ranked')
    Assert-Series $pair 'ranked'
    Passed 'ranked_same_connection_all_games_hashes_openers_timer_rating_once'

    $pair = Start-Pair 'chaos' @('--ruleset', 'chaos') @('--ruleset', 'chaos')
    Assert-Series $pair 'chaos'
    Passed 'chaos_modifiers_both_peers_replayable'

    $pair = Start-Pair 'legacy-host' @() @('--ruleset', 'casual')
    Assert-Series $pair 'casual' $false
    $pair = Start-Pair 'legacy-guest' @('--ruleset', 'casual') @()
    Assert-Series $pair 'casual' $false
    Passed 'casual_legacy_compatibility_both_host_and_guest_directions'

    $pair = Start-Pair 'ruleset-mismatch' @('--ruleset', 'ranked') @('--ruleset', 'chaos')
    $hostResult = Wait-Cli $pair.Host 1
    $guestResult = Wait-Cli $pair.Guest 1
    Check (($hostResult.Err + $guestResult.Err).Contains('赛制不一致')) '不同赛制没有明确拒绝。'
    No-Save $pair.HostSave; No-Save $pair.GuestSave
    $pair = Start-Pair 'legacy-ranked-rejection' @('--ruleset', 'ranked') @()
    $null = Wait-Cli $pair.Host 1
    $null = Wait-Cli $pair.Guest 1
    No-Save $pair.HostSave; No-Save $pair.GuestSave
    Passed 'mismatched_ruleset_and_legacy_ranked_refused_without_saves'

    $pair = Start-Pair 'interrupted-series' @('--ruleset', 'ranked') @('--ruleset', 'ranked') $false
    $null = Wait-Output $pair.Guest '请选择要禁用的对手号位'
    Send-Line $pair.Guest '1'
    $null = Wait-Output $pair.Guest '请选择首发号位'
    Send-Line $pair.Guest '1'
    $null = Wait-Output $pair.Guest '选择行动（q 退出）>'
    Send-Line $pair.Guest '/surrender'
    $null = Wait-Output $pair.Guest '联网赛制第 2 局'
    No-Save $pair.HostSave; No-Save $pair.GuestSave
    $pair.Guest.Process.Kill($true)
    $pair.Guest.Killed = $true
    [void]$pair.Guest.Process.WaitForExit(5000)
    $interrupted = Wait-Cli $pair.Host 1
    Check ($interrupted.Err -match '断开|连接|接收') '中途断线未明确失败。'
    No-Save $pair.HostSave; No-Save $pair.GuestSave
    Check (-not (Test-Path -LiteralPath (Join-Path (Split-Path $pair.Host.OutPath -Parent) 'match.md'))) '中断整场导出了成功战报。'
    Passed 'disconnect_after_confirmed_game_keeps_entire_match_unrated'

    $roomName = '赛制验收' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
    $hostSave = Join-Path $run 'lan-host-save'
    $guestSave = Join-Path $run 'lan-guest-save'
    $hostChild = Start-Cli 'lan-host' @('--lan-host', $roomName, '--ruleset', 'ranked', '--seed', '128', '--team', $hostTeam,
        '--save-dir', $hostSave, '--auto', '--report', 'match.md')
    $port = [int](Wait-Output $hostChild '(?m)^监听端口：([0-9]+)\r?$').Groups[1].Value
    $listing = Start-Cli 'lan-list' @('--lan-list')
    $listed = Wait-Cli $listing 0 15000
    Check ($listed.Out.Contains($roomName) -and $listed.Out.Contains('赛制：排位；最多 3 局') -and
        $listed.Out.Contains("--join ") -and $listed.Out.Contains("`:$port --ruleset ranked")) '实际局域网发现未展示真实赛制、局数和加入参数。'
    $guestChild = Start-Cli 'lan-guest' @('--join', "127.0.0.1:$port", '--ruleset', 'ranked', '--team', $guestTeam,
        '--save-dir', $guestSave, '--auto', '--report', 'match.md')
    Assert-Series ([pscustomobject]@{Host=$hostChild;Guest=$guestChild;HostSave=$hostSave;GuestSave=$guestSave}) 'ranked'
    Passed 'lan_actual_broadcast_ruleset_name_bestof_and_join'
}
catch { $failure = $_.Exception.ToString(); throw }
finally {
    foreach ($child in $children) {
        if ($child.Started -and -not $child.Process.HasExited) { $child.Process.Kill($true); $child.Killed = $true; [void]$child.Process.WaitForExit(5000) }
        if ($null -ne $child.OutTask) { [void]$child.OutTask.Wait(5000) }
        if ($null -ne $child.ErrTask) { [void]$child.ErrTask.Wait(5000) }
        if ($null -ne $child.OutStream) { $child.OutStream.Dispose() }
        if ($null -ne $child.ErrStream) { $child.ErrStream.Dispose() }
        $child.Process.Dispose()
    }
    $summary = [ordered]@{ passed=($null -eq $failure); checks=$checks.ToArray(); processes=$children.Count; failure=$failure; directory=$run }
    [IO.File]::WriteAllText((Join-Path $run 'result.json'), ($summary | ConvertTo-Json -Depth 8), $utf8)
    Write-Output "证据目录：$run"
}
