#requires -Version 7.0
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$repositoryPrefix = $repository.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$current = [IO.Path]::GetFullPath((Get-Location).Path)
if ($current -ne $repository -and -not $current.StartsWith($repositoryPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw '请在本仓库内运行此脚本。'
}
$runDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ('obj/team-code-check/' + [Guid]::NewGuid().ToString('N'))))
if (-not $runDirectory.StartsWith($repositoryPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw '验收产物目录必须位于本仓库内。'
}
[void][IO.Directory]::CreateDirectory($runDirectory)
$utf8 = [Text.UTF8Encoding]::new($false)
$cliDll = Join-Path $PSScriptRoot "bin/$Configuration/net8.0/Cli.dll"
$coreDll = Join-Path $PSScriptRoot "bin/$Configuration/net8.0/StreamDuel.Core.dll"
$checks = [Collections.Generic.List[string]]::new()
$invocations = [Collections.Generic.List[object]]::new()
$passed = $false

function Assert-Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "$Message 证据目录：$runDirectory" }
}

function New-Case([string]$Name) {
    $path = [IO.Path]::GetFullPath((Join-Path $runDirectory $Name))
    Assert-Check ($path.StartsWith($runDirectory + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) '用例目录越界。'
    [void][IO.Directory]::CreateDirectory($path)
    return $path
}

function Invoke-ProcessCheck([string]$Name, [string]$Directory, [string[]]$Arguments,
    [string]$InputText = '', [int]$ExpectedExit = 0, [int]$TimeoutMs = 30000) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = 'dotnet'
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $start.WorkingDirectory = $Directory
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
        if ($InputText.Length -gt 0) { $process.StandardInput.Write($InputText) }
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
        $invocations.Add([ordered]@{ name = $Name; exit = $result.Exit; timedOut = $timedOut; arguments = $Arguments })
        Assert-Check (-not $timedOut) "$Name 超过 $TimeoutMs 毫秒未退出。"
        Assert-Check ($result.Exit -eq $ExpectedExit) "$Name 应退出 $ExpectedExit，实际 $($result.Exit)。"
        return $result
    }
    finally {
        # 仅处理本次创建的进程对象，不按进程名枚举或结束其他窗口。
        if ($started -and -not $process.HasExited) {
            $process.Kill($true)
            [void]$process.WaitForExit(5000)
        }
        $process.Dispose()
    }
}

function Invoke-Cli([string]$Name, [string]$Directory, [string[]]$Parameters,
    [string]$InputText = '', [int]$ExpectedExit = 0) {
    return Invoke-ProcessCheck $Name $Directory (@($cliDll) + $Parameters) $InputText $ExpectedExit
}

function Extract-Code([string]$Output, [string]$Prefix, [string]$Label) {
    $matches = [regex]::Matches($Output, '(?m)^' + [regex]::Escape($Label) + '(' + [regex]::Escape($Prefix) + '[A-Za-z0-9_-]+)\r?$')
    Assert-Check ($matches.Count -eq 1) "输出应恰好包含一个 $Label"
    return $matches[0].Groups[1].Value
}

