#requires -Version 7.0
param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug')

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
