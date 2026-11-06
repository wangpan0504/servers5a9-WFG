#requires -Version 7.0
<#
.SYNOPSIS
将干净源码生成的独立 DLL 包上传为 Forgejo 预发布版本；失败保留草稿，不替换已有版本。
.PARAMETER KeepLatest
发布成功后只保留最新 N 个已发布 Release（含本次），更旧的连同附件删除；默认 3，0 表示不清理。草稿与本次版本永不删除，git 标签保留；清理失败只警告。上传遇到 HTTP 413（配额不足）时，会先按 N-1 个旧版本清理一次再重试上传。
.PARAMETER CleanupDryRun
只列出会删除的旧 Release，不执行删除（含 413 重试前的清理）。
.PARAMETER CheckOnly
仅核对远端仓库、版本和标签，不构建、不上传、不创建版本。
#>
[CmdletBinding()]
param(
    [ValidatePattern('^(?:v[0-9]+\.[0-9]+\.[0-9]+(?:-[A-Za-z0-9]+(?:[.-][A-Za-z0-9]+)*)?)?$')]
    [ValidateLength(0, 64)][string]$Version = '',
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$Commit,
    [string]$Directory = 'dist/releases',
    [uri]$ApiUrl = $env:GITHUB_API_URL,
    [ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')][string]$Repository = $env:GITHUB_REPOSITORY,
    [ValidateRange(0, 100)][int]$KeepLatest = 3,
    [switch]$CleanupDryRun,
    [switch]$CheckOnly
)
$ErrorActionPreference = 'Stop'
if (-not $ApiUrl -or $ApiUrl.Scheme -ne 'https' -or $ApiUrl.UserInfo -or $ApiUrl.Query -or $ApiUrl.Fragment) {
    throw 'ApiUrl 必须是无凭据、查询或片段的 HTTPS API 地址。'
}
if (-not $Repository -or -not $env:FORGEJO_TOKEN) { throw '缺少仓库名称或工作流自动令牌 FORGEJO_TOKEN。' }
$endpoint = $ApiUrl.AbsoluteUri.TrimEnd('/') + '/repos/' + $Repository
$headers = @{ Authorization = 'token ' + $env:FORGEJO_TOKEN }
. (Join-Path $PSScriptRoot 'ReleaseRetention.ps1')

function Write-ApiFailure([string]$Method, [string]$Uri, $Status, $Headers, $Content) {
    # 仅输出诊断信息，不改变失败即抛错的语义；不输出请求头（含 token）。
    Write-Host "Forgejo API 失败诊断：$Method $Uri -> HTTP $Status"
    $keep = '^(Content-Length|Content-Type|Server|Via|Retry-After|Date|Location|X-.*)$'
    $lines = @()
    if ($null -ne $Headers) {
        foreach ($entry in $Headers.GetEnumerator()) {
            if ($entry.Key -match $keep) { $lines += ('  {0}: {1}' -f $entry.Key, (@($entry.Value) -join ', ')) }
        }
    }
    if ($lines.Count -gt 0) { Write-Host '响应头：'; $lines | ForEach-Object { Write-Host $_ } }
    else { Write-Host '响应头：（无可输出的相关头）' }
    $text = if ($null -eq $Content) { '' } elseif ($Content -is [string]) { $Content } else { try { $Content | ConvertTo-Json -Depth 5 -Compress } catch { [string]$Content } }
    if ($env:FORGEJO_TOKEN) { $text = $text.Replace($env:FORGEJO_TOKEN, '***') }
    $total = $text.Length
    if ($total -gt 2000) { $text = $text.Substring(0, 2000) + "...（已截断，共 $total 字符）" }
    Write-Host "响应体（前 2000 字符）：$text"
}

function Invoke-Api([string]$Method, [string]$Path, $Body = $null, $Form = $null, [switch]$AllowMissing) {
    $arguments = @{
        Uri = $endpoint + $Path; Method = $Method; Headers = $headers
        SkipHttpErrorCheck = $true; StatusCodeVariable = 'status'; ResponseHeadersVariable = 'responseHeaders'; TimeoutSec = 120
    }
    if ($null -ne $Body) { $arguments.Body = $Body | ConvertTo-Json; $arguments.ContentType = 'application/json; charset=utf-8' }
    if ($null -ne $Form) { $arguments.Form = $Form }
    $response = Invoke-RestMethod @arguments
    if ($status -eq 404 -and $AllowMissing) { return $null }
    if ($status -lt 200 -or $status -ge 300) { Write-ApiFailure $Method $arguments.Uri $status $responseHeaders $response }
    if ($status -lt 200 -or $status -ge 300) { throw "Forgejo API $Method $Path 失败（HTTP $status）；未覆盖已有版本。" }
    return $response
}

# 按保留策略清理旧 Release（只删 Release 及附件，不删 git 标签，不删草稿，不删本次版本）。
# 返回实际删除数；列表或删除失败只警告，不抛出。
function Invoke-ReleaseCleanup([string]$CurrentTag, [int]$Keep, [string]$Reason) {
    if ($Keep -le 0) { return 0 }
    try {
        $all = @()
        for ($page = 1; ; $page++) {
            $items = @(Invoke-Api 'Get' "/releases?limit=50&page=$page")
            if ($items.Count -eq 0) { break }
            $all += $items
            if ($page -ge 40) { throw 'Release 列表分页超过 40 页，放弃清理。' }
        }
        $victims = @(Select-ReleasesToPrune -Releases $all -KeepLatest $Keep -CurrentTag $CurrentTag)
        Write-Host ("Release 清理（{0}）：共 {1} 个，保留最新 {2} 个（含 {3}），将删除 {4} 个{5}" -f
            $Reason, $all.Count, $Keep, $CurrentTag, $victims.Count, $(if ($CleanupDryRun) { '【试运行，不删除】' } else { '' }))
        $deleted = 0
        foreach ($victim in $victims) {
            if ($victim.draft -or $victim.tag_name -ceq $CurrentTag) { continue }   # 双重保险
            Write-Host ("  - {0}（id {1}，发布 {2}）" -f $victim.tag_name, $victim.id, $victim.published_at)
            if ($CleanupDryRun) { continue }
            try { [void](Invoke-Api 'Delete' "/releases/$($victim.id)"); $deleted++ }
            catch { Write-Warning "删除 Release $($victim.tag_name) 失败：$($_.Exception.Message)" }
        }
        return $deleted
    } catch {
        Write-Warning "Release 清理已跳过：$($_.Exception.Message)"
        return 0
    }
}

# 先确定仓库可访问，不能把无权访问或 Releases 未启用误判成版本不存在。
$repo = Invoke-Api 'Get' ''
if (-not $repo.has_releases) { throw '仓库未启用 Releases，请由管理员在仓库设置中启用。' }
if (-not $Version) {
    # 标签及草稿都占用版本号，失败上传留下的草稿也不能被后续自动发布覆盖。
    $latest = $null
    foreach ($collection in @('tags', 'releases')) {
        for ($page = 1; ; $page++) {
            $items = @(Invoke-Api 'Get' "/${collection}?limit=100&page=$page")
            foreach ($item in $items) {
                $name = if ($collection -eq 'tags') { $item.name } else { $item.tag_name }
                if ($name -match '^v([0-9]+\.[0-9]+\.[0-9]+)(?:-[A-Za-z0-9]+(?:[.-][A-Za-z0-9]+)*)?$') {
                    $candidate = [version]$Matches[1]
                    if ($null -eq $latest -or $candidate -gt $latest) { $latest = $candidate }
                }
            }
            if ($items.Count -lt 100) { break }
        }
    }
    $Version = if ($null -eq $latest) { 'v0.1.0' } else { "v$($latest.Major).$($latest.Minor).$([long]$latest.Build + 1)" }
}
if ((Invoke-Api 'Get' "/releases/tags/$Version" -AllowMissing) -or
    (Invoke-Api 'Get' "/tags/$Version" -AllowMissing)) {
    throw "版本或标签 $Version 已存在，请使用新版本号；不会覆盖或删除旧版本。"
}
if ($CheckOnly) {
    Write-Host "版本可用：$Version，源码提交：$Commit"
    if ($env:GITHUB_OUTPUT) { Add-Content -LiteralPath $env:GITHUB_OUTPUT -Value "version=$Version" -Encoding utf8 }
    return
}

$root = Split-Path $PSScriptRoot -Parent
if (-not [IO.Path]::IsPathRooted($Directory)) { $Directory = Join-Path $root $Directory }
$archiveName = "S5A9Server-$Version-win-x64.zip"
$archive = Join-Path $Directory $archiveName
$checksum = $archive + '.sha256'
$expectedHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
if (([IO.File]::ReadAllText($checksum)).Trim() -cne "$expectedHash  $archiveName") { throw 'ZIP 校验值不匹配，拒绝上传。' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($archive)
try {
    $reader = [IO.StreamReader]::new($zip.GetEntry('server-module.json').Open())
    try { $metadata = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
    if ($metadata.schema -ne 1 -or $metadata.hostApi -ne 1 -or $metadata.entry -cne 'DfoServer.dll' -or
        $metadata.commit -cne $Commit -or $metadata.version -cne $Version -or
        $metadata.modified -isnot [bool] -or $metadata.modified -or $metadata.runtime -cne 'win-x64') {
        throw '版本包与干净源码提交不一致，拒绝上传。'
    }
    $expected = @{}
    foreach ($file in $metadata.files) {
        $name = [string]$file.path
        if (-not $name -or $name -match '[\\:]|(^|/)\.\.(/|$)|\.exe$|^(Data|Logs|Resources|Sqlite)/' -or $expected.ContainsKey($name)) {
            throw 'ZIP 含不允许的成员。'
        }
        $entry = $zip.GetEntry($name)
        if (-not $entry -or $entry.Length -ne $file.size) { throw 'ZIP 成员不完整。' }
        $stream = $entry.Open()
        try { $actual = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) }
        finally { $stream.Dispose() }
        if ($actual -ine $file.sha256) { throw 'ZIP 成员校验失败。' }
        $expected[$name] = $true
    }
    foreach ($required in @('DfoServer.dll','DfoServer.deps.json','DfoServer.runtimeconfig.json','PvfLib.dll','start-server.bat','start-server.sh')) {
        if (-not $expected.ContainsKey($required)) { throw 'ZIP 缺少服务端组件。' }
    }
    if ($zip.Entries.Count -ne $expected.Count + 1) { throw 'ZIP 含清单外文件。' }
} finally { $zip.Dispose() }
$body = @"
Windows x64 独立服务端 DLL 包，不提供服务端 EXE，也不依赖 95JP 启动器。
安装 .NET 10 ASP.NET Core Runtime x64 后，使用 start-server.bat 或 dotnet DfoServer.dll 启动。

源码提交：$Commit
对应源码：$($repo.html_url)/archive/$Commit.zip
软件许可：AGPL-3.0-only；完整正文与第三方声明随包附带。
本版本为预发布，构建不代表客户端验收通过。
自行准备匹配客户端的 PVF；不包含客户端、PVF、玩家数据库、日志或本机配置。
下载 ZIP 及同名 .sha256 文件核对完整性；先停服再替换程序，保留原有 Data、Logs 和配置。
"@
$release = Invoke-Api 'Post' '/releases' @{
    tag_name = $Version; target_commitish = $Commit; name = "S5A9Server $Version (Windows x64)"
    body = $body; draft = $true; prerelease = $true
}
if (-not $release.id -or -not $release.draft) { throw '创建草稿的返回状态异常，停止上传。' }
try {
    foreach ($file in @($archive, $checksum)) {
        $item = Get-Item -LiteralPath $file
        $assetPath = "/releases/$($release.id)/assets?name=$($item.Name)"
        Write-Host ("上传附件：{0}，大小 {1} 字节（{2:N2} MiB），目标 {3}" -f $item.Name, $item.Length, ($item.Length / 1MB), ($endpoint + $assetPath))
        try { $asset = Invoke-Api 'Post' $assetPath -Form @{ attachment = $item } }
        catch {
            if ($_.Exception.Message -notmatch 'HTTP 413') { throw }
            # 配额不足：只在确实失败后清理旧版本（保留 KeepLatest-1 个旧版本，加本次共 KeepLatest 个），再重试一次。
            Write-Warning '上传返回 HTTP 413（疑似配额不足），尝试清理旧版本后重试一次。'
            if ((Invoke-ReleaseCleanup $Version $KeepLatest '413 重试前') -le 0) { throw }
            $asset = Invoke-Api 'Post' $assetPath -Form @{ attachment = $item }
        }
        if ($asset.name -cne $item.Name -or $asset.size -ne $item.Length) { throw '附件返回的文件名或长度不符。' }
    }
    $assets = @(Invoke-Api 'Get' "/releases/$($release.id)/assets")
    if ($assets.Count -ne 2) { throw '草稿附件数量不符。' }
    $published = Invoke-Api 'Patch' "/releases/$($release.id)" @{ draft = $false }
    if ($published.draft -ne $false -or $published.prerelease -ne $true -or $published.tag_name -cne $Version) {
        throw '发布返回状态异常，请核对 Releases 页面。'
    }
    Write-Host "Release 下载页：$($published.html_url)"
} catch {
    Write-Warning "Release $($release.id) 未确认完成，请检查草稿与附件；本脚本不删除版本或重试覆盖。"
    throw
}

# 仅在新版本附件齐全并确认发布后清理旧版本；任何清理问题只警告，不影响本次发布结果。
[void](Invoke-ReleaseCleanup $Version $KeepLatest '发布完成后')
