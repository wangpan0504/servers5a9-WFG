param(
    [ValidateSet('Debug','Release')][string]$Configuration = 'Debug',
    [string]$ArtifactsPath = (Join-Path (Split-Path $PSScriptRoot -Parent) 'build/check'),
    [string]$PvfPath,
    # 显式开发模式：只复用内容完全匹配的 core 成功记录；默认每次实际执行。
    [switch]$Incremental
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$output = [IO.Path]::GetFullPath($ArtifactsPath)
if ($Incremental -and $PvfPath) { throw 'Incremental 仅用于无资源开发检查；完整资源验收不能复用结果。' }
if ($PvfPath) {
    $PvfPath = (Resolve-Path -LiteralPath $PvfPath -ErrorAction Stop).ProviderPath
    if (-not (Test-Path -LiteralPath $PvfPath -PathType Leaf)) { throw 'PvfPath must point to a resource file.' }
}
. (Join-Path $PSScriptRoot 'DevelopmentCheckCache.ps1')
function Invoke-Dotnet([string[]]$Arguments) {
    $elapsed = [Diagnostics.Stopwatch]::StartNew()
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed (exit $LASTEXITCODE)" }
    Write-Output ('EXECUTED {0:F1}s: {1}' -f $elapsed.Elapsed.TotalSeconds, ($Arguments -join ' '))
}
$total = [Diagnostics.Stopwatch]::StartNew()
New-Item -ItemType Directory -Path $output -Force | Out-Null
$lock = $null
$stamp = Join-Path $output ("affected-core-{0}.json" -f $Configuration.ToLowerInvariant())
try {
    try { $lock = [IO.File]::Open((Join-Path $output 'development-check.lock'), 'OpenOrCreate', 'ReadWrite', 'None') }
    catch { throw '同一产物目录正在执行检查；请等待结束，不能并发改写程序集。' }
    $elapsed = [Diagnostics.Stopwatch]::StartNew()
    & (Join-Path $PSScriptRoot 'Check-Repository.ps1') -Repository $root
    Write-Output ('EXECUTED repository {0:F1}s' -f $elapsed.Elapsed.TotalSeconds)
    $source = Get-CheckSourceKey $root $Configuration
    $environment = Get-CheckEnvironmentKey
    # 保留 MSBuild 自身增量/还原判定；单节点与 CI 一致，避免多工作树争用内存。
    Invoke-Dotnet @('build', (Join-Path $root 'ServerS5A9.sln'), '-c', $Configuration, '--nologo', '--artifacts-path', $output, '-m:1', '-nr:false')
    $variant = $Configuration.ToLowerInvariant()
    $directory = Join-Path $output "bin/DfoServer/$variant"
    $server = Join-Path $directory 'DfoServer.dll'
    $artifact = Get-CheckArtifactKey $directory
    Assert-CheckInputsUnchanged $root $Configuration $source $directory $artifact
    foreach ($project in @('ArchitectureCheck', 'PvfLib.Tests')) {
        Invoke-Dotnet @((Join-Path $output "bin/$project/$variant/$project.dll"))
    }
    if ($PvfPath) {
        # 完整入口已包含核心自测；联合验证只运行一次，保留全部资源与事务覆盖。
        Invoke-Dotnet @($server, '--pvf-path', $PvfPath, '--selftest')
    } elseif ($Incremental -and (Test-CheckCache $stamp $source $environment $artifact)) {
        Write-Output 'CACHED --selftest-core：源码、SDK/环境与程序集内容完全匹配；不是本轮重新执行。'
    } else {
        Remove-Item -LiteralPath $stamp -ErrorAction SilentlyContinue
        Invoke-Dotnet @($server, '--selftest-core')
        Assert-CheckInputsUnchanged $root $Configuration $source $directory $artifact
        Save-CheckCache $stamp $source $environment $artifact
    }
    Assert-CheckInputsUnchanged $root $Configuration $source $directory $artifact
    if ($Incremental) { Write-Output 'Incremental development checks passed; cached stages are not a fresh full verification.' }
    elseif ($PvfPath) { Write-Output 'Development and resource checks passed; TCP tests run separately.' }
    else { Write-Output 'Development checks passed; resource integration and TCP tests run separately.' }
} catch {
    if ($lock) { Remove-Item -LiteralPath $stamp -ErrorAction SilentlyContinue }
    throw
} finally {
    if ($lock) { $lock.Dispose() }
    Write-Output ('Check elapsed: {0:F1}s (see stage results; elapsed time does not imply success).' -f $total.Elapsed.TotalSeconds)
}
