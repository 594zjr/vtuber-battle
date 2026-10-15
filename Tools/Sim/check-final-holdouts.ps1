#requires -Version 7.0
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [ValidateSet(1, 8)][int]$Jobs = 8,
    [ValidateRange(1, 3600)][int]$TimeoutSeconds = 900
)

# 从本 ignored 目录或复制到 Tools/Sim 后运行；不构建、不访问作者工作树。
# 仅重现 docs/ai-tuning.md 的三组最终留出，不等同于重新训练或重现检查点筛选。
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
if ($current -ne $repository -and -not $current.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw '请在本仓库内运行此脚本。'
}
$simProject = Join-Path $repository 'Tools/Sim/Sim.csproj'
if (-not (Test-Path -LiteralPath $simProject -PathType Leaf)) { throw '没有找到本仓库的 Sim 工程。' }
$output = Join-Path $repository "Tools/Sim/bin/$Configuration/net8.0"
$simDll = Join-Path $output 'Sim.dll'
$coreDll = Join-Path $output 'StreamDuel.Core.dll'
foreach ($file in @($simDll, $coreDll)) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "请先构建 Tools/Sim -c $Configuration。" }
}
$runDirectory = [IO.Path]::GetFullPath((Join-Path $repository ('Engine.Tests/obj/t95-final-holdouts/' + [Guid]::NewGuid().ToString('N'))))
if (-not $runDirectory.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw '产物目录必须位于本仓库内。' }
[void][IO.Directory]::CreateDirectory($runDirectory)
$tempDirectory = Join-Path $runDirectory 'temp'
[void][IO.Directory]::CreateDirectory($tempDirectory)
$utf8 = [Text.UTF8Encoding]::new($false)
$inputPaths = @{}
$inputHashes = @{}
$inputValues = @{}
$results = [Collections.Generic.List[object]]::new()
$binaryHashes = @(Get-FileHash -LiteralPath $simDll, $coreDll -Algorithm SHA256 | Select-Object Path, Hash)
$stage = '导出生产权重'

function Assert-Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Assert-InputsUnchanged {
    foreach ($name in $inputPaths.Keys) {
        Assert-Check ((Get-FileHash -LiteralPath $inputPaths[$name] -Algorithm SHA256).Hash -ceq $inputHashes[$name]) "输入权重 $name 被改写。"
    }
}

function Assert-Weights($Actual, $Expected, [string]$Name) {
    Assert-Check ($Actual.Count -eq 36 -and $Actual.Count -eq $Expected.Count) "$Name 参数数量不匹配。"
    foreach ($key in $Expected.Keys) {
        Assert-Check ($Actual.Contains($key) -and [double]$Actual[$key] -eq [double]$Expected[$key]) "$Name 的 $key 不匹配。"
    }
}

function Invoke-Sim([string]$Name, [string[]]$Parameters) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = 'dotnet'
    $start.ArgumentList.Add($simDll)
    foreach ($parameter in $Parameters) { $start.ArgumentList.Add($parameter) }
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
    $start.Environment['DOTNET_PROCESSOR_COUNT'] = '8'
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    $started = $false
    try {
        $started = $process.Start()
        Assert-Check $started "$Name 无法启动。"
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $process.StandardInput.Close()
        Assert-Check ($process.WaitForExit($TimeoutSeconds * 1000)) "$Name 超过 $TimeoutSeconds 秒。"
        $result = [pscustomobject]@{ Out = $stdout.Result; Err = $stderr.Result; Exit = $process.ExitCode }
        [IO.File]::WriteAllText((Join-Path $runDirectory "$Name.stdout.json"), $result.Out, $utf8)
        [IO.File]::WriteAllText((Join-Path $runDirectory "$Name.stderr.log"), $result.Err, $utf8)
        Assert-Check ($result.Exit -eq 0 -and $result.Err.Length -eq 0) "$Name 进程失败或有错误输出。"
        return $result.Out
    }
    finally {
        if ($started -and -not $process.HasExited) { $process.Kill($true); [void]$process.WaitForExit(5000) }
        $process.Dispose()
    }
}

