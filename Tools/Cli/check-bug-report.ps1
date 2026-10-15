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
$runDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ('obj/bug-report-check/' + [Guid]::NewGuid().ToString('N'))))
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


function Copy-PolicyDeployment([string]$Source, [string]$Destination) {
    $sourceFull = [IO.Path]::GetFullPath($Source)
    $destFull = [IO.Path]::GetFullPath($Destination)
    Assert-Check ($sourceFull.StartsWith($repositoryPrefix, [StringComparison]::OrdinalIgnoreCase) -and
        $destFull.StartsWith($runDirectory + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) '政策验收副本路径越界。'
    $item = Get-Item -LiteralPath $sourceFull
    Assert-Check (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) '政策验收不复制重解析点。'
    if ($item.PSIsContainer) {
        [void][IO.Directory]::CreateDirectory($destFull)
        foreach ($child in Get-ChildItem -LiteralPath $sourceFull -Force) {
            Copy-PolicyDeployment $child.FullName (Join-Path $destFull $child.Name)
        }
    }
    else {
        [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destFull))
        [IO.File]::Copy($sourceFull, $destFull, $false)
    }
}
function Set-TestPolicy([string[]]$DisabledPacks, [string[]]$DisabledStreamers, [string]$Reason) {
    $reasons = [ordered]@{}
    foreach ($id in @($DisabledPacks) + @($DisabledStreamers)) { $reasons[$id] = $Reason }
    $json = [ordered]@{ disabledPacks = @($DisabledPacks); disabledStreamers = @($DisabledStreamers); reason = $reasons } | ConvertTo-Json -Depth 5
    [IO.File]::WriteAllText($policyPath, $json, $utf8)
}

function Run-Cli([string]$Name, [string[]]$Parameters, [string]$InputText = '', [int]$Exit = 0) {
    $child = Start-Cli $Name $Parameters
    if ($InputText.Length -gt 0) { $child.Process.StandardInput.Write($InputText) }
    $child.Process.StandardInput.Close()
    return Wait-Cli $child $Exit
}
function Save-Directory([string]$Name) { return Join-Path $runDirectory ($Name + '-saves') }
function Bug-Codes([string]$Output) {
    return @([regex]::Matches($Output, '问题报告码：(SDBUG1-[A-Za-z0-9_-]+)') | ForEach-Object { $_.Groups[1].Value })
}
function One-Code([string]$Output) {
    $codes = @(Bug-Codes $Output)
    Assert-Check ($codes.Count -eq 1) '应恰好输出一份问题报告。'
    return $codes[0]
}
function Assert-NoSave([string]$Name) {
    Assert-Check (-not (Test-Path -LiteralPath (Save-Directory $Name))) "$Name 不应保存未完成对局。"
}
function Assert-Reproduce([string]$Name, [string]$Code, [string[]]$Extra = @()) {
    $result = Run-Cli $Name (@('--reproduce', $Code) + $Extra)
    Assert-Check ($result.Err.Length -eq 0 -and $result.Out.Contains('状态哈希：一致') -and
        $result.Out.Contains('最近日志哈希：一致') -and $result.Out.Contains('复现结果：一致')) "$Name 未完整复现。"
    Assert-Check (-not (Test-Path -LiteralPath (Join-Path $runDirectory 'saves'))) '复现模式写入了默认存档。'
    return $result
}
function Read-Record([string]$Name) {
    $files = @(Get-ChildItem -LiteralPath (Join-Path (Save-Directory $Name) 'replays') -File -Filter '*.json')
    Assert-Check ($files.Count -eq 1) "$Name 没有恰好一份回放。"
    return [StreamDuel.Core.BattleRecord]::FromJson([IO.File]::ReadAllText($files[0].FullName))
}
function Reject([string]$Name, [string[]]$Parameters, [string]$Reason = '') {
    $result = Run-Cli $Name $Parameters '' 1
    Assert-Check ($result.Err -match '[\u4e00-\u9fff]' -and $result.Err.Contains($Reason)) "$Name 没有预期中文失败原因。"
    Assert-Check (@(Bug-Codes $result.Out).Count -eq 0 -and -not $result.Out.Contains('档案已保存：')) "$Name 拒绝后仍导出报告或保存。"
    Assert-Check (-not (Test-Path -LiteralPath (Join-Path $runDirectory 'saves'))) "$Name 写入默认存档。"
}

