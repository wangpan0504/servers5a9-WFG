param(
    [string]$Repository=(Split-Path $PSScriptRoot -Parent),
    [Parameter(Mandatory)][string]$Store,
    [Parameter(Mandatory)][string]$ReceiptDirectory,
    [string]$Runner,
    [ValidateSet('Default','Targeted','Survey','All','Explicit')][string]$Scope='Default',
    [string[]]$Groups=@(),
    [ValidateSet('Debug','Release')][string]$Configuration='Debug',
    [ValidateSet('Default','CloudLowMemory')][string]$BuildProfile='Default',
    [string]$PvfPath, [string]$BudgetDirectory, [switch]$Run
)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'DevelopmentResumePlan.ps1')
$attempted=$false
try {
    $plan=Get-DevelopmentResumePlan $Repository $Store $ReceiptDirectory $Runner $Scope $Groups $Configuration $BuildProfile $PvfPath
    $plan | ConvertTo-Json -Depth 30
    if (-not $Run -or $plan.Remaining.Count -eq 0) { exit 0 }
    # 执行前重新采集，不能把先前输出的JSON或手填hash当作跳过授权。
    $fresh=Get-DevelopmentResumePlan $Repository $Store $ReceiptDirectory $Runner $Scope $Groups $Configuration $BuildProfile $PvfPath
    if ((ConvertTo-ResultCanonical $fresh) -cne (ConvertTo-ResultCanonical $plan)) { throw '计划输入或收据变化，请重新计划。' }
    $args=@{Action='Run';Mode='Dev';Repository=$Repository;Store=$Store;Groups=$fresh.Remaining;Configuration=$Configuration;BuildProfile=$BuildProfile}
    if ($PvfPath) { $args.PvfPath=$PvfPath }; if ($BudgetDirectory) { $args.BudgetDirectory=$BudgetDirectory }
    # 不嵌套申请permit；实际构建/测试及预算生命周期统一交给现有入口。
    $attempted=$true
    & (Join-Path $PSScriptRoot 'Test-Development.ps1') @args
    exit $LASTEXITCODE
} catch { @{State=$(if ($attempted) {'failed'} else {'blocked'});Execution=$(if ($attempted) {'attempted-see-stage-receipts'} else {'not-run'});Reason=$_.Exception.Message} | ConvertTo-Json; exit 2 }
