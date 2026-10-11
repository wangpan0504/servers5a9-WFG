<#
.SYNOPSIS
构建 Windows x64 自包含服务端及启动器，保留运行目录中的存档与日志。
.PARAMETER OutputDirectory
运行目录，默认 dist/win-x64。相对路径以仓库根目录为基准。
.PARAMETER PvfPath
首次运行所需的 Script.pvf；仅在运行目录尚无该文件时复制，不替换已有资源。
.PARAMETER SingleFile
将启动器和服务端托管依赖合并为一个 S5A9Server.exe，默认输出 dist/win-x64-single。
#>
[CmdletBinding()]
param(
    [ValidateNotNullOrEmpty()][string]$OutputDirectory = 'dist/win-x64',
    [string]$PvfPath,
    [switch]$SingleFile
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
if ($SingleFile -and -not $PSBoundParameters.ContainsKey('OutputDirectory')) {
    $OutputDirectory = 'dist/win-x64-single'
}
$program = if ($SingleFile) { 'S5A9Server.exe' } else { 'DfoServer.exe' }

function Get-FullPath([string]$Path) {
    if (-not [IO.Path]::IsPathRooted($Path)) { $Path = Join-Path $root $Path }
    return [IO.Path]::GetFullPath($Path).TrimEnd([IO.Path]::DirectorySeparatorChar)
}

function Test-Within([string]$Path, [string]$Parent) {
    return $Path.Equals($Parent, [StringComparison]::OrdinalIgnoreCase) -or
        $Path.StartsWith($Parent.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)
}

function Assert-NoLinks([string]$Path) {
    $current = $Path
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "运行目录及其父目录不能是目录链接：$current"
            }
        }
        $current = Split-Path $current -Parent
    }
}

function Invoke-Dotnet([string[]]$Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet 执行失败，退出码：$LASTEXITCODE" }
}

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw '本脚本发布 Windows x64 本机测试包，请在 Windows 上执行。' }
$output = Get-FullPath $OutputDirectory
if ((Test-Within $root $output) -or $output -eq [IO.Path]::GetPathRoot($output).TrimEnd('\')) {
    throw '输出目录不能是磁盘根目录、仓库根目录或仓库的父目录。'
}
foreach ($name in @('Server', 'Tools', 'Docs', '.git', '.idea', '.forgejo', 'build')) {
    if (Test-Within $output (Join-Path $root $name)) {
        throw "输出目录不能放在源码、工具或临时构建目录中：$name"
    }
}
Assert-NoLinks $output
if (Test-Path -LiteralPath $output) {
    if (-not (Test-Path -LiteralPath $output -PathType Container)) { throw '输出路径必须是目录。' }
    if (@(Get-ChildItem -LiteralPath $output -Force).Count -and
        -not (Test-Path -LiteralPath (Join-Path $output $(if ($SingleFile) { $program } else { 'DfoServer.dll' })) -PathType Leaf)) {
        throw '已有非空目录与本次发布模式不匹配；请选择空目录或同一模式的运行目录。'
    }
    foreach ($item in Get-ChildItem -LiteralPath $output -Recurse -Force) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "输出目录包含链接，无法安全备份或更新：$($item.FullName)"
        }
    }
}
$sourcePvf = $null
if ($PvfPath) {
    $sourcePvf = Get-FullPath $PvfPath
    if (-not (Test-Path -LiteralPath $sourcePvf -PathType Leaf)) { throw "PVF 文件不存在：$sourcePvf" }
}
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw '请先安装 .NET 10 SDK。' }
$sdks = & dotnet --list-sdks
if ($LASTEXITCODE -ne 0 -or -not @($sdks | Where-Object { $_ -match '^10\.' }).Count) {
    throw '构建需要 .NET 10 SDK；仅安装 Runtime 不够。'
}

