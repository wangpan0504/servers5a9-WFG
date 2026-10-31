<#
.SYNOPSIS
Build the self-contained Windows x64 server and launcher, preserving data and logs.
.PARAMETER OutputDirectory
Runtime directory, default dist/win-x64. Relative paths are based on the repository root.
.PARAMETER PvfPath
Optional initial Script.pvf; copied only when missing, never replacing existing resources.
.PARAMETER SingleFile
Bundle launcher and server into S5A9Server.exe, default dist/win-x64-single.
.PARAMETER PackageVersion
With SingleFile, create a ZIP and SHA256 in an empty directory (default dist/releases), without runtime data.
#>
[CmdletBinding()]
param(
    [ValidateNotNullOrEmpty()][string]$OutputDirectory = 'dist/win-x64',
    [string]$PvfPath,
    [switch]$SingleFile,
    [ValidatePattern('^v[0-9]+\.[0-9]+\.[0-9]+(?:-[A-Za-z0-9]+(?:[.-][A-Za-z0-9]+)*)?$')]
    [ValidateLength(1, 64)][string]$PackageVersion
)
$ErrorActionPreference = 'Stop'
# ASCII source avoids Windows PowerShell 5.1 ANSI decoding of UTF-8 without BOM.
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$OutputEncoding = [Console]::OutputEncoding
$root = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$package = $PSBoundParameters.ContainsKey('PackageVersion')
$windows = [Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT
if ($package -and (-not $SingleFile -or $PSBoundParameters.ContainsKey('PvfPath'))) {
    throw 'Packaging requires -SingleFile and does not accept PvfPath.'
}
if ($SingleFile -and -not $PSBoundParameters.ContainsKey('OutputDirectory')) {
    $OutputDirectory = if ($package) { 'dist/releases' } else { 'dist/win-x64-single' }
}
$program = if ($SingleFile) { 'S5A9Server.exe' } else { 'DfoServer.exe' }
$licensePaths = @('LICENSE', 'THIRD-PARTY-NOTICES.md', 'THIRD-PARTY.md', 'LICENSES')

function Get-FullPath([string]$Path) {
    if (-not [IO.Path]::IsPathRooted($Path)) { $Path = Join-Path $root $Path }
    return [IO.Path]::GetFullPath($Path).TrimEnd([IO.Path]::DirectorySeparatorChar)
}

function Test-Within([string]$Path, [string]$Parent) {
    return $Path.Equals($Parent, [StringComparison]::OrdinalIgnoreCase) -or
        $Path.StartsWith($Parent.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar,
            [StringComparison]::OrdinalIgnoreCase)
}

function Assert-NoLinks([string]$Path) {
    $current = $Path
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Runtime directories and their parents must not be links: $current"
            }
        }
        $current = Split-Path $current -Parent
    }
}

function Write-Step([string]$Message) {
    Write-Host ("[{0}] {1}" -f (Get-Date -Format 'HH:mm:ss'), $Message)
}

function Invoke-Dotnet([string[]]$Arguments) {
    $label = "dotnet $($Arguments[0]) $(Split-Path -Leaf $Arguments[1])"
    $watch = [Diagnostics.Stopwatch]::StartNew()
    Write-Step "Start: $label"
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed, exit code: $LASTEXITCODE" }
    Write-Step ("Done: {0}, elapsed {1:N1} seconds" -f $label, $watch.Elapsed.TotalSeconds)
}

