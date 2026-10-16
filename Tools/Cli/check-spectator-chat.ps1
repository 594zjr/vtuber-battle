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
if (-not (Test-Path -LiteralPath $cliDll -PathType Leaf)) { throw '请先构建 Tools/Cli。' }
$runDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ('obj/spectator-chat-check/' + [Guid]::NewGuid().ToString('N'))))
if (-not $runDirectory.StartsWith($repositoryPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw '产物必须位于仓库内。' }
[void][IO.Directory]::CreateDirectory($runDirectory)
$utf8 = [Text.UTF8Encoding]::new($false)
$children = [Collections.Generic.List[object]]::new()
$checks = [Collections.Generic.List[string]]::new()
$hashes = [Collections.Generic.List[string]]::new()
$cleanupFailures = [Collections.Generic.List[string]]::new()
$hostTeam = 'bell,enka,shiori,lumi,hane,momo'
$guestTeam = 'kei,yuan,nyako,buck,kasumi,bee'
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
    $workingDirectory = Join-Path $runDirectory $Name
    [void][IO.Directory]::CreateDirectory($workingDirectory)
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = 'dotnet'
    $start.ArgumentList.Add($cliDll)
    foreach ($parameter in $Parameters) { $start.ArgumentList.Add($parameter) }
    $start.WorkingDirectory = $workingDirectory
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardInputEncoding = $utf8
    $start.StandardOutputEncoding = $utf8
    $start.StandardErrorEncoding = $utf8
    $start.Environment['TEMP'] = $runDirectory
    $start.Environment['TMP'] = $runDirectory
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    $child = [pscustomobject]@{
        Name = $Name; Process = $process; WorkingDirectory = $workingDirectory; Started = $false
        OutPath = (Join-Path $runDirectory "$Name.stdout.log"); ErrPath = (Join-Path $runDirectory "$Name.stderr.log")
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

function Complete-Output($Child) {
    Assert-Check ([Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($Child.OutTask, $Child.ErrTask), 5000)) "$($Child.Name) 输出复制未完成。"
    $Child.OutStream.Flush()
    $Child.ErrStream.Flush()
}

function Wait-Output($Child, [string]$Pattern, [int]$Count = 1, [int]$TimeoutMs = 15000) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    while ($timer.ElapsedMilliseconds -lt $TimeoutMs) {
        $matches = [regex]::Matches((Read-Log $Child.OutPath), $Pattern)
        if ($matches.Count -ge $Count) { return $matches[$Count - 1] }
        if ($Child.Process.HasExited) {
            # 进程退出先于异步输出复制完成，排空后再检查，避免把完整输出误判为缺失。
            Complete-Output $Child
            $matches = [regex]::Matches((Read-Log $Child.OutPath), $Pattern)
            if ($matches.Count -ge $Count) { return $matches[$Count - 1] }
            throw "$($Child.Name) 已退出但缺少预期输出。证据目录：$runDirectory"
        }
        Start-Sleep -Milliseconds 20
    }
    throw "$($Child.Name) 等待输出超时。证据目录：$runDirectory"
}

function Wait-Cli($Child, [int]$ExpectedExit) {
    Assert-Check ($Child.Process.WaitForExit(30000)) "$($Child.Name) 30 秒内未退出。"
    Complete-Output $Child
    $Child.Result = [pscustomobject]@{
        Exit = $Child.Process.ExitCode; Out = (Read-Log $Child.OutPath); Err = (Read-Log $Child.ErrPath)
    }
    Assert-Check ($Child.Result.Exit -eq $ExpectedExit) "$($Child.Name) 应退出 $ExpectedExit，实际 $($Child.Result.Exit)。"
    return $Child.Result
}

function Send-Lines($Child, [string[]]$Lines) {
    foreach ($line in $Lines) { $Child.Process.StandardInput.WriteLine($line) }
    $Child.Process.StandardInput.Flush()
}

function Final-Hash([string]$Text) {
    $matches = [regex]::Matches($Text, '(?m)^最终状态哈希：([0-9a-f]{16})\r?$')
    Assert-Check ($matches.Count -eq 1) '完成进程没有且仅有一个最终哈希。'
    return $matches[0].Groups[1].Value
}

function Chat-Count([string]$Text) {
    return [regex]::Matches($Text, '(?m)^观众「[^\r\n]*」：[^\r\n]*\r?$').Count
}

function Assert-NoSave($Child) {
    Assert-Check (-not (Test-Path -LiteralPath (Join-Path $Child.WorkingDirectory 'saves'))) '观战或参数检查创建了默认存档目录。'
    Assert-Check (@(Get-ChildItem -LiteralPath $Child.WorkingDirectory -File -Recurse -Force).Count -eq 0) '只读进程写入了工作目录文件。'
}

function Start-Match([string]$Name, [string[]]$HostOptions, [string]$SenderName) {
    $hostChild = Start-Cli "$Name-host" (@('--host', '0', '--spectate-port', '0', '--seed', '122', '--team', $hostTeam) + $HostOptions)
    # 必须等完整端口行，不能把被管道拆开的十进制前缀当成实际端口。
    $spectatePort = [int](Wait-Output $hostChild '(?m)^观战端口：([0-9]+)\r?\n').Groups[1].Value
    $playPort = [int](Wait-Output $hostChild '(?m)^监听端口：([0-9]+)\r?\n').Groups[1].Value
    Assert-Check ($spectatePort -gt 0 -and $playPort -gt 0 -and $spectatePort -ne $playPort) '未分配独立的有效监听端口。'
    $address = "127.0.0.1:$spectatePort"
    $sender = Start-Cli "$Name-sender" @('--watch', $address, '--watch-name', $SenderName, '--hash')
    $receiver = Start-Cli "$Name-receiver" @('--watch', $address, '--watch-name', '观众乙', '--hash')
    $null = Wait-Output $hostChild '观众已连接（累计 2 位）'
    $guest = Start-Cli "$Name-guest" @('--join', "127.0.0.1:$playPort", '--team', $guestTeam, '--auto')
    $null = Wait-Output $hostChild '选择行动（q 退出）> '
    $null = Wait-Output $sender '观众昵称：'
    $null = Wait-Output $receiver '观众昵称：'
    return [pscustomobject]@{ Host = $hostChild; Guest = $guest; Sender = $sender; Receiver = $receiver; Address = $address }
}

function Finish-Match($Match) {
    Send-Lines $Match.Host @('/surrender')
    $Match.Host.Process.StandardInput.Close()
    $hostResult = Wait-Cli $Match.Host 0
    $guestResult = Wait-Cli $Match.Guest 0
    $senderResult = Wait-Cli $Match.Sender 0
    $receiverResult = Wait-Cli $Match.Receiver 0
    $hash = Final-Hash $hostResult.Out
    foreach ($result in @($hostResult, $guestResult, $senderResult, $receiverResult)) {
        Assert-Check ((Final-Hash $result.Out) -ceq $hash) '选手与观众的最终哈希不一致。'
        Assert-Check ($result.Err.Length -eq 0) '正常连接出现错误输出。'
    }
    foreach ($result in @($hostResult, $guestResult)) {
        Assert-Check ((Chat-Count $result.Out) -eq 0 -and -not $result.Out.Contains('观众快捷语已提交：')) '观众快捷语被发给了选手。'
    }
    foreach ($viewer in @($Match.Sender, $Match.Receiver)) {
        Assert-Check ($viewer.Result.Out.Contains('观战结束：客机 获胜')) '输入 EOF 后观众没有等到真实结束标记。'
        Assert-NoSave $viewer
    }
    $hashes.Add($hash)
}

try {
    $rawName = '观' + [char]0x200b + '众' + [char]0x202e + '甲'
    $enabled = Start-Match 'default-on' @() $rawName
    $null = Wait-Output $enabled.Sender '观众昵称：观众甲；'
    Send-Lines $enabled.Sender @('/say 0 自由文字', '/say 99', '/wave', '/say 0', '/say 1', '/say 2', '/say 3')
    $null = Wait-Output $enabled.Sender '(?m)^观战只支持 /say 0～7 的固定短句，或 q 退出。\r?$' 3
    $null = Wait-Output $enabled.Sender '(?m)^观众快捷语已提交：' 3
    $null = Wait-Output $enabled.Sender '(?m)^观众快捷语未发送：'
    $null = Wait-Output $enabled.Receiver '(?m)^观众「观众甲」：' 3
    Send-Lines $enabled.Receiver @('/say 6')
    $null = Wait-Output $enabled.Sender '(?m)^观众「观众乙」：谢谢\r?$'
    $enabled.Sender.Process.StandardInput.Close()
    $enabled.Receiver.Process.StandardInput.Close()

    $quitting = Start-Cli 'quitting-viewer' @('--watch', $enabled.Address)
    $null = Wait-Output $quitting '观战开始'
    Send-Lines $quitting @('q')
    $quitResult = Wait-Cli $quitting 0
    Assert-Check ($quitResult.Out.Contains('已退出观战。') -and -not $quitResult.Out.Contains('观战结束：')) 'q 没有退出，或伪造了完成的对局。'
    Assert-NoSave $quitting
    Finish-Match $enabled
    $senderOutput = $enabled.Sender.Result.Out
    $receiverOutput = $enabled.Receiver.Result.Out
    Assert-Check ((Chat-Count $receiverOutput) -eq 3 -and (Chat-Count $senderOutput) -eq 1) '同回合配额、只转发给其他观众或双向发送不正确。'
    foreach ($text in @('你好', '打得好', '好险')) {
        Assert-Check ($receiverOutput.Contains("观众「观众甲」：$text")) '合法固定短句没有收到。'
    }
    Assert-Check (-not $receiverOutput.Contains('观众「观众甲」：再来一局') -and
        -not $receiverOutput.Contains('自由文字') -and -not $receiverOutput.Contains($rawName)) '限流、固定短句或昵称清理失效。'
    $checks.Add('默认开启：双向观众快捷语、昵称清理、非法命令拒绝、每回合限 3 条、发送者不回显转发、选手零聊天')
    $checks.Add('观众 q 独立退出；其他观众 EOF 后继续观看到投降终局；四端哈希一致且观众零存档')

    $disabled = Start-Match 'disabled' @('--spectator-chat', 'off') '观众甲'
    Send-Lines $disabled.Sender @('/say 0', '/say 1', '/say 2', '/say 3')
    $null = Wait-Output $disabled.Sender '(?m)^观众快捷语(?:已提交|未发送)：' 4
    $disabled.Sender.Process.StandardInput.Close()
    $disabled.Receiver.Process.StandardInput.Close()
    Finish-Match $disabled
    Assert-Check ((Chat-Count $disabled.Sender.Result.Out) -eq 0 -and
        (Chat-Count $disabled.Receiver.Result.Out) -eq 0) '房主关闭后仍向观众转发了聊天。'
    Assert-Check ($hashes[0] -ceq $hashes[1]) '同种子与同一投降输入，有无聊天的终局哈希不同。'
    $checks.Add('关闭聊天仍正常观战和 EOF 收尾；不转发给观众或选手；有无聊天同种子终局哈希相同')

    $cases = @(
        @{ Name = 'chat-without-port'; Args = @('--host', '0', '--spectator-chat', 'off'); Error = '--spectator-chat' },
        @{ Name = 'chat-without-host'; Args = @('--spectate-port', '0', '--spectator-chat', 'off'); Error = '--spectate-port' },
        @{ Name = 'chat-duplicate'; Args = @('--host', '0', '--spectate-port', '0', '--spectator-chat', 'on', '--spectator-chat', 'off'); Error = '不能重复' },
        @{ Name = 'chat-invalid'; Args = @('--host', '0', '--spectate-port', '0', '--spectator-chat', 'invalid'); Error = 'on 或 off' },
        @{ Name = 'chat-missing'; Args = @('--spectator-chat'); Error = '缺少参数值' },
        @{ Name = 'watch-chat-option'; Args = @('--watch', '127.0.0.1:1', '--spectator-chat', 'off'); Error = '--watch' },
        @{ Name = 'watch-host-option'; Args = @('--watch', '127.0.0.1:1', '--host', '0'); Error = '--watch' },
        @{ Name = 'watch-name-without-watch'; Args = @('--watch-name', '观众甲'); Error = '--watch-name' },
        @{ Name = 'watch-name-duplicate'; Args = @('--watch', '127.0.0.1:1', '--watch-name', '甲', '--watch-name', '乙'); Error = '不能重复' },
        @{ Name = 'watch-player-name'; Args = @('--watch', '127.0.0.1:1', '--name-a', '甲'); Error = '--watch' }
    )
    foreach ($case in $cases) {
        $child = Start-Cli $case.Name $case.Args
        $result = Wait-Cli $child 1
        Assert-Check ($result.Err.Contains($case.Error) -and -not $result.Out.Contains('端口：') -and
            -not $result.Out.Contains('已连接观战')) '非法聊天或昵称选项未在监听、连接前明确拒绝。'
        Assert-NoSave $child
    }
    $checks.Add('参数门禁：缺少端口或主机、重复和非法开关、缺值、观战误用主机选项、昵称缺少观战和重复昵称均拒绝')

    function Get-T122ModeFixtureFingerprint([string]$Root) {
        $rows = [Collections.Generic.List[string]]::new()
        $rows.Add('D|.|' + [IO.Directory]::GetLastWriteTimeUtc($Root).Ticks)
        [string[]]$paths = [IO.Directory]::GetFileSystemEntries($Root, '*', [IO.SearchOption]::AllDirectories)
        [Array]::Sort($paths, [StringComparer]::Ordinal)
        foreach ($path in $paths) {
            $relative = [IO.Path]::GetRelativePath($Root, $path)
            if ([IO.Directory]::Exists($path)) {
                $rows.Add('D|' + $relative + '|' + [IO.Directory]::GetLastWriteTimeUtc($path).Ticks)
            }
            else {
                $rows.Add('F|' + $relative + '|' + [IO.File]::GetLastWriteTimeUtc($path).Ticks + '|' + [Convert]::ToBase64String([IO.File]::ReadAllBytes($path)))
            }
        }
        return [string]::Join([Environment]::NewLine, $rows)
    }

    $modeFixtureRoot = Join-Path $runDirectory 'mode-matrix-existing-profile'
    $modeFixtureKeep = Join-Path $modeFixtureRoot 'keep'
    [void][IO.Directory]::CreateDirectory($modeFixtureKeep)
    $modeFixtureProfile = Join-Path $modeFixtureRoot 'profile.json'
    # 合法的第 2 季档案，带真实历史、定级资格和高于当前分数的峰值。
    $modeFixtureJson = '{"packs":{"original":{"draws":1,"losses":2,"pickWins":{"bell":1},"picks":{"bell":3},"wins":1}},"ratings":{"默认":{"games":1,"rating":1510,"season":2,"peakRating":1520,"isPlaced":true,"history":[{"season":1,"finalRating":1520,"peakRating":1530,"tier":"人气聚光","games":12}]}}}'
    [IO.File]::WriteAllText($modeFixtureProfile, [Environment]::NewLine + '  ' + $modeFixtureJson + [Environment]::NewLine, $utf8)
    [IO.File]::WriteAllText((Join-Path $modeFixtureRoot '.profile.lock'), '', $utf8)
    [IO.File]::WriteAllText((Join-Path $modeFixtureKeep 'sentinel.txt'), '参数拒绝必须保留既有文件。', $utf8)
    $modeFixtureTime = [DateTime]::new(2020, 1, 2, 3, 4, 5, [DateTimeKind]::Utc)
    foreach ($path in [IO.Directory]::GetFiles($modeFixtureRoot, '*', [IO.SearchOption]::AllDirectories)) {
        [IO.File]::SetLastWriteTimeUtc($path, $modeFixtureTime)
    }
    [IO.Directory]::SetLastWriteTimeUtc($modeFixtureKeep, $modeFixtureTime)
    [IO.Directory]::SetLastWriteTimeUtc($modeFixtureRoot, $modeFixtureTime)
    $modeFixtureBefore = Get-T122ModeFixtureFingerprint $modeFixtureRoot
    [IO.File]::WriteAllText((Join-Path $runDirectory 'mode-matrix-fixture-before.txt'), $modeFixtureBefore, $utf8)
    $modeSource = Join-Path $runDirectory 'mode-matrix-unused.json'
    Assert-Check (-not (Test-Path -LiteralPath $modeSource)) '互斥夹具的录像路径应不存在，必须在读来源前拒绝。'

    $modeBases = @(
        [pscustomobject]@{ Name = 'branch'; RatingCommand = $null; Args = [string[]]@('--branch', $modeSource, '--turn', '1', '--side', '0') },
        [pscustomobject]@{ Name = 'new-season'; RatingCommand = '--new-season'; Args = [string[]]@('--new-season') },
        [pscustomobject]@{ Name = 'rating-history'; RatingCommand = '--rating-history'; Args = [string[]]@('--rating-history') }
    )
    $modeExtras = @(
        [pscustomobject]@{ Name = 'watch-name-alone'; Args = [string[]]@('--watch-name', '观众甲'); BranchError = '--watch-name 只能与 --watch 一起使用。'; Option = '--watch-name' },
        [pscustomobject]@{ Name = 'chat-alone'; Args = [string[]]@('--spectator-chat', 'off'); BranchError = '--spectator-chat 只能与 --spectate-port 一起使用。'; Option = '--spectator-chat' },
        [pscustomobject]@{ Name = 'watch-name-complete'; Args = [string[]]@('--watch', '127.0.0.1:1', '--watch-name', '观众甲'); BranchError = '--watch 只读观战，不支持'; Option = '--watch-name' },
        [pscustomobject]@{ Name = 'chat-complete'; Args = [string[]]@('--host', '0', '--spectate-port', '0', '--spectator-chat', 'off'); BranchError = '--branch 是独立的手动接手模式'; Option = '--spectator-chat' }
    )
    $modeMatrixCount = 0
    foreach ($modeBase in $modeBases) {
        foreach ($modeExtra in $modeExtras) {
            foreach ($reverse in @($false, $true)) {
                $order = if ($reverse) { 'extra-first' } else { 'mode-first' }
                $name = 'mode-matrix-' + $modeBase.Name + '-' + $modeExtra.Name + '-' + $order
                [string[]]$parameters = if ($reverse) { $modeExtra.Args + $modeBase.Args } else { $modeBase.Args + $modeExtra.Args }
                $parameters += @('--save-dir', $modeFixtureRoot)
                [IO.File]::WriteAllText((Join-Path $runDirectory ($name + '.argv.json')), ($parameters | ConvertTo-Json -Compress), $utf8)
                $child = Start-Cli $name $parameters
                $child.Process.StandardInput.Close()
                $result = Wait-Cli $child 1
                $after = Get-T122ModeFixtureFingerprint $modeFixtureRoot
                [IO.File]::WriteAllText((Join-Path $runDirectory ($name + '.fixture-after.txt')), $after, $utf8)
                Assert-Check ($after -ceq $modeFixtureBefore) "$name 改写了已有档案字节、mtime、文件或目录树。"
                Assert-Check ($result.Out.Length -eq 0 -and [regex]::IsMatch($result.Err, '[\u4e00-\u9fff]')) "$name 没有在任何输出或开局前给出中文参数错误。"
                Assert-Check (-not $result.Err.Contains('未知参数')) "$name 没有识别新增参数，不能把未知参数错误当作互斥验收通过。"
                if ($null -ne $modeBase.RatingCommand) {
                    Assert-Check ($result.Err.Contains($modeBase.RatingCommand + ' 是独立的排位档案操作') -and
                        $result.Err.Contains($modeExtra.Option)) "$name 没有优先执行赛季独立模式校验。"
                }
                else {
                    Assert-Check ($result.Err.Contains($modeExtra.BranchError)) "$name 没有命中观战依赖或 branch 独立模式的参数校验。"
                    if ($modeExtra.Name -eq 'watch-name-complete') {
                        Assert-Check ($result.Err.Contains('--branch')) "$name 未明确拒绝观战与接手共用。"
                    }
                    if ($modeExtra.Name -eq 'chat-complete') {
                        Assert-Check ($result.Err.Contains('--spectator-chat')) "$name 未明确拒绝接手与完整主机聊天参数共用。"
                    }
                }
                Assert-NoSave $child
                Assert-Check ([IO.Directory]::GetFileSystemEntries($child.WorkingDirectory).Length -eq 0) "$name 在子进程工作目录创建了文件或空目录。"
                Assert-Check (-not (Test-Path -LiteralPath $modeSource)) "$name 创建了本应只作为输入的来源录像。"
                $modeMatrixCount++
            }
        }
    }
    Assert-Check ($modeMatrixCount -eq 24) '必须完整执行 3 模式 × 4 观战参数组 × 2 种顺序的 24 次拒绝。'
    $checks.Add('T116/T119 交叉门禁：branch、new-season、rating-history 与两个观战新参数的裸参数/完整配置双顺序共 24 次拒绝；已有档案字节、mtime、文件树及子进程目录不变')

}
catch {
    $failure = $_
}
finally {
    foreach ($child in $children) {
        try {
            if ($child.Started -and -not $child.Process.HasExited) {
                $child.Process.Kill($true)
                Assert-Check ($child.Process.WaitForExit(5000)) "$($child.Name) 清理时未退出。"
            }
            if ($null -ne $child.OutTask) { Assert-Check ($child.OutTask.Wait(5000)) "$($child.Name) 标准输出未排空。" }
            if ($null -ne $child.ErrTask) { Assert-Check ($child.ErrTask.Wait(5000)) "$($child.Name) 错误输出未排空。" }
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
    Status = $status; Checks = $checks.ToArray(); Processes = $children.Count; FinalHashes = $hashes.ToArray()
    Failure = $(if ($null -eq $failure) { $null } else { $failure.Exception.Message }); CleanupFailures = $cleanupFailures.ToArray()
}
[IO.File]::WriteAllText((Join-Path $runDirectory 'report.json'), ($report | ConvertTo-Json -Depth 5), $utf8)
if ($null -ne $failure) { throw $failure }
Assert-Check ($cleanupFailures.Count -eq 0) ('子进程清理失败：' + ($cleanupFailures -join '；'))
Write-Output "PASS：$($checks.Count) 组观众聊天与 CLI 检查，$($children.Count) 个进程；证据：$runDirectory"
