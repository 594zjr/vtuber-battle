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
$runDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ('obj/rating-check/' + [Guid]::NewGuid().ToString('N'))))
if (-not $runDirectory.StartsWith($repositoryPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw '验收产物目录必须位于本仓库内。'
}
[void][IO.Directory]::CreateDirectory($runDirectory)
$utf8 = [Text.UTF8Encoding]::new($false)
$children = [Collections.Generic.List[object]]::new()
$checks = [Collections.Generic.List[string]]::new()
$hostTeam = 'bell,enka,shiori,lumi,hane,momo'
$guestTeam = 'kei,yuan,nyako,buck,kasumi,bee'

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
        Name = $Name; Process = $process; Started = $false
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
    return $child
}

function Wait-Output($Child, [string]$Pattern) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    while ($timer.ElapsedMilliseconds -lt 15000) {
        $match = [regex]::Match((Read-Log $Child.OutPath), $Pattern)
        if ($match.Success) { return $match }
        Assert-Check (-not $Child.Process.HasExited) "$($Child.Name) 在预期输出前退出。"
        Start-Sleep -Milliseconds 25
    }
    throw "$($Child.Name) 等待输出超时。证据目录：$runDirectory"
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

function Invoke-Cli([string]$Name, [string[]]$Parameters, [int]$ExpectedExit = 0) {
    $child = Start-Cli $Name $Parameters
    $child.Process.StandardInput.Close()
    return Wait-Cli $child $ExpectedExit
}

function Start-Pair([string]$Name, [string]$HostSave, [string]$GuestSave, [switch]$Auto) {
    $extra = if ($Auto) { @('--auto') } else { @() }
    $hostChild = Start-Cli "$Name-host" (@('--host', '0', '--seed', '7', '--hash', '--team', $hostTeam, '--save-dir', $HostSave) + $extra)
    $port = (Wait-Output $hostChild '(?m)^监听端口：([0-9]+)\r?$').Groups[1].Value
    $guestChild = Start-Cli "$Name-guest" (@('--join', "127.0.0.1:$port", '--hash', '--team', $guestTeam, '--save-dir', $GuestSave) + $extra)
    return [pscustomobject]@{ Host = $hostChild; Guest = $guestChild }
}

