#requires -Version 7.0
param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug')

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$prefix = $repository.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$current = [IO.Path]::GetFullPath((Get-Location).Path)
if ($current -ne $repository -and -not $current.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw '请在本工作树内运行脚本。' }
$toolDll = Join-Path $PSScriptRoot "bin/$Configuration/net8.0/Balance.dll"
if (-not (Test-Path -LiteralPath $toolDll -PathType Leaf)) { throw '请先构建 Tools/Balance；脚本不会启动构建。' }
$run = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ('obj/balance-compare-verification/' + [Guid]::NewGuid().ToString('N'))))
if (-not $run.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw '验收输出路径越界。' }
$fixtures = Join-Path $run 'fixtures'
[void][IO.Directory]::CreateDirectory($fixtures)
$utf8 = [Text.UTF8Encoding]::new($false)
$resources = Join-Path $repository 'UnityProject/Assets/StreamDuel/Resources'
$content = Join-Path (Split-Path $toolDll) 'Content'
$checks = [Collections.Generic.List[string]]::new()
$calls = [Collections.Generic.List[object]]::new()

function Assert-Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "$Message 证据目录：$run" }
}

function Snapshot-Files([string[]]$Roots) {
    $rows = [Collections.Generic.List[string]]::new()
    foreach ($root in $Roots | Sort-Object -Unique) {
        if (-not (Test-Path -LiteralPath $root)) { $rows.Add('MISSING|' + $root); continue }
        $item = Get-Item -LiteralPath $root -Force
        $entries = @($item)
        if ($item.PSIsContainer) { $entries += @(Get-ChildItem -LiteralPath $root -Recurse -Force) }
        foreach ($entry in $entries | Sort-Object FullName) {
            $entry.Refresh()
            if ($entry.PSIsContainer) { $rows.Add('D|' + $entry.FullName + '|' + $entry.LastWriteTimeUtc.Ticks) }
            else { $rows.Add('F|' + $entry.FullName + '|' + $entry.Length + '|' + $entry.LastWriteTimeUtc.Ticks + '|' + (Get-FileHash -LiteralPath $entry.FullName -Algorithm SHA256).Hash) }
        }
    }
    return [string]::Join("`n", $rows)
}

function Invoke-Balance([string]$Name, [string[]]$Parameters, [int]$ExpectedExit, [string[]]$Inputs = @(), [string]$Executable = $toolDll) {
    $before = Snapshot-Files (@($resources, $content) + $Inputs)
    [IO.File]::WriteAllText((Join-Path $run "$Name.before.txt"), $before, $utf8)
    $cwd = Join-Path $run ('calls/' + $Name)
    [void][IO.Directory]::CreateDirectory($cwd)
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = 'dotnet'
    $start.ArgumentList.Add($Executable)
    foreach ($value in $Parameters) { $start.ArgumentList.Add($value) }
    $start.WorkingDirectory = $cwd
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = $utf8
    $start.StandardErrorEncoding = $utf8
    $start.Environment['TEMP'] = $run
    $start.Environment['TMP'] = $run
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    try {
        Assert-Check ($process.Start()) "$Name 无法启动。"
        $outTask = $process.StandardOutput.ReadToEndAsync()
        $errTask = $process.StandardError.ReadToEndAsync()
        $process.StandardInput.Close()
        if (-not $process.WaitForExit(90000)) { $process.Kill($true); [void]$process.WaitForExit(5000); throw "$Name 超过90秒。" }
        $result = [pscustomobject]@{ Out=$outTask.Result; Err=$errTask.Result; Exit=$process.ExitCode }
        [IO.File]::WriteAllText((Join-Path $run "$Name.stdout.log"), $result.Out, $utf8)
        [IO.File]::WriteAllText((Join-Path $run "$Name.stderr.log"), $result.Err, $utf8)
        $calls.Add([ordered]@{ Name=$Name; Parameters=$Parameters; Exit=$result.Exit; Executable=$Executable })
        Assert-Check ($result.Exit -eq $ExpectedExit) "$Name 应退出$ExpectedExit，实际$($result.Exit)。"
        $after = Snapshot-Files (@($resources, $content) + $Inputs)
        [IO.File]::WriteAllText((Join-Path $run "$Name.after.txt"), $after, $utf8)
        Assert-Check ($before -ceq $after) "$Name 修改了输入/Resources/Content的文件、内容或精确mtime。"
        Assert-Check (@(Get-ChildItem -LiteralPath $cwd -Force).Count -eq 0) "$Name 向调用工作目录写入文件。"
        return $result
    }
    finally { $process.Dispose() }
}

