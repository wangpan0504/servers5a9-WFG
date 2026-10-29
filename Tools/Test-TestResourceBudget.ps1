# 纯合成进程/工作树回归；不启动服务、不加载 PVF、不构建服务器。
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'TestResourceBudget.psm1') -Force
$root = Join-Path ([IO.Path]::GetTempPath()) ('s5a9-budget-test-' + [guid]::NewGuid().ToString('N'))
$checks = 0
$children = [Collections.Generic.List[Diagnostics.Process]]::new()
function Assert([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "FAIL: $Message" }
    $script:checks++
}
function Reject([scriptblock]$Action, [string]$Message) {
    $failed = $false
    try { & $Action | Out-Null } catch { $failed = $true }
    Assert $failed $Message
}
function Wait-File([string]$Path) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while (-not (Test-Path -LiteralPath $Path)) {
        if ($watch.Elapsed.TotalSeconds -gt 15) { throw "等待合成信号超时：$Path" }
        Start-Sleep -Milliseconds 25
    }
}
function Start-Worker([string]$Worktree, [string]$Budget, [string]$Mode, [string]$Signal, [string]$Capacity = '1') {
    $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $PSHOME $(if ($IsWindows) { 'pwsh.exe' } else { 'pwsh' })))
    $start.UseShellExecute = $false
    $start.WorkingDirectory = $Worktree
    foreach ($arg in @('-NoProfile','-File',(Join-Path $root 'worker.ps1'),$Budget,$Mode,$Signal,$Capacity)) { $start.ArgumentList.Add($arg) }
    $process = [Diagnostics.Process]::Start($start)
    $children.Add($process)
    return $process
}
function Finish-Worker($Process, [int]$Expected = 0) {
    Assert ($Process.WaitForExit(15000)) '合成子进程按时结束'
    Assert ($Process.ExitCode -eq $Expected) "合成子进程退出码 $Expected"
}
try {
    New-Item -ItemType Directory -Path $root | Out-Null
    $a = Join-Path $root 'tree-a'; $b = Join-Path $root 'tree-b'
    New-Item -ItemType Directory -Path $a | Out-Null
    & git -C $a init --quiet
    Copy-Item (Join-Path $PSScriptRoot 'TestResourceBudget.psm1') $a
    & git -C $a add .
    & git -C $a -c user.name=BudgetTests -c user.email=budget@example.invalid commit --quiet -m fixture
    & git -C $a worktree add --quiet --detach $b
    if ($LASTEXITCODE -ne 0) { throw '无法建立合成 worktree。' }
    @'
param($Budget, $Mode, $Signal, $Capacity)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path (Get-Location) 'TestResourceBudget.psm1')
[IO.File]::WriteAllText($Signal + '.ready', 'ready')
if ($Mode -eq 'timeout') {
    try { $p = Enter-TestResourceBudget -BudgetDirectory $Budget -MaxHeavyJobs $Capacity -TimeoutSeconds 1; exit 11 }
    catch { [IO.File]::WriteAllText($Signal, $_.Exception.Message); exit 0 }
}
$p = Enter-TestResourceBudget -BudgetDirectory $Budget -MaxHeavyJobs $Capacity -TimeoutSeconds 10
$p | ConvertTo-Json | Set-Content -LiteralPath $Signal
if ($Mode -eq 'crash') { [Environment]::Exit(23) }
Exit-TestResourceBudget $p
'@ | Set-Content -LiteralPath (Join-Path $root 'worker.ps1')
    $budget = Join-Path $root 'host-budget'
    $p = Enter-TestResourceBudget -BudgetDirectory $budget -TimeoutSeconds 0
    try {
        Assert ($p.Capacity -eq 1 -and $p.Slot -eq 1) '默认单个重型 permit'
        Assert ($null -eq $p.ProcessTreePeakRssBytes -and $p.MemoryMeasurement.StartsWith('unknown:')) '缺测量明确 unknown'
        $signal = Join-Path $root 'blocked'
        $worker = Start-Worker $b $budget 'timeout' $signal
        Finish-Worker $worker
        Assert ((Get-Content $signal -Raw) -match '超时') '第二工作树不能越过活跃 owner'
        # 伪造同 token 对象不能释放真正 owner。
        $fake = $p | ConvertTo-Json | ConvertFrom-Json
        Reject { Exit-TestResourceBudget $fake } '拒绝伪造 owner'
        $signal = Join-Path $root 'admitted'
        $worker = Start-Worker $b $budget 'normal' $signal
        Wait-File ($signal + '.ready')
    } finally { Exit-TestResourceBudget $p }
    Finish-Worker $worker
    $admission = Get-Content $signal -Raw | ConvertFrom-Json
    Assert ($admission.OwnerToken -ne $p.OwnerToken -and $admission.WaitMs -ge 0) '释放后另一个工作树获得新 owner，报告等待指标'
    Reject { Exit-TestResourceBudget $p } '拒绝重复释放旧 owner'
    $p2 = Enter-TestResourceBudget -BudgetDirectory $budget -TimeoutSeconds 0
    Exit-TestResourceBudget $p2
    Assert (Test-Path (Join-Path $budget 'heavy-1.lock')) '正常释放不删除锁 inode'
    foreach ($bad in @('0','-1','65','1.5','abc',' 2','99999999999999999')) {
        Reject { Enter-TestResourceBudget -BudgetDirectory $budget -MaxHeavyJobs $bad -TimeoutSeconds 0 } "拒绝非法容量 $bad"
    }
    Reject { Enter-TestResourceBudget -BudgetDirectory $budget -MaxHeavyJobs 2 -TimeoutSeconds 0 } '不同工作树容量冲突不能绕过预算'
    Reject { Enter-TestResourceBudget -BudgetDirectory 'relative/path' -TimeoutSeconds 0 } '拒绝工作树相对预算目录'
    $signal = Join-Path $root 'crashed'
    Finish-Worker (Start-Worker $a $budget 'crash' $signal) 23
    $crashedRecord = [IO.File]::ReadAllText((Join-Path $budget 'heavy-1.lock'))
    Reject { Enter-TestResourceBudget -BudgetDirectory $budget -TimeoutSeconds 0 } 'owner 崩溃后不盲夺，子进程可能仍存活'
    Assert ([IO.File]::ReadAllText((Join-Path $budget 'heavy-1.lock')) -ceq $crashedRecord) '崩溃 owner 证据原样保留'
    # 此文件是本测试新建，未知/损坏记录同样 fail closed。
    [IO.File]::WriteAllText((Join-Path $budget 'heavy-1.lock'), '{unknown owner')
    Reject { Enter-TestResourceBudget -BudgetDirectory $budget -TimeoutSeconds 0 } '未知或损坏 owner 不按 TTL 清除'
    $two = Join-Path $root 'explicit-two'
    $p = Enter-TestResourceBudget -BudgetDirectory $two -MaxHeavyJobs 2 -TimeoutSeconds 0
    try {
        Reject { Enter-TestResourceBudget -BudgetDirectory $two -MaxHeavyJobs 2 -TimeoutSeconds 0 } '拒绝同进程嵌套获取'
        $signal = Join-Path $root 'two-admitted'
        Finish-Worker (Start-Worker $b $two 'normal' $signal '2')
        $second = Get-Content $signal -Raw | ConvertFrom-Json
        Assert ($p.Slot -ne $second.Slot) '显式容量 2 的不同进程使用不同槽位'
    } finally { Exit-TestResourceBudget $p }
    "PASS: $checks resource-budget checks; synthetic only; platform=$([Environment]::OSVersion.Platform)"
} finally {
    # 仅等待本脚本直接创建的进程；不按名称/PID 杀系统 dotnet 或其他任务。
    $allExited = $true
    foreach ($child in $children) {
        if (-not $child.HasExited) { $allExited = $child.WaitForExit(15000) -and $allExited }
        $child.Dispose()
    }
    if ($allExited) { Remove-Item -LiteralPath $root -Recurse -Force }
    else { Write-Warning "仍有合成子进程未退出；保留现场 $root，不擅自清理锁。" }
}
