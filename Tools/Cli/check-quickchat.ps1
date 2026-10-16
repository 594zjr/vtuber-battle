#requires -Version 7.0
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [ValidateSet('Both', 'Clean', 'Error')]
    [string]$Mode = 'Both',
    [ValidateRange(10, 120)]
    [int]$TimeoutSeconds = 60
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$repositoryPrefix = $repository.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$current = [IO.Path]::GetFullPath((Get-Location).Path)
if ($current -ne $repository -and -not $current.StartsWith($repositoryPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw '请在本仓库内运行此脚本。'
}
$binaryDirectory = Join-Path $repository "Tools/Cli/bin/$Configuration/net8.0"
$cliDll = Join-Path $binaryDirectory 'Cli.dll'
$coreDll = Join-Path $binaryDirectory 'StreamDuel.Core.dll'
foreach ($path in @($cliDll, $coreDll)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "请先运行 dotnet build Tools/Cli -c $Configuration。"
    }
}
$runDirectory = [IO.Path]::GetFullPath((Join-Path $repository ('Tools/Cli/obj/quickchat-completion/' + [Guid]::NewGuid().ToString('N'))))
if (-not $runDirectory.StartsWith($repositoryPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw '验收产物必须位于本仓库内。'
}
[void][IO.Directory]::CreateDirectory($runDirectory)
$utf8 = [Text.UTF8Encoding]::new($false)
$results = [Collections.Generic.List[object]]::new()
$cleanupFailures = [Collections.Generic.List[string]]::new()
$failure = $null
$hostTeam = [string[]]@('bell', 'enka', 'shiori', 'lumi', 'hane', 'momo')
$guestTeam = [string[]]@('kei', 'yuan', 'nyako', 'buck', 'kasumi', 'bee')
$chatCount = 257
$sentinel = 'T62 验收：排空聊天后收到终局错误'

function Assert-Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "$Message 证据目录：$runDirectory" }
}

function Assert-Healthy($Session) {
    Assert-Check ($Session.State -ne [StreamDuel.Core.NetState]::Error -and
        $Session.State -ne [StreamDuel.Core.NetState]::Desync) "真实联网对局提前失败：$($Session.Error)"
}

