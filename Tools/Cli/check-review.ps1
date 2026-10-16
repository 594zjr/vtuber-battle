#requires -Version 7.0
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

# 真实 TCP 的主客视角、完整存档与回看由 check-network.ps1 覆盖。
# 此脚本只补本地自动整局与保存回放重看；不构建，不删除证据，不写玩家存档。
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if (-not (Test-Path -LiteralPath (Join-Path $repository 'Engine/StreamDuel.Core.csproj'))) {
    throw '请从仓库内运行此脚本。'
}
$binaryDirectory = Join-Path $repository "Tools/Cli/bin/$Configuration/net8.0"
$cliDll = Join-Path $binaryDirectory 'Cli.dll'
$coreDll = Join-Path $binaryDirectory 'StreamDuel.Core.dll'
foreach ($path in @($cliDll, $coreDll)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "请先单独构建 Tools/Cli（$Configuration），本脚本不会构建。"
    }
}
$fixtureRoot = [IO.Path]::GetFullPath((Join-Path $repository 'Tools/Cli/obj/review-verification'))
$runDirectory = [IO.Path]::GetFullPath((Join-Path $fixtureRoot ([Guid]::NewGuid().ToString('N'))))
if (-not $runDirectory.StartsWith($fixtureRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw '验收目录越界。'
}
[void][IO.Directory]::CreateDirectory($runDirectory)
$utf8 = [Text.UTF8Encoding]::new($false, $true)
$children = [Collections.Generic.List[object]]::new()
$checks = [Collections.Generic.List[string]]::new()
$failure = $null
$team = @('bell', 'enka', 'shiori', 'lumi', 'hane', 'momo')

function Assert-Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "$Message 证据目录：$runDirectory" }
}

function Start-Cli([string]$Name, [string[]]$Parameters) {
    $workingDirectory = Join-Path $runDirectory $Name
    [void][IO.Directory]::CreateDirectory($workingDirectory)
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = 'dotnet'
    $start.ArgumentList.Add($cliDll)
    foreach ($parameter in $Parameters) { $start.ArgumentList.Add($parameter) }
    $start.WorkingDirectory = $workingDirectory
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = $utf8
    $start.StandardErrorEncoding = $utf8
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    $child = [pscustomobject]@{
        Name = $Name; Parameters = $Parameters; WorkingDirectory = $workingDirectory
        Process = $process; Started = $false; Killed = $false; CleanupError = $null
        OutTask = $null; ErrTask = $null; Timer = [Diagnostics.Stopwatch]::StartNew()
        OutPath = (Join-Path $workingDirectory 'stdout.log')
        ErrPath = (Join-Path $workingDirectory 'stderr.log'); Result = $null
    }
    $children.Add($child)
    Assert-Check ($process.Start()) "$Name 无法启动。"
    $child.Started = $true
    $child.OutTask = $process.StandardOutput.ReadToEndAsync()
    $child.ErrTask = $process.StandardError.ReadToEndAsync()
    $process.StandardInput.Close()
    return $child
}

function Collect-Output($Child) {
    if ($null -eq $Child.Result) {
        Assert-Check ($Child.OutTask.Wait(5000) -and $Child.ErrTask.Wait(5000)) "$($Child.Name) 输出读取超时。"
        $Child.Result = [pscustomobject]@{
            Exit = $Child.Process.ExitCode; Out = $Child.OutTask.Result; Err = $Child.ErrTask.Result
        }
        [IO.File]::WriteAllText($Child.OutPath, $Child.Result.Out, $utf8)
        [IO.File]::WriteAllText($Child.ErrPath, $Child.Result.Err, $utf8)
    }
}

function Wait-Cli($Child) {
    $remaining = [Math]::Max(1, 60000 - [int]$Child.Timer.ElapsedMilliseconds)
    Assert-Check ($Child.Process.WaitForExit($remaining)) "$($Child.Name) 超过 60 秒仍未退出。"
    Collect-Output $Child
    Assert-Check ($Child.Result.Exit -eq 0 -and $Child.Result.Err.Length -eq 0) "$($Child.Name) 应正常退出且没有错误输出。"
    return $Child.Result
}

