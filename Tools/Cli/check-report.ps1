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
if (-not $IsWindows) { throw '此回归包含 Windows 扩展路径检查，请使用 Windows PowerShell 7。' }
$cliDll = Join-Path $PSScriptRoot "bin/$Configuration/net8.0/Cli.dll"
if (-not (Test-Path -LiteralPath $cliDll -PathType Leaf)) {
    throw "请先运行 dotnet build Tools/Cli -c $Configuration。"
}

# 每次使用独立证据目录，冲突用例只接触本次生成或复制的文件。
$runDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ('obj/report-check/' + [Guid]::NewGuid().ToString('N'))))
if (-not $runDirectory.StartsWith($repositoryPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw '验收产物目录必须位于本仓库内。'
}
[void][IO.Directory]::CreateDirectory($runDirectory)
$utf8 = [Text.UTF8Encoding]::new($false)
$gameArguments = @('--auto', '--seed', '1', '--ban', '--lead')
$managedPattern = '^[0-9]{8}_[0-9]{6}_[0-9]{7}_[A-Za-z0-9_-]+\.json$'
$checks = [Collections.Generic.List[string]]::new()
$invocations = [Collections.Generic.List[object]]::new()

function Assert-Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "$Message 证据目录：$runDirectory" }
}

function New-Case([string]$Name) {
    $path = Join-Path $runDirectory $Name
    [void][IO.Directory]::CreateDirectory($path)
    return $path
}

function Invoke-CliCheck([string]$Name, [string]$Directory, [string[]]$Parameters,
    [string]$InputText = '', [int]$ExpectedExit = 0) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = 'dotnet'
    $start.ArgumentList.Add($cliDll)
    foreach ($parameter in $Parameters) { $start.ArgumentList.Add($parameter) }
    $start.WorkingDirectory = $Directory
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = $utf8
    $start.StandardErrorEncoding = $utf8
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    try {
        [void]$process.Start()
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if ($InputText.Length -gt 0) { $process.StandardInput.Write($InputText) }
        $process.StandardInput.Close()
        if (-not $process.WaitForExit(30000)) {
            $process.Kill($true)
            throw "$Name 超过 30 秒未退出。"
        }
        $result = [pscustomobject]@{ Out = $stdout.Result; Err = $stderr.Result; Exit = $process.ExitCode }
        [IO.File]::WriteAllText((Join-Path $runDirectory "$Name.stdout.log"), $result.Out, $utf8)
        [IO.File]::WriteAllText((Join-Path $runDirectory "$Name.stderr.log"), $result.Err, $utf8)
        $invocations.Add([ordered]@{ name = $Name; directory = $Directory; parameters = $Parameters; exit = $result.Exit })
        Assert-Check ($result.Exit -eq $ExpectedExit) "$Name 应退出 $ExpectedExit，实际 $($result.Exit)。"
        return $result
    }
    finally { $process.Dispose() }
}

