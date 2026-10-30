. (Join-Path $PSScriptRoot 'DevelopmentRuleProfiles.ps1')
# 可选验证结果库；不改变严格检查入口，不共享程序集、数据库或可写资源。
Set-StrictMode -Version Latest
function Get-ResultDigest([string]$Text) {
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($Text))).ToLowerInvariant()
}
function Get-ResultFileDigest([string]$Path) {
    # 保留PowerShell当前位置/provider的字面路径语义；每次读取完整内容，不缓存mtime或摘要。
    $provider=$null; $drive=$null
    $resolved=$ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Path,[ref]$provider,[ref]$drive)
    if($provider.Name -cne 'FileSystem'){throw '文件摘要只接受文件系统路径。'}
    $stream=[IO.File]::OpenRead($resolved)
    try { [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant() }
    finally { $stream.Dispose() }
}
function ConvertTo-ResultCanonical($Value) {
    if ($null -eq $Value) { return 'null' }
    if ($Value -is [System.Collections.IDictionary]) {
        $parts = foreach ($key in @($Value.Keys | Sort-Object -CaseSensitive)) {
            (ConvertTo-Json ([string]$key) -Compress) + ':' + (ConvertTo-ResultCanonical $Value[$key])
        }
        return '{' + ($parts -join ',') + '}'
    }
    if ($Value -is [System.Collections.IEnumerable] -and $Value -isnot [string]) {
        return '[' + ((@($Value | ForEach-Object { ConvertTo-ResultCanonical $_ })) -join ',') + ']'
    }
    ConvertTo-Json $Value -Compress -Depth 80
}
function Get-ResultSourceManifest([string]$Root) {
    $Root = [IO.Path]::GetFullPath($Root)
    $paths = @(& git -C $Root -c core.quotepath=false ls-files --cached --others --exclude-standard)
    if ($LASTEXITCODE -ne 0) { throw '源码输入必须是可枚举的Git工作树；无Git不能复用。' }
    $files = [Collections.Specialized.OrderedDictionary]::new([StringComparer]::Ordinal)
    foreach ($path in @($paths | Sort-Object -Unique -CaseSensitive)) {
        if ($path.StartsWith('"') -or $path.Contains("`n") -or [IO.Path]::IsPathRooted($path) -or $path.Split('/') -contains '..') {
            throw '不支持的输入路径：本次不能缓存。'
        }
        $full = Join-Path $Root $path
        # 本次逐文件只查询一次元数据；下一次清单仍重新检查全部文件。
        $item = Get-Item -LiteralPath $full -Force -ErrorAction Stop
        if ($item.PSIsContainer) { throw '源码缺失或子模块未展开：本次不能缓存。' }
        if ($item.LinkType) { throw '符号链接输入尚未闭合：本次不能缓存。' }
        $mode = if ($IsWindows) { [string]$item.Attributes } else { [string][IO.File]::GetUnixFileMode($full) }
        $files[$path] = @{ Digest = Get-ResultFileDigest $full; Mode = $mode }
    }
    $canonical = ConvertTo-ResultCanonical $files
    @{ SchemaVersion = 1; Files = $files; Key = Get-ResultDigest $canonical }
}
function Get-ResultInputManifest([string]$Root, [string]$DescriptorPath) {
    # 描述文件必须由已审计的构建输入收集器提供；未知闭包只允许实跑，不放行复用。
    $descriptor = Get-Content -LiteralPath $DescriptorPath -Raw | ConvertFrom-Json -AsHashtable
    foreach ($name in @('SchemaVersion','Closure','Build','Toolchain','Dependencies','Native','Configuration','EnvironmentDigest','TimePolicy','Scope','Resources')) {
        if (-not $descriptor.ContainsKey($name)) { throw "输入描述缺少 $name；本次不能缓存。" }
    }
    if ($descriptor.SchemaVersion -ne 1 -or $descriptor.Closure -notin @('complete-consumed-inputs-v1','observed-inputs-v1')) { throw '未证明完整构建输入闭包。' }
    if ($descriptor.EnvironmentDigest -notmatch '^[a-f0-9]{64}$' -or $descriptor.Scope.Count -ne 1) { throw '环境身份或有序测试范围无效。' }
    if (-not $descriptor.TimePolicy.ContainsKey('Value')) { throw '缺少时间策略。' }
    $eligible = $descriptor.Closure -eq 'complete-consumed-inputs-v1' -and $descriptor.TimePolicy.Kind -eq 'fixed'
    $source = Get-ResultSourceManifest $Root
    $inputs = [ordered]@{ SchemaVersion = 1; Source = $source.Key; Configuration = $descriptor.Configuration; EnvironmentDigest = $descriptor.EnvironmentDigest; TimePolicy = $descriptor.TimePolicy; Scope = @($descriptor.Scope) }
    foreach ($category in @('Build','Toolchain','Dependencies','Native','Resources')) {
        $entries = [Collections.Specialized.OrderedDictionary]::new([StringComparer]::Ordinal)
        foreach ($entry in @($descriptor[$category])) {
            if (-not $entry.Name -or $entries.Contains($entry.Name) -or -not $entry.Path) { throw '输入逻辑名称缺失或重复。' }
            $file = Get-Item -LiteralPath $entry.Path -Force -ErrorAction Stop
            if ($file.PSIsContainer -or $file.LinkType) { throw '外部输入必须是已枚举普通文件。' }
            $entries[$entry.Name] = Get-ResultFileDigest $file.FullName
        }
        if ($category -in @('Build','Toolchain') -and $entries.Count -eq 0) { throw '实际构建元数据和工具链身份不得为空。' }
        $inputs[$category] = $entries
    }
    $canonical = ConvertTo-ResultCanonical $inputs
    @{ SchemaVersion = 1; Key = Get-ResultDigest $canonical; Source = $source.Key; Inputs = $inputs; InputClosureProven = $eligible }
}
function Enter-ResultKeyLock([string]$Store, [string]$Key, [ValidateRange(0,86400)][int]$WaitSeconds=0) {
    if ($Key -notmatch '^[a-f0-9]{64}$') { throw '无效的缓存键。' }
    $locks = Join-Path $Store 'locks'; [IO.Directory]::CreateDirectory($locks) | Out-Null
    $watch=[Diagnostics.Stopwatch]::StartNew()
    do {
        try { return [IO.File]::Open((Join-Path $locks ($Key + '.lock')), 'OpenOrCreate', 'ReadWrite', 'None') }
        catch [IO.IOException] { if ($watch.Elapsed.TotalSeconds -ge $WaitSeconds) { return $null }; Start-Sleep -Milliseconds 50 }
    } while ($true)
}
function Write-ResultImmutable([string]$Directory, [string]$Text) {
    [IO.Directory]::CreateDirectory($Directory) | Out-Null
    $digest = Get-ResultDigest $Text; $target = Join-Path $Directory ($digest + '.json')
    $temp = Join-Path $Directory ([guid]::NewGuid().ToString('N') + '.tmp')
    try {
        [IO.File]::WriteAllText($temp, $Text, [Text.UTF8Encoding]::new($false))
        if (-not [IO.File]::Exists($target)) { [IO.File]::Move($temp, $target) }
        elseif ([IO.File]::ReadAllText($target) -cne $Text) { throw '内容寻址文件冲突。' }
    } finally { if ([IO.File]::Exists($temp)) { [IO.File]::Delete($temp) } }
    $target
}
function Test-ResultReceipt($Receipt, $Manifest, [string]$RunnerDigest) {
    foreach ($key in @('SchemaVersion','Execution','Passed','Status','Coverage','Skips','Identity','CacheEligible','Scope','Group','RunId','StartedUtc','CompletedUtc','SelectedGroups')) { if (-not $Receipt.ContainsKey($key)) { return $false } }
    if ($Manifest.Inputs.Contains('Profile')) {
        if (@($Manifest.Inputs.Scope).Count -ne 1) { return $false }
        $profile=Get-DevelopmentRuleProfile ([string]$Manifest.Inputs.Scope[0])
        if (-not $profile -or $Manifest.Inputs.Profile -cne $profile.Name) { return $false }
        if (-not $Receipt.ContainsKey('Profile') -or $Receipt.Profile -cne $profile.Name -or
            -not $Receipt.ContainsKey('Assertions') -or $Receipt.Assertions -ne $profile.Assertions -or
            -not $Receipt.ContainsKey('SharedResourcesLoaded') -or $Receipt.SharedResourcesLoaded -isnot [bool] -or $Receipt.SharedResourcesLoaded -ne $false) { return $false }
    }
    $started = [DateTimeOffset]::MinValue; $completed = [DateTimeOffset]::MinValue
    if (-not [DateTimeOffset]::TryParse([string]$Receipt.StartedUtc, [ref]$started) -or
        -not [DateTimeOffset]::TryParse([string]$Receipt.CompletedUtc, [ref]$completed) -or $completed -lt $started) { return $false }
    $Receipt.Passed -is [bool] -and $Receipt.Passed -eq $true -and $Receipt.SchemaVersion -eq 2 -and $Receipt.Execution -ceq 'executed' -and $Receipt.Status -ceq 'passed' -and
        $Receipt.Coverage -ceq 'complete' -and @($Receipt.Skips).Count -eq 0 -and $Receipt.CacheEligible -is [bool] -and $Receipt.CacheEligible -eq $true -and
        $Receipt.Identity.Source -ceq $Manifest.Source -and $Receipt.Identity.Input -ceq $Manifest.Key -and
        $Receipt.Identity.Runner -ceq $RunnerDigest -and $RunnerDigest -match '^[a-f0-9]{64}$' -and
        $Receipt.Scope -ceq 'group' -and $Receipt.RunId -match '^[a-f0-9]{32}$' -and
        (ConvertTo-ResultCanonical @($Receipt.SelectedGroups)) -ceq (ConvertTo-ResultCanonical @($Manifest.Inputs.Scope)) -and
        (ConvertTo-ResultCanonical @($Receipt.Group)) -ceq (ConvertTo-ResultCanonical @($Manifest.Inputs.Scope))
}
function Save-DevelopmentResult([string]$Store, $Before, $After, [string]$ReceiptPath, [string]$RunnerPath, [IO.FileStream]$HeldLock = $null, [string]$ArtifactPackage = '') {
    $lease = if ($HeldLock) { Assert-ResultHeldLock $Store $Before.Key $HeldLock; $HeldLock } else { Enter-ResultKeyLock $Store $Before.Key }
    if ($null -eq $lease) { return @{ State = 'blocked'; Reason = 'key-in-use' } }
    try {
        $receipt = Get-Content -LiteralPath $ReceiptPath -Raw | ConvertFrom-Json -AsHashtable
        $runner = Get-ResultFileDigest $RunnerPath
        $valid = $Before.InputClosureProven -eq $true -and $After.InputClosureProven -eq $true -and $Before.Key -ceq $After.Key -and (Test-ResultReceipt $receipt $Before $runner)
        if ($Before.Inputs.Contains('Profile') -and $ArtifactPackage -notmatch '^[a-f0-9]{64}$') { $valid = $false }
        $record = [ordered]@{ SchemaVersion = 1; Input = $Before; Receipt = $receipt; Runner = $runner; ArtifactPackage = $ArtifactPackage; Reusable = $valid }
        $directory = Join-Path (Join-Path $Store 'records') $Before.Key
        $path = Write-ResultImmutable $directory (ConvertTo-ResultCanonical $record)
        if (-not $valid) {
            # 同key的失败/部分/输入漂移保留不可变冲突证据，之后不能捞取更早的绿结果。
            Write-ResultImmutable (Join-Path $directory 'invalidations') (ConvertTo-ResultCanonical @{ Record = [IO.Path]::GetFileName($path); Reason = 'not-complete-equivalent-pass' }) | Out-Null
        }
        @{ State = $(if ($valid) { 'recorded' } else { 'run' }); Reason = $(if ($valid) { 'complete-pass-recorded' } else { 'not-cache-eligible' }); Path = $path }
    } catch {
        $directory = Join-Path (Join-Path (Join-Path $Store 'records') $Before.Key) 'invalidations'
        Write-ResultImmutable $directory (ConvertTo-ResultCanonical @{ Reason='record-not-verifiable'; RunId=[guid]::NewGuid().ToString('N') }) | Out-Null
        throw
    } finally { if (-not $HeldLock) { $lease.Dispose() } }
}
function Get-DevelopmentResultPlan([string]$Store, $Manifest, [IO.FileStream]$HeldLock = $null, [ValidateRange(0,86400)][int]$WaitSeconds=0) {
    if (-not $Manifest.InputClosureProven) { return @{ State='run'; Reason='dependency-or-time-closure-unproven' } }
    $lease = if ($HeldLock) { Assert-ResultHeldLock $Store $Manifest.Key $HeldLock; $HeldLock } else { Enter-ResultKeyLock $Store $Manifest.Key $WaitSeconds }
    if ($null -eq $lease) { return @{ State = 'blocked'; Reason = 'key-in-use' } }
    try {
        $directory = Join-Path (Join-Path $Store 'records') $Manifest.Key
        $starts = Join-Path $directory 'started'
        $ends = Join-Path $directory 'completed'
        if (Test-Path -LiteralPath $starts) {
            foreach ($start in Get-ChildItem -LiteralPath $starts -File -Filter '*.json') {
                $end = Join-Path $ends $start.Name
                if (-not (Test-Path -LiteralPath $end)) { return @{ State='run'; Reason='unfinished-execution' } }
                $startText = [IO.File]::ReadAllText($start.FullName)
                if ($start.BaseName -cne (Get-ResultDigest $startText) -or [IO.File]::ReadAllText($end) -cne $startText) {
                    return @{ State='run'; Reason='corrupt-execution-ledger' }
                }
            }
        }
        if (Test-Path -LiteralPath (Join-Path $directory 'invalidations')) { return @{ State = 'run'; Reason = 'conflicting-or-incomplete-result' } }
        if (-not (Test-Path -LiteralPath $directory)) { return @{ State = 'run'; Reason = 'cache-miss' } }
        $found = $null
        foreach ($file in Get-ChildItem -LiteralPath $directory -File -Filter '*.json') {
            try {
                $text = [IO.File]::ReadAllText($file.FullName)
                if ($file.BaseName -cne (Get-ResultDigest $text)) { throw 'digest' }
                $record = $text | ConvertFrom-Json -AsHashtable
                if ($record.SchemaVersion -ne 1 -or -not $record.Reusable -or
                    ($Manifest.Inputs.Contains('Profile') -and (-not $record.ContainsKey('ArtifactPackage') -or $record.ArtifactPackage -notmatch '^[a-f0-9]{64}$')) -or
                    (ConvertTo-ResultCanonical $record.Input) -cne (ConvertTo-ResultCanonical $Manifest) -or
                    -not (Test-ResultReceipt $record.Receipt $Manifest $record.Runner)) { throw 'identity' }
                $found = $file.FullName
            } catch { return @{ State = 'run'; Reason = 'corrupt-or-legacy-result' } }
        }
        if ($found) { return @{ State = 'cached'; Reason = 'equivalent-input-original-execution'; Path = $found } }
        @{ State = 'run'; Reason = 'cache-miss' }
    } finally { if (-not $HeldLock) { $lease.Dispose() } }
}

