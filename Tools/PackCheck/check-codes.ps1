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
$toolDll = Join-Path $binaryDirectory 'PackCheck.dll'
if (-not (Test-Path -LiteralPath $toolDll -PathType Leaf)) {
    throw "请先运行 dotnet build Tools/PackCheck -c $Configuration。"
}
$runNumber = 1
do {
    $runDirectory = [IO.Path]::GetFullPath((Join-Path $repository "Build/T103/packcheck-codes/run-$runNumber"))
    $runNumber++
} while (Test-Path -LiteralPath $runDirectory)
if (-not $runDirectory.StartsWith($repositoryPrefix, $comparison)) { throw '验收产物必须位于本仓库内。' }
[void][IO.Directory]::CreateDirectory($runDirectory)
$testTemp = Join-Path $runDirectory 'temp'
[void][IO.Directory]::CreateDirectory($testTemp)
$utf8 = [Text.UTF8Encoding]::new($false)
$checks = [Collections.Generic.List[string]]::new()
$invocations = 0

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

function Invoke-Check([string]$Name, [string[]]$Parameters, [int]$ExpectedExit = 0) {
    $script:invocations++
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

function Get-CodeSection([string]$Output, [string]$Kind, [string]$Code) {
    $expression = '(?m)^' + [regex]::Escape("$Kind $Code") + '\r?\n(?:^  [^\r\n]*\r?\n)*'
    $section = [regex]::Match($Output, $expression)
    Assert-Check $section.Success "缺少 $Kind $Code。"
    return $section.Value
}

function Assert-Order([string]$Output, [string]$Kind) {
    $codes = @([regex]::Matches($Output, ('(?m)^' + $Kind + ' ([A-Z][A-Z0-9_]*)\r?$')) |
        ForEach-Object { $_.Groups[1].Value })
    Assert-Check ($codes.Count -gt 0) "没有$Kind`代码。"
    [string[]]$sorted = @($codes)
    [Array]::Sort($sorted, [StringComparer]::Ordinal)
    Assert-Check (($codes -join '|') -ceq ($sorted -join '|')) "$Kind`代码没有按 ordinal 排序。"
    Assert-Check (@($codes | Select-Object -Unique).Count -eq $codes.Count) "$Kind`代码重复。"
    $summary = [regex]::Match($Output, ($Kind + '代码汇总：([0-9]+) 个代码，([0-9]+) 个未使用。'))
    Assert-Check ($summary.Success -and [int]$summary.Groups[1].Value -eq $codes.Count) "$Kind`代码数量与汇总不一致。"
}

function Reverse-Object($Value) {
    $reversed = [ordered]@{}
    $keys = @($Value.Keys)
    [Array]::Reverse($keys)
    foreach ($key in $keys) { $reversed[$key] = $Value[$key] }
    return $reversed
}

try {
    $sourceResources = Join-Path $repository 'UnityProject/Assets/StreamDuel/Resources'
    $all = Invoke-Check '01-real-packs' @('--codes')
    Assert-Order $all.Out '效果'
    Assert-Order $all.Out '人设'
    foreach ($file in Get-ChildItem -LiteralPath (Join-Path $sourceResources 'Packs') -File -Filter '*.json') {
        $pack = [IO.File]::ReadAllText($file.FullName) | ConvertFrom-Json -AsHashtable
        Assert-Check ($all.Out.Contains('内容包 ' + $pack.id + '「' + $pack.name + '」')) "真实包 $($pack.id) 没有出现在报告中。"
    }
    Assert-Check ((Get-CodeSection $all.Out '效果' 'STATUS').Contains('技能 aim_burst') -and
        (Get-CodeSection $all.Out '人设' 'T_REGEN').Contains('人设 healing')) '没有在对应代码下列出基础技能和人设。'
    Assert-Check (-not $all.Out.Contains('技能 warm_up')) '没有效果代码的纯伤害技能被误算为代码引用。'
    Assert-Check ($all.Out.Contains('定义未被主播选入仍算使用')) '没有说明使用统计口径。'
    $repeat = Invoke-Check '02-real-repeat' @('--codes')
    Assert-Check ($all.Out -ceq $repeat.Out -and $all.Err -ceq $repeat.Err) '相同输入的报告不确定。'
    $checks.Add('全部真实包、代码全集、稳定排序与重复运行逐字一致')

    $single = Invoke-Check '03-real-single' @('--codes', '--pack', 'original')
    Assert-Check ($single.Out.Contains('内容包 original「原创主播」')) '单包选择没有返回 original。'
    Assert-Check ([regex]::Matches($single.Out, '(?m)^内容包 ').Count -eq 1) '单包报告包含其他内容包。'
    Assert-Check ($single.Out.Contains('范围：1 个内容包')) '单包报告范围错误。'
    $checks.Add('单包过滤保留完整代码全集，未混入其他包')

    $conflicts = @(
        @('--codes', '--list-packs'), @('--list-packs', '--codes'),
        @('--codes', '--write-docs'), @('--write-docs', '--codes')
    )
    for ($i = 0; $i -lt $conflicts.Count; $i++) {
        $bad = Invoke-Check "04-conflict-$i" $conflicts[$i] 1
        Assert-Check ($bad.Err.Contains('不能与') -and -not $bad.Out.Contains('代码使用报告')) '互斥选项被静默忽略。'
    }
    $unknown = Invoke-Check '05-unknown-pack' @('--codes', '--pack', 'no_such_codes_pack') 1
    Assert-Check ($unknown.Err.Contains('找不到内容包')) '未知包没有明确错误。'
    $missing = Invoke-Check '06-missing-argument' @('--codes', '--pack') 1
    Assert-Check ($missing.Err.Contains('--pack 需要')) '缺少参数没有明确错误。'
    $missingDirectory = Join-Path $runDirectory 'does-not-exist'
    $missingDir = Invoke-Check '07-missing-directory' @('--codes', '--pack-dir', $missingDirectory) 1
    Assert-Check ($missingDir.Err.Contains('目录不存在或无法访问')) '明确指定的不存在目录被忽略。'
    $checks.Add('互斥操作、未知包、缺少参数及不存在目录均返回非零')

    # 修改规则的验收只在隔离副本中进行，真实资源不会被测试改写。
    $fixtureRoot = Join-Path $runDirectory 'fixture-repo'
    $toolDirectory = Join-Path $fixtureRoot 'Tools/PackCheck/bin'
    [void][IO.Directory]::CreateDirectory($toolDirectory)
    foreach ($file in Get-ChildItem -LiteralPath $binaryDirectory -File) {
        Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $toolDirectory $file.Name)
    }
    $toolDll = Join-Path $toolDirectory 'PackCheck.dll'
    Write-Text (Join-Path $fixtureRoot 'AGENTS.md') '# 代码使用报告隔离夹具'
    Write-Text (Join-Path $fixtureRoot 'Engine/StreamDuel.Core.csproj') '<Project />'
    Write-Text (Join-Path $fixtureRoot 'Tools/PackCheck/sensitive_words.txt') ''
    $resources = Join-Path $fixtureRoot 'UnityProject/Assets/StreamDuel/Resources'
    Write-Text (Join-Path $resources 'Lint/banned.txt') ''
    $rules = [IO.File]::ReadAllText((Join-Path $sourceResources 'StreamDuelRules.json')) | ConvertFrom-Json -AsHashtable
    $original = [IO.File]::ReadAllText((Join-Path $sourceResources 'Packs/original.json')) | ConvertFrom-Json -AsHashtable
    foreach ($skill in $rules.skills.Values) { $skill.effects = @() }
    $rules.struggle.effects = @()
    foreach ($persona in $rules.personas.Values) {
        $persona.code = 'T_REGEN'
        $persona.params = @{ frac = 0.01 }
    }
    $rulesPath = Join-Path $resources 'StreamDuelRules.json'
    Write-Json $rulesPath $rules
    Write-Json (Join-Path $resources 'Packs/original.json') $original
    $unused = Invoke-Check '08-unused' @('--codes')
    Assert-Check ((Get-CodeSection $unused.Out '效果' 'RECOIL').Contains('未使用')) '未使用效果码没有显式标记。'
    Assert-Check ((Get-CodeSection $unused.Out '人设' 'T_ENDURE').Contains('未使用')) '未使用人设码没有显式标记。'
    Assert-Check ((Get-CodeSection $unused.Out '人设' 'T_REGEN').Contains('人设 healing')) '已使用人设被遗漏。'
    $effectSummary = [regex]::Match($unused.Out, '效果代码汇总：([0-9]+) 个代码，([0-9]+) 个未使用。')
    Assert-Check ($effectSummary.Success -and $effectSummary.Groups[1].Value -ceq $effectSummary.Groups[2].Value) '空效果定义没有将全部效果码标为未使用。'
    $checks.Add('隔离定义没有引用的效果及人设逐项列出并正确汇总')

    $rules.skills.aim_burst.effects = @(
        @{ at = 'post'; code = 'HEAL'; frac = 0.1 },
        @{ at = 'post'; code = 'HEAL'; frac = 0.2 }
    )
    $rules.struggle.effects = @(@{ at = 'post'; code = 'RECOIL'; frac = 0.1 })
    Write-Json $rulesPath $rules
    $externalDirectory = Join-Path $runDirectory 'external'
    $externalPack = $original | ConvertTo-Json -Depth 64 | ConvertFrom-Json -AsHashtable
    $externalPack.id = 'codes_external'
    $externalPack.name = '代码夹具包'
    $externalPack['skills'] = @{
        zz_unused_in_team = @{
            name = '定义技能'; type = 'game'; cat = 'status'; pp = 5; power = 0
            effects = @(@{ at = 'post'; code = 'FIXED_DAMAGE'; target = 'foe'; n = 1 })
        }
    }
    $externalPack['personas'] = @{
        zz_unused_persona = @{ name = '定义人设'; code = 'T_ENDURE'; node = '受到伤害时'; desc = '用于代码报告。'; params = @{} }
        zz_unselected = @{ name = '未选人设'; code = 'T_FIRST_TURN_PRIO'; node = '排先手时'; desc = '用于代码报告。'; params = @{ prio = 1 } }
    }
    $externalPack.streamers[0].persona = 'zz_unused_persona'
    $externalPack.streamers[0]['personaName'] = '复制人设'
    $externalPath = Join-Path $externalDirectory '99-different-filename.json'
    Write-Json $externalPath $externalPack
    $external = Invoke-Check '09-external-selected' @('--codes', '--pack-dir', $externalDirectory, '--pack', 'codes_external')
    Assert-Check ([regex]::Matches($external.Out, '(?m)^内容包 ').Count -eq 1) '外部单包选择泄漏其他包。'
    Assert-Check ((Get-CodeSection $external.Out '效果' 'FIXED_DAMAGE').Contains('技能 zz_unused_in_team「定义技能」')) '未被主播选入的定义没有统计。'
    $heal = Get-CodeSection $external.Out '效果' 'HEAL'
    Assert-Check ([regex]::Matches($heal, '技能 aim_burst「').Count -eq 1) '基础继承技能遗漏或重复效果未去重。'
    Assert-Check ((Get-CodeSection $external.Out '效果' 'RECOIL').Contains('硬撑 struggle')) '硬撑的效果引用遗漏。'
    $endure = Get-CodeSection $external.Out '人设' 'T_ENDURE'
    Assert-Check ($endure.Contains('人设 zz_unused_persona「定义人设」') -and $endure.Contains('人设 enka#zz_unused_persona「复制人设」')) '原定义或主播复制的人设遗漏。'
    Assert-Check ((Get-CodeSection $external.Out '人设' 'T_FIRST_TURN_PRIO').Contains('人设 zz_unselected')) '没有主播引用的人设定义遗漏。'
    $checks.Add('外部包按 id 选择，继承、未选定义、重复效果、硬撑及复制人设均按口径统计')

    $combined = Invoke-Check '10-pack-order' @('--codes', '--pack-dir', $externalDirectory)
    $packHeaders = @([regex]::Matches($combined.Out, '(?m)^内容包 ([^「]+)「') | ForEach-Object { $_.Groups[1].Value })
    Assert-Check (($packHeaders -join '|') -ceq 'codes_external|original') '内置和外部包没有按 id 排序。'
    $rules.skills = Reverse-Object $rules.skills
    $rules.personas = Reverse-Object $rules.personas
    $externalPack.skills = Reverse-Object $externalPack.skills
    $externalPack.personas = Reverse-Object $externalPack.personas
    Write-Json $rulesPath $rules
    Write-Json $externalPath $externalPack
    $reordered = Invoke-Check '11-definition-order' @('--codes', '--pack-dir', $externalDirectory, '--pack', 'codes_external')
    Assert-Check ($external.Out -ceq $reordered.Out) 'JSON 字典顺序改变了代码使用报告。'
    $checks.Add('包 id 及定义 id 顺序稳定，JSON 定义插入顺序不影响报告')

    $invalidDirectory = Join-Path $runDirectory 'invalid'
    Write-Text (Join-Path $invalidDirectory 'broken.json') '{'
    $broken = Invoke-Check '12-invalid-json' @('--codes', '--pack-dir', $invalidDirectory) 1
    Assert-Check ($broken.Out.Contains('代码使用报告未生成') -and -not $broken.Out.Contains('未使用效果代码：')) '坏包被当作未使用代码继续输出报告。'
    $brokenSelected = Invoke-Check '13-invalid-selected' @('--codes', '--pack-dir', $invalidDirectory, '--pack', 'broken') 1
    Assert-Check ($brokenSelected.Out.Contains('代码使用报告未生成')) '选中的坏包未报告加载失败。'
    $unknownDirectory = Join-Path $runDirectory 'unknown-code'
    $externalPack.skills.zz_unused_in_team.effects[0].code = 'NO_SUCH_EFFECT'
    Write-Json (Join-Path $unknownDirectory 'unknown.json') $externalPack
    $unknownCode = Invoke-Check '14-unknown-code' @('--codes', '--pack-dir', $unknownDirectory) 1
    Assert-Check (($unknownCode.Out + $unknownCode.Err).Contains('NO_SUCH_EFFECT') -and -not $unknownCode.Out.Contains('未使用效果代码：')) '未知代码被静默忽略。'
    $externalPack.skills.zz_unused_in_team.effects[0].code = 'FIXED_DAMAGE'
    $externalPack.personas.zz_unselected.code = 'NO_SUCH_PERSONA'
    Write-Json (Join-Path $unknownDirectory 'unknown.json') $externalPack
    $unknownPersona = Invoke-Check '14-unknown-persona' @('--codes', '--pack-dir', $unknownDirectory) 1
    Assert-Check (($unknownPersona.Out + $unknownPersona.Err).Contains('NO_SUCH_PERSONA') -and -not $unknownPersona.Out.Contains('未使用人设代码：')) '未知人设代码被静默忽略。'
    $checks.Add('坏 JSON、选中坏包及未知代码均拒绝生成误导的未使用报告')

    $normalList = Invoke-Check '15-list-packs' @('--list-packs', '--pack-dir', $externalDirectory)
    Assert-Check ($normalList.Out.Contains('codes_external：代码夹具包') -and $normalList.Out.Contains('内容指纹：')) '原有列包行为改变。'
    Assert-Check (-not $normalList.Out.Contains('代码使用报告')) '正常列包误进入代码报告。'
    $help = Invoke-Check '16-help' @('--help')
    Assert-Check ($help.Out.Contains('--codes') -and $help.Out.Contains('不能与')) '帮助缺少新命令和冲突说明。'
    $checks.Add('原有列包行为保留，帮助说明新命令')

    Write-Json (Join-Path $runDirectory 'report.json') @{ status = 'PASS'; checks = $checks.ToArray(); invocations = $invocations }
    Write-Output "PASS：$($checks.Count) 组代码使用报告 CLI 验收，$invocations 个进程；证据：$runDirectory"
}
catch {
    Write-Json (Join-Path $runDirectory 'report.json') @{ status = 'FAIL'; error = $_.Exception.Message; checks = $checks.ToArray(); invocations = $invocations }
    throw
}
