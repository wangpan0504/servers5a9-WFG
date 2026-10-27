# 仅供无资源开发检查复用成功记录；完整验收及真实资源测试不读取此缓存。
# 源码、SDK/环境与实际程序集分别核对，不以 mtime 或单个 DLL 存在代替内容。
function Get-CheckDigest([string[]]$Parts) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes(($Parts -join "`n")))).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose() }
}

function Get-CheckFileHash([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '') }
    finally { $sha.Dispose(); $stream.Dispose() }
}

function Get-CheckSourceKey([string]$Root, [string]$Configuration) {
    # 源码 ZIP 也可完整检查；没有 Git 时不复用、不写成功缓存。
    if (-not (Get-Command git -ErrorAction SilentlyContinue)) { return $null }
    $head = & git -C $Root rev-parse HEAD 2>$null
    if ($LASTEXITCODE -ne 0) { return $null }
    $paths = @(& git -C $Root -c core.quotepath=false ls-files --cached --others --exclude-standard)
    if ($LASTEXITCODE -ne 0) { throw '无法枚举检查源码。' }
    $parts = [Collections.Generic.List[string]]::new()
    $parts.Add('development-check-cache-v2')
    $parts.Add($Root)
    $parts.Add($Configuration)
    $parts.Add($head)
    foreach ($path in ($paths | Sort-Object -Unique)) {
        # Git 对控制字符文件名采用引号编码；无法可靠解析时关闭缓存，不遗漏该输入。
        if ($path.StartsWith('"')) { return $null }
        # 真实资源始终实跑，不计算整份 PVF 哈希；资源文件不是 core 缓存的输入。
        if ([IO.Path]::GetExtension($path) -ieq '.pvf') { continue }
        $file = Join-Path $Root $path
        if (Test-Path -LiteralPath $file -PathType Leaf) {
            $parts.Add($path + ' ' + (Get-CheckFileHash $file))
        } else { $parts.Add($path + ' missing') }
    }
    Get-CheckDigest $parts.ToArray()
}

function Get-CheckEnvironmentKey {
    # 外部玩家样本/客户端资产不纳入缓存，不读取其内容或凭据。
    if ($env:DFO_MIGRATION_SAMPLE_DB -or $env:DFO_IMAGEPACKS2) { return $null }
    $info = @(& dotnet --info)
    if ($LASTEXITCODE -ne 0) { throw '无法确认 .NET SDK 与运行时。' }
    $parts = [Collections.Generic.List[string]]::new()
    $parts.Add(((Get-Command dotnet -ErrorAction Stop).Source))
    $parts.Add(($info -join "`n"))
    $parts.Add([Environment]::OSVersion.ToString())
    $parts.Add([Globalization.CultureInfo]::CurrentCulture.Name)
    $parts.Add([TimeZoneInfo]::Local.Id)
    $parts.Add((Get-Location).Path)
    $parts.Add([DateTime]::UtcNow.ToString('yyyy-MM-dd'))
    foreach ($entry in (Get-ChildItem Env: | Sort-Object Name)) {
        if ($entry.Name -match '^(DOTNET|COMPlus|DFO_|GM_|NUGET|MSBUILD|COREHOST|LC_)' -or
            $entry.Name -in @('PATH','HOME','USERPROFILE','LANG','TZ','TMP','TEMP','TMPDIR')) {
            $parts.Add($entry.Name + '=' + $entry.Value)
        }
    }
    # 环境只保存摘要，绝不把原值（可能含凭据）写入缓存或日志。
    Get-CheckDigest $parts.ToArray()
}

function Get-CheckArtifactKey([string]$Directory) {
    if (-not (Test-Path -LiteralPath (Join-Path $Directory 'DfoServer.dll') -PathType Leaf)) { return $null }
    $parts = [Collections.Generic.List[string]]::new()
    foreach ($file in (Get-ChildItem -LiteralPath $Directory -Recurse -File | Sort-Object FullName)) {
        if ($file.Extension -ieq '.pvf') { continue }
        $parts.Add($file.FullName.Substring($Directory.Length) + ' ' + (Get-CheckFileHash $file.FullName))
    }
    Get-CheckDigest $parts.ToArray()
}

function Test-CheckCache([string]$Path, [string]$Source, [string]$Environment, [string]$Artifact) {
    if (-not $Source -or -not $Environment -or -not $Artifact -or -not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $false }
    try {
        $record = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
        $completed = [DateTimeOffset]::MinValue
        if (-not [DateTimeOffset]::TryParse([string]$record.completedUtc, [ref]$completed)) { return $false }
        return $record.version -eq 2 -and $record.status -eq 'passed' -and
            $record.command -eq '--selftest-core' -and $record.source -eq $Source -and
            $record.environment -eq $Environment -and $record.artifact -eq $Artifact
    } catch { return $false }
}

function Write-CheckCacheReuse([string]$Path) {
    $record = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    Write-Output "CACHED --selftest-core：复用 $Path；原完成时间 $($record.completedUtc)；不是本轮重新执行。"
}

function Save-CheckCache([string]$Path, [string]$Source, [string]$Environment, [string]$Artifact) {
    if (-not $Source -or -not $Environment -or -not $Artifact) { return }
    $record = [ordered]@{ version = 2; status = 'passed'; command = '--selftest-core'; source = $Source;
        environment = $Environment; artifact = $Artifact; completedUtc = [DateTime]::UtcNow.ToString('o') }
    $temp = $Path + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
    try {
        [IO.File]::WriteAllText($temp, ($record | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
        if ([IO.File]::Exists($Path)) { [IO.File]::Replace($temp, $Path, [NullString]::Value) }
        else { [IO.File]::Move($temp, $Path) }
    } finally { if ([IO.File]::Exists($temp)) { [IO.File]::Delete($temp) } }
}

function Assert-CheckInputsUnchanged([string]$Root, [string]$Configuration, [string]$Source,
    [string]$ArtifactDirectory, [string]$Artifact, [string]$Environment) {
    if ($Environment -and (Get-CheckEnvironmentKey) -ne $Environment) {
        throw '检查期间 SDK 或环境改变：撤销成功缓存，请固定输入后重跑。'
    }
    if (-not $Source) { return }
    if ((Get-CheckSourceKey $Root $Configuration) -ne $Source -or
        (Get-CheckArtifactKey $ArtifactDirectory) -ne $Artifact) {
        throw '检查期间源码或程序集改变：结果不缓存，也不能作为当前代码通过记录；请停止并发构建后重跑。'
    }
}
