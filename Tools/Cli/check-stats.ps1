#requires -Version 7.0
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$prefix = $repository.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$binaryDirectory = Join-Path $PSScriptRoot "bin/$Configuration/net8.0"
$cliDll = Join-Path $binaryDirectory 'Cli.dll'
if (-not (Test-Path -LiteralPath $cliDll -PathType Leaf)) {
    throw "请先运行 dotnet build Tools/Cli -c $Configuration。"
}
$runDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ('obj/stats-verification/' + [Guid]::NewGuid().ToString('N'))))
if (-not $runDirectory.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw '验收产物必须位于本工作树内。'
}
[void][IO.Directory]::CreateDirectory($runDirectory)
$utf8 = [Text.UTF8Encoding]::new($false)
$children = [Collections.Generic.List[object]]::new()
$checks = [Collections.Generic.List[string]]::new()
$failure = $null
$finalHash = $null
$hostTeam = @('bell', 'enka', 'shiori', 'lumi', 'hane', 'momo')
$guestTeam = @('kei', 'yuan', 'nyako', 'buck', 'kasumi', 'bee')

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
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    $child = [pscustomobject]@{
        Name = $Name; Parameters = $Parameters; Process = $process; Started = $false
        OutPath = (Join-Path $runDirectory "$Name.stdout.log")
        ErrPath = (Join-Path $runDirectory "$Name.stderr.log")
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

function Wait-Cli($Child, [int]$ExpectedExit = 0, [int]$TimeoutMs = 90000) {
    if ($null -eq $Child.Result) {
        $timer = [Diagnostics.Stopwatch]::StartNew()
        while (-not $Child.Process.HasExited -and $timer.ElapsedMilliseconds -lt $TimeoutMs) {
            Start-Sleep -Milliseconds 25
        }
        Assert-Check ($Child.Process.HasExited) "$($Child.Name) 未在限定时间内退出。"
        Assert-Check ($Child.OutTask.Wait(5000)) "$($Child.Name) 标准输出未结束。"
        Assert-Check ($Child.ErrTask.Wait(5000)) "$($Child.Name) 标准错误未结束。"
        $Child.OutStream.Flush()
        $Child.ErrStream.Flush()
        $Child.Result = [pscustomobject]@{
            ExitCode = $Child.Process.ExitCode; Out = (Read-Log $Child.OutPath); Err = (Read-Log $Child.ErrPath)
        }
    }
    Assert-Check ($Child.Result.ExitCode -eq $ExpectedExit) "$($Child.Name) 退出码应为 $ExpectedExit，实际为 $($Child.Result.ExitCode)。"
    return $Child.Result
}

function Wait-Output($Child, [string]$Pattern) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    while ($timer.ElapsedMilliseconds -lt 15000) {
        $match = [regex]::Match((Read-Log $Child.OutPath), $Pattern)
        if ($match.Success) { return $match }
        Assert-Check (-not $Child.Process.HasExited) "$($Child.Name) 在出现预期输出前退出。"
        Start-Sleep -Milliseconds 25
    }
    throw "等待 $($Child.Name) 输出超时。"
}

function Save-Fingerprint([string]$Directory) {
    return ((Get-ChildItem -LiteralPath $Directory -File -Recurse -Force | Sort-Object FullName | ForEach-Object {
        [IO.Path]::GetRelativePath($Directory, $_.FullName) + ':' + $_.Length + ':' +
            $_.LastWriteTimeUtc.Ticks + ':' + (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    }) -join "`n")
}

function Only-LibraryFile([string]$SaveDirectory) {
    $files = @(Get-ChildItem -LiteralPath (Join-Path $SaveDirectory 'replay-library') -File -Filter '*.json' |
        Where-Object Name -ne 'index.json')
    Assert-Check ($files.Count -eq 1) '真实完成对局应有且仅有一份库录像。'
    return $files[0].FullName
}

function Read-Json([string]$Path) { return [IO.File]::ReadAllText($Path, $utf8) | ConvertFrom-Json -AsHashtable }
function Write-Json([string]$Path, $Value) { [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 100), $utf8) }

