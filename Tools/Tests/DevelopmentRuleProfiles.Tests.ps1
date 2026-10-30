$ErrorActionPreference='Stop'
. (Join-Path (Split-Path $PSScriptRoot -Parent) 'DevelopmentResumePlan.ps1')
$count=0
function Assert($condition,[string]$message) { if (-not $condition) { throw $message }; $script:count++ }
function Copy-Value($value) { ConvertTo-ResultCanonical $value | ConvertFrom-Json -AsHashtable }
$runner='b'*64
$names=@('chat-rate-rules','equipment-power-rules','frame-crc-rules')
function New-Receipt($name,$manifest,$runner) {
    $profile=Get-DevelopmentRuleProfile $name
    @{SchemaVersion=2;Execution='executed';Status='passed';Passed=$true;Coverage='complete';Skips=@();CacheEligible=$true;Scope='group';Group=$name;RunId=('d'*32);StartedUtc='2026-01-01T00:00:00Z';CompletedUtc='2026-01-01T00:00:01Z';SelectedGroups=@($name);Identity=@{Source=$manifest.Source;Input=$manifest.Key;Runner=$runner};Profile=$profile.Name;Assertions=$profile.Assertions;SharedResourcesLoaded=$false}
}
foreach ($name in $names) {
    $profile=Get-DevelopmentRuleProfile $name
    $manifest=@{Source=('a'*64);Key=('c'*64);InputClosureProven=$true;Inputs=[ordered]@{Profile=$profile.Name;Scope=@($name)}}
    $receipt=New-Receipt $name $manifest $runner
    $selected=@(@{Name=$name;Kind='targeted'})
    function Decision($r,$m=$manifest) {
        @(Get-DevelopmentResumeDecisions $selected @(@{Receipt=$r;Path='synthetic';Digest=('e'*64)}) $m $runner @{Receipt=$r})[0]
    }
    Assert (Test-ResultReceipt $receipt $manifest $runner) '明确白名单完整单组收据可消费。'
    Assert ((Decision $receipt).State -ceq 'covered') '匹配profile可核销。'
    foreach ($state in @('failed','running','interrupted','unknown')) {
        $bad=Copy-Value $receipt; $bad.Status=$state
        Assert (-not (Test-ResultReceipt $bad $manifest $runner)) 'Passed不能覆盖失败或未知终态。'
        Assert ((Decision $bad).State -ceq 'remaining') '取消或失败保留续跑。'
    }
    foreach ($coverage in @('partial','unknown')) {
        $bad=Copy-Value $receipt; $bad.Coverage=$coverage
        Assert ((Decision $bad).State -ceq 'remaining') '覆盖不全不核销。'
    }
    foreach ($field in @('Source','Input','Runner')) {
        $bad=Copy-Value $receipt; $bad.Identity[$field]='f'*64
        Assert ((Decision $bad).State -ceq 'remaining') '完整输入身份不可挪用。'
    }
    foreach ($field in @('Profile','Assertions','SharedResourcesLoaded')) {
        $bad=Copy-Value $receipt; $bad.Remove($field)
        Assert (-not (Test-ResultReceipt $bad $manifest $runner)) '缺字段不得借旧版通过。'
    }
    $bad=Copy-Value $receipt; $bad.Assertions++
    Assert ((Decision $bad).State -ceq 'remaining') '断言数量漂移拒绝。'
    $bad=Copy-Value $receipt; $bad.SharedResourcesLoaded=$true
    Assert ((Decision $bad).State -ceq 'remaining') '意外PVF加载拒绝。'
    $bad=Copy-Value $receipt; $bad.Profile='other-v1'
    Assert ((Decision $bad).State -ceq 'remaining') '不同profile拒绝。'
    $bad=Copy-Value $receipt; $bad.SelectedGroups=@($name,'settings')
    Assert ((Decision $bad).State -ceq 'remaining') '前序状态未审核的多组不核销。'
    $bad=Copy-Value $receipt; $bad.Skips=@(@{Code='missing';Reason='input'})
    Assert ((Decision $bad).State -ceq 'remaining') '跳项不能伪造complete。'
    $badManifest=Copy-Value $manifest; $badManifest.Inputs.Profile='unknown-v1'
    Assert (-not (Test-ResultReceipt $receipt $badManifest $runner)) '未知profile不能绕过特定合同。'
    $badManifest=Copy-Value $manifest; $badManifest.InputClosureProven=$false
    Assert ((Decision $receipt $badManifest).State -ceq 'remaining') '闭包未知仍实跑。'
    $conflict=@(@{Receipt=$receipt;Path='good';Digest=('e'*64)},@{Receipt=$bad;Path='skip';Digest=('f'*64)})
    Assert ((@(Get-DevelopmentResumeDecisions $selected $conflict $manifest $runner @{Receipt=$receipt}))[0].State -ceq 'remaining') '旧绿不掩盖同组skip。'
}
foreach ($unknown in @('settings-snapshot-rules','database-schema','settings-storage','class-change-rules','equipment-investment-rules','native-frame','native-channel','not-registered')) {
    Assert ($null -eq (Get-DevelopmentRuleProfile $unknown)) '规则名称、SQL、动态网络不自动放行。'
}
$repository=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$catalog=@(Get-DevelopmentGroupCatalog $repository)
foreach ($name in $names) {
    Assert (@($catalog | Where-Object { $_.Name -ceq $name -and $_.Kind -ceq 'targeted' }).Count -eq 1) '三个入口均不扩大Default。'
}
Assert ((Get-DevelopmentRuleProfile 'frame-crc-rules').TimePolicy.Kind -ceq 'deterministic-with-watchdog') '预算守卫明确区分时间oracle。'
# 只执行实际源码中的缺库判定表达式，不运行完整manifest、ldd或SDK扫描。
$nativeSource=[IO.File]::ReadAllText((Join-Path (Split-Path $PSScriptRoot -Parent) 'DevelopmentChatRateProfile.ps1'))
$guard=[regex]::Match($nativeSource, "if \((\`$relative -cne .*?)\) \{ \`$reasons.Add\('native-dependency-not-found'\)")
Assert $guard.Success '实际native缺库门禁形状可审核。'
$rejectNative=[scriptblock]::Create('param($relative,$normalized) '+$guard.Groups[1].Value)
Assert (-not (& $rejectNative 'shared/Microsoft.NETCore.App/10.0.14/libcoreclrtraceptprovider.so' 'liblttng-ust.so.0 => not found')) '仅当前精确可选provider缺失已审核。'
foreach ($path in @('shared/Microsoft.NETCore.App/10.0.13/libcoreclrtraceptprovider.so','shared/Microsoft.NETCore.App/10.0.15/libcoreclrtraceptprovider.so','shared/Microsoft.NETCore.App/10.0.14/libcoreclr.so')) {
    Assert (& $rejectNative $path 'liblttng-ust.so.0 => not found') '其他版本或库的缺失仍拒绝。'
}
Assert (& $rejectNative 'shared/Microsoft.NETCore.App/10.0.14/libcoreclrtraceptprovider.so' 'libc.so.6 => not found') '同provider其他缺库仍拒绝。'
# 用合成运行包和独立保存记录测试真实计划器；绝不构建或执行该文件。
$temp=Join-Path ([IO.Path]::GetTempPath()) ('profiles-plan-'+[guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($temp) | Out-Null
try {
    $runnerPath=Join-Path $temp 'DfoServer.dll'; [IO.File]::WriteAllText($runnerPath,'synthetic, never executed')
    $runner=Get-ResultFileDigest $runnerPath
    $receipts=Join-Path $temp 'receipts'; [IO.Directory]::CreateDirectory($receipts) | Out-Null
    $store=Join-Path $temp 'store'
    function Get-ProfileTreeDigest([string]$Directory) { 'f'*64 }
    function Get-ResultSourceManifest([string]$Root) { @{Key=('a'*64)} }
    $script:manifestCalls=0; $script:drift=$false
    function Get-ChatRateProfileManifest([string]$Repository,[string]$Configuration,[string]$BuildProfile,[string]$Group) {
        $script:manifestCalls++
        $profile=Get-DevelopmentRuleProfile $Group
        if (-not $profile) { throw 'unexpected group' }
        $key=if($script:drift -and $script:manifestCalls -gt 3){'9'*64}elseif($Group -ceq 'chat-rate-rules'){'c'*64}elseif($Group -ceq 'frame-crc-rules'){'8'*64}else{'e'*64}
        @{Source=('a'*64);Key=$key;InputClosureProven=$true;Inputs=[ordered]@{Profile=$profile.Name;Scope=@($Group)}}
    }
    foreach ($name in $names) {
        $manifest=Get-ChatRateProfileManifest '' '' '' $name
        $receipt=New-Receipt $name $manifest $runner
        $path=Join-Path $receipts ($name+'.json')
        [IO.File]::WriteAllText($path,(ConvertTo-ResultCanonical $receipt))
        $saved=Save-DevelopmentResult $store $manifest $manifest $path $runnerPath -ArtifactPackage ('f'*64)
        Assert ($saved.State -ceq 'recorded') '三个不同key分别保存成功合成记录。'
    }
    $plan=Get-DevelopmentResumePlan $repository $store $receipts $runnerPath 'Explicit' ($names+@('settings')) 'Debug' 'Default' ''
    Assert ($plan.Covered.Count -eq 3 -and $plan.Remaining.Count -eq 1 -and $plan.Remaining[0] -ceq 'settings') '三个独立manifest核销，未知组remaining。'
    $plan=Get-DevelopmentResumePlan $repository $store $receipts $runnerPath 'Explicit' $names 'Debug' 'Default' 'unproven.pvf'
    Assert ($plan.Covered.Count -eq 0 -and $plan.Remaining.Count -eq 3) '提供PVF不借无资源profile。'
    $receipt.Status='running'
    [IO.File]::WriteAllText((Join-Path $receipts 'conflict.json'),(ConvertTo-ResultCanonical $receipt))
    $plan=Get-DevelopmentResumePlan $repository $store $receipts $runnerPath 'Explicit' $names 'Debug' 'Default' ''
    Assert ($plan.Covered.Count -eq 2 -and $plan.Remaining.Count -eq 1 -and $plan.Remaining[0] -ceq 'frame-crc-rules') 'FrameCrc中断仅撤销本组，不误用邻组绿。'
    $script:manifestCalls=0; $script:drift=$true
    $rejected=$false
    try { Get-DevelopmentResumePlan $repository $store $receipts $runnerPath 'Explicit' $names 'Debug' 'Default' '' | Out-Null }
    catch { $rejected=$_.Exception.Message -eq '计划期间实际输入闭包变化。' }
    Assert $rejected '所有组决策结束重核每个manifest，不遗漏先算组漂移。'
} finally { Remove-Item -LiteralPath $temp -Recurse -Force }
"PASS $count rule-profile assertions; synthetic only, no build or server execution"
