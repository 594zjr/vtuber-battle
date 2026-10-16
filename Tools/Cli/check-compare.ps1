#requires -Version 7.0
param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug')

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$source = Join-Path $PSScriptRoot "bin/$Configuration/net8.0"
$run = Join-Path $PSScriptRoot ('obj/compare-verification/' + [Guid]::NewGuid().ToString('N'))
$deploy = Join-Path $run 'deploy'
$temp = Join-Path $run 'temp'
$utf8 = [Text.UTF8Encoding]::new($false)
$calls = [Collections.Generic.List[object]]::new()
$inputHashes = @{}
$passed = $false
$failure = $null

function Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "$Message 证据目录：$run" }
}
function Write-Utf8([string]$Path, [string]$Text) {
    Check ([IO.Path]::GetFullPath($Path).StartsWith($run + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) '写入路径越界。'
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path))
    [IO.File]::WriteAllText($Path, $Text, $utf8)
}
function Invoke-Cli([string]$Name, [string[]]$Arguments, [int]$ExpectedExit = 0) {
    $work = Join-Path $run ('calls/' + $Name)
    [void][IO.Directory]::CreateDirectory($work)
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = 'dotnet'
    $start.ArgumentList.Add($cliDll)
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
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
    $start.Environment['DOTNET_CLI_HOME'] = $temp
    $start.Environment['DOTNET_SKIP_FIRST_TIME_EXPERIENCE'] = '1'
    $start.Environment['DOTNET_CLI_TELEMETRY_OPTOUT'] = '1'
    $start.Environment['DOTNET_GENERATE_ASPNET_CERTIFICATE'] = 'false'
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    $started = $false
    try {
        $started = $process.Start()
        Check $started "$Name 未能启动。"
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $process.StandardInput.Close()
        $timedOut = -not $process.WaitForExit(15000)
        if ($timedOut) { $process.Kill($true); [void]$process.WaitForExit(5000) }
        Check ($stdout.Wait(5000) -and $stderr.Wait(5000)) "$Name 输出未结束。"
        $result = [pscustomobject]@{ Out = $stdout.Result; Err = $stderr.Result; Exit = $process.ExitCode }
        Write-Utf8 (Join-Path $run "calls/$Name.stdout.log") $result.Out
        Write-Utf8 (Join-Path $run "calls/$Name.stderr.log") $result.Err
        $entries = @(Get-ChildItem -LiteralPath $work -Force)
        $calls.Add([ordered]@{ name = $Name; arguments = $Arguments; exit = $result.Exit; expected_exit = $ExpectedExit; timed_out = $timedOut; created_entries = @($entries | ForEach-Object Name) })
        Check (-not $timedOut) "$Name 超时。"
        Check ($result.Exit -eq $ExpectedExit) "$Name 应退出 $ExpectedExit，实际 $($result.Exit)：$($result.Err)"
        Check ($entries.Count -eq 0) "$Name 意外写入存档或文件。"
        if ($ExpectedExit -ne 0) {
            Check ($result.Err.Contains('错误：')) "$Name 未给中文错误。"
            Check ($result.Out.Length -eq 0) "$Name 拒绝前已输出其他模式内容。"
        }
        return $result
    }
    finally {
        if ($started -and -not $process.HasExited) { $process.Kill($true); [void]$process.WaitForExit(5000) }
        $process.Dispose()
    }
}
function Reject([string]$Name, [string[]]$Arguments, [string]$Reason) {
    $actual = Invoke-Cli $Name $Arguments 1
    Check ($actual.Err.Contains($Reason)) "$Name 缺少预期中文原因：$Reason。"
}
function Assert-Compare([string]$Name, $Db, [string]$A, [string]$B, [string[]]$Arguments) {
    $before = $Db.ContentHash()
    # CLI 只验证转发原文，六维、倍率和伤害计算由 Core 验收。
    $expected = [StreamDuel.Core.StreamerCompare]::Build($Db, $A, $B).ToText()
    Check (-not [string]::IsNullOrWhiteSpace($expected)) "$Name 比较正文为空。"
    Write-Utf8 (Join-Path $run "expected/$Name.txt") $expected
    $actual = Invoke-Cli $Name $Arguments
    Check ($actual.Out -ceq $expected) "$Name 正文与 Core 不一致。"
    Check ([string]::IsNullOrWhiteSpace($actual.Err)) "$Name 成功时出现诊断错误。"
    Check ($Db.ContentHash() -ceq $before) "$Name 改变了内容。"
}

