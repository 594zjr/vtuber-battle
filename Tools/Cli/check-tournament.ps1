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
$allowedRoot = [IO.Path]::GetFullPath((Join-Path $repository 'UnityProject/Logs/T89'))
$runDirectory = [IO.Path]::GetFullPath((Join-Path $allowedRoot ('cli-' + [Guid]::NewGuid().ToString('N'))))
if (-not $runDirectory.StartsWith($allowedRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw '验收产物必须位于当前工作树 UnityProject/Logs/T89 内。'
}
[void][IO.Directory]::CreateDirectory($runDirectory)
$utf8 = [Text.UTF8Encoding]::new($false, $true)
$checks = [Collections.Generic.List[string]]::new()
$passed = $false
$failure = $null

function Assert-Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "$Message 证据目录：$runDirectory" }
}

function Invoke-Cli([string]$Name, [string[]]$Parameters, [string[]]$Lines = @(), [int]$ExpectedExit = 0) {
    $directory = Join-Path $runDirectory $Name
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
        $result = [pscustomobject]@{ Out = $stdout.Result; Err = $stderr.Result; Exit = $process.ExitCode }
        [IO.File]::WriteAllText((Join-Path $directory 'stdout.log'), $result.Out, $utf8)
        [IO.File]::WriteAllText((Join-Path $directory 'stderr.log'), $result.Err, $utf8)
        Assert-Check (-not $timedOut) "$Name 超时。"
        Assert-Check ($result.Exit -eq $ExpectedExit) "$Name 应退出 $ExpectedExit，实际 $($result.Exit)。"
        Assert-Check (-not (Test-Path -LiteralPath (Join-Path $directory 'saves'))) "$Name 不应创建玩家战绩或回放。"
        if ($ExpectedExit -eq 0) { Assert-Check ([string]::IsNullOrWhiteSpace($result.Err)) "$Name 成功时出现标准错误。" }
        else { Assert-Check ($result.Err.Contains('错误：')) "$Name 缺少中文错误提示。" }
        $checks.Add($Name)
        return $result
    }
    finally {
        if ($started -and -not $process.HasExited) { $process.Kill($true); [void]$process.WaitForExit(5000) }
        $process.Dispose()
    }
}

function New-Code([string]$PackId, [string[]]$Ids) {
    $preset = [StreamDuel.Core.TeamPreset]::new()
    $preset.Name = '赛事验收队伍'
    $preset.PackId = $PackId
    foreach ($id in $Ids) { $preset.Ids.Add($id) }
    return [StreamDuel.Core.TeamCode]::Encode($preset)
}

function Read-Tournament([string]$Path) {
    return [StreamDuel.Core.Tournament]::FromJson($db, [IO.File]::ReadAllText($Path, $utf8))
}

