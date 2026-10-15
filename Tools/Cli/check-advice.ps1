#requires -Version 7.0
param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug')

# 审查扩展草稿，基于 PR81/T92 9b248bfd1f1923464d6399c4c77dc19ddc754fd3。
# 未执行；应用时移到 Tools/Cli 后运行。原全部断言保留，新增 T86/T89 互斥及三模式真实存档核验。
# timer/share/report 的既有兼容例仍在首发 q 退出，只证明开局兼容和退出不保存，不宣称完整导出。
if (-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'Cli.csproj') -PathType Leaf)) {
    throw '此审查草稿须先放到本工作树 Tools/Cli，再从新 PowerShell 进程运行；不能直接从 obj 执行。'
}

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$binaryDirectory = Join-Path $PSScriptRoot "bin/$Configuration/net8.0"
$cliDll = Join-Path $binaryDirectory 'Cli.dll'
$coreDll = Join-Path $binaryDirectory 'StreamDuel.Core.dll'
foreach ($path in @($cliDll, $coreDll)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "请先运行 dotnet build Tools/Cli -c $Configuration。" }
}
$allowedRoot = [IO.Path]::GetFullPath((Join-Path $repository 'UnityProject/Logs/T92'))
$runDirectory = [IO.Path]::GetFullPath((Join-Path $allowedRoot ('cli-advice-' + [Guid]::NewGuid().ToString('N'))))
if (-not $runDirectory.StartsWith($allowedRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw '验收产物必须位于当前工作树 UnityProject/Logs/T92 内。'
}
[void][IO.Directory]::CreateDirectory($runDirectory)
$utf8 = [Text.UTF8Encoding]::new($false, $true)
$checks = [Collections.Generic.List[string]]::new()
$comparisons = [Collections.Generic.List[object]]::new()
$passed = $false
$failure = $null
$adviceTitle = '===== 开局对位建议 ====='

function Assert-Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "$Message 证据目录：$runDirectory" }
}

