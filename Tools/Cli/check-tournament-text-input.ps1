#requires -Version 7.0
param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug')

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
# 只使用当前工作树已构建的程序，产物放入独立的忽略目录。
$relativeRoot = if (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'Cli.csproj')) { '../..' } else { '../../..' }
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot $relativeRoot))
if (-not (Test-Path -LiteralPath (Join-Path $repository 'Tools/Cli/Cli.csproj'))) { throw '无法定位本仓库 CLI。' }
$binaryDirectory = Join-Path $repository "Tools/Cli/bin/$Configuration/net8.0"
$cliDll = Join-Path $binaryDirectory 'Cli.dll'
$coreDll = Join-Path $binaryDirectory 'StreamDuel.Core.dll'
foreach ($path in @($cliDll, $coreDll)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "请先构建 Tools/Cli -c $Configuration。" }
}
$allowedRoot = [IO.Path]::GetFullPath((Join-Path $repository 'Engine.Tests/obj/t97-review/runs'))
$runDirectory = [IO.Path]::GetFullPath((Join-Path $allowedRoot ([Guid]::NewGuid().ToString('N'))))
if (-not $runDirectory.StartsWith($allowedRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw '验收产物必须位于本仓库 obj/t97-review/runs 内。'
}
[void][IO.Directory]::CreateDirectory($runDirectory)
$temporaryDirectory = Join-Path $runDirectory 'test-temp'
[void][IO.Directory]::CreateDirectory($temporaryDirectory)
$utf8 = [Text.UTF8Encoding]::new($false, $true)
$checks = [Collections.Generic.List[string]]::new()
$binaryHashes = @(Get-FileHash -LiteralPath $cliDll, $coreDll -Algorithm SHA256 | Select-Object Path, Hash)
$passed = $false
$failure = $null

function Assert-Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "$Message 证据目录：$runDirectory" }
}

function Invoke-Cli([string]$Name, [string]$TournamentPath, [string[]]$Lines = @()) {
    $directory = Join-Path $runDirectory $Name
    [void][IO.Directory]::CreateDirectory($directory)
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = 'dotnet'
    foreach ($argument in @($cliDll, '--tournament', $TournamentPath)) { $start.ArgumentList.Add($argument) }
    $start.WorkingDirectory = $directory
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardInputEncoding = $utf8
    $start.StandardOutputEncoding = $utf8
    $start.StandardErrorEncoding = $utf8
    foreach ($key in @('TEMP', 'TMP', 'TMPDIR')) { $start.Environment[$key] = $temporaryDirectory }
    $start.Environment['DOTNET_GENERATE_ASPNET_CERTIFICATE'] = 'false'
    $start.Environment['DOTNET_SKIP_FIRST_TIME_EXPERIENCE'] = '1'
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
        $timedOut = -not $process.WaitForExit(30000)
        if ($timedOut) { $process.Kill($true); [void]$process.WaitForExit(5000) }
        Assert-Check ($stdout.Wait(5000) -and $stderr.Wait(5000)) "$Name 输出读取未结束。"
        $result = [pscustomobject]@{ Out = $stdout.Result; Err = $stderr.Result; Exit = $process.ExitCode }
        [IO.File]::WriteAllText((Join-Path $directory 'stdout.log'), $result.Out, $utf8)
        [IO.File]::WriteAllText((Join-Path $directory 'stderr.log'), $result.Err, $utf8)
        Assert-Check (-not $timedOut -and $result.Exit -eq 0 -and $result.Err.Length -eq 0) "$Name 应在 30 秒内正常完成且无错误。"
        Assert-Check (-not (Test-Path -LiteralPath (Join-Path $directory 'saves'))) "$Name 创建了赛事以外的玩家存档。"
        return $result
    }
    finally {
        if ($started -and -not $process.HasExited) { $process.Kill($true); [void]$process.WaitForExit(5000) }
        $process.Dispose()
    }
}

function New-Code([string[]]$Ids) {
    $preset = [StreamDuel.Core.TeamPreset]::new()
    $preset.Name = '赛事显示验收队伍'
    $preset.PackId = $db.PackId
    foreach ($id in $Ids) { $preset.Ids.Add($id) }
    return [StreamDuel.Core.TeamCode]::Encode($preset)
}

