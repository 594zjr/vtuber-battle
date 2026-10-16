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
    throw '请在本工作树内运行此脚本。'
}
$toolDll = Join-Path $PSScriptRoot "bin/$Configuration/net8.0/PackCheck.dll"
if (-not (Test-Path -LiteralPath $toolDll -PathType Leaf)) { throw '请先构建 Tools/PackCheck；本脚本不会启动构建。' }
$runDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ('obj/art-file-verification/' + [Guid]::NewGuid().ToString('N'))))
if (-not $runDirectory.StartsWith($repositoryPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw '验证产物必须位于本工作树内。' }
[void][IO.Directory]::CreateDirectory($runDirectory)
$utf8 = [Text.UTF8Encoding]::new($false)
$checks = [Collections.Generic.List[string]]::new()
$invocations = [Collections.Generic.List[object]]::new()
$resources = Join-Path $repository 'UnityProject/Assets/StreamDuel/Resources'
$packDirectory = Join-Path $resources 'Packs'
$original = [IO.File]::ReadAllText((Join-Path $packDirectory 'original.json'))
$originalData = $original | ConvertFrom-Json -AsHashtable
$rosterDirectory = Join-Path $repository 'docs/roster'

function Assert-Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "$Message 证据目录：$runDirectory" }
}

function New-FixtureDirectory([string]$Name) {
    $path = [IO.Path]::GetFullPath((Join-Path $runDirectory ('fixtures/' + $Name)))
    Assert-Check ($path.StartsWith($runDirectory + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) '夹具路径越界。'
    [void][IO.Directory]::CreateDirectory($path)
    return $path
}

function Get-TreeSnapshot([string[]]$Roots) {
    $items = [Collections.Generic.List[string]]::new()
    foreach ($root in $Roots | Sort-Object -Unique) {
        if (-not (Test-Path -LiteralPath $root)) { $items.Add('MISSING|' + $root); continue }
        $entries = @((Get-Item -LiteralPath $root -Force)) + @(Get-ChildItem -LiteralPath $root -Force -Recurse)
        foreach ($entry in $entries | Sort-Object FullName) {
            # 新建目录的枚举缓存可能早于最后一个子项关闭，显式刷新后比较真实元数据。
            $entry.Refresh()
            if ($entry.PSIsContainer) {
                $items.Add('D|' + $entry.FullName + '|' + $entry.LastWriteTimeUtc.Ticks)
            }
            else {
                $hash = (Get-FileHash -LiteralPath $entry.FullName -Algorithm SHA256).Hash
                $items.Add('F|' + $entry.FullName + '|' + $entry.Length + '|' + $entry.LastWriteTimeUtc.Ticks + '|' + $hash)
            }
        }
    }
    return [string]::Join("`n", $items)
}

function Invoke-PackCheck([string]$Name, [string[]]$Parameters, [int]$ExpectedExit, [string[]]$Inputs = @(), [string]$Executable = $toolDll) {
    $before = Get-TreeSnapshot (@($resources, $rosterDirectory) + $Inputs)
    [IO.File]::WriteAllText((Join-Path $runDirectory "$Name.snapshot-before.txt"), $before, $utf8)
    $workingDirectory = Join-Path $runDirectory ('calls/' + $Name)
    [void][IO.Directory]::CreateDirectory($workingDirectory)
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = 'dotnet'
    $start.ArgumentList.Add($Executable)
    foreach ($parameter in $Parameters) { $start.ArgumentList.Add($parameter) }
    $start.WorkingDirectory = $workingDirectory
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = $utf8
    $start.StandardErrorEncoding = $utf8
    $start.Environment['TEMP'] = $runDirectory
    $start.Environment['TMP'] = $runDirectory
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    try {
        Assert-Check ($process.Start()) "$Name 无法启动。"
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $process.StandardInput.Close()
        if (-not $process.WaitForExit(30000)) {
            $process.Kill($true)
            [void]$process.WaitForExit(5000)
            throw "$Name 超过 30 秒未退出。"
        }
        $result = [pscustomobject]@{ Out = $stdout.Result; Err = $stderr.Result; Exit = $process.ExitCode }
        [IO.File]::WriteAllText((Join-Path $runDirectory "$Name.stdout.log"), $result.Out, $utf8)
        [IO.File]::WriteAllText((Join-Path $runDirectory "$Name.stderr.log"), $result.Err, $utf8)
        $invocations.Add([ordered]@{ Name = $Name; Executable = $Executable; Parameters = $Parameters; Exit = $result.Exit })
        Assert-Check ($result.Exit -eq $ExpectedExit) "$Name 应退出 $ExpectedExit，实际 $($result.Exit)。"
        $after = Get-TreeSnapshot (@($resources, $rosterDirectory) + $Inputs)
        [IO.File]::WriteAllText((Join-Path $runDirectory "$Name.snapshot-after.txt"), $after, $utf8)
        if ($after -cne $before) {
            $difference = Compare-Object ($before -split "`n") ($after -split "`n") | Out-String -Width 4096
            [IO.File]::WriteAllText((Join-Path $runDirectory "$Name.snapshot-diff.txt"), $difference, $utf8)
            Assert-Check $false "$Name 修改了输入资源/名单的文件清单、内容或修改时间。"
        }
        Assert-Check (@(Get-ChildItem -LiteralPath $workingDirectory -Force).Count -eq 0) "$Name 向只读查询工作目录写入了文件。"
        return $result
    }
    finally { $process.Dispose() }
}

function Read-ArtReport($Result) {
    Assert-Check ($Result.Err.Length -eq 0) 'JSON 模式不能向 stderr 混入人类报告。'
    $data = $Result.Out | ConvertFrom-Json -AsHashtable -Depth 64
    Assert-Check ($data -is [Collections.IDictionary]) 'JSON 输出必须是单个对象。'
    foreach ($key in @('root', 'packId', 'packIds', 'issues', 'errorCount', 'warningCount', 'passed', 'reminder')) {
        Assert-Check ($data.Contains($key)) "JSON 缺少字段 $key。"
    }
    Assert-Check ($data.passed -is [bool] -and $data.passed -eq ($data.errorCount -eq 0)) 'passed 与错误数不一致。'
    $errors = @($data.issues | Where-Object { $_.level -eq 'error' })
    $warnings = @($data.issues | Where-Object { $_.level -eq 'warn' })
    Assert-Check ($errors.Count -eq $data.errorCount -and $warnings.Count -eq $data.warningCount) 'JSON 问题明细与汇总数量不一致。'
    foreach ($issue in $data.issues) {
        foreach ($key in @('code', 'level', 'path', 'id', 'message', 'width', 'height', 'bytes')) {
            Assert-Check ($issue.Contains($key)) "JSON 问题明细缺少字段 $key。"
        }
        Assert-Check ($issue.message -match '[\u4e00-\u9fff]') '问题说明缺少中文。'
    }
    Assert-Reminder ([string]$data.reminder)
    return $data
}

function Assert-Reminder([string]$Text) {
    Assert-Check ($Text.Contains('授权') -and $Text.Contains('官方') -and $Text.Contains('截图')) '报告缺少自制/授权及禁止官方素材、截图提醒。'
}

function Write-BigEndian32([IO.Stream]$Stream, [uint32]$Value) {
    $bytes = [byte[]]@(($Value -shr 24 -band 255), ($Value -shr 16 -band 255), ($Value -shr 8 -band 255), ($Value -band 255))
    $Stream.Write($bytes, 0, $bytes.Length)
}

function Get-Crc32([byte[]]$Bytes) {
    [uint32]$crc = [uint32]::MaxValue
    foreach ($value in $Bytes) {
        $crc = $crc -bxor [uint32]$value
        for ($bit = 0; $bit -lt 8; $bit++) {
            if (($crc -band 1) -ne 0) { $crc = ($crc -shr 1) -bxor [uint32]3988292384 }
            else { $crc = $crc -shr 1 }
        }
    }
    return ($crc -bxor [uint32]::MaxValue)
}

function Write-PngChunk([IO.Stream]$Stream, [string]$Kind, [byte[]]$Bytes) {
    $type = [Text.Encoding]::ASCII.GetBytes($Kind)
    Write-BigEndian32 $Stream ([uint32]$Bytes.Length)
    $Stream.Write($type, 0, $type.Length)
    $Stream.Write($Bytes, 0, $Bytes.Length)
    Write-BigEndian32 $Stream (Get-Crc32 ([byte[]]($type + $Bytes)))
}

function Write-Png([string]$Path, [int]$Side = 128) {
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path))
    $compressed = [IO.MemoryStream]::new()
    $zlib = [IO.Compression.ZLibStream]::new($compressed, [IO.Compression.CompressionLevel]::Optimal, $true)
    try {
        # 每行的过滤字节和透明像素均由脚本生成，不借用任何图片素材。
        $row = [byte[]]::new(1 + 4 * $Side)
        for ($line = 0; $line -lt $Side; $line++) { $zlib.Write($row, 0, $row.Length) }
    }
    finally { $zlib.Dispose() }
    $header = [IO.MemoryStream]::new()
    $file = [IO.File]::Create($Path)
    try {
        $signature = [byte[]]@(137, 80, 78, 71, 13, 10, 26, 10)
        $file.Write($signature, 0, $signature.Length)
        Write-BigEndian32 $header ([uint32]$Side)
        Write-BigEndian32 $header ([uint32]$Side)
        $tail = [byte[]]@(8, 6, 0, 0, 0)
        $header.Write($tail, 0, $tail.Length)
        Write-PngChunk $file 'IHDR' $header.ToArray()
        Write-PngChunk $file 'IDAT' $compressed.ToArray()
        Write-PngChunk $file 'IEND' ([byte[]]::new(0))
    }
    finally { $file.Dispose(); $header.Dispose(); $compressed.Dispose() }
}

