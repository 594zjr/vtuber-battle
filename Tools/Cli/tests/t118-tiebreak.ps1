#requires -Version 7.0
param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug')

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$cliRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$binary = Join-Path $cliRoot "bin/$Configuration/net8.0"
$cli = Join-Path $binary 'Cli.dll'
$core = Join-Path $binary 'StreamDuel.Core.dll'
foreach ($path in @($cli, $core)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "请先构建 Tools/Cli -c $Configuration；本脚本不自动构建。" }
}
$run = [IO.Path]::GetFullPath((Join-Path $cliRoot ('obj/T118/' + [Guid]::NewGuid().ToString('N'))))
if (-not $run.StartsWith($repository + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw '验收产物必须位于当前工作树。'
}
[void][IO.Directory]::CreateDirectory($run)
$temp = Join-Path $run 'temp'
[void][IO.Directory]::CreateDirectory($temp)
$utf8 = [Text.UTF8Encoding]::new($false, $true)
$checks = [Collections.Generic.List[string]]::new()
$calls = [Collections.Generic.List[object]]::new()
$failure = $null

function Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "$Message 证据目录：$run" }
}

function Invoke-Cli([string]$Name, [string[]]$Parameters, [string[]]$Lines = @(), [int]$Exit = 0) {
    $work = Join-Path $run $Name
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
    $start.Environment['TEMP'] = $temp
    $start.Environment['TMP'] = $temp
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    $started = $false
    try {
        $started = $process.Start()
        Check $started "$Name 启动失败。"
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if ($Lines.Count -gt 0) { $process.StandardInput.Write(($Lines -join "`n") + "`n") }
        $process.StandardInput.Close()
        Check ($process.WaitForExit(60000)) "$Name 超时。"
        Check ($stdout.Wait(5000) -and $stderr.Wait(5000)) "$Name 输出未结束。"
        $result = [pscustomobject]@{ Out = $stdout.Result; Err = $stderr.Result; Exit = $process.ExitCode }
        [IO.File]::WriteAllText((Join-Path $work 'stdout.log'), $result.Out, $utf8)
        [IO.File]::WriteAllText((Join-Path $work 'stderr.log'), $result.Err, $utf8)
        $calls.Add([ordered]@{ name = $Name; exit = $result.Exit; expected = $Exit })
        Check ($result.Exit -eq $Exit) "$Name 退出码应为 $Exit，实际 $($result.Exit)。"
        Check (-not (Test-Path -LiteralPath (Join-Path $work 'saves'))) "$Name 写入了普通玩家档案。"
        if ($Exit -eq 0) { Check ([string]::IsNullOrWhiteSpace($result.Err)) "$Name 成功时出现标准错误。" }
        else { Check ($result.Err -match '错误：|警告：') "$Name 没有中文错误提示。" }
        return $result
    }
    finally {
        if ($started -and -not $process.HasExited) { $process.Kill($true); [void]$process.WaitForExit(5000) }
        $process.Dispose()
    }
}

