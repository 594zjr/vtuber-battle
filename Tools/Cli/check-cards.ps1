#requires -Version 7.0
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug',
    [ValidateRange(5, 120)][int]$TimeoutSeconds = 15
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
# 只使用已构建输出；可替换策略和所有验收产物都在本仓库的新 obj 副本中。
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$prefix = $repository.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not (Test-Path -LiteralPath (Join-Path $repository 'Engine/StreamDuel.Core.csproj') -PathType Leaf) -or
    -not (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'Cli.csproj') -PathType Leaf)) {
    throw '请使用仓库 Tools/Cli 内的验收脚本。'
}
$source = Join-Path $repository "Tools/Cli/bin/$Configuration/net8.0"
foreach ($file in @('Cli.dll', 'StreamDuel.Core.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $source $file) -PathType Leaf)) {
        throw "请先运行 dotnet build Tools/Cli -c $Configuration。"
    }
}
$run = Join-Path $repository ('Tools/Cli/obj/cards-verification/' + [Guid]::NewGuid().ToString('N'))
$deploy = Join-Path $run 'deploy'
$temp = Join-Path $run 'temp'
foreach ($path in @($run, $deploy, $temp)) {
    if (-not [IO.Path]::GetFullPath($path).StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw '验收产物、发布副本和临时目录必须在本仓库内。'
    }
}
$utf8 = [Text.UTF8Encoding]::new($false)
$inputs = @{}
$calls = [Collections.Generic.List[object]]::new()
$checks = [Collections.Generic.List[string]]::new()
$references = [Collections.Generic.List[object]]::new()
$passed = $false
$failure = $null
$restoreLocale = $false
$previousLocale = $null

function Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "$Message 证据目录：$run" }
}
function Write-Utf8([string]$Path, [string]$Text) {
    $full = [IO.Path]::GetFullPath($Path)
    Check ($full.StartsWith($run + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) '写入路径越界。'
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($full))
    [IO.File]::WriteAllText($full, $Text, $utf8)
}
function Copy-Safe([string]$SourcePath, [string]$Destination) {
    $full = [IO.Path]::GetFullPath($SourcePath)
    $dest = [IO.Path]::GetFullPath($Destination)
    Check ($full.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) '复制源必须在本工作树内。'
    Check ($dest.StartsWith($run + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) '复制目标越界。'
    $item = Get-Item -LiteralPath $full
    Check (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) '拒绝复制重解析点。'
    if ($item.PSIsContainer) {
        [void][IO.Directory]::CreateDirectory($dest)
        foreach ($child in Get-ChildItem -LiteralPath $full -Force) { Copy-Safe $child.FullName (Join-Path $dest $child.Name) }
    }
    else {
        $inputs[$full] = (Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash
        [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($dest))
        [IO.File]::Copy($full, $dest, $false)
    }
}
function Set-Policy([string]$Json) { Write-Utf8 (Join-Path $deploy 'Content/PackPolicy.json') $Json }
function Invoke-Cli([string]$Name, [string[]]$Arguments, [int]$Exit = 0) {
    Check ($Name -cmatch '^[A-Za-z0-9_-]+$') '用例名称不能含路径字符。'
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
        Check $started "$Name 无法启动。"
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $process.StandardInput.Close()
        $timedOut = -not $process.WaitForExit($TimeoutSeconds * 1000)
        if ($timedOut) { $process.Kill($true); [void]$process.WaitForExit(5000) }
        Check ($stdout.Wait(5000) -and $stderr.Wait(5000)) "$Name 输出未结束。"
        $result = [pscustomobject]@{ Out = $stdout.Result; Err = $stderr.Result; Exit = $process.ExitCode }
        Write-Utf8 (Join-Path $run "calls/$Name.stdout.log") $result.Out
        Write-Utf8 (Join-Path $run "calls/$Name.stderr.log") $result.Err
        $entries = @(Get-ChildItem -LiteralPath $work -Force)
        $calls.Add([ordered]@{ name = $Name; arguments = $Arguments; exit = $result.Exit; expected = $Exit
            timed_out = $timedOut; created_entries = @($entries | ForEach-Object Name) })
        Check (-not $timedOut) "$Name 超时。"
        Check ($result.Exit -eq $Exit) "$Name 应退出 $Exit，实际 $($result.Exit)。"
        Check ($entries.Count -eq 0) "$Name 创建了文件或存档目录。"
        if ($Exit -eq 0) { Check ([string]::IsNullOrWhiteSpace($result.Err)) "$Name 成功时出现诊断错误。" }
        else {
            Check ($result.Err.Contains('错误：')) "$Name 缺少中文错误。"
            Check ($result.Out.Length -eq 0) "$Name 拒绝前已进入资料卡或其他模式。"
        }
        return $result
    }
    finally {
        if ($started -and -not $process.HasExited) { $process.Kill($true); [void]$process.WaitForExit(5000) }
        $process.Dispose()
    }
}
function Reject([string]$Name, [string[]]$Arguments, [string]$Reason) {
    $result = Invoke-Cli $Name $Arguments 1
    Check ($result.Err.Contains($Reason)) "$Name 缺少预期原因：$Reason。"
}
function Assert-Cards([string]$Name, $Db, [string[]]$Ids, [string[]]$Arguments) {
    $before = $Db.ContentHash()
    # 只验证 CLI 转发；排名、技能、克制的正确性由 Core 专项测试验证。
    $texts = @($Ids | ForEach-Object { [StreamDuel.Core.StreamerCard]::Build($Db, $_).ToText() })
    $expected = $texts -join "`n"
    Check (-not [string]::IsNullOrWhiteSpace($expected)) "$Name 卡片正文为空。"
    Check ($expected -notmatch '（未知）') "$Name 含未知占位。"
    Write-Utf8 (Join-Path $run "expected/$Name.txt") $expected
    $actual = Invoke-Cli $Name $Arguments
    Check ($actual.Out -ceq $expected) "$Name 输出顺序或正文与 Core 不一致。"
    Check ([regex]::Matches($actual.Out, '(?m)^主播资料卡：').Count -eq $Ids.Count) "$Name 卡片数量不符。"
    Check ($Db.ContentHash() -ceq $before) "$Name 生成资料卡改变内容库。"
    $references.Add([ordered]@{ name = $Name; pack = $Db.PackId; ids = $Ids; content_hash = $before })
}

try {
    [void][IO.Directory]::CreateDirectory($temp)
    Copy-Safe $source $deploy
    $cliDll = Join-Path $deploy 'Cli.dll'
    $coreDll = Join-Path $deploy 'StreamDuel.Core.dll'
    $assembly = [Reflection.Assembly]::LoadFrom($coreDll)
    Check ([StringComparer]::OrdinalIgnoreCase.Equals($assembly.Location, $coreDll)) '请在未加载其他 Core 的新 PowerShell 进程运行。'
    Check ($null -ne $assembly.GetType('StreamDuel.Core.StreamerCard', $false)) '现有二进制尚未包含 StreamerCard。'
    $previousLocale = [StreamDuel.Core.Loc]::Current
    $restoreLocale = $true
    [StreamDuel.Core.Loc]::Current = $null
    $content = Join-Path $deploy 'Content'
    $rules = [IO.File]::ReadAllText((Join-Path $content 'StreamDuelRules.json'), $utf8)
    $sourcePolicy = Join-Path $repository 'UnityProject/Assets/StreamDuel/Resources/PackPolicy.json'
    $inputs[$sourcePolicy] = (Get-FileHash -LiteralPath $sourcePolicy -Algorithm SHA256).Hash
    Check ((Get-FileHash -LiteralPath (Join-Path $content 'PackPolicy.json') -Algorithm SHA256).Hash -ceq $inputs[$sourcePolicy]) '发布输出未复制当前真实策略。'
    Set-Policy '{}'
    $empty = [StreamDuel.Core.PackPolicy]::Empty
    $databases = @{}
    foreach ($pack in @('original', 'original_wave2', 'fan_holo_niji', 'fan_asoul_vr')) {
        $json = [IO.File]::ReadAllText((Join-Path $content "Packs/$pack.json"), $utf8)
        $db = [StreamDuel.Core.ContentDb]::FromJson($rules, $json, $empty)
        $databases[$pack] = $db
        [string[]]$ids = @($db.Streamers | ForEach-Object Id)
        Assert-Cards "$pack-all" $db $ids @('--cards', $pack)
        Assert-Cards "$pack-single" $db @($ids[0]) @('--card', $ids[0], '--pack', $pack)
    }
    $original = $databases['original']
    [string[]]$originalIds = @($original.Streamers | ForEach-Object Id)
    $first = $originalIds[0]
    Assert-Cards 'default-single' $original @($first) @('--card', $first)
    Assert-Cards 'builtin-alias' $original $originalIds @('--cards', 'ORIGINAL')
    $mixed = [StreamDuel.Core.ContentDb]::Merge([StreamDuel.Core.ContentDb[]]@($original, $databases['fan_asoul_vr']))
    Assert-Cards 'mixed-single' $mixed @($first) @('--packs', 'original,fan_asoul_vr', '--card', $first)
    Assert-Cards 'mixed-all' $mixed @($mixed.Streamers | ForEach-Object Id) @('--cards', $mixed.PackId)
    $checks.Add('四包整包及单张、默认包、内置文件名别名、混合单张与规范 mix: 整包逐字一致')

    $help = Invoke-Cli 'help' @('--help')
    Check ($help.Out.Contains('--card') -and $help.Out.Contains('--cards')) '帮助缺少资料卡入口。'
    foreach ($mode in @('card', 'cards')) {
        $value = if ($mode -eq 'card') { $first } else { 'original' }
        $withHelp = Invoke-Cli "$mode-help" @("--$mode", $value, '--help')
        Check ($withHelp.Out -ceq $help.Out) '资料卡模式应保留帮助入口。'
        Reject "$mode-missing" @("--$mode") '缺少'
        Reject "$mode-empty" @("--$mode", '') '缺少'
        Reject "$mode-next-flag" @("--$mode", '--pack-dir', 'unused') '缺少'
        Reject "$mode-control" @("--$mode", "invalid`nvalue") '控制字符'
        Reject "$mode-duplicate" @("--$mode", $value, "--$mode", $value) '重复'
    }
    Reject 'unknown-streamer' @('--card', 'not_a_streamer') 'not_a_streamer'
    Reject 'unknown-pack' @('--cards', 'not_a_pack') '找不到内容包'
    Reject 'single-unknown-pack' @('--card', $first, '--pack', 'not_a_pack') '找不到内容包'
    Reject 'both-card-modes' @('--card', $first, '--cards', 'original') '不能同时使用'
    Reject 'both-card-modes-reverse' @('--cards', 'original', '--card', $first) '不能同时使用'
    Reject 'cards-pack' @('--cards', 'original', '--pack', 'original') '--cards'
    Reject 'cards-packs' @('--cards', 'original', '--packs', 'original,fan_asoul_vr') '--cards'
    Reject 'card-pack-and-packs' @('--card', $first, '--pack', 'original', '--packs', 'original,fan_asoul_vr') '不能同时使用'
    Reject 'unknown-option' @('--card', $first, '--not-an-option') '未知参数'
    [string[]]$teamA = $originalIds[0..5]
    [string[]]$teamB = $originalIds[6..11]
    $conflicts = @(
        @{ Name = 'auto'; Args = @('--auto') }, @{ Name = 'ban'; Args = @('--ban') },
        @{ Name = 'lead'; Args = @('--lead') }, @{ Name = 'hash'; Args = @('--hash') },
        @{ Name = 'advice'; Args = @('--advice') }, @{ Name = 'level'; Args = @('--level', 'normal') },
        @{ Name = 'seed'; Args = @('--seed', '113') }, @{ Name = 'team'; Args = @('--team', ($teamA -join ',')) },
        @{ Name = 'team-b'; Args = @('--team-b', ($teamB -join ',')) }, @{ Name = 'team-code'; Args = @('--team-code', 'ST1-invalid') },
        @{ Name = 'random-teams'; Args = @('--random-teams') }, @{ Name = 'export-team'; Args = @('--export-team') },
        @{ Name = 'hotseat'; Args = @('--hotseat') }, @{ Name = 'name-a'; Args = @('--name-a', '甲') },
        @{ Name = 'name-b'; Args = @('--name-b', '乙') }, @{ Name = 'timer'; Args = @('--timer', '10,30') },
        @{ Name = 'save-dir'; Args = @('--save-dir', 'unexpected-saves') }, @{ Name = 'report'; Args = @('--report', 'unexpected.md') },
        @{ Name = 'share'; Args = @('--share') }, @{ Name = 'tournament'; Args = @('--tournament', 'unexpected.json') },
        @{ Name = 'replays'; Args = @('--replays') }, @{ Name = 'replay'; Args = @('--replay', 'nonexistent.json') },
        @{ Name = 'replay-code'; Args = @('--replay-code', 'SD1-invalid') }, @{ Name = 'best-effort'; Args = @('--replay-best-effort') },
        @{ Name = 'host'; Args = @('--host', '0') }, @{ Name = 'join'; Args = @('--join', '127.0.0.1:1') },
        @{ Name = 'lan-host'; Args = @('--lan-host', '不得开房') }, @{ Name = 'lan-list'; Args = @('--lan-list') },
        @{ Name = 'list-packs'; Args = @('--list-packs') }, @{ Name = 'glossary'; Args = @('--glossary', '状态') },
        @{ Name = 'calc'; Args = @('--calc', $first, '1', $originalIds[6]) },
        @{ Name = 'matchup'; Args = @('--matchup', ($teamA -join ','), 'vs', ($teamB -join ',')) }
    )
    foreach ($mode in @('card', 'cards')) {
        [string[]]$selection = if ($mode -eq 'card') { @('--card', $first) } else { @('--cards', 'original') }
        foreach ($conflict in $conflicts) {
            Reject ("$mode-first-" + $conflict.Name) ($selection + $conflict.Args) "--$mode"
            Reject ("$mode-last-" + $conflict.Name) ($conflict.Args + $selection) "--$mode"
        }
    }
    $checks.Add('参数缺失/重复/未知/互斥，以及两入口分别与32种模式参数前后顺序冲突均拒绝且无文件产物')

    $external = Join-Path $run 'external'
    $originalJson = [IO.File]::ReadAllText((Join-Path $content 'Packs/original.json'), $utf8)
    $externalData = $originalJson | ConvertFrom-Json -AsHashtable
    $externalData['id'] = 'external_cards'
    $externalData['name'] = '资料卡验证内容包'
    $externalJson = $externalData | ConvertTo-Json -Depth 100 -Compress
    Write-Utf8 (Join-Path $external 'cards.json') $externalJson
    $externalDb = [StreamDuel.Core.ContentDb]::FromJson($rules, $externalJson, $empty)
    Assert-Cards 'external-single' $externalDb @($first) @('--card', $first, '--pack', 'external_cards', '--pack-dir', $external)
    Assert-Cards 'external-all' $externalDb $originalIds @('--cards', 'external_cards', '--pack-dir', $external)
    $policyJson = @{ disabledStreamers = @($first); reason = @{ $first = '资料卡政策验证' } } | ConvertTo-Json -Depth 5 -Compress
    Set-Policy $policyJson
    $policy = [StreamDuel.Core.PackPolicy]::FromJson($policyJson)
    $filtered = [StreamDuel.Core.ContentDb]::FromJson($rules, $externalJson, $policy)
    Reject 'disabled-single' @('--card', $first) '该主播已下架'
    Reject 'disabled-external-single' @('--card', $first, '--pack', 'external_cards', '--pack-dir', $external) '资料卡政策验证'
    Assert-Cards 'filtered-external-all' $filtered @($filtered.Streamers | ForEach-Object Id) @('--cards', 'external_cards', '--pack-dir', $external)
    $policyJson = @{ disabledStreamers = @($originalIds[0..6]); reason = @{ $first = '不足六人前预检' } } | ConvertTo-Json -Depth 5 -Compress
    Set-Policy $policyJson
    Reject 'hidden-pack-single' @('--card', $first, '--pack', 'external_cards', '--pack-dir', $external) '该主播已下架'
    Reject 'hidden-pack-all' @('--cards', 'external_cards', '--pack-dir', $external) '不足 6'
    Set-Policy '{"disabledPacks":["original","external_cards"],"reason":{"original":"禁止同 id 外包绕过","external_cards":"外包整包下架"}}'
    Write-Utf8 (Join-Path $external 'PackPolicy.json') '{}'
    Write-Utf8 (Join-Path $external 'renamed-original.json') $originalJson
    Reject 'disabled-builtin-alias' @('--cards', 'ORIGINAL') '禁止同 id 外包绕过'
    Reject 'external-policy-cannot-override' @('--cards', 'external_cards', '--pack-dir', $external) '外包整包下架'
    Reject 'same-id-external-cannot-bypass' @('--cards', 'original', '--pack-dir', $external) '禁止同 id 外包绕过'
    Reject 'disabled-mixed' @('--cards', $mixed.PackId) '该内容包已下架'
    Set-Policy '{"disabledPacks":null}'
    Reject 'malformed-policy' @('--card', $first) '下架策略'
    Set-Policy '{"disabledPacks":["original","original_wave2","fan_holo_niji","fan_asoul_vr"]}'
    Reject 'all-packs-disabled' @('--cards', 'original') '该内容包已下架'
    [IO.File]::Delete((Join-Path $deploy 'Content/PackPolicy.json'))
    Assert-Cards 'missing-policy' $original @($first) @('--card', $first)
    $checks.Add('外包单张/整包、主播过滤、少于6人隐藏、内置别名、外包政策与同id绕过、混合门禁、坏政策、全部下架及缺省政策')
    foreach ($path in $inputs.Keys) {
        Check ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ceq $inputs[$path]) '原构建或资源文件被修改。'
    }
    $checks.Add('原发布输出及正式政策 SHA256 均未变化；所有子进程 cwd 为空')
    $passed = $true
}
catch {
    $failure = $_.Exception.Message
    throw
}
finally {
    if ($restoreLocale) { [StreamDuel.Core.Loc]::Current = $previousLocale }
    $summary = [ordered]@{ passed = $passed; failure = $failure; run_directory = $run; configuration = $Configuration
        checks = $checks.ToArray(); references = $references.ToArray(); invocations = $calls.ToArray(); source_sha256 = $inputs }
    Write-Utf8 (Join-Path $run 'result.json') ($summary | ConvertTo-Json -Depth 12)
    Write-Host "T113 CLI cards verification: passed=$passed; $run"
}
