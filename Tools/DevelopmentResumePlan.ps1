# 续跑只复用已闭合的完整执行；未知覆盖仍保守重跑，不从日志PASS推断。
. (Join-Path $PSScriptRoot 'DevelopmentChatRateProfile.ps1')
function Get-DevelopmentGroupCatalog([string]$Repository) {
    $path = Join-Path $Repository 'Server/DfoServer/SelfTests/Tcp/TcpRegressionTests.cs'
    $text = [IO.File]::ReadAllText($path)
    # 整个已审核入口文件钉住指纹，含条件编译/多声明/注释变化均拒绝，不猜C#语义。
    if ((Get-ResultDigest $text.Replace("`r`n","`n")) -cne '601c22d5598c65d928accbc30c8318c89ac566d458d5cdc45ba899b83c917570') { throw '注册入口文件已变化，须重新审核；请使用原CLI完整入口。' }
    $groups = [regex]::Match($text, '(?s)\bGroups\s*=\s*\[(.*?)\n\s*\];')
    $targeted = [regex]::Match($text, '(?s)\bTargetedOnly\s*=\s*new\(StringComparer.Ordinal\)\s*\{(.*?)\};')
    if (-not $groups.Success -or -not $targeted.Success) { throw '当前注册表声明形状未知。' }
    $names = @([regex]::Matches($groups.Groups[1].Value, '(?m)^\s*\("([a-z0-9-]+)",') | ForEach-Object { $_.Groups[1].Value })
    $only = @([regex]::Matches($targeted.Groups[1].Value, '"([a-z0-9-]+)"') | ForEach-Object { $_.Groups[1].Value })
    if (-not $names.Count -or @($names | Select-Object -Unique).Count -ne $names.Count -or @($only | Where-Object { $_ -cnotin $names }).Count) { throw '重复或未登记的组。' }
    # 普查组是独立合同，不把所有名称含resource的业务测试误分类为普查。
    @($names | ForEach-Object { @{Name=$_;Kind=$(if ($_ -ceq 'resource-survey') {'survey'} elseif ($_ -cin $only) {'targeted'} else {'default'})} })
}
function Select-DevelopmentResumeGroups($Catalog, [string]$Scope, [string[]]$Groups) {
    $names = @($Catalog | ForEach-Object Name)
    if ($Scope -eq 'Explicit') {
        if (-not $Groups.Count -or @($Groups | Select-Object -Unique).Count -ne $Groups.Count -or @($Groups | Where-Object { $_ -cnotin $names }).Count) { throw 'Explicit需要实际登记且不重复的组；不接受别名。' }
        return @($Catalog | Where-Object { $_.Name -cin $Groups })
    }
    if ($Groups.Count) { throw '非Explicit范围不能同时传Groups。' }
    @($Catalog | Where-Object { $Scope -eq 'All' -or $_.Kind -ceq $Scope.ToLowerInvariant() })
}
function Read-DevelopmentResumeReceipts([string]$Directory) {
    $root=Get-Item -LiteralPath $Directory -Force -ErrorAction Stop
    if (-not $root.PSIsContainer -or $root.LinkType) { throw '收据根须为普通目录。' }
    $json=[Collections.Generic.List[object]]::new()
    Get-ChildItem -LiteralPath $Directory -Recurse -Force -ErrorAction Stop | ForEach-Object {
        if ($_.LinkType) { throw '收据目录不允许链接。' }
        if (-not $_.PSIsContainer -and $_.Extension -ceq '.json') {
            $json.Add($_)
            if ($json.Count -gt 10000) { throw '收据超过10000项；请指定单轮收据目录。' }
        }
    }
    foreach ($file in $json) {
        if ($file.Length -gt 1048576) { throw '单收据超过1MiB。' }
        $receipt = [IO.File]::ReadAllText($file.FullName) | ConvertFrom-Json -AsHashtable -ErrorAction Stop
        if (-not ($receipt -is [Collections.IDictionary]) -or -not $receipt.ContainsKey('Group')) { throw '目录包含非逐组收据；不能静默忽略。' }
        @{Path=$file.FullName;Digest=(Get-ResultFileDigest $file.FullName);Receipt=$receipt}
    }
}
function Get-DevelopmentResumeDecisions($Selected, $Receipts, $Manifest, [string]$RunnerDigest, $CachedRecord) {
    foreach ($group in $Selected) {
        $observed = @($Receipts | Where-Object { $_.Receipt.Group -ceq $group.Name })
        $reason = 'no-receipt'; $covered = $false
        if ($observed.Count) { $reason = 'coverage-or-input-unproven' }
        if ($group.Name -ceq 'chat-rate-rules' -and $Manifest -and $Manifest.InputClosureProven -eq $true -and $CachedRecord) {
            # 所有已知同组记录须一致完整；任何旧红、未知、partial均保守撤销本次核销。
            $valid = $observed.Count -gt 0
            foreach ($row in $observed) { if (-not (Test-ResultReceipt $row.Receipt $Manifest $RunnerDigest)) { $valid=$false } }
            if ($valid -and (Test-ResultReceipt $CachedRecord.Receipt $Manifest $RunnerDigest)) {
                $covered = $true; $reason = 'verified-complete-profile-original-execution'
            } else { $reason = 'receipt-conflict-or-not-complete' }
        }
        @{Group=$group.Name;Kind=$group.Kind;State=$(if ($covered) {'covered'} else {'remaining'});Reason=$reason;
          Evidence=@($observed | ForEach-Object { @{Path=$_.Path;Digest=$_.Digest;Status=$_.Receipt.Status;Coverage=$_.Receipt.Coverage;RunId=$_.Receipt.RunId} })}
    }
}
function Get-DevelopmentResumePlan([string]$Repository, [string]$Store, [string]$ReceiptDirectory, [string]$Runner,
    [string]$Scope, [string[]]$Groups, [string]$Configuration, [string]$BuildProfile, [string]$PvfPath) {
    $source = Get-ResultSourceManifest $Repository
    $catalog = @(Get-DevelopmentGroupCatalog $Repository)
    $selected = @(Select-DevelopmentResumeGroups $catalog $Scope $Groups)
    $receipts = @(Read-DevelopmentResumeReceipts $ReceiptDirectory)
    $manifest=$null; $cached=$null; $digest=''; $package=''
    if ($Runner) {
        $file=Get-Item -LiteralPath $Runner -Force -ErrorAction Stop
        if ($file.PSIsContainer -or $file.LinkType -or $file.Name -cne 'DfoServer.dll') { throw 'Runner须为实际普通DfoServer.dll文件。' }
        $digest=Get-ResultFileDigest $file.FullName
        $package=Get-ProfileTreeDigest $file.DirectoryName
    }
    if (-not $PvfPath -and $digest -and @($selected | Where-Object { $_.Name -ceq 'chat-rate-rules' }).Count) {
        $manifest=Get-ChatRateProfileManifest $Repository $Configuration $BuildProfile
        $cache=Get-DevelopmentResultPlan $Store $manifest
        if ($cache.State -ceq 'cached') {
            $recordText=[IO.File]::ReadAllText($cache.Path)
            if ([IO.Path]::GetFileNameWithoutExtension($cache.Path) -cne (Get-ResultDigest $recordText)) { throw '缓存记录内容校验失败。' }
            $record=$recordText | ConvertFrom-Json -AsHashtable
            if ((ConvertTo-ResultCanonical $record.Input) -cne (ConvertTo-ResultCanonical $manifest)) { throw '缓存记录输入变化。' }
            if ($record.Reusable -is [bool] -and $record.Reusable -eq $true -and $record.Runner -ceq $digest -and $record.ArtifactPackage -ceq $package) { $cached=$record }
        }
    }
    if ($manifest -and (Get-ChatRateProfileManifest $Repository $Configuration $BuildProfile).Key -cne $manifest.Key) { throw '计划期间实际输入闭包变化。' }
    if ($Runner -and ((Get-ResultFileDigest $Runner) -cne $digest -or (Get-ProfileTreeDigest (Split-Path ([IO.Path]::GetFullPath($Runner)) -Parent)) -cne $package)) { throw '计划期间运行包变化。' }
    $decisions=@(Get-DevelopmentResumeDecisions $selected $receipts $manifest $digest $cached)
    if ((Get-ResultSourceManifest $Repository).Key -cne $source.Key) { throw '生成计划期间源码变化。' }
    @{SchemaVersion=1;State='planned';Execution='not-run';Source=$source.Key;Runner=$digest;ArtifactPackage=$package;
      RegistryDigest=(Get-ResultFileDigest (Join-Path $Repository 'Server/DfoServer/SelfTests/Tcp/TcpRegressionTests.cs'));
      Scope=$Scope;Catalog=$catalog;Decisions=$decisions;
      Remaining=@($decisions | Where-Object { $_.State -ceq 'remaining' } | ForEach-Object Group);
      Covered=@($decisions | Where-Object { $_.State -ceq 'covered' } | ForEach-Object Group);
      OtherScopes='not-run';Note='计划不是新执行；未知覆盖仍重跑，仅当前已审计profile能核销。'}
}
