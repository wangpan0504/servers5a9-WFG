#requires -Version 7.0
# Historical entry retained; build now requires PowerShell 7.
param([string]$ScratchDirectory)
$ErrorActionPreference = 'Stop'
if (-not $ScratchDirectory) { $ScratchDirectory = Join-Path ([IO.Path]::GetTempPath()) ('server-output-guard-' + [guid]::NewGuid().ToString('N')) }
if (Test-Path -LiteralPath $ScratchDirectory) { throw 'Choose a new scratch directory.' }
[void][IO.Directory]::CreateDirectory((Join-Path $ScratchDirectory 'Data'))
$save = Join-Path $ScratchDirectory 'Data/game.db'
[IO.File]::WriteAllText($save, 'preserve-exactly')
$rejected = $false
try { & (Join-Path $PSScriptRoot 'Build-Server.ps1') -OutputDirectory $ScratchDirectory }
catch { if ($_.Exception.Message -notlike 'Output directory must not already exist*') { throw }; $rejected = $true }
if (-not $rejected -or [IO.File]::ReadAllText($save) -cne 'preserve-exactly') { throw 'Existing output or save was not protected.' }
Write-Output 'PASS existing output rejected before compiler; save unchanged.'
