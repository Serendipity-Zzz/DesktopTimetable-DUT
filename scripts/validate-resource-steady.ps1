param([ValidateRange(0,3600)][int]$WarmupSeconds=300,[ValidateRange(1,86400)][int]$SampleSeconds=600,[ValidateRange(1,60)][int]$IntervalSeconds=5,
 [ValidateSet('ClearFrosted','LiquidEdge','Solid')][string]$Material='ClearFrosted',[string]$ReleaseDirectory,
 [ValidateRange(0,30)][int]$SettingsCycles=0,[ValidateSet('UiAutomation','NativeMessages')][string]$SettingsDriver='UiAutomation')
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
if($ReleaseDirectory){
 $verified=& (Join-Path $PSScriptRoot 'verify-release.ps1') -Directory $ReleaseDirectory
 $release=[pscustomobject]@{directory=[IO.Path]::GetFullPath($ReleaseDirectory);version=$verified.version}
}else{$release=Get-Content (Join-Path $root 'docs/validation/current-release.json') -Raw|ConvertFrom-Json}
$fixture=Join-Path $root 'artifacts/resource-fixture-probe/ResourceFixture.Spike.dll'
if(!(Test-Path -LiteralPath $fixture)){throw 'Build spikes/ResourceFixture.Spike into artifacts/resource-fixture-probe first'}
$data=Join-Path $root ('artifacts/resource-steady-'+[Guid]::NewGuid().ToString('N'))
& (Join-Path $root '.tools/dotnet/dotnet.exe') $fixture $data
if($LASTEXITCODE -ne 0){throw 'Synthetic resource fixture preparation failed'}
if($Material -ne 'ClearFrosted'){
 # Set only the isolated synthetic fixture before startup, through the same
 # persisted configuration read by an ordinary product launch.
 $settingsPath=Join-Path $data 'settings.json'
 $settings=Get-Content -LiteralPath $settingsPath -Raw|ConvertFrom-Json
 $settings.material=$Material
 $settings|ConvertTo-Json -Depth 12|Set-Content -LiteralPath $settingsPath -Encoding utf8NoBOM
 $fixturePath=Join-Path $data 'fixture-metadata.json'
 $fixtureMetadata=Get-Content -LiteralPath $fixturePath -Raw|ConvertFrom-Json
 $fixtureMetadata.Material=$Material
 $fixtureMetadata|ConvertTo-Json -Depth 5|Set-Content -LiteralPath $fixturePath -Encoding utf8NoBOM
}
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type ([regex]::Match((Get-Content (Join-Path $PSScriptRoot 'smoke-desktop.ps1') -Raw),"(?s)Add-Type @'\r?\n(.*?)\r?\n'@").Groups[1].Value)
$start=[Diagnostics.ProcessStartInfo]::new((Join-Path $release.directory 'DlutTimetable.Desktop.exe'))
$start.UseShellExecute=$false;$start.CreateNoWindow=$true
$start.ArgumentList.Add('--data-directory');$start.ArgumentList.Add($data)
$before=(Get-FileHash -LiteralPath (Join-Path $data 'timetable.db')).Hash
$runKey='HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
function StartupValue(){(Get-ItemProperty -LiteralPath $runKey -Name DLUTDesktopTimetable -ErrorAction SilentlyContinue).DLUTDesktopTimetable}
$startupBefore=StartupValue
Add-Type @'
using System;using System.ComponentModel;using System.Runtime.InteropServices;using System.Text;
public static class ResourceOwnedInput {
 [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h,out uint pid);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr h,StringBuilder text,int max);
 [DllImport("user32.dll",SetLastError=true)] static extern IntPtr SendMessageTimeoutW(IntPtr h,uint message,UIntPtr w,IntPtr l,uint flags,uint timeout,out UIntPtr result);
 static void Send(IntPtr h,int expectedPid,string title,uint message,UIntPtr w,IntPtr l){
  GetWindowThreadProcessId(h,out var actual);var text=new StringBuilder(128);GetWindowText(h,text,text.Capacity);
  if(actual!=(uint)expectedPid || text.ToString()!=title)throw new InvalidOperationException("Owned benchmark target changed");
  if(SendMessageTimeoutW(h,message,w,l,3,3000,out var result)==IntPtr.Zero)throw new Win32Exception(Marshal.GetLastWin32Error());
 }
 public static void OpenSettings(IntPtr h,int pid){
  var point=new IntPtr((10<<16)|10);Send(h,pid,"大工课表 · 按钮0",0x201,new UIntPtr(1),point);Send(h,pid,"大工课表 · 按钮0",0x202,UIntPtr.Zero,point);
 }
 public static void CloseSettings(IntPtr h,int pid)=>Send(h,pid,"大工课表 · 设置",0x10,UIntPtr.Zero,IntPtr.Zero);
}
'@
$process=[Diagnostics.Process]::Start($start);$null=$process.Handle
$normalExit=$false;$operationError=$null;$samplerExitCode=$null;$samplingStarted=$false;$retainForObservation=$false
$completedSettingsCycles=0
$gpuEdgeBeforeSampling=$null;$gpuEdgeAfterSampling=$null;$gpuEnhancedAfterSampling=$null
[ordered]@{pid=$process.Id;startedUtc=$process.StartTime.ToUniversalTime().ToString('O');dataDirectory=$data;version=$release.version;material=$Material;applicationPath=$start.FileName}|
 ConvertTo-Json|Set-Content (Join-Path $data 'resource-owner.json') -Encoding utf8NoBOM