try {
    $assembly = [Reflection.Assembly]::LoadFrom($coreDll)
    Assert-Check ([IO.Path]::GetFullPath($assembly.Location) -eq [IO.Path]::GetFullPath($coreDll)) '请在新的 PowerShell 进程中运行。'
    $complete = Run-Cli 'complete' @('--auto', '--seed', '1', '--bug-report', '--share', '--save-dir', (Save-Directory 'complete'))
    $code = One-Code $complete.Out
    $report = [StreamDuel.Core.BugReport]::Parse($code)
    Assert-Check ($report.Record.ToJson() -ceq (Read-Record 'complete').ToJson()) '报告录像与实际保存的完整录像不同。'
    $profilePath = Join-Path (Save-Directory 'complete') 'profile.json'
    $profileHash = (Get-FileHash -LiteralPath $profilePath -Algorithm SHA256).Hash
    $reproduced = Assert-Reproduce 'reproduce-complete' $code
    Assert-Check ($reproduced.Out.Contains('复现阶段：结束')) '终局报告没有复现到结束。'
    [void](Assert-Reproduce 'reproduce-explicit-pack' $code @('--pack', 'original'))
    Assert-Check ((Get-FileHash -LiteralPath $profilePath -Algorithm SHA256).Hash -ceq $profileHash) '复现修改了原战绩。'
    $checks.Add('普通完整对局结束报告、原录像逐字一致、自动/显式选包复现双哈希且不写存档')

    foreach ($mode in @('normal', 'ban', 'lead', 'timer')) {
        $extra = switch ($mode) { 'ban' { @('--ban') }; 'lead' { @('--lead') }; 'timer' { @('--timer', '100,100') }; default { @() } }
        $name = 'mid-' + $mode
        $result = Run-Cli $name (@('--bug-report', '--save-dir', (Save-Directory $name)) + $extra) "/bug 中途验收`nq`n"
        $midCode = One-Code $result.Out
        $midReport = [StreamDuel.Core.BugReport]::Parse($midCode)
        Assert-Check ($midReport.Note -ceq '中途验收' -and $result.Out.Contains('未完成')) '中途报告备注或继续退出行为错误。'
        [void](Assert-Reproduce ($name + '-reproduce') $midCode)
        Assert-NoSave $name
    }
    $checks.Add('普通/禁用/首发/计时输入中的 /bug 不中断对局，提前退出不额外导出或保存；计时 AI 预采样后仍复现')

    $hotseat = Run-Cli 'hotseat-private' @('--hotseat', '--bug-report', '--save-dir', (Save-Directory 'hotseat-private')) "/bug 私密验收`n1`n`n/bug 私密验收`nq`n"
    $privateCodes = @(Bug-Codes $hotseat.Out)
    Assert-Check ($privateCodes.Count -eq 2 -and $privateCodes[0] -ceq $privateCodes[1]) '同屏第一方未结算选择泄漏或改变了问题报告。'
    [void](Assert-Reproduce 'hotseat-private-reproduce' $privateCodes[1])
    Assert-NoSave 'hotseat-private'
    $hotseatEnd = Run-Cli 'hotseat-end' @('--hotseat', '--bug-report', '--save-dir', (Save-Directory 'hotseat-end')) "/surrender`n"
    [void](Assert-Reproduce 'hotseat-end-reproduce' (One-Code $hotseatEnd.Out))
    $checks.Add('同屏提交首方私密选择前后报告逐字相同、未公开选择不泄漏；同屏终局导出可复现')

    $mixed = Run-Cli 'mixed' @('--packs', 'original,fan_asoul_vr', '--random-teams', '--seed', '7', '--bug-report', '--save-dir', (Save-Directory 'mixed')) "/surrender`n"
    $mixedCode = One-Code $mixed.Out
    $mixedReport = [StreamDuel.Core.BugReport]::Parse($mixedCode)
    Assert-Check ($mixedReport.PackId -ceq 'mix:fan_asoul_vr,original') '混合报告包编号错误。'
    [void](Assert-Reproduce 'mixed-reproduce' $mixedCode)

    $externalDirectory = Join-Path $runDirectory 'external-packs'
    [void][IO.Directory]::CreateDirectory($externalDirectory)
    $external = [IO.File]::ReadAllText((Join-Path $binaryDirectory 'Content/Packs/original.json')) | ConvertFrom-Json
    $external.id = 'bug_external'
    $externalFile = Join-Path $externalDirectory 'bug_external.json'
    [IO.File]::WriteAllText($externalFile, ($external | ConvertTo-Json -Depth 100), $utf8)
    $team = (@($external.streamers | Select-Object -First 6 -ExpandProperty id) -join ',')
    $externalRun = Run-Cli 'external' @('--pack', 'bug_external', '--pack-dir', $externalDirectory, '--team', $team,
        '--save-dir', (Save-Directory 'external')) "/bug 外部包验收`nq`n"
    $externalCode = One-Code $externalRun.Out
    [void](Assert-Reproduce 'external-reproduce' $externalCode @('--pack-dir', $externalDirectory))
    Assert-NoSave 'external'
    $external.streamers[0].stats.hp++
    [IO.File]::WriteAllText($externalFile, ($external | ConvertTo-Json -Depth 100), $utf8)
    $changed = Run-Cli 'changed-content' @('--reproduce', $externalCode, '--pack-dir', $externalDirectory) '' 1
    Assert-Check ($changed.Out.Contains('警告：') -and $changed.Out.Contains('内容指纹不同') -and
        $changed.Out.Contains('状态哈希：不一致') -and $changed.Out.Contains('复现阶段：')) '内容变更没有尽力复现并明确报告差异。'
    Reject 'missing-pack' @('--reproduce', $externalCode) 'bug_external'
    Reject 'wrong-pack' @('--reproduce', $code, '--pack', 'fan_asoul_vr') '不匹配'
    $checks.Add('混合包自动重建、外部包复现、内容差异尽力复现并警告、缺包/错包拒绝')

    $replacement = if ($code[10] -ceq 'A') { 'B' } else { 'A' }
    $corrupt = $code.Substring(0, 10) + $replacement + $code.Substring(11)
    Reject 'corrupt' @('--reproduce', $corrupt)
    # 仅为构造通过校验和的外来元数据夹具，复现仍完整经过 CLI/Core 的公开入口。
    $flags = [Reflection.BindingFlags]::Static -bor [Reflection.BindingFlags]::NonPublic
    $decodeText = [StreamDuel.Core.ShareCode].GetMethod('DecodeText', $flags)
    $encodeText = [StreamDuel.Core.ShareCode].GetMethod('EncodeText', $flags)
    $payload = $decodeText.Invoke($null, [object[]]@([string]$code, 'SDBUG1-')) | ConvertFrom-Json
    $payload.engineVersion = [string][char]27 + '[31m外来版本' + [char]0x202E
    $foreignCode = $encodeText.Invoke($null, [object[]]@([string]($payload | ConvertTo-Json -Depth 100 -Compress), 'SDBUG1-'))
    $foreign = Assert-Reproduce 'foreign-engine' $foreignCode
    Assert-Check ($foreign.Out.Contains('引擎版本不同') -and -not $foreign.Out.Contains([string][char]27) -and
        -not $foreign.Out.Contains([string][char]0x202E)) '外来元数据控制字符未清理或版本差异未警告。'
    $futureRecord = $report.Record
    $futureRecord.Version = [StreamDuel.Core.BattleRecord]::CurrentVersion + 1
    $payload.record = $futureRecord.ToJson()
    $payload.replayVersion = $futureRecord.Version
    $futureCode = $encodeText.Invoke($null, [object[]]@([string]($payload | ConvertTo-Json -Depth 100 -Compress), 'SDBUG1-'))
    Reject 'future-replay' @('--reproduce', $futureCode) '更新的版本'
    $conflicts = @(
        @('--reproduce', $code, '--auto'), @('--reproduce', $code, '--bug-report'),
        @('--reproduce', $code, '--save-dir', (Save-Directory 'invalid')),
        @('--reproduce', $code, '--report', (Join-Path $runDirectory 'invalid.md')),
        @('--reproduce', $code, '--host', '0'), @('--reproduce', $code, '--team', $team),
        @('--bug-report', '--replay-code', 'SD1-invalid'), @('--bug-report', '--replays'),
        @('--bug-report', '--list-packs'), @('--bug-report', '--bug-report'),
        @('--reproduce'), @('--reproduce', $code, '--reproduce', $code)
    )
    for ($i = 0; $i -lt $conflicts.Count; $i++) { Reject ('invalid-' + $i) $conflicts[$i] }
    $help = Run-Cli 'help' @('--help')
    Assert-Check ($help.Out.Contains('--bug-report') -and $help.Out.Contains('--reproduce') -and $help.Out.Contains('/bug [备注]')) '帮助遗漏新入口。'
    $checks.Add('篡改校验、未来录像拒绝、引擎版本警告与终端控制字符过滤、参数缺值/重复/模式冲突中文拒绝及帮助完整')

    $hostChild = Start-Cli 'network-host' @('--host', '0', '--seed', '11', '--bug-report', '--save-dir', (Save-Directory 'network-host'))
    $port = [int](Wait-Output $hostChild '(?m)^监听端口：([0-9]+)\r?$').Groups[1].Value
    $guestChild = Start-Cli 'network-guest' @('--join', "127.0.0.1:$port", '--bug-report', '--save-dir', (Save-Directory 'network-guest'))
    [void](Wait-Output $hostChild '选择行动')
    [void](Wait-Output $guestChild '选择行动')
    $hostChild.Process.StandardInput.WriteLine('1')
    [void](Wait-Output $hostChild '等待对手……')
    $hostChild.Process.StandardInput.WriteLine('/bug 联网私密验收')
    $guestChild.Process.StandardInput.WriteLine('/bug 联网私密验收')
    $hostMid = (Wait-Output $hostChild '问题报告码：(SDBUG1-[A-Za-z0-9_-]+)').Groups[1].Value
    $guestMid = (Wait-Output $guestChild '问题报告码：(SDBUG1-[A-Za-z0-9_-]+)').Groups[1].Value
    Assert-Check ($hostMid -ceq $guestMid -and [StreamDuel.Core.BugReport]::Parse($hostMid).Record.Events.Count -eq 0) '联网未结算选择进入报告或两侧已结算快照不一致。'
    $guestChild.Process.StandardInput.WriteLine('/surrender')
    $hostResult = Wait-Cli $hostChild 0
    $guestResult = Wait-Cli $guestChild 0
    $endReports = [Collections.Generic.List[object]]::new()
    foreach ($item in @(@('network-host', $hostResult), @('network-guest', $guestResult))) {
        $codes = @(Bug-Codes $item[1].Out)
        Assert-Check ($codes.Count -eq 2 -and $item[1].Out.LastIndexOf('问题报告码：') -gt
            $item[1].Out.IndexOf('等待对手完成终局校验')) '联网结束报告早于终局校验或数量错误。'
        $parsed = [StreamDuel.Core.BugReport]::Parse($codes[1])
        Assert-Check ($parsed.Record.ToJson() -ceq (Read-Record $item[0]).ToJson()) '联网结束报告没有保留实际录像。'
        $endReports.Add($parsed)
        [void](Assert-Reproduce ($item[0] + '-reproduce') $codes[1])
    }
    Assert-Check ($endReports[0].StateHash -eq $endReports[1].StateHash -and $endReports[0].LogHash -ceq $endReports[1].LogHash) '联网终局报告不一致。'
    [void](Assert-Reproduce 'network-mid-reproduce' $hostMid)

    $dropHost = Start-Cli 'drop-host' @('--host', '0', '--bug-report', '--save-dir', (Save-Directory 'drop-host'))
    $dropPort = [int](Wait-Output $dropHost '(?m)^监听端口：([0-9]+)\r?$').Groups[1].Value
    $dropGuest = Start-Cli 'drop-guest' @('--join', "127.0.0.1:$dropPort", '--bug-report', '--save-dir', (Save-Directory 'drop-guest'))
    [void](Wait-Output $dropHost '选择行动')
    [void](Wait-Output $dropGuest '选择行动')
    $dropGuest.Process.Kill($true)
    Assert-Check ($dropGuest.Process.WaitForExit(5000)) '断线夹具子进程未停止。'
    $drop = Wait-Cli $dropHost 1
    Assert-Check (@(Bug-Codes $drop.Out).Count -eq 0 -and $drop.Err -match '[\u4e00-\u9fff]') '断线仍导出自动终局报告或缺中文错误。'
    Assert-NoSave 'drop-host'; Assert-NoSave 'drop-guest'
    $checks.Add('真实 TCP 选招/等待双方中途报告、未结算选择隐私、终局校验后导出并复现；断线不导出终局报告或保存')

    $sourcePolicy = Join-Path $binaryDirectory 'Content/PackPolicy.json'
    $policyHash = (Get-FileHash -LiteralPath $sourcePolicy -Algorithm SHA256).Hash
    $deployment = Join-Path $runDirectory 'policy-deployment'
    Copy-PolicyDeployment $binaryDirectory $deployment
    $script:cliDll = Join-Path $deployment 'Cli.dll'
    $policyPath = Join-Path $deployment 'Content/PackPolicy.json'
    Set-TestPolicy @('original') @() '报告来源包下架验收'
    Reject 'policy-pack' @('--reproduce', $code) '报告来源包下架验收'
    Reject 'policy-mixed-source' @('--reproduce', $mixedCode) '报告来源包下架验收'
    Reject 'policy-explicit-bypass' @('--reproduce', $mixedCode, '--pack', 'fan_asoul_vr') '报告来源包下架验收'
    Set-TestPolicy @() @($report.Record.Teams[0][0]) '报告主播下架验收'
    Reject 'policy-streamer' @('--reproduce', $code) '报告主播下架验收'
    Assert-Check ((Get-FileHash -LiteralPath $sourcePolicy -Algorithm SHA256).Hash -ceq $policyHash) '验收修改了原始下架配置。'
    $checks.Add('隔离发布副本的源包/混合源包/主播下架门禁，显式选包不能绕过；原始配置未修改')
}
catch { $failure = $_.Exception.GetBaseException().Message }
finally {
    $script:cliDll = $builtCliDll
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
Write-Output "PASS：$($checks.Count) 组问题报告验收；证据目录：$runDirectory"
