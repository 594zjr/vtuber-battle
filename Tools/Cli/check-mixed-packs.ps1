#requires -Version 7.0
param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$repositoryPrefix = $repository.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$current = [IO.Path]::GetFullPath((Get-Location).Path)
if ($current -ne $repository -and -not $current.StartsWith($repositoryPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw '请在本仓库内运行此脚本。'
}
$binaryDirectory = Join-Path $PSScriptRoot "bin/$Configuration/net8.0"
$cliDll = Join-Path $binaryDirectory 'Cli.dll'
$coreDll = Join-Path $binaryDirectory 'StreamDuel.Core.dll'
$builtCliDll = $cliDll
foreach ($path in @($cliDll, $coreDll)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "请先构建 Tools/Cli -c $Configuration。" }
}
$runDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ('obj/mixed-packs-check/' + [Guid]::NewGuid().ToString('N'))))
if (-not $runDirectory.StartsWith($repositoryPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw '产物目录越界。' }
[void][IO.Directory]::CreateDirectory($runDirectory)
$testTemp = Join-Path $runDirectory 'test-temp'
[void][IO.Directory]::CreateDirectory($testTemp)
$utf8 = [Text.UTF8Encoding]::new($false)
$children = [Collections.Generic.List[object]]::new()
$checks = [Collections.Generic.List[string]]::new()
$failure = $null
function Assert-Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "$Message 证据目录：$runDirectory" }
}

function Read-Log([string]$Path) {
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
    $reader = [IO.StreamReader]::new($stream, $utf8)
    try { return $reader.ReadToEnd() }
    finally { $reader.Dispose() }
}

function Start-Cli([string]$Name, [string[]]$Parameters) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = 'dotnet'
    $start.ArgumentList.Add($cliDll)
    foreach ($parameter in $Parameters) { $start.ArgumentList.Add($parameter) }
    $start.WorkingDirectory = $runDirectory
    $start.Environment['TEMP'] = $testTemp
    $start.Environment['TMP'] = $testTemp
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
        Result = $null; Killed = $false; CleanupError = $null
    }
    $children.Add($child)
    $child.OutStream = [IO.FileStream]::new($child.OutPath, [IO.FileMode]::CreateNew,
        [IO.FileAccess]::Write, [IO.FileShare]::ReadWrite, 1, [IO.FileOptions]::Asynchronous)
    $child.ErrStream = [IO.FileStream]::new($child.ErrPath, [IO.FileMode]::CreateNew,
        [IO.FileAccess]::Write, [IO.FileShare]::ReadWrite, 1, [IO.FileOptions]::Asynchronous)
    Assert-Check ($process.Start()) "$Name 无法启动。"
    $child.Started = $true
    # 异步复制原始流能看到没有换行的输入提示，也不依赖 PowerShell 事件回调的 runspace。
    $child.OutTask = $process.StandardOutput.BaseStream.CopyToAsync($child.OutStream)
    $child.ErrTask = $process.StandardError.BaseStream.CopyToAsync($child.ErrStream)
    # stdin 必须保持打开，才能证明等待人工输入时无需 EOF 或回车也会响应断线。
    return $child
}

function Wait-Output($Child, [string]$Pattern, [int]$TimeoutMs = 15000) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    while ($timer.ElapsedMilliseconds -lt $TimeoutMs) {
        $match = [regex]::Match((Read-Log $Child.OutPath), $Pattern)
        if ($match.Success) { return $match }
        Assert-Check (-not $Child.Process.HasExited) "$($Child.Name) 在出现预期输出前退出，请检查 stdout/stderr 日志。"
        Start-Sleep -Milliseconds 25
    }
    throw "$($Child.Name) 等待输出超时。证据目录：$runDirectory"
}

function Wait-Cli($Child, [int]$ExpectedExit, [int]$TimeoutMs = 60000) {
    if ($null -eq $Child.Result) {
        $timer = [Diagnostics.Stopwatch]::StartNew()
        while (-not $Child.Process.HasExited -and $timer.ElapsedMilliseconds -lt $TimeoutMs) {
            Start-Sleep -Milliseconds 25
        }
        Assert-Check ($Child.Process.HasExited) "$($Child.Name) 超过 $TimeoutMs 毫秒仍未退出。"
        Assert-Check ($Child.OutTask.Wait(5000)) "$($Child.Name) 的标准输出未结束。"
        Assert-Check ($Child.ErrTask.Wait(5000)) "$($Child.Name) 的标准错误未结束。"
        $Child.OutStream.Flush()
        $Child.ErrStream.Flush()
        $Child.Result = [pscustomobject]@{
            ExitCode = $Child.Process.ExitCode
            Out = (Read-Log $Child.OutPath)
            Err = (Read-Log $Child.ErrPath)
        }
    }
    Assert-Check ($Child.Result.ExitCode -eq $ExpectedExit) "$($Child.Name) 退出码应为 $ExpectedExit，实际为 $($Child.Result.ExitCode)。"
    return $Child.Result
}


