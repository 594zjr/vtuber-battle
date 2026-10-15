#requires -Version 7.0
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$cli = Join-Path $repository "Tools/Cli/bin/$Configuration/net8.0/Cli.dll"
if (-not [IO.File]::Exists($cli)) { throw '请先构建 Tools/Cli；本脚本不执行构建。' }
$run = Join-Path $repository ('Tools/Cli/obj/T106/cli-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($run)
$utf8 = [Text.UTF8Encoding]::new($false)
$results = [Collections.Generic.List[object]]::new()

function Assert-Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "$Message；证据：$run" }
}

function Invoke-Check([string]$Name, [string[]]$Arguments, [int]$Exit = 0, [string]$ErrorText = '') {
    $start = [Diagnostics.ProcessStartInfo]::new('dotnet')
    $start.ArgumentList.Add($cli)
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $start.WorkingDirectory = $run
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = [Text.Encoding]::UTF8
    $start.StandardErrorEncoding = [Text.Encoding]::UTF8
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    try {
        [void]$process.Start()
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $process.StandardInput.Close()
        if (-not $process.WaitForExit(60000)) {
            $process.Kill($true)
            $process.WaitForExit()
            throw "CLI 验收超时：$Name"
        }
        $outText = $stdout.GetAwaiter().GetResult()
        $errText = $stderr.GetAwaiter().GetResult()
        [IO.File]::WriteAllText((Join-Path $run "$Name.stdout.log"), $outText, $utf8)
        [IO.File]::WriteAllText((Join-Path $run "$Name.stderr.log"), $errText, $utf8)
        $results.Add([ordered]@{ name = $Name; exit = $process.ExitCode; arguments = $Arguments })
        Assert-Check ($process.ExitCode -eq $Exit) "$Name 预期退出 $Exit，实际 $($process.ExitCode)"
        if ($Exit -eq 1) { Assert-Check ($errText.StartsWith('错误：')) "$Name 应返回中文错误" }
        if ($ErrorText) { Assert-Check ($errText.Contains($ErrorText)) "$Name 错误应含 $ErrorText" }
        if ($Exit -eq 0) { Assert-Check ($errText -eq '') "$Name 成功时不应有 stderr" }
        return $outText
    }
    finally { $process.Dispose() }
}

