<#
.SYNOPSIS
仅在 Windows 原生执行测试监督器验收，不启动服务、不编译、不启用正式 Run。
.DESCRIPTION
要求 Windows 10 / Server 2016+、PowerShell 7、64 位原生 Python 3.8+。
覆盖成功、非零退出、双输出流、日志上限、超时、自有后代、无关旁观进程、
连续运行的新收据以及禁止 breakaway。监督器 hard-kill 全时点和宿主嵌套 Job
兼容性仍未自动验证；本脚本通过不能自动开放正式测试入口或等同完整平台验收。
64 位 Python 检查不证明宿主硬件架构或仿真方式；ARM64 宿主/x64 Python 等组合
须分别保留原生运行证据，不能把其他组合的通过结果移用。测试输出当前报告架构与未知项。
本入口可由 CI 手动调用；不配置定时、后台服务、系统权限或安全策略。
.EXAMPLE
pwsh -NoProfile -File Tools/Test-WindowsTestSupervisor.ps1
.EXAMPLE
pwsh -NoProfile -File Tools/Test-WindowsTestSupervisor.ps1 -PythonExe python3
#>
param([string]$PythonExe = 'python')
$ErrorActionPreference = 'Stop'
try {
    if (-not $IsWindows) { throw 'NOT RUN：Windows 原生验证只能在 Windows 执行；纯合同测试不能替代。' }
    $python = (Get-Command $PythonExe -CommandType Application -ErrorAction Stop).Source
    $probe = @(& $python -c 'import sys,struct; valid=sys.platform == "win32" and struct.calcsize("P") == 8 and sys.version_info >= (3,8) and sys.getwindowsversion().major >= 10; print("s5a9-native-python-ready" if valid else "unsupported"); sys.exit(0 if valid else 2)')
    if ($LASTEXITCODE -ne 0 -or $probe -notcontains 's5a9-native-python-ready') { throw '要求 Windows 10/Server 2016+ 和 64 位原生 Python 3.8+；未运行测试。' }
    & $python (Join-Path $PSScriptRoot 'Test-DevelopmentChild.Windows.py') --require-native
    if ($LASTEXITCODE -ne 0) { throw "Windows 原生监督器验证未通过（exit=$LASTEXITCODE）。" }
    Write-Output 'Windows native synthetic validation passed. Formal Run remains Linux-only; hard-kill and host/nested-Job matrix still require native verification.'
} catch {
    Write-Error -Message $_.Exception.Message -ErrorAction Continue
    exit 2
}
