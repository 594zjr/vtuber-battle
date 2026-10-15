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
$cliDirectory = Join-Path $repository 'Tools/Cli'
$cliDll = Join-Path $cliDirectory "bin/$Configuration/net8.0/Cli.dll"
if (-not (Test-Path -LiteralPath $cliDll -PathType Leaf)) {
    throw "请先运行 dotnet build Tools/Cli -c $Configuration。"
}
$runDirectory = [IO.Path]::GetFullPath((Join-Path $cliDirectory ('obj/hotseat-check/' + [Guid]::NewGuid().ToString('N'))))
if (-not $runDirectory.StartsWith($repositoryPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw '验收产物目录必须位于本仓库内。'
}
[void][IO.Directory]::CreateDirectory($runDirectory)
$testTemp = Join-Path $runDirectory 'test-temp'
[void][IO.Directory]::CreateDirectory($testTemp)
$utf8 = [Text.UTF8Encoding]::new($false)
$children = [Collections.Generic.List[object]]::new()
$checks = [Collections.Generic.List[string]]::new()

function Assert-Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "$Message 证据目录：$runDirectory" }
}

function Read-Log([string]$Path) {
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
    $reader = [IO.StreamReader]::new($stream, $utf8)
    try { return $reader.ReadToEnd() }
    finally { $reader.Dispose() }
}

function Start-Cli([string]$Name, [string[]]$Parameters, [string]$InputText = '') {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = 'dotnet'
    $start.ArgumentList.Add($cliDll)
    foreach ($parameter in $Parameters) { $start.ArgumentList.Add($parameter) }
    $start.WorkingDirectory = $runDirectory
    $start.Environment['TEMP'] = $testTemp
    $start.Environment['TMP'] = $testTemp
    $start.Environment['DOTNET_GENERATE_ASPNET_CERTIFICATE'] = 'false'
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
        Name = $Name; Parameters = $Parameters; Process = $process; Started = $false
        OutPath = (Join-Path $runDirectory "$Name.stdout.log")
        ErrPath = (Join-Path $runDirectory "$Name.stderr.log")
        OutStream = $null; ErrStream = $null; OutTask = $null; ErrTask = $null
    }
    $children.Add($child)
    $child.OutStream = [IO.FileStream]::new($child.OutPath, [IO.FileMode]::CreateNew,
        [IO.FileAccess]::Write, [IO.FileShare]::ReadWrite, 1, [IO.FileOptions]::Asynchronous)
    $child.ErrStream = [IO.FileStream]::new($child.ErrPath, [IO.FileMode]::CreateNew,
        [IO.FileAccess]::Write, [IO.FileShare]::ReadWrite, 1, [IO.FileOptions]::Asynchronous)
    Assert-Check ($process.Start()) "$Name 无法启动。"
    $child.Started = $true
    $child.OutTask = $process.StandardOutput.BaseStream.CopyToAsync($child.OutStream)
    $child.ErrTask = $process.StandardError.BaseStream.CopyToAsync($child.ErrStream)
    if ($InputText.Length -gt 0) { $process.StandardInput.Write($InputText) }
    $process.StandardInput.Close()
    return $child
}

function Wait-Cli($Child, [int]$ExpectedExit = 0) {
    Assert-Check ($Child.Process.WaitForExit(30000)) "$($Child.Name) 超过 30 秒未退出。"
    Assert-Check ($Child.OutTask.Wait(5000) -and $Child.ErrTask.Wait(5000)) "$($Child.Name) 输出未结束。"
    $Child.OutStream.Flush()
    $Child.ErrStream.Flush()
    $result = [pscustomobject]@{ Out = (Read-Log $Child.OutPath); Err = (Read-Log $Child.ErrPath); Exit = $Child.Process.ExitCode }
    Assert-Check ($result.Exit -eq $ExpectedExit) "$($Child.Name) 应退出 $ExpectedExit，实际 $($result.Exit)。"
    return $result
}

function Invoke-Cli([string]$Name, [string[]]$Parameters, [string]$InputText = '', [int]$ExpectedExit = 0) {
    return Wait-Cli (Start-Cli $Name $Parameters $InputText) $ExpectedExit
}

