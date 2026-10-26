param([ValidateRange(10,60)][int]$Seconds=15)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @'
using System;using System.Runtime.InteropServices;using System.Text;
public static class IdleWindows {
 delegate bool Callback(IntPtr hwnd,IntPtr data);
 [DllImport("user32.dll")] static extern bool EnumWindows(Callback callback,IntPtr data);
 [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd,out uint pid);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd,StringBuilder text,int max);
 public static IntPtr Find(int pid,string title){IntPtr result=IntPtr.Zero;EnumWindows((h,d)=>{uint p;GetWindowThreadProcessId(h,out p);var name=new StringBuilder(256);GetWindowText(h,name,256);if(p==pid&&name.ToString()==title)result=h;return true;},IntPtr.Zero);return result;}
 [DllImport("user32.dll")] public static extern uint GetGuiResources(IntPtr process,uint flag);
}
'@
$taskData=Join-Path $root ('artifacts/material-idle-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $taskData|Out-Null
$dotnet=Join-Path $root '.tools/dotnet/dotnet.exe'
$app=Join-Path $root 'src/DlutTimetable.Desktop/bin/Release/net10.0-windows10.0.19041.0/DlutTimetable.Desktop.dll'
$process=Start-Process -FilePath $dotnet -ArgumentList @('"'+$app+'"','--desktop-demo','--data-directory','"'+$taskData+'"') -WindowStyle Hidden -PassThru
$samples=[System.Collections.Generic.List[object]]::new()
function Sample([string]$phase){
 $process.Refresh()
 $samples.Add(@{phase=$phase;atUtc=[DateTimeOffset]::UtcNow.ToString('O');privateBytes=$process.PrivateMemorySize64;workingSet=$process.WorkingSet64;cpuMilliseconds=$process.TotalProcessorTime.TotalMilliseconds;handles=$process.HandleCount;gdi=[IdleWindows]::GetGuiResources($process.Handle,0);user=[IdleWindows]::GetGuiResources($process.Handle,1)})
}
function Owned-Element([string]$title){
 $handle=[IdleWindows]::Find($process.Id,$title)
 if($handle -eq [IntPtr]::Zero){throw "Missing owned HWND: $title"}
 return [System.Windows.Automation.AutomationElement]::FromHandle($handle)
}
function Named($element,[string]$name){
 $match=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,$name)
 return $element.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$match)
}
try {
 $log=Join-Path $taskData 'desktop-validation.log'
 $deadline=[DateTime]::UtcNow.AddSeconds(25)
 do{
  if($process.HasExited){throw 'Application exited before material ready'}
  $ready=Test-Path $log
  if($ready){
   $lines=@(Get-Content $log)
   $ready=@($lines|Where-Object {$_ -match 'MATERIAL_FRAME LiveBackdrop '}).Count -gt 0 -and @($lines|Where-Object {$_ -match 'VIEW_RENDER'}).Count -ge 3
  }
  if(!$ready){Start-Sleep -Milliseconds 100}
 }while(!$ready -and [DateTime]::UtcNow -lt $deadline)
 if(!$ready){throw 'Material did not become ready'}
 Sample 'ready'
 # No UIA queries, input or app state changes during this short observation interval.
 Start-Sleep -Seconds $Seconds
 Sample 'idle-end'
 $validationLog=@(Get-Content $log)
 $gear=Owned-Element '大工课表 · 按钮0'
 (Named $gear '设置').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
 Start-Sleep -Milliseconds 200
 $settings=Owned-Element '大工课表 · 设置'
 (Named $settings '通用').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
 (Named $settings '退出程序').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
 if(!$process.WaitForExit(10000) -or $process.ExitCode -ne 0){throw 'Normal exit failed'}
 $report=@{capturedAtUtc=[DateTimeOffset]::UtcNow.ToString('O');dataDirectory=$taskData;seconds=$Seconds;logicalProcessors=[Environment]::ProcessorCount;samples=$samples;validationLog=$validationLog;exitCode=$process.ExitCode;note='Short demo observation after its three bounded render diagnostics and image export. No five-minute warmup, forced GC or working-set trimming. Not ordinary-process release budget acceptance.'}
 $report|ConvertTo-Json -Depth 6|Set-Content (Join-Path $root 'docs/validation/material-idle.json') -Encoding utf8
 $samples|ForEach-Object {[pscustomobject]$_}|Format-Table phase,privateBytes,workingSet,cpuMilliseconds,handles,gdi,user
 $validationLog|Where-Object {$_ -match 'VIEW_RENDER|MATERIAL_FRAME'}
}finally{
 if(!$process.HasExited){$process.Kill($true);$process.WaitForExit()}
 $process.Dispose()
}