function Run-Cli([string]$Name, [string[]]$Parameters, [string]$InputText = '', [int]$Exit = 0) {
    $save = Join-Path $runDirectory ($Name + '-saves')
    $child = Start-Cli $Name ($Parameters + @('--save-dir', $save))
    if ($InputText.Length -gt 0) { $child.Process.StandardInput.Write($InputText) }
    $child.Process.StandardInput.Close()
    return Wait-Cli $child $Exit
}
function Extract-Code([string]$Output, [string]$Prefix, [string]$Label) {
    $matches = [regex]::Matches($Output, '(?m)^' + [regex]::Escape($Label) + '(' + $Prefix + '[A-Za-z0-9_-]+)\r?$')
    Assert-Check ($matches.Count -eq 1) "缺少唯一的 $Label"
    return $matches[0].Groups[1].Value
}
function Read-Record([string]$Name) {
    $files = @(Get-ChildItem -LiteralPath (Join-Path $runDirectory "$Name-saves/replays") -File -Filter '*.json')
    Assert-Check ($files.Count -eq 1) "$Name 没有恰好一份回放。"
    return [pscustomobject]@{ File = $files[0].FullName; Record = [StreamDuel.Core.BattleRecord]::FromJson([IO.File]::ReadAllText($files[0].FullName)) }
}
function Assert-NoSave([string]$Name) {
    Assert-Check (-not (Test-Path -LiteralPath (Join-Path $runDirectory ($Name + '-saves')))) "$Name 不应保存未完成对局。"
}
function Reject([string]$Name, [string[]]$Parameters) {
    $result = Run-Cli $Name $Parameters '' 1
    Assert-Check ($result.Err -match '[\u4e00-\u9fff]') "$Name 没有中文错误。"
    Assert-NoSave $Name
}
function Copy-PolicyDeployment([string]$Source, [string]$Destination) {
    $sourceFull = [IO.Path]::GetFullPath($Source)
    $destFull = [IO.Path]::GetFullPath($Destination)
    Assert-Check ($sourceFull.StartsWith($repositoryPrefix, [StringComparison]::OrdinalIgnoreCase) -and
        $destFull.StartsWith($runDirectory + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) '政策验收副本路径越界。'
    $item = Get-Item -LiteralPath $sourceFull
    Assert-Check (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) '政策验收不复制重解析点。'
    if ($item.PSIsContainer) {
        [void][IO.Directory]::CreateDirectory($destFull)
        foreach ($child in Get-ChildItem -LiteralPath $sourceFull -Force) {
            Copy-PolicyDeployment $child.FullName (Join-Path $destFull $child.Name)
        }
    }
    else {
        [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destFull))
        [IO.File]::Copy($sourceFull, $destFull, $false)
    }
}
function Set-TestPolicy([string[]]$DisabledPacks, [string[]]$DisabledStreamers, [string]$Reason) {
    $reasons = [ordered]@{}
    foreach ($id in @($DisabledPacks) + @($DisabledStreamers)) { $reasons[$id] = $Reason }
    $json = [ordered]@{ disabledPacks = @($DisabledPacks); disabledStreamers = @($DisabledStreamers); reason = $reasons } | ConvertTo-Json -Depth 5
    [IO.File]::WriteAllText($policyPath, $json, $utf8)
}
function Reject-Policy([string]$Name, [string[]]$Parameters, [string]$Reason) {
    $report = Join-Path $runDirectory ($Name + '.md')
    $result = Run-Cli $Name ($Parameters + @('--report', $report)) '' 1
    Assert-Check ($result.Err.Contains($Reason) -and -not $result.Err.Contains('找不到混合内容包成员')) "$Name 没有明确的下架原因。"
    Assert-Check (-not $result.Out.Contains('本方队伍码：') -and -not $result.Out.Contains('分享码：') -and
        -not $result.Out.Contains('对局结束：') -and -not $result.Out.Contains('回看结束：')) "$Name 在拒绝前导出或完成了对局。"
    Assert-Check (-not (Test-Path -LiteralPath $report)) "$Name 下架拒绝仍生成战报。"
    Assert-NoSave $Name
}
function Final-Hash([string]$Output) {
    $found = [regex]::Matches($Output, '(?m)^最终状态哈希：([0-9a-f]{16})\r?$')
    Assert-Check ($found.Count -eq 1) '应恰好输出一份最终联网哈希。'
    return $found[0].Groups[1].Value
}

