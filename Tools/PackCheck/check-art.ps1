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
    throw '请在本仓库目录内运行此脚本。'
}
$toolDll = Join-Path $PSScriptRoot "bin/$Configuration/net8.0/PackCheck.dll"
if (-not (Test-Path -LiteralPath $toolDll -PathType Leaf)) { throw '请先运行 dotnet build Tools/PackCheck。' }
$cliDll = Join-Path $repository "Tools/Cli/bin/$Configuration/net8.0/Cli.dll"
if (-not (Test-Path -LiteralPath $cliDll -PathType Leaf)) { throw '请先运行 dotnet build Tools/Cli。' }
$runDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ('obj/art-check/' + [Guid]::NewGuid().ToString('N'))))
if (-not $runDirectory.StartsWith($repositoryPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw '验证产物必须位于仓库内。' }
[void][IO.Directory]::CreateDirectory($runDirectory)
$utf8 = [Text.UTF8Encoding]::new($false)
$checks = [Collections.Generic.List[string]]::new()
$invocations = [Collections.Generic.List[object]]::new()
$packDirectory = Join-Path $repository 'UnityProject/Assets/StreamDuel/Resources/Packs'
$original = [IO.File]::ReadAllText((Join-Path $packDirectory 'original.json'))
$sourcePolicyPath = Join-Path (Split-Path $packDirectory) 'PackPolicy.json'
$sourcePolicyHash = (Get-FileHash -LiteralPath $sourcePolicyPath -Algorithm SHA256).Hash
$sourceHashes = @{}
$builtIns = @('original', 'original_wave2', 'fan_asoul_vr', 'fan_holo_niji')
foreach ($id in $builtIns) {
    $path = Join-Path $packDirectory "$id.json"
    $sourceHashes[$id] = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
}

function Assert-Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "$Message 证据目录：$runDirectory" }
}

function Invoke-PackCheck([string]$Name, [string[]]$Parameters, [int]$ExpectedExit,
    [string]$Executable = $toolDll, [switch]$AllowStderr) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = 'dotnet'
    $start.ArgumentList.Add($Executable)
    foreach ($parameter in $Parameters) { $start.ArgumentList.Add($parameter) }
    $workingDirectory = Join-Path $runDirectory ('calls/' + $Name)
    [void][IO.Directory]::CreateDirectory($workingDirectory)
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
        if (-not $AllowStderr) { Assert-Check ($result.Err.Length -eq 0) "$Name 不应绕过逐包输出落入顶层错误处理。" }
        Assert-Check (@(Get-ChildItem -LiteralPath $workingDirectory -Force).Count -eq 0) "$Name 的只读查询写入了工作目录。"
        return $result
    }
    finally { $process.Dispose() }
}

function Assert-Summary([string]$Output, [int]$Packs, [int]$Errors, [int]$Disabled = 0) {
    $summary = [regex]::Match($Output, '检查完成：([0-9]+) 个内容包（其中 ([0-9]+) 个已下架），([0-9]+) 个错误，([0-9]+) 个提醒。')
    Assert-Check $summary.Success '缺少包含下架计数的检查汇总。'
    Assert-Check ([int]$summary.Groups[1].Value -eq $Packs -and [int]$summary.Groups[2].Value -eq $Disabled -and
        [int]$summary.Groups[3].Value -eq $Errors) '检查包数、下架数或错误数与预期不一致。'
}

function Write-Utf8([string]$Path, [string]$Text) {
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path))
    [IO.File]::WriteAllText($Path, $Text, $utf8)
}

function Copy-BinaryFiles([string]$SourceDll, [string]$Destination) {
    [void][IO.Directory]::CreateDirectory($Destination)
    foreach ($file in Get-ChildItem -LiteralPath (Split-Path $SourceDll) -File) {
        Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $Destination $file.Name)
    }
}

function Write-Pack([string]$Directory, [string]$Id, $Art) {
    [void][IO.Directory]::CreateDirectory($Directory)
    $pack = $original | ConvertFrom-Json -AsHashtable
    $pack['id'] = $Id
    $pack['name'] = '资源清单验证包'
    $pack['art'] = $Art
    [IO.File]::WriteAllText((Join-Path $Directory "$Id.json"), ($pack | ConvertTo-Json -Depth 64), $utf8)
}