# Self-checks have a deadline; terminate their process tree on timeout and report failure.
function Invoke-SelfCheck([string]$Name, [string]$FilePath, [string[]]$Arguments, [int]$TimeoutSeconds = 60) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    Write-Step "Self-check: $Name (timeout $TimeoutSeconds seconds)"
    $process = Start-Process -FilePath $FilePath -ArgumentList $Arguments -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        try {
            # .NET Framework has no Process.Kill(bool); taskkill is provided by Windows.
            & (Join-Path $env:SystemRoot 'System32/taskkill.exe') /PID $process.Id /T /F | Out-Null
            if ($LASTEXITCODE -ne 0 -and -not $process.HasExited) { throw 'taskkill failed' }
        } catch { Write-Warning "Failed to terminate self-check: $($_.Exception.Message)" }
        throw "Self-check timed out: $Name exceeded $TimeoutSeconds seconds (PID $($process.Id))."
    }
    $process.WaitForExit()
    Write-Step ("Self-check done: {0}, exit code {1}, elapsed {2:N1} seconds" -f $Name, $process.ExitCode, $watch.Elapsed.TotalSeconds)
    return $process
}

function Remove-Stage {
    $resolvedStage = (Resolve-Path -LiteralPath $stage).Path
    if (-not (Test-Within $resolvedStage $buildRoot) -or $resolvedStage -eq $buildRoot) {
        throw 'Temporary directory boundary check failed; nothing removed.'
    }
    # Preserve artifacts for incremental publishing; remove only this output.
    foreach ($name in 'publish', 'launcher', 'archive') {
        $path = Join-Path $resolvedStage $name
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
    }
}

if (-not $windows -and -not $package) { throw 'Runtime directories require Windows; other platforms only support -SingleFile -PackageVersion.' }
$output = Get-FullPath $OutputDirectory
if ((Test-Within $root $output) -or $output -eq [IO.Path]::GetPathRoot($output).TrimEnd([IO.Path]::DirectorySeparatorChar)) {
    throw 'Output must not be a drive root, repository root, or repository ancestor.'
}
foreach ($name in @('Server', 'Tools', 'Docs', '.git', '.idea', '.forgejo', 'build')) {
    if (Test-Within $output (Join-Path $root $name)) {
        throw "Output must not be inside source, tools, or build directories: $name"
    }
}
Assert-NoLinks $output
if (Test-Path -LiteralPath $output) {
    if (-not (Test-Path -LiteralPath $output -PathType Container)) { throw 'Output path must be a directory.' }
    if ($package -and @(Get-ChildItem -LiteralPath $output -Force).Count) {
        throw 'Packaging requires an empty directory; do not use a runtime directory or overwrite an existing package.'
    }
    if (@(Get-ChildItem -LiteralPath $output -Force).Count -and
        -not (Test-Path -LiteralPath (Join-Path $output $(if ($SingleFile) { $program } else { 'DfoServer.dll' })) -PathType Leaf)) {
        throw 'Nonempty output does not match this publish mode; choose an empty directory or the same runtime mode.'
    }
    foreach ($item in Get-ChildItem -LiteralPath $output -Recurse -Force) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "Output contains a link and cannot be safely backed up or updated: $($item.FullName)"
        }
    }
}
$sourcePvf = $null
if ($PvfPath) {
    $sourcePvf = Get-FullPath $PvfPath
    if (-not (Test-Path -LiteralPath $sourcePvf -PathType Leaf)) { throw "PVF file not found: $sourcePvf" }
}
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'Install .NET 10 SDK first.' }
$sdks = & dotnet --list-sdks
if ($LASTEXITCODE -ne 0 -or -not @($sdks | Where-Object { $_ -match '^10\.' }).Count) {
    throw 'Building requires .NET 10 SDK; the runtime alone is insufficient.'
}

