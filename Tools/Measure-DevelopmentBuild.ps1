<#
.SYNOPSIS
生成隔离构建测量计划；只有显式批准窗口才执行，不代表已证明提速。
.DESCRIPTION
Cold 使用新目录；WarmUnchanged 复用同一目录的成功基线。
WarmChangedTest 核对独立 baseline/changed 冻结源码与同一 executor 的成功基线；工具不改源码。
所有阶段固定源码/SDK/环境，分析器开启。BuildNoRestore 包含求值及依赖构建，
不能把其耗时当作纯 Csc 时间；请用 binlog 检查 Csc/CoreCompile 和跳过原因。
InputsComplete 是调用方对外部 imports、NuGet 配置/包等输入清单完整性的确认，
不是自动依赖证明。未确认、采样缺失或运行中输入变化均不能形成可比较结果。
工具自身取得重型 permit；调用方不得再套一层 permit，避免嵌套等待。
日志/binlog 可能含本机路径与配置，不应直接发布。
#>
[CmdletBinding()]
param(
    [ValidateSet('Cold','WarmUnchanged','WarmChangedTest')][string]$Scenario = 'Cold',
    [ValidateSet('Default','CompilerCpu1','CompilerCpu2')][string]$Profile = 'Default',
    [ValidateSet('Debug','Release')][string]$Configuration = 'Debug',
    [string]$ArtifactsPath,
    [string]$ChangedTestFile,
    [string]$BaselineSnapshotPath,
    [string]$ChangedSnapshotPath,
    [string[]]$AdditionalInputs = @(),
    [switch]$InputsComplete,
    [switch]$Execute,
    [string]$ApprovedWindow,
    [ValidateRange(1,86400)][int]$TimeoutSeconds = 1200,
    [string]$DotnetPath = 'dotnet'
)
$ErrorActionPreference = 'Stop'
$entryWatch=[Diagnostics.Stopwatch]::StartNew()
$entryStartedUtc=[DateTime]::UtcNow.ToString('o')
$inputScanMetrics=[ordered]@{ Attempts=0; Completed=0; Failed=0; FilesHashed=0; ElapsedMs=0L }
$root = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
if (-not $ArtifactsPath) { $ArtifactsPath = Join-Path $root ('build/measure/' + [guid]::NewGuid().ToString('N')) }
$artifacts = [IO.Path]::GetFullPath($ArtifactsPath, $root)
$allowed = (Join-Path $root 'build/measure') + [IO.Path]::DirectorySeparatorChar
if (-not $artifacts.StartsWith($allowed, [StringComparison]::Ordinal)) { throw '产物必须在本工作树 build/measure 的独立子目录中。' }
# 不跟随产物目录链；避免写入别的工作树或已有共享 bin/obj。
for ($p = $artifacts; $p; $p = Split-Path $p -Parent) {
    if ((Test-Path -LiteralPath $p) -and ((Get-Item -LiteralPath $p -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "产物路径不能含链接：$p" }
}
if ($Scenario -eq 'Cold' -and (Test-Path -LiteralPath $artifacts)) { throw 'Cold 必须使用不存在的新产物目录；不删除或清空已有目录。' }
if ($Scenario -eq 'WarmChangedTest' -and $ChangedTestFile -notmatch '^Server/DfoServer/SelfTests/(?:[^/]+/)*[^/]+\.cs$') { throw 'WarmChangedTest 必须指定仓库相对 SelfTests C# 文件。' }
$project = Join-Path $root 'Server/DfoServer/DfoServer.csproj'
$common = @('-nologo','-clp:PerformanceSummary','-m:1','-nr:false',"-p:Configuration=$Configuration", "-p:ArtifactsPath=$artifacts",'-p:UseArtifactsOutput=true','-p:UseSharedCompilation=false','-p:RunAnalyzers=true','-p:RunAnalyzersDuringBuild=true')
if ($Profile -ne 'Default') { $common += '-p:CscEnvironment=DOTNET_PROCESSOR_COUNT=' + $Profile.Substring($Profile.Length - 1) }
$steps = @(
    [ordered]@{ Name='Restore'; Arguments=@('msbuild',$project,'-t:Restore') + $common },
    [ordered]@{ Name='EvaluationProbe'; Arguments=@('msbuild',$project,'-getProperty:TargetFramework') + $common },
    [ordered]@{ Name='BuildNoRestore'; Arguments=@('build',$project,'--no-restore') + $common }
)
$plan = [ordered]@{ Schema=1; Mode='plan-only'; Scenario=$Scenario; Profile=$Profile; Configuration=$Configuration; SourceRoot=$root; ArtifactsPath=$artifacts; ColdDefinition='fresh bin/obj only; OS and NuGet caches are not cleared'; Steps=$steps; CompilerTiming='Read Csc/CoreCompile from binlog; BuildNoRestore is not compiler-only'; Memory='sampled owned process-tree RSS on Linux; unknown elsewhere'; InputsComplete=[bool]$InputsComplete }
if ($Scenario -eq 'WarmChangedTest') { $plan['SnapshotContract'] = '独立实验目录下 baseline/changed/executor 三份 Git 源码；Cold 后仅 executor 的指定测试文件变为 changed 内容；不共享 bin/obj。' }
if (-not $Execute) { $plan | ConvertTo-Json -Depth 8; return }
if (-not $IsLinux) { throw '当前仅验证 Linux 进程树采样；其它平台只支持计划，不生成可比较测量。' }
if ([string]::IsNullOrWhiteSpace($ApprovedWindow)) { throw '执行必须提供负责人批准的 -ApprovedWindow；参数本身不能代替授权。' }
if (-not $InputsComplete) { throw '缺少完整输入确认；拒绝执行不可比较的大构建。' }
$budgetModule = Join-Path $PSScriptRoot 'TestResourceBudget.psm1'
if (-not (Test-Path -LiteralPath $budgetModule)) { throw '缺少 TestResourceBudget.psm1；不能绕过跨工作树预算。' }
Import-Module $budgetModule
. (Join-Path $PSScriptRoot 'DevelopmentSelectedRun.ps1')
. (Join-Path $PSScriptRoot 'DevelopmentMeasurementInputs.ps1')
function Get-MeasurementDelta($Left,$Right) {
    @(@($Left.Keys)+@($Right.Keys) | Sort-Object -Unique | Where-Object { $Left[$_] -cne $Right[$_] })
}
function Get-ChangedSnapshotEvidence {
    if (-not $BaselineSnapshotPath -or -not $ChangedSnapshotPath) { throw 'WarmChangedTest 缺少 baseline/changed 冻结源码。' }
    # 强制三份源码位于专用实验目录；不允许在活动开发树制造变更。
    $experiment=Split-Path $root -Parent
    $measure=Split-Path $experiment -Parent
    if ((Split-Path $root -Leaf) -cne 'executor' -or (Split-Path $measure -Leaf) -cne 'measure' -or (Split-Path (Split-Path $measure -Parent) -Leaf) -cne 'build') { throw 'executor 必须位于 build/measure/<实验>/executor。' }
    $baseline=[IO.Path]::GetFullPath($BaselineSnapshotPath,$root)
    $changed=[IO.Path]::GetFullPath($ChangedSnapshotPath,$root)
    if ($baseline -cne (Join-Path $experiment 'baseline') -or $changed -cne (Join-Path $experiment 'changed')) { throw '冻结源码必须是同实验目录的独立 baseline/changed 兄弟目录。' }
    foreach($directory in @($root,$baseline,$changed)) { Assert-NoMeasurementLinks $directory }
    $left=Get-InputSnapshot $baseline
    $right=Get-InputSnapshot $changed
    $delta=@(Get-MeasurementDelta $left $right)
    if ($delta.Count -ne 1 -or $delta[0] -cne $ChangedTestFile -or -not $left.Contains($ChangedTestFile) -or -not $right.Contains($ChangedTestFile)) { throw '冻结快照只能有一个已存在的 SelfTests C# 文件内容变化。' }
    [ordered]@{ BaselineRoot=$baseline; ChangedRoot=$changed; Baseline=$left; Changed=$right; ChangedTestFile=$ChangedTestFile }
}
function Get-InputSnapshot([string]$SourceRoot = $root) {
    $scanWatch=[Diagnostics.Stopwatch]::StartNew()
    $scanCompleted=$false
    $hashedFiles=0
    try {
    $top=(& git -C $SourceRoot rev-parse --show-toplevel)
    if ($LASTEXITCODE -ne 0 -or [IO.Path]::GetFullPath($top) -cne $SourceRoot) { throw '源码必须是独立 Git 根，不能借用父仓库枚举。' }
    $paths = @(& git -C $SourceRoot -c core.quotepath=false ls-files --cached --others --exclude-standard)
    if ($LASTEXITCODE -ne 0 -or $paths.Count -eq 0) { throw '无法完整枚举源码输入。' }
    $files = [ordered]@{}
    foreach ($path in ($paths | Sort-Object -Unique)) {
        if ($path -match '(?i)\.pvf$') { throw '构建输入不允许包含 PVF；不计算整包哈希。' }
        $full = Join-Path $SourceRoot $path
        $files[$path] = Get-MeasurementFileDigest $full
        $hashedFiles++
    }
    foreach ($path in ($AdditionalInputs | Sort-Object -Unique)) {
        $full = [IO.Path]::GetFullPath($path,$root)
        if ($full -match '(?i)\.pvf$') { throw '不允许对 PVF 计算整包哈希。' }
        $files["external:$full"] = Get-MeasurementFileDigest $full
        $hashedFiles++
    }
    $scanCompleted=$true
    return $files
    } finally {
        # 每次扫描只累计一组数值，不为每个路径新增计时或日志。
        $inputScanMetrics.Attempts++
        if($scanCompleted){$inputScanMetrics.Completed++}else{$inputScanMetrics.Failed++}
        $inputScanMetrics.FilesHashed += $hashedFiles
        $inputScanMetrics.ElapsedMs += $scanWatch.ElapsedMilliseconds
    }
}
function Get-ArtifactSnapshot {
    $files = [ordered]@{}
    foreach ($name in @('bin','obj')) {
        $directory = Join-Path $artifacts $name
        if (-not (Test-Path -LiteralPath $directory)) { continue }
        if ((Get-Item -LiteralPath $directory -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'bin/obj 根不能是链接。' }
        foreach ($item in (Get-ChildItem -LiteralPath $directory -Recurse -Force | Sort-Object FullName)) {
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw '产物不能包含链接。' }
            if (-not $item.PSIsContainer) { $files[[IO.Path]::GetRelativePath($artifacts,$item.FullName)] = Get-MeasurementFileDigest $item.FullName }
        }
    }
    return $files
}
function Get-InputWriteTimes($Inputs) {
    $times=[ordered]@{}
    foreach($relative in $Inputs.Keys) {
        $full=if($relative.StartsWith('external:')){$relative.Substring(9)}else{Join-Path $root $relative}
        $times[$relative]=(Get-MeasurementFileObservation $full).WriteTicks
    }
    $times
}
function Get-ArtifactWriteTimes {
    $times=[ordered]@{}
    foreach($relative in (Get-ArtifactSnapshot).Keys) { $times[$relative]=(Get-MeasurementFileObservation (Join-Path $artifacts $relative)).WriteTicks }
    $times
}
function Assert-WarmArtifacts {
    if (-not $prior.Contains('ArtifactWriteTimesUtc') -or @(Get-MeasurementDelta (Get-ArtifactWriteTimes) $prior.ArtifactWriteTimesUtc).Count -ne 0) { throw 'Warm 产物时间戳缺失或变化；不能证明同一增量现场。' }
    if (@(Get-MeasurementDelta (Get-ArtifactSnapshot) $prior.Artifacts).Count -ne 0) { throw 'Warm 产物内容缺失或变化。' }
}
function Get-EnvironmentHash {
    $text = (Get-ChildItem Env: | Sort-Object Name | ForEach-Object { $_.Name + '=' + $_.Value }) -join "`n"
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($text)))
}
$dotnet = (Get-Command $DotnetPath -CommandType Application -ErrorAction Stop).Source
$sdk = (& $dotnet --info | Out-String)
if ($LASTEXITCODE -ne 0) { throw '无法读取 SDK 身份。' }
$snapshots=$null
if ($Scenario -eq 'WarmChangedTest') { $snapshots=Get-ChangedSnapshotEvidence }
$before = Get-InputSnapshot
$inputTimes=Get-InputWriteTimes $before
$environmentHash = Get-EnvironmentHash
$receiptPath = Join-Path $artifacts 'measurement.json'
if ($Scenario -ne 'Cold') {
    if (-not (Test-Path -LiteralPath $receiptPath)) { throw 'Warm 场景缺少同目录基线。' }
    $prior = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json -AsHashtable
    if (-not $prior.Valid -or $prior.SourceRoot -ne $root -or $prior.Profile -ne $Profile -or $prior.Configuration -ne $Configuration -or $prior.Sdk -cne $sdk -or $prior.EnvironmentHash -ne $environmentHash -or $prior.DotnetPath -ne $dotnet) { throw 'Warm 基线不是同源码目录/SDK/配置/环境/profile的有效结果。' }
    if ((Get-ArtifactSnapshot | ConvertTo-Json -Compress -Depth 4) -cne ($prior.Artifacts | ConvertTo-Json -Compress -Depth 4)) { throw 'Warm 产物缺失或已被其它构建修改。' }
    Assert-WarmArtifacts
    if (-not $prior.Contains('InputWriteTimesUtc')) { throw '基线缺少源码时间戳，须重新建立。' }
    $timeDelta=@(Get-MeasurementDelta $inputTimes $prior.InputWriteTimesUtc)
    $delta = @(@($before.Keys) + @($prior.Inputs.Keys) | Sort-Object -Unique | Where-Object { $before[$_] -ne $prior.Inputs[$_] })
    if ($Scenario -eq 'WarmUnchanged' -and ($delta.Count -ne 0 -or $timeDelta.Count -ne 0)) { throw 'WarmUnchanged 输入发生变化。' }
    if ($Scenario -eq 'WarmChangedTest') {
        if ($prior.ToolHash -cne (Get-FileHash $PSCommandPath -Algorithm SHA256).Hash) { throw '基线工具身份不同。' }
        if (@(Get-MeasurementDelta $prior.Inputs $snapshots.Baseline).Count -ne 0 -or @(Get-MeasurementDelta $before $snapshots.Changed).Count -ne 0) { throw '成功基线/执行源码不匹配冻结快照。' }
        if ($timeDelta.Count -ne 1 -or $timeDelta[0] -cne $ChangedTestFile) { throw '变更场景只能修改指定测试源码的时间戳。' }
        $changedTime=[long]$inputTimes[$ChangedTestFile]
        $latestArtifact=@($prior.ArtifactWriteTimesUtc.Values | ForEach-Object { [long]$_ } | Sort-Object)[-1]
        if ($changedTime -le $latestArtifact -or $changedTime -gt [DateTime]::UtcNow.Ticks) { throw '指定测试文件时间必须晚于基线产物且不在未来，避免错误跳过编译。' }
        if ($delta.Count -ne 1 -or $delta[0] -cne $ChangedTestFile) { throw '仅允许指定测试文件变化。' }
    }

}
New-Item -ItemType Directory -Path $artifacts -Force | Out-Null
if ($Scenario -eq 'Cold') {
    # 并发 Cold 不能在等待期间接管刚生成的目录。该标记永久保留，不用于释放预算。
    $claim=[IO.File]::Open((Join-Path $artifacts '.cold-owner'),[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
    $claim.Dispose()
}
$lockPath=Join-Path $artifacts '.measurement.lock'
if ((Test-Path -LiteralPath $lockPath) -and ((Get-Item -LiteralPath $lockPath -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw '测量锁不能是链接。' }
$lock = [IO.File]::Open($lockPath,[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
$run = Join-Path $artifacts ('logs-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $run | Out-Null
$result = [ordered]@{ Schema=1; ToolHash=(Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash; InputEvidence='declared source and additional inputs; not an automatic consumed-dependency closure'; Valid=$false; SourceRoot=$root; SourceCommit=(& git -C $root rev-parse HEAD); Scenario=$Scenario; Profile=$Profile; Configuration=$Configuration; DotnetPath=$dotnet; Sdk=$sdk; EnvironmentHash=$environmentHash; Inputs=$before; InputWriteTimesUtc=$inputTimes; ApprovedWindow=$ApprovedWindow; StartedUtc=[DateTime]::UtcNow.ToString('o'); Steps=@(); Failure=$null; CompilerTiming=$plan.CompilerTiming; StageTiming='包含监督器启动、输出排空与收据读取，不是纯 dotnet 或 Csc 时间；不含入口前后输入哈希。'; TaskTiming='各阶段 stdout 保留 MSBuild PerformanceSummary；Csc 包含分析器，不据此分离分析器时间；目标与任务计时可能重叠，不相加。' }
if ($snapshots) { $result['FrozenSnapshots']=$snapshots; $result['BaselineReceiptHash']=(Get-FileHash $receiptPath -Algorithm SHA256).Hash }
$result['EntryStartedUtc']=$entryStartedUtc
$result['EntryTiming']='从脚本主体开始到最终收据序列化前；含输入核验、预算等待与阶段，不含 PowerShell 启动及最终收据写入；不是纯编译时间。'
$result['InputSnapshotChecks']=$inputScanMetrics
$result['InputSnapshotTiming']='所有 Get-InputSnapshot 尝试的累计墙钟，含 Git 枚举、祖先检查与完整内容读取；不含单独的产物扫描和时间戳读取。'
$permit = $null
$cleanupFailed = $false
try {
    if ($Scenario -ne 'Cold') { Assert-WarmArtifacts }
    $permit = Enter-TestResourceBudget
    $result['BudgetWaitMs'] = $permit.WaitMs
    foreach ($step in $steps) {
        if ((Get-InputSnapshot | ConvertTo-Json -Compress -Depth 4) -cne ($before | ConvertTo-Json -Compress -Depth 4)) { throw '阶段开始前输入变化；结果 invalid。' }
        if ($snapshots -and ((Get-ChangedSnapshotEvidence | ConvertTo-Json -Compress -Depth 5) -cne ($snapshots | ConvertTo-Json -Compress -Depth 5))) { throw '阶段开始前冻结快照变化。' }
        $argsForStep = @($step.Arguments) + @("-bl:$(Join-Path $run ($step.Name + '.binlog'))")
        $log=Join-Path $run ($step.Name+'.stdout.log')
        $watch=[Diagnostics.Stopwatch]::StartNew()
        $stageReceipt=$null
        try {
            Invoke-DevelopmentChild -Executable $dotnet -Arguments $argsForStep -WorkingDirectory $root -Log $log -TimeoutSeconds $TimeoutSeconds
            $stageReceipt=Get-Content -LiteralPath ($log+'.json') -Raw | ConvertFrom-Json -AsHashtable
            if (-not (Test-Path -LiteralPath (Join-Path $run ($step.Name+'.binlog')))) { throw '缺少阶段 binlog；结果 invalid。' }
            if (-not $stageReceipt.memory -or $stageReceipt.memory.samples -le 0) { throw '缺少自有进程树 RSS 观测；结果 invalid。' }
        } catch {
            if ($_.Exception.Data.Contains('ChildTerminationUnknown')) { $cleanupFailed=$true }
            throw
        } finally {
            if (-not $stageReceipt -and (Test-Path -LiteralPath ($log+'.json'))) { $stageReceipt=Get-Content -LiteralPath ($log+'.json') -Raw | ConvertFrom-Json -AsHashtable }
            $result.Steps += [ordered]@{ Name=$step.Name; Arguments=$argsForStep; ElapsedMs=$watch.ElapsedMilliseconds; ExitCode=$(if($stageReceipt){$stageReceipt.exitCode}else{$null}); SupervisorReceipt=($log+'.json'); State=$(if($stageReceipt){$stageReceipt.state}else{'not-started-or-unknown'}); DescendantsTerminated=$(if($stageReceipt){$stageReceipt.descendantsTerminated}else{$false}); PeakOwnedTreeRssBytes=$(if($stageReceipt){$stageReceipt.memory.sampledPeakRssBytes}else{$null}); Samples=$(if($stageReceipt){$stageReceipt.memory.samples}else{0}); MemoryMeasurement='shared Linux subreaper RSS observation; partial sampling, not hard peak'; Ownership=$(if($stageReceipt){$stageReceipt.ownership}else{$null}) }
        }

    }
    $result['InputsAfter'] = Get-InputSnapshot
    if (($result.InputsAfter | ConvertTo-Json -Compress -Depth 4) -cne ($before | ConvertTo-Json -Compress -Depth 4) -or (Get-EnvironmentHash) -ne $environmentHash -or (& $dotnet --info | Out-String) -cne $sdk) { throw '运行期间源码/环境/SDK变化；结果 invalid。' }
    if ($snapshots -and ((Get-ChangedSnapshotEvidence | ConvertTo-Json -Compress -Depth 5) -cne ($snapshots | ConvertTo-Json -Compress -Depth 5))) { throw '运行期间冻结快照变化；结果 invalid。' }
    if (@(Get-MeasurementDelta (Get-InputWriteTimes $before) $inputTimes).Count -ne 0) { throw '运行期间源码时间戳变化；结果 invalid。' }
    $result['ArtifactWriteTimesUtc']=Get-ArtifactWriteTimes
    $result['Artifacts'] = Get-ArtifactSnapshot
    if ($Scenario -ne 'Cold') {
        $result['ArtifactContentChanges']=@(Get-MeasurementDelta $prior.Artifacts $result.Artifacts)
        $result['ArtifactWriteTimeChanges']=if ($prior.Contains('ArtifactWriteTimesUtc')) { @(Get-MeasurementDelta $prior.ArtifactWriteTimesUtc $result.ArtifactWriteTimesUtc) } else { $null }
    }
    if ($result.Artifacts.Count -eq 0) { throw '缺少构建产物；结果 invalid。' }
    $result.Valid=$true
} catch {
    $result.Failure=$_.Exception.Message
    throw
} finally {
    $result['FinishedUtc']=[DateTime]::UtcNow.ToString('o')
    $result['EntryElapsedMs']=$entryWatch.ElapsedMilliseconds
    $result['ProcessCleanup'] = if ($cleanupFailed) { 'unconfirmed; permit retained' } elseif ($result.Steps.Count -eq 0) { 'not-started' } elseif (@($result.Steps | Where-Object { -not $_.DescendantsTerminated }).Count -eq 0) { 'all launched stages confirmed terminal; see supervisor receipts' } else { 'not-started or known prelaunch failure; see stage receipts' }
    $json=$result | ConvertTo-Json -Depth 10
    try {
        [IO.File]::WriteAllText((Join-Path $run 'measurement.json'),$json)
        [IO.File]::WriteAllText($receiptPath,$json)
    } finally {
        try { if ($permit -and -not $cleanupFailed) { Exit-TestResourceBudget -Permit $permit } } finally { $lock.Dispose() }
    }
}
$result | ConvertTo-Json -Depth 10
