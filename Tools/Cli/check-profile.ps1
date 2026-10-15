param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$current = [IO.Path]::GetFullPath((Get-Location).Path)
$repositoryPrefix = $repository.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if ($current -ne $repository -and -not $current.StartsWith($repositoryPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw '请在本仓库目录内运行此脚本。'
}
$cliDll = Join-Path $PSScriptRoot "bin/$Configuration/net8.0/Cli.dll"
if (-not (Test-Path -LiteralPath $cliDll -PathType Leaf)) {
    throw "CLI 尚未构建，请先运行 dotnet build Tools/Cli -c $Configuration。"
}

# 每次留存独立的日志与档案，避免清理时碰到已有产物。
$checkRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'obj/profile-check'))
$runDirectory = Join-Path $checkRoot ([Guid]::NewGuid().ToString('N'))
if (-not $runDirectory.StartsWith($repositoryPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw '回归产物目录必须位于本仓库内。'
}
[void][IO.Directory]::CreateDirectory($runDirectory)
$saveDirectory = Join-Path $runDirectory 'saved'
$profilePath = Join-Path $saveDirectory 'profile.json'
$temporaryPath = Join-Path $saveDirectory '.profile.json.tmp'
$lockPath = Join-Path $saveDirectory '.profile.lock'
$utf8 = New-Object Text.UTF8Encoding($false)

function Assert-Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Invoke-CliCheck([string]$Name, [string]$Directory, [int]$ExpectedExit, [switch]$Quit) {
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = 'dotnet'
    $start.Arguments = '"' + $cliDll + '" --pack original --level normal --seed 1 --save-dir "' + $Directory + '"'
    if (-not $Quit) { $start.Arguments += ' --auto' }
    $start.WorkingDirectory = $runDirectory
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = [Text.Encoding]::UTF8
    $start.StandardErrorEncoding = [Text.Encoding]::UTF8
    $process = New-Object Diagnostics.Process
    $process.StartInfo = $start
    try {
        [void]$process.Start()
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if ($Quit) { $process.StandardInput.WriteLine('q') }
        $process.StandardInput.Close()
        $process.WaitForExit()
        [IO.File]::WriteAllText((Join-Path $runDirectory "$Name.stdout.log"), $stdout.Result, $utf8)
        [IO.File]::WriteAllText((Join-Path $runDirectory "$Name.stderr.log"), $stderr.Result, $utf8)
        Assert-Check ($process.ExitCode -eq $ExpectedExit) "$Name 退出码应为 $ExpectedExit，实际为 $($process.ExitCode)。日志：$runDirectory"
    }
    finally { $process.Dispose() }
}

function Read-Stats {
    $profile = [IO.File]::ReadAllText($profilePath) | ConvertFrom-Json
    Assert-Check (@($profile.packs.PSObject.Properties).Count -eq 1) '只应产生 original 内容包的战绩。'
    return $profile.packs.original
}

function Assert-Accumulation([int]$Games, $Baseline) {
    $stats = Read-Stats
    foreach ($name in @('wins', 'losses', 'draws')) {
        Assert-Check ($stats.$name -eq $Baseline.$name * $Games) "$name 未正确累计到第 $Games 局。"
    }
    Assert-Check (@($stats.picks.PSObject.Properties).Count -eq 6) '开局六位主播都应计入选用次数。'
    foreach ($pick in $Baseline.picks.PSObject.Properties) {
        Assert-Check ($stats.picks.($pick.Name) -eq $Games) "$($pick.Name) 的选用次数不正确。"
        Assert-Check ($stats.pickWins.($pick.Name) -eq $stats.wins) "$($pick.Name) 的选用胜场不正确。"
    }
}

Invoke-CliCheck '01-first' $saveDirectory 0
$baseline = Read-Stats
Assert-Check (($baseline.wins + $baseline.losses + $baseline.draws) -eq 1) '首局应恰好记录一次结果。'
Assert-Accumulation 1 $baseline
Invoke-CliCheck '02-accumulate' $saveDirectory 0
Assert-Accumulation 2 $baseline

[IO.File]::WriteAllText($temporaryPath, 'interrupted save', $utf8)
Invoke-CliCheck '03-stale-temporary' $saveDirectory 0
Assert-Accumulation 3 $baseline
Assert-Check (-not (Test-Path -LiteralPath $temporaryPath)) '成功保存后不应遗留临时文件。'

$beforeLocked = (Get-FileHash -LiteralPath $profilePath -Algorithm SHA256).Hash
[IO.File]::WriteAllText($temporaryPath, 'owned by lock holder', $utf8)
$heldLock = [IO.File]::Open($lockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
try {
    Invoke-CliCheck '04-locked' $saveDirectory 1
    Assert-Check ((Get-FileHash -LiteralPath $profilePath -Algorithm SHA256).Hash -eq $beforeLocked) '持锁冲突不得覆盖旧档案。'
    Assert-Check ([IO.File]::ReadAllText($temporaryPath) -eq 'owned by lock holder') '未取得锁的保存不得修改或清理临时文件。'
}
finally { $heldLock.Dispose() }

Invoke-CliCheck '05-lock-released' $saveDirectory 0
Assert-Accumulation 4 $baseline
Assert-Check (Test-Path -LiteralPath $lockPath -PathType Leaf) '锁文件应保留，锁的生命周期由文件句柄控制。'
Assert-Check (-not (Test-Path -LiteralPath $temporaryPath)) '释放锁后保存应清除临时文件。'

$beforeQuit = (Get-FileHash -LiteralPath $profilePath -Algorithm SHA256).Hash
Invoke-CliCheck '06-quit-existing' $saveDirectory 0 -Quit
Assert-Check ((Get-FileHash -LiteralPath $profilePath -Algorithm SHA256).Hash -eq $beforeQuit) '未完成的对局不得改动已有档案。'
$quitDirectory = Join-Path $runDirectory 'quit-without-save'
Invoke-CliCheck '07-quit-new' $quitDirectory 0 -Quit
Assert-Check (-not (Test-Path -LiteralPath $quitDirectory)) '提前退出不得创建存档目录。'

Write-Output "PASS：累计、陈旧临时文件恢复、锁冲突保护、释放锁续存、提前退出；日志与档案：$runDirectory"
