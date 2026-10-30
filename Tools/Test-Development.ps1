param(
    [ValidateSet('Plan','Record','Run')][string]$Action = 'Plan',
    [string]$Repository = (Split-Path $PSScriptRoot -Parent),
    [Parameter(Mandatory)][string]$Store,
    [string]$InputDescriptor,
    [ValidateSet('Dev','Full','Complete')][string]$Mode = 'Dev',
    [string[]]$Groups = @(), [string]$PvfPath, [string]$BudgetDirectory,
    [ValidateSet('Debug','Release')][string]$Configuration = 'Debug',
    [switch]$UseCache, [switch]$ForceFresh,
    [ValidateRange(0,[long]::MaxValue)][long]$StorageMaxBytes=0,
    [ValidateRange(0,[long]::MaxValue)][long]$StorageMaxFiles=0,
    [ValidateRange(0,86400)][int]$CacheWaitSeconds=600,
    [ValidateRange(1,86400)][int]$CompleteStageTimeoutSeconds=21600,
    [ValidateSet('Default','CloudLowMemory')][string]$BuildProfile='Default',
    [string]$BeforeManifest, [string]$Receipt, [string]$Runner, [string]$ManifestOutput
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'DevelopmentResultStore.ps1')
. (Join-Path $PSScriptRoot 'DevelopmentChatRateProfile.ps1')
. (Join-Path $PSScriptRoot 'DevelopmentSelectedRun.ps1')
. (Join-Path $PSScriptRoot 'DevelopmentCompleteRun.ps1')
try {
    $Repository = [IO.Path]::GetFullPath($Repository); $Store = [IO.Path]::GetFullPath($Store)
    if ($Action -eq 'Run' -and ($StorageMaxBytes -or $StorageMaxFiles)) {
        . (Join-Path $PSScriptRoot 'DevelopmentStorageBudget.ps1')
        $storage=Test-DevelopmentStorageBudget (Get-DevelopmentStorageInventory $Store @($Repository)) $StorageMaxBytes $StorageMaxFiles
        if($storage.State -eq 'blocked'){$storage|ConvertTo-Json -Depth 12;exit 2}
    }
    if ($Mode -eq 'Complete') {
        if ($InputDescriptor -or $UseCache -or $ForceFresh -or $Action -eq 'Record') { throw 'Complete不支持缓存或Record；使用严格实跑计划。' }
        $completePlan=Get-DevelopmentCompletePlan $PvfPath $Groups
        if($Action -eq 'Plan'){$completePlan|ConvertTo-Json -Depth 20;exit 0}
        if(-not (Get-Command Invoke-DevelopmentSelectedRun).Parameters.ContainsKey('CompleteStageTimeoutSeconds')){throw '完整执行监督接口未集成；禁止回退开发选组。'}
    }
    if ($Action -eq 'Run' -or (-not $InputDescriptor -and $Action -eq 'Plan')) {
        if ($Mode -eq 'Dev' -and ($Groups.Count -eq 0 -or @($Groups | Where-Object { $_ -notmatch '^[a-z0-9-]+$' }).Count)) { throw 'Dev需要显式、合法的Groups；未选范围不会执行。' }
        $profile = $UseCache -and $Mode -eq 'Dev' -and $Groups.Count -eq 1 -and (Get-DevelopmentRuleProfile $Groups[0]) -and -not $PvfPath
        if ($ForceFresh -and -not $profile) { throw 'ForceFresh要求UseCache及已审单组profile，用于同key重验；普通Dev本来实跑。' }
        $manifest = if ($profile) { Get-ChatRateProfileManifest $Repository $Configuration $BuildProfile $Groups[0] } else { $null }
        $plan = if ($manifest) { Get-DevelopmentResultPlan $Store $manifest $null $CacheWaitSeconds } else { @{State='run';Reason='explicit-selected-scope-no-cache'} }
        if ($ForceFresh -and $plan.State -ne 'blocked') { $plan=@{State='run';Reason='forced-same-input-revalidation'} }
        if ($manifest -and (Get-ChatRateProfileManifest $Repository $Configuration $BuildProfile $Groups[0]).Key -cne $manifest.Key) { throw '等待期间输入变化，需重新计划；不能复用旧key结果。' }
        if ($Action -eq 'Plan') {
            $plan.Mode=$Mode; $plan.Scope=$Groups; $plan.OtherScopes='not-run'
            if($Mode -eq 'Full'){$plan.Scope=@('repository','build','architecture','pvflib','core');if($PvfPath){$plan.Scope+='resources'};$plan.TCP='not-run';$plan.Coverage='not-established'}
            if ($manifest) { $plan.InputKey=$manifest.Key; $plan.ProfileReasons=$manifest.Reasons }
            $plan | ConvertTo-Json -Depth 30; if ($plan.State -eq 'blocked') { exit 2 }; exit 0
        }
        if ($plan.State -eq 'cached' -or $plan.State -eq 'blocked') { $plan | ConvertTo-Json -Depth 30; if ($plan.State -eq 'blocked') { exit 2 }; exit 0 }
        if ($manifest -and $manifest.InputClosureProven) {
            $result = Invoke-DevelopmentResultSingleFlight $Store $manifest {
                param($held)
                Invoke-DevelopmentSelectedRun $Repository $Store $Groups $Configuration $PvfPath $BudgetDirectory $Mode $manifest $held $BuildProfile $CompleteStageTimeoutSeconds
            } -WaitSeconds $CacheWaitSeconds -ValidateInputs { (Get-ChatRateProfileManifest $Repository $Configuration $BuildProfile $Groups[0]).Key -ceq $manifest.Key } -ForceExecute:$ForceFresh
        } else { $result = Invoke-DevelopmentSelectedRun $Repository $Store $Groups $Configuration $PvfPath $BudgetDirectory $Mode $null $null $BuildProfile $CompleteStageTimeoutSeconds }
        $result | ConvertTo-Json -Depth 30; if ($result.State -eq 'blocked') { exit 2 }; exit 0
    }
    $descriptor = Get-Content -LiteralPath $InputDescriptor -Raw | ConvertFrom-Json -AsHashtable
    # CLI不能把调用者手填的complete标志提升为依赖证明；生产profile尚未审计时只给实跑计划。
    if ($descriptor.Closure -ne 'observed-inputs-v1') { throw 'CLI只接受真实Capture输出；手填完整闭包不能授权缓存复用。' }
    $manifest = Get-ResultInputManifest $Repository $InputDescriptor
    if ($ManifestOutput) {
        [IO.File]::WriteAllText([IO.Path]::GetFullPath($ManifestOutput), (ConvertTo-ResultCanonical $manifest), [Text.UTF8Encoding]::new($false))
    }
    if ($Action -eq 'Plan') { $result = Get-DevelopmentResultPlan $Store $manifest $null $CacheWaitSeconds }
    else {
        if (-not $BeforeManifest -or -not $Receipt -or -not $Runner) { throw 'Record需要执行前manifest、实际receipt与runner。' }
        $before = Get-Content -LiteralPath $BeforeManifest -Raw | ConvertFrom-Json -AsHashtable
        $result = Save-DevelopmentResult $Store $before $manifest $Receipt $Runner
    }
    if ($result.State -eq 'run') {
        $check = @('-NoProfile','-File',(Join-Path $Repository 'Tools/Check-Development.ps1'), '-Configuration', $descriptor.Configuration, '-ArtifactsPath', $descriptor.ArtifactsRoot)
        $tcp = @((Join-Path $descriptor.ArtifactDirectory 'DfoServer.dll'))
        $pvf = @($descriptor.Resources | Where-Object { $_.Name -ceq 'Script.pvf' })
        if ($pvf.Count -eq 1) { $check += @('-PvfPath', $pvf[0].Path); $tcp += @('--pvf-path', $pvf[0].Path) }
        $tcp += @('--selftest-tcp', ($manifest.Inputs.Scope -join ','))
        $result.Commands = @(@{ Executable='pwsh'; Arguments=$check }, @{ Executable='dotnet'; Arguments=$tcp })
        $result.Note = '仅计划，未运行；严格入口成功并核对实际runner后再执行定向TCP。'
    }
    $result.InputKey = $manifest.Key; $result.SourceKey = $manifest.Source; $result.Scope = $manifest.Inputs.Scope
    $result | ConvertTo-Json -Depth 30
    if ($result.State -eq 'blocked' -or ($Action -eq 'Record' -and $result.State -ne 'recorded')) { exit 2 }
} catch {
    @{ State = $(if ($Action -eq 'Run') { 'failed' } else { 'blocked' }); Reason = $(if ($Action -eq 'Run') { 'execution-or-precondition-failed-see-stage-log' } else { 'input-or-store-not-verifiable' }); Detail = $_.Exception.Message } | ConvertTo-Json -Depth 5
    exit 2
}