try {
    $assembly = [Reflection.Assembly]::LoadFrom($coreDll)
    Assert-Check ([StringComparer]::OrdinalIgnoreCase.Equals([IO.Path]::GetFullPath($assembly.Location),
        [IO.Path]::GetFullPath($coreDll))) '请在未加载其他 Core 的新 PowerShell 进程运行。'
    Assert-Check ($null -ne $assembly.GetType('StreamDuel.Core.Tournament', $false)) '现有二进制尚未包含 T89 Tournament。'
    $content = Join-Path $binaryDirectory 'Content'
    $rules = [IO.File]::ReadAllText((Join-Path $content 'StreamDuelRules.json'), $utf8)
    $pack = [IO.File]::ReadAllText((Join-Path $content 'Packs/original.json'), $utf8)
    $db = [StreamDuel.Core.ContentDb]::FromJson($rules, $pack)
    [string[]]$teamA = @('bell', 'enka', 'shiori', 'lumi', 'hane', 'momo')
    [string[]]$teamB = @('kei', 'yuan', 'nyako', 'buck', 'kasumi', 'bee')
    $codeA = New-Code $db.PackId $teamA
    $codeB = New-Code $db.PackId $teamB

    $save = Join-Path $runDirectory 'resume.json'
    $first = Invoke-Cli '01-register-pause' @('--tournament', $save) @('1', '同名', $codeA, 'q')
    $registration = Read-Tournament $save
    Assert-Check ($registration.Round -eq 0 -and $registration.Players.Count -eq 1) '报名中途退出没有保留第一位选手。'
    $stale = $save + '.tournament.tmp'
    [IO.File]::WriteAllText($stale, '保留上次中断或其他程序的临时文件', $utf8)
    $resume = Invoke-Cli '02-register-resume-stale-temp' @('--tournament', $save) @('同名', $codeB, '', 'q')
    Assert-Check ([IO.File]::ReadAllText($stale, $utf8) -ceq '保留上次中断或其他程序的临时文件') '续赛覆盖或删除了原临时文件。'
    $pending = Read-Tournament $save
    Assert-Check ($pending.Round -eq 1 -and $pending.Players.Count -eq 2 -and -not $pending.IsFinished) '报名恢复或未报结果保存不正确。'
    Assert-Check ($resume.Out.Contains('0 = 报名 #1 同名 获胜') -and $resume.Out.Contains('1 = 报名 #2 同名 获胜')) '同名选手的结果选项不能区分。'
    $pendingJson = [IO.File]::ReadAllText($save, $utf8)
    $eof = Invoke-Cli '03-result-eof' @('--tournament', $save)
    Assert-Check ([IO.File]::ReadAllText($save, $utf8) -ceq $pendingJson) '结果输入 EOF 改变了未报对局。'
    $result = Invoke-Cli '04-manual-result' @('--tournament', $save) @('bad', '1')
    $finished = Read-Tournament $save
    Assert-Check ($finished.IsFinished -and $finished.Standings()[0].Player -eq 1 -and
        $finished.Standings()[0].Points -eq 3) '手动结果 1 未映射至第二位选手获胜。'
    Assert-Check ($result.Out.Contains('已保存结果：报名 #2 同名 获胜')) '结果显示缺少稳定报名编号。'

    $roundRobin = Join-Path $runDirectory 'roundrobin.json'
    $rrLines = @('1', '甲', $codeA, '乙', $codeB, '丙', $codeA, '丁', $codeB, '', '0', '1', '-1', '0', '1', '平局')
    $rr = Invoke-Cli '05-roundrobin-four' @('--tournament', $roundRobin) $rrLines
    $rrSaved = Read-Tournament $roundRobin
    Assert-Check ($rrSaved.IsFinished -and $rrSaved.Round -eq 3 -and
        [regex]::Matches($rr.Out, '已保存结果：').Count -eq 6) '四人循环赛未完成六场对局。'
    $reference = [StreamDuel.Core.Tournament]::new($db, 'roundrobin', 0)
    $reference.AddPlayer('甲', $codeA); $reference.AddPlayer('乙', $codeB)
    $reference.AddPlayer('丙', $codeA); $reference.AddPlayer('丁', $codeB)
    $answers = @(0, 1, -1, 0, 1, -1)
    $answerIndex = 0
    while (-not $reference.IsFinished) {
        foreach ($pairing in $reference.NextRound()) {
            $reference.ReportResult($pairing, $answers[$answerIndex]); $answerIndex++
        }
    }
    Assert-Check ($reference.ToJson() -ceq $rrSaved.ToJson()) 'CLI 循环赛保存的对阵/比分与公开 Core 接口不同。'

    $swissPath = Join-Path $runDirectory 'swiss.json'
    $swissLines = [Collections.Generic.List[string]]::new()
    $swissLines.Add('2'); $swissLines.Add('')
    for ($i = 0; $i -lt 7; $i++) { $swissLines.Add("选手$i"); $swissLines.Add($codeA) }
    $swissLines.Add('')
    for ($i = 0; $i -lt 9; $i++) { $swissLines.Add('-1') }
    $swiss = Invoke-Cli '06-swiss-default-seven' @('--tournament', $swissPath) $swissLines.ToArray()
    $swissSaved = Read-Tournament $swissPath
    Assert-Check ($swissSaved.IsFinished -and $swissSaved.Rounds -eq 3 -and
        @($swissSaved.Standings() | Where-Object Byes -GT 1).Count -eq 0) '七人默认瑞士轮轮数或轮空结果不正确。'

    $autoA = Join-Path $runDirectory 'auto-a.json'
    $autoB = Join-Path $runDirectory 'auto-b.json'
    [IO.File]::WriteAllText($autoA, $pendingJson, $utf8)
    [IO.File]::WriteAllText($autoB, $pendingJson, $utf8)
    $a = Invoke-Cli '07-auto-first' @('--tournament', $autoA, '--auto')
    $b = Invoke-Cli '08-auto-restart' @('--tournament', $autoB, '--auto')
    $hashA = [regex]::Match($a.Out, '自动对局种子：2654435769；回合：[0-9]+；最终状态哈希：([0-9a-f]{16})')
    $hashB = [regex]::Match($b.Out, '自动对局种子：2654435769；回合：[0-9]+；最终状态哈希：([0-9a-f]{16})')
    Assert-Check ($hashA.Success -and $hashB.Success -and $hashA.Value -ceq $hashB.Value) '同一未报对局重启后的种子、回合或哈希不同。'
    Assert-Check ([IO.File]::ReadAllText($autoA, $utf8) -ceq [IO.File]::ReadAllText($autoB, $utf8)) '自动代打恢复后保存的赛事不同。'
    $endJson = [IO.File]::ReadAllText($autoA, $utf8)
    $already = Invoke-Cli '09-already-finished' @('--tournament', $autoA, '--auto')
    Assert-Check ($already.Out.Contains('赛事已结束。') -and -not $already.Out.Contains('自动对局种子：') -and
        [IO.File]::ReadAllText($autoA, $utf8) -ceq $endJson) '已结束赛事被重复代打或改写。'

    $customPack = $pack | ConvertFrom-Json -AsHashtable
    $customPack.id = 't89_cli_fixture'
    $packDirectory = Join-Path $runDirectory 'external-fixture'
    [void][IO.Directory]::CreateDirectory($packDirectory)
    [IO.File]::WriteAllText((Join-Path $packDirectory 'fixture.json'), ($customPack | ConvertTo-Json -Depth 64), $utf8)
    $customDb = [StreamDuel.Core.ContentDb]::FromJson($rules, [IO.File]::ReadAllText((Join-Path $packDirectory 'fixture.json'), $utf8))
    $custom = [StreamDuel.Core.Tournament]::new($customDb, 'roundrobin', 0)
    $custom.AddPlayer('外部甲', (New-Code $customDb.PackId $teamA)); $custom.AddPlayer('外部乙', (New-Code $customDb.PackId $teamB))
    [void]$custom.NextRound()
    $customPath = Join-Path $runDirectory 'custom-tournament.json'
    [IO.File]::WriteAllText($customPath, $custom.ToJson(), $utf8)
    $customResult = Invoke-Cli '10-auto-pack-from-save' @('--tournament', $customPath, '--pack-dir', $packDirectory) @('-1')
    Assert-Check ([StreamDuel.Core.Tournament]::FromJson($customDb, [IO.File]::ReadAllText($customPath, $utf8)).IsFinished) '续赛未自动采用存档中的外部内容包。'
    $customText = [IO.File]::ReadAllText($customPath, $utf8)
    $null = Invoke-Cli '11-explicit-wrong-pack' @('--tournament', $customPath, '--pack', 'original', '--pack-dir', $packDirectory) @() 1
    Assert-Check ([IO.File]::ReadAllText($customPath, $utf8) -ceq $customText) '内容包不匹配时改写了存档。'

    $bad = Join-Path $runDirectory 'bad.json'
    [IO.File]::WriteAllText($bad, '{损坏档案', $utf8)
    $null = Invoke-Cli '12-bad-file' @('--tournament', $bad) @() 1
    Assert-Check ([IO.File]::ReadAllText($bad, $utf8) -ceq '{损坏档案') '损坏档案被覆盖。'
    $protected = Join-Path $runDirectory 'profile.json'
    [IO.File]::WriteAllText($protected, $pendingJson, $utf8)
    $null = Invoke-Cli '13-protected-player-path' @('--tournament', $protected, '--auto') @() 1
    Assert-Check ([IO.File]::ReadAllText($protected, $utf8) -ceq $pendingJson) '玩家档案保留路径被覆盖。'
    $null = Invoke-Cli '14-protected-program-path' @('--tournament', (Join-Path $binaryDirectory 'unused-tournament.json')) @() 1
    $null = Invoke-Cli '15-protected-content-path' @('--tournament', (Join-Path $packDirectory 'unused.json'), '--pack-dir', $packDirectory) @() 1
    if ($IsWindows) {
        foreach ($device in @('CON.json', 'NUL.json', 'COM1.json', 'LPT9.json', 'COM1/subfile.json')) {
            $null = Invoke-Cli ('reject-device-' + $device.Replace('/', '-').Replace('.', '-')) @('--tournament', (Join-Path $runDirectory $device)) @() 1
        }
    }

    $rejected = Join-Path $runDirectory 'must-not-create.json'
    foreach ($case in @(
        @{ Name = 'seed'; Args = @('--seed', '9') }, @{ Name = 'level'; Args = @('--level', 'hard') },
        @{ Name = 'hash'; Args = @('--hash') }, @{ Name = 'report'; Args = @('--report', 'out.md') },
        @{ Name = 'replay'; Args = @('--replay', 'record.json') }, @{ Name = 'list-packs'; Args = @('--list-packs') },
        @{ Name = 'host'; Args = @('--host', '0') }, @{ Name = 'timer'; Args = @('--timer', '1,1') },
        @{ Name = 'team'; Args = @('--team', ($teamA -join ',')) }, @{ Name = 'ban'; Args = @('--ban') },
        @{ Name = 'lead'; Args = @('--lead') }, @{ Name = 'save-dir'; Args = @('--save-dir', 'saves') },
        @{ Name = 'duplicate'; Args = @('--tournament', $rejected) }
    )) {
        $null = Invoke-Cli ('reject-' + $case.Name) (@('--tournament', $rejected) + $case.Args) @() 1
        Assert-Check (-not (Test-Path -LiteralPath $rejected)) '不兼容参数被拒绝前已创建赛事。'
    }
    $null = Invoke-Cli '16-missing-path' @('--tournament') @() 1
    $help = Invoke-Cli '17-help' @('--tournament', $rejected, '--help')
    Assert-Check ($help.Out.Contains('--tournament') -and -not (Test-Path -LiteralPath $rejected)) '帮助未显示赛事选项或创建了赛事。'
    $passed = $true
}
catch { $failure = $_.Exception.Message; throw }
finally {
    [IO.File]::WriteAllText((Join-Path $runDirectory 'result.json'), ([ordered]@{
        passed = $passed; checks = @($checks.ToArray()); failure = $failure; directory = $runDirectory
    } | ConvertTo-Json -Depth 6), $utf8)
    Write-Host "赛事 CLI 验收产物：$runDirectory"
}
