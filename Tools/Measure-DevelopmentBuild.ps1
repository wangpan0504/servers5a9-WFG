<#
.SYNOPSIS
生成隔离构建测量计划；只有显式批准窗口才执行，不代表已证明提速。
.DESCRIPTION
Cold 使用新目录；WarmUnchanged 复用同一目录的成功基线。
WarmChangedTest 尚未实现双冻结快照，仅计划可用，执行会拒绝。工具不改源码。
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
    [string[]]$AdditionalInputs = @(),
    [switch]$InputsComplete,
    [switch]$Execute,
    [string]$ApprovedWindow,
    [ValidateRange(1,86400)][int]$TimeoutSeconds = 1200,
    [string]$DotnetPath = 'dotnet'
)
$ErrorActionPreference = 'Stop'
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
$common = @('-nologo','-m:1','-nr:false',"-p:Configuration=$Configuration", "-p:ArtifactsPath=$artifacts",'-p:UseArtifactsOutput=true','-p:UseSharedCompilation=false','-p:RunAnalyzers=true','-p:RunAnalyzersDuringBuild=true')
if ($Profile -ne 'Default') { $common += '-p:CscEnvironment=DOTNET_PROCESSOR_COUNT=' + $Profile.Substring($Profile.Length - 1) }
$steps = @(
    [ordered]@{ Name='Restore'; Arguments=@('msbuild',$project,'-t:Restore') + $common },
    [ordered]@{ Name='EvaluationProbe'; Arguments=@('msbuild',$project,'-getProperty:TargetFramework') + $common },
    [ordered]@{ Name='BuildNoRestore'; Arguments=@('build',$project,'--no-restore') + $common }
)
$plan = [ordered]@{ Schema=1; Mode='plan-only'; Scenario=$Scenario; Profile=$Profile; Configuration=$Configuration; SourceRoot=$root; ArtifactsPath=$artifacts; ColdDefinition='fresh bin/obj only; OS and NuGet caches are not cleared'; Steps=$steps; CompilerTiming='Read Csc/CoreCompile from binlog; BuildNoRestore is not compiler-only'; Memory='sampled owned process-tree RSS on Linux; unknown elsewhere'; InputsComplete=[bool]$InputsComplete }
if ($Scenario -eq 'WarmChangedTest') { $plan['BlockedReason'] = '尚未实现双冻结快照；不修改开发工作树，当前仅生成计划。' }
if (-not $Execute) { $plan | ConvertTo-Json -Depth 8; return }
if ($Scenario -eq 'WarmChangedTest') { throw $plan.BlockedReason }
if (-not $IsLinux) { throw '当前仅验证 Linux 进程树采样；其它平台只支持计划，不生成可比较测量。' }
if ([string]::IsNullOrWhiteSpace($ApprovedWindow)) { throw '执行必须提供负责人批准的 -ApprovedWindow；参数本身不能代替授权。' }
if (-not $InputsComplete) { throw '缺少完整输入确认；拒绝执行不可比较的大构建。' }
$budgetModule = Join-Path $PSScriptRoot 'TestResourceBudget.psm1'
if (-not (Test-Path -LiteralPath $budgetModule)) { throw '缺少 TestResourceBudget.psm1；不能绕过跨工作树预算。' }
Import-Module $budgetModule
function Get-InputSnapshot {
    $paths = @(& git -C $root -c core.quotepath=false ls-files --cached --others --exclude-standard)
    if ($LASTEXITCODE -ne 0 -or $paths.Count -eq 0) { throw '无法完整枚举源码输入。' }
    $files = [ordered]@{}
    foreach ($path in ($paths | Sort-Object -Unique)) {
        if ($path -match '(?i)\.pvf$') { throw '构建输入不允许包含 PVF；不计算整包哈希。' }
        $full = Join-Path $root $path
        if (-not (Test-Path -LiteralPath $full -PathType Leaf)) { throw "缺失输入：$path" }
        if ((Get-Item -LiteralPath $full -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "输入不能是链接：$path" }
        $files[$path] = (Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash
    }
    foreach ($path in ($AdditionalInputs | Sort-Object -Unique)) {
        $full = [IO.Path]::GetFullPath($path,$root)
        if ($full -match '(?i)\.pvf$') { throw '不允许对 PVF 计算整包哈希。' }
        $files["external:$full"] = (Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash
    }
    return $files
}
function Get-ArtifactSnapshot {
    $files = [ordered]@{}
    foreach ($name in @('bin','obj')) {
        $directory = Join-Path $artifacts $name
        if (-not (Test-Path -LiteralPath $directory)) { continue }
        if ((Get-Item -LiteralPath $directory -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'bin/obj 根不能是链接。' }
        foreach ($item in (Get-ChildItem -LiteralPath $directory -Recurse -Force | Sort-Object FullName)) {
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw '产物不能包含链接。' }
            if (-not $item.PSIsContainer) { $files[[IO.Path]::GetRelativePath($artifacts,$item.FullName)] = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash }
        }
    }
    return $files
}
function Get-EnvironmentHash {
    $text = (Get-ChildItem Env: | Sort-Object Name | ForEach-Object { $_.Name + '=' + $_.Value }) -join "`n"
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($text)))
}
$dotnet = (Get-Command $DotnetPath -CommandType Application -ErrorAction Stop).Source
$sdk = (& $dotnet --info | Out-String)
if ($LASTEXITCODE -ne 0) { throw '无法读取 SDK 身份。' }
$before = Get-InputSnapshot
$environmentHash = Get-EnvironmentHash
$receiptPath = Join-Path $artifacts 'measurement.json'
if ($Scenario -ne 'Cold') {
    if (-not (Test-Path -LiteralPath $receiptPath)) { throw 'Warm 场景缺少同目录基线。' }
    $prior = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json -AsHashtable
    if (-not $prior.Valid -or $prior.SourceRoot -ne $root -or $prior.Profile -ne $Profile -or $prior.Configuration -ne $Configuration -or $prior.Sdk -cne $sdk -or $prior.EnvironmentHash -ne $environmentHash -or $prior.DotnetPath -ne $dotnet) { throw 'Warm 基线不是同源码目录/SDK/配置/环境/profile的有效结果。' }
    if ((Get-ArtifactSnapshot | ConvertTo-Json -Compress -Depth 4) -cne ($prior.Artifacts | ConvertTo-Json -Compress -Depth 4)) { throw 'Warm 产物缺失或已被其它构建修改。' }
    $delta = @(@($before.Keys) + @($prior.Inputs.Keys) | Sort-Object -Unique | Where-Object { $before[$_] -ne $prior.Inputs[$_] })
    if ($Scenario -eq 'WarmUnchanged' -and $delta.Count -ne 0) { throw 'WarmUnchanged 输入发生变化。' }

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
$result = [ordered]@{ Schema=1; ToolHash=(Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash; InputEvidence='declared source and additional inputs; not an automatic consumed-dependency closure'; Valid=$false; SourceRoot=$root; SourceCommit=(& git -C $root rev-parse HEAD); Scenario=$Scenario; Profile=$Profile; Configuration=$Configuration; DotnetPath=$dotnet; Sdk=$sdk; EnvironmentHash=$environmentHash; Inputs=$before; ApprovedWindow=$ApprovedWindow; StartedUtc=[DateTime]::UtcNow.ToString('o'); Steps=@(); Failure=$null; CompilerTiming=$plan.CompilerTiming }
$permit = $null
$cleanupFailed = $false
try {
    if ($Scenario -ne 'Cold' -and (Get-ArtifactSnapshot | ConvertTo-Json -Compress -Depth 4) -cne ($prior.Artifacts | ConvertTo-Json -Compress -Depth 4)) { throw '取得锁前产物发生变化。' }
    $permit = Enter-TestResourceBudget
    $result['BudgetWaitMs'] = $permit.WaitMs
    foreach ($step in $steps) {
        if ((Get-InputSnapshot | ConvertTo-Json -Compress -Depth 4) -cne ($before | ConvertTo-Json -Compress -Depth 4)) { throw '阶段开始前输入变化；结果 invalid。' }
        $argsForStep = @($step.Arguments) + @("-bl:$(Join-Path $run ($step.Name + '.binlog'))")
        $info = [Diagnostics.ProcessStartInfo]::new($dotnet)
        $info.WorkingDirectory=$root; $info.UseShellExecute=$false; $info.RedirectStandardOutput=$true; $info.RedirectStandardError=$true
        foreach ($arg in $argsForStep) { $info.ArgumentList.Add($arg) }
        $process = [Diagnostics.Process]::new(); $process.StartInfo=$info
        $out = [IO.File]::Create((Join-Path $run ($step.Name + '.stdout.log')))
        $err = [IO.File]::Create((Join-Path $run ($step.Name + '.stderr.log')))
        $watch = [Diagnostics.Stopwatch]::StartNew()
        $peak = 0L; $samples = 0; $samplingComplete = $IsLinux; $started=$false; $observed=@{}
        try {
            if (-not $process.Start()) { throw '启动构建失败。' }; $started=$true; $processStartUtc=$process.StartTime.ToUniversalTime().ToString('o')
            $copyOut=$process.StandardOutput.BaseStream.CopyToAsync($out); $copyErr=$process.StandardError.BaseStream.CopyToAsync($err)
            # 不匹配进程名；只采样本次根 PID 的后代。不宣称瞬时峰值或硬内存限制。
            while (-not $process.HasExited) {
                if ($watch.Elapsed.TotalSeconds -gt $TimeoutSeconds) { $process.Kill($true); if (-not $process.WaitForExit(5000)) { $cleanupFailed=$true }; throw '构建阶段超时；已请求停止本次进程树，详情以回收状态为准。' }
                if ($IsLinux) {
                    $rows=@{}
                    foreach ($dir in [IO.Directory]::EnumerateDirectories('/proc')) {
                        if ([IO.Path]::GetFileName($dir) -notmatch '^\d+$') { continue }
                        try {
                            $status=[IO.File]::ReadAllText((Join-Path $dir 'status'))
                            if ($status -match '(?m)^PPid:\s+(\d+)') { $parentId=[int]$Matches[1] } else { continue }
                            $rss=0L; if ($status -match '(?m)^VmRSS:\s+(\d+)') { $rss=[long]$Matches[1]*1024 }
                            $rows[[int][IO.Path]::GetFileName($dir)]=@{ Parent=$parentId; Rss=$rss }
                        } catch [IO.IOException] { } catch [UnauthorizedAccessException] { $samplingComplete=$false }
                    }
                    $owned=[Collections.Generic.HashSet[int]]::new(); [void]$owned.Add($process.Id)
                    do { $added=$false; foreach ($id in $rows.Keys) { if ($owned.Contains($rows[$id].Parent) -and $owned.Add($id)) { $added=$true } } } while ($added)
                    foreach ($id in $owned) {
                        if ($id -eq $process.Id -or $observed.ContainsKey($id)) { continue }
                        try { $child=[Diagnostics.Process]::GetProcessById($id); $observed[$id]=@{ Process=$child; StartTicks=$child.StartTime.ToUniversalTime().Ticks } } catch { }
                    }
                    $total=0L; foreach ($id in $owned) { if ($rows.ContainsKey($id)) { $total += $rows[$id].Rss } }
                    if ($rows.ContainsKey($process.Id) -and $rows[$process.Id].Rss -gt 0 -and -not $process.HasExited) { $peak=[Math]::Max($peak,$total); $samples++ }
                }
                [void]$process.WaitForExit(100)
            }
            $process.WaitForExit(); if (-not [Threading.Tasks.Task]::WaitAll(@($copyOut,$copyErr),5000)) { throw '子进程输出未关闭；停止本次已观察子进程。' }; [void]$copyOut.GetAwaiter().GetResult(); [void]$copyErr.GetAwaiter().GetResult()
            $record=[ordered]@{ Name=$step.Name; Arguments=$argsForStep; ProcessId=$process.Id; ProcessStartUtc=$processStartUtc; ObservedDescendants=$observed.Count; ExitCode=$process.ExitCode; ElapsedMs=$watch.ElapsedMilliseconds; PeakOwnedTreeRssBytes=$(if ($samples -gt 0) { $peak } else { $null }); Samples=$samples; MemoryMeasurement=$(if ($samplingComplete -and $samples -gt 0) { 'sampled-linux-proc-tree-rss; not an instantaneous peak or hard limit' } else { 'unknown/incomplete' }) }
            $result.Steps += $record
            if (-not (Test-Path -LiteralPath (Join-Path $run ($step.Name + '.binlog')))) { throw '缺少阶段 binlog；结果 invalid。' }
            if ($process.ExitCode -ne 0) { throw "阶段失败：$($step.Name)" }
            if (-not $samplingComplete -or $samples -eq 0) { throw '进程树内存证据不完整；结果 invalid。' }
        } catch {
            if ($result.Steps.Count -eq 0 -or $result.Steps[-1].Name -ne $step.Name) {
                $result.Steps += [ordered]@{ Name=$step.Name; Arguments=$argsForStep; ElapsedMs=$watch.ElapsedMilliseconds; Failure=$_.Exception.Message; PeakOwnedTreeRssBytes=$(if ($samples -gt 0) { $peak } else { $null }); Samples=$samples; MemoryMeasurement='partial; failed stage' }
            }
            throw
        } finally {
            try { if ($started -and -not $process.HasExited) { $process.Kill($true); if (-not $process.WaitForExit(5000)) { $cleanupFailed=$true } } } catch { $cleanupFailed=$true; Write-Warning ('根进程回收未确认：' + $_.Exception.Message) }
            foreach ($entry in $observed.Values) {
                $child=$entry.Process
                try { if (-not $child.HasExited -and $child.StartTime.ToUniversalTime().Ticks -eq $entry.StartTicks) { $child.Kill($true); if (-not $child.WaitForExit(5000)) { $cleanupFailed=$true } } } catch { $cleanupFailed=$true; Write-Warning ('无法回收本次子进程：' + $_.Exception.Message) } finally { $child.Dispose() }
            }
            $out.Dispose(); $err.Dispose(); $process.Dispose()
        }
        if ($cleanupFailed) { throw '本次子进程回收未确认；保留预算占用，禁止报告成功。' }
    }
    $result['InputsAfter'] = Get-InputSnapshot
    if (($result.InputsAfter | ConvertTo-Json -Compress -Depth 4) -cne ($before | ConvertTo-Json -Compress -Depth 4) -or (Get-EnvironmentHash) -ne $environmentHash -or (& $dotnet --info | Out-String) -cne $sdk) { throw '运行期间源码/环境/SDK变化；结果 invalid。' }
    $result['Artifacts'] = Get-ArtifactSnapshot
    if ($result.Artifacts.Count -eq 0) { throw '缺少构建产物；结果 invalid。' }
    $result.Valid=$true
} catch {
    $result.Failure=$_.Exception.Message
    throw
} finally {
    $result['FinishedUtc']=[DateTime]::UtcNow.ToString('o')
    $result['ProcessCleanup'] = if ($cleanupFailed) { 'unconfirmed; permit retained' } else { 'observed processes exited' }
    $json=$result | ConvertTo-Json -Depth 10
    try {
        [IO.File]::WriteAllText((Join-Path $run 'measurement.json'),$json)
        [IO.File]::WriteAllText($receiptPath,$json)
    } finally {
        try { if ($permit -and -not $cleanupFailed) { Exit-TestResourceBudget -Permit $permit } } finally { $lock.Dispose() }
    }
}
$result | ConvertTo-Json -Depth 10
