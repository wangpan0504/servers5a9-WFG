# 纯编排合同：替身不编译、不启动服务，也不推断真实测试覆盖。
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'DevelopmentCompleteRun.ps1')
$root=Join-Path ([IO.Path]::GetTempPath()) ('complete-contract-'+[guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root) | Out-Null
$pvf=Join-Path $root 'fixture.pvf'; [IO.File]::WriteAllText($pvf,'synthetic')
$checks=0
function Assert($Condition,$Message) { if (-not $Condition) { throw "FAIL $Message" }; $script:checks++ }
function Reject([scriptblock]$Action) { $failed=$false; try { & $Action | Out-Null } catch { $failed=$true }; Assert $failed '必须拒绝' }
function Get-ResultSourceManifest { @{Key=$script:source} }
function Get-ProfileTreeDigest { $script:package }
function dotnet {}
function Invoke-DevelopmentChild($Executable,$Arguments,$WorkingDirectory,$Log,$Environment,[int]$TimeoutSeconds) {
    $script:calls.Add(@{Arguments=$Arguments;Environment=$Environment;Timeout=$TimeoutSeconds})
    if ($script:calls.Count -eq $script:failAt) {
        $error=[InvalidOperationException]::new('synthetic-stage-failure'); $error.Data['ChildTerminationUnknown']=$true; throw $error
    }
    if ($script:calls.Count -eq $script:mutatePackageAt) { $script:package='changed-package' }
    if ($script:calls.Count -eq $script:mutateResourceAt) { [IO.File]::AppendAllText($pvf,'changed') }
    if ($script:calls.Count -eq $script:mutateAt) { $script:source='changed' }
}
function Run-Fixture([string[]]$Groups=@()) {
    $run=Join-Path $root ([guid]::NewGuid().ToString('N')); [IO.Directory]::CreateDirectory($run) | Out-Null
    $script:lastRun=$run
    Invoke-DevelopmentCompleteRun $root $run Debug $pvf $Groups @{CscEnvironment='synthetic'} 23456
}
$worker=[Environment]::GetEnvironmentVariable('DFO_SELFTEST_LUKE_RESTART_MANIFEST')
try {
    Remove-Item Env:DFO_SELFTEST_LUKE_RESTART_MANIFEST -ErrorAction SilentlyContinue
    Reject { Get-DevelopmentCompletePlan '' }
    Reject { Get-DevelopmentCompletePlan $pvf @('bad,name') }
    Reject { Get-DevelopmentCompletePlan $pvf @('same','same') }
    $plan=Get-DevelopmentCompletePlan $pvf
    Assert ($plan.Stages.Count -eq 2 -and $plan.Stages[1].Scope -eq 'runtime-default-groups-excluding-targeted-only') '默认TCP不冒充所有注册组'
    Assert ($plan.Coverage -eq 'not-established' -and $plan.OtherTargetedGroups -eq 'not-run') '计划无通过证据'
    $script:source='source'; $script:package='package'; $script:failAt=0; $script:mutateAt=0
    $script:calls=[Collections.Generic.List[object]]::new()
    $result=Run-Fixture @('chat-rate-rules')
    Assert ($calls.Count -eq 3 -and $result.State -eq 'executed') '完整串行三个阶段'
    Assert ($calls[0].Arguments -contains '-PvfPath' -and $calls[0].Arguments -notcontains '-Incremental') '严格资源入口不缓存'
    Assert ($calls[1].Arguments[-1] -eq '--selftest-tcp') '默认TCP不传不完整选择集'
    Assert ($calls[2].Arguments[-1] -eq 'chat-rate-rules') '额外定向独立阶段'
    Assert ($calls[1].Environment.DFO_TCP_PARALLEL -eq '1' -and $calls[1].Environment.DFO_TCP_SHARD_ROOT -eq '') '同许可串行且不继承分片子模式'
    Assert ($calls[0].Timeout -eq 23456 -and $calls[2].Timeout -eq 23456) '完整阶段显式超时'
    Assert ($result.Coverage -eq 'not-established' -and $result.Deduplication -eq 'disabled-unproven-equivalence') '成功退出不伪造覆盖或核销'
    foreach ($fail in @(1,2,3)) {
        $script:calls.Clear(); $script:failAt=$fail
        Reject { Run-Fixture @('chat-rate-rules') }
        $saved=Get-Content (Join-Path $lastRun 'complete-plan.json') -Raw | ConvertFrom-Json
        Assert ($calls.Count -eq $fail -and $saved.State -eq 'failed') '失败阻断后续'
        Assert ($saved.Stages[$fail-1].State -eq 'failed-or-interrupted') '原失败保留'
        if ($fail -lt 3) { Assert ($saved.Stages[$fail].State -eq 'not-run') '未执行阶段不变成失败或通过' }
    }
    $script:calls.Clear(); $script:failAt=0; $script:mutateAt=1
    Reject { Run-Fixture }
    Assert ($calls.Count -eq 1) '输入变更不继续TCP'
    $script:mutateAt=0; $script:source='source'; $script:calls.Clear(); $script:mutatePackageAt=2
    Reject { Run-Fixture }
    Assert ($calls.Count -eq 2) 'TCP期间产物变更拒绝终态'
    $script:mutatePackageAt=0; $script:calls.Clear(); $script:mutateResourceAt=1
    Reject { Run-Fixture }
    Assert ($calls.Count -eq 1) '资源元数据变化阻断TCP'
    $script:mutateResourceAt=0; $script:calls.Clear(); $script:failAt=1
    try { Run-Fixture } catch { Assert ($_.Exception.Data['ChildTerminationUnknown'] -eq $true) '后代未知异常保留隔离标记' }
    $script:calls.Clear(); [Environment]::SetEnvironmentVariable('DFO_SELFTEST_LUKE_RESTART_MANIFEST','worker')
    Reject { Run-Fixture }
    Assert ($calls.Count -eq 0) 'worker早退环境执行前拒绝'
    "PASS $checks complete orchestration contracts (synthetic; no build/server)"
} finally {
    [Environment]::SetEnvironmentVariable('DFO_SELFTEST_LUKE_RESTART_MANIFEST',$worker)
    Remove-Item -LiteralPath $root -Recurse -Force
}