function Save-Fingerprint([string]$Directory) {
    return ((Get-ChildItem -LiteralPath $Directory -File -Recurse -Force | Sort-Object FullName | ForEach-Object {
        $_.FullName + ':' + (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    }) -join "`n")
}

function Assert-Review($Battle, [string]$Output, [string]$ReportPath) {
    $loc = [StreamDuel.Core.Loc]::Current
    $heading = $loc.T('review.heading')
    $perspective = $loc.T('review.perspective', $Battle.Sides[0].Name)
    $moments = @([StreamDuel.Core.BattleReview]::KeyMoments($Battle, 0, 5))
    Assert-Check ($moments.Count -gt 0) '固定种子整局应产生实际关键回合，不能用空复盘通过接口验收。'
    $expected = $heading + "`n" + $perspective + "`n" + (($moments | ForEach-Object { '  ' + $_.Text }) -join "`n")
    $normalized = $Output.Replace("`r`n", "`n")
    $sections = [regex]::Matches($normalized, '(?ms)^' + [regex]::Escape($heading) + '\n.*?(?=^战报已保存：|\z)')
    Assert-Check ($sections.Count -eq 1 -and $sections[0].Value.TrimEnd([char]10) -ceq $expected) 'CLI 关键回合视角、数量、顺序或文字与 Core BattleReview 不一致。'
    Assert-Check ([regex]::Matches($normalized, '(?m)^战报已保存：').Count -eq 1) 'CLI 未恰好报告一次战报保存成功。'
    Assert-Check (Test-Path -LiteralPath $ReportPath -PathType Leaf) 'CLI 未导出 Markdown 战报。'
    $actualMarkdown = [IO.File]::ReadAllText($ReportPath, $utf8)
    $expectedMarkdown = [StreamDuel.Core.BattleReport]::Markdown($Battle, 0)
    Assert-Check ($actualMarkdown -ceq $expectedMarkdown) 'CLI 完整 Markdown 与同一回放的 Core 战报不一致。'
    Assert-Check ([regex]::Matches($actualMarkdown, '(?m)^## ' + [regex]::Escape($heading) + '$').Count -eq 1) 'Markdown 应恰好包含一个关键回合节。'
}

try {
    $assembly = [Reflection.Assembly]::LoadFrom($coreDll)
    Assert-Check ([IO.Path]::GetFullPath($assembly.Location) -eq [IO.Path]::GetFullPath($coreDll)) '请在未加载其他 Core 的新 PowerShell 进程运行。'
    Assert-Check ($null -ne $assembly.GetType('StreamDuel.Core.BattleReview')) '当前 CLI 构建尚未包含 T74。'
    $rules = [IO.File]::ReadAllText((Join-Path $binaryDirectory 'Content/StreamDuelRules.json'), $utf8)
    $pack = [IO.File]::ReadAllText((Join-Path $binaryDirectory 'Content/Packs/original.json'), $utf8)
    $db = [StreamDuel.Core.ContentDb]::FromJson($rules, $pack)
    $saveDirectory = Join-Path $runDirectory 'local-save'
    $localReport = Join-Path $runDirectory 'local-report.md'
    $localChild = Start-Cli 'local-game' @('--auto', '--pack', 'original', '--seed', '7', '--team', ($team -join ','), '--save-dir', $saveDirectory, '--report', $localReport)
    $localResult = Wait-Cli $localChild
    Assert-Check ($localResult.Out.Contains('对局结束：')) '本地自动对局没有完成。'
    $replayDirectory = Join-Path $saveDirectory 'replays'
    Assert-Check (Test-Path -LiteralPath $replayDirectory -PathType Container) '本地完整对局未保存回放目录。'
    $replayFiles = @(Get-ChildItem -LiteralPath $replayDirectory -File -Filter '*.json')
    Assert-Check ($replayFiles.Count -eq 1) '本地完整对局应恰好保存一份回放。'
    $record = [StreamDuel.Core.BattleRecord]::FromJson([IO.File]::ReadAllText($replayFiles[0].FullName, $utf8))
    Assert-Check ($record.Seed -eq 7 -and $record.PackId -ceq 'original' -and ($record.Teams[0] -join ',') -ceq ($team -join ',') -and $record.Events.Count -gt 0) '保存回放的种子、内容包、原始队伍或行动记录不完整。'
    $battle = [StreamDuel.Core.Replay]::Run($db, $record)
    Assert-Check ($battle.Phase -eq [StreamDuel.Core.Phase]::Over) '保存回放不能结算到终局。'
    Assert-Review $battle $localResult.Out $localReport
    $profilePath = Join-Path $saveDirectory 'profile.json'
    Assert-Check (Test-Path -LiteralPath $profilePath -PathType Leaf) '本地完整对局未保存档案。'
    $profile = [StreamDuel.Core.Profile]::FromJson([IO.File]::ReadAllText($profilePath, $utf8))
    Assert-Check ($profile.Packs.Count -eq 1 -and $profile.Packs.ContainsKey('original')) '本地档案内容包不正确。'
    $stats = $profile.Packs['original']
    $wins = [int]($battle.Winner -eq 0)
    $losses = [int]($battle.Winner -eq 1)
    $draws = [int]($battle.Winner -eq -1)
    Assert-Check ($stats.Wins -eq $wins -and $stats.Losses -eq $losses -and $stats.Draws -eq $draws -and $stats.Picks.Count -eq 6) '本地档案胜负或本方六人记录不正确，或重复累计。'
    foreach ($id in $team) {
        Assert-Check ($stats.Picks.ContainsKey($id) -and $stats.Picks[$id] -eq 1) "本方 $id 未恰好记录一次选用。"
    }
    $checks.Add('本地自动整局：完整回放/档案恰好一次，CLI 复盘逐字对应 BattleReview，完整 Markdown 对应 Core 战报')

    $before = Save-Fingerprint $saveDirectory
    $replayReport = Join-Path $runDirectory 'replay-report.md'
    $replayChild = Start-Cli 'saved-replay' @('--replay', $replayFiles[0].FullName, '--auto', '--save-dir', $saveDirectory, '--report', $replayReport)
    $replayResult = Wait-Cli $replayChild
    Assert-Check ($replayResult.Out.Contains('回看结束：')) 'CLI 未完整重看保存的对局。'
    Assert-Review $battle $replayResult.Out $replayReport
    Assert-Check ((Get-FileHash -LiteralPath $localReport -Algorithm SHA256).Hash -ceq (Get-FileHash -LiteralPath $replayReport -Algorithm SHA256).Hash) '回看与实际对局导出的完整战报字节不同。'
    Assert-Check ((Save-Fingerprint $saveDirectory) -ceq $before) '回看改动了档案、回放、索引或其他存档文件。'
    foreach ($child in $children) {
        Assert-Check (-not (Test-Path -LiteralPath (Join-Path $child.WorkingDirectory 'saves'))) 'CLI 绕过指定目录写入了默认存档。'
    }
    $checks.Add('保存回放重看：默认 0 方复盘逐字对应 Core，战报与实际对局字节一致，存档全部文件指纹不变')
}
catch { $failure = $_.Exception.Message }
finally {
    foreach ($child in $children) {
        try {
            if ($child.Started -and -not $child.Process.HasExited) {
                $child.Process.Kill($true)
                $child.Killed = $true
                Assert-Check ($child.Process.WaitForExit(5000)) "$($child.Name) 清理超时。"
            }
            if ($child.Started) { Collect-Output $child }
        }
        catch {
            $child.CleanupError = $_.Exception.Message
            if ($null -eq $failure) { $failure = $child.CleanupError }
        }
    }
    $invocations = @($children | ForEach-Object {
        [ordered]@{ name = $_.Name; parameters = $_.Parameters; killed = $_.Killed; cleanup_error = $_.CleanupError
            exit = $(if ($_.Started -and $_.Process.HasExited) { $_.Process.ExitCode } else { $null })
            stdout = $_.OutPath; stderr = $_.ErrPath }
    })
    foreach ($child in $children) { $child.Process.Dispose() }
    $summary = [ordered]@{ status = $(if ($null -eq $failure) { 'PASS' } else { 'FAIL' }); error = $failure
        checks = $checks.ToArray(); invocations = $invocations
        separate_required_check = '运行合入 T74 的 Tools/Cli/check-network.ps1：真实 TCP 主客各自复盘、战报、回放和存档' }
    [IO.File]::WriteAllText((Join-Path $runDirectory 'report.json'), ($summary | ConvertTo-Json -Depth 6), $utf8)
}
if ($null -ne $failure) { throw "$failure 证据目录：$runDirectory" }
Write-Output "PASS：$($checks.Count) 组 T74 本地 CLI 补充检查；证据：$runDirectory；另须运行 check-network.ps1。"
