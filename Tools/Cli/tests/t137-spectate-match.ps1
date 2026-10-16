#requires -Version 7.0
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$repositoryPrefix = $repository.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$current = [IO.Path]::GetFullPath((Get-Location).Path)
if ($current -ne $repository -and -not $current.StartsWith($repositoryPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw '请在本仓库内运行此脚本。'
}
$cliDll = Join-Path $PSScriptRoot "../bin/$Configuration/net8.0/Cli.dll"
if (-not (Test-Path -LiteralPath $cliDll -PathType Leaf)) { throw '请先构建 Tools/Cli。' }
$runDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ('../obj/T137/' + [Guid]::NewGuid().ToString('N'))))
if (-not $runDirectory.StartsWith($repositoryPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw '产物必须位于仓库内。' }
[void][IO.Directory]::CreateDirectory($runDirectory)
$utf8 = [Text.UTF8Encoding]::new($false)
$children = [Collections.Generic.List[object]]::new()
$checks = [Collections.Generic.List[string]]::new()
$hashes = [Collections.Generic.List[string]]::new()
$cleanupFailures = [Collections.Generic.List[string]]::new()
$hostTeam = 'bell,enka,shiori,lumi,hane,momo'
$guestTeam = 'kei,yuan,nyako,buck,kasumi,bee'
$failure = $null

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
    $workingDirectory = Join-Path $runDirectory $Name
    [void][IO.Directory]::CreateDirectory($workingDirectory)
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = 'dotnet'
    $start.ArgumentList.Add($cliDll)
    foreach ($parameter in $Parameters) { $start.ArgumentList.Add($parameter) }
    $start.WorkingDirectory = $workingDirectory
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardInputEncoding = $utf8
    $start.StandardOutputEncoding = $utf8
    $start.StandardErrorEncoding = $utf8
    $start.Environment['TEMP'] = $runDirectory
    $start.Environment['TMP'] = $runDirectory
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    $child = [pscustomobject]@{
        Name = $Name; Process = $process; WorkingDirectory = $workingDirectory; Started = $false
        OutPath = (Join-Path $runDirectory "$Name.stdout.log"); ErrPath = (Join-Path $runDirectory "$Name.stderr.log")
        OutStream = $null; ErrStream = $null; OutTask = $null; ErrTask = $null; Result = $null
    }
    $children.Add($child)
    $child.OutStream = [IO.FileStream]::new($child.OutPath, [IO.FileMode]::CreateNew,
        [IO.FileAccess]::Write, [IO.FileShare]::ReadWrite, 1, [IO.FileOptions]::Asynchronous)
    $child.ErrStream = [IO.FileStream]::new($child.ErrPath, [IO.FileMode]::CreateNew,
        [IO.FileAccess]::Write, [IO.FileShare]::ReadWrite, 1, [IO.FileOptions]::Asynchronous)
    Assert-Check ($process.Start()) "$Name 无法启动。"
    $child.Started = $true
    $child.OutTask = $process.StandardOutput.BaseStream.CopyToAsync($child.OutStream)
    $child.ErrTask = $process.StandardError.BaseStream.CopyToAsync($child.ErrStream)
    return $child
}

function Complete-Output($Child) {
    Assert-Check ([Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($Child.OutTask, $Child.ErrTask), 5000)) "$($Child.Name) 输出复制未完成。"
    $Child.OutStream.Flush()
    $Child.ErrStream.Flush()
}

function Wait-Output($Child, [string]$Pattern, [int]$Count = 1, [int]$TimeoutMs = 15000) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    while ($timer.ElapsedMilliseconds -lt $TimeoutMs) {
        $matches = [regex]::Matches((Read-Log $Child.OutPath), $Pattern)
        if ($matches.Count -ge $Count) { return $matches[$Count - 1] }
        if ($Child.Process.HasExited) {
            # 进程退出先于异步输出复制完成，排空后再检查，避免把完整输出误判为缺失。
            Complete-Output $Child
            $matches = [regex]::Matches((Read-Log $Child.OutPath), $Pattern)
            if ($matches.Count -ge $Count) { return $matches[$Count - 1] }
            throw "$($Child.Name) 已退出但缺少预期输出。证据目录：$runDirectory"
        }
        Start-Sleep -Milliseconds 20
    }
    throw "$($Child.Name) 等待输出超时。证据目录：$runDirectory"
}

