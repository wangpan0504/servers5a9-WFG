<#
.SYNOPSIS
按改动路径选择需要运行的检查与 TCP 测试组：开发内循环只跑受影响部分，合并前用 -Full 执行全量。

.EXAMPLE
pwsh Tools/Select-AffectedTests.ps1                                  # 只列出选择结果与预计耗时
pwsh Tools/Select-AffectedTests.ps1 -Paths Server/DfoServer/SelfTests/Tcp/TcpPeer.cs
pwsh Tools/Select-AffectedTests.ps1 -Run -PvfPath <PVF>              # 增量构建 + --selftest-core + 受影响 TCP 组（并行）
pwsh Tools/Select-AffectedTests.ps1 -Run -Full -PvfPath <PVF>        # 合并前：全量 TCP（完整 --selftest 另用 Check-Development.ps1 -PvfPath）
#>
param(
    # 默认 origin/master（存在时），避免本地 master 过旧导致改动列表偏大。
    [string]$Base,
    [switch]$Run,
    # 合并前全量 TCP；测试框架、表结构等改动在开发期只跑冒烟/定向组，合并前必须加此开关。
    [switch]$Full,
    [ValidateSet('Debug','Release')][string]$Configuration = 'Debug',
    [string]$PvfPath,
    [string]$ArtifactsPath,
    # 默认包含未提交与未跟踪文件；-IncludeUncommitted:$false 只比较已提交内容。
    [switch]$IncludeUncommitted = $true,
    # 直接给定改动路径（逗号或数组），用于检查映射规则，不读取 git 差异。
    [string[]]$Paths,
    # 不复用“源码未变”的构建与 --selftest-core 通过记录。
    [switch]$NoCache,
    # 多个工作树共用的 TCP 组耗时文件；默认放在用户目录，不写入任何工作树。
    [string]$TimingsPath
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (-not $ArtifactsPath) { $ArtifactsPath = Join-Path $root 'build/check' }
if (-not $TimingsPath) {
    $cache = if ($env:LOCALAPPDATA) { Join-Path $env:LOCALAPPDATA 'S5A9' } else { Join-Path $HOME '.cache/s5a9' }
    $TimingsPath = Join-Path $cache 'tcp-timings.json'
}

if ($Paths) {
    $baseText = '直接给定路径'
    $changed = @($Paths -split ',')
} else {
    if (-not $Base) {
        & git -C $root rev-parse --verify --quiet origin/master *> $null
        $Base = if ($LASTEXITCODE -eq 0) { 'origin/master' } else { 'master' }
    }
    $mergeBase = (& git -C $root merge-base $Base HEAD)
    if ($LASTEXITCODE -ne 0 -or -not $mergeBase) { throw "无法确定与 $Base 的合并基线" }
    $mergeBase = $mergeBase.Trim()
    $baseText = "$Base（$($mergeBase.Substring(0,7))）"
    $changed = @(& git -C $root diff --name-only $mergeBase HEAD)
    if ($IncludeUncommitted) {
        $changed += @(& git -C $root diff --name-only HEAD)
        $changed += @(& git -C $root ls-files --others --exclude-standard)
    }
}
$changed = @($changed | Where-Object { $_ } | ForEach-Object { $_.Trim() -replace '\\', '/' } | Sort-Object -Unique)

# 入口、协议核心、项目文件与共享资源库变化无法可靠缩小范围，直接全量。
$fullPatterns = @(
    '^Server/DfoServer/(Program\.cs|Application/|Infrastructure/|Network/Core/)',
    '^Server/DfoServer/SelfTests/(SelfTestRunner|TestCheck|SelfTestCatalogs)\.cs$',
    '^Tools/(PvfLib|PvfLib.Tests|ArchitectureCheck)/', '\.(csproj|fsproj|sln|slnx|props|targets)$',
    '(^|/)(global\.json|nuget\.config|packages\.lock\.json)$',
    '^Server/DfoServer/(Resources|Data)/', '\.(json|config|pvf)$'
)
# 公共测试框架与 TCP 入口：开发期跑冒烟组，合并前必须全量 TCP（见 CONTRIBUTING.md 检查范围）。
$harnessPattern = '^Server/DfoServer/SelfTests/Tcp/(TcpPeer|TcpRegressionTests|TcpTestFixture|TcpTestResources|TcpTestShards|TcpTestArtifacts|TcpTestRetention|TcpTestTimings|TcpTestDatabaseTemplate|TcpTestClient\w*)\.cs$'
$smokeGroups = @('commands','startup','authentication','native-login','channel-entry','town','inventory','dungeon-probe','hero-tomb-entry','quest-gold-mail','mailbox','shop')
# 迁移与表结构：结构检查 + 启动建库 + 持久化组；合并前仍须全量。
$schemaPatterns = @(
    '^Server/DfoServer/Persistence/(SqliteMigrations|SqliteDatabaseBootstrap|SqliteGameDatabase)',
    '^Server/DfoServer/Sqlite/'
)
$schemaGroups = @('database-schema','settings-storage','inventory-persistence','startup')

# 路径关键词到测试组；同时补充对应的资源定向组（这些组按需运行，不在默认全量 TCP 中）。
$keywordGroups = [ordered]@{
    'EpicPiece' = 'epic-piece-drops','epic-book','disjoint','expert-job-actions','dungeon-pickup-slots'
    'Raid'      = 'raid-create','anton-raid','raid-channel-type','raid-channel-landing','raid-channel-events','special-normal-entry'
    'Quest'      = 'quest-lifecycle','quest-extras','quest-chains','quest-chain-settlement','quest-metro','quest-auto-daily','quest-gold-mail','quest-question-state','quest-continuation','quest-core-rewards','quest-initialization','story-digest'
    'Social|PlayerInteraction|PublicCharacter|NamedWhisper' = 'social','presence','chat','channel-entry'
    'PartyDungeon' = 'party-dungeon','party-dungeon-modes'
    'EvildomWar' = 'evildom-war','channel-entry','solo-party'
    'Party'     = 'solo-party','social','presence','teleport','formal-dungeon'
    'Story'      = 'story-digest','story-cinematic','story-scenes','story-quest-entry','story-publication'
    'Dungeon|GriefTower|Dazzlement|LostLand' = 'formal-dungeon','dungeon-growth','grief-tower','dungeon-training','class-experience','dungeon-revive','dungeon-altars','dungeon-despair','dungeon-dazzlement','hero-tomb-entry','dungeon-probe','dungeon-settlement','dungeon-entry-plan','lost-land'
    'Booster'    = 'booster','booster-selection','booster-equipment','daily-retained-booster'
    'Inventory'  = 'inventory','consumable-use','inventory-persistence'
    'Items?/'    = 'inventory','shop','consumable-use'
    'Consumable' = 'consumable-use','consumable-use-rules'
    'Skill'      = 'skills','skill-switch-inventory','skill-slots','skill-material'
    'Adventure'  = 'adventure','adventure-names','adventure-representatives','adventure-expeditions','adventure-shop','adventure-capsules','adventure-weekly'
    'Town'       = 'town','presence','teleport'
    'Auth|Login|Account' = 'authentication','native-login'
    'Mail'       = 'mailbox'
    'CashShop|Cera' = 'cash-shop','cera-package'
    'Shop'       = 'shop'
    'Setting'    = 'settings','settings-storage'
    'Tutorial'   = 'tutorial'
    'Channel'    = 'channel-entry'
    'TagEvildom' = 'tag-evildom','channel-entry'
    'Title'      = 'titles'
    'CollectBox' = 'collectbox'
    'Gm|Development' = 'gm-assets','gm-progress','gm-debug'
    'Job'        = 'jobs'
    'Character'  = 'gm-progress','jobs','startup'
    'Packet|Capture' = 'packet-capture','packet-http'
}

# 从注册表读取每组调用的测试类，文件名可直接映射到所属组。
$registry = Get-Content (Join-Path $root 'Server/DfoServer/SelfTests/Tcp/TcpRegressionTests.cs') -Raw -Encoding utf8
$groupText = [ordered]@{}
$current = $null
foreach ($line in $registry -split "`n") {
    if ($line -match '^\s*\("([\w-]+)",') { $current = $Matches[1]; $groupText[$current] = '' }
    if ($current) { $groupText[$current] += $line }
    if ($line -match '^\s*\];') { $current = $null }
}
function Get-RegistryGroups([string]$name) {
    if ($name.Length -le 3) { return @() }
    $pattern = "\b$([regex]::Escape($name))\b"
    @($groupText.Keys | Where-Object { $groupText[$_] -match $pattern })
}

# 引用映射：业务类型未被注册表直接引用时，查找使用它的 TCP 测试类，再归到对应组。
$tcpSources = $null
function Get-ReferenceGroups([string]$stem) {
    if ($stem.Length -le 3) { return @() }
    if ($null -eq $script:tcpSources) {
        $script:tcpSources = @(Get-ChildItem (Join-Path $root 'Server/DfoServer/SelfTests/Tcp') -Filter *.cs -File |
            Where-Object { $_.Name -notmatch '^(TcpPeer|TcpRegressionTests|TcpTest\w*)\.cs$' } |
            ForEach-Object { [pscustomobject]@{ Class = ($_.BaseName -replace '\..*$', ''); Text = (Get-Content $_.FullName -Raw -Encoding utf8) } })
    }
    $pattern = "\b$([regex]::Escape($stem))\b"
    $classes = @($script:tcpSources | Where-Object { $_.Text -match $pattern } | ForEach-Object Class | Sort-Object -Unique)
    @($classes | ForEach-Object { Get-RegistryGroups $_ } | Sort-Object -Unique)
}

# 注意 PowerShell 变量不区分大小写：$full 即 -Full 开关，下方规则可将其置为 true。
$full = [bool]$Full
$harness = $false
$schema = $false
$coreOnly = New-Object System.Collections.Generic.List[string]
$reasons = New-Object System.Collections.Generic.List[string]
$groups = New-Object System.Collections.Generic.SortedSet[string]
$codeChanged = $false
$scriptChanged = $false
$broadLimit = [math]::Ceiling($groupText.Count * 0.4)
if ($Full) { $reasons.Add('指定 -Full（合并前全量）') }
foreach ($path in $changed) {
    if ($path -match '\.md$' -or $path -match '^Docs/') { continue }
    # 先识别共享配置与资源，不能被 Tools 或 .txt 的忽略规则吞掉。
    if ($fullPatterns | Where-Object { $path -match $_ }) {
        $codeChanged = $true; $full = $true; $reasons.Add("共享输入：$path"); continue
    }
    if ($path -match '\.txt$') { continue }
    if ($path -match '^Tools/.*\.(ps1|py|sh|bat|cmd)$') { $scriptChanged = $true; continue }
    $codeChanged = $true
    if ($path -match $harnessPattern) { $harness = $true; foreach ($g in $smokeGroups) { [void]$groups.Add($g) }; continue }
    if ($schemaPatterns | Where-Object { $path -match $_ }) { $schema = $true; foreach ($g in $schemaGroups) { [void]$groups.Add($g) }; continue }
    if ($path -notmatch '\.cs$') { $full = $true; $reasons.Add("无法证明依赖：$path"); continue }
    $stem = [IO.Path]::GetFileNameWithoutExtension($path) -replace '\..*$', ''
    $matched = @(Get-RegistryGroups $stem)
    foreach ($key in $keywordGroups.Keys) {
        if ($path -match $key) { $matched += $keywordGroups[$key] }
    }
    if ($matched.Count -eq 0 -and $path -match '\.cs$') {
        $referenced = @(Get-ReferenceGroups $stem)
        if ($referenced.Count -gt $broadLimit) { $full = $true; $reasons.Add("引用过广（$($referenced.Count) 组）：$path"); continue }
        $matched += $referenced
    }
    foreach ($g in $matched) { [void]$groups.Add($g) }
    if ($matched.Count -ne 0) { continue }
    # 非 TCP 的自测文件由 --selftest-core / 完整 --selftest 覆盖，不触发全量 TCP。
    if ($path -match '^Server/DfoServer/SelfTests/[^/]+\.cs$') { $coreOnly.Add($path); continue }
    $full = $true; $reasons.Add("无法映射：$path")
}

$selected = @($groups | Where-Object { $groupText.Contains($_) })
$selection = if ($full) { '' } else { $selected -join ',' }

# 用历史耗时估算：串行为各组之和，并行按 4 分片取 max(最长组, 总和/4)。
$timings = @{}
foreach ($file in @($TimingsPath, (Join-Path $root 'build/tcp-tests/timings.json'))) {
    if (Test-Path -LiteralPath $file) {
        try { (Get-Content -LiteralPath $file -Raw | ConvertFrom-Json).PSObject.Properties | ForEach-Object { $timings[$_.Name] = [long]$_.Value } } catch { }
    }
}
function Get-Estimate([string[]]$names) {
    $known = @($names | Where-Object { $timings.ContainsKey($_) })
    if ($known.Count -eq 0) { return '暂无历史耗时' }
    $values = @($known | ForEach-Object { $timings[$_] })
    $sum = ($values | Measure-Object -Sum).Sum / 1000
    $max = ($values | Measure-Object -Maximum).Maximum / 1000
    '预计串行 {0:F0}s、4 分片约 {1:F0}s（{2}/{3} 组有记录）' -f $sum, [math]::Max($max, $sum / 4), $known.Count, $names.Count
}

Write-Output "基线：$baseText；改动文件 $($changed.Count) 个"
if ($scriptChanged) { Write-Output '工具脚本变化：须执行对应脚本/参数回归；验证工具使用 Tools/Test-DevelopmentChecks.ps1。未在此自动执行脚本验证。' }
if (-not $codeChanged -and -not $Full) { Write-Output 'SKIPPED build/TCP：仅文档或独立工具脚本变化；文档检查与上述脚本验证不能省略。' }
elseif ($full) {
    Write-Output ('全量 TCP：' + (($reasons | Select-Object -First 5) -join '；'))
    Write-Output ('全量估计：' + (Get-Estimate @($groupText.Keys)))
} else {
    Write-Output "受影响 TCP 组（$($selected.Count)）：$selection"
    if ($selected.Count) { Write-Output ('估计：' + (Get-Estimate $selected)) }
    if ($harness) { Write-Output '测试框架/TCP 入口变化：开发期运行冒烟组；合并前必须 -Full 全量 TCP 并运行完整 --selftest。' }
    if ($schema) { Write-Output '迁移/表结构变化：运行结构检查与持久化组；合并前必须 -Full 全量 TCP 并运行完整 --selftest。' }
}
if ($coreOnly.Count) { Write-Output ('仅自测覆盖（--selftest-core / 完整 --selftest）：' + ($coreOnly -join '，')) }

if (-not $Run -or (-not $codeChanged -and -not $Full)) { return }

# 构建始终交给 MSBuild 增量判定，避免忽略 SDK、包依赖或外部导入变化。
# 只有开发期 core 可复用本地成功记录；-Full / -NoCache 必须实跑。
. (Join-Path $PSScriptRoot 'DevelopmentCheckCache.ps1')
$ArtifactsPath = [IO.Path]::GetFullPath($ArtifactsPath)
New-Item -ItemType Directory -Path $ArtifactsPath -Force | Out-Null
$lock = $null
try {
    try { $lock = [IO.File]::Open((Join-Path $ArtifactsPath 'development-check.lock'), 'OpenOrCreate', 'ReadWrite', 'None') }
    catch { throw '同一产物目录正在执行检查；请等待结束，不能并发改写程序集。' }
    $variant = $Configuration.ToLowerInvariant()
    $artifactDirectory = Join-Path $ArtifactsPath "bin/DfoServer/$variant"
    $dll = Join-Path $artifactDirectory 'DfoServer.dll'
    $coreStamp = Join-Path $ArtifactsPath "affected-core-$variant.json"
    $key = Get-CheckSourceKey $root $Configuration
    $environment = Get-CheckEnvironmentKey
    $watch = [Diagnostics.Stopwatch]::StartNew()
    & dotnet build (Join-Path $root 'ServerS5A9.sln') -c $Configuration --nologo --artifacts-path $ArtifactsPath -m:1 -nr:false
    if ($LASTEXITCODE -ne 0) { throw "构建失败（$LASTEXITCODE）" }
    Write-Output ('EXECUTED build {0:F1}s' -f $watch.Elapsed.TotalSeconds)
    $artifact = Get-CheckArtifactKey $artifactDirectory
    Assert-CheckInputsUnchanged $root $Configuration $key $artifactDirectory $artifact $environment
    $watch.Restart()
    if (-not $full -and -not $NoCache -and (Test-CheckCache $coreStamp $key $environment $artifact)) {
        Write-CheckCacheReuse $coreStamp
    } else {
        # 失败或中断前先撤销旧通过记录，不能保留旧绿灯。
        Remove-Item -LiteralPath $coreStamp -ErrorAction SilentlyContinue
        & dotnet $dll --selftest-core
        if ($LASTEXITCODE -ne 0) { throw "--selftest-core 失败（$LASTEXITCODE）" }
        Assert-CheckInputsUnchanged $root $Configuration $key $artifactDirectory $artifact $environment
        Save-CheckCache $coreStamp $key $environment $artifact
        Write-Output ('EXECUTED --selftest-core {0:F1}s' -f $watch.Elapsed.TotalSeconds)
    }
    if (-not $full -and -not $selection) { Write-Output '没有受影响的 TCP 组。'; return }
    $runArgs = @()
    if ($PvfPath) { $runArgs += @('--pvf-path', $PvfPath) }
    $runArgs += '--selftest-tcp'
    if (-not $full) { $runArgs += $selection }
    # 只在 TCP 子进程期间覆盖耗时文件，不能污染下轮 core 的环境指纹。
    $previousTimings = $env:DFO_TCP_TIMINGS
    try {
        $env:DFO_TCP_TIMINGS = $TimingsPath
        $watch.Restart()
        & dotnet $dll @runArgs
        if ($LASTEXITCODE -ne 0) { throw "TCP 回归失败（$LASTEXITCODE）" }
    } finally { $env:DFO_TCP_TIMINGS = $previousTimings }
    Write-Output ('TCP {0:F1}s' -f $watch.Elapsed.TotalSeconds)
    if ($full) { Write-Output '全量 TCP 已通过；合并前还须运行一次完整 --selftest（Tools/Check-Development.ps1 -PvfPath <PVF>）。' }
    elseif ($harness -or $schema) { Write-Output '开发期定向检查已通过；合并前仍须 -Full 全量 TCP 与完整 --selftest。' }

    Assert-CheckInputsUnchanged $root $Configuration $key $artifactDirectory $artifact $environment
} catch {
    if ($lock -and $coreStamp) { Remove-Item -LiteralPath $coreStamp -ErrorAction SilentlyContinue }
    throw
} finally { if ($lock) { $lock.Dispose() } }
