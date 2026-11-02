param(
    [Parameter(Mandatory)][string]$PvfCompanionSource,
    [Parameter(Mandatory)][string]$ArtifactDirectory
)
$ErrorActionPreference = 'Stop'
$source = (Resolve-Path -LiteralPath $PvfCompanionSource).Path.Replace('\', '/')
if (-not (Test-Path -LiteralPath (Join-Path $source 'internal/platform/pvf/repack.go'))) {
    throw 'Expected existing pvf-companion source checkout.'
}
$artifact = [System.IO.Path]::GetFullPath($ArtifactDirectory)
if (Test-Path -LiteralPath $artifact) { throw 'Isolated artifact directory must not exist.' }
New-Item -ItemType Directory -Path $artifact | Out-Null
$sourceLiteral = ConvertTo-Json -InputObject $source -Compress
$module = "module longheng.io/server/supportpatch`n`ngo 1.27.0`n`nrequire longheng.io/server v0.0.0`n`nreplace longheng.io/server => $sourceLiteral`n"
[System.IO.File]::WriteAllText((Join-Path $artifact 'go.mod'), $module)
$sum = Join-Path $source 'go.sum'
if (Test-Path -LiteralPath $sum) { Copy-Item -LiteralPath $sum -Destination (Join-Path $artifact 'go.sum') }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'PackDelta.go') -Destination (Join-Path $artifact 'main.go')
$env:GOTOOLCHAIN = 'local'
$env:GOPROXY = 'off'
$env:GOSUMDB = 'off'
Push-Location $artifact
try {
    & go build -mod=mod -o (Join-Path $artifact 'SupportApcPacker.exe') (Join-Path $artifact 'main.go') *> (Join-Path $artifact 'build.log')
    if ($LASTEXITCODE -ne 0) { throw ('Packer build failed. Read ' + (Join-Path $artifact 'build.log')) }
} finally { Pop-Location }
Write-Output ('PACKER_BUILT ' + (Join-Path $artifact 'SupportApcPacker.exe'))
