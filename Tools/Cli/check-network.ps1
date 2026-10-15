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
$binaryDirectory = Join-Path $PSScriptRoot "bin/$Configuration/net8.0"
$cliDll = Join-Path $binaryDirectory 'Cli.dll'
$coreDll = Join-Path $binaryDirectory 'StreamDuel.Core.dll'
foreach ($path in @($cliDll, $coreDll)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "请先运行 dotnet build Tools/Cli -c $Configuration。"
    }
}

# 独立保存每次检查，既不覆盖玩家档案，也不删除上一次失败的证据。
$runDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ('obj/network-verification/' + [Guid]::NewGuid().ToString('N'))))
if (-not $runDirectory.StartsWith($repositoryPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw '验收产物目录必须位于本仓库内。'
}
[void][IO.Directory]::CreateDirectory($runDirectory)
$utf8 = [Text.UTF8Encoding]::new($false)
$hostTeam = @('bell', 'enka', 'shiori', 'lumi', 'hane', 'momo')
$guestTeam = @('kei', 'yuan', 'nyako', 'buck', 'kasumi', 'bee')
$children = [Collections.Generic.List[object]]::new()
$checks = [Collections.Generic.List[string]]::new()
$finalHash = $null
$winner = $null
$mismatchEvidence = $null
$reportHashes = [ordered]@{ ipv4 = $null; ipv6 = $null }
$ipv6Evidence = [ordered]@{ supported = [Net.Sockets.Socket]::OSSupportsIPv6; status = 'NOT_RUN'; final_hash = $null }
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
        Result = $null; Killed = $false; CleanupError = $null
    }
    $children.Add($child)
    $child.OutStream = [IO.FileStream]::new($child.OutPath, [IO.FileMode]::CreateNew,
        [IO.FileAccess]::Write, [IO.FileShare]::ReadWrite, 1, [IO.FileOptions]::Asynchronous)
    $child.ErrStream = [IO.FileStream]::new($child.ErrPath, [IO.FileMode]::CreateNew,
        [IO.FileAccess]::Write, [IO.FileShare]::ReadWrite, 1, [IO.FileOptions]::Asynchronous)
    Assert-Check ($process.Start()) "$Name 无法启动。"
    $child.Started = $true
    # 异步复制原始流能看到没有换行的输入提示，也不依赖 PowerShell 事件回调的 runspace。
    $child.OutTask = $process.StandardOutput.BaseStream.CopyToAsync($child.OutStream)
    $child.ErrTask = $process.StandardError.BaseStream.CopyToAsync($child.ErrStream)
    # stdin 必须保持打开，才能证明等待人工输入时无需 EOF 或回车也会响应断线。
    return $child
}

function Wait-Output($Child, [string]$Pattern, [int]$TimeoutMs = 15000) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    while ($timer.ElapsedMilliseconds -lt $TimeoutMs) {
        $match = [regex]::Match((Read-Log $Child.OutPath), $Pattern)
        if ($match.Success) { return $match }
        Assert-Check (-not $Child.Process.HasExited) "$($Child.Name) 在出现预期输出前退出，请检查 stdout/stderr 日志。"
        Start-Sleep -Milliseconds 25
    }
    throw "$($Child.Name) 等待输出超时。证据目录：$runDirectory"
}

function Wait-Cli($Child, [int]$ExpectedExit, [int]$TimeoutMs = 60000) {
    if ($null -eq $Child.Result) {
        $timer = [Diagnostics.Stopwatch]::StartNew()
        while (-not $Child.Process.HasExited -and $timer.ElapsedMilliseconds -lt $TimeoutMs) {
            Start-Sleep -Milliseconds 25
        }
        Assert-Check ($Child.Process.HasExited) "$($Child.Name) 超过 $TimeoutMs 毫秒仍未退出。"
        Assert-Check ($Child.OutTask.Wait(5000)) "$($Child.Name) 的标准输出未结束。"
        Assert-Check ($Child.ErrTask.Wait(5000)) "$($Child.Name) 的标准错误未结束。"
        $Child.OutStream.Flush()
        $Child.ErrStream.Flush()
        $Child.Result = [pscustomobject]@{
            ExitCode = $Child.Process.ExitCode
            Out = (Read-Log $Child.OutPath)
            Err = (Read-Log $Child.ErrPath)
        }
    }
    Assert-Check ($Child.Result.ExitCode -eq $ExpectedExit) "$($Child.Name) 退出码应为 $ExpectedExit，实际为 $($Child.Result.ExitCode)。"
    return $Child.Result
}