function File-Hash([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function Save-Fingerprint([string]$Directory) {
    $path = Join-Path $Directory 'saves'
    if (-not (Test-Path -LiteralPath $path)) { return '' }
    return (@(Get-ChildItem -LiteralPath $path -File -Recurse -Force | Sort-Object FullName | ForEach-Object {
        $_.FullName + ':' + (File-Hash $_.FullName)
    }) -join "`n")
}

function Assert-StoredOnce([string]$Directory) {
    $profile = [IO.File]::ReadAllText((Join-Path $Directory 'saves/profile.json')) | ConvertFrom-Json
    $stats = $profile.packs.original
    Assert-Check (($stats.wins + $stats.losses + $stats.draws) -eq 1) '完整对局未恰好记录一次战绩。'
    Assert-Check (@($stats.picks.PSObject.Properties).Count -eq 5) '禁用后应记录五位主播的选用次数。'
    foreach ($pick in $stats.picks.PSObject.Properties) {
        Assert-Check ($pick.Value -eq 1) '完整对局的选用次数不为一。'
    }
    $replays = @(Get-ChildItem -LiteralPath (Join-Path $Directory 'saves/replays') -File |
        Where-Object { $_.Name -match $managedPattern })
    Assert-Check ($replays.Count -eq 1) '完整对局未恰好保存一份回放。'
    return $replays[0].FullName
}

function Assert-ReportFailed($Result) {
    Assert-Check ($Result.Err.Length -gt 0) '战报失败未告知错误。'
    Assert-Check (-not $Result.Out.Contains('战报已保存')) '战报失败却输出保存成功。'
}

try {
    $normalDirectory = New-Case 'normal'
    $gameReport = Join-Path $normalDirectory 'game report.md'
    $game = Invoke-CliCheck '01-game-report' $normalDirectory ($gameArguments + @('--report', $gameReport))
    Assert-Check ($game.Err.Length -eq 0 -and $game.Out.Contains('战报已保存')) '正常对局战报未成功导出。'
    Assert-Check ([IO.File]::ReadAllText($gameReport).Contains('## 第 ')) '导出的战报缺少回合正文。'
    $recordPath = Assert-StoredOnce $normalDirectory
    $beforeReplay = Save-Fingerprint $normalDirectory
    $replayReport = Join-Path $normalDirectory 'replayed report.md'
    $replay = Invoke-CliCheck '02-replay-report' $normalDirectory @('--replay', $recordPath, '--auto', '--report', $replayReport)
    Assert-Check ($replay.Err.Length -eq 0 -and $replay.Out.Contains('战报已保存')) '完整回看未成功导出战报。'
    Assert-Check ((File-Hash $gameReport) -ceq (File-Hash $replayReport)) '新局与回看导出的战报字节不一致。'
    Assert-Check ((Save-Fingerprint $normalDirectory) -ceq $beforeReplay) '导出回看战报改变了档案或回放。'
    $checks.Add('自动禁用首发完整新局与回看战报一致；回看不改档案和回放')

    foreach ($mode in @('game', 'replay')) {
        $quitDirectory = New-Case ("quit-$mode")
        $quitReport = Join-Path $quitDirectory 'quit.md'
        $parameters = if ($mode -eq 'game') { @('--seed', '1', '--ban', '--lead') } else { @('--replay', $recordPath) }
        $quit = Invoke-CliCheck ("03-quit-$mode") $quitDirectory ($parameters + @('--report', $quitReport)) "q`n"
        Assert-Check (-not (Test-Path -LiteralPath $quitReport) -and -not $quit.Out.Contains('战报已保存')) 'q 提前退出仍然导出了战报。'
        Assert-Check (-not (Test-Path -LiteralPath (Join-Path $quitDirectory 'saves'))) 'q 提前退出创建了存档。'
    }
    Assert-Check ((Save-Fingerprint $normalDirectory) -ceq $beforeReplay) '提前退出回看改变了原档案或回放。'
    $checks.Add('新局与回看 q 退出均不导出战报或创建存档')

    $blockedDirectory = New-Case 'blocked-parent'
    $blockedParent = Join-Path $blockedDirectory 'parent-file'
    [IO.File]::WriteAllText($blockedParent, 'keep parent file', $utf8)
    $blocked = Invoke-CliCheck '04-blocked-report-parent' $blockedDirectory ($gameArguments + @('--report', (Join-Path $blockedParent 'report.md'))) '' 1
    Assert-ReportFailed $blocked
    $null = Assert-StoredOnce $blockedDirectory
    Assert-Check ($blocked.Out.Contains('对战统计') -and $blocked.Out.Contains('档案已保存')) '战报写入失败阻止了完整对局结算或存档。'
    Assert-Check ([IO.File]::ReadAllText($blockedParent) -ceq 'keep parent file') '战报写入覆盖了父路径的原文件。'
    $checks.Add('战报父路径被文件占用：exit 1，但完整对局仍保存一次档案和回放')

    $aliasDirectory = New-Case 'replay-extended-alias'
    $recordCopy = Join-Path $aliasDirectory 'record-copy.json'
    [IO.File]::Copy($recordPath, $recordCopy)
    $recordHash = File-Hash $recordCopy
    $alias = Invoke-CliCheck '05-replay-extended-alias' $aliasDirectory @('--replay', $recordCopy, '--auto', '--report', ('\\?\' + $recordCopy)) '' 1
    Assert-ReportFailed $alias
    Assert-Check ((File-Hash $recordCopy) -ceq $recordHash) '扩展路径别名覆盖了回放输入。'
    Assert-Check (-not (Test-Path -LiteralPath (Join-Path $aliasDirectory 'saves'))) '拒绝回放输入别名时创建了存档。'
    $checks.Add('普通回放输入与扩展路径战报指向同一文件：拒绝且输入哈希不变')

    foreach ($mode in @('ordinary', 'extended')) {
        $profileDirectory = New-Case ("profile-$mode")
        $savePath = Join-Path $profileDirectory 'saves'
        [void][IO.Directory]::CreateDirectory($savePath)
        $profilePath = Join-Path $savePath 'profile.json'
        [IO.File]::Copy((Join-Path $normalDirectory 'saves/profile.json'), $profilePath)
        $beforeProfile = Save-Fingerprint $profileDirectory
        $target = if ($mode -eq 'extended') { '\\?\' + $profilePath } else { $profilePath }
        $conflict = Invoke-CliCheck ("06-profile-$mode") $profileDirectory ($gameArguments + @('--save-dir', $savePath, '--report', $target)) '' 1
        Assert-ReportFailed $conflict
        Assert-Check ((Save-Fingerprint $profileDirectory) -ceq $beforeProfile) '档案路径冲突未在开局前拒绝，旧档案哈希或文件清单改变。'
    }
    $checks.Add('普通和扩展路径指向新局档案：拒绝且旧档案哈希不变')

    $reserved = @(
        @{ Name = 'save-directory'; Relative = '' },
        @{ Name = 'replay-directory'; Relative = 'replays' },
        @{ Name = 'profile-temp'; Relative = '.profile.json.tmp' },
        @{ Name = 'profile-lock'; Relative = '.profile.lock' },
        @{ Name = 'replay-temp'; Relative = 'replays/.replay.json.tmp' },
        @{ Name = 'replay-lock'; Relative = 'replays/.replays.lock' }
    )
    foreach ($case in $reserved) {
        $directory = New-Case ("reserved-$($case.Name)")
        $savePath = Join-Path $directory 'saves'
        $target = if ($case.Relative.Length -eq 0) { $savePath } else { Join-Path $savePath $case.Relative }
        $conflict = Invoke-CliCheck ("07-reserved-$($case.Name)") $directory ($gameArguments + @('--save-dir', $savePath, '--report', $target)) '' 1
        Assert-ReportFailed $conflict
        Assert-Check (-not (Test-Path -LiteralPath $savePath)) "$($case.Name) 冲突未在创建存档路径前拒绝。"
    }
    $checks.Add('尚不存在的存档目录、回放目录、双方临时文件和锁路径均拒绝作为新局战报目标')

    $settingsReserved = @('settings.json', '.settings.json.tmp', '.settings.lock',
        'replays/.replay-ownership.json', 'replays/.replay-ownership.json.tmp')
    $caseNumber = 0
    foreach ($relative in $settingsReserved) {
        foreach ($mode in @('game', 'replay')) {
            foreach ($form in @('ordinary', 'extended')) {
                $caseNumber++
                $directory = New-Case ("settings-reserved-$caseNumber")
                $savePath = Join-Path $directory 'saves'
                $reservedPath = Join-Path $savePath $relative
                [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($reservedPath))
                [IO.File]::WriteAllText($reservedPath, 'preserve local data', $utf8)
                $fingerprint = Save-Fingerprint $directory
                $target = if ($form -eq 'extended') { '\\?\' + $reservedPath } else { $reservedPath }
                $parameters = if ($mode -eq 'game') { $gameArguments } else { @('--replay', $recordPath, '--auto') }
                $conflict = Invoke-CliCheck ("08-settings-reserved-$caseNumber") $directory ($parameters + @('--save-dir', $savePath, '--report', $target)) '' 1
                Assert-ReportFailed $conflict
                Assert-Check ((Save-Fingerprint $directory) -ceq $fingerprint) "$relative 在 $mode/$form 路径冲突后被改写。"
                Assert-Check (-not $conflict.Out.Contains('对战开始')) '设置路径冲突未在开局或回看前拒绝。'
            }
        }
    }
    $checks.Add('设置及回放管理索引的五种保留路径：新局/回看和普通/扩展路径均拒绝，原文件不变')

    $report = [ordered]@{ status = 'PASS'; checks = $checks.ToArray(); invocations = $invocations.ToArray() }
    [IO.File]::WriteAllText((Join-Path $runDirectory 'report.json'), ($report | ConvertTo-Json -Depth 8), $utf8)
    Write-Output "PASS：$($checks.Count) 组战报与路径保护检查；证据：$runDirectory"
}
catch {
    $report = [ordered]@{ status = 'FAIL'; error = $_.Exception.Message; checks = $checks.ToArray(); invocations = $invocations.ToArray() }
    [IO.File]::WriteAllText((Join-Path $runDirectory 'report.json'), ($report | ConvertTo-Json -Depth 8), $utf8)
    throw
}
