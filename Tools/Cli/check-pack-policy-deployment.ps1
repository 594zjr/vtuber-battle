#requires -Version 7.0
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug',
    [ValidateRange(10, 900)][int]$TimeoutSeconds = 180
)

# 可留在 Engine.Tests/obj/t101-review，或复制到 Tools/Cli。仅使用已构建输出，不构建。
# 先构建 Cli/Sim/Ladder/PackCheck 的指定 Configuration，以及 Bench 的 Release。
# 每个发布副本和进程产物都在本仓库新的 obj GUID 目录；原输出/源策略不改动。
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'Cli.csproj') -PathType Leaf) {
    $repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
}
elseif ((Split-Path $PSScriptRoot -Leaf) -eq 't101-review' -and
    (Split-Path (Split-Path $PSScriptRoot -Parent) -Leaf) -eq 'obj' -and
    (Split-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) -Leaf) -eq 'Engine.Tests') {
    $repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
}
else { throw '脚本只能位于 Tools/Cli 或 Engine.Tests/obj/t101-review。' }
$prefix = $repository.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$current = [IO.Path]::GetFullPath((Get-Location).Path)
if ($current -ne $repository -and -not $current.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw '请在本仓库内运行。'
}
$utf8 = [Text.UTF8Encoding]::new($false)
$run = Join-Path $repository ('Engine.Tests/obj/t101-deployment/' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($run)
$temp = Join-Path $run 'temp'
[void][IO.Directory]::CreateDirectory($temp)
$tools = @{}
$inputs = @{}
$checks = [Collections.Generic.List[string]]::new()
$calls = [Collections.Generic.List[object]]::new()
$failure = $null

function Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}
function Write-Utf8([string]$Path, [string]$Text) {
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path))
    [IO.File]::WriteAllText($Path, $Text, $utf8)
}
function Remember([string]$Path) {
    $inputs[$Path] = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}
function Copy-Safe([string]$Source, [string]$Destination) {
    $sourceFull = [IO.Path]::GetFullPath($Source)
    $destFull = [IO.Path]::GetFullPath($Destination)
    Check ($sourceFull.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -and
        $destFull.StartsWith($run + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) '复制路径必须在本仓库内。'
    $item = Get-Item -LiteralPath $sourceFull
    Check (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) '拒绝复制重解析点。'
    if ($item.PSIsContainer) {
        [void][IO.Directory]::CreateDirectory($destFull)
        foreach ($child in Get-ChildItem -LiteralPath $sourceFull -Force) {
            Copy-Safe $child.FullName (Join-Path $destFull $child.Name)
        }
    }
    else {
        Remember $sourceFull
        [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destFull))
        [IO.File]::Copy($sourceFull, $destFull, $false)
    }
}
function Set-Policy([string]$Json) {
    foreach ($tool in $tools.Values) { Write-Utf8 $tool.Policy $Json }
}
function Invoke-Tool([string]$Tool, [string]$Name, [string[]]$Arguments, [int]$Exit = 0) {
    $work = Join-Path $run ('calls/' + $Name)
    [void][IO.Directory]::CreateDirectory($work)
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = 'dotnet'
    $start.ArgumentList.Add($tools[$Tool].Dll)
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
    $start.Environment['DOTNET_SKIP_FIRST_TIME_EXPERIENCE'] = '1'
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
        Check ($process.WaitForExit($TimeoutSeconds * 1000)) "$Name 超时。"
        $result = [pscustomobject]@{ Out = $stdout.Result; Err = $stderr.Result; Exit = $process.ExitCode; Work = $work }
        Write-Utf8 (Join-Path $work 'stdout.log') $result.Out
        Write-Utf8 (Join-Path $work 'stderr.log') $result.Err
        $calls.Add([ordered]@{ name = $Name; tool = $Tool; exit = $result.Exit; expected = $Exit })
        Check ($result.Exit -eq $Exit) "$Name 退出码不符。"
        return $result
    }
    finally {
        if ($started -and -not $process.HasExited) { $process.Kill($true); [void]$process.WaitForExit(10000) }
        $process.Dispose()
    }
}
function Reject([string]$Tool, [string]$Name, [string[]]$Arguments, [string]$Reason) {
    $result = Invoke-Tool $Tool $Name $Arguments 1
    Check ($result.Err.Contains($Reason)) "$Name 缺少指定中文拒绝原因。"
    Check ($result.Out.Length -eq 0) "$Name 在拒绝前已经产生正常输出。"
    Check (-not (Test-Path -LiteralPath (Join-Path $result.Work 'saves'))) "$Name 不应创建存档目录。"
    Check (-not (Test-Path -LiteralPath (Join-Path $result.Work 'bench_out'))) "$Name 不应生成基准报告。"
    $checks.Add($Name)
}

