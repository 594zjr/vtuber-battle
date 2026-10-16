#requires -Version 7.0
param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug')

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$binary = Join-Path $repository "Tools/Cli/bin/$Configuration/net8.0"
$cli = Join-Path $binary 'Cli.dll'
$core = Join-Path $binary 'StreamDuel.Core.dll'
foreach ($path in @($cli, $core)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "请先构建 Tools/Cli -c $Configuration；脚本不自动构建。" }
}
$run = [IO.Path]::GetFullPath((Join-Path $repository ('Tools/Cli/obj/T116/' + [Guid]::NewGuid().ToString('N'))))
if (-not $run.StartsWith($repository + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw '验收产物必须位于本工作树内。'
}
[void][IO.Directory]::CreateDirectory($run)
$utf8 = [Text.UTF8Encoding]::new($false, $true)
$checks = [Collections.Generic.List[string]]::new()
$calls = [Collections.Generic.List[object]]::new()
$failure = $null

function Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "$Message 证据目录：$run" }
}

function Case-Directory([string]$Name) {
    $path = Join-Path $run $Name
    [void][IO.Directory]::CreateDirectory($path)
    return $path
}

function Invoke-Cli([string]$Name, [string[]]$Parameters, [string[]]$Lines = @(), [int]$Exit = 0, [string]$Executable = $cli,
    [string]$WorkDirectory = '') {
    $evidence = Case-Directory $Name
    $work = if ($WorkDirectory.Length -eq 0) { $evidence } else { $WorkDirectory }
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = 'dotnet'
    $start.ArgumentList.Add($Executable)
    foreach ($argument in $Parameters) { $start.ArgumentList.Add($argument) }
    $start.WorkingDirectory = $work
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardInputEncoding = $utf8
    $start.StandardOutputEncoding = $utf8
    $start.StandardErrorEncoding = $utf8
    $start.Environment['TEMP'] = $run
    $start.Environment['TMP'] = $run
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    $started = $false
    try {
        $started = $process.Start()
        Check $started "$Name 无法启动。"
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if ($Lines.Count -gt 0) { $process.StandardInput.Write(($Lines -join "`n") + "`n") }
        $process.StandardInput.Close()
        Check ($process.WaitForExit(60000)) "$Name 超过 60 秒未退出。"
        Check ($stdout.Wait(5000) -and $stderr.Wait(5000)) "$Name 输出未结束。"
        $result = [pscustomobject]@{ Out = $stdout.Result; Err = $stderr.Result; Exit = $process.ExitCode; Work = $work }
        [IO.File]::WriteAllText((Join-Path $evidence 'stdout.log'), $result.Out, $utf8)
        [IO.File]::WriteAllText((Join-Path $evidence 'stderr.log'), $result.Err, $utf8)
        $calls.Add([ordered]@{ name = $Name; parameters = $Parameters; exit = $result.Exit; expected = $Exit })
        Check ($result.Exit -eq $Exit) "$Name 退出码应为 $Exit，实际 $($result.Exit)。"
        if ($Exit -eq 0) { Check ($result.Err.Length -eq 0) "$Name 成功时出现标准错误。" }
        else { Check ($result.Err -match '错误：') "$Name 缺少中文错误。" }
        return $result
    }
    finally {
        if ($started -and -not $process.HasExited) { $process.Kill($true); [void]$process.WaitForExit(5000) }
        $process.Dispose()
    }
}

