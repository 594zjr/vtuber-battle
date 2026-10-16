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
$run = [IO.Path]::GetFullPath((Join-Path $cliRoot ('obj/T127/' + [Guid]::NewGuid().ToString('N'))))
if (-not $run.StartsWith($repository + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw '验收产物必须位于当前工作树。' }
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
    $start.Environment['DOTNET_CLI_HOME'] = $temp
    $start.Environment['DOTNET_SKIP_FIRST_TIME_EXPERIENCE'] = '1'
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
        Check ($process.WaitForExit(90000)) "$Name 超时。"
        Check ($stdout.Wait(5000) -and $stderr.Wait(5000)) "$Name 输出未结束。"
        $result = [pscustomobject]@{ Out = $stdout.Result; Err = $stderr.Result; Exit = $process.ExitCode }
        [IO.File]::WriteAllText((Join-Path $work 'stdout.log'), $result.Out, $utf8)
        [IO.File]::WriteAllText((Join-Path $work 'stderr.log'), $result.Err, $utf8)
        $calls.Add([ordered]@{ name = $Name; exit = $result.Exit; expected = $Exit })
        Check ($result.Exit -eq $Exit) "$Name 退出码应为 $Exit，实际 $($result.Exit)。"
        Check (-not (Test-Path -LiteralPath (Join-Path $work 'saves'))) "$Name 写入了普通玩家档案。"
        if ($Exit -eq 0) { Check ([string]::IsNullOrWhiteSpace($result.Err)) "$Name 成功时出现标准错误。" }
        else { Check ($result.Err.Contains('错误：')) "$Name 没有中文错误提示。" }
        return $result
    }
    finally {
        if ($started -and -not $process.HasExited) { $process.Kill($true); [void]$process.WaitForExit(5000) }
        $process.Dispose()
    }
}

