#requires -Version 7.0
param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug')

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$repositoryPrefix = $repository.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$cliDll = Join-Path $PSScriptRoot "bin/$Configuration/net8.0/Cli.dll"
if (-not (Test-Path -LiteralPath $cliDll -PathType Leaf)) {
    throw "请先运行 dotnet build Tools/Cli -c $Configuration。"
}
$runDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ('obj/ai-takeover-verification/' + [Guid]::NewGuid().ToString('N'))))
if (-not $runDirectory.StartsWith($repositoryPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw '验收产物必须位于本仓库内。'
}
[void][IO.Directory]::CreateDirectory($runDirectory)
$utf8 = [Text.UTF8Encoding]::new($false)
$children = [Collections.Generic.List[object]]::new()
$checks = [Collections.Generic.List[string]]::new()
$failure = $null
$team = 'bell,enka,shiori,lumi,hane,momo'
$foeTeam = 'kei,yuan,nyako,buck,kasumi,bee'

function Assert-Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "$Message 证据目录：$runDirectory" }
}

function Read-Log([string]$Path) {
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
    $reader = [IO.StreamReader]::new($stream, $utf8)
    try { return $reader.ReadToEnd() } finally { $reader.Dispose() }
}

function Start-Cli([string]$Name, [string[]]$Parameters) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = 'dotnet'
    $start.ArgumentList.Add($cliDll)
    foreach ($parameter in $Parameters) { $start.ArgumentList.Add($parameter) }
    $saveDirectory = Join-Path $runDirectory "$Name-saves"
    $start.ArgumentList.Add('--save-dir')
    $start.ArgumentList.Add($saveDirectory)
    $start.WorkingDirectory = $runDirectory
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
        Name = $Name; Process = $process; SaveDirectory = $saveDirectory; Started = $false
        OutPath = (Join-Path $runDirectory "$Name.stdout.log"); ErrPath = (Join-Path $runDirectory "$Name.stderr.log")
        OutStream = $null; ErrStream = $null; OutTask = $null; ErrTask = $null
    }
    $children.Add($child)
    $child.OutStream = [IO.FileStream]::new($child.OutPath, [IO.FileMode]::CreateNew,
        [IO.FileAccess]::Write, [IO.FileShare]::ReadWrite, 1, [IO.FileOptions]::Asynchronous)
    $child.ErrStream = [IO.FileStream]::new($child.ErrPath, [IO.FileMode]::CreateNew,
        [IO.FileAccess]::Write, [IO.FileShare]::ReadWrite, 1, [IO.FileOptions]::Asynchronous)
    Assert-Check ($process.Start()) "$Name 无法启动。"
    $child.Started = $true
    # 保持 stdin 开放，才能验证真的无人输入；原始流复制也能捕捉无换行提示。
    $child.OutTask = $process.StandardOutput.BaseStream.CopyToAsync($child.OutStream)
    $child.ErrTask = $process.StandardError.BaseStream.CopyToAsync($child.ErrStream)
    return $child
}

function Wait-Output($Child, [string]$Pattern, [int]$TimeoutMs = 15000) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    while ($timer.ElapsedMilliseconds -lt $TimeoutMs) {
        $match = [regex]::Match((Read-Log $Child.OutPath), $Pattern)
        if ($match.Success) { return $match }
        Assert-Check (-not $Child.Process.HasExited) "$($Child.Name) 在预期输出之前退出。"
        Start-Sleep -Milliseconds 25
    }
    throw "$($Child.Name) 等待输出超时。证据目录：$runDirectory"
}

function Wait-Cli($Child, [int]$ExpectedExit, [int]$TimeoutMs = 60000) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    while (-not $Child.Process.HasExited -and $timer.ElapsedMilliseconds -lt $TimeoutMs) {
        Start-Sleep -Milliseconds 25
    }
    Assert-Check ($Child.Process.HasExited) "$($Child.Name) 未按时退出。"
    Assert-Check ($Child.OutTask.Wait(5000)) "$($Child.Name) 输出未结束。"
    Assert-Check ($Child.ErrTask.Wait(5000)) "$($Child.Name) 错误输出未结束。"
    $Child.OutStream.Flush()
    $Child.ErrStream.Flush()
    Assert-Check ($Child.Process.ExitCode -eq $ExpectedExit) "$($Child.Name) 退出码应为 $ExpectedExit，实际为 $($Child.Process.ExitCode)。"
    return [pscustomobject]@{ Out = (Read-Log $Child.OutPath); Err = (Read-Log $Child.ErrPath) }
}

