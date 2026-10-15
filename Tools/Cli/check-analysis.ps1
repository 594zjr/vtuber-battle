#requires -Version 7.0
param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug')

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
        throw "请先运行 dotnet build Tools/Cli -c $Configuration。"
    }
}
$runDirectory = [IO.Path]::GetFullPath((Join-Path $repository ('Tools/Cli/obj/analysis-review/' + [Guid]::NewGuid().ToString('N'))))
$allowedPrefix = [IO.Path]::GetFullPath((Join-Path $repository 'Tools/Cli/obj')) + [IO.Path]::DirectorySeparatorChar
if (-not $runDirectory.StartsWith($allowedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw '产物目录必须位于 Tools/Cli/obj 内。'
}
[void][IO.Directory]::CreateDirectory($runDirectory)
$utf8 = [Text.UTF8Encoding]::new($false)
$checks = [Collections.Generic.List[string]]::new()
$invocations = [Collections.Generic.List[object]]::new()
$passed = $false
$failure = $null
$reference = $null
[string[]]$teamA = @('bell', 'enka', 'shiori', 'lumi', 'hane', 'momo')
[string[]]$teamB = @('kei', 'yuan', 'nyako', 'buck', 'kasumi', 'bee')

function Assert-Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "$Message 证据目录：$runDirectory" }
}