# 先发布到独立临时目录，编译失败不覆盖现有程序与玩家数据。
$buildRoot = Join-Path $root 'build'
$stage = Join-Path $buildRoot ('publish-' + [guid]::NewGuid().ToString('N'))
$payload = Join-Path $stage 'publish'
Write-Host "发布 Windows x64 Release 自包含程序，运行目录：$output"
if (-not $SingleFile) {
    Invoke-Dotnet @('publish', (Join-Path $root 'Server/DfoServer/DfoServer.csproj'),
        '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '--nologo',
        '--artifacts-path', (Join-Path $stage 'artifacts'), '-o', $payload)
}
$launcherPayload = Join-Path $stage 'launcher'
$launcherArguments = @('publish', (Join-Path $root 'Tools/ServerLauncher/ServerLauncher.csproj'),
    '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '--nologo',
    '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:EnableCompressionInSingleFile=true',
    '--artifacts-path', (Join-Path $stage 'artifacts'), '-o', $launcherPayload)
if ($SingleFile) { $launcherArguments += '-p:BundleServer=true' }
Invoke-Dotnet $launcherArguments
New-Item -ItemType Directory -Path $payload -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $launcherPayload $(if ($SingleFile) { $program } else { 'DfoLauncher.exe' })) -Destination $payload
$requiredFiles = if ($SingleFile) { @($program) } else {
    @('DfoServer.exe', 'DfoServer.dll', 'Resources/channel.txt', 'Sqlite/game_schema.sql', 'start-server.bat', 'DfoLauncher.exe')
}
foreach ($name in $requiredFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $payload $name) -PathType Leaf)) {
        throw "发布文件缺失：$name；临时产物保留在 $stage"
    }
}
if ($SingleFile) {
    $serverCheck = Start-Process -FilePath (Join-Path $payload $program) -ArgumentList '--server','--help' -WindowStyle Hidden -Wait -PassThru
    if ($serverCheck.ExitCode -ne 0) { throw "内置服务端启动检查失败；临时产物保留在 $stage" }
} else {
    & (Join-Path $payload $program) --help
    if ($LASTEXITCODE -ne 0) { throw "发布程序启动检查失败；临时产物保留在 $stage" }
}
$launcherCheck = Start-Process -FilePath (Join-Path $payload $(if ($SingleFile) { $program } else { 'DfoLauncher.exe' })) -ArgumentList '--help' -WindowStyle Hidden -Wait -PassThru
if ($launcherCheck.ExitCode -ne 0) { throw "启动器检查失败；临时产物保留在 $stage" }
$gmCheck = Start-Process -FilePath (Join-Path $payload $(if ($SingleFile) { $program } else { 'DfoLauncher.exe' })) -ArgumentList '--selftest-gm' -WindowStyle Hidden -Wait -PassThru
if ($gmCheck.ExitCode -ne 0) { throw "内置GM网页检查失败；临时产物保留在 $stage" }

