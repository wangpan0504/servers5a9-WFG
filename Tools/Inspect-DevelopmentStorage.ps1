param(
    [Parameter(Mandatory)][string]$Store,
    [string[]]$Repositories=@((Split-Path $PSScriptRoot -Parent)),
    [ValidateRange(0,[long]::MaxValue)][long]$MaxBytes=0,
    [ValidateRange(0,[long]::MaxValue)][long]$MaxFiles=0,
    [ValidateRange(1,1000000)][int]$MaxEntries=100000
)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'DevelopmentStorageBudget.ps1')
$result=Test-DevelopmentStorageBudget (Get-DevelopmentStorageInventory $Store $Repositories $MaxEntries) $MaxBytes $MaxFiles
$result | ConvertTo-Json -Depth 12
if($result.State -eq 'blocked'){exit 2}
