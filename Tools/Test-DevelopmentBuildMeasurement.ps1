# 合成仓库和假编译器；不编译服务端、不读取 PVF、不启动服务。
$ErrorActionPreference='Stop'
$homeFixture=Join-Path ([IO.Path]::GetTempPath()) ('s5a9-measure-test-'+[guid]::NewGuid().ToString('N'))
$experiment=Join-Path $homeFixture 'build/measure/case'
$fixture=Join-Path $experiment 'executor'
$checks=0
$originalEnvironment=@{}
foreach ($name in @('FAKE_MUTATE','FAKE_FAIL','FAKE_SLEEP','FAKE_BUDGET_LOG')) { $originalEnvironment[$name]=[Environment]::GetEnvironmentVariable($name) }
function Assert([bool]$Value,[string]$Message) { if (-not $Value) { throw "FAIL: $Message" }; $script:checks++ }
function Reject([scriptblock]$Action,[string]$Message) { $failed=$false; try { & $Action | Out-Null } catch { $failed=$true }; Assert $failed $Message }
function Write-File([string]$Name,[string]$Content) { $path=Join-Path $fixture $Name; New-Item -ItemType Directory -Path (Split-Path $path -Parent) -Force | Out-Null; [IO.File]::WriteAllText($path,$Content.Replace("`r`n","`n")) }
function Freeze-Source([string]$Target) {
    New-Item -ItemType Directory -Path $Target | Out-Null
    foreach($name in @(& git -C $fixture ls-files --cached --others --exclude-standard)) {
        $destination=Join-Path $Target $name
        New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force | Out-Null
        [IO.File]::Copy((Join-Path $fixture $name),$destination)
    }
    & git -C $Target init --quiet
    & git -C $Target add .
    & git -C $Target -c user.name=MeasureTests -c user.email=measure@example.invalid commit --quiet -m frozen
    if ($LASTEXITCODE -ne 0) { throw '无法冻结合成源码。' }
}
try {
    Write-File 'Tools/Measure-DevelopmentBuild.ps1' ([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'Measure-DevelopmentBuild.ps1')))
    foreach($helper in @('DevelopmentSelectedRun.ps1','DevelopmentArtifactWorkspace.ps1','Run-DevelopmentChild.py','DevelopmentMeasurementInputs.ps1')) { Write-File ('Tools/'+$helper) ([IO.File]::ReadAllText((Join-Path $PSScriptRoot $helper))) }
    Write-File '.gitignore' "build/`n"
    Write-File 'Server/DfoServer/DfoServer.csproj' '<Project />'
    Write-File 'Server/DfoServer/SelfTests/Sample.cs' '// initial test'
    Write-File 'Tools/TestResourceBudget.psm1' 'function Enter-TestResourceBudget { if($env:FAKE_BUDGET_LOG){[IO.File]::AppendAllText($env:FAKE_BUDGET_LOG,"enter`n")}; @{WaitMs=0} }; function Exit-TestResourceBudget { param($Permit) if($env:FAKE_BUDGET_LOG){[IO.File]::AppendAllText($env:FAKE_BUDGET_LOG,"exit`n")} }; Export-ModuleMember -Function *-TestResourceBudget'
    $scriptPath=Join-Path $fixture 'Tools/Measure-DevelopmentBuild.ps1'
    & git -C $fixture init --quiet
    & git -C $fixture add .
    & git -C $fixture -c user.name=MeasureTests -c user.email=measure@example.invalid commit --quiet -m fixture
    if ($LASTEXITCODE -ne 0) { throw '无法初始化合成仓库。' }
    $artifacts=Join-Path $fixture 'build/measure/run'
    foreach ($profile in 'Default','CompilerCpu1','CompilerCpu2') {
        $plan=& $scriptPath -ArtifactsPath $artifacts -Profile $profile | ConvertFrom-Json
        Assert ($plan.Mode -eq 'plan-only') '默认仅计划'
        Assert (-not (Test-Path $artifacts)) '计划不创建产物'
        Assert ($plan.Steps[2].Arguments -contains '--no-restore') '构建阶段不重复restore'
        Assert ($plan.Steps[2].Arguments -contains '-p:RunAnalyzersDuringBuild=true') '不禁用分析器'
        Assert ($plan.Steps[2].Arguments -contains '-p:UseSharedCompilation=false') '不连接共享编译器'
        if ($profile -ne 'Default') { Assert ($plan.Steps[2].Arguments -contains ('-p:CscEnvironment=DOTNET_PROCESSOR_COUNT='+$profile.Substring($profile.Length-1))) 'CPU实验仅编译器环境' }
    }
    Reject { & $scriptPath -ArtifactsPath $artifacts -Execute } '没有批准窗口拒绝执行'
    Reject { & $scriptPath -ArtifactsPath $artifacts -Execute -ApprovedWindow synthetic } '缺完整输入声明拒绝执行'
    Reject { & $scriptPath -ArtifactsPath (Join-Path $fixture 'outside') } '拒绝隔离目录之外路径'
    Reject { & $scriptPath -Scenario WarmChangedTest -ChangedTestFile '../bad.cs' } '拒绝任意源码变更'
    if ($IsLinux) {
        Write-File 'fake-dotnet' @'
#!/bin/bash
if [[ "$1" == --info ]]; then echo 'synthetic sdk'; exit 0; fi
for arg in "$@"; do
 case "$arg" in
  -p:ArtifactsPath=*) artifact="${arg#*=}" ;;
  -bl:*) log="${arg#-bl:}" ;;
 esac