function New-Row([string]$Id = 'alpha', [long]$Games = 10000, [long]$Wins = 5000, [long]$Draws = 0) {
    $z = 1.959963984540054
    $p = ($Wins + 0.5 * $Draws) / $Games
    $denominator = 1 + $z * $z / $Games
    $center = ($p + $z * $z / (2 * $Games)) / $denominator
    $margin = $z * [Math]::Sqrt($p * (1 - $p) / $Games + $z * $z / (4 * [double]$Games * $Games)) / $denominator
    return [ordered]@{ id=$Id; name=('测试主播' + $Id); games=$Games; wins=$Wins; draws=$Draws; winRate=$p; low95=[Math]::Max(0.0, $center-$margin); high95=[Math]::Min(1.0, $center+$margin) }
}

function New-Snapshot([long]$Games = 10000, [long]$Wins = 5000) {
    return [ordered]@{ version=1; gamesPerStreamer=2; seed=130; level='easy'; aiVersion='fixture-ai'; packs=@(
        [ordered]@{ packId='fixture'; contentHash='fixture-content'; battles=$Games; streamers=@((New-Row 'alpha' $Games $Wins)) }
    ) }
}

function Save-Fixture([string]$Name, $Value) {
    $path = Join-Path $fixtures ($Name + '.json')
    [IO.File]::WriteAllText($path, ($Value | ConvertTo-Json -Depth 32), $utf8)
    return $path
}

function Row-Marks([string]$Text, [string]$Id = 'alpha') {
    $lines = @($Text -split '\r?\n' | Where-Object { $_.Contains('（' + $Id + '） | ') })
    Assert-Check ($lines.Count -eq 1) "报告没有唯一主播行$Id。"
    return ($lines[0] -split '\|')[-1].Trim()
}

$firstPath = Join-Path $fixtures 'generated-first.json'
$secondPath = Join-Path $fixtures 'generated-second.json'
$simulation = @('--pack', 'original', '--games-per-streamer', '2', '--seed', '130', '--level', 'easy')
$legacy = Invoke-Balance '01-original-csv' $simulation 0
$first = Invoke-Balance '02-save-first' ($simulation + @('--save-balance', $firstPath)) 0
$second = Invoke-Balance '03-save-second' ($simulation + @('--save-balance', $secondPath)) 0 @($firstPath)
Assert-Check ($first.Out -ceq $legacy.Out -and $first.Out -ceq $second.Out) '保存模式改变了旧CSV或同条件两次CSV不同。'
Assert-Check ((Get-FileHash -LiteralPath $firstPath).Hash -ceq (Get-FileHash -LiteralPath $secondPath).Hash) '同程序集/条件的两次JSON不是逐字节一致。'
$generated = [IO.File]::ReadAllText($firstPath) | ConvertFrom-Json -AsHashtable
$pack = $generated.packs[0]
Assert-Check ($generated.version -eq 1 -and $generated.gamesPerStreamer -eq 2 -and $generated.seed -eq 130 -and $generated.level -eq 'easy' -and $generated.aiVersion.StartsWith('core-mvid:')) '快照缺少采样参数或实际Core版本。'
Assert-Check ($pack.packId -eq 'original' -and $pack.battles -gt 0 -and $pack.contentHash -match '^[0-9a-f]{16}$') '真实快照没有内容指纹/对局数。'
$csv = @($first.Out | ConvertFrom-Csv)
Assert-Check ($csv.Count -eq $pack.streamers.Count) 'CSV与JSON主播数量不一致。'
[long]$totalGames = 0
foreach ($row in $pack.streamers) {
    $totalGames += $row.games
    Assert-Check ($row.games -ge 2 -and $row.wins -ge 0 -and $row.draws -ge 0 -and $row.wins + $row.draws -le $row.games) '真实计数不是有效入队胜平次数。'
    Assert-Check ([Math]::Abs($row.winRate - ($row.wins + $row.draws * 0.5) / $row.games) -lt 1e-12) '快照胜率未使用实际Games。'
    $csvRow = @($csv | Where-Object { $_.streamer_id -ceq $row.id })
    Assert-Check ($csvRow.Count -eq 1 -and [Math]::Abs([double]::Parse($csvRow[0].win_rate, [Globalization.CultureInfo]::InvariantCulture)-$row.winRate) -lt 1e-12) 'CSV胜率与快照不一致。'
}
Assert-Check ($totalGames -eq 12 * $pack.battles) '真实6v6累计入队次数不等于12×对局数。'
$selfExit = if (@($pack.streamers | Where-Object { $_.winRate -lt 0.35 -or $_.winRate -gt 0.65 }).Count -gt 0) { 1 } else { 0 }
$self = Invoke-Balance '04-generated-self-compare' @('--compare-balance', $firstPath, $secondPath) $selfExit @($fixtures)
foreach ($row in $pack.streamers) { Assert-Check ((Row-Marks $self.Out $row.id).StartsWith('无变化')) '自比没有标识无变化。' }
Assert-Check (-not $self.Out.StartsWith('条件不同')) '同一程序集同条件自比被误报条件不同。'
$checks.Add('小样本真实CSV与两次JSON逐字节确定，保存不改变CSV；实际入队/胜平/版本/指纹完整，自比保留真实越界退出码')

