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
$runDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ('obj/art-check/' + [Guid]::NewGuid().ToString('N'))))
if (-not $runDirectory.StartsWith($repositoryPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw '验证产物必须位于仓库内。' }
[void][IO.Directory]::CreateDirectory($runDirectory)
$utf8 = [Text.UTF8Encoding]::new($false)
$checks = [Collections.Generic.List[string]]::new()
$invocations = [Collections.Generic.List[object]]::new()
$packDirectory = Join-Path $repository 'UnityProject/Assets/StreamDuel/Resources/Packs'
$original = [IO.File]::ReadAllText((Join-Path $packDirectory 'original.json'))
$sourceHashes = @{}
$builtIns = @('original', 'original_wave2', 'fan_asoul_vr', 'fan_holo_niji')
foreach ($id in $builtIns) {
    $path = Join-Path $packDirectory "$id.json"
    $sourceHashes[$id] = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
}

function Assert-Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "$Message 证据目录：$runDirectory" }
}

function Invoke-PackCheck([string]$Name, [string[]]$Parameters, [int]$ExpectedExit) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = 'dotnet'
    $start.ArgumentList.Add($toolDll)
    foreach ($parameter in $Parameters) { $start.ArgumentList.Add($parameter) }
    $start.WorkingDirectory = $runDirectory
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
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
        if (-not $process.WaitForExit(30000)) {
            $process.Kill($true)
            [void]$process.WaitForExit(5000)
            throw "$Name 超过 30 秒未退出。"
        }
        $result = [pscustomobject]@{ Out = $stdout.Result; Err = $stderr.Result; Exit = $process.ExitCode }
        [IO.File]::WriteAllText((Join-Path $runDirectory "$Name.stdout.log"), $result.Out, $utf8)
        [IO.File]::WriteAllText((Join-Path $runDirectory "$Name.stderr.log"), $result.Err, $utf8)
        $invocations.Add([ordered]@{ Name = $Name; Parameters = $Parameters; Exit = $result.Exit })
        Assert-Check ($result.Exit -eq $ExpectedExit) "$Name 应退出 $ExpectedExit，实际 $($result.Exit)。"
        Assert-Check ($result.Err.Length -eq 0) "$Name 不应绕过逐包输出落入顶层错误处理。"
        return $result
    }
    finally { $process.Dispose() }
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
Assert-Check ($baseline.Out.Contains('检查完成：4 个内容包，0 个错误，')) '四个无 art 内置包未全部通过。'
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
Assert-Check ($valid.Out.Contains('内容包 t61_valid 内容指纹：') -and $valid.Out.Contains('检查完成：1 个内容包，0 个错误，')) '合法外部 art 未通过检查。'
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
Assert-Check ($mixed.Out.Contains("检查完成：$expectedPacks 个内容包，$($cases.Count) 个错误，")) '批量错误总数或参与检查的包数不正确。'
$errorEvidence = @($cases | ForEach-Object { Assert-ArtError $mixed.Out $_ })
Assert-Check ($mixed.Out.Contains('内容包 zz_t61_valid_after_errors 内容指纹：')) '坏资源包阻止了后续合法包检查。'
$mixedList = Invoke-PackCheck '05-list-art-errors' @('--pack-dir', $invalidDirectory, '--list-packs') 1
foreach ($case in $cases) { $null = Assert-ArtError $mixedList.Out $case }
Assert-Check ($mixedList.Out.Contains('zz_t61_valid_after_errors：资源清单验证包〔外部〕')) '列包模式没有保留后续合法包。'
$checks.Add("$($cases.Count) 个错误包：引用 id、fx、状态/直播间 icon、accent 与两类路径均输出中文及出错 id，默认和列包模式均 exit 1")

$selected = Invoke-PackCheck '06-selected-art-error' @('--pack-dir', $invalidDirectory, '--pack', 't61_bad_fx') 1
Assert-Check ($selected.Out.Contains('检查完成：1 个内容包，1 个错误，')) '按文件名选择坏 art 包时没有保留具体错误。'
$null = Assert-ArtError $selected.Out ($cases | Where-Object { $_.PackId -eq 't61_bad_fx' } | Select-Object -First 1)
$checks.Add('选择单个坏资源包时输出原始错误，不误报找不到包；所有检查不生成名单或资源文件')

foreach ($id in $builtIns) {
    Assert-Check ((Get-FileHash -LiteralPath (Join-Path $packDirectory "$id.json") -Algorithm SHA256).Hash -ceq $sourceHashes[$id]) "验证修改了内置包 $id。"
}
$report = [ordered]@{ Status='PASS'; Checks=$checks.ToArray(); InvalidPackCount=$cases.Count; Errors=$errorEvidence; Invocations=$invocations.ToArray(); BuiltInSha256=$sourceHashes }
[IO.File]::WriteAllText((Join-Path $runDirectory 'report.json'), ($report | ConvertTo-Json -Depth 8), $utf8)
Write-Output "PASS：$($checks.Count) 组 art 输出检查、$($cases.Count) 个错误包；证据：$runDirectory"
