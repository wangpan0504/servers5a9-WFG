param(
 [ValidateSet('Start','Stop','Restart')][string]$Action='Start',
 [string]$ServerIp='127.0.0.1', [string]$ListenIp,
 [int]$ChannelPort=23100, [int]$GamePort=23101, [int]$GmPort=0,
 [switch]$PacketCapture, [string]$CaptureDirectory
)
$ErrorActionPreference='Stop'
if($CaptureDirectory -and -not $PacketCapture){throw 'CaptureDirectory requires PacketCapture'}
$repo=Split-Path $PSScriptRoot -Parent
$app=Join-Path $repo 'Server\DfoServer\bin\Debug\net10.0'
$exe=Join-Path $app 'DfoServer.exe'
$logs=Join-Path $app 'Logs'
$receipt=Join-Path $app 'Data\server-instance.json'

function Stop-OwnedServer {
 if(-not(Test-Path -LiteralPath $receipt)){return}
 $saved=Get-Content -LiteralPath $receipt -Raw -Encoding UTF8|ConvertFrom-Json
 $p=Get-CimInstance Win32_Process -Filter ('ProcessId='+[int]$saved.Pid)
 if(-not $p){return}
 if($saved.Executable -ine $exe -or $saved.LogDirectory -ine $logs -or $p.ExecutablePath -ine $exe -or
    $p.CreationDate.ToUniversalTime().Ticks -ne ([datetime]$saved.Created).ToUniversalTime().Ticks){throw 'Stored server process identity changed'}
 [IO.File]::WriteAllText((Join-Path $logs 'stop.request'),'stop')
 $until=(Get-Date).AddSeconds(20)
 do {Start-Sleep -Milliseconds 100;$p=Get-CimInstance Win32_Process -Filter ('ProcessId='+[int]$saved.Pid)} while($p -and (Get-Date) -lt $until)
 if($p){throw 'Owned server did not exit; no force kill performed'}
}

if($Action -in @('Stop','Restart')){Stop-OwnedServer;if($Action -eq 'Stop'){return}}
if(Test-Path -LiteralPath $receipt){
 $saved=Get-Content -LiteralPath $receipt -Raw -Encoding UTF8|ConvertFrom-Json
 if(Get-Process -Id $saved.Pid -ErrorAction SilentlyContinue){throw 'Previous server PID still exists; inspect its identity first'}
}
$lock=Join-Path $app 'Data\server.lock'
if(Test-Path -LiteralPath $lock){
 # 同目录可能有前台启动的实例；确认库未占用后才处理旧就绪/停止标记。
 $guard=[IO.File]::Open($lock,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
 $guard.Dispose()
}
& dotnet build (Join-Path $repo 'Server\DfoServer\DfoServer.csproj') -c Debug
if($LASTEXITCODE){throw 'Build failed'}
New-Item -ItemType Directory -Path $logs,(Split-Path $receipt -Parent) -Force|Out-Null
foreach($name in @('stop.request','ready.json')){
 $marker=Join-Path $logs $name
 if(Test-Path -LiteralPath $marker){Remove-Item -LiteralPath $marker}
}
$run=Join-Path $logs ((Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+[guid]::NewGuid().ToString('N').Substring(0,8))
New-Item -ItemType Directory -Path $run|Out-Null
$arguments=@('--server-ip',$ServerIp,'--channel-port',"$ChannelPort",'--game-port',"$GamePort",'--gm-port',"$GmPort")
if($ListenIp){$arguments+=@('--listen-ip',$ListenIp)}
if($PacketCapture){$arguments+='--packet-capture';if($CaptureDirectory){$arguments+=[IO.Path]::GetFullPath($CaptureDirectory)}}
# Start-Process 在 Windows 拼接命令行；保留带空格路径和末尾反斜杠。
$quoted=$arguments|ForEach-Object {if($_.Contains('"')){throw 'Arguments cannot contain double quotes'};'"'+($_ -replace '(\\+)$','$1$1')+'"'}
$p=Start-Process -FilePath $exe -ArgumentList $quoted -WorkingDirectory $app -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $run 'stdout.txt') -RedirectStandardError (Join-Path $run 'stderr.txt')
$identity=Get-CimInstance Win32_Process -Filter ('ProcessId='+$p.Id)
if(-not $identity){throw ('Server exited during startup; inspect '+$run)}
$value=[ordered]@{Pid=$p.Id;Executable=$exe;Created=$identity.CreationDate.ToString('o');LogDirectory=$logs;Arguments=$arguments}
[IO.File]::WriteAllText($receipt,($value|ConvertTo-Json -Depth 5),[Text.UTF8Encoding]::new($false))
$until=(Get-Date).AddSeconds(45)
$ready=Join-Path $logs 'ready.json'
try {
 while(-not(Test-Path -LiteralPath $ready)){
  $p.Refresh()
  if($p.HasExited -or (Get-Date) -gt $until){throw ('Server failed to become ready; inspect '+$run)}
  Start-Sleep -Milliseconds 100
 }
 $state=$null
 while(-not $state -and (Get-Date) -lt $until){
  try {$state=Get-Content -LiteralPath $ready -Raw -Encoding UTF8|ConvertFrom-Json} catch {Start-Sleep -Milliseconds 50}
 }
 if(-not $state -or $state.Pid -ne $p.Id){throw 'Ready receipt does not belong to this process'}
} catch {
 Stop-OwnedServer
 throw
}
$value|ConvertTo-Json -Depth 5
