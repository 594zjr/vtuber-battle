#requires -Version 7.0
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$repositoryPrefix = $repository.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$current = [IO.Path]::GetFullPath((Get-Location).Path)
if ($current -ne $repository -and -not $current.StartsWith($repositoryPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw '请在本仓库内运行此脚本。'
}
$cliDirectory = Join-Path $PSScriptRoot "bin/$Configuration/net8.0"
$cliDll = Join-Path $cliDirectory 'Cli.dll'
$coreDll = Join-Path $cliDirectory 'StreamDuel.Core.dll'
if (-not (Test-Path -LiteralPath $cliDll -PathType Leaf) -or -not (Test-Path -LiteralPath $coreDll -PathType Leaf)) {
    throw "请先运行 dotnet build Tools/Cli -c $Configuration。"
}
$runDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ('bin/text-input-check/' + [Guid]::NewGuid().ToString('N'))))
if (-not $runDirectory.StartsWith($repositoryPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw '验收产物目录必须位于本仓库内。'
}
[void][IO.Directory]::CreateDirectory($runDirectory)
$temporaryDirectory = Join-Path $repository 'Engine.Tests/bin/test-temp'
[void][IO.Directory]::CreateDirectory($temporaryDirectory)
$utf8 = [Text.UTF8Encoding]::new($false)
$checks = [Collections.Generic.List[string]]::new()
$invocations = [Collections.Generic.List[object]]::new()
$binaryHashes = @(Get-FileHash -LiteralPath $cliDll, $coreDll -Algorithm SHA256 | Select-Object Path, Hash)
[void][Reflection.Assembly]::LoadFrom($coreDll)

function Assert-Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "$Message 证据目录：$runDirectory" }
}

function Invoke-Cli([string]$Name, [string[]]$Parameters, [string]$InputText = '') {
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
    $start.Environment['TEMP'] = $temporaryDirectory
    $start.Environment['TMP'] = $temporaryDirectory
    $start.Environment['DOTNET_GENERATE_ASPNET_CERTIFICATE'] = 'false'
    $start.Environment['DOTNET_SKIP_FIRST_TIME_EXPERIENCE'] = '1'
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    try {
        Assert-Check ($process.Start()) "$Name 无法启动。"
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if ($InputText.Length -gt 0) { $process.StandardInput.Write($InputText) }
        $process.StandardInput.Close()
        if (-not $process.WaitForExit(30000)) {
            $process.Kill($true)
            [void]$process.WaitForExit(5000)
            throw "$Name 超过 30 秒未退出。"
        }
        $result = [pscustomobject]@{ Out = $stdout.Result; Err = $stderr.Result; Exit = $process.ExitCode }
        [IO.File]::WriteAllText((Join-Path $runDirectory "$Name.stdout.log"), $result.Out, $utf8)
        [IO.File]::WriteAllText((Join-Path $runDirectory "$Name.stderr.log"), $result.Err, $utf8)
        $invocations.Add([ordered]@{ name = $Name; parameters = $Parameters; exit = $result.Exit })
        Assert-Check ($result.Exit -eq 0 -and $result.Err.Length -eq 0) "$Name 应正常完成且无错误输出。"
        return $result
    }
    finally { $process.Dispose() }
}

function Assert-Safe([string]$Text, [string]$Name, [string]$Word) {
    foreach ($unsafe in @([char]0, [char]0x1b, [char]0x200b, [char]0x202e, [char]0xd800, [char]0xdc00)) {
        Assert-Check (-not $Text.Contains([string]$unsafe)) "$Name 含未移除的控制、零宽或无效字符。"
    }
    Assert-Check (-not $Text.Contains($Word)) "$Name 直接显示了词表内的原词。"
}

