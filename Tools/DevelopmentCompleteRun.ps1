# 完整计划复用严格入口；只记录阶段实际终态，不把成功退出提升为完整条件覆盖。
function Get-DevelopmentCompletePlan([string]$PvfPath, [string[]]$Groups = @()) {
    if (-not $PvfPath) { throw 'Complete 必须显式提供匹配 PvfPath；无资源检查不是完整计划。' }
    if (@($Groups | Where-Object { $_ -notmatch '^[a-z0-9-]+$' }).Count -or @($Groups | Select-Object -Unique).Count -ne $Groups.Count) { throw '额外定向组必须合法且不重复。' }
    $stages = @(
        @{Name='strict-development-resources';State='not-run';Scope=@('repository','build','architecture','pvflib','core','resources')},
        @{Name='default-tcp';State='not-run';Scope='runtime-default-groups-excluding-targeted-only'}
    )
    if ($Groups.Count) { $stages += @{Name='additional-selected-tcp';State='not-run';Scope=@($Groups)} }
    @{State='planned';Mode='Complete';Stages=$stages;Coverage='not-established';Cache='not-used';Deduplication='disabled-unproven-equivalence';OtherTargetedGroups='not-run';NativeClient='not-run'}
}

# 调用者持有唯一重型许可并负责后代终止/隔离；此函数不再次申请许可。
function Invoke-DevelopmentCompleteRun([string]$Repository, [string]$Run, [string]$Configuration,
    [string]$PvfPath, [string[]]$Groups, [hashtable]$CompilerEnvironment,
    [ValidateRange(1,86400)][int]$StageTimeoutSeconds = 21600) {
    $result = Get-DevelopmentCompletePlan $PvfPath $Groups
    if (Test-Path Env:DFO_SELFTEST_LUKE_RESTART_MANIFEST) { throw 'Complete 不能继承 core worker 模式。' }
    $pvf = Get-Item -LiteralPath $PvfPath -ErrorAction Stop
    if ($pvf.PSIsContainer -or $pvf.LinkType) { throw 'Complete 需要普通 PVF 文件。' }
    $PvfPath = $pvf.FullName
    # 只检查资源可见元数据，不计算整份 PVF 哈希，也不宣称证明了资源不可变闭包。
    $resourceStamp = "$($pvf.Length):$($pvf.LastWriteTimeUtc.Ticks)"
    $source = (Get-ResultSourceManifest $Repository).Key
    $output = Join-Path $Run 'artifacts'
    $runner = Join-Path $output ('bin/DfoServer/'+$Configuration.ToLowerInvariant()+'/DfoServer.dll')
    $package = $null
    $result.Path = $Run
    $result.ResourceIdentity = 'metadata-observation-only-not-cache-proof'
    $result.StageTimeoutSeconds = $StageTimeoutSeconds
    $record = Join-Path $Run 'complete-plan.json'
    function Save-CompleteState { [IO.File]::WriteAllText($record, ($result | ConvertTo-Json -Depth 20)) }
    Save-CompleteState
    try {
        foreach ($stage in $result.Stages) {
            $current = Get-Item -LiteralPath $PvfPath
            if ((Get-ResultSourceManifest $Repository).Key -cne $source -or "$($current.Length):$($current.LastWriteTimeUtc.Ticks)" -cne $resourceStamp -or ($package -and (Get-ProfileTreeDigest (Split-Path $runner -Parent)) -cne $package)) { throw '完整计划输入或产物变化，后续阶段未执行。' }
            $stage.State = 'running'; Save-CompleteState
            $log = Join-Path $Run ($stage.Name+'.log')
            if ($stage.Name -eq 'strict-development-resources') {
                $arguments = @('-NoProfile','-File',(Join-Path $Repository 'Tools/Check-Development.ps1'),'-Configuration',$Configuration,'-ArtifactsPath',$output,'-PvfPath',$PvfPath)
                Invoke-DevelopmentChild (Get-Command pwsh).Source $arguments $Repository $log $CompilerEnvironment -TimeoutSeconds $StageTimeoutSeconds
                $package = Get-ProfileTreeDigest (Split-Path $runner -Parent)
            } else {
                $arguments = @($runner,'--pvf-path',$PvfPath,'--selftest-tcp')
                if ($stage.Name -eq 'additional-selected-tcp') { $arguments += ($Groups -join ',') }
                $environment = @{DFO_TCP_PARALLEL='1';DFO_TEST_RECEIPTS_DIR=(Join-Path $Run ($stage.Name+'-receipts'));DFO_TEST_SOURCE_KEY=$source;DFO_TEST_INPUT_KEY='';DFO_TCP_SHARD_ROOT=''}
                Invoke-DevelopmentChild (Get-Command dotnet).Source $arguments $Repository $log $environment -TimeoutSeconds $StageTimeoutSeconds
                $stage.Receipts = $environment.DFO_TEST_RECEIPTS_DIR
            }
            $current = Get-Item -LiteralPath $PvfPath
            if ((Get-ResultSourceManifest $Repository).Key -cne $source -or "$($current.Length):$($current.LastWriteTimeUtc.Ticks)" -cne $resourceStamp -or (Get-ProfileTreeDigest (Split-Path $runner -Parent)) -cne $package) { throw '完整计划执行中输入或产物变化，不记录成功。' }
            $stage.State = 'executed'; $stage.Coverage = 'inspect-original-results-and-skips'; Save-CompleteState
        }
        $result.State='executed'; Save-CompleteState
        return $result
    } catch {
        $failure = $_
        if ($stage -and $stage.State -eq 'running') { $stage.State='failed-or-interrupted' }
        $result.State='failed'
        try { Save-CompleteState } catch { Write-Warning '无法更新完整计划终态；保留原执行错误及预算隔离标志。' }
        throw $failure
    }
}