try {
    [void][Reflection.Assembly]::LoadFrom($coreDll)
    $profiles = [ordered]@{
        normal = [StreamDuel.Core.AiWeights]::Default
        hard = [StreamDuel.Core.AiWeights]::HardDefault
        legacy = [StreamDuel.Core.AiWeights]::Legacy
    }
    foreach ($name in $profiles.Keys) {
        $inputPaths[$name] = Join-Path $runDirectory "$name.json"
        $json = $profiles[$name].ToJson()
        [IO.File]::WriteAllText($inputPaths[$name], $json, $utf8)
        $inputHashes[$name] = (Get-FileHash -LiteralPath $inputPaths[$name] -Algorithm SHA256).Hash
        $inputValues[$name] = ConvertFrom-Json -InputObject $json -AsHashtable
        Assert-Check ($inputValues[$name].Count -eq 36) "$name 应含完整 36 项生产参数。"
    }
    # 导出使用 Core 的规范 JSON；文件排版与作者历史文件不同，记录自身 SHA 而不冒认历史文件 SHA。
    [IO.File]::WriteAllText((Join-Path $runDirectory 'inputs.json'),
        ([ordered]@{ hashes = $inputHashes; binaries = $binaryHashes; configuration = $Configuration; jobs = $Jobs } | ConvertTo-Json -Depth 6), $utf8)
    $cases = @(
        [pscustomobject]@{ Name = 'normal-v-old'; Candidate = 'normal'; Baseline = 'legacy'; Level = 'normal'; Opponent = 'normal'; Seed = 1000001; Games = 800; Wins = 581; Losses = 219; Draws = 0; WilsonPercent = 69.43 },
        [pscustomobject]@{ Name = 'hard-v-old'; Candidate = 'hard'; Baseline = 'legacy'; Level = 'hard'; Opponent = 'hard'; Seed = 1010001; Games = 800; Wins = 517; Losses = 283; Draws = 0; WilsonPercent = 61.25 },
        [pscustomobject]@{ Name = 'hard-v-normal'; Candidate = 'hard'; Baseline = 'normal'; Level = 'hard'; Opponent = 'normal'; Seed = 1020001; Games = 400; Wins = 250; Losses = 150; Draws = 0; WilsonPercent = 57.66 }
    )
    foreach ($case in $cases) {
        $stage = $case.Name
        $reportPath = Join-Path $runDirectory ($case.Name + '.report.json')
        $arguments = [string[]]@('--compare', '--pack', 'original', '--level', $case.Level, '--opponent-level', $case.Opponent,
            '--weights', $inputPaths[$case.Candidate], '--baseline', $inputPaths[$case.Baseline], '--games', $case.Games.ToString(),
            '--seed', $case.Seed.ToString(), '--jobs', $Jobs.ToString(), '--report', $reportPath)
        $stdout = Invoke-Sim $case.Name $arguments
        Assert-InputsUnchanged
        Assert-Check ($stdout.TrimEnd([char[]]"`r`n") -ceq [IO.File]::ReadAllText($reportPath).TrimEnd([char[]]"`r`n")) 'stdout 与独立报告文件内容不同（仅忽略末尾 CR/LF）。'
        $report = ConvertFrom-Json -InputObject $stdout -AsHashtable
        Assert-Check ($report.schema -ceq 'streamduel-ai-comparison-v1' -and $report.status -ceq 'completed') '报告 schema 或完成状态不正确。'
        Assert-Check ($report.packId -ceq 'original' -and $report.contentHash -ceq '336820d77647481b') '包或内容指纹不同于文档，不能比较该固定留出。'
        Assert-Check ($report.level -ceq $case.Level -and $report.opponentLevel -ceq $case.Opponent -and $report.jobs -eq $Jobs) '报告双方难度或并行度不正确。'
        Assert-Check ($report.battleSeedStart -eq $case.Seed -and $report.teamSeed -eq ($case.Seed -bxor 0x63D83595) -and $report.seedCount -eq ($case.Games / 8)) '报告种子或样本块数不正确。'
        Assert-Check ($report.symmetryGames -eq 8 -and $report.symmetry -ceq 'team-order x candidate-side x choose-order; each factor 0 then 1') '八局对称协议元数据不正确。'
        Assert-Check ($report.replacementPolicy -ceq "ChooseReplacementWithWeights using each side's level; same side order as choose") '补位权重与顺序口径不正确。'
        Assert-Check ($report.winRateDefinition -ceq 'wins / all games for --weights side; draws are non-wins') '胜率分母或胜方口径不正确。'
        Assert-Check ($report.statisticalScope -ceq 'fixed comparison only; no mutation or selection; symmetry games are correlated') '报告遗漏固定比较及相关性限制。'
        Assert-Weights $report.weights $inputValues[$case.Candidate] '候选权重'
        Assert-Weights $report.baselineWeights $inputValues[$case.Baseline] '对照权重'
        $actual = $report.result
        Assert-Check ($actual.games -eq $case.Games -and $actual.wins -eq $case.Wins -and $actual.losses -eq $case.Losses -and $actual.draws -eq $case.Draws) "$stage 胜负平与文档精确计数不符。"
        Assert-Check ($actual.wins + $actual.losses + $actual.draws -eq $actual.games -and $actual.winRate -eq ([double]$case.Wins / $case.Games)) "$stage 胜率或总局数不符。"
        Assert-Check ([Math]::Round([double]$actual.wilson95Lower * 100, 2) -eq $case.WilsonPercent) "$stage Wilson 下界与文档精度不符。"
        $results.Add([ordered]@{ name = $stage; arguments = $arguments; result = $actual; reportSha256 = (Get-FileHash -LiteralPath $reportPath -Algorithm SHA256).Hash })
    }
    Assert-InputsUnchanged
    [IO.File]::WriteAllText((Join-Path $runDirectory 'result.json'),
        ([ordered]@{ status = 'PASS'; scope = '仅发布权重的三个固定最终留出，非完整训练复现'; inputHashes = $inputHashes; results = $results.ToArray() } | ConvertTo-Json -Depth 10), $utf8)
    Write-Output "PASS：三组固定留出共 2000 局精确匹配，输入 SHA 未变；证据：$runDirectory"
}
catch {
    [IO.File]::WriteAllText((Join-Path $runDirectory 'result.json'),
        ([ordered]@{ status = 'FAIL'; stage = $stage; error = $_.Exception.Message; inputHashes = $inputHashes; results = $results.ToArray() } | ConvertTo-Json -Depth 10), $utf8)
    throw
}
