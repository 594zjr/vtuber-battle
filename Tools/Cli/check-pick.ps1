#requires -Version 7.0
param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug')

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$repositoryPrefix = $repository.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$binaryDirectory = Join-Path $PSScriptRoot "bin/$Configuration/net8.0"
$cliDll = Join-Path $binaryDirectory 'Cli.dll'
$coreDll = Join-Path $binaryDirectory 'StreamDuel.Core.dll'
foreach ($path in @($cliDll, $coreDll)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "请先运行 dotnet build Tools/Cli -c $Configuration。" }
}
$runDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ('obj/pick-verification/' + [Guid]::NewGuid().ToString('N'))))
if (-not $runDirectory.StartsWith($repositoryPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw '验收产物必须位于本仓库。' }
[void][IO.Directory]::CreateDirectory($runDirectory)
$utf8 = [Text.UTF8Encoding]::new($false)
$children = [Collections.Generic.List[object]]::new()
$checks = [Collections.Generic.List[string]]::new()
$failure = $null
$hostTeam = 'bell,enka,shiori,lumi,hane,momo'
$guestTeam = 'kei,yuan,nyako,buck,kasumi,bee'

function Assert-Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "$Message 证据目录：$runDirectory" }
}

function Read-Log([string]$Path) {
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
    $reader = [IO.StreamReader]::new($stream, $utf8)
    try { return $reader.ReadToEnd() } finally { $reader.Dispose() }
}

function Start-Cli([string]$Name, [string[]]$Parameters) {
    $save = Join-Path $runDirectory "$Name-saves"
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = 'dotnet'
    $start.ArgumentList.Add($cliDll)
    foreach ($parameter in ($Parameters + @('--save-dir', $save))) { $start.ArgumentList.Add($parameter) }
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
        Name = $Name; Process = $process; SaveDirectory = $save; Started = $false; Result = $null
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
    $child.OutTask = $process.StandardOutput.BaseStream.CopyToAsync($child.OutStream)
    $child.ErrTask = $process.StandardError.BaseStream.CopyToAsync($child.ErrStream)
    return $child
}

function Wait-Output($Child, [string]$Pattern, [int]$TimeoutMs = 20000) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    while ($timer.ElapsedMilliseconds -lt $TimeoutMs) {
        $match = [regex]::Match((Read-Log $Child.OutPath), $Pattern)
        if ($match.Success) { return $match }
        Assert-Check (-not $Child.Process.HasExited) "$($Child.Name) 在预期输出之前退出。"
        Start-Sleep -Milliseconds 25
    }
    throw "$($Child.Name) 等待输出超时。"
}

function Wait-Cli($Child, [int]$ExpectedExit, [int]$TimeoutMs = 120000) {
    if ($null -eq $Child.Result) {
        $timer = [Diagnostics.Stopwatch]::StartNew()
        while (-not $Child.Process.HasExited -and $timer.ElapsedMilliseconds -lt $TimeoutMs) { Start-Sleep -Milliseconds 25 }
        Assert-Check ($Child.Process.HasExited) "$($Child.Name) 未按时退出。"
        Assert-Check ($Child.OutTask.Wait(5000)) "$($Child.Name) 输出未结束。"
        Assert-Check ($Child.ErrTask.Wait(5000)) "$($Child.Name) 错误输出未结束。"
        $Child.OutStream.Flush()
        $Child.ErrStream.Flush()
        $Child.Result = [pscustomobject]@{ Out = (Read-Log $Child.OutPath); Err = (Read-Log $Child.ErrPath); ExitCode = $Child.Process.ExitCode }
    }
    Assert-Check ($Child.Result.ExitCode -eq $ExpectedExit) "$($Child.Name) 退出码应为 $ExpectedExit，实际为 $($Child.Result.ExitCode)。"
    return $Child.Result
}

function Assert-NoSave($Child) {
    Assert-Check (-not (Test-Path -LiteralPath $Child.SaveDirectory)) "$($Child.Name) 不应创建存档目录。"
}