function Plan-Hotseat([string]$NameA, [string]$NameB) {
    $options = [StreamDuel.Core.BattleOptions]::new()
    $options.Seed = 97
    $options.BanOne = $true
    $options.LeadPick = $true
    $session = [StreamDuel.Core.HotseatSession]::new($db, $hotseatTeamA, $hotseatTeamB, $NameA, $NameB, $options)
    $transcript = [Text.StringBuilder]::new()
    $handovers = 0
    while ($session.Phase -ne [StreamDuel.Core.Phase]::Over) {
        Assert-Check ($transcript.Length -lt 20000) '同屏夹具未在合理步骤内结束。'
        if ($session.NeedsHandover) {
            [void]$transcript.Append("`n")
            $session.ConfirmHandover()
            $handovers++
        }
        $side = $session.WhoseTurn
        $view = $session.ViewFor($side)
        switch ($session.Phase.ToString()) {
            'Ban' { [void]$transcript.Append("1`n"); $session.SubmitBan(0) }
            'Lead' { [void]$transcript.Append("2`n"); $session.SubmitLead(1) }
            'Replace' {
                $target = $view.SwitchTargets[0]
                [void]$transcript.Append(($target + 1).ToString() + "`n")
                $session.SubmitReplace($target)
            }
            'Choose' {
                if ($null -ne $view.ControlledReason) {
                    [void]$transcript.Append("0`n")
                    $session.Submit([StreamDuel.Core.BattleAction]::Pass())
                } elseif ($view.Struggle) {
                    [void]$transcript.Append("0`n")
                    $action = [StreamDuel.Core.BattleAction]::new()
                    $action.Type = [StreamDuel.Core.ActionType]::Struggle
                    $session.Submit($action)
                } else {
                    $skill = @($view.Skills | Where-Object Usable | Sort-Object @{ Expression = 'DamageMax'; Descending = $true }, Index)[0]
                    [void]$transcript.Append(($skill.Index + 1).ToString() + "`n")
                    $session.Submit([StreamDuel.Core.BattleAction]::Skill($skill.Index))
                }
            }
        }
    }
    return [pscustomobject]@{ Session = $session; Input = $transcript.ToString(); Handovers = $handovers }
}

function Hotseat-Log([string]$Output) {
    $entries = [Collections.Generic.List[string]]::new()
    $node = $null
    $turn = 0
    foreach ($line in ($Output -split "`n")) {
        if ($line -match '^【第 ([0-9]+) 回合 · ([^】]+)】\r?$') { $turn = [int]$Matches[1]; $node = $Matches[2] }
        elseif ($null -ne $node -and $line.StartsWith('  ')) { $entries.Add("$turn|$node|" + $line.Substring(2).TrimEnd("`r")) }
        else { $node = $null }
    }
    return $entries -join "`n"
}