function Set-DeclaredDimensions([string]$Path, [uint32]$Width, [uint32]$Height) {
    # 故意改 IHDR 声明并重算 CRC；工具只读取文件头，不验证 IDAT 像素一致性。
    $bytes = [IO.File]::ReadAllBytes($Path)
    $dimensions = [IO.MemoryStream]::new()
    try {
        Write-BigEndian32 $dimensions $Width
        Write-BigEndian32 $dimensions $Height
        [Array]::Copy($dimensions.ToArray(), 0, $bytes, 16, 8)
    }
    finally { $dimensions.Dispose() }
    $crc = [IO.MemoryStream]::new()
    try {
        Write-BigEndian32 $crc (Get-Crc32 ([byte[]]$bytes[12..28]))
        [Array]::Copy($crc.ToArray(), 0, $bytes, 29, 4)
    }
    finally { $crc.Dispose() }
    [IO.File]::WriteAllBytes($Path, $bytes)
}

function Write-Pack([string]$Directory, [string]$Id) {
    $pack = $original | ConvertFrom-Json -AsHashtable
    $pack.id = $Id
    $pack.name = '自制资源验证包'
    $pack.art = @{ portraitDir = 'Portraits/check'; streamers = @{ bell = @{ portrait = 'nested/bell_v2'; accent = '#33ff66' } }; skillFx = @{ full_volume = 'wave' }; statusIcons = @{ stall = 'clock' } }
    [IO.File]::WriteAllText((Join-Path $Directory 'different-filename.json'), ($pack | ConvertTo-Json -Depth 64), $utf8)
}