function Assert-ArtError([string]$Output, $Case) {
    $lines = @($Output -split '\r?\n' | Where-Object { $_.StartsWith('[错误] ') -and $_.Contains($Case.PackId + '.json') })
    Assert-Check ($lines.Count -eq 1) "$($Case.PackId) 没有且仅有一条明确的逐包错误。"
    $separator = $lines[0].IndexOf('：')
    Assert-Check ($separator -ge 0) '错误行缺少位置与说明分隔。'
    $message = $lines[0].Substring($separator + 1)
    Assert-Check ($message -cmatch '[\u4e00-\u9fff]' -and $message.Contains($Case.ExpectedId)) "$($Case.PackId) 的错误说明缺少中文或出错 id「$($Case.ExpectedId)」。"
    Assert-Check (-not $Output.Contains('内容包 ' + $Case.PackId + ' 内容指纹：')) "$($Case.PackId) 的非法资源清单被当作有效包。"
    return [ordered]@{ PackId = $Case.PackId; ExpectedId = $Case.ExpectedId; Error = $message }
}

foreach ($id in $builtIns) {
    $source = [IO.File]::ReadAllText((Join-Path $packDirectory "$id.json")) | ConvertFrom-Json -AsHashtable
    Assert-Check (-not $source.Contains('art')) "$id 不应为本任务补写 art，现有包须保持默认资源行为。"
}
$baseline = Invoke-PackCheck '01-builtins' @() 0
Assert-Summary $baseline.Out 4 0
foreach ($id in $builtIns) { Assert-Check ($baseline.Out.Contains('内容包 ' + $id + ' 内容指纹：')) "内置包 $id 未被检查。" }
$checks.Add('四个现有内容包均无 art，全部通过，来源文件不改动')

$validDirectory = Join-Path $runDirectory 'valid-packs'
$validArt = @{
    portraitDir = 'Portraits/t61_valid'
    streamers = @{ bell = @{ accent = '#fFcC33'; portrait = 'bell_v2' }; enka = @{} }
    skillFx = @{ full_volume = 'wave'; quick_mic = 'slash'; aim_burst = 'burst'; sketch = 'sparkle'; whisper_tide = 'beam'; meme_punch = 'ring'; iron_wall = 'shield'; deep_breath = 'heal' }
    statusIcons = @{ stall = 'lock'; flame = 'down'; lag = 'up'; reconnect = 'shield'; hype = 'mark'; early = 'clock'; sub = 'heart'; mirror = 'star' }
}
Write-Pack $validDirectory 't61_valid' $validArt
$valid = Invoke-PackCheck '02-valid-external' @('--pack-dir', $validDirectory, '--pack', 't61_valid') 0
Assert-Check ($valid.Out.Contains('内容包 t61_valid 内容指纹：')) '合法外部 art 未通过检查。'
Assert-Summary $valid.Out 1 0
$listed = Invoke-PackCheck '03-list-valid' @('--pack-dir', $validDirectory, '--list-packs') 0
Assert-Check ($listed.Out.Contains('t61_valid：资源清单验证包〔外部〕')) '合法外部资源包未出现在列包结果。'
$checks.Add('外部合法 portraitDir/portrait/accent；全部 8 种特效和 8 种状态/直播间图标通过检查与列包')

