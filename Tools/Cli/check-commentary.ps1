#requires -Version 7.0
param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$runDirectory = Join-Path $repository ('Tools/Cli/obj/commentary-check/' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($runDirectory)
$binaryDirectory = Join-Path $runDirectory 'binary'
# 独立副本避免验收进程锁住其他任务的构建输出。
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "bin/$Configuration/net8.0") -Destination $binaryDirectory -Recurse
$cliDll = Join-Path $binaryDirectory 'Cli.dll'
$utf8 = [Text.UTF8Encoding]::new($false)
$children = [Collections.Generic.List[object]]::new()
$checks = [Collections.Generic.List[string]]::new()
$failure = $null
$team = 'bell,enka,shiori,lumi,hane,momo'
$foe = 'kei,yuan,nyako,buck,kasumi,bee'
function Assert-Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "$Message 证据目录：$runDirectory" }
}
function Read-Log([string]$Path) {
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
    $reader = [IO.StreamReader]::new($stream, $utf8)
    try { return $reader.ReadToEnd() }
    finally { $reader.Dispose() }
}

function Start-Cli([string]$Name, [string[]]$Parameters) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = 'dotnet'
    $start.ArgumentList.Add($cliDll)
    foreach ($parameter in $Parameters) { $start.ArgumentList.Add($parameter) }
    $working = Join-Path $runDirectory $Name
    [void][IO.Directory]::CreateDirectory($working)
    $start.WorkingDirectory = $working
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = $utf8
    $start.StandardErrorEncoding = $utf8
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    $child = [pscustomobject]@{
        Name = $Name; WorkingDirectory = $working; Parameters = $Parameters; Process = $process; Started = $false
        OutPath = (Join-Path $runDirectory "$Name.stdout.log")
        ErrPath = (Join-Path $runDirectory "$Name.stderr.log")
        OutStream = $null; ErrStream = $null; OutTask = $null; ErrTask = $null
        Result = $null; Killed = $false; CleanupError = $null
    }
    $children.Add($child)
    $child.OutStream = [IO.FileStream]::new($child.OutPath, [IO.FileMode]::CreateNew,
        [IO.FileAccess]::Write, [IO.FileShare]::ReadWrite, 1, [IO.FileOptions]::Asynchronous)
    $child.ErrStream = [IO.FileStream]::new($child.ErrPath, [IO.FileMode]::CreateNew,
        [IO.FileAccess]::Write, [IO.FileShare]::ReadWrite, 1, [IO.FileOptions]::Asynchronous)
    Assert-Check ($process.Start()) "$Name 无法启动。"
    $child.Started = $true
    # 异步复制原始流能看到没有换行的输入提示，也不依赖 PowerShell 事件回调的 runspace。
    $child.OutTask = $process.StandardOutput.BaseStream.CopyToAsync($child.OutStream)
    $child.ErrTask = $process.StandardError.BaseStream.CopyToAsync($child.ErrStream)
    # stdin 必须保持打开，才能证明等待人工输入时无需 EOF 或回车也会响应断线。
    return $child
}

function Wait-Output($Child, [string]$Pattern, [int]$TimeoutMs = 15000) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    while ($timer.ElapsedMilliseconds -lt $TimeoutMs) {
        $match = [regex]::Match((Read-Log $Child.OutPath), $Pattern)
        if ($match.Success) { return $match }
        Assert-Check (-not $Child.Process.HasExited) "$($Child.Name) 在出现预期输出前退出，请检查 stdout/stderr 日志。"
        Start-Sleep -Milliseconds 25
    }
    throw "$($Child.Name) 等待输出超时。证据目录：$runDirectory"
}

function Wait-Cli($Child, [int]$ExpectedExit, [int]$TimeoutMs = 60000) {
    if ($null -eq $Child.Result) {
        $timer = [Diagnostics.Stopwatch]::StartNew()
        while (-not $Child.Process.HasExited -and $timer.ElapsedMilliseconds -lt $TimeoutMs) {
            Start-Sleep -Milliseconds 25
        }
        Assert-Check ($Child.Process.HasExited) "$($Child.Name) 超过 $TimeoutMs 毫秒仍未退出。"
        Assert-Check ($Child.OutTask.Wait(5000)) "$($Child.Name) 的标准输出未结束。"
        Assert-Check ($Child.ErrTask.Wait(5000)) "$($Child.Name) 的标准错误未结束。"
        $Child.OutStream.Flush()
        $Child.ErrStream.Flush()
        $Child.Result = [pscustomobject]@{
            ExitCode = $Child.Process.ExitCode
            Out = (Read-Log $Child.OutPath)
            Err = (Read-Log $Child.ErrPath)
        }
    }
    Assert-Check ($Child.Result.ExitCode -eq $ExpectedExit) "$($Child.Name) 退出码应为 $ExpectedExit，实际为 $($Child.Result.ExitCode)。"
    return $Child.Result
}


