#requires -Version 7.0
<#
.SYNOPSIS
Release 保留策略的纯逻辑：从已有 Release 列表中选出应删除的旧版本。不访问网络，便于用 mock 数据验证。
#>

# 返回应删除的 Release（旧→新排序前的原对象，按从新到旧输出）。
# 规则：草稿永不删除也不计数；本次版本（CurrentTag）永不删除且占一个保留名额；
# 其余按发布时间（缺失则创建时间，再按 id）从新到旧，保留 KeepLatest-1 个，更旧的才列入删除。
# KeepLatest 为 0 表示关闭清理，返回空。
function Select-ReleasesToPrune {
    param(
        [object[]]$Releases = @(),
        [Parameter(Mandatory)][ValidateRange(0, 1000)][int]$KeepLatest,
        [Parameter(Mandatory)][string]$CurrentTag
    )
    if ($KeepLatest -eq 0) { return @() }
    $stamp = {
        param($r)
        foreach ($name in 'published_at', 'created_at') {
            $value = $r.$name
            if ($value) { try { return [datetimeoffset]$value } catch { } }
        }
        return [datetimeoffset]::MinValue
    }
    $others = @($Releases | Where-Object { $_ -and -not $_.draft -and $_.tag_name -cne $CurrentTag } |
        Sort-Object -Descending @{ Expression = { & $stamp $_ } }, @{ Expression = { [long]$_.id } })
    $keepOthers = $KeepLatest - 1
    if ($others.Count -le $keepOthers) { return @() }
    return @($others | Select-Object -Skip $keepOthers)
}
