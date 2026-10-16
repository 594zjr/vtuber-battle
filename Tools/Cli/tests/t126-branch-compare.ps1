#requires -Version 7.0
param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug')

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$binary = Join-Path $repository "Tools/Cli/bin/$Configuration/net8.0"
$cli = Join-Path $binary 'Cli.dll'
$core = Join-Path $binary 'StreamDuel.Core.dll'
foreach ($path in @($cli, $core)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "请先构建 Tools/Cli -c $Configuration；本脚本不自动构建。" }
}
$run = [IO.Path]::GetFullPath((Join-Path $repository ('Tools/Cli/obj/T126/' + [Guid]::NewGuid().ToString('N'))))
if (-not $run.StartsWith($repository + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw '验收产物必须位于当前工作树。'
}
[void][IO.Directory]::CreateDirectory($run)
$work = Join-Path $run 'work'
$fixtures = Join-Path $run 'fixtures'
$temp = Join-Path $run 'temp'
foreach ($path in @($work, $fixtures, $temp)) { [void][IO.Directory]::CreateDirectory($path) }
$utf8 = [Text.UTF8Encoding]::new($false, $true)
$checks = [Collections.Generic.List[string]]::new()
$calls = [Collections.Generic.List[object]]::new()
$failure = $null

function Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "$Message 证据目录：$run" }
}

