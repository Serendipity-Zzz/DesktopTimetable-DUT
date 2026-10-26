param([switch]$SnapshotOnly,[switch]$MaterialStage,[switch]$MotionStage,[ValidateRange(1,100)][int]$MotionCycles=5,[switch]$DragOnly,[switch]$MotionOnly,[switch]$SkipRapidInput,[string]$AppPath,[switch]$LiquidMotion,[switch]$SettlementMotion)
if($DragOnly){$SnapshotOnly=$true}
if($MotionOnly){$MotionStage=$true}
if($LiquidMotion -and (!$MotionOnly -or $MaterialStage)){throw 'LiquidMotion requires MotionOnly and separate material-stage validation'}
if($SettlementMotion -and !$MotionOnly){throw 'SettlementMotion requires MotionOnly'}
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class DesktopAudit {
 public sealed class Info { public long Hwnd; public string Title; public bool Visible; public long ExStyle,Owner; public int X,Y,Width,Height; public uint Dpi; }
 [StructLayout(LayoutKind.Sequential)] struct Rect { public int L,T,R,B; }
 [StructLayout(LayoutKind.Sequential)] struct Pt { public int X,Y; public Pt(int x,int y){X=x;Y=y;} }
 [StructLayout(LayoutKind.Sequential)] struct Mouse { public int X,Y;public uint Data,Flags,Time;public UIntPtr Extra; }
 [StructLayout(LayoutKind.Sequential)] struct Keyboard { public ushort Key,Scan;public uint Flags,Time;public UIntPtr Extra; }
 [StructLayout(LayoutKind.Explicit)] struct Union { [FieldOffset(0)] public Mouse Mouse;[FieldOffset(0)] public Keyboard Key; }
 [StructLayout(LayoutKind.Sequential)] struct Input { public uint Type;public Union Data; }
 delegate bool EnumProc(IntPtr hwnd,IntPtr data);
 [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc callback,IntPtr data);
 [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd,out uint pid);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd,StringBuilder text,int max);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd,StringBuilder text,int max);
 [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
 [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd,out Rect rect);
 [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")] static extern IntPtr GetLong(IntPtr hwnd,int index);
 [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hwnd,uint command);
 [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd);
 [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
 public static uint ForegroundPid(){uint pid;GetWindowThreadProcessId(GetForegroundWindow(),out pid);return pid;}
 [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(Pt point);
 [DllImport("user32.dll")] static extern bool SetCursorPos(int x,int y);
 [DllImport("user32.dll")] static extern bool GetCursorPos(out Pt point);
 [DllImport("user32.dll")] static extern uint SendInput(uint count,Input[] inputs,int size);
 [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd,IntPtr dc,uint flags);
 [DllImport("user32.dll")] public static extern uint GetGuiResources(IntPtr process,uint flag);
 public static int[] Cursor(){ Pt p;GetCursorPos(out p);return new[]{p.X,p.Y}; }
 public static void PressVerified(Info w){int x=w.X+w.Width/2,y=w.Y+w.Height/2;Move(x,y);System.Threading.Thread.Sleep(50);if(Hit(x,y)!=w.Hwnd)throw new Exception("Unsafe press target");MouseEvent(2);}
 public static void ReleaseMouse(){MouseEvent(4);}
 public static void Move(int x,int y){if(!SetCursorPos(x,y))throw new Exception("SetCursorPos failed");}
 public static long Hit(int x,int y){return WindowFromPoint(new Pt(x,y)).ToInt64();}
 public static string DescribeHit(int x,int y){var h=WindowFromPoint(new Pt(x,y));uint pid;GetWindowThreadProcessId(h,out pid);var name=new StringBuilder(256);GetClassName(h,name,256);return h.ToInt64()+":"+pid+":"+name.ToString();}
 public static Info[] Windows(int pid){return Windows(pid,false);}
 public static Info[] Windows(int pid,bool all){var list=new List<Info>();EnumWindows((h,d)=>{
  uint p;GetWindowThreadProcessId(h,out p);if(p!=pid)return true;
  var text=new StringBuilder(256);GetWindowText(h,text,256);if(!all&&!text.ToString().StartsWith("大工课表 · "))return true;
  Rect r;GetWindowRect(h,out r);list.Add(new Info{Hwnd=h.ToInt64(),Title=text.ToString(),Visible=IsWindowVisible(h),
   ExStyle=GetLong(h,-20).ToInt64(),Owner=GetWindow(h,4).ToInt64(),X=r.L,Y=r.T,Width=r.R-r.L,Height=r.B-r.T,Dpi=GetDpiForWindow(h)});return true;},IntPtr.Zero);return list.ToArray();}
 static void MouseEvent(uint flags){var a=new[]{new Input{Data=new Union{Mouse=new Mouse{Flags=flags}}}};if(SendInput(1,a,Marshal.SizeOf<Input>())!=1)throw new Exception("SendInput failed");}
 public static void Click(Info w,int x,int y){Move(x,y);System.Threading.Thread.Sleep(50);if(Hit(x,y)!=w.Hwnd)throw new Exception("Pointer is outside the verified owned HWND; input aborted: expected="+w.Hwnd+" actual="+DescribeHit(x,y)+" point="+x+","+y);MouseEvent(2);try{System.Threading.Thread.Sleep(20);}finally{MouseEvent(4);}}
 public static void ReverseClick(Info w){int x=w.X+w.Width/2,y=w.Y+w.Height/2;Click(w,x,y);System.Threading.Thread.Sleep(35);Click(w,x,y);}
 public static void BurstClicks(Info w,int count){int x=w.X+w.Width/2,y=w.Y+w.Height/2;for(int i=0;i<count;i++){if(i>0)System.Threading.Thread.Sleep(35);Click(w,x,y);}}
 public static void HoldAcrossEndpoint(Info w){int x=w.X+w.Width/2,y=w.Y+w.Height/2;Click(w,x,y);System.Threading.Thread.Sleep(100);Move(x,y);System.Threading.Thread.Sleep(25);if(Hit(x,y)!=w.Hwnd)throw new Exception("Owned hold target disappeared before press");MouseEvent(2);try{System.Threading.Thread.Sleep(450);if(Hit(x,y)!=w.Hwnd)throw new Exception("Held control disappeared at animation endpoint");}finally{MouseEvent(4);}}
 public static void Drag(Info w,int x,int y,int dx,int dy){Move(x,y);System.Threading.Thread.Sleep(50);if(Hit(x,y)!=w.Hwnd)throw new Exception("Unsafe drag target");MouseEvent(2);System.Threading.Thread.Sleep(70);Move(x+dx,y+dy);System.Threading.Thread.Sleep(150);MouseEvent(4);}
 public static void WinD(){var keys=new ushort[]{0x5B,0x44,0x44,0x5B};var a=new Input[4];for(int i=0;i<4;i++)a[i]=new Input{Type=1,Data=new Union{Key=new Keyboard{Key=keys[i],Flags=(uint)(i>=2?2:0)}}};if(SendInput(4,a,Marshal.SizeOf<Input>())!=4)throw new Exception("Win+D failed");}
}
'@
$checks = [System.Collections.Generic.List[object]]::new()
$shots = [System.Collections.Generic.List[object]]::new()
$taskData = Join-Path $root ('artifacts/desktop-smoke-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $taskData | Out-Null
if($LiquidMotion){@{schemaVersion=1;material='LiquidEdge';materialDisplacement=12}|ConvertTo-Json|Set-Content (Join-Path $taskData 'settings.json') -Encoding utf8NoBOM}
$dotnet = Join-Path $root '.tools/dotnet/dotnet.exe'
$app = Join-Path $root 'src/DlutTimetable.Desktop/bin/Release/net10.0-windows10.0.19041.0/DlutTimetable.Desktop.dll'
if($AppPath){$app=(Resolve-Path -LiteralPath $AppPath).Path}
$process = Start-Process -FilePath $dotnet -ArgumentList @('"'+$app+'"','--desktop-demo','--data-directory','"'+$taskData+'"') -WindowStyle Hidden -PassThru
$cursor = [DesktopAudit]::Cursor()
$toggled = $false
$materialResources = @()
$motionResources = @()
$startupKey='HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$startupBefore=(Get-ItemProperty -LiteralPath $startupKey -Name DlutDesktopTimetable -ErrorAction SilentlyContinue).DlutDesktopTimetable
function Validation-Log {
 foreach($suffix in @('.2','.1','')){Get-Content (Join-Path $taskData ('desktop-validation.log'+$suffix)) -ErrorAction SilentlyContinue}
}
function Material-Log { return @(Get-Content (Join-Path $taskData 'desktop-validation.log') -ErrorAction SilentlyContinue) }
function Resource-Sample([string]$phase) {
 $process.Refresh()
 return @{phase=$phase;atUtc=[DateTimeOffset]::UtcNow.ToString('O');privateBytes=$process.PrivateMemorySize64;workingSet=$process.WorkingSet64;cpuMilliseconds=$process.TotalProcessorTime.TotalMilliseconds;handles=$process.HandleCount;gdi=[DesktopAudit]::GetGuiResources($process.Handle,0);user=[DesktopAudit]::GetGuiResources($process.Handle,1)}
}
function Window([string]$role) { return @([DesktopAudit]::Windows($process.Id) | Where-Object { $_.Title -eq ('大工课表 · '+$role) -and $_.Visible }) | Select-Object -First 1 }
function Wait-For([scriptblock]$test,[string]$label) {
 $deadline=[DateTime]::UtcNow.AddSeconds(12)
 while([DateTime]::UtcNow -lt $deadline) {
  # Atomic settings replacement may briefly deny a concurrent read. Retry the
  # observation within the existing deadline, rather than aborting the UI stage.
  try { if (& $test) { return } } catch [System.IO.IOException] { }
  if($process.HasExited){throw "Process exited during $label"};Start-Sleep -Milliseconds 30
 }
 throw "Timed out: $label"
}
function Check([string]$name,[bool]$passed) { $checks.Add(@{name=$name;passed=$passed}) }
function Invoke-CommandButton([string]$role,[string]$name) {
 $w=Window $role;if(!$w){throw "Missing window $role"}
 $element=[System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$w.Hwnd)
 $match=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,$name)
 $button=$element.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$match)
 $controlDeadline=[DateTime]::UtcNow.AddSeconds(3)
 while(!$button -and [DateTime]::UtcNow -lt $controlDeadline){Start-Sleep -Milliseconds 40;$button=$element.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$match)}
 if(!$button){throw "Missing control $name"}
 $pattern=$null
 if($button.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern,[ref]$pattern)){$pattern.Invoke()}
 elseif($button.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern,[ref]$pattern)){$pattern.Toggle()}
 else{throw "No invocation pattern for $name"}
}
function Snapshot([string]$phase) { $shots.Add(@{phase=$phase;foreground=[DesktopAudit]::GetForegroundWindow().ToInt64();foregroundPid=[DesktopAudit]::ForegroundPid();windows=@([DesktopAudit]::Windows($process.Id))}) }
function Capture-Owned([string]$role,[string]$fileName){
 $window=Window $role;if(!$window){throw "Missing owned capture window $role"}
 $capture=[System.Drawing.Bitmap]::new($window.Width,$window.Height)
 $context=[System.Drawing.Graphics]::FromImage($capture);$handle=$context.GetHdc()
 try{$ok=[DesktopAudit]::PrintWindow([IntPtr]$window.Hwnd,$handle,2)}finally{$context.ReleaseHdc($handle);$context.Dispose()}
 try{if(!$ok){throw 'Owned PrintWindow failed'};$capture.Save((Join-Path $root $fileName))}finally{$capture.Dispose()}
}
try {
 Wait-For { (Window '按钮3') -and (Window '周课表') } 'initial desktop'
 $shanghai=[TimeZoneInfo]::ConvertTimeBySystemTimeZoneId([DateTimeOffset]::UtcNow,'Asia/Shanghai')
 $week=[Math]::Clamp([int][Math]::Floor(($shanghai.Date-[datetime]'2026-08-31').TotalDays/7)+1,1,18)
 $demoName=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,"第 $week 周课表，虚构示例")
 Wait-For { $el=[System.Windows.Automation.AutomationElement]::FromHandle([IntPtr](Window '周课表').Hwnd);$el.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$demoName) } 'demo render ready'
 Wait-For {Test-Path (Join-Path $taskData 'desktop-own-frame.png')} 'native own-layer frame'
 Start-Sleep -Milliseconds 120
 $initial=Window '周课表';Snapshot 'locked'
 Check 'locked-body-is-native-transparent' (($initial.ExStyle -band 0x20) -ne 0)
 Check 'base-780x468-at-window-dpi' ($initial.Width -eq [Math]::Round(780*$initial.Dpi/96) -and $initial.Height -eq [Math]::Round(468*$initial.Dpi/96))
 Check 'locked-is-desktop-owner-and-not-topmost' ($initial.Owner -ne 0 -and ($initial.ExStyle -band 8) -eq 0)
 $live=Window '实时毛玻璃'
 Check 'live-glass-is-layered-noactivate-and-mouse-transparent' ($live -and ($live.ExStyle -band 0x8080020) -eq 0x8080020 -and $live.Owner -eq $initial.Owner)
 $bitmap=[System.Drawing.Bitmap]::new($initial.Width,$initial.Height)
 $graphics=[System.Drawing.Graphics]::FromImage($bitmap);$dc=$graphics.GetHdc()
 try { $captured=[DesktopAudit]::PrintWindow([IntPtr]$initial.Hwnd,$dc,2) } finally{$graphics.ReleaseHdc($dc);$graphics.Dispose()}
 try{
  if($captured){
   $composite=[System.Drawing.Graphics]::FromImage($bitmap)
   try{foreach($w in @([DesktopAudit]::Windows($process.Id)|Where-Object{$_.Visible -and $_.Title -like '*按钮*'})){
    $overlay=[System.Drawing.Bitmap]::new($w.Width,$w.Height);$g=[System.Drawing.Graphics]::FromImage($overlay);$odc=$g.GetHdc()
    try{$ok=[DesktopAudit]::PrintWindow([IntPtr]$w.Hwnd,$odc,2)}finally{$g.ReleaseHdc($odc);$g.Dispose()}
    try{if($ok){$composite.DrawImageUnscaled($overlay,$w.X-$initial.X,$w.Y-$initial.Y)}}finally{$overlay.Dispose()}
   }}finally{$composite.Dispose()}
   $bitmap.Save((Join-Path $root 'docs/validation/desktop-foreground-only.png'))
  }
 }finally{$bitmap.Dispose()}
 # PrintWindow loses the alpha of separate layered HWNDs. Use the actual WPF visuals' own RGBA composition for review.
 Copy-Item -LiteralPath (Join-Path $taskData 'desktop-own-frame.png') -Destination (Join-Path $root 'docs/validation/desktop-foreground-only.png')
 if($DragOnly){
  $pinDebug=Window '按钮3'
  if([DesktopAudit]::Hit($pinDebug.X+12,$pinDebug.Y+12) -ne $pinDebug.Hwnd){[DesktopAudit]::WinD();$toggled=$true;Start-Sleep -Milliseconds 300}
  [DesktopAudit]::Click($pinDebug,$pinDebug.X+12,$pinDebug.Y+12)
  Wait-For {((Window '周课表').ExStyle -band 0x20) -eq 0} 'targeted unlock'
  $dragDebug=Window '周课表'
  [DesktopAudit]::Drag($dragDebug,$dragDebug.X+[int](300*$dragDebug.Dpi/96),$dragDebug.Y+[int](20*$dragDebug.Dpi/96),60,40)
  Start-Sleep -Milliseconds 150;$dragDebugResult=Window '周课表'
  Check 'targeted-native-header-drag' ($dragDebugResult.X -eq $dragDebug.X+60 -and $dragDebugResult.Y -eq $dragDebug.Y+40)
 }
 if($SnapshotOnly){
  Invoke-CommandButton '按钮0' '设置';Wait-For {Window '设置'} 'snapshot settings exit'
  $s=Window '设置';$e=[System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$s.Hwnd)
  $comboTypeSnapshot=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::ComboBox)
  Wait-For {$e.FindAll([System.Windows.Automation.TreeScope]::Descendants,$comboTypeSnapshot).Count -eq 3} 'layout controls accessible'
  Check 'settings-layout-sidebar-preserves-controls' $true
  $snapshotScale=$e.FindAll([System.Windows.Automation.TreeScope]::Descendants,$comboTypeSnapshot).Item(0)
  foreach($choice in @('0.8','1')){
   $expandSnapshot=$snapshotScale.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern);$expandSnapshot.Expand()
   $choiceMatch=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,$choice)
   Wait-For {$snapshotScale.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$choiceMatch)} 'styled combo popup items'
   $choiceElement=$snapshotScale.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$choiceMatch);$choicePattern=$null
   for($depth=0;$depth -lt 5 -and $choiceElement;$depth++){
    if($choiceElement.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern,[ref]$choicePattern)){break}
    $choiceElement=[System.Windows.Automation.TreeWalker]::RawViewWalker.GetParent($choiceElement)
   }
   if(!$choicePattern){throw 'Styled combo lost selection provider'}
   $choicePattern.Select();$expandSnapshot.Collapse()
   $factorSnapshot=[double]::Parse($choice,[Globalization.CultureInfo]::InvariantCulture)
   Wait-For {(Window '周课表').Width -eq [Math]::Round($initial.Width*$factorSnapshot)} 'styled combo scale projection'
   Check ('styled-combo-selects-'+$choice) ((Window '周课表').Height -eq [Math]::Round($initial.Height*$factorSnapshot))
  }
  Capture-Owned '设置' 'docs/validation/mac-settings-layout.png'
  foreach($page in @(@('材质与配色','背景材质'),@('数据与账号','导入 JSON'))){
   $pageMatch=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,$page[0])
   $e.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$pageMatch).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
   $controlMatch=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,$page[1])
   Wait-For {$e.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$controlMatch)} ('accessible page '+$page[0])
   Check ('settings-sidebar-page-'+$page[0]) $true
   if($page[0] -eq '材质与配色'){Capture-Owned '设置' 'docs/validation/mac-settings-material.png'}
   if($page[0] -eq '数据与账号'){
    $loginName=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,'从学校刷新课表')
    $login=$e.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$loginName)
    Check 'isolated-demo-login-entry-visible-without-launching-browser' ($login -and !$login.Current.IsEnabled)
    $settingsScroll=$null;$settingsScrollPattern=$null
    foreach($candidate in $e.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition)){
     $candidatePattern=$null
     if($candidate.TryGetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern,[ref]$candidatePattern) -and $candidatePattern.Current.VerticalScrollPercent -ne [System.Windows.Automation.ScrollPattern]::NoScroll){
      $settingsScroll=$candidate;$settingsScrollPattern=$candidatePattern;break
     }
    }
    if(!$settingsScrollPattern -or $settingsScrollPattern.Current.VerticalViewSize -ge 100){throw 'Settings data page must expose a vertically scrollable region'}
    $settingsScrollPattern.SetScrollPercent([System.Windows.Automation.ScrollPattern]::NoScroll,100)
    $lastDataActionMatch=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,'预览虚构示例')
    $lastDataAction=$e.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$lastDataActionMatch)
    Wait-For {!$lastDataAction.Current.IsOffscreen -and $settingsScrollPattern.Current.VerticalScrollPercent -ge 99} 'settings data page scrolls to its final action'
    Check 'settings-page-can-scroll-to-bottom' $true
    $settingsScrollPattern.SetScrollPercent([System.Windows.Automation.ScrollPattern]::NoScroll,0)
    Capture-Owned '设置' 'docs/validation/mac-settings-data.png'
   }
  }
  $m=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,'通用')
  $e.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$m).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
  $reducedSnapshotMatch=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,'减少动画（≤120 ms）')
  Wait-For {$e.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$reducedSnapshotMatch)} 'general switch accessible'
  $reducedSnapshot=$e.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$reducedSnapshotMatch)
  $reducedSnapshot.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
  Check 'general-switch-retains-toggle-pattern' ($reducedSnapshot.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On)
  $reducedSnapshot.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
  $startupMatch=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,'登录 Windows 后启动')
  $startupControl=$e.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$startupMatch)
  Check 'isolated-demo-does-not-enable-real-login-startup' ($startupControl -and !$startupControl.Current.IsEnabled)
  Invoke-CommandButton '设置' '退出程序'
  if(!$process.WaitForExit(10000) -or $process.ExitCode -ne 0){throw 'Snapshot cleanup failed'}
  Check 'visual-stage-settings-exits-cleanly' $true
  @{capturedAtUtc=[DateTimeOffset]::UtcNow.ToString('O');checks=$checks;exitCode=$process.ExitCode;dataDirectory=$taskData;validationLog=@(Validation-Log);note='Visual-stage native view and sidebar/accessibility smoke; not the full desktop/motion/performance matrix.'}|ConvertTo-Json -Depth 6|Set-Content (Join-Path $root 'docs/validation/visual-smoke.json') -Encoding utf8
  if(@($checks|Where-Object{!$_.passed}).Count -gt 0){throw 'Visual stage checks failed'}
  Write-Output "Visual smoke: $($checks.Count) checks passed; own native views captured; exit code 0."
  return
 }
 if(!$MotionOnly){
 Invoke-CommandButton '按钮3' '固定课表 / 允许拖动'
 Wait-For { ((Window '周课表').ExStyle -band 0x20) -eq 0 } 'unlock'
 Check 'pin-unlocks-without-topmost' (((Window '周课表').ExStyle -band 8) -eq 0)
 Invoke-CommandButton '按钮0' '设置'
 Wait-For { Window '设置' } 'settings'
 $settings=Window '设置'
 Check 'settings-is-an-independent-normal-window' ($settings.Owner -ne $initial.Owner)
 $element=[System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$settings.Hwnd)
 $comboType=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::ComboBox)
 $combos=$element.FindAll([System.Windows.Automation.TreeScope]::Descendants,$comboType)
 function Select-Combo($combo,[string]$name) {
  $expand=$combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern);$expand.Expand()
  $condition=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,$name)
  $item=$null;$itemDeadline=[DateTime]::UtcNow.AddSeconds(3)
  while(!$item -and [DateTime]::UtcNow -lt $itemDeadline){
   $item=$combo.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$condition)
   if(!$item){Start-Sleep -Milliseconds 30}
  }
  if(!$item){throw "Missing combo item $name"}
  $selection=$null
  for($depth=0;$depth -lt 5 -and $item;$depth++){
   if($item.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern,[ref]$selection)){break}
   $item=[System.Windows.Automation.TreeWalker]::RawViewWalker.GetParent($item)
  }
  if(!$selection){throw "Missing selection provider for $name"}
  $selection.Select();$expand.Collapse()
 }
 if($MaterialStage){
  $transform=$element.GetCurrentPattern([System.Windows.Automation.TransformPattern]::Pattern)
  $transform.Move($initial.X,$initial.Y)
  Check 'ordinary-settings-window-occludes-desktop-layer' ([DesktopAudit]::Hit($initial.X+100,$initial.Y+100) -eq $settings.Hwnd)
  $transform.Move($settings.X,$settings.Y)
  Wait-For { [bool](Window '实时毛玻璃') } 'live compositor surface'
  Check 'live-compositor-surface-visible' $true
  $tabMatch=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,'材质与配色')
  $element.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$tabMatch).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
  $kindName=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,'背景材质')
  $kindMatch=[System.Windows.Automation.AndCondition]::new($kindName,$comboType)
  $materialCombo=$element.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$kindMatch)
  $strengthMatch=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,'液态边缘位移强度')
  $strength=$element.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$strengthMatch)
  Check 'clear-frosted-disables-only-refraction-control' (!$strength.Current.IsEnabled)
  Select-Combo $materialCombo '液态边缘'
  Wait-For {(Window '边缘折射') -and $strength.Current.IsEnabled} 'liquid edge available'
  $edgeWindow=Window '边缘折射'
  Check 'edge-is-layered-pass-through-and-desktop-owned' (($edgeWindow.ExStyle -band 0x8080020) -eq 0x8080020 -and ($edgeWindow.ExStyle -band 8) -eq 0 -and $edgeWindow.Owner -eq $initial.Owner)
  $strength.GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern).SetValue(0)
  Wait-For {!(Window '边缘折射')} 'zero strength releases edge'
  Check 'zero-strength-releases-native-edge' $true
  $strength.GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern).SetValue(12)
  Wait-For {Window '边缘折射'} 'strength restores live edge'
  Check 'strength-restores-edge-with-same-panel-bounds' ((Window '边缘折射').Width -eq $initial.Width -and (Window '边缘折射').Height -eq $initial.Height)
  Select-Combo $materialCombo '轻透磨砂'
  Wait-For {!(Window '边缘折射') -and !$strength.Current.IsEnabled} 'clear frosted releases edge'
  Check 'clear-frosted-releases-enhancement' $true
  function Transparency-Control([string]$name) {
   $match=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,$name)
   $slider=$element.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$match)
   if(!$slider){throw "Missing transparency control: $name"}
   return $slider
  }
  $backgroundSlider=Transparency-Control '背景透明度';$foregroundSlider=Transparency-Control '前景透明度'
  $backgroundRange=$backgroundSlider.GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern)
  $foregroundRange=$foregroundSlider.GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern)
  Check 'transparency-controls-default-to-lighter-glass' ($backgroundRange.Current.Value -eq 80 -and $foregroundRange.Current.Value -eq 12)
  $glassHandle=(Window '实时毛玻璃').Hwnd
  $backgroundRange.SetValue(90);$foregroundRange.SetValue(22)
  Wait-For { $prefs=Get-Content (Join-Path $taskData 'settings.json') -Raw|ConvertFrom-Json; $prefs.backgroundTransparency -eq 0.9 -and $prefs.foregroundTransparency -eq 0.22 } 'independent transparency persisted'
  Check 'transparency-preview-reuses-live-compositor-HWND' ((Window '实时毛玻璃').Hwnd -eq $glassHandle)
  $materialResources += Resource-Sample 'expanded-idle-start'
  $frameCount=@(Material-Log | Where-Object {$_ -match 'MATERIAL_FRAME'}).Count
  Start-Sleep -Seconds 3
  $materialResources += Resource-Sample 'expanded-idle-end'
  Check 'idle-does-not-recreate-compositor' (@(Material-Log | Where-Object {$_ -match 'MATERIAL_FRAME'}).Count -eq $frameCount)
  Check 'live-backdrop-has-no-CPU-wallpaper-cache' (@(Material-Log | Where-Object {$_ -match 'MATERIAL_FRAME LiveBackdrop cache=0 '}).Count -gt 0)
  Select-Combo $materialCombo '实色'
  Wait-For { !(Window '实时毛玻璃') } 'solid releases compositor'
  Check 'solid-disables-only-background-transparency' (!$backgroundSlider.Current.IsEnabled -and $foregroundSlider.Current.IsEnabled)
  Check 'solid-releases-live-backdrop' $true
  Select-Combo $materialCombo '轻透磨砂'
  Wait-For { [bool](Window '实时毛玻璃') } 'live backdrop recreated'
  Check 'glass-reenables-background-transparency' $backgroundSlider.Current.IsEnabled
  Capture-Owned '设置' 'docs/validation/mac-settings-material.png'
  $layoutTabMatch=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,'布局与四态')
  $element.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$layoutTabMatch).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
  $combos=$element.FindAll([System.Windows.Automation.TreeScope]::Descendants,$comboType)
 }
 Select-Combo $combos.Item(0) '0.8'
 Wait-For { (Window '周课表').Width -eq [Math]::Round(780*0.8*$initial.Dpi/96) } '80 percent scale'
 Check '80-percent-keeps-ratio' ((Window '周课表').Height -eq [Math]::Round(468*0.8*$initial.Dpi/96))
 Select-Combo $combos.Item(0) '1.2'
 Wait-For { (Window '周课表').Width -eq [Math]::Round(780*1.2*$initial.Dpi/96) } '120 percent scale'
 Check '120-percent-keeps-ratio' ((Window '周课表').Height -eq [Math]::Round(468*1.2*$initial.Dpi/96))
 Select-Combo $combos.Item(0) '1'
 Wait-For { (Window '周课表').Width -eq $initial.Width } 'restore scale'
 $level=$combos.Item(2);Select-Combo $level '置顶层'
 Start-Sleep -Milliseconds 100
 $element.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
 Wait-For { !(Window '设置') } 'close settings'
 Check 'settings-X-keeps-process-and-scene-alive' (!$process.HasExited -and [bool](Window '周课表'))
 if($MaterialStage){
  $materialResources += Resource-Sample 'settings-closed-idle-start'
  Start-Sleep -Seconds 3
  $materialResources += Resource-Sample 'settings-closed-idle-end'
 }
 Invoke-CommandButton '按钮1' '侧边隐藏'
 Wait-For { $w=Window '侧边条';$w -and ($w.ExStyle -band 8) -ne 0 -and !(Window '周课表') } 'edge collapsed'
 $strip=Window '侧边条';Check 'edge-strip-topmost-option-applied' (($strip.ExStyle -band 8) -ne 0)
 if($MaterialStage){
  Wait-For { (Material-Log | Where-Object {$_ -match 'MATERIAL_FRAME'} | Select-Object -Last 1) -match 'Released cache=0 ' } 'collapsed edge releases frame'
  Check 'collapsed-edge-releases-material' $true
  $materialResources += Resource-Sample 'edge-collapsed'
 }
 [DesktopAudit]::Move($strip.X+[int]($strip.Width/2),$strip.Y+[int]($strip.Height/2))
 $watch=[Diagnostics.Stopwatch]::StartNew();Start-Sleep -Milliseconds 850
 Check 'hover-under-one-second-does-not-open' (!(Window '周课表'))
 Wait-For { Window '周课表' } 'hover open trigger'
 $openTriggerMs=$watch.ElapsedMilliseconds
 $timingLine=Get-Content (Join-Path $taskData 'desktop-validation.log') | Where-Object{$_ -match 'HOVER Open ([0-9.]+)$'} | Select-Object -Last 1
 if(!$timingLine){throw 'Missing monotonic hover timing event'}
 [void]($timingLine -match 'HOVER Open ([0-9.]+)$')
 $hoverWaitMs=[double]::Parse($Matches[1],[Globalization.CultureInfo]::InvariantCulture)
 Check 'hover-open-trigger-within-50ms' ($hoverWaitMs -ge 950 -and $hoverWaitMs -le 1050)
 Wait-For { $w=Window '按钮3';$w -and ($w.ExStyle -band 8) -ne 0 -and (Window '按钮0') -and (((Window '周课表').ExStyle -band 8) -ne 0) } 'one-second open and edge animation'
 $elapsed=$watch.ElapsedMilliseconds
 Check 'edge-panel-and-commands-share-topmost' ((@([DesktopAudit]::Windows($process.Id)|Where-Object{$_.Visible -and $_.Title -ne '大工课表 · 设置' -and ($_.ExStyle -band 8) -eq 0})).Count -eq 0)
 Snapshot 'edge-expanded'
 Invoke-CommandButton '按钮0' '设置';Wait-For {Window '设置'} 'S3 settings'
 $s3Settings=Window '设置'
 Check 'S3-settings-share-level-without-desktop-owner' (($s3Settings.ExStyle -band 8) -ne 0 -and $s3Settings.Owner -eq 0)
 [DesktopAudit]::Move(20,20);Start-Sleep -Milliseconds 1250
 Check 'settings-pauses-S3-auto-close' ([bool](Window '周课表'))
 $s3Element=[System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$s3Settings.Hwnd)
 $s3Element.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
 Wait-For {!(Window '设置')} 'S3 settings close'
 [DesktopAudit]::Move(20,20);Start-Sleep -Milliseconds 850
 Check 'leaving-under-one-second-does-not-close' ([bool](Window '周课表'))
 Wait-For { !(Window '周课表') } 'one-second close'
 Check 'edge-eventual-close' ([bool](Window '侧边条'))
 $closeLine=Get-Content (Join-Path $taskData 'desktop-validation.log') | Where-Object{$_ -match 'HOVER Close ([0-9.]+)$'} | Select-Object -Last 1
 if(!$closeLine){throw 'Missing close timing event'}
 [void]($closeLine -match 'HOVER Close ([0-9.]+)$');$closeWaitMs=[double]::Parse($Matches[1],[Globalization.CultureInfo]::InvariantCulture)
 Check 'hover-close-trigger-within-50ms' ($closeWaitMs -ge 950 -and $closeWaitMs -le 1050)
 [DesktopAudit]::Move($strip.X+[int]($strip.Width/2),$strip.Y+[int]($strip.Height/2))
 Wait-For { $w=Window '按钮2';$w -and ($w.ExStyle -band 8) -ne 0 } 'reopen edge'
 Invoke-CommandButton '按钮3' '固定课表 / 允许拖动'
 Wait-For {$w=Window '周课表';$w -and ($w.ExStyle -band 0x20) -ne 0 -and ($w.ExStyle -band 8) -eq 0 -and $w.Owner -ne 0} 'S3 Pin restores locked desktop'
 Check 'S3-pin-removes-topmost-and-locks-at-saved-position' ((Window '周课表').X -eq $initial.X -and (Window '周课表').Y -eq $initial.Y)
 $pinWindow=Window '按钮3';$pinElement=[System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$pinWindow.Hwnd)
 $pinName=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,'固定课表 / 允许拖动')
 $pinControl=$pinElement.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$pinName)
 Check 'pin-exposes-pressed-state-and-current-action' ($pinControl.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On -and $pinControl.Current.HelpText -eq '允许拖动课表（当前固定）')
 Invoke-CommandButton '按钮1' '侧边隐藏'
 Wait-For {(Window '侧边条') -and !(Window '周课表')} 'edge after Pin validation'
 $strip=Window '侧边条';[DesktopAudit]::Move($strip.X+$strip.Width/2,$strip.Y+$strip.Height/2)
 Wait-For {$w=Window '按钮2';$w -and ($w.ExStyle -band 8) -ne 0} 'reopen edge after Pin validation'
 Invoke-CommandButton '按钮2' '收成悬浮球'
 Wait-For { $w=Window '悬浮球';$w -and ($w.ExStyle -band 8) -eq 0 -and $w.Owner -ne 0 -and !(Window '收展过渡') } 'collapse ball'
 $orb=Window '悬浮球';Snapshot 'ball'
 if($MaterialStage){
  Wait-For { (Material-Log | Where-Object {$_ -match 'MATERIAL_FRAME'} | Select-Object -Last 1) -match 'Released cache=0 ' } 'ball releases frame'
  Check 'ball-releases-material' $true
  $materialResources += Resource-Sample 'ball'
 }
 Check 'ball-clears-edge-topmost' (($orb.ExStyle -band 8) -eq 0 -and $orb.Owner -ne 0)
 Start-Sleep -Milliseconds 150
 if([DesktopAudit]::Hit($orb.X+[int]($orb.Width/2),$orb.Y+[int]($orb.Height/2)) -ne $orb.Hwnd){[DesktopAudit]::WinD();$toggled=$true;Start-Sleep -Milliseconds 500}
 Snapshot 'after-desktop-expose-attempt'
 Wait-For { [DesktopAudit]::Hit($orb.X+[int]($orb.Width/2),$orb.Y+[int]($orb.Height/2)) -eq $orb.Hwnd } 'owned ball exposed on desktop'
 if($MaterialStage){
  # A full reversible cycle exercises both Show Desktop directions even when the ball was already exposed.
  [DesktopAudit]::WinD();Start-Sleep -Milliseconds 350
  [DesktopAudit]::WinD()
  Wait-For { [DesktopAudit]::Hit($orb.X+[int]($orb.Width/2),$orb.Y+[int]($orb.Height/2)) -eq $orb.Hwnd } 'ball after paired Show Desktop cycle'
  Check 'ball-remains-reachable-after-paired-WinD' $true
 }
 $before=[DesktopAudit]::GetForegroundWindow()
 Snapshot 'before-ball-drag'
 [DesktopAudit]::Drag($orb,$orb.X+[int]($orb.Width/2),$orb.Y+[int]($orb.Height/2),70,50)
 Start-Sleep -Milliseconds 150;$moved=Window '悬浮球'
 Check 'ball-drag-keeps-foreground' ([DesktopAudit]::GetForegroundWindow() -eq $before)
 Check 'dragging-ball-does-not-expand' ([bool]$moved -and !(Window '周课表'))
 Check 'ball-actually-moves' ($moved.X -ne $orb.X -or $moved.Y -ne $orb.Y)
 Snapshot 'after-ball-drag'
 $beforeBackground=[DesktopAudit]::GetForegroundWindow()
 $backgroundSecond=Start-Process -FilePath $dotnet -ArgumentList @('"'+$app+'"','--background','--data-directory','"'+$taskData+'"') -WindowStyle Hidden -PassThru
 try{Check 'background-second-instance-exits-quietly' ($backgroundSecond.WaitForExit(5000) -and $backgroundSecond.ExitCode -eq 0)}finally{$backgroundSecond.Dispose()}
 Start-Sleep -Milliseconds 150
 Check 'background-second-instance-preserves-ball-and-foreground' ((Window '悬浮球') -and !(Window '周课表') -and !(Window '设置') -and [DesktopAudit]::GetForegroundWindow() -eq $beforeBackground)
 $second=Start-Process -FilePath $dotnet -ArgumentList @('"'+$app+'"','--data-directory','"'+$taskData+'"') -WindowStyle Hidden -PassThru
 try{Check 'second-instance-exits-without-duplicate-session' ($second.WaitForExit(5000) -and $second.ExitCode -eq 0)}finally{$second.Dispose()}
 Wait-For { Window '按钮2' } 'single-instance restore'
 Snapshot 'after-single-instance-restore'
 Check 'second-instance-restores-original-session-locked' (((Window '周课表').ExStyle -band 0x20) -ne 0)
 Invoke-CommandButton '按钮2' '收成悬浮球'
 Wait-For { (Window '悬浮球') -and !(Window '收展过渡') } 'ball after IPC restore'
 $moved=Window '悬浮球'
 $beforeOrbClick=[DesktopAudit]::GetForegroundWindow()
 [DesktopAudit]::Click($moved,$moved.X+[int]($moved.Width/2),$moved.Y+[int]($moved.Height/2))
 Wait-For { Window '按钮3' } 'orb click restore'
 Snapshot 'after-orb-click'
 Check 'orb-click-does-not-activate-expanded-panel' ([DesktopAudit]::GetForegroundWindow() -eq $beforeOrbClick -or [DesktopAudit]::ForegroundPid() -ne $process.Id)
 $restored=Window '周课表'
 Check 'orb-restores-saved-rectangle-locked' ($restored.X -eq $initial.X -and $restored.Y -eq $initial.Y -and $restored.Width -eq $initial.Width -and ($restored.ExStyle -band 0x20) -ne 0)
 foreach($index in 0..3){$hot=Window ('按钮'+$index);Check ('locked-button-'+$index+'-native-hit') ([DesktopAudit]::Hit($hot.X+[int]($hot.Width/2),$hot.Y+[int]($hot.Height/2)) -eq $hot.Hwnd)}
 $gear=Window '按钮0';$gapHit=[DesktopAudit]::Hit($gear.X+$gear.Width+2,$gear.Y+[int]($gear.Height/2))
 Check 'locked-button-gap-passes-through' (@([DesktopAudit]::Windows($process.Id)|Where-Object{$_.Hwnd -eq $gapHit}).Count -eq 0)
 $beforeNativeInput=[DesktopAudit]::GetForegroundWindow()
 $pin=Window '按钮3';[DesktopAudit]::Click($pin,$pin.X+12,$pin.Y+12)
 Wait-For { ((Window '周课表').ExStyle -band 0x20) -eq 0 } 'native pin click'
 $movable=Window '周课表'
 Snapshot 'after-native-pin-before-header-drag'
 [DesktopAudit]::Drag($movable,$movable.X+[int](300*$movable.Dpi/96),$movable.Y+[int](20*$movable.Dpi/96),60,40)
 Start-Sleep -Milliseconds 180;$dragged=Window '周课表'
 Snapshot 'after-header-drag'
 Check 'header-drag-moves-panel' ($dragged.X -eq $movable.X+60 -and $dragged.Y -eq $movable.Y+40)
 Check 'native-drag-and-click-do-not-activate' ([DesktopAudit]::GetForegroundWindow() -eq $beforeNativeInput)
 Check 'native-drag-does-not-promote' (($dragged.ExStyle -band 8) -eq 0)
 [DesktopAudit]::Click($dragged,$dragged.X+[int](590*$dragged.Dpi/96),$dragged.Y+[int](20*$dragged.Dpi/96))
 $page2=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::HelpTextProperty,'第 2/2 页')
 Wait-For { $el=[System.Windows.Automation.AutomationElement]::FromHandle([IntPtr](Window '周课表').Hwnd);$el.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$page2) } 'periods 13-14 page'
 Check 'fourteen-period-page-keeps-rectangle' ((Window '周课表').Width -eq $dragged.Width -and (Window '周课表').Height -eq $dragged.Height)
 [DesktopAudit]::Click($dragged,$dragged.X+[int](550*$dragged.Dpi/96),$dragged.Y+[int](20*$dragged.Dpi/96))
 $pin=Window '按钮3';[DesktopAudit]::Click($pin,$pin.X+12,$pin.Y+12)
 Wait-For { ((Window '周课表').ExStyle -band 0x20) -ne 0 } 'lock again'
 $locked=Window '周课表'
 Check 'real-locked-body-passes-to-another-HWND' ([DesktopAudit]::Hit($locked.X+100,$locked.Y+200) -ne $locked.Hwnd)
 Snapshot 'dragged-and-relocked'
 }else{
  $control=Window '按钮2'
  if([DesktopAudit]::Hit($control.X+$control.Width/2,$control.Y+$control.Height/2) -ne $control.Hwnd){
   [DesktopAudit]::WinD();$toggled=$true
   Wait-For {[DesktopAudit]::Hit($control.X+$control.Width/2,$control.Y+$control.Height/2) -eq $control.Hwnd} 'motion-only controls exposed'
  }
 }
 if($MotionStage){
  $savedRect=Window '周课表'
  $foregroundBeforeMotion=[DesktopAudit]::GetForegroundWindow()
  $collapseControl=Window '按钮2'
  [DesktopAudit]::Click($collapseControl,$collapseControl.X+[int]($collapseControl.Width/2),$collapseControl.Y+[int]($collapseControl.Height/2))
   # Show() can expose the HWND before the same projection has hidden the old panel.
   # Observe the committed handoff; input/style assertions below remain independent.
   Wait-For {(Window '收展过渡') -and !(Window '周课表')} 'own texture transition handoff'
  $transition=Window '收展过渡';Snapshot 'texture-transition'
  $movingGlass=Window '实时毛玻璃'
  Check 'motion-keeps-live-glass-and-hides-stable-panel' ($movingGlass -and !(Window '周课表'))
  Check 'motion-glass-remains-pass-through-and-desktop-owned' ($movingGlass -and ($movingGlass.ExStyle -band 0x20) -ne 0 -and ($movingGlass.ExStyle -band 8) -eq 0 -and $movingGlass.Owner -eq $transition.Owner)
  Check 'motion-layer-is-transparent-and-desktop-owned' (($transition.ExStyle -band 0x20) -ne 0 -and ($transition.ExStyle -band 8) -eq 0 -and $transition.Owner -ne 0)
  Check 'motion-layer-does-not-intercept-pointer' ([DesktopAudit]::Hit($transition.X+[int]($transition.Width/2),$transition.Y+[int]($transition.Height/2)) -ne $transition.Hwnd)
  $motionBitmap=[System.Drawing.Bitmap]::new($transition.Width,$transition.Height)
  $motionGraphics=[System.Drawing.Graphics]::FromImage($motionBitmap);$motionDc=$motionGraphics.GetHdc()
  try{$motionCaptured=[DesktopAudit]::PrintWindow([IntPtr]$transition.Hwnd,$motionDc,2)}finally{$motionGraphics.ReleaseHdc($motionDc);$motionGraphics.Dispose()}
  try{if($motionCaptured){$motionBitmap.Save((Join-Path $root 'docs/validation/ball-motion-native.png'))}}finally{$motionBitmap.Dispose()}
  Check 'own-texture-native-frame-captured' $motionCaptured
  Wait-For { (Window '悬浮球') -and !(Window '收展过渡') -and !(Window '按钮2') } 'motion stable ball'
  Check 'stable-ball-releases-native-glass' (!(Window '实时毛玻璃'))
  $openingBall=Window '悬浮球'
  if(!$SkipRapidInput){
  [DesktopAudit]::ReverseClick($openingBall)
  Wait-For { (Window '悬浮球') -and !(Window '收展过渡') -and !(Window '按钮2') } 'orb reverse finishes at ball'
  Check 'orb-rapid-reverse-keeps-only-last-target' (!(Window '周课表'))
  $openingBall=Window '悬浮球'
  [DesktopAudit]::Click($openingBall,$openingBall.X+[int]($openingBall.Width/2),$openingBall.Y+[int]($openingBall.Height/2))
  Wait-For { (Window '按钮3') -and !(Window '收展过渡') } 'expand before header reversal'
  $reverseButton=Window '按钮2';[DesktopAudit]::ReverseClick($reverseButton)
  Wait-For { (Window '按钮3') -and !(Window '收展过渡') } 'header reverse finishes at panel'
  $afterReverse=Window '周课表'
  $restoredGlass=Window '实时毛玻璃'
  Check 'restored-panel-and-glass-share-bounds' ($restoredGlass -and $restoredGlass.X -eq $afterReverse.X -and $restoredGlass.Y -eq $afterReverse.Y -and $restoredGlass.Width -eq $afterReverse.Width -and $restoredGlass.Height -eq $afterReverse.Height)
  Check 'header-reverse-preserves-expanded-rectangle' ($afterReverse.X -eq $savedRect.X -and $afterReverse.Y -eq $savedRect.Y -and $afterReverse.Width -eq $savedRect.Width -and $afterReverse.Height -eq $savedRect.Height)
  Check 'motion-and-reverse-do-not-activate' ([DesktopAudit]::GetForegroundWindow() -eq $foregroundBeforeMotion)
  foreach($burstCount in @(3,4)){
   $burstButton=Window '按钮2';[DesktopAudit]::BurstClicks($burstButton,$burstCount)
   if($burstCount%2 -eq 1){
    Wait-For {(Window '悬浮球') -and !(Window '收展过渡') -and !(Window '按钮2')} 'odd header burst finishes at ball'
   }else{
    Wait-For {(Window '按钮3') -and !(Window '收展过渡')} 'even header burst finishes at panel'
    $burstButton=Window '按钮2';[DesktopAudit]::Click($burstButton,$burstButton.X+$burstButton.Width/2,$burstButton.Y+$burstButton.Height/2)
    Wait-For {(Window '悬浮球') -and !(Window '收展过渡') -and !(Window '按钮2')} 'ball before even orb burst'
   }
   Check ('header-'+$burstCount+'-click-last-target') $true
   $burstBall=Window '悬浮球';[DesktopAudit]::BurstClicks($burstBall,$burstCount)
   if($burstCount%2 -eq 0){
    Wait-For {(Window '悬浮球') -and !(Window '收展过渡') -and !(Window '按钮2')} 'even orb burst finishes at ball'
    $burstBall=Window '悬浮球';[DesktopAudit]::Click($burstBall,$burstBall.X+$burstBall.Width/2,$burstBall.Y+$burstBall.Height/2)
   }
   Wait-For {(Window '按钮3') -and !(Window '收展过渡')} 'panel after orb burst'
   Check ('orb-'+$burstCount+'-click-last-target') $true
  }
  Check 'odd-even-bursts-keep-foreground' ([DesktopAudit]::GetForegroundWindow() -eq $foregroundBeforeMotion)
  $heldHeader=Window '按钮2';[DesktopAudit]::HoldAcrossEndpoint($heldHeader)
  Wait-For {(Window '按钮3') -and !(Window '收展过渡')} 'held header release reverses to panel'
  Check 'header-hold-across-endpoint-retargets-on-release' $true
  $toBall=Window '按钮2';[DesktopAudit]::Click($toBall,$toBall.X+$toBall.Width/2,$toBall.Y+$toBall.Height/2)
  Wait-For {(Window '悬浮球') -and !(Window '收展过渡') -and !(Window '按钮2')} 'ball before held orb'
  $heldBall=Window '悬浮球';[DesktopAudit]::HoldAcrossEndpoint($heldBall)
  Wait-For {(Window '悬浮球') -and !(Window '收展过渡') -and !(Window '按钮2')} 'held orb release reverses to ball'
  Check 'orb-hold-across-endpoint-retargets-on-release' $true
  $toPanel=Window '悬浮球';[DesktopAudit]::Click($toPanel,$toPanel.X+$toPanel.Width/2,$toPanel.Y+$toPanel.Height/2)
  Wait-For {(Window '按钮3') -and !(Window '收展过渡')} 'panel after held controls'
  Check 'held-controls-keep-foreground' ([DesktopAudit]::GetForegroundWindow() -eq $foregroundBeforeMotion)
  }else{
   [DesktopAudit]::Click($openingBall,$openingBall.X+[int]($openingBall.Width/2),$openingBall.Y+[int]($openingBall.Height/2))
   Wait-For {(Window '按钮3') -and !(Window '收展过渡')} 'single-click expand without rapid-input acceptance'
  }
  $motionResources+=Resource-Sample 'before-motion-cycles'
  $stressStartedUtc=[DateTimeOffset]::UtcNow
  for($cycle=1;$cycle -le $MotionCycles;$cycle++){
   Invoke-CommandButton '按钮2' '收成悬浮球'
   Wait-For { (Window '悬浮球') -and !(Window '收展过渡') -and !(Window '按钮2') } ('cycle '+$cycle+' closed')
   $cycleBall=Window '悬浮球'
   [DesktopAudit]::Click($cycleBall,$cycleBall.X+[int]($cycleBall.Width/2),$cycleBall.Y+[int]($cycleBall.Height/2))
   Wait-For { (Window '按钮3') -and !(Window '收展过渡') } ('cycle '+$cycle+' opened')
   if($cycle -eq 1 -or $cycle%25 -eq 0 -or $cycle -eq $MotionCycles){$motionResources+=Resource-Sample ('motion-cycle-'+$cycle)}
  }
  $stressLines=@(Validation-Log | Where-Object {$_ -match 'BALL_END' -and [DateTimeOffset]::Parse($_.Split(' ')[0]) -ge $stressStartedUtc})
  $normalDurations=@($stressLines|ForEach-Object {if($_ -match 'elapsedMs=([0-9.]+) durationMs=320\.000') {[double]::Parse($Matches[1],[Globalization.CultureInfo]::InvariantCulture)}})
  Check 'all-requested-motion-cycles-completed' ($normalDurations.Count -eq 2*$MotionCycles)
  Check 'full-motion-completions-within-320-420ms' (@($normalDurations|Where-Object{$_ -lt 320 -or $_ -gt 420}).Count -eq 0)
  Start-Sleep -Seconds 5
  $motionResources+=Resource-Sample 'after-motion-idle'
  Check 'transition-releases-snapshot-and-frame-callback' ((Validation-Log|Where-Object{$_ -match 'BALL_RELEASE'}|Select-Object -Last 1) -match 'textureBytes=0 subscribed=False')
  Check 'transition-releases-one-shot-completion-timer' ((Validation-Log|Where-Object{$_ -match 'BALL_RELEASE'}|Select-Object -Last 1) -match 'completionTimer=False')
  Check 'stable-scene-has-no-transition-window' (!(Window '收展过渡'))
  Invoke-CommandButton '按钮0' '设置';Wait-For {Window '设置'} 'reduced motion settings'
  $motionSettings=Window '设置';$motionElement=[System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$motionSettings.Hwnd)
  $generalMatch=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,'通用')
  $motionElement.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$generalMatch).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
  $reducedMatch=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,'减少动画（≤120 ms）')
  $motionElement.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$reducedMatch).GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
  $motionElement.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close();Wait-For {!(Window '设置')} 'close reduced motion settings'
  $reducedStartedUtc=[DateTimeOffset]::UtcNow
  Invoke-CommandButton '按钮2' '收成悬浮球'
  Wait-For { (Window '悬浮球') -and !(Window '收展过渡') -and !(Window '按钮2') } 'reduced collapse'
  Wait-For {$w=Window '悬浮球';$w -and [DesktopAudit]::Hit($w.X+[int]($w.Width/2),$w.Y+[int]($w.Height/2)) -eq $w.Hwnd} 'reduced ball native hit ready'
  $cycleBall=Window '悬浮球'
  [DesktopAudit]::Click($cycleBall,$cycleBall.X+[int]($cycleBall.Width/2),$cycleBall.Y+[int]($cycleBall.Height/2))
  Wait-For { (Window '按钮3') -and !(Window '收展过渡') } 'reduced expand'
  $reducedDurations=@(Validation-Log|Where-Object {$_ -match 'BALL_END' -and [DateTimeOffset]::Parse($_.Split(' ')[0]) -ge $reducedStartedUtc}|ForEach-Object{if($_ -match 'elapsedMs=([0-9.]+) durationMs=80\.000'){[double]::Parse($Matches[1],[Globalization.CultureInfo]::InvariantCulture)}})
  Check 'reduced-motion-completes-within-120ms' ($reducedDurations.Count -eq 2 -and @($reducedDurations|Where-Object{$_ -gt 120}).Count -eq 0)
 }
 if($toggled){[DesktopAudit]::WinD();$toggled=$false;Start-Sleep -Milliseconds 300}
 Invoke-CommandButton '按钮0' '设置';Wait-For {Window '设置'} 'exit settings'
 $settings=Window '设置';$element=[System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$settings.Hwnd)
 $generalMatch=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,'通用')
 $general=$element.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$generalMatch)
 $general.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
 $startupMatch=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,'登录 Windows 后启动')
 $startupControl=$element.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$startupMatch)
 Check 'isolated-demo-startup-toggle-disabled' ($startupControl -and !$startupControl.Current.IsEnabled)
 Invoke-CommandButton '设置' '退出程序'
 Check 'explicit-settings-exit-ends-process' ($process.WaitForExit(10000))
 Check 'clean-exit-code' ($process.ExitCode -eq 0)
 $startupAfter=(Get-ItemProperty -LiteralPath $startupKey -Name DlutDesktopTimetable -ErrorAction SilentlyContinue).DlutDesktopTimetable
 Check 'isolated-demo-leaves-real-login-registration-unchanged' ($startupBefore -ceq $startupAfter)
 if($MotionStage){
  $motionSaved=Get-Content (Join-Path $taskData 'settings.json') -Raw|ConvertFrom-Json
  # SettingsStore uses DatasetJson's numeric enum representation (Locked = 0).
  Check 'reduced-motion-and-stable-target-persisted' ($motionSaved.reducedMotion -eq $true -and $motionSaved.state -eq 0)
  if($SettlementMotion){
   $normalScale=@();$reducedScale=@()
   foreach($line in @(Validation-Log)){
    if($line -match 'BALL_END .*durationMs=([0-9.]+).*minBallScale=([0-9.]+)'){
     $duration=[double]::Parse($Matches[1],[Globalization.CultureInfo]::InvariantCulture)
     $minimum=[double]::Parse($Matches[2],[Globalization.CultureInfo]::InvariantCulture)
     if($duration -eq 320){$normalScale+=$minimum}
     if($duration -eq 80){$reducedScale+=$minimum}
    }
   }
   Check 'normal-motion-has-bounded-damped-settlement' ($normalScale.Count -ge 2*$MotionCycles -and @($normalScale|Where-Object {$_ -lt 0.96 -or $_ -ge 0.995}).Count -eq 0)
   Check 'reduced-motion-has-no-settlement' ($reducedScale.Count -ge 2 -and @($reducedScale|Where-Object {$_ -ne 1}).Count -eq 0)
  }
  @{capturedAtUtc=[DateTimeOffset]::UtcNow.ToString('O');cycles=$MotionCycles;liquidMotion=[bool]$LiquidMotion;windowDpi=$initial.Dpi;rapidInputSkipped=[bool]$SkipRapidInput;checks=$checks;samples=$motionResources;normalDurationsMs=$normalDurations;reducedDurationsMs=$reducedDurations;validationLog=@(Validation-Log);exitCode=$process.ExitCode;note='Native cycle samples are not full G3/GPU/P95/24-hour acceptance.'}|ConvertTo-Json -Depth 7|Set-Content (Join-Path $root 'docs/validation/motion-smoke.json') -Encoding utf8
 }
 if($MaterialStage){
  $saved=Get-Content (Join-Path $taskData 'settings.json') -Raw | ConvertFrom-Json
  Check 'live-material-choice-persisted' ($saved.material -eq 'ClearFrosted')
  Check 'independent-transparency-survives-geometry-and-material-changes' ($saved.backgroundTransparency -eq 0.9 -and $saved.foregroundTransparency -eq 0.22)
  @{capturedAtUtc=[DateTimeOffset]::UtcNow.ToString('O');samples=$materialResources;validationLog=@(Material-Log);checks=$checks;note='Short smoke samples only; not G3 five-minute warmup/ten-minute acceptance, GPU or 24-hour results.'} | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $root 'docs/validation/material-smoke.json') -Encoding utf8
 }
 @{capturedAtUtc=[DateTimeOffset]::UtcNow.ToString('O');pid=$process.Id;checks=$checks;states=$shots;edgeHoverWaitMs=$hoverWaitMs;edgeCloseWaitMs=$closeWaitMs;edgePanelObservedMs=$openTriggerMs;edgeCommandsObservedMs=$elapsed;dataDirectory=$taskData;exitCode=$process.ExitCode} | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $root 'docs/validation/desktop-smoke.json') -Encoding utf8
 $failed=@($checks|Where-Object{!$_.passed});if($failed.Count -ne 0){throw "Desktop checks failed: $($failed.name -join ', ')"}
 Write-Output "Desktop smoke: $($checks.Count) checks passed; exit code $($process.ExitCode)"
} catch {
 @{error=$_.Exception.ToString();checks=$checks;states=$shots;edgeOpenTriggerMs=$openTriggerMs;ballPointerOwner=if($orb){[DesktopAudit]::DescribeHit($orb.X+[int]($orb.Width/2),$orb.Y+[int]($orb.Height/2))}else{$null};windows=@([DesktopAudit]::Windows($process.Id,$true));validationLog=@(Get-Content (Join-Path $taskData 'desktop-validation.log') -ErrorAction SilentlyContinue)} | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $root 'docs/validation/desktop-smoke-failure.json') -Encoding utf8
 throw
} finally {
 if($toggled){[DesktopAudit]::WinD()}
 [DesktopAudit]::Move($cursor[0],$cursor[1])
 if(!$process.HasExited){$process.Kill($true);$process.WaitForExit()}
 $process.Dispose()
}