function Assert-NoSave($Child) {
    Assert-Check (-not (Test-Path -LiteralPath (Join-Path $Child.SaveDirectory 'profile.json'))) '未完成对局产生档案。'
    $replays = Join-Path $Child.SaveDirectory 'replays'
    if (Test-Path -LiteralPath $replays) {
        Assert-Check (@(Get-ChildItem -LiteralPath $replays -File -Filter '*.json').Count -eq 0) '未完成对局产生回放。'
    }
}

function Start-Pair([string]$Name, [string[]]$HostExtra, [string[]]$GuestExtra) {
    $hostChild = Start-Cli "$Name-host" (@('--host', '0', '--team', $team, '--seed', '7') + $HostExtra)
    $port = (Wait-Output $hostChild '(?m)^监听端口：([0-9]+)\r?$').Groups[1].Value
    $guestChild = Start-Cli "$Name-guest" (@('--join', "127.0.0.1:$port", '--team', $foeTeam) + $GuestExtra)
    return [pscustomobject]@{ Host = $hostChild; Guest = $guestChild }
}

function Assert-PairCompleted($Pair) {
    $hostResult = Wait-Cli $Pair.Host 0
    $guestResult = Wait-Cli $Pair.Guest 0
    $hostHash = [regex]::Match($hostResult.Out, '(?m)^最终状态哈希：([0-9a-f]{16})\r?$')
    $guestHash = [regex]::Match($guestResult.Out, '(?m)^最终状态哈希：([0-9a-f]{16})\r?$')
    Assert-Check ($hostHash.Success -and $guestHash.Success -and $hostHash.Groups[1].Value -eq $guestHash.Groups[1].Value) '联网终局哈希不一致。'
    foreach ($child in @($Pair.Host, $Pair.Guest)) {
        Assert-Check (Test-Path -LiteralPath (Join-Path $child.SaveDirectory 'profile.json')) '联网完成后没有保存档案。'
    }
    return [pscustomobject]@{ Host = $hostResult; Guest = $guestResult }
}

