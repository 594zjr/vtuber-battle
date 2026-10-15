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

# 每次使用新目录，复跑不会重置已有档案或删除之前的验收证据。
$runDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ('obj/replay-check/' + [Guid]::NewGuid().ToString('N'))))
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

function Managed-Replays([string]$Directory) {
    $path = Join-Path $Directory 'saves/replays'
    return @(Get-ChildItem -LiteralPath $path -File | Where-Object { $_.Name -match $managedPattern } | Sort-Object Name)
}

function Read-Stats([string]$Directory, [string]$Pack = 'original') {
    $profile = [IO.File]::ReadAllText((Join-Path $Directory 'saves/profile.json')) | ConvertFrom-Json
    return $profile.packs.$Pack
}

function Assert-Stats($Stats, [int]$Games, $Baseline) {
    foreach ($name in @('wins', 'losses', 'draws')) {
        Assert-Check ($Stats.$name -eq $Baseline.$name * $Games) "累计 $name 不正确。"
    }
    Assert-Check (@($Stats.picks.PSObject.Properties).Count -eq 5) '禁用后的五位主播才应计入选用次数。'
    foreach ($pick in $Stats.picks.PSObject.Properties) {
        Assert-Check ($pick.Value -eq $Games) '选用次数未正确累计。'
        Assert-Check ($Stats.pickWins.($pick.Name) -eq $Stats.wins) '选用胜场未正确累计。'
    }
}

