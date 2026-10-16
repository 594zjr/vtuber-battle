#requires -Version 7.0
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [ValidateRange(1, 1800)][int]$TimeoutSeconds = 300
)

# 可留在 Engine.Tests/obj/t95-review 或复制到 Tools/Sim；必须先构建，不触发构建。
# 两档 tune 各 jobs=1/4 各重复两次，固定 2轮×2候选×8局+8局留出；compare 三次各8局。
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'Sim.csproj') -PathType Leaf) {
    $repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
}
elseif ((Split-Path $PSScriptRoot -Leaf) -eq 't95-review' -and
    (Split-Path (Split-Path $PSScriptRoot -Parent) -Leaf) -eq 'obj' -and
    (Split-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) -Leaf) -eq 'Engine.Tests') {
    $repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
}
else { throw '脚本必须放在 Engine.Tests/obj/t95-review 或 Tools/Sim。' }
$prefix = $repository.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$current = [IO.Path]::GetFullPath((Get-Location).Path)
if ($current -ne $repository -and -not $current.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw '请在本仓库内运行。' }
$output = Join-Path $repository "Tools/Sim/bin/$Configuration/net8.0"
$simDll = Join-Path $output 'Sim.dll'
$coreDll = Join-Path $output 'StreamDuel.Core.dll'
foreach ($file in @($simDll, $coreDll)) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "请先构建 Tools/Sim -c $Configuration。" }
}
$runDirectory = [IO.Path]::GetFullPath((Join-Path $repository ('Engine.Tests/obj/t95-determinism/' + [Guid]::NewGuid().ToString('N'))))
if (-not $runDirectory.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw '产物目录必须位于本仓库内。' }
[void][IO.Directory]::CreateDirectory($runDirectory)
$tempDirectory = Join-Path $runDirectory 'temp'
[void][IO.Directory]::CreateDirectory($tempDirectory)
$utf8 = [Text.UTF8Encoding]::new($false)
$checks = [Collections.Generic.List[string]]::new()
$invocations = [Collections.Generic.List[object]]::new()
$inputs = @{}
$inputHashes = @{}
$stage = '导出权重'

function Assert-Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Assert-InputsUnchanged {
    foreach ($name in $inputs.Keys) {
        Assert-Check ((Get-FileHash -LiteralPath $inputs[$name] -Algorithm SHA256).Hash -ceq $inputHashes[$name]) "输入 $name 被改写。"
    }
}

function Without-Jobs([string]$Json) {
    # 只归一化顶层 jobs 的数字；保留其它字符、空白、字段、顺序和值以免掩盖差异。
    $pattern = '(?m)^(  "jobs": )[0-9]+(,\r?)$'
    Assert-Check ([regex]::Matches($Json, $pattern).Count -eq 1) '报告应恰有一个顶层 jobs。'
    return [regex]::Replace($Json, $pattern, '${1}0${2}').TrimEnd([char[]]"`r`n")
}

