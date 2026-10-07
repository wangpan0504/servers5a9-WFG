param(
    [ValidateSet('Debug','Release')][string]$Configuration = 'Debug',
    [string]$ArtifactsPath = (Join-Path (Split-Path $PSScriptRoot -Parent) 'build/check')
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$output = [IO.Path]::GetFullPath($ArtifactsPath)
function Invoke-Dotnet([string[]]$Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed (exit $LASTEXITCODE)" }
}
& (Join-Path $PSScriptRoot 'Check-Repository.ps1') -Repository $root
Invoke-Dotnet @('build', (Join-Path $root 'ServerS5A9.sln'), '-c', $Configuration, '--nologo', '--artifacts-path', $output)
$variant = $Configuration.ToLowerInvariant()
foreach ($project in @('ArchitectureCheck', 'PvfLib.Tests')) {
    Invoke-Dotnet @((Join-Path $output "bin/$project/$variant/$project.dll"))
}
Invoke-Dotnet @((Join-Path $output "bin/DfoServer/$variant/DfoServer.dll"), '--selftest-core')
Write-Output 'Development checks passed; resource integration and TCP tests run separately.'
