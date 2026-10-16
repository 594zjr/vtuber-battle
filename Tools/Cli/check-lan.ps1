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
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "请先运行 dotnet build Tools/Cli -c $Configuration。"
    }
}
$runDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ('obj/lan-verification/' + [Guid]::NewGuid().ToString('N'))))
if (-not $runDirectory.StartsWith($repositoryPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw '验收产物必须位于本仓库内。'
}
[void][IO.Directory]::CreateDirectory($runDirectory)
$utf8 = [Text.UTF8Encoding]::new($false)
$strictUtf8 = [Text.UTF8Encoding]::new($false, $true)
$children = [Collections.Generic.List[object]]::new()
$checks = [Collections.Generic.List[string]]::new()
$sockets = [Collections.Generic.List[IDisposable]]::new()
$broadcastPackets = [Collections.Generic.List[object]]::new()
$broadcast = [ordered]@{ status = 'NOT_RUN'; packet_count = 0; content_checked = $false; cadence_ms = @(); stopped_after_connect = 'NOT_RUN'; error = $null }
$endToEnd = [ordered]@{ status = 'NOT_RUN'; reason = $null; endpoint = $null; final_hash = $null; seed = $null }
$scanMilliseconds = $null
$failure = $null
$lanPort = 47777
$team = 'bell,enka,shiori,lumi,hane,momo'

function Assert-Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "$Message 证据目录：$runDirectory" }
}

function Fair-Seed([string]$Output) {
    $matches = [regex]::Matches($Output, '(?m)^连接成功；本方：[^\r\n]+；公平种子：([0-9]+)\r?$')
    Assert-Check ($matches.Count -eq 1) '每端应恰好输出一次协商后的公平种子。'
    return [uint32]::Parse($matches[0].Groups[1].Value, [Globalization.CultureInfo]::InvariantCulture)
}

