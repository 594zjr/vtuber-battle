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
$runDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ('obj/timer-verification/' + [Guid]::NewGuid().ToString('N'))))
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
    $invalid = @('NaN,1', 'Infinity,1', '-1,2', '1,-2', '1e309,0', '1', '1,2,3', ',2', '1,', '1, 2')
    for ($index = 0; $index -lt $invalid.Count; $index++) {
        $child = Start-Cli "invalid-$index" @('--timer', $invalid[$index])
        $child.Process.StandardInput.Close()
        $result = Wait-Cli $child 1
        Assert-Check ($result.Err.Contains('--timer')) '非法预算未给出明确参数错误。'
        Assert-NoSave $child
    }
    foreach ($case in @(
        @{ Name = 'duplicate'; Args = @('--timer', '1,2', '--timer', '3,4') },
        @{ Name = 'replay'; Args = @('--timer', '1,2', '--replay', (Join-Path $runDirectory 'missing.json')) },
        @{ Name = 'replay-code'; Args = @('--timer', '1,2', '--replay-code', 'SD1-invalid') }
    )) {
        $child = Start-Cli $case.Name $case.Args
        $child.Process.StandardInput.Close()
        $result = Wait-Cli $child 1
        Assert-Check ($result.Err.Contains('--timer')) '不支持的组合未在读取文件或开局前拒绝。'
        Assert-NoSave $child
    }
    $checks.Add('非法、非有限、重复参数及回放组合均拒绝')

    $child = Start-Cli 'zero' @('--timer', '0,0', '--auto', '--team', $team)
    $result = Wait-Cli $child 0
    $zeroCompleted = $result.Out.Contains('你：超时，提交默认跳过') -and $result.Out.Contains('电脑：超时，提交默认跳过') -and $result.Out.Contains('对局结束：')
    Assert-Check $zeroCompleted '零预算没有让双方采用默认动作并正常结束。'
    $checks.Add('零预算自动模式双方立即采用默认动作')

    $coreDll = Join-Path $PSScriptRoot "bin/$Configuration/net8.0/StreamDuel.Core.dll"
    Assert-Check (Test-Path -LiteralPath $coreDll -PathType Leaf) "请先运行 dotnet build Tools/Cli -c $Configuration。"
    $assembly = [Reflection.Assembly]::LoadFrom($coreDll)
    Assert-Check ([IO.Path]::GetFullPath($assembly.Location) -eq [IO.Path]::GetFullPath($coreDll)) '请在未加载其他 Core 的新 PowerShell 进程运行。'
    $preset = [StreamDuel.Core.TeamPreset]::new()
    $preset.Name = '计时「原六人」队伍'
    $preset.PackId = 'original'
    $preset.Ids.AddRange($team.Split(','))
    $teamCode = [StreamDuel.Core.TeamCode]::Encode($preset)
    $child = Start-Cli 'zero-team-code' @('--timer', '0,0', '--auto', '--team-code', $teamCode, '--export-team', '--share', '--seed', '7')
    $result = Wait-Cli $child 0
    Assert-Check ($result.Err.Length -eq 0 -and $result.Out.Contains('对局结束：') -and
        $result.Out.Contains('你：超时，提交默认跳过') -and $result.Out.Contains('电脑：超时，提交默认跳过')) '队伍码与零预算共存时未正常超时完成对局。'
    $exported = [regex]::Matches($result.Out, '(?m)^本方队伍码：(SDT1-[A-Za-z0-9_-]+)\r?$')
    Assert-Check ($exported.Count -eq 1 -and $exported[0].Groups[1].Value -ceq $teamCode) '计时对局未原样导出队伍码。'
    $decoded = [StreamDuel.Core.TeamCode]::Decode($exported[0].Groups[1].Value)
    Assert-Check ($decoded.Name -ceq $preset.Name -and $decoded.PackId -ceq 'original' -and
        ($decoded.Ids -join ',') -ceq $team) '计时对局的导出码丢失自定义名称或原六人顺序。'
    $shared = [regex]::Matches($result.Out, '(?m)^分享码：(SD1-[A-Za-z0-9_-]+)\r?$')
    Assert-Check ($shared.Count -eq 1) '队伍码计时对局未恰好导出一份回放分享码。'
    $sharedRecord = [StreamDuel.Core.ShareCode]::Decode($shared[0].Groups[1].Value)
    Assert-Check ($sharedRecord.Seed -eq 7 -and $sharedRecord.PackId -ceq 'original' -and
        ($sharedRecord.Teams[0] -join ',') -ceq $team -and $sharedRecord.Events.Count -gt 0) '计时对局分享码未保留原始队伍或完整行动记录。'
    Assert-Check (Test-Path -LiteralPath (Join-Path $child.SaveDirectory 'profile.json')) '队伍码计时对局完成后未保存档案。'
    $checks.Add('T56 自定义队伍码与零预算完整对局共存，名称和原六人原样导出且可分享')

    $child = Start-Cli 'idle-then-input' @('--timer', '3,0', '--team', $team, '--seed', '7')
    [void](Wait-Output $child '选择行动（q 退出）>')
    [void](Wait-Output $child '你：超时，提交默认跳过')
    [void](Wait-Output $child '(?s)===== 第 2 回合 =====.*选择行动（q 退出）>')
    $child.Process.StandardInput.WriteLine('1')
    [void](Wait-Output $child '【第 2 回合 · 回合】')
    $output = Read-Log $child.OutPath
    Assert-Check ([regex]::Matches($output, '你：超时，提交默认跳过').Count -eq 1) '下一回合输入被旧读取吞掉或重复超时。'
    $child.Process.StandardInput.WriteLine('q')
    $result = Wait-Cli $child 0
    Assert-NoSave $child
    $checks.Add('保持 stdin 开放时自动超时，下一回合一行输入即可提交')

    $child = Start-Cli 'invalid-input' @('--timer', '2,0', '--team', $team)
    [void](Wait-Output $child '选择行动（q 退出）>')
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $timedOut = $false
    while ($timer.ElapsedMilliseconds -lt 4500) {
        if ((Read-Log $child.OutPath).Contains('你：超时，提交默认跳过')) { $timedOut = $true; break }
        $child.Process.StandardInput.WriteLine('invalid')
        Start-Sleep -Milliseconds 200
    }
    Assert-Check $timedOut '持续无效输入延长了同一阶段预算。'
    $child.Process.StandardInput.WriteLine('q')
    $result = Wait-Cli $child 0
    Assert-NoSave $child
    $checks.Add('持续无效输入不重置计时')

    $child = Start-Cli 'eof' @('--timer', '30,30', '--team', $team)
    $child.Process.StandardInput.Close()
    $result = Wait-Cli $child 0
    Assert-Check ($result.Out.Contains('已退出，对局未完成。') -and -not $result.Out.Contains('你：超时')) 'EOF 没有保留立即退出行为。'
    Assert-NoSave $child
    $checks.Add('计时模式 EOF 正常退出且不保存')

    $child = Start-Cli 'untimed-auto' @('--auto', '--team', $team, '--seed', '7')
    $result = Wait-Cli $child 0
    Assert-Check ($result.Out.Contains('对局结束：') -and -not $result.Out.Contains('计时（')) '未启用计时的本地自动对战出现回归。'
    $checks.Add('无计时的本地自动对战正常结束')

    $pair = Start-Pair 'host-timer' @('--timer', '0.3,0') @('--auto', '--timer', '0,0')
    $results = Assert-PairCompleted $pair
    Assert-Check ($results.Host.Out.Contains('房主：超时，提交默认跳过')) '房主无人输入时没有自动出招。'
    Assert-Check ($results.Guest.Out.Contains('已启用房主计时：每阶段 0.3 秒；总时间 0 秒。')) '客机没有采用房主预算。'
    $checks.Add('联网无人输入自动提交，客机忽略本地预算，两端终局哈希一致')

    $pair = Start-Pair 'host-untimed' @('--auto') @('--auto', '--timer', '0,0')
    $results = Assert-PairCompleted $pair
    Assert-Check ($results.Guest.Out.Contains('房主未启用计时') -and -not $results.Guest.Out.Contains('计时（')) '客机擅自启用了房主没有声明的计时。'
    $checks.Add('房主未启用计时时，客机参数不启用计时')
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
Write-Output "PASS：$($checks.Count) 项计时 CLI 检查；证据目录：$runDirectory"
