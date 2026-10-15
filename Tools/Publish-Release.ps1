#requires -Version 7.0
<#
.SYNOPSIS
将干净源码生成的单 EXE 包上传为 Forgejo 预发布版本；失败保留草稿，不替换已有版本。
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
    [switch]$CheckOnly
)
$ErrorActionPreference = 'Stop'
if (-not $ApiUrl -or $ApiUrl.Scheme -ne 'https' -or $ApiUrl.UserInfo -or $ApiUrl.Query -or $ApiUrl.Fragment) {
    throw 'ApiUrl 必须是无凭据、查询或片段的 HTTPS API 地址。'
}
if (-not $Repository -or -not $env:FORGEJO_TOKEN) { throw '缺少仓库名称或工作流自动令牌 FORGEJO_TOKEN。' }
$endpoint = $ApiUrl.AbsoluteUri.TrimEnd('/') + '/repos/' + $Repository
$headers = @{ Authorization = 'token ' + $env:FORGEJO_TOKEN }

function Invoke-Api([string]$Method, [string]$Path, $Body = $null, $Form = $null, [switch]$AllowMissing) {
    $arguments = @{
        Uri = $endpoint + $Path; Method = $Method; Headers = $headers
        SkipHttpErrorCheck = $true; StatusCodeVariable = 'status'; TimeoutSec = 120
    }
    if ($null -ne $Body) { $arguments.Body = $Body | ConvertTo-Json; $arguments.ContentType = 'application/json; charset=utf-8' }
    if ($null -ne $Form) { $arguments.Form = $Form }
    $response = Invoke-RestMethod @arguments
    if ($status -eq 404 -and $AllowMissing) { return $null }
    if ($status -lt 200 -or $status -ge 300) { throw "Forgejo API $Method $Path 失败（HTTP $status）；未覆盖已有版本。" }
    return $response
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
    $names = @($zip.Entries | ForEach-Object FullName | Sort-Object)
    $expectedNames = @('README.txt', 'S5A9Server.exe', 'build-info.json') | Sort-Object
    if (($names -join '|') -cne ($expectedNames -join '|')) { throw 'ZIP 文件清单不符，拒绝上传。' }
    $reader = [IO.StreamReader]::new($zip.GetEntry('build-info.json').Open())
    try { $metadata = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
    if ($metadata.commit -cne $Commit -or $metadata.version -cne $Version -or
        $metadata.modified -ne $false -or $metadata.runtime -cne 'win-x64' -or $metadata.configuration -cne 'Release' -or
        $metadata.windowsStartupChecked -isnot [bool] -or $zip.GetEntry('S5A9Server.exe').Length -eq 0) {
        throw '版本包与干净源码提交不一致，拒绝上传。'
    }
} finally { $zip.Dispose() }
$startup = if ($metadata.windowsStartupChecked) { '已通过启动器/服务端帮助与内置 GM 检查' } else { 'Linux 交叉编译，未执行 Windows EXE 启动检查' }
$body = @"
Windows x64 自包含单 EXE 包，解压后双击 S5A9Server.exe；无需另装 .NET。

源码提交：$Commit
Windows 检查：$startup
本版本为预发布，自动检查不代替客户端验收。
自行准备匹配客户端的 PVF；不包含客户端、PVF、玩家数据库、日志或本机配置。
下载 ZIP 及同名 .sha256 文件核对完整性；原有 Data、Logs 和配置应保留。
"@
$release = Invoke-Api 'Post' '/releases' @{
    tag_name = $Version; target_commitish = $Commit; name = "S5A9Server $Version (Windows x64)"
    body = $body; draft = $true; prerelease = $true
}
if (-not $release.id -or -not $release.draft) { throw '创建草稿的返回状态异常，停止上传。' }
try {
    foreach ($file in @($archive, $checksum)) {
        $item = Get-Item -LiteralPath $file
        $asset = Invoke-Api 'Post' "/releases/$($release.id)/assets?name=$($item.Name)" -Form @{ attachment = $item }
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
