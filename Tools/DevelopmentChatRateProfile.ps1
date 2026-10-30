# 已审单组规则共用完整输入采集；保留旧函数名兼容调用。
# 完整代码闭包钉住已审计整合源码；代码变更只允许实跑，须重新审计才启用缓存。
. (Join-Path $PSScriptRoot 'DevelopmentResultStore.ps1')
. (Join-Path $PSScriptRoot 'DevelopmentRuleProfiles.ps1')
function Get-DevelopmentEnvironmentMap {
    param([object[]]$Entries=@(Get-ChildItem Env:))
    $environment=[Collections.Specialized.OrderedDictionary]::new([StringComparer]::Ordinal)
    foreach($row in $Entries | Sort-Object Name -CaseSensitive){
        if($row.Name -cnotin @('PWD','OLDPWD','DFO_TEST_SOURCE_KEY','DFO_TEST_INPUT_KEY','DFO_TEST_RECEIPTS_DIR','DFO_TCP_SHARD_ROOT','DFO_TCP_PARALLEL')){$environment[$row.Name]=$row.Value}
    }
    return $environment
}
function Get-ProfileTreeDigest([string]$Directory) {
    $base = [IO.Path]::GetFullPath($Directory)
    $rootItem=Get-Item -LiteralPath $base -Force -ErrorAction Stop
    if(-not $rootItem.PSIsContainer -or $rootItem.LinkType -or ($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'profile摘要根目录必须是普通目录。'}
    $files = [Collections.Specialized.OrderedDictionary]::new([StringComparer]::Ordinal)
    $all = @(Get-ChildItem -LiteralPath $base -Recurse -Force -ErrorAction Stop)
    if (@($all | Where-Object { ($_.LinkType -and ($_.PSIsContainer -or $_.LinkType -cne 'HardLink')) -or ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) }).Count -gt 0) { throw 'profile依赖目录包含未闭合链接。' }
    foreach ($file in $all | Where-Object { -not $_.PSIsContainer } | Sort-Object FullName) {
        $files[[IO.Path]::GetRelativePath($base,$file.FullName).Replace('\','/')] = Get-ResultFileDigest $file.FullName
    }
    Get-ResultDigest (ConvertTo-ResultCanonical $files)
}
function Get-DevelopmentCompilerEnvironment([string]$BuildProfile) {
    switch ($BuildProfile) {
        'Default' { return @{CscEnvironment='DOTNET_PROCESSOR_COUNT=1'} }
        'CloudLowMemory' { return @{CscEnvironment='DOTNET_PROCESSOR_COUNT=1;DOTNET_GCHeapHardLimit=0xA0000000;DOTNET_GCConserveMemory=5'} }
        default { throw '未知显式编译预算配置。' }
    }
}
function Get-ChatRateProfileManifest([string]$Repository, [string]$Configuration, [string]$BuildProfile='Default', [string]$Group='chat-rate-rules') {
    $profile=Get-DevelopmentRuleProfile $Group
    if (-not $profile) { throw '未审核的纯规则profile。' }
    $source = Get-ResultSourceManifest $Repository
    $revision = [string](& git -C $Repository rev-parse HEAD)
    if ($LASTEXITCODE -ne 0 -or $revision -notmatch '^[a-f0-9]{40,64}$') { throw '实际SDK源码版本元数据不可核验。' }
    $code = [ordered]@{}
    foreach ($key in $source.Files.Keys) {
        if ($key -match '^(Server/|Tools/(PvfLib|ArchitectureCheck|PvfLib.Tests|ServerLauncher)/)' -or $key -match '\.(sln|props|targets)$') { $code[$key] = $source.Files[$key] }
    }
    $codeKey = Get-ResultDigest (ConvertTo-ResultCanonical $code)
    $reasons = [Collections.Generic.List[string]]::new()
    if (-not $IsLinux -or [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -ne 'X64') { $reasons.Add('profile-only-audited-linux-x64') }
    if ($codeKey -cne '82f4bc1fafbfa4e432c93da2511dabdb5754581f81db6129e942745bdc8d0ddd') { $reasons.Add('profile-code-closure-changed') }
    # 不能让Git忽略的新C#/构建输入逃出完整源码key。
    $ignored = @(& git -C $Repository -c core.quotepath=false ls-files --others --ignored --exclude-standard)
    if ($LASTEXITCODE -ne 0) { throw '无法核对被忽略的构建输入。' }
    foreach ($path in $ignored) {
        if ($path -notmatch '(^build/|/(bin|obj)/)' -and ($path -match '^(Server/|Tools/(PvfLib|ArchitectureCheck|PvfLib.Tests|ServerLauncher)/)' -or $path -match '\.(cs|csproj|props|targets|resx|sql|html|js|css|config)$')) { $reasons.Add('ignored-build-input'); break }
    }
    $parent = [IO.Directory]::GetParent([IO.Path]::GetFullPath($Repository))
    while ($null -ne $parent) {
        foreach ($name in @('Directory.Build.props','Directory.Build.targets','Directory.Packages.props','global.json')) {
            if ([IO.File]::Exists((Join-Path $parent.FullName $name))) { $reasons.Add('external-ancestor-build-configuration') }
        }
        $parent = $parent.Parent
    }
    $dotnet = (Get-Command dotnet -ErrorAction Stop).Source
    $dotnetRoot = Split-Path $dotnet -Parent
    if (-not $env:NUGET_PACKAGES -or -not (Test-Path -LiteralPath $env:NUGET_PACKAGES)) { $reasons.Add('restore-inputs-unavailable') }
    $environment = Get-DevelopmentEnvironmentMap
    foreach ($row in Get-ChildItem Env: | Sort-Object Name) {
        if ($row.Name -cin @('PWD','OLDPWD')) { continue } # 固定profile不读取；构建显式绑定工作树根。
        if ($row.Name -cin @('DFO_TEST_SOURCE_KEY','DFO_TEST_INPUT_KEY','DFO_TEST_RECEIPTS_DIR','DFO_TCP_SHARD_ROOT','DFO_TCP_PARALLEL')) { continue } # 执行器覆盖为本次逻辑输入/输出。
        if ($row.Name -match '^(LD_|DYLD_)' -or $row.Name -match '^(MSBUILD|MSBuild)' -or $row.Name -in @('DOTNET_STARTUP_HOOKS','DOTNET_ADDITIONAL_DEPS','DOTNET_SHARED_STORE')) { $reasons.Add('external-loader-or-build-override') }
        $environment[$row.Name] = $row.Value
    }
    $native = [ordered]@{}
    $missing = [ordered]@{}
    if ($IsLinux) {
        # 只检查已安装可信.NET宿主/运行时的链接依赖，不执行客户端或任意仓库二进制。
        $libraries = @($dotnet) + @(Get-ChildItem -LiteralPath (Join-Path $dotnetRoot 'shared') -Recurse -File -Filter '*.so' | ForEach-Object FullName)
        foreach ($library in $libraries) {
            $lines = @(& ldd $library 2>&1)
            if ($LASTEXITCODE -ne 0) { $reasons.Add('native-dependency-not-resolved'); continue }
            foreach ($line in $lines) {
                if ([string]$line -match 'not found') {
                    $relative = [IO.Path]::GetRelativePath($dotnetRoot,$library).Replace('\','/')
                    $normalized = ([string]$line).Trim() -replace '\s+',' '
                    $missing[$relative + ':' + $normalized] = 'observed-unavailable'
                    # 此精确可选LTTng provider缺失是已知输入，不等于假称依赖存在。
                    if ($relative -cne 'shared/Microsoft.NETCore.App/10.0.14/libcoreclrtraceptprovider.so' -or $normalized -cne 'liblttng-ust.so.0 => not found') { $reasons.Add('native-dependency-not-found') }
                }
                if ([string]$line -match '(/[^ ]+)\s+\(') {
                    $dependency = $Matches[1]
                    $native[$dependency] = Get-ResultFileDigest $dependency
                }
            }
        }
    }
    $inputs = [ordered]@{
        SchemaVersion=1; Profile=$profile.Name; Source=$source.Key; Code=$codeKey
        Configuration=$Configuration; BuildMetadata=@{SourceRevisionId=$revision}; BuildOptions=@('-m:1','-nr:false','-p:UseSharedCompilation=false','-p:RunAnalyzers=true'); CompilerEnvironment=(Get-DevelopmentCompilerEnvironment $BuildProfile)
        Toolchain=(Get-ProfileTreeDigest $dotnetRoot)
        Dependencies=$(if ($env:NUGET_PACKAGES -and (Test-Path -LiteralPath $env:NUGET_PACKAGES)) { Get-ProfileTreeDigest $env:NUGET_PACKAGES } else { 'unavailable' })
        Native=$native; MissingNative=$missing; EnvironmentDigest=(Get-ResultDigest (ConvertTo-ResultCanonical $environment))
        TimePolicy=$profile.TimePolicy
        Scope=@($Group); PathBindings=@{Source='explicit-working-directory';Fixtures='unique-shard-output';Receipts='unique-run-output'}
    }
    @{ SchemaVersion=1; Key=(Get-ResultDigest (ConvertTo-ResultCanonical $inputs)); Source=$source.Key; Inputs=$inputs; InputClosureProven=($reasons.Count -eq 0); Reasons=@($reasons) }
}