function New-Case([string]$Name) {
    Assert-Check ($Name -cmatch '^[A-Za-z0-9_-]+$') '用例名称包含路径字符。'
    $directory = [IO.Path]::GetFullPath((Join-Path $runDirectory $Name))
    Assert-Check ($directory.StartsWith($runDirectory + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) '用例路径越界。'
    [void][IO.Directory]::CreateDirectory($directory)
    return $directory
}

function Invoke-Cli([string]$Name, [string[]]$Parameters, [int]$ExpectedExit = 0, [int]$TimeoutMs = 15000) {
    $directory = New-Case $Name
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
    $start.StandardOutputEncoding = $utf8
    $start.StandardErrorEncoding = $utf8
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    $started = $false
    try {
        $started = $process.Start()
        Assert-Check $started "$Name 无法启动。"
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $process.StandardInput.Close()
        $timedOut = -not $process.WaitForExit($TimeoutMs)
        if ($timedOut) {
            $process.Kill($true)
            Assert-Check ($process.WaitForExit(5000)) "$Name 超时后无法退出。"
        }
        Assert-Check ($stdout.Wait(5000) -and $stderr.Wait(5000)) "$Name 输出未结束。"
        $result = [pscustomobject]@{ Out = $stdout.Result; Err = $stderr.Result; Exit = $process.ExitCode }
        [IO.File]::WriteAllText((Join-Path $runDirectory "$Name.stdout.log"), $result.Out, $utf8)
        [IO.File]::WriteAllText((Join-Path $runDirectory "$Name.stderr.log"), $result.Err, $utf8)
        $items = @(Get-ChildItem -LiteralPath $directory -Force)
        $invocations.Add([ordered]@{
            name = $Name; arguments = $Parameters; exit = $result.Exit; timedOut = $timedOut
            working_directory = $directory; created_entries = @($items | ForEach-Object Name)
        })
        Assert-Check (-not $timedOut) "$Name 超过 $TimeoutMs 毫秒未退出。"
        Assert-Check ($result.Exit -eq $ExpectedExit) "$Name 应退出 $ExpectedExit，实际 $($result.Exit)。"
        # 日志写在用例目录外，因此这里连临时存档、战报或导出目录也不能出现。
        Assert-Check ($items.Count -eq 0) "$Name 误写了存档、导出文件或目录。"
        if ($ExpectedExit -eq 0) {
            Assert-Check ([string]::IsNullOrWhiteSpace($result.Err)) "$Name 成功时出现标准错误。"
        }
        else {
            Assert-Check ($result.Err -match '错误：') "$Name 没有明确错误说明。"
            Assert-Check ($result.Out -notmatch '伤害计算器|阵容对位表|命令行对战|监听端口|回看结束|队伍码：|分享码：|回放已保存|战报已保存') "$Name 拒绝参数前已进入其他模式。"
        }
        return $result
    }
    finally {
        # 仅结束本次启动的进程及其子进程，不按名称查杀，也不删除验收证据。
        if ($started -and -not $process.HasExited) {
            $process.Kill($true)
            [void]$process.WaitForExit(5000)
        }
        $process.Dispose()
    }
}

function Assert-Preview($Result, $Expected, [int]$SkillNumber) {
    $damage = [regex]::Matches($Result.Out, '(?m)^预览伤害：([0-9]+)～([0-9]+)\r?$')
    Assert-Check ($damage.Count -eq 1) '伤害输出不是唯一的整数区间。'
    Assert-Check ([int]$damage[0].Groups[1].Value -eq $Expected.Min -and
        [int]$damage[0].Groups[2].Value -eq $Expected.Max) 'CLI 伤害区间与生产 DamageCalc.Range 不同。'
    $multiplier = $Expected.Eff.ToString('0.##', [Globalization.CultureInfo]::InvariantCulture)
    Assert-Check ($Result.Out.Contains('赛道倍率：×' + $multiplier)) 'CLI 赛道倍率与 Core 不同。'
    Assert-Check ($Result.Out -match ('(?m)^技能：' + $SkillNumber + ' · ')) 'CLI 技能序号映射不正确。'
    foreach ($note in $Expected.Notes) {
        Assert-Check ($Result.Out.Contains('  ' + $note)) 'CLI 漏掉 Core 预览说明。'
    }
}

try {
    $assembly = [Reflection.Assembly]::LoadFrom($coreDll)
    Assert-Check ([StringComparer]::OrdinalIgnoreCase.Equals([IO.Path]::GetFullPath($assembly.Location),
        [IO.Path]::GetFullPath($coreDll))) '请在未加载其他 Core 的新 PowerShell 进程运行。'
    Assert-Check ($null -ne $assembly.GetType('StreamDuel.Core.DamageCalc', $false)) '现有二进制尚未包含 T77 DamageCalc。'
    $content = Join-Path $binaryDirectory 'Content'
    $db = [StreamDuel.Core.ContentDb]::FromJson(
        [IO.File]::ReadAllText((Join-Path $content 'StreamDuelRules.json'), $utf8),
        [IO.File]::ReadAllText((Join-Path $content 'Packs/original.json'), $utf8))
    $contentHash = $db.ContentHash()

    $help = Invoke-Cli '00-help' @('--help')
    foreach ($option in @('--calc', '--matchup', '--lan-host', '--lan-list', '--timer', '--team-code',
        '--export-team', '--share', '--replay', '--replay-code', '--list-packs')) {
        Assert-Check ($help.Out.Contains($option)) "合流后帮助遗漏 $option。"
    }
    $checks.Add('分析入口与现有 LAN、计时、队伍码、分享、回放帮助均保留')

    $preview = [StreamDuel.Core.DamageCalc]::Range($db, 'bell', 4, 'kei', $null, $null)
    $calc = Invoke-Cli '01-calc-fifth' @('--pack', 'original', '--calc', 'bell', '5', 'kei')
    Assert-Preview $calc $preview 5
    Assert-Check ($calc.Out -match '〔第五技能〕') '第五技能缺少标记。'
    $statusIndex = $db.Streamer('bell').Skills.IndexOf('go_live_early')
    Assert-Check ($statusIndex -ge 0 -and $db.Skills['go_live_early'].Cat -eq 'status') '状态技能夹具不再有效。'
    $statusPreview = [StreamDuel.Core.DamageCalc]::Range($db, 'bell', $statusIndex, 'kei', $null, $null)
    Assert-Check ($statusPreview.Min -eq 0 -and $statusPreview.Max -eq 0) '生产 Core 状态技预览应为零。'
    $status = Invoke-Cli '02-calc-status' @('--calc', 'bell', [string]($statusIndex + 1), 'kei')
    Assert-Preview $status $statusPreview ($statusIndex + 1)
    $checks.Add('calc bell 5 kei 的范围、倍率和说明与生产 Core 一致；状态技能为 0；均无存档')

    [string[]]$commaMatchup = @('--matchup', ($teamA -join ','), 'vs', ($teamB -join ','))
    [string[]]$spaceMatchup = @('--matchup') + $teamA + @('vs') + $teamB
    $comma = Invoke-Cli '03-matchup-comma' $commaMatchup
    $space = Invoke-Cli '04-matchup-space' $spaceMatchup
    Assert-Check ($comma.Out -ceq $space.Out) '逗号和空格队伍参数输出不同。'
    $matrix = [StreamDuel.Core.DamageCalc]::Matchup($db, $teamA, $teamB)
    Assert-Check ($matrix.Rank -eq 2 -and $matrix.GetLength(0) -eq 6 -and $matrix.GetLength(1) -eq 6) '生产对位表不是 6×6。'
    Assert-Check ([regex]::Matches($comma.Out, '(?m)^A/B\s+B1\s+B2\s+B3\s+B4\s+B5\s+B6\r?$').Count -eq 1) 'CLI 对位表列标题不正确。'
    $rows = [regex]::Matches($comma.Out, '(?m)^A([0-9]+)[ \t]+([^\r\n]+)\r?$')
    Assert-Check ($rows.Count -eq 6) 'CLI 对位表应恰有六行。'
    $referenceRows = [Collections.Generic.List[object]]::new()
    for ($row = 0; $row -lt 6; $row++) {
        Assert-Check ([int]$rows[$row].Groups[1].Value -eq $row + 1) 'CLI 行顺序不正确。'
        $cells = $rows[$row].Groups[2].Value.Trim() -split '\s+'
        Assert-Check ($cells.Count -eq 6) 'CLI 对位表每行应恰有六格。'
        $expectedRow = [Collections.Generic.List[int]]::new()
        for ($column = 0; $column -lt 6; $column++) {
            Assert-Check ($cells[$column] -cmatch '^[0-9]+%$') '对位表单元格不是非负整数百分比。'
            $value = [int]$cells[$column].TrimEnd('%')
            Assert-Check ($value -eq $matrix[$row, $column]) "对位 A$($row + 1)/B$($column + 1) 与生产 DamageCalc.Matchup 不同。"
            $expectedRow.Add($matrix[$row, $column])
        }
        $referenceRows.Add($expectedRow.ToArray())
    }
    $checks.Add('matchup 逗号/空格完全一致，6×6 的 36 格与生产 Core 一致，均无存档')

    $invalid = @(
        @{ Name = 'skill-zero'; Args = @('--calc', 'bell', '0', 'kei') },
        @{ Name = 'skill-six'; Args = @('--calc', 'bell', '6', 'kei') },
        @{ Name = 'bad-attacker'; Args = @('--calc', 'missing_attacker', '1', 'kei') },
        @{ Name = 'bad-defender'; Args = @('--calc', 'bell', '1', 'missing_defender') },
        @{ Name = 'bad-team'; Args = @('--matchup', 'missing,bell,enka,shiori,lumi,hane', 'vs', ($teamB -join ',')) },
        @{ Name = 'duplicate-team'; Args = @('--matchup', 'bell,bell,shiori,lumi,hane,momo', 'vs', ($teamB -join ',')) },
        @{ Name = 'mixed-analysis'; Args = @('--calc', 'bell', '5', 'kei') + $commaMatchup },
        @{ Name = 'mixed-analysis-reverse'; Args = $commaMatchup + @('--calc', 'bell', '5', 'kei') }
    )
    foreach ($case in $invalid) {
        $null = Invoke-Cli ('invalid-' + $case.Name) $case.Args 1
    }
    $checks.Add('非法技能 0/6、未知双方 id、未知或重复队员、两种分析混用均退出 1 且无产物')

    # 使用有效输入，避免被测入口误跑回放/队伍导入后因坏夹具报错而假通过。
    $preset = [StreamDuel.Core.TeamPreset]::new()
    $preset.Name = '分析隔离验收'
    $preset.PackId = 'original'
    $preset.Ids.AddRange($teamA)
    $teamCode = [StreamDuel.Core.TeamCode]::Encode($preset)
    $fixtureTeams = [Collections.Generic.IList[string][]]::new(2)
    $fixtureTeams[0] = $teamA
    $fixtureTeams[1] = $teamB
    $fixture = [StreamDuel.Core.Battle]::new($db, $fixtureTeams, [string[]]@('甲', '乙'), [uint32]77)
    $replayPath = Join-Path $runDirectory 'valid-replay.json'
    [IO.File]::WriteAllText($replayPath, $fixture.Record.ToJson(), $utf8)
    $replayHash = (Get-FileHash -LiteralPath $replayPath -Algorithm SHA256).Hash
    $replayCode = [StreamDuel.Core.ShareCode]::Encode($fixture.Record)
    $conflicts = @(
        @{ Name = 'lan-host'; Args = @('--lan-host', '分析不得开房') },
        @{ Name = 'lan-list'; Args = @('--lan-list') },
        @{ Name = 'host'; Args = @('--host', '0') },
        @{ Name = 'join'; Args = @('--join', '127.0.0.1:1') },
        @{ Name = 'timer'; Args = @('--timer', '10,30') },
        @{ Name = 'team-code'; Args = @('--team-code', $teamCode) },
        @{ Name = 'export-team'; Args = @('--export-team') },
        @{ Name = 'share'; Args = @('--share') },
        @{ Name = 'replay'; Args = @('--replay', $replayPath) },
        @{ Name = 'replay-code'; Args = @('--replay-code', $replayCode) },
        @{ Name = 'list-packs'; Args = @('--list-packs') },
        @{ Name = 'auto'; Args = @('--auto') },
        @{ Name = 'ban'; Args = @('--ban') },
        @{ Name = 'lead'; Args = @('--lead') },
        @{ Name = 'hash'; Args = @('--hash') },
        @{ Name = 'level'; Args = @('--level', 'normal') },
        @{ Name = 'seed'; Args = @('--seed', '77') },
        @{ Name = 'team'; Args = @('--team', ($teamA -join ',')) },
        @{ Name = 'save-dir'; Args = @('--save-dir', 'unexpected saves') },
        @{ Name = 'report'; Args = @('--report', 'unexpected-report.md') }
    )
    foreach ($mode in @('calc', 'matchup')) {
        [string[]]$analysis = if ($mode -eq 'calc') { @('--calc', 'bell', '5', 'kei') } else { $commaMatchup }
        foreach ($conflict in $conflicts) {
            $result = Invoke-Cli ("reject-$mode-" + $conflict.Name) ($analysis + $conflict.Args) 1
            Assert-Check ($result.Err -notmatch '未知参数') '已合并功能被错误地当成未知参数。'
            Assert-Check ($result.Err -match '伤害分析|--calc|--matchup|--lan-list.*独立') '退出 1 并非分析模式互斥校验导致。'
        }
    }
    Assert-Check ((Get-FileHash -LiteralPath $replayPath -Algorithm SHA256).Hash -ceq $replayHash) '互斥参数检查修改了输入回放。'
    Assert-Check ($db.ContentHash() -ceq $contentHash) '生产分析调用修改了内容数据库。'
    $checks.Add('两种分析分别与 LAN/TCP、计时、队伍导入导出、分享、回放及全部对战存档参数互斥；40 例退出 1 且无产物')
    $reference = [ordered]@{
        content_hash = $contentHash; calc_min = $preview.Min; calc_max = $preview.Max
        calc_eff = $preview.Eff; status_skill = $statusIndex + 1; matchup = $referenceRows.ToArray()
    }
    $passed = $true
}
catch {
    $failure = $_.Exception.Message
    throw
}
finally {
    $summary = [ordered]@{
        passed = $passed; failure = $failure; run_directory = $runDirectory
        cli_sha256 = (Get-FileHash -LiteralPath $cliDll -Algorithm SHA256).Hash
        core_sha256 = (Get-FileHash -LiteralPath $coreDll -Algorithm SHA256).Hash
        checks = $checks.ToArray(); reference = $reference; invocations = $invocations.ToArray()
    }
    [IO.File]::WriteAllText((Join-Path $runDirectory 'result.json'), ($summary | ConvertTo-Json -Depth 10), $utf8)
    Write-Host "T77 CLI analysis verification: passed=$passed; $runDirectory"
}