function Start-Pair([string]$Name, [string]$HostSave, [string]$GuestSave, [switch]$Auto,
    [string]$Pack = 'original', [string[]]$HostExtra = @(), [string[]]$GuestExtra = @(),
    [string]$Address = '127.0.0.1') {
    $common = @('--pack', $Pack, '--hash')
    if ($Auto) { $common += '--auto' }
    $hostChild = Start-Cli "$Name-host" ($common + @('--host', '0', '--seed', '7',
        '--team', ($hostTeam -join ','), '--save-dir', $HostSave) + $HostExtra)
    $portOutput = Wait-Output $hostChild '(?m)^监听端口：([0-9]+)\r?$'
    $port = [int]$portOutput.Groups[1].Value
    Assert-Check ($port -gt 0 -and $port -le 65535) '主机没有输出系统实际分配的端口。'
    $guestChild = Start-Cli "$Name-guest" ($common + @('--join', "${Address}:$port", '--seed', '99',
        '--team', ($guestTeam -join ','), '--save-dir', $GuestSave) + $GuestExtra)
    return [pscustomobject]@{ Host = $hostChild; Guest = $guestChild; Port = $port }
}

function Final-Hash([string]$Output) {
    $matches = [regex]::Matches($Output, '(?m)^最终状态哈希：([0-9a-fA-F]{16})\r?$')
    Assert-Check ($matches.Count -eq 1) '每个完成的进程应输出且仅输出一行 16 位最终状态哈希。'
    return $matches[0].Groups[1].Value.ToLowerInvariant()
}

function Replay-Files([string]$SaveDirectory) {
    $directory = Join-Path $SaveDirectory 'replays'
    if (Test-Path -LiteralPath $directory -PathType Container) {
        Get-ChildItem -LiteralPath $directory -File -Filter '*.json' | Sort-Object Name
    }
}

function Assert-NoSave([string]$SaveDirectory) {
    Assert-Check (-not (Test-Path -LiteralPath (Join-Path $SaveDirectory 'profile.json'))) '未完成对局产生了胜负档案。'
    Assert-Check (@(Replay-Files $SaveDirectory).Count -eq 0) '未完成对局产生了回放。'
}