function Wait-Cli($Child, [int]$ExpectedExit) {
    Assert-Check ($Child.Process.WaitForExit(30000)) "$($Child.Name) 30 秒内未退出。"
    Complete-Output $Child
    $Child.Result = [pscustomobject]@{
        Exit = $Child.Process.ExitCode; Out = (Read-Log $Child.OutPath); Err = (Read-Log $Child.ErrPath)
    }
    Assert-Check ($Child.Result.Exit -eq $ExpectedExit) "$($Child.Name) 应退出 $ExpectedExit，实际 $($Child.Result.Exit)。"
    return $Child.Result
}

function Send-Lines($Child, [string[]]$Lines) {
    foreach ($line in $Lines) { $Child.Process.StandardInput.WriteLine($line) }
    $Child.Process.StandardInput.Flush()
}

function Final-Hash([string]$Text) {
    $matches = [regex]::Matches($Text, '(?m)^最终状态哈希：([0-9a-f]{16})\r?$')
    Assert-Check ($matches.Count -eq 1) '完成进程没有且仅有一个最终哈希。'
    return $matches[0].Groups[1].Value
}

function Chat-Count([string]$Text) {
    return [regex]::Matches($Text, '(?m)^观众「[^\r\n]*」：[^\r\n]*\r?$').Count
}

function Assert-NoSave($Child) {
    Assert-Check (-not (Test-Path -LiteralPath (Join-Path $Child.WorkingDirectory 'saves'))) '观战或参数检查创建了默认存档目录。'
    Assert-Check (@(Get-ChildItem -LiteralPath $Child.WorkingDirectory -File -Recurse -Force).Count -eq 0) '只读进程写入了工作目录文件。'
}

function Read-Replays($Child) {
    $directory = Join-Path $Child.WorkingDirectory 'saves/replays'
    return @(Get-ChildItem -LiteralPath $directory -File -Filter '*.json' | ForEach-Object {
        $json = [IO.File]::ReadAllText($_.FullName)
        [pscustomobject]@{ Json = $json; Record = [StreamDuel.Core.BattleRecord]::FromJson($json) }
    } | Sort-Object { $_.Record.Seed })
}

function Start-Series([string]$Name, [bool]$Publish = $true) {
    $parameters = @('--ruleset', 'ranked', '--seed', '137', '--team', $hostTeam)
    if ($Publish) { $parameters += @('--spectate-port', '0') }
    return Start-Cli $Name $parameters
}

function Wait-Game($Child, [int]$Game) {
    $null = Wait-Output $Child ('===== 赛制第 ' + $Game + ' 局')
    $null = Wait-Output $Child '请选择要禁用的对手号位（q 退出）> ' $Game
}