function Snapshot([string]$Directory) {
    if (-not [IO.Directory]::Exists($Directory)) { return '<missing>' }
    $lines = foreach ($file in Get-ChildItem -LiteralPath $Directory -File -Recurse -Force | Sort-Object FullName) {
        [IO.Path]::GetRelativePath($Directory, $file.FullName) + ':' + (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    }
    return $lines -join "`n"
}

function ComparableArchive([string]$Path) {
    return [regex]::Replace([IO.File]::ReadAllText($Path), '"exportedAt":"[^"]*"', '"exportedAt":"<caller-time>"')
}

try {
    $source = Join-Path $run 'source'
    $target = Join-Path $run 'restored'
    $archive = Join-Path $run 'portable/backup.json'
    $null = Invoke-Check '01-create-real-game' @('--auto', '--pack', 'original', '--seed', '106', '--save-dir', $source)
    $legacy = @(Get-ChildItem -LiteralPath (Join-Path $source 'replays') -Filter '*.json' -File | Where-Object { -not $_.Name.StartsWith('.') })
    Assert-Check ($legacy.Count -eq 1) '实际 CLI 对局应保存一份旧回放'
    $record = [IO.File]::ReadAllText($legacy[0].FullName) | ConvertFrom-Json
    $settings = '{"aiLevel":"Hard","animations":false,"defaultPack":"original","fastPlay":true,"language":"zh-CN","turnTimer":true,"version":1}'
    [IO.File]::WriteAllText((Join-Path $source 'settings.json'), $settings, $utf8)
    $teams = [ordered]@{ items = @([ordered]@{ name = '迁移队伍'; packId = 'original'; ids = @($record.teams[0]) }) }
    [IO.File]::WriteAllText((Join-Path $source 'teams.json'), ($teams | ConvertTo-Json -Depth 12 -Compress), $utf8)
    $sourceBefore = Snapshot $source
    $null = Invoke-Check '02-export' @('--backup', $archive, '--save-dir', $source)
    Assert-Check ((Snapshot $source) -ceq $sourceBefore) '导出不能改变源存档文件'
    $backup = [IO.File]::ReadAllText($archive) | ConvertFrom-Json
    Assert-Check ($backup.format -eq 'StreamDuelBackup' -and $backup.version -eq 1 -and $backup.includeReplays) '应产生版本 1、含录像的单文件备份'
    Assert-Check (@($backup.replays).Count -eq 1 -and @($backup.library).Count -eq 1) '旧回放与录像库均应包含一份真实录像'
    $null = Invoke-Check '03-restore-empty' @('--restore', $archive, '--save-dir', $target)
    $roundtrip = Join-Path $run 'roundtrip.json'
    $null = Invoke-Check '04-export-restored' @('--backup', $roundtrip, '--save-dir', $target)
    Assert-Check ((ComparableArchive $archive) -ceq (ComparableArchive $roundtrip)) '空目录恢复后再次导出除调用方时间外须完全一致'

    $targetSettings = $settings.Replace('"Hard"', '"Easy"')
    [IO.File]::WriteAllText((Join-Path $target 'settings.json'), $targetSettings, $utf8)
    $profile = [IO.File]::ReadAllText((Join-Path $target 'profile.json')) | ConvertFrom-Json
    $profile.packs.original.wins = 42
    [IO.File]::WriteAllText((Join-Path $target 'profile.json'), ($profile | ConvertTo-Json -Depth 12 -Compress), $utf8)
    $mergedText = Invoke-Check '05-merge' @('--restore', $archive, '--save-dir', $target)
    $mergedTeams = [IO.File]::ReadAllText((Join-Path $target 'teams.json')) | ConvertFrom-Json
    Assert-Check (@($mergedTeams.items).Count -eq 2 -and $mergedTeams.items[1].name -ceq '迁移队伍 (2)') '同名队伍应加序号'
    Assert-Check ([IO.File]::ReadAllText((Join-Path $target 'settings.json')) -ceq $targetSettings) '合并应保留现有设置'
    $mergedProfile = [IO.File]::ReadAllText((Join-Path $target 'profile.json')) | ConvertFrom-Json
    Assert-Check ($mergedProfile.packs.original.wins -eq 42) '合并应保留同包统计而不累加'
    Assert-Check ($mergedText.Contains('回放：0；录像库录像：0')) '合并重复录像 id 不应再次导入'
    $beforeOverwrite = Join-Path $run 'before-overwrite.json'
    $null = Invoke-Check '06-export-before-overwrite' @('--backup', $beforeOverwrite, '--save-dir', $target)
    $overwriteText = Invoke-Check '07-overwrite' @('--restore', $archive, '--overwrite', '--save-dir', $target)
    $automatic = @(Get-ChildItem -LiteralPath (Join-Path $target 'backups') -File -Filter 'before-restore-*.json')
    Assert-Check ($automatic.Count -eq 1 -and $overwriteText.Contains($automatic[0].FullName)) '覆盖前自动备份须落盘且打印路径'
    Assert-Check ((ComparableArchive $automatic[0].FullName) -ceq (ComparableArchive $beforeOverwrite)) '自动备份须保存覆盖前内容'
    $overwritten = Join-Path $run 'overwritten.json'
    $null = Invoke-Check '08-export-overwritten' @('--backup', $overwritten, '--save-dir', $target)
    Assert-Check ((ComparableArchive $overwritten) -ceq (ComparableArchive $archive)) '覆盖后内容须与原归档一致'
    $null = Invoke-Check '08a-restore-automatic-backup' @('--restore', $automatic[0].FullName, '--overwrite', '--save-dir', $target)
    $rolledBack = Join-Path $run 'restored-automatic.json'
    $null = Invoke-Check '08b-export-restored-automatic' @('--backup', $rolledBack, '--save-dir', $target)
    Assert-Check ((ComparableArchive $rolledBack) -ceq (ComparableArchive $beforeOverwrite)) '须能直接从本目标 backups 自动备份恢复覆盖前内容'

    $bad = Join-Path $run 'bad.json'
    [IO.File]::WriteAllText($bad, '{broken', $utf8)
    $missingTarget = Join-Path $run 'bad-target'
    $null = Invoke-Check '09-invalid-json' @('--restore', $bad, '--save-dir', $missingTarget) 1
    Assert-Check (-not [IO.Directory]::Exists($missingTarget)) '坏文件不得创建目标目录'
    $targetBefore = Snapshot $target
    $null = Invoke-Check '10-invalid-overwrite' @('--restore', $bad, '--overwrite', '--save-dir', $target) 1
    Assert-Check ((Snapshot $target) -ceq $targetBefore) '坏文件覆盖不得改变现有目标'
    $future = Join-Path $run 'future.json'
    [IO.File]::WriteAllText($future, ([regex]::new('"version":1').Replace([IO.File]::ReadAllText($archive), '"version":999', 1)), $utf8)
    $null = Invoke-Check '11-future-version' @('--restore', $future, '--save-dir', $missingTarget) 1
    Assert-Check (-not [IO.Directory]::Exists($missingTarget)) '未来版本不得创建目标目录'
    $large = Join-Path $run 'large.json'
    $largeStream = [IO.File]::Open($large, [IO.FileMode]::CreateNew)
    try { $largeStream.SetLength(64L * 1024 * 1024 + 1) } finally { $largeStream.Dispose() }
    $null = Invoke-Check '12-over-limit' @('--restore', $large, '--save-dir', $missingTarget) 1 '64 MiB'
    Assert-Check (-not [IO.Directory]::Exists($missingTarget)) '过大文件不得创建目标目录'
    $archiveBefore = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
    $null = Invoke-Check '13-existing-output' @('--backup', $archive, '--save-dir', $source) 1 '已存在'
    Assert-Check ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ceq $archiveBefore) '已有输出文件不得覆盖'

    $invalidCases = @(
        @{ name = '14-exclusive-backup-restore'; args = @('--backup', $archive, '--restore', $archive) },
        @{ name = '15-exclusive-auto'; args = @('--backup', $archive, '--auto') },
        @{ name = '16-exclusive-pack'; args = @('--restore', $archive, '--pack', 'original') },
        @{ name = '17-overwrite-without-restore'; args = @('--overwrite') },
        @{ name = '18-overwrite-export'; args = @('--backup', $archive, '--overwrite') },
        @{ name = '19-duplicate-backup'; args = @('--backup', $archive, '--backup', $archive) },
        @{ name = '20-duplicate-restore'; args = @('--restore', $archive, '--restore', $archive) },
        @{ name = '21-duplicate-overwrite'; args = @('--restore', $archive, '--overwrite', '--overwrite') },
        @{ name = '22-empty-backup'; args = @('--backup', '') },
        @{ name = '23-empty-restore'; args = @('--restore', ' ') },
        @{ name = '24-missing-value'; args = @('--backup') },
        @{ name = '25-empty-root'; args = @('--backup', $archive, '--save-dir', '') }
    )
    foreach ($case in $invalidCases) { $null = Invoke-Check $case.name $case.args 1 }
    Assert-Check (-not [IO.Directory]::Exists((Join-Path $run 'saves'))) '非法参数不得启动默认对战或创建默认存档'
    $null = Invoke-Check '26-output-in-root' @('--backup', (Join-Path $source 'forbidden.json'), '--save-dir', $source) 1 '之外'
    $null = Invoke-Check '27-input-in-root' @('--restore', (Join-Path $source 'settings.json'), '--save-dir', $source) 1 '之外'
    $null = Invoke-Check '28-root-as-file' @('--backup', $source, '--save-dir', $source) 1 '之外'
    $null = Invoke-Check '29-control-character' @('--backup', (Join-Path $run "bad`nname.json"), '--save-dir', $source) 1 '控制字符'
    $null = Invoke-Check '30-output-directory' @('--backup', (Join-Path $run 'portable'), '--save-dir', $source) 1 '类型'
    $null = Invoke-Check '31-missing-input' @('--restore', (Join-Path $run 'absent.json'), '--save-dir', $missingTarget) 1 '读取备份失败'
    Assert-Check (-not [IO.Directory]::Exists($missingTarget)) '缺失输入不得创建目标目录'

    if ($IsWindows) {
        $null = Invoke-Check '32-device-name' @('--backup', (Join-Path $run 'CON.json'), '--save-dir', $source) 1 '设备名'
        $null = Invoke-Check '33-dot-alias' @('--backup', (Join-Path $run 'alias.'), '--save-dir', $source) 1 '结尾'
        $null = Invoke-Check '34-alternate-stream' @('--backup', (Join-Path $run 'archive.json:payload'), '--save-dir', $source) 1 '无效字符'
        $junctionTarget = Join-Path $run 'junction-target'
        $junction = Join-Path $run 'junction'
        [void][IO.Directory]::CreateDirectory($junctionTarget)
        $null = New-Item -ItemType Junction -Path $junction -Target $junctionTarget
        $null = Invoke-Check '35-reparse-output' @('--backup', (Join-Path $junction 'archive.json'), '--save-dir', $source) 1 '重解析点'
        $null = Invoke-Check '36-reparse-root' @('--restore', $archive, '--save-dir', (Join-Path $junction 'saves')) 1 '重解析点'
        Assert-Check (@(Get-ChildItem -LiteralPath $junctionTarget -Force).Count -eq 0) '拒绝重解析点后不得写入其目标'
    }
    $help = Invoke-Check '37-help' @('--help')
    Assert-Check ($help.Contains('--backup') -and $help.Contains('--restore') -and $help.Contains('--overwrite')) '帮助应列出三个参数'
    Assert-Check ((Snapshot $source) -ceq $sourceBefore) '全部验收后源存档须保持不变'
    Assert-Check (@(Get-ChildItem -LiteralPath (Join-Path $run 'portable') -File -Filter '.backup-*.tmp').Count -eq 0) '导出成功或被拒绝后不能遗留临时文件'
    $report = [ordered]@{ passed = $true; commands = $results.Count; runDirectory = $run; backupSha256 = $archiveBefore; checks = $results }
    [IO.File]::WriteAllText((Join-Path $run 'report.json'), ($report | ConvertTo-Json -Depth 12), $utf8)
    Write-Output "PASS: $($results.Count) 次 CLI 命令；证据：$run"
}
catch {
    [IO.File]::WriteAllText((Join-Path $run 'failure.txt'), ($_ | Out-String), $utf8)
    [IO.File]::WriteAllText((Join-Path $run 'partial-report.json'), ($results | ConvertTo-Json -Depth 12), $utf8)
    throw
}