function Assert-Safe([string]$Output) {
    foreach ($unsafe in @([char]0, [char]0x1b, [char]0x200b, [char]0x202e)) {
        Assert-Check (-not $Output.Contains([string]$unsafe)) '赛事显示仍含控制、零宽或方向控制字符。'
    }
    Assert-Check (-not $Output.Contains($word) -and -not $Output.Contains($rawA) -and -not $Output.Contains($rawB)) '赛事显示仍泄漏原始禁词或超长姓名。'
}

function Assert-File([string]$Path, $Expected) {
    $json = [IO.File]::ReadAllText($Path, $utf8)
    Assert-Check ($json -ceq $Expected.ToJson()) '赛事 JSON 与相同报名/报分的公开 Core 结果不一致。'
    $restored = [StreamDuel.Core.Tournament]::FromJson($db, $json)
    Assert-Check ($restored.ToJson() -ceq $json) '赛事读回改变了原始姓名或其他存档字段。'
    return $restored
}

function Competition-State($Tournament) {
    # 只移除对照组刻意不同的姓名；比分、队伍码、编号、顺序和所有其余字段逐项比较。
    $root = $Tournament.ToJson() | ConvertFrom-Json -AsHashtable
    foreach ($player in $root.players) { [void]$player.Remove('name') }
    return $root | ConvertTo-Json -Depth 64 -Compress
}

function Standing-State($Tournament) {
    return @($Tournament.Standings() | Select-Object Player, Points, OpponentPoints, HeadToHeadPoints, Wins, Draws, Losses, Byes, Rank) |
        ConvertTo-Json -Depth 8 -Compress
}