function Tree([string]$Directory) {
    if (-not (Test-Path -LiteralPath $Directory)) { return '<不存在>' }
    return (@(Get-ChildItem -LiteralPath $Directory -Force -Recurse | Sort-Object FullName | ForEach-Object {
        $relative = [IO.Path]::GetRelativePath($Directory, $_.FullName)
        if ($_.PSIsContainer) { 'D:' + $relative }
        else { 'F:' + $relative + ':' + $_.LastWriteTimeUtc.Ticks + ':' + (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    }) -join "`n")
}

function Invoke-Cli([string]$Name, [string[]]$Parameters, [int]$Exit = 0, [string]$Executable = $cli) {
    $evidence = Join-Path $run $Name
    [void][IO.Directory]::CreateDirectory($evidence)
    $workBefore = Tree $work
    $inputBefore = Tree $fixtures
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = 'dotnet'
    $start.ArgumentList.Add($Executable)
    foreach ($argument in $Parameters) { $start.ArgumentList.Add($argument) }
    $start.WorkingDirectory = $work
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
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
        $process.StandardInput.Close()
        Check ($process.WaitForExit(60000)) "$Name 超时。"
        Check ($stdout.Wait(5000) -and $stderr.Wait(5000)) "$Name 输出未结束。"
        $result = [pscustomobject]@{ Out = $stdout.Result; Err = $stderr.Result; Exit = $process.ExitCode }
        [IO.File]::WriteAllText((Join-Path $evidence 'stdout.log'), $result.Out, $utf8)
        [IO.File]::WriteAllText((Join-Path $evidence 'stderr.log'), $result.Err, $utf8)
        $calls.Add([ordered]@{ name = $Name; parameters = $Parameters; exit = $result.Exit; expected = $Exit })
        Check ($result.Exit -eq $Exit) "$Name 退出码应为 $Exit，实际 $($result.Exit)。"
        Check ((Tree $work) -ceq $workBefore -and (Tree $fixtures) -ceq $inputBefore) "$Name 改写了输入、档案、库、索引或新增了文件。"
        if ($Exit -eq 0) { Check ([string]::IsNullOrWhiteSpace($result.Err)) "$Name 成功时出现标准错误。" }
        else { Check ($result.Err.Contains('错误：') -and $result.Out.Length -eq 0) "$Name 没有在输出结局前以中文错误拒绝。" }
        return $result
    }
    finally {
        if ($started -and -not $process.HasExited) { $process.Kill($true); [void]$process.WaitForExit(5000) }
        $process.Dispose()
    }
}

function New-Battle($Db, [uint32]$Seed = 126) {
    $ids = [string[]]@($Db.Streamers | Select-Object -First 6 | ForEach-Object { $_.Id })
    Check ($ids.Length -eq 6) '夹具需要六位主播。'
    $teams = [Collections.Generic.IList[string][]]::new(2)
    $teams[0] = $ids
    $teams[1] = [string[]]$ids.Clone()
    $options = [StreamDuel.Core.BattleOptions]::new()
    $options.Seed = $Seed
    return [StreamDuel.Core.Battle]::new($Db, $teams, [string[]]@(('原始甲' + [char]0x202e + "`n"), '原始乙'), $options)
}

function New-Source($Db, [uint32]$Seed = 126) {
    $battle = New-Battle $Db $Seed
    for ($i = 0; $i -lt 3; $i++) { $battle.ResolveTurn([StreamDuel.Core.BattleAction]::Pass(), [StreamDuel.Core.BattleAction]::Pass()) }
    $battle.Surrender(1)
    return $battle
}

function Save-Record([string]$Name, $Record) {
    $path = Join-Path $fixtures ($Name + '.json')
    [IO.File]::WriteAllText($path, $Record.ToJson(), $utf8)
    return $path
}

function Play-Automatic($Battle) {
    $steps = 0
    while ($Battle.Phase -ne [StreamDuel.Core.Phase]::Over) {
        Check (++$steps -lt 2000) '真实对局没有结束。'
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
    return $Battle
}

function Verify-Output($Actual, $Expected) {
    Check ($Actual.Out.Contains("接手回合：第 $($Expected.Turn) 回合；接手方：第 $($Expected.HumanSide) 方。") -and $Actual.Out.Contains('结论：' + $Expected.Conclusion)) '接手回合、查看方或中文结论没有使用 Core 结果。'
    $blocks = @{
        '原录像' = [regex]::Match($Actual.Out, '(?ms)^原录像：.*?(?=^接手录像：)')
        '接手录像' = [regex]::Match($Actual.Out, '(?ms)^接手录像：.*?(?=^结论：)')
    }
    Check ($blocks['原录像'].Success -and $blocks['接手录像'].Success) '两份结局没有清晰独立且顺序正确的输出区块。'
    foreach ($entry in @(@('原录像', $Expected.Original), @('接手录像', $Expected.Branch))) {
        $block = $blocks[$entry[0]].Value
        $outcome = $entry[1]
        $resultText = if ($outcome.Winner -eq -1) { '平局' } elseif ($outcome.Winner -eq $Expected.HumanSide) { '获胜' } else { '落败' }
        Check ($block.Contains($entry[0] + '：' + $resultText)) '胜负未按接手方视角显示。'
        Check ($block.Contains("结束行动回合数：$($outcome.Turns)；结束时回合号：$($outcome.EndTurn)。")) '行动回合数和结束时回合号混淆。'
        foreach ($side in $outcome.Sides) {
            $name = [StreamDuel.Core.TextInput]::Clean($side.Name, [StreamDuel.Core.TextInputKind]::PlayerName).Text
            $percent = $side.HpPercent.ToString('0.##', [Globalization.CultureInfo]::InvariantCulture)
            Check ($block.Contains("第 $($side.Side) 方（$name）：剩余 $($side.Remaining) 人；总血量 $($side.Hp) / $($side.MaxHp)（$percent%）。")) '对应录像区块内的双方人数或总 HP 比例不符。'
        }
        foreach ($moment in $outcome.KeyMoments) {
            Check ($moment.Turn -ge $Expected.Turn -and $block.Contains('  ' + $moment.Text)) '对应录像区块内的接手后关键回合缺失、被截断或包含接手前回合。'
        }
    }
    Check (-not $Actual.Out.Contains([string][char]0x202e)) '输出泄漏了未清理的昵称控制字符。'
    Check ([regex]::Matches($Actual.Out, '接手后的关键回合：').Count -eq 2) '没有分别展示两份录像的关键回合。'
}

try {
    $assembly = [Reflection.Assembly]::LoadFrom($core)
    Check ([StringComparer]::OrdinalIgnoreCase.Equals([IO.Path]::GetFullPath($assembly.Location), [IO.Path]::GetFullPath($core))) '请在未加载其他 Core 的新 PowerShell 进程运行。'
    $content = Join-Path $binary 'Content'
    $rules = [IO.File]::ReadAllText((Join-Path $content 'StreamDuelRules.json'), $utf8)
    $pack = [IO.File]::ReadAllText((Join-Path $content 'Packs/original.json'), $utf8)
    $policy = [StreamDuel.Core.PackPolicy]::Load((Join-Path $content 'PackPolicy.json'))
    $db = [StreamDuel.Core.ContentDb]::FromJson($rules, $pack, $policy)
    $normal = [StreamDuel.Core.AiLevel]::Normal
    $sourceBattle = New-Source $db
    $source = $sourceBattle.Record
    $sourcePath = Save-Record 'source' $source
    $same = [StreamDuel.Core.ReplayBranch]::Start($db, $source, 2, 0, $normal)
    for ($i = 0; $i -lt 2; $i++) { $same.ResolveTurn([StreamDuel.Core.BattleAction]::Pass(), [StreamDuel.Core.BattleAction]::Pass()) }
    $same.Surrender(1)
    $samePath = Save-Record 'same' $same.Record
    $baseArgs = @('--branch-compare', $sourcePath, $samePath)
    $expected = [StreamDuel.Core.BranchCompare]::Compare($db, $source, $same.Record)
    Check ($expected.Conclusion -ceq '结果相同') '同操作夹具没有保持同样结局。'
    $first = Invoke-Cli 'same' $baseArgs
    Verify-Output $first $expected
    $repeat = Invoke-Cli 'same-repeat' $baseArgs
    Check ($first.Out -ceq $repeat.Out) '同文件重复对照的完整标准输出不同。'
    Check (-not (Test-Path -LiteralPath (Join-Path $work 'saves'))) '对照创建了不存在的存档目录。'
    $checks.Add('PASS_identical_result_deterministic_output_no_save_creation')

    $save = Join-Path $work 'saves'
    $library = Join-Path $save 'replay-library'
    [void][IO.Directory]::CreateDirectory($library)
    [IO.File]::WriteAllText((Join-Path $save 'profile.json'), '{"t126":"现有档案保持字节"}', $utf8)
    [IO.File]::WriteAllText((Join-Path $library 'index.json'), '{"t126":"不维护已有索引"}', $utf8)
    $librarySource = Join-Path $library 'source.json'
    $libraryBranch = Join-Path $library 'branch.json'
    [IO.File]::WriteAllText($librarySource, $source.ToJson(), $utf8)
    [IO.File]::WriteAllText($libraryBranch, $same.Record.ToJson(), $utf8)
    $fromLibraryFiles = Invoke-Cli 'library-files' @('--branch-compare', $librarySource, $libraryBranch)
    Check ($fromLibraryFiles.Out -ceq $first.Out) '库内完整文件路径对照结果不同。'
    $checks.Add('PASS_existing_profile_library_index_and_input_files_unchanged')

    foreach ($side in 0, 1) {
        $changed = [StreamDuel.Core.ReplayBranch]::Start($db, $source, 2, $side, $normal)
        $changed.Surrender(1 - $side)
        $path = Save-Record "winner-side-$side" $changed.Record
        $actual = Invoke-Cli "winner-side-$side" @('--branch-compare', $sourcePath, $path)
        Verify-Output $actual ([StreamDuel.Core.BranchCompare]::Compare($db, $source, $changed.Record))
        Check ($actual.Out.Contains('接手录像：获胜')) '第二方接手获胜被误报为失败。'
    }
    $lost = [StreamDuel.Core.ReplayBranch]::Start($db, $source, 2, 0, $normal)
    $lost.Surrender(0)
    $lostPath = Save-Record 'lost' $lost.Record
    $actual = Invoke-Cli 'lost' @('--branch-compare', $sourcePath, $lostPath)
    Verify-Output $actual ([StreamDuel.Core.BranchCompare]::Compare($db, $source, $lost.Record))
    Check ($actual.Out.Contains('原录像：获胜') -and $actual.Out.Contains('接手录像：落败')) '改变选择后的胜负差异没有展示。'
    $draw = [StreamDuel.Core.ReplayBranch]::Start($db, $source, 2, 1, $normal)
    $draw.OfferDraw(1)
    $draw.RespondDraw(0, $true)
    $drawPath = Save-Record 'draw' $draw.Record
    $actual = Invoke-Cli 'draw' @('--branch-compare', $sourcePath, $drawPath)
    Verify-Output $actual ([StreamDuel.Core.BranchCompare]::Compare($db, $source, $draw.Record))
    Check ($actual.Out.Contains('接手录像：平局')) '和局被误报为胜负。'
    $checks.Add('PASS_changed_outcomes_side_one_and_draw')

    $natural = Play-Automatic (New-Battle $db)
    $naturalPath = Save-Record 'natural' $natural.Record
    $naturalBranch = Play-Automatic ([StreamDuel.Core.ReplayBranch]::Start($db, $natural.Record, 2, 1, $normal))
    $naturalBranchPath = Save-Record 'natural-branch' $naturalBranch.Record
    $comparison = [StreamDuel.Core.BranchCompare]::Compare($db, $natural.Record, $naturalBranch.Record)
    Check ($comparison.Original.KeyMoments.Count -gt 0 -and $comparison.Branch.KeyMoments.Count -gt 0) '真实战斗夹具缺少关键回合。'
    $actual = Invoke-Cli 'natural-key-moments' @('--branch-compare', $naturalPath, $naturalBranchPath)
    Verify-Output $actual $comparison
    $checks.Add('PASS_real_battle_stats_and_complete_post_branch_key_moments')

    $external = Join-Path $fixtures 'external'
    [void][IO.Directory]::CreateDirectory($external)
    $custom = $pack | ConvertFrom-Json -AsHashtable
    $custom.id = 't126_external'
    $customJson = $custom | ConvertTo-Json -Depth 30
    [IO.File]::WriteAllText((Join-Path $external 'custom.json'), $customJson, $utf8)
    $customDb = [StreamDuel.Core.ContentDb]::FromJson($rules, $customJson, $policy)
    $customSource = New-Source $customDb
    $customBranch = [StreamDuel.Core.ReplayBranch]::Start($customDb, $customSource.Record, 2, 1, $normal)
    $customBranch.Surrender(0)
    $customSourcePath = Save-Record 'custom-source' $customSource.Record
    $customBranchPath = Save-Record 'custom-branch' $customBranch.Record
    $actual = Invoke-Cli 'external-pack' @('--branch-compare', $customSourcePath, $customBranchPath, '--pack-dir', $external)
    Verify-Output $actual ([StreamDuel.Core.BranchCompare]::Compare($customDb, $customSource.Record, $customBranch.Record))
    $missingPack = Invoke-Cli 'external-missing' @('--branch-compare', $customSourcePath, $customBranchPath) 1
    Check ($missingPack.Err.Contains('找不到内容包')) '缺少外部内容包没有明确拒绝。'
    $checks.Add('PASS_external_pack_and_missing_pack_gate')

    $different = New-Source $db 127
    $differentPath = Save-Record 'different-source' $different.Record
    foreach ($pair in @(@($differentPath, $samePath), @($sourcePath, $sourcePath), @($samePath, $samePath), @($samePath, $sourcePath))) {
        $actual = Invoke-Cli ("bad-source-" + $calls.Count) @('--branch-compare', $pair[0], $pair[1]) 1
        Check ($actual.Err -match '来源|接手|分支') '来源不符没有明确中文原因。'
    }
    $unfinished = New-Battle $db
    for ($i = 0; $i -lt 3; $i++) { $unfinished.ResolveTurn([StreamDuel.Core.BattleAction]::Pass(), [StreamDuel.Core.BattleAction]::Pass()) }
    $unfinishedPath = Save-Record 'unfinished-source' $unfinished.Record
    $finishedBranch = [StreamDuel.Core.ReplayBranch]::Start($db, $unfinished.Record, 2, 0, $normal)
    $finishedBranch.Surrender(1)
    $finishedBranchPath = Save-Record 'unfinished-source-branch' $finishedBranch.Record
    $null = Invoke-Cli 'unfinished-source' @('--branch-compare', $unfinishedPath, $finishedBranchPath) 1
    $unfinishedBranch = [StreamDuel.Core.ReplayBranch]::Start($db, $source, 2, 0, $normal)
    $unfinishedBranchPath = Save-Record 'unfinished-branch' $unfinishedBranch.Record
    $null = Invoke-Cli 'unfinished-branch' @('--branch-compare', $sourcePath, $unfinishedBranchPath) 1
    $checks.Add('PASS_source_identity_same_file_reversal_and_complete_game_gate')

    foreach ($position in 0, 1) {
        $record = [StreamDuel.Core.BattleRecord]::FromJson($(if ($position -eq 0) { $source.ToJson() } else { $same.Record.ToJson() }))
        $record.Version = [StreamDuel.Core.BattleRecord]::CurrentVersion + 1
        $futurePath = Save-Record "future-$position" $record
        $parameters = if ($position -eq 0) { @('--branch-compare', $futurePath, $samePath) } else { @('--branch-compare', $sourcePath, $futurePath) }
        $actual = Invoke-Cli "future-$position" $parameters 1
        Check ($actual.Err.Contains('更新的版本')) '任一输入的未来版本没有拒绝。'
    }
    $changedContent = [StreamDuel.Core.BattleRecord]::FromJson($source.ToJson())
    $changedContent.ContentHash = '0000000000000000'
    $changedContentPath = Save-Record 'changed-content' $changedContent
    $null = Invoke-Cli 'changed-content' @('--branch-compare', $changedContentPath, $samePath) 1
    $broken = Join-Path $fixtures 'broken.json'
    [IO.File]::WriteAllText($broken, '{', $utf8)
    $invalidUtf8 = Join-Path $fixtures 'invalid-utf8.json'
    [IO.File]::WriteAllBytes($invalidUtf8, [byte[]]@(0xc3, 0x28))
    $utf16Path = Join-Path $fixtures 'utf16.json'
    [IO.File]::WriteAllText($utf16Path, $source.ToJson(), [Text.Encoding]::Unicode)
    foreach ($path in @($broken, $invalidUtf8, $utf16Path, (Join-Path $fixtures 'missing.json'), '20000101_000000_0000000_126')) {
        $actual = Invoke-Cli ("bad-file-" + $calls.Count) @('--branch-compare', $path, $samePath) 1
        if ($path -eq $invalidUtf8 -or $path -eq $utf16Path) { Check ($actual.Err.Contains('不是有效的 UTF-8')) '非法 UTF-8 或 UTF-16 BOM 没有按严格 UTF-8 拒绝。' }
    }
    $bomPath = Join-Path $fixtures 'utf8-bom.json'
    [IO.File]::WriteAllText($bomPath, $source.ToJson(), [Text.UTF8Encoding]::new($true, $true))
    $bom = Invoke-Cli 'utf8-bom' @('--branch-compare', $bomPath, $samePath)
    Check ($bom.Out -ceq $first.Out) 'UTF-8 BOM 改变了对照结果。'
    $checks.Add('PASS_future_fingerprint_invalid_json_strict_utf8_and_file_only')

    $invalid = @(@('--branch-compare'), @('--branch-compare', $sourcePath), @('--branch-compare', '', $samePath),
        @('--branch-compare', $sourcePath, ''), @('--branch-compare', $sourcePath, '--auto'))
    $extras = @(@('--save-dir', 'saves'), @('--auto'), @('--hash'), @('--pack', 'original'), @('--seed', '1'),
        @('--replay', $sourcePath), @('--replay-best-effort'), @('--branch', $sourcePath, '--turn', '2', '--side', '0'),
        @('--tournament', 'must-not-exist.json'), @('--tournament-report', 'must-not-exist.json'), @('--report', 'must-not-exist.md'),
        @('--card', 'bell'), @('--cards', 'original'), @('--hotseat'), @('--host', '0'), @('--watch', '127.0.0.1:9'),
        @('--spectator-chat', 'on'), @('--spectator-chat', 'off'), @('--watch-name', '对照验收观众'),
        @('--new-season'), @('--rating-history'), @('--share'), @('--replays'), @('--json'), @('--turn', '2'))
    foreach ($extra in $extras) { $invalid += ,($baseArgs + $extra); $invalid += ,($extra + $baseArgs) }
    $invalid += ,($baseArgs + $baseArgs)
    $invalid += ,($baseArgs + @('--pack-dir', $external, '--pack-dir', $external))
    for ($i = 0; $i -lt $invalid.Count; $i++) {
        $actual = Invoke-Cli "invalid-$i" $invalid[$i] 1
        Check ($actual.Err.Contains('--branch-compare') -or $actual.Err.Contains('--pack-dir')) '参数错误没有指出对照命令或重复目录参数。'
    }
    $help = Invoke-Cli 'help' @('--help')
    Check ($help.Out.Contains('--branch-compare') -and $help.Out.Contains('不支持库编号或 --save-dir')) '帮助缺少文件边界或只读对照入口。'
    $checks.Add('PASS_required_duplicate_and_bidirectional_mode_exclusion')

    $policyBinary = Join-Path $run 'policy-binary'
    [void][IO.Directory]::CreateDirectory($policyBinary)
    Copy-Item -Path (Join-Path $binary '*') -Destination $policyBinary -Recurse
    [IO.File]::WriteAllText((Join-Path $policyBinary 'Content/PackPolicy.json'), '{"disabledPacks":["original"],"reason":{"original":"对照验收下架"}}', $utf8)
    $actual = Invoke-Cli 'disabled-original' $baseArgs 1 (Join-Path $policyBinary 'Cli.dll')
    Check ($actual.Err.Contains('已下架')) '对照绕过了下架政策。'
    $checks.Add('PASS_disabled_pack_no_input_or_profile_writes')
}
catch { $failure = $_.ToString() + "`n" + $_.ScriptStackTrace }
finally {
    $report = [ordered]@{ status = $(if ($null -eq $failure) { 'PASS' } else { 'FAIL' }); checks = @($checks.ToArray()); calls = @($calls.ToArray()); error = $failure }
    [IO.File]::WriteAllText((Join-Path $run 'report.json'), ($report | ConvertTo-Json -Depth 10), $utf8)
}
foreach ($check in $checks) { Write-Host $check }
Write-Host "T126 CLI 验收证据：$run"
if ($null -ne $failure) { throw $failure }
