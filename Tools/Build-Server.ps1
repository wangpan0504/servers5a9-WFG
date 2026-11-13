#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$OutputDirectory = '',
    [string]$Version = '',
    [ValidateSet('win-x64','linux-x64','linux-arm64','osx-x64','osx-arm64')][string]$Runtime = 'win-x64',
    [Alias('PackageVersion')][string]$PackageVersionAlias = '',
    [switch]$Zip
)
$ErrorActionPreference = 'Stop'
$moduleRepository = Split-Path -Parent $PSScriptRoot
$moduleCommit = & git -C $moduleRepository rev-parse HEAD
if ($LASTEXITCODE -ne 0 -or $moduleCommit -notmatch '^[a-f0-9]{40}$') { throw 'Unable to determine server source identity.' }
if ($PackageVersionAlias) { $Version = $PackageVersionAlias; $Zip = $true }
if (-not $Version) { $Version = 'v' + (Get-Date -Format 'yyyy.MM.dd-HHmmss') }
if (-not $OutputDirectory) { $OutputDirectory = if ($Zip) { 'dist/releases/payload-' + $Version } else { 'build/server-dll' } }
if (-not [IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory = Join-Path $moduleRepository $OutputDirectory }
$moduleOutput = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $moduleOutput) { throw 'Output directory must not already exist; use a new output directory.' }
$moduleModified = [bool]@(& git -C $moduleRepository status --porcelain --untracked-files=normal)
if ($LASTEXITCODE -ne 0) { throw 'Unable to inspect source state.' }
if ($Version -notmatch '^[a-zA-Z0-9][a-zA-Z0-9.+-]{0,79}$') { throw 'Invalid module version.' }
& dotnet publish (Join-Path $moduleRepository 'Server/DfoServer/DfoServer.csproj') -c Release -r $Runtime `
    --self-contained false -p:ServerDllPackage=true -p:UseAppHost=false -p:PublishSingleFile=false -p:DebugType=none -p:DebugSymbols=false -o $moduleOutput
if ($LASTEXITCODE -ne 0) { throw 'Server module publish failed.' }
foreach ($moduleNotice in @('LICENSE','THIRD-PARTY-NOTICES.md','THIRD-PARTY.md')) {
    Copy-Item -LiteralPath (Join-Path $moduleRepository $moduleNotice) -Destination $moduleOutput
}
Copy-Item -LiteralPath (Join-Path $moduleRepository 'LICENSES') -Destination (Join-Path $moduleOutput 'LICENSES') -Recurse
foreach ($moduleScript in @('start-server.bat','start-server.sh')) {
    Copy-Item -LiteralPath (Join-Path $moduleRepository ('Tools/ServerDll/' + $moduleScript)) -Destination $moduleOutput
}
$moduleFiles = @(
    foreach ($moduleFile in Get-ChildItem -LiteralPath $moduleOutput -File -Recurse | Sort-Object FullName) {
        $moduleRelative = [IO.Path]::GetRelativePath($moduleOutput, $moduleFile.FullName).Replace('\', '/')
        if ($moduleRelative -match '\.(exe|pdb)$' -or
            $moduleRelative -match '^(Data|Logs|Resources|Sqlite)/') { throw "Unexpected standalone entry or player resource: $moduleRelative" }
        [ordered]@{ path=$moduleRelative; size=$moduleFile.Length; sha256=(Get-FileHash -LiteralPath $moduleFile.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    }
)
foreach ($moduleRequired in @('DfoServer.dll','DfoServer.deps.json','DfoServer.runtimeconfig.json','PvfLib.dll','start-server.bat','start-server.sh')) {
    if ($moduleRequired -cnotin $moduleFiles.path) { throw "Missing module dependency: $moduleRequired" }
}
$moduleManifest = [ordered]@{ schema=1; hostApi=1; entry='DfoServer.dll'; version=$Version; commit=$moduleCommit; modified=$moduleModified; runtime=$Runtime; files=$moduleFiles }
[IO.File]::WriteAllText((Join-Path $moduleOutput 'server-module.json'), ($moduleManifest | ConvertTo-Json -Depth 6) + "`n", [Text.UTF8Encoding]::new($false))
if ($Zip) {
    $moduleArchive = Join-Path (Split-Path -Parent $moduleOutput) "S5A9Server-$Version-$Runtime.zip"
    if (Test-Path -LiteralPath $moduleArchive) { throw 'Module archive already exists.' }
    [IO.Compression.ZipFile]::CreateFromDirectory($moduleOutput, $moduleArchive)
    [IO.File]::WriteAllText(($moduleArchive + '.sha256'), ((Get-FileHash -LiteralPath $moduleArchive -Algorithm SHA256).Hash.ToLowerInvariant() + "  " + [IO.Path]::GetFileName($moduleArchive) + "`n"), [Text.UTF8Encoding]::new($false))
}
Write-Output "Server module: $moduleOutput"
Write-Output 'Install while stopped. Start independently with start-server.bat or dotnet DfoServer.dll; launcher use is optional. Requires .NET 10 ASP.NET Core Runtime matching the target architecture. No server EXE is produced.'
