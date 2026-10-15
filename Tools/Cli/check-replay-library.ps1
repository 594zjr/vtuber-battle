#requires -Version 7.0
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$repositoryPrefix = $repository.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$current = [IO.Path]::GetFullPath((Get-Location).Path)
if ($current -ne $repository -and -not $current.StartsWith($repositoryPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw '请在本仓库内运行此脚本。'
}
$cliDll = Join-Path $PSScriptRoot "bin/$Configuration/net8.0/Cli.dll"
if (-not (Test-Path -LiteralPath $cliDll -PathType Leaf)) {
    throw "请先运行 dotnet build Tools/Cli -c $Configuration。"
}
$runDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ('bin/replay-library-check/' + [Guid]::NewGuid().ToString('N'))))
if (-not $runDirectory.StartsWith($repositoryPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw '验收产物目录必须位于本仓库内。'
}
[void][IO.Directory]::CreateDirectory($runDirectory)
$utf8 = [Text.UTF8Encoding]::new($false)
$children = [Collections.Generic.List[object]]::new()
$checks = [Collections.Generic.List[string]]::new()

function Assert-Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "$Message 证据目录：$runDirectory" }
}

function Read-Log([string]$Path) {
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
    $reader = [IO.StreamReader]::new($stream, $utf8)
    try { return $reader.ReadToEnd() }
    finally { $reader.Dispose() }
}

