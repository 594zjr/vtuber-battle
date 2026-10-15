#requires -Version 7.0
param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug')

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$prefix = $repository.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$current = [IO.Path]::GetFullPath((Get-Location).Path)
if ($current -ne $repository -and -not $current.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw '请在本仓库内运行此脚本。'
}
$output = Join-Path $PSScriptRoot "bin/$Configuration/net8.0"
$cliDll = Join-Path $output 'Cli.dll'
if (-not (Test-Path -LiteralPath $cliDll -PathType Leaf)) { throw "请先构建 Tools/Cli -c $Configuration。" }
$runDirectory = Join-Path $PSScriptRoot ('obj/replay-compat/' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($runDirectory)
$utf8 = [Text.UTF8Encoding]::new($false)
$checks = [Collections.Generic.List[string]]::new()
$invocations = [Collections.Generic.List[object]]::new()

function Assert-Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "$Message 证据：$runDirectory" }
}

function Write-Fixture([string]$Name, [string]$Json) {
    $path = Join-Path $runDirectory "$Name.json"
    [IO.File]::WriteAllText($path, $Json, $utf8)
    return $path
}

function Invoke-Cli([string]$Name, [string[]]$Parameters, [int]$ExpectedExit = 0) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = 'dotnet'
    $start.ArgumentList.Add($cliDll)
    foreach ($parameter in $Parameters) { $start.ArgumentList.Add($parameter) }
    $start.WorkingDirectory = $runDirectory
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = $utf8
    $start.StandardErrorEncoding = $utf8
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    try {
        [void]$process.Start()
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $process.StandardInput.Close()
        if (-not $process.WaitForExit(30000)) {
            $process.Kill($true)
            throw "$Name 超过 30 秒未退出。"
        }
        $result = [pscustomobject]@{ Out = $stdout.Result; Err = $stderr.Result; Exit = $process.ExitCode }
        [IO.File]::WriteAllText((Join-Path $runDirectory "$Name.stdout.log"), $result.Out, $utf8)
        [IO.File]::WriteAllText((Join-Path $runDirectory "$Name.stderr.log"), $result.Err, $utf8)
        $invocations.Add([ordered]@{ name = $Name; parameters = $Parameters; exit = $result.Exit })
        Assert-Check ($result.Exit -eq $ExpectedExit) "$Name 应退出 $ExpectedExit，实际 $($result.Exit)。"
        return $result
    }
    finally { $process.Dispose() }
}

