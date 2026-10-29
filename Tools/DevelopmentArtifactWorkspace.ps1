# 仅同一工作树保留MSBuild增量产物；这不是验证结果缓存，也不跨工作树共享bin/obj。
function Assert-DevelopmentPlainPath([string]$Path) {
    $current=[IO.Path]::GetFullPath($Path)
    while ($current) {
        $item=$null
        try { $item=Get-Item -LiteralPath $current -Force -ErrorAction Stop }
        catch { if($_.CategoryInfo.Category -ne [Management.Automation.ErrorCategory]::ObjectNotFound){throw} }
        if ($item -and ($item.LinkType -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint))) { throw '增量产物路径或输入路径包含链接；不能跨目录共享可写产物。' }
        $parent=[IO.Path]::GetDirectoryName($current)
        if ($parent -eq $current) { break }; $current=$parent
    }
}
function Get-DevelopmentDirectoryEnvironmentMap($FullEnvironment) {
    # 仅稳定目录不因已观察的宿主transport元数据复制；完整身份仍保存且漂移强制Rebuild。
    $directoryOnlyExcluded=@('BUNDLE_HTTPS_PROXY','BUNDLE_HTTP_PROXY','CODEX_TOOL_CALL_ID','DOCKER_HTTPS_PROXY','DOCKER_HTTP_PROXY','FTP_PROXY','HTTPS_PROXY','HTTP_PROXY','NO_PROXY','NPM_CONFIG_HTTPS_PROXY','NPM_CONFIG_HTTP_PROXY','NPM_CONFIG_NOPROXY','NPM_CONFIG_PROXY','PIP_PROXY','WSS_PROXY','WS_PROXY','YARN_HTTPS_PROXY','YARN_HTTP_PROXY','all_proxy','http_proxy','https_proxy','no_proxy','npm_config_noproxy','wss_proxy')
    $environment=[Collections.Specialized.OrderedDictionary]::new([StringComparer]::Ordinal)
    foreach($name in $FullEnvironment.Keys){if($name -cnotin $directoryOnlyExcluded){$environment[$name]=$FullEnvironment[$name]}}
    return $environment
}
function Get-DevelopmentArtifactWorkspace([string]$Repository, [string]$Configuration, [string]$BuildProfile) {
    Assert-DevelopmentPlainPath $Repository
    $source = Get-ResultSourceManifest $Repository
    $ignored=@(& git -C $Repository -c core.quotepath=false ls-files --others --ignored --exclude-standard)
    if($LASTEXITCODE -ne 0){throw '无法核验ignored构建输入。'}
    if(@($ignored | Where-Object {$_ -notmatch '(^build/|/(bin|obj)/)' -and $_ -match '^(Server/|Tools/(PvfLib|ArchitectureCheck|PvfLib.Tests|ServerLauncher)/)'}).Count){throw '存在未知ignored编译输入；不能复用增量目录。'}
    $projects = [ordered]@{}
    foreach ($path in $source.Files.Keys) {
        if ($path -match '\.(csproj|sln|props|targets)$' -or [IO.Path]::GetFileName($path) -in @('global.json','NuGet.Config','nuget.config','packages.lock.json')) { $projects[$path]=$source.Files[$path] }
    }
    $parent=[IO.Directory]::GetParent([IO.Path]::GetFullPath($Repository));$level=0
    while($parent){
        foreach($name in @('Directory.Build.props','Directory.Build.targets','Directory.Packages.props','global.json')){
            $external=Join-Path $parent.FullName $name
            if(Test-Path -LiteralPath $external){Assert-DevelopmentPlainPath $external;throw '存在未闭合的祖先构建配置/外部导入；请用原严格入口，不能沿用增量产物。'}
        }
        $parent=$parent.Parent;$level++
    }
    $packages=if($env:NUGET_PACKAGES){$env:NUGET_PACKAGES}else{Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)) '.nuget/packages'}
    $packageDigest=if(Test-Path -LiteralPath $packages){Assert-DevelopmentPlainPath $packages;Get-ProfileTreeDigest $packages}else{'not-restored'}
    $dotnet = (Get-Command dotnet -ErrorAction Stop).Source
    $fullEnvironment=Get-DevelopmentEnvironmentMap
    $environment=Get-DevelopmentDirectoryEnvironmentMap $fullEnvironment
    $settings = @{SchemaVersion=1; DirectoryEnvironmentPolicy='observed-transport-directory-only-v1'; Packages=$packageDigest; EnvironmentDigest=(Get-ResultDigest (ConvertTo-ResultCanonical $environment)); Configuration=$Configuration; Projects=$projects; Toolchain=(Get-ProfileTreeDigest (Split-Path $dotnet -Parent)); CompilerEnvironment=(Get-DevelopmentCompilerEnvironment $BuildProfile); Arguments=@('-m:1','-nr:false','-p:UseSharedCompilation=false','-p:RunAnalyzers=true')}
    $key = Get-ResultDigest (ConvertTo-ResultCanonical $settings)
    $path = Join-Path ([IO.Path]::GetFullPath($Repository)) ('build/development/'+$Configuration.ToLowerInvariant()+'/'+$key)
    Assert-DevelopmentPlainPath $path
    @{Key=$key; Path=$path; Settings=$settings; FullEnvironmentDigest=(Get-ResultDigest (ConvertTo-ResultCanonical $fullEnvironment))}
}
function Test-DevelopmentArtifactWorkspaceUnchanged([string]$Repository,[string]$Configuration,[string]$BuildProfile,$Expected) {
    $current=Get-DevelopmentArtifactWorkspace $Repository $Configuration $BuildProfile
    $current.Key -ceq $Expected.Key -and $current.FullEnvironmentDigest -ceq $Expected.FullEnvironmentDigest
}
function Enter-DevelopmentArtifactLock([string]$Directory, [ValidateRange(0,86400)][int]$WaitSeconds=600) {
    Assert-DevelopmentPlainPath $Directory
    [IO.Directory]::CreateDirectory($Directory) | Out-Null
    Assert-DevelopmentPlainPath $Directory
    $path = Join-Path $Directory 'execution.lock'; Assert-DevelopmentPlainPath $path; $watch=[Diagnostics.Stopwatch]::StartNew()
    do {
        $stream=$null
        try { $stream=[IO.File]::Open($path,'OpenOrCreate','ReadWrite','None') } catch [IO.IOException] { }
        if ($stream) {
            try {
                if ($stream.Length -ne 0) { throw '该增量产物仍有未确认终态记录；禁止清锁或改写运行包。' }
                $bytes=[Text.Encoding]::UTF8.GetBytes((ConvertTo-ResultCanonical @{Owner=[guid]::NewGuid().ToString('N'); ProcessId=$PID; StartedUtc=[DateTimeOffset]::UtcNow.ToString('O')}))
                $stream.Write($bytes); $stream.Flush($true); return $stream
            } catch { $stream.Dispose(); throw }
        }
        if ($watch.Elapsed.TotalSeconds -ge $WaitSeconds) { throw '等待本工作树产物独占锁超时；未启动构建。' }
        Start-Sleep -Milliseconds 50
    } while ($true)
}
function Exit-DevelopmentArtifactLock([IO.FileStream]$Lease, [bool]$DescendantsTerminated) {
    # 未知终态保留记录，即使进程退出释放OS锁也不能自动接管。
    try { if ($DescendantsTerminated) { $Lease.SetLength(0); $Lease.Flush($true) } } finally { $Lease.Dispose() }
}

