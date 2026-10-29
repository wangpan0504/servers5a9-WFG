param(
    [Parameter(Mandatory)][string]$Repository,
    [Parameter(Mandatory)][string]$ArtifactDirectory,
    [Parameter(Mandatory)][string]$ToolchainDirectory,
    [Parameter(Mandatory)][string]$Group,
    [ValidateSet('Debug','Release')][string]$Configuration = 'Debug',
    [string]$PvfPath,
    [Parameter(Mandatory)][string]$OutputPath
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'DevelopmentResultStore.ps1')
# 真实采集器不要求手填通过信息。首版记录可见闭包，不冒充自定义构建/系统native依赖已被证明。
$source = Get-ResultSourceManifest $Repository
function Get-InputFiles([string]$Directory) {
    $base = [IO.Path]::GetFullPath($Directory).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $all = @(Get-ChildItem -LiteralPath $base -Recurse -Force)
    if (@($all | Where-Object { $_.LinkType }).Count -gt 0) { throw '链接依赖尚未闭合。' }
    foreach ($file in $all | Where-Object { -not $_.PSIsContainer }) {
        if ($file.LinkType) { throw '符号链接依赖尚未解析；请严格实跑。' }
        @{ Name = [IO.Path]::GetRelativePath($base, $file.FullName).Replace('\','/'); Path = $file.FullName; Digest = Get-ResultFileDigest $file.FullName }
    }
}
$artifactFull = [IO.Path]::GetFullPath($ArtifactDirectory).TrimEnd([IO.Path]::DirectorySeparatorChar)
$artifactRoot = [IO.Directory]::GetParent([IO.Directory]::GetParent([IO.Directory]::GetParent($artifactFull).FullName).FullName).FullName
$expected = Join-Path $artifactRoot ('bin/DfoServer/' + $Configuration.ToLowerInvariant())
if ($artifactFull -cne [IO.Path]::GetFullPath($expected)) { throw '需要标准artifacts-path/bin/DfoServer/<configuration>布局。' }
$artifacts = @(Get-InputFiles $ArtifactDirectory)
if (-not ($artifacts.Name -contains 'DfoServer.dll') -or -not ($artifacts.Name -contains 'DfoServer.deps.json') -or
    -not ($artifacts.Name -contains 'DfoServer.runtimeconfig.json')) { throw '缺少真实完整runner产物。' }
$environment = [ordered]@{}
foreach ($row in Get-ChildItem Env: | Sort-Object Name) { $environment[$row.Name] = $row.Value }
$resources = @()
if ($PvfPath) { $resources += @{ Name='Script.pvf'; Path=(Resolve-Path -LiteralPath $PvfPath).ProviderPath; Digest=Get-ResultFileDigest $PvfPath } }
$descriptor = [ordered]@{
    SchemaVersion=1; Closure='observed-inputs-v1'; SourceAtCapture=$source.Key
    ArtifactsRoot=$artifactRoot; ArtifactDirectory=[IO.Path]::GetFullPath($ArtifactDirectory); ToolchainDirectory=[IO.Path]::GetFullPath($ToolchainDirectory)
    Build=@($artifacts | Where-Object { $_.Name.EndsWith('.deps.json') -or $_.Name.EndsWith('.runtimeconfig.json') })
    Toolchain=@(Get-InputFiles $ToolchainDirectory); Dependencies=$artifacts
    Native=@($artifacts | Where-Object { $_.Name.StartsWith('runtimes/') }); Resources=$resources
    Configuration=$Configuration; EnvironmentDigest=(Get-ResultDigest (ConvertTo-ResultCanonical $environment))
    TimePolicy=@{Kind='real-time-no-reuse';Value='runtime-default'}; Scope=@($Group)
    Limitations=@('actual-consumed-build-input-closure-not-proven','system-native-closure-not-proven','real-clock-not-frozen')
}
$parent = Split-Path ([IO.Path]::GetFullPath($OutputPath)) -Parent
[IO.Directory]::CreateDirectory($parent) | Out-Null
[IO.File]::WriteAllText([IO.Path]::GetFullPath($OutputPath), (ConvertTo-ResultCanonical $descriptor), [Text.UTF8Encoding]::new($false))
Write-Output ('Captured actual inputs; reuse disabled until dependency/time closure is proven: ' + $OutputPath)
