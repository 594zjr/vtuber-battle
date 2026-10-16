#requires -Version 7.0
param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug')

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$repositoryPrefix = $repository.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$cli = Join-Path $PSScriptRoot "bin/$Configuration/net8.0/Cli.dll"
if (-not (Test-Path -LiteralPath $cli -PathType Leaf)) { throw "请先构建 Tools/Cli -c $Configuration。" }
$run = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ('obj/season-summary-check/' + [Guid]::NewGuid().ToString('N'))))
if (-not $run.StartsWith($repositoryPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw '验收产物必须位于当前工作树内。' }
[void][IO.Directory]::CreateDirectory($run)
$utf8 = [Text.UTF8Encoding]::new($false, $true)
$children = [Collections.Generic.List[object]]::new()
$checks = [Collections.Generic.List[string]]::new()
$cleanupFailures = [Collections.Generic.List[string]]::new()
$hostTeam = 'bell,enka,shiori,lumi,hane,momo'
$guestTeam = 'kei,yuan,nyako,buck,kasumi,bee'
$failure = $null

function Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "$Message 证据目录：$run" }
}

function Read-Log([string]$Path) {
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
    $reader = [IO.StreamReader]::new($stream, $utf8)
    try { return $reader.ReadToEnd() } finally { $reader.Dispose() }
}

function Start-Cli([string]$Name, [string[]]$Parameters) {
    $work = Join-Path $run "calls/$Name"
    [void][IO.Directory]::CreateDirectory($work)
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = 'dotnet'
    $start.ArgumentList.Add($cli)
    foreach ($argument in $Parameters) { $start.ArgumentList.Add($argument) }
    $start.WorkingDirectory = $work
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardInputEncoding = $utf8
    $start.StandardOutputEncoding = $utf8
    $start.StandardErrorEncoding = $utf8
    $start.Environment['TEMP'] = $run
    $start.Environment['TMP'] = $run
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    $child = [pscustomobject]@{
        Name = $Name; Process = $process; Started = $false
        OutPath = (Join-Path $work 'stdout.log'); ErrPath = (Join-Path $work 'stderr.log')
        OutStream = $null; ErrStream = $null; OutTask = $null; ErrTask = $null
    }
    $children.Add($child)
    $child.OutStream = [IO.FileStream]::new($child.OutPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::ReadWrite, 1, [IO.FileOptions]::Asynchronous)
    $child.ErrStream = [IO.FileStream]::new($child.ErrPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::ReadWrite, 1, [IO.FileOptions]::Asynchronous)
    $child.Started = $process.Start()
    Check $child.Started "$Name 无法启动。"
    $child.OutTask = $process.StandardOutput.BaseStream.CopyToAsync($child.OutStream)
    $child.ErrTask = $process.StandardError.BaseStream.CopyToAsync($child.ErrStream)
    return $child
}

function Complete-Output($Child) {
    Check ([Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($Child.OutTask, $Child.ErrTask), 5000)) "$($Child.Name) 输出未结束。"
    $Child.OutStream.Flush()
    $Child.ErrStream.Flush()
}

function Wait-Output($Child, [string]$Pattern) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    while ($timer.ElapsedMilliseconds -lt 15000) {
        $match = [regex]::Match((Read-Log $Child.OutPath), $Pattern)
        if ($match.Success) { return $match }
        if ($Child.Process.HasExited) {
            # 退出和输出复制存在先后差，先排空再作最终判断。
            Complete-Output $Child
            $match = [regex]::Match((Read-Log $Child.OutPath), $Pattern)
            if ($match.Success) { return $match }
            throw "$($Child.Name) 已退出但缺少预期输出。证据目录：$run"
        }
        Start-Sleep -Milliseconds 20
    }
    throw "$($Child.Name) 等待输出超时。证据目录：$run"
}

function Wait-Cli($Child, [int]$Exit = 0, [string]$Diagnostic = '错误：') {
    Check ($Child.Process.WaitForExit(30000)) "$($Child.Name) 超过 30 秒未退出。"
    Complete-Output $Child
    $result = [pscustomobject]@{ Out = (Read-Log $Child.OutPath); Err = (Read-Log $Child.ErrPath); Exit = $Child.Process.ExitCode }
    Check ($result.Exit -eq $Exit) "$($Child.Name) 应退出 $Exit，实际 $($result.Exit)。"
    if ($Exit -eq 0) { Check ([string]::IsNullOrWhiteSpace($result.Err)) "$($Child.Name) 正常运行出现错误。" }
    else { Check ($result.Err.StartsWith($Diagnostic)) "$($Child.Name) 未提供预期的中文失败说明。" }
    return $result
}

