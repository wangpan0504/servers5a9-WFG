# 同主机、同账号、所有工作树共用的协作式重型任务预算；不提供动态 RAM 调度。
# 默认按主机名隔离账号目录；跨账号需操作者预先配置同一本机目录及正常访问权限。
# 仅用于协作式本地文件锁，不是跨容器/网络文件系统或恶意参与者的安全边界。
# 每个重型任务持有一个 permit，内部不得再并行启动多个重型分片。
# 文件始终保留，禁止通过删除锁文件、TTL 或仅检查 PID 来抢占。
Set-StrictMode -Version Latest
$script:permits = @{}

function Open-BudgetLock([string]$Path) {
    try { return [IO.File]::Open($Path, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None) }
    catch [IO.IOException] { return $null }
}

function Read-BudgetRecord([IO.FileStream]$Stream) {
    $Stream.Position = 0
    $reader = [IO.StreamReader]::new($Stream, [Text.Encoding]::UTF8, $true, 1024, $true)
    try { return $reader.ReadToEnd() } finally { $reader.Dispose() }
}

function Write-BudgetRecord([IO.FileStream]$Stream, [string]$Text) {
    $bytes = [Text.Encoding]::UTF8.GetBytes($Text)
    $Stream.Position = 0
    $Stream.SetLength(0)
    $Stream.Write($bytes, 0, $bytes.Length)
    $Stream.Flush($true)
}

function Enter-TestResourceBudget {
    [CmdletBinding()]
    param(
        [string]$MaxHeavyJobs = $env:DFO_TEST_MAX_HEAVY_JOBS,
        [ValidateRange(0,86400)][int]$TimeoutSeconds = 600,
        [string]$BudgetDirectory = $env:DFO_TEST_BUDGET_DIRECTORY
    )
    if ($script:permits.Count -gt 0) { throw '同进程已持有重型预算；禁止嵌套获取，应复用外层生命周期。' }
    if ([string]::IsNullOrEmpty($MaxHeavyJobs)) { $MaxHeavyJobs = '1' }
    if ($MaxHeavyJobs -notmatch '^[1-9][0-9]*$' -or $MaxHeavyJobs.Length -gt 2 -or [int]$MaxHeavyJobs -gt 64) {
        throw 'DFO_TEST_MAX_HEAVY_JOBS 必须为 1..64 的整数；默认 1。'
    }
    $capacity = [int]$MaxHeavyJobs
    if ([string]::IsNullOrEmpty($BudgetDirectory)) {
        $homePath = [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)
        if (-not $homePath) { throw '无法确定账号目录；请统一配置 DFO_TEST_BUDGET_DIRECTORY。' }
        $BudgetDirectory = Join-Path $homePath ('.s5a9-test-budget/' + [Environment]::MachineName)
    }
    if (-not [IO.Path]::IsPathFullyQualified($BudgetDirectory)) { throw '预算目录必须为主机统一的绝对路径，不能取各工作树的相对路径。' }
    [IO.Directory]::CreateDirectory($BudgetDirectory) | Out-Null
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $gate = $null
    do {
        $gate = Open-BudgetLock (Join-Path $BudgetDirectory 'capacity.lock')
        if ($null -ne $gate) { break }
        if ($watch.Elapsed.TotalSeconds -ge $TimeoutSeconds) { throw '等待主机预算配置锁超时；未启动任务。' }
        Start-Sleep -Milliseconds 100
    } while ($true)
    try {
        $record = Read-BudgetRecord $gate
        if ($record.Length -eq 0) { Write-BudgetRecord $gate "v1:$capacity" }
        elseif ($record -cne "v1:$capacity") {
            throw "主机预算配置冲突或损坏（现有 '$record'，请求 v1:$capacity）；不得通过换目录绕开，需先排空全部任务后由操作者统一调整。"
        }
    } finally { $gate.Dispose() }
    $blocked = @()
    do {
        $blocked = @()
        for ($slot = 1; $slot -le $capacity; $slot++) {
            $stream = Open-BudgetLock (Join-Path $BudgetDirectory "heavy-$slot.lock")
            if ($null -eq $stream) { continue }
            $retained = $false
            try {
                $record = Read-BudgetRecord $stream
                if ($record.Length -gt 0) {
                    # 即使 PID 已退出，其子进程也可能存活；残留记录必须人工核实，不自动接管。
                    $blocked += $slot
                    continue
                }
                $owner = [Diagnostics.Process]::GetCurrentProcess()
                try { $startTicks = $owner.StartTime.ToUniversalTime().Ticks } finally { $owner.Dispose() }
                $permit = [pscustomobject]@{
                    OwnerToken = [guid]::NewGuid().ToString('N'); ProcessId = $PID
                    ProcessStartUtcTicks = $startTicks; Slot = $slot; Capacity = $capacity
                    BudgetDirectory = $BudgetDirectory; WaitMs = [math]::Round($watch.Elapsed.TotalMilliseconds, 1)
                    AdmittedUtc = [DateTime]::UtcNow.ToString('o'); Scheduling = 'fixed-permit'
                    ProcessTreePeakRssBytes = $null; ProcessTreePeakPrivateBytes = $null
                    MemoryMeasurement = 'unknown: process-tree sampling not implemented'
                }
                Write-BudgetRecord $stream ($permit | ConvertTo-Json -Compress)
                $script:permits[$permit.OwnerToken] = @{ Permit = $permit; Stream = $stream }
                $retained = $true
                Write-Verbose "Heavy admission: slot=$slot/$capacity waitMs=$($permit.WaitMs)"
                return $permit
            } finally { if (-not $retained) { $stream.Dispose() } }
        }
        if ($watch.Elapsed.TotalSeconds -ge $TimeoutSeconds) {
            throw "等待主机重型任务预算超时（waitMs=$($watch.ElapsedMilliseconds)，保守隔离的残留槽位=$($blocked -join ',')）；未启动任务，不按 TTL 抢占、不停止未知进程。"
        }
        Start-Sleep -Milliseconds 100
    } while ($true)
}

function Exit-TestResourceBudget {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Permit)
    if (-not $script:permits.ContainsKey($Permit.OwnerToken)) { throw '未知或已释放的预算 owner。' }
    $entry = $script:permits[$Permit.OwnerToken]
    if (-not [object]::ReferenceEquals($entry.Permit, $Permit) -or $Permit.ProcessId -ne $PID) { throw '预算 owner 身份不匹配。' }
    # 调用者须先等待所有自有重型子进程退出，再释放；异常退出时保留 owner 记录隔离。
    Write-BudgetRecord $entry.Stream ''
    $entry.Stream.Dispose()
    $script:permits.Remove($Permit.OwnerToken)
}

Export-ModuleMember -Function Enter-TestResourceBudget, Exit-TestResourceBudget