function Read-Completed($Child, [int]$Count, [bool]$Ban, [int]$Side = 0) {
    $result = Wait-Cli $Child 0
    Assert-Check ($result.Err.Length -eq 0 -and $result.Out.Contains('对局结束：')) "$($Child.Name) 未正常完成对局。"
    $files = @(Get-ChildItem -LiteralPath (Join-Path $Child.SaveDirectory 'replays') -File -Filter '*.json')
    Assert-Check ($files.Count -eq 1) "$($Child.Name) 完成后应保存一份回放。"
    $json = [IO.File]::ReadAllText($files[0].FullName, $utf8)
    $record = [StreamDuel.Core.BattleRecord]::FromJson($json)
    Assert-Check ($record.PickCount -eq $Count -and $record.BanOne -eq $Ban) '回放选人人数或禁用规则错误。'
    Assert-Check ($record.Teams[0].Count -eq 6 -and $record.Teams[1].Count -eq 6) '回放必须保留原始公开六人。'
    $picked = @($record.Events | Where-Object { $_.Type -eq [StreamDuel.Core.BattleRecordEventType]::Pick })
    Assert-Check ($picked.Count -eq $(if ($Count -eq 0) { 0 } else { 2 })) '回放选人事件数量错误。'
    if ($Count -gt 0) {
        Assert-Check (@($record.Events | Where-Object { $_.Type -eq [StreamDuel.Core.BattleRecordEventType]::Lead }).Count -eq 0) '选人后不应再出现额外首发选择。'
        foreach ($pick in $picked) {
            Assert-Check ($pick.Indices.Count -eq $Count -and @($pick.Indices | Select-Object -Unique).Count -eq $Count) '记录的选人数量或去重错误。'
            if ($Ban) {
                $banEvent = @($record.Events | Where-Object { $_.Type -eq [StreamDuel.Core.BattleRecordEventType]::Ban -and $_.Side -eq 1 - $pick.Side })
                Assert-Check ($banEvent.Count -eq 1 -and -not $pick.Indices.Contains($banEvent[0].To)) '出战包含已禁用号位。'
            }
        }
    }
    $battle = [StreamDuel.Core.Replay]::Run($db, $record)
    Assert-Check ($battle.Phase -eq [StreamDuel.Core.Phase]::Over) '回放未到终局。'
    $size = if ($Count -gt 0) { $Count } elseif ($Ban) { 5 } else { 6 }
    Assert-Check ($battle.Sides[0].Team.Count -eq $size -and $battle.Sides[1].Team.Count -eq $size) '重放后实际出战人数错误。'
    $profile = [StreamDuel.Core.Profile]::FromJson([IO.File]::ReadAllText((Join-Path $Child.SaveDirectory 'profile.json'), $utf8))
    $actualPicks = @($profile.Packs['original'].Picks.Keys | Sort-Object)
    $expectedPicks = @($battle.Sides[$Side].Team | ForEach-Object { $_.Def.Id } | Sort-Object)
    Assert-Check (($actualPicks -join ',') -ceq ($expectedPicks -join ',')) '档案应仅统计本方实际入选成员。'
    return [pscustomobject]@{ Result = $result; Record = $record; Battle = $battle; Json = $json; Path = $files[0].FullName }
}

function Start-Pair([string]$Name, [string[]]$HostExtra, [string[]]$GuestExtra) {
    $hostChild = Start-Cli "$Name-host" (@('--host', '0', '--seed', '17', '--team', $hostTeam, '--hash') + $HostExtra)
    $port = (Wait-Output $hostChild '(?m)^监听端口：([0-9]+)\r?$').Groups[1].Value
    $guestChild = Start-Cli "$Name-guest" (@('--join', "127.0.0.1:$port", '--seed', '99', '--team', $guestTeam, '--hash') + $GuestExtra)
    return [pscustomobject]@{ Host = $hostChild; Guest = $guestChild }
}

