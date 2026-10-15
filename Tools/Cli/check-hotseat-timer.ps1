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
    throw '请在本仓库内运行此脚本。'
}
$cliDirectory = Join-Path $repository 'Tools/Cli'
$cliDll = Join-Path $cliDirectory "bin/$Configuration/net8.0/Cli.dll"
if (-not (Test-Path -LiteralPath $cliDll -PathType Leaf)) {
    throw "请先运行 dotnet build Tools/Cli -c $Configuration。"
}
$runDirectory = [IO.Path]::GetFullPath((Join-Path $cliDirectory ('obj/hotseat-timer-check/' + [Guid]::NewGuid().ToString('N'))))
if (-not $runDirectory.StartsWith($repositoryPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw '验收产物目录必须位于本仓库内。'
}
[void][IO.Directory]::CreateDirectory($runDirectory)
$testTemp = Join-Path $runDirectory 'test-temp'
[void][IO.Directory]::CreateDirectory($testTemp)
$utf8 = [Text.UTF8Encoding]::new($false)
$children = [Collections.Generic.List[object]]::new()
$checks = [Collections.Generic.List[string]]::new()

function Assert-Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "$Message 证据目录：$runDirectory" }
}

function Read-Log([string]$Path) {
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
    $reader = [IO.StreamReader]::new($stream, $utf8)
    try { return $reader.ReadToEnd() }
    finally { $reader.Dispose() }
}

function Start-Cli([string]$Name, [string[]]$Parameters, [string]$InputText = '') {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = 'dotnet'
    $start.ArgumentList.Add($cliDll)
    foreach ($parameter in $Parameters) { $start.ArgumentList.Add($parameter) }
    $start.WorkingDirectory = $runDirectory
    $start.Environment['TEMP'] = $testTemp
    $start.Environment['TMP'] = $testTemp
    $start.Environment['DOTNET_GENERATE_ASPNET_CERTIFICATE'] = 'false'
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = $utf8
    $start.StandardErrorEncoding = $utf8
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    $child = [pscustomobject]@{
        Name = $Name; Parameters = $Parameters; Process = $process; Started = $false
        OutPath = (Join-Path $runDirectory "$Name.stdout.log")
        ErrPath = (Join-Path $runDirectory "$Name.stderr.log")
        OutStream = $null; ErrStream = $null; OutTask = $null; ErrTask = $null
    }
    $children.Add($child)
    $child.OutStream = [IO.FileStream]::new($child.OutPath, [IO.FileMode]::CreateNew,
        [IO.FileAccess]::Write, [IO.FileShare]::ReadWrite, 1, [IO.FileOptions]::Asynchronous)
    $child.ErrStream = [IO.FileStream]::new($child.ErrPath, [IO.FileMode]::CreateNew,
        [IO.FileAccess]::Write, [IO.FileShare]::ReadWrite, 1, [IO.FileOptions]::Asynchronous)
    Assert-Check ($process.Start()) "$Name 无法启动。"
    $child.Started = $true
    $child.OutTask = $process.StandardOutput.BaseStream.CopyToAsync($child.OutStream)
    $child.ErrTask = $process.StandardError.BaseStream.CopyToAsync($child.ErrStream)
    return $child
}

function Wait-Cli($Child, [int]$ExpectedExit = 0) {
    Assert-Check ($Child.Process.WaitForExit(30000)) "$($Child.Name) 超过 30 秒未退出。"
    Assert-Check ($Child.OutTask.Wait(5000) -and $Child.ErrTask.Wait(5000)) "$($Child.Name) 输出未结束。"
    $Child.OutStream.Flush()
    $Child.ErrStream.Flush()
    $result = [pscustomobject]@{ Out = (Read-Log $Child.OutPath); Err = (Read-Log $Child.ErrPath); Exit = $Child.Process.ExitCode }
    Assert-Check ($result.Exit -eq $ExpectedExit) "$($Child.Name) 应退出 $ExpectedExit，实际 $($result.Exit)。"
    return $result
}

