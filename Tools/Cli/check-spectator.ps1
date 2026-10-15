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
$cliDll = Join-Path $PSScriptRoot "bin/$Configuration/net8.0/Cli.dll"
if (-not (Test-Path -LiteralPath $cliDll -PathType Leaf)) { throw '请先构建 Tools/Cli。' }
$runDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ('obj/spectator-check/' + [Guid]::NewGuid().ToString('N'))))
if (-not $runDirectory.StartsWith($repositoryPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw '产物必须位于仓库内。' }
[void][IO.Directory]::CreateDirectory($runDirectory)
$utf8 = [Text.UTF8Encoding]::new($false)
$children = [Collections.Generic.List[object]]::new()
$checks = [Collections.Generic.List[string]]::new()
$hostTeam = 'bell,enka,shiori,lumi,hane,momo'
$guestTeam = 'kei,yuan,nyako,buck,kasumi,bee'
$failure = $null
$drop = $null

function Assert-Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "$Message 证据目录：$runDirectory" }
}

function Read-Log([string]$Path) {
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
    $reader = [IO.StreamReader]::new($stream, $utf8)
    try { return $reader.ReadToEnd() }
    finally { $reader.Dispose() }
}

function Start-Cli([string]$Name, [string[]]$Parameters) {
    $workingDirectory = Join-Path $runDirectory $Name
    [void][IO.Directory]::CreateDirectory($workingDirectory)
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = 'dotnet'
    $start.ArgumentList.Add($cliDll)
    foreach ($parameter in $Parameters) { $start.ArgumentList.Add($parameter) }
    $start.WorkingDirectory = $workingDirectory
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.Environment['TEMP'] = $runDirectory
    $start.Environment['TMP'] = $runDirectory
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    $child = [pscustomobject]@{
        Name = $Name; Process = $process; WorkingDirectory = $workingDirectory; Started = $false
        OutPath = (Join-Path $runDirectory "$Name.stdout.log"); ErrPath = (Join-Path $runDirectory "$Name.stderr.log")
        OutStream = $null; ErrStream = $null; OutTask = $null; ErrTask = $null; Result = $null
        DrivenPrompts = 0
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

function Wait-Output($Child, [string]$Pattern, [int]$TimeoutMs = 15000) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    while ($timer.ElapsedMilliseconds -lt $TimeoutMs) {
        $match = [regex]::Match((Read-Log $Child.OutPath), $Pattern)
        if ($match.Success) { return $match }
        Assert-Check (-not $Child.Process.HasExited) "$($Child.Name) 在预期输出前退出。"
        Start-Sleep -Milliseconds 20
    }
    throw "$($Child.Name) 等待输出超时。证据目录：$runDirectory"
}

function Wait-Cli($Child, [int]$ExpectedExit) {
    Assert-Check ($Child.Process.WaitForExit(30000)) "$($Child.Name) 30 秒内未退出。"
    Assert-Check ($Child.OutTask.Wait(5000) -and $Child.ErrTask.Wait(5000)) '输出复制未完成。'
    $Child.OutStream.Flush()
    $Child.ErrStream.Flush()
    $Child.Result = [pscustomobject]@{
        Exit = $Child.Process.ExitCode; Out = (Read-Log $Child.OutPath); Err = (Read-Log $Child.ErrPath)
    }
    Assert-Check ($Child.Result.Exit -eq $ExpectedExit) "$($Child.Name) 应退出 $ExpectedExit，实际 $($Child.Result.Exit)。"
    return $Child.Result
}

function Drive-Host($Child, [int]$UntilTurn = 0) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    while (-not $Child.Process.HasExited -and $timer.ElapsedMilliseconds -lt 30000) {
        $text = Read-Log $Child.OutPath
        $prompts = [regex]::Matches($text, '(选择行动|请选择补位号位)（q 退出）> ')
        if ($prompts.Count -gt $Child.DrivenPrompts) {
            $turns = [regex]::Matches($text, '===== 第 ([0-9]+) 回合 =====')
            if ($UntilTurn -gt 0 -and $turns.Count -gt 0 -and [int]$turns[$turns.Count - 1].Groups[1].Value -ge $UntilTurn) { return }
            $prompt = $prompts[$prompts.Count - 1]
            $start = if ($prompt.Groups[1].Value -eq '请选择补位号位') { $text.LastIndexOf('可补位队友：') } else { $text.LastIndexOf('技能：') }
            Assert-Check ($start -ge 0) '输入提示前没有可用菜单。'
            $menu = $text.Substring($start, $prompt.Index - $start)
            $choices = @([regex]::Matches($menu, '(?m)^  \[([0-9]+)\] ([^\r\n]*)') | Where-Object { -not $_.Groups[2].Value.Contains('不可用') })
            Assert-Check ($choices.Count -gt 0) '当前菜单没有合法选项。'
            $Child.DrivenPrompts = $prompts.Count
            $Child.Process.StandardInput.WriteLine($choices[0].Groups[1].Value)
            $Child.Process.StandardInput.Flush()
        }
        Start-Sleep -Milliseconds 10
    }
    Assert-Check ($Child.Process.HasExited) '驱动人工选择超过 30 秒。'
    Assert-Check ($UntilTurn -eq 0) '对局在到达预期中盘之前结束。'
}