function Comment-Lines([string]$Output) {
    return @([regex]::Matches($Output, '【解说 · 第 (?<turn>[0-9]+) 回合】(?<text>[^\r\n]*)'))
}
function Assert-Commentary([string]$Output, $Battle, [int]$ThroughTurn = [int]::MaxValue) {
    $display = [StreamDuel.Core.TextInput]::ForDisplay($Battle)
    $expected = @([StreamDuel.Core.Commentary]::For($display, 0, $display.Events.Count) |
        Where-Object { $display.Events[$_.EventIndex].Turn -le $ThroughTurn } |
        ForEach-Object { '【解说 · 第 ' + $display.Events[$_.EventIndex].Turn + ' 回合】' + $_.Text })
    $lines = @(Comment-Lines $Output)
    Assert-Check ($lines.Count -eq $expected.Count -and $lines.Count -gt 0) '解说数量与公开 Core 结果不一致或没有解说。'
    for ($i = 0; $i -lt $expected.Count; $i++) {
        Assert-Check ($lines[$i].Value -ceq $expected[$i]) "第 $($i + 1) 条解说重复、错序或文案不一致。"
        Assert-Check (-not $lines[$i].Groups['text'].Value.Contains('{')) '解说有未替换占位符。'
    }
    foreach ($group in ($lines | Group-Object { $_.Groups['turn'].Value })) {
        Assert-Check ($group.Count -le 2) '同一回合输出超过两条解说。'
    }
}
function Saved-Battle($Child) {
    $files = @(Get-ChildItem -LiteralPath (Join-Path $Child.WorkingDirectory 'saves/replays') -File -Filter '*.json')
    Assert-Check ($files.Count -eq 1) '完成对局没有恰好一份回放。'
    $record = [StreamDuel.Core.BattleRecord]::FromJson([IO.File]::ReadAllText($files[0].FullName, $utf8))
    return [pscustomobject]@{ Path = $files[0].FullName; Record = $record; Battle = [StreamDuel.Core.Replay]::Run($db, $record) }
}
function Send-Line($Child, [string]$Line) {
    $Child.Process.StandardInput.WriteLine($Line)
    $Child.Process.StandardInput.Flush()
}
try {
    $core = [Reflection.Assembly]::LoadFrom((Join-Path $binaryDirectory 'StreamDuel.Core.dll'))
    $cli = [Reflection.Assembly]::LoadFrom($cliDll)
    Assert-Check ([IO.Path]::GetFullPath($cli.Location) -eq [IO.Path]::GetFullPath($cliDll)) '请在新的 PowerShell 进程执行此脚本。'
    $db = [StreamDuel.Core.ContentDb]::FromJson(
        [IO.File]::ReadAllText((Join-Path $binaryDirectory 'Content/StreamDuelRules.json'), $utf8),
        [IO.File]::ReadAllText((Join-Path $binaryDirectory 'Content/Packs/original.json'), $utf8))

    $local = Start-Cli 'local' @('--auto', '--seed', '7', '--team', $team)
    $localResult = Wait-Cli $local 0
    $saved = Saved-Battle $local
    Assert-Check ($saved.Battle.Phase -eq [StreamDuel.Core.Phase]::Over -and $saved.Battle.Turn -gt 2) '普通局未覆盖多回合。'
    Assert-Commentary $localResult.Out $saved.Battle
    $checks.Add('普通局多回合解说逐条等于 Core，回合最多两条且无重复')

    $print = $cli.GetType('StreamDuel.Cli.Program').GetMethod('PrintNewLog', [Reflection.BindingFlags]'NonPublic,Static')
    Assert-Check ($null -ne $print) '缺少真实 CLI 日志输出入口。'
    $beforeHash = $saved.Battle.StateHash()
    $beforeRecord = $saved.Battle.Record.ToJson()
    $beforeRng = $saved.Battle.Clone().Random()
    $writer = [IO.StringWriter]::new()
    $previous = [Console]::Out
    try {
        [Console]::SetOut($writer)
        [void]$print.Invoke($null, [object[]]@($saved.Battle, 0, $null))
        [void]$print.Invoke($null, [object[]]@($saved.Battle, 0, $null))
    }
    finally { [Console]::SetOut($previous) }
    Assert-Commentary $writer.ToString() $saved.Battle
    $writer.Dispose()
    Assert-Check ($saved.Battle.StateHash() -eq $beforeHash -and $saved.Battle.Record.ToJson() -ceq $beforeRecord -and
        $saved.Battle.Clone().Random() -eq $beforeRng) '显示解说改变状态、录像或随机流。'
    $checks.Add('真实 PrintNewLog 重置日志游标重复刷新仍只输出一份解说，不改变原始对局')

    $replay = Start-Cli 'replay-auto' @('--replay', $saved.Path, '--auto')
    $replayResult = Wait-Cli $replay 0
    Assert-Commentary $replayResult.Out $saved.Battle
    $partial = Start-Cli 'replay-quit' @('--replay', $saved.Path)
    Send-Line $partial 'invalid'
    Send-Line $partial 'q'
    $partialResult = Wait-Cli $partial 0
    Assert-Commentary $partialResult.Out $saved.Battle ([int]$saved.Battle.Log[0].Turn)
    Assert-Check ($partialResult.Out.Contains('已退出回看。') -and -not $partialResult.Out.Contains('回看结束：') -and
        -not (Test-Path -LiteralPath (Join-Path $partial.WorkingDirectory 'saves'))) '提前回看泄露终局或写入存档。'
    $checks.Add('完整回看解说一致；首回合无效刷新再q只有当前页解说且不落盘')

    $series = Start-Cli 'ruleset' @('--ruleset', 'ranked', '--auto', '--seed', '7', '--team', $team)
    $seriesResult = Wait-Cli $series 0
    $games = @(Get-ChildItem -LiteralPath (Join-Path $series.WorkingDirectory 'saves/replays') -File -Filter '*.json' | Sort-Object Name)
    $pages = [regex]::Matches($seriesResult.Out, '(?s)===== 赛制第 ([0-9]+) 局 · 种子 ([0-9]+) =====(.*?)(?====== 赛制第 |\z)')
    Assert-Check ($games.Count -ge 2 -and $pages.Count -eq $games.Count) '赛制未覆盖多个独立 Battle。'
    for ($i = 0; $i -lt $games.Count; $i++) {
        $record = [StreamDuel.Core.BattleRecord]::FromJson([IO.File]::ReadAllText($games[$i].FullName, $utf8))
        Assert-Check ($record.Seed.ToString() -ceq $pages[$i].Groups[2].Value) '赛制回放与输出局序不一致。'
        Assert-Commentary $pages[$i].Groups[3].Value ([StreamDuel.Core.Replay]::Run($db, $record))
    }
    $checks.Add('同进程多局赛制的各 Battle 独立计数，分别与各自回放逐条一致')

    $hotseat = Start-Cli 'hotseat' @('--hotseat', '--seed', '7', '--team', $team, '--team-b', $foe, '--name-a', "甲`u{200b}玩家", '--name-b', "乙`u{200b}玩家")
    [void](Wait-Output $hotseat '选择行动（私密输入，q 退出）>')
    Send-Line $hotseat '1'
    [void](Wait-Output $hotseat '请把电脑交给「乙玩家」。')
    Assert-Check (@(Comment-Lines (Read-Log $hotseat.OutPath)).Count -eq 0) '第一方私密提交泄露未结算的解说。'
    Send-Line $hotseat ''
    Send-Line $hotseat '1'
    [void](Wait-Output $hotseat '===== 第 2 回合 · 乙玩家 操作 =====')
    $publicComments = (@(Comment-Lines (Read-Log $hotseat.OutPath)) | ForEach-Object Value) -join "`n"
    Assert-Check ($publicComments.Length -gt 0) '完成首回合后没有解说。'
    Send-Line $hotseat '1'
    [void](Wait-Output $hotseat '(?s)===== 第 2 回合 · 乙玩家 操作 =====.*请把电脑交给「甲玩家」。')
    Assert-Check (((@(Comment-Lines (Read-Log $hotseat.OutPath)) | ForEach-Object Value) -join "`n") -ceq $publicComments) '第二回合第一方提交重复或提前输出解说。'
    Send-Line $hotseat ''
    Send-Line $hotseat '/surrender'
    $hotseatResult = Wait-Cli $hotseat 0
    $hotseatSaved = Saved-Battle $hotseat
    Assert-Commentary $hotseatResult.Out $hotseatSaved.Battle
    $hotseatLines = (@(Comment-Lines $hotseatResult.Out) | ForEach-Object Value) -join "`n"
    Assert-Check ($hotseatLines.Contains('乙玩家') -and -not $hotseatLines.Contains("乙`u{200b}玩家")) '胜方显示名未经过文字清理。'
    $checks.Add('同屏两个私密半回合均无新解说；交接不重播，投降终局准确且玩家名已清理')

    $hostChild = Start-Cli 'network-host' @('--host', '0', '--spectate-port', '0', '--auto', '--team', $team)
    $port = (Wait-Output $hostChild '(?m)^监听端口：([0-9]+)\r?$').Groups[1].Value
    $watchPort = (Wait-Output $hostChild '(?m)^观战端口：([0-9]+)\r?$').Groups[1].Value
    $watch = Start-Cli 'spectator' @('--watch', "127.0.0.1:$watchPort")
    [void](Wait-Output $watch '已连接观战')
    $guest = Start-Cli 'network-guest' @('--join', "127.0.0.1:$port", '--auto', '--team', $foe)
    $hostResult = Wait-Cli $hostChild 0
    $guestResult = Wait-Cli $guest 0
    $watchResult = Wait-Cli $watch 0
    $hostSaved = Saved-Battle $hostChild
    $guestSaved = Saved-Battle $guest
    Assert-Check ($hostSaved.Record.ToJson() -ceq $guestSaved.Record.ToJson()) '联网双方记录不一致。'
    Assert-Commentary $hostResult.Out $hostSaved.Battle
    Assert-Commentary $guestResult.Out $guestSaved.Battle
    Assert-Commentary $watchResult.Out $hostSaved.Battle
    Assert-Check ($watchResult.Out.Contains('观战结束：') -and -not (Test-Path -LiteralPath (Join-Path $watch.WorkingDirectory 'saves'))) '观战未完成或产生存档。'
    $visibleTurn = 0
    foreach ($line in ($watchResult.Out -split "`n")) {
        $logTurn = [regex]::Match($line, '^【第 ([0-9]+) 回合 · ')
        if ($logTurn.Success) { $visibleTurn = [Math]::Max($visibleTurn, [int]$logTurn.Groups[1].Value) }
        foreach ($comment in @(Comment-Lines $line)) {
            Assert-Check ([int]$comment.Groups['turn'].Value -le $visibleTurn) '观战解说领先当前可见延迟日志。'
        }
    }
    $checks.Add('真实TCP主客与延迟观战解说均逐条匹配各自终局，替换帧不重播且不领先可见日志')
}
catch { $failure = $_.Exception.GetBaseException().Message }
finally {
    foreach ($child in $children) {
        try {
            if ($child.Started -and -not $child.Process.HasExited) { $child.Process.Kill($true); [void]$child.Process.WaitForExit(5000) }
            if ($child.Started) { [void]$child.OutTask.Wait(5000); [void]$child.ErrTask.Wait(5000) }
            $child.OutStream.Dispose(); $child.ErrStream.Dispose(); $child.Process.Dispose()
        }
        catch { if ($null -eq $failure) { $failure = '进程清理失败：' + $_.Exception.Message } }
    }
    $report = [ordered]@{ status = $(if ($null -eq $failure) { 'PASS' } else { 'FAIL' }); failure = $failure; checks = $checks.ToArray(); directory = $runDirectory }
    [IO.File]::WriteAllText((Join-Path $runDirectory 'result.json'), ($report | ConvertTo-Json -Depth 8), $utf8)
}
if ($null -ne $failure) { throw "$failure 证据目录：$runDirectory" }
Write-Output "PASS：$($checks.Count) 组解说 CLI 验收；证据目录：$runDirectory"
