<#
.SYNOPSIS
按与基线的差异选择需要运行的检查与 TCP 测试组，开发内循环只跑受影响部分；合入前仍执行全量。

.EXAMPLE
pwsh Tools/Select-AffectedTests.ps1                      # 只列出选择结果
pwsh Tools/Select-AffectedTests.ps1 -Run -PvfPath <PVF>  # 增量构建 + --selftest-core + 受影响 TCP 组（并行）
#>
param(
    [string]$Base = 'master',
    [switch]$Run,
    [ValidateSet('Debug','Release')][string]$Configuration = 'Debug',
    [string]$PvfPath,
    [string]$ArtifactsPath,
    [switch]$IncludeUncommitted = $true,
    # 直接给出变更路径（调试映射用），不读取 git 差异。
    [string[]]$Paths
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (-not $ArtifactsPath) { $ArtifactsPath = Join-Path $root 'build/check' }

$mergeBase = (& git -C $root merge-base $Base HEAD).Trim()
if ($Paths) { $changed = @($Paths -split ',') } else {
if ($LASTEXITCODE -ne 0) { throw "无法确定与 $Base 的合并基点" }
$changed = @(& git -C $root diff --name-only $mergeBase HEAD)
if ($IncludeUncommitted) {
    $changed += @(& git -C $root diff --name-only HEAD)
    $changed += @(& git -C $root ls-files --others --exclude-standard)
}
}
$changed = @($changed | Where-Object { $_ } | ForEach-Object { $_ -replace '\\', '/' } | Sort-Object -Unique)

# 公共入口、数据库结构、协议核心与测试框架变化无法可靠缩小范围，回退全量。
$fullPatterns = @(
    '^Server/DfoServer/(Program\.cs|Application/|Infrastructure/|Network/Core/)',
    '^Server/DfoServer/Persistence/(SqliteMigrations|SqliteDatabaseBootstrap|SqliteGameDatabase)',
    '^Server/DfoServer/Sqlite/',
    '^Server/DfoServer/SelfTests/(SelfTestRunner|TestCheck|SelfTestCatalogs)\.cs$',
    '^Server/DfoServer/SelfTests/Tcp/(TcpRegressionTests|TcpTestFixture|TcpTestResources|TcpTestShards|TcpTestArtifacts|TcpTestRetention|TcpTestTimings|TcpTestDatabaseTemplate|TcpTestClient\w*)\.cs$',
    '^Tools/PvfLib/', '\.csproj$', '\.sln$', '^Directory\.Build\.', '^global\.json$'
)

# 路径关键词到测试组；同时包含对应的资源定向组（这些组按需运行，不在默认全量 TCP 中）。
$keywordGroups = [ordered]@{
    'Quest'      = 'quests','quest-gold-mail','quest-question-state','quest-continuation','quest-core-rewards','quest-initialization','story-digest'
    'Story'      = 'story-digest','story-cinematic','story-scenes','story-quest-entry','story-publication'
    'Dungeon'    = 'formal-dungeon','dungeon-training','class-experience','dungeon-revive','hero-tomb-entry','dungeon-probe','dungeon-settlement','dungeon-entry-plan'
    'Inventory'  = 'inventory','consumable-use','inventory-persistence'
    'Items?/'    = 'inventory','shop','consumable-use'
    'Consumable' = 'consumable-use','consumable-use-rules'
    'Skill'      = 'skills','skill-slots','skill-material'
    'Adventure'  = 'adventure','adventure-names','adventure-representatives','adventure-expeditions','adventure-shop','adventure-capsules'
    'Town'       = 'town','presence','teleport'
    'Auth|Login|Account' = 'authentication','native-login'
    'Mail'       = 'mailbox'
    'CashShop|Cera' = 'cash-shop','cera-package'
    'Shop'       = 'shop'
    'Setting'    = 'settings'
    'Tutorial'   = 'tutorial'
    'Channel'    = 'channel-entry'
    'Title'      = 'titles'
    'Gm|Development' = 'gm-assets','gm-progress','gm-debug'
    'Job'        = 'jobs'
    'Character'  = 'gm-progress','jobs','startup'
    'Packet|Capture' = 'packet-capture'
}

# 从注册表解析组名与其引用的测试类，变更的测试类直接映射到所在组。
$registry = Get-Content (Join-Path $root 'Server/DfoServer/SelfTests/Tcp/TcpRegressionTests.cs') -Raw -Encoding utf8
$groupText = [ordered]@{}
$current = $null
foreach ($line in $registry -split "`n") {
    if ($line -match '^\s*\("([\w-]+)",') { $current = $Matches[1]; $groupText[$current] = '' }
    if ($current) { $groupText[$current] += $line }
    if ($line -match '^\s*\];') { $current = $null }
}

$full = $false
$reasons = New-Object System.Collections.Generic.List[string]
$groups = New-Object System.Collections.Generic.SortedSet[string]
$codeChanged = $false
foreach ($path in $changed) {
    if ($path -match '\.(md|txt)$' -or $path -match '^(Docs|\.forgejo)/') { continue }
    if ($path -match '^Tools/' -and $path -notmatch '^Tools/(PvfLib|ArchitectureCheck)/') { continue }
    $codeChanged = $true
    if ($fullPatterns | Where-Object { $path -match $_ }) { $full = $true; $reasons.Add("公共文件：$path"); continue }
    $stem = [IO.Path]::GetFileNameWithoutExtension($path) -replace '\..*$', ''
    $matched = $false
    foreach ($name in $groupText.Keys) {
        if ($stem.Length -gt 3 -and $groupText[$name] -match "\b$([regex]::Escape($stem))\b") { [void]$groups.Add($name); $matched = $true }
    }
    foreach ($key in $keywordGroups.Keys) {
        if ($path -match $key) { foreach ($g in $keywordGroups[$key]) { [void]$groups.Add($g) }; $matched = $true }
    }
    if (-not $matched -and $path -match '\.cs$') { $full = $true; $reasons.Add("无法映射：$path") }
}

$selection = if ($full) { '' } else { ($groups | Where-Object { $groupText.Contains($_) }) -join ',' }
Write-Output "基线：$Base（$($mergeBase.Substring(0,7))），变更文件 $($changed.Count) 个"
if (-not $codeChanged) { Write-Output '仅文档或脚本变化：检查链接与内容即可，无需构建或 TCP。' }
elseif ($full) { Write-Output ('回退全量：' + (($reasons | Select-Object -First 5) -join '；')) }
else { Write-Output "受影响 TCP 组（$($groups.Count)）：$selection" }

if (-not $Run -or -not $codeChanged) { return }
$dll = Join-Path $ArtifactsPath ("bin/DfoServer/{0}/DfoServer.dll" -f $Configuration.ToLowerInvariant())
& dotnet build (Join-Path $root 'ServerS5A9.sln') -c $Configuration --nologo --artifacts-path $ArtifactsPath -nr:false
if ($LASTEXITCODE -ne 0) { throw "构建失败（$LASTEXITCODE）" }
& dotnet $dll --selftest-core
if ($LASTEXITCODE -ne 0) { throw "--selftest-core 失败（$LASTEXITCODE）" }
$runArgs = @()
if ($PvfPath) { $runArgs += @('--pvf-path', $PvfPath) }
$runArgs += '--selftest-tcp'
if (-not $full) { $runArgs += $selection }
& dotnet $dll @runArgs
if ($LASTEXITCODE -ne 0) { throw "TCP 回归失败（$LASTEXITCODE）" }
if ($full) { Write-Output '公共文件变化：合入前还需运行完整 --selftest。' }
