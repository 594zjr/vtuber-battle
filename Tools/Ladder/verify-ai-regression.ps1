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
    Test-Command 'actual-regression-failure' @('--ai-regression', '--games', '2', '--baseline', $baseline) 1 '电脑强度回归未通过'

    Test-Command 'ordinary-ladder' @('--teams', '2', '--rounds', '1', '--seed', '1', '--out', (Join-Path $output 'ladder')) 0 '已输出 teams.csv、streamers.csv'
    [IO.File]::WriteAllText((Join-Path $output 'report.json'), ($checks | ConvertTo-Json -Depth 4), [Text.UTF8Encoding]::new($false))
    Write-Host ($checks.Count.ToString() + ' 项 CLI 参数、基准错误、实际回归失败与旧天梯路由检查通过；完整强度统计需单独运行。日志：' + $output)
}
finally
{
    Pop-Location
}
