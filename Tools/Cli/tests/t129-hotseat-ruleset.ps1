#requires -Version 7.0
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug',
    [string]$BaselineDll
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$prefix = $repository.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
function Inside-Repository([string]$Path) {
    $resolved = [IO.Path]::GetFullPath($Path)
    if (-not $resolved.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw "路径必须位于本仓库：$Path" }
    return $resolved
}
$cliDll = Inside-Repository (Join-Path $repository "Tools/Cli/bin/$Configuration/net8.0/Cli.dll")
if (-not $BaselineDll) { $BaselineDll = Join-Path $repository 'Engine.Tests/obj/T129-baseline/Tools/Cli/bin/Debug/net8.0/Cli.dll' }
$BaselineDll = Inside-Repository $BaselineDll
foreach ($dll in @($cliDll, $BaselineDll)) {
    if (-not (Test-Path -LiteralPath $dll -PathType Leaf)) { throw "请先构建当前 CLI 和独立旧基线 CLI：$dll" }
}
$run = Inside-Repository (Join-Path $repository ('Tools/Cli/obj/t129-hotseat-ruleset/' + [Guid]::NewGuid().ToString('N')))
$temp = Inside-Repository (Join-Path $run 'temp')
$cliHome = Inside-Repository (Join-Path $run 'dotnet-home')
foreach ($path in @($run, $temp, $cliHome)) { [void][IO.Directory]::CreateDirectory($path) }
$utf8 = [Text.UTF8Encoding]::new($false)
$checks = [Collections.Generic.List[string]]::new()

function Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "$Message；证据：$run" }
    $checks.Add($Message)
}

function Read-Available($State) {
    if ($State.Closed) { return }
    if ($null -eq $State.Pending) { $State.Pending = $State.Reader.ReadAsync($State.Buffer, 0, $State.Buffer.Length) }
    if (-not $State.Pending.IsCompleted) { return }
    $count = $State.Pending.GetAwaiter().GetResult()
    $State.Pending = $null
    if ($count -eq 0) { $State.Closed = $true }
    else { [void]$State.Text.Append($State.Buffer, 0, $count) }
}

