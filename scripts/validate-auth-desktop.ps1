param([switch]$ManualOnly,[string]$AppPath,[ValidateRange(1,10)][int]$Cycles=1,[switch]$SettingsCancel)
if($SettingsCancel -and !$ManualOnly){throw 'SettingsCancel requires ManualOnly'}
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
Add-Type -AssemblyName UIAutomationClient;Add-Type -AssemblyName UIAutomationTypes
$helper=[regex]::Match((Get-Content (Join-Path $PSScriptRoot 'smoke-desktop.ps1') -Raw),"(?s)Add-Type @'\r?\n(.*?)\r?\n'@")
if(!$helper.Success){throw 'Owned-window helper missing'}
Add-Type -TypeDefinition $helper.Groups[1].Value
$dotnet=Join-Path $root '.tools/dotnet/dotnet.exe'
$app=Join-Path $root 'src/DlutTimetable.Desktop/bin/Release/net10.0-windows10.0.19041.0/DlutTimetable.Desktop.dll'
if($AppPath){$app=(Resolve-Path -LiteralPath $AppPath).Path}
$runKey='HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$before=(Get-ItemProperty -LiteralPath $runKey -Name DlutDesktopTimetable -ErrorAction SilentlyContinue).DlutDesktopTimetable
$samples=[System.Collections.Generic.List[object]]::new()
function Named($element,[string]$name){$element.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,$name))}
function Wait-For([scriptblock]$condition){
 $deadline=[DateTime]::UtcNow.AddSeconds(15)
 while([DateTime]::UtcNow -lt $deadline){$value=&$condition;if($value){return $value};Start-Sleep -Milliseconds 50};throw "Auth desktop observation timed out: $mode / $stage"
}
function Owned-Tree([int]$owner){
 $tree=@($owner);$all=@(Get-CimInstance Win32_Process)
 do{$added=@($all|Where-Object{$_.ParentProcessId -in $tree -and $_.ProcessId -notin $tree}|ForEach-Object{[int]$_.ProcessId});$tree+=$added}while($added.Count)
 return $tree
}
$modes=if($ManualOnly){@(1..$Cycles|ForEach-Object {'CancelRetry'})}else{@('ExplicitExit','ParentCrash')}
foreach($mode in $modes){
 $data=Join-Path $root ('artifacts/auth-desktop-'+$mode+'-'+[Guid]::NewGuid().ToString('N'))
 New-Item -ItemType Directory -Path $data | Out-Null
 $process=Start-Process -FilePath $dotnet -ArgumentList @(('"'+$app+'"'),'--data-directory',('"'+$data+'"')) -WindowStyle Hidden -PassThru
 try {
  $stage='settings'
  $settingsWindow=Wait-For {[DesktopAudit]::Windows($process.Id)|Where-Object Title -eq '大工课表 · 设置'|Select-Object -First 1}
  $settings=[System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$settingsWindow.Hwnd)
  if(Get-CimInstance Win32_Process -Filter "ParentProcessId=$($process.Id) AND Name='DlutTimetable.AuthWorker.exe'"){throw 'Startup opened a school worker'}
  $cacheBefore=(Get-FileHash -LiteralPath (Join-Path $data 'timetable.db')).Hash
  (Named $settings '数据与账号').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
  $login=Named $settings '从学校刷新课表';if(!$login.Current.IsEnabled){throw 'Ordinary session login unavailable'}
  $login.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
  $stage='worker process'
  $worker=Wait-For {Get-CimInstance Win32_Process -Filter "ParentProcessId=$($process.Id) AND Name='DlutTimetable.AuthWorker.exe'"|Select-Object -First 1}
  $stage='worker window'
  $workerWindow=Wait-For {[DesktopAudit]::Windows($worker.ProcessId)|Where-Object Title -eq '大工课表 · 学校登录'|Select-Object -First 1}
  $workerElement=[System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$workerWindow.Hwnd)
  $readyMatch=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty,'AuthBrowserState')
  $stage='browser ready'
  $null=Wait-For {
   $workerElement=[System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$workerWindow.Hwnd)
   $state=$workerElement.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$readyMatch)
   $state -and $state.Current.HelpText -eq 'browser-ready'
  }
  $workerElement=[System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$workerWindow.Hwnd)
  $browserChromeStatus=$workerElement.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$readyMatch).Current.Name
  $cancelledTree=@()
  if($ManualOnly){
   $cancelledTree=@(Owned-Tree $worker.ProcessId)
   $cancelledProcess=[Diagnostics.Process]::GetProcessById($worker.ProcessId);$null=$cancelledProcess.Handle
   $settings=[System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$settingsWindow.Hwnd)
   if((Named $settings '从学校刷新课表').Current.IsEnabled){throw 'Refresh not disabled while busy'}
   if(@(Get-CimInstance Win32_Process -Filter "ParentProcessId=$($process.Id) AND Name='DlutTimetable.AuthWorker.exe'").Count -ne 1){throw 'Multiple refresh workers'}
   $cancel=if($SettingsCancel){Named $settings '取消学校读取'}else{
    $workerElement.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty,'AuthCancel'))
   }
   $cancel.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
   $stage='cancel cleanup'
   try{if(!$cancelledProcess.WaitForExit(10000)){throw 'Captured worker process did not terminate after cancellation'}}finally{$cancelledProcess.Dispose()}
   $null=Wait-For {@($cancelledTree|Where-Object {Get-Process -Id $_ -ErrorAction SilentlyContinue}).Count -eq 0}
   $stage='retry admission';$null=Wait-For {
    $script:settings=[System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$settingsWindow.Hwnd)
    (Named $settings '从学校刷新课表').Current.IsEnabled
   }
   if((Get-FileHash -LiteralPath (Join-Path $data 'timetable.db')).Hash -cne $cacheBefore){throw 'Cancellation changed the cache'}
   (Named $settings '从学校刷新课表').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
   $stage='retry worker';$worker=Wait-For {Get-CimInstance Win32_Process -Filter "ParentProcessId=$($process.Id) AND Name='DlutTimetable.AuthWorker.exe'"|Select-Object -First 1}
  }
  $tree=@(Owned-Tree $worker.ProcessId)
  $watch=[Diagnostics.Stopwatch]::StartNew()
  if($mode -ne 'ParentCrash'){
   (Named $settings '通用').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
   (Named $settings '退出程序').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
  }else{
   # Only the exact Process instance launched above is crashed. No name-based tree termination.
   $process.Kill($false)
  }
  if(!$process.WaitForExit(10000)){throw 'Parent exit exceeded deadline'}
  $stage='owned group exit'
  $null=Wait-For {@($tree|Where-Object{Get-Process -Id $_ -ErrorAction SilentlyContinue}).Count -eq 0}
  $passed=$watch.ElapsedMilliseconds -lt 10000 -and ($mode -eq 'ParentCrash' -or $process.ExitCode -eq 0)
  $samples.Add(@{mode=$mode;passed=$passed;parentPid=$process.Id;workerPid=$worker.ProcessId;observedOwnedProcessIds=$tree;cancelledBrowserGroup=$cancelledTree;cancelledBrowserGroupGone=($ManualOnly -and $cancelledTree.Count -gt 0);elapsedMs=$watch.Elapsed.TotalMilliseconds;exitCode=$process.ExitCode;remainingObservedProcesses=0;browserChromeStatus=$browserChromeStatus})
 } finally {if(!$process.HasExited){$process.Kill($false);$process.WaitForExit()};$process.Dispose()}
}
$after=(Get-ItemProperty -LiteralPath $runKey -Name DlutDesktopTimetable -ErrorAction SilentlyContinue).DlutDesktopTimetable
$unchanged=$before -ceq $after
$output=if($SettingsCancel){'recovery-worker-ui.json'}elseif($ManualOnly){'manual-refresh-ui.json'}else{'auth-desktop.json'}
@{capturedAtUtc=[DateTimeOffset]::UtcNow.ToString('O');samples=$samples;realStartupRegistrationUnchanged=$unchanged;settingsCancel=[bool]$SettingsCancel;manualOnly=$ManualOnly.IsPresent;cycles=$Cycles;scope='Isolated desktop, user refresh entry and owned browser cleanup. ManualOnly checks no startup worker, busy gate, cancellation cache preservation and explicit retry. Each cycle cancels one browser-ready worker then exits while the retry worker starts. No credentials entered or timetable fetched.'}|ConvertTo-Json -Depth 6|Set-Content (Join-Path $root ('docs/validation/'+$output)) -Encoding utf8
if(!$unchanged -or @($samples|Where-Object{!$_.passed}).Count){throw 'Auth desktop lifecycle checks failed'}
Write-Output 'Auth desktop selected checks passed; real startup unchanged.'