$oldPath = Save-Fixture 'old' (New-Snapshot)
$newPath = Save-Fixture 'changed' (New-Snapshot 10000 6000)
$changed = Invoke-Balance '05-significant-in-range' @('--compare-balance', $oldPath, $newPath) 0 @($fixtures)
Assert-Check ((Row-Marks $changed.Out) -ceq '明显变化') '区间分离且变化超过5百分点没有明确标识。'
$outsidePath = Save-Fixture 'outside' (New-Snapshot 10000 7000)
$outside = Invoke-Balance '06-outside' @('--compare-balance', $oldPath, $outsidePath) 1 @($fixtures)
Assert-Check ((Row-Marks $outside.Out).Contains('明显变化') -and (Row-Marks $outside.Out).Contains('超出平衡范围')) '越界结果未同时报告显著变化与超范围。'
$overlapOld = Save-Fixture 'overlap-old' (New-Snapshot 20 10)
$overlapNew = Save-Fixture 'overlap-new' (New-Snapshot 20 12)
$overlap = Invoke-Balance '07-overlapping-ci' @('--compare-balance', $overlapOld, $overlapNew) 0 @($fixtures)
Assert-Check ((Row-Marks $overlap.Out) -ceq '未达到明显变化条件') '重叠置信区间被误标明显变化。'
$exactPath = Save-Fixture 'exact-threshold' (New-Snapshot 10000 5500)
$exact = Invoke-Balance '08-exact-five-points' @('--compare-balance', $oldPath, $exactPath) 0 @($fixtures)
Assert-Check ((Row-Marks $exact.Out) -ceq '未达到明显变化条件') '恰好5百分点被误标。'
$custom = Invoke-Balance '09-custom-threshold' @('--compare-balance', $oldPath, $exactPath, '--change-threshold', '0.04') 0 @($fixtures)
Assert-Check ((Row-Marks $custom.Out) -ceq '明显变化') '自定义阈值没有生效。'
foreach ($wins in @(3500, 6500)) {
    $edge = Save-Fixture ('edge-' + $wins) (New-Snapshot 10000 $wins)
    $null = Invoke-Balance ('10-inclusive-' + $wins) @('--compare-balance', $oldPath, $edge) 0 @($fixtures)
}
$different = New-Snapshot
$different.seed = 131
$differentPath = Save-Fixture 'different-seed' $different
$conditions = Invoke-Balance '11-different-conditions' @('--compare-balance', $oldPath, $differentPath) 0 @($fixtures)
Assert-Check ($conditions.Out.StartsWith('条件不同，对比仅供参考')) '条件不同提示未放在报告开头。'
$checks.Add('有效独立Wilson合成数据验证明显变化、区间重叠、5百分点严格门槛、自定义阈值、35%/65%含边界、越界exit1及异条件首行')

$isolated = Join-Path $run 'isolated-bin'
[void][IO.Directory]::CreateDirectory($isolated)
foreach ($file in Get-ChildItem -LiteralPath (Split-Path $toolDll) -File) { Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $isolated $file.Name) }
$isolatedResult = Invoke-Balance '12-no-content-needed' @('--compare-balance', $oldPath, $newPath) 0 @($fixtures, $isolated) (Join-Path $isolated 'Balance.dll')
Assert-Check ($isolatedResult.Out -ceq $changed.Out) '没有Content部署目录时只读比较未保持相同结果。'
$checks.Add('只复制程序集、不部署Content时仍可比较，报告逐字节一致；对比无需读取规则或内容包')

foreach ($kind in @('syntax', 'duplicate-root', 'duplicate-streamer-field', 'rate', 'interval', 'counts', 'nan')) {
    $value = New-Snapshot
    if ($kind -eq 'rate') { $value.packs[0].streamers[0].winRate = 0.8 }
    if ($kind -eq 'interval') { $value.packs[0].streamers[0].low95 = 0.1 }
    if ($kind -eq 'counts') { $value.packs[0].streamers[0].draws = 10000 }
    $json = $value | ConvertTo-Json -Depth 32 -Compress
    switch ($kind) {
        'syntax' { $json = '{不合法JSON' }
        'duplicate-root' { $json = '{"version":1,' + $json.Substring(1) }
        'duplicate-streamer-field' { $json = $json.Replace('"id":', '"id":"duplicate","id":') }
        'nan' { $json = $json.Replace('"winRate":0.5', '"winRate":NaN') }
    }
    $badPath = Join-Path $fixtures ('bad-' + $kind + '.json')
    [IO.File]::WriteAllText($badPath, $json, $utf8)
    $bad = Invoke-Balance ('13-bad-' + $kind) @('--compare-balance', $oldPath, $badPath) 1 @($fixtures)
    Assert-Check ($bad.Out.Length -eq 0 -and $bad.Err -match '[\u4e00-\u9fff]') '坏结果文件没有中文错误或仍输出了表格。'
}
$checks.Add('坏JSON、根/嵌套重键、错误比例/区间/计数、NaN均中文拒绝，原始输入逐字节和mtime不变')

