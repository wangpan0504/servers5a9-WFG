param(
    [ValidateSet('Debug','Release')][string]$Configuration = 'Debug',
    [string]$ArtifactsPath = (Join-Path (Split-Path $PSScriptRoot -Parent) 'build/check'),
    [string]$PvfPath
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$output = [IO.Path]::GetFullPath($ArtifactsPath)
if ($PvfPath) {
    $PvfPath = (Resolve-Path -LiteralPath $PvfPath -ErrorAction Stop).ProviderPath
    if (-not (Test-Path -LiteralPath $PvfPath -PathType Leaf)) { throw 'PvfPath must point to a resource file.' }
}
function Invoke-Dotnet([string[]]$Arguments) {
    $elapsed = [Diagnostics.Stopwatch]::StartNew()
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed (exit $LASTEXITCODE)" }
    Write-Output ('Check stage passed in {0:F1}s: {1}' -f $elapsed.Elapsed.TotalSeconds, ($Arguments -join ' '))
}
& (Join-Path $PSScriptRoot 'Check-Repository.ps1') -Repository $root
Invoke-Dotnet @('build', (Join-Path $root 'ServerS5A9.sln'), '-c', $Configuration, '--nologo', '--artifacts-path', $output, '-nr:false')
$variant = $Configuration.ToLowerInvariant()
foreach ($project in @('ArchitectureCheck', 'PvfLib.Tests')) {
    Invoke-Dotnet @((Join-Path $output "bin/$project/$variant/$project.dll"))
}
$server = Join-Path $output "bin/DfoServer/$variant/DfoServer.dll"
# 不常驻 MSBuild 节点，避免多个工作树并行检查后遗留占内存的 dotnet 进程。
if ($PvfPath) {
    # 完整入口已包含核心自测；联合验证只运行一次，保留全部资源与事务覆盖。
    Invoke-Dotnet @($server, '--pvf-path', $PvfPath, '--selftest')
    Write-Output 'Development and resource checks passed; TCP tests run separately (Tools/Select-AffectedTests.ps1 picks affected TCP groups).'
} else {
    Invoke-Dotnet @($server, '--selftest-core')
    Write-Output 'Development checks passed; resource integration and TCP tests run separately (Tools/Select-AffectedTests.ps1 picks affected TCP groups).'
}