function Invoke-Cli([string]$Name, [string[]]$Parameters, [int]$Exit = 0) {
    $child = Start-Cli $Name $Parameters
    $child.Process.StandardInput.Close()
    return Wait-Cli $child $Exit
}

function Fingerprint([string]$Directory) {
    if (-not (Test-Path -LiteralPath $Directory)) { return '<missing>' }
    return (@(Get-ChildItem -LiteralPath $Directory -Recurse -Force | Sort-Object FullName | ForEach-Object {
        if ($_.PSIsContainer) { 'directory:' + $_.FullName }
        else { $_.FullName + ':' + $_.LastWriteTimeUtc.Ticks + ':' + (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    }) -join "`n")
}

function Read-Profile([string]$Directory) {
    return ([IO.File]::ReadAllText((Join-Path $Directory 'profile.json'), $utf8) | ConvertFrom-Json -AsHashtable)
}

function Invoke-Summary([string]$Name, [string]$Directory, [int]$Season = 0, [int]$Exit = 0) {
    $before = Fingerprint $Directory
    $arguments = @('--season-summary')
    if ($Season -gt 0) { $arguments += [string]$Season }
    $result = Invoke-Cli $Name ($arguments + @('--save-dir', $Directory)) $Exit
    Check ((Fingerprint $Directory) -ceq $before) '赛季查询改写了档案、录像、索引、锁文件或目录内容。'
    Check (-not $result.Out.Contains('监听端口：') -and -not $result.Out.Contains('对局结束：')) '只读查询启动了对战。'
    return $result
}

function Assert-Counts([string]$Text, [int]$Wins, [int]$Losses, [int]$Draws) {
    $sequence = "$Wins\s*胜\s*[/／、， ]*\s*$Losses\s*负\s*[/／、， ]*\s*$Draws\s*平"
    $labelled = "胜[：: ]+$Wins[^\r\n]*负[：: ]+$Losses[^\r\n]*平[：: ]+$Draws"
    $table = "胜\s*[/／]\s*负\s*[/／]\s*平[：: ]+$Wins\s*[/／]\s*$Losses\s*[/／]\s*$Draws"
    Check ([regex]::IsMatch($Text, "(?:$sequence|$labelled|$table)")) "摘要胜负平应为 $Wins/$Losses/$Draws。"
}

function Read-RankedArchive([string]$Directory, [int]$Side, [int]$After) {
    $files = @(Get-ChildItem -LiteralPath (Join-Path $Directory 'replay-library') -File -Filter '*.json' | Where-Object { $_.Name -ne 'index.json' })
    Check ($files.Count -eq 1) '首次联网完局没有恰好归档一份录像。'
    $record = [IO.File]::ReadAllText($files[0].FullName, $utf8) | ConvertFrom-Json -AsHashtable
    Check ($record.library.Contains('ranked')) '真实联网终局没有自动写入排位标签。'
    $tag = $record.library.ranked
    Check ($tag.version -eq 1 -and $tag.format -ceq '默认' -and $tag.season -eq 1 -and $tag.gameNumber -eq 1 -and
        $tag.playerSide -eq $Side -and $tag.ratingBefore -eq 1500 -and $tag.ratingAfter -eq $After -and
        [regex]::IsMatch($tag.recordHash, '^[0-9a-f]{64}$')) '自动排位标签的赛季、局序、本人视角、分数或记录摘要不正确。'
    return $files[0]
}

function Invoke-FailedSave([string]$Name, [string]$HostDirectory, [string]$BlockedPath) {
    $hostChild = Start-Cli ($Name + '-host') @('--host', '0', '--team', $hostTeam, '--seed', '125', '--save-dir', $HostDirectory)
    $port = (Wait-Output $hostChild '(?m)^监听端口：([0-9]+)\r?\n').Groups[1].Value
    $guestDirectory = Join-Path $run ($Name + '-guest-saves')
    $guestChild = Start-Cli ($Name + '-guest') @('--join', "127.0.0.1:$port", '--team', $guestTeam, '--auto', '--save-dir', $guestDirectory)
    $null = Wait-Output $hostChild '选择行动（q 退出）> '
    # 握手完成后制造本局保存故障，避免把建连失败误当作完局保存验收。
    [void][IO.Directory]::CreateDirectory($HostDirectory)
    if ($BlockedPath -ceq '.profile.json.tmp') { [void][IO.Directory]::CreateDirectory((Join-Path $HostDirectory $BlockedPath)) }
    else { [IO.File]::WriteAllText((Join-Path $HostDirectory $BlockedPath), '本脚本创建的保存失败夹具', $utf8) }
    $hostChild.Process.StandardInput.WriteLine('/surrender')
    $hostChild.Process.StandardInput.Flush()
    $hostChild.Process.StandardInput.Close()
    $result = Wait-Cli $hostChild 1 '警告：'
    $guestResult = Wait-Cli $guestChild
    Check ($result.Out.Contains('对局结束：客机 获胜') -and $guestResult.Out.Contains('对局结束：客机 获胜')) '保存失败用例没有完成真实联网终局。'
    return $result
}

try {
    $missing = Join-Path $run 'missing-parent/saves'
    $empty = Invoke-Summary 'empty' $missing
    Check (-not (Test-Path -LiteralPath (Split-Path $missing -Parent)) -and $empty.Out.Contains('第 1 赛季') -and
        $empty.Out.Contains('1500') -and $empty.Out.Contains('0')) '空库查询未显示初始赛季，或创建了存档目录。'
    Assert-Counts $empty.Out 0 0 0
    $checks.Add('空档案和录像库查询只读，显示第 1 赛季初始状态')

    $hostSave = Join-Path $run 'host-saves'
    $guestSave = Join-Path $run 'guest-saves'
    $hostChild = Start-Cli 'network-host' @('--host', '0', '--team', $hostTeam, '--seed', '125', '--save-dir', $hostSave)
    # 管道可拆分 WriteLine，完整换行到达才读取端口数字。
    $port = (Wait-Output $hostChild '(?m)^监听端口：([0-9]+)\r?\n').Groups[1].Value
    $guest = Start-Cli 'network-guest' @('--join', "127.0.0.1:$port", '--team', $guestTeam, '--auto', '--save-dir', $guestSave)
    $null = Wait-Output $hostChild '选择行动（q 退出）> '
    $hostChild.Process.StandardInput.WriteLine('/surrender')
    $hostChild.Process.StandardInput.Flush()
    $hostChild.Process.StandardInput.Close()
    $hostResult = Wait-Cli $hostChild
    $guestResult = Wait-Cli $guest
    Check ($hostResult.Out.Contains('对局结束：客机 获胜') -and $guestResult.Out.Contains('对局结束：客机 获胜')) '真实联网投降没有产生双方一致的结果。'
    $hostArchive = Read-RankedArchive $hostSave 0 1480
    $guestArchive = Read-RankedArchive $guestSave 1 1520
    $hostSummary = Invoke-Summary 'host-summary' $hostSave
    $guestSummary = Invoke-Summary 'guest-summary' $guestSave
    Assert-Counts $hostSummary.Out 0 1 0
    Assert-Counts $guestSummary.Out 1 0 0
    Check ($hostSummary.Out.Contains('1480') -and $guestSummary.Out.Contains('1520') -and
        -not $hostSummary.Out.Contains('有 1 局录像缺失') -and -not $guestSummary.Out.Contains('有 1 局录像缺失')) '本人评级或录像覆盖错误。'
    $checks.Add('真实联网完局自动保存双方排位标签；主机负、客机胜；赛季摘要本人视角与分数正确')

    $ai = Start-Cli 'local-ai' @('--team', $hostTeam, '--seed', '125', '--save-dir', $hostSave)
    $null = Wait-Output $ai '选择行动（q 退出）> '
    $ai.Process.StandardInput.WriteLine('/surrender')
    $ai.Process.StandardInput.Flush()
    $ai.Process.StandardInput.Close()
    $null = Wait-Cli $ai
    $afterAi = Invoke-Summary 'after-ai' $hostSave
    Check ($afterAi.Out -ceq $hostSummary.Out) '对电脑完成一局改变了排位赛季摘要。'
    Check ((Read-Profile $hostSave).ratings.'默认'.games -eq 1) '对电脑对局增加了排位场数。'
    $rankedCount = 0
    foreach ($file in Get-ChildItem -LiteralPath (Join-Path $hostSave 'replay-library') -File -Filter '*.json') {
        if ($file.Name -eq 'index.json') { continue }
        $record = [IO.File]::ReadAllText($file.FullName, $utf8) | ConvertFrom-Json -AsHashtable
        if ($record.library.Contains('ranked')) { $rankedCount++ }
    }
    Check ($rankedCount -eq 1) '非排位录像被错误打上排位标签。'
    $checks.Add('对电脑仍保存普通录像，但不改变排位标签数量、场数或摘要')

    $null = Invoke-Cli 'new-season' @('--new-season', '--save-dir', $guestSave)
    $current = Invoke-Summary 'current-season' $guestSave
    $history = Invoke-Summary 'history-season-1' $guestSave 1
    Check ($current.Out.Contains('第 2 赛季') -and $current.Out.Contains('1510')) '切季后的默认查询没有采用新赛季。'
    Assert-Counts $current.Out 0 0 0
    Check ($history.Out.Contains('第 1 赛季') -and $history.Out.Contains('1520')) '历史查询没有保留旧赛季最终分。'
    Assert-Counts $history.Out 1 0 0
    $unknown = Invoke-Summary 'future-season' $guestSave 3 1
    Check ($unknown.Err.Contains('赛季')) '不存在的赛季没有明确拒绝。'
    $checks.Add('切季后默认查询当前赛季；显式历史查询保留旧结果；未来赛季拒绝')

    # 只删除本脚本刚生成的录像文件，保留旧索引以验证查询以录像为准且不会修复索引。
    $archivePath = [IO.Path]::GetFullPath($guestArchive.FullName)
    $allowedArchivePrefix = [IO.Path]::GetFullPath((Join-Path $run 'guest-saves/replay-library')).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    Check ($archivePath.StartsWith($allowedArchivePrefix, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetExtension($archivePath) -ceq '.json' -and [IO.Path]::GetFileName($archivePath) -cne 'index.json') '缺局夹具的目标路径越界。'
    Remove-Item -LiteralPath $archivePath
    $incomplete = Invoke-Summary 'missing-ranked-replay' $guestSave 1
    Check ($incomplete.Out.Contains('有 1 局录像缺失，部分统计不完整') -and $incomplete.Out.Contains('1520')) '缺少录像时未提示准确缺局数，或丢失档案最终分。'
    Assert-Counts $incomplete.Out 0 0 0
    $checks.Add('真实排位录像缺失时提示 1 局不足；保留档案最终分；不改写残留索引和档案')

    $badProfileSave = Join-Path $run 'bad-profile-saves'
    [void][IO.Directory]::CreateDirectory($badProfileSave)
    [IO.File]::WriteAllText((Join-Path $badProfileSave 'profile.json'), '{broken', $utf8)
    $badProfile = Invoke-Summary 'bad-profile-query' $badProfileSave 0 1
    Check (-not $badProfile.Out.Contains('排位赛季总结')) '坏档案查询伪装成初始赛季成功输出。'
    $checks.Add('坏档案只读查询明确失败，原字节、时间戳与目录内容均不改写')

    $failedProfileSave = Join-Path $run 'failed-profile-saves'
    $failedProfile = Invoke-FailedSave 'failed-profile' $failedProfileSave '.profile.json.tmp'
    Check ($failedProfile.Err.Contains('档案保存失败：') -and -not $failedProfile.Err.Contains('录像库保存失败：')) '档案失败没有独立于录像库保存处理。'
    Check (-not (Test-Path -LiteralPath (Join-Path $failedProfileSave 'profile.json'))) '档案保存失败却写入了新的排位档案。'
    $ordinaryFiles = @(Get-ChildItem -LiteralPath (Join-Path $failedProfileSave 'replay-library') -File -Filter '*.json' | Where-Object { $_.Name -ne 'index.json' })
    Check ($ordinaryFiles.Count -eq 1) '档案失败时没有保留一份独立的普通录像。'
    $ordinary = [IO.File]::ReadAllText($ordinaryFiles[0].FullName, $utf8) | ConvertFrom-Json -AsHashtable
    Check (-not $ordinary.library.Contains('ranked')) '档案结算未落盘却给普通录像打上了排位标签。'
    $ordinarySummary = Invoke-Summary 'failed-profile-query' $failedProfileSave
    Assert-Counts $ordinarySummary.Out 0 0 0
    Check ($ordinarySummary.Out.Contains('档案场数：0；可统计录像：0')) '未成功结算的普通录像被计入了排位摘要。'
    $checks.Add('真实联网档案写入失败返回 1；独立普通录像仍保存且无排位标签')

    $failedLibrarySave = Join-Path $run 'failed-library-saves'
    $failedLibrary = Invoke-FailedSave 'failed-library' $failedLibrarySave 'replay-library'
    Check ($failedLibrary.Err.Contains('录像库保存失败：') -and -not $failedLibrary.Err.Contains('档案保存失败：')) '录像库失败没有独立于档案保存处理。'
    $savedRating = (Read-Profile $failedLibrarySave).ratings.'默认'
    Check ($savedRating.games -eq 1 -and $savedRating.rating -eq 1480) '录像库失败回滚或丢失了已经完成的排位结算。'
    $invalidLibrary = Invoke-Summary 'invalid-library-query' $failedLibrarySave 0 1
    Check (-not $invalidLibrary.Out.Contains('排位赛季总结')) '录像库路径是文件时伪装成空录像库成功输出。'
    $blockedLibraryPath = [IO.Path]::GetFullPath((Join-Path $failedLibrarySave 'replay-library'))
    $expectedBlockedPath = [IO.Path]::GetFullPath((Join-Path $run 'failed-library-saves/replay-library'))
    Check ($blockedLibraryPath -ceq $expectedBlockedPath -and (Test-Path -LiteralPath $blockedLibraryPath -PathType Leaf)) '录像库故障夹具路径越界或类型改变。'
    Remove-Item -LiteralPath $blockedLibraryPath
    [void][IO.Directory]::CreateDirectory($blockedLibraryPath)
    $missingLibrary = Invoke-Summary 'repaired-empty-library-query' $failedLibrarySave
    Check ($missingLibrary.Out.Contains('有 1 局录像缺失，部分统计不完整') -and
        $missingLibrary.Out.Contains('档案场数：1；可统计录像：0') -and $missingLibrary.Out.Contains('1480')) '合法空库没有如实报告已结算但缺录像的 1 局。'
    Assert-Counts $missingLibrary.Out 0 0 0
    $checks.Add('真实联网录像库失败返回 1；档案仍累计排位；错误路径查询失败，合法空库明确显示缺 1 局')

    $invalidSave = Join-Path $run 'invalid-parent/saves'
    $cases = @(
        @{ Name = 'zero'; Args = @('--season-summary', '0') },
        @{ Name = 'negative'; Args = @('--season-summary', '-1') },
        @{ Name = 'word'; Args = @('--season-summary', 'abc') },
        @{ Name = 'overflow'; Args = @('--season-summary', '2147483648') },
        @{ Name = 'duplicate'; Args = @('--season-summary', '--season-summary') },
        @{ Name = 'host'; Args = @('--season-summary', '--host', '0') },
        @{ Name = 'auto'; Args = @('--season-summary', '--auto') },
        @{ Name = 'new-season'; Args = @('--season-summary', '--new-season') },
        @{ Name = 'history'; Args = @('--season-summary', '--rating-history') }
    )
    foreach ($case in $cases) {
        $rejected = Invoke-Cli ('invalid-' + $case.Name) ($case.Args + @('--save-dir', $invalidSave)) 1
        Check ($rejected.Err.Contains('--season-summary') -and -not $rejected.Out.Contains('监听端口：')) '非法赛季参数没有在联网或读写前拒绝。'
        Check (-not (Test-Path -LiteralPath (Split-Path $invalidSave -Parent))) '非法参数创建了存档目录。'
    }
    $checks.Add('非法、溢出、重复赛季号及混用其他模式均在产生副作用前拒绝')
}
catch { $failure = $_ }
finally {
    foreach ($child in $children) {
        try {
            if ($child.Started -and -not $child.Process.HasExited) {
                $child.Process.Kill($true)
                Check ($child.Process.WaitForExit(5000)) "$($child.Name) 清理时未退出。"
            }
            if ($null -ne $child.OutTask) { Check ($child.OutTask.Wait(5000)) "$($child.Name) 标准输出未排空。" }
            if ($null -ne $child.ErrTask) { Check ($child.ErrTask.Wait(5000)) "$($child.Name) 错误输出未排空。" }
        }
        catch { $cleanupFailures.Add("$($child.Name)：$($_.Exception.Message)") }
        finally {
            if ($null -ne $child.OutStream) { $child.OutStream.Dispose() }
            if ($null -ne $child.ErrStream) { $child.ErrStream.Dispose() }
            $child.Process.Dispose()
        }
    }
}
$status = if ($null -eq $failure -and $cleanupFailures.Count -eq 0) { 'PASS' } else { 'FAIL' }
$report = [ordered]@{
    Status = $status; Checks = $checks.ToArray(); Processes = $children.Count
    Failure = $(if ($null -eq $failure) { $null } else { $failure.Exception.Message }); CleanupFailures = $cleanupFailures.ToArray()
}
[IO.File]::WriteAllText((Join-Path $run 'report.json'), ($report | ConvertTo-Json -Depth 5), $utf8)
if ($null -ne $failure) { throw $failure }
Check ($cleanupFailures.Count -eq 0) ('子进程清理失败：' + ($cleanupFailures -join '；'))
Write-Output "PASS：$($checks.Count) 组赛季总结检查，$($children.Count) 个 CLI 进程；证据：$run"