function Final-Hash([string]$Text) {
    $matches = [regex]::Matches($Text, '(?m)^最终状态哈希：([0-9a-f]{16})\r?$')
    Assert-Check ($matches.Count -eq 1) '完成进程没有且仅有一个最终哈希。'
    return $matches[0].Groups[1].Value
}

function Assert-NoSave($Child) {
    Assert-Check (-not (Test-Path -LiteralPath (Join-Path $Child.WorkingDirectory 'saves'))) '观战或参数检查创建了默认存档目录。'
    Assert-Check (@(Get-ChildItem -LiteralPath $Child.WorkingDirectory -File -Recurse -Force).Count -eq 0) '只读进程写入了工作目录文件。'
}

try {
    $packDirectory = Join-Path $runDirectory 'packs'
    $changedDirectory = Join-Path $runDirectory 'changed-packs'
    [void][IO.Directory]::CreateDirectory($packDirectory)
    [void][IO.Directory]::CreateDirectory($changedDirectory)
    $pack = [IO.File]::ReadAllText((Join-Path (Split-Path $cliDll) 'Content/Packs/original.json')) | ConvertFrom-Json
    $pack.id = 'spectator_check'
    $pack.name = '观战验证包'
    [IO.File]::WriteAllText((Join-Path $packDirectory 'custom.json'), ($pack | ConvertTo-Json -Depth 64), $utf8)
    $pack.streamers[0].blurb = '观战指纹不一致验证。'
    [IO.File]::WriteAllText((Join-Path $changedDirectory 'custom.json'), ($pack | ConvertTo-Json -Depth 64), $utf8)
    $content = @('--pack', 'spectator_check', '--pack-dir', $packDirectory)
    $hostChild = Start-Cli 'host' ($content + @('--host', '0', '--spectate-port', '0', '--seed', '7', '--team', $hostTeam))
    $spectatePort = [int](Wait-Output $hostChild '(?m)^观战端口：([0-9]+)\r?$').Groups[1].Value
    $playPort = [int](Wait-Output $hostChild '(?m)^监听端口：([0-9]+)\r?$').Groups[1].Value
    Assert-Check ($spectatePort -gt 0 -and $playPort -gt 0 -and $spectatePort -ne $playPort) '未分配独立的有效监听端口。'
    $address = "127.0.0.1:$spectatePort"
    $early = Start-Cli 'early-viewer' ($content + @('--watch', $address, '--hash'))
    $null = Wait-Output $early '已连接观战'
    $null = Wait-Output $hostChild '观众已连接（累计 1 位）'
    Assert-Check (-not (Read-Log $early.OutPath).Contains('观战开始')) '选手尚未连接时误发了开局。'
    $wrongPack = Start-Cli 'wrong-pack-viewer' @('--watch', $address, '--pack', 'original')
    $wrongHash = Start-Cli 'wrong-hash-viewer' @('--watch', $address, '--pack', 'spectator_check', '--pack-dir', $changedDirectory)
    $null = Wait-Output $wrongPack '已连接观战'
    $null = Wait-Output $wrongHash '已连接观战'
    $guest = Start-Cli 'guest' ($content + @('--join', "127.0.0.1:$playPort", '--team', $guestTeam, '--auto'))
    $null = Wait-Output $hostChild '选择行动（q 退出）> '
    $null = Wait-Output $early '观战开始'
    $badPack = Wait-Cli $wrongPack 1
    $badHash = Wait-Cli $wrongHash 1
    Assert-Check ($badPack.Err.Contains('内容包') -and $badHash.Err.Contains('内容指纹不一致')) '内容包或指纹不匹配未清楚报错。'
    $checks.Add('独立动态端口；观众先到不丢；内容包和内容指纹不一致分别拒绝')

    Drive-Host $hostChild 5
    $late = Start-Cli 'late-viewer' ($content + @('--watch', $address, '--hash'))
    $null = Wait-Output $late '观战开始'
    $null = Wait-Output $late '观战状态哈希'
    $hostTurns = [regex]::Matches((Read-Log $hostChild.OutPath), '===== 第 ([0-9]+) 回合 =====')
    $currentTurn = [int]$hostTurns[$hostTurns.Count - 1].Groups[1].Value
    foreach ($viewer in @($early, $late)) {
        $turns = [regex]::Matches((Read-Log $viewer.OutPath), '观战状态哈希（第 ([0-9]+) 回合）')
        Assert-Check ($turns.Count -gt 0 -and [int]$turns[$turns.Count - 1].Groups[1].Value -le [Math]::Max(1, $currentTurn - 2)) '观众看到了两回合延迟范围内的局面。'
    }
    $drop = [Net.Sockets.TcpClient]::new()
    $drop.Connect('127.0.0.1', $spectatePort)
    $null = Wait-Output $hostChild '观众已连接（累计 5 位）'
    $drop.Dispose()
    $drop = $null
    Drive-Host $hostChild
    $hostResult = Wait-Cli $hostChild 0
    $guestResult = Wait-Cli $guest 0
    $earlyResult = Wait-Cli $early 0
    $lateResult = Wait-Cli $late 0
    $hash = Final-Hash $hostResult.Out
    foreach ($result in @($guestResult, $earlyResult, $lateResult)) {
        Assert-Check ((Final-Hash $result.Out) -ceq $hash) '选手与观众的最终哈希不一致。'
        Assert-Check ($result.Err.Length -eq 0) '正常连接出现错误输出。'
    }
    Assert-Check ($earlyResult.Out.Contains('观战结束：') -and $lateResult.Out.Contains('观战结束：')) '观众未收到结束标记后正常退出。'
    foreach ($viewer in @($early, $late, $wrongPack, $wrongHash)) { Assert-NoSave $viewer }
    foreach ($player in @($hostChild, $guest)) {
        Assert-Check (Test-Path -LiteralPath (Join-Path $player.WorkingDirectory 'saves/profile.json')) '正常选手没有保存个人档案。'
    }
    $checks.Add('中盘加入与持续输入等待接收；固定两回合延迟；观众掉线不影响选手；多观众终局一致且零存档')

    $interruptedHost = Start-Cli 'interrupted-host' @('--host', '0', '--spectate-port', '0')
    $interruptedPort = [int](Wait-Output $interruptedHost '(?m)^观战端口：([0-9]+)\r?$').Groups[1].Value
    $interruptedViewer = Start-Cli 'interrupted-viewer' @('--watch', "127.0.0.1:$interruptedPort")
    $null = Wait-Output $interruptedViewer '已连接观战'
    $null = Wait-Output $interruptedHost '观众已连接'
    $interruptedHost.Process.Kill($true)
    $interrupted = Wait-Cli $interruptedViewer 1
    Assert-Check ($interrupted.Err.Contains('断开') -and -not $interrupted.Out.Contains('观战结束：')) '未完成的观战断线被误报成功。'
    Assert-NoSave $interruptedViewer
    $checks.Add('结束标记前房主断开：观众 exit 1、不伪造终局、不写存档')

    $cases = @(
        @{ Name = 'watch-host'; Extra = @('--host','0') },
        @{ Name = 'watch-join'; Extra = @('--join','127.0.0.1:1') },
        @{ Name = 'watch-port'; Extra = @('--spectate-port','0') },
        @{ Name = 'watch-replay'; Extra = @('--replay','unused.json') },
        @{ Name = 'watch-ban'; Extra = @('--ban') },
        @{ Name = 'watch-lead'; Extra = @('--lead') },
        @{ Name = 'watch-auto'; Extra = @('--auto') },
        @{ Name = 'watch-team'; Extra = @('--team',$hostTeam) },
        @{ Name = 'watch-seed'; Extra = @('--seed','1') },
        @{ Name = 'watch-level'; Extra = @('--level','normal') },
        @{ Name = 'watch-report'; Extra = @('--report','unused.md') },
        @{ Name = 'watch-save'; Extra = @('--save-dir','saves') },
        @{ Name = 'watch-duplicate'; Extra = @('--watch','127.0.0.1:1') }
    )
    foreach ($case in $cases) {
        $child = Start-Cli $case.Name (@('--watch','127.0.0.1:1') + $case.Extra)
        $result = Wait-Cli $child 1
        Assert-Check ($result.Err.Contains('--watch') -and -not $result.Out.Contains('只读观战')) '非法观战组合未在连接前拒绝。'
        Assert-NoSave $child
    }
    foreach ($case in @(
        @{ Name='port-without-host'; Args=@('--spectate-port','0') },
        @{ Name='port-duplicate'; Args=@('--host','0','--spectate-port','0','--spectate-port','0') },
        @{ Name='port-same'; Args=@('--host','32123','--spectate-port','32123') },
        @{ Name='port-invalid'; Args=@('--host','0','--spectate-port','65536') },
        @{ Name='watch-invalid'; Args=@('--watch','127.0.0.1:0') }
    )) {
        $child = Start-Cli $case.Name $case.Args
        $result = Wait-Cli $child 1
        Assert-Check ($result.Err.Contains('--') -and -not $result.Out.Contains('端口：')) '非法端口参数未在监听前拒绝。'
        Assert-NoSave $child
    }
    $listChild = Start-Cli 'watch-list-packs' @('--watch','127.0.0.1:1','--list-packs')
    $list = Wait-Cli $listChild 0
    Assert-Check ($list.Out.Contains('original') -and -not $list.Out.Contains('只读观战')) '列包模式意外开始观战。'
    Assert-NoSave $listChild
    $checks.Add('18组非法组合/重复/端口检查；list-packs只列包；显式默认选项也拒绝')
    [IO.File]::WriteAllText((Join-Path $runDirectory 'report.json'), (@{ Status='PASS'; FinalHash=$hash; Checks=$checks.ToArray() } | ConvertTo-Json -Depth 5), $utf8)
    Write-Output "PASS：$($checks.Count) 组观战与 CLI 检查；证据：$runDirectory"
}
catch {
    $failure = $_
}
finally {
    if ($null -ne $drop) { $drop.Dispose() }
    foreach ($child in $children) {
        if ($child.Started -and -not $child.Process.HasExited) { $child.Process.Kill($true); [void]$child.Process.WaitForExit(5000) }
        if ($null -ne $child.OutTask) { [void]$child.OutTask.Wait(5000) }
        if ($null -ne $child.ErrTask) { [void]$child.ErrTask.Wait(5000) }
        if ($null -ne $child.OutStream) { $child.OutStream.Dispose() }
        if ($null -ne $child.ErrStream) { $child.ErrStream.Dispose() }
        $child.Process.Dispose()
    }
}
if ($null -ne $failure) { throw $failure }