# Publish to staging first; failed compilation must not overwrite programs or player data.
$buildRoot = Join-Path $root 'build'
# Keep incremental artifacts; rebuild publish/launcher/archive and remove legacy publish-<GUID> output.
$stage = Join-Path $buildRoot 'publish'
if (Test-Path -LiteralPath $buildRoot) {
    Get-ChildItem -LiteralPath $buildRoot -Directory | Where-Object { $_.Name -match '^publish-[0-9a-f]{32}$' } | ForEach-Object {
        if (Test-Within $_.FullName $buildRoot) { Remove-Item -LiteralPath $_.FullName -Recurse -Force }
    }
}
foreach ($leftover in 'publish', 'launcher', 'archive') {
    $leftoverPath = Join-Path $stage $leftover
    if (Test-Path -LiteralPath $leftoverPath) { Remove-Item -LiteralPath $leftoverPath -Recurse -Force }
}
$payload = Join-Path $stage 'publish'
Write-Host "Publishing self-contained Windows x64 Release; runtime directory: $output"
if (-not $SingleFile) {
    Invoke-Dotnet @('publish', (Join-Path $root 'Server/DfoServer/DfoServer.csproj'),
        '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '--nologo',
        '--artifacts-path', (Join-Path $stage 'artifacts'), '-o', $payload, '-nr:false')
}
$launcherPayload = Join-Path $stage 'launcher'
$launcherArguments = @('publish', (Join-Path $root 'Tools/ServerLauncher/ServerLauncher.csproj'),
    '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '--nologo',
    '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:EnableCompressionInSingleFile=true',
    '--artifacts-path', (Join-Path $stage 'artifacts'), '-o', $launcherPayload, '-nr:false')
if ($SingleFile) { $launcherArguments += '-p:BundleServer=true' }
if ($package) { $launcherArguments += @('-p:EnableWindowsTargeting=true', '-m:1') }
Invoke-Dotnet $launcherArguments
New-Item -ItemType Directory -Path $payload -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $launcherPayload $(if ($SingleFile) { $program } else { 'DfoLauncher.exe' })) -Destination $payload
foreach ($name in $licensePaths) {
    Copy-Item -LiteralPath (Join-Path $root $name) -Destination $payload -Recurse -Force
}
$requiredFiles = if ($SingleFile) { @($program) } else {
    @('DfoServer.exe', 'DfoServer.dll', 'Resources/channel.txt', 'Sqlite/game_schema.sql', 'start-server.bat', 'DfoLauncher.exe')
}
foreach ($name in $requiredFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $payload $name) -PathType Leaf)) {
        throw "Missing published file: $name; staging retained at $stage"
    }
}
if (-not $windows) {
    Write-Warning 'Cross-compiling for Windows; Windows EXE and GM checks were not run. Game verification is pending.'
} elseif ($SingleFile) {
    $serverCheck = Invoke-SelfCheck 'server --server --help' (Join-Path $payload $program) @('--server', '--help')
    if ($serverCheck.ExitCode -ne 0) { throw "Embedded server startup check failed; staging retained at $stage" }
} else {
    & (Join-Path $payload $program) --help
    if ($LASTEXITCODE -ne 0) { throw "Published program startup check failed; staging retained at $stage" }
}
if ($windows) {
    $launcherCheck = Invoke-SelfCheck 'launcher --help' (Join-Path $payload $(if ($SingleFile) { $program } else { 'DfoLauncher.exe' })) @('--help')
    if ($launcherCheck.ExitCode -ne 0) { throw "Launcher check failed; staging retained at $stage" }
    $gmCheck = Invoke-SelfCheck 'embedded GM --selftest-gm' (Join-Path $payload $(if ($SingleFile) { $program } else { 'DfoLauncher.exe' })) @('--selftest-gm')
    if ($gmCheck.ExitCode -ne 0) { throw "Embedded GM check failed; staging retained at $stage" }
}