function Fingerprint([string]$Directory) {
    if (-not (Test-Path -LiteralPath $Directory)) { return '' }
    return (@(Get-ChildItem -LiteralPath $Directory -Recurse -Force | Sort-Object FullName | ForEach-Object {
        if ($_.PSIsContainer) { 'directory:' + $_.FullName }
        else { $_.FullName + ':' + (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    }) -join "`n")
}

function Assert-Rejected([string]$Name, [string[]]$Parameters) {
    $directory = New-Case $Name
    [IO.File]::WriteAllText((Join-Path $directory 'keep.txt'), '原有文件保持不变', $utf8)
    $before = Fingerprint $directory
    $result = Invoke-Cli $Name $directory ($Parameters + @('--save-dir', (Join-Path $directory 'saves'))) '' 1
    Assert-Check ($result.Err.Length -gt 0) "$Name 没有说明拒绝原因。"
    Assert-Check (-not $result.Out.Contains('本方队伍码：') -and -not $result.Out.Contains('分享码：') -and
        -not $result.Out.Contains('开播对决 · 命令行对战') -and -not $result.Out.Contains('开播对决 · 对战回看')) "$Name 拒绝前已经导出或进入对战。"
    Assert-Check ((Fingerprint $directory) -ceq $before) "$Name 拒绝输入后写入了文件或目录。"
}

try {
    if (-not $SkipBuild) {
        $null = Invoke-ProcessCheck '00-build' $repository @('build', (Join-Path $PSScriptRoot 'Cli.csproj'), '-c', $Configuration, '--nologo') '' 0 120000
    }
    Assert-Check ((Test-Path -LiteralPath $cliDll -PathType Leaf) -and (Test-Path -LiteralPath $coreDll -PathType Leaf)) "请先运行 dotnet build Tools/Cli -c $Configuration。"
    $assembly = [Reflection.Assembly]::LoadFrom($coreDll)
    Assert-Check ([IO.Path]::GetFullPath($assembly.Location) -eq [IO.Path]::GetFullPath($coreDll)) '当前 PowerShell 已加载其他 Core，请在新 PowerShell 进程运行。'
    $ids = @('bell', 'enka', 'shiori', 'lumi', 'hane', 'momo')
    $exportDirectory = New-Case 'export'
    $export = Invoke-Cli '01-export' $exportDirectory @('--pack', 'original', '--team', ($ids -join ','), '--export-team', '--seed', '1') "q`n"
    $teamCode = Extract-Code $export.Out 'SDT1-' '本方队伍码：'
    $decoded = [StreamDuel.Core.TeamCode]::Decode($teamCode)
    Assert-Check ($decoded.PackId -ceq 'original' -and ($decoded.Ids -join ',') -ceq ($ids -join ',')) '导出码没有保留所选包和队伍顺序。'
    Assert-Check ($teamCode.Length -lt 200 -and $export.Err.Length -eq 0) '普通队伍码过长或导出产生错误。'
    Assert-Check (-not (Test-Path -LiteralPath (Join-Path $exportDirectory 'saves'))) '导出后 q 退出仍创建存档。'

    $importDirectory = New-Case 'import-whitespace'
    $wrapped = [regex]::Replace($teamCode, '(.{16})', "`$1 `r`n`t")
    $import = Invoke-Cli '02-import-whitespace' $importDirectory @('--team-code', $wrapped, '--export-team', '--seed', '1') "q`n"
    Assert-Check ((Extract-Code $import.Out 'SDT1-' '本方队伍码：') -ceq $teamCode) '含空白队伍码再导出不一致。'
    Assert-Check ($import.Err.Length -eq 0 -and -not (Test-Path -LiteralPath (Join-Path $importDirectory 'saves'))) '导入后 q 退出产生错误或存档。'

    $fanDirectory = New-Case 'fan-export'
    $fan = Invoke-Cli '03-fan-export' $fanDirectory @('--pack', 'fan_asoul_vr', '--team', 'bella,diana,eileen,nana7mi,koichi,aza', '--export-team') "q`n"
    $fanCode = Extract-Code $fan.Out 'SDT1-' '本方队伍码：'
    $fanImportDirectory = New-Case 'fan-import'
    $fanImport = Invoke-Cli '04-fan-import' $fanImportDirectory @('--team-code', $fanCode, '--export-team') "q`n"
    Assert-Check ((Extract-Code $fanImport.Out 'SDT1-' '本方队伍码：') -ceq $fanCode -and $fanImport.Err.Length -eq 0) '省略 --pack 时没有采用队伍码里的内容包。'
    Assert-Check (-not (Test-Path -LiteralPath (Join-Path $fanDirectory 'saves')) -and -not (Test-Path -LiteralPath (Join-Path $fanImportDirectory 'saves'))) '同人包导出/导入后 q 退出创建了存档。'
    $checks.Add('指定六人顺序导出、含空白码导入再导出、自动选择码内内容包；提前退出不保存')

    $gameDirectory = New-Case 'complete-game'
    $save = Join-Path $gameDirectory 'saves'
    $namedPreset = [StreamDuel.Core.TeamCode]::Decode($teamCode)
    $namedPreset.Name = '自定义「夜间六人队」'
    $namedCode = [StreamDuel.Core.TeamCode]::Encode($namedPreset)
    $game = Invoke-Cli '05-game' $gameDirectory @('--team-code', $namedCode, '--export-team', '--auto', '--ban', '--lead', '--seed', '1', '--share', '--save-dir', $save)
    $gameTeamCode = Extract-Code $game.Out 'SDT1-' '本方队伍码：'
    $gamePreset = [StreamDuel.Core.TeamCode]::Decode($gameTeamCode)
    Assert-Check ($gameTeamCode -ceq $namedCode -and $gamePreset.Name -ceq $namedPreset.Name -and
        ($gamePreset.Ids -join ',') -ceq ($ids -join ',') -and $game.Err.Length -eq 0) '禁用/首发对局导出没有保留原六人及自定义名称。'
    $shareCode = Extract-Code $game.Out 'SD1-' '分享码：'
    $records = @(Get-ChildItem -LiteralPath (Join-Path $save 'replays') -File | Where-Object Name -Match '^[0-9]{8}_[0-9]{6}_[0-9]{7}_[A-Za-z0-9_-]+\.json$')
    Assert-Check ($records.Count -eq 1) '完整对局未恰好保存一份回放。'
    $recordJson = [IO.File]::ReadAllText($records[0].FullName)
    $record = $recordJson | ConvertFrom-Json
    Assert-Check (($record.teams[0] -join ',') -ceq ($ids -join ',')) '真实对局未使用导入的六人队伍。'
    Assert-Check ($record.banOne -and $record.leadPick -and
        @($record.events | Where-Object type -EQ 'ban').Count -eq 2 -and
        @($record.events | Where-Object type -EQ 'lead').Count -eq 2) '完整对局没有实际执行双方禁用和首发。'
    $profile = [IO.File]::ReadAllText((Join-Path $save 'profile.json')) | ConvertFrom-Json
    Assert-Check (($profile.packs.original.wins + $profile.packs.original.losses + $profile.packs.original.draws) -eq 1) '完整对局战绩未恰好累计一次。'
    $stored = Fingerprint $save
    $explicitDirectory = New-Case 'explicit-team-baseline'
    $explicitSave = Join-Path $explicitDirectory 'saves'
    $explicit = Invoke-Cli '05-explicit-team-baseline' $explicitDirectory @('--pack', 'original', '--team', ($ids -join ','), '--auto', '--ban', '--lead', '--seed', '1', '--share', '--save-dir', $explicitSave)
    $explicitRecords = @(Get-ChildItem -LiteralPath (Join-Path $explicitSave 'replays') -File | Where-Object Name -Match '^[0-9]{8}_[0-9]{6}_[0-9]{7}_[A-Za-z0-9_-]+\.json$')
    Assert-Check ($explicitRecords.Count -eq 1 -and $explicit.Err.Length -eq 0) '显式队伍基准对局未正常保存一份回放。'
    Assert-Check ([IO.File]::ReadAllText($explicitRecords[0].FullName) -ceq $recordJson) '相同种子下，队伍码与显式六人队伍的完整回放 JSON 不一致。'
    Assert-Check ((Extract-Code $explicit.Out 'SD1-' '分享码：') -ceq $shareCode) '显式队伍基准与导入队伍的分享码不一致。'
    $replay = Invoke-Cli '06-share-replay' $gameDirectory @('--replay-code', $shareCode, '--auto', '--share', '--save-dir', $save)
    Assert-Check ((Extract-Code $replay.Out 'SD1-' '分享码：') -ceq $shareCode -and $replay.Err.Length -eq 0) '原有 T48 分享码回看/再分享退化。'
    Assert-Check ((Fingerprint $save) -ceq $stored) '分享码回看改变了存档。'
    $checks.Add('自定义名称/原六人保留，双方禁用首发真实完成；同种子显式队伍完整回放逐字一致；只累计一次且分享回看不改存档')

    Assert-Rejected 'reject-corrupt' @('--team-code', $teamCode.Substring(0, $teamCode.Length - 2))
    Assert-Rejected 'reject-empty' @('--team-code', '')
    Assert-Rejected 'reject-pack' @('--team-code', $teamCode, '--pack', 'fan_asoul_vr')
    Assert-Rejected 'reject-two-sources' @('--team-code', $teamCode, '--team', ($ids -join ','))
    Assert-Rejected 'reject-duplicate-code' @('--team-code', $teamCode, '--team-code', $teamCode)
    foreach ($invalid in @('unknown', 'duplicate', 'five', 'missing-pack')) {
        $preset = [StreamDuel.Core.TeamCode]::Decode($teamCode)
        if ($invalid -eq 'unknown') { $preset.Ids[0] = 'missing_streamer' }
        if ($invalid -eq 'duplicate') { $preset.Ids[0] = $preset.Ids[1] }
        if ($invalid -eq 'five') { $preset.Ids.RemoveAt(5) }
        if ($invalid -eq 'missing-pack') { $preset.PackId = 'missing_pack' }
        Assert-Rejected ("reject-$invalid") @('--team-code', [StreamDuel.Core.TeamCode]::Encode($preset), '--export-team')
    }
    foreach ($mode in @('file', 'code')) {
        $parameters = if ($mode -eq 'file') { @('--replay', $records[0].FullName) } else { @('--replay-code', $shareCode) }
        Assert-Rejected ("reject-$mode-team") ($parameters + @('--team-code', $teamCode))
        Assert-Rejected ("reject-$mode-export") ($parameters + @('--export-team'))
    }
    Assert-Rejected 'reject-replay-as-team' @('--team-code', $shareCode)
    Assert-Rejected 'reject-team-as-replay' @('--replay-code', $teamCode)
    Assert-Rejected 'reject-list-export' @('--list-packs', '--export-team')
    $checks.Add('坏码、非法队伍/包、重复来源、回放与队伍参数冲突及两种码混用均拒绝且不落盘')

    foreach ($mode in @('game', 'file', 'code')) {
        foreach ($reserved in @('teams.json', '.teams.json.tmp', '.teams.lock')) {
            $name = "report-$mode-$($reserved.Replace('.', '_'))"
            $directory = New-Case $name
            $protected = Join-Path $directory 'saves'
            [void][IO.Directory]::CreateDirectory($protected)
            foreach ($file in @('teams.json', '.teams.json.tmp', '.teams.lock')) {
                [IO.File]::WriteAllText((Join-Path $protected $file), '保留原队伍预设：' + $file, $utf8)
            }
            $report = Join-Path $protected $reserved
            $parameters = switch ($mode) {
                'game' { @('--auto', '--team-code', $teamCode) }
                'file' { @('--auto', '--replay', $records[0].FullName) }
                'code' { @('--auto', '--replay-code', $shareCode) }
            }
            $before = Fingerprint $directory
            $result = Invoke-Cli $name $directory ($parameters + @('--save-dir', $protected, '--report', $report)) '' 1
            Assert-Check ($result.Err.Contains('--report') -and $result.Err.Contains($reserved)) "$name 未由保留路径检查拒绝。"
            Assert-Check (-not $result.Out.Contains('战报已保存') -and (Fingerprint $directory) -ceq $before) "$name 改动了预设或创建了其他存档。"
        }
    }
    Assert-Check ((Fingerprint $save) -ceq $stored) '拒绝输入或路径检查改变了此前的真实对局存档。'
    $checks.Add('新局、文件回放、分享码回放均保护 teams.json、.teams.json.tmp 和 .teams.lock')
    $passed = $true
    Write-Output "PASS：队伍码 CLI 与预设战报保护；证据目录：$runDirectory"
}
finally {
    $summary = [ordered]@{ passed = $passed; checks = $checks.ToArray(); invocations = $invocations.ToArray(); directory = $runDirectory }
    [IO.File]::WriteAllText((Join-Path $runDirectory 'result.json'), ($summary | ConvertTo-Json -Depth 8), $utf8)
}