function Write-AllPortraits([string]$Directory) {
    foreach ($streamer in $originalData.streamers) {
        $name = if ($streamer.id -eq 'bell') { 'nested/bell_v2' } else { $streamer.id }
        $side = if ($streamer.id -eq 'bell') { 128 } else { 256 }
        Write-Png (Join-Path $Directory ('Portraits/check/' + $name + '.png')) $side
    }
}

function Copy-ValidFixture([string]$Name) {
    $target = New-FixtureDirectory $Name
    foreach ($entry in Get-ChildItem -LiteralPath $validRoot -Force) {
        Copy-Item -LiteralPath $entry.FullName -Destination (Join-Path $target $entry.Name) -Recurse
    }
    return $target
}

$baseline = Invoke-PackCheck '01-original-legacy' @('--pack', 'original') 0
Assert-Check ($baseline.Out.Contains('内容包 original 内容指纹：') -and $baseline.Out.Contains('0 个错误')) '原 PackCheck 单包检查发生回归。'
$checks.Add('原 --pack original 检查成功且源资源、名单均不变')

$emptyRoot = New-FixtureDirectory 'empty-art'
$missingText = Invoke-PackCheck '02-missing-text' @('--art', $emptyRoot, '--pack', 'original') 0 @($emptyRoot)
Assert-Check ($missingText.Out.Contains('将使用占位')) '缺失资源未说明将使用占位。'
Assert-Reminder (($missingText.Out.Trim() -split '\r?\n')[-1])
$missingJson = Read-ArtReport (Invoke-PackCheck '03-missing-json' @('--art', $emptyRoot, '--pack', 'original', '--json') 0 @($emptyRoot))
Assert-Check ($missingJson.packId -eq 'original' -and $missingJson.errorCount -eq 0) '缺失资源报告包 id 或错误数不正确。'
Assert-Check (@($missingJson.issues | Where-Object { $_.code -eq 'missing' -and $_.level -eq 'warn' }).Count -eq $originalData.streamers.Count) '缺失立绘没有按每位主播报告。'
Assert-Check (@($missingJson.issues | Where-Object { $_.code -eq 'procedural' -and $_.level -eq 'info' }).Count -gt 0) '报告未说明特效/图标目前使用程序表现。'
$allMissing = Read-ArtReport (Invoke-PackCheck '04-default-all-builtins' @('--art', $emptyRoot, '--json') 0 @($emptyRoot))
$expectedIds = @(Get-ChildItem -LiteralPath $packDirectory -Filter '*.json' -File | ForEach-Object { ([IO.File]::ReadAllText($_.FullName) | ConvertFrom-Json).id } | Sort-Object)
Assert-Check ((@($allMissing.packIds | Sort-Object) -join '|') -ceq ($expectedIds -join '|')) '资源根没有包 JSON 时未检查全部内置包。'
$checks.Add('全部缺失仅提醒、exit 0，单包/默认全集选择正确；JSON 单对象、中文说明、程序表现和末尾授权提醒完整')