function Invoke-CompletionCase([bool]$WithError) {
    $name = if ($WithError) { 'error-after-chat' } else { 'clean-chat-eof' }
    $caseDirectory = Join-Path $runDirectory $name
    [void][IO.Directory]::CreateDirectory($caseDirectory)
    $saveDirectory = Join-Path $caseDirectory 'saves'
    $reportPath = Join-Path $caseDirectory 'battle.md'
    $evidence = [ordered]@{
        mode = $name; status = 'RUNNING'; port = $null; chat_messages_sent = 0
        peer_closed_before_finish = $false; chats_before_finish = $null; chats_after_finish = $null
        state_before_finish = $null; state_after_finish = $null; final_hash = $null
        finish_returned = $false; save_invoked = $false; save_exit = $null
        completion_error = $null; failure = $null; elapsed_ms = $null
    }
    $results.Add($evidence)
    $listener = $null
    $hostTcp = $null
    $guestTcp = $null
    $stdout = [IO.StringWriter]::new([Globalization.CultureInfo]::InvariantCulture)
    $stderr = [IO.StringWriter]::new([Globalization.CultureInfo]::InvariantCulture)
    $elapsed = [Diagnostics.Stopwatch]::StartNew()
    try {
        $port = 0
        $listener = [StreamDuel.Core.TcpTransport]::Listen(0, [ref]$port)
        $evidence.port = $port
        $guestTcp = [StreamDuel.Core.TcpTransport]::Connect('127.0.0.1', $port, 5000)
        $hostTcp = $listener.Accept(5000)
        Assert-Check ($null -ne $hostTcp) '真实 TCP 连接未完成。'
        $listener.Dispose()
        $listener = $null
        $hostContribution = [byte[]](0..31)
        $guestContribution = [byte[]](32..63)
        $sha = [Security.Cryptography.SHA256]::Create()
        try { $seedDigest = $sha.ComputeHash([byte[]]($hostContribution + $guestContribution)) }
        finally { $sha.Dispose() }
        $expectedSeed = [uint32]($seedDigest[0] + 256L * $seedDigest[1] + 65536L * $seedDigest[2] + 16777216L * $seedDigest[3])
        $hostSession = [StreamDuel.Core.NetSession]::new($db, $hostTcp, $true, $hostTeam, '房主', [uint32]7, $null, $null, $true, [StreamDuel.Core.PlayerRating]::new(), $true, $hostContribution)
        $guestSession = [StreamDuel.Core.NetSession]::new($db, $guestTcp, $false, $guestTeam, '客机', $null, $null, $null, $true, [StreamDuel.Core.PlayerRating]::new(), $true, $guestContribution)
        $sessionElapsed = [Diagnostics.Stopwatch]::StartNew()

        while ($hostSession.State -ne [StreamDuel.Core.NetState]::Over -or
            $guestSession.State -ne [StreamDuel.Core.NetState]::Over) {
            Assert-Check ($elapsed.Elapsed.TotalSeconds -lt $TimeoutSeconds) '真实联网对局未在期限内完成。'
            $seconds = $sessionElapsed.Elapsed.TotalSeconds
            $sessionElapsed.Restart()
            foreach ($session in @($hostSession, $guestSession)) {
                $timerRevision = $session.TimerRevision
                $session.Poll()
                # 新开的承诺/公开窗口不能承担上一窗口耗时。
                $session.Tick($(if ($timerRevision -eq $session.TimerRevision) { $seconds } else { 0 }))
                Assert-Healthy $session
                if ($session.State -eq [StreamDuel.Core.NetState]::Choose -and -not $session.HasSubmitted) {
                    $session.Submit([StreamDuel.Core.Ai]::Choose($session.Battle, $session.MySide, [StreamDuel.Core.AiLevel]::Normal))
                }
                elseif ($session.State -eq [StreamDuel.Core.NetState]::Replace -and -not $session.HasSubmitted) {
                    $session.SubmitReplace([StreamDuel.Core.Ai]::ChooseReplacement($session.Battle, $session.MySide, [StreamDuel.Core.AiLevel]::Normal))
                }
            }
            [Threading.Thread]::Sleep(1)
        }
        Assert-Check ($hostSession.Battle.Phase -eq [StreamDuel.Core.Phase]::Over -and
            $guestSession.Battle.Phase -eq [StreamDuel.Core.Phase]::Over) '会话未完成真实战斗。'
        Assert-Check ($hostSession.Battle.Seed -eq $expectedSeed -and $guestSession.Battle.Seed -eq $expectedSeed -and
            $hostSession.Battle.Record.Seed -eq $expectedSeed -and $guestSession.Battle.Record.Seed -eq $expectedSeed) '真实 TCP 会话或回放没有使用双方固定贡献按小端公式算出的公平种子。'
        $hashBefore = $hostSession.Battle.StateHash()
        $recordBefore = $hostSession.Battle.Record.ToJson()
        $rngBefore = $hostSession.Battle.Clone().Random()
        Assert-Check ($hashBefore -eq $guestSession.Battle.StateHash() -and
            $recordBefore -ceq $guestSession.Battle.Record.ToJson()) '双方终局哈希或完整回放不一致。'
        $evidence.final_hash = $hashBefore.ToString('x16', [Globalization.CultureInfo]::InvariantCulture)
        $evidence.state_before_finish = $hostSession.State.ToString()

        # 绕过发送频率限制以验证接收上限；仍由真实 Send 封装重连序号，不复制线上 packet 协议。
        for ($index = 0; $index -lt $chatCount; $index++) {
            [void]$sendMethod.Invoke($guestSession, [object[]]@('{"type":"chat","index":' + ($index % 8) + '}'))
            $evidence.chat_messages_sent++
        }
        if ($WithError) { [void]$sendMethod.Invoke($guestSession, [object[]]@('{"type":"error","message":"' + $sentinel + '"}')) }
        $guestSession.PrepareClose()
        $guestTcp.CompleteSending()
        $eofWait = [Diagnostics.Stopwatch]::StartNew()
        # 此处故意不调用 Poll/TryReceive。PeerClosed 说明后台已排入最后一行及其后的 EOF。
        while (-not $hostTcp.PeerClosed -and $eofWait.ElapsedMilliseconds -lt 10000) {
            [Threading.Thread]::Sleep(1)
        }
        Assert-Check $hostTcp.PeerClosed '对端的正常 EOF 未到达。'
        $evidence.peer_closed_before_finish = $true
        $evidence.chats_before_finish = $hostSession.ReceivedChats.Count
        Assert-Check ($evidence.chats_before_finish -eq 0) '收尾验证前意外消费了聊天积压。'

        $arguments = [string[]]@('--host', '0', '--auto', '--team', ($hostTeam -join ','), '--seed', '7',
            '--share', '--save-dir', $saveDirectory, '--report', $reportPath)
        $parseArguments = [object[]]::new(1)
        $parseArguments[0] = $arguments
        $options = $parseMethod.Invoke($null, $parseArguments)
        # 只略过已完成的建连构造；复用真实会话与传输，收尾仍执行当前 CLI 的 Poll/FinishNetwork。
        $network = [Runtime.CompilerServices.RuntimeHelpers]::GetUninitializedObject($networkType)
        foreach ($entry in @(
            @{ Name = 'options'; Value = $options },
            @{ Name = 'clock'; Value = [Diagnostics.Stopwatch]::StartNew() },
            @{ Name = '<Session>k__BackingField'; Value = $hostSession },
            @{ Name = '<Connection>k__BackingField'; Value = $hostTcp }
        )) {
            $field = $networkType.GetField($entry.Name, $instanceFlags)
            Assert-Check ($null -ne $field) ('当前 CLI 连接适配字段缺失：' + $entry.Name)
            $field.SetValue($network, $entry.Value)
        }
        $previousOut = [Console]::Out
        $previousError = [Console]::Error
        try {
            [Console]::SetOut($stdout)
            [Console]::SetError($stderr)
            try {
                # 调用构建产物中的原 CLI 方法，避免复制或模拟收尾及保存实现。
                [void]$finishMethod.Invoke($null, [object[]]@($network, $null))
                $evidence.finish_returned = $true
                $evidence.save_invoked = $true
                $evidence.save_exit = $saveMethod.Invoke($null, [object[]]@($hostSession.Battle, $options, 0, $hostSession.OpponentRating, $true))
            }
            catch { $evidence.completion_error = $_.Exception.GetBaseException().Message }
        }
        finally {
            [Console]::SetOut($previousOut)
            [Console]::SetError($previousError)
        }
        $evidence.state_after_finish = $hostSession.State.ToString()
        $evidence.chats_after_finish = $hostSession.ReceivedChats.Count
        Assert-Check ($evidence.chats_after_finish -eq 6) '聊天积压没有遵守每回合六条接收上限。'
        Assert-Check ($hostSession.Battle.StateHash() -eq $hashBefore -and
            $hostSession.Battle.Record.ToJson() -ceq $recordBefore -and
            $hostSession.Battle.Clone().Random() -eq $rngBefore) '聊天或收尾改变了终局、回放或随机流。'

        if ($WithError) {
            Assert-Check ($null -ne $evidence.completion_error -and $evidence.completion_error.Contains($sentinel)) '聊天后排队的哨兵错误未阻止收尾。'
            Assert-Check ($hostSession.State -eq [StreamDuel.Core.NetState]::Error -and
                -not $evidence.finish_returned -and -not $evidence.save_invoked) '错误终局仍进入了保存流程。'
            Assert-Check (-not (Test-Path -LiteralPath $saveDirectory) -and
                -not (Test-Path -LiteralPath $reportPath)) '错误终局产生了存档或战报。'
            Assert-Check (-not $stdout.ToString().Contains('分享码：') -and
                -not $stdout.ToString().Contains('对局结束：')) '错误终局仍分享或宣布保存完成。'
        }
        else {
            Assert-Check ($null -eq $evidence.completion_error -and $evidence.finish_returned -and
                $evidence.save_invoked -and $evidence.save_exit -eq 0 -and
                $hostSession.State -eq [StreamDuel.Core.NetState]::Over) '正常聊天及 EOF 未成功完成收尾和保存。'
            Assert-Check ($stderr.ToString().Length -eq 0) '正常收尾产生了错误输出。'
            $profile = [StreamDuel.Core.Profile]::FromJson([IO.File]::ReadAllText((Join-Path $saveDirectory 'profile.json'), $utf8))
            Assert-Check ($profile.Packs.Count -eq 1 -and $profile.Packs.ContainsKey('original')) '正常终局档案内容包不正确。'
            Assert-Check ($profile.Ratings.Count -eq 1 -and $profile.Ratings.ContainsKey([StreamDuel.Core.Profile]::DefaultRatingFormat) -and
                $profile.Ratings[[StreamDuel.Core.Profile]::DefaultRatingFormat].Games -eq 1) '正常终局未恰好累计一次默认赛制排位。'
            $stats = $profile.Packs['original']
            Assert-Check (($stats.Wins + $stats.Losses + $stats.Draws) -eq 1) '正常终局未恰好累计一次。'
            $replays = @(Get-ChildItem -LiteralPath (Join-Path $saveDirectory 'replays') -File |
                Where-Object Name -Match '^[0-9]{8}_[0-9]{6}_[0-9]{7}_[A-Za-z0-9_-]+\.json$')
            Assert-Check ($replays.Count -eq 1) '正常终局未恰好保存一份回放。'
            Assert-Check ([IO.File]::ReadAllText($replays[0].FullName, $utf8) -ceq $recordBefore) '保存的回放与真实联网终局不一致。'
            Assert-Check (Test-Path -LiteralPath $reportPath -PathType Leaf) '正常终局没有导出战报。'
            $shares = [regex]::Matches($stdout.ToString(), '(?m)^分享码：(SD1-[A-Za-z0-9_-]+)\r?$')
            Assert-Check ($shares.Count -eq 1 -and
                [StreamDuel.Core.ShareCode]::Decode($shares[0].Groups[1].Value).ToJson() -ceq $recordBefore) '正常终局分享码未复现完整记录。'
        }
        $evidence.status = 'PASS'
    }
    catch {
        $evidence.status = 'FAIL'
        $evidence.failure = $_.Exception.GetBaseException().Message
        throw
    }
    finally {
        $evidence.elapsed_ms = $elapsed.ElapsedMilliseconds
        [IO.File]::WriteAllText((Join-Path $caseDirectory 'stdout.log'), $stdout.ToString(), $utf8)
        [IO.File]::WriteAllText((Join-Path $caseDirectory 'stderr.log'), $stderr.ToString(), $utf8)
        foreach ($resource in @($hostTcp, $guestTcp, $listener, $stdout, $stderr)) {
            if ($null -ne $resource) {
                try { $resource.Dispose() }
                catch { $cleanupFailures.Add($name + ': ' + $_.Exception.GetBaseException().Message) }
            }
        }
        [IO.File]::WriteAllText((Join-Path $caseDirectory 'result.json'), ($evidence | ConvertTo-Json -Depth 6), $utf8)
    }
}

