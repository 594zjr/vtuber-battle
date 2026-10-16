#requires -Version 7.0
param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug')

# 同时保留兼容提示、显示清理和原始分享记录，避免两个功能合并时互相绕过。
# 先构建 T93 + T97 + T98 合并后的 Tools/Cli；本脚本不构建，产物仅在本仓库 obj 下。
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'Cli.csproj') -PathType Leaf)) {
    throw '请在本仓库 Tools/Cli 中运行此脚本。'
}
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$prefix = $repository.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$current = [IO.Path]::GetFullPath((Get-Location).Path)
if ($current -ne $repository -and -not $current.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw '请在本仓库内运行此脚本。'
}
$output = Join-Path $PSScriptRoot "bin/$Configuration/net8.0"
$cliDll = Join-Path $output 'Cli.dll'
$coreDll = Join-Path $output 'StreamDuel.Core.dll'
if (-not (Test-Path -LiteralPath $cliDll -PathType Leaf) -or -not (Test-Path -LiteralPath $coreDll -PathType Leaf)) {
    throw "请先运行 dotnet build Tools/Cli -c $Configuration。"
}
$runDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ('obj/text-replay-compat/' + [Guid]::NewGuid().ToString('N'))))
if (-not $runDirectory.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw '验收产物目录必须位于本仓库内。'
}
[void][IO.Directory]::CreateDirectory($runDirectory)
$temporaryDirectory = Join-Path $runDirectory 'temp'
[void][IO.Directory]::CreateDirectory($temporaryDirectory)
$utf8 = [Text.UTF8Encoding]::new($false)
$checks = [Collections.Generic.List[string]]::new()
$invocations = [Collections.Generic.List[object]]::new()
$binaryHashes = @(Get-FileHash -LiteralPath $cliDll, $coreDll -Algorithm SHA256 | Select-Object Path, Hash)
$stage = '准备夹具'
$failure = $null

function Assert-Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { $script:failure = $Message; throw $Message }
}

function Write-Fixture([string]$Name, [string]$Json) {
    $path = Join-Path $runDirectory "$Name.json"
    [IO.File]::WriteAllText($path, $Json, $utf8)
    return $path
}

function File-Bytes([string]$Path) {
    return [Convert]::ToBase64String([IO.File]::ReadAllBytes($Path))
}

function Assert-Safe([string]$Text, [string]$Name) {
    # 换行是输出排版；其余控制字符、不可见格式字符和孤立代理项不得泄漏。
    Assert-Check ($Text -notmatch '[\x00-\x08\x0B\x0C\x0E-\x1F\x7F\p{Cf}\u034F]') "$Name 含未清理的控制或不可见格式字符。"
    for ($index = 0; $index -lt $Text.Length; $index++) {
        if ([char]::IsHighSurrogate($Text[$index])) {
            Assert-Check ($index + 1 -lt $Text.Length -and [char]::IsLowSurrogate($Text[$index + 1])) "$Name 含孤立高代理项。"
            $index++
        }
        else { Assert-Check (-not [char]::IsLowSurrogate($Text[$index])) "$Name 含孤立低代理项。" }
    }
    Assert-Check (-not $Text.Contains($word)) "$Name 直接显示了词表内的原词。"
}

function Invoke-Cli([string]$Name, [string[]]$Parameters, [int]$ExpectedExit = 0) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = 'dotnet'
    $start.ArgumentList.Add($cliDll)
    foreach ($parameter in $Parameters) { $start.ArgumentList.Add($parameter) }
    $start.WorkingDirectory = $runDirectory
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = $utf8
    $start.StandardErrorEncoding = $utf8
    $start.Environment['TEMP'] = $temporaryDirectory
    $start.Environment['TMP'] = $temporaryDirectory
    $start.Environment['DOTNET_SKIP_FIRST_TIME_EXPERIENCE'] = '1'
    $start.Environment['DOTNET_GENERATE_ASPNET_CERTIFICATE'] = 'false'
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    $started = $false
    try {
        $started = $process.Start()
        Assert-Check $started "$Name 无法启动。"
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $process.StandardInput.Close()
        Assert-Check ($process.WaitForExit(30000)) "$Name 超过 30 秒未退出。"
        $result = [pscustomobject]@{ Out = $stdout.Result; Err = $stderr.Result; Exit = $process.ExitCode }
        # 原始输出只留本地产物，不把夹具或子进程错误内容带进结果摘要。
        [IO.File]::WriteAllText((Join-Path $runDirectory "$Name.stdout.log"), $result.Out, $utf8)
        [IO.File]::WriteAllText((Join-Path $runDirectory "$Name.stderr.log"), $result.Err, $utf8)
        $invocations.Add([ordered]@{ name = $Name; exit = $result.Exit; expectedExit = $ExpectedExit })
        Assert-Check ($result.Exit -eq $ExpectedExit) "$Name 退出码不符合预期。"
        return $result
    }
    finally {
        if ($started -and -not $process.HasExited) {
            $process.Kill($true)
            [void]$process.WaitForExit(5000)
        }
        $process.Dispose()
    }
}

