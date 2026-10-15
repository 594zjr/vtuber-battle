#requires -Version 7.0
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$repositoryPrefix = $repository.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
$current = [IO.Path]::GetFullPath((Get-Location).Path)
if (-not $current.Equals($repository, $comparison) -and -not $current.StartsWith($repositoryPrefix, $comparison)) {
    throw '请在本仓库内运行此脚本。'
}
$binaryDirectory = Join-Path $PSScriptRoot "bin/$Configuration/net8.0"
if (-not (Test-Path -LiteralPath (Join-Path $binaryDirectory 'PackCheck.dll') -PathType Leaf)) {
    throw "请先运行 dotnet build Tools/PackCheck -c $Configuration。"
}
$runDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ('bin/text-lint-check/' + [Guid]::NewGuid().ToString('N'))))
if (-not $runDirectory.StartsWith($repositoryPrefix, $comparison)) { throw '验收产物必须位于本仓库内。' }
[void][IO.Directory]::CreateDirectory($runDirectory)
$testTemp = Join-Path $PSScriptRoot 'bin/test-temp'
[void][IO.Directory]::CreateDirectory($testTemp)
$utf8 = [Text.UTF8Encoding]::new($false)
$checks = [Collections.Generic.List[string]]::new()

function Assert-Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "$Message 证据目录：$runDirectory" }
}

function Write-Text([string]$Path, [string]$Text) {
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path))
    [IO.File]::WriteAllText($Path, $Text, $utf8)
}

function Write-Json([string]$Path, $Value) {
    Write-Text $Path ($Value | ConvertTo-Json -Depth 64)
}

function Clean-Display($Value) {
    if ($Value -is [Collections.IDictionary]) {
        foreach ($key in @($Value.Keys)) {
            if ($Value[$key] -is [string] -and $key -in @('name', 'desc', 'node', 'when', 'blurb', 'org', 'personaName', 'personaDesc', 'notice')) {
                $Value[$key] = if ($key -in @('desc', 'blurb', 'personaDesc', 'notice')) { '用于检查的中性说明。' } else { '中性文本' }
            }
            elseif ($key -in @('skillNames', 'fieldNames', 'statusNames')) {
                foreach ($id in @($Value[$key].Keys)) { $Value[$key][$id] = '中性文本' }
            }
            else { Clean-Display $Value[$key] }
        }
    }
    elseif ($Value -is [Collections.IEnumerable] -and $Value -isnot [string]) {
        foreach ($item in $Value) { Clean-Display $item }
    }
}

function Invoke-Check([string]$Name, [string[]]$Parameters, [int]$ExpectedExit = 0) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = 'dotnet'
    $start.ArgumentList.Add($toolDll)
    foreach ($parameter in $Parameters) { $start.ArgumentList.Add($parameter) }
    $start.WorkingDirectory = $runDirectory
    $start.Environment['TEMP'] = $testTemp
    $start.Environment['TMP'] = $testTemp
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = $utf8
    $start.StandardErrorEncoding = $utf8
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    try {
        Assert-Check ($process.Start()) "$Name 无法启动。"
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(30000)) {
            $process.Kill($true)
            $process.WaitForExit(5000) | Out-Null
            throw "$Name 超过 30 秒未退出。"
        }
        $result = [pscustomobject]@{ Out = $stdout.Result; Err = $stderr.Result; Exit = $process.ExitCode }
        Write-Text (Join-Path $runDirectory "$Name.stdout.log") $result.Out
        Write-Text (Join-Path $runDirectory "$Name.stderr.log") $result.Err
        Assert-Check ($result.Exit -eq $ExpectedExit) "$Name 应退出 $ExpectedExit，实际 $($result.Exit)。"
        return $result
    }
    finally { $process.Dispose() }
}