function Invoke-Sim([string]$Name, [string[]]$Parameters, [bool]$Tune) {
    $reportPath = Join-Path $runDirectory "$Name.report.json"
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = 'dotnet'
    $start.ArgumentList.Add($simDll)
    foreach ($parameter in $Parameters) { $start.ArgumentList.Add($parameter) }
    $start.ArgumentList.Add('--report')
    $start.ArgumentList.Add($reportPath)
    $start.WorkingDirectory = $runDirectory
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = $utf8
    $start.StandardErrorEncoding = $utf8
    $start.Environment['TEMP'] = $tempDirectory
    $start.Environment['TMP'] = $tempDirectory
    $start.Environment['DOTNET_SKIP_FIRST_TIME_EXPERIENCE'] = '1'
    $start.Environment['DOTNET_gcServer'] = '1'
    $start.Environment['DOTNET_PROCESSOR_COUNT'] = '4'
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    $started = $false
    try {
        $started = $process.Start()
        Assert-Check $started "$Name 无法启动。"
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $process.StandardInput.Close()
        Assert-Check ($process.WaitForExit($TimeoutSeconds * 1000)) "$Name 超时。"
        $outText = $stdout.Result
        $errText = $stderr.Result
        [IO.File]::WriteAllText((Join-Path $runDirectory "$Name.stdout.json"), $outText, $utf8)
        [IO.File]::WriteAllText((Join-Path $runDirectory "$Name.stderr.log"), $errText, $utf8)
        $invocations.Add([ordered]@{ name = $Name; parameters = $Parameters; exit = $process.ExitCode })
        Assert-Check ($process.ExitCode -eq 0) "$Name 进程失败。"
        Assert-Check ($errText -notmatch '(?m)^\[错误\]|^错误：') "$Name 含错误提示。"
        if (-not $Tune) { Assert-Check ($errText.Length -eq 0) "$Name 对比模式不应有错误输出。" }
        $json = [IO.File]::ReadAllText($reportPath)
        $report = ConvertFrom-Json -InputObject $json -AsHashtable
        Assert-Check ($report.status -ceq 'completed' -and $report.packId -ceq 'original') "$Name 未完成或包不正确。"
        if (-not $Tune) { Assert-Check ($outText.TrimEnd([char[]]"`r`n") -ceq $json.TrimEnd([char[]]"`r`n")) "$Name stdout 与报告不同。" }
        Assert-InputsUnchanged
        return [pscustomobject]@{ Out = $outText.TrimEnd([char[]]"`r`n"); Json = $json; Report = $report }
    }
    finally {
        if ($started -and -not $process.HasExited) { $process.Kill($true); [void]$process.WaitForExit(5000) }
        $process.Dispose()
    }
}

