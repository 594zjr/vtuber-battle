#requires -Version 7.0
param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug')

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
# 所有验证输入与产物均保留在本仓库 obj 中。
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$binaryDirectory = Join-Path $repository "Tools/Cli/bin/$Configuration/net8.0"
$cliDll = Join-Path $binaryDirectory 'Cli.dll'
$coreDll = Join-Path $binaryDirectory 'StreamDuel.Core.dll'
foreach ($path in @($cliDll, $coreDll)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "请先运行 dotnet build Tools/Cli -c $Configuration。"
    }
}
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $repository 'Tools/Cli/obj/glossary-verification'))
$runDirectory = [IO.Path]::GetFullPath((Join-Path $artifactRoot ([Guid]::NewGuid().ToString('N'))))
$repositoryPrefix = $repository.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $artifactRoot.StartsWith($repositoryPrefix, [StringComparison]::OrdinalIgnoreCase) -or
    -not $runDirectory.StartsWith($artifactRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw '验收产物必须位于本仓库 Tools/Cli/obj/glossary-verification 内。'
}
[void][IO.Directory]::CreateDirectory($runDirectory)
$utf8 = [Text.UTF8Encoding]::new($false)
$checks = [Collections.Generic.List[string]]::new()
$invocations = [Collections.Generic.List[object]]::new()
$references = [Collections.Generic.List[object]]::new()
$passed = $false
$failure = $null
$restoreLocale = $false
$previousLocale = $null
[string[]]$teamA = @('bell', 'enka', 'shiori', 'lumi', 'hane', 'momo')
[string[]]$teamB = @('kei', 'yuan', 'nyako', 'buck', 'kasumi', 'bee')

function Assert-Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "$Message 证据目录：$runDirectory" }
}

function New-Case([string]$Name) {
    Assert-Check ($Name -cmatch '^[A-Za-z0-9_-]+$') '用例名称包含路径字符。'
    $directory = [IO.Path]::GetFullPath((Join-Path $runDirectory $Name))
    Assert-Check ($directory.StartsWith($runDirectory + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) '用例目录越界。'
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
        # 证据和输入夹具都在用例目录外，空目录检查同时覆盖存档、临时文件及导出。
        Assert-Check ($items.Count -eq 0) "$Name 误写了存档、文件或目录。"
        if ($ExpectedExit -eq 0) {
            Assert-Check ([string]::IsNullOrWhiteSpace($result.Err)) "$Name 成功时出现标准错误。"
        }
        else {
            Assert-Check ($result.Err -match '错误：') "$Name 未给出错误说明。"
            Assert-Check ($result.Out -notmatch '开播对决 · 规则说明|伤害计算器|阵容对位表|命令行对战|监听端口|对战回看|回看结束|队伍码：|分享码：|回放已保存|战报已保存') "$Name 拒绝参数前已进入其他模式。"
        }
        return $result
    }
    finally {
        # 只处理本次创建的进程对象，不按名称查杀或删除证据。
        if ($started -and -not $process.HasExited) {
            $process.Kill($true)
            [void]$process.WaitForExit(5000)
        }
        $process.Dispose()
    }
}

function Assert-Glossary([string]$Name, $Db, [string[]]$Parameters, [AllowNull()][string]$Keyword,
    [ValidateSet('all', 'some', 'none')][string]$ExpectedKind) {
    $before = $Db.ContentHash()
    [Collections.Generic.List[StreamDuel.Core.GlossaryEntry]]$all = [StreamDuel.Core.Glossary]::Build($Db)
    $entries = @([StreamDuel.Core.Glossary]::Search($all, $Keyword))
    Assert-Check ($all.Count -gt 0) "$Name 的生产词条列表为空。"
    if ($ExpectedKind -eq 'all') {
        Assert-Check ($entries.Count -eq $all.Count) "$Name 空查询没有返回全部词条。"
    }
    elseif ($ExpectedKind -eq 'some') {
        Assert-Check ($entries.Count -gt 0) "$Name 关键字夹具没有命中。"
    }
    else { Assert-Check ($entries.Count -eq 0) "$Name 无结果夹具意外命中。" }

    # 仅按 CLI 的公开格式拼接生产词条，不复制 Build/Search 的分类、文案或搜索逻辑。
    $loc = [StreamDuel.Core.Loc]::Current
    $lines = [Collections.Generic.List[string]]::new()
    $lines.Add($loc.T('glossary.cli.title', [object[]]@($Db.PackName)))
    $lines.Add($Db.PackNotice)
    $lines.Add($loc.T('glossary.cli.count', [object[]]@($entries.Count)))
    if ($entries.Count -eq 0) {
        $query = if ($null -eq $Keyword) { '' } else { $Keyword.Trim() }
        $lines.Add($loc.T('glossary.cli.no.matches', [object[]]@($query)))
    }
    foreach ($entry in $entries) {
        Assert-Check (-not [string]::IsNullOrWhiteSpace($entry.Title) -and
            -not [string]::IsNullOrWhiteSpace($entry.Text)) "$Name 生产词条标题或正文为空。"
        $lines.Add('')
        $category = $loc.T('glossary.category.' + $entry.Category, [object[]]@())
        $lines.Add($loc.T('glossary.cli.entry', [object[]]@($category, $entry.Title)))
        $lines.Add($entry.Text)
    }
    $expected = ($lines -join "`n") + "`n"
    [IO.File]::WriteAllText((Join-Path $runDirectory "$Name.expected.txt"), $expected, $utf8)
    $actual = Invoke-Cli $Name $Parameters
    Assert-Check ($actual.Out -ceq $expected) "$Name 的数量、顺序、分类、标题或正文与生产 Core 不一致。"
    Assert-Check ($Db.ContentHash() -ceq $before) "$Name 构造或搜索词条改变了内容库。"
    $references.Add([ordered]@{
        name = $Name; pack = $Db.PackId; content_hash = $before; keyword = $Keyword
        entry_count = $entries.Count; entries = @($entries | ForEach-Object { $_.Category + '/' + $_.Id })
    })
}

