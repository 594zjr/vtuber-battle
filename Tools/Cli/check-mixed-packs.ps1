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
foreach ($path in @($cliDll, $coreDll)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "请先构建 Tools/Cli -c $Configuration。" }
}
$runDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ('obj/mixed-packs-check/' + [Guid]::NewGuid().ToString('N'))))
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
    $result = Run-Cli $Name $Parameters '' 1
    Assert-Check ($result.Err -match '[\u4e00-\u9fff]') "$Name 没有中文错误。"
    Assert-NoSave $Name
}
function Final-Hash([string]$Output) {
    $found = [regex]::Matches($Output, '(?m)^最终状态哈希：([0-9a-f]{16})\r?$')
    Assert-Check ($found.Count -eq 1) '应恰好输出一份最终联网哈希。'
    return $found[0].Groups[1].Value
}

try {
    $assembly = [Reflection.Assembly]::LoadFrom($coreDll)
    Assert-Check ([IO.Path]::GetFullPath($assembly.Location) -eq [IO.Path]::GetFullPath($coreDll)) '请在新 PowerShell 进程中运行。'
    $rules = [IO.File]::ReadAllText((Join-Path $binaryDirectory 'Content/StreamDuelRules.json'))
    $originalJson = [IO.File]::ReadAllText((Join-Path $binaryDirectory 'Content/Packs/original.json'))
    $fanJson = [IO.File]::ReadAllText((Join-Path $binaryDirectory 'Content/Packs/fan_asoul_vr.json'))
    $original = [StreamDuel.Core.ContentDb]::FromJson($rules, $originalJson)
    $fan = [StreamDuel.Core.ContentDb]::FromJson($rules, $fanJson)
    $db = [StreamDuel.Core.ContentDb]::Merge([StreamDuel.Core.ContentDb[]]@($original, $fan))
    Assert-Check ($db.PackId -ceq 'mix:fan_asoul_vr,original') '组合编号没有排序。'
    $teamA = 'bell,enka,shiori,bella,diana,eileen'
    $teamB = 'kei,yuan,nyako,nana7mi,koichi,aza'
    $manual = Run-Cli 'manual' @('--packs', 'original,fan_asoul_vr', '--team', $teamA, '--team-b', $teamB,
        '--auto', '--seed', '7', '--share', '--export-team', '--report', (Join-Path $runDirectory 'manual.md'))
    Assert-Check ($manual.Err.Length -eq 0) '手动混合阵容产生错误。'
    $saved = Read-Record 'manual'
    Assert-Check ($saved.Record.PackId -ceq $db.PackId -and ($saved.Record.Teams[0] -join ',') -ceq $teamA -and
        ($saved.Record.Teams[1] -join ',') -ceq $teamB) '手动双方阵容或组合编号没有保留。'
    foreach ($id in ($teamA + ',' + $teamB).Split(',')) {
        Assert-Check ($manual.Out.Contains('（' + $id + '）：' + $db.NoticeFor($id))) "缺少 $id 来源声明。"
    }
    Assert-Check ($manual.Out.Contains($original.PackNotice) -and $manual.Out.Contains($fan.PackNotice)) '聚合声明丢失。'
    $expectedBattle = [StreamDuel.Core.Replay]::Run($db, $saved.Record)
    Assert-Check ($expectedBattle.Phase -eq [StreamDuel.Core.Phase]::Over) '手动混合对局未打完。'
    $teamCode = Extract-Code $manual.Out 'SDT1-' '本方队伍码：'
    $share = Extract-Code $manual.Out 'SD1-' '分享码：'
    $import = Run-Cli 'team-import' @('--team-code', $teamCode, '--export-team') "q`n"
    Assert-Check ((Extract-Code $import.Out 'SDT1-' '本方队伍码：') -ceq $teamCode -and $import.Err.Length -eq 0) '混合队伍码不能自动重建内容包。'
    Assert-NoSave 'team-import'
    $replay = Run-Cli 'replay-file' @('--replay', $saved.File, '--auto', '--report', (Join-Path $runDirectory 'replay.md'))
    $replayCode = Run-Cli 'replay-code' @('--replay-code', $share, '--auto')
    Assert-Check ($replay.Err.Length -eq 0 -and $replayCode.Err.Length -eq 0) '混合回放或分享码加载失败。'
    Assert-Check ([IO.File]::ReadAllText((Join-Path $runDirectory 'manual.md')) -ceq
        [IO.File]::ReadAllText((Join-Path $runDirectory 'replay.md'))) '重放报告与原始报告不一致。'
    Assert-NoSave 'replay-file'; Assert-NoSave 'replay-code'
    $checks.Add('手动混合12人完整对局、逐人及聚合声明、队伍码导入、文件/分享码回看、战报一致且回看不保存')

    $single = Run-Cli 'single-mix' @('--pack', 'mix:original', '--random-teams', '--seed', '13', '--auto', '--export-team')
    Assert-Check ($single.Err.Length -eq 0) '单源 mix:original 不能新建随机对局。'
    $singleSaved = Read-Record 'single-mix'
    Assert-Check ($singleSaved.Record.PackId -ceq 'mix:original') '单源混合编号改变。'
    $singleCode = Extract-Code $single.Out 'SDT1-' '本方队伍码：'
    $singleImport = Run-Cli 'single-mix-import' @('--team-code', $singleCode, '--export-team') "q`n"
    $singleReplay = Run-Cli 'single-mix-replay' @('--replay', $singleSaved.File, '--auto')
    Assert-Check ($singleImport.Err.Length -eq 0 -and $singleReplay.Err.Length -eq 0 -and
        (Extract-Code $singleImport.Out 'SDT1-' '本方队伍码：') -ceq $singleCode) '单源混合队伍码或录像无法重建。'
    Assert-NoSave 'single-mix-import'; Assert-NoSave 'single-mix-replay'
    $checks.Add('Core合法的单源mix:original随机完整对局、队伍码和回放自动重建')

    $expected = [StreamDuel.Core.MixedBattle]::RandomTeams($db, [uint32]17)
    $first = Run-Cli 'random-a' @('--packs', 'original,fan_asoul_vr', '--random-teams', '--auto', '--seed', '17')
    $second = Run-Cli 'random-b' @('--packs', 'fan_asoul_vr,original', '--random-teams', '--auto', '--seed', '17')
    $recordA = (Read-Record 'random-a').Record
    $recordB = (Read-Record 'random-b').Record
    Assert-Check ($first.Err.Length -eq 0 -and $second.Err.Length -eq 0 -and $recordA.ToJson() -ceq $recordB.ToJson()) '输入包顺序改变了随机对局。'
    for ($side = 0; $side -lt 2; $side++) {
        Assert-Check (($recordA.Teams[$side] -join ',') -ceq ($expected[$side] -join ',')) '随机阵容与 Core API 不符。'
    }
    Assert-Check (@(@($recordA.Teams[0]) + @($recordA.Teams[1]) | Sort-Object -Unique).Count -eq 12) '随机双方存在重复主播。'
    $checks.Add('随机双方与 Core 相同、十二人互不重复、反转包顺序后完整录像逐字相同')

    $externalDirectory = Join-Path $runDirectory 'external-packs'
    [void][IO.Directory]::CreateDirectory($externalDirectory)
    $external = $fanJson | ConvertFrom-Json
    $external.id = 'mixed,external:中文%note'
    $external.name = '外部混合验收包'
    $external.notice = '外部声明保留验收：' + $external.notice
    $externalJson = $external | ConvertTo-Json -Depth 100
    [IO.File]::WriteAllText((Join-Path $externalDirectory 'external.json'), $externalJson, $utf8)
    $externalDb = [StreamDuel.Core.ContentDb]::FromJson($rules, $externalJson)
    $externalMix = [StreamDuel.Core.ContentDb]::Merge([StreamDuel.Core.ContentDb[]]@($original, $externalDb))
    $externalGame = Run-Cli 'external' @('--pack', $externalMix.PackId, '--pack-dir', $externalDirectory,
        '--random-teams', '--auto', '--seed', '19', '--export-team')
    Assert-Check ($externalGame.Err.Length -eq 0 -and $externalGame.Out.Contains($external.notice)) '外部特殊编号或声明加载失败。'
    $externalRecord = Read-Record 'external'
    $externalCode = Extract-Code $externalGame.Out 'SDT1-' '本方队伍码：'
    $externalReplay = Run-Cli 'external-replay' @('--replay', $externalRecord.File, '--pack-dir', $externalDirectory, '--auto')
    $externalImport = Run-Cli 'external-import' @('--team-code', $externalCode, '--pack-dir', $externalDirectory, '--export-team') "q`n"
    Assert-Check ($externalReplay.Err.Length -eq 0 -and $externalImport.Err.Length -eq 0 -and
        (Extract-Code $externalImport.Out 'SDT1-' '本方队伍码：') -ceq $externalCode) '外部组合的回放或队伍码无法重建。'
    Assert-NoSave 'external-replay'; Assert-NoSave 'external-import'
    Reject 'external-missing' @('--replay', $externalRecord.File, '--auto')
    $checks.Add('外部逗号/冒号/中文/百分号ID的规范编码、完整对局、回放和队伍码重建；缺包中文拒绝')

    $invalid = @(
        @('--packs', 'original'), @('--packs', 'original,'), @('--packs', 'original,original'),
        @('--packs', 'original,missing_pack'), @('--packs', 'original,fan_asoul_vr', '--pack', 'original'),
        @('--packs', 'original,fan_asoul_vr', '--random-teams', '--team', $teamA),
        @('--packs', 'original,fan_asoul_vr', '--random-teams', '--team-b', $teamB),
        @('--random-teams', '--team-code', $teamCode), @('--random-teams', '--replay', $saved.File),
        @('--packs', 'original,fan_asoul_vr', '--replays'), @('--random-teams', '--host', '0'),
        @('--team-code', $teamCode, '--pack', 'original'), @('--pack', 'mix:original,fan_asoul_vr'),
        @('--team-b', $teamB))
    for ($i = 0; $i -lt $invalid.Count; $i++) { Reject ('invalid-' + $i) $invalid[$i] }
    $checks.Add('参数互斥、未知/重复/空包、非规范组合、队伍码组合不符和联网缺显式seed全部拒绝且不存档')

    foreach ($mismatch in @($false, $true)) {
        $prefix = if ($mismatch) { 'network-mismatch' } else { 'network' }
        $hostSave = Join-Path $runDirectory ($prefix + '-host-saves')
        $guestSave = Join-Path $runDirectory ($prefix + '-guest-saves')
        $hostChild = Start-Cli ($prefix + '-host') @('--packs', 'original,fan_asoul_vr', '--random-teams', '--seed', '23', '--host', '0', '--auto', '--hash', '--save-dir', $hostSave)
        $port = [int](Wait-Output $hostChild '(?m)^监听端口：([0-9]+)\r?$').Groups[1].Value
        $guestSeed = if ($mismatch) { '24' } else { '23' }
        $guestChild = Start-Cli ($prefix + '-guest') @('--packs', 'fan_asoul_vr,original', '--random-teams', '--seed', $guestSeed, '--join', "127.0.0.1:$port", '--auto', '--hash', '--save-dir', $guestSave)
        $hostChild.Process.StandardInput.Close(); $guestChild.Process.StandardInput.Close()
        $exit = if ($mismatch) { 1 } else { 0 }
        $hostResult = Wait-Cli $hostChild $exit
        $guestResult = Wait-Cli $guestChild $exit
        if ($mismatch) {
            Assert-Check (($hostResult.Err + $guestResult.Err).Contains('随机联网阵容不一致')) '不同seed没有明确拒绝原因。'
            Assert-NoSave ($prefix + '-host'); Assert-NoSave ($prefix + '-guest')
        }
        else {
            Assert-Check ($hostResult.Err.Length -eq 0 -and $guestResult.Err.Length -eq 0 -and
                (Final-Hash $hostResult.Out) -ceq (Final-Hash $guestResult.Out)) '随机混合联网哈希不一致。'
            $hostRecord = (Read-Record ($prefix + '-host')).Record
            $guestRecord = (Read-Record ($prefix + '-guest')).Record
            Assert-Check ($hostRecord.ToJson() -ceq $guestRecord.ToJson()) '随机混合联网双方录像不一致。'
            $networkTeams = [StreamDuel.Core.MixedBattle]::RandomTeams($db, [uint32]23)
            for ($side = 0; $side -lt 2; $side++) {
                Assert-Check (($hostRecord.Teams[$side] -join ',') -ceq ($networkTeams[$side] -join ',')) '联网没有采用对应方随机阵容。'
            }
        }
    }
    $checks.Add('反转包顺序的真实TCP随机完整对局、双方哈希/录像一致；不同seed明确拒绝且不保存')
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
Write-Output "PASS：$($checks.Count) 组混合包验收；证据目录：$runDirectory"