try {
    $prefix = $repository.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    Check ($run.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) '运行目录必须在仓库内。'
    Check (Test-Path -LiteralPath (Join-Path $source 'Cli.dll') -PathType Leaf) "请先运行 dotnet build Tools/Cli -c $Configuration。"
    $files = @(Get-ChildItem -LiteralPath $source -Recurse -Force)
    Check (@($files | Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 }).Count -eq 0) '构建输出不能含重解析点。'
    foreach ($file in $files | Where-Object { -not $_.PSIsContainer }) {
        $inputHashes[$file.FullName] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    }
    [void][IO.Directory]::CreateDirectory($run)
    [void][IO.Directory]::CreateDirectory($temp)
    Copy-Item -LiteralPath $source -Destination $deploy -Recurse
    $cliDll = Join-Path $deploy 'Cli.dll'
    [void][Reflection.Assembly]::LoadFrom((Join-Path $deploy 'StreamDuel.Core.dll'))
    $content = Join-Path $deploy 'Content'
    $policyPath = Join-Path $content 'PackPolicy.json'
    Write-Utf8 $policyPath '{}'
    $rules = [IO.File]::ReadAllText((Join-Path $content 'StreamDuelRules.json'), $utf8)
    $emptyPolicy = [StreamDuel.Core.PackPolicy]::Empty
    $dbs = @{}
    foreach ($pack in @('original', 'original_wave2', 'fan_asoul_vr', 'fan_holo_niji')) {
        $json = [IO.File]::ReadAllText((Join-Path $content "Packs/$pack.json"), $utf8)
        $db = [StreamDuel.Core.ContentDb]::FromJson($rules, $json, $emptyPolicy)
        $dbs[$pack] = $db
        Assert-Compare $pack $db $db.Streamers[0].Id $db.Streamers[1].Id @('--pack', $pack, '--compare', $db.Streamers[0].Id, $db.Streamers[1].Id)
    }
    $original = $dbs['original']
    $a = $original.Streamer('bell').Id
    $b = $original.Streamer('kei').Id
    $fan = $dbs['fan_asoul_vr']
    $fanId = $fan.Streamers[0].Id
    Assert-Compare 'default' $original $a $b @('--compare', $a, $b)
    Assert-Compare 'self' $original $a $a @('--compare', $a, $a)
    Assert-Compare 'builtin-alias' $original $a $b @('--compare', $a, $b, '--pack', 'ORIGINAL')
    $mixed = [StreamDuel.Core.ContentDb]::Merge([StreamDuel.Core.ContentDb[]]@($original, $fan))
    Assert-Compare 'mixed' $mixed $a $fanId @('--compare', $a, $fanId, '--packs', 'original,fan_asoul_vr')
    Assert-Compare 'mixed-encoded' $mixed $fanId $a @('--pack', $mixed.PackId, '--compare', $fanId, $a)
    $help = Invoke-Cli 'help' @('--help')
    Check ($help.Out.Contains('--compare')) '帮助缺少 --compare。'
    Check ((Invoke-Cli 'compare-help' @('--compare', $a, $b, '--help')).Out -ceq $help.Out) '比较模式的帮助入口不一致。'
    Reject 'missing-first' @('--compare') '缺少A 方'
    Reject 'missing-second' @('--compare', $a) '缺少B 方'
    Reject 'empty-first' @('--compare', '', $b) '缺少'
    Reject 'empty-second' @('--compare', $a, ' ') '缺少'
    Reject 'next-option' @('--compare', $a, '--pack', 'original') '缺少'
    Reject 'control-first' @('--compare', "bad`nvalue", $b) '控制字符'
    Reject 'control-second' @('--compare', $a, "bad`tvalue") '控制字符'
    Reject 'duplicate' @('--compare', $a, $b, '--compare', $a, $b) '重复'
    Reject 'extra-id' @('--compare', $a, $b, $a) '未知参数'
    Reject 'unknown-first' @('--compare', 'not_a_streamer', $b) 'not_a_streamer'
    Reject 'unknown-second' @('--compare', $a, 'not_a_streamer') 'not_a_streamer'
    Reject 'unknown-pack' @('--compare', $a, $b, '--pack', 'not_a_pack') '找不到内容包'
    Reject 'pack-conflict' @('--compare', $a, $b, '--pack', 'original', '--packs', 'original,fan_asoul_vr') '不能同时使用'
    $conflicts = @(
        @{ Name='auto'; Args=@('--auto') }, @{ Name='card'; Args=@('--card', $a) },
        @{ Name='cards'; Args=@('--cards', 'original') }, @{ Name='calc'; Args=@('--calc', $a, '1', $b) },
        @{ Name='glossary'; Args=@('--glossary') }, @{ Name='list'; Args=@('--list-packs') },
        @{ Name='replay'; Args=@('--replay', 'missing.json') }, @{ Name='host'; Args=@('--host', '0') },
        @{ Name='save'; Args=@('--save-dir', 'unexpected') }, @{ Name='report'; Args=@('--report', 'unexpected.md') },
        @{ Name='random'; Args=@('--random-teams') }, @{ Name='hotseat'; Args=@('--hotseat') }
    )
    foreach ($conflict in $conflicts) {
        Reject ('conflict-first-' + $conflict.Name) (@('--compare', $a, $b) + $conflict.Args) '--compare'
        Reject ('conflict-last-' + $conflict.Name) ($conflict.Args + @('--compare', $a, $b)) '--compare'
    }
    $external = Join-Path $run 'external'
    $externalJson = [IO.File]::ReadAllText((Join-Path $content 'Packs/original.json'), $utf8) | ConvertFrom-Json -AsHashtable
    $externalJson['id'] = 'external_compare'
    $externalJson['name'] = '主播对比验收内容包'
    $externalText = $externalJson | ConvertTo-Json -Depth 100 -Compress
    Write-Utf8 (Join-Path $external 'compare.json') $externalText
    $externalDb = [StreamDuel.Core.ContentDb]::FromJson($rules, $externalText, $emptyPolicy)
    Assert-Compare 'external' $externalDb $a $b @('--compare', $a, $b, '--pack', 'external_compare', '--pack-dir', $external)
    $externalMixed = [StreamDuel.Core.ContentDb]::Merge([StreamDuel.Core.ContentDb[]]@($externalDb, $fan))
    Assert-Compare 'external-mixed' $externalMixed $a $fanId @('--compare', $a, $fanId, '--packs', 'external_compare,fan_asoul_vr', '--pack-dir', $external)
    Write-Utf8 $policyPath (@{ disabledStreamers=@($a); reason=@{ $a='对比下架验收' } } | ConvertTo-Json -Compress)
    Reject 'disabled-first' @('--compare', $a, $b) '对比下架验收'
    Reject 'disabled-second' @('--compare', $b, $a) '对比下架验收'
    Reject 'disabled-external' @('--compare', $a, $b, '--pack', 'external_compare', '--pack-dir', $external) '对比下架验收'
    Write-Utf8 $policyPath (@{ disabledStreamers=@($original.Streamers | Select-Object -First 7 | ForEach-Object Id) } | ConvertTo-Json -Compress)
    Reject 'hidden-pack' @('--compare', $a, $b) '该主播已下架'
    Write-Utf8 $policyPath '{"disabledPacks":["original"]}'
    Reject 'disabled-pack' @('--compare', $a, $b) '该内容包已下架'
    Reject 'disabled-mixed-source' @('--compare', $a, $fanId, '--packs', 'original,fan_asoul_vr') '该内容包已下架'
    foreach ($path in $inputHashes.Keys) {
        Check ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ceq $inputHashes[$path]) '原构建输出被修改。'
    }
    $passed = $true
}
catch { $failure = $_.Exception.Message; throw }
finally {
    $summary = [ordered]@{ passed=$passed; failure=$failure; configuration=$Configuration; run_directory=$run; invocations=$calls.ToArray(); source_sha256=$inputHashes }
    Write-Utf8 (Join-Path $run 'result.json') ($summary | ConvertTo-Json -Depth 10)
    Write-Host "T120 CLI compare verification: passed=$passed; $run"
}
