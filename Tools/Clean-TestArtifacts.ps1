<#
.SYNOPSIS
按默认授权的保留策略手动清理 build/tcp-tests 下的 TCP 测试现场（与每轮 TCP 启动时的自动回收规则一致）。

通过的轮次直接删除；失败轮次保留最近 KeepFailed 个、KeepDays 天内且总量不超过 MaxGB（保守统计全部文件路径长度，包含 PVF 和硬链接），
最新一个失败现场始终保留；正在运行（.running 被占用）的轮次和非 GUID 目录不处理，不跟随符号链接或 junction。

.EXAMPLE
pwsh Tools/Clean-TestArtifacts.ps1 -WhatIf
pwsh Tools/Clean-TestArtifacts.ps1 -All        # 删除全部非运行中的轮次（含失败现场）
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$Repository = (Split-Path $PSScriptRoot -Parent),
    [int]$KeepFailed = $(if ($env:DFO_TCP_KEEP_FAILED) { [int]$env:DFO_TCP_KEEP_FAILED } else { 5 }),
    [double]$KeepDays = $(if ($env:DFO_TCP_KEEP_DAYS) { [double]$env:DFO_TCP_KEEP_DAYS } else { 3 }),
    [double]$MaxGB = $(if ($env:DFO_TCP_MAX_GB) { [double]$env:DFO_TCP_MAX_GB } else { 5 }),
    [switch]$All
)
$ErrorActionPreference = 'Stop'
$parent = Join-Path ([IO.Path]::GetFullPath($Repository)) 'build/tcp-tests'
if (-not (Test-Path -LiteralPath $parent)) { Write-Output "无 TCP 现场：$parent"; return }

function Test-Running([string]$dir) {
    $marker = Join-Path $dir '.running'
    if (-not (Test-Path -LiteralPath $marker)) { return $false }
    try { $s = [IO.File]::Open($marker, 'Open', 'ReadWrite', 'None'); $s.Dispose(); return $false } catch { return $true }
}
function Test-Passed([string]$dir) {
    $result = Join-Path $dir 'result.json'
    if (-not (Test-Path -LiteralPath $result)) { return $false }
    try { return [bool]((Get-Content -LiteralPath $result -Raw | ConvertFrom-Json).Passed) } catch { return $false }
}
function Get-RunSize([string]$dir) {
    $sum = 0L
    foreach ($item in Get-ChildItem -LiteralPath $dir -Force) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { continue }
        if ($item.PSIsContainer) { $sum += Get-RunSize $item.FullName }
        else { $sum += $item.Length }
    }
    $sum
}
function Remove-Run([string]$dir) {
    if ($PSCmdlet.ShouldProcess($dir, '删除 TCP 现场')) {
        # 先移除目录链接本身，避免进入链接目标。
        Get-ChildItem -LiteralPath $dir -Recurse -Force -Directory -Attributes ReparsePoint -ErrorAction SilentlyContinue |
            ForEach-Object { [IO.Directory]::Delete($_.FullName) }
        Remove-Item -LiteralPath $dir -Recurse -Force
    }
    $script:removed++
}

$removed = 0
$failed = @()
$before = (Get-PSDrive -Name ([IO.Path]::GetPathRoot($parent).Substring(0,1)) -ErrorAction SilentlyContinue).Free
foreach ($dir in Get-ChildItem -LiteralPath $parent -Directory -Force) {
    $guid = [guid]::Empty
    if (-not [guid]::TryParseExact($dir.Name, 'N', [ref]$guid)) { continue }
    if ($dir.Attributes -band [IO.FileAttributes]::ReparsePoint) { continue }
    if (Test-Running $dir.FullName) { Write-Output "跳过运行中：$($dir.Name)"; continue }
    if ($All -or (Test-Passed $dir.FullName)) { Remove-Run $dir.FullName; continue }
    $failed += [pscustomobject]@{ Path = $dir.FullName; Time = $dir.LastWriteTimeUtc; Size = (Get-RunSize $dir.FullName) }
}
$kept = 0; $total = 0L; $cap = [long]($MaxGB * 1GB); $now = [DateTime]::UtcNow
foreach ($run in $failed | Sort-Object Time -Descending) {
    $keep = ($kept -eq 0) -or ($kept -lt $KeepFailed -and ($now - $run.Time).TotalDays -le $KeepDays -and ($total + $run.Size) -le $cap)
    if ($keep) { $kept++; $total += $run.Size } else { Remove-Run $run.Path }
}
$after = (Get-PSDrive -Name ([IO.Path]::GetPathRoot($parent).Substring(0,1)) -ErrorAction SilentlyContinue).Free
Write-Output ("删除 {0} 个轮次，保留失败现场 {1} 个（{2:N2} GB）；释放约 {3:N2} GB" -f $removed, $kept, ($total / 1GB), (($after - $before) / 1GB))
