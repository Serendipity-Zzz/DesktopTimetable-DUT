param([Parameter(Mandatory)][string]$AppPath)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$app=(Resolve-Path -LiteralPath $AppPath).Path
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
# Reuse only the PID-scoped native helper, without executing the broader smoke.
$helper=[regex]::Match((Get-Content (Join-Path $PSScriptRoot 'smoke-desktop.ps1') -Raw),"(?s)Add-Type @'\r?\n(.*?)\r?\n'@").Groups[1].Value
Add-Type $helper
$checks=[Collections.Generic.List[object]]::new()
$cases=[Collections.Generic.List[object]]::new()
$cursor=[DesktopAudit]::Cursor()
function Check($name,$passed){$checks.Add(@{name=$name;passed=[bool]$passed});if(!$passed){throw "Failed: $name"}}
function Owned($role){@([DesktopAudit]::Windows($process.Id)|Where-Object {$_.Visible -and $_.Title -eq ('大工课表 · '+$role)})|Select-Object -First 1}
function Wait-For([scriptblock]$condition,$label){
 $deadline=[datetime]::UtcNow.AddSeconds(12)
 while([datetime]::UtcNow -lt $deadline){if(&$condition){return};if($process.HasExited){throw "Process exited: $label"};Start-Sleep -Milliseconds 35}
 throw "Timed out: $label"
}
function Element($role){$w=Owned $role;if($w){[System.Windows.Automation.AutomationElement]::FromHandle([intptr]$w.Hwnd)}}
function Named($element,$name){if($element){$element.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,$name))}}
function Invoke-Control($role,$name){
 Wait-For { $null -ne (Named (Element $role) $name) } $name
 (Named (Element $role) $name).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}
