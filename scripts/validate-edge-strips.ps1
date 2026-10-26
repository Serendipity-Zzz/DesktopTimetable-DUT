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
function Element($role){[System.Windows.Automation.AutomationElement]::FromHandle([intptr](Owned $role).Hwnd)}
function Named($element,$name){$element.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,$name))}
function Invoke-Control($role,$name){
 Wait-For { $null -ne (Named (Element $role) $name) } $name
 (Named (Element $role) $name).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}
function Verify-Strip($label,$thickness,$length){
 Wait-For { (Owned '侧边条') -and !(Owned '周课表') } "$label collapsed"
 $w=Owned '侧边条';$factor=$w.Dpi/96
 $vertical=$direction -in @(0,2)
 Check "$label dimensions" ($(if($vertical){$w.Width -eq [math]::Round($thickness*$factor) -and $w.Height -eq [math]::Round($length*$factor)}else{$w.Width -eq [math]::Round($length*$factor) -and $w.Height -eq [math]::Round($thickness*$factor)}))
 Check "$label topmost-noactivate" (($w.ExStyle -band 0x8000008) -eq 0x8000008)
 $x=$w.X+[int]($w.Width/2);$y=$w.Y+[int]($w.Height/2)
 Wait-For { [DesktopAudit]::Hit($x,$y) -eq $w.Hwnd } "$label rendered hit surface"
 Check "$label visible-center-hit" ([DesktopAudit]::Hit($x,$y) -eq $w.Hwnd)
 $dx=if($vertical){$w.Width+2}else{0};$dy=if($vertical){0}else{$w.Height+2}
 Check "$label outside-visible-strip-passes-through" ([DesktopAudit]::Hit($x+$dx,$y+$dy) -ne $w.Hwnd -and [DesktopAudit]::Hit($x-$dx,$y-$dy) -ne $w.Hwnd)
 $before=[DesktopAudit]::GetForegroundWindow()
 [DesktopAudit]::Move($x,$y)
 Start-Sleep -Milliseconds 800
 Check "$label no-early-hover" (!(Owned '周课表'))
 Wait-For { (Owned '按钮0') -and (Owned '周课表') } "$label hover expand"
 Check "$label hover-does-not-activate" ([DesktopAudit]::GetForegroundWindow() -eq $before)
 $cases.Add(@{edge=$direction;size=@($thickness,$length);dpi=$w.Dpi;strip=@($w.X,$w.Y,$w.Width,$w.Height)})
 Invoke-Control '按钮0' '设置'
 Wait-For { (Owned '设置') -and (Named (Element '设置') '侧边条厚度') } 'settings loaded'
}
try {
 foreach($direction in 0..3){
  $process=$null
  $data=Join-Path $root ('artifacts/edge-strips-'+[guid]::NewGuid().ToString('N'))
  New-Item -ItemType Directory -Path $data|Out-Null
  @{schemaVersion=1;state=2;edgeDirection=$direction;edgeLevel=1;stripThickness=12;stripLength=120}|ConvertTo-Json|Set-Content (Join-Path $data 'settings.json') -Encoding utf8
  try {
   [DesktopAudit]::Move(300,300)
   $process=Start-Process -FilePath (Join-Path $root '.tools/dotnet/dotnet.exe') -ArgumentList @('"'+$app+'"','--desktop-demo','--data-directory','"'+$data+'"') -WindowStyle Hidden -PassThru
   $null=$process.Handle
   Verify-Strip "edge-$direction maximum" 12 120
   $settings=Element '设置'
   $thickness=(Named $settings '侧边条厚度').GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern)
   $length=(Named $settings '侧边条长度').GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern)
   Check "edge-$direction slider-ranges" ($thickness.Current.Minimum -eq 4 -and $thickness.Current.Maximum -eq 12 -and $length.Current.Minimum -eq 48 -and $length.Current.Maximum -eq 120)
   $thickness.SetValue(4);$length.SetValue(48)
   # Close immediately: the pending coalesced preview must flush before save.
   $settings.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
   Wait-For {!(Owned '设置')} 'close settings'
   [DesktopAudit]::Move(300,300)
   Start-Sleep -Milliseconds 150
   Verify-Strip "edge-$direction minimum" 4 48
   $saved=Get-Content (Join-Path $data 'settings.json') -Raw|ConvertFrom-Json
   Check "edge-$direction saved-minimum" ($saved.stripThickness -eq 4 -and $saved.stripLength -eq 48)
   (Named (Element '设置') '通用').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
   Invoke-Control '设置' '退出程序'
   Check "edge-$direction normal-exit" ($process.WaitForExit(10000) -and $process.ExitCode -eq 0)
  } finally {if($process){if(!$process.HasExited){$process.Kill($true);$process.WaitForExit()};$process.Dispose()}}
 }
 @{capturedAtUtc=[DateTimeOffset]::UtcNow.ToString('O');checks=$checks;cases=$cases;note='Four edges, min/max sizes, native hit boundaries, hover/no-activation, UI close flush and normal exit on current monitor/DPI. Not physical multi-monitor acceptance.'}|ConvertTo-Json -Depth 6|Set-Content (Join-Path $root 'docs/validation/edge-strips.json') -Encoding utf8
 Write-Output "Edge strips: $($checks.Count) checks passed."
} catch {
 @{error=$_.Exception.ToString();checks=$checks;cases=$cases}|ConvertTo-Json -Depth 6|Set-Content (Join-Path $root 'docs/validation/edge-strips-failure.json') -Encoding utf8
 throw
} finally {[DesktopAudit]::Move($cursor[0],$cursor[1])}