if ($package) {
    # Package only the listed programs and notices, excluding runtime resources, saves, settings and logs.
    $revision = & git -C $root rev-parse HEAD
    if ($LASTEXITCODE -ne 0 -or $revision -notmatch '^[0-9a-f]{40}$') { throw 'Cannot determine the source revision.' }
    $changes = & git -C $root status --porcelain --untracked-files=normal
    if ($LASTEXITCODE -ne 0) { throw 'Cannot check source status.' }
    $archiveRoot = Join-Path $stage 'archive'
    New-Item -ItemType Directory -Path $archiveRoot | Out-Null
    Copy-Item -LiteralPath (Join-Path $payload $program) -Destination $archiveRoot
    foreach ($name in $licensePaths) {
        Copy-Item -LiteralPath (Join-Path $payload $name) -Destination $archiveRoot -Recurse
    }
    $metadata = [ordered]@{
        version = $PackageVersion; commit = $revision; modified = [bool]$changes
        runtime = 'win-x64'; configuration = 'Release'; windowsStartupChecked = $windows
    }
    [IO.File]::WriteAllText((Join-Path $archiveRoot 'build-info.json'),
        ($metadata | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
    $instructions = @"
S5A9Server $PackageVersion (Windows x64)
Extract to a separate directory and open S5A9Server.exe. No separate .NET runtime is required.
Provide Script.pvf matching your client, then select the resource and save directories in the launcher.
This package excludes the client, PVF, player databases, logs and local settings.
Before updating, stop the server and close the launcher. Update EXE and licenses; preserve Data, Logs and settings.
Command-line help: S5A9Server.exe --server --help
Licensed under AGPL-3.0-only. Distribute LICENSE, THIRD-PARTY-NOTICES.md, THIRD-PARTY.md and LICENSES.
Upstream source: https://gitgud.io/licocon/servers5a9/-/archive/$revision/servers5a9-$revision.zip
Modified packages must provide the complete corresponding source including their changes.
Source revision and Windows startup status are in build-info.json. Automated checks do not replace game verification.
"@
    [IO.File]::WriteAllText((Join-Path $archiveRoot 'README.txt'), $instructions, [Text.UTF8Encoding]::new($true))
    New-Item -ItemType Directory -Path $output -Force | Out-Null
    $archive = Join-Path $output "S5A9Server-$PackageVersion-win-x64.zip"
    $archivePaths = @($program, 'README.txt', 'build-info.json') + $licensePaths
    Write-Step 'Start: compress ZIP'
    Compress-Archive -LiteralPath @($archivePaths | ForEach-Object { Join-Path $archiveRoot $_ }) -DestinationPath $archive
    Write-Step 'Done: compress ZIP'
    $stream = [IO.File]::OpenRead($archive)
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try { $hash = [BitConverter]::ToString($sha256.ComputeHash($stream)).Replace('-', '').ToLowerInvariant() }
    finally { $stream.Dispose(); $sha256.Dispose() }
    [IO.File]::WriteAllText(($archive + '.sha256'), "$hash  $([IO.Path]::GetFileName($archive))`n", [Text.UTF8Encoding]::new($false))
    Remove-Stage
    Write-Host "Package: $archive"
    Write-Host "SHA256: $archive.sha256"
    return
}

$data = Join-Path $output 'Data'
New-Item -ItemType Directory -Path $data -Force | Out-Null
$guard = $null
$launcherGuard = $null
$backup = $null
try {
    # Hold the server instance locks to prevent database access while updating.
    try {
        $guard = [IO.File]::Open((Join-Path $data 'server.lock'),
            [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        $launcherGuard = [IO.File]::Open((Join-Path $data 'launcher.lock'),
            [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    } catch {
        throw 'Runtime directory is busy or not writable. Stop the server and close the launcher before rebuilding; programs and saves were not overwritten.'
    }
    $savedData = @(Get-ChildItem -LiteralPath $data -Force | Where-Object { $_.Name -notin @('server.lock', 'launcher.lock') })
    if ($savedData.Count) {
        # Back up Data including SQLite WAL/SHM and PVF, excluding instance locks.
        $backupRoot = $output + '.backups'
        Assert-NoLinks $backupRoot
        $backup = Join-Path $backupRoot ((Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
        $backupData = Join-Path $backup 'Data'
        New-Item -ItemType Directory -Path $backupData -Force | Out-Null
        foreach ($item in $savedData) {
            Copy-Item -LiteralPath $item.FullName -Destination $backupData -Recurse -Force
        }
        Write-Host "Pre-update Data backup: $backup"
    }
    foreach ($item in Get-ChildItem -LiteralPath $payload -Force) {
        if ($item.Name -in @('Data', 'Logs', 'launcher.local.json')) { continue }
        Copy-Item -LiteralPath $item.FullName -Destination $output -Recurse -Force
    }
    $pvfDirectory = Join-Path $data 'Pvf'
    New-Item -ItemType Directory -Path $pvfDirectory -Force | Out-Null
    $readme = Join-Path $pvfDirectory 'README.md'
    if (-not $SingleFile -and -not (Test-Path -LiteralPath $readme)) {
        Copy-Item -LiteralPath (Join-Path $payload 'Data/Pvf/README.md') -Destination $readme
    }
    $targetPvf = Join-Path $pvfDirectory 'Script.pvf'
    if (Test-Path -LiteralPath $targetPvf -PathType Leaf) {
        Write-Host "Preserving existing PVF: $targetPvf"
    } else {
        if (-not $sourcePvf -and -not $SingleFile -and (Test-Path -LiteralPath (Join-Path $payload 'Data/Pvf/Script.pvf') -PathType Leaf)) {
            $sourcePvf = Join-Path $payload 'Data/Pvf/Script.pvf'
        }
        if ($sourcePvf) { Copy-Item -LiteralPath $sourcePvf -Destination $targetPvf }
    }
    if ($SingleFile) {
        $installedCheck = Invoke-SelfCheck 'installed --server --help' (Join-Path $output $program) @('--server', '--help')
        if ($installedCheck.ExitCode -ne 0) { throw 'Installed single-file startup check failed.' }
    } else {
        & (Join-Path $output $program) --help
        if ($LASTEXITCODE -ne 0) { throw 'Installed startup check failed.' }
    }
    $launcherConfig = Join-Path $output 'launcher.local.json'
    if (-not $SingleFile -and -not (Test-Path -LiteralPath $launcherConfig)) {
        $defaults = [ordered]@{
            ServerIp = '127.0.0.1'; ChannelPort = 23100; GamePort = 23101
            PvfPath = 'Data/Pvf/Script.pvf'; DataDirectory = 'Data'
            GmEnabled = $true; GmPort = 23102; GmWebPort = 23103
        }
        [IO.File]::WriteAllText($launcherConfig, ($defaults | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
        Write-Host "Created default launcher settings: $launcherConfig"
    } elseif (Test-Path -LiteralPath $launcherConfig) {
        Write-Host "Preserving existing launcher settings: $launcherConfig"
    }
} catch {
    Write-Warning "Build/update did not finish; staging retained at $stage. Data and Logs are preserved."
    if ($backup) { Write-Warning "Pre-update Data backup: $backup" }
    throw
} finally {
    if ($launcherGuard) { $launcherGuard.Dispose() }
    if ($guard) { $guard.Dispose() }
}

# Remove only this staging output after validating its absolute path within build.
Remove-Stage
if ($SingleFile) {
    Write-Host "Build complete. Single executable: $output\$program"
    Write-Host 'Distribute the EXE, LICENSE, THIRD-PARTY-NOTICES.md, THIRD-PARTY.md and LICENSES; no additional DLL, Resources, Sqlite or startup script is required. Select PVF and saves in the launcher.'
} else {
    Write-Host "Build complete. Open launcher: $output\DfoLauncher.exe"
    Write-Host "start-server.bat opens the launcher without arguments; with arguments it starts the command-line server."
}
Write-Host "Default local connection: 127.0.0.1, channel TCP 23100, game TCP 23101; settings are read from launcher.local.json."
Write-Host "GM data API listens on localhost only; use the native 95JP launcher GM page (--gm-web-port default 23103; see GmWebPort in launcher.local.json)."
Write-Host "Saves: $data\game.db; logs: $output\Logs. Reuse the same output directory for later builds."
if (-not (Test-Path -LiteralPath $targetPvf -PathType Leaf)) {
    Write-Warning "Resources are missing. Place matching Script.pvf at $targetPvf before starting."
}
