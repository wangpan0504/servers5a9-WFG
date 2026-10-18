#requires -Version 7.0
<#
.SYNOPSIS
用 mock 数据验证 Release 保留策略（Tools/ReleaseRetention.ps1），不访问网络、不删除任何真实 Release。
#>
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ReleaseRetention.ps1')
function New-Rel($id, $tag, $published, [bool]$draft = $false) {
    [pscustomobject]@{ id = $id; tag_name = $tag; draft = $draft; published_at = $published; created_at = $published }
}
$releases = @(
    (New-Rel 5 'v0.1.5' '2026-10-05T10:00:00Z'), (New-Rel 9 'v0.1.9' '2026-10-09T10:00:00Z'),
    (New-Rel 1 'v0.1.1' '2026-10-01T10:00:00Z'), (New-Rel 7 'v0.1.7' '2026-10-07T10:00:00Z'),
    (New-Rel 3 'v0.1.3' '2026-10-03T10:00:00Z'), (New-Rel 8 'v0.1.8-draft' '' $true),
    (New-Rel 10 'v0.1.10' '2026-10-10T10:00:00Z'))
$failed = 0
function Assert-Prune([string]$Name, [int]$Keep, [string]$Current, [string]$Expected) {
    $actual = (Select-ReleasesToPrune -Releases $releases -KeepLatest $Keep -CurrentTag $Current | ForEach-Object tag_name) -join ','
    if ($actual -ceq $Expected) { Write-Host "PASS $Name -> [$actual]" }
    else { Write-Host "FAIL $Name -> [$actual]，期望 [$Expected]"; $script:failed++ }
}
Assert-Prune '保留 3（本次已发布，最新）' 3 'v0.1.10' 'v0.1.5,v0.1.3,v0.1.1'
Assert-Prune '保留 1（仅本次）' 1 'v0.1.10' 'v0.1.9,v0.1.7,v0.1.5,v0.1.3,v0.1.1'
Assert-Prune '保留 3（413 重试，本次尚未出现）' 3 'v0.1.11' 'v0.1.7,v0.1.5,v0.1.3,v0.1.1'
Assert-Prune '本次是最旧版本也不删本次' 2 'v0.1.1' 'v0.1.9,v0.1.7,v0.1.5,v0.1.3'
Assert-Prune '保留 0 关闭清理' 0 'v0.1.10' ''
Assert-Prune '保留数大于总数' 99 'v0.1.10' ''
$drafts = @(Select-ReleasesToPrune -Releases $releases -KeepLatest 1 -CurrentTag 'v0.1.10' | Where-Object draft)
if ($drafts.Count -ne 0) { Write-Host 'FAIL 草稿被列入删除'; $failed++ } else { Write-Host 'PASS 草稿不会被删除' }
if (@(Select-ReleasesToPrune -Releases @() -KeepLatest 3 -CurrentTag 'v1').Count -ne 0) { Write-Host 'FAIL 空列表'; $failed++ } else { Write-Host 'PASS 空列表' }
if ($failed) { throw "$failed 项失败" }