try {
    [void][Reflection.Assembly]::LoadFrom($coreDll)
    $content = Join-Path $output 'Content'
    $db = [StreamDuel.Core.ContentDb]::FromJson(
        [IO.File]::ReadAllText((Join-Path $content 'StreamDuelRules.json')),
        [IO.File]::ReadAllText((Join-Path $content 'Packs/original.json')))
    $teams = [Collections.Generic.IList[string][]]@(
        [Collections.Generic.List[string]]@('bell', 'enka', 'shiori', 'lumi', 'hane', 'momo'),
        [Collections.Generic.List[string]]@('kei', 'yuan', 'nyako', 'buck', 'kasumi', 'bee'))
    $battle = [StreamDuel.Core.Battle]::new($db, $teams, [string[]]@('你', '对手'), [uint32]97)
    for ($step = 0; $step -lt 1000 -and $battle.Phase -ne [StreamDuel.Core.Phase]::Over; $step++) {
        if ($battle.Phase -eq [StreamDuel.Core.Phase]::Choose) {
            $battle.ResolveTurn([StreamDuel.Core.Ai]::Choose($battle, 0), [StreamDuel.Core.Ai]::Choose($battle, 1))
        }
        else {
            for ($side = 0; $side -lt 2; $side++) {
                if ($battle.Need[$side]) { $battle.Replace($side, [StreamDuel.Core.Ai]::ChooseReplacement($battle, $side)) }
            }
        }
    }
    Assert-Check ($battle.Phase -eq [StreamDuel.Core.Phase]::Over) '夹具未完成对局。'
    $record = [StreamDuel.Core.BattleRecord]::FromJson($battle.Record.ToJson())
    $word = [StreamDuel.Core.TextLint]::DefaultBannedWords[0]
    Assert-Check (-not [string]::IsNullOrEmpty($word)) '文字夹具需要非空词表项。'
    $firstRaw = " `t甲`n" + [char]0x200b + $word + '🦊' + [char]0x202e + ' 名称 ' + [char]0
    $secondRaw = ('🛰' * 16) + [char]0xd800 + "`r`n"
    $record.Names = [string[]]@($firstRaw, $secondRaw)
    $record.ContentHash = '0000000000000000'
    $rawJson = $record.ToJson()
    $source = Write-Fixture 'unsafe-mismatch' $rawJson
    $sourceBytes = File-Bytes $source
    $sourceHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
    $compatibility = [StreamDuel.Core.Replay]::Check($db, $record)
    Assert-Check ($compatibility.CanReplay -and $compatibility.Reason.Contains('结果可能不同')) '夹具没有实际触发指纹不符兼容警告。'
    $safeFirst = [StreamDuel.Core.TextInput]::Clean($firstRaw, [StreamDuel.Core.TextInputKind]::PlayerName).Text
    $safeSecond = [StreamDuel.Core.TextInput]::Clean($secondRaw, [StreamDuel.Core.TextInputKind]::PlayerName).Text
    Assert-Check ($safeFirst.Contains('*') -and $safeSecond -ceq ('🛰' * 12)) '夹具没有实际覆盖屏蔽词及 Unicode 超长截断。'

    $stage = '异常昵称与指纹不符的显式尽力回放'
    $saveDirectory = Join-Path $runDirectory 'must-not-create-saves'
    $reportPath = Join-Path $runDirectory 'approximate.md'
    $played = Invoke-Cli 'mismatch-best' @('--replay', $source, '--auto', '--replay-best-effort', '--share', '--report', $reportPath, '--save-dir', $saveDirectory)
    Assert-Check ($played.Err.Length -eq 0 -and $played.Out.Contains('回看结束：')) '显式尽力回放未正常完局。'
    Assert-Check ($played.Out.Contains('结果可能不同') -and $played.Out.Contains('昵称显示已调整：')) '兼容性或昵称净化的中文警告丢失。'
    Assert-Check ($played.Out.Contains($safeFirst) -and $played.Out.Contains($safeSecond)) 'stdout 没有显示净化后的双方昵称。'
    Assert-Safe $played.Out 'stdout'
    $markdown = [IO.File]::ReadAllText($reportPath)
    Assert-Safe $markdown 'Markdown'
    Assert-Check ($markdown.Contains($safeFirst) -and $markdown.Contains($safeSecond)) 'Markdown 没有显示净化后的双方昵称。'
    $replayed = [StreamDuel.Core.Replay]::RunBestEffort($db, $record)
    Assert-Check ($replayed.Phase -eq [StreamDuel.Core.Phase]::Over) 'Core 尽力回放夹具未完局。'
    $expectedMarkdown = "> 尽力重放：原录像与当前内容不同，以下结果按当前内容重新计算，可能与原对局不同。`n`n" +
        [StreamDuel.Core.BattleReport]::Markdown([StreamDuel.Core.TextInput]::ForDisplay($replayed), 0)
    Assert-Check ($markdown -ceq $expectedMarkdown) '战报不是包含尽力警告的完整净化显示副本。'
    $shared = [regex]::Matches($played.Out, '(?m)^分享码：(SD1-[A-Za-z0-9_-]+)\r?$')
    Assert-Check ($shared.Count -eq 1) '回放应恰好导出一条分享码。'
    $decoded = [StreamDuel.Core.ShareCode]::Decode($shared[0].Groups[1].Value)
    Assert-Check ($decoded.ToJson() -ceq $rawJson) '分享码没有保留完整原始记录。'
    Assert-Check ($decoded.Names[0] -ceq $firstRaw -and $decoded.Names[1] -ceq $secondRaw -and $decoded.ContentHash -ceq $record.ContentHash) '分享码改写了原始昵称或指纹。'
    Assert-Check ($record.ToJson() -ceq $rawJson -and (File-Bytes $source) -ceq $sourceBytes) '显示、回放或导出改写了输入记录或源字节。'
    Assert-Check (-not (Test-Path -LiteralPath $saveDirectory) -and -not (Test-Path -LiteralPath (Join-Path $runDirectory 'saves'))) '回放创建了战绩或新录像存档。'
    $checks.Add('指纹不符显式尽力回放：stdout/完整 Markdown 净化；两类中文警告保留；分享码原记录及源字节不变；无战绩和新录像')

    # T98 追加：真实 v3 终止记录分别覆盖严格投降与指纹不符的和局链，不替换原 T93/T97 检查。
    function Library-Fingerprint([string]$Directory) {
        return [string]::Join("`n", @([IO.Directory]::GetFiles($Directory, '*', [IO.SearchOption]::AllDirectories) |
            Sort-Object | ForEach-Object { $_ + ':' + (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash }))
    }
    foreach ($ending in @('surrender', 'draw')) {
        $stage = "v3-$ending"
        $caseDirectory = Join-Path $runDirectory $stage
        [void][IO.Directory]::CreateDirectory($caseDirectory)
        $terminalBattle = [StreamDuel.Core.Battle]::new($db, $teams, [string[]]@($firstRaw, $secondRaw), [uint32]98)
        if ($ending -eq 'surrender') { $terminalBattle.Surrender(0) }
        else {
            $terminalBattle.OfferDraw(0)
            $terminalBattle.RespondDraw(1, $false)
            $terminalBattle.OfferDraw(1)
            $terminalBattle.RespondDraw(0, $true)
        }
        Assert-Check ($terminalBattle.Phase -eq [StreamDuel.Core.Phase]::Over -and $terminalBattle.Record.Version -eq 3) 'v3 夹具没有通过真实终止入口完局。'
        $terminalRecord = [StreamDuel.Core.BattleRecord]::FromJson($terminalBattle.Record.ToJson())
        if ($ending -eq 'draw') { $terminalRecord.ContentHash = '0000000000000000' }
        $terminalJson = $terminalRecord.ToJson()
        $terminalSource = Join-Path $caseDirectory 'source.json'
        [IO.File]::WriteAllText($terminalSource, $terminalJson, $utf8)
        $terminalBytes = File-Bytes $terminalSource
        $terminalExpected = if ($ending -eq 'draw') { [StreamDuel.Core.Replay]::RunBestEffort($db, $terminalRecord) } else { [StreamDuel.Core.Replay]::Run($db, $terminalRecord) }
        $terminalMarkdown = [StreamDuel.Core.BattleReport]::Markdown([StreamDuel.Core.TextInput]::ForDisplay($terminalExpected), 0)
        if ($ending -eq 'draw') { $terminalMarkdown = "> 尽力重放：原录像与当前内容不同，以下结果按当前内容重新计算，可能与原对局不同。`n`n" + $terminalMarkdown }
        $unusedSave = Join-Path $caseDirectory 'must-not-create-saves'
        $librarySave = Join-Path $caseDirectory 'library-save'
        $libraryDirectory = Join-Path $librarySave 'replay-library'
        $library = [StreamDuel.Core.ReplayLibrary]::new($libraryDirectory, 200, $null)
        $libraryId = $library.Save($terminalRecord, [StreamDuel.Core.BattleSummary]::From($terminalBattle), $null)
        Assert-Check ($library.Load($libraryId).ToJson() -ceq $terminalJson) '录像库没有保留原始 v3 事件、响应或名称。'
        $libraryBefore = Library-Fingerprint $librarySave
        foreach ($entryPoint in @('file', 'library')) {
            $name = "v3-$ending-$entryPoint"
            $inputReference = if ($entryPoint -eq 'file') { $terminalSource } else { $libraryId }
            $selectedSave = if ($entryPoint -eq 'file') { $unusedSave } else { $librarySave }
            $terminalReport = Join-Path $caseDirectory "$entryPoint.md"
            $parameters = @('--replay', $inputReference, '--auto', '--share', '--report', $terminalReport, '--save-dir', $selectedSave)
            if ($ending -eq 'draw') { $parameters += '--replay-best-effort' }
            $terminalOutput = Invoke-Cli $name $parameters
            Assert-Check ($terminalOutput.Err.Length -eq 0 -and $terminalOutput.Out.Contains('回看结束：')) "$name 没有正常完成。"
            Assert-Check ($terminalOutput.Out.Contains('昵称显示已调整：')) "$name 缺少昵称净化提示。"
            Assert-Check ($terminalOutput.Out.Contains('结果可能不同') -eq ($ending -eq 'draw')) "$name 的兼容警告不准确。"
            Assert-Check ($terminalOutput.Out.Contains($safeFirst) -and $terminalOutput.Out.Contains($safeSecond)) "$name 未显示净化后的双方名称。"
            Assert-Safe $terminalOutput.Out "$name stdout"
            $actualMarkdown = [IO.File]::ReadAllText($terminalReport)
            Assert-Safe $actualMarkdown "$name Markdown"
            Assert-Check ($actualMarkdown -ceq $terminalMarkdown) "$name 没有导出完整的净化战报和适用警告。"
            $exported = [regex]::Matches($terminalOutput.Out, '(?m)^分享码：(SD1-[A-Za-z0-9_-]+)\r?$')
            Assert-Check ($exported.Count -eq 1) "$name 分享码数量错误。"
            Assert-Check ([StreamDuel.Core.ShareCode]::Decode($exported[0].Groups[1].Value).ToJson() -ceq $terminalJson) "$name 的分享码改写了原始 v3 记录。"
            Assert-Check ((File-Bytes $terminalSource) -ceq $terminalBytes -and $terminalRecord.ToJson() -ceq $terminalJson) "$name 改写了源字节或源记录。"
            Assert-Check ((Library-Fingerprint $librarySave) -ceq $libraryBefore) "$name 新增、清理或改写了录像库/原生索引。"
            Assert-Check (-not (Test-Path -LiteralPath $unusedSave) -and -not (Test-Path -LiteralPath (Join-Path $librarySave 'profile.json'))) "$name 回看累计了战绩或创建了额外存档。"
        }
        $roundtrip = $library.Load($libraryId)
        Assert-Check ($roundtrip.ToJson() -ceq $terminalJson) '两种回看之后录像库原记录改变。'
        $reloaded = if ($ending -eq 'draw') { [StreamDuel.Core.Replay]::RunBestEffort($db, $roundtrip) } else { [StreamDuel.Core.Replay]::Run($db, $roundtrip) }
        Assert-Check ($reloaded.StateHash() -eq $terminalBattle.StateHash()) 'v3 录像库往返未恢复终局状态。'
        Assert-Check ((Library-Fingerprint $librarySave) -ceq $libraryBefore) '录像库读取过程改写了原生索引或源文件。'
    }
    $checks.Add('v3 投降/拒绝后同意和局：文件与原生录像库入口均净化 stdout/Markdown，适用兼容警告保留，分享与库往返原 record 不变，回看不新增战绩或录像')
    $stage = '未来版本仍须拒绝且不得导出'
    $future = [StreamDuel.Core.BattleRecord]::FromJson($rawJson)
    $future.Version = [StreamDuel.Core.BattleRecord]::CurrentVersion + 1
    $futureJson = $future.ToJson()
    $futurePath = Write-Fixture 'unsafe-future' $futureJson
    $futureBytes = File-Bytes $futurePath
    Assert-Check ([StreamDuel.Core.BattleRecord]::FromJson($futureJson).Version -eq $future.Version) '未来夹具不是可读取的合法结构。'
    foreach ($target in @('fresh', 'existing')) {
        $futureReport = Join-Path $runDirectory ("future-$target.md")
        if ($target -eq 'existing') { [IO.File]::WriteAllText($futureReport, '既有战报哨兵：必须保留。', $utf8) }
        $before = if ($target -eq 'existing') { File-Bytes $futureReport } else { $null }
        $refused = Invoke-Cli ("future-$target") @('--replay', $futurePath, '--auto', '--replay-best-effort', '--share', '--report', $futureReport, '--save-dir', $saveDirectory) 1
        Assert-Check ($refused.Err.Contains('录像来自更新的版本') -and $refused.Err -notmatch ' at |未知参数') '未来版本未由兼容检查给出中文拒绝。'
        Assert-Check ($refused.Out.Length -eq 0) '未来版本拒绝前产生了回放、分享码或导出输出。'
        Assert-Safe $refused.Err '未来版本错误提示'
        if ($target -eq 'existing') { Assert-Check ((File-Bytes $futureReport) -ceq $before) '未来版本拒绝时覆盖了既有战报。' }
        else { Assert-Check (-not (Test-Path -LiteralPath $futureReport)) '未来版本拒绝时仍创建了战报。' }
        Assert-Check ((File-Bytes $futurePath) -ceq $futureBytes -and $future.ToJson() -ceq $futureJson) '未来版本拒绝时改写了源文件或记录。'
        Assert-Check (-not (Test-Path -LiteralPath $saveDirectory) -and -not (Test-Path -LiteralPath (Join-Path $runDirectory 'saves'))) '未来版本拒绝时创建了战绩或新录像。'
    }
    Assert-Check ((File-Bytes $source) -ceq $sourceBytes) '后续拒绝流程改写了此前来源录像。'
    $checks.Add('未来合法版本含异常昵称及不同指纹：尽力回放仍提前中文拒绝，stdout 为空，不新建/覆盖战报，不改源字节，不保存战绩或录像')
    [IO.File]::WriteAllText((Join-Path $runDirectory 'result.json'),
        ([ordered]@{ status = 'PASS'; checks = $checks.ToArray(); invocations = $invocations.ToArray(); binaries = $binaryHashes; sourceSha256 = $sourceHash } | ConvertTo-Json -Depth 8), $utf8)
    Write-Output "PASS：T93/T97/T98 联合回归，$($invocations.Count) 个子进程；证据：$runDirectory"
}
catch {
    # 框架异常可能含原始文本，只把固定断言和阶段放入可分享的结果摘要。
    $safeFailure = if ($null -eq $failure) { '执行失败，请在本地检查证据；原始异常未写入摘要。' } else { $failure }
    [IO.File]::WriteAllText((Join-Path $runDirectory 'result.json'),
        ([ordered]@{ status = 'FAIL'; stage = $stage; error = $safeFailure; checks = $checks.ToArray(); invocations = $invocations.ToArray(); binaries = $binaryHashes } | ConvertTo-Json -Depth 8), $utf8)
    throw "T93/T97/T98 联合回归未通过：$stage；$safeFailure 证据：$runDirectory"
}
