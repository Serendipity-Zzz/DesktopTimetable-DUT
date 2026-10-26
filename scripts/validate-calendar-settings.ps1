$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type ([regex]::Match((Get-Content (Join-Path $PSScriptRoot 'smoke-desktop.ps1') -Raw),"(?s)Add-Type @'\r?\n(.*?)\r?\n'@").Groups[1].Value)
Add-Type ([regex]::Match((Get-Content (Join-Path $PSScriptRoot 'validate-data-management.ps1') -Raw),"(?s)Add-Type @'\r?\n(.*?)\r?\n'@").Groups[1].Value)
function Named($e,[string]$name){$e.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,$name))}
function Id($e,[string]$id){$e.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty,$id))}
function Wait-For([scriptblock]$condition){$until=[DateTime]::UtcNow.AddSeconds(12);while([DateTime]::UtcNow -lt $until){$value=&$condition;if($value){return $value};Start-Sleep -Milliseconds 40};throw 'Calendar UI observation timed out'}
function Own([string]$title){[DesktopAudit]::Windows($process.Id,$true)|Where-Object {$_.Title -eq $title -and $_.Visible}|Select-Object -First 1}
function Ui($w){[System.Windows.Automation.AutomationElement]::FromHandle([intptr]$w.Hwnd)}
function Exit-Owned {
 if($process.HasExited){return}
 $command=(Get-CimInstance Win32_Process -Filter "ProcessId=$($process.Id)").CommandLine
 if(!$command -or !$command.Contains($data) -or !$command.Contains('DlutTimetable.Desktop.dll')){throw 'Unexpected desktop process; exit aborted'}
 $settings=Ui (Own '大工课表 · 设置')
 (Named $settings '通用').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
 (Named $settings '退出程序').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
 if(!$process.WaitForExit(10000)){throw 'Calendar fixture desktop did not exit'}
}
$data=Join-Path $root ('artifacts/calendar-settings-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $data|Out-Null
$app=Join-Path $root 'src/DlutTimetable.Desktop/bin/Release/net10.0-windows10.0.19041.0/DlutTimetable.Desktop.dll'
$dotnet=Join-Path $root '.tools/dotnet/dotnet.exe'
$process=Start-Process -FilePath $dotnet -ArgumentList @(('"'+$app+'"'),'--data-directory',('"'+$data+'"')) -WindowStyle Hidden -PassThru
try {
 $settingsWindow=Wait-For {Own '大工课表 · 设置'};$settings=Ui $settingsWindow
 (Named $settings '数据与账号').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
 (Named $settings '导入 JSON').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
 $dialog=Wait-For {Own '导入个人课表'}
 if(![OwnedDialogCancel]::SetFileName([intptr]$dialog.Hwnd,$process.Id,(Join-Path $root 'fixtures/dlut/synthetic-term.json')) -or ![OwnedDialogCancel]::Save([intptr]$dialog.Hwnd,$process.Id)){throw 'Owned import dialog unavailable'}
 Wait-For {(Id (Ui $settingsWindow) 'OpenCalendarSettings').Current.IsEnabled}|Out-Null
 $database=Join-Path $data 'timetable.db';$before=(Get-FileHash -LiteralPath $database).Hash
 (Id (Ui $settingsWindow) 'OpenCalendarSettings').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
 $editor=Wait-For {Own '校历与节次'};$edit=Ui $editor
 $weeks=Wait-For {Id $edit 'CalendarWeekCount'};$weeks.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('20')
 (Id $edit 'CalendarSave').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
 Wait-For {! (Own '校历与节次')}|Out-Null
 if((Id (Ui $settingsWindow) 'SchoolDataSummary').Current.Name -notmatch '本机校历与节次设置已应用'){throw 'Saved adjustment not applied to settings summary'}
 if((Get-FileHash -LiteralPath $database).Hash -ne $before){throw 'Calendar adjustment changed source database'}
 Exit-Owned
 $saved=Get-Content (Join-Path $data 'settings.json') -Raw|ConvertFrom-Json
 if($saved.calendarAdjustment.weekCount -ne 20){throw 'Calendar preference not persisted'}
 $process=Start-Process -FilePath $dotnet -ArgumentList @(('"'+$app+'"'),'--data-directory',('"'+$data+'"')) -WindowStyle Hidden -PassThru
 $gear=Wait-For {[DesktopAudit]::Windows($process.Id)|Where-Object {$_.Title -eq '大工课表 · 按钮0' -and $_.Visible}|Select-Object -First 1}
 (Named (Ui $gear) '设置').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
 $settingsWindow=Wait-For {Own '大工课表 · 设置'};$settings=Ui $settingsWindow
 (Named $settings '数据与账号').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
 if((Id $settings 'SchoolDataSummary').Current.Name -notmatch '本机校历与节次设置已应用'){throw 'Restart lost the local calendar'}
 (Id $settings 'OpenCalendarSettings').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
 $editor=Wait-For {Own '校历与节次'};$edit=Ui $editor
 $weeks=Wait-For {Id $edit 'CalendarWeekCount'}
 if($weeks.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value -ne '20'){throw 'Editor did not restore persisted calendar'}
 (Id $edit 'CalendarReset').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
 Wait-For {! (Own '校历与节次')}|Out-Null
 if((Id (Ui $settingsWindow) 'SchoolDataSummary').Current.Name -match '本机校历与节次设置已应用'){throw 'Reset did not restore source calendar'}
 if((Get-FileHash -LiteralPath $database).Hash -ne $before){throw 'Reset changed source database'}
 $ballButton=Own '大工课表 · 按钮2'
 (Named (Ui $ballButton) '收成悬浮球').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
 Wait-For {Own '大工课表 · 悬浮球'}|Out-Null
 (Ui $settingsWindow).GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
 Wait-For {!(Own '大工课表 · 设置')}|Out-Null
 if($process.HasExited -or !(Own '大工课表 · 悬浮球')){throw 'Closing settings stopped the native-ball session'}
 # The same-data-directory instance command restores the owned timetable; it
 # does not create another resident session or need physical mouse input.
 $restore=Start-Process -FilePath $dotnet -ArgumentList @(('"'+$app+'"'),'--data-directory',('"'+$data+'"')) -WindowStyle Hidden -PassThru
 if(!$restore.WaitForExit(10000)){throw 'Single-instance restore did not finish'}
 $gear=Wait-For {Own '大工课表 · 按钮0'}
 (Named (Ui $gear) '设置').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
 $settingsWindow=Wait-For {Own '大工课表 · 设置'}
 if(Get-CimInstance Win32_Process -Filter "ParentProcessId=$($process.Id) AND Name='DlutTimetable.AuthWorker.exe'"){throw 'Local calendar operation opened school browser'}
 [ordered]@{passed=$true;scope='Isolated fictional JSON import, no personal cache or school requests';savedPreference=$true;persistedAcrossRestart=$true;editorReloadedPreference=$true;resetRestoredSource=$true;sourceDatabaseBytesUnchanged=$true;settingsCloseKeptNativeBallAlive=$true;singleInstanceRestoredTimetable=$true;noSchoolBrowser=$true;checkedAt=[DateTimeOffset]::Now.ToString('O')}|ConvertTo-Json|Set-Content (Join-Path $root 'docs/validation/calendar-settings-ui.json') -Encoding utf8
 Get-Content (Join-Path $root 'docs/validation/calendar-settings-ui.json')
} finally {if(!$process.HasExited){Exit-Owned}}
