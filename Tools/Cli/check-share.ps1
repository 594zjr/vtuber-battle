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
$runDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ('obj/share-check/' + [Guid]::NewGuid().ToString('N'))))
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

function Start-Cli([string]$Name, [string[]]$Parameters, [string]$InputText = '') {
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
    $process.StandardInput.Close()
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

function Share-Code([string]$Output) {
    $found = [regex]::Matches($Output, '(?m)^分享码：(SD1-[A-Za-z0-9_-]+)\r?$')
    Assert-Check ($found.Count -eq 1) '完整输出必须恰好包含一个分享码。'
    return $found[0].Groups[1].Value
}

function Fingerprint([string]$Directory) {
    if (-not (Test-Path -LiteralPath $Directory)) { return '' }
    return (@(Get-ChildItem -LiteralPath $Directory -File -Recurse -Force | Sort-Object FullName | ForEach-Object {
        $_.FullName + ':' + (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    }) -join "`n")
}

function Assert-SameFile([string]$First, [string]$Second) {
    Assert-Check ((Get-FileHash -LiteralPath $First -Algorithm SHA256).Hash -ceq
        (Get-FileHash -LiteralPath $Second -Algorithm SHA256).Hash) '新局与分享码回看的战报不一致。'
}

try {
    $save = Join-Path $runDirectory 'local-saves'
    $gameReport = Join-Path $runDirectory 'game.md'
    $game = Invoke-Cli '01-game' @('--auto', '--seed', '1', '--ban', '--lead', '--share', '--save-dir', $save, '--report', $gameReport)
    $code = Share-Code $game.Out
    Assert-Check ($game.Err.Length -eq 0) '完整本机对局产生错误。'
    $records = @(Get-ChildItem -LiteralPath (Join-Path $save 'replays') -File -Filter '*.json')
    Assert-Check ($records.Count -eq 1) '完整对局未恰好保存一份回放。'
    $profile = [IO.File]::ReadAllText((Join-Path $save 'profile.json')) | ConvertFrom-Json
    Assert-Check (($profile.packs.original.wins + $profile.packs.original.losses + $profile.packs.original.draws) -eq 1) '完整对局战绩未只累计一次。'
    $before = Fingerprint $save
    $wrappedCode = [regex]::Replace($code, '(.{64})', "`$1 `r`n`t")
    $codeReport = Join-Path $runDirectory 'from-code.md'
    $replayed = Invoke-Cli '02-code' @('--replay-code', $wrappedCode, '--auto', '--share', '--pack', 'fan_asoul_vr', '--seed', '99',
        '--team', 'ignored', '--save-dir', $save, '--report', $codeReport)
    Assert-Check ((Share-Code $replayed.Out) -ceq $code) '分享码再导出没有保留原记录。'
    Assert-SameFile $gameReport $codeReport
    Assert-Check ((Fingerprint $save) -ceq $before) '分享码回看改变了档案或回放文件。'
    $fileReplay = Invoke-Cli '03-file' @('--replay', $records[0].FullName, '--auto', '--share', '--save-dir', $save)
    Assert-Check ((Share-Code $fileReplay.Out) -ceq $code) '原文件回放未正确导出分享码。'
    Assert-Check ((Fingerprint $save) -ceq $before) '原文件回看改变了存档。'
    $checks.Add('禁用首发新局分享、含空白码回看、原文件回放再导出均一致，且回看不写存档')

    $protected = Invoke-Cli '04-protected-report' @('--replay-code', $code, '--auto', '--save-dir', $save,
        '--report', (Join-Path $save 'profile.json')) '' 1
    Assert-Check ($protected.Err.Contains('--report') -and (Fingerprint $save) -ceq $before) '分享码的战报路径可覆盖档案。'
    $checks.Add('分享码回看的战报不能占用档案保留路径')

    foreach ($mode in @('file', 'code')) {
        $blockedParent = Join-Path $runDirectory ("blocked-report-$mode-parent")
        $marker = "保留战报父文件：$mode"
        [IO.File]::WriteAllText($blockedParent, $marker, $utf8)
        $blockedReport = Join-Path $blockedParent 'report.md'
        $parameters = if ($mode -eq 'file') { @('--replay', $records[0].FullName) } else { @('--replay-code', $code) }
        $result = Invoke-Cli ("04-blocked-report-$mode") ($parameters + @('--auto', '--share', '--save-dir', $save,
            '--report', $blockedReport)) '' 1
        Assert-Check ($result.Out.Contains('回看结束：') -and $result.Err.Contains('战报保存失败：')) '战报失败回归没有完成回看或缺少保存错误。'
        Assert-Check ((Share-Code $result.Out) -ceq $code) '战报保存失败阻止了分享码导出。'
        Assert-Check (-not $result.Out.Contains('战报已保存：') -and -not [IO.File]::Exists($blockedReport)) '失败战报被谎报已保存。'
        Assert-Check ([IO.File]::ReadAllText($blockedParent) -ceq $marker) '战报保存失败覆盖了原父文件。'
        Assert-Check ((Fingerprint $save) -ceq $before) '战报保存失败的回看改变了档案或回放文件。'
    }
    $checks.Add('文件及分享码回看战报写入失败均退出 1，仍导出同码且保留存档')

    $reservedReports = @(
        @{ Name = 'settings'; Path = 'settings.json' },
        @{ Name = 'settings-tmp'; Path = '.settings.json.tmp' },
        @{ Name = 'settings-lock'; Path = '.settings.lock' },
        @{ Name = 'ownership'; Path = 'replays/.replay-ownership.json' },
        @{ Name = 'ownership-tmp'; Path = 'replays/.replay-ownership.json.tmp' }
    )
    foreach ($case in $reservedReports) {
        $reservedSave = Join-Path $runDirectory ("reserved-$($case.Name)-saves")
        $reservedReport = Join-Path $reservedSave $case.Path
        [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($reservedReport))
        $marker = "保留存档文件：$($case.Name)"
        [IO.File]::WriteAllText($reservedReport, $marker, $utf8)
        $reservedBefore = Fingerprint $reservedSave
        $result = Invoke-Cli ("04-protected-$($case.Name)") @('--replay-code', $code, '--auto', '--share',
            '--save-dir', $reservedSave, '--report', $reservedReport) '' 1
        Assert-Check ($result.Err.Contains('--report') -and -not $result.Out.Contains('战报已保存：') -and
            -not $result.Out.Contains('分享码：')) '分享码回看的保留路径没有在导出前拒绝。'
        Assert-Check ([IO.File]::ReadAllText($reservedReport) -ceq $marker) '分享码回看的战报覆盖了保留标记文件。'
        Assert-Check ((Fingerprint $reservedSave) -ceq $reservedBefore) '分享码回看的保留路径拒绝改变了存档。'
    }
    $checks.Add('分享码回看拒绝设置、设置临时文件及锁、回放归属索引及临时文件，标记内容不变')

    $invalidCases = @(
        @{ Name = 'prefix'; Args = @('--replay-code', ('XX1-' + $code.Substring(4)), '--auto', '--share') },
        @{ Name = 'payload'; Args = @('--replay-code', ($code.Substring(0, 8) + $(if ($code[8] -eq 'A') { 'B' } else { 'A' }) + $code.Substring(9)), '--auto', '--share') },
        @{ Name = 'mutual'; Args = @('--replay', $records[0].FullName, '--replay-code', $code) },
        @{ Name = 'hash'; Args = @('--replay-code', $code, '--hash') },
        @{ Name = 'host'; Args = @('--host', '0', '--replay-code', $code) },
        @{ Name = 'join'; Args = @('--join', '127.0.0.1:1', '--replay-code', $code) },
        @{ Name = 'missing'; Args = @('--replay-code') },
        @{ Name = 'empty'; Args = @('--replay-code', " `t ") }
    )
    foreach ($case in $invalidCases) {
        $unusedSave = Join-Path $runDirectory ("invalid-$($case.Name)-saves")
        $result = Invoke-Cli ("05-$($case.Name)") ($case.Args + @('--save-dir', $unusedSave)) '' 1
        Assert-Check ($result.Err.StartsWith('错误：')) '错误分享参数没有中文诊断。'
        Assert-Check (-not $result.Out.Contains('分享码：') -and -not (Test-Path -LiteralPath $unusedSave)) '失败路径输出分享码或创建了存档。'
    }
    $checks.Add('损坏码、互斥参数、联网/hash组合及缺值均拒绝，且不创建存档')

    foreach ($mode in @('game', 'code')) {
        $unusedSave = Join-Path $runDirectory ("quit-$mode-saves")
        $parameters = if ($mode -eq 'game') { @('--seed', '1') } else { @('--replay-code', $code) }
        $result = Invoke-Cli ("06-quit-$mode") ($parameters + @('--share', '--save-dir', $unusedSave)) "q`n"
        Assert-Check (-not $result.Out.Contains('分享码：') -and -not (Test-Path -LiteralPath $unusedSave)) '提前退出仍输出分享码或创建存档。'
    }
    $checks.Add('手动对局与分享码回看提前退出均不分享、不写存档')

    $external = Join-Path $runDirectory 'external'
    [void][IO.Directory]::CreateDirectory($external)
    $fixture = [IO.File]::ReadAllText((Join-Path $repository 'UnityProject/Assets/StreamDuel/Resources/Packs/original.json')) | ConvertFrom-Json
    $fixture.id = 'share_cli_fixture'
    $fixture.name = '分享码烟测内容包'
    $fixturePath = Join-Path $external 'fixture.json'
    [IO.File]::WriteAllText($fixturePath, ($fixture | ConvertTo-Json -Depth 50), $utf8)
    $externalGame = Invoke-Cli '07-external-game' @('--auto', '--share', '--pack', $fixture.id, '--pack-dir', $external,
        '--save-dir', (Join-Path $runDirectory 'external-saves'))
    $externalCode = Share-Code $externalGame.Out
    $externalReplay = Invoke-Cli '08-external-code' @('--auto', '--share', '--replay-code', $externalCode, '--pack-dir', $external)
    Assert-Check ($externalReplay.Out.Contains('内容包：分享码烟测内容包')) '分享码没有选择记录中的外部包。'
    Assert-Check ((Share-Code $externalReplay.Out) -ceq $externalCode) '外部包分享码回看再导出不一致。'
    $fixture.name += '（修改）'
    [IO.File]::WriteAllText($fixturePath, ($fixture | ConvertTo-Json -Depth 50), $utf8)
    $changed = Invoke-Cli '09-fingerprint' @('--auto', '--share', '--replay-code', $externalCode, '--pack-dir', $external) '' 1
    Assert-Check ($changed.Err.Contains('内容不一致') -and -not $changed.Out.Contains('分享码：')) '分享码绕过了内容指纹验证。'
    Assert-Check (-not (Test-Path -LiteralPath (Join-Path $runDirectory 'saves'))) '分享码回看创建了默认存档。'
    $checks.Add('按分享码选择外部包，内容变化时指纹校验拒绝')

    $hostChild = Start-Cli '10-network-host' @('--host', '0', '--auto', '--share', '--seed', '7', '--hash',
        '--team', 'bell,enka,shiori,lumi,hane,momo', '--save-dir', (Join-Path $runDirectory 'host-saves'))
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $port = $null
    while ($timer.ElapsedMilliseconds -lt 15000) {
        $match = [regex]::Match((Read-Log $hostChild.OutPath), '(?m)^监听端口：([0-9]+)\r?$')
        if ($match.Success) { $port = $match.Groups[1].Value; break }
        Assert-Check (-not $hostChild.Process.HasExited) '联网房主提前退出。'
        Start-Sleep -Milliseconds 25
    }
    Assert-Check ($null -ne $port) '房主未输出监听端口。'
    $guestChild = Start-Cli '10-network-guest' @('--join', "127.0.0.1:$port", '--auto', '--share', '--hash',
        '--team', 'kei,yuan,nyako,buck,kasumi,bee', '--save-dir', (Join-Path $runDirectory 'guest-saves'))
    $hostResult = Wait-Cli $hostChild
    $guestResult = Wait-Cli $guestChild
    $networkCode = Share-Code $hostResult.Out
    Assert-Check ((Share-Code $guestResult.Out) -ceq $networkCode) '联网双方分享码不同。'
    foreach ($result in @($hostResult, $guestResult)) {
        Assert-Check ($result.Out.IndexOf('最终状态哈希：') -ge 0 -and
            $result.Out.IndexOf('最终状态哈希：') -lt $result.Out.IndexOf('分享码：')) '联网分享码在终局验证前输出。'
    }
    $networkReplay = Invoke-Cli '11-network-replay' @('--auto', '--share', '--replay-code', $networkCode)
    Assert-Check ((Share-Code $networkReplay.Out) -ceq $networkCode) '联网分享码未能完整回看再导出。'
    Assert-Check (-not (Test-Path -LiteralPath (Join-Path $runDirectory 'saves'))) '联网分享码回看创建了默认存档。'
    $checks.Add('真实本机TCP对局双方仅在终局校验后输出同码，可完整回看再导出')

    $help = Invoke-Cli '12-help' @('--help')
    Assert-Check ($help.Out.Contains('--share') -and $help.Out.Contains('--replay-code') -and $help.Out.Contains('互斥')) '帮助缺少分享码用法。'
    $checks.Add('帮助包含分享与回看说明')

    $oversizedPacks = Join-Path $runDirectory 'oversized-packs'
    $oversizedSave = Join-Path $runDirectory 'oversized-saves'
    [void][IO.Directory]::CreateDirectory($oversizedPacks)
    $oversized = [IO.File]::ReadAllText((Join-Path $repository 'UnityProject/Assets/StreamDuel/Resources/Packs/original.json')) | ConvertFrom-Json
    $oversized.id = 'share_oversized_fixture'
    $oversized.name = '分享码大小上限烟测'
    $oversized.PSObject.Properties.Remove('seasons')
    foreach ($streamer in $oversized.streamers) { $streamer.id += '_' + ('x' * 90000) }
    [IO.File]::WriteAllText((Join-Path $oversizedPacks 'fixture.json'), ($oversized | ConvertTo-Json -Depth 50), $utf8)
    $oversizedGame = Invoke-Cli '13-oversized' @('--auto', '--share', '--pack', $oversized.id, '--pack-dir', $oversizedPacks,
        '--save-dir', $oversizedSave) '' 1
    Assert-Check ($oversizedGame.Out.Contains('对局结束：') -and -not $oversizedGame.Out.Contains('分享码：')) '超限分享导出没有正常完局，或输出了分享码。'
    Assert-Check ($oversizedGame.Err.Contains('错误：分享码导出失败：') -and $oversizedGame.Err.Contains('1 MiB')) '超限分享缺少明确中文错误。'
    $oversizedRecords = @(Get-ChildItem -LiteralPath (Join-Path $oversizedSave 'replays') -File -Filter '*.json')
    Assert-Check ($oversizedRecords.Count -eq 1 -and $oversizedRecords[0].Length -gt 1048576) '超限分享失败后未恰好保存一份大于 1 MiB 的回放。'
    $oversizedProfile = [IO.File]::ReadAllText((Join-Path $oversizedSave 'profile.json')) | ConvertFrom-Json
    $oversizedStats = $oversizedProfile.packs.($oversized.id)
    Assert-Check (($oversizedStats.wins + $oversizedStats.losses + $oversizedStats.draws) -eq 1) '超限分享失败后战绩未恰好累计一次。'
    $checks.Add('超过分享上限的合法外部包对局报错退出 1，仍恰保存一份回放及一局档案')
    [IO.File]::WriteAllText((Join-Path $runDirectory 'report.json'),
        (@{ status = 'PASS'; checks = $checks.ToArray(); shareLength = $code.Length; networkShareLength = $networkCode.Length } | ConvertTo-Json -Depth 5), $utf8)
    Write-Output "PASS：$($checks.Count) 组分享码CLI检查；证据：$runDirectory"
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