function Fingerprint([string]$Directory) {
    if (-not (Test-Path -LiteralPath $Directory)) { return '' }
    return (@(Get-ChildItem -LiteralPath $Directory -File -Recurse -Force | Sort-Object FullName | ForEach-Object {
        $_.FullName + ':' + (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    }) -join "`n")
}

function Battle-Lines([string]$Output) {
    $entries = [Collections.Generic.List[string]]::new()
    $node = $null
    $turn = 0
    $plain = [regex]::Replace($Output, '\x1b\[[0-9;]*[A-Za-z]', '')
    foreach ($line in ($plain -split "`n")) {
        if ($line -match '^【第 ([0-9]+) 回合 · ([^】]+)】\r?$') { $turn = [int]$Matches[1]; $node = $Matches[2] }
        elseif ($null -ne $node -and $line.StartsWith('  ')) { $entries.Add("$turn|$node|" + $line.Substring(2).TrimEnd("`r")) }
        else { $node = $null }
    }
    return $entries -join "`n"
}

function Plan-Game([bool]$Ban, [bool]$Lead) {
    $options = [StreamDuel.Core.BattleOptions]::new()
    $options.Seed = 17
    $options.BanOne = $Ban
    $options.LeadPick = $Lead
    $session = [StreamDuel.Core.HotseatSession]::new($db, $teamA, $teamB, '红方', '蓝方', $options)
    $transcript = [Text.StringBuilder]::new()
    $handovers = 0
    $singleReplacements = 0
    $switched = $false
    for ($step = 0; $session.Phase -ne [StreamDuel.Core.Phase]::Over; $step++) {
        Assert-Check ($step -lt 1000) '夹具对局超出合理步骤数。'
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
                if ($session.ViewFor(1 - $side).SwitchTargets.Count -eq 0) { $singleReplacements++ }
                $target = $view.SwitchTargets[0]
                [void]$transcript.Append(($target + 1).ToString() + "`n")
                $session.SubmitReplace($target)
            }
            'Choose' {
                if (-not $switched -and $view.SwitchTargets.Count -gt 0) {
                    $target = $view.SwitchTargets[0]
                    [void]$transcript.Append(($target + 6).ToString() + "`n")
                    $session.Submit([StreamDuel.Core.BattleAction]::SwitchTo($target))
                    $switched = $true
                }
                elseif ($null -ne $view.ControlledReason) {
                    [void]$transcript.Append("0`n")
                    $session.Submit([StreamDuel.Core.BattleAction]::Pass())
                }
                elseif ($view.Struggle) {
                    [void]$transcript.Append("0`n")
                    $action = [StreamDuel.Core.BattleAction]::new()
                    $action.Type = [StreamDuel.Core.ActionType]::Struggle
                    $session.Submit($action)
                }
                else {
                    $skill = @($view.Skills | Where-Object Usable | Sort-Object @{ Expression = 'DamageMax'; Descending = $true }, Index)[0]
                    [void]$transcript.Append(($skill.Index + 1).ToString() + "`n")
                    $session.Submit([StreamDuel.Core.BattleAction]::Skill($skill.Index))
                }
            }
        }
    }
    return [pscustomobject]@{ Input = $transcript.ToString(); Session = $session; Handovers = $handovers; SingleReplacements = $singleReplacements }
}

