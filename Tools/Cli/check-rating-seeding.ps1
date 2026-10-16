#requires -Version 7.0
param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug')

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$binary = Join-Path $PSScriptRoot "bin/$Configuration/net8.0"
$cli = Join-Path $binary 'Cli.dll'
$core = Join-Path $binary 'StreamDuel.Core.dll'
foreach ($path in @($cli, $core)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "请先构建 Tools/Cli -c $Configuration；本脚本不构建。" }
}
$run = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ('obj/rating-seeding-verification/' + [Guid]::NewGuid().ToString('N'))))
if (-not $run.StartsWith($repository + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw '验收产物必须位于当前工作树内。'
}
$temporary = Join-Path $run 'temp'
$sources = Join-Path $run 'sources'
[void][IO.Directory]::CreateDirectory($temporary)
[void][IO.Directory]::CreateDirectory($sources)
$utf8 = [Text.UTF8Encoding]::new($false, $true)
$checks = [Collections.Generic.List[string]]::new()
$calls = [Collections.Generic.List[object]]::new()
$failure = $null

function Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "$Message 证据目录：$run" }
}

function Passed([string]$Name) {
    $checks.Add($Name)
    Write-Output "PASS $Name"
}

function Invoke-Cli([string]$Name, [string[]]$Parameters, [string[]]$Lines = @(), [int]$Exit = 0) {
    $work = Join-Path $run ('calls/' + $Name)
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
    $start.Environment['TEMP'] = $temporary
    $start.Environment['TMP'] = $temporary
    $start.Environment['DOTNET_SKIP_FIRST_TIME_EXPERIENCE'] = '1'
    $start.Environment['DOTNET_CLI_TELEMETRY_OPTOUT'] = '1'
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    $started = $false
    try {
        $started = $process.Start()
        Check $started "$Name 无法启动。"
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
        Check (-not (Test-Path -LiteralPath (Join-Path $work 'saves'))) "$Name 创建了普通玩家存档。"
        if ($Exit -eq 0) { Check ([string]::IsNullOrWhiteSpace($result.Err)) "$Name 成功时出现标准错误。" }
        else { Check ($result.Err.Contains('错误：')) "$Name 未给出中文错误。" }
        return $result
    }
    finally {
        if ($started -and -not $process.HasExited) { $process.Kill($true); [void]$process.WaitForExit(5000) }
        $process.Dispose()
    }
}