try {
    foreach ($name in @('Cli', 'Sim', 'Ladder', 'Bench', 'PackCheck')) {
        $build = if ($name -eq 'Bench') { 'Release' } else { $Configuration }
        $source = Join-Path $repository "Tools/$name/bin/$build/net8.0"
        Check (Test-Path -LiteralPath (Join-Path $source "$name.dll") -PathType Leaf) "请先构建 Tools/$name -c $build。"
        $destination = Join-Path $run "deploy/$name"
        [void][IO.Directory]::CreateDirectory($destination)
        foreach ($file in Get-ChildItem -LiteralPath $source -File) { Copy-Safe $file.FullName (Join-Path $destination $file.Name) }
        if ($name -ne 'PackCheck') {
            Copy-Safe (Join-Path $source 'Content') (Join-Path $destination 'Content')
            $policyPath = Join-Path $destination 'Content/PackPolicy.json'
            Check (Test-Path -LiteralPath $policyPath -PathType Leaf) "$name 发布输出漏复制 PackPolicy.json。"
        }
        else {
            # PackCheck 从最近的仓库标记找资源；建立独立源码结构，不改真实 Resources。
            Copy-Safe (Join-Path $repository 'AGENTS.md') (Join-Path $destination 'AGENTS.md')
            Copy-Safe (Join-Path $repository 'Engine/StreamDuel.Core.csproj') (Join-Path $destination 'Engine/StreamDuel.Core.csproj')
            Copy-Safe (Join-Path $repository 'UnityProject/Assets/StreamDuel/Resources') (Join-Path $destination 'UnityProject/Assets/StreamDuel/Resources')
            $policyPath = Join-Path $destination 'UnityProject/Assets/StreamDuel/Resources/PackPolicy.json'
        }
        $tools[$name] = @{ Dll = Join-Path $destination "$name.dll"; Root = $destination; Policy = $policyPath }
    }
    $sourcePolicy = Join-Path $repository 'UnityProject/Assets/StreamDuel/Resources/PackPolicy.json'
    $sourcePolicyHash = (Get-FileHash -LiteralPath $sourcePolicy -Algorithm SHA256).Hash
    foreach ($name in @('Cli', 'Sim', 'Ladder', 'Bench')) {
        Check ((Get-FileHash -LiteralPath $tools[$name].Policy -Algorithm SHA256).Hash -ceq $sourcePolicyHash) "$name 发布输出策略与真实资源配置不同。"
    }
    $checks.Add('deployed-policy-matches-real-resource')
    [void][Reflection.Assembly]::LoadFrom((Join-Path $tools['Cli'].Root 'StreamDuel.Core.dll'))
    $content = Join-Path $tools['Cli'].Root 'Content'
    $rules = [IO.File]::ReadAllText((Join-Path $content 'StreamDuelRules.json'))
    $pack = [IO.File]::ReadAllText((Join-Path $content 'Packs/original.json'))
    $empty = [StreamDuel.Core.PackPolicy]::Empty
    $db = [StreamDuel.Core.ContentDb]::FromJson($rules, $pack, $empty)
    $ids = [string[]]@($db.Streamers | ForEach-Object { $_.Id })
    Check ($ids.Count -ge 12) '审查夹具需要 original 的至少 12 位主播。'
    $disabled = $ids[0]
    $team = [string[]]$ids[0..5]
    $preset = [StreamDuel.Core.TeamPreset]::new()
    $preset.PackId = $db.PackId
    $preset.Name = '政策审查队伍'
    $preset.Ids.AddRange($team)
    $teamCode = [StreamDuel.Core.TeamCode]::Encode($preset)
    $weights = Join-Path $run 'weights.json'
    Write-Utf8 $weights ([StreamDuel.Core.AiWeights]::Default.ToJson())
    Remember $weights
    $teams = [Collections.Generic.IList[string][]]@($team, [string[]]$ids[6..11])
    $battle = [StreamDuel.Core.Battle]::new($db, $teams, [string[]]@("甲$([char]0x1b)", '乙'), [uint32]101)
    $v2 = $battle.Record.ToJson()
    Check ($battle.Record.Version -eq 2) '普通录像必须仍为 v2。'
    $battle.Surrender(0)
    Check ($battle.Record.Version -eq 3) '投降录像必须升级为 v3。'
    $v3 = $battle.Record.ToJson()
    $v2Path = Join-Path $run 'ordinary-v2.json'
    $v3Path = Join-Path $run 'surrender-v3.json'
    Write-Utf8 $v2Path $v2
    Write-Utf8 $v3Path $v3
    Remember $v2Path
    Remember $v3Path
    $librarySave = Join-Path $run 'library-save'
    $libraryDir = Join-Path $librarySave 'replay-library'
    $library = [StreamDuel.Core.ReplayLibrary]::new($libraryDir, 200, $null, $empty)
    $libraryId = $library.Save($battle.Record, [StreamDuel.Core.BattleSummary]::From($battle), '保留原始记录')
    $libraryFile = Join-Path $libraryDir ($libraryId + '.json')
    Remember $libraryFile

    Set-Policy '{}'
    $baseline = Invoke-Tool 'Cli' 'empty-list' @('--list-packs')
    Check ($baseline.Out -match '(?m)^original：') '空政策应列出 original。'
    foreach ($tool in $tools.Values) { [IO.File]::Delete($tool.Policy) }
    $missing = Invoke-Tool 'Cli' 'missing-list' @('--list-packs')
    Check ($missing.Out -ceq $baseline.Out -and $missing.Err -ceq $baseline.Err) '缺失策略应与空政策兼容。'
    $checks.Add('missing-equals-empty')

    Set-Policy '{"disabledPacks":["original"],"reason":{"original":"部署整包审查"}}'
    $list = Invoke-Tool 'Cli' 'disabled-pack-list' @('--list-packs')
    Check ($list.Out -notmatch '(?m)^original：' -and $list.Out.Length -gt 0) '只隐藏下架包，其他包应保留。'
    Reject 'Cli' 'disabled-pack-cli' @('--auto') '该内容包已下架'
    Reject 'Sim' 'disabled-pack-sim' @('--games', '1') '该内容包已下架'
    Reject 'Sim' 'disabled-pack-tune' @('--tune', '--rounds', '1', '--candidates', '1', '--games', '8', '--holdout-games', '8') '该内容包已下架'
    Reject 'Sim' 'disabled-pack-compare' @('--compare', '--games', '8', '--weights', $weights, '--baseline', $weights) '该内容包已下架'
    Reject 'Ladder' 'disabled-pack-ladder' @('--teams', '2', '--rounds', '1') '该内容包已下架'
    Reject 'Bench' 'disabled-pack-bench' @('--quick') '该内容包已下架'
    $diagnostic = Invoke-Tool 'PackCheck' 'disabled-pack-packcheck' @('--list-packs')
    Check ($diagnostic.Out.Contains('[下架]') -and $diagnostic.Out.Contains('部署整包审查') -and
        $diagnostic.Out -notmatch '(?m)^original：') 'PackCheck 应显示真实策略和下架诊断。'
    $checks.Add('pack-list-and-diagnostics')

    $one = @{ disabledStreamers = @($disabled); reason = @{ $disabled = '部署主播审查' } } | ConvertTo-Json -Compress
    Set-Policy $one
    $filtered = [StreamDuel.Core.ContentDb]::FromJson($rules, $pack, [StreamDuel.Core.PackPolicy]::FromJson($one))
    Check ($filtered.Streamers.Count -eq $db.Streamers.Count - 1) '过滤夹具必须确实移除一位。'
    Reject 'Cli' 'disabled-team' @('--team', ($team -join ','), '--auto') '该主播已下架'
    Reject 'Cli' 'disabled-team-code' @('--team-code', $teamCode, '--auto') '该主播已下架'
    Reject 'Bench' 'disabled-fixed-bench-team' @('--quick') '该主播已下架'
    $suggest = Invoke-Tool 'Cli' 'filtered-suggest' @('--export-team')
    $match = [regex]::Match($suggest.Out, '(?m)^本方队伍码：([^\r\n]+)')
    Check $match.Success '过滤后仍应能自动建议合法队伍。'
    $exported = [StreamDuel.Core.TeamCode]::Decode($match.Groups[1].Value)
    Check ($exported.Ids.Count -eq 6 -and -not $exported.Ids.Contains($disabled)) '自动队伍不应包含已下架主播。'
    Check (-not (Test-Path -LiteralPath (Join-Path $suggest.Work 'saves'))) 'EOF 提前退出不保存。'
    $tuneReport = Join-Path $run 'filtered-tune.json'
    $null = Invoke-Tool 'Sim' 'filtered-tune' @('--tune', '--rounds', '1', '--candidates', '1', '--games', '8', '--holdout-games', '8', '--report', $tuneReport)
    $tuning = Get-Content -LiteralPath $tuneReport -Raw | ConvertFrom-Json
    Check ($tuning.status -eq 'completed' -and $tuning.contentHash -ceq $filtered.ContentHash()) 'Tune 必须使用过滤后的内容指纹。'
    $compare = Invoke-Tool 'Sim' 'filtered-compare' @('--compare', '--games', '8', '--weights', $weights, '--baseline', $weights)
    $comparison = $compare.Out | ConvertFrom-Json
    Check ($comparison.contentHash -ceq $filtered.ContentHash() -and $comparison.result.games -eq 8) 'Compare 必须使用过滤后的内容。'
    $checks.Add('filtered-suggestion-tuning-and-compare')

    foreach ($fixture in @(@('v2', $v2Path), @('v3', $v3Path))) {
        Reject 'Cli' ('blocked-replay-' + $fixture[0]) @('--replay', $fixture[1], '--auto', '--replay-best-effort', '--share') '该主播已下架'
    }
    $listed = Invoke-Tool 'Cli' 'blocked-library-list' @('--replays', '--save-dir', $librarySave)
    Check ($listed.Out.Contains('不可重放：') -and $listed.Out.Contains('该主播已下架') -and
        -not $listed.Out.Contains('已损坏：')) '录像库需区分不可用与损坏。'
    $index = Get-Content -LiteralPath (Join-Path $libraryDir 'index.json') -Raw | ConvertFrom-Json
    Check (@($index.entries).Count -eq 1 -and $index.entries[0].isUnavailable -and
        -not $index.entries[0].isCorrupt) '原生索引需要保留下架元数据。'
    Reject 'Cli' 'blocked-library-load' @('--replay', $libraryId, '--save-dir', $librarySave, '--auto') '该主播已下架'
    Set-Policy '{}'
    $report = Join-Path $run 'restored-v3.md'
    $restored = Invoke-Tool 'Cli' 'restored-v3' @('--replay', $libraryId, '--save-dir', $librarySave, '--auto', '--share', '--report', $report)
    Check ($restored.Out.Contains('投降') -and -not $restored.Out.Contains([string][char]0x1b)) '恢复政策后 v3 回放应正确显示且清理昵称。'
    $markdown = [IO.File]::ReadAllText($report)
    Check ($markdown.Contains('投降') -and -not $markdown.Contains([string][char]0x1b)) 'Markdown 应显示清理后的 v3 日志。'
    $share = [regex]::Match($restored.Out, '(?m)^分享码：([^\r\n]+)')
    Check ($share.Success -and [StreamDuel.Core.ShareCode]::Decode($share.Groups[1].Value).ToJson() -ceq $v3) '分享必须保留原始 v3 记录。'
    Check (-not (Test-Path -LiteralPath (Join-Path $librarySave 'profile.json')) -and
        -not (Test-Path -LiteralPath (Join-Path $librarySave 'replays'))) '回看不应累计档案或保存旧格式录像。'
    Check (@(Get-ChildItem -LiteralPath $libraryDir -Filter '*.json' -File | Where-Object Name -ne 'index.json').Count -eq 1) '回看不应新建录像库条目。'
    $checks.Add('v2-v3-policy-gate-library-display-and-raw-sharing')

    # 外部包仅改副本 id；目录中的空政策样例不是包文件，不能解除部署方的下架策略。
    $externalId = 't101_external'
    Check ($baseline.Out -notmatch '(?m)^t101_external：') '外部包夹具 id 必须与内置包不同。'
    $externalDirectory = Join-Path $run 'external'
    $externalPath = Join-Path $externalDirectory 'external.json'
    Copy-Safe (Join-Path $repository 'UnityProject/Assets/StreamDuel/Resources/Packs/original.json') $externalPath
    $externalData = [IO.File]::ReadAllText($externalPath) | ConvertFrom-Json -AsHashtable
    $externalData['id'] = $externalId
    Write-Utf8 $externalPath ($externalData | ConvertTo-Json -Depth 100)
    Remember $externalPath
    $externalEmptyPolicy = Join-Path $externalDirectory 'PackPolicy.json.example'
    Write-Utf8 $externalEmptyPolicy '{"disabledPacks":[],"disabledStreamers":[],"reason":{}}'
    Remember $externalEmptyPolicy
    $externalPreset = [StreamDuel.Core.TeamPreset]::new()
    $externalPreset.PackId = $externalId
    $externalPreset.Name = '外部政策审查队伍'
    $externalPreset.Ids.AddRange($team)
    $externalCode = [StreamDuel.Core.TeamCode]::Encode($externalPreset)

    Set-Policy $one
    $externalList = Invoke-Tool 'Cli' 'external-filtered-list' @('--pack-dir', $externalDirectory, '--list-packs')
    Check ($externalList.Out -match '(?m)^t101_external：.*〔外部〕' -and
        $externalList.Err.Length -eq 0) '单主播下架后外部包仍应正常列出，非 JSON 策略样例不应作为坏包。'
    Reject 'Cli' 'external-disabled-team' @('--pack-dir', $externalDirectory, '--pack', $externalId, '--team', ($team -join ','), '--auto') '该主播已下架'
    Reject 'Cli' 'external-disabled-team-code' @('--pack-dir', $externalDirectory, '--team-code', $externalCode, '--auto') '该主播已下架'
    $externalSuggest = Invoke-Tool 'Cli' 'external-filtered-suggest' @('--pack-dir', $externalDirectory, '--pack', $externalId, '--export-team')
    $externalMatch = [regex]::Match($externalSuggest.Out, '(?m)^本方队伍码：([^\r\n]+)')
    Check $externalMatch.Success '外部过滤包应仍能通过真实 CLI 自动组队。'
    $externalExported = [StreamDuel.Core.TeamCode]::Decode($externalMatch.Groups[1].Value)
    Check ($externalExported.PackId -ceq $externalId -and $externalExported.Ids.Count -eq 6 -and
        -not $externalExported.Ids.Contains($disabled)) '外部包自动队伍必须保留包 id 并排除下架成员。'
    Check (-not (Test-Path -LiteralPath (Join-Path $externalSuggest.Work 'saves'))) '外部包 EOF 提前退出不保存。'
    $externalSim = Invoke-Tool 'Sim' 'external-filtered-sim' @('--pack-dir', $externalDirectory, '--pack', $externalId, '--games', '1')
    $externalRows = @(($externalSim.Out -split '\r?\n\r?\n', 2)[0] | ConvertFrom-Csv)
    Check ($externalRows.Count -eq $ids.Count - 1 -and $disabled -cnotin $externalRows.id -and
        (($externalRows.id | Sort-Object) -join ',') -ceq (($ids | Where-Object { $_ -cne $disabled } | Sort-Object) -join ',')) '外部包模拟统计必须只包含过滤后完整名单。'
    $checks.Add('external-streamer-policy-list-team-code-suggestion-and-simulation')

    Set-Policy '{"disabledPacks":["t101_external"],"reason":{"t101_external":"部署外部包审查"}}'
    foreach ($toolName in @('Cli', 'Sim')) {
        $externalHidden = Invoke-Tool $toolName ('external-hidden-list-' + $toolName) @('--pack-dir', $externalDirectory, '--list-packs')
        Check ($externalHidden.Out -notmatch '(?m)^t101_external：' -and
            $externalHidden.Out -match '(?m)^original：' -and $externalHidden.Err.Length -eq 0) '下架外部包应被隐藏且保留原内置包。'
    }
    Reject 'Cli' 'external-disabled-pack-cli' @('--pack-dir', $externalDirectory, '--pack', $externalId, '--auto') '该内容包已下架'
    Reject 'Sim' 'external-disabled-pack-sim' @('--pack-dir', $externalDirectory, '--pack', $externalId, '--games', '1') '该内容包已下架'
    $checks.Add('external-pack-policy-list-and-selection')

    $hide = @{ disabledStreamers = [string[]]$ids[0..6] } | ConvertTo-Json -Compress
    Set-Policy $hide
    $hidden = Invoke-Tool 'Cli' 'less-than-six-list' @('--list-packs')
    Check ($hidden.Out -notmatch '(?m)^original：') '不足六人应隐藏整包。'
    Reject 'Cli' 'less-than-six-load' @('--auto') '不足 6 位'
    Set-Policy '{"disabledPacks":["original"],"disabledPacks":[]}'
    foreach ($name in @('Cli', 'Sim', 'Ladder', 'Bench', 'PackCheck')) {
        $argsForTool = switch ($name) {
            'Cli' { @('--list-packs') }; 'Sim' { @('--list-packs') }; 'Ladder' { @('--teams', '2', '--rounds', '1') }
            'Bench' { @('--quick') }; 'PackCheck' { @('--list-packs') }
        }
        Reject $name ('invalid-policy-' + $name) $argsForTool '下架策略的字段重复'
    }
    foreach ($path in $inputs.Keys) {
        Check ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ceq $inputs[$path]) '源输入或录像原字节被修改。'
    }
    $checks.Add('source-and-record-sha-unchanged')
}
catch { $failure = $_.Exception.Message; throw }
finally {
    Write-Utf8 (Join-Path $run 'result.json') ([ordered]@{
        completed = ($null -eq $failure); failure = $failure; checks = $checks.ToArray(); calls = $calls.ToArray(); inputSHA256 = $inputs
    } | ConvertTo-Json -Depth 8)
    Write-Host ('T101 部署检查产物：' + $run)
}
