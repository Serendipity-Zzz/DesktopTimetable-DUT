param([ValidateSet('Window','Host','HostTray','Header','HeaderPhysical')][string]$Mode='Window')
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$source=Get-Content (Join-Path $PSScriptRoot 'smoke-desktop.ps1') -Raw
$match=[regex]::Match($source,"(?s)Add-Type @'\r?\n(.*?)\r?\n'@")
if(!$match.Success){throw 'Native input helper source missing'}
Add-Type -TypeDefinition $match.Groups[1].Value
$output=Join-Path $root ('artifacts/input-probe-'+$Mode+'-'+[Guid]::NewGuid().ToString('N'))
$dotnet=Join-Path $root '.tools/dotnet/dotnet.exe'
$app=Join-Path $root 'spikes/NativeInput.Spike/bin/Release/net10.0-windows10.0.19041.0/NativeInput.Spike.dll'
$arguments=@(('"'+$app+'"'),('"'+$output+'"'))
if($Mode -ne 'Window'){$arguments+='--host'}
if($Mode -eq 'HostTray'){$arguments+='--tray'}
if($Mode -in @('Header','HeaderPhysical')){$arguments+=@('--tray','--header')}
$cursor=[DesktopAudit]::Cursor();$toggled=$false
$process=Start-Process -FilePath $dotnet -ArgumentList $arguments -WindowStyle Hidden -PassThru
try {
 $readyPath=Join-Path $output 'ready.json';$deadline=[DateTime]::UtcNow.AddSeconds(10)
 while(!(Test-Path $readyPath)){
  if($process.HasExited -or [DateTime]::UtcNow -ge $deadline){throw 'Input probe did not become ready'}
  Start-Sleep -Milliseconds 30
 }
 $ready=Get-Content $readyPath -Raw|ConvertFrom-Json
 $window=[DesktopAudit]::Windows($process.Id)|Where-Object {$_.Hwnd -eq $ready.hwnd}|Select-Object -First 1
 if(!$window){throw 'Owned input target missing'}
 $x=$window.X+[int]($window.Width/2);$y=$window.Y+[int]($window.Height/2)
 if([DesktopAudit]::Hit($x,$y) -ne $window.Hwnd){
  if($Mode -eq 'Window'){throw 'Plain input probe occluded'}
  [DesktopAudit]::WinD();$toggled=$true;Start-Sleep -Milliseconds 350
 }
 $input=[System.Collections.Generic.List[object]]::new()
 $input.Add(@{at=[DateTimeOffset]::UtcNow.ToString('O');phase='before-first';hit=[DesktopAudit]::Hit($x,$y)})
 if($Mode -eq 'Header'){
  Add-Type -AssemblyName UIAutomationClient;Add-Type -AssemblyName UIAutomationTypes
  $input.Add(@{phase='before-fromhandle';foreground=[DesktopAudit]::GetForegroundWindow().ToInt64()})
  $element=[System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$window.Hwnd)
  $input.Add(@{phase='after-fromhandle';foreground=[DesktopAudit]::GetForegroundWindow().ToInt64()})
  $name=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,'收成悬浮球')
  $button=$element.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$name)
  $input.Add(@{phase='before-invoke';foreground=[DesktopAudit]::GetForegroundWindow().ToInt64()})
  $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
  $input.Add(@{phase='after-invoke';foreground=[DesktopAudit]::GetForegroundWindow().ToInt64()})
 }else{[DesktopAudit]::Click($window,$x,$y)}
 Start-Sleep -Milliseconds 35
 $input.Add(@{at=[DateTimeOffset]::UtcNow.ToString('O');phase='before-second';hit=[DesktopAudit]::Hit($x,$y)})
 [DesktopAudit]::Click($window,$x,$y)
 if(!$process.WaitForExit(10000)){throw 'Input probe failed to exit'}
 if($process.ExitCode -ne 0){throw "Input probe exited with error; inspect $output"}
 $input|ConvertTo-Json|Set-Content (Join-Path $output 'input.json')
 $result=Get-Content (Join-Path $output 'result.json') -Raw|ConvertFrom-Json
 $result|Where-Object {$_.kind -in @('FINAL','CLICK','ERROR','GESTURE','HOST','STATE','ACTIVATED')}|ConvertTo-Json -Depth 5
 Write-Output "Input probe evidence: $output"
} finally {
 if($toggled){[DesktopAudit]::WinD()}
 [DesktopAudit]::Move($cursor[0],$cursor[1])
 if(!$process.HasExited){$process.Kill($true);$process.WaitForExit()}
 $process.Dispose()
}