function Tree([string]$Directory) {
    if (-not (Test-Path -LiteralPath $Directory)) { return '<不存在>' }
    return (@(Get-ChildItem -LiteralPath $Directory -Force -Recurse | Sort-Object FullName | ForEach-Object {
        $relative = [IO.Path]::GetRelativePath($Directory, $_.FullName)
        if ($_.PSIsContainer) { 'D:' + $relative }
        else { 'F:' + $relative + ':' + $_.LastWriteTimeUtc.Ticks + ':' + (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    }) -join "`n")
}

function New-Source($Db, [uint32]$Seed = 116, [int]$OfferedSide = -1) {
    $ids = [string[]]@($Db.Streamers | Select-Object -First 6 | ForEach-Object { $_.Id })
    Check ($ids.Length -eq 6) '夹具需要六位主播。'
    $teams = [Collections.Generic.IList[string][]]::new(2)
    $teams[0] = $ids
    $teams[1] = [string[]]$ids.Clone()
    $options = [StreamDuel.Core.BattleOptions]::new()
    $options.Seed = $Seed
    $battle = [StreamDuel.Core.Battle]::new($Db, $teams, [string[]]@('原始甲', '原始乙'), $options)
    for ($i = 0; $i -lt 3; $i++) {
        $battle.ResolveTurn([StreamDuel.Core.BattleAction]::Pass(), [StreamDuel.Core.BattleAction]::Pass())
        if ($i -eq 0 -and $OfferedSide -ge 0) { $battle.OfferDraw($OfferedSide) }
    }
    $battle.Surrender(1)
    Check ($battle.Phase -eq [StreamDuel.Core.Phase]::Over) '来源夹具未结束。'
    return $battle
}

function Read-OnlyBranchRecord([string]$Directory) {
    $files = @(Get-ChildItem -LiteralPath (Join-Path $Directory 'saves/replay-library') -Filter '*.json' -File | Where-Object { $_.Name -ne 'index.json' })
    Check ($files.Count -eq 1) '单局接手应只新增一份录像。'
    $record = [StreamDuel.Core.BattleRecord]::FromJson([IO.File]::ReadAllText($files[0].FullName, $utf8))
    Check $record.IsBranch '新录像缺少分支标记。'
    $metadata = [IO.File]::ReadAllText($files[0].FullName, $utf8) | ConvertFrom-Json
    Check ($metadata.library.note.Contains($record.Branch.SourceHash) -and $metadata.library.note.Contains('回合')) '新录像备注缺少来源与回合。'
    return $record
}

function Resolve-HumanSwitch($Battle, [int]$Side, $Level) {
    Check ($Battle.LegalFor($Side).Switches.Contains(1)) '夹具的二号队友不能换入。'
    $human = [StreamDuel.Core.BattleAction]::SwitchTo(1)
    $foe = [StreamDuel.Core.Ai]::Choose($Battle, 1 - $Side, $Level)
    if ($Side -eq 0) { $Battle.ResolveTurn($human, $foe) }
    else { $Battle.ResolveTurn($foe, $human) }
}

try {
    $assembly = [Reflection.Assembly]::LoadFrom($core)
    Check ([StringComparer]::OrdinalIgnoreCase.Equals($assembly.Location, $core)) '请在未加载其他 Core 的新 PowerShell 进程运行。'
    $content = Join-Path $binary 'Content'
    $rules = [IO.File]::ReadAllText((Join-Path $content 'StreamDuelRules.json'), $utf8)
    $pack = [IO.File]::ReadAllText((Join-Path $content 'Packs/original.json'), $utf8)
    $policy = [StreamDuel.Core.PackPolicy]::Load((Join-Path $content 'PackPolicy.json'))
    $db = [StreamDuel.Core.ContentDb]::FromJson($rules, $pack, $policy)
    $sourceBattle = New-Source $db
    $source = $sourceBattle.Record
    $sourceJson = $source.ToJson()
    $fixtures = Case-Directory 'fixtures'
    $sourcePath = Join-Path $fixtures 'source.json'
    [IO.File]::WriteAllText($sourcePath, $sourceJson, $utf8)
    $sourceTree = Tree $fixtures
    $sourceIdentity = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($utf8.GetBytes($sourceJson))).ToLowerInvariant()
    $normal = [StreamDuel.Core.AiLevel]::Normal
    $baseArgs = @('--branch', $sourcePath, '--turn', '2', '--side', '0')
    $help = Invoke-Cli 'help' @('--help')
    Check ($help.Out.Contains('--branch') -and $help.Out.Contains('--ai normal|hard|expert')) '帮助缺少接手命令。'

    foreach ($side in 0, 1) {
        $expected = [StreamDuel.Core.ReplayBranch]::Start($db, $source, 2, $side, $normal)
        Resolve-HumanSwitch $expected $side $normal
        $expected.Surrender($side)
        $parameters = @('--branch', $sourcePath, '--turn', '2', '--side', [string]$side)
        $first = Invoke-Cli "side-$side-a" $parameters @('7', '/surrender')
        $second = Invoke-Cli "side-$side-b" $parameters @('7', '/surrender')
        Check ($first.Out -ceq $second.Out) "第 $side 方相同输入的完整输出不一致。"
        $a = Read-OnlyBranchRecord $first.Work
        $b = Read-OnlyBranchRecord $second.Work
        Check ($a.ToJson() -ceq $b.ToJson() -and $a.ToJson() -ceq $expected.Record.ToJson()) "第 $side 方选招、AI顺序或录像不可复现。"
        Check ($a.Branch.SourceHash -ceq $sourceIdentity -and $a.Branch.Turn -eq 2 -and $a.Branch.HumanSide -eq $side -and $a.Branch.Level -eq $normal) '分支来源信息错误。'
        Check ($a.Branch.Seed -eq [StreamDuel.Core.ReplayBranch]::DeriveSeed($source.Seed, 2)) '派生种子错误。'
        $replayed = [StreamDuel.Core.Replay]::Run($db, $a)
        Check ($replayed.IsBranch -and $replayed.StateHash() -eq $expected.StateHash() -and $replayed.Winner -eq 1 - $side) '新录像不能独立重放到正确终局。'
        foreach ($result in @($first, $second)) {
            Check (-not (Test-Path -LiteralPath (Join-Path $result.Work 'saves/profile.json'))) '接手创建了正式档案。'
            Check (-not (Test-Path -LiteralPath (Join-Path $result.Work 'saves/replays'))) '接手写入了正式回放目录。'
            Check (-not $result.Out.Contains('本包战绩：')) '接手输出了正式战绩。'
        }
    }
    Check ($source.ToJson() -ceq $sourceJson -and (Tree $fixtures) -ceq $sourceTree) '接手修改了来源对象或文件。'
    $checks.Add('两侧手动换人后投降：完整输出与录像逐字一致、来源身份正确、独立回放终局一致、不计正式战绩')

    foreach ($level in 'hard', 'expert') {
        $levelEnum = [Enum]::Parse([StreamDuel.Core.AiLevel], $level, $true)
        $expected = [StreamDuel.Core.ReplayBranch]::Start($db, $source, 2, 1, $levelEnum)
        Resolve-HumanSwitch $expected 1 $levelEnum
        $expected.Surrender(1)
        $result = Invoke-Cli "ai-$level" @('--branch', $sourcePath, '--turn', '2', '--side', '1', '--ai', $level) @('7', '/surrender')
        $actual = Read-OnlyBranchRecord $result.Work
        Check ($actual.ToJson() -ceq $expected.Record.ToJson()) "$level 参数未用于真实电脑选择。"
    }
    $checks.Add('hard/expert 参数实际参与电脑选择与分支来源记录')

    # 用真实外部内容包使主动换入者被一击击倒，再从双方的实际补位菜单继续。
    $external = Case-Directory 'external-pack'
    $custom = $pack | ConvertFrom-Json -AsHashtable
    $custom.id = 't116_cli_fixture'
    $custom.name = '接手验收原创包'
    foreach ($streamer in $custom.streamers) {
        $streamer.stats.hp = 1
        $streamer.persona = 'pure'
        $streamer.skills = @('quick_mic', 'aim_burst', 'roast_chain', 'sketch')
        $streamer.fifth = 'endgame'
    }
    $customJson = $custom | ConvertTo-Json -Depth 100 -Compress
    [IO.File]::WriteAllText((Join-Path $external 'custom.json'), $customJson, $utf8)
    $customDb = [StreamDuel.Core.ContentDb]::FromJson($rules, $customJson, $policy)
    foreach ($side in 0, 1) {
        $found = $false
        for ($seed = 1; $seed -le 32; $seed++) {
            $fixture = New-Source $customDb $seed
            $expected = [StreamDuel.Core.ReplayBranch]::Start($customDb, $fixture.Record, 2, $side, $normal)
            Resolve-HumanSwitch $expected $side $normal
            if ($expected.Phase -eq [StreamDuel.Core.Phase]::Replace -and $expected.Need[$side] -and -not $expected.Need[1 - $side]) {
                $found = $true
                break
            }
        }
        Check $found '没有找到真实击倒后的补位夹具。'
        $expected.Replace($side, 2)
        $expected.Surrender($side)
        $fixturePath = Join-Path $external "replace-side-$side.replay"
        [IO.File]::WriteAllText($fixturePath, $fixture.Record.ToJson(), $utf8)
        $before = Tree $external
        $result = Invoke-Cli "replacement-$side" @('--branch', $fixturePath, '--turn', '2', '--side', [string]$side, '--pack-dir', $external) @('7', '3', '/surrender')
        Check ($result.Out.Contains('请选择补位号位')) '真实 CLI 未进入人类补位菜单。'
        $actual = Read-OnlyBranchRecord $result.Work
        Check ($actual.ToJson() -ceq $expected.Record.ToJson()) '补位操作给错了人类一方或改变了后续记录。'
        Check ([StreamDuel.Core.Replay]::Run($customDb, $actual).StateHash() -eq $expected.StateHash()) '补位分支无法独立重放。'
        Check ((Tree $external) -ceq $before) '外部内容或原录像被修改。'
    }
    $checks.Add('外部包自动加载，双方实际击倒/补位菜单和完整录像均与 Core 独立执行一致')

    $drawDirectory = Case-Directory 'draw-sources'
    foreach ($humanSide in 0, 1) {
        foreach ($offeredSide in 0, 1) {
            $drawSource = New-Source $db 116 $offeredSide
            $path = Join-Path $drawDirectory "draw-$offeredSide.json"
            [IO.File]::WriteAllText($path, $drawSource.Record.ToJson(), $utf8)
            $before = Tree $drawDirectory
            $expected = [StreamDuel.Core.ReplayBranch]::Start($db, $drawSource.Record, 2, $humanSide, $normal)
            Check ($expected.HasDrawOffer($offeredSide)) '接手未保留待答求和夹具。'
            $parameters = @('--branch', $path, '--turn', '2', '--side', [string]$humanSide)
            if ($humanSide -eq $offeredSide) {
                $accepted = [StreamDuel.Core.Ai]::AcceptDraw($expected, 1 - $humanSide)
                Check $accepted '均势待答求和夹具应被电脑接受。'
                $expected.RespondDraw(1 - $humanSide, $accepted)
                $result = Invoke-Cli "inherited-ai-$humanSide" $parameters
                Check ($result.Out.Contains('电脑同意继承的和局提议')) '电脑没有回应来源遗留的人类求和。'
            }
            else {
                $expected.RespondDraw($humanSide, $true)
                $result = Invoke-Cli "inherited-human-accept-$humanSide" $parameters @('/draw accept')
                Check ($result.Out.Contains('对方有尚未回应的和局提议')) '人类没有收到待答求和提示。'
                $rejectExpected = [StreamDuel.Core.ReplayBranch]::Start($db, $drawSource.Record, 2, $humanSide, $normal)
                $rejectExpected.RespondDraw($humanSide, $false)
                Resolve-HumanSwitch $rejectExpected $humanSide $normal
                $rejectExpected.Surrender($humanSide)
                $rejected = Invoke-Cli "inherited-human-reject-$humanSide" $parameters @('/draw reject', '7', '/surrender')
                $rejectRecord = Read-OnlyBranchRecord $rejected.Work
                Check ($rejectRecord.ToJson() -ceq $rejectExpected.Record.ToJson()) '拒绝历史求和后未继续原有选择，或回应方错误。'
            }
            $actual = Read-OnlyBranchRecord $result.Work
            Check ($actual.ToJson() -ceq $expected.Record.ToJson()) '继承求和的真实回应未进入新录像。'
            Check ([StreamDuel.Core.Replay]::Run($db, $actual).StateHash() -eq $expected.StateHash()) '继承求和分支重放不一致。'
            Check ((Tree $drawDirectory) -ceq $before) '回应继承求和改变了原录像。'
        }
    }
    $checks.Add('两侧继承待答求和：电脑自动真实回应；人类可接受或拒绝后继续；新事件可独立重放')

    $quit = Invoke-Cli 'quit' $baseArgs @('q')
    $eof = Invoke-Cli 'eof' $baseArgs
    foreach ($result in @($quit, $eof)) {
        Check ($result.Out.Contains('已退出，对局未完成') -and -not (Test-Path -LiteralPath (Join-Path $result.Work 'saves'))) 'q/EOF 创建了存档或没有退出。'
    }
    $checks.Add('q/EOF 不创建存档目录或保存未完成记录')

    $libraryWork = Case-Directory 'library-id'
    $libraryPath = Join-Path $libraryWork 'saves/replay-library'
    $fixedClock = [Func[DateTimeOffset]]{ [DateTimeOffset]::new(2000, 1, 1, 0, 0, 0, [TimeSpan]::Zero) }
    $library = [StreamDuel.Core.ReplayLibrary]::new($libraryPath, [int]::MaxValue, $fixedClock, $policy)
    $summary = [StreamDuel.Core.BattleSummary]::From($sourceBattle)
    $id = $library.Save($source, $summary, $null)
    for ($i = 1; $i -lt 200; $i++) { $null = $library.Save($source, $summary, $null) }
    $oldFiles = @(Get-ChildItem -LiteralPath $libraryPath -Filter '*.json' -File | Where-Object { $_.Name -ne 'index.json' })
    Check ($oldFiles.Count -eq 200) '容量边界夹具没有 200 份真实录像。'
    $oldHashes = @{}
    foreach ($file in $oldFiles) { $oldHashes[$file.FullName] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash }
    $profilePath = Join-Path $libraryWork 'saves/profile.json'
    $profile = [StreamDuel.Core.Profile]::new()
    $profile.Record($sourceBattle, 0)
    $defaultRating = [StreamDuel.Core.PlayerRating]::new()
    $defaultRating.Rating = 1660
    $defaultRating.Games = 13
    $profile.Ratings.Add([StreamDuel.Core.Profile]::DefaultRatingFormat, $defaultRating)
    $otherRating = [StreamDuel.Core.PlayerRating]::new()
    $otherRating.Rating = 1790
    $otherRating.Games = 21
    $profile.Ratings.Add('保留赛制', $otherRating)
    $profileJson = $profile.ToJson()
    Check ([StreamDuel.Core.Profile]::FromJson($profileJson).Ratings.Count -eq 2) '正式档案夹具没有保存两个赛制的排位数据。'
    [IO.File]::WriteAllText($profilePath, $profileJson, $utf8)
    $profileHash = (Get-FileHash -LiteralPath $profilePath -Algorithm SHA256).Hash
    $before = Tree (Join-Path $libraryWork 'saves')
    $null = Invoke-Cli 'library-id-read' @('--branch', $id, '--turn', '2', '--side', '0') @('q') -WorkDirectory $libraryWork
    Check ((Tree (Join-Path $libraryWork 'saves')) -ceq $before) '按库编号读取后 q 改写了目录、索引、锁或源文件。'
    $null = Invoke-Cli 'library-id-save' @('--branch', $id, '--turn', '2', '--side', '0') @('/surrender') -WorkDirectory $libraryWork
    Check (@(Get-ChildItem -LiteralPath $libraryPath -Filter '*.json' -File | Where-Object { $_.Name -ne 'index.json' }).Count -eq 201) '接手保存触发了旧录像容量清理。'
    foreach ($file in $oldFiles) { Check ((Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash -ceq $oldHashes[$file.FullName]) '按库编号接手修改或删除了旧录像。' }
    Check ([IO.File]::ReadAllText($profilePath, $utf8) -ceq $profileJson) '接手修改了已有正式档案。'
    Check ((Get-FileHash -LiteralPath $profilePath -Algorithm SHA256).Hash -ceq $profileHash) '接手改变了含正式战绩和两个赛制排位数据的档案字节。'
    $checks.Add('库编号来源纯读、含正式战绩及两个赛制 ratings 的原档案字节不变、200 份容量边界追加后保留全部旧录像')

    foreach ($reserved in 'index.json', '.library-write.tmp', '.library.lock', 'INDEX.JSON') {
        $name = 'reserved-' + $reserved.Replace('.', '_')
        $work = Case-Directory $name
        $libraryPath = Join-Path $work 'saves/replay-library'
        [void][IO.Directory]::CreateDirectory($libraryPath)
        $path = Join-Path $libraryPath $reserved
        [IO.File]::WriteAllText($path, $sourceJson, $utf8)
        $before = Tree (Join-Path $work 'saves')
        $result = Invoke-Cli $name @('--branch', $path, '--turn', '2', '--side', '0') @('/surrender') 1
        Check ($result.Out.Length -eq 0 -and $result.Err.Contains('索引') -and (Tree (Join-Path $work 'saves')) -ceq $before) '保留管理路径在开局前未拒绝，或源文件被修改。'
    }
    if ($IsWindows) {
        foreach ($prefix in '\\?\', '\\.\') {
            $prefixName = if ($prefix.Contains('?')) { 'extended' } else { 'device' }
            foreach ($direction in 'source', 'library') {
                foreach ($reserved in 'index.json', '.library-write.tmp', '.library.lock') {
                    $name = "alias-$prefixName-$direction-" + $reserved.Replace('.', '_')
                    $work = Case-Directory $name
                    $save = Join-Path $work 'saves'
                    $libraryPath = Join-Path $save 'replay-library'
                    [void][IO.Directory]::CreateDirectory($libraryPath)
                    $path = Join-Path $libraryPath $reserved
                    [IO.File]::WriteAllText($path, $sourceJson, $utf8)
                    $before = Tree $save
                    $readPath = if ($direction -eq 'source') { $prefix + $path } else { $path }
                    $savePath = if ($direction -eq 'library') { $prefix + $save } else { $save }
                    $result = Invoke-Cli $name @('--branch', $readPath, '--turn', '2', '--side', '0', '--save-dir', $savePath) @('/surrender') 1
                    Check ($result.Out.Length -eq 0 -and $result.Err.Contains('索引') -and (Tree $save) -ceq $before) 'Windows 路径别名绕过保留文件保护，或改变了来源内容、mtime、目录。'
                }
            }
            $before = Tree $fixtures
            $result = Invoke-Cli "safe-$prefixName-source" @('--branch', ($prefix + $sourcePath), '--turn', '2', '--side', '0') @('/surrender')
            $actual = Read-OnlyBranchRecord $result.Work
            $expected = [StreamDuel.Core.ReplayBranch]::Start($db, $source, 2, 0, $normal)
            $expected.Surrender(0)
            Check ($actual.ToJson() -ceq $expected.Record.ToJson() -and [StreamDuel.Core.Replay]::Run($db, $actual).StateHash() -eq $expected.StateHash()) '安全的扩展来源路径不能完成独立接手和回放。'
            Check ((Tree $fixtures) -ceq $before) '安全扩展来源的原文件被改动。'
        }
        $checks.Add('Windows 两种扩展前缀、来源/目标两方向、三个保留文件全部拒绝；安全扩展来源仍可完成且不改原文件')
    }
    $junctionWork = Case-Directory 'junction-source'
    $junctionTarget = Join-Path $junctionWork 'saves/replay-library'
    [void][IO.Directory]::CreateDirectory($junctionTarget)
    [IO.File]::WriteAllText((Join-Path $junctionTarget '.library-write.tmp'), $sourceJson, $utf8)
    $junction = Join-Path $junctionWork 'source-alias'
    $junctionCreated = $false
    try { $null = New-Item -ItemType Junction -Path $junction -Target $junctionTarget; $junctionCreated = $true }
    catch { $checks.Add('目录联接夹具无法创建，未冒称实际链接验收：' + $_.Exception.Message) }
    if ($junctionCreated) {
        $before = Tree (Join-Path $junctionWork 'saves')
        $result = Invoke-Cli 'junction-source' @('--branch', (Join-Path $junction '.library-write.tmp'), '--turn', '2', '--side', '0') @('/surrender') 1
        Check ($result.Out.Length -eq 0 -and $result.Err.Contains('重解析点') -and (Tree (Join-Path $junctionWork 'saves')) -ceq $before) '源文件的目录联接别名未拒绝或改动了来源。'
        $checks.Add('目录联接指向目标库管理文件的来源别名在开局前拒绝，源文件未改动')
    }
    $checks.Add('目标录像库索引、临时文件、锁及 Windows 大小写别名均在开局前拒绝')

    $utf8Path = Join-Path $run 'invalid-utf8.json'
    [IO.File]::WriteAllBytes($utf8Path, [byte[]]@(0xc3, 0x28))
    $result = Invoke-Cli 'invalid-utf8' @('--branch', $utf8Path, '--turn', '2', '--side', '0') @() 1
    Check ($result.Err.Contains('来源录像不是有效的 UTF-8')) '非法 UTF-8 未提供中文读取语境。'
    $blockedWork = Case-Directory 'save-blocked'
    [void][IO.Directory]::CreateDirectory((Join-Path $blockedWork 'saves'))
    $blockedPath = Join-Path $blockedWork 'saves/replay-library'
    [IO.File]::WriteAllText($blockedPath, '保留的普通文件', $utf8)
    $before = Tree (Join-Path $blockedWork 'saves')
    $result = Invoke-Cli 'save-blocked' $baseArgs @('/surrender') 1
    Check ($result.Err.Contains('无法保存接手录像') -and (Tree (Join-Path $blockedWork 'saves')) -ceq $before) '保存失败没有中文语境或改写了原有文件。'
    $checks.Add('非法 UTF-8、输出目录被普通文件占用均提供明确中文失败语境且不改原有文件')

    $invalid = @(
        @('--branch', $sourcePath), @('--branch', $sourcePath, '--turn', '2'), @('--turn', '2'), @('--side', '0'), @('--ai', 'normal'),
        @('--branch', '', '--turn', '2', '--side', '0'), @('--branch', $sourcePath, '--turn', '0', '--side', '0'),
        @('--branch', $sourcePath, '--turn', '-1', '--side', '0'), @('--branch', $sourcePath, '--turn', '2147483648', '--side', '0'),
        @('--branch', $sourcePath, '--turn', '1.5', '--side', '0'), @('--branch', $sourcePath, '--turn', '2', '--side', '2')
    )
    $invalid += ,($baseArgs + @('--ai', 'easy'))
    foreach ($extra in @(@('--auto'), @('--hash'), @('--pack', 'original'), @('--level', 'normal'), @('--seed', '1'),
        @('--replay', $sourcePath), @('--replay-best-effort'), @('--host', '0'), @('--hotseat'), @('--ban'), @('--lead'),
        @('--report', 'should-not-exist.md'), @('--tournament-report', 'missing.json'), @('--json'), @('--replays'),
        @('--card', 'bell'), @('--cards', 'original'),
        @('--watch', '127.0.0.1:9'), @('--spectate-port', '0'), @('--host', '0', '--spectate-port', '0'),
        @('--branch', $sourcePath), @('--turn', '2'), @('--side', '0'), @('--ai', 'hard', '--ai', 'normal'),
        @('--save-dir', 'saves', '--save-dir', 'saves'), @('--pack-dir', $external, '--pack-dir', $external))) {
        $invalid += ,($baseArgs + $extra)
    }
    foreach ($extra in @(@('--format', 'single'), @('--tournament', 'must-not-exist.json', '--format', 'double'))) {
        $invalid += ,($baseArgs + $extra)
        $invalid += ,($extra + $baseArgs)
    }
    for ($i = 0; $i -lt $invalid.Count; $i++) {
        $result = Invoke-Cli "invalid-$i" $invalid[$i] @() 1
        Check ($result.Out.Length -eq 0 -and -not (Test-Path -LiteralPath (Join-Path $result.Work 'saves'))) '参数错误仍开始了接手或创建了存档。'
        Check (-not (Test-Path -LiteralPath (Join-Path $result.Work 'must-not-exist.json'))) '互斥参数创建了赛事文件。'
        if ($invalid[$i] -contains '--card' -or $invalid[$i] -contains '--cards') {
            Check ($result.Err.Contains('不能与') -and $result.Err.Contains('--branch')) '资料卡与接手组合未由独立模式互斥校验拒绝。'
        }
        if ($invalid[$i] -contains '--watch') {
            Check ($result.Err.Contains('--watch 只读观战，不支持') -and $result.Err.Contains('--branch')) '观战与接手组合未在连接之前由模式校验拒绝。'
        }
        if ($invalid[$i] -contains '--spectate-port') {
            if ($invalid[$i] -contains '--host') {
                Check ($result.Err.Contains('--branch 是独立') -and $result.Err.Contains('不能与') -and $result.Err.Contains('--spectate-port')) '主机观战端口与接手组合未由独立模式互斥校验拒绝。'
            }
            else {
                Check ($result.Err.Contains('--spectate-port 只能与 --host 或 --lan-host 一起使用')) '接手模式的孤立观战端口未明确拒绝。'
            }
        }
    }
    $checks.Add('必填、范围、空值、重复参数与独立模式互斥全部在开局前拒绝')

    $badDirectory = Case-Directory 'bad-records'
    $bad = [StreamDuel.Core.BattleRecord]::FromJson($sourceJson)
    $bad.Version = [StreamDuel.Core.BattleRecord]::CurrentVersion + 1
    [IO.File]::WriteAllText((Join-Path $badDirectory 'future.json'), $bad.ToJson(), $utf8)
    $bad = [StreamDuel.Core.BattleRecord]::FromJson($sourceJson)
    $bad.ContentHash = '0000000000000000'
    [IO.File]::WriteAllText((Join-Path $badDirectory 'fingerprint.json'), $bad.ToJson(), $utf8)
    $bad = [StreamDuel.Core.BattleRecord]::FromJson($sourceJson)
    $bad.PackId = 'missing_t116_pack'
    [IO.File]::WriteAllText((Join-Path $badDirectory 'missing-pack.json'), $bad.ToJson(), $utf8)
    [IO.File]::WriteAllText((Join-Path $badDirectory 'broken.json'), '{', $utf8)
    $before = Tree $badDirectory
    foreach ($name in 'future', 'fingerprint', 'missing-pack', 'broken', 'missing-file') {
        $result = Invoke-Cli "record-$name" @('--branch', (Join-Path $badDirectory "$name.json"), '--turn', '2', '--side', '0') @() 1
        Check (-not (Test-Path -LiteralPath (Join-Path $result.Work 'saves'))) '无效来源创建了存档。'
        if ($name -eq 'future') { Check ($result.Err.Contains('更新的版本')) '未来版本未按兼容门禁拒绝。' }
        if ($name -eq 'fingerprint') { Check ($result.Err.Contains('指纹')) '内容不一致未拒绝严格接手。' }
        if ($name -eq 'missing-file') { Check ($result.Err.Contains('无法读取接手来源录像')) '缺文件未提供中文读取语境。' }
    }
    $result = Invoke-Cli 'out-of-range' @('--branch', $sourcePath, '--turn', '4', '--side', '0') @() 1
    Check ($result.Err.Contains('回合') -and -not (Test-Path -LiteralPath (Join-Path $result.Work 'saves'))) '超出录像长度未给出中文回合错误。'
    $result = Invoke-Cli 'missing-id' @('--branch', '20000101_000000_0000000_116', '--turn', '2', '--side', '0') @() 1
    Check (-not (Test-Path -LiteralPath (Join-Path $result.Work 'saves'))) '缺少库编号时创建了录像目录或锁。'
    Check ((Tree $badDirectory) -ceq $before) '拒绝无效录像时改写了来源。'
    $checks.Add('未来版本、内容指纹变化、缺包、坏 JSON、缺文件/库编号、超出回合全部拒绝且不创建存档')

    # 下架门禁在独立二进制副本内验证，不修改共用构建产物的政策。
    $policyBinary = Case-Directory 'policy-binary'
    Copy-Item -Path (Join-Path $binary '*') -Destination $policyBinary -Recurse
    [IO.File]::WriteAllText((Join-Path $policyBinary 'Content/PackPolicy.json'), '{"disabledPacks":["original"],"reason":{"original":"接手验收下架"}}', $utf8)
    $result = Invoke-Cli 'disabled-pack' $baseArgs @() 1 (Join-Path $policyBinary 'Cli.dll')
    Check ($result.Err.Contains('已下架') -and -not (Test-Path -LiteralPath (Join-Path $result.Work 'saves'))) '接手绕过了下架门禁。'
    Check ($source.ToJson() -ceq $sourceJson -and (Tree $fixtures) -ceq $sourceTree) '最终来源对象或文件发生变化。'
    $checks.Add('独立政策副本下架拒绝；全部命令后来源对象和文件保持不变')
}
catch { $failure = $_.ToString(); throw }
finally {
    $report = [ordered]@{ success = $null -eq $failure; failure = $failure; checks = @($checks); invocations = @($calls) }
    [IO.File]::WriteAllText((Join-Path $run 'report.json'), ($report | ConvertTo-Json -Depth 8), $utf8)
    Write-Host "T116 CLI 验收证据：$run"
}
Write-Host "通过 $($checks.Count) 组检查、$($calls.Count) 次真实 CLI 调用。"
