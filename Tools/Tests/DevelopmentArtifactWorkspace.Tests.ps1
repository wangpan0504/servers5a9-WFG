$ErrorActionPreference='Stop'
. (Join-Path (Split-Path $PSScriptRoot -Parent) 'DevelopmentChatRateProfile.ps1')
. (Join-Path (Split-Path $PSScriptRoot -Parent) 'DevelopmentArtifactWorkspace.ps1')
$count=0
function Assert($Condition,[string]$Message) { if (-not $Condition) { throw $Message }; $script:count++ }
$root=Join-Path ([IO.Path]::GetTempPath()) ('artifact-workspace-'+[guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root)|Out-Null
try {
    # 只测路径/键/锁合同，不冒充真实MSBuild增量或性能测试。
    $script:toolchainFile=Join-Path $root 'toolchain-input'; [IO.File]::WriteAllText($script:toolchainFile,'toolchain-one')
    function Get-ProfileTreeDigest([string]$Directory) { Get-ResultFileDigest $script:toolchainFile }
    foreach ($name in @('a','b')) {
        $tree=Join-Path $root $name; [IO.Directory]::CreateDirectory($tree)|Out-Null
        & git -C $tree init -q
        [IO.File]::WriteAllText((Join-Path $tree 'project.csproj'),'<Project/>')
        [IO.File]::WriteAllText((Join-Path $tree 'sample.cs'),'class A {}')
        [IO.File]::WriteAllText((Join-Path $tree '.gitignore'),"build/`n")
        & git -C $tree add .
    }
    $a=Join-Path $root 'a';$b=Join-Path $root 'b'
    $first=Get-DevelopmentArtifactWorkspace $a Debug Default
    $second=Get-DevelopmentArtifactWorkspace $b Debug Default
    Assert ($first.Key -eq $second.Key -and $first.Path -ne $second.Path) '同构建设置跨tree不能共享可写产物路径。'
    [IO.File]::WriteAllText((Join-Path $a 'sample.cs'),'class A { int n; }')
    Assert ((Get-DevelopmentArtifactWorkspace $a Debug Default).Path -eq $first.Path) '小C#变化沿用本tree增量目录，不是沿用测试结果。'
    [IO.File]::WriteAllText((Join-Path $a 'project.csproj'),'<Project Sdk="Microsoft.NET.Sdk"/>')
    Assert ((Get-DevelopmentArtifactWorkspace $a Debug Default).Key -ne $first.Key) '项目/导入图变化隔离构建状态。'
    Assert ((Get-DevelopmentArtifactWorkspace $b Debug CloudLowMemory).Key -ne $second.Key) '编译预算配置必须入设置key。'
    [IO.File]::WriteAllText($script:toolchainFile,'toolchain-two')
    Assert ((Get-DevelopmentArtifactWorkspace $b Debug Default).Key -ne $second.Key) '实际工具链变化隔离构建状态。'
    $lease=Enter-DevelopmentArtifactLock $first.Path 0
    try {
        $blocked=$false; try { Enter-DevelopmentArtifactLock $first.Path 0 | Out-Null } catch { $blocked=$true }
        Assert $blocked '构建及运行期间其他调用不能改写相同产物。'
    } finally { Exit-DevelopmentArtifactLock $lease $true }
    $lease=Enter-DevelopmentArtifactLock $first.Path 0
    Exit-DevelopmentArtifactLock $lease $false
    $blocked=$false;try { Enter-DevelopmentArtifactLock $first.Path 0 | Out-Null } catch { $blocked=$true }
    Assert $blocked '后代终态未知留下隔离记录，不按PID/TTL自动接管。'
    $outside=Join-Path $root 'outside';[IO.Directory]::CreateDirectory($outside)|Out-Null
    $link=Join-Path $root 'linked';New-Item -ItemType SymbolicLink -Path $link -Target $outside|Out-Null
    $blocked=$false;try{Enter-DevelopmentArtifactLock (Join-Path $link 'nested') 0|Out-Null}catch{$blocked=$true}
    Assert $blocked '任一父目录符号链接必须拒绝，不能跨tree写产物。'
    $ordinary=Join-Path $root 'ordinary';[IO.Directory]::CreateDirectory($ordinary)|Out-Null
    [IO.File]::WriteAllText((Join-Path $outside 'lock'),'')
    New-Item -ItemType SymbolicLink -Path (Join-Path $ordinary 'execution.lock') -Target (Join-Path $outside 'lock')|Out-Null
    $blocked=$false;try{Enter-DevelopmentArtifactLock $ordinary 0|Out-Null}catch{$blocked=$true}
    Assert $blocked '锁文件本身链接也必须拒绝。'
    $dangling=Join-Path $root 'dangling';[IO.Directory]::CreateDirectory($dangling)|Out-Null
    New-Item -ItemType SymbolicLink -Path (Join-Path $dangling 'execution.lock') -Target (Join-Path $outside 'absent')|Out-Null
    $blocked=$false;try{Enter-DevelopmentArtifactLock $dangling 0|Out-Null}catch{$blocked=$true}
    Assert ($blocked -and -not (Test-Path (Join-Path $outside 'absent'))) '悬空锁链接也不得创建外部目标。'
    $linkedBuild=Join-Path $root 'linked-build';[IO.Directory]::CreateDirectory($linkedBuild)|Out-Null
    New-Item -ItemType SymbolicLink -Path (Join-Path $linkedBuild 'obj') -Target $outside|Out-Null
    $blocked=$false;try{Start-DevelopmentIncrementalBuild $linkedBuild (Get-DevelopmentBuildInputs $b)|Out-Null}catch{$blocked=$true}
    Assert $blocked '已有obj链接必须在MSBuild之前拒绝，不能先Rebuild再检查。'
    $beforeParent=(Get-DevelopmentArtifactWorkspace $b Debug Default).Key
    [IO.File]::WriteAllText((Join-Path $root 'Directory.Build.props'),'<Project/>')
    $blocked=$false;try{Get-DevelopmentArtifactWorkspace $b Debug Default|Out-Null}catch{$blocked=$true}
    Assert $blocked '未知祖先导入可能指向外部文件，必须拒绝而非只hash一层假闭合。'
    $build=Join-Path $root 'incremental';[IO.Directory]::CreateDirectory($build)|Out-Null
    $inputs=Get-DevelopmentBuildInputs $b
    Assert (-not (Start-DevelopmentIncrementalBuild $build $inputs).Rebuild) '空目录首建用正常Build。'
    [IO.Directory]::CreateDirectory((Join-Path $build 'bin'))|Out-Null
    [IO.File]::WriteAllText((Join-Path $build 'bin/out.dll'),'synthetic output')
    Complete-DevelopmentIncrementalBuild $build $inputs
    Assert (-not (Start-DevelopmentIncrementalBuild $build $inputs).Rebuild) '相同内容的成功前置允许MSBuild增量判定。'
    Complete-DevelopmentIncrementalBuild $build $inputs
    $differentEnvironment=$inputs.Clone();$differentEnvironment.BuildEnvironmentDigest='different-transport-environment'
    Assert (Start-DevelopmentIncrementalBuild $build $differentEnvironment).Rebuild '目录相同但完整构建env漂移必须Rebuild，不能声称跨调用温构建。'
    Complete-DevelopmentIncrementalBuild $build $inputs
    $file=Join-Path $b 'sample.cs';$oldTime=(Get-Item $file).LastWriteTimeUtc
    [IO.File]::WriteAllText($file,'class FreshEdit {}');[IO.File]::SetLastWriteTimeUtc($file,[DateTime]::UtcNow.AddSeconds(5))
    $fresh=Get-DevelopmentBuildInputs $b
    Assert (-not (Start-DevelopmentIncrementalBuild $build $fresh).Rebuild) '正常新鲜mtime的小编辑仍交MSBuild增量，不总是冷建。'
    Complete-DevelopmentIncrementalBuild $build $fresh
    [IO.File]::WriteAllText($file,'class Changed {}');[IO.File]::SetLastWriteTimeUtc($file,$oldTime)
    $changed=Get-DevelopmentBuildInputs $b
    Assert (Start-DevelopmentIncrementalBuild $build $changed).Rebuild '内容改变但mtime保留必须Rebuild，不能旧DLL贴新source。'
    Complete-DevelopmentIncrementalBuild $build $changed
    Remove-Item $file; & git -C $b rm --cached -q sample.cs
    $deleted=Get-DevelopmentBuildInputs $b
    Assert (Start-DevelopmentIncrementalBuild $build $deleted).Rebuild '删除源码必须Rebuild以排除旧编译输出。'
    Complete-DevelopmentIncrementalBuild $build $deleted
    [IO.File]::WriteAllText((Join-Path $build 'bin/out.dll'),'externally changed output')
    Assert (Start-DevelopmentIncrementalBuild $build $deleted).Rebuild '实际产物内容被改即使源码未变也不能沿用旧编译身份。'
    Complete-DevelopmentIncrementalBuild $build $deleted
    Start-DevelopmentIncrementalBuild $build $deleted|Out-Null
    Assert (Start-DevelopmentIncrementalBuild $build $deleted).Rebuild '未完成构建marker不能假称成功可增量。'
    Write-Output "PASS $count artifact workspace assertions"
} finally { Remove-Item -LiteralPath $root -Recurse -Force }