function Fingerprint([string]$Path) {
    $files = [Collections.Generic.List[string]]::new()
    if (Test-Path -LiteralPath $Path -PathType Leaf) { $files.Add($Path) }
    foreach ($item in Get-ChildItem -LiteralPath ([IO.Path]::GetDirectoryName($Path)) -File -Force) {
        if ($item.FullName.StartsWith($Path + '.tournament.', [StringComparison]::OrdinalIgnoreCase)) { $files.Add($item.FullName) }
    }
    if (Test-Path -LiteralPath ($Path + '.replay-library') -PathType Container) {
        foreach ($item in Get-ChildItem -LiteralPath ($Path + '.replay-library') -File -Force -Recurse) { $files.Add($item.FullName) }
    }
    return (($files | Sort-Object -Unique | ForEach-Object {
        $item = Get-Item -LiteralPath $_ -Force
        $_ + ':' + $item.LastWriteTimeUtc.Ticks + ':' + (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash
    }) -join "`n")
}

function Read-Tournament([string]$Path) {
    return [StreamDuel.Core.Tournament]::FromJson($db, [IO.File]::ReadAllText($Path, $utf8))
}

function Registration {
    return ,@('选手1', $code, '选手2', $code, '')
}

function New-ReadyTournament([string]$Path) {
    $options = [StreamDuel.Core.TournamentOptions]::new()
    $options.Seed = [uint32]118
    $options.AutoTiebreak = $true
    $tournament = [StreamDuel.Core.Tournament]::new($db, 'single', 0, $options)
    $tournament.AddPlayer('选手1', $code)
    $tournament.AddPlayer('选手2', $code)
    $null = $tournament.NextRound()
    [IO.File]::WriteAllText($Path, $tournament.ToJson(), $utf8)
    return $tournament
}

function Play-Game($Battle) {
    $steps = 0
    while ($Battle.Phase -ne [StreamDuel.Core.Phase]::Over) {
        Check (++$steps -le 1000) '检查点对局未在预期上限内结束。'
        if ($Battle.Phase -eq [StreamDuel.Core.Phase]::Replace) {
            for ($side = 0; $side -lt 2 -and $Battle.Phase -eq [StreamDuel.Core.Phase]::Replace; $side++) {
                if ($Battle.Need[$side]) { $Battle.Replace($side, [StreamDuel.Core.Ai]::ChooseReplacement($Battle, $side, [StreamDuel.Core.AiLevel]::Normal)) }
            }
        }
        else {
            $first = [StreamDuel.Core.Ai]::Choose($Battle, 0, [StreamDuel.Core.AiLevel]::Normal)
            $second = [StreamDuel.Core.Ai]::Choose($Battle, 1, [StreamDuel.Core.AiLevel]::Normal)
            $Battle.ResolveTurn($first, $second)
        }
    }
    Check ($Battle.Winner -eq -1) '无伤害夹具没有产生真实平局。'
    return $Battle
}

function Report([string]$Name, [string]$Path) {
    $before = Fingerprint $Path
    $result = Invoke-Cli $Name @('--tournament-report', $Path, '--pack-dir', $external, '--json')
    Check ((Fingerprint $Path) -ceq $before) "$Name 改写了赛事或录像库。"
    return ($result.Out | ConvertFrom-Json -AsHashtable)
}

function Verify-Completed([string]$Name, [string]$Path) {
    $tournament = Read-Tournament $Path
    Check ($tournament.AutoTiebreak -and $tournament.IsFinished -and $null -ne $tournament.Champion) "$Name 未自动决出冠军。"
    $json = [IO.File]::ReadAllText($Path, $utf8) | ConvertFrom-Json -AsHashtable
    Check ($json.version -eq 3 -and $json.autoTiebreak) "$Name 没有保存 v3 自动加赛配置。"
    $report = Report "$Name-report" $Path
    Check ($report.version -eq 2 -and $report.autoTiebreak) "$Name 战报未使用逐局版格式。"
    $matches = @($report.rounds | ForEach-Object { $_.matches } | Where-Object { $null -ne $_.player1 })
    $library = [StreamDuel.Core.ReplayLibrary]::OpenReadOnly($Path + '.replay-library', $policy)
    $records = [Collections.Generic.List[string]]::new()
    $count = 0
    foreach ($pairing in $tournament.Pairings) {
        if ($pairing.IsBye) { continue }
        Check ($pairing.AdvancedBySeed -and $pairing.Games.Count -eq 4) "$Name 未在原场与三场加赛全部平局后按种子晋级。"
        $match = @($matches | Where-Object { $_.pairingId -eq $pairing.Id })[0]
        Check ($match.advancedBySeed -and $match.games.Count -eq 4) "$Name 战报缺少逐局记录或种子晋级标记。"
        $seeds = @($pairing.Games | ForEach-Object { $_.Seed } | Sort-Object -Unique)
        Check ($seeds.Count -eq 4) "$Name 四局种子没有区分。"
        for ($attempt = 0; $attempt -lt 4; $attempt++) {
            $game = $pairing.Games[$attempt]
            $shown = $match.games[$attempt]
            Check ($game.Attempt -eq $attempt -and $game.WinnerSide -eq -1 -and $game.Seed -eq $tournament.GameSeed($pairing, $attempt)) "$Name 真实局次、平局结果或派生种子不正确。"
            Check ($shown.attempt -eq $attempt -and $shown.seed -eq $game.Seed -and $shown.winnerSide -eq -1 -and $shown.recordHash -ceq $game.RecordHash) "$Name 战报逐局字段与赛事记录不一致。"
            Check ($shown.replay.status -ceq 'saved' -and -not [string]::IsNullOrEmpty($shown.replay.id)) "$Name 第 $attempt 局没有有效录像关联。"
            $record = $library.Load($shown.replay.id)
            $recordJson = $record.ToJson()
            $digest = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($utf8.GetBytes($recordJson))).ToLowerInvariant()
            Check ($digest -ceq $game.RecordHash) "$Name 规范录像摘要不匹配。"
            $replayed = [StreamDuel.Core.Replay]::Run($db, $record)
            Check ($replayed.Phase -eq [StreamDuel.Core.Phase]::Over -and $replayed.Winner -eq -1 -and $record.Seed -eq $game.Seed) "$Name 逐局录像不能独立重放成真实平局。"
            $records.Add($recordJson)
            $count++
        }
    }
    Check ($report.coverage.completedGames -eq $count -and $report.coverage.archivedGames -eq $count -and $report.coverage.missingGames -eq 0 -and $report.coverage.invalidGames -eq 0) "$Name 战报覆盖率没有按真实局数计算。"
    $recordFiles = @(Get-ChildItem -LiteralPath ($Path + '.replay-library') -File -Filter '*.json' | Where-Object { $_.Name -cne 'index.json' })
    Check ($recordFiles.Count -eq $count) "$Name 存在重复归档或漏存局次。"
    return ,$records.ToArray()
}