try {
    $save = Join-Path $runDirectory 'baseline-saves'
    $null = Invoke-Cli '01-baseline' @('--auto', '--pack', 'original', '--seed', '97', '--ban', '--lead', '--save-dir', $save)
    $files = @(Get-ChildItem -LiteralPath (Join-Path $save 'replays') -File -Filter '*.json' |
        Where-Object { -not $_.Name.StartsWith('.') })
    Assert-Check ($files.Count -eq 1) '基线应生成一份完整回放。'
    $record = [StreamDuel.Core.BattleRecord]::FromJson([IO.File]::ReadAllText($files[0].FullName))
    $word = [StreamDuel.Core.TextLint]::DefaultBannedWords[0]
    $firstRaw = " `t甲`n" + [char]0x200b + $word + '🦊' + [char]0x202e + ' 名称 ' + [char]0
    $secondRaw = ('🛰' * 16) + [char]0xd800 + "`r`n"
    $record.Names = [string[]]@($firstRaw, $secondRaw)
    $rawJson = $record.ToJson()
    $inputPath = Join-Path $runDirectory 'unsafe-names.json'
    [IO.File]::WriteAllText($inputPath, $rawJson, $utf8)
    $inputHash = (Get-FileHash -LiteralPath $inputPath -Algorithm SHA256).Hash
    $reportPath = Join-Path $runDirectory 'unsafe-names-report.md'
    $unusedSave = Join-Path $runDirectory 'replay-must-not-save'
    $replay = Invoke-Cli '02-filtered-replay' @('--replay', $inputPath, '--auto', '--share', '--report', $reportPath, '--save-dir', $unusedSave)
    $safeNames = @($record.Names | ForEach-Object { [StreamDuel.Core.TextInput]::Clean($_, [StreamDuel.Core.TextInputKind]::PlayerName).Text })
    foreach ($name in $safeNames) { Assert-Check ($replay.Out.Contains($name)) '回放未使用清理后的玩家名。' }
    Assert-Safe $replay.Out '回放终端输出' $word
    Assert-Check ($replay.Out.Contains('昵称显示已调整：')) '回放缺少中文清理提示。'
    Assert-Check ((Get-FileHash -LiteralPath $inputPath -Algorithm SHA256).Hash -eq $inputHash) '回看改写了原始录像。'
    Assert-Check (-not (Test-Path -LiteralPath $unusedSave)) '回看产生了新存档。'
    $shared = [regex]::Match($replay.Out, '(?m)^分享码：(SD1-[A-Za-z0-9_-]+)\r?$')
    Assert-Check ($shared.Success) '完整回放未导出分享码。'
    Assert-Check ([StreamDuel.Core.ShareCode]::Decode($shared.Groups[1].Value).ToJson() -ceq $rawJson) '显示过滤改写了分享码内原始记录。'
    $checks.Add('异常昵称回放：安全显示、提示、原文件不变、分享码保真、不新增存档')

    $rules = [IO.File]::ReadAllText((Join-Path $cliDirectory 'Content/StreamDuelRules.json'))
    $pack = [IO.File]::ReadAllText((Join-Path $cliDirectory 'Content/Packs/original.json'))
    $db = [StreamDuel.Core.ContentDb]::FromJson($rules, $pack)
    $battle = [StreamDuel.Core.Replay]::Run($db, $record)
    $display = [StreamDuel.Core.TextInput]::ForDisplay($battle)
    $report = [IO.File]::ReadAllText($reportPath)
    Assert-Check ($report -ceq [StreamDuel.Core.BattleReport]::Markdown($display, 0)) 'CLI 战报未与 Core 显示副本完整一致。'
    Assert-Safe $report 'Markdown 战报' $word
    Assert-Check ($record.ToJson() -ceq $rawJson -and $battle.Record.ToJson() -ceq $rawJson) '显示副本修改了原记录。'
    $checks.Add('异常昵称战报：安全显示且与 Core 显示副本完全一致')

    $librarySave = Join-Path $runDirectory 'library-saves'
    $libraryPath = Join-Path $librarySave 'replay-library'
    $library = [StreamDuel.Core.ReplayLibrary]::new($libraryPath, 200, $null)
    $note = " `t前`n" + [char]0x200b + $word + [char]0x1b + ' 后 ' + ('🦊' * 65) + [char]0xdc00
    $id = $library.Save($record, [StreamDuel.Core.BattleSummary]::From($battle), $note)
    $libraryFile = Join-Path $libraryPath ($id + '.json')
    $libraryHash = (Get-FileHash -LiteralPath $libraryFile -Algorithm SHA256).Hash
    $listed = Invoke-Cli '03-filtered-library' @('--replays', '--save-dir', $librarySave)
    Assert-Safe $listed.Out '录像库列表' $word
    foreach ($name in $safeNames) { Assert-Check ($listed.Out.Contains($name)) '录像列表未使用清理后的玩家名。' }
    $safeNote = [StreamDuel.Core.TextInput]::Clean($note, [StreamDuel.Core.TextInputKind]::Note).Text
    Assert-Check ($listed.Out.Contains($safeNote)) '录像列表未使用清理后的备注。'
    Assert-Check ($listed.Out.Contains('文字提示：')) '录像列表缺少中文清理提示。'
    Assert-Check ((Get-FileHash -LiteralPath $libraryFile -Algorithm SHA256).Hash -eq $libraryHash) '列出录像改写了原始录像。'
    Assert-Check ($library.List()[0].Note -ceq $note -and $library.Load($id).ToJson() -ceq $rawJson) '列表过滤破坏了备注或录像保真。'
    $checks.Add('录像库：异常昵称备注安全显示、中文提示、原始元数据和记录保真')

    $library.SetNote($id, " `t`r`n")
    $cleared = Invoke-Cli '04-empty-note' @('--replays', '--save-dir', $librarySave)
    Assert-Check ($cleared.Out.Contains('备注：无备注') -and $null -eq $library.List()[0].Note) '默认备注显示改变了清除收藏的语义。'
    $checks.Add('空备注显示默认值，原始备注仍为 null')

    $legacy = [StreamDuel.Core.BattleRecord]::FromJson($rawJson)
    $legacy.Names = $null
    $legacyPath = Join-Path $runDirectory 'legacy-without-names.json'
    $legacyJson = $legacy.ToJson()
    Assert-Check (-not $legacyJson.Contains('"names"')) '旧记录样例仍包含 names。'
    [IO.File]::WriteAllText($legacyPath, $legacyJson, $utf8)
    $legacyReport = Join-Path $runDirectory 'legacy-report.md'
    $legacyReplay = Invoke-Cli '05-legacy-without-names' @('--replay', $legacyPath, '--auto', '--report', $legacyReport, '--save-dir', $unusedSave)
    Assert-Check ($legacyReplay.Out.Contains('回看结束：') -and $legacyReplay.Out.Contains('你的队伍：') -and $legacyReplay.Out.Contains('对手的队伍：')) '缺省 names 的旧记录不能正常回看。'
    Assert-Check ([IO.File]::ReadAllText($legacyPath) -ceq $legacyJson -and -not (Test-Path -LiteralPath $unusedSave)) '旧记录回看改写文件或创建存档。'
    $checks.Add('无 names 的旧录像仍使用你/对手，完整回看和战报成功且不存档')

    $preset = [StreamDuel.Core.TeamPreset]::new()
    $preset.Name = $firstRaw + ('🦊' * 20)
    $preset.PackId = 'original'
    foreach ($member in $record.Teams[0]) { $preset.Ids.Add($member) }
    $teamCode = [StreamDuel.Core.TeamCode]::Encode($preset)
    $teamSave = Join-Path $runDirectory 'team-code-must-not-save'
    $team = Invoke-Cli '06-team-code' @('--team-code', $teamCode, '--export-team', '--save-dir', $teamSave) "q`n"
    Assert-Safe $team.Out '队伍码导入提示' $word
    Assert-Check ($team.Out.Contains('文字提示：') -and $team.Out.Contains('本方队伍码：' + $teamCode)) '队伍码导入未提示或更改了导出字节。'
    Assert-Check (-not (Test-Path -LiteralPath $teamSave)) '队伍码提前退出产生了存档。'
    $checks.Add('队伍码异常名称仅显示通用提示，导出字节不变，提前退出不存档')

    [string[]]$hotseatTeamA = @('bell', 'enka', 'shiori', 'lumi', 'hane', 'momo')
    [string[]]$hotseatTeamB = @('kei', 'yuan', 'nyako', 'buck', 'kasumi', 'bee')
    # CLI 沿用控制字符拒绝契约；同屏输入用可进入会话的零宽、方向格式字符和超长名字。
    $hotseatRawA = '甲' + [char]0x200b + $word + [char]0x202e + ' ' + ('🦊' * 20)
    $hotseatRawB = ('🛰' * 16) + '乙'
    $hotseatSafeA = [StreamDuel.Core.TextInput]::Clean($hotseatRawA, [StreamDuel.Core.TextInputKind]::PlayerName).Text
    $hotseatSafeB = [StreamDuel.Core.TextInput]::Clean($hotseatRawB, [StreamDuel.Core.TextInputKind]::PlayerName).Text
    $plan = Plan-Hotseat $hotseatRawA $hotseatRawB
    $hotseatSave = Join-Path $runDirectory 'hotseat-saves'
    $hotseatReport = Join-Path $runDirectory 'hotseat-report.md'
    $hotseatInput = Join-Path $runDirectory 'hotseat-input.txt'
    [IO.File]::WriteAllText($hotseatInput, $plan.Input, $utf8)
    $hotseat = Invoke-Cli '07-hotseat' @('--hotseat', '--seed', '97', '--ban', '--lead',
        '--team', ($hotseatTeamA -join ','), '--team-b', ($hotseatTeamB -join ','),
        '--name-a', $hotseatRawA, '--name-b', $hotseatRawB, '--share', '--report', $hotseatReport,
        '--save-dir', $hotseatSave) $plan.Input
    $clearSequence = "$([char]0x1b)[2J$([char]0x1b)[H$([char]0x1b)[3J"
    $handoverCount = [regex]::Matches($hotseat.Out, [regex]::Escape($clearSequence)).Count
    Assert-Check ($plan.Handovers -gt 1 -and $handoverCount -eq $plan.Handovers) '同屏显示清理改变了交接或清屏次数。'
    # 只移除程序刻意输出的完整清屏序列，其他 ESC 仍须被安全断言拦截。
    $hotseatPlain = $hotseat.Out.Replace($clearSequence, '')
    Assert-Safe $hotseatPlain '同屏开场、交接与完整日志' $word
    Assert-Check ($hotseatPlain.Contains("种子：97；A 方：$hotseatSafeA；B 方：$hotseatSafeB") -and
        $hotseatPlain.Contains('A 方昵称显示已调整：') -and $hotseatPlain.Contains('B 方昵称显示已调整：')) '同屏开场未显示安全名字与两方中文提示。'
    foreach ($name in @($hotseatSafeA, $hotseatSafeB)) {
        Assert-Check ($hotseatPlain.Contains("请把电脑交给「$name」。") -and
            $hotseatPlain.Contains($name + ' 操作 =====')) '同屏交接或操作视图未显示安全名字。'
    }
    $expectedLog = ($plan.Session.DisplayLog | ForEach-Object { "$($_.Turn)|$($_.Node)|$($_.Text)" }) -join "`n"
    $rawLog = ($plan.Session.Log | ForEach-Object { "$($_.Turn)|$($_.Node)|$($_.Text)" }) -join "`n"
    Assert-Check ($expectedLog -cne $rawLog -and (Hotseat-Log $hotseatPlain) -ceq $expectedLog) '同屏完整公开日志没有逐字使用安全显示副本。'
    $checks.Add('同屏异常昵称：开场与两方提示、交接、操作视图及完整公开日志安全，清屏次数不变')

    $hotseatRecords = @(Get-ChildItem -LiteralPath (Join-Path $hotseatSave 'replays') -File -Filter '*.json' |
        Where-Object { -not $_.Name.StartsWith('.') })
    Assert-Check ($hotseatRecords.Count -eq 1) '同屏完整对局没有恰好保存一份旧版回放。'
    $hotseatRawJson = $plan.Session.Record.ToJson()
    Assert-Check ([IO.File]::ReadAllText($hotseatRecords[0].FullName, $utf8) -ceq $hotseatRawJson) '同屏显示过滤改变了原始回放。'
    $hotseatLibrary = [StreamDuel.Core.ReplayLibrary]::new((Join-Path $hotseatSave 'replay-library'), 200, $null)
    $hotseatEntries = @($hotseatLibrary.List())
    Assert-Check ($hotseatEntries.Count -eq 1 -and
        $hotseatLibrary.Load($hotseatEntries[0].Id).ToJson() -ceq $hotseatRawJson) '同屏显示过滤改变了录像库记录。'
    $hotseatCode = [regex]::Match($hotseat.Out, '(?m)^分享码：(SD1-[A-Za-z0-9_-]+)\r?$')
    Assert-Check ($hotseatCode.Success -and
        [StreamDuel.Core.ShareCode]::Decode($hotseatCode.Groups[1].Value).ToJson() -ceq $hotseatRawJson) '同屏分享码未保留原始名字或记录。'
    $hotseatMarkdown = [IO.File]::ReadAllText($hotseatReport, $utf8)
    Assert-Safe $hotseatMarkdown '同屏战报' $word
    Assert-Check ($hotseatMarkdown -ceq [StreamDuel.Core.BattleReport]::Markdown(
        [StreamDuel.Core.TextInput]::ForDisplay($plan.Session.CloneCompletedBattle()), 0)) '同屏战报不是完整安全显示副本。'
    $checks.Add('同屏原始回放、录像库及分享码完整保真，战报使用安全显示副本')

    foreach ($binary in $binaryHashes) {
        Assert-Check ((Get-FileHash -LiteralPath $binary.Path -Algorithm SHA256).Hash -eq $binary.Hash) '验收期间构建产物发生变化，需重新验收。'
    }
    [IO.File]::WriteAllText((Join-Path $runDirectory 'report.json'),
        ([ordered]@{ status = 'PASS'; checks = $checks.ToArray(); binaries = $binaryHashes; invocations = $invocations.ToArray() } | ConvertTo-Json -Depth 8), $utf8)
    Write-Output "PASS：$($checks.Count) 组文字输入显示检查；证据：$runDirectory"
}
catch {
    [IO.File]::WriteAllText((Join-Path $runDirectory 'report.json'),
        ([ordered]@{ status = 'FAIL'; error = $_.Exception.Message; checks = $checks.ToArray(); binaries = $binaryHashes; invocations = $invocations.ToArray() } | ConvertTo-Json -Depth 8), $utf8)
    throw
}