function Tree-Fingerprint([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return 'missing' }
    $items = @((Get-Item -LiteralPath $Path -Force))
    if (Test-Path -LiteralPath $Path -PathType Container) { $items += @(Get-ChildItem -LiteralPath $Path -Recurse -Force) }
    return (($items | Sort-Object FullName | ForEach-Object {
        if ($_.PSIsContainer) { $_.FullName + ':directory' }
        else { $_.FullName + ':' + $_.Attributes + ':' + $_.LastWriteTimeUtc.Ticks + ':' + (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    }) -join "`n")
}

function Read-Tournament([string]$Path) {
    return [StreamDuel.Core.Tournament]::FromJson($db, [IO.File]::ReadAllText($Path, $utf8))
}

function New-Profile([string]$Name, [int]$Rating, [int]$Games, [bool]$NewSeason = $false) {
    $directory = Join-Path $sources $Name
    [void][IO.Directory]::CreateDirectory($directory)
    $profile = [StreamDuel.Core.Profile]::new()
    $value = [StreamDuel.Core.PlayerRating]::new()
    $value.Rating = $Rating
    $value.Games = $Games
    if ($NewSeason) { [StreamDuel.Core.RatingSystem]::NewSeason($value); $value.Games = 3 }
    $profile.Ratings[[StreamDuel.Core.Profile]::DefaultRatingFormat] = $value
    $other = [StreamDuel.Core.PlayerRating]::new()
    $other.Rating = 5000
    $other.Games = 999
    $profile.Ratings['另一赛制'] = $other
    [IO.File]::WriteAllText((Join-Path $directory 'profile.json'), $profile.ToJson(), $utf8)
    return $directory
}

function Register-Lines([string]$Name, [string]$Source = '', [string]$Directory = '') {
    return ,@($Name, $code, $Source, $Directory)
}

try {
    $assembly = [Reflection.Assembly]::LoadFrom($core)
    Check ([StringComparer]::OrdinalIgnoreCase.Equals([IO.Path]::GetFullPath($assembly.Location), [IO.Path]::GetFullPath($core))) '请在未加载其他 Core 的新 PowerShell 进程中运行。'
    $content = Join-Path $binary 'Content'
    $policy = [StreamDuel.Core.PackPolicy]::Load((Join-Path $content 'PackPolicy.json'))
    $db = [StreamDuel.Core.ContentDb]::FromJson([IO.File]::ReadAllText((Join-Path $content 'StreamDuelRules.json'), $utf8),
        [IO.File]::ReadAllText((Join-Path $content 'Packs/original.json'), $utf8), $policy)
    $preset = [StreamDuel.Core.TeamPreset]::new()
    $preset.PackId = $db.PackId
    $preset.Name = '排位种子验收'
    foreach ($id in @('bell', 'enka', 'shiori', 'lumi', 'hane', 'momo')) { $preset.Ids.Add($id) }
    $code = [StreamDuel.Core.TeamCode]::Encode($preset)

    $missing = Join-Path $run 'must-not-exist/event.json'
    $invalid = @(
        @('--seed-by'), @('--seed-by', ''), @('--seed-by', 'Rating'), @('--seed-by', 'default'),
        @('--seed-by', 'rating'), @('--tournament', $missing, '--seed-by'),
        @('--tournament', $missing, '--seed-by', 'unknown'),
        @('--tournament', $missing, '--seed-by', 'rating', '--seed-by', 'rating'),
        @('--tournament-report', $missing, '--seed-by', 'rating'),
        @('--tournament', $missing, '--seed-by', 'rating', '--tournament-report', $missing),
        @('--tournament', $missing, '--seed-by', 'rating', '--card', 'bell'),
        @('--cards', 'original', '--tournament', $missing, '--seed-by', 'rating'),
        @('--new-season', '--tournament', $missing, '--seed-by', 'rating'),
        @('--rating-history', '--seed-by', 'rating'),
        @('--tournament', $missing, '--seed-by', 'rating', '--branch', 'missing', '--turn', '1', '--side', '0'),
        @('--tournament', $missing, '--seed-by', 'rating', '--hotseat'),
        @('--tournament', $missing, '--seed-by', 'rating', '--list-packs'),
        @('--tournament', $missing, '--seed-by', 'rating', '--replays'),
        @('--tournament', $missing, '--seed-by', 'rating', '--replay', 'missing'),
        @('--tournament', $missing, '--seed-by', 'rating', '--host', '0'),
        @('--watch', '127.0.0.1:1', '--tournament', $missing, '--seed-by', 'rating'),
        @('--tournament', $missing, '--seed-by', 'rating', '--ruleset', 'casual'),
        @('--ruleset', 'ranked', '--seed-by', 'rating', '--tournament', $missing),
        @('--tournament', $missing, '--seed-by', 'rating', '--watch-name', '排种子观众'),
        @('--tournament', $missing, '--seed-by', 'rating', '--watch', '127.0.0.1:1', '--watch-name', '排种子观众'),
        @('--watch', '127.0.0.1:1', '--watch-name', '排种子观众', '--seed-by', 'rating', '--tournament', $missing),
        @('--tournament', $missing, '--seed-by', 'rating', '--spectator-chat', 'on'),
        @('--tournament', $missing, '--seed-by', 'rating', '--host', '0', '--spectate-port', '0', '--spectator-chat', 'on'),
        @('--host', '0', '--spectate-port', '0', '--spectator-chat', 'off', '--seed-by', 'rating', '--tournament', $missing),
        @('--tournament', $missing, '--seed-by', 'rating', '--seed', '1')
    )
    for ($i = 0; $i -lt $invalid.Count; $i++) {
        $result = Invoke-Cli "invalid-$i" $invalid[$i] @() 1
        Check ($result.Out.Length -eq 0) '无效参数在拒绝前进入赛事或其他入口。'
    }
    Check (-not (Test-Path -LiteralPath (Split-Path $missing -Parent))) '无效参数创建了赛事目录。'
    $help = Invoke-Cli 'help' @('--help')
    Check ($help.Out.Contains('--seed-by rating') -and $help.Out.Contains('只读档案目录')) '帮助缺少种子方式或只读档案说明。'
    Passed 'arguments_help_and_independent_modes'

    $profiles = @((New-Profile 'p0' 1700 5), (New-Profile 'p1' 1900 2), (New-Profile 'p2' 1900 12), (New-Profile 'p3' 1900 12))
    $sourceBefore = Tree-Fingerprint $sources
    $registration = [Collections.Generic.List[string]]::new()
    for ($i = 0; $i -lt 4; $i++) { $registration.AddRange([string[]](Register-Lines "选手$i" '' $profiles[$i])) }
    $registration.AddRange([string[]](Register-Lines '新真人'))
    foreach ($level in @('normal', 'hard', 'expert')) { $registration.AddRange([string[]](Register-Lines "电脑$level" $level)) }
    $registration.Add(''); $registration.Add('q')
    $eventPath = Join-Path $run 'ranked-single.json'
    $created = Invoke-Cli 'ranked-single' @('--tournament', $eventPath, '--format', 'single', '--seed-by', 'rating') $registration.ToArray()
    $event = Read-Tournament $eventPath
    Check ($event.SeedMode.ToString() -eq 'ByRating' -and $event.RatingSeeds.Count -eq 8) '没有冻结全部报名快照。'
    Check (($event.EffectiveSeedOrder -join ',') -ceq '2,3,1,7,0,6,4,5') '分数、场数、报名顺序三级排序错误。'
    Check (($event.RatingSeeds.Rating -join ',') -ceq '1700,1900,1900,1900,1500,1400,1600,1800') '真人初始分或电脑参考分错误。'
    Check (($event.RatingSeeds.Player -join ',') -ceq '0,1,2,3,4,5,6,7') '快照没有保持报名顺序。'
    Check ($event.RatingSeeds[5].ComputerLevel.ToString() -eq 'Normal' -and $event.RatingSeeds[6].ComputerLevel.ToString() -eq 'Hard' -and
        $event.RatingSeeds[7].ComputerLevel.ToString() -eq 'Expert') '电脑身份未保留。'
    $firstHalf = @($event.Pairings[0].Player0, $event.Pairings[0].Player1, $event.Pairings[1].Player0, $event.Pairings[1].Player1)
    Check (($firstHalf -contains 2) -xor ($firstHalf -contains 3)) '八人单败前两种子没有分在不同半区。'
    $savedText = [IO.File]::ReadAllText($eventPath, $utf8)
    $saved = $savedText | ConvertFrom-Json -AsHashtable
    Check ($saved.version -eq 4 -and $saved.seedBy -eq 'rating' -and $saved.ratingSeeds.Count -eq 8) '新模式赛事没有保存 v4/rating 快照。'
    foreach ($source in $profiles) { Check (-not $savedText.Contains(($source | ConvertTo-Json -Compress))) '赛事保存了来源目录。' }
    Check ($created.Out.Contains('种子 1：排位快照：报名 #3') -and $created.Out.Contains('1900 分；12 场；第 1 季')) 'CLI 没有显示实际种子顺序和快照。'
    Check ((Tree-Fingerprint $sources) -ceq $sourceBefore) '报名改写了源档案。'
    Passed 'eight_player_three_level_sort_and_computer_references'

    $null = New-Profile 'p0' 3000 99
    $sourceBefore = Tree-Fingerprint $sources
    foreach ($explicit in @($false, $true)) {
        $parameters = @('--tournament', $eventPath)
        if ($explicit) { $parameters += @('--seed-by', 'rating') }
        $resumed = Invoke-Cli "snapshot-resume-$explicit" $parameters @('q')
        Check ([IO.File]::ReadAllText($eventPath, $utf8) -ceq $savedText) '修改来源后续赛改变已冻结快照或对阵。'
        Check ($resumed.Out.Contains('1700 分；5 场') -and -not $resumed.Out.Contains('3000 分')) '续赛重新读取了来源分数。'
        Check (-not $resumed.Out.Contains('选手来源（')) '已开赛续赛重新要求报名来源。'
    }
    Check ((Tree-Fingerprint $sources) -ceq $sourceBefore) '续赛访问改变了源文件。'
    Passed 'snapshot_resume_omitted_and_explicit_rating'

    foreach ($format in @('roundrobin', 'swiss', 'single', 'double')) {
        $path = Join-Path $run ("format-$format.json")
        $parameters = @('--tournament', $path, '--seed-by', 'rating')
        $lines = @()
        if ($format -in @('single', 'double')) { $parameters += @('--format', $format) }
        else { $lines += $format; if ($format -eq 'swiss') { $lines += '1' } }
        $lines += (Register-Lines '低分' 'normal') + (Register-Lines '高分' 'expert') + @('', 'q')
        $null = Invoke-Cli "format-$format" $parameters $lines
        $loaded = Read-Tournament $path
        Check ($loaded.Format -eq $format -and $loaded.Round -eq 1 -and ($loaded.EffectiveSeedOrder -join ',') -eq '1,0') "$format 没有使用按分数排序的赛事。"
        $finished = Invoke-Cli "finish-$format" @('--tournament', $path) @('0', '0', '0', '0')
        Check ((Read-Tournament $path).IsFinished -and $finished.Out.Contains('赛事已结束。')) "$format 无法正常续赛结束。"
    }
    Passed 'all_four_formats_create_resume_and_finish'

    $seasonPath = New-Profile 'current-season' 2300 40 $true
    $seasonFile = Join-Path $seasonPath 'profile.json'
    [IO.File]::SetAttributes($seasonFile, [IO.FileAttributes]::ReadOnly)
    $otherOnly = Join-Path $sources 'other-format-only'
    [void][IO.Directory]::CreateDirectory($otherOnly)
    [IO.File]::WriteAllText((Join-Path $otherOnly 'profile.json'), '{"packs":{},"ratings":{"另一赛制":{"rating":5000,"games":999}}}', $utf8)
    $noFile = Join-Path $sources 'does-not-exist'
    $emptyProfile = Join-Path $sources 'valid-empty-profile'
    [void][IO.Directory]::CreateDirectory($emptyProfile)
    [IO.File]::WriteAllText((Join-Path $emptyProfile 'profile.json'), '{"packs":{}}', $utf8)
    $sourceBefore = Tree-Fingerprint $sources
    $currentPath = Join-Path $run 'current-season.json'
    $lines = (Register-Lines '有档案专家' 'expert' $seasonPath) + (Register-Lines '有档案真人' 'human' $seasonPath)
    $lines += (Register-Lines '无默认评级' 'human' $otherOnly) + (Register-Lines '缺档案普通电脑' 'normal' $noFile)
    $lines += (Register-Lines '有空档案专家' 'expert' $emptyProfile) + @('', 'q')
    $currentOutput = Invoke-Cli 'current-season' @('--tournament', $currentPath, '--format', 'single', '--seed-by', 'rating') $lines
    $current = Read-Tournament $currentPath
    Check (($current.RatingSeeds.Rating -join ',') -eq '1900,1900,1500,1400,1500' -and ($current.RatingSeeds.Games -join ',') -eq '3,3,0,0,0') '未使用当前赛季默认赛制，或电脑档案被参考分覆盖。'
    Check (($current.RatingSeeds.Season -join ',') -eq '2,2,1,1,1' -and $currentOutput.Out.Contains('第 2 季')) '当前赛季快照错误。'
    Check ((Tree-Fingerprint $sources) -ceq $sourceBefore -and -not (Test-Path -LiteralPath $noFile)) '只读来源生成了目录、锁或改写了文件。'
    Passed 'current_season_profile_precedence_missing_and_readonly_sources'

    $badSources = [Collections.Generic.List[string]]::new()
    foreach ($kind in @('malformed', 'empty', 'bad-utf8', 'profile-is-directory')) {
        $directory = Join-Path $sources $kind
        [void][IO.Directory]::CreateDirectory($directory)
        $profileFile = Join-Path $directory 'profile.json'
        if ($kind -eq 'bad-utf8') { [IO.File]::WriteAllBytes($profileFile, [byte[]]@(255, 254, 255)) }
        elseif ($kind -eq 'profile-is-directory') { [void][IO.Directory]::CreateDirectory($profileFile) }
        else { [IO.File]::WriteAllText($profileFile, $(if ($kind -eq 'empty') { '' } else { '{broken' }), $utf8) }
        $badSources.Add($directory)
    }
    $notDirectory = Join-Path $sources 'not-directory.txt'
    [IO.File]::WriteAllText($notDirectory, '原文件不能改写', $utf8)
    $badSources.Add($notDirectory)
    $sourceBefore = Tree-Fingerprint $sources
    for ($i = 0; $i -lt $badSources.Count; $i++) {
        $path = Join-Path $run "bad-source-$i.json"
        $bad = Invoke-Cli "bad-source-$i" @('--tournament', $path, '--format', 'single', '--seed-by', 'rating') @('待报名', $code, 'human', $badSources[$i], 'q')
        Check ($bad.Out.Contains('档案读取失败') -and (Read-Tournament $path).Players.Count -eq 0) '坏档案被默认为初始分或部分添加了报名。'
    }
    Check ((Tree-Fingerprint $sources) -ceq $sourceBefore) '拒绝坏档案时改动了来源。'
    $correctedPath = Join-Path $run 'corrected-source.json'
    $corrected = Invoke-Cli 'corrected-source' @('--tournament', $correctedPath, '--format', 'single', '--seed-by', 'rating') @('修正来源', $code, 'easy', 'hard', $badSources[0], $seasonPath, 'q')
    $correctedEvent = Read-Tournament $correctedPath
    Check ($corrected.Out.Contains('不支持 easy') -and $corrected.Out.Contains('档案读取失败') -and $correctedEvent.Players.Count -eq 1 -and
        $correctedEvent.RatingSeeds[0].Rating -eq 1900 -and $correctedEvent.RatingSeeds[0].ComputerLevel.ToString() -eq 'Hard') '非法来源修正后没有仅报名一次。'
    Passed 'corrupt_source_rejected_without_fallback_and_retry'

    $partialPath = Join-Path $run 'partial-registration.json'
    $null = Invoke-Cli 'partial-initial' @('--tournament', $partialPath, '--format', 'single', '--seed-by', 'rating') ((Register-Lines '已保存') + @('q'))
    $partialBefore = [IO.File]::ReadAllText($partialPath, $utf8)
    $tails = @(@('未完成', $code, 'q'), @('未完成', $code), @('未完成', $code, 'human', 'q'), @('未完成', $code, 'expert'))
    for ($i = 0; $i -lt $tails.Count; $i++) {
        $null = Invoke-Cli "partial-exit-$i" @('--tournament', $partialPath) $tails[$i]
        Check ([IO.File]::ReadAllText($partialPath, $utf8) -ceq $partialBefore) 'q 或 EOF 丢失既有报名或留下半份新报名。'
    }
    $namePath = Join-Path $run 'name-not-path.json'
    $null = Invoke-Cli 'name-not-path' @('--tournament', $namePath, '--format', 'single', '--seed-by', 'rating') ((Register-Lines $badSources[0]) + @('q'))
    Check ((Read-Tournament $namePath).Players.Count -eq 1 -and (Read-Tournament $namePath).RatingSeeds[0].Rating -eq 1500) '把选手姓名当作来源路径读取。'
    Passed 'registration_exit_atomicity_and_names_are_not_paths'

    foreach ($format in @('roundrobin', 'single')) {
        $path = Join-Path $run "legacy-$format.json"
        $parameters = @('--tournament', $path)
        $lines = @()
        if ($format -eq 'single') { $parameters += @('--format', 'single') } else { $lines += '1' }
        $lines += @('旧甲', $code, '旧乙', $code, '', 'q')
        $legacyOutput = Invoke-Cli "legacy-$format" $parameters $lines
        $expected = [StreamDuel.Core.Tournament]::new($db, $format, 0, $null)
        $expected.AddPlayer('旧甲', $code); $expected.AddPlayer('旧乙', $code)
        $null = $expected.NextRound()
        Check ([IO.File]::ReadAllText($path, $utf8) -ceq $expected.ToJson()) '默认旧交互改变了报名或赛程。'
        Check (-not $legacyOutput.Out.Contains('选手来源（') -and -not $legacyOutput.Out.Contains('排位快照')) '默认模式新增了输入或输出。'
        $before = Tree-Fingerprint $path
        $rejected = Invoke-Cli "legacy-reject-rating-$format" @('--tournament', $path, '--seed-by', 'rating') @() 1
        Check ($rejected.Err.Contains('不能更改续赛方式') -and (Tree-Fingerprint $path) -ceq $before) '显式 rating 改变了旧赛事方式。'
        $null = Invoke-Cli "legacy-finish-$format" @('--tournament', $path) @('0')
        Check ((Read-Tournament $path).IsFinished) '默认旧赛事无法继续完成。'
    }
    Passed 'legacy_interaction_and_resume_mode_protection'

    # T118 的随机排种子和 rating 排种子不能同时指定，且拒绝必须早于赛事入口。
    $t118SourceBefore = Tree-Fingerprint $sources
    foreach ($existing in @($false, $true)) {
        $path = if ($existing) { $partialPath } else { Join-Path $run 't118-conflict-must-not-exist/event.json' }
        $before = Tree-Fingerprint $path
        foreach ($reverse in @($false, $true)) {
            $parameters = if ($reverse) {
                @('--tournament-seed', '124', '--seed-by', 'rating', '--format', 'single', '--tournament', $path)
            } else {
                @('--tournament', $path, '--format', 'single', '--seed-by', 'rating', '--tournament-seed', '124')
            }
            $rejected = Invoke-Cli "t118-seed-conflict-$existing-$reverse" $parameters @() 1
            Check ($rejected.Out.Length -eq 0 -and $rejected.Err.Contains('--seed-by') -and
                $rejected.Err.Contains('--tournament-seed') -and $rejected.Err -notmatch '未知参数|不认识的参数|unknown') '排种子冲突没有在参数阶段明确拒绝。'
            Check ((Tree-Fingerprint $path) -ceq $before) '排种子冲突修改或创建了赛事文件。'
            if (-not $existing) { Check (-not (Test-Path -LiteralPath (Split-Path $path -Parent))) '排种子冲突创建了赛事目录。' }
        }
    }
    Check ((Tree-Fingerprint $sources) -ceq $t118SourceBefore) '排种子冲突改变了档案来源。'
    Passed 't118_seed_conflict_both_orders_before_io'

    # 用 --auto 创建但先退出报名，才能证明 no-auto 真正覆盖自动模式的默认值。
    foreach ($format in @('single', 'double')) {
        foreach ($reverse in @($false, $true)) {
            $path = Join-Path $run "t118-no-auto-$format-$reverse.json"
            $parameters = if ($reverse) {
                @('--no-auto-tiebreak', '--seed-by', 'rating', '--auto', '--format', $format, '--tournament', $path)
            } else {
                @('--tournament', $path, '--format', $format, '--auto', '--seed-by', 'rating', '--no-auto-tiebreak')
            }
            $linesForNoAuto = (Register-Lines '普通参考分' 'normal') + (Register-Lines '专家参考分' 'expert') + @('q')
            $null = Invoke-Cli "t118-no-auto-create-$format-$reverse" $parameters $linesForNoAuto
            $loaded = Read-Tournament $path
            Check ($loaded.SeedMode.ToString() -eq 'ByRating' -and -not $loaded.AutoTiebreak -and
                $loaded.Round -eq 0 -and $loaded.Players.Count -eq 2 -and ($loaded.EffectiveSeedOrder -join ',') -eq '1,0') 'rating 与 no-auto 组合丢失配置、报名或评分顺序。'
            $resumed = Invoke-Cli "t118-no-auto-resume-$format-$reverse" @('--tournament', $path, '--seed-by', 'rating', '--no-auto-tiebreak') @('', 'q')
            $loaded = Read-Tournament $path
            Check ($loaded.Round -eq 1 -and -not $loaded.AutoTiebreak -and $loaded.RatingSeeds.Count -eq 2 -and
                ($loaded.EffectiveSeedOrder -join ',') -eq '1,0' -and $resumed.Out.Contains('输入结果：')) 'no-auto 续赛未进入手工结果流程，或丢失已冻结评分。'
        }
    }
    Check ((Tree-Fingerprint $sources) -ceq $t118SourceBefore) 'no-auto 兼容用例改变了档案来源。'
    Passed 't118_no_auto_rating_both_orders_and_resume'

    # 原 FF FE FF 同时是坏 JSON；这里仅把合法 JSON 的非默认赛制键内一字节换成 FF。
    $badStringDirectory = Join-Path $sources 'bad-utf8-in-json-string'
    [void][IO.Directory]::CreateDirectory($badStringDirectory)
    $badStringProfile = [StreamDuel.Core.Profile]::new()
    $badStringRating = [StreamDuel.Core.PlayerRating]::new()
    $badStringRating.Rating = 1777
    $badStringRating.Games = 11
    $badStringProfile.Ratings['badX'] = $badStringRating
    $badStringJson = $badStringProfile.ToJson()
    $marker = $badStringJson.IndexOf('"badX"', [StringComparison]::Ordinal)
    Check ($marker -ge 0 -and $marker -eq $badStringJson.LastIndexOf('"badX"', [StringComparison]::Ordinal)) '坏字节夹具标记不唯一。'
    [byte[]]$badStringBytes = $utf8.GetBytes($badStringJson)
    $badStringBytes[$utf8.GetByteCount($badStringJson.Substring(0, $marker + 4))] = 255
    $replacementDecoder = [Text.UTF8Encoding]::new($false, $false)
    $tolerated = [StreamDuel.Core.Profile]::FromJson($replacementDecoder.GetString($badStringBytes))
    $replacementKey = 'bad' + [char]0xFFFD
    Check ($tolerated.Ratings.ContainsKey($replacementKey) -and $tolerated.Ratings[$replacementKey].Rating -eq 1777 -and
        $tolerated.Ratings[$replacementKey].Games -eq 11) '夹具未证明替换解码会得到合法档案。'
    [IO.File]::WriteAllBytes((Join-Path $badStringDirectory 'profile.json'), $badStringBytes)
    $sourceBefore = Tree-Fingerprint $sources
    $badStringPath = Join-Path $run 'bad-utf8-in-json-string.json'
    $rejected = Invoke-Cli 'bad-utf8-in-json-string' @('--tournament', $badStringPath, '--format', 'single', '--seed-by', 'rating') ((Register-Lines '坏字节选手' 'human' $badStringDirectory) + @('q'))
    $loaded = Read-Tournament $badStringPath
    Check ($rejected.Out.Contains('档案读取失败') -and $rejected.Out.Contains('UTF-8') -and $loaded.Players.Count -eq 0 -and
        $loaded.RatingSeeds.Count -eq 0) '合法 JSON 字符串中的非法 UTF-8 被替换后接受，或留下半份评分报名。'
    Check ((Tree-Fingerprint $sources) -ceq $sourceBefore) '拒绝字符串内坏字节时改写了来源或创建了来源锁。'
    Passed 'strict_utf8_rejects_invalid_byte_inside_valid_json_string'

    $bomDirectory = New-Profile 'valid-utf8-bom' 1723 7
    $bomFile = Join-Path $bomDirectory 'profile.json'
    [byte[]]$bomBytes = [byte[]]@(239, 187, 191) + [IO.File]::ReadAllBytes($bomFile)
    [IO.File]::WriteAllBytes($bomFile, $bomBytes)
    $sourceBefore = Tree-Fingerprint $sources
    $bomPath = Join-Path $run 'valid-utf8-bom.json'
    $null = Invoke-Cli 'valid-utf8-bom' @('--tournament', $bomPath, '--format', 'single', '--seed-by', 'rating') ((Register-Lines 'BOM 选手' 'human' $bomDirectory) + @('q'))
    $loaded = Read-Tournament $bomPath
    Check ($loaded.Players.Count -eq 1 -and $loaded.RatingSeeds.Count -eq 1 -and $loaded.RatingSeeds[0].Rating -eq 1723 -and
        $loaded.RatingSeeds[0].Games -eq 7 -and $loaded.RatingSeeds[0].Season -eq 1) '合法 UTF-8 BOM 档案不能只读提取真实评级。'
    Check ((Tree-Fingerprint $sources) -ceq $sourceBefore) '读取 UTF-8 BOM 档案修改了原字节、mtime 或来源目录。'
    Passed 'utf8_bom_profile_accepted_without_source_changes'

    # TournamentFileStore.Save 先独占 .tournament.lock，再比较来源并创建 .tournament.tmp。
    $lockedPath = Join-Path $run 'save-lock/event.json'
    $null = Invoke-Cli 'save-lock-initial' @('--tournament', $lockedPath, '--format', 'single', '--seed-by', 'rating') ((Register-Lines '既有报名' 'human' $bomDirectory) + @('q'))
    $lockPath = $lockedPath + '.tournament.lock'
    Check (Test-Path -LiteralPath $lockPath -PathType Leaf) '初始化未生成生产 Save 使用的持久锁文件。'
    $lockedDirectory = Split-Path $lockedPath -Parent
    $eventBefore = Tree-Fingerprint $lockedDirectory
    $sourceBefore = Tree-Fingerprint $sources
    $heldLock = [IO.FileStream]::new($lockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try {
        $failedSave = Invoke-Cli 'save-lock-reject-registration' @('--tournament', $lockedPath, '--seed-by', 'rating') ((Register-Lines '待保存报名' 'human' $seasonPath) + @('q')) 1
        Check ($failedSave.Err.Contains('.tournament.lock') -and $failedSave.Out.Contains('可选档案目录') -and
            -not $failedSave.Out.Contains('档案读取失败') -and -not $failedSave.Out.Contains('报名失败：')) '没有在有效报名后的真实 Save 独占锁步骤失败。'
    }
    finally { $heldLock.Dispose() }
    Check ((Tree-Fingerprint $lockedDirectory) -ceq $eventBefore -and (Tree-Fingerprint $sources) -ceq $sourceBefore) 'Save 锁冲突改变赛事、来源、mtime 或留下额外临时文件。'
    $loaded = Read-Tournament $lockedPath
    Check ($loaded.Players.Count -eq 1 -and $loaded.RatingSeeds.Count -eq 1 -and $loaded.RatingSeeds[0].Rating -eq 1723) 'Save 锁失败丢失既有报名或持久化了未成功的新报名。'
    $null = Invoke-Cli 'save-lock-recover-registration' @('--tournament', $lockedPath, '--seed-by', 'rating') ((Register-Lines '待保存报名' 'human' $seasonPath) + @('q'))
    $loaded = Read-Tournament $lockedPath
    Check ($loaded.Players.Count -eq 2 -and $loaded.RatingSeeds.Count -eq 2 -and ($loaded.RatingSeeds.Rating -join ',') -eq '1723,1900' -and
        ($loaded.RatingSeeds.Games -join ',') -eq '7,3' -and ($loaded.RatingSeeds.Season -join ',') -eq '1,2') '锁释放后重试没有只新增一份真实赛季快照。'
    Check ((Tree-Fingerprint $sources) -ceq $sourceBefore -and @(Get-ChildItem -LiteralPath $lockedDirectory -Force -Filter '*.tournament.tmp*').Count -eq 0) '锁释放后成功报名改变了来源或遗留临时文件。'
    Passed 'real_save_lock_failure_preserves_files_and_recovers_once'

    # 两名电脑按低分先报名，真实自动完赛覆盖 rating 与 T118 逐局归档的完整接线。
    $autoPath = Join-Path $run 'rating-auto-complete/event.json'
    $autoSourceBefore = Tree-Fingerprint $sources
    $autoLines = (Register-Lines '低分先报名' 'normal') + (Register-Lines '高分后报名' 'expert') + @('')
    $autoCompleted = Invoke-Cli 'rating-auto-complete' @('--tournament', $autoPath, '--format', 'single', '--seed-by', 'rating', '--auto') $autoLines
    $autoEvent = Read-Tournament $autoPath
    $autoJson = [IO.File]::ReadAllText($autoPath, $utf8) | ConvertFrom-Json -AsHashtable
    Check ($autoCompleted.Out.Contains('赛事已结束。') -and $autoEvent.IsFinished -and $null -ne $autoEvent.Champion -and
        $autoEvent.AutoTiebreak -and $autoEvent.SeedMode.ToString() -eq 'ByRating' -and $null -eq $autoEvent.Seed) 'rating 自动赛事没有以原配置完整决出冠军。'
    Check ($autoJson.version -eq 4 -and $autoJson.autoTiebreak -and $autoJson.seedBy -ceq 'rating' -and
        ($autoEvent.EffectiveSeedOrder -join ',') -ceq '1,0' -and ($autoEvent.RatingSeeds.Player -join ',') -ceq '0,1' -and
        ($autoEvent.RatingSeeds.Rating -join ',') -ceq '1400,1800' -and ($autoEvent.RatingSeeds.Games -join ',') -ceq '0,0' -and
        ($autoEvent.RatingSeeds.Season -join ',') -ceq '1,1' -and ($autoJson.ratingSeeds.rating -join ',') -ceq '1400,1800') '真实自动赛事丢失 v4、自动加赛或反报名顺序的冻结评级。'
    Check ($autoEvent.Pairings.Count -eq 1) '两人单败没有恰好一个实际对阵。'
    $autoPairing = $autoEvent.Pairings[0]
    Check (-not $autoPairing.IsBye -and $null -ne $autoPairing.WinnerSide -and $autoPairing.Games.Count -ge 1 -and
        $autoPairing.Games.Count -le 4) '真实自动赛事缺少原场/加赛单局记录。'
    $autoDirectory = Split-Path $autoPath -Parent
    $autoFilesBefore = Tree-Fingerprint $autoDirectory
    $autoReportOutput = Invoke-Cli 'rating-auto-report' @('--tournament-report', $autoPath, '--json')
    $autoReport = $autoReportOutput.Out | ConvertFrom-Json -AsHashtable
    Check ($autoReport.version -eq 2 -and $autoReport.autoTiebreak -and $autoReport.isFinished -and
        $autoReport.champion -eq $autoEvent.Champion) 'rating 自动赛事的真实 CLI 战报未使用 v2 逐局格式或冠军不符。'
    $autoMatches = @($autoReport.rounds | ForEach-Object { $_.matches } | Where-Object { $null -ne $_.player1 })
    Check ($autoMatches.Count -eq 1 -and $autoMatches[0].pairingId -eq $autoPairing.Id -and
        $autoMatches[0].player0 -eq $autoPairing.Player0 -and $autoMatches[0].player1 -eq $autoPairing.Player1 -and
        $autoMatches[0].winnerSide -eq $autoPairing.WinnerSide -and $autoMatches[0].replay.status -ceq 'saved' -and
        $autoMatches[0].games.Count -eq $autoPairing.Games.Count) '战报没有完整对应真实对阵、胜方和已归档单局。'
    $autoLibraryPath = $autoPath + '.replay-library'
    $autoLibrary = [StreamDuel.Core.ReplayLibrary]::OpenReadOnly($autoLibraryPath, $policy)
    $autoReplayIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    for ($attempt = 0; $attempt -lt $autoPairing.Games.Count; $attempt++) {
        $game = $autoPairing.Games[$attempt]
        $shown = $autoMatches[0].games[$attempt]
        Check ($game.Attempt -eq $attempt -and $game.Seed -eq $autoEvent.GameSeed($autoPairing, $attempt) -and
            $shown.attempt -eq $attempt -and $shown.seed -eq $game.Seed -and $shown.winnerSide -eq $game.WinnerSide -and
            $shown.recordHash -ceq $game.RecordHash -and $shown.replay.status -ceq 'saved' -and
            -not [string]::IsNullOrWhiteSpace($shown.replay.id)) '自动赛事战报的单局序号、种子、胜负、摘要或保存状态不符。'
        Check ($autoReplayIds.Add([string]$shown.replay.id)) '不同单局错误地共用同一录像编号。'
        $record = $autoLibrary.Load($shown.replay.id)
        $digest = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($utf8.GetBytes($record.ToJson()))).ToLowerInvariant()
        $replayed = [StreamDuel.Core.Replay]::Run($db, $record)
        Check ($digest -ceq $game.RecordHash -and $record.Seed -eq $game.Seed -and
            @($record.Events | Where-Object { $_.Type -eq [StreamDuel.Core.BattleRecordEventType]::Turn }).Count -gt 0 -and
            $replayed.Phase -eq [StreamDuel.Core.Phase]::Over -and $replayed.Winner -eq $game.WinnerSide) '归档录像不是对应的真实 AI 完局，或不能独立重放。'
        for ($side = 0; $side -lt 2; $side++) {
            $playerId = if ($side -eq 0) { $autoPairing.Player0 } else { $autoPairing.Player1 }
            Check ($record.Names[$side] -ceq $autoEvent.Players[$playerId].Name -and
                ($record.Teams[$side] -join ',') -ceq ($autoEvent.Players[$playerId].Team -join ',')) '实际归档录像的参赛双方与评级排种子后的对阵不一致。'
        }
    }
    Check ($autoReport.coverage.completedGames -eq $autoPairing.Games.Count -and $autoReport.coverage.archivedGames -eq $autoPairing.Games.Count -and
        $autoReport.coverage.missingGames -eq 0 -and $autoReport.coverage.invalidGames -eq 0) 'v2 战报覆盖率未按全部真实单局统计。'
    $autoRecordFiles = @(Get-ChildItem -LiteralPath $autoLibraryPath -File -Filter '*.json' | Where-Object { $_.Name -cne 'index.json' })
    Check ($autoRecordFiles.Count -eq $autoPairing.Games.Count -and (Tree-Fingerprint $autoDirectory) -ceq $autoFilesBefore) '录像数量与真实单局不符，或只读报告/重放改写了赛事与录像库。'
    $autoResumed = Invoke-Cli 'rating-auto-finished-resume' @('--tournament', $autoPath, '--auto')
    Check ($autoResumed.Out.Contains('赛事已结束。') -and (Tree-Fingerprint $autoDirectory) -ceq $autoFilesBefore) '完成后 --auto 续赛重复保存或改写了赛事与录像库。'
    $autoReportAgain = Invoke-Cli 'rating-auto-finished-report' @('--tournament-report', $autoPath, '--json')
    Check ($autoReportAgain.Out -ceq $autoReportOutput.Out -and (Tree-Fingerprint $autoDirectory) -ceq $autoFilesBefore -and
        (Tree-Fingerprint $sources) -ceq $autoSourceBefore) '完成后只读报告或自动续赛改变了原文件指纹或报告内容。'
    Passed 'rating_auto_complete_v4_games_v2_report_and_readonly_finished_resume'
}
catch {
    $failure = $_.Exception.ToString()
    throw
}
finally {
    $summary = [ordered]@{ passed = ($null -eq $failure); checks = $checks.ToArray(); calls = $calls.ToArray(); failure = $failure; directory = $run }
    [IO.File]::WriteAllText((Join-Path $run 'result.json'), ($summary | ConvertTo-Json -Depth 10), $utf8)
    Write-Output "证据目录：$run"
}
