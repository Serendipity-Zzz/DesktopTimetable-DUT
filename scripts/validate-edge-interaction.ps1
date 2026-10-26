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
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class EdgeCover {
 [StructLayout(LayoutKind.Sequential)] struct Rect {public int L,T,R,B;}
 [StructLayout(LayoutKind.Sequential)] struct MonitorInfo {public int Size;public Rect Monitor,Work;public uint Flags;}
 [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd,uint flags);
 [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr monitor,ref MonitorInfo info);
 public static int[] WorkArea(IntPtr hwnd){var m=new MonitorInfo{Size=Marshal.SizeOf<MonitorInfo>()};if(!GetMonitorInfo(MonitorFromWindow(hwnd,2),ref m))throw new Exception("Monitor unavailable");return new[]{m.Work.L,m.Work.T,m.Work.R,m.Work.B};}
 [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern IntPtr CreateWindowEx(uint ex,string cls,string title,uint style,int x,int y,int width,int height,IntPtr parent,IntPtr menu,IntPtr instance,IntPtr param);
 [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd,IntPtr after,int x,int y,int width,int height,uint flags);
 [DllImport("user32.dll")] public static extern bool DestroyWindow(IntPtr hwnd);
 public static IntPtr Show(int x,int y){var h=CreateWindowEx(0x08000080,"STATIC","Controlled edge occlusion",0x90000100,x-100,y-100,200,200,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero);if(h==IntPtr.Zero)throw new System.ComponentModel.Win32Exception();SetWindowPos(h,IntPtr.Zero,0,0,0,0,0x13);return h;}
}
'@
function Log-Lines { @(Get-Content (Join-Path $data 'desktop-validation.log') -ErrorAction SilentlyContinue) }
function Trigger-Count($kind){@((Log-Lines)|Where-Object{$_ -match "HOVER $kind "}).Count}
function Open-FromStrip {
 $w=Owned '侧边条';[DesktopAudit]::Move($w.X+[int]($w.Width/2),$w.Y+[int]($w.Height/2))
 Wait-For {Owned '按钮0'} 'hover open'
}
try {
 foreach($direction in 0..3){foreach($level in 0..1){
  $process=$null;$cover=[intptr]::Zero;$label="edge-$direction level-$level"
  $data=Join-Path $root ('artifacts/edge-interaction-'+[guid]::NewGuid().ToString('N'))
  New-Item -ItemType Directory -Path $data|Out-Null
  @{schemaVersion=1;state=2;edgeDirection=$direction;edgeLevel=$level}|ConvertTo-Json|Set-Content (Join-Path $data 'settings.json') -Encoding utf8
  try {
   [DesktopAudit]::Move(300,300)
   $process=Start-Process -FilePath (Join-Path $root '.tools/dotnet/dotnet.exe') -ArgumentList @('"'+$app+'"','--desktop-demo','--data-directory','"'+$data+'"') -WindowStyle Hidden -PassThru
   $null=$process.Handle
   Wait-For {
    $candidate=Owned '侧边条';if(!$candidate){return $false}
    $vertical=$direction -in @(0,2);$factor=$candidate.Dpi/96
    $area=[EdgeCover]::WorkArea([intptr]$candidate.Hwnd)
    $size=if($vertical){$candidate.Width -eq [math]::Round(6*$factor) -and $candidate.Height -eq [math]::Round(64*$factor)}else{$candidate.Width -eq [math]::Round(64*$factor) -and $candidate.Height -eq [math]::Round(6*$factor)}
    $anchored=switch($direction){0 {$candidate.X -eq $area[0]} 1 {$candidate.Y -eq $area[1]} 2 {$candidate.X+$candidate.Width -eq $area[2]} 3 {$candidate.Y+$candidate.Height -eq $area[3]}}
    $centered=if($vertical){[math]::Abs($candidate.Y-($area[1]+$area[3]-$candidate.Height)/2) -le 1}else{[math]::Abs($candidate.X-($area[0]+$area[2]-$candidate.Width)/2) -le 1}
    $size -and $anchored -and $centered
   } 'strip positioned at final size'
   $w=Owned '侧边条';$x=$w.X+[int]($w.Width/2);$y=$w.Y+[int]($w.Height/2)
   $cover=[EdgeCover]::Show($x,$y)
   Write-Output "Layer probe $label strip=$($w.Hwnd) cover=$cover point=$x,$y hit=$([DesktopAudit]::DescribeHit($x,$y))"
   Wait-For { [DesktopAudit]::Hit($x,$y) -eq $(if($level -eq 0){$cover.ToInt64()}else{$w.Hwnd}) } 'cover layering'
   $focus=[DesktopAudit]::GetForegroundWindow()
   [DesktopAudit]::Move($x,$y);Start-Sleep -Milliseconds 1250
   if($level -eq 0){
    Check "$label covered strip never opens" (!(Owned '周课表') -and (Trigger-Count 'Open') -eq 0)
   }else{
    Wait-For {Owned '按钮0'} 'topmost hover over ordinary window'
    Check "$label visible topmost strip opens" ([bool](Owned '周课表'))
   }
   Check "$label hover preserves foreground" ([DesktopAudit]::GetForegroundWindow() -eq $focus)
   [EdgeCover]::DestroyWindow($cover)|Out-Null;$cover=[intptr]::Zero
   [DesktopAudit]::Move(300,300);Start-Sleep -Milliseconds 150
   if(!(Owned '按钮0')){Open-FromStrip}
   $w=Owned '侧边条';$x=$w.X+[int]($w.Width/2);$y=$w.Y+[int]($w.Height/2)
   # Leaving less than one second then returning must cancel the pending close.
   [DesktopAudit]::Move(10,10);Start-Sleep -Milliseconds 650
   [DesktopAudit]::Move($x,$y);Start-Sleep -Milliseconds 550
   Check "$label reentry cancels close" ([bool](Owned '按钮0'))
   $closeBefore=Trigger-Count 'Close'
   [DesktopAudit]::Move(10,10)
   Wait-For { (Trigger-Count 'Close') -gt $closeBefore } 'closing trigger'
   Start-Sleep -Milliseconds 45
   [DesktopAudit]::Move($x,$y)
   Wait-For {Owned '按钮0'} 'closing reversed'
   Start-Sleep -Milliseconds 300
   Check "$label reverse survives old completion" ([bool](Owned '按钮0'))
   Invoke-Control '按钮0' '设置';Wait-For {Owned '设置'} 'settings popup'
   [DesktopAudit]::Move(10,10);Start-Sleep -Milliseconds 1250
   Check "$label settings suspends auto close" ([bool](Owned '周课表'))
   (Element '设置').GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
   Wait-For {!(Owned '设置')} 'settings closed'
   Start-Sleep -Milliseconds 650
   Check "$label settings close restarts wait" ([bool](Owned '周课表'))
   Wait-For {!(Owned '周课表')} 'eventual close'
   Check "$label close returns strip only" ([bool](Owned '侧边条') -and !(Owned '按钮0'))
   Open-FromStrip
   Invoke-Control '按钮0' '设置';Wait-For {Named (Element '设置') '通用'} 'general tab'
   (Named (Element '设置') '通用').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
   Invoke-Control '设置' '退出程序'
   Check "$label normal exit" ($process.WaitForExit(10000) -and $process.ExitCode -eq 0)
   $cases.Add(@{edge=$direction;level=$level;dpi=$w.Dpi;strip=@($w.X,$w.Y,$w.Width,$w.Height);log=@(Log-Lines)})
   Write-Output "$label passed"
  } finally {
   if($cover -ne [intptr]::Zero){[EdgeCover]::DestroyWindow($cover)|Out-Null}
   if($process){if(!$process.HasExited){$process.Kill($true);$process.WaitForExit()};$process.Dispose()}
  }
 }}
 @{capturedAtUtc=[DateTimeOffset]::UtcNow.ToString('O');checks=$checks;cases=$cases;note='Actual ordinary native cover owned by test process, all four edges and both levels, real mouse timing/reentry/settings. Current physical monitor/DPI only.'}|ConvertTo-Json -Depth 6|Set-Content (Join-Path $root 'docs/validation/edge-interaction.json') -Encoding utf8
 Write-Output "Edge interaction: $($checks.Count) checks passed."
} catch {
 @{error=$_.Exception.ToString();checks=$checks;cases=$cases;dataDirectory=$data;log=@(Log-Lines)}|ConvertTo-Json -Depth 6|Set-Content (Join-Path $root 'docs/validation/edge-interaction-failure.json') -Encoding utf8
 throw
} finally {[DesktopAudit]::Move($cursor[0],$cursor[1])}
