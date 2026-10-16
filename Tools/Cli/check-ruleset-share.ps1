#requires -Version 7.0
param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug')

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$repositoryPrefix = $repository.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$cli = Join-Path $PSScriptRoot "bin/$Configuration/net8.0/Cli.dll"
if (-not (Test-Path -LiteralPath $cli -PathType Leaf)) { throw "请先构建 Tools/Cli -c $Configuration。" }
$run = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ('obj/ruleset-share-check/' + [Guid]::NewGuid().ToString('N'))))
if (-not $run.StartsWith($repositoryPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw '验收产物必须位于当前工作树内。' }
[void][IO.Directory]::CreateDirectory($run)
$utf8 = [Text.UTF8Encoding]::new($false, $true)
$checks = [Collections.Generic.List[string]]::new()
$cleanupFailures = [Collections.Generic.List[string]]::new()
$processCount = 0
$failure = $null

function Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "$Message 证据目录：$run" }
}

function Write-Text([string]$Path, [string]$Text) {
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path))
    [IO.File]::WriteAllText($Path, $Text, $utf8)
}

function Invoke-Cli([string]$Name, [string[]]$Parameters, [int]$Exit = 0) {
    $work = Join-Path $run "calls/$Name"
    [void][IO.Directory]::CreateDirectory($work)
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = 'dotnet'
    $start.ArgumentList.Add($cli)
    foreach ($argument in $Parameters) { $start.ArgumentList.Add($argument) }
    $start.WorkingDirectory = $work
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardInputEncoding = $utf8
    $start.StandardOutputEncoding = $utf8
    $start.StandardErrorEncoding = $utf8
    $start.Environment['TEMP'] = $run
    $start.Environment['TMP'] = $run
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    $started = $false
    $outTask = $null
    $errTask = $null
    try {
        $started = $process.Start()
        Check $started "$Name 无法启动。"
        $script:processCount++
        $outTask = $process.StandardOutput.ReadToEndAsync()
        $errTask = $process.StandardError.ReadToEndAsync()
        $process.StandardInput.Close()
        Check ($process.WaitForExit(30000)) "$Name 超过 30 秒未退出。"
        # 进程退出不代表异步输出已经排空；等待两路读取结束再判断结果。
        Check ([Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($outTask, $errTask), 5000)) "$Name 输出未结束。"
        $result = [pscustomobject]@{ Out = $outTask.Result; Err = $errTask.Result; Exit = $process.ExitCode }
        Check ($result.Exit -eq $Exit) "$Name 应退出 $Exit，实际 $($result.Exit)。"
        if ($Exit -eq 0) { Check ([string]::IsNullOrWhiteSpace($result.Err)) "$Name 正常运行出现错误。" }
        else { Check ($result.Err.StartsWith('错误：')) "$Name 未提供中文错误。" }
        return $result
    }
    finally {
        try {
            if ($started -and -not $process.HasExited) {
                $process.Kill($true)
                Check ($process.WaitForExit(5000)) "$Name 清理时未退出。"
            }
            if ($null -ne $outTask) {
                Check ($outTask.Wait(5000)) "$Name 标准输出清理超时。"
                Write-Text (Join-Path $work 'stdout.log') $outTask.Result
            }
            if ($null -ne $errTask) {
                Check ($errTask.Wait(5000)) "$Name 标准错误清理超时。"
                Write-Text (Join-Path $work 'stderr.log') $errTask.Result
            }
        }
        catch { $cleanupFailures.Add("${Name}：$($_.Exception.Message)") }
        finally { $process.Dispose() }
    }
}