try {
    $assembly = [Reflection.Assembly]::LoadFrom($core)
    Check ([StringComparer]::OrdinalIgnoreCase.Equals([IO.Path]::GetFullPath($assembly.Location), [IO.Path]::GetFullPath($core))) '请在未加载其他 Core 的新 PowerShell 进程运行。'
    $content = Join-Path $binary 'Content'
    $rules = [IO.File]::ReadAllText((Join-Path $content 'StreamDuelRules.json'), $utf8)
    $policy = [StreamDuel.Core.PackPolicy]::Load((Join-Path $content 'PackPolicy.json'))
    $external = Join-Path $run 'draw-fixture'
    [void][IO.Directory]::CreateDirectory($external)
    $skills = [ordered]@{}
    $skillIds = @(1..5 | ForEach-Object { "t118_wait_$_" })
    foreach ($id in $skillIds) { $skills[$id] = @{ name = '安静等待'; type = 'talk'; cat = 'status'; power = 0; pp = 999; effects = @() } }
    $streamers = @(0..5 | ForEach-Object {
        @{ id = "t118_actor_$_"; name = "平局演员$_"; type = 'talk'; persona = 'punctual';
            stats = @{ hp = 400; atk = 200; def = 200; spa = 200; sdef = 200; spd = 200 };
            skills = @($skillIds[0..3]); fifth = $skillIds[4]; blurb = '无伤害验收角色。' }
    })
    $pack = @{ id = 't118_draw'; name = '加赛平局验收'; notice = '原创临时验收包。'; skills = $skills; streamers = $streamers } | ConvertTo-Json -Depth 20
    [IO.File]::WriteAllText((Join-Path $external 'fixture.json'), $pack, $utf8)
    $db = [StreamDuel.Core.ContentDb]::FromJson($rules, $pack, $policy)
    $preset = [StreamDuel.Core.TeamPreset]::new()
    $preset.PackId = $db.PackId
    $preset.Name = '加赛验收队伍'
    foreach ($streamer in $streamers) { $preset.Ids.Add($streamer.id) }
    $code = [StreamDuel.Core.TeamCode]::Encode($preset)

    $missing = Join-Path $run 'not-created/event.json'
    $invalid = @(
        @('--no-auto-tiebreak'), @('--tournament-seed', '1'), @('--tournament-seed'),
        @('--tournament', $missing, '--tournament-seed', ''), @('--tournament', $missing, '--tournament-seed', '-1'),
        @('--tournament', $missing, '--tournament-seed', '4294967296'), @('--tournament', $missing, '--tournament-seed', '1.5'),
        @('--tournament', $missing, '--tournament-seed', '+1'), @('--tournament', $missing, '--tournament-seed', '１'),
        @('--tournament', $missing, '--tournament-seed', '1', '--tournament-seed', '1'),
        @('--tournament', $missing, '--no-auto-tiebreak', '--no-auto-tiebreak'),
        @('--tournament', $missing, '--format', 'single', '--seed', '118'),
        @('--tournament-report', $missing, '--tournament-seed', '1'),
        @('--tournament', $missing, '--tournament-report', $missing, '--no-auto-tiebreak'),
        @('--card', 'bell', '--no-auto-tiebreak'),
        @('--tournament', $missing, '--format', 'single', '--watch', '127.0.0.1:9', '--tournament-seed', '1')
    )
    $branch = @('--branch', (Join-Path $run 'not-created/source.json'), '--turn', '1', '--side', '0')
    foreach ($extra in @(@('--tournament-seed', '118'), @('--no-auto-tiebreak'), @('--tournament', $missing),
        @('--tournament', $missing, '--tournament-seed', '118'), @('--tournament', $missing, '--no-auto-tiebreak'))) {
        $invalid += ,($branch + $extra)
        $invalid += ,($extra + $branch)
    }
    for ($i = 0; $i -lt $invalid.Count; $i++) {
        $result = Invoke-Cli "invalid-$i" $invalid[$i] @() 1
        Check ($result.Out.Length -eq 0) '无效参数进入了交互或对战流程。'
        if ($invalid[$i] -contains '--branch') {
            Check ($result.Err.Contains('--branch 是独立') -and $result.Err.Contains('不能与')) '接手与赛事参数没有由独立模式互斥校验在读取来源之前拒绝。'
        }
    }
    Check (-not (Test-Path -LiteralPath (Split-Path $missing -Parent))) '无效参数创建了赛事目录。'
    foreach ($format in @('roundrobin', 'swiss')) {
        $path = Join-Path $run "invalid-$format.json"
        $null = Invoke-Cli "invalid-config-$format" @('--tournament', $path, '--tournament-seed', '118') @($format) 1
        Check (-not (Test-Path -LiteralPath $path)) '非淘汰赛专用配置在拒绝前写入文件。'
    }
    $help = Invoke-Cli 'help' @('--help')
    Check ($help.Out.Contains('--no-auto-tiebreak') -and $help.Out.Contains('--tournament-seed') -and $help.Out.Contains('手工')) '帮助缺少加赛开关或手工模式边界。'
    $checks.Add('PASS strict_arguments_modes_and_help')

    $singlePath = Join-Path $run 'single.json'
    $singleOut = Invoke-Cli 'single-new-auto' @('--tournament', $singlePath, '--format', 'single', '--auto', '--tournament-seed', '118', '--pack', $db.PackId, '--pack-dir', $external) (Registration)
    Check ($singleOut.Out.Contains('加赛 3 次仍平，按种子顺位晋级') -and [regex]::Matches($singleOut.Out, '自动对局种子：').Count -eq 4) '单败没有依次完成原场和三次真实加赛。'
    $records = Verify-Completed 'single' $singlePath
    $checks.Add('PASS single_four_real_draws_archives_and_report')

    $repeatPath = Join-Path $run 'single-repeat.json'
    $null = Invoke-Cli 'single-repeat-auto' @('--tournament', $repeatPath, '--format', 'single', '--auto', '--tournament-seed', '118', '--pack', $db.PackId, '--pack-dir', $external) (Registration)
    $repeated = Verify-Completed 'single-repeat' $repeatPath
    Check ([IO.File]::ReadAllText($singlePath, $utf8) -ceq [IO.File]::ReadAllText($repeatPath, $utf8)) '同赛事种子两次赛事存档不一致。'
    Check (($records -join "`n") -ceq ($repeated -join "`n")) '同赛事种子两次逐局录像不一致。'
    $before = Fingerprint $singlePath
    $text = Invoke-Cli 'single-report-text' @('--tournament-report', $singlePath, '--pack-dir', $external)
    Check ((Fingerprint $singlePath) -ceq $before -and $text.Out.Contains('加赛 3 次仍平，按种子顺位晋级') -and $text.Out.Contains('原局') -and $text.Out.Contains('加赛 3')) '文本战报未解释各局与种子晋级，或查询改写了文件。'
    $checks.Add('PASS_seed_deterministic_records_and_readonly_text_report')

    $doublePath = Join-Path $run 'double.json'
    $null = Invoke-Cli 'double-new-auto' @('--tournament', $doublePath, '--format', 'double', '--auto', '--pack', $db.PackId, '--pack-dir', $external) (Registration)
    $null = Verify-Completed 'double' $doublePath
    Check ((Read-Tournament $doublePath).Champion -eq 0) '未指定赛事种子时没有按报名顺序晋级。'
    $checks.Add('PASS_double_auto_default_seed_registration_order')

    # 用公开 API 建立两个合法断点：一局已登记，下一局已归档但尚未登记；再交真实 CLI 续赛。
    $resumePath = Join-Path $run 'resume.json'
    $resume = New-ReadyTournament $resumePath
    $pairing = $resume.Pairings[0]
    $library = [StreamDuel.Core.ReplayLibrary]::new($resumePath + '.replay-library', 200, $null, $policy)
    $battle = Play-Game ($resume.CreateGame($pairing))
    $null = $library.SaveTournamentGame($resume, $pairing, $battle, 0)
    $resume.ReportGame($pairing, $battle)
    [IO.File]::WriteAllText($resumePath, $resume.ToJson(), $utf8)
    $battle = Play-Game ($resume.CreateGame($pairing))
    $pendingId = $library.SaveTournamentGame($resume, $pairing, $battle, 1)
    $savedRecords = @{}
    foreach ($entry in $library.List($null)) {
        $savedRecords[$entry.Id] = (Get-FileHash -LiteralPath (Join-Path ($resumePath + '.replay-library') ($entry.Id + '.json')) -Algorithm SHA256).Hash
    }
    Check ($savedRecords.Count -eq 2) '断点没有保留两份独立真实录像。'
    $before = Fingerprint $resumePath
    $readonly = Invoke-Cli 'resume-needs-auto' @('--tournament', $resumePath, '--pack-dir', $external) @('0')
    Check ($readonly.Out.Contains('请加 --auto') -and (Fingerprint $resumePath) -ceq $before) '开启加赛的存档被手工报分或改写。'
    $null = Invoke-Cli 'resume-wrong-seed' @('--tournament', $resumePath, '--pack-dir', $external, '--auto', '--tournament-seed', '119') @() 1
    $null = Invoke-Cli 'resume-disable-refused' @('--tournament', $resumePath, '--pack-dir', $external, '--auto', '--no-auto-tiebreak') @() 1
    Check ((Fingerprint $resumePath) -ceq $before) '续赛冲突参数修改了检查点。'
    $resumedOut = Invoke-Cli 'resume-auto' @('--tournament', $resumePath, '--pack-dir', $external, '--auto', '--tournament-seed', '118')
    Check ([regex]::Matches($resumedOut.Out, '自动对局种子：').Count -eq 3) '续赛没有从第一场加赛继续。'
    $resumed = Verify-Completed 'resumed' $resumePath
    Check ([IO.File]::ReadAllText($singlePath, $utf8) -ceq [IO.File]::ReadAllText($resumePath, $utf8) -and ($records -join "`n") -ceq ($resumed -join "`n")) '断点恢复与不中断结果不一致。'
    foreach ($id in $savedRecords.Keys) {
        Check ((Get-FileHash -LiteralPath (Join-Path ($resumePath + '.replay-library') ($id + '.json')) -Algorithm SHA256).Hash -ceq $savedRecords[$id]) '幂等恢复改写了已存录像或其元数据。'
    }
    $resumedReport = Report 'resumed-idempotent-report' $resumePath
    Check ($resumedReport.rounds[0].matches[0].games[1].replay.id -ceq $pendingId) '归档后未登记的录像没有复用原编号。'
    $checks.Add('PASS_checkpoint_resume_idempotent_archive_and_no_manual_bypass')

    $blockedPath = Join-Path $run 'blocked.json'
    $null = New-ReadyTournament $blockedPath
    $blockedBefore = [IO.File]::ReadAllText($blockedPath, $utf8)
    $blockedLibrary = $blockedPath + '.replay-library'
    [IO.File]::WriteAllText($blockedLibrary, '测试占位：此处必须是目录', $utf8)
    $blocked = Invoke-Cli 'archive-failure' @('--tournament', $blockedPath, '--pack-dir', $external, '--auto') @() 1
    Check ($blocked.Err.Contains('结果未登记') -and [regex]::Matches($blocked.Out, '自动对局种子：').Count -eq 1) '归档失败仍重试或没有明确未登记。'
    Check ([IO.File]::ReadAllText($blockedPath, $utf8) -ceq $blockedBefore -and (Read-Tournament $blockedPath).Pairings[0].Games.Count -eq 0) '归档失败推进了赛事局次。'
    Check ([IO.File]::ReadAllText($blockedLibrary, $utf8) -ceq '测试占位：此处必须是目录') '归档失败覆盖了阻塞文件。'
    Check ([IO.Path]::GetFullPath($blockedLibrary).StartsWith($run + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) '测试清理路径越界。'
    Remove-Item -LiteralPath $blockedLibrary
    $null = Invoke-Cli 'archive-repaired-resume' @('--tournament', $blockedPath, '--pack-dir', $external, '--auto')
    $null = Verify-Completed 'repaired' $blockedPath
    Check ([IO.File]::ReadAllText($singlePath, $utf8) -ceq [IO.File]::ReadAllText($blockedPath, $utf8)) '保存障碍修复后的续赛结果不一致。'
    $checks.Add('PASS_archive_failure_does_not_advance_and_repair_resumes')

    foreach ($format in @('single', 'double')) {
        $path = Join-Path $run "disabled-$format.json"
        $disabled = Invoke-Cli "disabled-$format" @('--tournament', $path, '--format', $format, '--auto', '--no-auto-tiebreak', '--pack', $db.PackId, '--pack-dir', $external) (Registration) 1
        $tournament = Read-Tournament $path
        $json = [IO.File]::ReadAllText($path, $utf8) | ConvertFrom-Json -AsHashtable
        Check (-not $tournament.AutoTiebreak -and $json.version -eq 2 -and $tournament.Pairings[0].Games.Count -eq 0 -and $null -eq $tournament.Pairings[0].WinnerSide) '关闭加赛时没有保留旧待赛语义。'
        Check ($disabled.Err.Contains('本场保持待赛') -and [regex]::Matches($disabled.Out, '自动对局种子：').Count -eq 1 -and -not (Test-Path -LiteralPath ($path + '.replay-library'))) '关闭模式进行了重复加赛或归档了未决平局。'
    }
    $manualPath = Join-Path $run 'manual-default.json'
    $manual = Invoke-Cli 'manual-default-off' @('--tournament', $manualPath, '--format', 'single', '--pack', $db.PackId, '--pack-dir', $external) ((Registration) + @('0'))
    Check (-not (Read-Tournament $manualPath).AutoTiebreak -and (Read-Tournament $manualPath).IsFinished -and $manual.Out.Contains('手工报分模式不生成对战录像') -and -not (Test-Path -LiteralPath ($manualPath + '.replay-library'))) '手工报分默认行为不兼容或虚构了录像。'
    $checks.Add('PASS_disabled_legacy_draw_and_manual_default')
}
catch { $failure = $_.ToString() + "`n" + $_.ScriptStackTrace }
finally {
    $result = [ordered]@{ status = $(if ($null -eq $failure) { 'PASS' } else { 'FAIL' }); checks = @($checks.ToArray()); calls = @($calls.ToArray()); error = $failure }
    [IO.File]::WriteAllText((Join-Path $run 'report.json'), ($result | ConvertTo-Json -Depth 10), $utf8)
}
foreach ($check in $checks) { Write-Host $check }
Write-Host "证据目录：$run"
if ($null -ne $failure) { throw $failure }
