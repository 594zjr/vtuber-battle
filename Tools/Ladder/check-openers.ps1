#requires -Version 7.0
param([switch]$SkipBuild)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$output = Join-Path $PSScriptRoot ('obj/opener-verification/' + [Guid]::NewGuid().ToString('N'))
$binary = Join-Path $output 'binary'
$utf8 = [Text.UTF8Encoding]::new($false)
$checks = [Collections.Generic.List[object]]::new()
$originalTemp = $env:TEMP
$originalTmp = $env:TMP
New-Item -ItemType Directory -Path $binary, (Join-Path $output 'temp') -Force | Out-Null
$env:TEMP = Join-Path $output 'temp'
$env:TMP = $env:TEMP
$dll = Join-Path $binary 'Ladder.dll'

function Assert-Check([bool]$condition, [string]$message)
{
    if (-not $condition) { throw "$message 证据：$output" }
}

function Invoke-Check([string]$name, [string[]]$parameters, [int]$expectedExit = 0, [string]$errorText = '')
{
    $start = [Diagnostics.ProcessStartInfo]::new('dotnet')
    $start.ArgumentList.Add($dll)
    foreach ($parameter in $parameters) { $start.ArgumentList.Add($parameter) }
    $start.WorkingDirectory = $output
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = $utf8
    $start.StandardErrorEncoding = $utf8
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    try
    {
        [void]$process.Start()
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $finished = $process.WaitForExit(300000)
        if (-not $finished) { $process.Kill($true); [void]$process.WaitForExit(5000) }
        Assert-Check ([Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($stdout, $stderr), 5000)) "$name 输出未关闭。"
        [IO.File]::WriteAllText((Join-Path $output "$name.stdout.txt"), $stdout.Result, $utf8)
        [IO.File]::WriteAllText((Join-Path $output "$name.stderr.txt"), $stderr.Result, $utf8)
        Assert-Check $finished "$name 超时。"
        Assert-Check ($process.ExitCode -eq $expectedExit) "$name 退出码 $($process.ExitCode)，预期 $expectedExit。"
        if ($expectedExit -ne 0)
        {
            Assert-Check ($stdout.Result.Length -eq 0 -and $stderr.Result.Contains($errorText)) "$name 必须明确报错且 stdout 为空。"
            Assert-Check (-not (Test-Path -LiteralPath (Join-Path $output 'ladder_out'))) "$name 不得生成默认结果。"
        }
        else { Assert-Check ($stderr.Result.Length -eq 0) "$name 成功时不应有错误输出。" }
        $checks.Add([pscustomobject]@{ name = $name; exitCode = $process.ExitCode; passed = $true })
        return $stdout.Result
    }
    finally
    {
        if (-not $process.HasExited) { $process.Kill($true); [void]$process.WaitForExit(5000) }
        $process.Dispose()
    }
}

