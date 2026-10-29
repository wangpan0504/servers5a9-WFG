# 合成仓库和假编译器；不编译服务端、不读取 PVF、不启动服务。
$ErrorActionPreference='Stop'
$fixture=Join-Path ([IO.Path]::GetTempPath()) ('s5a9-measure-test-'+[guid]::NewGuid().ToString('N'))
$checks=0
$originalEnvironment=@{}
foreach ($name in @('FAKE_MUTATE','FAKE_FAIL','FAKE_SLEEP')) { $originalEnvironment[$name]=[Environment]::GetEnvironmentVariable($name) }
function Assert([bool]$Value,[string]$Message) { if (-not $Value) { throw "FAIL: $Message" }; $script:checks++ }
function Reject([scriptblock]$Action,[string]$Message) { $failed=$false; try { & $Action | Out-Null } catch { $failed=$true }; Assert $failed $Message }
function Write-File([string]$Name,[string]$Content) { $path=Join-Path $fixture $Name; New-Item -ItemType Directory -Path (Split-Path $path -Parent) -Force | Out-Null; [IO.File]::WriteAllText($path,$Content.Replace("`r`n","`n")) }
try {
    Write-File 'Tools/Measure-DevelopmentBuild.ps1' ([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'Measure-DevelopmentBuild.ps1')))
    Write-File '.gitignore' "build/`n"
    Write-File 'Server/DfoServer/DfoServer.csproj' '<Project />'
    Write-File 'Server/DfoServer/SelfTests/Sample.cs' '// initial test'
    Write-File 'Tools/TestResourceBudget.psm1' 'function Enter-TestResourceBudget { @{WaitMs=0} }; function Exit-TestResourceBudget { param($Permit) }; Export-ModuleMember -Function *-TestResourceBudget'
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
        Reject { & $scriptPath @params } 'cold不覆盖旧目录'
        $warm=& $scriptPath @params -Scenario WarmUnchanged | ConvertFrom-Json
        Assert $warm.Valid '相同输入warm成功'
        $bin=Join-Path $artifacts 'bin'; $moved=Join-Path $fixture 'build/moved-bin'
        Move-Item -LiteralPath $bin -Destination $moved
        try {
            New-Item -ItemType SymbolicLink -Path $bin -Target $moved | Out-Null
            Reject { & $scriptPath @params -Scenario WarmUnchanged } 'bin根链接不能绕过产物隔离'
        } finally { Remove-Item -LiteralPath $bin -Force; Move-Item -LiteralPath $moved -Destination $bin }
        [IO.File]::AppendAllText((Join-Path $fixture 'Server/DfoServer/SelfTests/Sample.cs'),'// controlled delta')
        Reject { & $scriptPath @params -Scenario WarmUnchanged } '未变场景拒绝源码改变'
        Reject { & $scriptPath @params -Scenario WarmChangedTest -ChangedTestFile 'Server/DfoServer/SelfTests/Sample.cs' } 'changed场景未实现双冻结快照，不执行'
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
    } else { Write-Host 'SKIP: Linux 假进程树回归；当前平台仅计划/参数检查。' }
    Write-Host "PASS: $checks checks (synthetic only)"
} finally {
    foreach ($name in $originalEnvironment.Keys) { [Environment]::SetEnvironmentVariable($name,$originalEnvironment[$name]) }
    if (Test-Path $fixture) { Remove-Item -LiteralPath $fixture -Recurse -Force }
}