try {
    $coreDll = Join-Path (Split-Path $cliDll) 'StreamDuel.Core.dll'
    [void][Reflection.Assembly]::LoadFrom($coreDll)
    Assert-Check ([StringComparer]::OrdinalIgnoreCase.Equals(
        [IO.Path]::GetFullPath([StreamDuel.Core.HotseatSession].Assembly.Location),
        [IO.Path]::GetFullPath($coreDll))) '请在未加载其他 Core 的新 PowerShell 进程运行。'
    Assert-Check ($null -ne [StreamDuel.Core.HotseatSession].Assembly.GetType('StreamDuel.Core.ReplayLibrary', $false)) '当前 CLI/Core 二进制尚未包含 T80 ReplayLibrary。'
    $content = Join-Path (Split-Path $cliDll) 'Content'
    $db = [StreamDuel.Core.ContentDb]::FromJson([IO.File]::ReadAllText((Join-Path $content 'StreamDuelRules.json')),
        [IO.File]::ReadAllText((Join-Path $content 'Packs/original.json')))
    [string[]]$teamA = @('bell', 'enka', 'shiori', 'lumi', 'hane', 'momo')
    [string[]]$teamB = @('kei', 'yuan', 'nyako', 'buck', 'kasumi', 'bee')
    $common = @('--hotseat', '--team', ($teamA -join ','), '--team-b', ($teamB -join ','), '--name-a', '红方', '--name-b', '蓝方', '--seed', '17')

    foreach ($kind in @('basic', 'ban-lead')) {
        $selection = $kind -eq 'ban-lead'
        $plan = Plan-Game $selection $selection
        Assert-Check ($plan.Handovers -gt 0 -and $plan.SingleReplacements -gt 0) '完整对局夹具必须覆盖交接和单方补位。'
        $save = Join-Path $runDirectory ("$kind-saves")
        $report = Join-Path $runDirectory ("$kind-report.md")
        $extra = if ($selection) { @('--ban', '--lead') } else { @() }
        $game = Invoke-Cli ("01-$kind") ($common + $extra + @('--share', '--report', $report, '--save-dir', $save)) $plan.Input
        Assert-Check ($game.Err.Length -eq 0 -and $game.Out.Contains('对局结束：')) '热座完整对局有错误或未正常结束。'
        Assert-Check ([regex]::Matches($game.Out, '请把电脑交给「').Count -eq $plan.Handovers) '交接次数与会话流程不同。'
        Assert-Check ([regex]::Matches($game.Out, '\x1b\[2J\x1b\[H\x1b\[3J').Count -eq $plan.Handovers) '每次交接没有清屏并清理滚动记录。'
        $expectedLog = ($plan.Session.Log | ForEach-Object { "$($_.Turn)|$($_.Node)|$($_.Text)" }) -join "`n"
        Assert-Check ((Battle-Lines $game.Out) -ceq $expectedLog) 'CLI 显示日志与会话已公开日志逐字不一致。'
        $records = @(Get-ChildItem -LiteralPath (Join-Path $save 'replays') -File -Filter '*.json' | Where-Object { -not $_.Name.StartsWith('.') })
        Assert-Check ($records.Count -eq 1) '每局必须保存恰好一份回放。'
        $expectedRecord = $plan.Session.Record.ToJson()
        # 先核对 CLI 原始索引，不能先调用 List 重建后再把修复结果当成保存结果。
        $libraryDirectory = Join-Path $save 'replay-library'
        Assert-Check (Test-Path -LiteralPath $libraryDirectory -PathType Container) '完整热座对局没有创建录像库。'
        $libraryFiles = @(Get-ChildItem -LiteralPath $libraryDirectory -File -Filter '*.json' |
            Where-Object { $_.Name -ne 'index.json' -and -not $_.Name.StartsWith('.') })
        Assert-Check ($libraryFiles.Count -eq 1) '每场完整热座对局的新录像库必须恰好一份录像。'
        $libraryId = [IO.Path]::GetFileNameWithoutExtension($libraryFiles[0].Name)
        $libraryText = [IO.File]::ReadAllText($libraryFiles[0].FullName, $utf8)
        Assert-Check ([StreamDuel.Core.BattleRecord]::FromJson($libraryText).ToJson() -ceq $expectedRecord) '新库录像改变了完整 BattleRecord。'
        $libraryMetadata = [StreamDuel.Core.MiniJson]::Parse($libraryText)['library']
        $indexPath = Join-Path $libraryDirectory 'index.json'
        Assert-Check (Test-Path -LiteralPath $indexPath -PathType Leaf) 'CLI 保存后缺少录像库索引。'
        $indexText = [IO.File]::ReadAllText($indexPath, $utf8)
        $indexRoot = [StreamDuel.Core.MiniJson]::Parse($indexText)
        Assert-Check ($indexRoot['version'] -eq 1 -and $indexRoot['entries'].Count -eq 1) '新库索引必须只包含本局条目。'
        $indexEntry = $indexRoot['entries'][0]
        $expectedSummary = [StreamDuel.Core.BattleSummary]::From($plan.Session.CloneCompletedBattle())
        foreach ($metadata in @($libraryMetadata, $indexEntry)) {
            Assert-Check ($metadata['version'] -eq 1 -and $metadata['id'] -ceq $libraryId -and
                $metadata['winner'] -eq $plan.Session.Winner -and $metadata['turns'] -eq $expectedSummary.Turns -and
                $null -eq $metadata['note']) '录像文件或索引的版本、编号、胜方、回合数或备注错误。'
            $date = [DateTimeOffset]::ParseExact($metadata['date'], 'O',
                [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::None)
            Assert-Check ($date.Offset -eq [TimeSpan]::Zero) '录像库日期没有按 UTC 保存。'
        }
        Assert-Check ($indexEntry['date'] -ceq $libraryMetadata['date'] -and
            $indexEntry['packId'] -ceq $plan.Session.Record.PackId -and $indexEntry['isCorrupt'] -eq $false -and
            $null -eq $indexEntry['error']) '索引日期、内容包或正常条目标记与本局不一致。'
        Assert-Check ($indexEntry['names'].Count -eq 2 -and $indexEntry['teams'].Count -eq 2) '索引没有保存双方名字和原始阵容。'
        for ($side = 0; $side -lt 2; $side++) {
            Assert-Check ($indexEntry['names'][$side] -ceq $plan.Session.Record.Names[$side]) '索引双方姓名顺序错误。'
            $expectedTeam = $plan.Session.Record.Teams[$side]
            Assert-Check ($indexEntry['teams'][$side].Count -eq $expectedTeam.Count) '索引丢失了禁用或首发重排前的原始成员。'
            for ($slot = 0; $slot -lt $expectedTeam.Count; $slot++) {
                Assert-Check ($indexEntry['teams'][$side][$slot] -ceq $expectedTeam[$slot]) '索引原始队伍成员或顺序错误。'
            }
        }
        $library = [StreamDuel.Core.ReplayLibrary]::new($libraryDirectory, 200, $null)
        Assert-Check ($library.Load($libraryId).ToJson() -ceq $expectedRecord) '生产 ReplayLibrary.Load 未恢复本局完整记录。'
        Assert-Check ([IO.File]::ReadAllText($indexPath, $utf8) -ceq $indexText) '只读加载改写了已保存索引。'
        $checks.Add("$kind 完整局的新录像库恰一份；原生记录、索引元数据和原始阵容一致，生产 Load 往返正确")
        Assert-Check ([IO.File]::ReadAllText($records[0].FullName) -ceq $expectedRecord) 'CLI 录像与相同选择序列不一致。'
        Assert-Check ([IO.File]::ReadAllText($report) -ceq [StreamDuel.Core.BattleReport]::Markdown($plan.Session.CloneCompletedBattle())) '终局战报与会话快照不一致。'
        $code = [regex]::Match($game.Out, '(?m)^分享码：(SD1-[A-Za-z0-9_-]+)\r?$')
        Assert-Check $code.Success '完整对局缺少分享码。'
        Assert-Check ([StreamDuel.Core.ShareCode]::Decode($code.Groups[1].Value).ToJson() -ceq $expectedRecord) '分享码不是本场完整记录。'
        $profile = [IO.File]::ReadAllText((Join-Path $save 'profile.json')) | ConvertFrom-Json
        $stats = $profile.packs.original
        Assert-Check (($stats.wins + $stats.losses + $stats.draws) -eq 1) '双人对局被重复计入同一档案。'
        $expectedStat = if ($plan.Session.Winner -lt 0) { 'draws' } elseif ($plan.Session.Winner -eq 0) { 'wins' } else { 'losses' }
        Assert-Check ($stats.$expectedStat -eq 1) '档案没有按 A 方胜负累计。'
        $before = Fingerprint $save
        $replay = Invoke-Cli ("02-$kind-replay") @('--replay', $records[0].FullName, '--auto', '--save-dir', $save)
        Assert-Check ((Battle-Lines $replay.Out) -ceq $expectedLog -and (Fingerprint $save) -ceq $before) '热座录像无法完整回看或回看写入了存档。'
        $checks.Add("$kind 完整局：私密轮流、主动换人、单方补位、清屏次数、公开日志、录像、战报、分享与 A 方一次战绩")
    }

    foreach ($kind in @('action', 'ban', 'lead')) {
        $save = Join-Path $runDirectory ("private-$kind-saves")
        $extra = switch ($kind) { 'action' { @() }; 'ban' { @('--ban') }; 'lead' { @('--lead') } }
        $first = Invoke-Cli ("03-$kind-one") ($common + $extra + @('--save-dir', $save)) "1`nq`n"
        $second = Invoke-Cli ("03-$kind-two") ($common + $extra + @('--save-dir', $save)) "2`nq`n"
        Assert-Check ($first.Out -ceq $second.Out -and $first.Err -ceq $second.Err) '先提交的不同选择在交接确认前泄露到输出。'
        Assert-Check ($first.Out.Contains('接手后按回车确认') -and -not $first.Out.Contains('蓝方 操作 =====')) '下一位未经确认就看到选择界面。'
        Assert-Check (-not (Test-Path -LiteralPath $save)) '交接中退出仍保存了对局。'
    }
    $checks.Add('行动、禁用、首发不同首选在交接确认前输出完全一致，退出不存档')

    $retrySave = Join-Path $runDirectory 'retry-saves'
    $retry = Invoke-Cli '04-retry' ($common + @('--save-dir', $retrySave)) "PRIVATE_INPUT_SENTINEL`n999`n1`nPRIVATE_CONFIRM_SENTINEL`n`nq`n"
    Assert-Check (-not $retry.Out.Contains('PRIVATE_INPUT_SENTINEL') -and -not $retry.Out.Contains('PRIVATE_CONFIRM_SENTINEL')) '私密输入被回显。'
    Assert-Check ($retry.Out.Contains('请输入列表中的数字') -and $retry.Out.Contains('该选项当前不可用') -and
        $retry.Out.Contains('请交给下一位玩家，再直接按回车确认。') -and $retry.Out.Contains('蓝方 操作 =====')) '非法输入/交接确认没有正确重试。'
    Assert-Check (-not (Test-Path -LiteralPath $retrySave)) '重试后退出写入了存档。'
    foreach ($inputText in @('', "q`n", "1`n")) {
        $save = Join-Path $runDirectory ('quit-' + [Guid]::NewGuid().ToString('N'))
        $quit = Invoke-Cli ('05-quit-' + [Guid]::NewGuid().ToString('N')) ($common + @('--save-dir', $save, '--share', '--report', (Join-Path $save 'report.md'))) $inputText
        Assert-Check ($quit.Out.Contains('已退出，对局未完成。') -and -not $quit.Out.Contains('分享码：') -and
            -not (Test-Path -LiteralPath $save)) 'q/EOF 仍导出或保存未完成对局。'
    }
    $checks.Add('私密输入不回显、非法值和交接重试、q/EOF 不导出未完成结果')

    $invalidCases = @(
        @{ Name = 'auto'; Args = @('--hotseat', '--auto') },
        @{ Name = 'level'; Args = @('--hotseat', '--level', 'normal') },
        @{ Name = 'timer'; Args = @('--hotseat', '--timer', '1,1') },
        @{ Name = 'hash'; Args = @('--hotseat', '--hash') },
        @{ Name = 'host'; Args = @('--hotseat', '--host', '0') },
        @{ Name = 'join'; Args = @('--hotseat', '--join', '127.0.0.1:1') },
        @{ Name = 'lan'; Args = @('--hotseat', '--lan-list') },
        @{ Name = 'replay'; Args = @('--hotseat', '--replay', 'missing.json') },
        @{ Name = 'code'; Args = @('--hotseat', '--replay-code', 'bad') },
        @{ Name = 'list'; Args = @('--hotseat', '--list-packs') },
        @{ Name = 'calc'; Args = @('--hotseat', '--calc', 'bell', '1', 'kei') },
        @{ Name = 'team-b-only'; Args = @('--team-b', ($teamB -join ',')) },
        @{ Name = 'name-only'; Args = @('--name-a', '甲') },
        @{ Name = 'empty-name'; Args = @('--hotseat', '--name-b', ' ') },
        @{ Name = 'control-name'; Args = @('--hotseat', '--name-a', "甲`n乙") },
        @{ Name = 'bad-team'; Args = @('--hotseat', '--team-b', 'missing,missing,missing,missing,missing,missing', '--export-team') },
        @{ Name = 'repeat'; Args = @('--hotseat', '--hotseat') }
    )
    foreach ($case in $invalidCases) {
        $save = Join-Path $runDirectory ("invalid-$($case.Name)-saves")
        $rejected = Invoke-Cli ("06-$($case.Name)") ($case.Args + @('--save-dir', $save)) '' 1
        Assert-Check ($rejected.Err.StartsWith('错误：') -and -not $rejected.Out.Contains('同屏双人对战') -and
            -not $rejected.Out.Contains('本方队伍码：') -and -not (Test-Path -LiteralPath $save)) '错误组合未在开始/导出/存档前拒绝。'
    }
    $checks.Add('冲突模式、无效队伍/名称、专属参数误用和重复标记提前中文拒绝')

    foreach ($case in @(
        @{ Name = 'glossary'; Option = '--glossary'; Args = @('--glossary', '状态') },
        @{ Name = 'replays'; Option = '--replays'; Args = @('--replays') }
    )) {
        foreach ($order in @('hotseat-first', 'hotseat-last')) {
            $caseName = "09-main-conflict-$($case.Name)-$order"
            $save = Join-Path $runDirectory ($caseName + '-saves')
            Assert-Check (-not (Test-Path -LiteralPath $save)) '互斥用例的存档目录已经存在。'
            $parameters = if ($order -eq 'hotseat-first') { @('--hotseat') + $case.Args }
                else { $case.Args + @('--hotseat') }
            $rejected = Invoke-Cli $caseName ($parameters + @('--save-dir', $save)) '' 1
            Assert-Check ($rejected.Out.Length -eq 0 -and $rejected.Err.StartsWith('错误：') -and
                $rejected.Err -notmatch '未知参数|缺少参数' -and
                ($rejected.Err.Contains('--hotseat') -or $rejected.Err.Contains($case.Option))) '新主线入口没有在进入模式前按互斥规则拒绝。'
            Assert-Check (-not (Test-Path -LiteralPath $save)) '新主线入口互斥失败创建了存档或录像库目录。'
        }
    }
    $checks.Add('glossary/replays 与 hotseat 双顺序互斥：4 例退出 1、无 stdout、无新存档或录像库')

    $preset = [StreamDuel.Core.TeamPreset]::new()
    $preset.Name = '热座原始预设'
    $preset.PackId = 'original'
    $preset.Ids.AddRange($teamA)
    $teamCode = [StreamDuel.Core.TeamCode]::Encode($preset)
    $importSave = Join-Path $runDirectory 'import-saves'
    $imported = Invoke-Cli '07-team-code' @('--hotseat', '--team-code', $teamCode, '--export-team', '--ban', '--lead', '--save-dir', $importSave) "q`n"
    Assert-Check ($imported.Out.Contains('本方队伍码：' + $teamCode) -and $imported.Out.Contains('玩家 A的队伍：') -and
        $imported.Out.Contains('玩家 B的队伍：') -and -not (Test-Path -LiteralPath $importSave)) '队伍码、默认名称/队伍或禁用前导出不兼容。'
    foreach ($member in $teamB) { Assert-Check ($imported.Out.Contains($member)) '默认 B 方队伍没有先选 A 方之外的主播。' }
    $checks.Add('A 方队伍码和名称保留、默认双方名称/B 队、开局禁用前导出兼容')

    $help = Invoke-Cli '08-help' @('--help')
    Assert-Check ($help.Out.Contains('--hotseat') -and $help.Out.Contains('--team-b') -and $help.Out.Contains('--name-a') -and
        $help.Out.Contains('私密') -and $help.Out.Contains('A 方累计一次')) '帮助缺少热座玩法或档案说明。'
    $checks.Add('帮助说明交接、私密输入、参数与 A 方档案语义')
    [IO.File]::WriteAllText((Join-Path $runDirectory 'report.json'),
        (@{ status = 'PASS'; checks = $checks.ToArray() } | ConvertTo-Json -Depth 5), $utf8)
    Write-Output "PASS：$($checks.Count) 组同屏双人 CLI 检查；证据：$runDirectory"
}
catch {
    [IO.File]::WriteAllText((Join-Path $runDirectory 'report.json'),
        (@{ status = 'FAIL'; error = $_.Exception.Message; checks = $checks.ToArray() } | ConvertTo-Json -Depth 5), $utf8)
    throw
}
finally {
    foreach ($child in $children) {
        if ($child.Started -and -not $child.Process.HasExited) { $child.Process.Kill($true); $child.Process.WaitForExit(5000) | Out-Null }
        if ($null -ne $child.OutTask) { [void]$child.OutTask.Wait(5000) }
        if ($null -ne $child.ErrTask) { [void]$child.ErrTask.Wait(5000) }
        if ($null -ne $child.OutStream) { $child.OutStream.Dispose() }
        if ($null -ne $child.ErrStream) { $child.ErrStream.Dispose() }
        $child.Process.Dispose()
    }
}
