#requires -Version 7.0
param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug')

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$output = Join-Path $repository "Tools/Cli/bin/$Configuration/net8.0"
$cli = Join-Path $output 'Cli.dll'
if (-not [IO.File]::Exists($cli)) { throw '请先构建 Tools/Cli；此脚本不执行构建。' }
$run = Join-Path $repository ('Tools/Cli/obj/T112/cli-' + [Guid]::NewGuid().ToString('N'))
$fixtures = Join-Path $run 'fixtures'
[void][IO.Directory]::CreateDirectory($fixtures)
$utf8 = [Text.UTF8Encoding]::new($false)
$invocations = [Collections.Generic.List[object]]::new()

function Assert-Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "$Message；证据：$run" }
}

function Write-Fixture([string]$Name, [string]$Json) {
    $path = Join-Path $fixtures "$Name.json"
    [IO.File]::WriteAllText($path, $Json, $utf8)
    return $path
}

function Tree-Snapshot([string]$Directory) {
    if (-not [IO.Directory]::Exists($Directory)) { return '<missing>' }
    $lines = foreach ($item in Get-ChildItem -LiteralPath $Directory -Recurse -Force | Sort-Object FullName) {
        $relative = [IO.Path]::GetRelativePath($Directory, $item.FullName)
        if ($item.PSIsContainer) { 'directory:' + $relative }
        else { 'file:' + $relative + ':' + $item.LastWriteTimeUtc.Ticks + ':' + (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash }
    }
    return $lines -join "`n"
}

function Invoke-Cli([string]$Name, [string[]]$Parameters, [int]$ExpectedExit = 0, [string]$Executable = $cli) {
    $start = [Diagnostics.ProcessStartInfo]::new('dotnet')
    $start.ArgumentList.Add($Executable)
    foreach ($parameter in $Parameters) { $start.ArgumentList.Add($parameter) }
    $start.WorkingDirectory = $run
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = $utf8
    $start.StandardErrorEncoding = $utf8
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    try {
        [void]$process.Start()
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $process.StandardInput.Close()
        if (-not $process.WaitForExit(30000)) {
            $process.Kill($true)
            $process.WaitForExit()
            throw "$Name 超过 30 秒未退出。"
        }
        $result = [pscustomobject]@{ Out = $stdout.GetAwaiter().GetResult(); Err = $stderr.GetAwaiter().GetResult(); Exit = $process.ExitCode }
        [IO.File]::WriteAllText((Join-Path $run "$Name.stdout.log"), $result.Out, $utf8)
        [IO.File]::WriteAllText((Join-Path $run "$Name.stderr.log"), $result.Err, $utf8)
        $invocations.Add([ordered]@{ name = $Name; parameters = $Parameters; exit = $result.Exit })
        Assert-Check ($result.Exit -eq $ExpectedExit) "$Name 预期退出 $ExpectedExit，实际 $($result.Exit)"
        if ($ExpectedExit -eq 0) { Assert-Check ($result.Err -eq '') "$Name 成功时不应有 stderr" }
        else { Assert-Check ($result.Err.StartsWith('错误：') -and -not $result.Err.Contains(' at ')) "$Name 应输出中文错误而非堆栈" }
        return $result
    }
    finally { $process.Dispose() }
}

try {
    Add-Type -Path (Join-Path $output 'StreamDuel.Core.dll')
    $content = Join-Path $output 'Content'
    $rules = [IO.File]::ReadAllText((Join-Path $content 'StreamDuelRules.json'))
    $db = [StreamDuel.Core.ContentDb]::FromJson($rules, [IO.File]::ReadAllText((Join-Path $content 'Packs/original.json')))
    $teams = [Collections.Generic.IList[string][]]@(
        [Collections.Generic.List[string]]@('bell', 'enka', 'shiori', 'lumi', 'hane', 'momo'),
        [Collections.Generic.List[string]]@('kei', 'yuan', 'nyako', 'buck', 'kasumi', 'bee'))
    $battle = [StreamDuel.Core.Battle]::new($db, $teams, [string[]]@('你', '对手'), [uint32]112)
    $pass = [StreamDuel.Core.BattleAction]::Pass()
    $battle.ResolveTurn($pass, $pass)
    $short = Write-Fixture 'one-turn' $battle.Record.ToJson()
    $battle.ResolveTurn($pass, $pass)
    $battle.Surrender(1)
    $original = $battle.Record.ToJson()
    $same = Write-Fixture 'two-turns' $original
    $changed = [StreamDuel.Core.BattleRecord]::FromJson($original)
    $changed.Events[1].A0 = [StreamDuel.Core.BattleAction]::SwitchTo(1)
    $different = Write-Fixture 'changed-action' $changed.ToJson()
    $seed = [StreamDuel.Core.BattleRecord]::FromJson($original)
    $seed.Seed++
    $seedPath = Write-Fixture 'seed' $seed.ToJson()
    $future = [StreamDuel.Core.BattleRecord]::FromJson($original)
    $future.Version = [StreamDuel.Core.BattleRecord]::CurrentVersion + 1
    $futurePath = Write-Fixture 'future' $future.ToJson()
    $missingPack = [StreamDuel.Core.BattleRecord]::FromJson($original)
    $missingPack.PackId = 't112_missing_pack'
    $missingPackPath = Write-Fixture 'missing-pack' $missingPack.ToJson()
    $oldContent = [StreamDuel.Core.BattleRecord]::FromJson($original)
    $oldContent.ContentHash = '0123456789abcdef'
    $oldContentPath = Write-Fixture 'old-content' $oldContent.ToJson()
    $dirtyNames = [StreamDuel.Core.BattleRecord]::FromJson($original)
    $dirtyNames.Names = [string[]]@("玩家`e[31m恶意换行`n伪造输出", "对手$([char]0x202E)名字")
    $dirtyPath = Write-Fixture 'dirty-names' $dirtyNames.ToJson()
    $bad = Write-Fixture 'broken' '{invalid'
    $shape = Write-Fixture 'bad-shape' '{"seed":1,"packId":"original","teams":[],"events":[]}'
    $fanDb = [StreamDuel.Core.ContentDb]::FromJson($rules, [IO.File]::ReadAllText((Join-Path $content 'Packs/fan_asoul_vr.json')))
    $fanIds = [Collections.Generic.List[string]]@($fanDb.Streamers | ForEach-Object { $_.Id } | Sort-Object | Select-Object -First 6)
    $fanTeams = [Collections.Generic.IList[string][]]@($fanIds, [Collections.Generic.List[string]]@($fanIds))
    $fanBattle = [StreamDuel.Core.Battle]::new($fanDb, $fanTeams, [string[]]@('你', '对手'), [uint32]112)
    $fanPath = Write-Fixture 'fan-opening' $fanBattle.Record.ToJson()

    $save = Join-Path $run 'existing-save'
    $library = [StreamDuel.Core.ReplayLibrary]::new((Join-Path $save 'replay-library'))
    $id = $library.Save($battle.Record, [StreamDuel.Core.BattleSummary]::From($battle), '只读验收')
    $missingSave = Join-Path $run 'missing-save'
    $fixtureBefore = Tree-Snapshot $fixtures
    $saveBefore = Tree-Snapshot $save
    $first = Invoke-Cli '01-self' @('--replay-diff', $same, $same)
    Assert-Check ($first.Out.Contains('开局相同') -and $first.Out.Contains('未发现差异')) '自比应无差异'
    $again = Invoke-Cli '02-repeat' @('--replay-diff', $same, $same)
    Assert-Check ($again.Out -ceq $first.Out) '相同输入完整输出应一致'
    $action = Invoke-Cli '03-action' @('--replay-diff', $same, $different)
    Assert-Check ($action.Out.Contains('首个行动分歧：第 2 回合') -and $action.Out.Contains('首个状态分歧：第 2 回合')) '应定位第 2 回合行动和状态变化'
    Assert-Check ($action.Out -match '(?m)^录像1 状态哈希：[0-9a-f]{16}$' -and $action.Out -match '(?m)^录像2 状态哈希：[0-9a-f]{16}$') '差异状态应显示两份完整 16 位哈希'
    Assert-Check ($action.Out -match '(?m)^- .+') '状态差异列表应非空'
    $opening = Invoke-Cli '04-seed' @('--replay-diff', $same, $seedPath)
    Assert-Check ($opening.Out.Contains('开局不同：种子') -and $opening.Out.Contains('未继续逐回合比较')) '改种子应指出开局不同'
    Assert-Check (-not $opening.Out.Contains('首个行动分歧') -and -not $opening.Out.Contains('状态哈希')) '开局不同时不应输出逐回合结果'
    $differentPack = Invoke-Cli '05-pack' @('--replay-diff', $same, $fanPath)
    Assert-Check ($differentPack.Out.Contains('开局不同：内容包') -and $differentPack.Out.Contains('未继续逐回合比较')) '不同的可加载内容包应直接报告开局差异'
    $shortFirst = Invoke-Cli '06-short-first' @('--replay-diff', $short, $same)
    Assert-Check ($shortFirst.Out.Contains('录像1已记录 1 个行动回合，从第 2 回合缺失')) '短者是录像1，不能误称在未记录的第 2 回合结束'
    $shortSecond = Invoke-Cli '07-short-second' @('--replay-diff', $same, $short)
    Assert-Check ($shortSecond.Out.Contains('录像2已记录 1 个行动回合，从第 2 回合缺失')) '应正确识别另一边较短'
    $libraryResult = Invoke-Cli '08-library-id' @('--replay-diff', $id, $same, '--save-dir', $save)
    Assert-Check ($libraryResult.Out -ceq $first.Out) '录像库编号与原始记录文件对比应一致'
    $missingDirectory = Invoke-Cli '09-files-missing-save' @('--replay-diff', $same, $same, '--save-dir', $missingSave)
    Assert-Check ($missingDirectory.Out -ceq $first.Out -and -not [IO.Directory]::Exists($missingSave)) '文件对比不得创建指定存档目录'
    $warning = Invoke-Cli '10-fingerprint-warning' @('--replay-diff', $oldContentPath, $oldContentPath)
    Assert-Check ($warning.Out.Contains('0123456789abcdef') -and $warning.Out.Contains($db.ContentHash())) '内容警告须保留两份完整指纹，不能截断诊断'
    Assert-Check ($warning.Out.Contains('结果可能不同') -and $warning.Out.Contains('未发现差异')) '按当前内容对比应有警告且仍完成自比'
    $dirty = Invoke-Cli '11-names-safe' @('--replay-diff', $dirtyPath, $dirtyPath)
    Assert-Check (-not $dirty.Out.Contains([char]0x1B) -and -not $dirty.Out.Contains([char]0x202E) -and -not $dirty.Out.Contains('伪造输出')) '不应输出原始不安全昵称'

    foreach ($case in @(
        @{ name = '12-broken-first'; args = @('--replay-diff', $bad, $same) },
        @{ name = '13-broken-second'; args = @('--replay-diff', $same, $shape) },
        @{ name = '14-future-first'; args = @('--replay-diff', $futurePath, $seedPath); error = '录像来自更新的版本' },
        @{ name = '15-future-second'; args = @('--replay-diff', $seedPath, $futurePath); error = '录像来自更新的版本' },
        @{ name = '16-missing-pack'; args = @('--replay-diff', $same, $missingPackPath); error = '找不到内容包' },
        @{ name = '17-missing-id'; args = @('--replay-diff', 'missing-id', $same, '--save-dir', $missingSave); error = '读取录像失败' },
        @{ name = '18-invalid-id'; args = @('--replay-diff', 'index', $same, '--save-dir', $save); error = '录像 id' },
        @{ name = '19-missing-file'; args = @('--replay-diff', (Join-Path $fixtures 'missing.json'), $same); error = '读取录像失败' },
        @{ name = '20-one-argument'; args = @('--replay-diff', $same) },
        @{ name = '21-empty'; args = @('--replay-diff', '', $same) },
        @{ name = '22-duplicate'; args = @('--replay-diff', $same, $same, '--replay-diff', $same, $same) },
        @{ name = '23-report'; args = @('--replay-diff', $same, $same, '--report', (Join-Path $run 'forbidden.md')) },
        @{ name = '24-auto'; args = @('--replay-diff', $same, $same, '--auto') },
        @{ name = '25-replay'; args = @('--replay-diff', $same, $same, '--replay', $same) },
        @{ name = '26-pack-override'; args = @('--replay-diff', $same, $same, '--pack', 'original') },
        @{ name = '27-best-effort'; args = @('--replay-diff', $same, $same, '--replay-best-effort') },
        @{ name = '28-duplicate-root'; args = @('--replay-diff', $same, $same, '--save-dir', $save, '--save-dir', $save) }
    )) {
        $failed = Invoke-Cli $case.name $case.args 1
        if ($case.ContainsKey('error')) { Assert-Check ($failed.Err.Contains($case.error)) "$($case.name) 中文原因不准确" }
    }

    # 政策夹具使用隔离的已构建程序副本，不修改共享 DLL 或真实政策文件。
    $private = Join-Path $run 'policy-cli'
    [void][IO.Directory]::CreateDirectory($private)
    foreach ($file in Get-ChildItem -LiteralPath $output -File) { Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $private $file.Name) }
    Copy-Item -LiteralPath $content -Destination (Join-Path $private 'Content') -Recurse
    $privateCli = Join-Path $private 'Cli.dll'
    $policyFile = Join-Path $private 'Content/PackPolicy.json'
    [IO.File]::WriteAllText($policyFile, '{"disabledPacks":["fan_asoul_vr"],"disabledStreamers":[],"reason":{}}', $utf8)
    $disabledPack = Invoke-Cli '29-disabled-second-pack' @('--replay-diff', $same, $fanPath) 1 $privateCli
    Assert-Check ($disabledPack.Err.Contains('该内容包已下架') -and -not $disabledPack.Out.Contains('开局不同')) '第二份记录下架必须先拒绝，不能由开局不同绕过'
    [IO.File]::WriteAllText($policyFile, '{"disabledPacks":[],"disabledStreamers":["bell"],"reason":{}}', $utf8)
    $disabledStreamer = Invoke-Cli '30-disabled-second-streamer' @('--replay-diff', $fanPath, $same) 1 $privateCli
    Assert-Check ($disabledStreamer.Err.Contains('该主播已下架')) '第二份记录中的下架主播必须拒绝'
    $help = Invoke-Cli '31-help' @('--help')
    Assert-Check ($help.Out.Contains('--replay-diff') -and $help.Out.Contains('不写存档、锁、索引或战报')) '帮助须说明独立只读边界'
    Assert-Check ((Tree-Snapshot $fixtures) -ceq $fixtureBefore) '输入目录树、录像哈希及写入时间必须完全不变'
    Assert-Check ((Tree-Snapshot $save) -ceq $saveBefore) '已有存档目录树不能创建或更新锁、索引、档案或录像'
    Assert-Check (-not [IO.Directory]::Exists($missingSave) -and -not [IO.Directory]::Exists((Join-Path $run 'saves'))) '不存在的存档目录须继续不存在'
    Assert-Check (-not [IO.File]::Exists((Join-Path $run 'forbidden.md'))) '互斥报告参数不得写文件'
    [IO.File]::WriteAllText((Join-Path $run 'report.json'), ([ordered]@{ passed = $true; commands = $invocations.Count; runDirectory = $run; invocations = $invocations } | ConvertTo-Json -Depth 12), $utf8)
    Write-Output "PASS: $($invocations.Count) 次 CLI 命令；证据：$run"
}
catch {
    [IO.File]::WriteAllText((Join-Path $run 'failure.txt'), ($_ | Out-String), $utf8)
    [IO.File]::WriteAllText((Join-Path $run 'partial-report.json'), ($invocations | ConvertTo-Json -Depth 12), $utf8)
    throw
}