try {
    $coreAssembly = [Reflection.Assembly]::LoadFrom($coreDll)
    $cliAssembly = [Reflection.Assembly]::LoadFrom($cliDll)
    Assert-Check ([IO.Path]::GetFullPath($coreAssembly.Location) -eq [IO.Path]::GetFullPath($coreDll) -and
        [IO.Path]::GetFullPath($cliAssembly.Location) -eq [IO.Path]::GetFullPath($cliDll)) '当前 PowerShell 已加载其他构建，请在新进程运行。'
    Assert-Check ([StreamDuel.Core.NetSession]::ProtocolVersion -eq 6) '当前构建尚未包含 T87 协议 6，请先构建整合后的 CLI。'
    $programType = $cliAssembly.GetType('StreamDuel.Cli.Program', $true)
    $optionsType = $cliAssembly.GetType('StreamDuel.Cli.CliOptions', $true)
    $methodFlags = [Reflection.BindingFlags]'Static, Public, NonPublic'
    $instanceFlags = [Reflection.BindingFlags]'Instance, Public, NonPublic'
    $networkType = $programType.GetNestedType('NetworkConnection', [Reflection.BindingFlags]::NonPublic)
    $sendMethod = [StreamDuel.Core.NetSession].GetMethod('Send', $instanceFlags)
    Assert-Check ($null -ne $networkType -and $null -ne $sendMethod) '当前 CLI 或 Core 缺少真实连接及发送入口。'
    $finishMethod = $programType.GetMethod('FinishNetwork', $methodFlags)
    $saveMethod = $programType.GetMethod('SaveCompletedBattle', $methodFlags)
    $parseMethod = $optionsType.GetMethod('Parse', $methodFlags)
    Assert-Check ($null -ne $finishMethod -and $null -ne $saveMethod -and $null -ne $parseMethod) '当前 CLI 缺少原收尾、保存或参数入口。'
    $db = [StreamDuel.Core.ContentDb]::FromJson(
        [IO.File]::ReadAllText((Join-Path $binaryDirectory 'Content/StreamDuelRules.json'), $utf8),
        [IO.File]::ReadAllText((Join-Path $binaryDirectory 'Content/Packs/original.json'), $utf8))
    if ($Mode -eq 'Both' -or $Mode -eq 'Clean') { Invoke-CompletionCase $false }
    if ($Mode -eq 'Both' -or $Mode -eq 'Error') { Invoke-CompletionCase $true }
}
catch { $failure = $_.Exception.GetBaseException().Message }
finally {
    if ($cleanupFailures.Count -gt 0 -and $null -eq $failure) { $failure = 'TCP 或日志资源清理失败。' }
    $summary = [ordered]@{
        status = $(if ($null -eq $failure) { 'PASS' } else { 'FAIL' })
        mode = $Mode; failure = $failure; directory = $runDirectory
        core = $coreDll; cli = $cliDll; cases = $results.ToArray(); cleanup_failures = $cleanupFailures.ToArray()
    }
    [IO.File]::WriteAllText((Join-Path $runDirectory 'result.json'), ($summary | ConvertTo-Json -Depth 8), $utf8)
}
if ($null -ne $failure) { throw "$failure 证据目录：$runDirectory" }
Write-Output "PASS：$($results.Count) 项真实 TCP 快捷语积压收尾检查；证据目录：$runDirectory"
