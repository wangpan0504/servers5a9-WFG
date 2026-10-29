param([string]$SupervisorPath=(Join-Path (Split-Path $PSScriptRoot -Parent) 'Run-DevelopmentChild.py'))
$ErrorActionPreference = 'Stop'
. (Join-Path (Split-Path $PSScriptRoot -Parent) 'DevelopmentResultStore.ps1')
$count = 0
function Assert($Condition, [string]$Message) { if (-not $Condition) { throw $Message }; $script:count++ }
$root = Join-Path ([IO.Path]::GetTempPath()) ('result-store-test-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root) | Out-Null
try {
    $a = Join-Path $root 'a'; $b = Join-Path $root 'b'; $store = Join-Path $root 'store'
    foreach ($tree in @($a,$b)) {
        [IO.Directory]::CreateDirectory($tree) | Out-Null
        & git -C $tree init -q
        [IO.File]::WriteAllText((Join-Path $tree 'source.cs'), 'same content')
        & git -C $tree add source.cs
    }
    $one = Get-ResultSourceManifest $a; $two = Get-ResultSourceManifest $b
    Assert ($one.Key -ceq $two.Key) '不同worktree同内容应同key。'
    [IO.File]::WriteAllText((Join-Path $b 'source.cs'), 'dirty')
    Assert ($one.Key -cne (Get-ResultSourceManifest $b).Key) 'dirty必须miss。'
    [IO.File]::WriteAllText((Join-Path $b 'source.cs'), 'same content')
    [IO.File]::WriteAllText((Join-Path $b 'new.cs'), 'untracked')
    Assert ($one.Key -cne (Get-ResultSourceManifest $b).Key) 'untracked必须计入。'
    $inputFile = Join-Path $root 'dependency'; [IO.File]::WriteAllText($inputFile,'binary')
    $descriptor = @{ SchemaVersion=1; Closure='complete-consumed-inputs-v1'; Build=@(@{Name='evaluated-build';Path=$inputFile}); Toolchain=@(@{Name='runtime';Path=$inputFile}); Dependencies=@(@{Name='native';Path=$inputFile}); Native=@(); Resources=@(); Configuration='Debug'; EnvironmentDigest=('a'*64); TimePolicy=@{Kind='fixed';Value='2026-01-01T00:00:00Z'}; Scope=@('group-a') }
    $desc = Join-Path $root 'input.json'; $descriptor | ConvertTo-Json -Depth 20 | Set-Content $desc
    $manifest = Get-ResultInputManifest $a $desc
    Remove-Item -LiteralPath (Join-Path $b 'new.cs')
    $cross = Get-ResultInputManifest $b $desc
    Assert ($manifest.Key -ceq $cross.Key) '跨工作树完整输入相同才同key。'
    [IO.File]::WriteAllText($inputFile,'changed')
    Assert ($manifest.Key -cne (Get-ResultInputManifest $a $desc).Key) '依赖内容改变必须miss。'
    [IO.File]::WriteAllText($inputFile,'binary')
    $receipt = @{SchemaVersion=2;Passed=$true;Execution='executed';Status='passed';Coverage='complete';Skips=@();Identity=@{Source=$manifest.Source;Input=$manifest.Key;Runner=(Get-ResultFileDigest $inputFile)};CacheEligible=$true;Scope='group';Group='group-a';SelectedGroups=@('group-a');RunId=('a'*32);StartedUtc='2026-01-01T00:00:00Z';CompletedUtc='2026-01-01T00:00:01Z'}
    $receipt.Passed = $false
    Assert (-not (Test-ResultReceipt $receipt $manifest (Get-ResultFileDigest $inputFile))) '矛盾Passed=false必须拒绝。'
    $receipt.Passed = $true
    $receiptPath = Join-Path $root 'receipt.json'; $receipt | ConvertTo-Json -Depth 10 | Set-Content $receiptPath
    Assert ((Get-DevelopmentResultPlan $store $manifest).State -eq 'run') '初次应miss。'
    $saved = Save-DevelopmentResult $store $manifest $manifest $receiptPath $inputFile
    Assert ($saved.State -eq 'recorded') '完整结果应保存。'
    Assert ((Get-DevelopmentResultPlan $store $cross).State -eq 'cached') '跨工作树等价输入才复用。'
    $refreshStore=Join-Path $root 'refresh'
    Save-DevelopmentResult $refreshStore $manifest $manifest $receiptPath $inputFile | Out-Null
    $script:refreshed=$false
    $refreshed=Invoke-DevelopmentResultSingleFlight $refreshStore $manifest {
        param($held)
        $script:refreshed=$true
        Save-DevelopmentResult $refreshStore $manifest $manifest $receiptPath $inputFile $held
    } -ForceExecute
    Assert ($script:refreshed -and $refreshed.State -eq 'recorded') 'ForceFresh同key已有绿也必须真正调用受锁执行器。'
    try { Invoke-DevelopmentResultSingleFlight $refreshStore $manifest { throw 'forced fresh failure' } -ForceExecute | Out-Null } catch { }
    Assert ((Get-DevelopmentResultPlan $refreshStore $manifest).State -eq 'run') '同key强制重验红必须取消旧绿，不换store不删锁。'
    $lease = Enter-ResultKeyLock $store $manifest.Key
    try {
        Assert ((Get-DevelopmentResultPlan $store $manifest).State -eq 'blocked') '活锁不能接管。'
        $wait=[Diagnostics.Stopwatch]::StartNew()
        Assert ((Get-DevelopmentResultPlan $store $manifest $null 1).State -eq 'blocked' -and $wait.Elapsed.TotalMilliseconds -ge 900) '有界等待超时仍blocked，不抢活锁。'
        $childScript = Join-Path $root 'lock-child.ps1'
        $module = Join-Path (Split-Path $PSScriptRoot -Parent) 'DevelopmentResultStore.ps1'
        [IO.File]::WriteAllText($childScript, 'param($Module,$Store,$Key); . $Module; $lease=Enter-ResultKeyLock $Store $Key; if($null -ne $lease){$lease.Dispose();exit 3};exit 0')
        $info = [Diagnostics.ProcessStartInfo]::new((Get-Process -Id $PID).Path)
        $info.UseShellExecute = $false
        foreach ($arg in @('-NoProfile','-File',$childScript,$module,$store,$manifest.Key)) { $info.ArgumentList.Add($arg) }
        $child = [Diagnostics.Process]::Start($info)
        try {
            if (-not $child.WaitForExit(15000)) { $child.Kill($true); throw '合成锁子进程未及时退出。' }
            Assert ($child.ExitCode -eq 0) '另一个PowerShell进程也不能夺活锁。'
        } finally { $child.Dispose() }
        $waiterScript=Join-Path $root 'waiter.ps1'; $ready=Join-Path $root 'waiter-ready'; $manifestFile=Join-Path $root 'manifest.json'
        [IO.File]::WriteAllText($manifestFile,(ConvertTo-ResultCanonical $manifest))
        [IO.File]::WriteAllText($waiterScript,'param($Module,$Store,$ManifestFile,$Ready);. $Module;$m=Get-Content $ManifestFile -Raw|ConvertFrom-Json -AsHashtable;[IO.File]::WriteAllText($Ready,"ready");$r=Get-DevelopmentResultPlan $Store $m $null 5;if($r.State -ne "cached"){exit 9};exit 0')
        $info=[Diagnostics.ProcessStartInfo]::new((Get-Process -Id $PID).Path);$info.UseShellExecute=$false
        foreach($arg in @('-NoProfile','-File',$waiterScript,$module,$store,$manifestFile,$ready)){$info.ArgumentList.Add($arg)}
        $waiter=[Diagnostics.Process]::Start($info)
        try {
            $watch=[Diagnostics.Stopwatch]::StartNew()
            while(-not (Test-Path $ready) -and -not $waiter.HasExited -and $watch.Elapsed.TotalSeconds -lt 10){Start-Sleep -Milliseconds 20}
            Assert ((Test-Path $ready) -and -not $waiter.WaitForExit(100)) '另一进程在活锁下等待，不伪称命中。'
            $lease.Dispose()
            if(-not $waiter.WaitForExit(10000)){$waiter.Kill($true);$waiter.WaitForExit();throw '等待复用子进程未结束。'}
            Assert ($waiter.ExitCode -eq 0) '锁释放后重新读取真实完整收据得到命中。'
        } finally { if(-not $waiter.HasExited){$waiter.Kill($true);$waiter.WaitForExit()};$waiter.Dispose() }

    } finally { $lease.Dispose() }
    $receipt.Status='failed'; $receipt.CacheEligible=$false
    $receipt | ConvertTo-Json -Depth 10 | Set-Content $receiptPath
    Save-DevelopmentResult $store $manifest $manifest $receiptPath $inputFile | Out-Null
    Assert ((Get-DevelopmentResultPlan $store $manifest).State -eq 'run') '同key失败不能捞旧绿。'
    $otherStore = Join-Path $root 'corrupt-store'; $receipt.Status='passed';$receipt.CacheEligible=$true
    $receipt | ConvertTo-Json -Depth 10 | Set-Content $receiptPath
    $saved = Save-DevelopmentResult $otherStore $manifest $manifest $receiptPath $inputFile
    [IO.File]::WriteAllText($saved.Path,'corrupt')
    Assert ((Get-DevelopmentResultPlan $otherStore $manifest).State -eq 'run') '损坏必须miss。'
    $receipt.Coverage='partial'; $receipt.Skips=@(@{Code='resource-missing';Reason='synthetic'})
    Assert (-not (Test-ResultReceipt $receipt $manifest (Get-ResultFileDigest $inputFile))) 'skip不能复用。'
    $receipt.SchemaVersion=1
    Assert (-not (Test-ResultReceipt $receipt $manifest (Get-ResultFileDigest $inputFile))) '旧receipt不能复用。'
    $changed = Invoke-DevelopmentResultSingleFlight $store $manifest { throw '不应执行' } -WaitSeconds 1 -ValidateInputs { $false }
    Assert ($changed.State -eq 'blocked' -and $changed.Reason -eq 'inputs-changed-while-waiting') '获得锁后输入变更禁止捞旧绿。'
    $flightStore = Join-Path $root 'singleflight'
    $script:entered = $false
    try { Invoke-DevelopmentResultSingleFlight $flightStore $manifest {
        param($held)
        $script:entered = $true
        Assert ((Get-DevelopmentResultPlan $flightStore $manifest).State -eq 'blocked') '整个执行期间不释放key锁。'
        Assert ((Get-DevelopmentResultPlan $flightStore $manifest $held).State -eq 'run') '拥有者可在锁内读取。'
    } | Out-Null } catch { }
    Assert $script:entered '第一次实际执行单flight回调。'
    try { Invoke-DevelopmentResultSingleFlight $flightStore $manifest { throw 'synthetic interruption' } | Out-Null } catch { }
    Assert ((Get-DevelopmentResultPlan $flightStore $manifest).State -eq 'run') '中断留下不可变失效证据。'
    $descriptor.TimePolicy.Kind = 'wall-clock'
    $descriptor | ConvertTo-Json -Depth 20 | Set-Content $desc
    $unproven = Get-ResultInputManifest $a $desc
    Assert ((Get-DevelopmentResultPlan $store $unproven).State -eq 'run') '未冻结真实时钟必须实跑。'
    $artifact = Join-Path $root 'artifacts/bin/DfoServer/debug'
    $toolchain = Join-Path $root 'toolchain'
    [IO.Directory]::CreateDirectory($artifact) | Out-Null
    [IO.Directory]::CreateDirectory($toolchain) | Out-Null
    foreach ($name in @('DfoServer.dll','DfoServer.deps.json','DfoServer.runtimeconfig.json')) {
        [IO.File]::WriteAllText((Join-Path $artifact $name), 'synthetic-build-input')
    }
    [IO.File]::WriteAllText((Join-Path $toolchain 'host'), 'synthetic-toolchain')
    $captured = Join-Path $root 'captured.json'
    & (Join-Path (Split-Path $PSScriptRoot -Parent) 'Capture-DevelopmentInputs.ps1') -Repository $a -ArtifactDirectory $artifact -ToolchainDirectory $toolchain -Group group-a -OutputPath $captured | Out-Null
    $plan = (& (Join-Path (Split-Path $PSScriptRoot -Parent) 'Test-Development.ps1') -Repository $a -Store $store -InputDescriptor $captured) | ConvertFrom-Json
    Assert ($plan.State -eq 'run' -and $plan.Commands.Count -eq 2) '真实采集入口给可执行实跑计划，不要求手填PASS。'
    Assert ($plan.Commands[0].Arguments -contains (Join-Path $root 'artifacts')) '计划保留实际产物路径。'
    $devPlan = (& (Join-Path (Split-Path $PSScriptRoot -Parent) 'Test-Development.ps1') -Repository $a -Store $store -Groups @('group-a','group-b')) | ConvertFrom-Json
    Assert ($devPlan.State -eq 'run' -and $devPlan.OtherScopes -eq 'not-run' -and $devPlan.Scope.Count -eq 2) '显式Dev计划不把其他范围算通过。'
    . (Join-Path (Split-Path $PSScriptRoot -Parent) 'DevelopmentSelectedRun.ps1')
    $childLog = Join-Path $root 'child.log'
    Invoke-DevelopmentChild (Get-Command pwsh).Source @('-NoProfile','-Command','Write-Output "synthetic child"; exit 0') $root $childLog -SupervisorPath $SupervisorPath
    Assert ((Get-Content ($childLog+'.json') -Raw | ConvertFrom-Json).ExitCode -eq 0) '实际子进程终态与独立日志落盘。'
    $oldReceipt = Get-ResultFileDigest ($childLog+'.json')
    $failed = $false
    try { Invoke-DevelopmentChild (Get-Command pwsh).Source @('-NoProfile','-Command','exit 0') $root $childLog -SupervisorPath $SupervisorPath } catch { $failed = $true }
    Assert ($failed -and (Get-ResultFileDigest ($childLog+'.json')) -ceq $oldReceipt) '旧监督收据不可被同路径重试误用。'
    $failed = $false
    try { Invoke-DevelopmentChild (Get-Command pwsh).Source @('-NoProfile','-Command','exit 7') $root (Join-Path $root 'failed.log') -SupervisorPath $SupervisorPath } catch { $failed = $true }
    Assert $failed '非零实际子进程不能成为通过。'
    $cliLog = Join-Path $root 'blocked-cli.log'
    $failed = $false
    try { Invoke-DevelopmentChild (Get-Command pwsh).Source @('-NoProfile','-File',(Join-Path (Split-Path $PSScriptRoot -Parent) 'Test-Development.ps1'),'-Store',$store,'-Repository',$a,'-Groups','invalid_group') $root $cliLog -SupervisorPath $SupervisorPath } catch { $failed = $true }
    Assert ($failed -and (Get-Content ($cliLog+'.json') -Raw | ConvertFrom-Json).ExitCode -eq 2) 'CLI blocked必须非零，不能被CI当成功。'
    Write-Output "PASS $count result-store synthetic assertions"
} finally { Remove-Item -LiteralPath $root -Recurse -Force }