try {
    [void][Reflection.Assembly]::LoadFrom($coreDll)
    Assert-Check ([StringComparer]::OrdinalIgnoreCase.Equals(
        [IO.Path]::GetFullPath([StreamDuel.Core.Tournament].Assembly.Location), [IO.Path]::GetFullPath($coreDll))) '请在没有加载其他 Core 的新 PowerShell 进程运行。'
    Assert-Check ($null -ne [StreamDuel.Core.Tournament].Assembly.GetType('StreamDuel.Core.TextInput', $false)) '当前 Core 尚未包含 T97 TextInput。'
    $content = Join-Path $binaryDirectory 'Content'
    $db = [StreamDuel.Core.ContentDb]::FromJson(
        [IO.File]::ReadAllText((Join-Path $content 'StreamDuelRules.json'), $utf8),
        [IO.File]::ReadAllText((Join-Path $content 'Packs/original.json'), $utf8))
    $codeA = New-Code @('bell', 'enka', 'shiori', 'lumi', 'hane', 'momo')
    $codeB = New-Code @('kei', 'yuan', 'nyako', 'buck', 'kasumi', 'bee')
    $word = [StreamDuel.Core.TextLint]::DefaultBannedWords[0]
    $rawPrefix = '甲' + [char]0x200b + $word + ('🦊' * 20)
    $rawA = $rawPrefix + 'A'
    $rawB = $rawPrefix + 'B'
    $safeA = [StreamDuel.Core.TextInput]::Clean($rawA, [StreamDuel.Core.TextInputKind]::PlayerName).Text
    $safeB = [StreamDuel.Core.TextInput]::Clean($rawB, [StreamDuel.Core.TextInputKind]::PlayerName).Text
    Assert-Check ($safeA -ceq $safeB -and $rawA -cne $rawB) '夹具必须覆盖两个原名清理后同名。'
    $save = Join-Path $runDirectory 'filtered-tournament.json'
    $expected = [StreamDuel.Core.Tournament]::new($db, 'roundrobin', 0)

    $registered = Invoke-Cli '01-registration-exit' $save @('1', $rawA, $codeA, 'q')
    $expected.AddPlayer($rawA, $codeA)
    $saved = Assert-File $save $expected
    Assert-Check ($saved.Round -eq 0 -and $saved.Players[0].Name -ceq $rawA) '报名中退出没有原样保存姓名。'
    Assert-Safe $registered.Out
    Assert-Check ($registered.Out.Contains("请输入报名 #1 $safeA 的队伍码") -and
        $registered.Out.Contains("已保存报名：报名 #1 $safeA") -and
        $registered.Out.Contains('报名 #1 昵称显示已调整：')) '报名提示、保存提示或中文调整提示不正确。'
    $checks.Add('首次报名后退出：安全显示及提示，完整 JSON 保留原姓名')

    $resumed = Invoke-Cli '02-registration-resume' $save @($rawB, $codeB, '', 'q')
    $expected.AddPlayer($rawB, $codeB)
    [void]$expected.NextRound()
    $saved = Assert-File $save $expected
    Assert-Check ($saved.Round -eq 1 -and -not $saved.IsFinished -and $saved.Players[1].Name -ceq $rawB) '续报名或未报对阵未正确保存。'
    Assert-Safe $resumed.Out
    Assert-Check ($resumed.Out.Contains("已报名：报名 #1 $safeA") -and
        $resumed.Out.Contains("请输入报名 #2 $safeB 的队伍码") -and
        $resumed.Out.Contains("0 = 报名 #1 $safeA 获胜；1 = 报名 #2 $safeB 获胜") -and
        $resumed.Out.Contains('报名 #1 昵称显示已调整：') -and
        $resumed.Out.Contains('报名 #2 昵称显示已调整：')) '续赛或清理后同名选手没有使用安全姓名和稳定编号。'
    $checks.Add('续报名后待赛退出：两原名保真，清理后同名仍用报名编号区分')

    $finished = Invoke-Cli '03-result-resume' $save @('1')
    $expected.ReportResult($expected.NextRound()[0], 1)
    $saved = Assert-File $save $expected
    Assert-Check ($saved.IsFinished -and $saved.Standings()[0].Player -eq 1 -and $saved.Standings()[0].Points -eq 3) '第二位选手胜利或排名发生变化。'
    Assert-Safe $finished.Out
    Assert-Check ($finished.Out.Contains("已保存结果：报名 #2 $safeB 获胜") -and
        $finished.Out.Contains("1 · 报名 #2 ${safeB}：3 分") -and
        $finished.Out.Contains("2 · 报名 #1 ${safeA}：0 分") -and $finished.Out.Contains('赛事已结束。')) '报分、排名或终局显示没有使用清理后的名字。'
    $checks.Add('续赛至结束：报分及排名安全，结果与原始 Core 赛事完全一致')

    $normalPath = Join-Path $runDirectory 'normal-tournament.json'
    $normal = Invoke-Cli '04-normal-names-control' $normalPath @('1', '正常甲', $codeA, '正常乙', $codeB, '', '1')
    $normalExpected = [StreamDuel.Core.Tournament]::new($db, 'roundrobin', 0)
    $normalExpected.AddPlayer('正常甲', $codeA)
    $normalExpected.AddPlayer('正常乙', $codeB)
    $normalExpected.ReportResult($normalExpected.NextRound()[0], 1)
    $normalSaved = Assert-File $normalPath $normalExpected
    Assert-Check (-not $normal.Out.Contains('昵称显示已调整：') -and
        $normal.Out.Contains('请输入报名 #1 正常甲 的队伍码') -and
        $normal.Out.Contains('已保存结果：报名 #2 正常乙 获胜') -and
        $normal.Out.Contains('1 · 报名 #2 正常乙：3 分')) '正常中文名字被改动或出现多余清理提示。'
    Assert-Check ((Competition-State $saved) -ceq (Competition-State $normalSaved) -and
        (Standing-State $saved) -ceq (Standing-State $normalSaved)) '清理显示改变了对阵、队伍、比分、排序或任何非姓名赛事字段。'
    $checks.Add('正常姓名对照：显示保持，双方对阵/非姓名存档字段/排名完全相同')

    $before = (Get-FileHash -LiteralPath $save -Algorithm SHA256).Hash
    $reopened = Invoke-Cli '05-finished-reopen' $save
    Assert-Safe $reopened.Out
    Assert-Check ($reopened.Out.Contains("1 · 报名 #2 ${safeB}：3 分") -and
        (Get-FileHash -LiteralPath $save -Algorithm SHA256).Hash -ceq $before) '已结束赛事重新打开改写了原文件或未清理排名。'
    $checks.Add('终局重开：仍安全显示，原赛事文件 SHA256 不变')
    foreach ($binary in $binaryHashes) {
        Assert-Check ((Get-FileHash -LiteralPath $binary.Path -Algorithm SHA256).Hash -ceq $binary.Hash) '验收期间构建产物发生变化。'
    }
    $passed = $true
}
catch { $failure = $_.Exception.Message; throw }
finally {
    [IO.File]::WriteAllText((Join-Path $runDirectory 'report.json'), ([ordered]@{
        passed = $passed; checks = $checks.ToArray(); failure = $failure; binaries = $binaryHashes
    } | ConvertTo-Json -Depth 6), $utf8)
    Write-Output "赛事文字验收产物：$runDirectory"
}