function Owned([string]$role){[DesktopAudit]::Windows($process.Id,$true)|Where-Object {$_.Visible -and $_.Title -eq ('大工课表 · '+$role)}|Select-Object -First 1}
function Named($window,[string]$name){
 if(!$window){return $null}
 $ui=[System.Windows.Automation.AutomationElement]::FromHandle([intptr]$window.Hwnd)
 $ui.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,$name))
}
function Wait-For([scriptblock]$condition){
 $deadline=[DateTime]::UtcNow.AddSeconds(20)
 while([DateTime]::UtcNow -lt $deadline){if($process.HasExited){throw 'Owned benchmark application exited early'};$v=&$condition;if($v){return $v};Start-Sleep -Milliseconds 100}
 throw 'Owned benchmark window did not become ready'
}
try{
 $null=Wait-For {Owned '周课表'}
 for($index=0;$index -lt $SettingsCycles;$index++){
  if($SettingsDriver -eq 'NativeMessages'){
   [ResourceOwnedInput]::OpenSettings([intptr](Owned '按钮0').Hwnd,$process.Id)
   $settings=Wait-For {Owned '设置'}
   # Allow layout/presentation without creating an automation peer in the app.
   Start-Sleep -Milliseconds 100
   [ResourceOwnedInput]::CloseSettings([intptr]$settings.Hwnd,$process.Id)
  }else{
   (Named (Owned '按钮0') '设置').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
   $settings=Wait-For {Owned '设置'}
   $settingsUi=[System.Windows.Automation.AutomationElement]::FromHandle([intptr]$settings.Hwnd)
   $settingsUi.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
  }
  $null=Wait-For {!(Owned '设置')};$completedSettingsCycles++
 }

 if($Material -eq 'LiquidEdge'){
  # This HWND is bound only after the first GPU draw, not merely after
  # selecting the effect. No settings UI is opened before steady sampling.
  $null=Wait-For {Owned 'GPU 边缘'};$gpuEdgeBeforeSampling=$true
 }
 # This is the ordinary published process, with a real validated SQLite cache;
 # no --desktop-demo, forced rendering, UIA queries or interaction during sampling.
 $samplingStarted=$true
 & (Join-Path $PSScriptRoot 'measure-resource-budget.ps1') -RootProcessId $process.Id -ExpectedProcessName DlutTimetable.Desktop -WarmupSeconds $WarmupSeconds -SampleSeconds $SampleSeconds -IntervalSeconds $IntervalSeconds -OutputDirectory (Join-Path $data 'measurement')
 $samplerExitCode=$LASTEXITCODE
}catch{$operationError=$_.Exception.GetType().Name;if(!$samplingStarted){$retainForObservation=$true}}
finally{
 try{
  if(!$process.HasExited -and !$retainForObservation){
   $gear=Named (Owned '按钮0') '设置'
   if(!$gear){throw 'Owned settings command missing'}
   $gear.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
   $settings=Wait-For {Owned '设置'}
   if($Material -eq 'LiquidEdge'){
    $gpuEdgeAfterSampling=[bool](Owned 'GPU 边缘')
    (Named $settings '材质与配色').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    $gpuEnhancedAfterSampling=[bool](Named $settings '实时毛玻璃 · 柔光与色散')
   }
   (Named $settings '通用').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
   (Named $settings '退出程序').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
   $normalExit=$process.WaitForExit(10000) -and $process.ExitCode -eq 0
  }
 }catch{if(!$operationError){$operationError='NormalExitFailed'}}
 # Never kill an application because observation or UIA timed out.
 $summaryFile=Join-Path $data 'measurement/summary.json'
 $summary=if(Test-Path -LiteralPath $summaryFile){Get-Content -LiteralPath $summaryFile -Raw|ConvertFrom-Json}else{$null}
 $report=@{capturedAtUtc=[DateTimeOffset]::UtcNow.ToString('O');version=$release.version;rootPid=$process.Id;dataDirectory=$data;
  datasetSynthetic=$true;visibleWeeklyOccurrences=40;material=$Material;demoMode=$false;settingsCyclesBeforeWarmup=$completedSettingsCycles;settingsDriver=$SettingsDriver;warmupSeconds=$WarmupSeconds;sampleSeconds=$SampleSeconds;
  gpuEdgeBeforeSampling=$gpuEdgeBeforeSampling;gpuEdgeAfterSampling=$gpuEdgeAfterSampling;gpuEnhancedAfterSampling=$gpuEnhancedAfterSampling;
  retainedProcessForObservation=(!$process.HasExited -and !$normalExit);
  measurementDirectory=(Join-Path $data 'measurement');normalExit=$normalExit;operationError=$operationError;samplerExitCode=$samplerExitCode;
  databaseUnchanged=((Get-FileHash -LiteralPath (Join-Path $data 'timetable.db')).Hash -eq $before);
  startupRegistrationUnchanged=((StartupValue) -ceq $startupBefore);summary=$summary;
  canonicalEnvironment=$false;note='Actual display/DPI/GPU recorded in measurement metadata. Does not establish the prescribed 1080p/100%, 4K/200%, dual-monitor or 24-hour acceptance. GPU counters are reported raw; DWM attribution not determined.'}
 $report|ConvertTo-Json -Depth 9|Set-Content (Join-Path $root 'docs/validation/resource-steady.json') -Encoding utf8NoBOM
 $process.Dispose()
}
if($operationError -or !$normalExit -or !$report.databaseUnchanged -or !$report.startupRegistrationUnchanged -or !$summary.completedRequestedDuration -or
 ($Material -eq 'LiquidEdge' -and (!$gpuEdgeBeforeSampling -or !$gpuEdgeAfterSampling -or !$gpuEnhancedAfterSampling))){throw 'Resource measurement did not complete cleanly; inspect resource-steady.json and retained resource-owner.json before further action'}
Write-Output ('Resource steady observation completed; ordinary release '+$release.version+'; synthetic 40-course cache preserved; normal exit.')