function Assert-ResultHeldLock([string]$Store, [string]$Key, [IO.FileStream]$Lease) {
    $expected = [IO.Path]::GetFullPath((Join-Path (Join-Path $Store 'locks') ($Key + '.lock')))
    if ($Lease.SafeFileHandle.IsClosed -or $Lease.Name -cne $expected) { throw '缓存执行锁不属于当前key。' }
}
function Invoke-DevelopmentResultSingleFlight([string]$Store, $Manifest, [scriptblock]$Run, [ValidateRange(0,86400)][int]$WaitSeconds=0, [scriptblock]$ValidateInputs=$null, [switch]$ForceExecute) {
    $lease = Enter-ResultKeyLock $Store $Manifest.Key $WaitSeconds
    if ($null -eq $lease) { return @{ State='blocked'; Reason='key-in-use' } }
    try {
        if ($ValidateInputs -and -not (& $ValidateInputs)) { return @{State='blocked';Reason='inputs-changed-while-waiting'} }
        $plan = Get-DevelopmentResultPlan $Store $Manifest $lease
        if ($plan.State -eq 'cached' -and -not $ForceExecute) { return $plan }
        # 执行器负责调用原严格命令、捕获前后输入和真实receipt；锁一直保留到登记完成。
        $directory = Join-Path (Join-Path $Store 'records') $Manifest.Key
        $startText = ConvertTo-ResultCanonical @{ RunId=[guid]::NewGuid().ToString('N'); Input=$Manifest.Key }
        $start = Write-ResultImmutable (Join-Path $directory 'started') $startText
        $result = & $Run $lease
        if ($null -eq $result -or $result.State -ne 'recorded') { throw '执行器未登记完整等价通过结果。' }
        Write-ResultImmutable (Join-Path $directory 'completed') $startText | Out-Null
        $result
    } catch {
        $directory = Join-Path (Join-Path (Join-Path $Store 'records') $Manifest.Key) 'invalidations'
        Write-ResultImmutable $directory (ConvertTo-ResultCanonical @{ Reason='execution-not-completed'; RunId=[guid]::NewGuid().ToString('N') }) | Out-Null
        throw
    } finally { $lease.Dispose() }
}