try {
    foreach ($case in @(
        @{ Name = 'local'; Args = @('--ai-takeover') },
        @{ Name = 'duplicate'; Args = @('--host', '0', '--ai-takeover', '--ai-takeover') },
        @{ Name = 'guest'; Args = @('--join', '127.0.0.1:1', '--ai-takeover') },
        @{ Name = 'replay'; Args = @('--ai-takeover', '--replay', 'missing.json') },
        @{ Name = 'hotseat'; Args = @('--hotseat', '--ai-takeover') }
    )) {
        $child = Start-Cli $case.Name $case.Args
        $child.Process.StandardInput.Close()
        $result = Wait-Cli $child 1
        Assert-Check ($result.Err.Contains('--ai-takeover')) '不支持的组合未在开局前拒绝。'
        Assert-NoSave $child
    }
    $checks.Add('参数限房主声明，拒绝重复和不支持的模式')

    $pair = Start-Pair 'single-timeout' @('--ai-takeover', '--timer', '0.15,0') @('--auto')
    $results = Assert-PairCompleted $pair
    Assert-Check ($results.Host.Out.Contains('电脑代打') -and $results.Guest.Out.Contains('房主已启用电脑代打')) '客机未获知房主选项或未提示代打。'
    foreach ($child in @($pair.Host, $pair.Guest)) {
        $files = @(Get-ChildItem -LiteralPath (Join-Path $child.SaveDirectory 'replays') -Filter '*.json' -File)
        Assert-Check ($files.Count -eq 1) '完成对局没有一份录像。'
        $json = Get-Content -LiteralPath $files[0].FullName -Raw
        Assert-Check ($json.Contains('"aiTakeoverMask":1') -and -not $json.Contains('"offlineContinuation"')) '在线单边代打记录缺少标记或误标离线。'
    }
    $checks.Add('无人输入单边耗尽后持续代打，客机自动采纳，双端终局哈希一致且正常记战绩')

    $pair = Start-Pair 'both-timeout' @('--ai-takeover', '--timer', '0,0') @()
    $results = Assert-PairCompleted $pair
    foreach ($child in @($pair.Host, $pair.Guest)) {
        $file = @(Get-ChildItem -LiteralPath (Join-Path $child.SaveDirectory 'replays') -Filter '*.json' -File)[0]
        Assert-Check ((Get-Content -LiteralPath $file.FullName -Raw).Contains('"aiTakeoverMask":3')) '双方零预算未由两侧 AI 持续代打。'
    }
    $checks.Add('双方零预算和开放 stdin 不阻塞，双端 AI 完成整局')

    $pair = Start-Pair 'enabled-no-timeout' @('--ai-takeover', '--auto') @('--auto')
    $results = Assert-PairCompleted $pair
    foreach ($child in @($pair.Host, $pair.Guest)) {
        $file = @(Get-ChildItem -LiteralPath (Join-Path $child.SaveDirectory 'replays') -Filter '*.json' -File)[0]
        $json = Get-Content -LiteralPath $file.FullName -Raw
        Assert-Check (-not $json.Contains('"aiTakeoverMask"') -and -not $json.Contains('"offlineContinuation"')) '未发生代打的记录被污染。'
    }
    $checks.Add('开关启用但无超时，真人协议正常完成且录像不增加无效标记')

    $offlineReport = Join-Path $runDirectory 'offline-report.md'
    $pair = Start-Pair 'disconnect' @('--ai-takeover', '--auto', '--hash', '--report', $offlineReport) @()
    [void](Wait-Output $pair.Guest '选择行动（q 退出）>')
    $pair.Guest.Process.Kill($true)
    [void]$pair.Guest.Process.WaitForExit(5000)
    $result = Wait-Cli $pair.Host 0
    Assert-Check ($result.Out.Contains('已进入离线续局') -and $result.Out.Contains('离线终局哈希：') -and $result.Out.Contains('未经双方终局确认')) '断线未明确提示本地分叉结果。'
    Assert-Check (-not $result.Out.Contains('最终状态哈希：') -and -not $result.Out.Contains('等待对手完成终局校验')) '离线结果被作为已确认的联网终局。'
    Assert-Check (-not (Test-Path -LiteralPath (Join-Path $pair.Host.SaveDirectory 'profile.json'))) '离线分叉被累计为联网战绩。'
    $offlineFiles = @(Get-ChildItem -LiteralPath (Join-Path $pair.Host.SaveDirectory 'replays') -Filter '*.json' -File)
    Assert-Check ($offlineFiles.Count -eq 1) '离线完成后未保留可回看的录像。'
    $offlineJson = Get-Content -LiteralPath $offlineFiles[0].FullName -Raw
    Assert-Check ($offlineJson.Contains('"offlineContinuation":true') -and $offlineJson.Contains('"aiTakeoverMask":2')) '离线录像缺少来源或代打方标记。'
    Assert-Check ((Get-Content -LiteralPath $offlineReport -Raw).Contains('离线续局（未经双方终局确认）')) '离线战报未标明来源。'
    $replayReport = Join-Path $runDirectory 'offline-replay-report.md'
    $replay = Start-Cli 'offline-replay' @('--auto', '--replay', $offlineFiles[0].FullName, '--report', $replayReport)
    $replayResult = Wait-Cli $replay 0
    Assert-Check ($replayResult.Out.Contains('电脑代打') -and $replayResult.Out.Contains('离线续局（未经双方终局确认）')) '离线录像回看丢失标记。'
    Assert-Check ((Get-Content -LiteralPath $replayReport -Raw).Contains('离线续局（未经双方终局确认）')) '回放导出的战报未标明离线来源。'
    Assert-NoSave $replay
    $checks.Add('真实 TCP 断线超宽限后离线完成、保存可回放标记，不累计联网战绩')

    $pair = Start-Pair 'disabled-disconnect' @('--auto') @()
    [void](Wait-Output $pair.Guest '选择行动（q 退出）>')
    $pair.Guest.Process.Kill($true)
    [void]$pair.Guest.Process.WaitForExit(5000)
    $result = Wait-Cli $pair.Host 1
    Assert-Check (-not $result.Out.Contains('已进入离线续局')) '默认关闭却启用了离线代打。'
    Assert-NoSave $pair.Host
    $checks.Add('默认关闭时真实 TCP 断线沿用失败且不保存')
}
catch { $failure = $_.Exception.Message }
finally {
    foreach ($child in $children) {
        try {
            if ($child.Started -and -not $child.Process.HasExited) {
                $child.Process.Kill($true)
                [void]$child.Process.WaitForExit(5000)
            }
            if ($null -ne $child.OutTask) { [void]$child.OutTask.Wait(5000) }
            if ($null -ne $child.ErrTask) { [void]$child.ErrTask.Wait(5000) }
        }
        catch { if ($null -eq $failure) { $failure = "清理测试进程失败：$($_.Exception.Message)" } }
        finally {
            if ($null -ne $child.OutStream) { $child.OutStream.Dispose() }
            if ($null -ne $child.ErrStream) { $child.ErrStream.Dispose() }
            $child.Process.Dispose()
        }
    }
    $summary = [ordered]@{ status = $(if ($null -eq $failure) { 'PASS' } else { 'FAIL' }); checks = $checks.ToArray(); failure = $failure }
    [IO.File]::WriteAllText((Join-Path $runDirectory 'summary.json'), ($summary | ConvertTo-Json -Depth 4), $utf8)
}
if ($null -ne $failure) { throw "$failure 证据目录：$runDirectory" }
Write-Output "PASS：$($checks.Count) 项电脑代打 CLI 检查；证据目录：$runDirectory"