function Fingerprint([string]$Path) {
    $items = @()
    foreach ($candidate in @($Path, ($Path + '.tournament.lock'), ($Path + '.replay-library'))) {
        if (Test-Path -LiteralPath $candidate) {
            $items += Get-Item -LiteralPath $candidate -Force
            if (Test-Path -LiteralPath $candidate -PathType Container) {
                $items += @(Get-ChildItem -LiteralPath $candidate -Recurse -Force)
            }
        }
    }
    $parent = Split-Path $Path -Parent
    if (Test-Path -LiteralPath $parent -PathType Container) {
        $prefix = [IO.Path]::GetFileName($Path) + '.tournament.tmp'
        $items += @(Get-ChildItem -LiteralPath $parent -Force | Where-Object { $_.Name.StartsWith($prefix, [StringComparison]::Ordinal) })
    }
    return (($items | Sort-Object FullName -Unique | ForEach-Object {
        $value = $_.FullName + ':' + $_.Attributes + ':' + $_.LastWriteTimeUtc.Ticks
        if ($_.PSIsContainer) { $value + ':directory' }
        else { $value + ':' + (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    }) -join "`n")
}

function Replay-Fingerprint([string]$Path) {
    $item = Get-Item -LiteralPath $Path -Force
    return ($item.LastWriteTimeUtc.Ticks.ToString() + ':' + (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash)
}

function Read-Tournament([string]$Path, $Database) {
    return [StreamDuel.Core.Tournament]::FromJson($Database, [IO.File]::ReadAllText($Path, $utf8))
}

function Registration([string]$First, [string]$Second) { return ,@('选手甲', $First, '选手乙', $Second, '') }

function New-ReadyTournament([string]$Path, $Database, [string]$First, [string]$Second, [string]$Format = 'single', $Ruleset = $ranked) {
    $options = if ($Format -eq 'roundrobin') { $null } else { [StreamDuel.Core.TournamentOptions]::new() }
    if ($null -ne $options) { $options.Seed = [uint32]127; $options.AutoTiebreak = $true }
    $tournament = [StreamDuel.Core.Tournament]::new($Database, $Format, 0, $options, $Ruleset)
    $tournament.AddPlayer('选手甲', $First)
    $tournament.AddPlayer('选手乙', $Second)
    $null = $tournament.NextRound()
    [IO.File]::WriteAllText($Path, $tournament.ToJson(), $utf8)
    return $tournament
}

function Prepare-Game($Battle) {
    while ($Battle.Phase -eq [StreamDuel.Core.Phase]::Ban -or $Battle.Phase -eq [StreamDuel.Core.Phase]::Lead) {
        if ($Battle.Phase -eq [StreamDuel.Core.Phase]::Ban) {
            for ($side = 0; $side -lt 2; $side++) { $Battle.ChooseBan($side, [StreamDuel.Core.Ai]::ChooseBan($Battle, $side)) }
        } else {
            for ($side = 0; $side -lt 2; $side++) { $Battle.ChooseLead($side, [StreamDuel.Core.Ai]::ChooseLead($Battle, $side)) }
        }
    }
    return $Battle
}

function Play-Game($Battle) {
    $Battle = Prepare-Game $Battle
    $steps = 0
    while ($Battle.Phase -ne [StreamDuel.Core.Phase]::Over) {
        Check (++$steps -le 1000) '对局没有在预期上限结束。'
        if ($Battle.Phase -eq [StreamDuel.Core.Phase]::Replace) {
            for ($side = 0; $side -lt 2 -and $Battle.Phase -eq [StreamDuel.Core.Phase]::Replace; $side++) {
                if ($Battle.Need[$side]) { $Battle.Replace($side, [StreamDuel.Core.Ai]::ChooseReplacement($Battle, $side, [StreamDuel.Core.AiLevel]::Normal)) }
            }
        } else {
            $first = [StreamDuel.Core.Ai]::Choose($Battle, 0, [StreamDuel.Core.AiLevel]::Normal)
            $second = [StreamDuel.Core.Ai]::Choose($Battle, 1, [StreamDuel.Core.AiLevel]::Normal)
            $Battle.ResolveTurn($first, $second)
        }
    }
    return $Battle
}

function Save-Game([string]$Path, $Tournament, $Battle) {
    $pairing = $Tournament.Pairings[0]
    $library = [StreamDuel.Core.ReplayLibrary]::new($Path + '.replay-library', 200, $null, $policy)
    $id = $library.SaveTournamentGame($Tournament, $pairing, $Battle, $pairing.Games.Count)
    $Tournament.ReportGame($pairing, $Battle)
    [IO.File]::WriteAllText($Path, $Tournament.ToJson(), $utf8)
    return $id
}

function Report([string]$Name, [string]$Path) {
    $before = Fingerprint $Path
    $result = Invoke-Cli $Name @('--tournament-report', $Path, '--pack-dir', $external, '--json')
    Check ((Fingerprint $Path) -ceq $before) "$Name 改写了赛事或录像库。"
    return ($result.Out | ConvertFrom-Json -AsHashtable)
}

function Verify-Games([string]$Name, [string]$Path, $Database, [int]$PerPairing, [bool]$Finished, [bool]$BySeed = $false) {
    $tournament = Read-Tournament $Path $Database
    Check ($tournament.HasRuleset -and $tournament.AutoTiebreak -and $tournament.IsFinished -eq $Finished) "$Name 逐局配置或结束状态不正确。"
    $json = [IO.File]::ReadAllText($Path, $utf8) | ConvertFrom-Json -AsHashtable
    Check ($json.version -eq 5 -and $json.ruleset.bestOf -eq 3) "$Name 未保存 v5 规则快照。"
    $report = Report "$Name-report" $Path
    Check ($report.version -eq 3 -and $report.bestOf -eq 3 -and $report.ruleset.banOne -and $report.ruleset.leadPick) "$Name 战报没有逐局赛制配置。"
    $matches = @($report.rounds | ForEach-Object { $_.matches } | Where-Object { $null -ne $_.player1 })
    $library = [StreamDuel.Core.ReplayLibrary]::OpenReadOnly($Path + '.replay-library', $policy)
    $records = [Collections.Generic.List[string]]::new()
    $count = 0
    foreach ($pairing in $tournament.Pairings) {
        if ($pairing.IsBye) { continue }
        Check ($pairing.Games.Count -eq $PerPairing -and $pairing.AdvancedBySeed -eq $BySeed) "$Name 局数或种子晋级不正确。"
        $match = @($matches | Where-Object { $_.pairingId -eq $pairing.Id })[0]
        Check ($match.games.Count -eq $PerPairing -and $match.advancedBySeed -eq $BySeed) "$Name 战报遗漏真实局次。"
        Check ((@($match.wins)[0] + @($match.wins)[1] + $match.draws) -eq $PerPairing) "$Name 战报胜场和平局计数不一致。"
        for ($sequence = 0; $sequence -lt $PerPairing; $sequence++) {
            $game = $pairing.Games[$sequence]
            $shown = $match.games[$sequence]
            Check ($game.Attempt -eq $sequence -and $shown.gameNumber -eq $sequence + 1 -and $game.Seed -eq $tournament.GameSeed($pairing, $sequence)) "$Name 局序或派生种子不正确。"
            Check ($shown.replay.status -ceq 'saved' -and $shown.recordHash -ceq $game.RecordHash -and $shown.winnerSide -eq $game.WinnerSide) "$Name 逐局录像未关联正确。"
            Check (($sequence -lt 3 -and $null -eq $shown.tiebreakNumber) -or ($sequence -ge 3 -and $shown.tiebreakNumber -eq $sequence - 2)) "$Name 混淆了规定局与加赛编号。"
            $record = $library.Load($shown.replay.id)
            $recordJson = $record.ToJson()
            $digest = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($utf8.GetBytes($recordJson))).ToLowerInvariant()
            Check ($digest -ceq $game.RecordHash -and $record.BanOne -and $record.LeadPick) "$Name 录像摘要或每局禁用/首发配置不符。"
            Check (@($record.Events | Where-Object Type -eq ([StreamDuel.Core.BattleRecordEventType]::Ban)).Count -eq 2 -and @($record.Events | Where-Object Type -eq ([StreamDuel.Core.BattleRecordEventType]::Lead)).Count -eq 2) "$Name 录像缺少本局独立的禁用和首发。"
            for ($side = 0; $side -lt 2; $side++) {
                $player = if ($side -eq 0) { $pairing.Player0 } else { $pairing.Player1 }
                Check ($record.Teams[$side].Count -eq 6 -and ($record.Teams[$side] -join ',') -ceq ($tournament.Players[$player].Team -join ',')) "$Name 跨局继承了禁用后名单或首发重排。"
            }
            $replayed = [StreamDuel.Core.Replay]::Run($Database, $record)
            Check ($replayed.Phase -eq [StreamDuel.Core.Phase]::Over -and $replayed.Winner -eq $game.WinnerSide -and $replayed.Sides[0].Team.Count -eq 5 -and $replayed.Sides[1].Team.Count -eq 5) "$Name 录像不能独立重放本局结果。"
            $records.Add($recordJson)
            $count++
        }
    }
    Check ($report.coverage.completedGames -eq $count -and $report.coverage.archivedGames -eq $count -and $report.coverage.missingGames -eq 0 -and $report.coverage.invalidGames -eq 0) "$Name 覆盖率未按真实局数计算。"
    Check (@(Get-ChildItem -LiteralPath ($Path + '.replay-library') -File -Filter '*.json' | Where-Object Name -cne 'index.json').Count -eq $count) "$Name 重复或遗漏归档。"
    return ,$records.ToArray()
}

try {
    $assembly = [Reflection.Assembly]::LoadFrom($core)
    Check ([StringComparer]::OrdinalIgnoreCase.Equals([IO.Path]::GetFullPath($assembly.Location), [IO.Path]::GetFullPath($core))) '请在未加载其他 Core 的新 PowerShell 进程运行。'
    $content = Join-Path $binary 'Content'
    $sourceHashes = @{}
    foreach ($item in Get-ChildItem -LiteralPath $content -File -Recurse) { $sourceHashes[$item.FullName] = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash }
    $rules = [IO.File]::ReadAllText((Join-Path $content 'StreamDuelRules.json'), $utf8)
    $policy = [StreamDuel.Core.PackPolicy]::Load((Join-Path $content 'PackPolicy.json'))
    $ranked = @([StreamDuel.Core.Rulesets]::All | Where-Object Id -eq 'ranked')[0]
    $external = Join-Path $run 'fixtures'
    [void][IO.Directory]::CreateDirectory($external)
    $skills = [ordered]@{}
    foreach ($mode in @('wait', 'hit')) {
        foreach ($number in 1..5) {
            $skills["t127_${mode}_$number"] = @{ name = "验收动作$number"; type = 'talk'; cat = $(if ($mode -eq 'hit') { 'interact' } else { 'status' }); power = $(if ($mode -eq 'hit') { 180 } else { 0 }); pp = 999;
                effects = $(if ($mode -eq 'hit') { ,@(@{ code = 'FIXED_DAMAGE'; at = 'post'; target = 'foe'; n = 9999 }) } else { ,@() }) }
        }
    }
    $streamers = @(foreach ($mode in @('wait', 'hit')) { foreach ($number in 0..5) {
        @{ id = "t127_${mode}_$number"; name = "验收角色${mode}$number"; type = 'talk'; persona = 'punctual';
            stats = @{ hp = 400; atk = 200; def = 200; spa = 200; sdef = 200; spd = 200 };
            skills = @(1..4 | ForEach-Object { "t127_${mode}_$_" }); fifth = "t127_${mode}_5"; blurb = '原创临时验收角色。' }
    } })
    $pack = @{ id = 't127_fixture'; name = '逐局验收'; notice = '原创临时验收包。'; skills = $skills; streamers = $streamers } | ConvertTo-Json -Depth 20
    [IO.File]::WriteAllText((Join-Path $external 'fixture.json'), $pack, $utf8)
    $db = [StreamDuel.Core.ContentDb]::FromJson($rules, $pack, $policy)
    $codes = @{}
    foreach ($mode in @('wait', 'hit')) {
        $preset = [StreamDuel.Core.TeamPreset]::new(); $preset.PackId = $db.PackId; $preset.Name = '逐局验收队伍'
        foreach ($number in 0..5) { $preset.Ids.Add("t127_${mode}_$number") }
        $codes[$mode] = [StreamDuel.Core.TeamCode]::Encode($preset)
    }
    $missing = Join-Path $run 'not-created/event.json'
    $invalid = @(
        @('--tournament-ruleset'), @('--tournament-ruleset', ''), @('--tournament-ruleset', 'unknown'), @('--tournament-ruleset', 'ranked'),
        @('--tournament', $missing, '--tournament-ruleset', 'ranked', '--tournament-ruleset', 'ranked')
    )
    foreach ($extra in @(@('--no-auto-tiebreak'), @('--ruleset', 'ranked'), @('--tournament-report', $missing), @('--seed', '127'), @('--host', '0'), @('--ban'))) {
        $base = @('--tournament', $missing, '--tournament-ruleset', 'ranked', '--auto')
        $invalid += ,($base + $extra); $invalid += ,($extra + $base)
    }
    for ($i = 0; $i -lt $invalid.Count; $i++) {
        $result = Invoke-Cli "invalid-$i" $invalid[$i] @() 1
        Check ($result.Out.Length -eq 0) '非法参数进入了交互或对战。'
    }
    $null = Invoke-Cli 'new-requires-auto' @('--tournament', $missing, '--tournament-ruleset', 'ranked', '--format', 'single') @() 1
    $null = Invoke-Cli 'swiss-refused' @('--tournament', $missing, '--tournament-ruleset', 'ranked', '--auto') @('2') 1
    Check (-not (Test-Path -LiteralPath (Split-Path $missing -Parent))) '拒绝参数仍创建了赛事目录。'
    $help = Invoke-Cli 'help' @('--help')
    Check ($help.Out.Contains('--tournament-ruleset') -and $help.Out.Contains('墙钟') -and $help.Out.Contains('决胜加赛')) '帮助缺少入口或执行边界。'
    $checks.Add('PASS strict_new_flag_modes_auto_swiss_and_help')

    foreach ($format in @('single', 'double', 'roundrobin')) {
        $path = Join-Path $run "$format-wins.json"
        $arguments = @('--tournament', $path, '--tournament-ruleset', 'ranked', '--auto', '--pack', $db.PackId, '--pack-dir', $external)
        $lines = Registration $codes.hit $codes.wait
        if ($format -eq 'roundrobin') { $lines = @('1') + $lines } else { $arguments += @('--format', $format, '--tournament-seed', '127') }
        $null = Invoke-Cli "$format-wins" $arguments $lines
        $null = Verify-Games "$format-wins" $path $db 2 $true
        $tournament = Read-Tournament $path $db
        foreach ($pairing in $tournament.Pairings) {
            if ($pairing.IsBye) { continue }
            $winner = if ($pairing.WinnerSide -eq 0) { $pairing.Player0 } else { $pairing.Player1 }
            Check ($winner -eq 0 -and @($pairing.Games | Where-Object WinnerSide -eq $pairing.WinnerSide).Count -eq 2) "$format 没有在 2:0 时提前结束。"
        }
    }
    $checks.Add('PASS_single_double_roundrobin_two_wins_per_game_ban_lead_report')

    $single = Join-Path $run 'all-draws.json'
    $null = Invoke-Cli 'six-draws' @('--tournament', $single, '--format', 'single', '--tournament-ruleset', 'ranked', '--auto', '--tournament-seed', '127', '--pack', $db.PackId, '--pack-dir', $external) (Registration $codes.wait $codes.wait)
    $records = Verify-Games 'six-draws' $single $db 6 $true $true
    Check (@((Read-Tournament $single $db).Pairings[0].Games | Where-Object WinnerSide -ne -1).Count -eq 0) '种子晋级改写了单局真实平局。'
    $checks.Add('PASS_three_regulation_draws_three_extra_draws_seed_advancement')

    # 真实断点包含一局已登记与一局仅归档，恢复须逐字复用录像，不能重置局序。
    $resumePath = Join-Path $run 'resume.json'
    $resume = New-ReadyTournament $resumePath $db $codes.wait $codes.wait
    $null = Save-Game $resumePath $resume (Play-Game ($resume.CreateGame($resume.Pairings[0])))
    $orphanLibrary = [StreamDuel.Core.ReplayLibrary]::new($resumePath + '.replay-library', 200, $null, $policy)
    $orphan = $orphanLibrary.SaveTournamentGame($resume, $resume.Pairings[0], (Play-Game ($resume.CreateGame($resume.Pairings[0]))), 1)
    $saved = @{}
    foreach ($entry in $orphanLibrary.List($null)) { $saved[$entry.Id] = (Get-FileHash -LiteralPath (Join-Path ($resumePath + '.replay-library') ($entry.Id + '.json')) -Algorithm SHA256).Hash }
    $before = Fingerprint $resumePath
    $readOnly = Invoke-Cli 'resume-readonly' @('--tournament', $resumePath, '--pack-dir', $external)
    Check ($readOnly.Out.Contains('请加 --auto') -and (Fingerprint $resumePath) -ceq $before) '无 auto 的逐局续赛未保持只读。'
    $null = Invoke-Cli 'resume-mismatch' @('--tournament', $resumePath, '--pack-dir', $external, '--auto', '--tournament-ruleset', 'casual') @() 1
    Check ((Fingerprint $resumePath) -ceq $before) '不同预设改写了续赛快照。'
    $null = Invoke-Cli 'resume-auto' @('--tournament', $resumePath, '--pack-dir', $external, '--auto', '--tournament-ruleset', 'ranked')
    $resumed = Verify-Games 'resumed' $resumePath $db 6 $true $true
    Check ([IO.File]::ReadAllText($resumePath, $utf8) -ceq [IO.File]::ReadAllText($single, $utf8) -and ($records -join "`n") -ceq ($resumed -join "`n")) '中途恢复与不中断结果不一致。'
    foreach ($id in $saved.Keys) { Check ((Get-FileHash -LiteralPath (Join-Path ($resumePath + '.replay-library') ($id + '.json')) -Algorithm SHA256).Hash -ceq $saved[$id]) '恢复改写了已归档录像。' }
    Check ((Report 'orphan-report' $resumePath).rounds[0].matches[0].games[1].replay.id -ceq $orphan) '恢复没有复用孤立归档编号。'
    $checks.Add('PASS_checkpoint_orphan_archive_idempotency_and_readonly_snapshot')

    foreach ($format in @('single', 'roundrobin')) {
        $path = Join-Path $run "$format-full-regulation.json"
        $tournament = New-ReadyTournament $path $db $codes.wait $codes.wait $format
        for ($i = 0; $i -lt 3; $i++) {
            $battle = Prepare-Game ($tournament.CreateGame($tournament.Pairings[0]))
            if ($format -eq 'single' -and $i -eq 0) { $battle.Surrender(1) } else { $battle.OfferDraw(0); $battle.RespondDraw(1, $true) }
            $null = Save-Game $path $tournament $battle
        }
        $regularFiles = @{}
        foreach ($item in Get-ChildItem -LiteralPath ($path + '.replay-library') -File -Filter '*.json' | Where-Object Name -cne 'index.json') {
            $regularFiles[$item.FullName] = Replay-Fingerprint $item.FullName
        }
        Check ($regularFiles.Count -eq 3) '常规满局结算没有完整保存三局录像。'
        $completedBefore = Fingerprint $path
        $result = Invoke-Cli "$format-after-full-regulation" @('--tournament', $path, '--pack-dir', $external, '--auto')
        Check (-not $result.Out.Contains('准备对局') -and $result.Out.Contains('赛事已结束。')) '已按满局比分完成的赛事仍启动第四局，或没有正常结束。'
        $null = Verify-Games "$format-after-full-regulation" $path $db 3 $true
        $match = (Report "$format-after-full-regulation-result" $path).rounds[0].matches[0]
        Check ([string]::IsNullOrEmpty($match.pendingDecision) -and $match.draws -eq $(if ($format -eq 'single') { 2 } else { 3 }) -and
            $match.winnerSide -eq $(if ($format -eq 'single') { 0 } else { -1 }) -and -not $match.advancedBySeed) '战报没有按满局领先或循环赛整场平局结算，或改写了真实平局。'
        if ($format -eq 'single') {
            Check (($match.wins -join ',') -ceq '1,0') '单败 1 胜 2 平未保留实际局比分。'
        } else {
            Check (($match.wins -join ',') -ceq '0,0') '循环赛三平错误记入胜场。'
            foreach ($standing in (Read-Tournament $path $db).Standings()) {
                Check ($standing.Points -eq 1 -and $standing.Draws -eq 1 -and $standing.Wins -eq 0 -and $standing.Losses -eq 0) '循环赛整场平局没有按原规则各记一分、一场平局。'
            }
        }
        foreach ($recordPath in $regularFiles.Keys) { Check ((Replay-Fingerprint $recordPath) -ceq $regularFiles[$recordPath]) '满局结算后查询或续赛改写了已有录像。' }
        Check ((Fingerprint $path) -ceq $completedBefore) '满局已完成赛事的续赛或报告改写了赛事、录像、索引或锁。'
    }
    $rrPath = Join-Path $run 'roundrobin-live-draws.json'
    $null = Invoke-Cli 'roundrobin-live-draws' @('--tournament', $rrPath, '--tournament-ruleset', 'ranked', '--auto', '--pack', $db.PackId, '--pack-dir', $external) (@('1') + (Registration $codes.wait $codes.wait))
    $null = Verify-Games 'roundrobin-live-draws' $rrPath $db 3 $true
    $rrComplete = Read-Tournament $rrPath $db
    Check ($rrComplete.Pairings[0].WinnerSide -eq -1 -and -not $rrComplete.Pairings[0].AdvancedBySeed) '循环赛真实三平没有以整场平局结束。'
    foreach ($standing in $rrComplete.Standings()) {
        Check ($standing.Points -eq 1 -and $standing.Draws -eq 1 -and $standing.Wins -eq 0 -and $standing.Losses -eq 0) '循环赛自动三平没有保留原积分和整场平局计数。'
    }
    $checks.Add('PASS_full_regulation_leader_wins_roundrobin_draws_without_extra_game')

    $threePath = Join-Path $run 'three-games.json'
    $three = New-ReadyTournament $threePath $db $codes.hit $codes.wait
    foreach ($loser in @(0, 1)) {
        $battle = Prepare-Game ($three.CreateGame($three.Pairings[0])); $battle.Surrender($loser)
        $null = Save-Game $threePath $three $battle
    }
    $null = Invoke-Cli 'third-regulation-win' @('--tournament', $threePath, '--pack-dir', $external, '--auto')
    $null = Verify-Games 'third-regulation-win' $threePath $db 3 $true
    $score = ((Report 'three-score' $threePath).rounds[0].matches[0].wins | Sort-Object) -join ','
    Check ($score -eq '1,2') '2:1 续赛比分不正确。'
    $checks.Add('PASS_two_to_one_resumed_regulation_finish')

    $blockedPath = Join-Path $run 'blocked.json'
    $null = New-ReadyTournament $blockedPath $db $codes.hit $codes.wait
    $blockedBefore = [IO.File]::ReadAllText($blockedPath, $utf8)
    $blockedLibrary = $blockedPath + '.replay-library'
    [IO.File]::WriteAllText($blockedLibrary, '验收占位文件', $utf8)
    $blocked = Invoke-Cli 'archive-blocked' @('--tournament', $blockedPath, '--pack-dir', $external, '--auto') @() 1
    Check ($blocked.Err.Contains('本局结果未登记') -and [IO.File]::ReadAllText($blockedPath, $utf8) -ceq $blockedBefore -and (Read-Tournament $blockedPath $db).Pairings[0].Games.Count -eq 0) '归档失败仍推进了局序。'
    [IO.File]::Move($blockedLibrary, $blockedLibrary + '.backup')
    $null = Invoke-Cli 'archive-repaired' @('--tournament', $blockedPath, '--pack-dir', $external, '--auto')
    $null = Verify-Games 'archive-repaired' $blockedPath $db 2 $true
    $checks.Add('PASS_archive_failure_no_advance_and_repair_resume')

    $oldPath = Join-Path $run 'old.json'
    $old = New-ReadyTournament $oldPath $db $codes.wait $codes.wait 'single' $null
    $before = Fingerprint $oldPath
    $null = Invoke-Cli 'old-cannot-upgrade' @('--tournament', $oldPath, '--pack-dir', $external, '--auto', '--tournament-ruleset', 'ranked') @() 1
    Check ((Fingerprint $oldPath) -ceq $before -and -not (Read-Tournament $oldPath $db).HasRuleset) '新 flag 升级或覆盖了旧赛事。'
    $custom = [StreamDuel.Core.Ruleset]::FromJson($ranked.ToJson()); $custom.TurnSeconds++
    $customPath = Join-Path $run 'same-id-different-snapshot.json'
    $null = New-ReadyTournament $customPath $db $codes.wait $codes.wait 'single' $custom
    $before = Fingerprint $customPath
    $null = Invoke-Cli 'same-id-different-snapshot' @('--tournament', $customPath, '--pack-dir', $external, '--auto', '--tournament-ruleset', 'ranked') @() 1
    Check ((Fingerprint $customPath) -ceq $before) '同 ID 的不同规则快照被覆盖。'

    # 报名读取真实档案；低分先报名，完整逐局赛制须保留反向评级顺位。
    $ratingSources = @()
    $ratingSourceBefore = @{}
    foreach ($rating in @(1400, 1800)) {
        $directory = Join-Path $run "rating-sources/$rating"
        [void][IO.Directory]::CreateDirectory($directory)
        $profile = [StreamDuel.Core.Profile]::new()
        $value = [StreamDuel.Core.PlayerRating]::new()
        $value.Rating = $rating; $value.Games = 7
        $profile.Ratings[[StreamDuel.Core.Profile]::DefaultRatingFormat] = $value
        $profilePath = Join-Path $directory 'profile.json'
        [IO.File]::WriteAllText($profilePath, $profile.ToJson(), $utf8)
        $ratingSources += $directory
        $ratingSourceBefore[$profilePath] = Replay-Fingerprint $profilePath
    }
    $ratingLines = @('选手甲', $codes.hit, 'human', $ratingSources[0], '选手乙', $codes.wait, 'human', $ratingSources[1])
    $ratingPath = Join-Path $run 'rating-complete/event.json'
    $ratingArguments = @('--tournament', $ratingPath, '--format', 'single', '--tournament-ruleset', 'ranked', '--seed-by', 'rating', '--auto', '--pack', $db.PackId, '--pack-dir', $external)
    $ratingResult = Invoke-Cli 'rating-ruleset-complete' $ratingArguments ($ratingLines + @(''))
    $ratingTournament = Read-Tournament $ratingPath $db
    $ratingJson = [IO.File]::ReadAllText($ratingPath, $utf8) | ConvertFrom-Json -AsHashtable
    Check ($ratingResult.Out.Contains('赛事已结束。') -and $ratingTournament.SeedMode.ToString() -ceq 'ByRating' -and
        $null -eq $ratingTournament.Seed -and ($ratingTournament.EffectiveSeedOrder -join ',') -ceq '1,0' -and
        ($ratingTournament.RatingSeeds.Player -join ',') -ceq '0,1' -and ($ratingTournament.RatingSeeds.Rating -join ',') -ceq '1400,1800' -and
        ($ratingTournament.RatingSeeds.Games -join ',') -ceq '7,7' -and ($ratingTournament.RatingSeeds.Season -join ',') -ceq '1,1') '真实评级多局赛事丢失冻结分数、场数、赛季或有效种子顺位。'
    Check ($ratingJson.version -eq 5 -and $ratingJson.seedBy -ceq 'rating' -and $ratingJson.autoTiebreak -and
        $ratingJson.ruleset.bestOf -eq 3 -and ($ratingJson.ratingSeeds.rating -join ',') -ceq '1400,1800' -and
        -not [IO.File]::ReadAllText($ratingPath, $utf8).Contains('rating-sources')) 'v5 多局赛事遗漏评级/规则快照或泄露档案路径。'
    $ratingRecords = Verify-Games 'rating-ruleset-complete' $ratingPath $db 2 $true
    Check ($ratingTournament.Champion -eq 0 -and @($ratingTournament.Pairings[0].Games | Where-Object WinnerSide -eq $ratingTournament.Pairings[0].WinnerSide).Count -eq 2) '真实评级对阵没有由原报名进攻队赢足两局。'
    foreach ($json in $ratingRecords) {
        $record = [StreamDuel.Core.BattleRecord]::FromJson($json)
        Check (@($record.Events | Where-Object Type -eq ([StreamDuel.Core.BattleRecordEventType]::Turn)).Count -gt 0) '评级多局夹具没有执行真实 AI 回合。'
    }
    $ratingFilesBefore = Fingerprint $ratingPath
    $null = Invoke-Cli 'rating-ruleset-finished-resume' @('--tournament', $ratingPath, '--pack-dir', $external, '--auto')
    $null = Report 'rating-ruleset-finished-report' $ratingPath
    Check ((Fingerprint $ratingPath) -ceq $ratingFilesBefore) '完整评级赛事再次续赛或查询改写了赛事、录像、索引或锁。'

    # CLI 先保存同一批报名；公共 API 生成一局真实检查点，再由 CLI 续完余下局。
    $ratingResumePath = Join-Path $run 'rating-resume/event.json'
    $null = Invoke-Cli 'rating-ruleset-register-checkpoint' @('--tournament', $ratingResumePath, '--format', 'single', '--tournament-ruleset', 'ranked', '--seed-by', 'rating', '--auto', '--pack', $db.PackId, '--pack-dir', $external) ($ratingLines + @('q'))
    $ratingResume = Read-Tournament $ratingResumePath $db
    Check ($ratingResume.Round -eq 0 -and $ratingResume.Players.Count -eq 2 -and $ratingResume.RatingSeeds.Count -eq 2) '评级多局报名断点不完整或提前开局。'
    $null = $ratingResume.NextRound()
    $firstId = Save-Game $ratingResumePath $ratingResume (Play-Game ($ratingResume.CreateGame($ratingResume.Pairings[0])))
    $firstPath = Join-Path ($ratingResumePath + '.replay-library') ($firstId + '.json')
    $firstBefore = Replay-Fingerprint $firstPath
    $checkpointBefore = Fingerprint $ratingResumePath
    $readOnly = Invoke-Cli 'rating-ruleset-mid-readonly' @('--tournament', $ratingResumePath, '--pack-dir', $external, '--seed-by', 'rating')
    Check ($readOnly.Out.Contains('请加 --auto') -and (Fingerprint $ratingResumePath) -ceq $checkpointBefore) '评级多局中途只读查看改写了赛事、录像、索引或锁。'
    $null = Invoke-Cli 'rating-ruleset-mid-mismatch' @('--tournament', $ratingResumePath, '--pack-dir', $external, '--auto', '--tournament-ruleset', 'casual') @() 1
    Check ((Fingerprint $ratingResumePath) -ceq $checkpointBefore) '拒绝不同赛制快照时改变了评级赛事的任一文件。'
    $null = Invoke-Cli 'rating-ruleset-mid-resume' @('--tournament', $ratingResumePath, '--pack-dir', $external, '--auto')
    $ratingResumedRecords = Verify-Games 'rating-ruleset-mid-resume' $ratingResumePath $db 2 $true
    Check ([IO.File]::ReadAllText($ratingResumePath, $utf8) -ceq [IO.File]::ReadAllText($ratingPath, $utf8) -and
        ($ratingResumedRecords -join "`n") -ceq ($ratingRecords -join "`n") -and (Replay-Fingerprint $firstPath) -ceq $firstBefore) '评级多局断点续赛与不中断结果不同，或重写了已完成局。'
    Check ((Report 'rating-ruleset-original-id' $ratingResumePath).rounds[0].matches[0].games[0].replay.id -ceq $firstId) '评级续赛没有保留原第一局录像编号。'
    foreach ($profilePath in $ratingSourceBefore.Keys) {
        Check ((Replay-Fingerprint $profilePath) -ceq $ratingSourceBefore[$profilePath] -and
            @(Get-ChildItem -LiteralPath (Split-Path $profilePath -Parent) -Force).Count -eq 1) '赛事报名/续赛修改了来源档案或创建了档案锁。'
    }
    $checks.Add('PASS_rating_ruleset_v5_real_games_checkpoint_resume_and_readonly_files')

    # 新旧独立入口都必须先解析为已知参数，再拒绝组合，不得读取来源或维护存档。
    $freshConflict = Join-Path $run 'rating-conflict-must-not-exist/event.json'
    $conflicts = @(
        @('--tournament-seed', '127'), @('--no-auto-tiebreak'),
        @('--ruleset', 'casual'), @('--ruleset', 'ranked'), @('--ruleset', 'chaos'),
        @('--branch-compare', 'absent-original.json', 'absent-branch.json'),
        @('--hotseat'), @('--advice'), @('--rating-history'),
        @('--tournament-report', $ratingPath), @('--replay', 'absent-replay.json')
    )
    $conflictNumber = 0
    foreach ($targetPath in @($ratingPath, $freshConflict)) {
        $before = Fingerprint $targetPath
        $base = @('--tournament', $targetPath, '--tournament-ruleset', 'ranked', '--seed-by', 'rating', '--auto', '--pack-dir', $external)
        foreach ($extra in $conflicts) {
            foreach ($arguments in @(,($base + $extra)) + @(,($extra + $base))) {
                $result = Invoke-Cli ('rating-ruleset-conflict-' + (++$conflictNumber)) $arguments @() 1
                Check ($result.Out.Length -eq 0 -and $result.Err -notmatch '未知参数|unknown|无法识别' -and
                    (Fingerprint $targetPath) -ceq $before) '互斥参数没有提前按中文组合错误拒绝，或改变了已有赛事/库文件。'
            }
        }
    }
    Check (-not (Test-Path -LiteralPath (Split-Path $freshConflict -Parent))) '互斥参数创建了新赛事目录。'
    $checks.Add('PASS_rating_ruleset_double_order_conflicts_preserve_existing_and_missing_files')

    foreach ($path in $sourceHashes.Keys) { Check ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ceq $sourceHashes[$path]) '验收修改了原内容数据。' }
    $checks.Add('PASS_old_format_and_exact_ruleset_snapshot_preserved_source_unchanged')
}
catch { $failure = $_.ToString() + "`n" + $_.ScriptStackTrace }
finally {
    $result = [ordered]@{ status = $(if ($null -eq $failure) { 'PASS' } else { 'FAIL' }); checks = @($checks.ToArray()); calls = @($calls.ToArray()); error = $failure }
    [IO.File]::WriteAllText((Join-Path $run 'report.json'), ($result | ConvertTo-Json -Depth 10), $utf8)
}
foreach ($check in $checks) { Write-Host $check }
Write-Host "证据目录：$run"
if ($null -ne $failure) { throw $failure }