$validRoot = New-FixtureDirectory 'valid-auto-pack'
Write-Pack $validRoot 't123_art_complete'
Write-AllPortraits $validRoot
$automatic = Read-ArtReport (Invoke-PackCheck '05-auto-top-level-pack' @('--art', $validRoot, '--json') 0 @($validRoot))
Assert-Check ($automatic.packId -eq 't123_art_complete' -and @($automatic.packIds).Count -eq 1) '资源根顶层包 JSON 未优先于内置全集。'
$externalDirectory = New-FixtureDirectory 'external-json'
Write-Pack $externalDirectory 't123_external'
$external = Read-ArtReport (Invoke-PackCheck '06-explicit-pack-dir' @('--art', $validRoot, '--pack-dir', $externalDirectory, '--pack', 't123_external', '--json') 0 @($validRoot, $externalDirectory))
Assert-Check ($external.packId -eq 't123_external' -and @($external.packIds).Count -eq 1 -and $external.warningCount -eq 0) '显式外部目录/内部包 id 选择或独立资源根映射错误。'
$checks.Add('顶层包自动发现与显式外包目录选择正确；文件名不同于内部 id，portraitDir 和嵌套 portrait 覆盖实际生效')

Assert-Check ($automatic.passed -and $automatic.errorCount -eq 0 -and $automatic.warningCount -eq 0) '全部正确资源没有通过。'
$validText = Invoke-PackCheck '07-all-valid-text' @('--art', $validRoot) 0 @($validRoot)
Assert-Check ($validText.Out.Contains('通过')) '全部有效资源中文报告没有“通过”。'
Assert-Reminder (($validText.Out.Trim() -split '\r?\n')[-1])
$edgeRoot = Copy-ValidFixture 'inclusive-boundaries'
$edgePortrait = Join-Path $edgeRoot 'Portraits/check/nested/bell_v2.png'
Set-DeclaredDimensions $edgePortrait 4096 128
$edgeFile = [IO.File]::Open($edgePortrait, [IO.FileMode]::Open, [IO.FileAccess]::Write)
try { $edgeFile.SetLength(8 * 1024 * 1024) } finally { $edgeFile.Dispose() }
$edgeReport = Read-ArtReport (Invoke-PackCheck '08-inclusive-size-boundaries' @('--art', $edgeRoot, '--json') 0 @($edgeRoot))
Assert-Check ($edgeReport.errorCount -eq 0 -and $edgeReport.warningCount -eq 0) '128/4096 像素或恰好 8 MiB 的含边界规则错误。'
$checks.Add('128/256 自制完整 PNG 全部通过；IHDR 4096×128、恰好 8 MiB 文件头夹具验证含边界规则，不冒充像素解码检查')