function Invoke-Cli([string]$Name, [string[]]$Parameters, [string[]]$Lines = @(), [int]$ExpectedExit = 0) {
    Assert-Check ($Name -cmatch '^[A-Za-z0-9_-]+$') '用例名称包含路径字符。'
    $directory = [IO.Path]::GetFullPath((Join-Path $runDirectory $Name))
    Assert-Check ($directory.StartsWith($runDirectory + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) '用例目录越界。'
    [void][IO.Directory]::CreateDirectory($directory)
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = 'dotnet'
    $start.ArgumentList.Add($cliDll)
    foreach ($parameter in $Parameters) { $start.ArgumentList.Add($parameter) }
    $start.WorkingDirectory = $directory
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardInputEncoding = $utf8
    $start.StandardOutputEncoding = $utf8
    $start.StandardErrorEncoding = $utf8
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    $started = $false
    try {
        $started = $process.Start()
        Assert-Check $started "$Name 启动失败。"
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if ($Lines.Count -gt 0) { $process.StandardInput.Write(($Lines -join "`n") + "`n") }
        $process.StandardInput.Close()
        $timedOut = -not $process.WaitForExit(60000)
        if ($timedOut) { $process.Kill($true); [void]$process.WaitForExit(5000) }
        Assert-Check ($stdout.Wait(5000) -and $stderr.Wait(5000)) "$Name 输出读取未结束。"
        $result = [pscustomobject]@{ Out = $stdout.Result; Err = $stderr.Result; Exit = $process.ExitCode; Directory = $directory }
        [IO.File]::WriteAllText((Join-Path $directory 'stdout.log'), $result.Out, $utf8)
        [IO.File]::WriteAllText((Join-Path $directory 'stderr.log'), $result.Err, $utf8)
        Assert-Check (-not $timedOut) "$Name 超时。"
        Assert-Check ($result.Exit -eq $ExpectedExit) "$Name 应退出 $ExpectedExit，实际 $($result.Exit)。"
        Assert-Check (($result.Out + $result.Err) -notmatch '\[advice\.') "$Name 显示了缺失的本地化键。"
        if ($ExpectedExit -eq 0) { Assert-Check ([string]::IsNullOrWhiteSpace($result.Err)) "$Name 成功时出现标准错误。" }
        else {
            Assert-Check ($result.Err.Contains('错误：') -and $result.Err.Contains('--advice')) "$Name 缺少针对 --advice 的中文错误提示。"
            Assert-Check ($result.Out -notmatch '命令行对战|监听端口|开局对位建议|对战回看|伤害计算器|阵容对位表') "$Name 拒绝参数前进入了其他模式。"
            Assert-Check (@(Get-ChildItem -LiteralPath $directory -Force | Where-Object Name -NotIn @('stdout.log', 'stderr.log')).Count -eq 0) "$Name 拒绝参数前写了存档。"
        }
        $checks.Add($Name)
        return $result
    }
    finally {
        if ($started -and -not $process.HasExited) { $process.Kill($true); [void]$process.WaitForExit(5000) }
        $process.Dispose()
    }
}

function Assert-Advice($Result, [string]$FirstStage, $ContentDb, [string[]]$Mine, [string[]]$Theirs) {
    Assert-Check ([regex]::Matches($Result.Out, [regex]::Escape($adviceTitle)).Count -eq 1) '建议必须且只能展示一次。'
    $start = $Result.Out.IndexOf($adviceTitle, [StringComparison]::Ordinal)
    $end = $Result.Out.IndexOf($FirstStage, [StringComparison]::Ordinal)
    Assert-Check ($end -gt $start) '建议未在首次选择阶段前展示。'
    $text = $Result.Out.Substring($start, $end - $start)
    Assert-Check ($text.Contains('原始六人阵容') -and $text.Contains('不代表胜率') -and $text.Contains('禁用后不重新计算')) '建议未明确原始公开阵容及适用范围。'
    Assert-Check ([regex]::Matches($text, '(?m)^建议禁用：').Count -eq 1 -and
        [regex]::Matches($text, '(?m)^首发建议 [12]：').Count -eq 2 -and
        [regex]::Matches($text, '(?m)^主要威胁 [12]：').Count -eq 2 -and
        [regex]::Matches($text, '(?m)^应对 ').Count -eq 2) '建议的四类条目不完整。'
    foreach ($fighter in $ContentDb.Streamers) {
        Assert-Check ($text -cnotmatch ('(?<![A-Za-z0-9_])' + [regex]::Escape($fighter.Id) + '(?![A-Za-z0-9_])')) '建议区域泄露了主播内部 id。'
    }
    Assert-Check ($text -notmatch '\[[A-Za-z_][A-Za-z0-9_.]*\]|T_[A-Z_]+|STATUS|FIXED_DAMAGE|pre|post') '建议区域泄露了本地化键或效果代码。'
    $expected = [StreamDuel.Core.TeamAdvice]::For($ContentDb, $Mine, $Theirs)
    Assert-Check ($text.Contains($expected.Scope)) 'CLI 漏掉 Core 给出的预览限制。'
    foreach ($entry in @($expected.BanSuggestion) + @($expected.LeadSuggestions) + @($expected.Threats)) {
        $name = '原始 ' + ($entry.Index + 1) + ' 号 ' + $entry.Name
        Assert-Check ($text.Contains($name) -and $text.Contains($entry.Reason)) 'CLI 未原样使用 Core 的姓名、原始号位或理由。'
    }
    foreach ($answer in $expected.Answers) {
        Assert-Check ($text.Contains('原始 ' + ($answer.ThreatIndex + 1) + ' 号 ' + $answer.ThreatName) -and
            $text.Contains('原始 ' + ($answer.AnswerIndex + 1) + ' 号 ' + $answer.AnswerName) -and
            $text.Contains($answer.Reason)) 'CLI 的威胁与应对关系不符合 Core 结果。'
    }
    return $text
}

function Read-CompletedBattle($Result, $ContentDb) {
    $directory = Join-Path $Result.Directory 'saves/replays'
    $files = @(Get-ChildItem -LiteralPath $directory -File -Filter '*.json' -Force | Where-Object { -not $_.Name.StartsWith('.') })
    Assert-Check ($files.Count -eq 1) '完整对局未保存唯一的原始回放。'
    $record = [StreamDuel.Core.BattleRecord]::FromJson([IO.File]::ReadAllText($files[0].FullName, $utf8))
    $battle = [StreamDuel.Core.Replay]::Run($ContentDb, $record)
    Assert-Check ($battle.Phase -eq [StreamDuel.Core.Phase]::Over) '保存的回放不是完整对局。'
    return [pscustomobject]@{ Record = $record; Battle = $battle }
}


function Assert-CompletedStores($Result, $Completed, $ContentDb) {
    $profilePath = Join-Path $Result.Directory 'saves/profile.json'
    Assert-Check (Test-Path -LiteralPath $profilePath -PathType Leaf) '完整局未写入档案。'
    $profileText = [IO.File]::ReadAllText($profilePath, $utf8)
    $profile = [StreamDuel.Core.Profile]::FromJson($profileText)
    Assert-Check ($profile.Packs.Count -eq 1 -and $profile.Packs.ContainsKey($ContentDb.PackId)) '全新目录中的档案包数或内容包不匹配。'
    $stats = $profile.Packs[$ContentDb.PackId]
    Assert-Check (($stats.Wins + $stats.Losses + $stats.Draws) -eq 1) '单场完整对局未恰好累计一次档案。'
    Assert-Check ($stats.Wins -eq [int]($Completed.Battle.Winner -eq 0) -and
        $stats.Losses -eq [int]($Completed.Battle.Winner -eq 1) -and
        $stats.Draws -eq [int]($Completed.Battle.Winner -eq -1)) '档案胜负不是 A 方/本方视角。'
    $expectedProfile = [StreamDuel.Core.Profile]::new()
    $expectedProfile.Record($Completed.Battle, 0)
    Assert-Check ($profile.ToJson() -ceq $expectedProfile.ToJson()) '档案选用/胜场没有按禁用后的最终队伍累计一次。'

    # 直接读取 CLI 原生写出的文件；禁止先调用 ReplayLibrary.List 或 --replays 触发索引重建。
    $libraryDirectory = Join-Path $Result.Directory 'saves/replay-library'
    Assert-Check (Test-Path -LiteralPath $libraryDirectory -PathType Container) '完整局没有保存独立录像库。'
    $indexPath = Join-Path $libraryDirectory 'index.json'
    Assert-Check (Test-Path -LiteralPath $indexPath -PathType Leaf) 'CLI 原生录像库索引缺失，不能先重建再验收。'
    $indexText = [IO.File]::ReadAllText($indexPath, $utf8)
    $index = [StreamDuel.Core.MiniJson]::Parse($indexText)
    Assert-Check ($index -is [Collections.Generic.Dictionary[string,object]] -and
        $index.ContainsKey('version') -and $index['version'] -eq 1 -and
        $index.ContainsKey('entries') -and $index['entries'] -is [Collections.Generic.List[object]] -and
        $index['entries'].Count -eq 1) 'CLI 原生索引不是版本 1 的单条目索引。'
    $files = @(Get-ChildItem -LiteralPath $libraryDirectory -File -Filter '*.json' -Force |
        Where-Object { $_.Name -cne 'index.json' -and -not $_.Name.StartsWith('.') })
    Assert-Check ($files.Count -eq 1) '单场完整对局没有恰好保存一份原生录像库文件。'
    $libraryText = [IO.File]::ReadAllText($files[0].FullName, $utf8)
    $root = [StreamDuel.Core.MiniJson]::Parse($libraryText)
    Assert-Check ($root -is [Collections.Generic.Dictionary[string,object]] -and
        $root.ContainsKey('library') -and $root['library'] -is [Collections.Generic.Dictionary[string,object]]) '录像缺少原生 library 元数据。'
    $metadata = $root['library']
    $entry = $index['entries'][0]
    Assert-Check ($entry -is [Collections.Generic.Dictionary[string,object]]) '索引条目不是 JSON 对象。'
    foreach ($key in @('version', 'id', 'date', 'winner', 'turns', 'note')) {
        Assert-Check ($metadata.ContainsKey($key) -and $entry.ContainsKey($key)) "录像或索引缺少元数据字段 $key。"
        Assert-Check ($metadata[$key] -ceq $entry[$key]) "原生录像与索引的 $key 不一致。"
    }
    $record = [StreamDuel.Core.BattleRecord]::FromJson($libraryText)
    Assert-Check ($record.ToJson() -ceq $Completed.Record.ToJson()) '原生录像库的 BattleRecord 与旧版回放不一致。'
    $turns = @($record.Events | Where-Object { $_.Type -eq [StreamDuel.Core.BattleRecordEventType]::Turn }).Count
    Assert-Check ($metadata['version'] -eq 1 -and $metadata['id'] -ceq $files[0].BaseName -and
        $metadata['winner'] -eq $Completed.Battle.Winner -and $metadata['turns'] -eq $turns -and
        $null -eq $metadata['note']) '录像元数据的版本/编号/胜方/回合数/备注不符合真实完成局。'
    $metadataDate = [DateTimeOffset]::MinValue
    Assert-Check ($metadata['date'] -is [string] -and [DateTimeOffset]::TryParseExact(
        $metadata['date'], 'O', [Globalization.CultureInfo]::InvariantCulture,
        [Globalization.DateTimeStyles]::None, [ref]$metadataDate)) '录像日期不是合法的往返格式时间。'
    Assert-Check ($metadataDate.Offset -eq [TimeSpan]::Zero) '原生录像日期没有按 UTC 保存。'
    $expectedId = $metadataDate.ToUniversalTime().ToString('yyyyMMdd_HHmmss_fffffff', [Globalization.CultureInfo]::InvariantCulture) +
        '_' + $record.Seed.ToString([Globalization.CultureInfo]::InvariantCulture)
    Assert-Check ($metadata['id'] -ceq $expectedId) '全新录像库首条文件名与原生日期/种子不一致。'
    foreach ($key in @('packId', 'names', 'teams', 'isCorrupt', 'error')) {
        Assert-Check ($entry.ContainsKey($key)) "原生索引缺少 $key。"
    }
    Assert-Check ($entry['packId'] -ceq $record.PackId -and $entry['isCorrupt'] -is [bool] -and
        $entry['isCorrupt'] -eq $false -and $null -eq $entry['error']) '原生索引的内容包或损坏标记错误。'
    Assert-Check ($entry['names'] -is [Collections.Generic.List[object]] -and $entry['names'].Count -eq 2 -and
        $entry['teams'] -is [Collections.Generic.List[object]] -and $entry['teams'].Count -eq 2) '原生索引没有双方姓名和原始队伍。'
    for ($side = 0; $side -lt 2; $side++) {
        Assert-Check ($entry['names'][$side] -ceq $record.Names[$side]) '原生索引的玩家姓名与记录不一致。'
        $indexedTeam = $entry['teams'][$side]
        Assert-Check ($indexedTeam -is [Collections.Generic.List[object]] -and
            $indexedTeam.Count -eq $record.Teams[$side].Count) '原生索引队伍规模与原始记录不同。'
        for ($slot = 0; $slot -lt $record.Teams[$side].Count; $slot++) {
            Assert-Check ($indexedTeam[$slot] -ceq $record.Teams[$side][$slot]) '原生索引队伍号位与原始记录不同。'
        }
    }
    Assert-Check ([IO.File]::ReadAllText($indexPath, $utf8) -ceq $indexText -and
        [IO.File]::ReadAllText($files[0].FullName, $utf8) -ceq $libraryText -and
        [IO.File]::ReadAllText($profilePath, $utf8) -ceq $profileText) '只读核验意外改写了档案、录像或索引。'
    $checks.Add(([IO.Path]::GetFileName($Result.Directory)) + '-native-stores')
}
try {
    $assembly = [Reflection.Assembly]::LoadFrom($coreDll)
    Assert-Check ([StringComparer]::OrdinalIgnoreCase.Equals([IO.Path]::GetFullPath($assembly.Location),
        [IO.Path]::GetFullPath($coreDll))) '请在未加载其他 Core 的新 PowerShell 进程运行。'
    Assert-Check ($null -ne $assembly.GetType('StreamDuel.Core.TeamAdvice', $false)) '现有二进制尚未包含 T92 TeamAdvice。'
    $content = Join-Path $binaryDirectory 'Content'
    $rules = [IO.File]::ReadAllText((Join-Path $content 'StreamDuelRules.json'), $utf8)
    $pack = [IO.File]::ReadAllText((Join-Path $content 'Packs/original.json'), $utf8)
    $db = [StreamDuel.Core.ContentDb]::FromJson($rules, $pack)
    [string[]]$mine = @('bell', 'enka', 'shiori', 'lumi', 'hane', 'momo')
    [string[]]$theirs = @($db.Streamers | Where-Object { $_.Id -notin $mine } | Select-Object -First 6 | ForEach-Object Id)
    $common = @('--pack', 'original', '--seed', '17', '--team', ($mine -join ','), '--level', 'normal', '--auto', '--hash', '--save-dir', 'saves')
    foreach ($mode in @(
        @{ Name = 'ban'; Flags = @('--ban'); Stage = '===== 开局禁用 =====' },
        @{ Name = 'lead'; Flags = @('--lead'); Stage = '===== 选择首发 =====' },
        @{ Name = 'ban-lead'; Flags = @('--ban', '--lead'); Stage = '===== 开局禁用 =====' }
    )) {
        $baseline = Invoke-Cli ($mode.Name + '-baseline') ($common + $mode.Flags)
        $withAdvice = Invoke-Cli ($mode.Name + '-advice') ($common + $mode.Flags + @('--advice'))
        Assert-Check (-not $baseline.Out.Contains($adviceTitle)) '未启用 --advice 时出现建议区域。'
        $adviceText = Assert-Advice $withAdvice $mode.Stage $db $mine $theirs
        $left = Read-CompletedBattle $baseline $db
        $right = Read-CompletedBattle $withAdvice $db
        Assert-CompletedStores $baseline $left $db
        Assert-CompletedStores $withAdvice $right $db
        Assert-Check ($left.Record.ToJson() -ceq $right.Record.ToJson()) '开启建议改变了原始选择、结算随机检查点或对局记录。'
        Assert-Check ($left.Battle.StateHash() -eq $right.Battle.StateHash()) '开启建议改变了最终状态哈希。'
        $leftLog = @($left.Battle.Log | ForEach-Object { "$($_.Turn)|$($_.Node)|$($_.Side)|$($_.Kind)|$($_.Text)" }) -join "`n"
        $rightLog = @($right.Battle.Log | ForEach-Object { "$($_.Turn)|$($_.Node)|$($_.Side)|$($_.Kind)|$($_.Text)" }) -join "`n"
        Assert-Check ($leftLog -ceq $rightLog) '开启建议改变了战斗日志。'
        $hashesLeft = @([regex]::Matches($baseline.Out, '第 [0-9]+ 回合状态哈希：[0-9a-f]{16}') | ForEach-Object Value) -join "`n"
        $hashesRight = @([regex]::Matches($withAdvice.Out, '第 [0-9]+ 回合状态哈希：[0-9a-f]{16}') | ForEach-Object Value) -join "`n"
        Assert-Check ($hashesLeft.Length -gt 0 -and $hashesLeft -ceq $hashesRight) '开启建议改变了逐回合哈希输出。'
        # 从相同首个阶段到结尾应逐字一致；建议只占开局显示区域。
        Assert-Check ($baseline.Out.Substring($baseline.Out.IndexOf($mode.Stage, [StringComparison]::Ordinal)) -ceq
            $withAdvice.Out.Substring($withAdvice.Out.IndexOf($mode.Stage, [StringComparison]::Ordinal))) '建议显示后原 CLI 行为输出发生变化。'
        $comparisons.Add([ordered]@{ mode = $mode.Name; seed = 17; hash = $left.Battle.StateHash().ToString('x16'); turns = $left.Battle.Turn })
    }

    $manualArgs = @('--pack', 'original', '--team', ($mine -join ','), '--seed', '17')
    $manual = Invoke-Cli 'manual-ban-lead' ($manualArgs + @('--ban', '--lead', '--advice')) @('1', 'q')
    $null = Assert-Advice $manual '===== 开局禁用 =====' $db $mine $theirs
    Assert-Check ($manual.Out.Contains('===== 选择首发 =====') -and -not (Test-Path -LiteralPath (Join-Path $manual.Directory 'saves'))) '禁用后首发阶段退出未保持无存档行为。'
    $eof = Invoke-Cli 'manual-lead-eof' ($manualArgs + @('--lead', '--advice'))
    $null = Assert-Advice $eof '===== 选择首发 =====' $db $mine $theirs
    Assert-Check (-not (Test-Path -LiteralPath (Join-Path $eof.Directory 'saves'))) '首发选择前 EOF 仍写了存档。'

    $preset = [StreamDuel.Core.TeamPreset]::new()
    $preset.Name = '建议验收队伍'; $preset.PackId = $db.PackId
    foreach ($id in $mine) { $preset.Ids.Add($id) }
    $teamCode = [StreamDuel.Core.TeamCode]::Encode($preset)
    $compatible = Invoke-Cli 'compatible-local-options' @('--advice', '--lead', '--team-code', $teamCode, '--export-team',
        '--level', 'hard', '--seed', '0', '--timer', '10,30', '--hash', '--share', '--report', 'report.md', '--save-dir', 'saves') @('q')
    $null = Assert-Advice $compatible '===== 选择首发 =====' $db $mine $theirs
    Assert-Check ($compatible.Out.Contains('本方队伍码：') -and -not (Test-Path -LiteralPath (Join-Path $compatible.Directory 'report.md')) -and
        -not (Test-Path -LiteralPath (Join-Path $compatible.Directory 'saves'))) '可组合选项被错误忽略，或提前退出时写了档案/战报。'

    $customPack = $pack | ConvertFrom-Json -AsHashtable
    $customPack.id = 't92_advice_fixture'; $customPack.name = '对位建议验收包'
    for ($i = 0; $i -lt $customPack.streamers.Count; $i++) { $customPack.streamers[$i].name = '验收主播' + ($i + 1) }
    $packDirectory = Join-Path $runDirectory 'fixture-packs'
    [void][IO.Directory]::CreateDirectory($packDirectory)
    $customJson = $customPack | ConvertTo-Json -Depth 64
    [IO.File]::WriteAllText((Join-Path $packDirectory 'fixture.json'), $customJson, $utf8)
    $customDb = [StreamDuel.Core.ContentDb]::FromJson($rules, $customJson)
    $custom = Invoke-Cli 'external-pack-display-names' @('--pack', $customDb.PackId, '--pack-dir', $packDirectory,
        '--team', ($mine -join ','), '--advice', '--ban') @('q')
    $null = Assert-Advice $custom '===== 开局禁用 =====' $customDb $mine $theirs

    foreach ($case in @(
        @{ Name = 'no-phase'; Args = @() }, @{ Name = 'no-phase-auto'; Args = @('--auto') },
        @{ Name = 'replay'; Args = @('--ban', '--replay', 'missing.json') },
        @{ Name = 'replay-code'; Args = @('--lead', '--replay-code', 'SD1-invalid') },
        @{ Name = 'replays'; Args = @('--ban', '--replays') },
        @{ Name = 'list-packs'; Args = @('--ban', '--list-packs') },
        @{ Name = 'glossary'; Args = @('--lead', '--glossary') },
        @{ Name = 'calc'; Args = @('--ban', '--calc', 'bell', '1', 'enka') },
        @{ Name = 'matchup'; Args = @('--lead', '--matchup', ($mine -join ','), 'vs', ($theirs -join ',')) },
        @{ Name = 'host'; Args = @('--ban', '--host', '0') },
        @{ Name = 'join'; Args = @('--lead', '--join', '127.0.0.1:7777') },
        @{ Name = 'lan-host'; Args = @('--ban', '--lan-host', '测试房间') },
        @{ Name = 'lan-list'; Args = @('--ban', '--lan-list') },
        @{ Name = 'duplicate'; Args = @('--ban', '--advice') }
    )) {
        $null = Invoke-Cli ('reject-' + $case.Name) (@('--advice') + $case.Args) @() 1
    }
    # T86/T89 合并后必须仍然在解析阶段互斥；仅因旧二进制不认识新参数而失败不算通过。
    $sentinelPath = Join-Path $runDirectory 'existing-tournament.json'
    $sentinelText = "{`"reviewSentinel`":`"原赛事字节必须保留`",`"round`":7}`n"
    [IO.File]::WriteAllText($sentinelPath, $sentinelText, $utf8)
    $sentinelBytes = [Convert]::ToBase64String([IO.File]::ReadAllBytes($sentinelPath))
    foreach ($adviceFirst in @($true, $false)) {
        $order = if ($adviceFirst) { 'advice-first' } else { 'mode-first' }
        $hotseatArgs = if ($adviceFirst) { @('--advice', '--ban', '--hotseat', '--save-dir', 'saves') }
            else { @('--hotseat', '--ban', '--advice', '--save-dir', 'saves') }
        $hotseat = Invoke-Cli ('reject-hotseat-' + $order) $hotseatArgs @() 1
        Assert-Check ($hotseat.Err -match '[\u4e00-\u9fff]' -and $hotseat.Err -notmatch '未知参数|unknown\s+(option|argument)' -and
            $hotseat.Err -match '不能|不支持|同用') '同屏互斥必须是中文模式冲突，不能依赖未识别新参数。'
        Assert-Check ($hotseat.Out -notmatch '同屏双人对战|请把电脑交给|私密输入' -and
            -not (Test-Path -LiteralPath (Join-Path $hotseat.Directory 'saves'))) '同屏互斥前已经进入会话或新建存档。'
        foreach ($existing in @($true, $false)) {
            $state = if ($existing) { 'existing' } else { 'fresh' }
            $tournamentPath = if ($existing) { $sentinelPath } else { Join-Path $runDirectory ('fresh-tournament-' + $order + '.json') }
            if (-not $existing) { Assert-Check (-not (Test-Path -LiteralPath $tournamentPath)) '全新赛事夹具已存在。' }
            $tournamentArgs = if ($adviceFirst) { @('--advice', '--tournament', $tournamentPath) }
                else { @('--tournament', $tournamentPath, '--advice') }
            $tournament = Invoke-Cli ('reject-tournament-' + $state + '-' + $order) $tournamentArgs @() 1
            Assert-Check ($tournament.Err -match '[\u4e00-\u9fff]' -and $tournament.Err -notmatch '未知参数|unknown\s+(option|argument)' -and
                $tournament.Err -match '不能|不支持|同用') '赛事互斥必须是中文模式冲突，不能依赖未识别新参数。'
            Assert-Check ($tournament.Err.Contains('--tournament')) '赛事互斥必须识别赛事入口，不能仅因缺少禁用或首发参数而拒绝。'
            Assert-Check ($tournament.Out.Length -eq 0 -and
                -not (Test-Path -LiteralPath (Join-Path $tournament.Directory 'saves'))) '赛事互斥前已经进入模式或新建存档。'
            Assert-Check ([IO.File]::ReadAllText($sentinelPath, $utf8) -ceq $sentinelText -and
                [Convert]::ToBase64String([IO.File]::ReadAllBytes($sentinelPath)) -ceq $sentinelBytes) '模式互斥改写了已有赛事哨兵。'
            if (-not $existing) { Assert-Check (-not (Test-Path -LiteralPath $tournamentPath)) '模式互斥创建了原本不存在的赛事文件。' }
            Assert-Check (@(Get-ChildItem -LiteralPath $runDirectory -File -Force |
                Where-Object { $_.Name -like '*.tournament.lock' -or $_.Name -like '*.tournament.tmp*' }).Count -eq 0) '模式互斥创建了赛事锁或临时文件。'
        }
    }
    $help = Invoke-Cli 'advice-help' @('--advice', '--help')
    Assert-Check ($help.Out.Contains('--advice') -and -not $help.Out.Contains($adviceTitle)) '帮助模式错误进入对战或漏掉建议参数。'
    Assert-Check (@(Get-ChildItem -LiteralPath $help.Directory -Force | Where-Object Name -NotIn @('stdout.log', 'stderr.log')).Count -eq 0) '帮助模式写了文件。'
    $passed = $true
}
catch { $failure = $_.Exception.Message; throw }
finally {
    [IO.File]::WriteAllText((Join-Path $runDirectory 'result.json'), ([ordered]@{
        passed = $passed; checks = @($checks.ToArray()); comparisons = @($comparisons.ToArray()); failure = $failure; directory = $runDirectory
    } | ConvertTo-Json -Depth 8), $utf8)
    Write-Host "对位建议 CLI 验收产物：$runDirectory"
}