function Wait-Output($Child, [string]$Pattern, [int]$TimeoutMs = 15000) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    while ($timer.ElapsedMilliseconds -lt $TimeoutMs) {
        $found = [regex]::Match((Read-Log $Child.OutPath), $Pattern)
        if ($found.Success) { return $found }
        Assert-Check (-not $Child.Process.HasExited) "$($Child.Name) 提前退出，缺少预期输出。"
        Start-Sleep -Milliseconds 20
    }
    throw "$($Child.Name) 等待输出超时。证据目录：$runDirectory"
}
function Run-Cli([string]$Name, [string[]]$Parameters, [string]$InputText = '', [int]$Exit = 0) {
    $child = Start-Cli $Name $Parameters
    if ($InputText.Length -gt 0) { $child.Process.StandardInput.Write($InputText) }
    $child.Process.StandardInput.Close()
    return Wait-Cli $child $Exit
}
function Save-Directory([string]$Name) { return Join-Path $runDirectory ($Name + '-saves') }
function Assert-NoSave([string]$Name) {
    Assert-Check (-not (Test-Path -LiteralPath (Save-Directory $Name))) "$Name 不应保存未完成对局。"
}
function Read-Record([string]$Name) {
    $files = @(Get-ChildItem -LiteralPath (Join-Path (Save-Directory $Name) 'replays') -File -Filter '*.json')
    Assert-Check ($files.Count -eq 1) "$Name 没有恰好一份回放。"
    return [StreamDuel.Core.BattleRecord]::FromJson([IO.File]::ReadAllText($files[0].FullName))
}

