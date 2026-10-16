#requires -Version 7.0
param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$repositoryPrefix = $repository.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$current = [IO.Path]::GetFullPath((Get-Location).Path)
if ($current -ne $repository -and -not $current.StartsWith($repositoryPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw '请在本仓库内运行此脚本。'
}
$binaryDirectory = Join-Path $PSScriptRoot "bin/$Configuration/net8.0"
$cliDll = Join-Path $binaryDirectory 'Cli.dll'
$coreDll = Join-Path $binaryDirectory 'StreamDuel.Core.dll'
$builtCliDll = $cliDll
foreach ($path in @($cliDll, $coreDll)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "请先构建 Tools/Cli -c $Configuration。" }
}
$runDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ('obj/damage-breakdown-check/' + [Guid]::NewGuid().ToString('N'))))
if (-not $runDirectory.StartsWith($repositoryPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw '产物目录越界。' }
[void][IO.Directory]::CreateDirectory($runDirectory)
$testTemp = Join-Path $runDirectory 'test-temp'
[void][IO.Directory]::CreateDirectory($testTemp)
$utf8 = [Text.UTF8Encoding]::new($false)
$children = [Collections.Generic.List[object]]::new()
$checks = [Collections.Generic.List[string]]::new()
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
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = 'dotnet'
    $start.ArgumentList.Add($cliDll)
    foreach ($parameter in $Parameters) { $start.ArgumentList.Add($parameter) }
    $start.WorkingDirectory = $runDirectory
    $start.Environment['TEMP'] = $testTemp
    $start.Environment['TMP'] = $testTemp
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
        Name = $Name; Parameters = $Parameters; Process = $process; Started = $false
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
    # 联网双方共享这个启动入口；由调用方决定何时关闭 stdin，不提前注入退出输入。
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


function Run-Cli([string]$Name, [string[]]$Parameters, [string]$InputText = '', [int]$Exit = 0) {
    $save = Join-Path $runDirectory ($Name + '-saves')
    $child = Start-Cli $Name ($Parameters + @('--save-dir', $save))
    if ($InputText.Length -gt 0) { $child.Process.StandardInput.Write($InputText) }
    $child.Process.StandardInput.Close()
    return Wait-Cli $child $Exit
}
function Extract-Code([string]$Output, [string]$Prefix, [string]$Label) {
    $matches = [regex]::Matches($Output, '(?m)^' + [regex]::Escape($Label) + '(' + $Prefix + '[A-Za-z0-9_-]+)\r?$')
    Assert-Check ($matches.Count -eq 1) "缺少唯一的 $Label"
    return $matches[0].Groups[1].Value
}
function Read-Record([string]$Name) {
    $files = @(Get-ChildItem -LiteralPath (Join-Path $runDirectory "$Name-saves/replays") -File -Filter '*.json')
    Assert-Check ($files.Count -eq 1) "$Name 没有恰好一份回放。"
    return [pscustomobject]@{ File = $files[0].FullName; Record = [StreamDuel.Core.BattleRecord]::FromJson([IO.File]::ReadAllText($files[0].FullName)) }
}
function Assert-NoSave([string]$Name) {
    Assert-Check (-not (Test-Path -LiteralPath (Join-Path $runDirectory ($Name + '-saves')))) "$Name 不应保存未完成对局。"
}
function Reject([string]$Name, [string[]]$Parameters) {
    # 不追加存档选项，避免因其它互斥参数被拒绝而掩盖拆解选项本身的问题。
    $child = Start-Cli $Name $Parameters
    $child.Process.StandardInput.Close()
    $result = Wait-Cli $child 1
    $explainsConflict = $result.Err.Contains('--explain-damage') -or
        ($Parameters -contains '--lan-list' -and $result.Err.Contains('--lan-list 是独立的 5 秒搜索操作'))
    Assert-Check ($result.Err -match '[\u4e00-\u9fff]' -and $explainsConflict) "$Name 没有解释选项冲突的中文错误。"
    Assert-NoSave $Name
}

function Breakdown-Lines([string]$Output) {
    return @([regex]::Matches($Output, '(?m)^    伤害拆解：([^\r\n]*)\r?$') | ForEach-Object { $_.Groups[1].Value })
}
function Assert-DamageOutput([string]$Output, $Battle, [int]$LastTurn = [int]::MaxValue, [bool]$AllowEmpty = $false) {
    $expected = [Collections.Generic.List[string]]::new()
    $display = [StreamDuel.Core.TextInput]::ForDisplay($Battle)
    $normalized = $Output.Replace("`r`n", "`n")
    for ($i = 0; $i -lt $display.Log.Count; $i++) {
        if ($display.Log[$i].Turn -gt $LastTurn) { continue }
        $lines = [StreamDuel.Core.BattleViewModel]::DamageExplanationForLog($display, $i)
        if ($lines.Count -eq 0) { continue }
        $block = '  ' + $display.Log[$i].Text + "`n"
        foreach ($line in $lines) {
            $expected.Add($line)
            $block += '    伤害拆解：' + $line + "`n"
        }
        Assert-Check ($normalized.Contains($block)) "日志 $i 的拆解没有紧接原伤害日志或未沿用 Core 文字。"
    }
    Assert-Check ($AllowEmpty -or $expected.Count -gt 0) '对局夹具必须实际造成伤害。'
    $actual = @(Breakdown-Lines $Output)
    Assert-Check ($actual.Count -eq $expected.Count -and ($actual -join "`n") -ceq ($expected -join "`n")) '拆解条数、顺序或内容与 Core 已结算事件不一致。'
}
function Without-Explanations([string]$Output, [string]$Name, [string]$Report = '') {
    $text = [regex]::Replace($Output, '(?m)^    伤害拆解：[^\r\n]*\r?\n', '')
    $text = $text.Replace((Join-Path $runDirectory ($Name + '-saves')), '{存档}')
    if ($Report.Length -gt 0) { $text = $text.Replace($Report, '{战报}') }
    return $text
}

try {
    $assembly = [Reflection.Assembly]::LoadFrom($coreDll)
    Assert-Check ([IO.Path]::GetFullPath($assembly.Location) -eq [IO.Path]::GetFullPath($coreDll)) '请在新的 PowerShell 进程运行此脚本。'
    $rules = [IO.File]::ReadAllText((Join-Path $binaryDirectory 'Content/StreamDuelRules.json'))
    $db = [StreamDuel.Core.ContentDb]::FromJson($rules,
        [IO.File]::ReadAllText((Join-Path $binaryDirectory 'Content/Packs/original.json')))
    $baseReport = Join-Path $runDirectory 'baseline.md'
    $explainedReport = Join-Path $runDirectory 'explained.md'
    $baseline = Run-Cli 'baseline' @('--auto', '--seed', '17', '--hash', '--share', '--report', $baseReport)
    $explained = Run-Cli 'explained' @('--auto', '--seed', '17', '--hash', '--share', '--report', $explainedReport, '--explain-damage')
    Assert-Check ($baseline.Err.Length -eq 0 -and $explained.Err.Length -eq 0) '本地完整对局出现错误。'
    $baseRecord = Read-Record 'baseline'
    $explainedRecord = Read-Record 'explained'
    Assert-Check ($baseRecord.Record.ToJson() -ceq $explainedRecord.Record.ToJson()) '显示开关改变了录像。'
    Assert-Check (@(Breakdown-Lines $baseline.Out).Count -eq 0) '默认输出出现拆解。'
    Assert-Check ((Without-Explanations $baseline.Out 'baseline' $baseReport) -ceq
        (Without-Explanations $explained.Out 'explained' $explainedReport)) '扣除新增拆解行后，旧完整输出或逐回合哈希发生变化。'
    Assert-Check ([IO.File]::ReadAllText($baseReport) -ceq [IO.File]::ReadAllText($explainedReport)) '显示开关修改了导出战报。'
    $battle = [StreamDuel.Core.Replay]::Run($db, $baseRecord.Record)
    Assert-DamageOutput $explained.Out $battle
    $share = Extract-Code $baseline.Out 'SD1-' '分享码：'
    Assert-Check ($share -ceq (Extract-Code $explained.Out 'SD1-' '分享码：')) '显示开关修改了分享码。'
    $checks.Add('本地完整对局逐条拆解与Core相同且紧随原日志；默认输出、录像、逐回合哈希、分享码和战报保持一致')

    $sourceHash = (Get-FileHash -LiteralPath $baseRecord.File -Algorithm SHA256).Hash
    $replayBase = Run-Cli 'replay-base' @('--replay', $baseRecord.File, '--auto')
    $replayExplain = Run-Cli 'replay-explain' @('--replay', $baseRecord.File, '--auto', '--explain-damage')
    $shareExplain = Run-Cli 'share-explain' @('--replay-code', $share, '--auto', '--explain-damage')
    Assert-Check ($replayBase.Err.Length -eq 0 -and $replayExplain.Err.Length -eq 0 -and $shareExplain.Err.Length -eq 0) '文件或分享码回看出错。'
    Assert-Check ($replayBase.Out -ceq (Without-Explanations $replayExplain.Out 'replay-explain')) '回看默认输出发生变化。'
    Assert-DamageOutput $replayExplain.Out $battle
    Assert-DamageOutput $shareExplain.Out $battle
    $partial = Run-Cli 'replay-partial' @('--replay', $baseRecord.File, '--explain-damage') "q`n"
    Assert-Check ($partial.Out.Contains('已退出回看。')) '回看提前退出失败。'
    Assert-DamageOutput $partial.Out $battle $battle.Log[0].Turn $true
    foreach ($name in @('replay-base', 'replay-explain', 'share-explain', 'replay-partial')) { Assert-NoSave $name }
    Assert-Check ((Get-FileHash -LiteralPath $baseRecord.File -Algorithm SHA256).Hash -ceq $sourceHash) '回看修改了原始录像。'
    $checks.Add('文件/分享码回看逐条一致，提前退出不打印未来回合拆解，不写战绩或新录像')

    $teamA = 'enka,bell,shiori,lumi,hane,momo'
    $teamB = 'kei,yuan,nyako,buck,kasumi,bee'
    $hotseat = @('--hotseat', '--team', $teamA, '--team-b', $teamB, '--name-a', '红方', '--name-b', '蓝方', '--seed', '17')
    $hotBase = Run-Cli 'hotseat-base' $hotseat "1`n`n1`n/surrender`n"
    $hotExplain = Run-Cli 'hotseat-explain' ($hotseat + @('--explain-damage')) "1`n`n1`n/surrender`n"
    $hotRecord = Read-Record 'hotseat-base'
    Assert-Check ($hotBase.Err.Length -eq 0 -and $hotExplain.Err.Length -eq 0 -and
        $hotRecord.Record.ToJson() -ceq (Read-Record 'hotseat-explain').Record.ToJson()) '同屏开关改变录像或导致错误。'
    Assert-Check ((Without-Explanations $hotBase.Out 'hotseat-base') -ceq
        (Without-Explanations $hotExplain.Out 'hotseat-explain')) '同屏原输出、交接清屏或私密输入行为变化。'
    Assert-DamageOutput $hotExplain.Out ([StreamDuel.Core.Replay]::Run($db, $hotRecord.Record))
    $private = Run-Cli 'hotseat-private' ($hotseat + @('--explain-damage')) "1`nq`n"
    Assert-Check (@(Breakdown-Lines $private.Out).Count -eq 0 -and $private.Out.Contains('未完成')) '同屏未公开选择提前产生拆解。'
    Assert-NoSave 'hotseat-private'
    $checks.Add('同屏开关不改录像/交接/私密选择，拆解仅来自已结算事件')

    $hostSave = Join-Path $runDirectory 'network-host-saves'
    $guestSave = Join-Path $runDirectory 'network-guest-saves'
    $hostChild = Start-Cli 'network-host' @('--host', '0', '--team', $teamA, '--seed', '17', '--auto', '--hash', '--explain-damage', '--save-dir', $hostSave)
    $port = [int](Wait-Output $hostChild '(?m)^监听端口：([0-9]+)\r?$').Groups[1].Value
    $guestChild = Start-Cli 'network-guest' @('--join', "127.0.0.1:$port", '--team', $teamB, '--auto', '--hash', '--save-dir', $guestSave)
    $hostResult = Wait-Cli $hostChild 0
    $guestResult = Wait-Cli $guestChild 0
    Assert-Check ($hostResult.Err.Length -eq 0 -and $guestResult.Err.Length -eq 0) '真实TCP对局报错。'
    $hostRecord = Read-Record 'network-host'
    $guestRecord = Read-Record 'network-guest'
    Assert-Check ($hostRecord.Record.ToJson() -ceq $guestRecord.Record.ToJson()) '联网两侧开关不一致时录像不同。'
    $hostHash = [regex]::Match($hostResult.Out, '最终状态哈希：([0-9a-f]{16})')
    $guestHash = [regex]::Match($guestResult.Out, '最终状态哈希：([0-9a-f]{16})')
    Assert-Check ($hostHash.Success -and $guestHash.Success -and $hostHash.Groups[1].Value -ceq $guestHash.Groups[1].Value) '联网显示开关影响终局同步。'
    Assert-Check (@(Breakdown-Lines $guestResult.Out).Count -eq 0) '未开启的客机输出了拆解。'
    $networkBattle = [StreamDuel.Core.Replay]::Run($db, $hostRecord.Record)
    Assert-Check ($networkBattle.StateHash().ToString('x16') -ceq $hostHash.Groups[1].Value) '联网录像无法复现最终状态。'
    Assert-DamageOutput $hostResult.Out $networkBattle
    $checks.Add('真实TCP一侧开一侧关，双方录像/最终哈希一致；主机拆解和重放Core事件逐条一致')

    $invalid = @(
        @('--explain-damage', '--explain-damage'), @('--list-packs', '--explain-damage'),
        @('--replays', '--explain-damage'), @('--lan-list', '--explain-damage'),
        @('--glossary', '--explain-damage'), @('--calc', 'enka', '1', 'kei', '--explain-damage'),
        @('--card', 'enka', '--explain-damage'), @('--explain-damage', '--card', 'enka'),
        @('--cards', 'original', '--explain-damage'), @('--explain-damage', '--cards', 'original'),
        @('--tournament', (Join-Path $runDirectory 'invalid-event.json'), '--explain-damage'),
        @('--tournament-report', (Join-Path $runDirectory 'invalid-event.json'), '--explain-damage')
    )
    for ($i = 0; $i -lt $invalid.Count; $i++) { Reject ('invalid-' + $i) $invalid[$i] }
    Assert-Check (-not (Test-Path -LiteralPath (Join-Path $runDirectory 'invalid-event.json'))) '拒绝的赛事模式仍写了文件。'
    $help = Run-Cli 'help' @('--help')
    Assert-Check ($help.Out.Contains('--explain-damage') -and $help.Out.Contains('默认关闭')) '帮助未说明开关。'
    Assert-Check (-not (Test-Path -LiteralPath (Join-Path $runDirectory 'saves'))) '烟测写入了默认存档目录。'
    $checks.Add('重复和无逐局日志模式中文拒绝，帮助完整，产物仅在obj')
}
catch { $failure = $_.Exception.GetBaseException().Message }
finally {
    foreach ($child in $children) {
        if ($child.Started -and -not $child.Process.HasExited) { $child.Process.Kill($true); [void]$child.Process.WaitForExit(5000) }
        foreach ($task in @($child.OutTask, $child.ErrTask)) { if ($null -ne $task) { [void]$task.Wait(5000) } }
        foreach ($stream in @($child.OutStream, $child.ErrStream)) { if ($null -ne $stream) { $stream.Dispose() } }
        $child.Process.Dispose()
    }
    $summary = [ordered]@{ status = $(if ($null -eq $failure) { 'PASS' } else { 'FAIL' }); failure = $failure; checks = $checks.ToArray(); directory = $runDirectory }
    [IO.File]::WriteAllText((Join-Path $runDirectory 'result.json'), ($summary | ConvertTo-Json -Depth 6), $utf8)
}
if ($null -ne $failure) { throw "$failure 证据目录：$runDirectory" }
Write-Output "PASS：$($checks.Count) 组伤害拆解验收；证据目录：$runDirectory"