function Get-DevelopmentBuildInputs([string]$Repository,[string]$BuildEnvironmentDigest='') {
    $source=Get-ResultSourceManifest $Repository;$files=[ordered]@{}
    foreach($path in $source.Files.Keys){$files[$path]=@{Digest=$source.Files[$path].Digest;Mode=$source.Files[$path].Mode;WriteTicks=(Get-Item -LiteralPath (Join-Path $Repository $path) -Force).LastWriteTimeUtc.Ticks}}
    @{Source=$source.Key;Files=$files;BuildEnvironmentDigest=$BuildEnvironmentDigest}
}
function Get-DevelopmentOutputIdentity([string]$Directory) {
    $files=[ordered]@{}
    foreach($name in @('bin','obj')){
        $path=Join-Path $Directory $name;Assert-DevelopmentPlainPath $path
        if(Test-Path -LiteralPath $path){
            foreach($file in Get-ChildItem -LiteralPath $path -Recurse -Force){
                if(($file.LinkType -and ($file.PSIsContainer -or $file.LinkType -cne 'HardLink')) -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw '构建缓存包含未闭合链接。'}
                if(-not $file.PSIsContainer){$files[[IO.Path]::GetRelativePath($Directory,$file.FullName)]=Get-ResultFileDigest $file.FullName}
            }
        }
    }
    Get-ResultDigest (ConvertTo-ResultCanonical $files)
}
# fixture通过硬链接只读装配运行包。下一次写入前仅替换本工作树目录项，绝不改历史fixture inode。
function Separate-DevelopmentOutputHardLinks([string]$Directory) {
    Get-DevelopmentOutputIdentity $Directory | Out-Null # 先检查完整bin/obj，拒绝任何重定向链接。
    foreach($name in @('bin','obj')) {
        $path=Join-Path $Directory $name
        if(-not (Test-Path -LiteralPath $path)){continue}
        foreach($file in Get-ChildItem -LiteralPath $path -Recurse -Force -File) {
            if($file.LinkType -cne 'HardLink'){continue}
            $digest=Get-ResultFileDigest $file.FullName
            $time=$file.LastWriteTimeUtc
            $mode=if(-not $IsWindows){[IO.File]::GetUnixFileMode($file.FullName)}else{$null}
            $temp=Join-Path $file.DirectoryName ([guid]::NewGuid().ToString('N')+'.private-copy')
            try {
                [IO.File]::Copy($file.FullName,$temp,$false)
                if((Get-ResultFileDigest $temp) -cne $digest -or (Get-ResultFileDigest $file.FullName) -cne $digest){throw '硬链接内容在写前分离期间变化；未启动构建。'}
                [IO.File]::SetLastWriteTimeUtc($temp,$time)
                if(-not $IsWindows){[IO.File]::SetUnixFileMode($temp,$mode)}
                [IO.File]::Move($temp,$file.FullName,$true)
                Assert-DevelopmentPlainPath $file.FullName
            } finally { if(Test-Path -LiteralPath $temp){[IO.File]::Delete($temp)} }
        }
    }
}
function Start-DevelopmentIncrementalBuild([string]$Directory, $Inputs) {
    $state=Join-Path $Directory 'build-state.json';$marker=Join-Path $Directory 'build-started.json'
    Assert-DevelopmentPlainPath $state;Assert-DevelopmentPlainPath $marker
    Separate-DevelopmentOutputHardLinks $Directory
    $currentOutputs=Get-DevelopmentOutputIdentity $Directory # 必须在异常降级逻辑外，链接不能降级成Rebuild后照写外部。
    $rebuild=(Test-Path -LiteralPath $marker)
    if(Test-Path -LiteralPath $state){
        try {
            $saved=Get-Content -LiteralPath $state -Raw|ConvertFrom-Json -AsHashtable
            if($saved.Digest -cne (Get-ResultDigest (ConvertTo-ResultCanonical $saved.Payload))){throw '损坏构建快照'}
            $old=$saved.Payload
            if($old.Inputs.BuildEnvironmentDigest -cne $Inputs.BuildEnvironmentDigest){$rebuild=$true}
            if($old.OutputIdentity -cne $currentOutputs){$rebuild=$true}
            foreach($path in $old.Inputs.Files.Keys){if(-not $Inputs.Files.Contains($path)){$rebuild=$true}}
            foreach($path in $Inputs.Files.Keys){
                $file=$Inputs.Files[$path]
                if(-not $old.Inputs.Files.ContainsKey($path) -or $old.Inputs.Files[$path].Digest -cne $file.Digest -or $old.Inputs.Files[$path].Mode -cne $file.Mode){
                    if($file.WriteTicks -le $old.MaxOutputWriteTicks){$rebuild=$true}
                }
            }
        } catch {$rebuild=$true}
    } elseif((Test-Path (Join-Path $Directory 'bin')) -or (Test-Path (Join-Path $Directory 'obj'))){$rebuild=$true}
    [IO.File]::WriteAllText($marker,(ConvertTo-ResultCanonical @{Source=$Inputs.Source;StartedUtc=[DateTimeOffset]::UtcNow.ToString('O');Rebuild=$rebuild}))
    @{Rebuild=$rebuild;Reason=$(if($rebuild){'content-or-interrupted-state-requires-rebuild'}else{'msbuild-incremental-inputs-current'})}
}
function Complete-DevelopmentIncrementalBuild([string]$Directory, $Inputs) {
    $max=[long]0
    foreach($name in @('bin','obj')){
        $path=Join-Path $Directory $name;Assert-DevelopmentPlainPath $path
        if(Test-Path $path){
            foreach($file in Get-ChildItem -LiteralPath $path -Recurse -Force){
                if($file.LinkType -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw '构建产物包含未闭合链接。'}
                if(-not $file.PSIsContainer){$max=[Math]::Max($max,$file.LastWriteTimeUtc.Ticks)}
            }
        }
    }
    $payload=@{SchemaVersion=1;Inputs=$Inputs;MaxOutputWriteTicks=$max;OutputIdentity=(Get-DevelopmentOutputIdentity $Directory)}
    $state=Join-Path $Directory 'build-state.json';Assert-DevelopmentPlainPath $state
    $temp=Join-Path $Directory ([guid]::NewGuid().ToString('N')+'.tmp')
    [IO.File]::WriteAllText($temp,(ConvertTo-ResultCanonical @{Payload=$payload;Digest=(Get-ResultDigest (ConvertTo-ResultCanonical $payload))}))
    [IO.File]::Move($temp,$state,$true)
    [IO.File]::Delete((Join-Path $Directory 'build-started.json'))
}