function Read-Log([string]$Path) {
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
    $reader = [IO.StreamReader]::new($stream, $utf8)
    try { return $reader.ReadToEnd() } finally { $reader.Dispose() }
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
    $start.StandardOutputEncoding = $utf8
    $start.StandardErrorEncoding = $utf8
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    $child = [pscustomobject]@{
        Name = $Name; Parameters = $Parameters; WorkingDirectory = $workingDirectory
        Process = $process; Started = $false; Killed = $false; CleanupError = $null
        OutPath = (Join-Path $workingDirectory 'stdout.log'); ErrPath = (Join-Path $workingDirectory 'stderr.log')
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

function Wait-Cli($Child, [int]$ExpectedExit, [int]$TimeoutMs = 15000) {
    if ($null -eq $Child.Result) {
        $timer = [Diagnostics.Stopwatch]::StartNew()
        while (-not $Child.Process.HasExited -and $timer.ElapsedMilliseconds -lt $TimeoutMs) {
            Start-Sleep -Milliseconds 25
        }
        Assert-Check ($Child.Process.HasExited) "$($Child.Name) 未按时退出。"
        Assert-Check ($Child.OutTask.Wait(5000)) "$($Child.Name) 标准输出未结束。"
        Assert-Check ($Child.ErrTask.Wait(5000)) "$($Child.Name) 标准错误未结束。"
        $Child.OutStream.Flush()
        $Child.ErrStream.Flush()
        $Child.Result = [pscustomobject]@{
            Out = (Read-Log $Child.OutPath); Err = (Read-Log $Child.ErrPath); Exit = $Child.Process.ExitCode
        }
    }
    Assert-Check ($Child.Result.Exit -eq $ExpectedExit) "$($Child.Name) 退出码应为 $ExpectedExit，实际为 $($Child.Result.Exit)。"
    return $Child.Result
}

function Assert-NoSave($Child) {
    Assert-Check (-not (Test-Path -LiteralPath (Join-Path $Child.WorkingDirectory 'saves'))) "$($Child.Name) 发现或失败操作误建了存档目录。"
}

function Bind-DiscoveryPort {
    $udp = [Net.Sockets.UdpClient]::new([Net.Sockets.AddressFamily]::InterNetwork)
    $sockets.Add($udp)
    $udp.Client.ExclusiveAddressUse = $true
    $udp.Client.Bind([Net.IPEndPoint]::new([Net.IPAddress]::Any, $lanPort))
    return $udp
}

function Send-LocalPacket($Sender, [string]$Text) {
    $bytes = $utf8.GetBytes($Text)
    [void]$Sender.Send($bytes, $bytes.Length, [Net.IPEndPoint]::new([Net.IPAddress]::Loopback, $lanPort))
}

function Room-Packet([string]$Name, [int]$Port, [int]$Protocol = $protocolVersion) {
    return ([ordered]@{
        magic = 'SD-LAN-1'; name = $Name; port = $Port; protocol = $Protocol
        contentHash = 'local-injection-fingerprint'; ruleset = 'casual'; address = '198.51.100.77'
    } | ConvertTo-Json -Compress)
}

try {
    $assembly = [Reflection.Assembly]::LoadFrom($coreDll)
    Assert-Check ([IO.Path]::GetFullPath($assembly.Location) -eq [IO.Path]::GetFullPath($coreDll)) '请在未加载其他 Core 的新 PowerShell 进程运行。'
    $protocolVersion = [StreamDuel.Core.NetSession]::TerminationProtocolVersion
    $invalid = @(
        @{ Name = 'missing-name'; Args = @('--lan-host') },
        @{ Name = 'empty-name'; Args = @('--lan-host', '') },
        @{ Name = 'blank-name'; Args = @('--lan-host', '   ') },
        @{ Name = 'control-name'; Args = @('--lan-host', "房`n间") },
        @{ Name = 'duplicate-name'; Args = @('--lan-host', '甲', '--lan-host', '乙') },
        @{ Name = 'duplicate-list'; Args = @('--lan-list', '--lan-list') },
        @{ Name = 'host-join'; Args = @('--lan-host', '甲', '--join', '127.0.0.1:1') },
        @{ Name = 'host-ban'; Args = @('--lan-host', '甲', '--ban') },
        @{ Name = 'host-lead'; Args = @('--lan-host', '甲', '--lead') },
        @{ Name = 'host-replay'; Args = @('--lan-host', '甲', '--replay', 'missing.json') },
        @{ Name = 'host-replay-code'; Args = @('--lan-host', '甲', '--replay-code', 'SD1-invalid') },
        @{ Name = 'host-packs'; Args = @('--lan-host', '甲', '--list-packs') }
    )
    $listConflicts = @(
        @('--host', '0'), @('--join', '127.0.0.1:1'), @('--lan-host', '甲'), @('--list-packs'),
        @('--replay', 'missing.json'), @('--ban'), @('--lead'), @('--auto'), @('--hash'),
        @('--pack', 'original'), @('--pack-dir', '.'), @('--level', 'normal'), @('--seed', '7'),
        @('--team', $team), @('--save-dir', 'unexpected-saves'), @('--report', 'unexpected-report.md'),
        @('--timer', '1,2'), @('--team-code', 'SDT1-invalid'), @('--export-team'), @('--share'),
        @('--replay-code', 'SD1-invalid')
    )
    for ($index = 0; $index -lt $listConflicts.Count; $index++) {
        $invalid += @{ Name = "list-conflict-$index"; Args = (@('--lan-list') + $listConflicts[$index]) }
    }
    foreach ($case in $invalid) {
        $child = Start-Cli $case.Name $case.Args
        $child.Process.StandardInput.Close()
        $result = Wait-Cli $child 1
        Assert-Check ($result.Err -match '错误：.*(lan-|联网)') "$($case.Name) 缺少中文参数错误。"
        Assert-Check (-not $result.Out.Contains('监听端口：') -and -not $result.Out.Contains('正在搜索局域网')) '非法参数启动了网络操作。'
        Assert-NoSave $child
        Assert-Check (-not (Test-Path -LiteralPath (Join-Path $child.WorkingDirectory 'unexpected-report.md'))) '非法发现参数产生战报。'
        Assert-Check (-not (Test-Path -LiteralPath (Join-Path $child.WorkingDirectory 'unexpected-saves'))) '非法发现参数产生指定存档目录。'
    }
    $checks.Add('新参数缺值、空白、控制字符、重复及不兼容组合在网络操作前拒绝')

    $child = Start-Cli 'help' @('--lan-list', '--help')
    $result = Wait-Cli $child 0
    Assert-Check ($result.Out.Contains('--lan-host') -and $result.Out.Contains('--lan-list')) '帮助未说明两个发现参数。'
    Assert-NoSave $child
    $checks.Add('发现参数帮助可直接退出且无存档')

    # 固定发现端口是协议的一部分；独占占用仅限本脚本持有的本机套接字。
    $blocker = Bind-DiscoveryPort
    $child = Start-Cli 'occupied-discovery-port' @('--lan-list')
    $result = Wait-Cli $child 1
    Assert-Check ($result.Err -match '错误：.*47777' -and $result.Err -match '占用|监听|端口') 'UDP 端口占用没有给出带端口号的中文错误。'
    Assert-Check ($result.Err -notmatch 'Unhandled exception|\bat StreamDuel\.') '端口占用泄漏了未处理异常堆栈。'
    Assert-NoSave $child
    $blocker.Dispose()
    $checks.Add('UDP 47777 占用时中文报错，释放后可继续发现')

    # 注入只发往回环地址，确保广播被防火墙隔离时仍可验证完整 CLI 接收路径。
    $sender = [Net.Sockets.UdpClient]::new([Net.IPEndPoint]::new([Net.IPAddress]::Loopback, 0))
    $sockets.Add($sender)
    $child = Start-Cli 'injected-list' @('--lan-list')
    [void](Wait-Output $child '正在搜索局域网房间（5 秒）')
    $scan = [Diagnostics.Stopwatch]::StartNew()
    $noiseSent = $false
    while (-not $child.Process.HasExited -and $scan.ElapsedMilliseconds -lt 12000) {
        $milliseconds = $scan.ElapsedMilliseconds
        if ($milliseconds -lt 800) { Send-LocalPacket $sender (Room-Packet '早期房间' 45671) }
        $activeName = if ($milliseconds -lt 400) { '旧房间名称' } else { '持续房间' }
        Send-LocalPacket $sender (Room-Packet $activeName 45672)
        Send-LocalPacket $sender (Room-Packet '旧协议房间' 45675 2)
        Send-LocalPacket $sender '{"magic":"OTHER","name":"坏包不得出现","port":45673,"protocol":2,"contentHash":"x","ruleset":"casual"}'
        Send-LocalPacket $sender (Room-Packet ('x' * 600) 45674)
        if (-not $noiseSent) {
            [void]$sender.Send([byte[]]@(0xc3, 0x28), 2, [Net.IPEndPoint]::new([Net.IPAddress]::Loopback, $lanPort))
            [void]$sender.Send([byte[]]@(), 0, [Net.IPEndPoint]::new([Net.IPAddress]::Loopback, $lanPort))
            $noiseSent = $true
        }
        Start-Sleep -Milliseconds 100
    }
    $scanMilliseconds = $scan.ElapsedMilliseconds
    $result = Wait-Cli $child 0
    Assert-Check ($scanMilliseconds -ge 4500 -and $scanMilliseconds -lt 12000) '发现没有执行约 5 秒的扫描窗口。'
    Assert-Check ($result.Out.Contains('发现 3 个房间')) '本机注入房间数错误，可能重复、丢失过期记录或接收了垃圾包。'
    $blocks = @([regex]::Matches($result.Out, '(?ms)^房间：(?<name>[^\r\n]+)\r?\n(?<body>.*?)(?=^房间：|\z)'))
    Assert-Check ($blocks.Count -eq 3) '房间没有按端点去重。'
    $early = @($blocks | Where-Object { $_.Groups['name'].Value -eq '早期房间' })
    $active = @($blocks | Where-Object { $_.Groups['name'].Value -eq '持续房间' })
    $legacy = @($blocks | Where-Object { $_.Groups['name'].Value -eq '旧协议房间' })
    Assert-Check ($early.Count -eq 1 -and $active.Count -eq 1) '早期房间被漏掉，或同端点新房名没有覆盖旧值。'
    Assert-Check ($early[0].Groups['body'].Value.Contains('状态：已失联') -and -not $early[0].Groups['body'].Value.Contains('加入：')) '已失联条目仍提供加入命令或缺失状态说明。'
    Assert-Check ($active[0].Groups['body'].Value.Contains('状态：可见') -and $active[0].Groups['body'].Value.Contains('加入：--join 127.0.0.1:45672')) '持续房间没有显示来源地址和可复制的加入命令。'
    Assert-Check ($result.Out.Contains(('地址：127.0.0.1:45671；协议：{0}；规则：casual；内容指纹：local-injection-fingerprint' -f $protocolVersion))) '发现列表遗漏广播字段或没有使用 UDP 来源地址。'
    Assert-Check ($legacy.Count -eq 1) '仍在广播的旧协议房间未显示或出现重复。'
    $legacyBody = $legacy[0].Groups['body'].Value
    Assert-Check ($legacyBody.Contains('地址：127.0.0.1:45675；协议：2；') -and
        $legacyBody.Contains(('状态：协议版本不兼容，当前版本为 {0}。' -f $protocolVersion)) -and
        -not $legacyBody.Contains('加入：')) '旧协议房间未标为不兼容，或错误地提供了加入命令。'
    Assert-Check (-not $result.Out.Contains('198.51.100.77') -and -not $result.Out.Contains('坏包不得出现') -and -not $result.Out.Contains('旧房间名称')) '发现列表信任了伪造来源、接收垃圾包或保留旧同端点值。'
    Assert-NoSave $child
    $sender.Dispose()
    $released = Bind-DiscoveryPort
    $released.Dispose()
    $checks.Add('5 秒真实 CLI 接收：端点去重更新、来源地址、过期标记、旧协议不可加入、垃圾包丢弃及退出后 UDP 端口释放')

    $rules = [IO.File]::ReadAllText((Join-Path $binaryDirectory 'Content/StreamDuelRules.json'), $utf8)
    $pack = [IO.File]::ReadAllText((Join-Path $binaryDirectory 'Content/Packs/original.json'), $utf8)
    $db = [StreamDuel.Core.ContentDb]::FromJson($rules, $pack)
    $expectedHash = $db.ContentHash()
    $observer = Bind-DiscoveryPort
    $longName = '房' * 40
    $hostChild = Start-Cli 'broadcast-host' @('--lan-host', $longName, '--host', '0', '--team', $team, '--seed', '7')
    $tcpPort = [int](Wait-Output $hostChild '(?m)^监听端口：([0-9]+)\r?$').Groups[1].Value
    Assert-Check ($tcpPort -ge 1 -and $tcpPort -le 65535) '开房没有公开实际 TCP 端口。'
    $capture = [Diagnostics.Stopwatch]::StartNew()
    while ($capture.ElapsedMilliseconds -lt 3600) {
        Assert-Check (-not $hostChild.Process.HasExited) '广播房主在等待连接时提前退出。'
        while ($observer.Available -gt 0) {
            $remote = [Net.IPEndPoint]::new([Net.IPAddress]::Any, 0)
            $bytes = $observer.Receive([ref]$remote)
            try { $text = $strictUtf8.GetString($bytes); $packet = $text | ConvertFrom-Json -AsHashtable }
            catch { continue }
            if ($packet['magic'] -ne 'SD-LAN-1' -or $packet['port'] -ne $tcpPort) { continue }
            Assert-Check ($bytes.Length -le 512) '房主广播超过 512 字节。'
            Assert-Check ($packet['name'] -ceq ('房' * 24)) '广播房名未正确截断到 24 字符。'
            Assert-Check ($packet['protocol'] -eq [StreamDuel.Core.NetSession]::TerminationProtocolVersion) '广播协议版本与会话不一致。'
            Assert-Check ($packet['contentHash'] -ceq $expectedHash -and $packet['ruleset'] -ceq 'casual') '广播内容指纹或规则集不正确。'
            $broadcastPackets.Add([pscustomobject]@{
                elapsed_ms = $capture.ElapsedMilliseconds; sender = $remote.Address.ToString(); sender_port = $remote.Port
                bytes = $bytes.Length; json = $text
            })
        }
        Start-Sleep -Milliseconds 25
    }
    $broadcast.packet_count = $broadcastPackets.Count
    $broadcast.content_checked = $broadcastPackets.Count -gt 0
    if ($broadcastPackets.Count -eq 0) {
        $broadcast.status = 'UNOBSERVED'
        $broadcast.error = '本机观察者未收到实际广播；可能被主机网络环境隔离。回环注入通过不等于真实广播可达。'
    }
    else {
        $broadcast.status = if ($broadcastPackets.Count -ge 2) { 'OBSERVED' } else { 'OBSERVED_SINGLE_PACKET' }
        $intervals = [Collections.Generic.List[long]]::new()
        for ($index = 1; $index -lt $broadcastPackets.Count; $index++) {
            $intervals.Add($broadcastPackets[$index].elapsed_ms - $broadcastPackets[$index - 1].elapsed_ms)
        }
        $broadcast.cadence_ms = $intervals.ToArray()
        Assert-Check ($broadcastPackets.Count -le 6) '3.6 秒内广播过多，疑似按轮询频率或补发积压数据。'
        if ($broadcastPackets.Count -ge 3) {
            foreach ($interval in $intervals) {
                Assert-Check ($interval -ge 250 -and $interval -le 2500) '实际广播间隔明显偏离每秒一次，或出现短时突发。'
            }
        }
    }

    # 连接后先保持 TCP 打开，验证等待中的广播已停止，再沿握手错误路径退出。
    $peer = [Net.Sockets.TcpClient]::new([Net.Sockets.AddressFamily]::InterNetwork)
    $sockets.Add($peer)
    $peer.Connect([Net.IPAddress]::Loopback, $tcpPort)
    [void](Wait-Output $hostChild '等待对手握手')
    while ($observer.Available -gt 0) {
        $remote = [Net.IPEndPoint]::new([Net.IPAddress]::Any, 0)
        [void]$observer.Receive([ref]$remote)
    }
    $quiet = [Diagnostics.Stopwatch]::StartNew()
    while ($quiet.ElapsedMilliseconds -lt 1500) {
        Assert-Check (-not $hostChild.Process.HasExited) '本机连接尚未关闭，房主却退出了握手。'
        while ($observer.Available -gt 0) {
            $remote = [Net.IPEndPoint]::new([Net.IPAddress]::Any, 0)
            $bytes = $observer.Receive([ref]$remote)
            try { $packet = $strictUtf8.GetString($bytes) | ConvertFrom-Json -AsHashtable }
            catch { continue }
            Assert-Check ($packet['magic'] -ne 'SD-LAN-1' -or $packet['port'] -ne $tcpPort) 'TCP 连接已被接受，房间仍继续广播。'
        }
        Start-Sleep -Milliseconds 25
    }
    $broadcast.stopped_after_connect = if ($broadcastPackets.Count -gt 0) { 'PASS' } else { 'UNOBSERVED' }
    $observer.Dispose()
    $peer.Dispose()
    $hostResult = Wait-Cli $hostChild 1
    Assert-Check ($hostResult.Err.Contains('错误：')) '本机握手对端关闭时没有正常返回中文错误。'
    Assert-NoSave $hostChild
    $tcpRebind = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Any, $tcpPort)
    try { $tcpRebind.Start() } finally { $tcpRebind.Stop() }
    foreach ($sourcePort in @($broadcastPackets | ForEach-Object { $_.sender_port } | Sort-Object -Unique)) {
        $udpRebind = [Net.Sockets.UdpClient]::new([Net.Sockets.AddressFamily]::InterNetwork)
        try {
            $udpRebind.Client.ExclusiveAddressUse = $true
            $udpRebind.Client.Bind([Net.IPEndPoint]::new([Net.IPAddress]::Any, $sourcePort))
        }
        finally { $udpRebind.Dispose() }
    }
    $released = Bind-DiscoveryPort
    $released.Dispose()
    $checks.Add('真实房主开 TCP 临时端口，正常错误退出后 TCP 与可观测 UDP 源端口均可重绑')

    if ($broadcastPackets.Count -eq 0) {
        $endToEnd.reason = '当前环境未观测到真实广播，无法验证真实发现后加入；不以注入替代此项。'
    }
    else {
        $roomName = '验收房-' + [Guid]::NewGuid().ToString('N').Substring(0, 12)
        $hostChild = Start-Cli 'discovered-game-host' @('--lan-host', $roomName, '--auto', '--team', $team, '--seed', '7')
        $tcpPort = [int](Wait-Output $hostChild '(?m)^监听端口：([0-9]+)\r?$').Groups[1].Value
        Assert-Check ($tcpPort -ge 1 -and $tcpPort -le 65535) '独立 --lan-host 没有自动分配实际端口。'
        $listChild = Start-Cli 'discover-real-host' @('--lan-list')
        $listResult = Wait-Cli $listChild 0
        Assert-NoSave $listChild
        $roomPattern = '(?ms)^房间：' + [regex]::Escape($roomName) + '\r?\n(?<body>.*?)(?=^房间：|\z)'
        $matches = [regex]::Matches($listResult.Out, $roomPattern)
        Assert-Check ($matches.Count -eq 1) '真实房主没有在 5 秒发现结果中出现，或同端点重复出现。'
        $join = [regex]::Match($matches[0].Groups['body'].Value, '(?m)^\s*加入：--join (?<address>[0-9.]+):(?<port>[0-9]+)\r?$')
        Assert-Check ($join.Success) '真实房间没有提供可复制的 IPv4 加入命令。'
        $joinAddress = [Net.IPAddress]::Parse($join.Groups['address'].Value)
        $joinPort = [int]$join.Groups['port'].Value
        Assert-Check ($joinPort -eq $tcpPort) '发现的加入端口与真实房主监听端口不同。'
        $localAddresses = @([Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces() | ForEach-Object {
            $_.GetIPProperties().UnicastAddresses | ForEach-Object { $_.Address.ToString() }
        })
        Assert-Check ([Net.IPAddress]::IsLoopback($joinAddress) -or $localAddresses -contains $joinAddress.ToString()) '发现地址不属于本机，脚本拒绝连接其他设备。'
        $endpoint = $joinAddress.ToString() + ':' + $joinPort
        $guestChild = Start-Cli 'discovered-game-guest' @('--join', $endpoint, '--auto', '--team', 'kei,yuan,nyako,buck,kasumi,bee')
        $hostResult = Wait-Cli $hostChild 0 60000
        $guestResult = Wait-Cli $guestChild 0 60000
        $fairSeed = Fair-Seed $hostResult.Out
        Assert-Check ((Fair-Seed $guestResult.Out) -eq $fairSeed) '通过发现加入的双方协商种子不一致。'
        $hostHash = [regex]::Match($hostResult.Out, '(?m)^最终状态哈希：([0-9a-f]{16})\r?$')
        $guestHash = [regex]::Match($guestResult.Out, '(?m)^最终状态哈希：([0-9a-f]{16})\r?$')
        Assert-Check ($hostHash.Success -and $guestHash.Success -and $hostHash.Groups[1].Value -ceq $guestHash.Groups[1].Value) '通过发现命令加入的对局没有完成一致的终局校验。'
        $discoveredChildren = @($hostChild, $guestChild)
        for ($side = 0; $side -lt 2; $side++) {
            $child = $discoveredChildren[$side]
            $replayDirectory = Join-Path $child.WorkingDirectory 'saves/replays'
            Assert-Check (Test-Path -LiteralPath $replayDirectory -PathType Container) '通过发现加入的对局没有保存回放目录。'
            $replayFiles = @(Get-ChildItem -LiteralPath $replayDirectory -File -Filter '*.json')
            Assert-Check ($replayFiles.Count -eq 1) '通过发现加入的对局应为每端保存一份回放。'
            $record = [StreamDuel.Core.BattleRecord]::FromJson([IO.File]::ReadAllText($replayFiles[0].FullName, $utf8))
            Assert-Check ($record.Seed -eq $fairSeed) '发现对局回放未保存最终公平种子。'
            $replayed = [StreamDuel.Core.Replay]::Run($db, $record)
            Assert-Check ($replayed.Phase -eq [StreamDuel.Core.Phase]::Over -and
                $replayed.StateHash().ToString('x16') -ceq $hostHash.Groups[1].Value) '发现对局的回放无法复现本局终局哈希。'
            $profilePath = Join-Path $child.WorkingDirectory 'saves/profile.json'
            Assert-Check (Test-Path -LiteralPath $profilePath -PathType Leaf) '发现对局没有保存档案。'
            $profile = [StreamDuel.Core.Profile]::FromJson([IO.File]::ReadAllText($profilePath, $utf8))
            Assert-Check ($profile.Packs.Count -eq 1 -and $profile.Packs.ContainsKey('original')) '发现对局档案内容包不正确。'
            $stats = $profile.Packs['original']
            Assert-Check ($stats.Wins -eq [int]($replayed.Winner -eq $side) -and
                $stats.Losses -eq [int]($replayed.Winner -ge 0 -and $replayed.Winner -ne $side) -and
                $stats.Draws -eq [int]($replayed.Winner -eq -1)) '发现对局档案没有按本方和本局胜者记录一次胜负。'
        }
        $endToEnd.status = 'PASS'
        $endToEnd.endpoint = $endpoint
        $endToEnd.final_hash = $hostHash.Groups[1].Value
        $endToEnd.seed = $fairSeed
        $checks.Add('独立 --lan-host 默认临时端口：真实发现、复制加入命令、双方自动完整对战、哈希一致及各一份回放')
    }

    # 此项使用监听端口回环加入，独立验证组合参数，不替代上面的真实发现路径。
    $combinedPreset = [StreamDuel.Core.TeamPreset]::new()
    $combinedPreset.Name = '局域网「零预算」原六人'
    $combinedPreset.PackId = 'original'
    $combinedPreset.Ids.AddRange($team.Split(','))
    $combinedCode = [StreamDuel.Core.TeamCode]::Encode($combinedPreset)
    $combinedGuestTeam = 'kei,yuan,nyako,buck,kasumi,bee'
    $combinedDeadline = [Diagnostics.Stopwatch]::StartNew()
    $combinedHost = Start-Cli 'combined-game-host' @('--lan-host', '组合验证房', '--timer', '0,0', '--team-code', $combinedCode, '--export-team', '--share', '--auto', '--seed', '63')
    $combinedPort = [int](Wait-Output $combinedHost '(?m)^监听端口：([0-9]+)\r?$').Groups[1].Value
    Assert-Check ($combinedPort -ge 1 -and $combinedPort -le 65535) '组合对局没有分配实际 TCP 端口。'
    $combinedGuest = Start-Cli 'combined-game-guest' @('--join', ('127.0.0.1:' + $combinedPort), '--auto', '--share', '--team', $combinedGuestTeam)
    $combinedHostResult = Wait-Cli $combinedHost 0 ([Math]::Max(1, 60000 - [int]$combinedDeadline.ElapsedMilliseconds))
    $combinedGuestResult = Wait-Cli $combinedGuest 0 ([Math]::Max(1, 60000 - [int]$combinedDeadline.ElapsedMilliseconds))
    $combinedChildren = @($combinedHost, $combinedGuest)
    $combinedResults = @($combinedHostResult, $combinedGuestResult)
    $combinedHashes = [Collections.Generic.List[string]]::new()
    $combinedReplayJson = [Collections.Generic.List[string]]::new()
    $combinedSeed = Fair-Seed $combinedHostResult.Out
    for ($side = 0; $side -lt 2; $side++) {
        $combinedChild = $combinedChildren[$side]
        $combinedResult = $combinedResults[$side]
        $combinedName = if ($side -eq 0) { '房主' } else { '客机' }
        Assert-Check ((Fair-Seed $combinedResult.Out) -eq $combinedSeed) "$combinedName 组合对局的公平种子与对端不同。"
        Assert-Check ([string]::IsNullOrWhiteSpace($combinedResult.Err)) "$combinedName 组合对局输出错误。"
        Assert-Check ($combinedResult.Out.Contains('已启用房主计时：每阶段 0 秒；总时间 0 秒。')) "$combinedName 未采用房主零预算。"
        Assert-Check ($combinedResult.Out.Contains($combinedName + '：超时，提交默认跳过') -and $combinedResult.Out.Contains('对局结束：')) "$combinedName 未通过默认超时动作完成对局。"
        $combinedHash = [regex]::Matches($combinedResult.Out, '(?m)^最终状态哈希：([0-9a-f]{16})\r?$')
        Assert-Check ($combinedHash.Count -eq 1) "$combinedName 未恰好输出一份终局哈希。"
        $combinedHashes.Add($combinedHash[0].Groups[1].Value)

        $combinedSave = Join-Path $combinedChild.WorkingDirectory 'saves'
        $combinedReplayDirectory = Join-Path $combinedSave 'replays'
        Assert-Check (Test-Path -LiteralPath $combinedReplayDirectory -PathType Container) "$combinedName 未保存回放目录。"
        $combinedReplayFiles = @(Get-ChildItem -LiteralPath $combinedReplayDirectory -File -Filter '*.json')
        Assert-Check ($combinedReplayFiles.Count -eq 1) "$combinedName 应恰好保存一份完整回放。"
        $combinedJson = [IO.File]::ReadAllText($combinedReplayFiles[0].FullName, $utf8)
        $combinedReplayJson.Add($combinedJson)
        $combinedRecord = [StreamDuel.Core.BattleRecord]::FromJson($combinedJson)
        Assert-Check ($combinedRecord.Seed -eq $combinedSeed -and $combinedRecord.PackId -ceq 'original' -and
            ($combinedRecord.Teams[0] -join ',') -ceq $team -and ($combinedRecord.Teams[1] -join ',') -ceq $combinedGuestTeam -and
            $combinedRecord.Events.Count -gt 0) "$combinedName 回放丢失种子、队伍原顺序或行动记录。"
        $combinedReplay = [StreamDuel.Core.Replay]::Run($db, $combinedRecord)
        Assert-Check ($combinedReplay.Phase -eq [StreamDuel.Core.Phase]::Over -and
            $combinedReplay.StateHash().ToString('x16') -ceq $combinedHashes[$side]) "$combinedName 保存回放不能复现终局。"

        $combinedProfilePath = Join-Path $combinedSave 'profile.json'
        Assert-Check (Test-Path -LiteralPath $combinedProfilePath -PathType Leaf) "$combinedName 未保存档案。"
        $combinedProfile = [StreamDuel.Core.Profile]::FromJson([IO.File]::ReadAllText($combinedProfilePath, $utf8))
        Assert-Check ($combinedProfile.Packs.Count -eq 1 -and $combinedProfile.Packs.ContainsKey('original')) "$combinedName 档案内容包不正确。"
        $combinedStats = $combinedProfile.Packs['original']
        $combinedWins = [int]($combinedReplay.Winner -eq $side)
        $combinedLosses = [int]($combinedReplay.Winner -ge 0 -and $combinedReplay.Winner -ne $side)
        $combinedDraws = [int]($combinedReplay.Winner -eq -1)
        Assert-Check ($combinedStats.Wins -eq $combinedWins -and $combinedStats.Losses -eq $combinedLosses -and
            $combinedStats.Draws -eq $combinedDraws) "$combinedName 档案胜负不正确或重复累计。"
        Assert-Check ($combinedStats.Picks.Count -eq 6 -and $combinedStats.PickWins.Count -eq 6) "$combinedName 档案选用名单不是六位。"
        foreach ($id in $combinedRecord.Teams[$side]) {
            Assert-Check ($combinedStats.Picks.ContainsKey($id) -and $combinedStats.Picks[$id] -eq 1 -and
                $combinedStats.PickWins.ContainsKey($id) -and $combinedStats.PickWins[$id] -eq $combinedWins) "$combinedName 的 $id 选用记录不正确或重复累计。"
        }

        $combinedShared = [regex]::Matches($combinedResult.Out, '(?m)^分享码：(SD1-[A-Za-z0-9_-]+)\r?$')
        Assert-Check ($combinedShared.Count -eq 1) "$combinedName 未恰好导出一份分享码。"
        $combinedSharedRecord = [StreamDuel.Core.ShareCode]::Decode($combinedShared[0].Groups[1].Value)
        Assert-Check ($combinedSharedRecord.ToJson() -ceq $combinedRecord.ToJson()) "$combinedName 分享码未保留完整保存回放。"
        $combinedSharedReplay = [StreamDuel.Core.Replay]::Run($db, $combinedSharedRecord)
        Assert-Check ($combinedSharedReplay.Phase -eq [StreamDuel.Core.Phase]::Over -and
            $combinedSharedReplay.StateHash().ToString('x16') -ceq $combinedHashes[$side]) "$combinedName 分享码不能复现终局。"
    }
    Assert-Check ($combinedHashes[0] -ceq $combinedHashes[1] -and $combinedReplayJson[0] -ceq $combinedReplayJson[1]) '组合对局双方终局哈希或完整回放不同。'
    $combinedExported = [regex]::Matches($combinedHostResult.Out, '(?m)^本方队伍码：(SDT1-[A-Za-z0-9_-]+)\r?$')
    Assert-Check ($combinedExported.Count -eq 1 -and $combinedExported[0].Groups[1].Value -ceq $combinedCode) '组合对局没有原样导出自定义队伍码。'
    $combinedDecoded = [StreamDuel.Core.TeamCode]::Decode($combinedExported[0].Groups[1].Value)
    Assert-Check ($combinedDecoded.Name -ceq $combinedPreset.Name -and $combinedDecoded.PackId -ceq 'original' -and
        ($combinedDecoded.Ids -join ',') -ceq $team) '组合对局导出的名称、内容包或原六人顺序发生变化。'
    $checks.Add('独立真实 TCP：LAN 开房、房主零预算、双方默认超时、队伍码原样导出、分享码复现、终局哈希和完整回放一致及各一份档案/回放')
}
catch { $failure = $_ }
finally {
    foreach ($socket in $sockets) { $socket.Dispose() }
    foreach ($child in $children) {
        try {
            if ($child.Started -and -not $child.Process.HasExited) {
                $child.Process.Kill($true)
                $child.Killed = $true
                Assert-Check ($child.Process.WaitForExit(5000)) "$($child.Name) 清理超时。"
            }
            foreach ($task in @($child.OutTask, $child.ErrTask)) {
                if ($null -ne $task) { Assert-Check ($task.Wait(5000)) "$($child.Name) 输出清理超时。" }
            }
        }
        catch {
            $child.CleanupError = $_.Exception.Message
            if ($null -eq $failure) { $failure = $_ }
        }
        finally {
            if ($null -ne $child.OutStream) { $child.OutStream.Dispose() }
            if ($null -ne $child.ErrStream) { $child.ErrStream.Dispose() }
        }
    }
    $invocations = @($children | ForEach-Object {
        [ordered]@{
            name = $_.Name; parameters = $_.Parameters
            exit = $(if ($_.Started -and $_.Process.HasExited) { $_.Process.ExitCode } else { $null })
            killed = $_.Killed; cleanup_error = $_.CleanupError; stdout = $_.OutPath; stderr = $_.ErrPath
        }
    })
    foreach ($child in $children) { $child.Process.Dispose() }
    $report = [ordered]@{
        status = $(if ($null -eq $failure) { 'PASS' } else { 'FAIL' })
        error = $(if ($null -eq $failure) { $null } else { $failure.Exception.Message })
        scan_ms = $scanMilliseconds; real_broadcast = $broadcast; packets = $broadcastPackets.ToArray(); discover_and_join = $endToEnd
        checks = $checks.ToArray(); invocations = $invocations
    }
    [IO.File]::WriteAllText((Join-Path $runDirectory 'report.json'), ($report | ConvertTo-Json -Depth 8), $utf8)
}
if ($null -ne $failure) { throw $failure }
Write-Output "PASS：$($checks.Count) 组局域网发现 CLI 检查；实际广播状态：$($broadcast.status)；证据：$runDirectory"