$invalidDirectory = Join-Path $runDirectory 'mixed-packs'
$cases = [Collections.Generic.List[object]]::new()
foreach ($case in @(
    @{ PackId='t61_bad_streamer'; ExpectedId='t61_missing_streamer'; Art=@{ streamers=@{ t61_missing_streamer=@{ accent='#ffffff' } } } },
    @{ PackId='t61_bad_skill_id'; ExpectedId='t61_missing_skill'; Art=@{ skillFx=@{ t61_missing_skill='wave' } } },
    @{ PackId='t61_bad_status_id'; ExpectedId='t61_missing_status'; Art=@{ statusIcons=@{ t61_missing_status='clock' } } },
    @{ PackId='t61_bad_fx'; ExpectedId='full_volume'; Art=@{ skillFx=@{ full_volume='laser' } } },
    @{ PackId='t61_bad_icon'; ExpectedId='stall'; Art=@{ statusIcons=@{ stall='skull' } } },
    @{ PackId='t61_bad_field_icon'; ExpectedId='shield'; Art=@{ statusIcons=@{ shield='skull' } } },
    @{ PackId='t61_bad_accent'; ExpectedId='bell'; Art=@{ streamers=@{ bell=@{ accent='yellow' } } } },
    @{ PackId='t61_bad_accent_short'; ExpectedId='bell'; Art=@{ streamers=@{ bell=@{ accent='#fff' } } } },
    @{ PackId='t61_bad_accent_hex'; ExpectedId='bell'; Art=@{ streamers=@{ bell=@{ accent='#gg0000' } } } }
)) { $cases.Add($case) }
$badPaths = @('/rooted', '../parent', 'Portraits/../parent', 'Portraits:local', 'C:/local', 'Portraits\local', 'http://invalid.example/local', 'Portraits/HTTP_local')
for ($index = 0; $index -lt $badPaths.Count; $index++) {
    $id = 't61_bad_dir_' + $index
    $cases.Add(@{ PackId=$id; ExpectedId=$id; Art=@{ portraitDir=$badPaths[$index] } })
    $cases.Add(@{ PackId=('t61_bad_portrait_' + $index); ExpectedId='bell'; Art=@{ streamers=@{ bell=@{ portrait=$badPaths[$index] } } } })
}
foreach ($case in $cases) { Write-Pack $invalidDirectory $case.PackId $case.Art }
Write-Pack $invalidDirectory 'zz_t61_valid_after_errors' $validArt
$mixed = Invoke-PackCheck '04-all-art-errors' @('--pack-dir', $invalidDirectory) 1
$expectedPacks = $builtIns.Count + $cases.Count + 1
Assert-Summary $mixed.Out $expectedPacks $cases.Count
$errorEvidence = @($cases | ForEach-Object { Assert-ArtError $mixed.Out $_ })
Assert-Check ($mixed.Out.Contains('内容包 zz_t61_valid_after_errors 内容指纹：')) '坏资源包阻止了后续合法包检查。'
$mixedList = Invoke-PackCheck '05-list-art-errors' @('--pack-dir', $invalidDirectory, '--list-packs') 1
foreach ($case in $cases) { $null = Assert-ArtError $mixedList.Out $case }
Assert-Check ($mixedList.Out.Contains('zz_t61_valid_after_errors：资源清单验证包〔外部〕')) '列包模式没有保留后续合法包。'
$checks.Add("$($cases.Count) 个错误包：引用 id、fx、状态/直播间 icon、accent 与两类路径均输出中文及出错 id，默认和列包模式均 exit 1")

$selected = Invoke-PackCheck '06-selected-art-error' @('--pack-dir', $invalidDirectory, '--pack', 't61_bad_fx') 1
Assert-Summary $selected.Out 1 1
$null = Assert-ArtError $selected.Out ($cases | Where-Object { $_.PackId -eq 't61_bad_fx' } | Select-Object -First 1)
$checks.Add('选择单个坏资源包时输出原始错误，不误报找不到包；所有检查不生成名单或资源文件')

# 独立复制运行文件和规则，使策略场景不触碰真实 Resources 或构建输出。
$fixtureRoot = Join-Path $runDirectory 'policy-fixture'
$packCheckBin = Join-Path $fixtureRoot 'Tools/PackCheck/bin'
$cliBin = Join-Path $fixtureRoot 'Tools/Cli/bin'
Copy-BinaryFiles $toolDll $packCheckBin
Copy-BinaryFiles $cliDll $cliBin
$fixturePackCheck = Join-Path $packCheckBin 'PackCheck.dll'
$fixtureCli = Join-Path $cliBin 'Cli.dll'
Write-Utf8 (Join-Path $fixtureRoot 'AGENTS.md') '# 验收隔离目录'
Write-Utf8 (Join-Path $fixtureRoot 'Engine/StreamDuel.Core.csproj') '<Project />'
$fixtureResources = Join-Path $fixtureRoot 'UnityProject/Assets/StreamDuel/Resources'
$fixtureContent = Join-Path $cliBin 'Content'
foreach ($destination in @($fixtureResources, $fixtureContent)) {
    [void][IO.Directory]::CreateDirectory((Join-Path $destination 'Packs'))
    Copy-Item -LiteralPath (Join-Path (Split-Path $packDirectory) 'StreamDuelRules.json') -Destination (Join-Path $destination 'StreamDuelRules.json')
    foreach ($id in $builtIns) {
        Copy-Item -LiteralPath (Join-Path $packDirectory "$id.json") -Destination (Join-Path $destination "Packs/$id.json")
    }
}
Write-Utf8 (Join-Path $fixtureRoot 'Tools/PackCheck/sensitive_words.txt') ([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'sensitive_words.txt')))
Write-Utf8 (Join-Path $fixtureResources 'Lint/banned.txt') ([IO.File]::ReadAllText((Join-Path (Split-Path $packDirectory) 'Lint/banned.txt')))
function Set-FixturePolicy([string]$Json) {
    Write-Utf8 (Join-Path $fixtureResources 'PackPolicy.json') $Json
    Write-Utf8 (Join-Path $fixtureContent 'PackPolicy.json') $Json
}