try {
    [void][Reflection.Assembly]::LoadFrom($coreDll)
    $db = [StreamDuel.Core.ContentDb]::FromJson(
        [IO.File]::ReadAllText((Join-Path $binaryDirectory 'Content/StreamDuelRules.json'), $utf8),
        [IO.File]::ReadAllText((Join-Path $binaryDirectory 'Content/Packs/original.json'), $utf8))

    $invalid = @(
        @('--pick'), @('--pick', '0'), @('--pick', '2'), @('--pick', '5'), @('--pick', '-1'),
        @('--pick', '3.0'), @('--pick', ' 3'), @('--pick', '3', '--pick', '4'),
        @('--pick', '3', '--replay', 'missing'), @('--pick', '3', '--replays'),
        @('--pick', '3', '--list-packs'), @('--pick', '3', '--glossary'),
        @('--pick', '3', '--calc', 'bell', '1', 'kei')
    )
    for ($i = 0; $i -lt $invalid.Count; $i++) {
        $child = Start-Cli "invalid-$i" $invalid[$i]
        $result = Wait-Cli $child 1 10000
        Assert-Check ([regex]::IsMatch($result.Err, '[\u4e00-\u9fff]')) '无效参数未给出中文错误。'
        Assert-NoSave $child
    }
    $checks.Add('无效人数、重复、缺值与独立模式冲突均中文拒绝且不写存档')

    $common = @('--auto', '--seed', '17', '--team', $hostTeam, '--hash')
    $first = Read-Completed (Start-Cli 'local-3' ($common + @('--pick', '3'))) 3 $false
    $again = Read-Completed (Start-Cli 'local-3-repeat' ($common + @('--pick', '3'))) 3 $false
    Assert-Check ($first.Json -ceq $again.Json) '相同种子与选人设置的完整回放不确定。'
    [void](Read-Completed (Start-Cli 'local-4-ban-lead' ($common + @('--pick', '4', '--ban', '--lead'))) 4 $true)
    [void](Read-Completed (Start-Cli 'local-3-easy' ($common + @('--pick', '3', '--level', 'easy'))) 3 $false)
    [void](Read-Completed (Start-Cli 'local-3-hard' ($common + @('--pick', '3', '--level', 'hard'))) 3 $false)
    [void](Read-Completed (Start-Cli 'local-classic' $common) 0 $false)
    $replay = Start-Cli 'replay-3' @('--replay', $first.Path, '--auto')
    [void](Wait-Cli $replay 0)
    Assert-NoSave $replay
    $checks.Add('本地3/4人、禁用+选序取代首发、Easy/Hard、经典模式、确定性和回放')

    $manual = Start-Cli 'manual-order' @('--pick', '3', '--team', $hostTeam, '--seed', '17')
    [void](Wait-Output $manual '请选择 3 个原始号位')
    foreach ($line in @('1,1,2', '1,2', '1,2,7')) {
        $manual.Process.StandardInput.WriteLine($line)
        $manual.Process.StandardInput.Flush()
    }
    [void](Wait-Output $manual '(?s)(选择无效：.*){3}')
    $manual.Process.StandardInput.WriteLine('6,2,4')
    $manual.Process.StandardInput.Flush()
    [void](Wait-Output $manual '选择行动（q 退出）')
    $manual.Process.StandardInput.WriteLine('q')
    $manual.Process.StandardInput.Flush()
    $result = Wait-Cli $manual 0
    Assert-Check ([regex]::IsMatch($result.Out, '你的队伍：1号 [^\r\n]+（momo，[^\r\n]+；2号 [^\r\n]+（enka，[^\r\n]+；3号 [^\r\n]+（lumi，')) '手动选序没有成为出战顺序。'
    Assert-NoSave $manual
    $checks.Add('人工拒绝重复/数量/越界，接受原始6,2,4顺序，退出不记胜负')

    foreach ($count in @(3, 4)) {
        $hostExtra = @('--pick', [string]$count, '--auto')
        if ($count -eq 4) { $hostExtra += '--ban' }
        $pair = Start-Pair "network-$count" $hostExtra @('--pick', $(if ($count -eq 3) { '4' } else { '3' }), '--auto')
        $hostDone = Read-Completed $pair.Host $count ($count -eq 4) 0
        $guestDone = Read-Completed $pair.Guest $count ($count -eq 4) 1
        $hostHash = [regex]::Match($hostDone.Result.Out, '(?m)^最终状态哈希：([0-9a-f]{16})\r?$').Groups[1].Value
        $guestHash = [regex]::Match($guestDone.Result.Out, '(?m)^最终状态哈希：([0-9a-f]{16})\r?$').Groups[1].Value
        Assert-Check ($hostHash.Length -eq 16 -and $hostHash -ceq $guestHash) '联网终局哈希不一致。'
        Assert-Check ($hostHash -ceq $hostDone.Battle.StateHash().ToString('x16') -and $hostHash -ceq $guestDone.Battle.StateHash().ToString('x16')) '联网回放哈希不一致。'
        Assert-Check ($guestDone.Result.Out.Contains('选人与禁用采用房主设置')) '客机未说明采用房主规则。'
    }
    $checks.Add('本机TCP 3/4人及禁用→选人完整对战，房主覆盖客机人数，两端与回放哈希一致')

    $pair = Start-Pair 'secret-pick' @('--pick', '3') @('--pick', '4')
    [void](Wait-Output $pair.Host '请选择 3 个原始号位')
    [void](Wait-Output $pair.Guest '请选择 3 个原始号位')
    $pair.Host.Process.StandardInput.WriteLine('6,2,4')
    $pair.Host.Process.StandardInput.Flush()
    [void](Wait-Output $pair.Host '等待对手……')
    Start-Sleep -Milliseconds 250
    Assert-Check (-not (Read-Log $pair.Host.OutPath).Contains('确认出战阵容') -and -not (Read-Log $pair.Guest.OutPath).Contains('确认出战阵容')) '对方提交前泄露了选人日志。'
    $pair.Guest.Process.StandardInput.WriteLine('q')
    $pair.Guest.Process.StandardInput.Flush()
    [void](Wait-Cli $pair.Guest 0 10000)
    $disconnected = Wait-Cli $pair.Host 1 10000
    Assert-Check ($disconnected.Err.Contains('对方已断开')) '等待对方选人时没有及时发现断线。'
    Assert-NoSave $pair.Host
    Assert-NoSave $pair.Guest
    $pair = Start-Pair 'pick-input-disconnect' @('--pick', '3') @()
    [void](Wait-Output $pair.Host '请选择 3 个原始号位')
    [void](Wait-Output $pair.Guest '请选择 3 个原始号位')
    $pair.Guest.Process.StandardInput.WriteLine('q')
    $pair.Guest.Process.StandardInput.Flush()
    [void](Wait-Cli $pair.Guest 0 10000)
    $disconnected = Wait-Cli $pair.Host 1 10000
    Assert-Check ($disconnected.Err.Contains('对方已断开')) '选人输入等待时没有及时发现断线。'
    Assert-NoSave $pair.Host
    Assert-NoSave $pair.Guest
    $checks.Add('秘密选人提交前不公开名单，选人输入及已提交等待均响应断线且不保存')
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
Write-Output "PASS：$($checks.Count) 组选人 CLI 检查；证据目录：$runDirectory"