function Assert-Counts([string]$Output, [int]$MinimumErrors = 0, [int]$MinimumWarnings = 0) {
    $summary = [regex]::Match($Output, '文本检查汇总：([0-9]+) 个 error，([0-9]+) 个 warn。')
    Assert-Check $summary.Success '缺少文本专项汇总。'
    $errors = [regex]::Matches($Output, '(?m)^\[文本\] Level=error Where=.+ Id=.* Message=.+\r?$').Count
    $warnings = [regex]::Matches($Output, '(?m)^\[文本\] Level=warn Where=.+ Id=.* Message=.+\r?$').Count
    Assert-Check ($errors -eq [int]$summary.Groups[1].Value -and $warnings -eq [int]$summary.Groups[2].Value) '专项汇总与逐条 Level/Where/Id/Message 不一致。'
    Assert-Check ($errors -ge $MinimumErrors -and $warnings -ge $MinimumWarnings) '缺少预期的文本问题。'
}

function Copy-CleanPack([string]$Id) {
    $pack = $cleanPack | ConvertTo-Json -Depth 64 | ConvertFrom-Json -AsHashtable
    $pack.id = $Id
    return $pack
}

try {
    # 只在 bin 内复制运行所需文件；真实内置内容的问题仍由正常 PackCheck 独立报告。
    $fixtureRoot = Join-Path $runDirectory 'fixture-repo'
    $toolDirectory = Join-Path $fixtureRoot 'Tools/PackCheck/bin'
    [void][IO.Directory]::CreateDirectory($toolDirectory)
    foreach ($file in Get-ChildItem -LiteralPath $binaryDirectory -File) {
        Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $toolDirectory $file.Name)
    }
    $toolDll = Join-Path $toolDirectory 'PackCheck.dll'
    Write-Text (Join-Path $fixtureRoot 'AGENTS.md') '# 文本检查隔离夹具'
    Write-Text (Join-Path $fixtureRoot 'Engine/StreamDuel.Core.csproj') '<Project />'
    Write-Text (Join-Path $fixtureRoot 'Tools/PackCheck/sensitive_words.txt') ''
    $resources = Join-Path $fixtureRoot 'UnityProject/Assets/StreamDuel/Resources'
    $sourceResources = Join-Path $repository 'UnityProject/Assets/StreamDuel/Resources'
    $bannedText = [IO.File]::ReadAllText((Join-Path $sourceResources 'Lint/banned.txt'))
    $bannedPath = Join-Path $resources 'Lint/banned.txt'
    Write-Text $bannedPath $bannedText
    $rules = [IO.File]::ReadAllText((Join-Path $sourceResources 'StreamDuelRules.json')) | ConvertFrom-Json -AsHashtable
    $cleanPack = [IO.File]::ReadAllText((Join-Path $sourceResources 'Packs/original.json')) | ConvertFrom-Json -AsHashtable
    Clean-Display $rules
    Clean-Display $cleanPack
    $cleanPack.id = 'lint_clean'
    Write-Json (Join-Path $resources 'StreamDuelRules.json') $rules
    Write-Json (Join-Path $resources 'Packs/clean.json') $cleanPack
    $external = Join-Path $fixtureRoot 'external-packs'
    [void][IO.Directory]::CreateDirectory($external)

    $clean = Invoke-Check '01-clean' @()
    Assert-Counts $clean.Out
    Assert-Check ($clean.Out.Contains('0 个 error') -and -not $clean.Out.Contains('Level=error')) '中性夹具仍有文本错误。'
    $checks.Add('隔离的中性夹具退出 0，专项统计与逐条输出一致')

    foreach ($length in @(41, 60)) {
        $pack = Copy-CleanPack "lint_warn_$length"
        $pack.streamers[0].blurb = '甲' * $length
        Write-Json (Join-Path $external ($pack.id + '.json')) $pack
        $warning = Invoke-Check ("02-warn-$length") @('--pack-dir', $external, '--pack', $pack.id)
        Assert-Counts $warning.Out 0 1
        Assert-Check (-not $warning.Out.Contains('Level=error') -and $warning.Out.Contains($pack.streamers[0].id)) '41～60 字简介必须只提醒，并标出主播编号。'
    }
    $checks.Add('41 字和 60 字简介仅 warn，退出 0；没有旧硬错误干扰')

    foreach ($kind in @('long-name', 'long-blurb', 'empty', 'banned')) {
        $pack = Copy-CleanPack ("lint_error_" + $kind.Replace('-', '_'))
        switch ($kind) {
            'long-name' { $pack.streamers[0].name = '甲' * 16 }
            'long-blurb' { $pack.streamers[0].blurb = '甲' * 61 }
            'empty' { $pack.streamers[0].blurb = " `t " }
            'banned' {
                $word = @($bannedText -split '\r?\n' | ForEach-Object { $_.Trim() } | Where-Object { $_ -and -not $_.StartsWith('#') })[0]
                $pack.streamers[0].blurb = $word
            }
        }
        Write-Json (Join-Path $external ($pack.id + '.json')) $pack
        $invalid = Invoke-Check ("03-$kind") @('--pack-dir', $external, '--pack', $pack.id) 1
        Assert-Counts $invalid.Out 1
        Assert-Check ($invalid.Out.Contains($pack.streamers[0].id)) '文本错误没有定位到定义编号。'
    }
    $checks.Add('超长姓名、61 字简介、空文本及资源禁用词分别导致 error 和退出 1')

    $customWord = '夹具专用禁词'
    Write-Text $bannedPath ($bannedText.TrimEnd() + "`n" + $customWord + "`n")
    $custom = Copy-CleanPack 'lint_custom_word'
    $custom.streamers[0].blurb = $customWord
    Write-Json (Join-Path $external 'custom-word.json') $custom
    $customResult = Invoke-Check '04-banned-file' @('--pack-dir', $external, '--pack', $custom.id) 1
    Assert-Counts $customResult.Out 1
    Write-Text $bannedPath $bannedText
    $checks.Add('新增资源禁用词立即生效，PackCheck 不只使用编译快照')

    $fan = Copy-CleanPack 'fan_lint_notice'
    $fan.notice = '非官方同人作品，未获授权。'
    Write-Json (Join-Path $external 'fan-notice.json') $fan
    $notice = Invoke-Check '05-notice' @('--pack-dir', $external, '--pack', $fan.id)
    Assert-Counts $notice.Out
    Assert-Check (-not $notice.Out.Contains('Level=error')) '同人声明的例外没有生效。'
    $fan.streamers[0].blurb = '官方展示。'
    Write-Json (Join-Path $external 'fan-notice.json') $fan
    $fanError = Invoke-Check '06-fan-text' @('--pack-dir', $external, '--pack', $fan.id) 1
    Assert-Counts $fanError.Out 1
    $checks.Add('notice 中身份表述按例外放行，其他同人文案仍报 error')

    $roster = Join-Path $fixtureRoot ('docs/roster/' + $fan.id + '.md')
    $marker = '已有名单必须保留'
    Write-Text $roster $marker
    $writeDenied = Invoke-Check '07-write-docs' @('--pack-dir', $external, '--pack', $fan.id, '--write-docs') 1
    Assert-Counts $writeDenied.Out 1
    Assert-Check ([IO.File]::ReadAllText($roster) -ceq $marker) '文本错误覆盖了已有名单。'
    Assert-Check (-not $writeDenied.Out.Contains('已生成：')) '文本错误仍报告已生成名单。'
    $checks.Add('文本错误阻止 --write-docs，已有名单逐字保留')

    $help = Invoke-Check '08-help' @('--help')
    Assert-Check ($help.Out.Contains('Level') -and $help.Out.Contains('error/warn')) '帮助缺少文本检查输出说明。'
    $checks.Add('帮助说明分级、定位与汇总')
    Write-Json (Join-Path $runDirectory 'report.json') @{ status = 'PASS'; checks = $checks.ToArray() }
    Write-Output "PASS：$($checks.Count) 组文本检查 CLI 验收；证据：$runDirectory"
}
catch {
    Write-Json (Join-Path $runDirectory 'report.json') @{ status = 'FAIL'; error = $_.Exception.Message; checks = $checks.ToArray() }
    throw
}