Add-Type ([regex]::Match((Get-Content (Join-Path $PSScriptRoot 'validate-data-management.ps1') -Raw),"(?s)Add-Type @'\r?\n(.*?)\r?\n'@").Groups[1].Value)
$taskRoot=Join-Path $root ('artifacts/cache-recovery-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $taskRoot|Out-Null
function AnyWindow($title){@([DesktopAudit]::Windows($process.Id,$true)|Where-Object {$_.Visible -and $_.Title -eq $title})|Select-Object -First 1}
function Start-Case($directory){
 $script:process=Start-Process -FilePath (Join-Path $root '.tools/dotnet/dotnet.exe') -ArgumentList @('"'+$app+'"','--data-directory','"'+$directory+'"') -WindowStyle Hidden -PassThru
 $null=$process.Handle
 Wait-For {(Owned '设置') -and (Named (Element '设置') '数据与账号')} 'settings ready'
 (Named (Element '设置') '数据与账号').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
}
function Exit-Case {
 (Named (Element '设置') '通用').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
 Invoke-Control '设置' '退出程序'
 Check "$mode normal-exit" ($process.WaitForExit(10000) -and $process.ExitCode -eq 0)
}
function Stop-Case {if($process){if(!$process.HasExited){$process.Kill($true);$process.WaitForExit()};$process.Dispose();$script:process=$null}}
function Export-Report($destination){
 Invoke-Control '设置' '导出脱敏诊断';Wait-For {AnyWindow '导出脱敏诊断'} 'diagnostic dialog'
 $dialog=AnyWindow '导出脱敏诊断'
 if(![OwnedDialogCancel]::SetFileName([intptr]$dialog.Hwnd,$process.Id,$destination) -or ![OwnedDialogCancel]::Save([intptr]$dialog.Hwnd,$process.Id)){throw 'Owned export dialog unavailable'}
 Wait-For {Test-Path -LiteralPath $destination} 'diagnostic report'
}
$process=$null;$locked=$null
try {
 # Obtain a known valid empty schema from the real application, without cloning its SQL.
 $mode='clean-seed';$seed=Join-Path $taskRoot 'seed';Start-Case $seed;Exit-Case;Stop-Case
 foreach($mode in @('non-database','future-schema','invalid-dataset','locked-settings')){
  $directory=Join-Path $taskRoot $mode;New-Item -ItemType Directory -Path $directory|Out-Null
  $database=Join-Path $directory 'timetable.db';Copy-Item -LiteralPath (Join-Path $seed 'timetable.db') -Destination $database
  $settingsPath=Join-Path $directory 'settings.json'
  if($mode -eq 'non-database'){[IO.File]::WriteAllText($database,'NotSQLitePrivateSecret')}
  if($mode -in @('future-schema','invalid-dataset')){
   $code='import sqlite3,sys; c=sqlite3.connect(sys.argv[1]); c.execute("PRAGMA user_version=99") if sys.argv[2]=="future-schema" else c.execute("INSERT INTO current_dataset VALUES(1,?,?,?,?,?,?,?,?)",("PrivateAccountSecret","term","1.0","adapter","{}","PrivateCourseSecret","2026-10-25T00:00:00Z","hash")); c.commit(); c.close()'
   & python -c $code $database $mode
   if($LASTEXITCODE -ne 0){throw 'Isolated fixture creation failed'}
  }
  if($mode -eq 'locked-settings'){
   [IO.File]::WriteAllText($settingsPath,'{"schemaVersion":1,"startupDesired":false}')
   $settingsHash=(Get-FileHash -LiteralPath $settingsPath).Hash
   $locked=[IO.FileStream]::new($settingsPath,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
  }
  $before=(Get-FileHash -LiteralPath $database).Hash
  try {
   Start-Case $directory
   $readOnly=$mode -ne 'locked-settings'
   Check "$mode refresh-admission" ((Named (Element '设置') '从学校刷新课表').Current.IsEnabled -eq !$readOnly)
   Check "$mode import-admission" ((Named (Element '设置') '导入 JSON').Current.IsEnabled -eq !$readOnly)
   Check "$mode retry-admission" ((Named (Element '设置') '重新读取缓存').Current.IsEnabled -eq $readOnly)
   Check "$mode no-worker-on-start" (@(Get-CimInstance Win32_Process -Filter "ParentProcessId=$($process.Id) AND Name='DlutTimetable.AuthWorker.exe'").Count -eq 0)
   $report=Join-Path $taskRoot ($mode+'-diagnostic.json');Export-Report $report
   $text=Get-Content -LiteralPath $report -Raw;$diagnostic=$text|ConvertFrom-Json
   Check "$mode report-state" ($diagnostic.ui.cacheReadable -eq !$readOnly)
   Check "$mode report-omits-source-content" (!$text.Contains('Private') -and !$text.Contains($directory))
   Check "$mode original-database-preserved" ($before -ceq (Get-FileHash -LiteralPath $database).Hash)
   if($readOnly){
    Invoke-Control '设置' '重新读取缓存'
    Wait-For {(Named (Element '设置') '重新读取缓存').Current.IsEnabled} 'failed reread finishes'
    Check "$mode failed-reread-preserves-source" ($before -ceq (Get-FileHash -LiteralPath $database).Hash)
    Copy-Item -LiteralPath $database -Destination ($database+'.original')
    Copy-Item -LiteralPath (Join-Path $seed 'timetable.db') -Destination $database -Force
    Invoke-Control '设置' '重新读取缓存'
    Wait-For {(Named (Element '设置') '导入 JSON').Current.IsEnabled} 'external repair reread'
    Check "$mode repaired-cache-reenables-refresh" (Named (Element '设置') '从学校刷新课表').Current.IsEnabled
    Check "$mode damaged-backup-preserved" ($before -ceq (Get-FileHash -LiteralPath ($database+'.original')).Hash)
   }
   Exit-Case
   if($locked){$locked.Dispose();$locked=$null;Check "$mode settings-unchanged" ($settingsHash -ceq (Get-FileHash -LiteralPath $settingsPath).Hash)}
   $cases.Add(@{mode=$mode;passed=$true})
  } finally {if($locked){$locked.Dispose();$locked=$null};Stop-Case}
 }
 $mode='background-startup-failure';$blocked=Join-Path $taskRoot 'PrivatePathSecret';[IO.File]::WriteAllText($blocked,'original')
 $process=Start-Process -FilePath (Join-Path $root '.tools/dotnet/dotnet.exe') -ArgumentList @('"'+$app+'"','--background','--data-directory','"'+$blocked+'"') -WindowStyle Hidden -PassThru
 $null=$process.Handle
 Check 'background-startup-failure-exits-without-modal-dialog' ($process.WaitForExit(5000) -and $process.ExitCode -eq 1)
 Check 'background-startup-failure-preserves-source' ([IO.File]::ReadAllText($blocked) -eq 'original');Stop-Case
 @{capturedAtUtc=[DateTimeOffset]::UtcNow.ToString('O');checks=$checks;cases=$cases;scope='Isolated synthetic corrupt/future/unreadable fixtures, external repair uses real-app empty schema. No personal data or startup registration changes.'}|ConvertTo-Json -Depth 5|Set-Content (Join-Path $root 'docs/validation/cache-recovery-ui.json') -Encoding utf8
 Write-Output "Cache recovery: $($checks.Count) checks passed."
} catch {
 @{error=$_.Exception.ToString();checks=$checks;cases=$cases;mode=$mode;taskDirectory=$taskRoot}|ConvertTo-Json -Depth 5|Set-Content (Join-Path $root 'docs/validation/cache-recovery-failure.json') -Encoding utf8
 throw
} finally {if($locked){$locked.Dispose()};Stop-Case;[DesktopAudit]::Move($cursor[0],$cursor[1])}
