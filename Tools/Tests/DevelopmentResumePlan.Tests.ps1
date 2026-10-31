$ErrorActionPreference='Stop'
. (Join-Path (Split-Path $PSScriptRoot -Parent) 'DevelopmentResumePlan.ps1')
$count=0
function Assert($condition,[string]$message) { if (-not $condition) { throw $message }; $script:count++ }
function Reject([scriptblock]$action,[string]$message) { $bad=$false; try { & $action | Out-Null } catch { $bad=$true }; Assert $bad $message }
$repository=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$catalog=@(Get-DevelopmentGroupCatalog $repository)
Assert ($catalog.Count -gt 0) '真实注册表可读。'
$default=@(Select-DevelopmentResumeGroups $catalog 'Default' @())
$targeted=@(Select-DevelopmentResumeGroups $catalog 'Targeted' @())
$survey=@(Select-DevelopmentResumeGroups $catalog 'Survey' @())
Assert (($default.Count+$targeted.Count+$survey.Count) -eq $catalog.Count) '三分类不漏不重。'
Assert ($survey.Count -eq 1 -and $survey[0].Name -eq 'resource-survey') '普查不是普通targeted。'
Assert ('chat-rate-rules' -cin @($targeted | ForEach-Object Name)) '纯规则属于定向。'
Assert ('retired-dungeon-entry' -cin @($default | ForEach-Object Name)) '退役入口拒绝回归仍属于默认必跑。'
Assert (-not (Get-DevelopmentRuleProfile 'retired-dungeon-entry')) '退役业务回归不扩展纯规则缓存白名单。'
Reject { Select-DevelopmentResumeGroups $catalog 'Explicit' @('chat-rate-rules','chat-rate-rules') } '重复选择拒绝。'
Reject { Select-DevelopmentResumeGroups $catalog 'Explicit' @('not-a-registered-group') } '未知组拒绝。'
Reject { Select-DevelopmentResumeGroups $catalog 'Default' @('chat-rate-rules') } '隐含范围不混显式组。'
$selected=@(Select-DevelopmentResumeGroups $catalog 'Explicit' @('chat-rate-rules','settings'))
$runner='b'*64
$manifest=@{Source=('a'*64);Key=('c'*64);InputClosureProven=$true;Inputs=[ordered]@{Profile='chat-rate-rules-v1';Scope=@('chat-rate-rules')}}
$receipt=@{SchemaVersion=2;Execution='executed';Status='passed';Passed=$true;Coverage='complete';Skips=@();CacheEligible=$true;Scope='group';Group='chat-rate-rules';RunId=('d'*32);StartedUtc='2026-01-01T00:00:00Z';CompletedUtc='2026-01-01T00:00:01Z';SelectedGroups=@('chat-rate-rules');Identity=@{Source=$manifest.Source;Input=$manifest.Key;Runner=$runner};Profile='chat-rate-rules-v1';Assertions=39;SharedResourcesLoaded=$false}
function Rows($r) { ,@{Receipt=$r;Path='synthetic';Digest=('e'*64)} }
function Copy-ResumeValue($r) { ConvertTo-ResultCanonical $r | ConvertFrom-Json -AsHashtable }
$result=@(Get-DevelopmentResumeDecisions $selected (Rows $receipt) $manifest $runner @{Receipt=$receipt})
Assert ($result[0].State -eq 'covered') '完整已审规则可核销。'
Assert ($result[1].State -eq 'remaining') '无收据仍重跑。'
foreach ($field in @('Status','Coverage','Execution')) {
 $bad=Copy-ResumeValue $receipt; $bad[$field]='unknown'
 $r=@(Get-DevelopmentResumeDecisions $selected (Rows $bad) $manifest $runner @{Receipt=$receipt})
 Assert ($r[0].State -eq 'remaining') "${field}未知不复用。"
}
foreach ($state in @('failed','running','interrupted')) {
 $bad=Copy-ResumeValue $receipt; $bad.Status=$state
 $r=@(Get-DevelopmentResumeDecisions $selected @((Rows $receipt),(Rows $bad)) $manifest $runner @{Receipt=$receipt})
 Assert ($r[0].State -eq 'remaining') '旧绿不能掩盖失败/中断。'
}
$bad=Copy-ResumeValue $receipt; $bad.Skips=@(@{Code='missing';Reason='input'})
Assert ((@(Get-DevelopmentResumeDecisions $selected (Rows $bad) $manifest $runner @{Receipt=$receipt}))[0].State -eq 'remaining') 'skip即使Passed也不核销。'
foreach ($field in @('Source','Input','Runner')) {
 $bad=Copy-ResumeValue $receipt; $bad.Identity[$field]='f'*64
 Assert ((@(Get-DevelopmentResumeDecisions $selected (Rows $bad) $manifest $runner @{Receipt=$receipt}))[0].State -eq 'remaining') '身份漂移不复用。'
}
Assert ((@(Get-DevelopmentResumeDecisions $selected (Rows $receipt) $manifest $runner $null))[0].State -eq 'remaining') '独立手写receipt没有可信store记录不核销。'
$closed=Copy-ResumeValue $manifest; $closed.InputClosureProven=$false
Assert ((@(Get-DevelopmentResumeDecisions $selected (Rows $receipt) $closed $runner @{Receipt=$receipt}))[0].State -eq 'remaining') '未闭合输入不能复用。'
$bad=Copy-ResumeValue $receipt; $bad.Group='settings';$bad.SelectedGroups=@('settings')
Assert ((@(Get-DevelopmentResumeDecisions $selected (Rows $bad) $manifest $runner @{Receipt=$bad}))[1].State -eq 'remaining') '任意组不能伪装profile。'
$temp=Join-Path ([IO.Path]::GetTempPath()) ('resume-test-'+[guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($temp)|Out-Null
try {
 $registryRelative='Server/DfoServer/SelfTests/Tcp/TcpRegressionTests.cs'
 $fakeRoot=Join-Path $temp 'registry'
 $fakeRegistry=Join-Path $fakeRoot $registryRelative
 [IO.Directory]::CreateDirectory((Split-Path $fakeRegistry -Parent)) | Out-Null
 $registry=[IO.File]::ReadAllText((Join-Path $repository $registryRelative))
 foreach ($replacement in @('( "new-group",','("UpperCase",','BuildExtraGroup(),')) {
  $altered=$registry.Replace('("commands",',$replacement)
  [IO.File]::WriteAllText($fakeRegistry,$altered)
  Reject { Get-DevelopmentGroupCatalog $fakeRoot } '不能静默漏未识别的注册条目。'
 }
 [IO.File]::WriteAllText($fakeRegistry,$registry.Replace('"chat-rate-rules",', '// "settings"' + "`n" + '"chat-rate-rules",'))
 Reject { Get-DevelopmentGroupCatalog $fakeRoot } 'TargetedOnly注释不能误删默认组。'
 [IO.File]::WriteAllText($fakeRegistry,$registry.Replace('!TargetedOnly.Contains(name)','TargetedOnly.Contains(name)'))
 Reject { Get-DevelopmentGroupCatalog $fakeRoot } '默认选择语义改变必须拒绝。'
 [IO.File]::WriteAllText((Join-Path $temp 'group.json'),(ConvertTo-ResultCanonical $receipt))
 $read=@(Read-DevelopmentResumeReceipts $temp)
 Assert ($read.Count -eq 1 -and $read[0].Digest -match '^[a-f0-9]{64}$') '实际文件采集hash。'
 $plan=Get-DevelopmentResumePlan $repository (Join-Path $temp 'store') $temp '' 'Explicit' @('chat-rate-rules') 'Debug' 'Default' ''
 Assert ($plan.Remaining.Count -eq 1 -and $plan.Covered.Count -eq 0) '未提供实际runner的真plan不接受手填身份。'
 Assert ($plan.Execution -eq 'not-run' -and $plan.Source -match '^[a-f0-9]{64}$') '真实源码自动采集不执行。'
 $cli=Join-Path (Split-Path $PSScriptRoot -Parent) 'Resume-DevelopmentTests.ps1'
 $output=& (Get-Process -Id $PID).Path -NoProfile -File $cli -Repository $repository -Store (Join-Path $temp 'store') -ReceiptDirectory $temp -Scope Explicit -Groups chat-rate-rules
 Assert ($LASTEXITCODE -eq 0) 'CLI真实只计划返回0。'
 $cliPlan=($output -join "`n") | ConvertFrom-Json -AsHashtable
 Assert ($cliPlan.Execution -eq 'not-run' -and $cliPlan.Remaining.Count -eq 1) 'CLI没有把手填receipt变为covered。'
 $output=& (Get-Process -Id $PID).Path -NoProfile -File $cli -Repository $repository -Store (Join-Path $temp 'store') -ReceiptDirectory $temp -Scope Explicit -Groups not-registered
 Assert ($LASTEXITCODE -eq 2) 'CLI未知组非零阻断。'
 [IO.File]::WriteAllText((Join-Path $temp 'bad.json'),'not-json')
 Reject { Read-DevelopmentResumeReceipts $temp } '损坏收据不静默跳过。'
} finally { Remove-Item -LiteralPath $temp -Recurse -Force }
"PASS $count resume-plan assertions; no project build or TCP execution"