foreach ($case in @(
    @{ Name = 'extension'; Relative = 'Portraits/check/nested/bell_v2.jpg' },
    @{ Name = 'fake-png'; Relative = 'Portraits/check/nested/bell_v2.png' },
    @{ Name = 'truncated'; Relative = 'Portraits/check/nested/bell_v2.png' },
    @{ Name = 'too-small'; Relative = 'Portraits/check/nested/bell_v2.png' },
    @{ Name = 'too-wide'; Relative = 'Portraits/check/nested/bell_v2.png' },
    @{ Name = 'too-large'; Relative = 'Portraits/check/nested/bell_v2.png' }
)) {
    $caseRoot = Copy-ValidFixture $case.Name
    $portrait = Join-Path $caseRoot 'Portraits/check/nested/bell_v2.png'
    switch ($case.Name) {
        'extension' { Move-Item -LiteralPath $portrait -Destination (Join-Path $caseRoot $case.Relative); [IO.File]::WriteAllText((Join-Path $caseRoot $case.Relative), 'not a PNG', $utf8) }
        'fake-png' { [IO.File]::WriteAllText($portrait, 'not a PNG with a png filename', $utf8) }
        'truncated' { [IO.File]::WriteAllBytes($portrait, [byte[]]@(137, 80, 78, 71, 13, 10, 26, 10)) }
        'too-small' { Set-DeclaredDimensions $portrait 127 128 }
        'too-wide' { Set-DeclaredDimensions $portrait 4097 128 }
        'too-large' {
            $file = [IO.File]::Open($portrait, [IO.FileMode]::Open, [IO.FileAccess]::Write)
            try { $file.SetLength(8 * 1024 * 1024 + 1) } finally { $file.Dispose() }
        }
    }
    $name = '09-invalid-' + $case.Name
    $result = Read-ArtReport (Invoke-PackCheck $name @('--art', $caseRoot, '--json') 1 @($caseRoot))
    $matching = @($result.issues | Where-Object { $_.level -eq 'error' -and ([string]$_.path).Replace('\', '/').EndsWith($case.Relative, [StringComparison]::Ordinal) })
    Assert-Check ($matching.Count -ge 1) "$($case.Name) 没有在对应资源位置报告错误。"
}
$checks.Add('非 PNG 扩展、伪装 PNG、截断头、127/4097 像素和超过 8 MiB 均返回非零并定位文件')

$unusedRoot = Copy-ValidFixture 'unused'
Write-Png (Join-Path $unusedRoot 'Portraits/check/extra.png')
$unused = Read-ArtReport (Invoke-PackCheck '10-unused-warning' @('--art', $unusedRoot, '--json') 0 @($unusedRoot))
$unusedIssues = @($unused.issues | Where-Object { $_.code -eq 'unused' -and $_.level -eq 'warn' })
Assert-Check ($unused.errorCount -eq 0 -and $unused.warningCount -eq 1 -and $unusedIssues.Count -eq 1 -and ([string]$unusedIssues[0].path).Replace('\', '/').EndsWith('Portraits/check/extra.png')) '多余资源未且仅按提醒报告。'
$checks.Add('清单未引用的 PNG 仅产生 unused 提醒，正常退出')

$invalidParameters = @(
    ,@('--art')
    ,@('--art', '')
    ,@('--art', $emptyRoot, '--pack')
    ,@('--art', $emptyRoot, '--pack-dir')
    ,@('--art', $emptyRoot, '--art', $emptyRoot)
    ,@('--art', $emptyRoot, '--pack', 'original', '--pack', 'original')
    ,@('--art', $emptyRoot, '--pack-dir', $externalDirectory, '--pack-dir', $externalDirectory)
    ,@('--art', $emptyRoot, '--json', '--json')
    ,@('--json')
    ,@('--art', $emptyRoot, '--unknown-option')
    ,@('--art', $emptyRoot, '--write-docs')
    ,@('--write-docs', '--art', $emptyRoot)
    ,@('--art', $emptyRoot, '--list-packs')
    ,@('--list-packs', '--art', $emptyRoot)
)
for ($index = 0; $index -lt $invalidParameters.Count; $index++) {
    $parameters = [string[]]$invalidParameters[$index]
    $invalid = Invoke-PackCheck ('11-invalid-options-' + $index) $parameters 1 @($emptyRoot, $externalDirectory)
    Assert-Check (($invalid.Out + $invalid.Err) -match '[\u4e00-\u9fff]') '参数错误缺少中文说明。'
    if ($parameters -contains '--json') { $null = Read-ArtReport $invalid }
}
$invalidJson = Read-ArtReport (Invoke-PackCheck '12-unknown-json' @('--art', $emptyRoot, '--unknown-option', '--json') 1 @($emptyRoot))
Assert-Check ($invalidJson.errorCount -gt 0) 'JSON 模式参数错误未写入同一报告结构。'
$badPackRoot = New-FixtureDirectory 'malformed-top-level-pack'
[IO.File]::WriteAllText((Join-Path $badPackRoot 'invalid.json'), '{"id":', $utf8)
$badPackReport = Read-ArtReport (Invoke-PackCheck '13-malformed-pack-json' @('--art', $badPackRoot, '--json') 1 @($badPackRoot))
Assert-Check ($badPackReport.errorCount -gt 0) '坏内容包加载失败未返回统一 JSON 报告。'
$checks.Add('缺值/空值/重复/未知参数、孤立 --json、坏内容包及写文档/列包双顺序组合全部拒绝且无副作用')

# 副本从自身路径寻找假仓库；目录联接的目标也只在本轮 obj 夹具内，不触碰真实 Resources。
$fixtureRepository = New-FixtureDirectory 'junction-repository'
$fixtureTarget = New-FixtureDirectory 'junction-target-packs'
Copy-Item -LiteralPath (Join-Path $packDirectory 'original.json') -Destination (Join-Path $fixtureTarget 'original.json')
$fixtureBin = Join-Path $fixtureRepository 'Tools/PackCheck/bin'
$fixtureResources = Join-Path $fixtureRepository 'UnityProject/Assets/StreamDuel/Resources'
foreach ($directory in @($fixtureBin, $fixtureResources, (Join-Path $fixtureRepository 'Engine'))) {
    [void][IO.Directory]::CreateDirectory($directory)
}
foreach ($file in Get-ChildItem -LiteralPath (Split-Path $toolDll) -File) {
    Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $fixtureBin $file.Name)
}
[IO.File]::WriteAllText((Join-Path $fixtureRepository 'AGENTS.md'), '# 验收隔离目录', $utf8)
[IO.File]::WriteAllText((Join-Path $fixtureRepository 'Engine/StreamDuel.Core.csproj'), '<Project />', $utf8)
foreach ($name in @('StreamDuelRules.json', 'PackPolicy.json')) {
    Copy-Item -LiteralPath (Join-Path $resources $name) -Destination (Join-Path $fixtureResources $name)
}
$junctionPath = Join-Path $fixtureResources 'Packs'
foreach ($path in @($junctionPath, $fixtureTarget)) {
    Assert-Check ([IO.Path]::GetFullPath($path).StartsWith($runDirectory + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) '目录联接或目标超出本轮验证目录。'
}
[void](New-Item -ItemType Junction -Path $junctionPath -Target $fixtureTarget)
Assert-Check (((Get-Item -LiteralPath $junctionPath -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) '目录联接夹具未正确建立。'
$junctionReport = Read-ArtReport (Invoke-PackCheck '14-builtin-fallback-junction' @('--art', $emptyRoot, '--pack', 'original', '--json') 1 @($fixtureRepository, $fixtureTarget, $emptyRoot) (Join-Path $fixtureBin 'PackCheck.dll'))
Assert-Check (@($junctionReport.issues | Where-Object { $_.code -eq 'input' -and $_.level -eq 'error' -and $_.message -match '链接|联接' }).Count -eq 1) '内置包目录 fallback 没有以 JSON input error 拒绝目录联接。'
Assert-Check (@($junctionReport.packIds).Count -eq 0) '内置 fallback 在拒绝目录联接前已经加载包定义。'
$checks.Add('隔离程序副本的内置 Packs 为 obj 内目录联接时，fallback 返回 JSON input error，假仓库/目标/资源根文件和精确mtime均不变；联接产物保留')
$checks.Add('全部真实进程逐次验证源包、策略、名单、夹具的文件清单、SHA256 和修改时间不变；没有新存档/输出文件落入调用 cwd')

$report = [ordered]@{
    Status = 'PASS'
    Checks = $checks.ToArray()
    InvocationCount = $invocations.Count
    Invocations = $invocations.ToArray()
    ImageScope = 'Self-generated complete 128/256 PNGs; boundary/invalid dimensions deliberately modify IHDR+CRC; file-size cases append padding. Header checks only, no decoded-image validation claimed.'
    OutputDirectory = $runDirectory
}
[IO.File]::WriteAllText((Join-Path $runDirectory 'report.json'), ($report | ConvertTo-Json -Depth 12), $utf8)
Write-Output "PASS：$($checks.Count) 组、$($invocations.Count) 次真实 PackCheck 进程；证据：$runDirectory"