Push-Location $repository
try
{
    if (-not $SkipBuild)
    {
        & dotnet build Tools/Ladder -c Release -warnaserror --nologo *> (Join-Path $output 'build.log')
        Assert-Check ($LASTEXITCODE -eq 0) 'Release 构建失败。'
    }
    $source = Join-Path $PSScriptRoot 'bin/Release/net8.0'
    Assert-Check (Test-Path -LiteralPath (Join-Path $source 'Ladder.dll')) '请先构建 Release。'
    # 子进程和程序集加载使用独立副本，避免验收期间锁住其他测试的构建输出。
    Get-ChildItem -LiteralPath $source | Copy-Item -Destination $binary -Recurse
    $help = Invoke-Check 'help' @('--help')
    foreach ($mode in @('--compare-openers', '--ai-regression', '--compare-expert'))
    { Assert-Check ($help.Contains($mode)) "普通帮助缺少 $mode。" }
    Assert-Check ($help.Contains('--blocks 250') -and $help.Contains('1000')) '缺少默认比较规模。'
    foreach ($mode in @('--compare-expert', '--compare-styles', '--ai-regression'))
    { Assert-Check ((Invoke-Check ($mode.TrimStart('-') + '-help') @($mode, '--help')).Contains($mode)) "$mode 帮助丢失。" }

    [void](Invoke-Check 'blocks-without-mode' @('--blocks', '1') 1 '只能与 --compare-openers')
    [void](Invoke-Check 'missing-blocks' @('--compare-openers', '--blocks') 1 '缺少参数值')
    [void](Invoke-Check 'duplicate-blocks' @('--compare-openers', '--blocks', '1', '--blocks', '2') 1 '不能重复指定')
    [void](Invoke-Check 'duplicate-mode' @('--compare-openers', '--compare-openers') 1 '不能重复指定')
    foreach ($value in @('0', '-1', '+1', '1.5', '536870912', '2147483648'))
    { [void](Invoke-Check ('invalid-blocks-' + $value) @('--compare-openers', '--blocks', $value) 1 '1..536870911') }
    foreach ($mode in @('--ai-regression', '--compare-expert', '--compare-styles'))
    {
        $expected = if ($mode -eq '--ai-regression') { '不能同时使用' } else { '不支持参数：--compare-openers' }
        [void](Invoke-Check ('mixed-' + $mode.TrimStart('-')) @('--compare-openers', $mode) 1 $expected)
        [void](Invoke-Check ('mixed-reverse-' + $mode.TrimStart('-')) @($mode, '--compare-openers') 1 $expected)
    }
    foreach ($entry in @(@('--teams', '2'), @('--rounds', '1'), @('--level', 'hard')))
    { [void](Invoke-Check ('ladder-only-' + $entry[0].TrimStart('-')) (@('--compare-openers') + $entry) 1 '不能混用') }
    foreach ($entry in @(@('--games', '2'), @('--baseline', 'absent.json'), @('--write-baseline', (Join-Path $output 'forbidden.json'))))
    { [void](Invoke-Check ('regression-only-' + $entry[0].TrimStart('-')) (@('--compare-openers') + $entry) 1 '只能与 --ai-regression') }
    Assert-Check (-not (Test-Path -LiteralPath (Join-Path $output 'forbidden.json'))) '不能写入被拒绝的候选基准。'
    Write-Output 'PASS 参数边界、独立模式互斥及既有帮助'

    $first = Join-Path $output 'first'
    $second = Join-Path $output 'second'
    $parameters = @('--compare-openers', '--blocks', '2', '--pack', 'original', '--season', 's1', '--seed', '4294967295')
    $firstText = Invoke-Check 'first' ($parameters + @('--out', $first))
    $secondText = Invoke-Check 'second' ($parameters + @('--out', $second))
    Assert-Check ($firstText -ceq $secondText) '重复运行控制台输出不同。'
    foreach ($file in @('opener-games.csv', 'opener-records.jsonl', 'opener-summary.json'))
    {
        $path = Join-Path $first $file
        Assert-Check ((Get-FileHash -LiteralPath $path).Hash -ceq (Get-FileHash -LiteralPath (Join-Path $second $file)).Hash) "$file 不可复现。"
        $bytes = [IO.File]::ReadAllBytes($path)
        Assert-Check (-not ($bytes.Length -ge 3 -and $bytes[0] -eq 239 -and $bytes[1] -eq 187 -and $bytes[2] -eq 191)) "$file 含 BOM。"
        if ($file -ne 'opener-summary.json')
        { Assert-Check (-not ([IO.File]::ReadAllText($path).Contains("`r"))) "$file 不是 LF 换行。" }
    }
    Write-Output 'PASS 两次真实八局逐字节一致、UTF-8 无 BOM、CSV/JSONL 使用 LF'

    $rows = @(Import-Csv -LiteralPath (Join-Path $first 'opener-games.csv'))
    $jsonLines = [IO.File]::ReadAllLines((Join-Path $first 'opener-records.jsonl'))
    $summary = Get-Content -LiteralPath (Join-Path $first 'opener-summary.json') -Raw | ConvertFrom-Json
    Assert-Check ($rows.Count -eq 8 -and $jsonLines.Count -eq 8) '两组应有八条 CSV 和八条录像。'
    [void][Reflection.Assembly]::LoadFrom((Join-Path $binary 'StreamDuel.Core.dll'))
    $content = Join-Path $binary 'Content'
    $policyPath = Join-Path $content 'PackPolicy.json'
    $db = [StreamDuel.Core.ContentDb]::FromJson([IO.File]::ReadAllText((Join-Path $content 'StreamDuelRules.json')),
        [IO.File]::ReadAllText((Join-Path $content 'Packs/original.json')), [StreamDuel.Core.PackPolicy]::Load($policyPath))
    for ($i = 0; $i -lt $rows.Count; $i++)
    {
        $row = $rows[$i]
        $recordJson = $jsonLines[$i] | ConvertFrom-Json
        $record = [StreamDuel.Core.BattleRecord]::FromJson($jsonLines[$i])
        $battle = [StreamDuel.Core.Replay]::Run($db, $record)
        $game = $i % 4
        $block = [int][Math]::Floor($i / 4)
        $expectedSeed = if ($block -eq 0) { [uint32]::MaxValue } else { 0 }
        $expectedSide = @(0, 1, 1, 0)[$game]
        Assert-Check ([int]$row.block -eq $block + 1 -and [int]$row.game -eq $game + 1 -and
            [uint64]$row.battle_seed -eq $expectedSeed -and $record.Seed -eq $expectedSeed -and
            [int]$row.new_side -eq $expectedSide) "第 $i 行四向配对或种子环回错误。"
        Assert-Check ($record.BanOne -and $record.LeadPick -and $battle.Phase.ToString() -ceq 'Over' -and
            $battle.Winner -eq [int]$row.winner -and $battle.StateHash().ToString('x16') -ceq $row.state_hash) "第 $i 行回放终局不一致。"
        $turns = @($recordJson.events | Where-Object { $_.type -ceq 'turn' }).Count
        Assert-Check ([int]$row.turns -eq $turns -and $turns -ge 1 -and $turns -le [StreamDuel.Core.Battle]::TurnCap) "第 $i 行不是实际结算回合数。"
        $result = if ($battle.Winner -lt 0) { 'draw' } elseif ($battle.Winner -eq $expectedSide) { 'win' } else { 'loss' }
        Assert-Check ($row.new_result -ceq $result) "第 $i 行新策略结果错误。"
        for ($side = 0; $side -lt 2; $side++)
        {
            $team = [string[]]@(1..6 | ForEach-Object { $row."side${side}_slot$_" })
            Assert-Check (($team -join ',') -ceq ($record.Teams[$side] -join ',') -and
                [StreamDuel.Core.TeamCheck]::Validate($db, 's1', $team).Count -eq 0) "第 $i 行队伍、号位或赛季限制错误。"
            $label = if (($game -in @(1, 3)) -xor ($side -eq 1)) { 'B' } else { 'A' }
            Assert-Check ($row."team$side" -ceq $label) "第 $i 行阵容标记错误。"
            $referenceSide = if ($label -eq 'A') { 0 } else { 1 }
            $reference = $rows[$block * 4]
            $referenceTeam = @(1..6 | ForEach-Object { $reference."side${referenceSide}_slot$_" })
            Assert-Check (($team -join ',') -ceq ($referenceTeam -join ',')) "第 $i 行换边改变了原阵容或号位。"
            $ban = @($recordJson.events | Where-Object { $_.type -ceq 'ban' -and $_.side -eq $side })
            $lead = @($recordJson.events | Where-Object { $_.type -ceq 'lead' -and $_.side -eq $side })
            Assert-Check ($ban.Count -eq 1 -and $lead.Count -eq 1) "第 $i 行缺少真实禁用/首发。"
            Assert-Check ($row."ban$side" -ceq $record.Teams[1 - $side][$ban[0].to]) "第 $i 行禁用 id 错误。"
            $remaining = @($team | Where-Object { $_ -cne $row.('ban' + (1 - $side)) })
            Assert-Check ($remaining.Count -eq 5 -and $row."lead$side" -ceq $remaining[$lead[0].to]) "第 $i 行首发 id 错误。"
        }
    }
    Assert-Check ($summary.status -ceq 'completed' -and $null -eq $summary.failure -and $summary.blocks -eq 2 -and
        $summary.expected_games -eq 8 -and $summary.completed_games -eq 8 -and $summary.not_completed_games -eq 0 -and
        $summary.replay_verified_games -eq 8 -and $summary.content_hash -ceq $db.ContentHash() -and
        $summary.new_opening_level -ceq 'hard' -and $summary.old_opening_level -ceq 'normal' -and
        $summary.action_level -ceq 'hard' -and $summary.replacement_level -ceq 'hard') '汇总配置或完整性错误。'
    Assert-Check (($summary.results.new_side -join ',') -ceq '0,1,all') '汇总必须独立报告两侧和总计。'
    foreach ($result in $summary.results)
    {
        $subset = @($rows | Where-Object { $result.new_side -ceq 'all' -or $_.new_side -ceq $result.new_side })
        $wins = @($subset | Where-Object new_result -CEQ 'win').Count
        $draws = @($subset | Where-Object new_result -CEQ 'draw').Count
        $losses = @($subset | Where-Object new_result -CEQ 'loss').Count
        Assert-Check ($result.games -eq $subset.Count -and $result.wins -eq $wins -and $result.draws -eq $draws -and $result.losses -eq $losses -and
            $result.win_rate -eq $wins / $subset.Count -and $result.score_rate -eq ($wins + 0.5 * $draws) / $subset.Count -and
            $result.meets_50_percent -eq ($wins * 2 -ge $subset.Count)) '胜平负或严格胜率统计错误。'
    }
    Write-Output 'PASS 四向配对、季节阵容、真实禁用/首发、回放终局、逐事件回合数和胜率'

    $savedPolicy = [IO.File]::ReadAllBytes($policyPath)
    try
    {
        [IO.File]::WriteAllText($policyPath, '{"disabledPacks":["original"],"reason":{"original":"T90门禁验收"}}', $utf8)
        [void](Invoke-Check 'disabled-pack' @('--compare-openers', '--blocks', '1') 1 'T90门禁验收')
        [IO.File]::WriteAllText($policyPath, '{"disabledPacks":42}', $utf8)
        [void](Invoke-Check 'invalid-policy' @('--compare-openers', '--blocks', '1') 1 '下架策略')
    }
    finally { [IO.File]::WriteAllBytes($policyPath, $savedPolicy) }
    Write-Output 'PASS 下架与损坏策略 fail-closed，未写结果'
    [IO.File]::WriteAllText((Join-Path $output 'checks.json'), ($checks | ConvertTo-Json -Depth 5), $utf8)
    Write-Output "PASS 全部 $($checks.Count) 次 CLI 调用。证据：$output"
}
finally
{
    Pop-Location
    $env:TEMP = $originalTemp
    $env:TMP = $originalTmp
}
