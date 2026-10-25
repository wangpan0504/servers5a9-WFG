# 无 PVF、无服务启动的脚本回归：用合成仓库和 dotnet 替身验证缓存/失败/完整模式。
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'DevelopmentCheckCache.ps1')
$root = Join-Path ([IO.Path]::GetTempPath()) ('s5a9-check-tests-' + [guid]::NewGuid().ToString('N'))
$checks = 0
function Assert([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "FAIL: $Message" }
    $script:checks++
}
function Reject([scriptblock]$Action, [string]$Message) {
    $failed = $false
    try { & $Action | Out-Null } catch { $failed = $true }
    Assert $failed $Message
}
function Write-Fixture([string]$Path, [string]$Text) {
    $target = Join-Path $root $Path
    New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
    [IO.File]::WriteAllText($target, $Text)
}
$global:DevelopmentCheckMock = @{ Core = 0; Tcp = 0; Build = 0; Fail = $false; Mutate = $false; Root = $root }
function global:dotnet {
    $global:LASTEXITCODE = 0
    $m = $global:DevelopmentCheckMock
    if ($args[0] -eq '--info') { 'Synthetic SDK 10'; return }
    if ($args[0] -eq 'build') {
        $m.Build++
        if ($m.FailBuild) { $global:LASTEXITCODE = 1; return }
        $output = $args[[Array]::IndexOf($args, '--artifacts-path') + 1]
        foreach ($project in @('DfoServer','ArchitectureCheck','PvfLib.Tests')) {
            $dir = Join-Path $output "bin/$project/debug"
            New-Item -ItemType Directory -Path $dir -Force | Out-Null
            if (-not (Test-Path (Join-Path $dir "$project.dll"))) { [IO.File]::WriteAllText((Join-Path $dir "$project.dll"), 'synthetic assembly') }
        }
        return
    }
    if ($args -contains '--selftest-core') {
        $m.Core++
        if ($m.Fail) { $global:LASTEXITCODE = 1 }
        if ($m.Mutate) { [IO.File]::AppendAllText((Join-Path $m.Root 'input.cs'), 'changed while running') }
    }
    if ($args -contains '--selftest-tcp') { $m.Tcp++ }
}
try {
    New-Item -ItemType Directory -Path $root -Force | Out-Null
    & git -C $root init --quiet
    if ($LASTEXITCODE -ne 0) { throw '无法创建合成 Git 仓库。' }
    Write-Fixture '.gitignore' "build/`n*.pvf`n"
    Write-Fixture 'input.cs' 'original input'
    Write-Fixture 'ServerS5A9.sln' 'synthetic solution'
    Write-Fixture 'Server/DfoServer/SelfTests/SampleTests.cs' 'synthetic core-only tests'
    Write-Fixture 'Server/DfoServer/SelfTests/Tcp/TcpRegressionTests.cs' '("commands", CommandTests.RunAsync),'
    foreach ($script in @('DevelopmentCheckCache.ps1','Check-Repository.ps1','Check-Development.ps1','Select-AffectedTests.ps1')) {
        Write-Fixture "Tools/$script" ([IO.File]::ReadAllText((Join-Path $PSScriptRoot $script)))
    }
    & git -C $root add -- .
    & git -C $root -c user.name=CheckTests -c user.email=check@example.invalid commit --quiet -m fixture
    if ($LASTEXITCODE -ne 0) { throw '无法保存合成 Git 基线。' }
    $artifacts = Join-Path $root 'build/check'
    $selector = Join-Path $root 'Tools/Select-AffectedTests.ps1'
    $params = @{ Paths = @('Server/DfoServer/SelfTests/SampleTests.cs'); Run = $true; ArtifactsPath = $artifacts; TimingsPath = (Join-Path $root 'build/timings.json') }
    & $selector @params | Out-Null
    Assert ($global:DevelopmentCheckMock.Core -eq 1) '首次执行 core'
    & $selector @params | Out-Null
    Assert ($global:DevelopmentCheckMock.Core -eq 1 -and $global:DevelopmentCheckMock.Build -eq 2) '相同输入只复用 core，构建仍增量检查'
    Write-Fixture 'input.cs' 'dirty input'
    & $selector @params | Out-Null
    Assert ($global:DevelopmentCheckMock.Core -eq 2) '未提交内容使缓存失效'
    $stamp = Join-Path $artifacts 'affected-core-debug.json'
    [IO.File]::WriteAllText($stamp, '{broken')
    & $selector @params | Out-Null
    Assert ($global:DevelopmentCheckMock.Core -eq 3) '损坏记录不能命中'
    $dll = Join-Path $artifacts 'bin/DfoServer/debug/DfoServer.dll'
    [IO.File]::WriteAllText($dll, 'changed assembly')
    & $selector @params | Out-Null
    Assert ($global:DevelopmentCheckMock.Core -eq 4) '程序集变动不能命中'
    Remove-Item -LiteralPath $dll
    & $selector @params | Out-Null
    Assert ($global:DevelopmentCheckMock.Core -eq 5) '缺失产物重建后不得复用不同内容记录'
    $before = $global:DevelopmentCheckMock.Core
    & $selector @params -Full | Out-Null
    Assert ($global:DevelopmentCheckMock.Core -eq $before + 1 -and $global:DevelopmentCheckMock.Tcp -eq 1) 'Full 强制 core 和 TCP 实跑'
    $global:DevelopmentCheckMock.FailBuild = $true
    Reject { & $selector @params } '构建失败必须停止'
    Assert (-not (Test-Path $stamp)) '构建失败撤销旧成功缓存'
    $global:DevelopmentCheckMock.FailBuild = $false
    $global:DevelopmentCheckMock.Fail = $true
    Reject { & $selector @params -NoCache } '失败返回错误'
    Assert (-not (Test-Path $stamp)) '失败前撤销旧成功缓存'
    $global:DevelopmentCheckMock.Fail = $false
    $global:DevelopmentCheckMock.Mutate = $true
    Reject { & $selector @params } '执行期间源码变化拒绝通过'
    Assert (-not (Test-Path $stamp)) '执行期间变化不保存缓存'
    $global:DevelopmentCheckMock.Mutate = $false
    $key = Get-CheckSourceKey $root Debug
    Assert ($key -ne (Get-CheckSourceKey $root Release)) '配置隔离'
    $artifact = Get-CheckArtifactKey (Split-Path $dll -Parent)
    Save-CheckCache $stamp $key 'sdk-a' $artifact
    Assert (-not (Test-CheckCache $stamp $key 'sdk-b' $artifact)) 'SDK/环境隔离'
    Assert (-not (Test-CheckCache $stamp $key 'sdk-a' '')) '缺产物不复用'
    $handle = [IO.File]::Open((Join-Path $artifacts 'development-check.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
    try { Reject { & $selector @params } '同一产物正在检查时拒绝并发' } finally { $handle.Dispose() }
    $check = Join-Path $root 'Tools/Check-Development.ps1'
    & $check -ArtifactsPath $artifacts | Out-Null
    $before = $global:DevelopmentCheckMock.Core
    & $check -ArtifactsPath $artifacts -Incremental | Out-Null
    Assert ($global:DevelopmentCheckMock.Core -eq $before) '显式 Incremental 复用同一 core 记录'
    & $check -ArtifactsPath $artifacts | Out-Null
    Assert ($global:DevelopmentCheckMock.Core -eq $before + 1) '默认 Check-Development 始终实跑'
    Reject { & $check -ArtifactsPath $artifacts -Incremental -PvfPath 'unused.pvf' } '资源模式禁止通过开发缓存'
    Assert (@(Get-ChildItem $artifacts -Filter '*.tmp').Count -eq 0) '原子写没有遗留临时记录'
    $repositoryCheck = Join-Path $root 'Tools/Check-Repository.ps1'
    Write-Fixture 'Docs/ok.md' "[source](../input.cs)`n[folder](../Server/)"
    & $repositoryCheck -Repository $root
    Assert $true '有效文件和含文件目录链接保持通过'
    Write-Fixture 'Docs/bad.md' "[missing](absent.md)`n[excluded](../build/)"
    Reject { & $repositoryCheck -Repository $root } '缺失与产物目录链接仍拒绝'
    Remove-Item (Join-Path $root 'Docs/bad.md')
    Write-Fixture 'bad.cs' ('first line' + "`n" + 'C' + ':' + '/machine/path')
    Reject { & $repositoryCheck -Repository $root } '机器绝对路径仍拒绝'
    Remove-Item (Join-Path $root 'bad.cs')
    Remove-Item -LiteralPath (Join-Path $root '.git') -Recurse -Force
    $before = $global:DevelopmentCheckMock.Core
    & $check -ArtifactsPath $artifacts -Incremental | Out-Null
    Assert ($global:DevelopmentCheckMock.Core -eq $before + 1) '源码归档无 Git 时仍实跑、不复用缓存'
    Write-Output "PASS $checks development cache and execution assertions"
} finally {
    Remove-Item Function:global:dotnet -ErrorAction SilentlyContinue
    Remove-Variable DevelopmentCheckMock -Scope Global -ErrorAction SilentlyContinue
    if (Test-Path $root) { Remove-Item -LiteralPath $root -Recurse -Force }
}