try {
    [void][Reflection.Assembly]::LoadFrom($coreDll)
    $profiles = [ordered]@{ normal = [StreamDuel.Core.AiWeights]::Default; hard = [StreamDuel.Core.AiWeights]::HardDefault; legacy = [StreamDuel.Core.AiWeights]::Legacy }
    foreach ($name in $profiles.Keys) {
        $inputs[$name] = Join-Path $runDirectory "$name.json"
        [IO.File]::WriteAllText($inputs[$name], $profiles[$name].ToJson(), $utf8)
        $inputHashes[$name] = (Get-FileHash -LiteralPath $inputs[$name] -Algorithm SHA256).Hash
    }
    [IO.File]::WriteAllText((Join-Path $runDirectory 'inputs.json'),
        ([ordered]@{ hashes = $inputHashes; binaries = @(Get-FileHash -LiteralPath $simDll, $coreDll -Algorithm SHA256 | Select-Object Path, Hash) } | ConvertTo-Json -Depth 6), $utf8)
    foreach ($level in @('normal', 'hard')) {
        $reference = $null
        $seed = if ($level -eq 'normal') { 2095001 } else { 2096001 }
        foreach ($jobs in @(1, 4)) {
            foreach ($repeat in @(1, 2)) {
                $stage = "$level-jobs$jobs-repeat$repeat"
                $parameters = [string[]]@('--tune', '--pack', 'original', '--level', $level, '--rounds', '2', '--candidates', '2',
                    '--games', '8', '--holdout-games', '8', '--seed', $seed.ToString(), '--jobs', $jobs.ToString(),
                    '--perturbation', '0.3', '--changes', '3', '--weights', $inputs[$level], '--baseline', $inputs.legacy)
                $run = Invoke-Sim $stage $parameters $true
                $report = $run.Report
                Assert-Check ($report.schema -ceq 'streamduel-ai-tuning-v2' -and $report.level -ceq $level -and $report.jobs -eq $jobs -and $report.seed -eq $seed) "$stage 元数据错误。"
                Assert-Check ($report.requestedRounds -eq 2 -and $report.rounds.Count -eq 2 -and $report.candidatesPerRound -eq 2 -and $report.gamesPerCandidate -eq 8 -and $report.holdoutGames -eq 8) "$stage 不是约定的小样本调参。"
                foreach ($round in $report.rounds) {
                    Assert-Check ($round.candidates.Count -eq 2 -and $round.seedCount -eq 1) "$stage 候选或种子块数错误。"
                    foreach ($candidate in $round.candidates) { Assert-Check ($candidate.result.games -eq 8 -and $candidate.result.wins + $candidate.result.losses + $candidate.result.draws -eq 8) "$stage 候选没有完成八局。" }
                }
                Assert-Check ($report.holdout.result.games -eq 8 -and $report.holdout.battleSeedStart -eq $seed + 2) "$stage 留出局数或独立种子错误。"
                $emitted = ConvertFrom-Json -InputObject $run.Out -AsHashtable
                Assert-Check ($emitted.Count -eq 36 -and $report.finalWeights.Count -eq 36) "$stage 输出权重不完整。"
                foreach ($key in $emitted.Keys) { Assert-Check ($report.finalWeights.Contains($key) -and [double]$emitted[$key] -eq [double]$report.finalWeights[$key]) "$stage stdout 不是最终权重。" }
                if ($null -eq $reference) { $reference = $run }
                else {
                    Assert-Check ($run.Out -ceq $reference.Out) "$stage stdout 权重与首次 jobs1 不同。"
                    Assert-Check ((Without-Jobs $run.Json) -ceq (Without-Jobs $reference.Json)) "$stage 报告除 jobs 外有变化。"
                }
            }
        }
        $checks.Add("$level：jobs1/4 各两次，输出权重相同，完整报告仅 jobs 可不同")
    }

    $reference = $null
    foreach ($case in @('first', 'repeat', 'swapped')) {
        $stage = "compare-$case"
        $level = if ($case -eq 'swapped') { 'normal' } else { 'hard' }
        $opponent = if ($case -eq 'swapped') { 'hard' } else { 'normal' }
        $parameters = [string[]]@('--compare', '--pack', 'original', '--level', $level, '--opponent-level', $opponent,
            '--weights', $inputs[$level], '--baseline', $inputs[$opponent], '--games', '8', '--seed', '2097001', '--jobs', '1')
        $run = Invoke-Sim $stage $parameters $false
        $report = $run.Report
        Assert-Check ($report.schema -ceq 'streamduel-ai-comparison-v1' -and $report.level -ceq $level -and $report.opponentLevel -ceq $opponent -and $report.battleSeedStart -eq 2097001) "$stage 比较模式元数据错误。"
        Assert-Check ($report.result.games -eq 8 -and $report.result.wins + $report.result.losses + $report.result.draws -eq 8) "$stage 未完成八局。"
        if ($case -eq 'first') { $reference = $run }
        elseif ($case -eq 'repeat') { Assert-Check ($run.Out -ceq $reference.Out) '同 seed 的 compare 完整报告不能复现。' }
        else {
            Assert-Check ($report.result.wins -eq $reference.Report.result.losses -and $report.result.losses -eq $reference.Report.result.wins -and $report.result.draws -eq $reference.Report.result.draws) '交换双方难度与权重后，胜负没有对调或平局数改变。'
        }
    }
    $checks.Add('compare：同 seed 重复报告一致；交换 Hard/Normal 及权重后胜负对调、平局不变')
    Assert-InputsUnchanged
    [IO.File]::WriteAllText((Join-Path $runDirectory 'result.json'),
        ([ordered]@{ status = 'PASS'; checks = $checks.ToArray(); inputHashes = $inputHashes; invocations = $invocations.ToArray(); games = 344 } | ConvertTo-Json -Depth 8), $utf8)
    Write-Output "PASS：小样本确定性，共 11 个子进程、344 局；证据：$runDirectory"
}
catch {
    [IO.File]::WriteAllText((Join-Path $runDirectory 'result.json'),
        ([ordered]@{ status = 'FAIL'; stage = $stage; error = $_.Exception.Message; checks = $checks.ToArray(); invocations = $invocations.ToArray() } | ConvertTo-Json -Depth 8), $utf8)
    throw
}