function Save-Fingerprint([string]$Directory) {
    $path = Join-Path $Directory 'saves'
    if (-not (Test-Path -LiteralPath $path)) { return '' }
    return (@(Get-ChildItem -LiteralPath $path -File -Recurse | Sort-Object FullName | ForEach-Object {
        $_.FullName + ':' + (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    }) -join "`n")
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

function Summary-Text([string]$Output) {
    $match = [regex]::Match($Output, '(?ms)^对战统计\n.*?(?=^回放已保存到：|\z)')
    Assert-Check $match.Success '缺少结算统计。'
    return $match.Value.TrimEnd()
}

function Replay-Turns([string]$Output) {
    return @([regex]::Matches($Output, '【第 ([0-9]+) 回合 ·') | ForEach-Object { [int]$_.Groups[1].Value } | Select-Object -Unique)
}

function Seed-Replays([string]$Directory, [int]$Count, [string]$Json) {
    $path = Join-Path $Directory 'saves/replays'
    [void][IO.Directory]::CreateDirectory($path)
    for ($i = 0; $i -lt $Count; $i++) {
        [IO.File]::WriteAllText((Join-Path $path ('20000101_000000_{0:D7}_original.json' -f $i)), $Json, $utf8)
    }
    return $path
}

try {
    $firstDirectory = New-Case 'same-state-a'
    $secondDirectory = New-Case 'same-state-b'
    $first = Invoke-CliCheck '01-first' $firstDirectory $gameArguments
    $second = Invoke-CliCheck '02-same-state' $secondDirectory $gameArguments
    Assert-Check ($first.Out -ceq $second.Out -and $first.Err -ceq $second.Err) '相同初始档案的完整输出不一致。'
    Assert-Check ($first.Err.Length -eq 0) '正常对局不应输出错误。'
    $firstFiles = @(Managed-Replays $firstDirectory)
    $secondFiles = @(Managed-Replays $secondDirectory)
    Assert-Check ($firstFiles.Count -eq 1 -and $secondFiles.Count -eq 1) '每局应新增一份带时间的回放。'
    $replayPath = $firstFiles[0].FullName
    $recordJson = [IO.File]::ReadAllText($replayPath)
    Assert-Check ($recordJson -ceq [IO.File]::ReadAllText($secondFiles[0].FullName)) '相同对局的回放 JSON 不一致。'
    $record = $recordJson | ConvertFrom-Json
    Assert-Check (($record.events[0..3].type -join ',') -eq 'ban,ban,lead,lead') '记录未按禁用、首发顺序保存。'
    $baselineStats = Read-Stats $firstDirectory
    Assert-Check (($baselineStats.wins + $baselineStats.losses + $baselineStats.draws) -eq 1) '首局应记录一次战绩。'
    Assert-Stats $baselineStats 1 $baselineStats
    $bannedIndex = @($record.events | Where-Object { $_.type -eq 'ban' -and $_.side -eq 1 })[0].to
    Assert-Check ($null -eq $baselineStats.picks.PSObject.Properties[$record.teams[0][$bannedIndex]]) '被禁主播不能计入选用次数。'
    $checks.Add('相同初始档案：完整输出、回放 JSON 一致；禁用后五位选用统计')

    $accumulated = Invoke-CliCheck '03-accumulate' $firstDirectory $gameArguments
    Assert-Stats (Read-Stats $firstDirectory) 2 $baselineStats
    Assert-Check (@(Managed-Replays $firstDirectory).Count -eq 2) '共享目录第二局未增加回放。'
    Assert-Check ($first.Out -cne $accumulated.Out) '连续两局必须如实显示累计战绩变化。'
    Assert-Check (($first.Out -split '本包战绩：')[0] -ceq ($accumulated.Out -split '本包战绩：')[0]) '档案累计影响了战斗输出。'
    $checks.Add('共享目录连续两局：战绩累计两次，战斗内容一致')

    $beforeReplay = Save-Fingerprint $firstDirectory
    $autoReplay = Invoke-CliCheck '04-replay-auto' $firstDirectory @('--replay', $replayPath, '--auto')
    Assert-Check ((Battle-Lines $first.Out) -ceq (Battle-Lines $autoReplay.Out)) '回放日志与原对局不一致。'
    Assert-Check ((Summary-Text $first.Out) -ceq (Summary-Text $autoReplay.Out)) '回放结算统计与原对局不一致。'
    Assert-Check ((Save-Fingerprint $firstDirectory) -ceq $beforeReplay) '回放改变了档案或回放文件。'
    $checks.Add('original 自动回看：完整日志与结算一致，无存档写入')

    foreach ($pack in @('fan_asoul_vr', 'fan_holo_niji')) {
        $directory = New-Case $pack
        $game = Invoke-CliCheck ("05-$pack-game") $directory ($gameArguments + @('--pack', $pack))
        $saved = @(Managed-Replays $directory)[0].FullName
        $before = Save-Fingerprint $directory
        $replay = Invoke-CliCheck ("06-$pack-replay") $directory @('--replay', $saved, '--auto')
        Assert-Check ((Battle-Lines $game.Out) -ceq (Battle-Lines $replay.Out)) "$pack 回放日志不一致。"
        Assert-Check ((Summary-Text $game.Out) -ceq (Summary-Text $replay.Out)) "$pack 回放结算不一致。"
        Assert-Check (($game.Out -split "`n")[1] -ceq ($replay.Out -split "`n")[1]) "$pack 未自动选中记录中的内容包。"
        Assert-Check ((Save-Fingerprint $directory) -ceq $before) "$pack 回放改变了存档。"
        $checks.Add("$pack 自动读取内容包、回看日志与结算一致、不写存档")
    }

    $externalDirectory = New-Case 'external-pack'
    $packDirectory = Join-Path $externalDirectory 'packs'
    [void][IO.Directory]::CreateDirectory($packDirectory)
    $externalPack = [IO.File]::ReadAllText((Join-Path (Split-Path $cliDll) 'Content/Packs/original.json')) | ConvertFrom-Json
    $externalPack.id = '../自制'
    $externalPack.name = '外部回放验收包'
    [IO.File]::WriteAllText((Join-Path $packDirectory 'custom.json'), ($externalPack | ConvertTo-Json -Depth 64), $utf8)
    $externalGame = Invoke-CliCheck '06-external-game' $externalDirectory ($gameArguments + @('--pack', '../自制', '--pack-dir', 'packs'))
    $externalFiles = @(Managed-Replays $externalDirectory)
    Assert-Check ($externalFiles.Count -eq 1 -and $externalFiles[0].Name -match '_id-[0-9a-f]{64}\.json$') '特殊内容包 id 没有保存为目录内的安全摘要文件名。'
    $externalRecord = [IO.File]::ReadAllText($externalFiles[0].FullName) | ConvertFrom-Json
    Assert-Check ($externalRecord.packId -ceq '../自制') '回放 JSON 未保留外部包原始 id。'
    Assert-Check ($externalFiles[0].DirectoryName -eq [IO.Path]::GetFullPath((Join-Path $externalDirectory 'saves/replays'))) '特殊包 id 影响了保存目录。'
    $externalBefore = Save-Fingerprint $externalDirectory
    $externalReplay = Invoke-CliCheck '06-external-replay' $externalDirectory @('--replay', $externalFiles[0].FullName, '--auto', '--pack-dir', 'packs')
    Assert-Check ((Battle-Lines $externalGame.Out) -ceq (Battle-Lines $externalReplay.Out)) '外部包回放日志不一致。'
    Assert-Check ((Summary-Text $externalGame.Out) -ceq (Summary-Text $externalReplay.Out)) '外部包回放结算不一致。'
    $externalMissing = Invoke-CliCheck '06-external-missing' $externalDirectory @('--replay', $externalFiles[0].FullName, '--auto') '' 1
    Assert-Check ($externalMissing.Err.Contains('找不到内容包')) '未提供外部包目录时没有清楚报错。'
    $list = Invoke-CliCheck '06-list-packs' $externalDirectory @('--list-packs', '--pack-dir', 'packs')
    Assert-Check ($list.Out.Contains('../自制') -and $list.Out.Contains('外部回放验收包')) '列表缺少可用的外部内容包。'
    Assert-Check ((Save-Fingerprint $externalDirectory) -ceq $externalBefore) '外部包回放或列包操作改变了存档。'
    $checks.Add('外部包特殊 id：安全文件名、原 id 保留、pack-dir 回放、列包与缺包错误')

    $manualDirectory = New-Case 'replay-input'
    $qReplay = Invoke-CliCheck '07-replay-q' $manualDirectory @('--replay', $replayPath) "q`n"
    $eofReplay = Invoke-CliCheck '08-replay-eof' $manualDirectory @('--replay', $replayPath)
    $nextReplay = Invoke-CliCheck '09-replay-next-q' $manualDirectory @('--replay', $replayPath) "`nq`n"
    $invalidReplay = Invoke-CliCheck '10-replay-invalid-next-q' $manualDirectory @('--replay', $replayPath) "x`n`nq`n"
    Assert-Check ($qReplay.Out.Contains('已退出回看。') -and $eofReplay.Out.Contains('已退出回看。')) 'q/EOF 没有退出回看。'
    Assert-Check (@(Replay-Turns $qReplay.Out).Count -eq 1 -and @(Replay-Turns $eofReplay.Out).Count -eq 1) 'q/EOF 播放了多余回合。'
    Assert-Check (@(Replay-Turns $nextReplay.Out).Count -eq 2) '回车没有前进一回合。'
    Assert-Check ($invalidReplay.Out.Contains('请直接按回车') -and (Battle-Lines $invalidReplay.Out) -ceq (Battle-Lines $nextReplay.Out)) '非法回看输入改变了播放进度。'
    foreach ($result in @($qReplay, $eofReplay, $nextReplay, $invalidReplay)) {
        Assert-Check (-not $result.Out.Contains('对战统计')) '提前退出回看不应提前显示最终统计。'
    }
    $completeReplay = Invoke-CliCheck '11-replay-manual-complete' $manualDirectory @('--replay', $replayPath) ("`n" * 140)
    Assert-Check ((Battle-Lines $completeReplay.Out) -ceq (Battle-Lines $autoReplay.Out)) '手动逐回合播放与自动播放日志不一致。'
    Assert-Check ((Summary-Text $completeReplay.Out) -ceq (Summary-Text $autoReplay.Out)) '手动完整回看缺少统计。'
    Assert-Check (-not (Test-Path -LiteralPath (Join-Path $manualDirectory 'saves'))) '回看创建了默认存档。'
    $checks.Add('手动回看：首回合、回车、非法输入、q、EOF、完整播放')

    $selectionDirectory = New-Case 'manual-selection'
    $selection = Invoke-CliCheck '12-ban-lead-invalid-six' $selectionDirectory @('--ban', '--lead', '--seed', '1') "1`n6`n1`nq`n"
    $selectionControl = Invoke-CliCheck '13-ban-lead-control' $selectionDirectory @('--ban', '--lead', '--seed', '1') "1`n1`nq`n"
    Assert-Check ($selection.Out.IndexOf('===== 开局禁用 =====') -lt $selection.Out.IndexOf('===== 选择首发 =====')) '首发出现在禁用之前。'
    $leadList = [regex]::Match($selection.Out, '(?s)选择我方首发（按当前剩余队伍号位）：(?<items>.*?)请选择首发号位')
    Assert-Check ($leadList.Success -and [regex]::Matches($leadList.Groups['items'].Value, '\[[1-5]\]').Count -eq 5 -and -not $leadList.Value.Contains('[6]')) '禁用后的首发列表不是剩余五位。'
    Assert-Check ($selection.Out.Contains('该号位不存在，请选择列表中的队友。')) '首发号位 6 没有重新提示。'
    Assert-Check ((Battle-Lines $selection.Out) -ceq (Battle-Lines $selectionControl.Out)) '非法首发输入消耗了随机数或改变开局。'
    Assert-Check ($selection.Out.Contains('已退出，对局未完成。')) '对战阶段 q 未退出。'
    $null = Invoke-CliCheck '14-ban-q' $selectionDirectory @('--ban', '--lead') "q`n"
    $null = Invoke-CliCheck '15-lead-q' $selectionDirectory @('--ban', '--lead') "1`nq`n"
    $null = Invoke-CliCheck '16-game-eof' $selectionDirectory @('--ban', '--lead')
    Assert-Check (-not (Test-Path -LiteralPath (Join-Path $selectionDirectory 'saves'))) '未完成的禁用、首发或对局创建了存档。'
    $checks.Add('禁用→五位首发→对战；号位 6 重试不耗随机数；q/EOF 不存档')

    $retentionDirectory = New-Case 'retention'
    $retentionPath = Seed-Replays $retentionDirectory 51 $recordJson
    $foreignPath = Join-Path $retentionPath 'foreign.json'
    [IO.File]::WriteAllText($foreignPath, '{"keep":true}', $utf8)
    [IO.File]::WriteAllText((Join-Path $retentionPath '.replay.json.tmp'), 'interrupted replay', $utf8)
    [IO.File]::WriteAllText((Join-Path $retentionDirectory 'saves/.profile.json.tmp'), 'interrupted profile', $utf8)
    $null = Invoke-CliCheck '17-retention' $retentionDirectory $gameArguments
    Assert-Check (@(Managed-Replays $retentionDirectory).Count -eq 50) '自动回放未保留 50 份。'
    Assert-Check (-not (Test-Path -LiteralPath (Join-Path $retentionPath '20000101_000000_0000000_original.json')) -and -not (Test-Path -LiteralPath (Join-Path $retentionPath '20000101_000000_0000001_original.json'))) '没有优先删除最旧的自动回放。'
    Assert-Check ([IO.File]::ReadAllText($foreignPath) -ceq '{"keep":true}') '清理误删或覆盖外来 JSON。'
    Assert-Check (@(Get-ChildItem -LiteralPath (Join-Path $retentionDirectory 'saves') -File -Recurse -Force | Where-Object { $_.Name.EndsWith('.tmp') }).Count -eq 0) '成功保存后遗留临时文件。'
    $checks.Add('回放保留 50、按时间清理、保护外来 JSON、恢复陈旧临时文件')

    $lockedDirectory = New-Case 'retention-lock'
    $lockedPath = Seed-Replays $lockedDirectory 50 $recordJson
    $oldest = Join-Path $lockedPath '20000101_000000_0000000_original.json'
    $heldLock = [IO.File]::Open($oldest, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        $locked = Invoke-CliCheck '18-retention-locked' $lockedDirectory $gameArguments '' 1
        Assert-Check (@(Managed-Replays $lockedDirectory).Count -eq 51) '清理失败时未保留新回放。'
        Assert-Stats (Read-Stats $lockedDirectory) 1 $baselineStats
        Assert-Check ($locked.Err.Length -gt 0 -and $locked.Out.Contains('本包战绩：')) '清理失败未告知或未正常记录战绩。'
    }
    finally { $heldLock.Dispose() }
    $null = Invoke-CliCheck '19-retention-recovered' $lockedDirectory $gameArguments
    Assert-Check (@(Managed-Replays $lockedDirectory).Count -eq 50) '解除占用后未恢复保留 50 份。'
    Assert-Stats (Read-Stats $lockedDirectory) 2 $baselineStats
    $checks.Add('旧回放被占用：新回放与战绩保存、exit 1 告警；解除后恢复 50')

    $errorDirectory = New-Case 'invalid-replays'
    $badJson = Join-Path $errorDirectory 'bad.json'
    $traversal = Join-Path $errorDirectory 'traversal.json'
    $emptyTeams = Join-Path $errorDirectory 'empty-teams.json'
    [IO.File]::WriteAllText($badJson, '{', $utf8)
    [IO.File]::WriteAllText($traversal, $recordJson.Replace('"packId":"original"', '"packId":"../escape"'), $utf8)
    [IO.File]::WriteAllText($emptyTeams, '{"seed":1,"packId":"original","teams":[[],[]],"events":[]}', $utf8)
    $errorCases = @(
        @{ Name = '20-missing'; Parameters = @('--replay', (Join-Path $errorDirectory 'missing.json'), '--auto') },
        @{ Name = '21-bad-json'; Parameters = @('--replay', $badJson, '--auto') },
        @{ Name = '22-path-traversal'; Parameters = @('--replay', $traversal, '--auto') },
        @{ Name = '23-empty-teams'; Parameters = @('--replay', $emptyTeams, '--auto') },
        @{ Name = '24-replay-hash'; Parameters = @('--replay', $replayPath, '--hash') }
    )
    foreach ($case in $errorCases) {
        $result = Invoke-CliCheck $case.Name $errorDirectory $case.Parameters '' 1
        Assert-Check ($result.Err.StartsWith('错误：')) "$($case.Name) 未提供中文错误。"
    }
    Assert-Check (-not (Test-Path -LiteralPath (Join-Path $errorDirectory 'saves'))) '非法回放创建了存档。'
    $checks.Add('缺文件、坏 JSON、不存在的路径形式包 id、空队伍、replay+hash：中文错误且 exit 1')

    $help = Invoke-CliCheck '25-help' $errorDirectory @('--help')
    foreach ($parameter in @('--pack', '--pack-dir', '--list-packs', '--level', '--seed', '--team', '--auto', '--ban', '--lead', '--hash', '--save-dir', '--replay', '--help')) {
        Assert-Check ($help.Out.Contains($parameter)) "帮助缺少 $parameter。"
    }
    $checks.Add('所有参数均有帮助说明')

    $blockedDirectory = New-Case 'replay-path-file'
    $blockedSavePath = Join-Path $blockedDirectory 'saves'
    [void][IO.Directory]::CreateDirectory($blockedSavePath)
    $blockedReplayPath = Join-Path $blockedSavePath 'replays'
    [IO.File]::WriteAllText($blockedReplayPath, 'keep blocked replay path', $utf8)
    $blocked = Invoke-CliCheck '26-replay-path-file' $blockedDirectory $gameArguments '' 1
    Assert-Stats (Read-Stats $blockedDirectory) 1 $baselineStats
    Assert-Check ($blocked.Out.Contains('对战统计') -and $blocked.Out.Contains('本包战绩：') -and $blocked.Err.Length -gt 0) '回放路径被文件占用时未完成结算、记录战绩或告知失败。'
    Assert-Check (-not $blocked.Out.Contains('回放已保存到：')) '回放路径被文件占用时谎报保存成功。'
    Assert-Check ([IO.File]::ReadAllText($blockedReplayPath) -ceq 'keep blocked replay path') '保存回放覆盖了占用路径的原文件。'
    $checks.Add('回放目录被普通文件占用：exit 1、战绩累计一次、不谎报保存成功')

    $writeLockedDirectory = New-Case 'replay-save-lock'
    $writeLockedPath = Join-Path $writeLockedDirectory 'saves/replays'
    [void][IO.Directory]::CreateDirectory($writeLockedPath)
    $heldReplayLock = [IO.File]::Open((Join-Path $writeLockedPath '.replays.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try {
        $writeLocked = Invoke-CliCheck '27-replay-save-locked' $writeLockedDirectory $gameArguments '' 1
        Assert-Stats (Read-Stats $writeLockedDirectory) 1 $baselineStats
        Assert-Check (@(Managed-Replays $writeLockedDirectory).Count -eq 0) '独占回放锁期间意外写入回放。'
        Assert-Check ($writeLocked.Out.Contains('对战统计') -and $writeLocked.Out.Contains('本包战绩：') -and $writeLocked.Err.Length -gt 0) '回放锁被占用时未完成结算、记录战绩或告知失败。'
        Assert-Check (-not $writeLocked.Out.Contains('回放已保存到：')) '回放锁被占用时谎报保存成功。'
    }
    finally { $heldReplayLock.Dispose() }
    $writeRecovered = Invoke-CliCheck '28-replay-save-recovered' $writeLockedDirectory $gameArguments
    Assert-Stats (Read-Stats $writeLockedDirectory) 2 $baselineStats
    Assert-Check (@(Managed-Replays $writeLockedDirectory).Count -eq 1) '释放回放锁后未保存且仅保存新对局的一份回放。'
    Assert-Check ($writeRecovered.Err.Length -eq 0 -and $writeRecovered.Out.Contains('回放已保存到：')) '释放回放锁后未恢复正常保存。'
    $checks.Add('回放锁独占：exit 1、战绩一次、无回放；释放后战绩两次且一份回放')

    $customDirectory = New-Case 'custom-save-directory'
    $customParent = Join-Path $customDirectory 'custom save space'
    $customSavePath = Join-Path $customParent 'saves'
    $customGame = Invoke-CliCheck '29-custom-save-game' $customDirectory ($gameArguments + @('--save-dir', $customSavePath))
    Assert-Stats (Read-Stats $customParent) 1 $baselineStats
    $customReplays = @(Managed-Replays $customParent)
    Assert-Check ($customReplays.Count -eq 1) '带空格的自定义保存目录未保存一份回放。'
    Assert-Check (-not (Test-Path -LiteralPath (Join-Path $customDirectory 'saves'))) '自定义保存目录的新对局误写默认目录。'
    $customFingerprint = Save-Fingerprint $customParent
    $customReplay = Invoke-CliCheck '30-custom-save-replay' $customDirectory @('--replay', $customReplays[0].FullName, '--auto', '--save-dir', $customSavePath)
    Assert-Check ((Battle-Lines $customReplay.Out) -ceq (Battle-Lines $customGame.Out)) '自定义目录回看日志与原对局不同。'
    Assert-Check ((Summary-Text $customReplay.Out) -ceq (Summary-Text $customGame.Out)) '自定义目录回看统计与原对局不同。'
    Assert-Check ((Save-Fingerprint $customParent) -ceq $customFingerprint) '回看修改了自定义目录的档案或回放。'
    Assert-Check (-not (Test-Path -LiteralPath (Join-Path $customDirectory 'saves'))) '自定义保存目录的回看误写默认目录。'
    $checks.Add('带空格 --save-dir：新局正确保存、回看一致且档案和回放不变')

    $report = [ordered]@{
        status = 'PASS'
        stdout_sha256 = (Get-FileHash -LiteralPath (Join-Path $runDirectory '01-first.stdout.log') -Algorithm SHA256).Hash
        replay_sha256 = (Get-FileHash -LiteralPath $replayPath -Algorithm SHA256).Hash
        checks = $checks.ToArray()
        invocations = $invocations.ToArray()
    }
    [IO.File]::WriteAllText((Join-Path $runDirectory 'report.json'), ($report | ConvertTo-Json -Depth 8), $utf8)
    Write-Output "PASS：$($checks.Count) 组回放与 CLI 集成检查；证据：$runDirectory"
}
catch {
    $report = [ordered]@{ status = 'FAIL'; error = $_.Exception.Message; checks = $checks.ToArray(); invocations = $invocations.ToArray() }
    [IO.File]::WriteAllText((Join-Path $runDirectory 'report.json'), ($report | ConvertTo-Json -Depth 8), $utf8)
    throw
}