try {
    $binary = Split-Path -Parent $cliDll
    [void][Reflection.Assembly]::LoadFrom((Join-Path $binary 'StreamDuel.Core.dll'))
    $resources = Join-Path $repository 'UnityProject/Assets/StreamDuel/Resources'
    $rules = [IO.File]::ReadAllText((Join-Path $resources 'StreamDuelRules.json'))
    $pack = [IO.File]::ReadAllText((Join-Path $resources 'Packs/original.json'))
    $db = [StreamDuel.Core.ContentDb]::FromJson($rules, $pack)

    $hostChild = Start-Series 'series-host'
    Wait-Game $hostChild 1
    $port = [int](Wait-Output $hostChild '(?m)^观战端口：([0-9]+)\r?\n').Groups[1].Value
    $address = "127.0.0.1:$port"
    $early = Start-Cli 'early' @('--watch', $address, '--hash', '--watch-name', '早到观众')
    $null = Wait-Output $early '(?m)^第 1 局 比分 0:0\r?$'
    Send-Lines $early @('/say 0', '/say 1', '/say 2', '/say 3')
    $null = Wait-Output $early '观众快捷语已提交：' 3
    $null = Wait-Output $early '观众快捷语未发送：'
    Send-Lines $hostChild @('/draw')
    Wait-Game $hostChild 2
    $null = Wait-Output $early '(?m)^第 1 局结束：平局；比分 0:0；平局 1\r?$'
    $null = Wait-Output $early '(?m)^第 2 局 比分 0:0\r?$'

    $late = Start-Cli 'late' @('--watch', $address, '--hash', '--watch-name', '晚到观众')
    $null = Wait-Output $late '(?m)^第 2 局 比分 0:0\r?$'
    Send-Lines $early @('/say 6')
    $null = Wait-Output $late '(?m)^观众「早到观众」：谢谢\r?$'
    $early.Process.StandardInput.Close()
    $late.Process.StandardInput.Close()
    $quit = Start-Cli 'quit-viewer' @('--watch', $address)
    $null = Wait-Output $quit '(?m)^第 2 局 比分 0:0\r?$'
    Send-Lines $quit @('q')
    $quitResult = Wait-Cli $quit 0
    Assert-Check ($quitResult.Out.Contains('已退出观战。') -and -not $quitResult.Out.Contains('观战比赛结束：')) '观众 q 必须独立退出，不能伪造整场完成。'
    Assert-NoSave $quit

    Send-Lines $hostChild @('/surrender')
    Wait-Game $hostChild 3
    foreach ($viewer in @($early, $late)) {
        $null = Wait-Output $viewer '(?m)^第 2 局结束：电脑 获胜；比分 0:1；平局 1\r?$'
        $null = Wait-Output $viewer '(?m)^第 3 局 比分 0:1\r?$'
    }
    Send-Lines $hostChild @('/surrender')
    $hostChild.Process.StandardInput.Close()
    $hostResult = Wait-Cli $hostChild 0
    Assert-Check ($hostResult.Out.Contains('赛制结束：共 3 局；你 0 : 2 电脑；平局 1')) '真实发布端必须完成三局及正确比分。'
    $games = @(Read-Replays $hostChild)
    Assert-Check ($games.Count -eq 3) '每一实际完成局必须仍保存原回放。'
    $profile = [IO.File]::ReadAllText((Join-Path $hostChild.WorkingDirectory 'saves/profile.json')) | ConvertFrom-Json
    Assert-Check ($profile.packs.original.losses -eq 2 -and $profile.packs.original.draws -eq 1) '主机每局普通战绩正确累计。'
    foreach ($viewer in @($early, $late)) {
        $result = Wait-Cli $viewer 0
        Assert-Check ($result.Err.Length -eq 0 -and $result.Out.Contains('观战比赛结束：最终比分 0:2；平局 1')) '观众 EOF 必须等到明确整场结束，且没有错误。'
        $first = if ($viewer.Name -eq 'early') { 1 } else { 2 }
        Assert-Check ([regex]::Matches($result.Out, '(?m)^观战开始；种子：').Count -eq (4 - $first)) '能力协商不能重复显示同局开场。'
        Assert-Check ([regex]::Matches($result.Out, '(?m)^观众昵称：').Count -eq 1) '能力协商不能重复显示聊天菜单。'
        Assert-Check ([regex]::Matches($result.Out, '(?m)^第 \d+ 局结束：').Count -eq (4 - $first)) '观众必须只完整展示其加入后的每局结果，不能遗漏或重复。'
        for ($game = $first; $game -le 3; $game++) {
            $replayed = [StreamDuel.Core.Replay]::Run($db, $games[$game - 1].Record)
            $hash = $replayed.StateHash().ToString('x16')
            Assert-Check ($result.Out.Contains("第 $game 局最终状态哈希：$hash")) '观众逐局终态必须等于主机原录像独立重演结果。'
            $hashes.Add($hash)
        }
        Assert-NoSave $viewer
    }
    Assert-Check (-not $late.Result.Out.Contains('第 1 局 比分') -and -not $late.Result.Out.Contains('第 1 局结束：')) '晚加入不能伪造未收到的前一局完整过程。'
    $checks.Add('真实 BO3 连续三局、开局及结果比分、晚加入当前局、逐局独立重演哈希、EOF 完整收尾与观众零存档')
    $checks.Add('跨局聊天配额重置、真实转发、观众 q 不影响其他观众及比赛')

    $baseline = Start-Series 'no-viewers' $false
    for ($game = 1; $game -le 3; $game++) {
        Wait-Game $baseline $game
        Send-Lines $baseline @($(if ($game -eq 1) { '/draw' } else { '/surrender' }))
    }
    $baseline.Process.StandardInput.Close()
    $null = Wait-Cli $baseline 0
    $plain = @(Read-Replays $baseline)
    Assert-Check ($plain.Count -eq 3) '无观战基线完成相同三局。'
    for ($game = 0; $game -lt 3; $game++) {
        Assert-Check ($plain[$game].Json -ceq $games[$game].Json) '有观众及聊天不能改变同输入原录像的任何字节。'
    }
    $checks.Add('启用真实观众及聊天与无观战基线三份录像逐字节相同')

    $aborted = Start-Series 'aborted-host'
    Wait-Game $aborted 1
    $abortPort = [int](Wait-Output $aborted '(?m)^观战端口：([0-9]+)\r?\n').Groups[1].Value
    $abortedViewer = Start-Cli 'aborted-viewer' @('--watch', "127.0.0.1:$abortPort")
    $null = Wait-Output $abortedViewer '(?m)^第 1 局 比分 0:0\r?$'
    Send-Lines $aborted @('/surrender')
    Wait-Game $aborted 2
    $null = Wait-Output $abortedViewer '(?m)^第 2 局 比分 0:1\r?$'
    Send-Lines $aborted @('q')
    $null = Wait-Cli $aborted 0
    $abortedResult = Wait-Cli $abortedViewer 1
    Assert-Check ($abortedResult.Err.Contains('观战失败：') -and -not $abortedResult.Out.Contains('观战比赛结束：')) '主机提前退出必须是未完成观战，不能冒充整场结束。'
    Assert-Check (@(Read-Replays $aborted).Count -eq 1) '提前退出仍仅保存此前完整局。'
    Assert-NoSave $abortedViewer
    $checks.Add('主机跨局 q 关闭未完成流，观众报错且不伪造最终比分，已完成局保留')

    $extras = @(
        @('--host', '0'), @('--join', '127.0.0.1:9'), @('--watch', '127.0.0.1:9'),
        @('--hotseat'), @('--timer', '1,1'), @('--replay', 'missing.json')
    )
    $case = 0
    foreach ($extra in $extras) {
        foreach ($reverse in @($false, $true)) {
            $baseArgs = @('--ruleset', 'ranked', '--spectate-port', '0')
            $parameters = if ($reverse) { @($extra) + $baseArgs } else { $baseArgs + @($extra) }
            $invalid = Start-Cli "invalid-$case" $parameters
            $case++
            $invalid.Process.StandardInput.Close()
            $result = Wait-Cli $invalid 1
            Assert-Check ($result.Err.Contains('错误：') -and -not $result.Out.Contains('观战端口：')) '互斥模式必须在打开监听和对战前拒绝。'
            Assert-NoSave $invalid
        }
    }
    $checks.Add('规则模式仍拒绝联网、同屏、独立回放和覆盖计时参数，双顺序 12 次明确拒绝且无存档')

    # 使用实际 CLI 的 LocalTimer 边界，受控阻塞代替不可控的 TCP 背压；不修改战斗或时钟规则。
    $cliAssembly = [Reflection.Assembly]::LoadFrom($cliDll)
    $timerType = $cliAssembly.GetType('StreamDuel.Cli.Program+LocalTimer', $true)
    $clock = [StreamDuel.Core.TurnClock]::new(100, 30)
    $teams = [Collections.Generic.IList[string][]]::new(2)
    $teams[0] = [string[]]($hostTeam -split ',')
    $teams[1] = [string[]]($hostTeam -split ',')
    $battle = [StreamDuel.Core.Battle]::new($db, $teams, [string[]]@('你', '电脑'), [uint32]137, $false)
    $timer = [Activator]::CreateInstance($timerType, [object[]]@($clock, $battle))
    $plainPump = $timerType.GetMethod('Pump', [type[]]@())
    $observerPump = $timerType.GetMethod('Pump', [type[]]@([Action]))
    $null = $plainPump.Invoke($timer, $null)
    $before = $clock.TurnLeft(0)
    $delay = [Action]{ [Threading.Thread]::Sleep(1500) }
    $null = $observerPump.Invoke($timer, [object[]]@($delay))
    $null = $plainPump.Invoke($timer, $null)
    Assert-Check (($before - $clock.TurnLeft(0)) -lt 1.0) '观战发送阻塞 1.5 秒不能扣入本地思考预算。'
    $before = $clock.TurnLeft(0)
    [Threading.Thread]::Sleep(250)
    $null = $plainPump.Invoke($timer, $null)
    Assert-Check (($before - $clock.TurnLeft(0)) -ge 0.2) '真实选手等待时间仍必须正常扣除。'
    $checks.Add('真实 LocalTimer 集成：1.5 秒受控观战阻塞不扣预算，250ms 普通等待正常扣除')

    # 分批递送真实协议，固定 TCP 可能出现的开场先到、追赶事件后到，而非依赖线程调度碰巧覆盖。
    [StreamDuel.Core.INetTransport]$sourceHost = $null
    [StreamDuel.Core.INetTransport]$sourceRelay = $null
    [StreamDuel.Core.INetTransport]$clientRelay = $null
    [StreamDuel.Core.INetTransport]$clientEnd = $null
    [StreamDuel.Core.LoopbackPair]::Create([ref]$sourceHost, [ref]$sourceRelay)
    [StreamDuel.Core.LoopbackPair]::Create([ref]$clientRelay, [ref]$clientEnd)
    $stagedBattle = [StreamDuel.Core.Battle]::new($db, $teams, [string[]]@('你', '电脑'), [uint32]137, $false)
    $stagedRules = [StreamDuel.Core.Ruleset]::new()
    $stagedRules.Id = 'display-probe'
    $stagedRules.Name = '分批显示'
    $stagedRules.BestOf = 3
    $stagedFeed = [StreamDuel.Core.SpectatorMatchFeed]::new($stagedRules, 0, $false)
    $stagedFeed.AddViewer($sourceHost)
    $stagedFeed.StartGame($stagedBattle)
    $stagedViewer = [StreamDuel.Core.SpectatorClient]::new($db, $clientEnd, $false, '观众', $true)
    $stagedBattle.ResolveTurn([StreamDuel.Core.BattleAction]::Pass(), [StreamDuel.Core.BattleAction]::Pass())
    $stagedFeed.Pump()
    $frame = $null
    while ($sourceRelay.TryReceive([ref]$frame)) { $clientRelay.Send($frame) }
    $stagedViewer.Poll()
    Assert-Check ($null -eq $stagedViewer.Error -and -not $stagedViewer.HasMatch) '分批验收先取得正常旧单局前缀。'
    $previous = $stagedViewer.Battle
    $previousLogCount = $previous.Log.Count
    while ($clientRelay.TryReceive([ref]$frame)) { $sourceRelay.Send($frame) }
    $stagedFeed.Pump()
    $held = [Collections.Generic.List[string]]::new()
    while ($sourceRelay.TryReceive([ref]$frame)) {
        $message = $frame | ConvertFrom-Json
        if ($message.kind -eq 'frame' -and ($message.json | ConvertFrom-Json).type -ne 'spectator_start') { $held.Add($frame) }
        else { $clientRelay.Send($frame) }
    }
    $stagedViewer.Poll()
    Assert-Check ($null -eq $stagedViewer.Error -and $stagedViewer.HasMatch -and $held.Count -gt 0 -and
        $stagedViewer.Battle.Log.Count -lt $previousLogCount) '分批验收必须先回到较短开场，确实把旧日志追赶留到下一次轮询。'
    $displayType = $cliAssembly.GetType('StreamDuel.Cli.Program+MatchSpectatorDisplay', $true)
    $display = [Activator]::CreateInstance($displayType, $true)
    $optionsType = $cliAssembly.GetType('StreamDuel.Cli.CliOptions', $true)
    $options = [Activator]::CreateInstance($optionsType, $true)
    $render = $displayType.GetMethod('Render')
    $consoleBefore = [Console]::Out
    $captured = [IO.StringWriter]::new()
    try {
        [Console]::SetOut($captured)
        $null = $render.Invoke($display, [object[]]@($stagedViewer, $options, $previous, $previousLogCount))
        foreach ($frame in $held) { $clientRelay.Send($frame) }
        $stagedViewer.Poll()
        $null = $render.Invoke($display, [object[]]@($stagedViewer, $options, $previous, $previousLogCount))
        foreach ($entry in $previous.Log) {
            Assert-Check (-not $captured.ToString().Contains('  ' + $entry.Text)) '协商后补发的旧日志不得再次显示。'
        }
        $captured.GetStringBuilder().Clear() | Out-Null
        $stagedBattle.ResolveTurn([StreamDuel.Core.BattleAction]::Pass(), [StreamDuel.Core.BattleAction]::Pass())
        $stagedFeed.Pump()
        while ($sourceRelay.TryReceive([ref]$frame)) { $clientRelay.Send($frame) }
        $stagedViewer.Poll()
        $null = $render.Invoke($display, [object[]]@($stagedViewer, $options, $previous, $previousLogCount))
        Assert-Check ($null -eq $stagedViewer.Error -and $stagedViewer.Battle.Log.Count -gt $previousLogCount -and
            $captured.ToString().Contains('  ' + $stagedViewer.Battle.Log[$previousLogCount].Text)) '追赶完成后的真实新日志必须继续显示，不能丢失。'
    }
    finally { [Console]::SetOut($consoleBefore); $captured.Dispose() }
    $checks.Add('能力升级分批投递真实消息：先到短开场、后到旧事件不会重复日志，后续新事件正常显示')
}
catch { $failure = $_ }
finally {
    foreach ($child in $children) {
        try {
            if ($child.Started -and -not $child.Process.HasExited) {
                $child.Process.Kill($true)
                Assert-Check ($child.Process.WaitForExit(5000)) "$($child.Name) 清理时未退出。"
            }
            if ($null -ne $child.OutTask) { Assert-Check ($child.OutTask.Wait(5000)) "$($child.Name) 标准输出未排空。" }
            if ($null -ne $child.ErrTask) { Assert-Check ($child.ErrTask.Wait(5000)) "$($child.Name) 错误输出未排空。" }
        }
        catch { $cleanupFailures.Add("$($child.Name)：$($_.Exception.Message)") }
        finally {
            if ($null -ne $child.OutStream) { $child.OutStream.Dispose() }
            if ($null -ne $child.ErrStream) { $child.ErrStream.Dispose() }
            $child.Process.Dispose()
        }
    }
}
$status = if ($null -eq $failure -and $cleanupFailures.Count -eq 0) { 'PASS' } else { 'FAIL' }
$report = [ordered]@{
    Status = $status; Checks = $checks.ToArray(); Processes = $children.Count; FinalHashes = $hashes.ToArray()
    Failure = $(if ($null -eq $failure) { $null } else { $failure.Exception.Message }); CleanupFailures = $cleanupFailures.ToArray()
}
[IO.File]::WriteAllText((Join-Path $runDirectory 'report.json'), ($report | ConvertTo-Json -Depth 5), $utf8)
if ($null -ne $failure) { throw $failure }
Assert-Check ($cleanupFailures.Count -eq 0) ('子进程清理失败：' + ($cleanupFailures -join '；'))
Write-Output "PASS：$($checks.Count) 组多局观战与 CLI 检查，$($children.Count) 个进程；证据：$runDirectory"