function Query-ReadOnly([string]$Name, [string]$Directory, [string[]]$Extra = @()) {
    $before = Save-Fingerprint $Directory
    $result = Wait-Cli (Start-Cli $Name (@('--stats', '--save-dir', $Directory) + $Extra))
    Assert-Check ((Save-Fingerprint $Directory) -ceq $before) "$Name 改动了存档文件内容、时间或名单。"
    foreach ($heading in @('本机对战统计（只读）', '主播选用：', '常用阵容（前 10）：', '对电脑战绩：', '最近对局（前 10）：', '保留录像中的连胜/连败：')) {
        Assert-Check ($result.Out.Contains($heading)) "$Name 缺少统计分区：$heading"
    }
    return $result
}

try {
    $emptySave = Join-Path $runDirectory 'empty-not-created'
    $empty = Wait-Cli (Start-Cli 'empty' @('--stats', '--save-dir', $emptySave))
    Assert-Check (-not (Test-Path -LiteralPath $emptySave)) '查看空档案创建了目录。'
    Assert-Check ($empty.Out.Contains('保留录像：0 局') -and $empty.Out.Contains('当前连胜 0')) '空档案输出不正确。'
    $help = Wait-Cli (Start-Cli 'help' @('--stats', '--help'))
    Assert-Check ($help.Out.Contains('--stats')) '帮助缺少统计用法。'
    Assert-Check (-not (Test-Path -LiteralPath (Join-Path $runDirectory 'saves'))) '帮助创建了默认存档。'
    $existingEmptySave = Join-Path $runDirectory 'existing-empty-save'
    [void][IO.Directory]::CreateDirectory((Join-Path $existingEmptySave 'replay-library'))
    $existing = Query-ReadOnly 'existing-empty-stats' $existingEmptySave
    Assert-Check ($existing.Out.Contains('保留录像：0 局')) '已有空库查询错误。'
    Assert-Check (@(Get-ChildItem -LiteralPath $existingEmptySave -File -Recurse -Force).Count -eq 0) '已有空库查询创建了锁、索引或档案。'
    $checks.Add('PASS empty_and_help_do_not_create_save')

    $invalid = @(
        @('--stats'), @('--auto'), @('--host', '0'), @('--join', '127.0.0.1:1'), @('--hotseat'),
        @('--replay', 'missing.json'), @('--replays'), @('--list-packs'), @('--timer', '1,2'),
        @('--team', ($hostTeam -join ',')), @('--level', 'easy'), @('--ban'), @('--lead'),
        @('--tournament', 'missing.json'), @('--advice'), @('--glossary'), @('--calc', 'enka', '1', 'kei')
    )
    for ($i = 0; $i -lt $invalid.Count; $i++) {
        $save = Join-Path $runDirectory "invalid-save-$i"
        $result = Wait-Cli (Start-Cli "invalid-$i" (@('--stats', '--save-dir', $save) + $invalid[$i])) 1
        Assert-Check ($result.Err -match '错误：.*--stats') '冲突参数未给出中文统计错误。'
        Assert-Check (-not (Test-Path -LiteralPath $save)) '冲突参数创建了存档。'
    }
    $checks.Add('PASS independent_argument_conflicts')

    $localSave = Join-Path $runDirectory 'local-save'
    $local = Wait-Cli (Start-Cli 'local-auto' @('--auto', '--ban', '--level', 'normal', '--seed', '17',
        '--team', ($hostTeam -join ','), '--save-dir', $localSave))
    $localFile = Only-LibraryFile $localSave
    $localJson = Read-Json $localFile
    $metadata = $localJson.library.stats
    Assert-Check ($metadata.playerSide -eq 0 -and $metadata.aiLevel -eq 'normal') '本地电脑对局未保存真实视角/难度。'
    Assert-Check ($metadata.fighters.Count -eq 5) '禁用后的实际五人名单未进入统计摘要。'
    $profile = Read-Json (Join-Path $localSave 'profile.json')
    Assert-Check ($profile.packs.original.picks.Count -eq 5) '累计档案错误计入被禁用主播。'
    foreach ($fighter in $metadata.fighters) {
        Assert-Check ($profile.packs.original.picks.Contains($fighter.streamerId)) '摘要名单与最终选用档案不一致。'
        Assert-Check ($fighter.damageDealt -ge 0 -and $fighter.kos -ge 0) '摘要伤害或击倒非法。'
    }
    $query = Query-ReadOnly 'local-stats' $localSave @('--pack', 'original', '--pack-dir', (Join-Path $binaryDirectory 'Content/Packs'))
    Assert-Check ($query.Out.Contains('含本方详细数据：1 局') -and $query.Out -match '(?m)^  普通：[0-9]+ 胜') '本地已知 AI 样本没有显示。'
    $checks.Add('PASS completed_local_ban_metadata_and_readonly_query')

    # 只改独立测试副本，模拟累计七局但旧录像仅保留一局的真实保存范围差异。
    $historicalSave = Join-Path $runDirectory 'historical-save'
    Copy-Item -LiteralPath $localSave -Destination $historicalSave -Recurse
    $historicalProfile = Read-Json (Join-Path $historicalSave 'profile.json')
    $history = $historicalProfile.packs.original
    $history.wins = 3; $history.losses = 3; $history.draws = 1
    foreach ($id in @($history.picks.Keys)) { $history.picks[$id] = 7; $history.pickWins[$id] = 3 }
    Write-Json (Join-Path $historicalSave 'profile.json') $historicalProfile
    $historyResult = Query-ReadOnly 'historical-stats' $historicalSave
    $rows = @([regex]::Matches($historyResult.Out, '(?m)^  original / .+：选用 7 次；胜率 42\.86%；平均造成伤害 [0-9.]+；击倒 [0-9]+（伤害/击倒样本 1 局；选用来源：累计档案）\。$'))
    Assert-Check ($rows.Count -eq 5) '累计局数、胜率或单录像样本分母被混合。'
    $pack = Read-Json (Join-Path $binaryDirectory 'Content/Packs/original.json')
    foreach ($fighter in $metadata.fighters) {
        $name = @($pack.streamers | Where-Object id -eq $fighter.streamerId)[0].name
        $expected = "original / ${name}：选用 7 次；胜率 42.86%；平均造成伤害 $($fighter.damageDealt)；击倒 $($fighter.kos)"
        Assert-Check ($historyResult.Out.Contains($expected)) '平均伤害没有使用一局摘要样本作为分母。'
    }
    $checks.Add('PASS_profile_totals_distinct_from_retained_samples')

    $legacySave = Join-Path $runDirectory 'legacy-save'
    Copy-Item -LiteralPath $localSave -Destination $legacySave -Recurse
    $legacyFile = Only-LibraryFile $legacySave
    $legacy = Read-Json $legacyFile
    [void]$legacy.library.Remove('stats')
    Write-Json $legacyFile $legacy
    [IO.File]::WriteAllText((Join-Path $legacySave 'replay-library/index.json'), '故意损坏的索引，不可作为事实来源', $utf8)
    [IO.File]::WriteAllText((Join-Path $legacySave 'replay-library/broken_fixture.json'), '{bad', $utf8)
    $old = Query-ReadOnly 'legacy-stats' $legacySave
    Assert-Check ($old.Out.Contains('含本方详细数据：0 局；损坏：1 份') -and $old.Out.Contains('本方结果：未知')) '旧录像被推测视角，或坏录像未被隔离。'
    Assert-Check ($old.Out.Contains('平均造成伤害 未知；击倒 未知') -and $old.Out.Contains('暂无已知电脑难度数据。')) '旧录像未知伤害/AI 被错误填零或猜测。'
    Assert-Check ($old.Out.Contains('当前连胜 0；当前连败 0；最长连胜 0')) '未知结果未中断连胜。'
    $checks.Add('PASS_legacy_unknown_and_corrupt_index_readonly')

    $hostSave = Join-Path $runDirectory 'network-host-save'
    $guestSave = Join-Path $runDirectory 'network-guest-save'
    $hostChild = Start-Cli 'network-host' @('--host', '0', '--auto', '--hash', '--seed', '7', '--level', 'easy',
        '--team', ($hostTeam -join ','), '--save-dir', $hostSave)
    $port = [int](Wait-Output $hostChild '(?m)^监听端口：([0-9]+)\r?$').Groups[1].Value
    Assert-Check ($port -gt 0 -and $port -le 65535) '主机未输出有效动态端口。'
    $guestChild = Start-Cli 'network-guest' @('--join', "127.0.0.1:$port", '--auto', '--hash', '--level', 'normal',
        '--team', ($guestTeam -join ','), '--save-dir', $guestSave)
    $hostResult = Wait-Cli $hostChild
    $guestResult = Wait-Cli $guestChild
    $hostHash = [regex]::Match($hostResult.Out, '(?m)^最终状态哈希：([0-9a-f]{16})\r?$')
    $guestHash = [regex]::Match($guestResult.Out, '(?m)^最终状态哈希：([0-9a-f]{16})\r?$')
    Assert-Check ($hostHash.Success -and $guestHash.Success -and $hostHash.Groups[1].Value -ceq $guestHash.Groups[1].Value) '真实联网对局最终哈希不一致。'
    $finalHash = $hostHash.Groups[1].Value
    $hostJson = Read-Json (Only-LibraryFile $hostSave)
    $guestJson = Read-Json (Only-LibraryFile $guestSave)
    Assert-Check ($hostJson.library.stats.playerSide -eq 0 -and $guestJson.library.stats.playerSide -eq 1) '联网客机没有保存本方 1 视角。'
    Assert-Check ($null -eq $hostJson.library.stats.aiLevel -and $null -eq $guestJson.library.stats.aiLevel) '联网 --auto 被伪记为对电脑难度。'
    $guestIds = @($guestJson.library.stats.fighters | ForEach-Object streamerId | Sort-Object)
    Assert-Check (($guestIds -join ',') -ceq (($guestTeam | Sort-Object) -join ',')) '客机摘要错误保存为房主名单。'
    $guestStats = Query-ReadOnly 'network-guest-stats' $guestSave
    $expectedOutcome = if ($guestJson.library.winner -lt 0) { '平' } elseif ($guestJson.library.winner -eq 1) { '胜' } else { '负' }
    Assert-Check ($guestStats.Out.Contains("本方结果：$expectedOutcome") -and $guestStats.Out.Contains('暂无已知电脑难度数据。')) '客机胜负方向或对电脑分组不正确。'
    $checks.Add('PASS_network_auto_side_one_and_no_invented_ai')
}
catch { $failure = $_.ToString() }
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
        catch { if ($null -eq $failure) { $failure = $_.ToString() } }
        finally {
            if ($null -ne $child.OutStream) { $child.OutStream.Dispose() }
            if ($null -ne $child.ErrStream) { $child.ErrStream.Dispose() }
            $child.Process.Dispose()
        }
    }
    $report = [ordered]@{ status = $(if ($null -eq $failure) { 'PASS' } else { 'FAIL' }); checks = @($checks.ToArray()); final_hash = $finalHash; error = $failure }
    Write-Json (Join-Path $runDirectory 'report.json') $report
}
foreach ($check in $checks) { Write-Host $check }
Write-Host "证据目录：$runDirectory"
if ($null -ne $failure) { throw $failure }