try {
    Add-Type -Path (Join-Path $output 'StreamDuel.Core.dll')
    $content = Join-Path $output 'Content'
    $db = [StreamDuel.Core.ContentDb]::FromJson(
        [IO.File]::ReadAllText((Join-Path $content 'StreamDuelRules.json')),
        [IO.File]::ReadAllText((Join-Path $content 'Packs/original.json')))
    $teams = [Collections.Generic.IList[string][]]@(
        [Collections.Generic.List[string]]@('bell', 'enka', 'shiori', 'lumi', 'hane', 'momo'),
        [Collections.Generic.List[string]]@('kei', 'yuan', 'nyako', 'buck', 'kasumi', 'bee'))
    $battle = [StreamDuel.Core.Battle]::new($db, $teams, [string[]]@('你', '对手'), [uint32]93)
    for ($step = 0; $step -lt 1000 -and $battle.Phase -ne [StreamDuel.Core.Phase]::Over; $step++) {
        if ($battle.Phase -eq [StreamDuel.Core.Phase]::Choose) {
            $battle.ResolveTurn([StreamDuel.Core.Ai]::Choose($battle, 0), [StreamDuel.Core.Ai]::Choose($battle, 1))
        }
        else {
            for ($side = 0; $side -lt 2; $side++) {
                if ($battle.Need[$side]) { $battle.Replace($side, [StreamDuel.Core.Ai]::ChooseReplacement($battle, $side)) }
            }
        }
    }
    Assert-Check ($battle.Phase -eq [StreamDuel.Core.Phase]::Over) '夹具未完成对局。'
    $originalJson = $battle.Record.ToJson()
    $validPath = Write-Fixture 'current' $originalJson
    $valid = Invoke-Cli 'current' @('--replay', $validPath, '--auto')
    $validBest = Invoke-Cli 'current-best' @('--replay', $validPath, '--auto', '--replay-best-effort')
    Assert-Check ($valid.Out.Contains('回看结束：') -and $valid.Err.Length -eq 0) '当前录像回看失败。'
    Assert-Check ($validBest.Out -ceq $valid.Out -and $validBest.Err.Length -eq 0) '匹配内容的显式尽力回放改变了输出或错误地产生警告。'
    $checks.Add('当前 v2 完整录像严格/尽力回看输出一致，无伪警告')

    $legacy = $originalJson | ConvertFrom-Json -AsHashtable
    $legacy.Remove('version')
    $legacy.Remove('contentHash')
    $legacy.Remove('names')
    $legacyPath = Write-Fixture 'legacy' ($legacy | ConvertTo-Json -Depth 20 -Compress)
    $old = Invoke-Cli 'legacy' @('--replay', $legacyPath, '--auto')
    Assert-Check ($old.Out -ceq $valid.Out -and $old.Err.Length -eq 0) '无版本/指纹/名称的旧录像默认值或日志不兼容。'
    $checks.Add('无版本 v1 旧录像保持完整回看日志与缺省名称')

    $changed = [StreamDuel.Core.BattleRecord]::FromJson($originalJson)
    $changed.ContentHash = '0000000000000000'
    $changedPath = Write-Fixture 'different-content' $changed.ToJson()
    $inputHash = (Get-FileHash -LiteralPath $changedPath -Algorithm SHA256).Hash
    $strict = Invoke-Cli 'mismatch-strict' @('--replay', $changedPath, '--auto') 1
    Assert-Check ($strict.Err.Contains('内容不一致') -and -not $strict.Out.Contains('回看结束')) '默认回看应拒绝内容指纹差异。'
    $report = Join-Path $runDirectory 'approximate-report.md'
    $approximate = Invoke-Cli 'mismatch-best' @('--replay', $changedPath, '--auto', '--replay-best-effort', '--report', $report)
    Assert-Check ($approximate.Out.Contains('结果可能不同') -and $approximate.Out.Contains('回看结束：') -and $approximate.Err.Length -eq 0) '显式尽力回放没有警告或未完成。'
    Assert-Check ([IO.File]::ReadAllText($report).StartsWith('> 尽力重放：')) '导出的近似战报缺少警告。'
    $code = [StreamDuel.Core.ShareCode]::Encode($changed)
    $shared = Invoke-Cli 'mismatch-share' @('--replay-code', $code, '--auto', '--replay-best-effort', '--share')
    $exported = [regex]::Match($shared.Out, '(?m)^分享码：(SD1-[A-Za-z0-9_-]+)\r?$')
    Assert-Check ($shared.Out.Contains('结果可能不同') -and $exported.Success) '分享码回看未警告或没有重新导出。'
    # PowerShell 与 CLI 的运行时压缩实现可不同，解码后的完整记录才是保留性依据。
    Assert-Check ([StreamDuel.Core.ShareCode]::Decode($exported.Groups[1].Value).ToJson() -ceq $changed.ToJson()) '重新分享改写了原录像。'
    Assert-Check ((Get-FileHash -LiteralPath $changedPath -Algorithm SHA256).Hash -ceq $inputHash) '尽力回放改写了来源录像。'
    $checks.Add('指纹不符默认拒绝；显式文件/分享码回看警告，战报标注，来源和再分享记录不变')

    $futureRecord = [StreamDuel.Core.BattleRecord]::FromJson($originalJson)
    $futureRecord.Version = [StreamDuel.Core.BattleRecord]::CurrentVersion + 1
    $futurePath = Write-Fixture 'future' $futureRecord.ToJson()
    Assert-Check ([StreamDuel.Core.BattleRecord]::FromJson($futureRecord.ToJson()).Version -eq $futureRecord.Version) '读取器未保留任意正版本号的历史行为。'
    foreach ($extra in @(@(), @('--replay-best-effort'))) {
        $future = Invoke-Cli ('future-' + $extra.Count) (@('--replay', $futurePath, '--auto') + $extra) 1
        Assert-Check ($future.Err.Contains('录像来自更新的版本') -and -not $future.Err.Contains(' at ')) '未来版本回看未给中文提示或泄漏堆栈。'
    }
    $futureCode = [StreamDuel.Core.ShareCode]::Encode($futureRecord)
    foreach ($extra in @(@(), @('--replay-best-effort'))) {
        $futureShare = Invoke-Cli ('future-share-' + $extra.Count) (@('--replay-code', $futureCode, '--auto') + $extra) 1
        Assert-Check ($futureShare.Err.Contains('录像来自更新的版本')) '未来分享码没有版本提示。'
    }
    $malformedFuturePath = Write-Fixture 'malformed-future' ('{"version":' + $futureRecord.Version + ',"teams":false}')
    $malformedFuture = Invoke-Cli 'malformed-future' @('--replay', $malformedFuturePath, '--auto') 1
    Assert-Check ($malformedFuture.Err.Contains('teams 必须是数组')) '未来版本的坏结构未继续校验。'
    $checks.Add('未来版本可解析，文件与分享码的严格/尽力回看均由兼容检查拒绝；坏结构仍报错')

    foreach ($kind in @('pack', 'streamer', 'modifier')) {
        $bad = [StreamDuel.Core.BattleRecord]::FromJson($originalJson)
        if ($kind -eq 'pack') { $bad.PackId = 'missing_pack' }
        elseif ($kind -eq 'streamer') { $bad.Teams[0][0] = 'missing_streamer' }
        else { $bad.Modifiers = [Collections.Generic.List[string]]@('unknown_modifier') }
        $badPath = Write-Fixture $kind $bad.ToJson()
        $failed = Invoke-Cli $kind @('--replay', $badPath, '--auto', '--replay-best-effort') 1
        Assert-Check ($failed.Err.Contains('错误：') -and -not $failed.Out.Contains('回看结束')) '尽力回放放行了缺包、成员或修饰器错误。'
    }
    $checks.Add('尽力回放拒绝缺包、未知主播及未知修饰器')

    foreach ($parameters in @(
        @('--replay-best-effort'), @('--replays', '--replay-best-effort'),
        @('--replay', $validPath, '--list-packs', '--replay-best-effort'),
        @('--replay', $validPath, '--replay-best-effort', '--replay-best-effort'),
        @('--host', '0', '--replay-best-effort'))) {
        $name = 'options-' + $invocations.Count
        $invalid = Invoke-Cli $name $parameters 1
        Assert-Check ($invalid.Err.Contains('--replay-best-effort') -and -not $invalid.Err.Contains('未知参数')) '尽力选项上下文或重复校验不明确。'
    }
    Assert-Check (-not (Test-Path -LiteralPath (Join-Path $runDirectory 'saves'))) '回看或错误输入产生了档案/录像目录。'
    $checks.Add('无回看、列表、重复及联网误用先报错；全部回看均不写战绩或新录像')

    # 每例只给新模式与尽力回放两个选项，避免别的非法组合提前报错造成假通过。
    $sentinelPath = Join-Path $runDirectory 'existing-tournament-review.json'
    $sentinelText = '{"reviewSentinel":"T93 既有赛事不得改写","round":7}'
    [IO.File]::WriteAllText($sentinelPath, $sentinelText, $utf8)
    $sentinelBytes = [Convert]::ToBase64String([IO.File]::ReadAllBytes($sentinelPath))
    foreach ($order in @('mode-first', 'best-first')) {
        $freshDirectory = Join-Path $runDirectory ('fresh-tournament-' + $order)
        $freshTournament = Join-Path $freshDirectory 'event.json'
        Assert-Check (-not (Test-Path -LiteralPath $freshDirectory)) '新赛事的父目录必须尚不存在。'
        $scenarios = @(
            [pscustomobject]@{ Name = 'hotseat'; Arguments = [string[]]@('--hotseat'); Marker = '--hotseat' },
            [pscustomobject]@{ Name = 'advice'; Arguments = [string[]]@('--advice'); Marker = '--advice' },
            [pscustomobject]@{ Name = 'tournament-existing'; Arguments = [string[]]@('--tournament', $sentinelPath); Marker = '赛事模式' },
            [pscustomobject]@{ Name = 'tournament-fresh'; Arguments = [string[]]@('--tournament', $freshTournament); Marker = '赛事模式' }
        )
        foreach ($scenario in $scenarios) {
            $parameters = if ($order -eq 'mode-first') {
                @($scenario.Arguments) + @('--replay-best-effort')
            }
            else {
                @('--replay-best-effort') + @($scenario.Arguments)
            }
            Assert-Check (@($parameters | Where-Object { $_.StartsWith('--', [StringComparison]::Ordinal) }).Count -eq 2) '互斥夹具应只有模式及 --replay-best-effort 两个选项。'
            $directoriesBefore = @([IO.Directory]::GetDirectories($runDirectory) | Sort-Object)
            $filesBefore = @([IO.Directory]::GetFiles($runDirectory) | Sort-Object)
            $name = 'mode-conflict-' + $scenario.Name + '-' + $order
            $invalid = Invoke-Cli $name $parameters 1
            Assert-Check ($invalid.Out.Length -eq 0) "$name 在参数拒绝前进入了显示或交互流程。"
            Assert-Check ($invalid.Err.Contains('错误：') -and $invalid.Err.Contains('--replay-best-effort') -and $invalid.Err.Contains($scenario.Marker)) "$name 没有识别两种选项并说明兼容性冲突。"
            Assert-Check ($invalid.Err -match '[\u4e00-\u9fff]' -and $invalid.Err -match '不能|不支持') "$name 没有中文互斥原因。"
            Assert-Check ($invalid.Err -notmatch '未知参数|unknown\s+(option|argument)') "$name 被未知参数错误假冒成兼容性拒绝。"
            Assert-Check (-not (Test-Path -LiteralPath (Join-Path $runDirectory 'saves'))) "$name 产生了存档目录。"
            Assert-Check (-not (Test-Path -LiteralPath $freshTournament) -and -not (Test-Path -LiteralPath $freshDirectory)) "$name 提前创建了新赛事或其父目录。"
            Assert-Check ([IO.File]::ReadAllText($sentinelPath) -ceq $sentinelText) "$name 改写了既有赛事文字。"
            Assert-Check ([Convert]::ToBase64String([IO.File]::ReadAllBytes($sentinelPath)) -ceq $sentinelBytes) "$name 改写了既有赛事字节。"
            $directoriesAfter = @([IO.Directory]::GetDirectories($runDirectory) | Sort-Object)
            Assert-Check ([string]::Join("`n", $directoriesAfter) -ceq [string]::Join("`n", $directoriesBefore)) "$name 创建或删除了目录。"
            $filesAfter = @([IO.Directory]::GetFiles($runDirectory) | Sort-Object)
            $expectedFiles = @($filesBefore) + @((Join-Path $runDirectory "$name.stdout.log"), (Join-Path $runDirectory "$name.stderr.log"))
            Assert-Check ([string]::Join("`n", $filesAfter) -ceq [string]::Join("`n", @($expectedFiles | Sort-Object))) "$name 产生或删除了日志以外的文件（含赛事锁、临时文件或存档）。"
        }
    }
    $checks.Add('hotseat/advice/tournament 双顺序均提前互斥；8 例仅含两个选项、中文非未知参数、stdout 为空、无新目录/存档/赛事，既有赛事字节不变')

    $librarySave = Join-Path $runDirectory 'library-save'
    $libraryDirectory = Join-Path $librarySave 'replay-library'
    $library = [StreamDuel.Core.ReplayLibrary]::new($libraryDirectory, 200, $null)
    $replayId = $library.Save($changed, [StreamDuel.Core.BattleSummary]::From($battle), $null)
    $libraryReplay = Invoke-Cli 'library-best' @('--replay', $replayId, '--auto', '--replay-best-effort', '--save-dir', $librarySave)
    Assert-Check ($libraryReplay.Out.Contains('结果可能不同') -and $libraryReplay.Out.Contains('回看结束：')) '录像编号回看未使用兼容入口。'
    $futureLibraryPath = Join-Path $libraryDirectory 'future.json'
    $futureLibraryJson = [IO.File]::ReadAllText((Join-Path $libraryDirectory ($replayId + '.json')))
    $futureLibraryJson = $futureLibraryJson.Replace('"version":' + $changed.Version, '"version":' + $futureRecord.Version).Replace('"id":"' + $replayId + '"', '"id":"future"')
    [IO.File]::WriteAllText($futureLibraryPath, $futureLibraryJson, $utf8)
    $futureHash = (Get-FileHash -LiteralPath $futureLibraryPath -Algorithm SHA256).Hash
    $listed = Invoke-Cli 'library-list-future' @('--replays', '--save-dir', $librarySave)
    Assert-Check ($listed.Out.Contains('录像来自更新的版本') -and $listed.Out.Contains($replayId)) '未来录像妨碍了正常列表或缺少明确提示。'
    $failedLoad = Invoke-Cli 'library-future' @('--replay', 'future', '--auto', '--replay-best-effort', '--save-dir', $librarySave) 1
    Assert-Check ($failedLoad.Err.Contains('录像来自更新的版本')) '录像库未来版本未被拒绝。'
    $noteError = $null
    try { $library.SetNote('future', '不应写入') }
    catch { $noteError = $_.Exception.Message }
    Assert-Check ($null -ne $noteError -and $noteError.Contains('录像来自更新的版本')) '未来录像可被改备注重写。'
    Assert-Check ((Get-FileHash -LiteralPath $futureLibraryPath -Algorithm SHA256).Hash -ceq $futureHash) '未来录像被改写或清理。'
    Assert-Check (@(Get-ChildItem -LiteralPath $libraryDirectory -File -Filter '*.json' | Where-Object Name -ne 'index.json').Count -eq 2) '回看新增了录像。'
    Assert-Check (-not (Test-Path -LiteralPath (Join-Path $librarySave 'profile.json'))) '录像库回看累计了战绩。'
    $checks.Add('录像编号支持尽力回看；未来录像列表可提示、加载拒绝、原文件保留且正常条目仍可用')
    [IO.File]::WriteAllText((Join-Path $runDirectory 'report.json'),
        ([ordered]@{ status = 'PASS'; checks = $checks.ToArray(); invocations = $invocations.ToArray() } | ConvertTo-Json -Depth 8), $utf8)
    Write-Output "PASS：$($checks.Count) 组旧录像兼容 CLI 检查，$($invocations.Count) 个子进程；证据：$runDirectory"
}
catch {
    [IO.File]::WriteAllText((Join-Path $runDirectory 'report.json'),
        ([ordered]@{ status = 'FAIL'; error = $_.Exception.Message; checks = $checks.ToArray(); invocations = $invocations.ToArray() } | ConvertTo-Json -Depth 8), $utf8)
    throw
}
