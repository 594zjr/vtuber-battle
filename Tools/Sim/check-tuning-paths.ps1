#requires -Version 7.0
param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug')

# 复制到 Tools/Sim 后，从新的 PowerShell 进程执行；只使用已经构建的产物，不自动构建。
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not [OperatingSystem]::IsWindows()) { throw '本脚本验证 Windows 普通路径与扩展路径别名。' }
if (-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'Sim.csproj') -PathType Leaf)) {
    throw '请将脚本放在 Tools/Sim，并从新的 PowerShell 进程执行。'
}
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$binaryDirectory = Join-Path $PSScriptRoot "bin/$Configuration/net8.0"
$simDll = [IO.Path]::GetFullPath((Join-Path $binaryDirectory 'Sim.dll'))
$coreDll = [IO.Path]::GetFullPath((Join-Path $binaryDirectory 'StreamDuel.Core.dll'))
foreach ($path in @($simDll, $coreDll)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "请先构建 Tools/Sim -c $Configuration：缺少 $path" }
}
if (@([AppDomain]::CurrentDomain.GetAssemblies() | Where-Object { $_.GetName().Name -eq 'StreamDuel.Core' }).Count -ne 0) {
    throw '当前进程已经加载 Core；请从新的 PowerShell 进程运行。'
}
[void][Reflection.Assembly]::LoadFrom($coreDll)
if (-not [StringComparer]::OrdinalIgnoreCase.Equals(
    [IO.Path]::GetFullPath([StreamDuel.Core.AiWeights].Assembly.Location), $coreDll)) {
    throw '实际加载的 Core 不在所选 Sim 旁边。'
}
$legacy = [StreamDuel.Core.AiWeights]::Legacy
if ($null -eq $legacy -or @($legacy.Names()).Count -ne 36) { throw '当前 Core 必须含 T95 Legacy 的完整 36 项权重。' }
$legacyJson = $legacy.ToJson()
$utf8 = [Text.UTF8Encoding]::new($false, $true)
$expectedBytes = [Convert]::ToBase64String($utf8.GetBytes($legacyJson))
$binaryHashes = @(Get-FileHash -LiteralPath $simDll, $coreDll -Algorithm SHA256 | Select-Object Path, Hash)
$allowedRoot = [IO.Path]::GetFullPath((Join-Path $repository 'Engine.Tests/obj/t95-review/runs'))
$runDirectory = [IO.Path]::GetFullPath((Join-Path $allowedRoot ([Guid]::NewGuid().ToString('N'))))
if (-not $runDirectory.StartsWith($allowedRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw '产物目录越界。' }
[void][IO.Directory]::CreateDirectory($runDirectory)
[IO.File]::WriteAllText((Join-Path $runDirectory 'legacy-reference.json'), $legacyJson, $utf8)
$results = [Collections.Generic.List[object]]::new()

function Assert-Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Extended-Path([string]$Path) {
    if ($Path.StartsWith('\\', [StringComparison]::Ordinal)) { return '\\?\UNC\' + $Path.Substring(2) }
    return '\\?\' + $Path
}

function Input-State([string[]]$Paths) {
    foreach ($path in $Paths) {
        $bytes = [IO.File]::ReadAllBytes($path)
        [pscustomobject]@{
            path = $path; sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
            length = $bytes.Length; originalBytes = ([Convert]::ToBase64String($bytes) -ceq $expectedBytes)
        }
    }
}

function Assert-GameCount($Result, [int]$Games) {
    Assert-Check ($Result.games -eq $Games -and
        ($Result.wins + $Result.losses + $Result.draws) -eq $Games) '报告胜负平合计与局数不符。'
}

function Run-Case([string]$Mode, [string]$Target, [string]$Direction) {
    $name = "$Mode-$Target-$Direction"
    $directory = [IO.Path]::GetFullPath((Join-Path $runDirectory $name))
    Assert-Check ($directory.StartsWith($runDirectory + '\', [StringComparison]::OrdinalIgnoreCase)) '用例目录越界。'
    [void][IO.Directory]::CreateDirectory($directory)
    $weights = Join-Path $directory 'weights.json'
    $baseline = Join-Path $directory 'baseline.json'
    foreach ($path in @($weights, $baseline)) { [IO.File]::WriteAllText($path, $legacyJson, $utf8) }
    $weightArgument = $weights
    $baselineArgument = $baseline
    $expectedExit = if ($Target -eq 'separate') { 0 } else { 1 }
    $reportPath = Join-Path $directory 'report.json'
    if ($expectedExit -eq 1) {
        $reportPath = if ($Target -eq 'weights') { $weights } else { $baseline }
        if ($Direction -eq 'report-extended') { $reportPath = Extended-Path $reportPath }
        elseif ($Direction -eq 'input-extended') {
            if ($Target -eq 'weights') { $weightArgument = Extended-Path $weights }
            else { $baselineArgument = Extended-Path $baseline }
        }
    }
    $arguments = @("--$Mode", '--pack', 'original', '--level', 'normal', '--games', '8', '--seed', '95', '--jobs', '1')
    if ($Mode -eq 'tune') { $arguments += @('--rounds', '1', '--candidates', '1', '--holdout-games', '8') }
    else { $arguments += @('--opponent-level', 'normal') }
    $arguments += @('--weights', $weightArgument, '--baseline', $baselineArgument, '--report', $reportPath)
    $evidence = [ordered]@{
        name = $name; mode = $Mode; target = $Target; direction = $Direction; expectedExit = $expectedExit
        executable = 'dotnet'; argv = @($simDll) + $arguments; workingDirectory = $directory
        before = @(Input-State @($weights, $baseline)); after = @(); exit = $null
        timedOut = $false; passed = $false; failure = $null
    }
    [IO.File]::WriteAllText((Join-Path $directory 'argv.json'), ($evidence.argv | ConvertTo-Json), $utf8)
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = 'dotnet'
    foreach ($argument in $evidence.argv) { $start.ArgumentList.Add($argument) }
    $start.WorkingDirectory = $directory
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = $utf8
    $start.StandardErrorEncoding = $utf8
    $temporary = Join-Path $directory 'temp'
    $dotnetHome = Join-Path $directory 'dotnet-home'
    [void][IO.Directory]::CreateDirectory($temporary)
    [void][IO.Directory]::CreateDirectory($dotnetHome)
    foreach ($key in @('TEMP', 'TMP', 'TMPDIR')) { $start.Environment[$key] = $temporary }
    $start.Environment['DOTNET_CLI_HOME'] = $dotnetHome
    $start.Environment['DOTNET_NOLOGO'] = '1'
    $start.Environment['DOTNET_CLI_TELEMETRY_OPTOUT'] = '1'
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    $started = $false
    try {
        $started = $process.Start()
        Assert-Check $started 'Sim 子进程无法启动。'
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $process.StandardInput.Close()
        $evidence.timedOut = -not $process.WaitForExit(60000)
        if ($evidence.timedOut) { $process.Kill($true); [void]$process.WaitForExit(5000) }
        Assert-Check ($stdout.Wait(5000) -and $stderr.Wait(5000)) '子进程输出未结束。'
        $evidence.exit = $process.ExitCode
        [IO.File]::WriteAllText((Join-Path $directory 'stdout.log'), $stdout.Result, $utf8)
        [IO.File]::WriteAllText((Join-Path $directory 'stderr.log'), $stderr.Result, $utf8)
        $evidence.after = @(Input-State @($weights, $baseline))
        Assert-Check (-not $evidence.timedOut) 'Sim 子进程超过 60 秒。'
        Assert-Check (@($evidence.after | Where-Object { -not $_.originalBytes }).Count -eq 0) '原始权重或基线字节被报告覆盖。'
        for ($index = 0; $index -lt 2; $index++) {
            Assert-Check ($evidence.before[$index].sha256 -ceq $evidence.after[$index].sha256) '输入 SHA-256 发生变化。'
        }
        Assert-Check ($evidence.exit -eq $expectedExit) "预期退出 $expectedExit，实际 $($evidence.exit)。"
        if ($expectedExit -eq 1) {
            Assert-Check ([string]::IsNullOrWhiteSpace($stdout.Result)) '拒绝输入覆盖前不应输出训练结果。'
            Assert-Check ($stderr.Result.Contains('错误：') -and $stderr.Result.Contains('--report') -and
                $stderr.Result.Contains('不能覆盖')) '没有针对报告覆盖输入的中文错误。'
        }
        else {
            Assert-Check ($stderr.Result -notmatch '(?m)^错误：') '正常小规模运行输出了错误。'
            $reportText = [IO.File]::ReadAllText($reportPath, $utf8)
            $report = $reportText | ConvertFrom-Json
            Assert-Check ($report.status -ceq 'completed' -and $report.packId -ceq 'original') '报告未正常完成或内容包不符。'
            if ($Mode -eq 'tune') {
                Assert-Check (@($report.rounds).Count -eq 1 -and @($report.rounds[0].candidates).Count -eq 1) 'tiny tune 未执行一轮一个候选。'
                Assert-GameCount $report.rounds[0].candidates[0].result 8
                Assert-GameCount $report.holdout.result 8
                $outputWeights = [StreamDuel.Core.AiWeights]::FromJson($stdout.Result)
                $reportedWeights = [StreamDuel.Core.AiWeights]::FromJson(($report.finalWeights | ConvertTo-Json -Compress))
                Assert-Check ($outputWeights.ToJson() -ceq $reportedWeights.ToJson()) 'stdout 最终权重与报告不一致。'
            }
            else {
                Assert-GameCount $report.result 8
                Assert-Check ($stdout.Result.TrimEnd([char[]]"`r`n") -ceq $reportText.TrimEnd([char[]]"`r`n")) 'compare stdout 与报告不一致。'
            }
        }
        $evidence.passed = $true
    }
    catch { $evidence.failure = $_.Exception.Message }
    finally {
        if ($started -and -not $process.HasExited) { $process.Kill($true); [void]$process.WaitForExit(5000) }
        $process.Dispose()
        if ($evidence.after.Count -eq 0) { $evidence.after = @(Input-State @($weights, $baseline)) }
        [IO.File]::WriteAllText((Join-Path $directory 'result.json'), ($evidence | ConvertTo-Json -Depth 10), $utf8)
        $results.Add([pscustomobject]$evidence)
    }
}

foreach ($mode in @('tune', 'compare')) {
    foreach ($target in @('weights', 'baseline')) {
        foreach ($direction in @('report-extended', 'input-extended', 'plain')) { Run-Case $mode $target $direction }
    }
    Run-Case $mode 'separate' 'plain'
}
$binaryChanges = @($binaryHashes | Where-Object { (Get-FileHash -LiteralPath $_.Path -Algorithm SHA256).Hash -cne $_.Hash })
$failed = @($results | Where-Object { -not $_.passed })
$passed = $results.Count -eq 14 -and $failed.Count -eq 0 -and $binaryChanges.Count -eq 0
[IO.File]::WriteAllText((Join-Path $runDirectory 'report.json'), ([ordered]@{
    passed = $passed; count = $results.Count; failed = $failed.Count; binaries = $binaryHashes
    binaryChanges = $binaryChanges; cases = $results.ToArray(); directory = $runDirectory
} | ConvertTo-Json -Depth 12), $utf8)
if (-not $passed) {
    throw "FAIL：$($failed.Count)/$($results.Count) 个路径用例失败；构建产物变化 $($binaryChanges.Count) 项。证据：$runDirectory"
}
Write-Output "PASS：14 个调参路径用例；证据：$runDirectory"
