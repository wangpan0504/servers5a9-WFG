# 清单一致性不是执行或覆盖证明；不因用户提供JSON扩大缓存/续跑权限。
. (Join-Path $PSScriptRoot 'DevelopmentResumePlan.ps1')
function Assert-RegistryJsonProperties($Element) {
    if($Element.ValueKind -eq [Text.Json.JsonValueKind]::Object) {
        $names=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach($property in $Element.EnumerateObject()) {
            if(-not $names.Add($property.Name)){throw '注册清单包含重复JSON字段。'}
            Assert-RegistryJsonProperties $property.Value
        }
    } elseif($Element.ValueKind -eq [Text.Json.JsonValueKind]::Array) {
        foreach($value in $Element.EnumerateArray()){Assert-RegistryJsonProperties $value}
    }
}
function Compare-DevelopmentRegistry([string]$Repository,$Registry,[string]$RunnerDigest) {
    $catalog=@(Get-DevelopmentGroupCatalog $Repository)
    if($Registry -isnot [Collections.IDictionary] -or $Registry.SchemaVersion -isnot [long] -and $Registry.SchemaVersion -isnot [int] -or
       $Registry.SchemaVersion -ne 1 -or $Registry.Purpose -cne 'registration-only' -or $Registry.Execution -cne 'not-run' -or
       $RunnerDigest -cnotmatch '^[a-f0-9]{64}$' -or $Registry.Runner -cne $RunnerDigest){throw '清单合同或实际程序集摘要不匹配。'}
    if($Registry.Groups -isnot [array] -or $Registry.Groups.Count -ne $catalog.Count){throw '组数或数组类型不匹配。'}
    for($i=0;$i -lt $catalog.Count;$i++) {
        $group=$Registry.Groups[$i]
        if($group -isnot [Collections.IDictionary] -or $group.Name -isnot [string] -or $group.Kind -isnot [string] -or
           $group.Name -cne $catalog[$i].Name -or $group.Kind -cne $catalog[$i].Kind){throw '有序组名或分类不匹配。'}
    }
    foreach($pair in @(@('DefaultGroups','default'),@('TargetedGroups','targeted'),@('SurveyGroups','survey'))) {
        $expected=@($catalog | Where-Object Kind -CEQ $pair[1] | ForEach-Object Name)
        $actual=$Registry[$pair[0]]
        if($actual -isnot [array] -or (ConvertTo-ResultCanonical $actual) -cne (ConvertTo-ResultCanonical $expected)){throw '分范围有序清单不匹配。'}
    }
    # 已钉住的源文件明确只有这两个别名；别名不作为独立覆盖组。
    $aliases=@{
        'dungeon-story-events'=@('story-cinematic','story-scenes','story-quest-entry','story-publication')
        'quests'=@('quest-lifecycle','quest-extras','quest-chains','quest-chain-settlement','quest-metro')
    }
    if((ConvertTo-ResultCanonical $Registry.Aliases) -cne (ConvertTo-ResultCanonical $aliases)){throw '别名展开不匹配。'}
    @{SchemaVersion=1;State='consistent';Execution='not-run';ProofOfCoverage=$false;Runner=$RunnerDigest;
      Groups=$catalog.Count;Default=@($catalog | Where-Object Kind -CEQ 'default').Count;
      Targeted=@($catalog | Where-Object Kind -CEQ 'targeted').Count;Survey=@($catalog | Where-Object Kind -CEQ 'survey').Count;
      RegistryDigest=Get-ResultFileDigest (Join-Path $Repository 'Server/DfoServer/SelfTests/Tcp/TcpRegressionTests.cs');
      Note='只核对已审核源码和所提供程序集清单一致性；不是清单来源认证、测试成功或省略/缓存授权。'}
}
