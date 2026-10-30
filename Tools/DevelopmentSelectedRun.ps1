# 显式开发范围执行；不把未选择的范围记为通过。进程结束前不归还共享重型许可。
. (Join-Path $PSScriptRoot 'DevelopmentArtifactWorkspace.ps1')
function Invoke-DevelopmentChild([string]$Executable, [string[]]$Arguments, [string]$WorkingDirectory, [string]$Log, [hashtable]$Environment = @{}, [string]$SupervisorPath = (Join-Path $PSScriptRoot 'Run-DevelopmentChild.py'), [ValidateRange(1,86400)][int]$TimeoutSeconds=1800) {
    if (-not $IsLinux) { throw '受控后代生命周期当前仅支持Linux；其他平台请用原严格入口。' }
    if (Test-Path -LiteralPath ($Log+'.json')) { throw '监督收据路径已存在；必须使用新的运行路径，不能沿用旧终态。' }
    $helper = $SupervisorPath
    if (-not (Test-Path -LiteralPath $helper)) { throw '缺少受控子进程监督器，未启动任务。' }
    $info = [Diagnostics.ProcessStartInfo]::new((Get-Command python3 -ErrorAction Stop).Source)
    $info.WorkingDirectory=$WorkingDirectory; $info.UseShellExecute=$false
    foreach ($arg in @($helper,'--working-directory',$WorkingDirectory,'--log',$Log,'--receipt',($Log+'.json'),'--timeout-seconds',[string]$TimeoutSeconds,'--max-log-bytes','16777216','--',$Executable)+$Arguments) { $info.ArgumentList.Add($arg) }
    foreach ($key in $Environment.Keys) { $info.Environment[$key]=$Environment[$key] }
    $process = [Diagnostics.Process]::new(); $process.StartInfo=$info; $launched=$false
    try {
        if (-not $process.Start()) { throw '无法启动受控子进程监督器。' }; $launched=$true
        $process.WaitForExit()
    } finally {
        if ($launched) {
            $confirmed=$false
            try {
                if ($process.HasExited -and (Test-Path -LiteralPath ($Log+'.json'))) {
                    $receipt=Get-Content -LiteralPath ($Log+'.json') -Raw | ConvertFrom-Json -AsHashtable
                    $confirmed=$receipt.descendantsTerminated -is [bool] -and $receipt.descendantsTerminated -eq $true
                }
            } catch { $confirmed=$false }
            if (-not $confirmed) {
                $error=[InvalidOperationException]::new('子进程/后代终态无法确认；保留预算隔离，不启动后续任务。')
                $error.Data['ChildTerminationUnknown']=$true
                throw $error
            }
        }
    }
    try {
        if ($process.ExitCode -ne 0 -or $receipt.state -cne 'completed' -or $receipt.exitCode -ne 0) { throw "选定检查失败：state=$($receipt.state)，日志=$Log" }
    } finally { $process.Dispose() }
}
function Invoke-DevelopmentSelectedRun([string]$Repository, [string]$Store, [string[]]$Groups, [string]$Configuration, [string]$PvfPath, [string]$BudgetDirectory, [string]$Mode, $Manifest = $null, [IO.FileStream]$HeldLock = $null, [string]$BuildProfile = 'Default', [ValidateRange(1,86400)][int]$CompleteStageTimeoutSeconds=21600) {
    $module = Join-Path $PSScriptRoot 'TestResourceBudget.psm1'
    if (-not (Test-Path -LiteralPath $module)) { throw '缺少共享资源预算模块，不能无许可启动。' }
    if (-not $IsLinux) { throw 'Run当前只支持有后代监督的Linux，其他平台使用原严格入口。' }
    Import-Module $module
    $compilerEnvironment = Get-DevelopmentCompilerEnvironment $BuildProfile
    $permit = if ([string]::IsNullOrEmpty($BudgetDirectory)) { Enter-TestResourceBudget } else { Enter-TestResourceBudget -BudgetDirectory $BudgetDirectory }
    $release = $true; $artifactLock=$null
    try {
        $run = Join-Path $Store ('runs/'+[guid]::NewGuid().ToString('N')); [IO.Directory]::CreateDirectory($run) | Out-Null
        if ($Mode -eq 'Complete') { return (Invoke-DevelopmentCompleteRun $Repository $run $Configuration $PvfPath $Groups $compilerEnvironment $CompleteStageTimeoutSeconds) }
        $workspace = if ($Mode -eq 'Dev') { Get-DevelopmentArtifactWorkspace $Repository $Configuration $BuildProfile } else { $null }
        $output = if ($workspace) { $workspace.Path } else { Join-Path $run 'artifacts' }
        if ($workspace) {
            $artifactLock = Enter-DevelopmentArtifactLock $output
            if(-not (Test-DevelopmentArtifactWorkspaceUnchanged $Repository $Configuration $BuildProfile $workspace)){throw '等待产物锁期间构建设置变化，需重新计划。'}
            [IO.File]::WriteAllText((Join-Path $run 'build-workspace.json'),(ConvertTo-ResultCanonical $workspace))
        }
        $source = Get-ResultSourceManifest $Repository
        if ($Mode -eq 'Full') {
            $args = @('-NoProfile','-File',(Join-Path $Repository 'Tools/Check-Development.ps1'),'-Configuration',$Configuration,'-ArtifactsPath',$output)
            if ($PvfPath) { $args += @('-PvfPath',$PvfPath) }
            Invoke-DevelopmentChild (Get-Command pwsh).Source $args $Repository (Join-Path $run 'full.log') $compilerEnvironment
            return @{State='executed';Mode='Full';Scope='Check-Development';TCP='not-run';Path=$run}
        }
        Invoke-DevelopmentChild (Get-Command pwsh).Source @('-NoProfile','-File',(Join-Path $Repository 'Tools/Check-Repository.ps1'),'-Repository',$Repository) $Repository (Join-Path $run 'repository.log')
        $buildInputs=Get-DevelopmentBuildInputs $Repository $workspace.FullEnvironmentDigest
        $incremental=Start-DevelopmentIncrementalBuild $output $buildInputs
        [IO.File]::WriteAllText((Join-Path $run 'incremental-decision.json'),(ConvertTo-ResultCanonical $incremental))
        $buildArgs=@('build',(Join-Path $Repository 'ServerS5A9.sln'),'-c',$Configuration,'--nologo','--artifacts-path',$output,'-m:1','-nr:false','-p:UseSharedCompilation=false','-p:RunAnalyzers=true')
        if($incremental.Rebuild){$buildArgs+='-t:Rebuild'}
        Invoke-DevelopmentChild (Get-Command dotnet).Source $buildArgs $Repository (Join-Path $run 'build.log') $compilerEnvironment
        if(-not (Test-DevelopmentArtifactWorkspaceUnchanged $Repository $Configuration $BuildProfile $workspace)){throw '构建期间实际设置/依赖变化，不能声明该输入已完成。'}
        if ((Get-ResultSourceManifest $Repository).Key -cne $source.Key) { throw '构建期间源码变化，不能执行或缓存混合版本。' }
        Complete-DevelopmentIncrementalBuild $output $buildInputs
        $runner = Join-Path $output ('bin/DfoServer/'+$Configuration.ToLowerInvariant()+'/DfoServer.dll')
        $artifactBefore = Get-ProfileTreeDigest (Split-Path $runner -Parent)
        $args = @($runner)
        if ($PvfPath) { $args += @('--pvf-path',$PvfPath) }
        $args += @('--selftest-tcp',($Groups -join ','))
        $environment = @{DFO_TCP_PARALLEL='1';DFO_TEST_RECEIPTS_DIR=(Join-Path $run 'receipts');DFO_TCP_SHARD_ROOT=(Join-Path $run 'fixtures');DFO_TEST_SOURCE_KEY=$source.Key;DFO_TEST_INPUT_KEY=''}
        if ($Manifest) {
            $before = Get-ChatRateProfileManifest $Repository $Configuration $BuildProfile $Groups[0]
            if ($before.Key -cne $Manifest.Key) { throw '构建改变了缓存输入（例如首次还原包）；本次不运行缓存profile，请重新生成计划。' }
            $environment.DFO_TEST_INPUT_KEY=$Manifest.Key
        }
        Invoke-DevelopmentChild (Get-Command dotnet).Source $args $Repository (Join-Path $run 'selected.log') $environment
        if ((Get-ResultSourceManifest $Repository).Key -cne $source.Key -or (Get-ProfileTreeDigest (Split-Path $runner -Parent)) -cne $artifactBefore) { throw '执行期间源码或运行包变化，结果不可复用。' }
        if(-not (Test-DevelopmentArtifactWorkspaceUnchanged $Repository $Configuration $BuildProfile $workspace)){throw '执行期间实际构建设置变化，结果不可复用。'}
        [IO.File]::WriteAllText((Join-Path $run 'artifacts.json'),(ConvertTo-ResultCanonical @{Runner=(Get-ResultFileDigest $runner);Package=$artifactBefore;Source=$source.Key}))
        if ($Manifest) {
            $after = Get-ChatRateProfileManifest $Repository $Configuration $BuildProfile $Groups[0]
            $receipts = @(Get-ChildItem -LiteralPath $environment.DFO_TEST_RECEIPTS_DIR -Recurse -File -Filter '*.json')
            if ($receipts.Count -ne 1) { throw '真实单组执行收据数量不符。' }
            return Save-DevelopmentResult $Store $Manifest $after $receipts[0].FullName $runner $HeldLock $artifactBefore
        }
        @{State='executed';Mode='Dev';Scope=$Groups;OtherScopes='not-run';Cache='not-used';Path=$run}
    } catch {
        if ($_.Exception.Data.Contains('ChildTerminationUnknown')) { $release = $false }
        throw
    } finally {
        if ($artifactLock) { Exit-DevelopmentArtifactLock $artifactLock $release }
        if ($release) { Exit-TestResourceBudget -Permit $permit }
    }
}