function Fingerprint([string]$Directory) {
    if (-not (Test-Path -LiteralPath $Directory)) { return '' }
    return (@(Get-ChildItem -LiteralPath $Directory -File -Recurse -Force | Sort-Object FullName | ForEach-Object {
        $_.FullName + ':' + (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    }) -join "`n")
}

function Read-Profile([string]$Directory) {
    return [IO.File]::ReadAllText((Join-Path $Directory 'profile.json')) | ConvertFrom-Json
}

function Write-RatedProfile([string]$Directory, [int]$Rating, [int]$Games) {
    [void][IO.Directory]::CreateDirectory($Directory)
    $profile = @{ packs = @{}; ratings = @{ '默认' = @{ rating = $Rating; games = $Games }; '保留赛制' = @{ rating = 1601; games = 8 } } }
    [IO.File]::WriteAllText((Join-Path $Directory 'profile.json'), ($profile | ConvertTo-Json -Depth 5), $utf8)
}

function Expected-Rating([int]$Rating, [int]$Games, [int]$Opponent, [double]$Score) {
    $k = if ($Games -lt 10) { 40 } else { 24 }
    $expected = 1.0 / (1.0 + [Math]::Pow(10.0, ($Opponent - $Rating) / 400.0))
    return $Rating + [int][Math]::Round($k * ($Score - $expected), [MidpointRounding]::AwayFromZero)
}

function Assert-RatedResult([string]$Directory, $Result, [string]$Name, [int]$BeforeRating,
    [int]$BeforeGames, [int]$Opponent, [int]$FormatCount) {
    $winner = [regex]::Match($Result.Out, '(?m)^对局结束：(房主 获胜|客机 获胜|平局)\r?$')
    Assert-Check $winner.Success '联网完局缺少胜负结果。'
    $score = if ($winner.Groups[1].Value -eq '平局') { 0.5 } elseif ($winner.Groups[1].Value -eq "$Name 获胜") { 1.0 } else { 0.0 }
    $profile = Read-Profile $Directory
    $rating = $profile.ratings.'默认'
    Assert-Check ($rating.rating -eq (Expected-Rating $BeforeRating $BeforeGames $Opponent $score)) "$Name 没有按自己和对手的不同分数更新 Elo。"
    Assert-Check ($rating.games -eq $BeforeGames + 1) "$Name 的排位局数没有恰好加一。"
    Assert-Check (@($profile.ratings.PSObject.Properties).Count -eq $FormatCount) "$Name 错存了对手或额外赛制。"
    $stats = $profile.packs.original
    Assert-Check ($stats.wins -eq [int]($score -eq 1) -and $stats.losses -eq [int]($score -eq 0) -and $stats.draws -eq [int]($score -eq 0.5)) "$Name 胜负统计错误或重复累计。"
    Assert-Check (@($stats.picks.PSObject.Properties).Count -eq 6) "$Name 的选用统计不是六位主播。"
    foreach ($pick in $stats.picks.PSObject.Properties) { Assert-Check ($pick.Value -eq 1) "$Name 的选用次数重复累计。" }
    Assert-Check (@(Get-ChildItem -LiteralPath (Join-Path $Directory 'replays') -File -Filter '*.json').Count -eq 1) "$Name 未恰保存一份回放。"
    Assert-Check ($Result.Out.IndexOf('最终状态哈希：') -ge 0 -and
        $Result.Out.IndexOf('本机排位（默认）：') -gt $Result.Out.IndexOf('最终状态哈希：')) '排位输出先于终局校验。'
    Assert-Check ($Result.Err.Length -eq 0) "$Name 正常完局出现错误。"
    return $rating
}

try {
    $legacySave = Join-Path $runDirectory 'legacy-saves'
    [void][IO.Directory]::CreateDirectory($legacySave)
    [IO.File]::WriteAllText((Join-Path $legacySave 'profile.json'),
        '{"packs":{"original":{"wins":2,"losses":1,"draws":1,"picks":{"bell":3},"pickWins":{"bell":2}}}}', $utf8)
    $offline = Invoke-Cli '01-legacy-offline' @('--auto', '--seed', '1', '--save-dir', $legacySave)
    $legacy = Read-Profile $legacySave
    $stats = $legacy.packs.original
    Assert-Check (($stats.wins + $stats.losses + $stats.draws) -eq 5 -and $stats.picks.bell -ge 3) '旧档案离线累计丢失或重复战绩。'
    Assert-Check ($null -eq $legacy.PSObject.Properties['ratings'] -and -not $offline.Out.Contains('本机排位（')) '离线对局创建了排位记录。'
    $beforeReplay = Fingerprint $legacySave
    $replay = @(Get-ChildItem -LiteralPath (Join-Path $legacySave 'replays') -File -Filter '*.json')[0]
    [void](Invoke-Cli '02-replay' @('--auto', '--replay', $replay.FullName, '--save-dir', $legacySave))
    Assert-Check ((Fingerprint $legacySave) -ceq $beforeReplay) '回看修改了档案或增加回放。'
    $checks.Add('旧版无评级档案保留原战绩并正常离线累计，回看不写存档')

    $ratedOfflineSave = Join-Path $runDirectory 'rated-offline-saves'
    Write-RatedProfile $ratedOfflineSave 1320 4
    [void](Invoke-Cli '03-rated-offline' @('--auto', '--save-dir', $ratedOfflineSave))
    $ratedOffline = Read-Profile $ratedOfflineSave
    Assert-Check ($ratedOffline.ratings.'默认'.rating -eq 1320 -and $ratedOffline.ratings.'默认'.games -eq 4 -and
        $ratedOffline.ratings.'保留赛制'.rating -eq 1601 -and $ratedOffline.ratings.'保留赛制'.games -eq 8) '离线对局改变了已有排位分。'
    $checks.Add('已有默认及其他赛制评级在离线对局后保持不变')

    $hostSave = Join-Path $runDirectory 'host-saves'
    $guestSave = Join-Path $runDirectory 'guest-saves'
    Write-RatedProfile $hostSave 1320 4
    Write-RatedProfile $guestSave 1780 23
    $hostProfilePath = Join-Path $hostSave 'profile.json'
    $openingProfile = [IO.File]::ReadAllText($hostProfilePath)
    $snapshotStream = [IO.FileStream]::new($hostProfilePath, [IO.FileMode]::Open, [IO.FileAccess]::Read,
        [IO.FileShare]::Read -bor [IO.FileShare]::Delete)
    $snapshotReader = [IO.StreamReader]::new($snapshotStream)
    try {
        $pair = Start-Pair '04-rated-network' $hostSave $guestSave -Auto
        $hostResult = Wait-Cli $pair.Host
        $guestResult = Wait-Cli $pair.Guest
        Assert-Check ($snapshotReader.ReadToEnd() -ceq $openingProfile) '原子替换后原读取句柄没有保留完整旧档案。'
    }
    finally { $snapshotReader.Dispose() }
    $hostRating = Assert-RatedResult $hostSave $hostResult '房主' 1320 4 1780 2
    $guestRating = Assert-RatedResult $guestSave $guestResult '客机' 1780 23 1320 2
    foreach ($directory in @($hostSave, $guestSave)) {
        $retained = (Read-Profile $directory).ratings.'保留赛制'
        Assert-Check ($retained.rating -eq 1601 -and $retained.games -eq 8) '联网结算覆盖了其他赛制。'
    }
    Assert-Check ($hostResult.Out.Contains('本方自报排位分：1320；对手自报排位分：1780') -and
        $guestResult.Out.Contains('本方自报排位分：1780；对手自报排位分：1320')) '联网双方没有交换开场评级快照。'
    $hostReplay = @(Get-ChildItem -LiteralPath (Join-Path $hostSave 'replays') -File -Filter '*.json')[0]
    $guestReplay = @(Get-ChildItem -LiteralPath (Join-Path $guestSave 'replays') -File -Filter '*.json')[0]
    Assert-Check ((Get-FileHash -LiteralPath $hostReplay.FullName).Hash -ceq (Get-FileHash -LiteralPath $guestReplay.FullName).Hash) '不同评级影响了双方战斗回放。'
    $checks.Add('不同分数及定级局数的真实TCP完局双方正确结算，Games各加一，只保存本方默认评级')
    $checks.Add('旧档案读取句柄打开期间仍可保存终局，新路径是新档案而旧句柄保留完整旧快照')

    $beforeHost = Fingerprint $hostSave
    $beforeGuest = Fingerprint $guestSave
    $quitPair = Start-Pair '05-existing-quit' $hostSave $guestSave
    [void](Wait-Output $quitPair.Host '选择行动（q 退出）>')
    [void](Wait-Output $quitPair.Guest '选择行动（q 退出）>')
    $quitPair.Host.Process.StandardInput.WriteLine('q')
    [void](Wait-Cli $quitPair.Host)
    $disconnected = Wait-Cli $quitPair.Guest 1
    Assert-Check ($disconnected.Err.StartsWith('错误：')) '断线缺少中文错误。'
    Assert-Check ((Fingerprint $hostSave) -ceq $beforeHost -and (Fingerprint $guestSave) -ceq $beforeGuest) '主动退出或断线改变了已有存档。'
    $checks.Add('握手后的主动退出和对端断线均保持既有存档字节不变')

    $freshHostSave = Join-Path $runDirectory 'fresh-host-saves'
    $freshGuestSave = Join-Path $runDirectory 'fresh-guest-saves'
    $freshQuit = Start-Pair '06-fresh-quit' $freshHostSave $freshGuestSave
    [void](Wait-Output $freshQuit.Host '选择行动（q 退出）>')
    [void](Wait-Output $freshQuit.Guest '选择行动（q 退出）>')
    Assert-Check (-not (Test-Path -LiteralPath $freshHostSave) -and -not (Test-Path -LiteralPath $freshGuestSave)) '只读加载评级或握手创建了存档目录。'
    $freshQuit.Host.Process.StandardInput.WriteLine('q')
    [void](Wait-Cli $freshQuit.Host)
    [void](Wait-Cli $freshQuit.Guest 1)
    Assert-Check (-not (Test-Path -LiteralPath $freshHostSave) -and -not (Test-Path -LiteralPath $freshGuestSave)) '无档案的中断对局创建了存档。'
    $checks.Add('无档案的握手和中断完全不创建存档目录')

    $freshPair = Start-Pair '07-default-network' $freshHostSave $freshGuestSave -Auto
    $freshHost = Wait-Cli $freshPair.Host
    $freshGuest = Wait-Cli $freshPair.Guest
    [void](Assert-RatedResult $freshHostSave $freshHost '房主' 1500 0 1500 1)
    [void](Assert-RatedResult $freshGuestSave $freshGuest '客机' 1500 0 1500 1)
    $checks.Add('无评级档案的双方按1500分0局开始，完局建立默认评级并仅计一局')

    $lockedPath = Join-Path $hostSave 'profile.json'
    $beforeLocked = Fingerprint $hostSave
    $held = [IO.File]::Open($lockedPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
    try { $failedLoad = Invoke-Cli '08-load-failure' @('--host', '0', '--save-dir', $hostSave) 1 }
    finally { $held.Dispose() }
    Assert-Check ($failedLoad.Err.StartsWith('错误：') -and -not $failedLoad.Out.Contains('监听端口：')) '评级读取失败没有在启动联网前给出中文诊断。'
    Assert-Check ((Fingerprint $hostSave) -ceq $beforeLocked) '评级读取失败改变了存档。'
    $checks.Add('评级档案读取失败沿用中文诊断，且不开始监听或修改存档')

    [IO.File]::WriteAllText((Join-Path $runDirectory 'report.json'),
        (@{ status = 'PASS'; checks = $checks.ToArray(); host = $hostRating; guest = $guestRating } | ConvertTo-Json -Depth 8), $utf8)
    Write-Output "PASS：$($checks.Count) 组评级CLI检查；证据：$runDirectory"
}
catch {
    [IO.File]::WriteAllText((Join-Path $runDirectory 'report.json'),
        (@{ status = 'FAIL'; error = $_.Exception.Message; checks = $checks.ToArray() } | ConvertTo-Json -Depth 5), $utf8)
    throw
}
finally {
    foreach ($child in $children) {
        if ($child.Started -and -not $child.Process.HasExited) { $child.Process.Kill($true); [void]$child.Process.WaitForExit(5000) }
        if ($null -ne $child.OutTask) { [void]$child.OutTask.Wait(5000) }
        if ($null -ne $child.ErrTask) { [void]$child.ErrTask.Wait(5000) }
        if ($null -ne $child.OutStream) { $child.OutStream.Dispose() }
        if ($null -ne $child.ErrStream) { $child.ErrStream.Dispose() }
        $child.Process.Dispose()
    }
}
