[CmdletBinding()]
param([Parameter(Mandatory)][string]$PvfPath, [Parameter(Mandatory)][string]$PvfLibPath,
    [Parameter(Mandatory)][string]$CandidatePvfPath)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'New-AntonTremorClearDelta.ps1') -PvfPath $PvfPath -PvfLibPath $PvfLibPath
Add-Type -Path (Resolve-Path -LiteralPath $PvfLibPath).ProviderPath
$archive = [PvfLib.PvfArchive]::Open((Resolve-Path -LiteralPath $PvfPath).ProviderPath)
$candidate = [PvfLib.PvfArchive]::Open((Resolve-Path -LiteralPath $CandidatePvfPath).ProviderPath)
try {
    if ($archive.FileCount -ne $candidate.FileCount) { throw 'File count changed.' }
    foreach ($id in @(212, 214)) {
        $plan = New-AntonTremorClearPlan $archive $id
        $again = New-AntonTremorClearPlan $candidate $id
        if ($plan.State -ne 'patched' -or $again.State -ne 'already_applied' -or
            -not [Linq.Enumerable]::SequenceEqual([byte[]]$plan.Raw, [byte[]]$again.Raw) -or
            -not [Linq.Enumerable]::SequenceEqual([byte[]]$plan.Before, [byte[]]$plan.Raw[0..($plan.Before.Length - 1)])) {
            throw 'Delta/readback/idempotency failed.'
        }
    }
    foreach ($path in @('passiveobject/actionobject/map/anton/phase3/cartel/action/cartel_basic_0.act',
        'passiveobject/actionobject/map/anton/phase3/misile/action/etc_misile_2.act',
        'monster/newmonsters/anton/phase1/fleet_fire/basic_2/action/last.act')) {
        if (-not [Linq.Enumerable]::SequenceEqual([byte[]]$archive.ReadRaw($path), [byte[]]$candidate.ReadRaw($path))) {
            throw "Unrelated resource changed: $path"
        }
    }
    $proxy = [pscustomobject]@{ Archive = $archive }
    $proxy | Add-Member ScriptMethod ReadText {
        param($Path)
        if ($Path.EndsWith('.dgn')) { return $this.Archive.ReadText($Path) + ' [clear condition] [hunt monster] 1 1 [/clear condition]' }
        $this.Archive.ReadText($Path)
    }
    $rejected = $false
    try { New-AntonTremorClearPlan $proxy 212 | Out-Null } catch {
        if ($_.Exception.Message -ne 'Unknown clear condition.') { throw }
        $rejected = $true
    }
    if (-not $rejected) { throw 'Unknown condition accepted.' }
    Write-Output 'PASS Anton tremor delta: exact targets, original tokens, readback, idempotency, unknown-condition rejection and original cannon/fleet preserved.'
} finally { $candidate.Dispose(); $archive.Dispose() }