function Fingerprint([string]$Directory) {
    if (-not (Test-Path -LiteralPath $Directory)) { return '<missing>' }
    return (@(Get-ChildItem -LiteralPath $Directory -Recurse -Force | Sort-Object FullName | ForEach-Object {
        if ($_.PSIsContainer) { 'directory:' + $_.FullName }
        else { $_.FullName + ':' + $_.LastWriteTimeUtc.Ticks + ':' + (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    }) -join "`n")
}

function Json($Value) { return (ConvertTo-Json -InputObject $Value -Depth 25 -Compress) }
function Read-Profile([string]$Directory) {
    return ([IO.File]::ReadAllText((Join-Path $Directory 'profile.json'), $utf8) | ConvertFrom-Json -AsHashtable)
}

function Profile-Fingerprint([string]$Directory) {
    $path = Join-Path $Directory 'profile.json'
    if (-not (Test-Path -LiteralPath $path)) { return '<missing>' }
    return [IO.File]::GetLastWriteTimeUtc($path).Ticks.ToString() + ':' + (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
}

function Invoke-Management([string]$Name, [string]$Directory, [string[]]$Parameters,
    [int]$Exit = 0, [bool]$ReadOnly = $false) {
    $before = Fingerprint $Directory
    $profileBefore = Profile-Fingerprint $Directory
    $result = Invoke-Cli $Name ($Parameters + @('--save-dir', $Directory)) $Exit
    Check (-not $result.Out.Contains('监听端口：') -and -not $result.Out.Contains('对局结束：') -and
        -not $result.Out.Contains('赛制结束：')) '赛制管理命令启动了对战。'
    if ($ReadOnly) {
        Check ((Fingerprint $Directory) -ceq $before) "$Name 改写了档案、锁、文件时间戳或目录内容。"
    }
    elseif ($Exit -ne 0) {
        # 写命令可使用已有的档案锁，但失败绝不能覆盖或重写原档案。
        Check ((Profile-Fingerprint $Directory) -ceq $profileBefore) "$Name 拒绝输入时改写了档案字节或时间戳。"
    }
    return $result
}

function Export-Code([string]$Name, [string]$Directory, [string]$RulesetName) {
    $result = Invoke-Management $Name $Directory @('--ruleset-code', $RulesetName) 0 $true
    $codes = [regex]::Matches($result.Out, 'SDRULE1-[A-Za-z0-9_-]+')
    Check ($codes.Count -eq 1) '导出没有提供恰好一个 SDRULE1 分享码。'
    return $codes[0].Value
}

try {
    $missing = Join-Path $run 'missing-parent/saves'
    $empty = Invoke-Management 'list-missing' $missing @('--ruleset-list') 0 $true
    Check ($empty.Out.Contains('休闲') -and $empty.Out.Contains('排位') -and $empty.Out.Contains('乱斗')) '空档查询没有列出内置赛制。'
    $builtinCode = Export-Code 'code-builtin-missing' $missing 'casual'
    $null = Invoke-Management 'code-unknown-missing' $missing @('--ruleset-code', '不存在的赛制') 1 $true
    Check (-not (Test-Path -LiteralPath (Split-Path $missing -Parent))) '只读查询创建了缺失的父目录。'
    $checks.Add('缺档案 list/code 只读；内置预设可导出；不存在名称明确拒绝')

    $save = Join-Path $run 'source-saves'
    $profileJson = '{"packs":{},"ratings":{"默认":{"rating":1600,"games":2}}}'
    Write-Text (Join-Path $save 'profile.json') $profileJson
    Write-Text (Join-Path $save 'settings.json') '{"version":1,"defaultPack":"original"}'
    Write-Text (Join-Path $save 'teams.json') '{"version":1,"teams":[]}'
    $sideFilesBefore = @('settings.json', 'teams.json') | ForEach-Object { (Get-FileHash -LiteralPath (Join-Path $save $_)).Hash }
    $ruleset = [ordered]@{
        id = 'smoke_short'; name = '原始赛制名'; season = $null; banOne = $false; leadPick = $false
        bestOf = 1; modifiers = @(); turnSeconds = 0; bankSeconds = 0
    }
    $rulesJson = Json $ruleset
    $name = '我的短局'
    $null = Invoke-Management 'save-literal' $save @('--ruleset-save', $name, $rulesJson, '--pack', 'original')
    $listed = Invoke-Management 'list-saved' $save @('--ruleset-list') 0 $true
    Check ($listed.Out.Contains($name)) '字面 JSON 保存后列表缺少自定义名称。'
    $code = Export-Code 'code-saved' $save $name
    $source = Read-Profile $save
    Check ((Json $source.packs) -ceq '{}' -and $source.ratings.'默认'.rating -eq 1600 -and
        $source.ratings.'默认'.games -eq 2) '保存赛制改写了原战绩或排位。'
    Check ($source.myRulesets.Count -eq 1) '首次保存没有恰好新增一个我的赛制。'
    $stored = $source.myRulesets[0]
    Check ($stored.id -ceq 'smoke_short' -and $stored.name -ceq $name -and $null -eq $stored.season -and
        $stored.banOne -eq $false -and $stored.leadPick -eq $false -and $stored.bestOf -eq 1 -and
        $stored.modifiers.Count -eq 0 -and $stored.turnSeconds -eq 0 -and $stored.bankSeconds -eq 0) '保存后赛制字段丢失或被改变。'
    $rulesPath = Join-Path $run 'fixture/ruleset.json'
    Write-Text $rulesPath $rulesJson
    $null = Invoke-Management 'save-file' $save @('--ruleset-save', '文件短局', $rulesPath)
    $null = Invoke-Management 'save-code' $save @('--ruleset-save', '码复制短局', $code)
    $null = Invoke-Management 'copy-builtin' $save @('--ruleset-save', '内置副本', $builtinCode)
    $listCopies = Invoke-Management 'list-copies' $save @('--ruleset-list') 0 $true
    foreach ($expected in @($name, '文件短局', '码复制短局', '内置副本')) {
        Check ($listCopies.Out.Contains($expected)) "保存列表缺少 $expected。"
    }
    $checks.Add('字面 JSON、JSON 文件和分享码均可保存；内置预设可复制；保留原战绩与排位')

    $importSave = Join-Path $run 'import-saves'
    $null = Invoke-Management 'import-code' $importSave @('--ruleset-import', $code)
    Check ((Export-Code 'export-imported' $importSave $name) -ceq $code) '跨档案导入导出不能逐字恢复同一赛制分享码。'
    $null = Invoke-Management 'rename' $save @('--ruleset-rename', '文件短局', '更名短局')
    $renamed = Invoke-Management 'list-renamed' $save @('--ruleset-list') 0 $true
    Check ($renamed.Out.Contains('更名短局') -and -not $renamed.Out.Contains('文件短局')) '改名后旧名称仍存在或新名称缺失。'
    $null = Invoke-Management 'old-name-rejected' $save @('--ruleset-code', '文件短局') 1 $true
    $null = Invoke-Management 'delete' $save @('--ruleset-delete', '更名短局')
    $null = Invoke-Management 'deleted-name-rejected' $save @('--ruleset-code', '更名短局') 1 $true
    $checks.Add('跨档案分享码往返完全一致；自定义赛制可改名和删除，旧名称不能继续查询')

    $unsafeName = "  清`u{200b}洁`n名称  "
    $null = Invoke-Management 'clean-name' $save @('--ruleset-save', $unsafeName, $rulesJson)
    $cleaned = Invoke-Management 'list-cleaned' $save @('--ruleset-list') 0 $true
    $savedText = [IO.File]::ReadAllText((Join-Path $save 'profile.json'), $utf8)
    Check ($cleaned.Out.Contains('清洁名称') -and $savedText.Contains('清洁名称') -and
        -not $savedText.Contains([string][char]0x200b)) '自定义赛制名称未按文字规则清理不可见字符或换行。'
    $checks.Add('玩家赛制名称清理控制字符与不可见字符后保存、显示')

    $badBestOf = $rulesJson.Replace('"bestOf":1', '"bestOf":2')
    $badModifier = $rulesJson.Replace('"modifiers":[]', '"modifiers":["does_not_exist"]')
    $unknownField = $rulesJson.Substring(0, $rulesJson.Length - 1) + ',"unknownSetting":true}'
    $duplicateField = $rulesJson.Substring(0, $rulesJson.Length - 1) + ',"bestOf":3}'
    $unknownFieldPath = Join-Path $run 'fixture/unknown-field.json'
    $duplicateFieldPath = Join-Path $run 'fixture/duplicate-field.json'
    Write-Text $unknownFieldPath $unknownField
    Write-Text $duplicateFieldPath $duplicateField
    $codeIndex = 12
    $replacement = if ($code[$codeIndex] -ceq 'A') { 'B' } else { 'A' }
    $tampered = $code.Substring(0, $codeIndex) + $replacement + $code.Substring($codeIndex + 1)
    $invalid = @(
        @{ Name = 'bestof'; Args = @('--ruleset-save', '非法局数', $badBestOf) },
        @{ Name = 'modifier'; Args = @('--ruleset-save', '未知修饰', $badModifier) },
        @{ Name = 'json'; Args = @('--ruleset-save', '坏 JSON', '{broken') },
        @{ Name = 'unknown-literal'; Args = @('--ruleset-save', '未知字段', $unknownField) },
        @{ Name = 'duplicate-literal'; Args = @('--ruleset-save', '重复字段', $duplicateField) },
        @{ Name = 'unknown-file'; Args = @('--ruleset-save', '文件未知字段', $unknownFieldPath) },
        @{ Name = 'duplicate-file'; Args = @('--ruleset-save', '文件重复字段', $duplicateFieldPath) },
        @{ Name = 'tampered'; Args = @('--ruleset-import', $tampered) },
        @{ Name = 'duplicate-save'; Args = @('--ruleset-save', $name, $rulesJson) },
        @{ Name = 'duplicate-import'; Args = @('--ruleset-import', $code) },
        @{ Name = 'import-builtin'; Args = @('--ruleset-import', $builtinCode) },
        @{ Name = 'overwrite-builtin'; Args = @('--ruleset-save', 'casual', $rulesJson) },
        @{ Name = 'delete-builtin'; Args = @('--ruleset-delete', 'casual') },
        @{ Name = 'rename-builtin'; Args = @('--ruleset-rename', 'casual', '改掉内置') },
        @{ Name = 'rename-to-builtin'; Args = @('--ruleset-rename', $name, 'ranked') },
        @{ Name = 'rename-collision'; Args = @('--ruleset-rename', $name, '码复制短局') },
        @{ Name = 'delete-unknown'; Args = @('--ruleset-delete', '不存在的赛制') },
        @{ Name = 'missing-value'; Args = @('--ruleset-save', '少一个参数') },
        @{ Name = 'duplicate'; Args = @('--ruleset-list', '--ruleset-list') },
        @{ Name = 'mixed-modes'; Args = @('--ruleset-list', '--ruleset-import', $code) },
        @{ Name = 'battle-conflict'; Args = @('--ruleset-list', '--ruleset', 'casual') },
        @{ Name = 'auto-conflict'; Args = @('--ruleset-code', $name, '--auto') },
        @{ Name = 'network-conflict'; Args = @('--ruleset-list', '--host', '0') },
        @{ Name = 'rating-conflict'; Args = @('--ruleset-list', '--new-season') }
    )
    foreach ($case in $invalid) {
        $null = Invoke-Management ('invalid-' + $case.Name) $save $case.Args 1
    }
    Check ((Export-Code 'builtin-unchanged' $missing 'casual') -ceq $builtinCode) '拒绝修改内置赛制后预设仍发生改变。'
    $checks.Add('非法规则、损坏码、内置修改、名称冲突、缺参、重复或混用命令均拒绝且档案不变')

    $rejectedMissing = Join-Path $run 'rejected-missing-parent/saves'
    $null = Invoke-Management 'invalid-json-missing-dir' $rejectedMissing @('--ruleset-save', '非法新档', '{broken') 1 $true
    $null = Invoke-Management 'invalid-code-missing-dir' $rejectedMissing @('--ruleset-import', $tampered) 1 $true
    Check (-not (Test-Path -LiteralPath (Split-Path $rejectedMissing -Parent))) '非法 JSON 或码在校验前创建了存档父目录。'
    $invalidUtf8Path = Join-Path $run 'fixture/invalid-utf8.json'
    [IO.File]::WriteAllBytes($invalidUtf8Path, [byte[]]@(0x7b, 0x22, 0xff, 0x22, 0x7d))
    $invalidUtf8Before = (Get-FileHash -LiteralPath $invalidUtf8Path -Algorithm SHA256).Hash
    $invalidUtf8 = Invoke-Management 'invalid-utf8-file' $save @('--ruleset-save', '非法编码', $invalidUtf8Path) 1
    Check ($invalidUtf8.Err.Contains('UTF-8') -and
        (Get-FileHash -LiteralPath $invalidUtf8Path -Algorithm SHA256).Hash -ceq $invalidUtf8Before) '非法 UTF-8 输入没有中文编码错误，或来源文件被改写。'
    $checks.Add('缺目录时非法 JSON/码不创建目录；非法 UTF-8 文件中文拒绝并保留原档案与来源')

    $badSave = Join-Path $run 'bad-profile-saves'
    Write-Text (Join-Path $badSave 'profile.json') '{broken'
    foreach ($case in @(
        @{ Name = 'list'; Args = @('--ruleset-list') },
        @{ Name = 'code'; Args = @('--ruleset-code', $name) },
        @{ Name = 'save'; Args = @('--ruleset-save', '不能覆盖坏档', $rulesJson) },
        @{ Name = 'import'; Args = @('--ruleset-import', $code) },
        @{ Name = 'rename'; Args = @('--ruleset-rename', $name, '不能覆盖坏档') },
        @{ Name = 'delete'; Args = @('--ruleset-delete', $name) }
    )) { $null = Invoke-Management ('bad-profile-' + $case.Name) $badSave $case.Args 1 ($case.Name -in @('list', 'code')) }
    $checks.Add('坏档案的管理查询和写入均明确失败，不以默认档案覆盖原字节')

    $battle = Invoke-Cli 'custom-battle' @('--ruleset', $name, '--auto', '--seed', '131', '--save-dir', $importSave)
    Check ($battle.Out.Contains('赛制结束：共 1 局') -and $battle.Out.Contains($name)) '保存的自定义名称不能启动并完成真实一局对战。'
    $afterBattle = Read-Profile $importSave
    $stats = $afterBattle.packs.original
    Check (($stats.wins + $stats.losses + $stats.draws) -eq 1 -and -not $afterBattle.Contains('ratings')) '本地自定义一局没有正确累计普通战绩，或误生成了排位分。'
    Check ((Export-Code 'code-after-battle' $importSave $name) -ceq $code) '完局保存档案丢失或改写了自定义赛制。'
    $replays = @(Get-ChildItem -LiteralPath (Join-Path $importSave 'replay-library') -File -Filter '*.json' | Where-Object { $_.Name -ne 'index.json' })
    Check ($replays.Count -eq 1) '真实自定义对局没有恰好归档一份录像。'
    $record = [IO.File]::ReadAllText($replays[0].FullName, $utf8) | ConvertFrom-Json -AsHashtable
    Check (-not $record.library.Contains('ranked')) '本地自定义对局被错误标为联网排位。'
    $checks.Add('保存名称可启动真实无计时一局；普通战绩和录像落盘；保留赛制且不计联网排位')

    $sideFilesAfter = @('settings.json', 'teams.json') | ForEach-Object { (Get-FileHash -LiteralPath (Join-Path $save $_)).Hash }
    Check (($sideFilesAfter -join ',') -ceq ($sideFilesBefore -join ',')) '赛制管理改写了设置或队伍预设。'
}
catch { $failure = $_ }
$report = [ordered]@{
    Status = $(if ($null -eq $failure -and $cleanupFailures.Count -eq 0) { 'PASS' } else { 'FAIL' })
    Checks = $checks.ToArray(); Processes = $processCount
    Failure = $(if ($null -eq $failure) { $null } else { $failure.Exception.Message })
    CleanupFailures = $cleanupFailures.ToArray()
}
Write-Text (Join-Path $run 'report.json') (ConvertTo-Json -InputObject $report -Depth 5)
if ($null -ne $failure) { throw $failure }
Check ($cleanupFailures.Count -eq 0) ('子进程清理失败：' + ($cleanupFailures -join '；'))
Write-Output "PASS：$($checks.Count) 组赛制分享检查，$processCount 个 CLI 进程；证据：$run"