try {
    $assembly = [Reflection.Assembly]::LoadFrom($coreDll)
    Assert-Check ([IO.Path]::GetFullPath($assembly.Location) -eq [IO.Path]::GetFullPath($coreDll)) '请在新 PowerShell 进程中运行。'
    $rules = [IO.File]::ReadAllText((Join-Path $binaryDirectory 'Content/StreamDuelRules.json'))
    $originalJson = [IO.File]::ReadAllText((Join-Path $binaryDirectory 'Content/Packs/original.json'))
    $fanJson = [IO.File]::ReadAllText((Join-Path $binaryDirectory 'Content/Packs/fan_asoul_vr.json'))
    $original = [StreamDuel.Core.ContentDb]::FromJson($rules, $originalJson)
    $fan = [StreamDuel.Core.ContentDb]::FromJson($rules, $fanJson)
    $db = [StreamDuel.Core.ContentDb]::Merge([StreamDuel.Core.ContentDb[]]@($original, $fan))
    Assert-Check ($db.PackId -ceq 'mix:fan_asoul_vr,original') '组合编号没有排序。'
    $teamA = 'bell,enka,shiori,bella,diana,eileen'
    $teamB = 'kei,yuan,nyako,nana7mi,koichi,aza'
    $manual = Run-Cli 'manual' @('--packs', 'original,fan_asoul_vr', '--team', $teamA, '--team-b', $teamB,
        '--auto', '--seed', '7', '--share', '--export-team', '--report', (Join-Path $runDirectory 'manual.md'))
    Assert-Check ($manual.Err.Length -eq 0) '手动混合阵容产生错误。'
    $saved = Read-Record 'manual'
    Assert-Check ($saved.Record.PackId -ceq $db.PackId -and ($saved.Record.Teams[0] -join ',') -ceq $teamA -and
        ($saved.Record.Teams[1] -join ',') -ceq $teamB) '手动双方阵容或组合编号没有保留。'
    foreach ($id in ($teamA + ',' + $teamB).Split(',')) {
        Assert-Check ($manual.Out.Contains('（' + $id + '）：' + $db.NoticeFor($id))) "缺少 $id 来源声明。"
    }
    Assert-Check ($manual.Out.Contains($original.PackNotice) -and $manual.Out.Contains($fan.PackNotice)) '聚合声明丢失。'
    $expectedBattle = [StreamDuel.Core.Replay]::Run($db, $saved.Record)
    Assert-Check ($expectedBattle.Phase -eq [StreamDuel.Core.Phase]::Over) '手动混合对局未打完。'
    $teamCode = Extract-Code $manual.Out 'SDT1-' '本方队伍码：'
    $share = Extract-Code $manual.Out 'SD1-' '分享码：'
    $import = Run-Cli 'team-import' @('--team-code', $teamCode, '--export-team') "q`n"
    Assert-Check ((Extract-Code $import.Out 'SDT1-' '本方队伍码：') -ceq $teamCode -and $import.Err.Length -eq 0) '混合队伍码不能自动重建内容包。'
    Assert-NoSave 'team-import'
    $replay = Run-Cli 'replay-file' @('--replay', $saved.File, '--auto', '--report', (Join-Path $runDirectory 'replay.md'))
    $replayCode = Run-Cli 'replay-code' @('--replay-code', $share, '--auto')
    Assert-Check ($replay.Err.Length -eq 0 -and $replayCode.Err.Length -eq 0) '混合回放或分享码加载失败。'
    Assert-Check ([IO.File]::ReadAllText((Join-Path $runDirectory 'manual.md')) -ceq
        [IO.File]::ReadAllText((Join-Path $runDirectory 'replay.md'))) '重放报告与原始报告不一致。'
    Assert-NoSave 'replay-file'; Assert-NoSave 'replay-code'
    $checks.Add('手动混合12人完整对局、逐人及聚合声明、队伍码导入、文件/分享码回看、战报一致且回看不保存')

    $singlePreset = [StreamDuel.Core.TeamPreset]::new()
    $singlePreset.PackId = $original.PackId
    $singlePreset.Ids = [Collections.Generic.List[string]]::new([string[]]@($original.Streamers | Select-Object -First 6 -ExpandProperty Id))
    $ordinaryCode = [StreamDuel.Core.TeamCode]::Encode($singlePreset)
    for ($order = 0; $order -lt 2; $order++) {
        $arguments = if ($order -eq 0) { @('--team-code', $teamCode, '--team-b', $teamB) }
            else { @('--team-b', $teamB, '--team-code', $teamCode) }
        $name = 'mixed-code-opponent-' + $order
        $importedMatch = Run-Cli $name ($arguments + @('--auto', '--seed', '7'))
        $importedRecord = (Read-Record $name).Record
        Assert-Check ($importedMatch.Err.Length -eq 0 -and $importedRecord.PackId -ceq $db.PackId -and
            ($importedRecord.Teams[0] -join ',') -ceq $teamA -and ($importedRecord.Teams[1] -join ',') -ceq $teamB) '混合队伍码自动选包后未保留双方手动阵容。'
        Assert-Check ($importedRecord.ToJson() -ceq $saved.Record.ToJson()) '混合队伍码与显式混合组队的相同种子录像不一致。'
        $invalidArguments = if ($order -eq 0) { @('--team-code', $ordinaryCode, '--team-b', $teamB) }
            else { @('--team-b', $teamB, '--team-code', $ordinaryCode) }
        Reject ('single-code-opponent-' + $order) ($invalidArguments + @('--auto'))
    }
    $checks.Add('混合队伍码自动选包可同时指定电脑队伍，两种参数顺序与显式组队录像一致；普通单包码仍拒绝且不存档')

    $single = Run-Cli 'single-mix' @('--pack', 'mix:original', '--random-teams', '--seed', '13', '--auto', '--export-team')
    Assert-Check ($single.Err.Length -eq 0) '单源 mix:original 不能新建随机对局。'
    $singleSaved = Read-Record 'single-mix'
    Assert-Check ($singleSaved.Record.PackId -ceq 'mix:original') '单源混合编号改变。'
    $singleCode = Extract-Code $single.Out 'SDT1-' '本方队伍码：'
    $singleImport = Run-Cli 'single-mix-import' @('--team-code', $singleCode, '--export-team') "q`n"
    $singleReplay = Run-Cli 'single-mix-replay' @('--replay', $singleSaved.File, '--auto')
    Assert-Check ($singleImport.Err.Length -eq 0 -and $singleReplay.Err.Length -eq 0 -and
        (Extract-Code $singleImport.Out 'SDT1-' '本方队伍码：') -ceq $singleCode) '单源混合队伍码或录像无法重建。'
    Assert-NoSave 'single-mix-import'; Assert-NoSave 'single-mix-replay'
    $checks.Add('Core合法的单源mix:original随机完整对局、队伍码和回放自动重建')

    $expected = [StreamDuel.Core.MixedBattle]::RandomTeams($db, [uint32]17)
    $first = Run-Cli 'random-a' @('--packs', 'original,fan_asoul_vr', '--random-teams', '--auto', '--seed', '17')
    $second = Run-Cli 'random-b' @('--packs', 'fan_asoul_vr,original', '--random-teams', '--auto', '--seed', '17')
    $recordA = (Read-Record 'random-a').Record
    $recordB = (Read-Record 'random-b').Record
    Assert-Check ($first.Err.Length -eq 0 -and $second.Err.Length -eq 0 -and $recordA.ToJson() -ceq $recordB.ToJson()) '输入包顺序改变了随机对局。'
    for ($side = 0; $side -lt 2; $side++) {
        Assert-Check (($recordA.Teams[$side] -join ',') -ceq ($expected[$side] -join ',')) '随机阵容与 Core API 不符。'
    }
    Assert-Check (@(@($recordA.Teams[0]) + @($recordA.Teams[1]) | Sort-Object -Unique).Count -eq 12) '随机双方存在重复主播。'
    $checks.Add('随机双方与 Core 相同、十二人互不重复、反转包顺序后完整录像逐字相同')

    $hsSeed = [uint32]17
    $hsTeams = [StreamDuel.Core.MixedBattle]::RandomTeams($db, $hsSeed)
    $hsIds = @($hsTeams[0]) + @($hsTeams[1])
    Assert-Check ($hsTeams.Count -eq 2 -and $hsTeams[0].Length -eq 6 -and $hsTeams[1].Length -eq 6 -and
        @($hsIds | Sort-Object -Unique).Count -eq 12) '同屏夹具必须是双方各六位且十二人不重复。'
    $hsOriginalIds = @($original.Streamers | ForEach-Object Id)
    $hsFanIds = @($fan.Streamers | ForEach-Object Id)
    Assert-Check (@($hsIds | Where-Object { $hsOriginalIds -ccontains $_ }).Count -gt 0 -and
        @($hsIds | Where-Object { $hsFanIds -ccontains $_ }).Count -gt 0) '同屏夹具必须实际选到原创与同人两个来源。'
    $hsOptions = [StreamDuel.Core.BattleOptions]::new()
    $hsOptions.Seed = $hsSeed
    $hsOptions.BanOne = $true
    $hsSession = [StreamDuel.Core.HotseatSession]::new($db, $hsTeams[0], $hsTeams[1], '混合甲', '混合乙', $hsOptions)
    $hsSession.SubmitBan(0)
    Assert-Check ($hsSession.NeedsHandover -and $hsSession.WhoseTurn -eq 1) '夹具未进入 B 方交接。'
    $hsSession.ConfirmHandover()
    $hsSession.Surrender()
    $hsBattle = $hsSession.CloneCompletedBattle()
    Assert-Check ($hsBattle.Phase -eq [StreamDuel.Core.Phase]::Over -and $hsBattle.Winner -eq 0) '夹具必须由 B 方投降、A 方获胜。'
    $hsExpectedRecord = $hsSession.Record.ToJson()
    $hsSave = Join-Path $runDirectory 'mixed-hotseat-saves'
    $hsReport = Join-Path $runDirectory 'mixed-hotseat.md'
    Assert-Check (-not (Test-Path -LiteralPath $hsSave)) '同屏验收必须从全新存档目录开始。'
    $hsGame = Run-Cli 'mixed-hotseat' @('--hotseat', '--packs', 'original,fan_asoul_vr', '--random-teams',
        '--seed', '17', '--ban', '--name-a', '混合甲', '--name-b', '混合乙', '--share', '--report', $hsReport) "1`n`n/surrender`n"
    Assert-Check ($hsGame.Err.Length -eq 0 -and $hsGame.Out.Contains('同屏双人对战') -and
        $hsGame.Out.Contains('对局结束：混合甲 获胜') -and
        [regex]::Matches($hsGame.Out, '请把电脑交给「混合乙」').Count -eq 1 -and
        [regex]::Matches($hsGame.Out, '\x1b\[2J\x1b\[H\x1b\[3J').Count -eq 1) '混合同屏未经过一次真实交接、清屏及投降结算。'
    $hsSaved = Read-Record 'mixed-hotseat'
    Assert-Check ([IO.File]::ReadAllText($hsSaved.File, $utf8) -ceq $hsExpectedRecord -and
        $hsSaved.Record.PackId -ceq $db.PackId) '混合同屏原始录像与相同 Core 选择序列不一致。'
    for ($side = 0; $side -lt 2; $side++) {
        Assert-Check (($hsSaved.Record.Teams[$side] -join ',') -ceq ($hsTeams[$side] -join ',')) '混合同屏未保留双方随机的原始六人及顺序。'
    }
    foreach ($id in $hsIds) {
        Assert-Check ($hsGame.Out.Contains('（' + $id + '）：' + $db.NoticeFor($id))) "混合同屏缺少 $id 的来源声明。"
    }
    $hsShare = Extract-Code $hsGame.Out 'SD1-' '分享码：'
    Assert-Check ([StreamDuel.Core.ShareCode]::Decode($hsShare).ToJson() -ceq $hsExpectedRecord) '混合同屏分享码改变了原始记录。'
    $hsReplay = [StreamDuel.Core.Replay]::Run($db, $hsSaved.Record)
    Assert-Check ($hsReplay.Phase -eq [StreamDuel.Core.Phase]::Over -and $hsReplay.Winner -eq 0 -and
        $hsReplay.StateHash() -eq $hsBattle.StateHash()) '混合同屏投降记录无法重放至同一终局状态。'
    $hsExpectedMarkdown = [StreamDuel.Core.BattleReport]::Markdown($hsBattle)
    Assert-Check ([IO.File]::ReadAllText($hsReport, $utf8) -ceq $hsExpectedMarkdown) '混合同屏战报与 Core 终局不一致。'

    # 直接检查 CLI 保存的文件与原生索引，不先调用 List 修补索引。
    $hsLibraryDirectory = Join-Path $hsSave 'replay-library'
    $hsLibraryFiles = @(Get-ChildItem -LiteralPath $hsLibraryDirectory -File -Filter '*.json' |
        Where-Object { $_.Name -ne 'index.json' -and -not $_.Name.StartsWith('.') })
    Assert-Check ($hsLibraryFiles.Count -eq 1) '混合同屏新录像库必须恰好保存一份录像。'
    Assert-Check ([StreamDuel.Core.BattleRecord]::FromJson([IO.File]::ReadAllText($hsLibraryFiles[0].FullName, $utf8)).ToJson() -ceq
        $hsExpectedRecord) '混合同屏新录像库改变了原始记录。'
    $hsIndex = [StreamDuel.Core.MiniJson]::Parse([IO.File]::ReadAllText((Join-Path $hsLibraryDirectory 'index.json'), $utf8))
    Assert-Check ($hsIndex['version'] -eq 1 -and $hsIndex['entries'].Count -eq 1) '混合同屏原生索引缺少唯一条目。'
    $hsEntry = $hsIndex['entries'][0]
    Assert-Check ($hsEntry['id'] -ceq [IO.Path]::GetFileNameWithoutExtension($hsLibraryFiles[0].Name) -and
        $hsEntry['packId'] -ceq $db.PackId -and $hsEntry['winner'] -eq 0 -and
        $hsEntry['isCorrupt'] -eq $false -and $null -eq $hsEntry['error']) '混合同屏索引未正确记录混合包和胜方。'
    $hsProfile = [StreamDuel.Core.MiniJson]::Parse([IO.File]::ReadAllText((Join-Path $hsSave 'profile.json'), $utf8))
    Assert-Check ($hsProfile['packs'].Count -eq 1 -and $hsProfile['packs'].ContainsKey($db.PackId)) '混合同屏档案没有使用唯一混合包编号。'
    $hsStats = $hsProfile['packs'][$db.PackId]
    Assert-Check ($hsStats['wins'] -eq 1 -and $hsStats['losses'] -eq 0 -and $hsStats['draws'] -eq 0 -and
        $hsStats['picks'].Count -eq 6 -and $hsStats['pickWins'].Count -eq 6) '混合同屏必须只按 A 方累计一次胜场和六人选用。'
    foreach ($id in $hsTeams[0]) {
        Assert-Check ($hsStats['picks'][$id] -eq 1 -and $hsStats['pickWins'][$id] -eq 1) '混合同屏主播选用或胜场重复累计。'
    }

    $hsFingerprint = {
        (@(Get-ChildItem -LiteralPath $hsSave -File -Recurse -Force | Sort-Object FullName | ForEach-Object {
            $_.FullName + ':' + (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        }) -join "`n")
    }
    $hsBeforeReplay = & $hsFingerprint
    foreach ($hsKind in @('file', 'share')) {
        $hsReplayReport = Join-Path $runDirectory ("mixed-hotseat-replay-$hsKind.md")
        $hsReplayArgs = if ($hsKind -eq 'file') { @('--replay', $hsSaved.File) } else { @('--replay-code', $hsShare) }
        $hsReplayChild = Start-Cli ("mixed-hotseat-replay-$hsKind") ($hsReplayArgs +
            @('--auto', '--share', '--report', $hsReplayReport, '--save-dir', $hsSave))
        $hsReplayChild.Process.StandardInput.Close()
        $hsReplayResult = Wait-Cli $hsReplayChild 0
        Assert-Check ($hsReplayResult.Err.Length -eq 0 -and $hsReplayResult.Out.Contains('回看结束：混合甲 获胜') -and
            (Extract-Code $hsReplayResult.Out 'SD1-' '分享码：') -ceq $hsShare -and
            [IO.File]::ReadAllText($hsReplayReport, $utf8) -ceq $hsExpectedMarkdown) '混合同屏文件或分享码回看未恢复同一结果、战报和原始分享码。'
        Assert-Check ((& $hsFingerprint) -ceq $hsBeforeReplay) '混合同屏回看改写了源录像、索引或档案，或重复保存了对局。'
    }
    $checks.Add('混合同屏随机十二人和双来源；真实交接后投降完整结算；原始/新库录像、分享和文件/码回看一致；A方恰一胜且回看无存档副作用')

    $externalDirectory = Join-Path $runDirectory 'external-packs'
    [void][IO.Directory]::CreateDirectory($externalDirectory)
    $external = $fanJson | ConvertFrom-Json
    $external.id = 'mixed,external:中文%note'
    $external.name = '外部混合验收包'
    $external.notice = '外部声明保留验收：' + $external.notice
    $externalJson = $external | ConvertTo-Json -Depth 100
    [IO.File]::WriteAllText((Join-Path $externalDirectory 'external.json'), $externalJson, $utf8)
    $externalDb = [StreamDuel.Core.ContentDb]::FromJson($rules, $externalJson)
    $externalMix = [StreamDuel.Core.ContentDb]::Merge([StreamDuel.Core.ContentDb[]]@($original, $externalDb))
    $externalGame = Run-Cli 'external' @('--pack', $externalMix.PackId, '--pack-dir', $externalDirectory,
        '--random-teams', '--auto', '--seed', '19', '--export-team')
    Assert-Check ($externalGame.Err.Length -eq 0 -and $externalGame.Out.Contains($external.notice)) '外部特殊编号或声明加载失败。'
    $externalRecord = Read-Record 'external'
    $externalCode = Extract-Code $externalGame.Out 'SDT1-' '本方队伍码：'
    $externalReplay = Run-Cli 'external-replay' @('--replay', $externalRecord.File, '--pack-dir', $externalDirectory, '--auto')
    $externalImport = Run-Cli 'external-import' @('--team-code', $externalCode, '--pack-dir', $externalDirectory, '--export-team') "q`n"
    Assert-Check ($externalReplay.Err.Length -eq 0 -and $externalImport.Err.Length -eq 0 -and
        (Extract-Code $externalImport.Out 'SDT1-' '本方队伍码：') -ceq $externalCode) '外部组合的回放或队伍码无法重建。'
    Assert-NoSave 'external-replay'; Assert-NoSave 'external-import'
    Reject 'external-missing' @('--replay', $externalRecord.File, '--auto')
    $checks.Add('外部逗号/冒号/中文/百分号ID的规范编码、完整对局、回放和队伍码重建；缺包中文拒绝')

    $invalid = @(
        @('--packs', 'original'), @('--packs', 'original,'), @('--packs', 'original,original'),
        @('--packs', 'original,missing_pack'), @('--packs', 'original,fan_asoul_vr', '--pack', 'original'),
        @('--packs', 'original,fan_asoul_vr', '--random-teams', '--team', $teamA),
        @('--packs', 'original,fan_asoul_vr', '--random-teams', '--team-b', $teamB),
        @('--random-teams', '--team-code', $teamCode), @('--random-teams', '--replay', $saved.File),
        @('--packs', 'original,fan_asoul_vr', '--replays'), @('--random-teams', '--host', '0'),
        @('--team-code', $teamCode, '--pack', 'original'), @('--pack', 'mix:original,fan_asoul_vr'),
        @('--team-b', $teamB))
    for ($i = 0; $i -lt $invalid.Count; $i++) { Reject ('invalid-' + $i) $invalid[$i] }
    $checks.Add('参数互斥、未知/重复/空包、非规范组合、队伍码组合不符和联网缺显式seed全部拒绝且不存档')

    foreach ($mismatch in @($false, $true)) {
        $prefix = if ($mismatch) { 'network-mismatch' } else { 'network' }
        $hostSave = Join-Path $runDirectory ($prefix + '-host-saves')
        $guestSave = Join-Path $runDirectory ($prefix + '-guest-saves')
        $hostChild = Start-Cli ($prefix + '-host') @('--packs', 'original,fan_asoul_vr', '--random-teams', '--seed', '23', '--host', '0', '--auto', '--hash', '--save-dir', $hostSave)
        $port = [int](Wait-Output $hostChild '(?m)^监听端口：([0-9]+)\r?$').Groups[1].Value
        $guestSeed = if ($mismatch) { '24' } else { '23' }
        $guestChild = Start-Cli ($prefix + '-guest') @('--packs', 'fan_asoul_vr,original', '--random-teams', '--seed', $guestSeed, '--join', "127.0.0.1:$port", '--auto', '--hash', '--save-dir', $guestSave)
        $hostChild.Process.StandardInput.Close(); $guestChild.Process.StandardInput.Close()
        $exit = if ($mismatch) { 1 } else { 0 }
        $hostResult = Wait-Cli $hostChild $exit
        $guestResult = Wait-Cli $guestChild $exit
        if ($mismatch) {
            Assert-Check (($hostResult.Err + $guestResult.Err).Contains('随机联网阵容不一致')) '不同seed没有明确拒绝原因。'
            Assert-NoSave ($prefix + '-host'); Assert-NoSave ($prefix + '-guest')
        }
        else {
            Assert-Check ($hostResult.Err.Length -eq 0 -and $guestResult.Err.Length -eq 0 -and
                (Final-Hash $hostResult.Out) -ceq (Final-Hash $guestResult.Out)) '随机混合联网哈希不一致。'
            $hostRecord = (Read-Record ($prefix + '-host')).Record
            $guestRecord = (Read-Record ($prefix + '-guest')).Record
            Assert-Check ($hostRecord.ToJson() -ceq $guestRecord.ToJson()) '随机混合联网双方录像不一致。'
            $networkTeams = [StreamDuel.Core.MixedBattle]::RandomTeams($db, [uint32]23)
            for ($side = 0; $side -lt 2; $side++) {
                Assert-Check (($hostRecord.Teams[$side] -join ',') -ceq ($networkTeams[$side] -join ',')) '联网没有采用对应方随机阵容。'
            }
        }
    }
    $checks.Add('反转包顺序的真实TCP随机完整对局、双方哈希/录像一致；不同seed明确拒绝且不保存')

    # 只修改 obj 内的发布副本，源政策、正常构建和前面保存的原始录像保持原样。
    $sourcePolicy = Join-Path $binaryDirectory 'Content/PackPolicy.json'
    Assert-Check (Test-Path -LiteralPath $sourcePolicy -PathType Leaf) 'CLI 输出未包含下架政策。'
    $policyHash = (Get-FileHash -LiteralPath $sourcePolicy -Algorithm SHA256).Hash
    $recordHash = (Get-FileHash -LiteralPath $saved.File -Algorithm SHA256).Hash
    $deployment = Join-Path $runDirectory 'policy-deployment'
    Copy-PolicyDeployment $binaryDirectory $deployment
    $script:cliDll = Join-Path $deployment 'Cli.dll'
    $policyPath = Join-Path $deployment 'Content/PackPolicy.json'

    Set-TestPolicy @('original') @() '混合来源整包下架验收'
    Reject-Policy 'policy-pack-list-form' @('--packs', 'original,fan_asoul_vr', '--random-teams', '--auto') '混合来源整包下架验收'
    Reject-Policy 'policy-pack-encoded' @('--pack', $db.PackId, '--random-teams', '--auto') '混合来源整包下架验收'
    Reject-Policy 'policy-pack-team-code' @('--team-code', $teamCode, '--export-team') '混合来源整包下架验收'
    Reject-Policy 'policy-pack-replay' @('--replay', $saved.File, '--auto') '混合来源整包下架验收'
    Reject-Policy 'policy-pack-best-effort' @('--replay', $saved.File, '--auto', '--replay-best-effort') '混合来源整包下架验收'
    Reject-Policy 'policy-pack-share' @('--replay-code', $share, '--auto') '混合来源整包下架验收'
    $libraryChild = Start-Cli 'policy-pack-library' @('--replays', '--save-dir', (Join-Path $runDirectory 'manual-saves'))
    $libraryChild.Process.StandardInput.Close()
    $libraryResult = Wait-Cli $libraryChild 0
    Assert-Check ($libraryResult.Out.Contains('不可重放：') -and $libraryResult.Out.Contains('混合来源整包下架验收')) '混合录像库未标记源包下架。'
    $unrelated = Run-Cli 'policy-unrelated-pack' @('--packs', 'fan_asoul_vr,original_wave2', '--export-team') "q`n"
    Assert-Check ($unrelated.Err.Length -eq 0 -and $unrelated.Out.Contains('本方队伍码：')) '默认 original 错误阻止了不含它的组合。'
    Assert-NoSave 'policy-unrelated-pack'

    Set-TestPolicy @($external.id) @() '特殊外部来源下架验收'
    Reject-Policy 'policy-external-pack' @('--pack', $externalMix.PackId, '--pack-dir', $externalDirectory, '--random-teams', '--auto') '特殊外部来源下架验收'
    Reject-Policy 'policy-external-replay' @('--replay', $externalRecord.File, '--pack-dir', $externalDirectory, '--auto', '--replay-best-effort') '特殊外部来源下架验收'

    Set-TestPolicy @() @('bell') '混合主播下架验收'
    Reject-Policy 'policy-streamer-team' @('--packs', 'original,fan_asoul_vr', '--team', $teamA, '--team-b', $teamB, '--export-team') '该主播已下架'
    Reject-Policy 'policy-streamer-opponent' @('--packs', 'original,fan_asoul_vr', '--team', $teamB, '--team-b', $teamA, '--export-team') '该主播已下架'
    Reject-Policy 'policy-streamer-code' @('--team-code', $teamCode, '--export-team') '该主播已下架'
    Reject-Policy 'policy-streamer-replay' @('--replay', $saved.File, '--auto') '该主播已下架'
    Reject-Policy 'policy-streamer-best-effort' @('--replay', $saved.File, '--auto', '--replay-best-effort') '该主播已下架'
    Reject-Policy 'policy-streamer-share' @('--replay-code', $share, '--auto') '该主播已下架'
    $filtered = Run-Cli 'policy-random-filtered' @('--packs', 'original,fan_asoul_vr', '--random-teams', '--seed', '29', '--auto')
    $filteredRecord = (Read-Record 'policy-random-filtered').Record
    Assert-Check ($filtered.Err.Length -eq 0 -and -not (@($filteredRecord.Teams[0]) + @($filteredRecord.Teams[1]) -contains 'bell')) '随机混合阵容重新引入了下架主播。'

    Set-TestPolicy @() @('bella') '外部主播下架验收'
    Reject-Policy 'policy-external-streamer' @('--pack', $externalMix.PackId, '--pack-dir', $externalDirectory,
        '--team', $teamA, '--team-b', $teamB) '外部主播下架验收'
    Set-TestPolicy @() @('bella', 'diana') '不足六人隐藏验收'
    Reject-Policy 'policy-hidden-source' @('--packs', 'original,fan_asoul_vr', '--random-teams', '--auto') '不足 6 位'
    Reject-Policy 'policy-hidden-external' @('--pack', $externalMix.PackId, '--pack-dir', $externalDirectory, '--random-teams', '--auto') '不足 6 位'
    Assert-Check ((Get-FileHash -LiteralPath $sourcePolicy -Algorithm SHA256).Hash -ceq $policyHash -and
        (Get-FileHash -LiteralPath $saved.File -Algorithm SHA256).Hash -ceq $recordHash) '下架验收修改了原始政策或录像。'
    $script:cliDll = $builtCliDll
    $checks.Add('隔离发布副本：源包/外部包/主播下架、双方手动与随机阵容、队伍码、回放/尽力重放/分享码/录像库门禁和不足六人原因；原策略/录像未改')

}
catch { $failure = $_.Exception.GetBaseException().Message }
finally {
    $script:cliDll = $builtCliDll
    foreach ($child in $children) {
        if ($child.Started -and -not $child.Process.HasExited) { $child.Process.Kill($true); [void]$child.Process.WaitForExit(5000) }
        foreach ($task in @($child.OutTask, $child.ErrTask)) { if ($null -ne $task) { [void]$task.Wait(5000) } }
        foreach ($stream in @($child.OutStream, $child.ErrStream)) { if ($null -ne $stream) { $stream.Dispose() } }
        $child.Process.Dispose()
    }
    $summary = [ordered]@{ status = $(if ($null -eq $failure) { 'PASS' } else { 'FAIL' }); failure = $failure; checks = $checks.ToArray(); directory = $runDirectory }
    [IO.File]::WriteAllText((Join-Path $runDirectory 'result.json'), ($summary | ConvertTo-Json -Depth 6), $utf8)
}
if ($null -ne $failure) { throw "$failure 证据目录：$runDirectory" }
Write-Output "PASS：$($checks.Count) 组混合包验收；证据目录：$runDirectory"