function Run-Cli([string]$Name, [string[]]$Arguments, [string]$InputText = '', [int]$Exit = 0,
    [string]$Dll = $cliDll, [object[]]$Steps = @(), [string]$Profile = '') {
    $directory = Inside-Repository (Join-Path $run $Name)
    [void][IO.Directory]::CreateDirectory($directory)
    $saves = Join-Path $directory 'saves'
    if ($Profile) {
        [void][IO.Directory]::CreateDirectory($saves)
        [IO.File]::WriteAllText((Join-Path $saves 'profile.json'), $Profile, $utf8)
    }
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = 'dotnet'
    $start.ArgumentList.Add($Dll)
    foreach ($argument in ($Arguments + @('--save-dir', 'saves'))) { $start.ArgumentList.Add($argument) }
    $start.WorkingDirectory = $directory
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardInputEncoding = $utf8
    $start.StandardOutputEncoding = $utf8
    $start.StandardErrorEncoding = $utf8
    $start.Environment['TEMP'] = $temp
    $start.Environment['TMP'] = $temp
    $start.Environment['DOTNET_CLI_HOME'] = $cliHome
    $start.Environment['DOTNET_GENERATE_ASPNET_CERTIFICATE'] = 'false'
    $start.Environment['DOTNET_CLI_TELEMETRY_OPTOUT'] = '1'
    $start.Environment['DOTNET_SKIP_FIRST_TIME_EXPERIENCE'] = '1'
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    $state = $null
    $started = $false
    try {
        [void]$process.Start()
        $started = $true
        $stderr = $process.StandardError.ReadToEndAsync()
        $state = [pscustomobject]@{
            Reader = $process.StandardOutput; Buffer = [char[]]::new(4096)
            Text = [Text.StringBuilder]::new(); Pending = $null; Closed = $false
        }
        $elapsed = [Diagnostics.Stopwatch]::StartNew()
        if ($InputText.Length -gt 0) { $process.StandardInput.Write($InputText); $process.StandardInput.Flush() }
        $offset = 0
        foreach ($step in $Steps) {
            while ($true) {
                Read-Available $state
                $found = $state.Text.ToString().IndexOf($step.Expect, $offset, [StringComparison]::Ordinal)
                if ($found -ge 0) { $offset = $found + $step.Expect.Length; break }
                if ($state.Closed -or $elapsed.Elapsed.TotalSeconds -gt 90) { throw "$Name 没有等到：$($step.Expect)" }
                Start-Sleep -Milliseconds 10
            }
            if ($step.ContainsKey('DelayMs')) {
                $until = $elapsed.ElapsedMilliseconds + $step.DelayMs
                while ($elapsed.ElapsedMilliseconds -lt $until) { Read-Available $state; Start-Sleep -Milliseconds 10 }
            }
            $process.StandardInput.Write($step.Input)
            $process.StandardInput.Flush()
        }
        $process.StandardInput.Close()
        while (-not $process.HasExited -or -not $state.Closed) {
            Read-Available $state
            if ($elapsed.Elapsed.TotalSeconds -gt 90) { throw "$Name 运行超过 90 秒。" }
            Start-Sleep -Milliseconds 10
        }
        $output = $state.Text.ToString()
        $errorText = $stderr.GetAwaiter().GetResult()
        [IO.File]::WriteAllText((Join-Path $directory 'stdout.log'), $output, $utf8)
        [IO.File]::WriteAllText((Join-Path $directory 'stderr.log'), $errorText, $utf8)
        [IO.File]::WriteAllText((Join-Path $directory 'arguments.json'), (ConvertTo-Json -InputObject $Arguments), $utf8)
        Check ($process.ExitCode -eq $Exit) "$Name 退出码 $Exit"
        return [pscustomobject]@{ Out = $output; Err = $errorText; Directory = $directory; Saves = $saves }
    }
    finally {
        if ($null -ne $state) { [IO.File]::WriteAllText((Join-Path $directory 'stdout.log'), $state.Text.ToString(), $utf8) }
        if ($started -and -not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
        $process.Dispose()
    }
}

function Records($Result) {
    $path = Join-Path $Result.Saves 'replays'
    if (-not (Test-Path -LiteralPath $path)) { return @() }
    return @(Get-ChildItem -LiteralPath $path -File -Filter '*.json' | Where-Object { -not $_.Name.StartsWith('.') } |
        ForEach-Object { $json = [IO.File]::ReadAllText($_.FullName); [pscustomobject]@{ Json = $json; Record = [StreamDuel.Core.BattleRecord]::FromJson($json) } } |
        Sort-Object { $_.Record.Seed })
}

function Plan([string]$Ruleset, [bool]$Ban = $false, [bool]$Lead = $false, [bool]$OneGame = $false) {
    if ($Ruleset) {
        $rule = @([StreamDuel.Core.Rulesets]::All | Where-Object Id -eq $Ruleset)[0]
        $session = [StreamDuel.Core.HotseatSession]::new($db, $teamA, $teamB, $nameA, $nameB, $rule, [uint32]129)
    }
    else {
        $option = [StreamDuel.Core.BattleOptions]::new()
        $option.Seed = 129; $option.BanOne = $Ban; $option.LeadPick = $Lead
        $session = [StreamDuel.Core.HotseatSession]::new($db, $teamA, $teamB, $nameA, $nameB, $option)
    }
    $input = [Text.StringBuilder]::new()
    $expected = [Collections.Generic.List[string]]::new()
    $firstChoosePrefix = $null
    $firstGame = $null
    for ($guard = 0; $guard -lt 100; $guard++) {
        if ($session.Phase -eq [StreamDuel.Core.Phase]::Over) {
            $expected.Add($session.Record.ToJson())
            if ($null -eq $firstGame) { $firstGame = $input.ToString() }
            if (-not $Ruleset -or $session.MatchOver -or $OneGame) { break }
            $session.NextGame()
        }
        if ($session.NeedsHandover) { [void]$input.Append("`n"); $session.ConfirmHandover() }
        $side = $session.WhoseTurn
        $view = $session.ViewFor($side)
        if ($session.Phase -eq [StreamDuel.Core.Phase]::Choose -and $null -eq $firstChoosePrefix) { $firstChoosePrefix = $input.ToString() }
        $hasTurn = @($session.Record.Events | Where-Object { $_.Type -eq [StreamDuel.Core.BattleRecordEventType]::Turn }).Count -gt 0
        if ($hasTurn -and $side -eq 1) { [void]$input.Append("/surrender`n"); $session.Surrender(); continue }
        switch ($session.Phase.ToString()) {
            'Ban' { [void]$input.Append("1`n"); $session.SubmitBan(0) }
            'Lead' { [void]$input.Append("1`n"); $session.SubmitLead(0) }
            'Replace' { $target = $view.SwitchTargets[0]; [void]$input.Append(($target + 1).ToString() + "`n"); $session.SubmitReplace($target) }
            'Choose' {
                if ($view.ControlledReason -or $view.Struggle) { [void]$input.Append("0`n"); $session.Submit([StreamDuel.Core.BattleAction]::Pass()) }
                else { $skill = @($view.Skills | Where-Object Usable)[0]; [void]$input.Append(($skill.Index + 1).ToString() + "`n"); $session.Submit([StreamDuel.Core.BattleAction]::Skill($skill.Index)) }
            }
        }
    }
    Check ($expected.Count -gt 0) '夹具完成至少一局'
    return [pscustomobject]@{ Input = $input.ToString(); Expected = $expected.ToArray(); FirstGame = $firstGame; FirstChoosePrefix = $firstChoosePrefix }
}

$core = Join-Path (Split-Path $cliDll) 'StreamDuel.Core.dll'
[void][Reflection.Assembly]::LoadFrom($core)
Check ([IO.Path]::GetFullPath([StreamDuel.Core.HotseatSession].Assembly.Location) -eq [IO.Path]::GetFullPath($core)) '加载当前 CLI 的 Core'
$content = Join-Path (Split-Path $cliDll) 'Content'
$db = [StreamDuel.Core.ContentDb]::FromJson([IO.File]::ReadAllText((Join-Path $content 'StreamDuelRules.json')),
    [IO.File]::ReadAllText((Join-Path $content 'Packs/original.json')))
[string[]]$teamA = @('bell', 'enka', 'shiori', 'lumi', 'hane', 'momo')
[string[]]$teamB = @('kei', 'yuan', 'nyako', 'buck', 'kasumi', 'bee')
$nameA = '<b>红方</b>'; $nameB = '<i>蓝方</i>'
$common = @('--hotseat', '--pack', 'original', '--team', ($teamA -join ','), '--team-b', ($teamB -join ','),
    '--name-a', $nameA, '--name-b', $nameB, '--seed', '129')
$displayA = [StreamDuel.Core.TextInput]::Clean($nameA, [StreamDuel.Core.TextInputKind]::PlayerName).Text
$displayB = [StreamDuel.Core.TextInput]::Clean($nameB, [StreamDuel.Core.TextInputKind]::PlayerName).Text
$ratingFixture = '{"packs":{},"ratings":{"默认":{"rating":1601,"games":8},"另一赛制":{"rating":1499,"games":2}}}'

$rankedPlan = Plan 'ranked'
$ranked = Run-Cli 'ranked' ($common + @('--ruleset', 'ranked', '--report', 'match.md')) $rankedPlan.Input -Profile $ratingFixture
$records = @(Records $ranked)
Check ($ranked.Err.Length -eq 0 -and $records.Count -eq 2) 'ranked 两次 B 方投降使 A 方连胜，逐局保存两份录像'
Check ($ranked.Out.Contains("赛制结束：共 2 局；$displayA 2 : 0 $displayB；平局 0")) '整场比分使用双方清理后的昵称'
for ($i = 0; $i -lt 2; $i++) {
    Check ($records[$i].Record.Seed -eq 129 + $i) "第 $($i + 1) 局递增种子"
    Check ($records[$i].Json -ceq $rankedPlan.Expected[$i]) "第 $($i + 1) 局录像与独立 Core 会话逐字一致"
    Check ($records[$i].Record.BanOne -and $records[$i].Record.LeadPick -and $records[$i].Record.Teams[0].Count -eq 6) '每局保留禁用、首发与原始六人名单'
    Check (@($records[$i].Record.Events | Where-Object { $_.Type -eq [StreamDuel.Core.BattleRecordEventType]::Ban }).Count -eq 2) '双方禁用均进入已确认录像'
    Check (@($records[$i].Record.Events | Where-Object { $_.Type -eq [StreamDuel.Core.BattleRecordEventType]::Lead }).Count -eq 2) '双方首发均进入已确认录像'
}
$profile = [IO.File]::ReadAllText((Join-Path $ranked.Saves 'profile.json')) | ConvertFrom-Json
$initial = [StreamDuel.Core.Profile]::FromJson($ratingFixture).ToJson() | ConvertFrom-Json
Check (($profile.ratings | ConvertTo-Json -Depth 20 -Compress) -ceq ($initial.ratings | ConvertTo-Json -Depth 20 -Compress)) '同屏 ranked 不改变已有各赛制排位分'
Check ($profile.packs.original.wins -eq 2 -and $profile.packs.original.losses -eq 0) '普通档案按 A 方每局累计一次'
$report = [IO.File]::ReadAllText((Join-Path $ranked.Directory 'match.md'))
Check ([regex]::Matches($report, '(?m)^## 第 \d+ 局$').Count -eq 2 -and $report.Contains("比分：$displayA 2 : 0 $displayB")) '复用整场汇总并保留两局与双方昵称'
$index = [IO.File]::ReadAllText((Join-Path $ranked.Saves 'replay-library/index.json')) | ConvertFrom-Json
Check ($index.entries.Count -eq 2) '独立录像库同时保存每局索引'

$quit = Run-Cli 'between-games-quit' ($common + @('--ruleset', 'ranked', '--report', 'match.md')) ($rankedPlan.FirstGame + "q`n")
Check (@(Records $quit).Count -eq 1 -and $quit.Out.Contains('已完成 1 局')) '跨局交接退出保留第一局且不保存第二局'
Check (-not (Test-Path -LiteralPath (Join-Path $quit.Directory 'match.md'))) '未完成整场不导出汇总'
$initialQuit = Run-Cli 'initial-quit' ($common + @('--ruleset', 'ranked')) "q`n"
Check (-not (Test-Path -LiteralPath $initialQuit.Saves)) '首局提前退出不创建存档'

foreach ($kind in @('plain', 'ban', 'lead', 'ban-lead')) {
    $ban = $kind -in @('ban', 'ban-lead'); $lead = $kind -in @('lead', 'ban-lead')
    $plan = Plan '' $ban $lead
    $extra = @('--share', '--report', 'report.md')
    if ($ban) { $extra += '--ban' }; if ($lead) { $extra += '--lead' }
    $old = Run-Cli "legacy-$kind" ($common + $extra) $plan.Input -Dll $BaselineDll
    $new = Run-Cli "current-$kind" ($common + $extra) $plan.Input
    foreach ($file in @('stdout.log', 'stderr.log', 'report.md', 'saves/profile.json')) {
        Check ((Get-FileHash -LiteralPath (Join-Path $old.Directory $file)).Hash -ceq
            (Get-FileHash -LiteralPath (Join-Path $new.Directory $file)).Hash) "旧 $kind 的 $file 字节保持不变"
    }
    Check (@(Records $old)[0].Json -ceq @(Records $new)[0].Json) "旧 $kind 的录像 JSON 字节保持不变"
    # 录像库 id/date 按真实保存时间产生，比较承载的 BattleRecord，不要求两个运行共享时间戳。
    $newLibrary = @(Get-ChildItem -LiteralPath (Join-Path $new.Saves 'replay-library') -Filter '*.json' |
        Where-Object { $_.Name -ne 'index.json' -and -not $_.Name.StartsWith('.') })
    Check ($newLibrary.Count -eq 1 -and [StreamDuel.Core.BattleRecord]::FromJson([IO.File]::ReadAllText($newLibrary[0].FullName)).ToJson() -ceq $plan.Expected[0]) '旧模式录像库仍承载同一完整记录'
}

foreach ($ruleset in @('casual', 'chaos')) {
    $plan = Plan $ruleset
    $result = Run-Cli $ruleset ($common + @('--ruleset', $ruleset)) $plan.Input
    Check (@(Records $result).Count -eq 1 -and @(Records $result)[0].Json -ceq $plan.Expected[0]) "$ruleset 同屏正确应用单局赛制与修饰器"
}

$conflicts = @(
    @('--timer', '1,1'), @('--ban'), @('--lead'), @('--auto'), @('--level', 'normal'), @('--hash'),
    @('--host', '0'), @('--join', '127.0.0.1:1'), @('--replay', 'missing.json'), @('--replays'),
    @('--advice'), @('--tournament', 'missing.json'), @('--turn-limit', '1')
)
for ($i = 0; $i -lt $conflicts.Count; $i++) {
    foreach ($reverse in @($false, $true)) {
        $mode = @('--hotseat', '--ruleset', 'ranked')
        $argsForCase = if ($reverse) { @($conflicts[$i]) + $mode } else { $mode + @($conflicts[$i]) }
        $bad = Run-Cli "conflict-$i-$reverse" $argsForCase '' 1
        Check ($bad.Out.Length -eq 0 -and $bad.Err.StartsWith('错误：') -and -not (Test-Path -LiteralPath $bad.Saves)) '非法模式组合在显示及存档前拒绝'
    }
}
$unknown = Run-Cli 'unknown-ruleset' @('--hotseat', '--ruleset', 'missing') '' 1
Check ($unknown.Out.Length -eq 0 -and $unknown.Err.Contains('未知赛制')) '未知赛制明确拒绝'

foreach ($choice in @('1', '2')) {
    $private = Run-Cli "private-ban-$choice" ($common + @('--ruleset', 'ranked')) ($choice + "`nq`n")
    if ($choice -eq '1') { $firstPrivate = $private.Out } else { Check ($firstPrivate -ceq $private.Out) '禁用首选在交接确认前不泄露到输出' }
    Check (-not (Test-Path -LiteralPath $private.Saves)) '私密交接退出不保存'
}

$timed = Run-Cli 'timed-stale-input' ($common + @('--ruleset', 'ranked')) $rankedPlan.FirstChoosePrefix -Steps @(
    @{ Expect = '选择行动（私密输入，q 退出）> '; DelayMs = 41000; Input = "PRIVATE_EXPIRED_SENTINEL`n`n/surrender`n继续`n`n1`n/surrender`nq`n" }
)
$timedRecords = @(Records $timed)
Check ($timed.Out.Contains('本次输入已超时') -and $timed.Out.Contains('输入“继续”恢复交接')) '真实经过时间耗尽后自动提交并要求恢复屏障'
Check (-not $timed.Out.Contains('PRIVATE_EXPIRED_SENTINEL')) '过期私密输入不回显'
Check ($timedRecords.Count -eq 1) '超时恢复后只保存完成的第一局'
$turn = @($timedRecords[0].Record.Events | Where-Object { $_.Type -eq [StreamDuel.Core.BattleRecordEventType]::Turn })[0]
Check ($turn.A0.Type -eq [StreamDuel.Core.ActionType]::Pass -and $turn.A1.Type -eq [StreamDuel.Core.ActionType]::Skill) '旧输入与恢复前命令不跨玩家复用，超时 Pass 与新玩家技能正常结算'
$surrenders = @($timedRecords[0].Record.Events | Where-Object { $_.Type -eq [StreamDuel.Core.BattleRecordEventType]::Surrender })
Check ($surrenders.Count -eq 1 -and $surrenders[0].Side -eq 1) '恢复屏障之前的过期投降未执行'

[IO.File]::WriteAllText((Join-Path $run 'summary.json'), (ConvertTo-Json -Depth 5 -InputObject @{
    checks = $checks.ToArray(); cli = $cliDll; baseline = $BaselineDll
}), $utf8)
Write-Output "T129 同屏赛制验收通过：$($checks.Count) 项检查。证据：$run"
