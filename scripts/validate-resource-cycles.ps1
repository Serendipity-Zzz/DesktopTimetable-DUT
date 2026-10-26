param([string]$ReleaseDirectory,[string]$ExistingReport,[switch]$SettingsOnly,[switch]$KeepRunning,[string]$ReportFile,[ValidateRange(10,120)][int]$SettleSeconds=60)
# One isolated PRD interaction batch, not a replacement for 24-hour/GPU budgets.
$ErrorActionPreference='Stop'
if($KeepRunning -and !$SettingsOnly){throw 'KeepRunning is limited to isolated settings diagnostics'}
$root=Split-Path $PSScriptRoot -Parent
if(!$ReleaseDirectory){$ReleaseDirectory=(Get-Content (Join-Path $root 'docs/validation/current-release.json') -Raw|ConvertFrom-Json).directory}
$verified=& (Join-Path $PSScriptRoot 'verify-release.ps1') -Directory $ReleaseDirectory
if(!$ReportFile){$ReportFile=Join-Path $root 'docs/validation/resource-cycles.json'}
$previous=if($ExistingReport){Get-Content -LiteralPath $ExistingReport -Raw|ConvertFrom-Json}else{$null}
if($previous){
 if(!$previous.databaseUnchanged -or !$previous.startupUnchanged -or $previous.version -ne $verified.version -or [bool]$previous.settingsOnly -ne [bool]$SettingsOnly){throw 'Cannot resume mismatched or changed cycle fixture'}
 $data=$previous.owner.dataDirectory
 Copy-Item -LiteralPath $ExistingReport -Destination (Join-Path $data ('cycle-report-before-resume-'+[guid]::NewGuid().ToString('N')+'.json'))
}else{
 $data=Join-Path $root ('artifacts/resource-cycles-'+[guid]::NewGuid().ToString('N'))
 & (Join-Path $root '.tools/dotnet/dotnet.exe') (Join-Path $root 'artifacts/resource-fixture-probe/ResourceFixture.Spike.dll') $data
 if($LASTEXITCODE -ne 0){throw 'Fixture creation failed'}
}
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type ([regex]::Match((Get-Content (Join-Path $PSScriptRoot 'smoke-desktop.ps1') -Raw),"(?s)Add-Type @'\r?\n(.*?)\r?\n'@").Groups[1].Value)
$startupKey='HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
function Startup(){(Get-ItemProperty -LiteralPath $startupKey -Name DLUTDesktopTimetable -ErrorAction SilentlyContinue).DLUTDesktopTimetable}
$startupBefore=Startup
$databaseBefore=(Get-FileHash -LiteralPath (Join-Path $data 'timetable.db')).Hash
$start=[Diagnostics.ProcessStartInfo]::new((Join-Path $ReleaseDirectory 'DlutTimetable.Desktop.exe'))
$start.UseShellExecute=$false;$start.CreateNoWindow=$true
$start.ArgumentList.Add('--data-directory');$start.ArgumentList.Add($data)
if($previous){
 $process=Get-Process -Id $previous.owner.pid
 if($process.StartTime.ToUniversalTime().Ticks -ne ([datetimeoffset]$previous.owner.startedUtc).UtcTicks -or $process.MainModule.FileName -ne $start.FileName){throw 'Original cycle process identity mismatch'}
}else{$process=[Diagnostics.Process]::Start($start)}
$null=$process.Handle
$owner=@{pid=$process.Id;startedUtc=$process.StartTime.ToUniversalTime().ToString('O');application=$start.FileName;dataDirectory=$data;version=$verified.version}
$owner|ConvertTo-Json|Set-Content (Join-Path $data 'resource-owner.json') -Encoding utf8NoBOM
$samples=[Collections.Generic.List[object]]::new();$workers=[Collections.Generic.List[object]]::new()
$counts=@{settings=0;weeks=0;workers=0};$normalExit=$false;$stage='startup';$failure=$null
if($previous){
 foreach($item in $previous.samples){$samples.Add($item)}
 foreach($item in $previous.workers){$workers.Add($item)}
 foreach($key in @('settings','weeks','workers')){$counts[$key]=[int]$previous.completed.$key}
}
$cursor=[DesktopAudit]::Cursor()
function Own([string]$role,[int]$OwnerId=$process.Id){[DesktopAudit]::Windows($OwnerId,$true)|Where-Object {$_.Visible -and $_.Title -eq ('大工课表 · '+$role)}|Select-Object -First 1}
function Ui($window){[System.Windows.Automation.AutomationElement]::FromHandle([intptr]$window.Hwnd)}
function Find($element,[string]$value,[bool]$ById=$false){
 $property=if($ById){[System.Windows.Automation.AutomationElement]::AutomationIdProperty}else{[System.Windows.Automation.AutomationElement]::NameProperty}
 $element.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new($property,$value))
}
function Wait([scriptblock]$Condition,[int]$Seconds=20){
 $until=[datetime]::UtcNow.AddSeconds($Seconds)
 while([datetime]::UtcNow -lt $until){if($process.HasExited){throw 'Owned process exited'};$result=&$Condition;if($result){return $result};Start-Sleep -Milliseconds 75}
 throw ('Observation timed out at '+$stage+'; retain the same process for inspection')
}
function Invoke($element){if(!$element){throw 'Owned control missing'};$element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()}
function Snapshot([string]$Phase){
 $process.Refresh()
 $samples.Add(@{phase=$Phase;utc=[datetimeoffset]::UtcNow.ToString('O');privateBytes=$process.PrivateMemorySize64;workingSetBytes=$process.WorkingSet64;
  cpuMilliseconds=$process.TotalProcessorTime.TotalMilliseconds;logicalProcessors=[Environment]::ProcessorCount;handles=$process.HandleCount;threads=$process.Threads.Count;gdi=[DesktopAudit]::GetGuiResources($process.Handle,0);user=[DesktopAudit]::GetGuiResources($process.Handle,1)})
}
function OpenSettings(){
 Invoke (Find (Ui (Own '按钮0')) '设置')
 $window=Wait {Own '设置'}
 $counts.settings++
 return $window
}
function CloseSettings($window){(Ui $window).GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close();$null=Wait {!(Own '设置')}}
function WorkerCycle([bool]$AdoptExisting=$false){
 $window=if($AdoptExisting){Own '设置'}else{OpenSettings};$ui=Ui $window
 (Find $ui '数据与账号').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
 if(!$AdoptExisting){
  $refresh=Wait {$button=Find $ui 'SchoolManualRefresh' $true;if($button -and $button.Current.IsEnabled){$button}}
  Invoke $refresh
 }
 $worker=Wait {Get-CimInstance Win32_Process -Filter "ParentProcessId=$($process.Id) AND Name='DlutTimetable.AuthWorker.exe'"|Select-Object -First 1}
 $owned=Get-Process -Id $worker.ProcessId;$started=$owned.StartTime.ToUniversalTime();$null=$owned.Handle
 try{
  $login=Wait {Own '学校登录' $worker.ProcessId}
  $loginUi=Ui $login
  $null=Wait {$fresh=Ui (Own '学校登录' $worker.ProcessId);$state=Find $fresh 'AuthBrowserState' $true;$state -and $state.Current.HelpText -eq 'browser-ready'} 120
  $readySeconds=([datetime]::UtcNow-$started).TotalSeconds
  Snapshot ('worker-ready-'+($counts.workers+1))
  Invoke (Find (Ui (Own '学校登录' $worker.ProcessId)) 'AuthCancel' $true)
  if(!$owned.WaitForExit(10000) -or $owned.ExitCode -ne 0){throw 'Owned worker did not exit cleanly'}
  $null=Wait {(Find $ui 'SchoolManualRefresh' $true).Current.IsEnabled}
  $workers.Add(@{pid=$owned.Id;startedUtc=$started.ToString('O');exitCode=$owned.ExitCode;readyObservedAfterStartSeconds=$readySeconds;cancelledWithoutCredentials=$true})
  $counts.workers++
 }finally{$owned.Dispose()}
 CloseSettings $window
 Snapshot ('worker-closed-'+$counts.workers)
}
function Weeks([int]$Number){
 # Reach S3 through the real topmost edge hover. No global Win+D or foreign window changes.
 Invoke (Find (Ui (Own '按钮1')) '侧边隐藏')
 $strip=Wait {$w=Own '侧边条';if($w -and !(Own '周课表')){$w}}
 [DesktopAudit]::Move($strip.X+[int]($strip.Width/2),$strip.Y+[int]($strip.Height/2))
 $panel=Wait {$w=Own '周课表';if($w -and ($w.ExStyle -band 8) -ne 0 -and [DesktopAudit]::Hit($w.X+[int]($w.Width*180/780),$w.Y+[int]($w.Height*18/468)) -eq $w.Hwnd){$w}}
 [DesktopAudit]::Move($panel.X+[int]($panel.Width*180/780),$panel.Y+[int]($panel.Height*18/468))
 for($i=0;$i -lt $Number;$i++){
  $panel=Own '周课表';if(!$panel){throw 'Expanded timetable closed during week cycle'}
  $x=if($i%2 -eq 0){180}else{14};$week=if($i%2 -eq 0){2}else{1}
  [DesktopAudit]::Click($panel,$panel.X+[int]($panel.Width*$x/780),$panel.Y+[int]($panel.Height*18/468))
  $null=Wait {Find (Ui (Own '周课表')) ('第 '+$week+' 周课表，虚构示例')}
  $counts.weeks++;Snapshot ('week-'+$counts.weeks)
 }
 # Existing Pin command restores locked S1 and drops S3 topmost.
 $pin=Find (Ui (Own '按钮3')) '固定课表 / 允许拖动'
 $pin.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
 $null=Wait {$w=Own '周课表';$w -and ($w.ExStyle -band 0x20) -ne 0 -and ($w.ExStyle -band 8) -eq 0}
}
function Settle([string]$Phase){
 $until=[datetime]::UtcNow.AddSeconds($SettleSeconds)
 while([datetime]::UtcNow -lt $until){Snapshot $Phase;Start-Sleep -Seconds 2}
}
try{
 if($SettingsOnly){
  $null=Wait {Own '按钮3'}
  if(!@($samples|Where-Object phase -eq 'baseline').Count){
   $stage='settings warmup';$window=OpenSettings;CloseSettings $window;$counts.settings--
   $stage='baseline settle';Settle 'baseline'
  }
  $stage='settings cycles'
  while($counts.settings -lt 30){$window=OpenSettings;CloseSettings $window;Snapshot ('settings-'+$counts.settings)}
 }else{
 $pendingWorker=Get-CimInstance Win32_Process -Filter "ParentProcessId=$($process.Id) AND Name='DlutTimetable.AuthWorker.exe'"|Select-Object -First 1
 if($previous -and $pendingWorker){$stage='resume existing worker';WorkerCycle $true}
 if($previous -and $counts.weeks -eq 0){
  # A normal second-instance restore uses the existing private IPC; no process restart.
  $client=[Diagnostics.Process]::Start($start)
  try{if(!$client.WaitForExit(10000) -or $client.ExitCode -ne 0){throw 'Existing session restore failed'}}finally{$client.Dispose()}
 }
 $null=Wait {Own '按钮3'}
 if($counts.weeks -eq 0){
 $stage='configure isolated edge level'
 $window=OpenSettings;$counts.settings--; $ui=Ui $window
 (Find $ui '布局与四态').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
 $combos=$ui.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::ComboBox))
 if($combos.Count -ne 3){throw 'Unexpected layout controls'};$combo=$combos[2]
 $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
 $item=Wait {Find $combo '置顶层'}
 $selection=$null
 for($depth=0;$depth -lt 5 -and $item;$depth++){
  if($item.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern,[ref]$selection)){break}
  $item=[System.Windows.Automation.TreeWalker]::RawViewWalker.GetParent($item)
 }
 if(!$selection){throw 'Edge level selection unavailable'};$selection.Select();$combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse()
 CloseSettings $window
 }
 if($counts.workers -eq 0){$stage='warmup worker';WorkerCycle}
 if($counts.weeks -eq 0){$stage='warmup week';Weeks 2}
 if(!@($samples|Where-Object phase -eq 'baseline').Count){$stage='baseline settle';Settle 'baseline'}
 $stage='settings cycles'
 # WorkerCycle opens settings too: 10 worker opens + 20 standalone opens = 30.
 while(($counts.settings-$counts.workers) -lt 20){$window=OpenSettings;CloseSettings $window;Snapshot ('settings-'+$counts.settings)}
 $stage='week cycles';if($counts.weeks -lt 50){Weeks (50-$counts.weeks)}
 $stage='worker cycles';while($counts.workers -lt 10){WorkerCycle}
 }
 $stage='final settle';Settle 'settled'
 if($counts.settings -ne 30 -or $counts.weeks -ne $(if($SettingsOnly){0}else{50}) -or $counts.workers -ne $(if($SettingsOnly){0}else{10})){throw 'Incomplete requested cycle counts'}
 if(!$KeepRunning){
 $stage='exit'
 $window=OpenSettings;$ui=Ui $window
 (Find $ui '通用').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
 Invoke (Find $ui '退出程序')
 $normalExit=$process.WaitForExit(10000) -and $process.ExitCode -eq 0
 if(!$normalExit){throw 'Owned main process did not exit cleanly'}
 }else{$stage='ObservationReady'}
}catch{$failure=$_.Exception.GetType().Name+': '+$_.Exception.Message; $failureStack=$_.ScriptStackTrace}
finally{
 if(!$SettingsOnly){[DesktopAudit]::Move($cursor[0],$cursor[1])}
 $report=@{capturedAtUtc=[datetimeoffset]::UtcNow.ToString('O');owner=$owner;version=$verified.version;stage=$stage;failure=$failure;failureStack=$failureStack;
  settingsOnly=[bool]$SettingsOnly;keptRunningByRequest=[bool]$KeepRunning;requested=@{settings=30;weeks=$(if($SettingsOnly){0}else{50});workers=$(if($SettingsOnly){0}else{10})};completed=$counts;extraExitSettingsOpen=($counts.settings -eq 31);settleSeconds=$SettleSeconds;
  normalExit=$normalExit;retainedForInspection=(!$process.HasExited);workers=$workers.ToArray();samples=$samples.ToArray();
  databaseUnchanged=((Get-FileHash -LiteralPath (Join-Path $data 'timetable.db')).Hash -eq $databaseBefore);startupUnchanged=((Startup) -ceq $startupBefore);
  scope=$(if($SettingsOnly){'Ordinary self-contained release, isolated synthetic 40-course database; settings UIA only, no school worker, credentials, mouse input or global desktop commands.'}else{'Ordinary self-contained release, isolated synthetic 40-course database; real UIA and owned-HWND mouse input. No credentials, real personal cache or global desktop commands.'});
  fullResourceBudgetPassed=$null;complete24Hours=$false;gpuTextureGrowthVerified=$false}
 $report|ConvertTo-Json -Depth 8|Set-Content (Join-Path $data 'cycle-report.json') -Encoding utf8NoBOM
 $report|ConvertTo-Json -Depth 8|Set-Content $ReportFile -Encoding utf8NoBOM
 # A timeout leaves the exact live owner recorded above. Never terminate/restart it to hide a failure.
 $process.Dispose()
}
if($failure -or (!$normalExit -and !$KeepRunning) -or !$report.databaseUnchanged -or !$report.startupUnchanged){throw 'Cycle batch incomplete; inspect resource-cycles.json and its original owner before continuing'}
Write-Output ('Completed requested cycles; settings-only='+$SettingsOnly+'; settled '+$SettleSeconds+' seconds; normalExit='+$normalExit+'; report '+$ReportFile)