try {
    $assembly = [Reflection.Assembly]::LoadFrom($coreDll)
    Assert-Check ([StringComparer]::OrdinalIgnoreCase.Equals([IO.Path]::GetFullPath($assembly.Location),
        [IO.Path]::GetFullPath($coreDll))) '请在未加载其他 Core 的新 PowerShell 进程运行。'
    Assert-Check ($null -ne $assembly.GetType('StreamDuel.Core.Glossary', $false)) '现有二进制尚未包含 T76 Glossary。'
    $previousLocale = [StreamDuel.Core.Loc]::Current
    $restoreLocale = $true
    [StreamDuel.Core.Loc]::Current = $null
    $content = Join-Path $binaryDirectory 'Content'
    $rules = [IO.File]::ReadAllText((Join-Path $content 'StreamDuelRules.json'), $utf8)
    $databases = @{}
    $noMatch = '验收专用不存在词条' + [Guid]::NewGuid().ToString('N')
    foreach ($pack in @('original', 'original_wave2', 'fan_holo_niji', 'fan_asoul_vr')) {
        $packJson = [IO.File]::ReadAllText((Join-Path $content ("Packs/$pack.json")), $utf8)
        $db = [StreamDuel.Core.ContentDb]::FromJson($rules, $packJson)
        $databases[$pack] = $db
        $status = @([StreamDuel.Core.Glossary]::Build($db) | Where-Object Category -EQ 'status')[0]
        $keyword = '  ' + $status.Title + '  '
        Assert-Glossary "$pack-all" $db @('--pack', $pack, '--glossary') $null 'all'
        Assert-Glossary "$pack-query" $db @('--glossary', $keyword, '--pack', $pack) $keyword 'some'
        Assert-Glossary "$pack-case-query" $db @('--pack', $pack, '--glossary', 'pp') 'pp' 'some'
        Assert-Glossary "$pack-none" $db @('--pack', $pack, '--glossary', $noMatch) $noMatch 'none'
    }
    $checks.Add('四包全列表、状态名查询（含两端空格）、小写 pp 查询和无结果逐项对照生产 Build/Search；16 例成功且 cwd 为空')

    $help = Invoke-Cli 'help' @('--help')
    foreach ($option in @('--glossary', '--pack', '--pack-dir', '--calc', '--matchup', '--lan-host', '--lan-list',
        '--timer', '--team-code', '--export-team', '--share', '--replay', '--replay-code', '--list-packs')) {
        Assert-Check ($help.Out.Contains($option)) "合流后帮助遗漏 $option。"
    }
    $glossaryHelp = Invoke-Cli 'glossary-help' @('--glossary', '--help')
    $queryHelp = Invoke-Cli 'query-short-help' @('--glossary', '状态', '-h')
    Assert-Check ($glossaryHelp.Out -ceq $help.Out -and $queryHelp.Out -ceq $help.Out) '词条查询没有保留帮助入口。'
    $checks.Add('普通帮助、词条无关键字帮助和带关键字 -h 输出相同，无产物')

    $fixtures = New-Case 'fixtures'
    $externalDirectory = Join-Path $fixtures 'external packs'
    [void][IO.Directory]::CreateDirectory($externalDirectory)
    $external = [IO.File]::ReadAllText((Join-Path $content 'Packs/original.json'), $utf8) | ConvertFrom-Json -AsHashtable
    $external['id'] = 'glossary_review_external'
    $external['name'] = '词条目录验收包'
    $external['notice'] = '仅用于本地 CLI 验收的原创包副本。'
    $originalDb = $databases['original']
    $status = @([StreamDuel.Core.Glossary]::Build($originalDb) | Where-Object Category -EQ 'status')[0]
    if (-not $external.Contains('statusNames')) { $external['statusNames'] = [ordered]@{} }
    $external['statusNames'][$status.Id] = '验收专用状态名'
    $externalJson = $external | ConvertTo-Json -Depth 100
    $externalPath = Join-Path $externalDirectory 'review-pack.json'
    [IO.File]::WriteAllText($externalPath, $externalJson, $utf8)
    $externalHash = (Get-FileHash -LiteralPath $externalPath -Algorithm SHA256).Hash
    $externalDb = [StreamDuel.Core.ContentDb]::FromJson($rules, $externalJson)
    Assert-Glossary 'external-all' $externalDb @('--glossary', '--pack', $externalDb.PackId, '--pack-dir', $externalDirectory) $null 'all'
    Assert-Glossary 'external-query' $externalDb @('--pack-dir', $externalDirectory, '--pack', $externalDb.PackId,
        '--glossary', '验收专用状态名') '验收专用状态名' 'some'
    Assert-Check ((Get-FileHash -LiteralPath $externalPath -Algorithm SHA256).Hash -ceq $externalHash) '外部内容包被查阅过程修改。'
    $checks.Add('带空格 pack-dir 可加载独立外部包及中文状态改名；全列表/查询与 Core 相同，输入文件不变')

    # 有效回放和队伍码用于区分真正的参数互斥与误入其他模式后的输入报错。
    $preset = [StreamDuel.Core.TeamPreset]::new()
    $preset.Name = '词条隔离验收'
    $preset.PackId = 'original'
    $preset.Ids.AddRange($teamA)
    $teamCode = [StreamDuel.Core.TeamCode]::Encode($preset)
    $fixtureTeams = [Collections.Generic.IList[string][]]::new(2)
    $fixtureTeams[0] = $teamA
    $fixtureTeams[1] = $teamB
    $battle = [StreamDuel.Core.Battle]::new($originalDb, $fixtureTeams, [string[]]@('甲', '乙'), [uint32]76)
    $replayPath = Join-Path $fixtures 'valid-replay.json'
    [IO.File]::WriteAllText($replayPath, $battle.Record.ToJson(), $utf8)
    $replayHash = (Get-FileHash -LiteralPath $replayPath -Algorithm SHA256).Hash
    $replayCode = [StreamDuel.Core.ShareCode]::Encode($battle.Record)
    $conflicts = @(
        @{ Name = 'calc'; Args = @('--calc', 'bell', '5', 'kei') },
        @{ Name = 'matchup'; Args = @('--matchup', ($teamA -join ','), 'vs', ($teamB -join ',')) },
        @{ Name = 'lan-host'; Args = @('--lan-host', '词条不得开房') },
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
        @{ Name = 'seed'; Args = @('--seed', '76') },
        @{ Name = 'team'; Args = @('--team', ($teamA -join ',')) },
        @{ Name = 'save-dir'; Args = @('--save-dir', 'unexpected saves') },
        @{ Name = 'report'; Args = @('--report', 'unexpected-report.md') }
    )
    foreach ($conflict in $conflicts) {
        foreach ($order in @('first', 'last')) {
            [string[]]$parameters = if ($order -eq 'first') { @('--glossary', '状态') + $conflict.Args }
                else { $conflict.Args + @('--glossary', '状态') }
            $result = Invoke-Cli ("reject-$order-" + $conflict.Name) $parameters 1
            Assert-Check ($result.Err -notmatch '未知参数') '已合并功能被错误地当成未知参数。'
            Assert-Check ($result.Err -match '--glossary|伤害分析|--lan-list.*独立') '退出 1 并非词条模式互斥校验导致。'
        }
    }
    $duplicate = Invoke-Cli 'reject-duplicate' @('--glossary', '状态', '--glossary', '人设') 1
    Assert-Check ($duplicate.Err -match '--glossary.*重复') '重复词条参数没有明确拒绝。'
    Assert-Check ((Get-FileHash -LiteralPath $replayPath -Algorithm SHA256).Hash -ceq $replayHash) '互斥参数检查修改了输入回放。'
    $checks.Add('词条与 calc/matchup、LAN/TCP、timer、队伍码/导出、share/replay 等 22 选项前后顺序均互斥；44 例及重复选项退出 1，非未知参数，无产物')
    $passed = $true
}
catch {
    $failure = $_.Exception.Message
    throw
}
finally {
    if ($restoreLocale) { [StreamDuel.Core.Loc]::Current = $previousLocale }
    $summary = [ordered]@{
        passed = $passed; failure = $failure; run_directory = $runDirectory
        cli_sha256 = (Get-FileHash -LiteralPath $cliDll -Algorithm SHA256).Hash
        core_sha256 = (Get-FileHash -LiteralPath $coreDll -Algorithm SHA256).Hash
        checks = $checks.ToArray(); reference = $references.ToArray(); invocations = $invocations.ToArray()
    }
    [IO.File]::WriteAllText((Join-Path $runDirectory 'result.json'), ($summary | ConvertTo-Json -Depth 10), $utf8)
    Write-Host "T76 CLI glossary verification: passed=$passed; $runDirectory"
}