done
mkdir -p "$artifact/bin/Fake" "$artifact/obj/Fake"
printf 'synthetic binlog' > "$log"
printf 'synthetic artifact' > "$artifact/bin/Fake/Fake.dll"
# 保留一个真实子进程，验证 owned-tree 采样，不运行编译器。
sleep "${FAKE_SLEEP:-0.3}"
if [[ -n "$FAKE_FAIL" ]]; then exit 7; fi
if [[ "$1" == build && -n "$FAKE_MUTATE" ]]; then echo '// mutation' >> "$FAKE_MUTATE"; fi
'@
        Assert ([IO.File]::ReadAllText((Join-Path $fixture 'fake-dotnet')).StartsWith("#!/bin/bash`n")) 'CRLF检出不污染Linux替身shebang'
        & chmod +x (Join-Path $fixture 'fake-dotnet')
        $params=@{ArtifactsPath=$artifacts;Execute=$true;ApprovedWindow='synthetic-only';InputsComplete=$true;DotnetPath=(Join-Path $fixture 'fake-dotnet')}
        $cold=& $scriptPath @params | ConvertFrom-Json
        Assert $cold.Valid '合成cold成功'
        Assert ($cold.Steps.Count -eq 3) 'restore/evaluation/build分开记录'
        Assert ($cold.Steps[0].PeakOwnedTreeRssBytes -gt 0) '采样到真实假进程树'
        Assert ($cold.EntryElapsedMs -ge ($cold.Steps | Measure-Object ElapsedMs -Sum).Sum) '入口计时包括阶段及输入扫描，不藏校验开销'
        Assert ($cold.InputSnapshotChecks.Attempts -eq 5 -and $cold.InputSnapshotChecks.Completed -eq 5 -and $cold.InputSnapshotChecks.Failed -eq 0 -and $cold.InputSnapshotChecks.FilesHashed -gt 0) '保留入口、三阶段前及收尾全部五次内容核验'
        Reject { & $scriptPath @params } 'cold不覆盖旧目录'
        $artifactFile=Join-Path $artifacts 'bin/Fake/Fake.dll'
        $stamp=(Get-Item $artifactFile).LastWriteTimeUtc
        [IO.File]::SetLastWriteTimeUtc($artifactFile,$stamp.AddSeconds(1))
        Reject { & $scriptPath @params -Scenario WarmUnchanged } '仅touch产物也拒绝warm'
        [IO.File]::SetLastWriteTimeUtc($artifactFile,$stamp)
        $sourceFile=Join-Path $fixture 'Server/DfoServer/SelfTests/Sample.cs'
        $sourceStamp=(Get-Item $sourceFile).LastWriteTimeUtc
        [IO.File]::SetLastWriteTimeUtc($sourceFile,$sourceStamp.AddSeconds(1))
        Reject { & $scriptPath @params -Scenario WarmUnchanged } '仅touch源码也拒绝warm'
        [IO.File]::SetLastWriteTimeUtc($sourceFile,$sourceStamp)
        $warm=& $scriptPath @params -Scenario WarmUnchanged | ConvertFrom-Json
        Assert $warm.Valid '相同输入warm成功'
        $bin=Join-Path $artifacts 'bin'; $moved=Join-Path $fixture 'build/moved-bin'
        Move-Item -LiteralPath $bin -Destination $moved
        try {
            New-Item -ItemType SymbolicLink -Path $bin -Target $moved | Out-Null
            Reject { & $scriptPath @params -Scenario WarmUnchanged } 'bin根链接不能绕过产物隔离'
        } finally { Remove-Item -LiteralPath $bin -Force; Move-Item -LiteralPath $moved -Destination $bin }
        $baseline=Join-Path $experiment 'baseline'; $changed=Join-Path $experiment 'changed'
        Freeze-Source $baseline
        [IO.File]::AppendAllText((Join-Path $fixture 'Server/DfoServer/SelfTests/Sample.cs'),'// controlled delta')
        Freeze-Source $changed
        Reject { & $scriptPath @params -Scenario WarmUnchanged } '未变场景拒绝源码改变'
        Reject { & $scriptPath @params -Scenario WarmChangedTest -ChangedTestFile 'Server/DfoServer/SelfTests/Sample.cs' } 'changed缺少双冻结快照，不执行'
        $changedParams=@{Scenario='WarmChangedTest';ChangedTestFile='Server/DfoServer/SelfTests/Sample.cs';BaselineSnapshotPath=$baseline;ChangedSnapshotPath=$changed}
        Reject { & $scriptPath @params @changedParams -Profile CompilerCpu1 } 'changed配置不匹配拒绝'
        $changedParams.ChangedSnapshotPath=$baseline
        Reject { & $scriptPath @params @changedParams } '冻结根别名拒绝'
        $changedParams.ChangedSnapshotPath=$changed
        $extra=Join-Path $changed 'unexpected.txt'
        [IO.File]::WriteAllText($extra,'extra')
        Reject { & $scriptPath @params @changedParams } '冻结快照额外变更拒绝'
        Remove-Item $extra
        $baselineFile=Join-Path $baseline 'Server/DfoServer/SelfTests/Sample.cs'
        $baselineBytes=[IO.File]::ReadAllBytes($baselineFile)
        [IO.File]::WriteAllText($baselineFile,'wrong baseline')
        Reject { & $scriptPath @params @changedParams } '冻结基线不匹配成功收据拒绝'
        [IO.File]::WriteAllBytes($baselineFile,$baselineBytes)
        $changedSourceStamp=(Get-Item $sourceFile).LastWriteTimeUtc
        [IO.File]::SetLastWriteTimeUtc($sourceFile,$sourceStamp)
        Reject { & $scriptPath @params @changedParams } 'changed内容变化但旧时间戳拒绝'
        [IO.File]::SetLastWriteTimeUtc($sourceFile,$changedSourceStamp)
        $changedResult=& $scriptPath @params @changedParams | ConvertFrom-Json
        Assert $changedResult.Valid '隔离双冻结changed成功'
        Assert ($changedResult.InputSnapshotChecks.Attempts -eq 15 -and $changedResult.InputSnapshotChecks.Completed -eq 15) 'changed保留三份源在所有边界的十五次内容核验'
        Assert ($changedResult.FrozenSnapshots.ChangedTestFile -eq 'Server/DfoServer/SelfTests/Sample.cs') '收据绑定变更文件'
        Assert ($changedResult.BaselineReceiptHash.Length -eq 64) '保留前一成功收据身份'
        Assert ([IO.File]::ReadAllText($baselineFile) -eq '// initial test') '工具不修改冻结基线'
        # 运行中改变冻结源会使changed收据无效，不能把别的源当成已测。
        [IO.File]::WriteAllText((Join-Path $fixture 'Server/DfoServer/SelfTests/Sample.cs'),'// initial test')
        [IO.File]::AppendAllText((Join-Path $artifacts 'bin/Fake/Fake.dll'),'corrupt')
        Reject { & $scriptPath @params -Scenario WarmUnchanged } '拒绝被改写的产物'
        $env:FAKE_MUTATE=Join-Path $fixture 'Server/DfoServer/SelfTests/Sample.cs'
        $params.ArtifactsPath=Join-Path $fixture 'build/measure/mutation'
        Reject { & $scriptPath @params } '运行中源变更必须invalid'
        $invalid=Get-Content (Join-Path $params.ArtifactsPath 'measurement.json') -Raw | ConvertFrom-Json
        Assert (-not $invalid.Valid) 'invalid记录保留'
        Remove-Item Env:FAKE_MUTATE
        $env:FAKE_FAIL='1'; $params.ArtifactsPath=Join-Path $fixture 'build/measure/failure'
        Reject { & $scriptPath @params } '失败阶段不成功'
        $invalid=Get-Content (Join-Path $params.ArtifactsPath 'measurement.json') -Raw | ConvertFrom-Json
        Assert (-not $invalid.Valid) '失败结果invalid'
        Assert ($invalid.EntryElapsedMs -gt 0 -and $invalid.InputSnapshotChecks.Completed -gt 0) '已进入执行的失败也保留扫描和入口耗时'
        Remove-Item Env:FAKE_FAIL
        $env:FAKE_SLEEP='5'; $params.ArtifactsPath=Join-Path $fixture 'build/measure/timeout'
        $unrelated=Start-Process /bin/sleep -ArgumentList '15' -PassThru
        try {
            Reject { & $scriptPath @params -TimeoutSeconds 1 } '超时停止本次进程树'
            Assert (-not $unrelated.HasExited) '不停止同名无关进程'
            $invalid=Get-Content (Join-Path $params.ArtifactsPath 'measurement.json') -Raw | ConvertFrom-Json
            Assert (-not $invalid.Valid) '超时结果invalid'
        } finally { if (-not $unrelated.HasExited) { $unrelated.Kill(); $unrelated.WaitForExit() }; $unrelated.Dispose() }
        Remove-Item Env:FAKE_SLEEP
        $env:FAKE_BUDGET_LOG=Join-Path $fixture 'build/budget-events.log'
        Write-File 'Tools/Run-DevelopmentChild.py' "raise SystemExit(1)`n"
        $params.ArtifactsPath=Join-Path $fixture 'build/measure/unknown'
        Reject { & $scriptPath @params } '监督器未提供终态时不续跑'
        Assert ([IO.File]::ReadAllText($env:FAKE_BUDGET_LOG) -eq "enter`n") '未知终态不得释放统一预算'
        $invalid=Get-Content (Join-Path $params.ArtifactsPath 'measurement.json') -Raw | ConvertFrom-Json
        Assert (-not $invalid.Valid -and $invalid.ProcessCleanup -eq 'unconfirmed; permit retained') '未知终态保留失败收据'
        Remove-Item Env:FAKE_BUDGET_LOG
    } else { Write-Host 'SKIP: Linux 假进程树回归；当前平台仅计划/参数检查。' }
    Write-Host "PASS: $checks checks (synthetic only)"
} finally {
    foreach ($name in $originalEnvironment.Keys) { [Environment]::SetEnvironmentVariable($name,$originalEnvironment[$name]) }
    if (Test-Path $homeFixture) { Remove-Item -LiteralPath $homeFixture -Recurse -Force }
}