$failure = $null
try {
    $binaryDirectory = Split-Path $cliDll
    $coreDll = Join-Path $binaryDirectory 'StreamDuel.Core.dll'
    $assembly = [Reflection.Assembly]::LoadFrom($coreDll)
    Assert-Check ([IO.Path]::GetFullPath($assembly.Location) -eq [IO.Path]::GetFullPath($coreDll)) '请在新的 PowerShell 进程中运行。'
    $db = [StreamDuel.Core.ContentDb]::FromJson(
        [IO.File]::ReadAllText((Join-Path $binaryDirectory 'Content/StreamDuelRules.json')),
        [IO.File]::ReadAllText((Join-Path $binaryDirectory 'Content/Packs/original.json')))
    [string[]]$teamA = @('enka', 'bell', 'shiori', 'lumi', 'hane', 'momo')
    [string[]]$teamB = @('kei', 'yuan', 'nyako', 'buck', 'kasumi', 'bee')
    $common = @('--hotseat', '--team', ($teamA -join ','), '--team-b', ($teamB -join ','), '--name-a', '红方', '--name-b', '蓝方', '--seed', '17')

    $untimed = Run-Cli 'untimed' ($common + @('--save-dir', (Save-Directory 'untimed'))) "1`n`n1`n/surrender`n"
    $zero = Run-Cli 'zero' ($common + @('--turn-limit', '0', '--save-dir', (Save-Directory 'zero'))) "1`n`n1`n/surrender`n"
    $manual = Run-Cli 'manual-timed' ($common + @('--turn-limit', '10', '--save-dir', (Save-Directory 'manual-timed'))) "1`n`n1`n/surrender`n"
    $expectedRecord = (Read-Record 'untimed').ToJson()
    Assert-Check ((Read-Record 'zero').ToJson() -ceq $expectedRecord -and
        (Read-Record 'manual-timed').ToJson() -ceq $expectedRecord) '默认不限时、显式0和正常限时输入改变了相同选择的录像。'
    foreach ($result in @($untimed, $zero, $manual)) {
        Assert-Check ($result.Err.Length -eq 0 -and $result.Out.Contains('对局结束：') -and
            -not $result.Out.Contains('超时，已自动选择')) '正常手动同屏出现错误或误触发超时。'
        Assert-Check ([regex]::Matches($result.Out, '\x1b\[2J\x1b\[H\x1b\[3J').Count -eq 1) '正常交接没有清理屏幕及滚动记录。'
        Assert-Check (-not [regex]::IsMatch($result.Out, '(?m)^1\r?$')) '私密数字输入被回显。'
    }
    $checks.Add('默认不限时/显式0/正常限时选招录像逐字相同，既有交接清屏与私密输入保留')

    # 三个进程分别验证有限时、零限时和非Choose停表；全部为本脚本启动且无窗口。
    $idleZero = Start-Cli 'idle-zero' ($common + @('--turn-limit', '0', '--save-dir', (Save-Directory 'idle-zero')))
    $paused = Start-Cli 'paused' ($common + @('--turn-limit', '10', '--ban', '--lead', '--save-dir', (Save-Directory 'paused')))
    $timed = Start-Cli 'timed' ($common + @('--turn-limit', '10', '--save-dir', (Save-Directory 'timed')))
    [void](Wait-Output $idleZero '选择行动')
    [void](Wait-Output $paused '禁用对手')
    [void](Wait-Output $timed '选择行动')
    $elapsed = [Diagnostics.Stopwatch]::StartNew()
    $timed.Process.StandardInput.Write('5')
    [void](Wait-Output $timed '红方：超时，已自动选择')
    Assert-Check ($elapsed.Elapsed.TotalSeconds -ge 8.5) '10秒限时过早触发。'
    [void](Wait-Output $timed '重新确认 > ')
    # 超时前开始的私密行现在才完成，后面还积压一个数字和空回车，均不能跨阶段。
    $timed.Process.StandardInput.Write("`n1`n`n")
    [void](Wait-Output $timed '已忽略过期输入')
    Start-Sleep -Milliseconds 11000
    $beforeContinue = Read-Log $timed.OutPath
    Assert-Check ([regex]::Matches($beforeContinue, '超时，已自动选择').Count -eq 1 -and
        -not $beforeContinue.Contains('蓝方 操作') -and $beforeContinue.Contains('请把电脑交给「蓝方」')) '交接期间继续计时、旧输入自动确认，或未及时遮挡并交接。'
    $privateClearAt = $beforeContinue.LastIndexOf([string][char]27 + '[2J')
    Assert-Check ($privateClearAt -gt $beforeContinue.IndexOf('超时，已自动选择') -and
        $privateClearAt -lt $beforeContinue.IndexOf('超时前的输入已失效') -and
        -not $beforeContinue.Substring($privateClearAt).Contains('超时，已自动选择')) '未先清屏再进行同步，或同步提示泄漏了上一位默认技能。'
    Assert-Check (-not [regex]::IsMatch($beforeContinue, '(?m)^[15]\r?$')) '过期私密输入被回显。'
    foreach ($child in @($idleZero, $paused)) {
        Assert-Check (-not $child.Process.HasExited -and -not (Read-Log $child.OutPath).Contains('超时，已自动选择')) '零限时或禁用阶段错误自动行动。'
    }
    $idleZero.Process.StandardInput.Close()
    $idleResult = Wait-Cli $idleZero
    Assert-Check ($idleResult.Out.Contains('未完成') -and $idleResult.Err.Length -eq 0) '不限时等待中的EOF没有退出。'
    Assert-NoSave 'idle-zero'

    $timed.Process.StandardInput.WriteLine('继续')
    [void](Wait-Output $timed '请把电脑交给「蓝方」')
    [void](Wait-Output $timed '接手后按回车确认')
    $afterClear = Read-Log $timed.OutPath
    $clearAt = $afterClear.LastIndexOf([string][char]27 + '[2J')
    Assert-Check ($clearAt -gt $afterClear.IndexOf('超时，已自动选择') -and
        -not $afterClear.Substring($clearAt).Contains('超时，已自动选择')) '交接清屏后泄漏了上一位默认技能。'
    $timed.Process.StandardInput.WriteLine('')
    [void](Wait-Output $timed '第 1 回合 · 蓝方 操作')
    $timed.Process.StandardInput.WriteLine('1')
    [void](Wait-Output $timed '第 2 回合 · 蓝方 操作')
    $timed.Process.StandardInput.WriteLine('/surrender')
    $timed.Process.StandardInput.Close()
    $timedResult = Wait-Cli $timed
    Assert-Check ($timedResult.Err.Length -eq 0 -and [regex]::Matches($timedResult.Out, '超时，已自动选择').Count -eq 1) '限时对局没有正常完成或发生额外超时。'
    $timedRecord = Read-Record 'timed'
    Assert-Check ($timedRecord.ToJson() -ceq $expectedRecord) '超时默认技能或迟到输入改变了预期录像。'
    $replay = [StreamDuel.Core.Replay]::Run($db, $timedRecord)
    $original = [StreamDuel.Core.Replay]::Run($db, (Read-Record 'untimed'))
    Assert-Check ($replay.StateHash() -eq $original.StateHash()) '超时对局与正常选择的回放状态不同。'
    $checks.Add('真实10秒无完整输入超时、默认行动录像与手动相同；11秒交接停表；迟到数字/空回车失效；先清屏再同步，交接期间无默认技能信息')

    # 禁用已停表超过20秒；完成禁用后继续验证首发也不计时。
    $paused.Process.StandardInput.Write("1`n`n1`n")
    [void](Wait-Output $paused '选择首发')
    Start-Sleep -Milliseconds 11000
    Assert-Check (-not $paused.Process.HasExited -and -not (Read-Log $paused.OutPath).Contains('超时，已自动选择')) '首发阶段错误自动行动。'
    $paused.Process.StandardInput.Close()
    $pausedResult = Wait-Cli $paused
    Assert-Check ($pausedResult.Err.Length -eq 0 -and $pausedResult.Out.Contains('未完成')) '暂停阶段EOF退出错误。'
    Assert-NoSave 'paused'
    $eof = Run-Cli 'timed-eof' ($common + @('--turn-limit', '10', '--save-dir', (Save-Directory 'timed-eof')))
    Assert-Check ($eof.Err.Length -eq 0 -and $eof.Out.Contains('未完成') -and
        -not $eof.Out.Contains('超时，已自动选择')) '限时等待中EOF没有立即退出。'
    Assert-NoSave 'timed-eof'
    $checks.Add('不限时真实等待、禁用/首发暂停、限时与暂停阶段EOF退出均不保存')

    foreach ($value in @('20', '30', '60')) {
        $name = 'allowed-' + $value
        $result = Run-Cli $name ($common + @('--turn-limit', $value, '--save-dir', (Save-Directory $name))) "q`n"
        Assert-Check ($result.Err.Length -eq 0 -and $result.Out.Contains('每次选招限时 ' + $value + ' 秒')) '合法配置未生效。'
        Assert-NoSave $name
    }
    $invalid = @(
        @('--turn-limit', '0'), @('--hotseat', '--turn-limit'),
        @('--hotseat', '--turn-limit', '10', '--turn-limit', '10'),
        @('--hotseat', '--turn-limit', '10', '--timer', '10,30'),
        @('--hotseat', '--turn-limit', '10', '--auto'),
        @('--replays', '--turn-limit', '10'), @('--host', '0', '--turn-limit', '10')
    )
    foreach ($value in @('-1', '1', '15', '10.0', '+10', '010', ' 10', 'NaN', 'Infinity', '2147483648')) {
        $invalid += ,@('--hotseat', '--turn-limit', $value)
    }
    for ($i = 0; $i -lt $invalid.Count; $i++) {
        $name = 'invalid-' + $i
        $result = Run-Cli $name ($invalid[$i] + @('--save-dir', (Save-Directory $name))) '' 1
        Assert-Check ($result.Err -match '[\u4e00-\u9fff]' -and -not $result.Out.Contains('对局结束：')) "$name 缺少中文拒绝或错误开始对局。"
        Assert-NoSave $name
    }
    $help = Run-Cli 'help' @('--help')
    Assert-Check ($help.Out.Contains('--turn-limit') -and $help.Out.Contains('0（默认不限时）、10、20、30、60')) '帮助遗漏同屏计时配置。'
    Assert-Check (-not (Test-Path -LiteralPath (Join-Path $runDirectory 'saves'))) '验收写入了默认存档目录。'
    $checks.Add('所有合法配置、仅同屏约束、缺值/重复/非法值/互斥参数与帮助')
}
catch { $failure = $_.Exception.GetBaseException().Message }
finally {
    foreach ($child in $children) {
        if ($child.Started -and -not $child.Process.HasExited) { $child.Process.Kill($true); [void]$child.Process.WaitForExit(5000) }
        foreach ($task in @($child.OutTask, $child.ErrTask)) { if ($null -ne $task) { [void]$task.Wait(5000) } }
        foreach ($stream in @($child.OutStream, $child.ErrStream)) { if ($null -ne $stream) { $stream.Dispose() } }
        $child.Process.Dispose()
    }
    $summary = [ordered]@{ status = $(if ($null -eq $failure) { 'PASS' } else { 'FAIL' }); failure = $failure; checks = $checks.ToArray(); directory = $runDirectory }
    [IO.File]::WriteAllText((Join-Path $runDirectory 'result.json'), ($summary | ConvertTo-Json -Depth 6), $utf8)
}
if ($null -ne $failure) { throw "$failure 证据目录：$runDirectory" }
Write-Output "PASS：$($checks.Count) 组同屏限时验收；证据目录：$runDirectory"