$existing = Invoke-Balance '14-existing-no-overwrite' ($simulation + @('--save-balance', $firstPath)) 1 @($fixtures)
Assert-Check ($existing.Out.Length -eq 0 -and $existing.Err.Contains('已存在')) '已有结果没有在模拟/输出前拒绝覆盖。'
$streamsBefore = @(Get-Item -LiteralPath $oldPath -Stream * | Sort-Object Stream | ForEach-Object { $_.Stream + '|' + $_.Length }) -join "`n"
$ads = Invoke-Balance '15-reject-ads' ($simulation + @('--save-balance', ($oldPath + ':result'))) 1 @($fixtures)
$streamsAfter = @(Get-Item -LiteralPath $oldPath -Stream * | Sort-Object Stream | ForEach-Object { $_.Stream + '|' + $_.Length }) -join "`n"
Assert-Check ($streamsBefore -ceq $streamsAfter -and $ads.Err -match '[\u4e00-\u9fff]') 'ADS保存创建/修改了流或没有中文拒绝。'
$device = Invoke-Balance '16-reject-device-name' ($simulation + @('--save-balance', (Join-Path $fixtures 'NUL.json'))) 1 @($fixtures)
Assert-Check ($device.Out.Length -eq 0 -and $device.Err -match '[\u4e00-\u9fff]') '设备名未在写入前中文拒绝。'
$resourcePath = Join-Path $resources ('t130-forbidden-' + [Guid]::NewGuid().ToString('N') + '.json')
$resource = Invoke-Balance '17-reject-resource-output' ($simulation + @('--save-balance', $resourcePath)) 1 @($fixtures)
Assert-Check ($resource.Out.Length -eq 0 -and $resource.Err.Contains('资源')) '保存到源Resources目录未拒绝。'
Assert-Check (-not (Test-Path -LiteralPath $resourcePath)) '在源Resources创建了结果文件。'
$checks.Add('已有文件、ADS额外流、NUL.json与源资源目录保存均拒绝；原结果/流清单/资源未改动')

$invalid = [Collections.Generic.List[object]]::new()
foreach ($pair in @(
    ,@('--pack', 'original')
    ,@('--games-per-streamer', '2')
    ,@('--seed', '130')
    ,@('--level', 'easy')
    ,@('--save-balance', (Join-Path $fixtures 'must-not-create.json'))
)) {
    $invalid.Add([string[]](@('--compare-balance', $oldPath, $newPath) + $pair))
    $invalid.Add([string[]]($pair + @('--compare-balance', $oldPath, $newPath)))
}
foreach ($threshold in @('NaN', 'Infinity', '-0.1', '1.01', 'bad')) { $invalid.Add([string[]]@('--compare-balance', $oldPath, $newPath, '--change-threshold', $threshold)) }
$invalid.Add([string[]]@('--change-threshold', '0.05'))
$invalid.Add([string[]]@('--compare-balance', $oldPath))
$invalid.Add([string[]]@('--compare-balance', $oldPath, $newPath, '--change-threshold', '0.1', '--change-threshold', '0.2'))
$invalid.Add([string[]]@('--compare-balance', $oldPath, $newPath, '--compare-balance', $oldPath, $newPath))
$invalid.Add([string[]]@('--save-balance'))
$invalid.Add([string[]]@('--save-balance', ''))
for ($index = 0; $index -lt $invalid.Count; $index++) {
    $failure = Invoke-Balance ('18-invalid-option-' + $index) ([string[]]$invalid[$index]) 1 @($fixtures)
    Assert-Check ($failure.Out.Length -eq 0 -and $failure.Err -match '[\u4e00-\u9fff]') '互斥/无效参数没有在模拟或报告前中文拒绝。'
}
$checks.Add('比较与模拟/保存参数双顺序互斥，阈值非有限/越界、缺值/重复参数中文拒绝，无新结果或自动模拟输出')

$report = [ordered]@{ Status='PASS'; Checks=$checks.ToArray(); InvocationCount=$calls.Count; Invocations=$calls.ToArray(); GeneratedSelfCompareExit=$selfExit; OutputDirectory=$run }
[IO.File]::WriteAllText((Join-Path $run 'report.json'), ($report | ConvertTo-Json -Depth 12), $utf8)
Write-Output "PASS：$($checks.Count)组、$($calls.Count)次真实Balance进程；证据：$run"
