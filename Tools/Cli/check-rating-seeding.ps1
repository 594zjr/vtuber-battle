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
    $start.Environment['DOTNET_CLI_HOME'] = 'D:\vtuber-battle\.dotnet-home'
    $start.Environment['NUGET_PACKAGES'] = 'D:\vtuber-battle\.nuget\packages'
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