function Start-Cli([string]$Name, [string[]]$Parameters, [string]$InputText = '', [switch]$KeepInputOpen) {
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
    $start.StandardOutputEncoding = $utf8
    $start.StandardErrorEncoding = $utf8
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    $child = [pscustomobject]@{
        Name = $Name; Parameters = $Parameters; Process = $process; Started = $false
        OutPath = (Join-Path $runDirectory "$Name.stdout.log")
        ErrPath = (Join-Path $runDirectory "$Name.stderr.log")
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
    if ($InputText.Length -gt 0) { $process.StandardInput.Write($InputText) }
    if (-not $KeepInputOpen) { $process.StandardInput.Close() }
    return $child
}

function Wait-Cli($Child, [int]$ExpectedExit = 0) {
    Assert-Check ($Child.Process.WaitForExit(30000)) "$($Child.Name) 超过 30 秒未退出。"
    Assert-Check ($Child.OutTask.Wait(5000) -and $Child.ErrTask.Wait(5000)) "$($Child.Name) 输出未结束。"
    $Child.OutStream.Flush()
    $Child.ErrStream.Flush()
    $result = [pscustomobject]@{ Out = (Read-Log $Child.OutPath); Err = (Read-Log $Child.ErrPath); Exit = $Child.Process.ExitCode }
    Assert-Check ($result.Exit -eq $ExpectedExit) "$($Child.Name) 应退出 $ExpectedExit，实际 $($result.Exit)。"
    return $result
}

function Invoke-Cli([string]$Name, [string[]]$Parameters, [string]$InputText = '', [int]$ExpectedExit = 0) {
    return Wait-Cli (Start-Cli $Name $Parameters $InputText) $ExpectedExit
}

function Fingerprint([string]$Directory) {
    if (-not (Test-Path -LiteralPath $Directory)) { return '' }
    return (@(Get-ChildItem -LiteralPath $Directory -File -Recurse -Force | Sort-Object FullName | ForEach-Object {
        $_.FullName + ':' + (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    }) -join "`n")
}

function Library-Files([string]$SaveDirectory) {
    $directory = Join-Path $SaveDirectory 'replay-library'
    if (Test-Path -LiteralPath $directory -PathType Container) {
        Get-ChildItem -LiteralPath $directory -File -Filter '*.json' |
            Where-Object { $_.Name -ne 'index.json' -and -not $_.Name.StartsWith('.') } | Sort-Object Name
    }
}

function Old-Replays([string]$SaveDirectory) {
    $directory = Join-Path $SaveDirectory 'replays'
    if (Test-Path -LiteralPath $directory -PathType Container) {
        Get-ChildItem -LiteralPath $directory -File -Filter '*.json' | Where-Object { -not $_.Name.StartsWith('.') }
    }
}

function Assert-OneGame([string]$SaveDirectory) {
    $profile = [IO.File]::ReadAllText((Join-Path $SaveDirectory 'profile.json')) | ConvertFrom-Json
    $stats = $profile.packs.original
    Assert-Check (($stats.wins + $stats.losses + $stats.draws) -eq 1) '档案应恰好记录一局。'
}

function Battle-Lines([string]$Output) {
    $entries = [Collections.Generic.List[string]]::new()
    $node = $null
    foreach ($line in ($Output -split "`n")) {
        if ($line -match '(【第 [0-9]+ 回合 · [^】]+】)') { $node = $Matches[1] }
        elseif ($null -ne $node -and $line.StartsWith('  ')) { $entries.Add($node + $line) }
        else { $node = $null }
    }
    return $entries -join "`n"
}

function Wait-Output($Child, [string]$Pattern) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    while ($timer.ElapsedMilliseconds -lt 15000) {
        $match = [regex]::Match((Read-Log $Child.OutPath), $Pattern)
        if ($match.Success) { return $match }
        Assert-Check (-not $Child.Process.HasExited) "$($Child.Name) 在出现预期输出前退出。"
        Start-Sleep -Milliseconds 25
    }
    throw "$($Child.Name) 等待输出超时。证据目录：$runDirectory"
}

try {
    $emptySave = Join-Path $runDirectory 'empty-saves'
    $empty = Invoke-Cli '01-empty' @('--replays', '--save-dir', $emptySave, '--pack', 'not-a-pack')
    Assert-Check ($empty.Out.Contains('暂无录像。') -and -not $empty.Out.Contains('命令行对战')) '空列表不应开局或要求内容包可用。'
    Assert-Check (-not (Test-Path -LiteralPath (Join-Path $emptySave 'profile.json')) -and
        @(Library-Files $emptySave).Count -eq 0 -and @(Old-Replays $emptySave).Count -eq 0) '列出空库产生了对局存档。'
    $checks.Add('空库列表不依赖内容包、不创建对局')

    $save = Join-Path $runDirectory 'local-saves'
    $game = Invoke-Cli '02-game' @('--auto', '--seed', '1', '--ban', '--lead', '--share', '--save-dir', $save)
    Assert-Check ($game.Err.Length -eq 0 -and $game.Out.Contains('录像库已更新：')) '正常对局未成功归档录像库。'
    $files = @(Library-Files $save)
    $old = @(Old-Replays $save)
    Assert-Check ($files.Count -eq 1 -and $old.Count -eq 1) '新库与旧回放各应保存一份。'
    Assert-OneGame $save
    $id = [IO.Path]::GetFileNameWithoutExtension($files[0].Name)
    $libraryRecord = [IO.File]::ReadAllText($files[0].FullName) | ConvertFrom-Json
    $oldRecord = [IO.File]::ReadAllText($old[0].FullName) | ConvertFrom-Json
    Assert-Check ($null -ne $libraryRecord.PSObject.Properties['library']) '录像文件没有独立的 library 元数据。'
    Assert-Check ($libraryRecord.seed -eq $oldRecord.seed -and $libraryRecord.packId -ceq $oldRecord.packId -and
        ($libraryRecord.events | ConvertTo-Json -Depth 50 -Compress) -ceq ($oldRecord.events | ConvertTo-Json -Depth 50 -Compress)) '归档改变了原生记录。'
    $listing = Invoke-Cli '03-list' @('--replays', '--save-dir', $save)
    Assert-Check ($listing.Out.Contains('编号：' + $id) -and $listing.Out.Contains('内容包：original') -and
        $listing.Out.Contains('UTC') -and $listing.Out.Contains('双方：你 / 电脑') -and $listing.Out.Contains('；回合：') -and
        $listing.Out.Contains('备注：无')) '列表缺少编号、时间、内容包、双方、回合或备注。'
    foreach ($member in $oldRecord.teams[0] + $oldRecord.teams[1]) {
        Assert-Check ($listing.Out.Contains($member)) '列表没有保留开局前完整阵容。'
    }
    $checks.Add('完整对局同时保存新库、旧回放和一次档案；列表显示原始阵容及元数据')

    $before = Fingerprint $save
    $byId = Invoke-Cli '04-by-id' @('--replay', $id, '--auto', '--share', '--save-dir', $save)
    $byFile = Invoke-Cli '05-by-library-file' @('--replay', $files[0].FullName, '--auto', '--share', '--save-dir', $save)
    $byOld = Invoke-Cli '06-by-old-file' @('--replay', $old[0].FullName, '--auto', '--share', '--save-dir', $save)
    Assert-Check ($byId.Out -ceq $byFile.Out -and $byId.Out -ceq $byOld.Out) '按编号、新库文件与旧回放文件播放输出不一致。'
    Assert-Check ((Battle-Lines $byId.Out) -ceq (Battle-Lines $game.Out)) '库录像没有逐字复现原局日志。'
    Assert-Check ((Fingerprint $save) -ceq $before) '回看新增或改变了档案、录像。'
    $codeMatch = [regex]::Match($game.Out, '(?m)^分享码：(SD1-[A-Za-z0-9_-]+)\r?$')
    Assert-Check $codeMatch.Success '原局未输出分享码。'
    $code = $codeMatch.Groups[1].Value
    $byCode = Invoke-Cli '07-by-code' @('--replay-code', $code, '--auto', '--share', '--save-dir', $save)
    Assert-Check ($byId.Out -ceq $byCode.Out -and (Fingerprint $save) -ceq $before) '分享码兼容输出或不写存档保证发生变化。'
    $bareFile = Join-Path $runDirectory 'existing-bare-record'
    [IO.File]::WriteAllText($bareFile, [IO.File]::ReadAllText($old[0].FullName), $utf8)
    $bare = Invoke-Cli '08-bare-file' @('--replay', 'existing-bare-record', '--auto', '--share', '--save-dir', $save)
    Assert-Check ($bare.Out -ceq $byId.Out) '原有无扩展名文件被错误当作库编号。'
    $checks.Add('编号、新库文件、旧文件、无扩展名现有文件及分享码均兼容；完整回看不写存档')

    $protectedPaths = @('index.json', ($id + '.json'), '.library.lock', '.index.json.tmp', 'new-record.json', 'nested/report.md')
    foreach ($mode in @('game', 'id', 'file', 'code')) {
        $modeArguments = switch ($mode) {
            'game' { @('--seed', '1') }
            'id' { @('--replay', $id) }
            'file' { @('--replay', $files[0].FullName) }
            'code' { @('--replay-code', $code) }
        }
        for ($i = 0; $i -lt $protectedPaths.Count; $i++) {
            $target = Join-Path (Join-Path $save 'replay-library') $protectedPaths[$i]
            $rejected = Invoke-Cli ("09-protected-$mode-$i") ($modeArguments + @('--auto', '--save-dir', $save, '--report', $target)) '' 1
            Assert-Check ($rejected.Err.Contains('--report') -and $rejected.Out.Length -eq 0) '战报占用库路径没有在开始前拒绝。'
            Assert-Check ((Fingerprint $save) -ceq $before) '战报路径拒绝改变了库或档案。'
        }
    }
    $alias = Join-Path $save 'replay-library/child/../index.json'
    if ($IsWindows) { $alias = $alias.ToUpperInvariant() }
    $null = Invoke-Cli '09-protected-normalized' @('--auto', '--save-dir', $save, '--report', $alias) '' 1
    Assert-Check ((Fingerprint $save) -ceq $before) '路径规范化或大小写别名绕过了录像库保护。'
    if ($IsWindows) {
        foreach ($ending in @('.', ' ')) {
            $alias = Join-Path $save ("replay-library$ending/index.json")
            $null = Invoke-Cli ("09-protected-ending-" + [int][char]$ending) @('--auto', '--save-dir', $save, '--report', $alias) '' 1
            Assert-Check ((Fingerprint $save) -ceq $before) 'Windows 目录末尾点或空格别名绕过了录像库保护。'
        }
    }
    $checks.Add('所有对战/回看模式在开始前拒绝覆盖库索引、录像、锁、临时文件及子目录；规范化别名同样拒绝')

    $invalidCases = @(
        @{ Name = 'missing-id'; Args = @('--replay', 'not-found') },
        @{ Name = 'invalid-id'; Args = @('--replay', '..') },
        @{ Name = 'list-replay'; Args = @('--replays', '--replay', $id) },
        @{ Name = 'list-code'; Args = @('--replays', '--replay-code', $code) },
        @{ Name = 'list-packs'; Args = @('--replays', '--list-packs') },
        @{ Name = 'list-host'; Args = @('--replays', '--host', '0') },
        @{ Name = 'list-share'; Args = @('--replays', '--share') },
        @{ Name = 'list-report'; Args = @('--replays', '--report', (Join-Path $runDirectory 'unused.md')) }
    )
    foreach ($case in $invalidCases) {
        $invalid = Invoke-Cli ("10-$($case.Name)") ($case.Args + @('--save-dir', $save)) '' 1
        Assert-Check ($invalid.Err.StartsWith('错误：') -and (Fingerprint $save) -ceq $before) '错误编号或冲突参数没有安全失败。'
    }
    foreach ($mode in @('game', 'id')) {
        foreach ($inputKind in @('q', 'eof')) {
            $targetSave = if ($mode -eq 'game') { Join-Path $runDirectory ("quit-$inputKind") } else { $save }
            $parameters = if ($mode -eq 'game') { @('--ban', '--lead') } else { @('--replay', $id) }
            $inputText = if ($inputKind -eq 'q') { "q`n" } else { '' }
            $null = Invoke-Cli ("11-quit-$mode-$inputKind") ($parameters + @('--save-dir', $targetSave)) $inputText
            if ($mode -eq 'game') { Assert-Check (-not (Test-Path -LiteralPath $targetSave)) '提前退出对局产生了存档。' }
            else { Assert-Check ((Fingerprint $save) -ceq $before) '提前退出回看改变了存档。' }
        }
    }
    $checks.Add('坏编号、互斥参数中文报错；对局与回看 q/EOF 均不新增存档')

    foreach ($failure in @('library', 'old-replay', 'profile')) {
        $brokenSave = Join-Path $runDirectory ("broken-$failure")
        [void][IO.Directory]::CreateDirectory($brokenSave)
        $blocked = switch ($failure) { 'library' { 'replay-library' }; 'old-replay' { 'replays' }; 'profile' { 'profile.json' } }
        $marker = '原文件必须保留'
        $blockedPath = Join-Path $brokenSave $blocked
        $markerPath = $blockedPath
        if ($failure -eq 'profile') {
            # 损坏的档案文本会按既有设计恢复；目录占位才能稳定模拟档案写入故障。
            [void][IO.Directory]::CreateDirectory($blockedPath)
            $markerPath = Join-Path $blockedPath 'marker.txt'
        }
        [IO.File]::WriteAllText($markerPath, $marker, $utf8)
        $failed = Invoke-Cli ("12-broken-$failure") @('--auto', '--seed', '1', '--save-dir', $brokenSave) '' 1
        Assert-Check ($failed.Out.Contains('对局结束：') -and $failed.Err.Length -gt 0) '保存故障未完成对局或未告知错误。'
        Assert-Check ([IO.File]::ReadAllText($markerPath) -ceq $marker) '保存故障覆盖了原阻塞文件。'
        if ($failure -eq 'profile') {
            Assert-Check (Test-Path -LiteralPath $blockedPath -PathType Container) '档案故障擅自替换了原阻塞目录。'
        }
        if ($failure -ne 'library') { Assert-Check (@(Library-Files $brokenSave).Count -eq 1) '其他保存故障阻止了库归档。' }
        if ($failure -ne 'old-replay') { Assert-Check (@(Old-Replays $brokenSave).Count -eq 1) '其他保存故障阻止了旧回放保存。' }
        if ($failure -ne 'profile') { Assert-OneGame $brokenSave }
    }
    $checks.Add('库、旧回放、档案分别故障时其余可写结果仍保存，阻塞原文件保留')

    $maintenanceSave = Join-Path $runDirectory 'maintenance-failure'
    $indexDirectory = Join-Path $maintenanceSave 'replay-library/index.json'
    [void][IO.Directory]::CreateDirectory($indexDirectory)
    $maintenance = Invoke-Cli '12-maintenance' @('--auto', '--save-dir', $maintenanceSave) '' 1
    Assert-Check ($maintenance.Err.Contains('录像已写入，但录像库维护失败：') -and
        @(Library-Files $maintenanceSave).Count -eq 1 -and @(Old-Replays $maintenanceSave).Count -eq 1) '索引写入失败时没有保留新旧录像或没有明确告知。'
    Assert-OneGame $maintenanceSave
    Assert-Check (Test-Path -LiteralPath $indexDirectory -PathType Container) '维护失败擅自替换了原索引阻塞目录。'
    $checks.Add('录像已写入但索引维护失败时明确警告、退出 1，旧回放及档案仍保存')

    $index = Join-Path $save 'replay-library/index.json'
    $recordHash = (Get-FileHash -LiteralPath $files[0].FullName -Algorithm SHA256).Hash
    [IO.File]::WriteAllText($index, '{broken index', $utf8)
    $rebuilt = Invoke-Cli '13-rebuild-index' @('--replays', '--save-dir', $save)
    Assert-Check ($rebuilt.Out.Contains('编号：' + $id) -and -not $rebuilt.Out.Contains('已损坏：')) '损坏索引没有从原生录像恢复。'
    $null = [IO.File]::ReadAllText($index) | ConvertFrom-Json
    Assert-Check ((Get-FileHash -LiteralPath $files[0].FullName -Algorithm SHA256).Hash -ceq $recordHash) '重建索引修改了原录像。'
    [IO.File]::WriteAllText($files[0].FullName, '{broken replay', $utf8)
    $badList = Invoke-Cli '14-corrupt-entry' @('--replays', '--save-dir', $save)
    Assert-Check ($badList.Out.Contains('编号：' + $id) -and $badList.Out.Contains('已损坏：')) '坏录像没有在列表标记。'
    $corruptBefore = Fingerprint $save
    $badLoad = Invoke-Cli '15-corrupt-load' @('--replay', $id, '--auto', '--save-dir', $save) '' 1
    Assert-Check ($badLoad.Err.StartsWith('错误：') -and -not $badLoad.Out.Contains('对战回看')) '坏录像未在播放前拒绝。'
    Assert-Check ((Fingerprint $save) -ceq $corruptBefore) '读取坏录像更改了存档。'
    $checks.Add('坏索引可重建且不改原录像；坏录像列出标记、加载中文报错')

    $hostSave = Join-Path $runDirectory 'host-saves'
    $guestSave = Join-Path $runDirectory 'guest-saves'
    $hostChild = Start-Cli '16-network-host' @('--host', '0', '--auto', '--seed', '7', '--hash',
        '--team', 'bell,enka,shiori,lumi,hane,momo', '--save-dir', $hostSave)
    $port = (Wait-Output $hostChild '(?m)^监听端口：([0-9]+)\r?$').Groups[1].Value
    $guestChild = Start-Cli '16-network-guest' @('--join', "127.0.0.1:$port", '--auto', '--hash',
        '--team', 'kei,yuan,nyako,buck,kasumi,bee', '--save-dir', $guestSave)
    $hostResult = Wait-Cli $hostChild
    $guestResult = Wait-Cli $guestChild
    foreach ($result in @($hostResult, $guestResult)) {
        Assert-Check ($result.Out.IndexOf('最终状态哈希：') -ge 0 -and
            $result.Out.IndexOf('最终状态哈希：') -lt $result.Out.IndexOf('录像库已更新：')) '联网录像在终局验证前归档。'
    }
    foreach ($networkSave in @($hostSave, $guestSave)) {
        Assert-Check (@(Library-Files $networkSave).Count -eq 1 -and @(Old-Replays $networkSave).Count -eq 1) '联网终局未同时保存新旧录像。'
        Assert-OneGame $networkSave
    }
    $hostFile = @(Library-Files $hostSave)[0]
    $networkId = [IO.Path]::GetFileNameWithoutExtension($hostFile.Name)
    $networkBefore = Fingerprint $hostSave
    $networkReplay = Invoke-Cli '17-network-replay' @('--replay', $networkId, '--auto', '--save-dir', $hostSave)
    Assert-Check ((Battle-Lines $networkReplay.Out) -ceq (Battle-Lines $hostResult.Out) -and
        (Fingerprint $hostSave) -ceq $networkBefore) '联网归档回看未复现日志或改变了存档。'
    $checks.Add('真实 TCP 双端完整对局仅在终局校验后各归档一次，编号回看逐字一致')

    $quitHostSave = Join-Path $runDirectory 'network-quit-host'
    $quitGuestSave = Join-Path $runDirectory 'network-quit-guest'
    $quitHost = Start-Cli '18-quit-host' @('--host', '0', '--save-dir', $quitHostSave) '' -KeepInputOpen
    $quitPort = (Wait-Output $quitHost '(?m)^监听端口：([0-9]+)\r?$').Groups[1].Value
    $quitGuest = Start-Cli '18-quit-guest' @('--join', "127.0.0.1:$quitPort", '--save-dir', $quitGuestSave) '' -KeepInputOpen
    $null = Wait-Output $quitHost '【第 [0-9]+ 回合 ·'
    $null = Wait-Output $quitGuest '【第 [0-9]+ 回合 ·'
    $quitHost.Process.StandardInput.WriteLine('q')
    $quitHost.Process.StandardInput.Close()
    $quitHostResult = Wait-Cli $quitHost
    $quitGuestResult = Wait-Cli $quitGuest 1
    foreach ($unfinished in @($quitHostSave, $quitGuestSave)) {
        Assert-Check (-not (Test-Path -LiteralPath $unfinished)) '未完成联网对局产生了存档。'
    }
    Assert-Check (-not $quitHostResult.Out.Contains('录像库已更新：') -and
        -not $quitGuestResult.Out.Contains('录像库已更新：')) '未完成联网对局谎报已归档。'
    $checks.Add('联网人工退出与对端断线均不归档')

    $help = Invoke-Cli '19-help' @('--help')
    Assert-Check ($help.Out.Contains('--replays') -and $help.Out.Contains('--replay 编号/文件') -and
        $help.Out.Contains('replay-library') -and $help.Out.Contains('互斥')) '帮助缺少录像库用法。'
    $checks.Add('帮助包含录像库位置、编号回放与冲突说明')
    [IO.File]::WriteAllText((Join-Path $runDirectory 'report.json'),
        (@{ status = 'PASS'; checks = $checks.ToArray(); replayId = $id } | ConvertTo-Json -Depth 5), $utf8)
    Write-Output "PASS：$($checks.Count) 组录像库 CLI 检查；证据：$runDirectory"
}
catch {
    [IO.File]::WriteAllText((Join-Path $runDirectory 'report.json'),
        (@{ status = 'FAIL'; error = $_.Exception.Message; checks = $checks.ToArray() } | ConvertTo-Json -Depth 5), $utf8)
    throw
}
finally {
    foreach ($child in $children) {
        if ($child.Started -and -not $child.Process.HasExited) { $child.Process.Kill($true); $child.Process.WaitForExit(5000) | Out-Null }
        if ($null -ne $child.OutTask) { [void]$child.OutTask.Wait(5000) }
        if ($null -ne $child.ErrTask) { [void]$child.ErrTask.Wait(5000) }
        if ($null -ne $child.OutStream) { $child.OutStream.Dispose() }
        if ($null -ne $child.ErrStream) { $child.ErrStream.Dispose() }
        $child.Process.Dispose()
    }
}
