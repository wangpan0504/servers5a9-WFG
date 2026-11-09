[CmdletBinding()]
param([Parameter(Mandatory)][string]$PvfPath, [Parameter(Mandatory)][string]$PvfLibPath,
    [Parameter(Mandatory)][string]$CandidatePvfPath)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'New-BeastChannelLevelDelta.ps1') -PvfPath $PvfPath -PvfLibPath $PvfLibPath
Add-Type -Path (Resolve-Path -LiteralPath $PvfLibPath).ProviderPath
$archive = [PvfLib.PvfArchive]::Open((Resolve-Path -LiteralPath $PvfPath).ProviderPath)
$candidate = [PvfLib.PvfArchive]::Open((Resolve-Path -LiteralPath $CandidatePvfPath).ProviderPath)
try {
    $plan = New-BeastChannelLevelPlan $archive
    $again = New-BeastChannelLevelPlan $candidate
    if ($plan.State -ne 'patched' -or $again.State -ne 'already_applied' -or
        -not [Linq.Enumerable]::SequenceEqual([byte[]]$plan.Raw, [byte[]]$again.Raw)) { throw 'Candidate or idempotency mismatch.' }
    if (-not [Linq.Enumerable]::SequenceEqual([byte[]]$plan.Before, [byte[]]$plan.Raw[0..($plan.Before.Length - 1)])) { throw 'Existing recommendations changed.' }
    $proxy = [pscustomobject]@{ Archive = $archive; Text = '' }
    $proxy | Add-Member ScriptMethod ReadText { param($Path) $this.Text }
    $proxy | Add-Member ScriptMethod ReadRaw { param($Path) ,$this.Archive.ReadRaw($Path) }
    foreach ($text in @('[channel] `[beast]` 95 95 [/channel]', '[channel] `[beast]` 90 95 [/channel] [channel] `[beast]` 90 95 [/channel]', '[channel] `[beast]` bad [/channel]')) {
        $proxy.Text = $text; $rejected = $false
        try { New-BeastChannelLevelPlan $proxy | Out-Null } catch {
            if ($_.Exception.Message -notlike 'Unknown beast recommendation*') { throw }
            $rejected = $true
        }
        if (-not $rejected) { throw 'Unknown or duplicate range was accepted.' }
    }
    Write-Output 'PASS beast recommendation: exact 90-95 readback, original prefix preserved, idempotency and unknown/duplicate declaration rejection.'
} finally { $candidate.Dispose(); $archive.Dispose() }
