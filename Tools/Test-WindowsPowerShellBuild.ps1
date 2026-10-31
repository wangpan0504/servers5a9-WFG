param([string]$ScratchDirectory)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -ne 'Desktop' -or $PSVersionTable.PSVersion.Major -ne 5 -or $PSVersionTable.PSVersion.Minor -lt 1) {
    throw 'Run with the Windows built-in powershell.exe 5.1.'
}
$target = Join-Path $PSScriptRoot 'Build-Server.ps1'
$bytes = [IO.File]::ReadAllBytes($target)
if (@($bytes | Where-Object { $_ -gt 127 }).Count) { throw 'Player build script must remain ASCII for BOM-independent PowerShell 5.1 parsing.' }
$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($target, [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw ($errors | Out-String) }
Write-Output 'PASS Windows PowerShell 5.1 parses the complete ASCII build script'
foreach ($name in @('Write-Step', 'Invoke-SelfCheck')) {
    $functionAst = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name }, $true)
    . ([scriptblock]::Create($functionAst.Extent.Text))
}
$command = Join-Path $env:SystemRoot 'System32/cmd.exe'
$ok = Invoke-SelfCheck 'exit zero' $command @('/d', '/c', 'exit 0') 10
if ($ok.ExitCode -ne 0) { throw 'Lost successful self-check exit code' }
$failed = Invoke-SelfCheck 'exit seventeen' $command @('/d', '/c', 'exit 17') 10
if ($failed.ExitCode -ne 17) { throw 'Lost failed self-check exit code' }
$timedOut = $false
try { Invoke-SelfCheck 'deadline' $command @('/d', '/c', 'ping -n 30 127.0.0.1 >nul') 1 | Out-Null }
catch { if ($_.Exception.Message -notlike 'Self-check timed out:*') { throw }; $timedOut = $true }
if (-not $timedOut) { throw 'Self-check timeout did not fail' }
Write-Output 'PASS self-check success, failure and process-tree timeout on .NET Framework'
if (-not $ScratchDirectory) { $ScratchDirectory = Join-Path ([IO.Path]::GetTempPath()) ('build-ps51-' + [guid]::NewGuid().ToString('N')) }
if (Test-Path -LiteralPath $ScratchDirectory) { throw 'Choose a new scratch directory' }
$fake = Join-Path $ScratchDirectory 'fake-tools'; $output = Join-Path $ScratchDirectory "output ' literal"
New-Item -ItemType Directory -Path $fake, (Join-Path $output 'Data'), (Join-Path $output 'Logs') -Force | Out-Null
[IO.File]::WriteAllText((Join-Path $fake 'dotnet.cmd'), "@echo off`r`nif `"%~1`"==`"--list-sdks`" (echo 10.0.100 [fake]& exit /b 0)`r`nexit /b 23`r`n", [Text.Encoding]::ASCII)
foreach ($name in @('S5A9Server.exe', 'Data/save.txt', 'Logs/preserve.txt', 'launcher.local.json')) {
    [IO.File]::WriteAllText((Join-Path $output $name), 'preserve-exactly')
}
$savedPath = $env:PATH
try {
    $env:PATH = $fake + ';' + (Join-Path $env:SystemRoot 'System32')
    if (Get-Command pwsh -ErrorAction SilentlyContinue) { throw 'pwsh must not be available' }
    $shell = Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe'
    $info = New-Object Diagnostics.ProcessStartInfo
    $info.FileName = $shell
    $info.Arguments = '-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + $target + '" -SingleFile -OutputDirectory "' + $output + '"'
    $info.UseShellExecute = $false; $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true; $info.RedirectStandardError = $true
    $child = [Diagnostics.Process]::Start($info)
    $stdout = $child.StandardOutput.ReadToEndAsync(); $stderr = $child.StandardError.ReadToEndAsync()
    if (-not $child.WaitForExit(30000)) { $child.Kill(); throw 'Expected compiler failure timed out' }
    $child.WaitForExit()
    [IO.File]::WriteAllText((Join-Path $ScratchDirectory 'failed-build.log'), $stdout.Result)
    [IO.File]::WriteAllText((Join-Path $ScratchDirectory 'failed-build.err'), $stderr.Result)
    if ($child.ExitCode -eq 0 -or $stderr.Result -notlike '*dotnet failed, exit code: 23*') { throw 'Native dotnet failure was incorrectly reported' }
    foreach ($name in @('S5A9Server.exe', 'Data/save.txt', 'Logs/preserve.txt', 'launcher.local.json')) {
        if ([IO.File]::ReadAllText((Join-Path $output $name)) -ne 'preserve-exactly') { throw "Failed build modified $name" }
    }
} finally { $env:PATH = $savedPath }
Write-Output 'PASS native compiler failure preserves executable, Data, Logs and settings without pwsh'