$data = Join-Path $output 'Data'
New-Item -ItemType Directory -Path $data -Force | Out-Null
$guard = $null
$launcherGuard = $null
$backup = $null
try {
    # 持有服务端同一实例锁，覆盖期间服务端也不能打开数据库。
    try {
        $guard = [IO.File]::Open((Join-Path $data 'server.lock'),
            [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        $launcherGuard = [IO.File]::Open((Join-Path $data 'launcher.lock'),
            [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    } catch {
        throw '运行目录正被占用或不可写。请正常停止服务并关闭启动器后重新构建；未覆盖程序和存档。'
    }
    $savedData = @(Get-ChildItem -LiteralPath $data -Force | Where-Object { $_.Name -notin @('server.lock', 'launcher.lock') })
    if ($savedData.Count) {
        # 整个 Data 备份包含 SQLite 的 WAL/SHM 和 PVF；实例锁不属于存档。
        $backupRoot = $output + '.backups'
        Assert-NoLinks $backupRoot
        $backup = Join-Path $backupRoot ((Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
        $backupData = Join-Path $backup 'Data'
        New-Item -ItemType Directory -Path $backupData -Force | Out-Null
        foreach ($item in $savedData) {
            Copy-Item -LiteralPath $item.FullName -Destination $backupData -Recurse -Force
        }
        Write-Host "更新前 Data 备份：$backup"
    }
    foreach ($item in Get-ChildItem -LiteralPath $payload -Force) {
        if ($item.Name -in @('Data', 'Logs', 'launcher.local.json')) { continue }
        Copy-Item -LiteralPath $item.FullName -Destination $output -Recurse -Force
    }
    $pvfDirectory = Join-Path $data 'Pvf'
    New-Item -ItemType Directory -Path $pvfDirectory -Force | Out-Null
    $readme = Join-Path $pvfDirectory 'README.md'
    if (-not $SingleFile -and -not (Test-Path -LiteralPath $readme)) {
        Copy-Item -LiteralPath (Join-Path $payload 'Data/Pvf/README.md') -Destination $readme
    }
    $targetPvf = Join-Path $pvfDirectory 'Script.pvf'
    if (Test-Path -LiteralPath $targetPvf -PathType Leaf) {
        Write-Host "保留已有 PVF：$targetPvf"
    } else {
        if (-not $sourcePvf -and -not $SingleFile -and (Test-Path -LiteralPath (Join-Path $payload 'Data/Pvf/Script.pvf') -PathType Leaf)) {
            $sourcePvf = Join-Path $payload 'Data/Pvf/Script.pvf'
        }
        if ($sourcePvf) { Copy-Item -LiteralPath $sourcePvf -Destination $targetPvf }
    }
    if ($SingleFile) {
        $installedCheck = Start-Process -FilePath (Join-Path $output $program) -ArgumentList '--server','--help' -WindowStyle Hidden -Wait -PassThru
        if ($installedCheck.ExitCode -ne 0) { throw '运行目录中的单文件程序启动检查失败。' }
    } else {
        & (Join-Path $output $program) --help
        if ($LASTEXITCODE -ne 0) { throw '运行目录中的程序启动检查失败。' }
    }
    $launcherConfig = Join-Path $output 'launcher.local.json'
    if (-not $SingleFile -and -not (Test-Path -LiteralPath $launcherConfig)) {
        $defaults = [ordered]@{
            ServerIp = '127.0.0.1'; ChannelPort = 23100; GamePort = 23101
            PvfPath = 'Data/Pvf/Script.pvf'; DataDirectory = 'Data'
        }
        [IO.File]::WriteAllText($launcherConfig, ($defaults | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
        Write-Host "已生成默认启动配置：$launcherConfig"
    } elseif (Test-Path -LiteralPath $launcherConfig) {
        Write-Host "保留已有启动配置：$launcherConfig"
    }
} catch {
    Write-Warning "构建/更新未完成，临时产物保留在 $stage；Data 和 Logs 不会被删除。"
    if ($backup) { Write-Warning "更新前 Data 备份：$backup" }
    throw
} finally {
    if ($launcherGuard) { $launcherGuard.Dispose() }
    if ($guard) { $guard.Dispose() }
}

# 仅删除本次创建、且经绝对路径核对位于 build 内的临时产物。
$resolvedStage = (Resolve-Path -LiteralPath $stage).Path
if (-not (Test-Within $resolvedStage $buildRoot) -or $resolvedStage -eq $buildRoot) {
    throw '临时目录边界检查失败，未清理。'
}
Remove-Item -LiteralPath $resolvedStage -Recurse -Force
if ($SingleFile) {
    Write-Host "构建完成。单程序：$output\$program"
    Write-Host '分发程序只需复制这个 EXE，无需附带 DLL、Resources、Sqlite 或启动脚本。PVF 和存档仍由启动界面选择。'
} else {
    Write-Host "构建完成。双击启动器：$output\DfoLauncher.exe"
    Write-Host "无参数的 start-server.bat 也会打开启动器；带参数时直接运行命令行服务。"
}
Write-Host "默认本机连接：127.0.0.1，频道 TCP 23100，游戏 TCP 23101；实际设置读取 launcher.local.json。"
Write-Host "存档：$data\game.db；日志：$output\Logs。后续构建请始终使用同一输出目录。"
if (-not (Test-Path -LiteralPath $targetPvf -PathType Leaf)) {
    Write-Warning "尚未准备资源，请将匹配客户端的 Script.pvf 放到 $targetPvf 后再启动。"
}