function Save-Fingerprint([string]$SaveDirectory) {
    $files = @(Get-ChildItem -LiteralPath $SaveDirectory -File -Recurse -Force | Sort-Object FullName)
    return (($files | ForEach-Object {
        $_.FullName + ':' + (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    }) -join "`n")
}

function Assert-Profile([string]$SaveDirectory, [int]$MySide, [string[]]$Team, [int]$Winner) {
    $path = Join-Path $SaveDirectory 'profile.json'
    Assert-Check (Test-Path -LiteralPath $path -PathType Leaf) '完成对局未保存档案。'
    $profile = [StreamDuel.Core.Profile]::FromJson([IO.File]::ReadAllText($path, $utf8))
    Assert-Check ($profile.Packs.Count -eq 1 -and $profile.Packs.ContainsKey('original')) '档案的内容包不正确。'
    $stats = $profile.Packs['original']
    $wins = [int]($Winner -eq $MySide)
    $losses = [int]($Winner -ge 0 -and $Winner -ne $MySide)
    $draws = [int]($Winner -eq -1)
    Assert-Check ($stats.Wins -eq $wins -and $stats.Losses -eq $losses -and $stats.Draws -eq $draws) "MySide=$MySide 的胜负记录不正确或重复累计。"
    Assert-Check ($stats.Picks.Count -eq 6 -and $stats.PickWins.Count -eq 6) "MySide=$MySide 的选用名单不是六位。"
    foreach ($id in $Team) {
        Assert-Check ($stats.Picks.ContainsKey($id) -and $stats.Picks[$id] -eq 1) "MySide=$MySide 的 $id 选用次数不正确。"
        Assert-Check ($stats.PickWins.ContainsKey($id) -and $stats.PickWins[$id] -eq $wins) "MySide=$MySide 的 $id 选用胜场不正确。"
    }
}

function Assert-ReportsEqual([string]$HostReport, [string]$GuestReport) {
    $strictUtf8 = [Text.UTF8Encoding]::new($false, $true)
    $firstHash = $null
    foreach ($path in @($HostReport, $GuestReport)) {
        Assert-Check (Test-Path -LiteralPath $path -PathType Leaf) '完成的联网对局未导出战报。'
        # 严格解码会拒绝无效 UTF-8；结构校验允许 UTF-8 BOM，不依赖系统默认编码。
        $text = $strictUtf8.GetString([IO.File]::ReadAllBytes($path)).TrimStart([char]0xfeff)
        foreach ($pattern in @(
            '\A# 对战战报\r?\n',
            '(?m)^内容包：原创主播\r?$',
            '(?m)^胜负：(房主 获胜|客机 获胜|平局)\r?$',
            '(?m)^### 甲方：房主\r?$',
            '(?m)^### 乙方：客机\r?$',
            '(?m)^## 第 [0-9]+ 回合\r?$',
            '(?m)^### 开场\r?$',
            '(?m)^### 结束\r?$',
            '(?m)^## 对战统计\r?$'
        )) {
            Assert-Check ([regex]::IsMatch($text, $pattern)) "战报缺少要求的 Markdown 结构：$pattern。"
        }
        Assert-Check ([regex]::Matches($text, '(?m)^\| 号位 \| 主播 \| 造成伤害 \| 承受伤害 \| 击倒 \| 在场回合 \| 状态 \| 技能使用 \|\r?$').Count -eq 2) '战报没有双方各一份统计表。'
        foreach ($id in @($hostTeam + $guestTeam)) {
            Assert-Check ($text.Contains('（' + $id + '）')) "战报首发阵容缺少主播 $id。"
        }
        $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        if ($null -eq $firstHash) { $firstHash = $hash }
        Assert-Check ($hash -ceq $firstHash) '主客战报的文件内容不一致。'
    }
    return $firstHash
}

try {
    $hostSave = Join-Path $runDirectory 'host-save'
    $guestSave = Join-Path $runDirectory 'guest-save'
    $hostReport = Join-Path $runDirectory 'host-report.md'
    $guestReport = Join-Path $runDirectory 'guest-report.md'
    $pair = Start-Pair '01-auto' $hostSave $guestSave -Auto `
        -HostExtra @('--report', $hostReport) -GuestExtra @('--report', $guestReport)
    $hostResult = Wait-Cli $pair.Host 0
    $guestResult = Wait-Cli $pair.Guest 0
    $finalHash = Final-Hash $hostResult.Out
    Assert-Check ($finalHash -ceq (Final-Hash $guestResult.Out)) '双进程的最终状态哈希不同。'
    foreach ($result in @($hostResult, $guestResult)) {
        Assert-Check ($result.Err.Length -eq 0) '正常联网对局产生了标准错误。'
        Assert-Check ($result.Out.Contains('房主种子：7')) '客机没有使用主机种子 7。'
        Assert-Check ($result.Out.Contains('对局结束：') -and $result.Out.Contains('等待对手……')) '正常对局未显示等待对手或结束信息。'
        Assert-Check ($result.Out.Contains('战报已保存：')) '完成的联网对局未报告战报保存成功。'
    }
    $checks.Add('双进程自动对战：动态端口、双方 exit 0、主机种子 7 覆盖客机 99、最终哈希一致')
    $reportHashes.ipv4 = Assert-ReportsEqual $hostReport $guestReport
    $checks.Add('双方独立战报：内容字节一致、严格 UTF-8、首发阵容/胜负/回合节点/双方统计表完整')

    # 回看 CLI 不接受 --hash；使用同一构建的公开 Replay API 重新结算后核对哈希。
    [void][Reflection.Assembly]::LoadFrom($coreDll)
    $db = [StreamDuel.Core.ContentDb]::FromJson(
        [IO.File]::ReadAllText((Join-Path $binaryDirectory 'Content/StreamDuelRules.json'), $utf8),
        [IO.File]::ReadAllText((Join-Path $binaryDirectory 'Content/Packs/original.json'), $utf8))
    $replayPaths = [Collections.Generic.List[string]]::new()
    foreach ($save in @($hostSave, $guestSave)) {
        $files = @(Replay-Files $save)
        Assert-Check ($files.Count -eq 1) '每端完成对局应恰好保存一份回放。'
        $replayPaths.Add($files[0].FullName)
        $record = [StreamDuel.Core.BattleRecord]::FromJson([IO.File]::ReadAllText($files[0].FullName, $utf8))
        Assert-Check ($record.Seed -eq 7 -and $record.PackId -ceq 'original') '回放种子或内容包不正确。'
        Assert-Check (-not $record.BanOne -and -not $record.LeadPick) '基础联网回放意外启用了禁用或首发。'
        Assert-Check (($record.Teams[0] -join ',') -ceq ($hostTeam -join ',') -and
            ($record.Teams[1] -join ',') -ceq ($guestTeam -join ',')) '回放的主客队伍或顺序不正确。'
        $replayed = [StreamDuel.Core.Replay]::Run($db, $record)
        Assert-Check ($replayed.Phase -eq [StreamDuel.Core.Phase]::Over) '回放没有重新结算到终局。'
        Assert-Check ($replayed.StateHash().ToString('x16', [Globalization.CultureInfo]::InvariantCulture) -ceq $finalHash) '重新运行回放的最终哈希与联网对局不同。'
        if ($null -eq $winner) { $winner = $replayed.Winner }
        Assert-Check ($winner -eq $replayed.Winner) '两份回放的胜者不同。'
    }
    Assert-Profile $hostSave 0 $hostTeam $winner
    Assert-Profile $guestSave 1 $guestTeam $winner
    $checks.Add('两端各一份回放：Replay.Run 复现终局哈希；主客分别按 MySide 记录胜负及本方六人')

    $hostFingerprint = Save-Fingerprint $hostSave
    $guestFingerprint = Save-Fingerprint $guestSave
    for ($side = 0; $side -lt 2; $side++) {
        $save = @($hostSave, $guestSave)[$side]
        $replayChild = Start-Cli "02-replay-$side" @('--replay', $replayPaths[$side], '--auto', '--save-dir', $save)
        $replayResult = Wait-Cli $replayChild 0
        Assert-Check ($replayResult.Out.Contains('回看结束：') -and $replayResult.Err.Length -eq 0) '联网回放不能通过 CLI 正常回看。'
    }
    Assert-Check ((Save-Fingerprint $hostSave) -ceq $hostFingerprint -and
        (Save-Fingerprint $guestSave) -ceq $guestFingerprint) '回看改动了档案或回放。'
    $checks.Add('两端回放均能通过 CLI 自动回看，且不改写原档案和回放')

    foreach ($quitSide in @('guest', 'host')) {
        $prefix = "03-disconnect-$quitSide"
        $disconnectHostSave = Join-Path $runDirectory "$prefix-host-save"
        $disconnectGuestSave = Join-Path $runDirectory "$prefix-guest-save"
        $disconnectHostReport = Join-Path $runDirectory "$prefix-host-report.md"
        $disconnectGuestReport = Join-Path $runDirectory "$prefix-guest-report.md"
        $manual = Start-Pair $prefix $disconnectHostSave $disconnectGuestSave `
            -HostExtra @('--report', $disconnectHostReport) -GuestExtra @('--report', $disconnectGuestReport)
        $null = Wait-Output $manual.Host '选择行动（q 退出）> '
        $null = Wait-Output $manual.Guest '选择行动（q 退出）> '
        $quitter = if ($quitSide -eq 'guest') { $manual.Guest } else { $manual.Host }
        $waiting = if ($quitSide -eq 'guest') { $manual.Host } else { $manual.Guest }
        $timer = [Diagnostics.Stopwatch]::StartNew()
        $quitter.Process.StandardInput.WriteLine('q')
        $quitter.Process.StandardInput.Flush()
        $quitResult = Wait-Cli $quitter 0 10000
        $remaining = [Math]::Max(1, 10000 - [int]$timer.ElapsedMilliseconds)
        $waitingResult = Wait-Cli $waiting 1 $remaining
        Assert-Check ($timer.ElapsedMilliseconds -lt 10000) '人工输入等待期间的断线处理超过 10 秒。'
        Assert-Check ($quitResult.Out.Contains('已退出，对局未完成。')) '主动 q 退出没有说明对局未完成。'
        Assert-Check (($waitingResult.Out + $waitingResult.Err).Contains('对方已断开')) '未输入的一端没有报告对方已断开。'
        foreach ($result in @($quitResult, $waitingResult)) {
            Assert-Check (-not $result.Out.Contains('最终状态哈希：')) '断线对局意外报告最终状态哈希。'
            Assert-Check (-not $result.Out.Contains('战报已保存')) '断线对局意外报告战报保存成功。'
        }
        Assert-NoSave $disconnectHostSave
        Assert-NoSave $disconnectGuestSave
        Assert-Check (-not (Test-Path -LiteralPath $disconnectHostReport) -and
            -not (Test-Path -LiteralPath $disconnectGuestReport)) '断线对局产生了战报。'
        $checks.Add("$quitSide 输入 q：另一端 stdin 保持打开且没有输入，10 秒内 exit 1，两端不保存档案/回放/战报")
    }

    # 内置包优先于同 id 的外部包，所以两端都用本次专属 id，且各加载自己的目录。
    $mismatchPackId = 'network_check_' + [IO.Path]::GetFileName($runDirectory)
    $mismatchPackName = '联网数值差异验收 ' + $mismatchPackId
    $mismatchHostPacks = Join-Path $runDirectory 'mismatch-host-packs'
    $mismatchGuestPacks = Join-Path $runDirectory 'mismatch-guest-packs'
    [void][IO.Directory]::CreateDirectory($mismatchHostPacks)
    [void][IO.Directory]::CreateDirectory($mismatchGuestPacks)
    $mismatchRules = [IO.File]::ReadAllText((Join-Path $binaryDirectory 'Content/StreamDuelRules.json'), $utf8)
    $originalPackJson = [IO.File]::ReadAllText((Join-Path $binaryDirectory 'Content/Packs/original.json'), $utf8)
    $mismatchHostPack = $originalPackJson | ConvertFrom-Json
    $mismatchHostPack.id = $mismatchPackId
    $mismatchHostPack.name = $mismatchPackName
    $mismatchHostJson = $mismatchHostPack | ConvertTo-Json -Depth 100
    $mismatchGuestPack = $mismatchHostJson | ConvertFrom-Json
    $mismatchGuestPack.streamers[0].stats.hp += 1
    $mismatchGuestJson = $mismatchGuestPack | ConvertTo-Json -Depth 100
    [IO.File]::WriteAllText((Join-Path $mismatchHostPacks "$mismatchPackId.json"), $mismatchHostJson, $utf8)
    [IO.File]::WriteAllText((Join-Path $mismatchGuestPacks "$mismatchPackId.json"), $mismatchGuestJson, $utf8)
    $builtInPacks = [StreamDuel.Core.PackLoader]::LoadDirectory($mismatchRules, (Join-Path $binaryDirectory 'Content/Packs'))
    Assert-Check (@($builtInPacks | Where-Object { $null -ne $_.Db -and $_.Db.PackId -ceq $mismatchPackId }).Count -eq 0) '差异验收包 id 与内置包冲突。'
    $mismatchHostLoaded = [StreamDuel.Core.PackLoader]::LoadDirectory($mismatchRules, $mismatchHostPacks)
    $mismatchGuestLoaded = [StreamDuel.Core.PackLoader]::LoadDirectory($mismatchRules, $mismatchGuestPacks)
    foreach ($loaded in @($mismatchHostLoaded, $mismatchGuestLoaded)) {
        Assert-Check ($loaded.Count -eq 1 -and $null -ne $loaded[0].Db) '数值差异验收包加载失败。'
        Assert-Check ($loaded[0].Db.PackId -ceq $mismatchPackId) '数值差异验收包没有使用专属 id。'
    }
    $mismatchHostHash = $mismatchHostLoaded[0].Db.ContentHash()
    $mismatchGuestHash = $mismatchGuestLoaded[0].Db.ContentHash()
    Assert-Check ($mismatchHostHash -cne $mismatchGuestHash) '数值差异样本未产生不同内容指纹。'
    $mismatchEvidence = [ordered]@{
        pack_id = $mismatchPackId; host_hash = $mismatchHostHash; guest_hash = $mismatchGuestHash
        streamer_id = $mismatchHostPack.streamers[0].id
        host_hp = $mismatchHostPack.streamers[0].stats.hp; guest_hp = $mismatchGuestPack.streamers[0].stats.hp
    }
    $mismatchHostSave = Join-Path $runDirectory '04-mismatch-host-save'
    $mismatchGuestSave = Join-Path $runDirectory '04-mismatch-guest-save'
    $mismatch = Start-Pair '04-mismatch' $mismatchHostSave $mismatchGuestSave -Auto -Pack $mismatchPackId `
        -HostExtra @('--pack-dir', $mismatchHostPacks) -GuestExtra @('--pack-dir', $mismatchGuestPacks)
    foreach ($child in @($mismatch.Host, $mismatch.Guest)) {
        $result = Wait-Cli $child 1 15000
        Assert-Check ($result.Out.Contains('内容包：' + $mismatchPackName)) '进程没有加载本次专属的差异验收包。'
        Assert-Check (($result.Out + $result.Err).Contains('内容不一致')) '同包 id 不同内容未报告内容不一致。'
        Assert-Check (-not $result.Out.Contains('连接成功；')) '内容不一致仍进入了对战。'
    }
    Assert-NoSave $mismatchHostSave
    Assert-NoSave $mismatchGuestSave
    $checks.Add('专属同包 id 的实际数值差异：预检指纹不同，两端中文报错并 exit 1，未开局且不保存')

    $invalidCases = @(
        @{ Name = 'host-range'; Parameters = @('--host', '65536') },
        @{ Name = 'join-zero'; Parameters = @('--join', 'localhost:0') },
        @{ Name = 'join-ipv6-brackets'; Parameters = @('--join', '::1:7777') },
        @{ Name = 'mutually-exclusive'; Parameters = @('--host', '0', '--join', '127.0.0.1:1') },
        @{ Name = 'duplicate-host'; Parameters = @('--host', '0', '--host', '0') },
        @{ Name = 'duplicate-join'; Parameters = @('--join', 'localhost:1', '--join', 'localhost:1') },
        @{ Name = 'network-ban'; Parameters = @('--host', '0', '--ban') },
        @{ Name = 'network-lead'; Parameters = @('--host', '0', '--lead') },
        @{ Name = 'network-replay'; Parameters = @('--host', '0', '--replay', $replayPaths[0]) },
        @{ Name = 'network-list-packs'; Parameters = @('--host', '0', '--list-packs') }
    )
    $invalidSave = Join-Path $runDirectory '05-invalid-save'
    foreach ($case in $invalidCases) {
        $child = Start-Cli "05-$($case.Name)" ($case.Parameters + @('--save-dir', $invalidSave))
        $result = Wait-Cli $child 1 10000
        Assert-Check ($result.Err.StartsWith('错误：') -and -not $result.Out.Contains('监听端口：')) "$($case.Name) 未在连接前给出中文参数错误。"
    }
    Assert-NoSave $invalidSave
    $checks.Add('端口范围、IPv6 格式、互斥与重复参数、联网不支持选项：连接前中文错误且 exit 1')

    if ($ipv6Evidence.supported) {
        $ipv6HostSave = Join-Path $runDirectory '06-ipv6-host-save'
        $ipv6GuestSave = Join-Path $runDirectory '06-ipv6-guest-save'
        $ipv6HostReport = Join-Path $runDirectory '06-ipv6-host-report.md'
        $ipv6GuestReport = Join-Path $runDirectory '06-ipv6-guest-report.md'
        $ipv6Evidence.status = 'RUNNING'
        $ipv6 = Start-Pair '06-ipv6-auto' $ipv6HostSave $ipv6GuestSave -Auto -Address '[::1]' `
            -HostExtra @('--report', $ipv6HostReport) -GuestExtra @('--report', $ipv6GuestReport)
        $ipv6HostResult = Wait-Cli $ipv6.Host 0
        $ipv6GuestResult = Wait-Cli $ipv6.Guest 0
        foreach ($result in @($ipv6HostResult, $ipv6GuestResult)) {
            Assert-Check ($result.Err.Length -eq 0 -and $result.Out.Contains('对局结束：')) 'IPv6 自动回环未正常完成。'
            Assert-Check ($result.Out.Contains('房主种子：7') -and $result.Out.Contains('战报已保存：')) 'IPv6 自动回环未采用主机种子或保存战报。'
            Assert-Check ((Final-Hash $result.Out) -ceq $finalHash) 'IPv6 终局哈希与相同队伍、种子的 IPv4 对局不同。'
        }
        $reportHashes.ipv6 = Assert-ReportsEqual $ipv6HostReport $ipv6GuestReport
        Assert-Check ($reportHashes.ipv6 -ceq $reportHashes.ipv4) 'IPv6 与相同队伍、种子的 IPv4 战报不同。'
        Assert-Profile $ipv6HostSave 0 $hostTeam $winner
        Assert-Profile $ipv6GuestSave 1 $guestTeam $winner
        Assert-Check (@(Replay-Files $ipv6HostSave).Count -eq 1 -and
            @(Replay-Files $ipv6GuestSave).Count -eq 1) 'IPv6 对局没有为双方各保存一份回放。'
        $ipv6Evidence.status = 'PASS'
        $ipv6Evidence.final_hash = Final-Hash $ipv6HostResult.Out
        $checks.Add('IPv6 [::1] 真实自动回环：双方 exit 0、战报/终局与 IPv4 相同、各自档案及回放保存')
    }
    else {
        $ipv6Evidence.status = 'UNSUPPORTED'
        Write-Output 'IPv6：Socket.OSSupportsIPv6=false，平台不支持，未执行该项。'
    }
    Assert-Check (-not (Test-Path -LiteralPath (Join-Path $runDirectory 'saves'))) '验收进程误写了默认存档目录。'
}
catch { $failure = $_ }
finally {
    # 只结束本次明确启动的进程树；所有证据目录保留供定位失败。
    foreach ($child in $children) {
        try {
            if ($child.Started -and -not $child.Process.HasExited) {
                $child.Process.Kill($true)
                $child.Killed = $true
                Assert-Check ($child.Process.WaitForExit(5000)) "$($child.Name) 终止进程树后仍未退出。"
            }
            foreach ($task in @($child.OutTask, $child.ErrTask)) {
                if ($null -ne $task) { Assert-Check ($task.Wait(5000)) "$($child.Name) 清理输出流超时。" }
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
            killed = $_.Killed; cleanup_error = $_.CleanupError
            stdout = $_.OutPath; stderr = $_.ErrPath
        }
    })
    foreach ($child in $children) { $child.Process.Dispose() }
    $report = [ordered]@{
        status = $(if ($null -eq $failure) { 'PASS' } else { 'FAIL' })
        error = $(if ($null -eq $failure) { $null } else { $failure.Exception.Message })
        final_hash = $finalHash; winner = $winner; content_mismatch = $mismatchEvidence
        battle_reports_sha256 = $reportHashes; ipv6 = $ipv6Evidence
        checks = $checks.ToArray(); invocations = $invocations
    }
    [IO.File]::WriteAllText((Join-Path $runDirectory 'report.json'), ($report | ConvertTo-Json -Depth 8), $utf8)
}
if ($null -ne $failure) { throw $failure }
Write-Output "PASS：$($checks.Count) 组联网 CLI 集成检查；证据：$runDirectory"