Set-FixturePolicy '{"disabledStreamers":["bell"],"reason":{"bell":"资源清单策略验证"}}'
$filtered = Invoke-PackCheck '07-disabled-streamer-art' @('--pack-dir', $validDirectory, '--pack', 't61_valid') 0 -Executable $fixturePackCheck
Assert-Summary $filtered.Out 1 0
Assert-Check ($filtered.Out.Contains('该主播已下架：bell（资源清单策略验证）') -and
    $filtered.Out.Contains('内容包 t61_valid 内容指纹：')) '带有显式 art 的主播下架后，剩余合法内容应继续通过检查。'

# --card 支持 --packs；这里只验加载与合并校验，资料卡本身不调用 Art 查询接口。
$mixedPacks = 't61_valid,original_wave2'
$filteredCard = Invoke-PackCheck '08-mixed-card-filtered' @('--card', 'enka', '--packs', $mixedPacks, '--pack-dir', $validDirectory) 0 -Executable $fixtureCli
Assert-Check ($filteredCard.Out.Contains('编号：enka') -and $filteredCard.Out.Contains('mix:original_wave2,t61_valid')) '下架一位主播后，含 art 的混合包仍应允许读取其他主播资料。'
$disabledCard = Invoke-PackCheck '09-mixed-card-disabled-streamer' @('--card', 'bell', '--packs', $mixedPacks, '--pack-dir', $validDirectory) 1 -Executable $fixtureCli -AllowStderr
Assert-Check ($disabledCard.Out.Length -eq 0 -and $disabledCard.Err.Contains('该主播已下架：bell（资源清单策略验证）')) '混合包资料查询必须明确拒绝已下架主播。'
$checks.Add('隔离下架一位有显式 art 的主播后，PackCheck 及 CLI 混合包读取其余主播正常，目标主播明确拒绝')

Set-FixturePolicy '{"disabledPacks":["t61_valid"],"reason":{"t61_valid":"资源包策略验证"}}'
$disabledPack = Invoke-PackCheck '10-disabled-pack-art' @('--pack-dir', $validDirectory, '--pack', 't61_valid') 0 -Executable $fixturePackCheck
Assert-Summary $disabledPack.Out 1 0 1
Assert-Check ($disabledPack.Out.Contains('[下架]') -and $disabledPack.Out.Contains('该内容包已下架：t61_valid（资源包策略验证）') -and
    -not $disabledPack.Out.Contains('内容包 t61_valid 内容指纹：')) '已下架 art 包应单独计数，不应显示可用内容指纹。'
$disabledMixed = Invoke-PackCheck '11-mixed-card-disabled-pack' @('--card', 'enka', '--packs', $mixedPacks, '--pack-dir', $validDirectory) 1 -Executable $fixtureCli -AllowStderr
Assert-Check ($disabledMixed.Out.Length -eq 0 -and $disabledMixed.Err.Contains('该内容包已下架：t61_valid（资源包策略验证）')) '混合包资料查询必须保留来源包下架门禁。'
$checks.Add('隔离下架 art 来源包后，PackCheck 正确计数并省略指纹，CLI 混合包资料查询保留来源包门禁')

Set-FixturePolicy '{}'
$restoredCard = Invoke-PackCheck '12-mixed-card-restored' @('--card', 'bell', '--packs', $mixedPacks, '--pack-dir', $validDirectory) 0 -Executable $fixtureCli
Assert-Check ($restoredCard.Out.Contains('编号：bell') -and $restoredCard.Out.Contains('mix:original_wave2,t61_valid')) '空策略下含 art 的混合包资料查询未恢复。'
$checks.Add('空策略下含 art 的混合包加载与资料查询通过；全部查询工作目录为空，不创建存档')

foreach ($id in $builtIns) {
    Assert-Check ((Get-FileHash -LiteralPath (Join-Path $packDirectory "$id.json") -Algorithm SHA256).Hash -ceq $sourceHashes[$id]) "验证修改了内置包 $id。"
}
Assert-Check ((Get-FileHash -LiteralPath $sourcePolicyPath -Algorithm SHA256).Hash -ceq $sourcePolicyHash) '隔离策略验证修改了真实 PackPolicy.json。'
$report = [ordered]@{ Status='PASS'; Checks=$checks.ToArray(); InvalidPackCount=$cases.Count; Errors=$errorEvidence; Invocations=$invocations.ToArray(); BuiltInSha256=$sourceHashes; PolicySha256=$sourcePolicyHash; MixedQueryScope='CLI --card covers content loading, merge validation and policy gates; Art query APIs are covered by engine tests; Unity import separately verifies compilation.' }
[IO.File]::WriteAllText((Join-Path $runDirectory 'report.json'), ($report | ConvertTo-Json -Depth 8), $utf8)
Write-Output "PASS：$($checks.Count) 组 art 输出检查、$($cases.Count) 个错误包；证据：$runDirectory"
