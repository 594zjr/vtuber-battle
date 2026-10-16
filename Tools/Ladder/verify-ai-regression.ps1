#requires -Version 7.0
param([switch]$SkipBuild)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$repository = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$output = Join-Path $PSScriptRoot ('obj/ai-regression-verification/' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $output -Force | Out-Null
$env:TEMP = Join-Path $output 'temp'
$env:TMP = $env:TEMP
New-Item -ItemType Directory -Path $env:TEMP -Force | Out-Null
$dll = Join-Path $PSScriptRoot 'bin/Release/net8.0/Ladder.dll'
$checks = [System.Collections.Generic.List[object]]::new()

function Test-Command([string]$name, [string[]]$arguments, [int]$exitCode, [string]$expectedText)
{
    $stdout = Join-Path $output ($name + '.stdout.txt')
    $stderr = Join-Path $output ($name + '.stderr.txt')
    & dotnet $dll @arguments 1> $stdout 2> $stderr
    $actualExit = $LASTEXITCODE
    $text = [IO.File]::ReadAllText($stdout) + [IO.File]::ReadAllText($stderr)
    if ($actualExit -ne $exitCode -or -not $text.Contains($expectedText))
    {
        throw "$name 失败：退出码 $actualExit（预期 $exitCode），需要输出「$expectedText」。日志：$output"
    }
    $checks.Add([pscustomobject]@{ name = $name; exitCode = $actualExit; passed = $true })
}

function Get-ExpectedRegressionExit($reference, $candidate)
{
    foreach ($key in @('schemaVersion', 'samplingVersion', 'packId', 'gamesPerPair', 'seed', 'maxTurns'))
    {
        if ($reference.$key -cne $candidate.$key) { throw "候选元数据不匹配：$key" }
    }
    if (@($candidate.matchups).Count -ne 6) { throw '候选须包含六组真实计数。' }
    $seen = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $failed = $false
    foreach ($pair in $candidate.matchups)
    {
        if (-not $seen.Add($pair.lower + '/' + $pair.higher)) { throw '候选难度对局重复。' }
        $prior = @($reference.matchups | Where-Object { $_.lower -ceq $pair.lower -and $_.higher -ceq $pair.higher })
        if ($prior.Count -ne 1) { throw '候选难度组合与基准不一致。' }
        $games = [long]$pair.lowerWins + [long]$pair.higherWins + [long]$pair.draws
        if ($games -ne $candidate.gamesPerPair -or $pair.lowerWins -lt 0 -or $pair.higherWins -lt 0 -or $pair.draws -lt 0 -or
            $pair.truncated -lt 0 -or $pair.truncated -gt $pair.draws) { throw '候选胜负计数不合法。' }
        # 用真实整数计数独立判定；真实 AI 变强后全胜应通过，不能把失败写成固定预期。
        $allowedWinsDrop = ([decimal]$reference.allowedDrop + [decimal]0.000000000001) * $games
        if ($pair.truncated -gt 0 -or [long]$pair.higherWins * 2 -le $games -or
            [long]$prior[0].higherWins - [long]$pair.higherWins -gt $allowedWinsDrop) { $failed = $true }
    }
    if ($failed) { return 1 }
    return 0
}

function Assert-PrintedCounts([string]$name, $candidate)
{
    $text = [IO.File]::ReadAllText((Join-Path $output ($name + '.stdout.txt')))
    $labels = @{ Easy = '简单'; Normal = '普通'; Hard = '困难'; Expert = '专家' }
    foreach ($pair in $candidate.matchups)
    {
        $row = '| ' + $labels[$pair.higher] + ' / ' + $labels[$pair.lower] + ' | ' + $pair.higherWins + ' | ' +
            $pair.lowerWins + ' | ' + $pair.draws + ' | ' + $pair.truncated + ' | '
        if (-not $text.Contains($row)) { throw "$name 缺少与候选一致的真实统计行：$row" }
    }
}

function Test-ControlledStatistics([string]$name, $template, [int]$higherWins, [int]$draws, [int]$truncated,
    [double]$allowedDrop, [int]$expectedFailures, [string]$expectedText)
{
    $reference = $template | ConvertTo-Json -Depth 6 | ConvertFrom-Json
    $reference.gamesPerPair = 20
    $reference.allowedDrop = $allowedDrop
    foreach ($pair in $reference.matchups) { $pair.lowerWins = 0; $pair.higherWins = 20; $pair.draws = 0; $pair.truncated = 0 }
    $referenceJson = $reference | ConvertTo-Json -Depth 6
    $candidate = $referenceJson | ConvertFrom-Json
    $candidate.matchups[0].higherWins = $higherWins
    $candidate.matchups[0].lowerWins = 20 - $higherWins - $draws
    $candidate.matchups[0].draws = $draws
    $candidate.matchups[0].truncated = $truncated
    $candidateJson = $candidate | ConvertTo-Json -Depth 6
    [IO.File]::WriteAllText((Join-Path $output ($name + '.baseline.json')), $referenceJson, [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $output ($name + '.candidate.json')), $candidateJson, [Text.UTF8Encoding]::new($false))
    # 从公开 JSON 协议构造统计结果，直接检验 Core 判定，不依赖真实选招必须输或平。
    $baselineObject = [StreamDuel.Core.AiRegressionBaseline]::FromJson($referenceJson)
    $candidateObject = [StreamDuel.Core.AiRegressionBaseline]::FromJson($candidateJson)
    $failures = $baselineObject.Check($candidateObject.Result)
    $failureText = $failures -join "`n"
    [IO.File]::WriteAllText((Join-Path $output ($name + '.failures.txt')), $failureText, [Text.UTF8Encoding]::new($false))
    if ($failures.Count -ne $expectedFailures -or ($expectedFailures -gt 0 -and -not $failureText.Contains($expectedText)))
    {
        throw "$name 失败：预期 $expectedFailures 项统计失败及「$expectedText」，实际：$failureText"
    }
    $expectedExit = if ($expectedFailures -eq 0) { 0 } else { 1 }
    if ((Get-ExpectedRegressionExit $reference $candidate) -ne $expectedExit) { throw "$name 的独立计数判定与固定期望不符。" }
    $checks.Add([pscustomobject]@{ name = $name; kind = 'controlled-core-statistics'; failures = $failures.Count; passed = $true })
}

Push-Location $repository
try
{
    if (-not $SkipBuild)
    {
        & dotnet build Tools/Ladder -c Release --nologo *> (Join-Path $output 'build.log')
        if ($LASTEXITCODE -ne 0) { throw "构建失败，日志：$output" }
    }
    if (-not (Test-Path -LiteralPath $dll -PathType Leaf)) { throw "缺少 Release 程序，请先构建 Tools/Ladder。" }

    Test-Command 'help' @('--help') 0 '--ai-regression'
    Test-Command 'duplicate-mode' @('--ai-regression', '--ai-regression') 1 '不能重复指定'
    Test-Command 'regression-with-expert-comparison' @('--ai-regression', '--compare-expert') 1 '专家比较模式不支持参数：--ai-regression'
    Test-Command 'regression-with-style-comparison' @('--ai-regression', '--compare-styles') 1 '风格比较模式不支持参数：--ai-regression'
    Test-Command 'unknown' @('--ai-regression', '--unknown') 1 '未知参数'
    Test-Command 'missing-games' @('--ai-regression', '--games') 1 '缺少参数值'
    Test-Command 'missing-baseline' @('--ai-regression', '--baseline') 1 '缺少参数值'
    Test-Command 'empty-baseline' @('--ai-regression', '--baseline', ' ') 1 '非空'
    Test-Command 'empty-write' @('--ai-regression', '--write-baseline', ' ') 1 '非空'
    Test-Command 'conflicting-files' @('--ai-regression', '--baseline', 'a.json', '--write-baseline', 'b.json') 1 '不能同时使用'
    $invalidGames = @('0', '1', '3', '-2', '+2', '2.5', '2147483647', '2147483648')
    for ($i = 0; $i -lt $invalidGames.Count; $i++)
    {
        Test-Command ('invalid-games-' + $i) @('--ai-regression', '--games', $invalidGames[$i]) 1 '偶数'
    }
    $oldOptions = @(
        @('--teams', '2'), @('--rounds', '1'), @('--level', 'normal'), @('--season', 's1'), @('--out', $output)
    )
    for ($i = 0; $i -lt $oldOptions.Count; $i++)
    {
        Test-Command ('ladder-option-' + $i) (@('--ai-regression') + $oldOptions[$i]) 1 '不能与 --ai-regression 同时使用'
    }
    Test-Command 'games-without-mode' @('--games', '2') 1 '只能与 --ai-regression'
    Test-Command 'baseline-without-mode' @('--baseline', 'a.json') 1 '只能与 --ai-regression'
    Test-Command 'write-without-mode' @('--write-baseline', 'a.json') 1 '只能与 --ai-regression'
    Test-Command 'missing-file' @('--ai-regression', '--baseline', (Join-Path $output 'missing.json')) 1 '错误：'

    $badJson = Join-Path $output 'invalid.json'
    [IO.File]::WriteAllText($badJson, '{}', [Text.UTF8Encoding]::new($false))
    Test-Command 'invalid-file' @('--ai-regression', '--baseline', $badJson) 1 '错误：'

    $pairs = [System.Collections.Generic.List[object]]::new()
    $levels = @('Easy', 'Normal', 'Hard', 'Expert')
    for ($lower = 0; $lower -lt 3; $lower++)
    {
        for ($higher = $lower + 1; $higher -lt 4; $higher++)
        {
            $pairs.Add([ordered]@{ lower = $levels[$lower]; higher = $levels[$higher]; lowerWins = 0; higherWins = 2; draws = 0; truncated = 0 })
        }
    }
    $fixture = [ordered]@{
        schemaVersion = 1; samplingVersion = 1; packId = 'original'; contentHash = '0000000000000000'
        gamesPerPair = 2; seed = 1; maxTurns = 120; allowedDrop = 0.05; matchups = @($pairs.ToArray())
    }
    $baseline = Join-Path $output 'metadata-fixture.json'
    [IO.File]::WriteAllText($baseline, ($fixture | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
    Test-Command 'mismatch-games' @('--ai-regression', '--games', '4', '--baseline', $baseline) 1 '与基准不一致'
    Test-Command 'mismatch-seed' @('--ai-regression', '--games', '2', '--seed', '2', '--baseline', $baseline) 1 '与基准不一致'
    Test-Command 'mismatch-pack' @('--ai-regression', '--pack', 'original_wave2', '--games', '2', '--baseline', $baseline) 1 '与基准不一致'
    $fixture.maxTurns = 121
    [IO.File]::WriteAllText($baseline, ($fixture | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
    Test-Command 'mismatch-turn-cap' @('--ai-regression', '--games', '2', '--baseline', $baseline) 1 '与基准不一致'
    $fixture.maxTurns = 120
    $fixture.samplingVersion = 2
    [IO.File]::WriteAllText($baseline, ($fixture | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
    Test-Command 'unsupported-sampling' @('--ai-regression', '--games', '2', '--baseline', $baseline) 1 '抽样版本'

    Test-Command 'bundled-baseline' @('--ai-regression', '--games', '2') 1 '与基准不一致'
    $fixture.samplingVersion = 1
    $fixture.allowedDrop = 0
    [IO.File]::WriteAllText($baseline, ($fixture | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
    $candidatePath = Join-Path $output 'actual-candidate.json'
    & dotnet $dll --ai-regression --games 2 --write-baseline $candidatePath 1> (Join-Path $output 'actual-candidate.stdout.txt') 2> (Join-Path $output 'actual-candidate.stderr.txt')
    $candidateExit = $LASTEXITCODE
    if (-not (Test-Path -LiteralPath $candidatePath -PathType Leaf)) { throw "未写入真实候选基准。日志：$output" }
    $candidate = [IO.File]::ReadAllText($candidatePath) | ConvertFrom-Json
    $writeExit = Get-ExpectedRegressionExit $candidate $candidate
    $writeText = [IO.File]::ReadAllText((Join-Path $output 'actual-candidate.stdout.txt')) + [IO.File]::ReadAllText((Join-Path $output 'actual-candidate.stderr.txt'))
    $writeVerdict = if ($writeExit -eq 0) { '电脑强度回归通过。' } else { '电脑强度回归未通过' }
    if ($candidateExit -ne $writeExit -or -not $writeText.Contains('已写入实际测得的候选基准') -or -not $writeText.Contains($writeVerdict))
    {
        throw "实际候选退出码 $candidateExit 与真实计数判定 $writeExit 不符，或缺少候选/判定输出。日志：$output"
    }
    Assert-PrintedCounts 'actual-candidate' $candidate
    $checks.Add([pscustomobject]@{ name = 'actual-candidate'; exitCode = $candidateExit; passed = $true })
    $regressionExit = Get-ExpectedRegressionExit $fixture $candidate
    $regressionVerdict = if ($regressionExit -eq 0) { '电脑强度回归通过。' } else { '电脑强度回归未通过' }
    Test-Command 'actual-regression-verdict' @('--ai-regression', '--games', '2', '--baseline', $baseline) $regressionExit $regressionVerdict
    Assert-PrintedCounts 'actual-regression-verdict' $candidate

    [Reflection.Assembly]::LoadFrom((Join-Path (Split-Path $dll -Parent) 'StreamDuel.Core.dll')) | Out-Null
    Test-ControlledStatistics 'controlled-full-wins' $fixture 20 0 0 0 0 ''
    Test-ControlledStatistics 'controlled-half-wins' $fixture 10 0 0 1 1 '胜率未超过 50%'
    Test-ControlledStatistics 'controlled-drop' $fixture 19 0 0 0 1 '胜率下降超过允许幅度'
    Test-ControlledStatistics 'controlled-unfinished' $fixture 19 1 1 1 1 '存在未正常结束的对局'

    Test-Command 'ordinary-ladder' @('--teams', '2', '--rounds', '1', '--seed', '1', '--out', (Join-Path $output 'ladder')) 0 '已输出 teams.csv、streamers.csv'
    [IO.File]::WriteAllText((Join-Path $output 'report.json'), ($checks | ConvertTo-Json -Depth 4), [Text.UTF8Encoding]::new($false))
    Write-Host ($checks.Count.ToString() + ' 项 CLI 参数、基准错误、真实统计判定、可控失败分支与旧天梯路由检查通过；完整强度统计需单独运行。日志：' + $output)
}
finally
{
    Pop-Location
}
