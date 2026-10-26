param([string]$AppPath,[switch]$DiagnosticsOnly,[switch]$Recovery)
if($Recovery){$DiagnosticsOnly=$true}
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type ([regex]::Match((Get-Content (Join-Path $PSScriptRoot 'smoke-desktop.ps1') -Raw),"(?s)Add-Type @'\r?\n(.*?)\r?\n'@").Groups[1].Value)
Add-Type @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class OwnedDialogCancel {
 [DllImport("user32.dll")] static extern IntPtr GetDlgItem(IntPtr dialog,int id);
 [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window,out uint pid);
 [DllImport("user32.dll")] static extern int GetDlgCtrlID(IntPtr window);
 [DllImport("user32.dll")] static extern bool PostMessage(IntPtr window,uint message,IntPtr wParam,IntPtr lParam);
 delegate bool EnumProc(IntPtr window,IntPtr data);
 [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr window,EnumProc callback,IntPtr data);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr window,StringBuilder name,int length);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern IntPtr SendMessageTimeout(IntPtr window,uint message,IntPtr wParam,string text,uint flags,uint timeout,out IntPtr result);
 public static bool SetFileName(IntPtr dialog,int expectedPid,string value){
  uint pid;GetWindowThreadProcessId(dialog,out pid);if(pid!=expectedPid)return false;IntPtr edit=IntPtr.Zero;
  EnumChildWindows(dialog,(window,_)=>{var name=new StringBuilder(128);GetClassName(window,name,128);var id=GetDlgCtrlID(window);if((id==1001||id==1148||id==1152)&&name.ToString()=="Edit"){GetWindowThreadProcessId(window,out pid);if(pid==expectedPid){edit=window;return false;}}return true;},IntPtr.Zero);
  IntPtr result;return edit!=IntPtr.Zero&&SendMessageTimeout(edit,0x000C,IntPtr.Zero,value,2,1000,out result)!=IntPtr.Zero;
 }
 public static bool Save(IntPtr dialog,int expectedPid){
  uint pid;GetWindowThreadProcessId(dialog,out pid);if(pid!=expectedPid)return false;
  var button=GetDlgItem(dialog,1);if(button==IntPtr.Zero||GetDlgCtrlID(button)!=1)return false;
  GetWindowThreadProcessId(button,out pid);return pid==expectedPid&&PostMessage(button,0x00F5,IntPtr.Zero,IntPtr.Zero);
 }
 public static bool Cancel(IntPtr dialog,int expectedPid){
  uint pid;GetWindowThreadProcessId(dialog,out pid);if(pid!=expectedPid)return false;
  var button=GetDlgItem(dialog,2);if(button==IntPtr.Zero||GetDlgCtrlID(button)!=2)return false;
  GetWindowThreadProcessId(button,out pid);return pid==expectedPid&&PostMessage(button,0x00F5,IntPtr.Zero,IntPtr.Zero);
 }
}
'@
$data=Join-Path $root ('artifacts/data-management-'+[Guid]::NewGuid().ToString('N'))
$exports=Join-Path $root ('artifacts/data-management-exports-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $data,$exports|Out-Null
$dotnet=Join-Path $root '.tools/dotnet/dotnet.exe'
$app=Join-Path $root 'src/DlutTimetable.Desktop/bin/Release/net10.0-windows10.0.19041.0/DlutTimetable.Desktop.dll'
if($AppPath){$app=(Resolve-Path -LiteralPath $AppPath).Path}
$runKey='HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$before=(Get-ItemProperty -LiteralPath $runKey -Name DlutDesktopTimetable -ErrorAction SilentlyContinue).DlutDesktopTimetable
$profile=Join-Path $env:LOCALAPPDATA 'DLUTDesktopTimetable/auth-profile'
$profileBefore=Test-Path -LiteralPath $profile
$checks=[System.Collections.Generic.List[object]]::new()
function Check([string]$name,[bool]$passed){$checks.Add(@{name=$name;passed=$passed});if(!$passed){throw "Failed: $name"}}
function Named($element,[string]$name){$element.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,$name))}
function Id($element,[string]$id){$element.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty,$id))}
$process=Start-Process -FilePath $dotnet -ArgumentList @(('"'+$app+'"'),'--data-directory',('"'+$data+'"')) -WindowStyle Hidden -PassThru
function Wait-For([scriptblock]$condition){
 $until=[DateTime]::UtcNow.AddSeconds(12)
 while([DateTime]::UtcNow -lt $until){$result=&$condition;if($result){return $result};if($process.HasExited){throw 'Owned UI process exited unexpectedly'};Start-Sleep -Milliseconds 50}
 throw "Data management UI observation timed out: $script:stage"
}
function OwnWindow([string]$title){[DesktopAudit]::Windows($process.Id,$true)|Where-Object {$_.Title -eq $title -and $_.Visible}|Select-Object -First 1}
function CurrentSettings(){[System.Windows.Automation.AutomationElement]::FromHandle([IntPtr](OwnWindow '大工课表 · 设置').Hwnd)}
function Select-DialogFile([string]$button,[string]$title,[string]$target,[bool]$saving=$true){
 $script:stage=$title+' dialog'
 (Named (CurrentSettings) $button).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
 $window=Wait-For {OwnWindow $title}
 $script:stage=$title+' filename control'
 $null=Wait-For {[OwnedDialogCancel]::SetFileName([IntPtr]$window.Hwnd,$process.Id,$target)}
 if(![OwnedDialogCancel]::Save([IntPtr]$window.Hwnd,$process.Id)){throw 'Owned save button was not identified'}
 if($saving){$script:stage=$title+' saved file';$null=Wait-For {Test-Path -LiteralPath $target}}
}
$completed=$false
try {
 $null=Wait-For {OwnWindow '大工课表 · 设置'}
 $settings=CurrentSettings
 (Named $settings '数据与账号').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
 $settings=CurrentSettings
 Check 'personal-and-diagnostic-exports-are-distinct' ([bool](Named $settings '导出当前 JSON') -and [bool](Named $settings '导出脱敏诊断'))
 Check 'session-delete-is-explicit' (Named $settings '删除学校登录会话').Current.IsEnabled
 (Named $settings '删除学校登录会话').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
 $confirmation=Wait-For {OwnWindow '删除学校登录会话'}
 if(![OwnedDialogCancel]::Cancel([IntPtr]$confirmation.Hwnd,$process.Id)){throw 'Owned Cancel button not found; never confirm real-profile deletion during UI validation'}
 $null=Wait-For {!(OwnWindow '删除学校登录会话')}
 Check 'cancelled-delete-preserves-real-profile-presence' ($profileBefore -eq (Test-Path -LiteralPath $profile))
 Check 'cancelled-delete-starts-no-browser' (@(Get-CimInstance Win32_Process -Filter "ParentProcessId=$($process.Id) AND Name='DlutTimetable.AuthWorker.exe'").Count -eq 0)
 (Named (CurrentSettings) '预览虚构示例').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
 $script:stage='synthetic preview status'
 $null=Wait-For {(Id (CurrentSettings) 'SettingsStatus').Current.Name.StartsWith('正在预览虚构示例')}
 $timetable=Join-Path $exports 'timetable.json';Select-DialogFile '导出当前 JSON' '导出个人课表' $timetable
 $diagnostics=Join-Path $exports 'diagnostics.json';Select-DialogFile '导出脱敏诊断' '导出脱敏诊断' $diagnostics
 $personal=Get-Content -LiteralPath $timetable -Raw|ConvertFrom-Json
 $diagnostic=Get-Content -LiteralPath $diagnostics -Raw|ConvertFrom-Json
 Check 'personal-export-retains-explicit-synthetic-marker' ($personal.synthetic -and $personal.meetings.Count -gt 0)
 Check 'diagnostic-exports-counts-and-state' ($diagnostic.data.meetingCount -eq $personal.meetings.Count -and $diagnostic.diagnosticVersion -eq 2 -and [bool]$diagnostic.ui)
 $diagnosticText=Get-Content -LiteralPath $diagnostics -Raw
 Check 'diagnostic-omits-course-content-and-account' (!$diagnosticText.Contains($personal.accountId) -and @($personal.meetings|Where-Object{$diagnosticText.Contains($_.name)}).Count -eq 0)
 Check 'export-leaves-no-temporary-files' (@(Get-ChildItem -LiteralPath $exports -Filter '*.tmp').Count -eq 0)
 Select-DialogFile '导入 JSON' '导入个人课表' $timetable $false
 $script:stage='imported synthetic dataset'
 $null=Wait-For {(Id (CurrentSettings) 'SettingsStatus').Current.Name.StartsWith('已导入当前课表')}
 $database=Join-Path $data 'timetable.db';$databaseBefore=(Get-FileHash -LiteralPath $database -Algorithm SHA256).Hash
 $settingsPath=Join-Path $data 'settings.json'
 $settingsBefore=if(Test-Path -LiteralPath $settingsPath){(Get-FileHash -LiteralPath $settingsPath -Algorithm SHA256).Hash}else{'missing'}
 if(!$DiagnosticsOnly){
 $scope=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes([IO.Path]::GetFullPath($data).TrimEnd([IO.Path]::DirectorySeparatorChar).ToUpperInvariant())))
 $scopedProfile=Join-Path (Split-Path $profile -Parent) ('profiles/'+$scope+'/auth-profile')
 if($scope.Length -ne 64 -or $scopedProfile -eq $profile -or (Test-Path -LiteralPath $scopedProfile)){throw 'Expected a fresh isolated profile; no deletion will be confirmed'}
 New-Item -ItemType Directory -Path $scopedProfile|Out-Null
 [IO.File]::WriteAllText((Join-Path $scopedProfile 'fake-session-sentinel'),'fictional')
 (Named (CurrentSettings) '从学校刷新课表').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
 $script:stage='isolated auth worker'
 $worker=Wait-For {Get-CimInstance Win32_Process -Filter "ParentProcessId=$($process.Id) AND Name='DlutTimetable.AuthWorker.exe'"|Select-Object -First 1}
 $script:stage='isolated browser ready'
 $null=Wait-For {
  $browser=[DesktopAudit]::Windows($worker.ProcessId,$true)|Where-Object Title -eq '大工课表 · 学校登录'|Select-Object -First 1
  if($browser){$window=[System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$browser.Hwnd);$state=Id $window 'AuthBrowserState';$state -and $state.Current.HelpText -eq 'browser-ready'}
 }
 $owned=@([int]$worker.ProcessId);$all=@(Get-CimInstance Win32_Process)
 do{$added=@($all|Where-Object{$_.ParentProcessId -in $owned -and $_.ProcessId -notin $owned}|ForEach-Object{[int]$_.ProcessId});$owned+=$added}while($added.Count)
 (Named (CurrentSettings) '删除学校登录会话').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
 $script:stage='isolated-profile confirmation'
 $confirmation=Wait-For {OwnWindow '删除学校登录会话'}
 # This OK targets only the fresh hashed scope above; the default personal profile is never cleared.
 if(![OwnedDialogCancel]::Save([IntPtr]$confirmation.Hwnd,$process.Id)){throw 'Owned isolated-profile confirmation button missing'}
 $script:stage='isolated session cleared'
 $null=Wait-For {(Id (CurrentSettings) 'SettingsStatus').Current.Name.StartsWith('已删除学校登录会话')}
 Check 'confirmed-delete-removes-only-isolated-profile' (!(Test-Path -LiteralPath $scopedProfile))
 Check 'confirmed-delete-preserves-complete-database' ($databaseBefore -ceq (Get-FileHash -LiteralPath $database -Algorithm SHA256).Hash)
 $settingsAfter=if(Test-Path -LiteralPath $settingsPath){(Get-FileHash -LiteralPath $settingsPath -Algorithm SHA256).Hash}else{'missing'}
 Check 'confirmed-delete-preserves-settings' ($settingsBefore -ceq $settingsAfter)
 Check 'confirmed-delete-keeps-default-personal-profile' ($profileBefore -eq (Test-Path -LiteralPath $profile))
 Check 'confirmed-delete-reaps-owned-browser-group' (@($owned|Where-Object{Get-Process -Id $_ -ErrorAction SilentlyContinue}).Count -eq 0)
 Check 'login-available-after-clear' (Named (CurrentSettings) '从学校刷新课表').Current.IsEnabled
 }else{
  Check 'diagnostic-has-bounded-typed-journal' ($diagnostic.diagnostics.entries.Count -gt 0 -and $diagnostic.diagnostics.entries.Count -le 4096)
  $bad=Join-Path $exports 'CookieTokenStudentSecret.json'
  [IO.File]::WriteAllText($bad,'{"CookieTokenStudentSecret": "PrivateCourseSecret"')
  Select-DialogFile '导入 JSON' '导入个人课表' $bad $false
  $script:stage='typed import error'
  $null=Wait-For {(Id (CurrentSettings) 'SettingsStatus').Current.Name.Contains('DataOperationFailed')}
  $errorStatus=(Id (CurrentSettings) 'SettingsStatus').Current.Name
  Check 'error-shows-code-and-random-correlation-without-source-content' ($errorStatus -match 'DataOperationFailed · [0-9a-f]{32}' -and !$errorStatus.Contains('Secret'))
  Check 'failed-import-preserves-database' ($databaseBefore -ceq (Get-FileHash -LiteralPath $database -Algorithm SHA256).Hash)
  $errorReport=Join-Path $exports 'error-diagnostics.json';Select-DialogFile '导出脱敏诊断' '导出脱敏诊断' $errorReport
  $errorText=Get-Content -LiteralPath $errorReport -Raw;$errorDiagnostic=$errorText|ConvertFrom-Json
  $errorRecord=@($errorDiagnostic.diagnostics.entries|Where-Object code -eq 'DataOperationFailed')|Select-Object -Last 1
  Check 'error-export-correlates-ui-and-journal' ($errorRecord -and $errorStatus.Contains($errorRecord.correlationId.Replace('-','')))
  Check 'error-export-omits-path-and-untrusted-content' (!$errorText.Contains('Secret') -and !$errorText.Contains($data) -and !$errorText.Contains($exports))
  Check 'diagnostics-only-starts-no-school-worker' (@(Get-CimInstance Win32_Process -Filter "ParentProcessId=$($process.Id) AND Name='DlutTimetable.AuthWorker.exe'").Count -eq 0)
  if($Recovery){
   (Named (CurrentSettings) '布局与四态').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
   (Named (CurrentSettings) '修复桌面显示').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
   $null=Wait-For {(Id (CurrentSettings) 'SettingsStatus').Current.Name.StartsWith('已修复桌面显示')}
   $restored=OwnWindow '大工课表 · 周课表'
   Check 'desktop-repair-retains-desktop-layer-and-courses' ($restored -and $restored.Owner -ne 0 -and ($restored.ExStyle -band 8) -eq 0 -and $databaseBefore -ceq (Get-FileHash -LiteralPath $database -Algorithm SHA256).Hash)
   (Named (CurrentSettings) '材质与配色').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
   $oldGlass=(OwnWindow '大工课表 · 实时毛玻璃').Hwnd
   (Named (CurrentSettings) '重建当前材质').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
   $null=Wait-For {(Id (CurrentSettings) 'SettingsStatus').Current.Name.StartsWith('已重新尝试材质')}
   $glass=OwnWindow '大工课表 · 实时毛玻璃'
   Check 'material-retry-actually-replaces-live-surface' ($glass -and $glass.Hwnd -ne $oldGlass)
   Check 'recovery-preserves-database' ($databaseBefore -ceq (Get-FileHash -LiteralPath $database -Algorithm SHA256).Hash)
  }

 }

 $settings=CurrentSettings
 (Named $settings '通用').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
 (Named (CurrentSettings) '退出程序').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
 Check 'unified-exit-remains-successful' ($process.WaitForExit(10000) -and $process.ExitCode -eq 0)
 $after=(Get-ItemProperty -LiteralPath $runKey -Name DlutDesktopTimetable -ErrorAction SilentlyContinue).DlutDesktopTimetable
 Check 'real-startup-registration-unchanged' ($before -ceq $after)
 if($DiagnosticsOnly){
  $files=@(Get-ChildItem -LiteralPath (Join-Path $data 'logs') -Filter 'runtime-diagnostics.jsonl*')
  Check 'runtime-logs-stay-within-three-files-and-size-budget' ($files.Count -ge 1 -and $files.Count -le 3 -and @($files|Where-Object Length -gt (10*1024*1024)).Count -eq 0)
  $logs=@($files|ForEach-Object {Get-Content -LiteralPath $_.FullName});$logText=$logs -join "`n"
  $entries=@($logs|ForEach-Object {$_|ConvertFrom-Json})
  Check 'normal-exit-drains-journal' (@($entries|Where-Object Code -eq 'SessionExiting').Count -eq 1)
  if($Recovery){Check 'manual-recovery-events-record-completion' (@($entries|Where-Object Code -eq 'ShellRecoverySucceeded').Count -eq 1 -and @($entries|Where-Object Code -eq 'MaterialRecoveryFinished').Count -eq 1)}

  Check 'runtime-log-omits-course-account-and-untrusted-file' (!$logText.Contains('Secret') -and !$logText.Contains($personal.accountId) -and @($personal.meetings|Where-Object{$logText.Contains($_.name)}).Count -eq 0)
 }

 $completed=$true
} finally {
 if(!$process.HasExited){$process.Kill($false);$process.WaitForExit()};$process.Dispose()
 @{capturedAtUtc=[DateTimeOffset]::UtcNow.ToString('O');passed=$completed;checks=$checks;diagnosticsOnly=[bool]$DiagnosticsOnly;scope=$(if($DiagnosticsOnly){'Isolated synthetic import/export and fault injection. No school browser or session deletion; no real credentials or course data.'}else{'Isolated ordinary UI with synthetic import/export. Confirms deletion only of its fresh hashed browser scope; default personal profile unchanged. Anonymous school entry only; no credentials or course data fetched.'})}|ConvertTo-Json -Depth 5|Set-Content (Join-Path $root $(if($Recovery){'docs/validation/recovery-ui.json'}elseif($DiagnosticsOnly){'docs/validation/diagnostic-ui.json'}else{'docs/validation/data-management-ui.json'})) -Encoding utf8
}
Write-Output "Data management UI: $($checks.Count) checks passed. DiagnosticsOnly skips school browser and session deletion."
