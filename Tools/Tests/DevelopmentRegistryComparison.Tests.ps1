$ErrorActionPreference='Stop'
. (Join-Path (Split-Path $PSScriptRoot -Parent) 'DevelopmentRegistryComparison.ps1')
$count=0
function Assert($value,$message){if(-not $value){throw $message};$script:count++}
function Reject([scriptblock]$action,$message){$rejected=$false;try{& $action|Out-Null}catch{$rejected=$true};Assert $rejected $message}
$root=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$catalog=@(Get-DevelopmentGroupCatalog $root)
$registry=@{SchemaVersion=1;Purpose='registration-only';Execution='not-run';Runner=('a'*64);Groups=@($catalog|ForEach-Object{@{Name=$_.Name;Kind=$_.Kind}});
 DefaultGroups=@($catalog|Where-Object Kind -CEQ 'default'|ForEach-Object Name);TargetedGroups=@($catalog|Where-Object Kind -CEQ 'targeted'|ForEach-Object Name);
 SurveyGroups=@($catalog|Where-Object Kind -CEQ 'survey'|ForEach-Object Name);
 Aliases=@{'dungeon-story-events'=@('story-cinematic','story-scenes','story-quest-entry','story-publication');'quests'=@('quest-lifecycle','quest-extras','quest-chains','quest-chain-settlement','quest-metro')}}
function Copy-Registry { ConvertTo-ResultCanonical $registry | ConvertFrom-Json -AsHashtable }
$result=Compare-DevelopmentRegistry $root $registry ('a'*64)
Assert ($result.State -ceq 'consistent' -and -not $result.ProofOfCoverage -and $result.Execution -ceq 'not-run') '一致不是覆盖证明。'
Assert ($result.Groups -eq ($result.Default+$result.Targeted+$result.Survey)) '分区总数一致。'
foreach($field in @('Purpose','Execution','Runner','SchemaVersion')){
 $bad=Copy-Registry;$bad[$field]='unknown';Reject {Compare-DevelopmentRegistry $root $bad ('a'*64)} '元信息未知拒绝。'
}
$bad=Copy-Registry;$bad.Groups[0].Name='NOT-SAME';Reject {Compare-DevelopmentRegistry $root $bad ('a'*64)} '组名改变拒绝。'
$bad=Copy-Registry;$bad.Groups[0].Kind='targeted';Reject {Compare-DevelopmentRegistry $root $bad ('a'*64)} '分类漂移拒绝。'
$bad=Copy-Registry;$bad.Groups=@($bad.Groups[1],$bad.Groups[0])+$bad.Groups[2..($bad.Groups.Count-1)];Reject {Compare-DevelopmentRegistry $root $bad ('a'*64)} '顺序变化拒绝。'
$bad=Copy-Registry;$bad.Groups[1]=$bad.Groups[0];Reject {Compare-DevelopmentRegistry $root $bad ('a'*64)} '重复组拒绝。'
foreach($field in @('DefaultGroups','TargetedGroups','SurveyGroups')){
 $bad=Copy-Registry;$bad[$field]=@();Reject {Compare-DevelopmentRegistry $root $bad ('a'*64)} '分区遗漏拒绝。'
}
$bad=Copy-Registry;$bad.Aliases.quests=@('quest-lifecycle');Reject {Compare-DevelopmentRegistry $root $bad ('a'*64)} '别名漏组拒绝。'
foreach($json in @('{"Groups":[],"Groups":[]}','{"Aliases":{"x":[],"x":[]}}','{"Groups":[{"Name":"a","Name":"b"}]}')) {
 $doc=[Text.Json.JsonDocument]::Parse($json)
 try{Reject {Assert-RegistryJsonProperties $doc.RootElement} '重复JSON字段不得后写覆盖。'}finally{$doc.Dispose()}
}
"PASS $count registry consistency assertions; synthetic only, no test coverage proof"
