param([Parameter(Mandatory)][string]$AppPath,[int]$AttachProcessId=0,[string]$DataDirectory)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$app=(Resolve-Path -LiteralPath $AppPath).Path
if($DataDirectory){$DataDirectory=[IO.Path]::GetFullPath($DataDirectory)}
$release=[pscustomobject]@{version=[Diagnostics.FileVersionInfo]::GetVersionInfo($app).ProductVersion;directory=[IO.Path]::GetDirectoryName($app)}
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type ([regex]::Match((Get-Content (Join-Path $root 'scripts/smoke-desktop.ps1') -Raw),"(?s)Add-Type @'\r?\n(.*?)\r?\n'@").Groups[1].Value)
Add-Type @'
using System;using System.Text;using System.Runtime.InteropServices;
public static class TrayReview {
 [StructLayout(LayoutKind.Sequential)]public struct Identity{public uint Size;public IntPtr Window;public uint Id;public Guid Guid;}
 [DllImport("shell32.dll")]public static extern int Shell_NotifyIconGetRect(ref Identity identity,out Rect rectangle);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)]public static extern IntPtr FindWindowW(string c,string n);
 [StructLayout(LayoutKind.Sequential)]struct Point{public int X,Y;public Point(int x,int y){X=x;Y=y;}}
 [DllImport("user32.dll")]static extern IntPtr WindowFromPoint(Point p);
 [StructLayout(LayoutKind.Sequential)]struct Mouse{public int X,Y;public uint Data,Flags,Time;public UIntPtr Extra;}
 [StructLayout(LayoutKind.Explicit)]struct Union{[FieldOffset(0)]public Mouse Mouse;}
 [StructLayout(LayoutKind.Sequential)]struct Input{public uint Type;public Union Data;}
 [DllImport("user32.dll",SetLastError=true)]static extern uint SendInput(uint n,Input[] inputs,int size);
 [DllImport("user32.dll")]static extern bool SetCursorPos(int x,int y);
 public static Rect Icon(IntPtr owner){var id=new Identity{Size=(uint)Marshal.SizeOf<Identity>(),Window=owner,Id=1};
  if(Shell_NotifyIconGetRect(ref id,out var r)!=0)throw new Exception("Owned shell icon rectangle unavailable");return r;}
 public static void RightClickOwnedIcon(IntPtr owner,int shellPid){var r=Icon(owner);var x=(r.L+r.R)/2;var y=(r.T+r.B)/2;
  if(r.R<=r.L||r.B<=r.T||!SetCursorPos(x,y))throw new Exception("Invalid owned icon rectangle");
  System.Threading.Thread.Sleep(50);GetWindowThreadProcessId(WindowFromPoint(new Point(x,y)),out var pid);
  if(pid!=shellPid)throw new Exception("Shell icon is obscured; input aborted");
  var down=new[]{new Input{Data=new Union{Mouse=new Mouse{Flags=8}}}};
  if(SendInput(1,down,Marshal.SizeOf<Input>())!=1)throw new Exception("Right mouse down failed");
  try{System.Threading.Thread.Sleep(20);}finally{var up=new[]{new Input{Data=new Union{Mouse=new Mouse{Flags=16}}}};SendInput(1,up,Marshal.SizeOf<Input>());}
 }

 delegate bool Callback(IntPtr h,IntPtr d);
 [DllImport("user32.dll")]static extern bool EnumWindows(Callback callback,IntPtr data);
 [DllImport("user32.dll")]static extern uint GetWindowThreadProcessId(IntPtr h,out uint pid);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern int GetClassName(IntPtr h,StringBuilder name,int n);
 [DllImport("user32.dll")]public static extern bool PostMessageW(IntPtr h,uint m,IntPtr w,IntPtr l);
 [DllImport("user32.dll")]public static extern IntPtr SendMessageW(IntPtr h,uint m,IntPtr w,IntPtr l);
 [StructLayout(LayoutKind.Sequential)]public struct Rect{public int L,T,R,B;}
 [DllImport("user32.dll")]public static extern bool GetMenuItemRect(IntPtr owner,IntPtr menu,uint item,out Rect rect);
 [DllImport("user32.dll")]public static extern uint GetMenuState(IntPtr menu,uint item,uint flags);
 [DllImport("user32.dll")]public static extern int GetMenuItemCount(IntPtr menu);
 public static IntPtr Menu(int pid){IntPtr found=IntPtr.Zero;EnumWindows((h,d)=>{GetWindowThreadProcessId(h,out var p);
  var n=new StringBuilder(64);GetClassName(h,n,64);if(p==pid&&n.ToString()=="#32768")found=h;return true;},IntPtr.Zero);return found;}
}
'@
$data=$DataDirectory
if(!$AttachProcessId){
 $data=Join-Path $root ('artifacts/native-tray-review-'+[guid]::NewGuid().ToString('N'))
 & (Join-Path $root '.tools/dotnet/dotnet.exe') (Join-Path $root 'artifacts/resource-fixture-probe/ResourceFixture.Spike.dll') $data
 if($LASTEXITCODE -ne 0){throw 'Fixture preparation failed'}
}else{
 $identity=Get-CimInstance Win32_Process -Filter "ProcessId=$AttachProcessId"
 if(!$data -or !$identity.CommandLine.Contains($data) -or !$identity.CommandLine.Contains($release.directory)){throw 'Review attach identity mismatch'}
}
$before=(Get-FileHash (Join-Path $data 'timetable.db')).Hash
$start=[Diagnostics.ProcessStartInfo]::new((Join-Path $root '.tools/dotnet/dotnet.exe'))
$start.ArgumentList.Add($app)
$start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.ArgumentList.Add('--data-directory');$start.ArgumentList.Add($data)
$process=if($AttachProcessId){Get-Process -Id $AttachProcessId}else{[Diagnostics.Process]::Start($start)};$null=$process.Handle
$cursor=[DesktopAudit]::Cursor()
$runRecord=Join-Path $data 'tray-overflow-owner.json'
@{pid=$process.Id;startedUtc=$process.StartTime.ToUniversalTime().ToString('O');app=$app;dataDirectory=$data;status='Running'}|ConvertTo-Json|Set-Content -LiteralPath $runRecord -Encoding utf8NoBOM
function Own([string]$role,[bool]$visible=$true){[DesktopAudit]::Windows($process.Id,$true)|Where-Object {$_.Title -eq ('大工课表 · '+$role) -and (!$visible -or $_.Visible)}|Select-Object -First 1}
function Wait-For([scriptblock]$condition){$until=[DateTime]::UtcNow.AddSeconds(15);do{$v=&$condition;if($v){return $v};Start-Sleep -Milliseconds 50}while([DateTime]::UtcNow -lt $until);throw 'Owned native tray interaction timed out'}
function Named($element,[string]$name){$element.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,$name))}
$overflowOpened=$false
function Open-Menu {
 $owner=Wait-For {Own '托盘消息' $false}
 $shell=[TrayReview]::FindWindowW('Shell_TrayWnd',$null)
 if($shell -eq [intptr]::Zero){throw 'System notification area unavailable'}
 $ui=[System.Windows.Automation.AutomationElement]::FromHandle($shell)
 $chevron=$ui.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,'显示隐藏的图标'))
 if(!$chevron -or (Get-Process -Id $chevron.Current.ProcessId).ProcessName -ne 'explorer'){throw 'Verified system overflow command unavailable'}
 $shellPid=$chevron.Current.ProcessId
 $overflow=[System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ClassNameProperty,'TopLevelWindowForOverflowXamlIsland'))
 if(!$overflow){$chevron.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()}
 $script:overflowOpened=$true
 $null=Wait-For {
  $r=[TrayReview]::Icon([intptr]$owner.Hwnd)
  $point=[System.Windows.Point]::new(($r.L+$r.R)/2,($r.T+$r.B)/2)
  $overflow=[System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ClassNameProperty,'TopLevelWindowForOverflowXamlIsland'))
  if($overflow -and $overflow.Current.ProcessId -eq $shellPid){
   $icons=$overflow.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,'大工桌面课表'))
   foreach($icon in $icons){if($icon.Current.BoundingRectangle.Contains($point)){$true;break}}
  }
 }
 [TrayReview]::RightClickOwnedIcon([intptr]$owner.Hwnd,$shellPid)
 return (Wait-For {$h=[TrayReview]::Menu($process.Id);if($h -ne [intptr]::Zero){$h}})
}
function Choose-Item([intptr]$window,[uint32]$index){
 $menu=[TrayReview]::SendMessageW($window,0x1e1,0,0)
 $rect=[TrayReview+Rect]::new()
 if(![TrayReview]::GetMenuItemRect(0,$menu,$index,[ref]$rect)){throw 'Owned menu item rectangle unavailable'}
 $info=[DesktopAudit]::Windows($process.Id,$true)|Where-Object {$_.Hwnd -eq $window.ToInt64()}|Select-Object -First 1
 if(!$info){throw 'Menu does not belong to review process'}
 [DesktopAudit]::Click($info,[int](($rect.L+$rect.R)/2),[int](($rect.T+$rect.B)/2))
}
try{
 $null=Wait-For {Own '周课表'}
 $menu=Open-Menu
 Choose-Item $menu 1
 $window=Wait-For {Own '设置'}
 [System.Windows.Automation.AutomationElement]::FromHandle([intptr]$window.Hwnd).GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
 $null=Wait-For {if(!(Own '设置')){$true}}
 if($process.HasExited){throw 'Closing settings ended the application'}
 $menu=Open-Menu
 $nativeMenu=[TrayReview]::SendMessageW($menu,0x1e1,0,0)
 if(([TrayReview]::GetMenuState($nativeMenu,4,0) -band 1) -eq 0){throw 'Isolated startup command must be disabled'}
 $formsLoaded=@($process.Modules|Where-Object {$_.ModuleName -match '^System.Windows.Forms'}).Count -gt 0
 if($formsLoaded){throw 'Forms is still resident'}
 Choose-Item $menu ([uint32]([TrayReview]::GetMenuItemCount($nativeMenu)-1))
 if(!$process.WaitForExit(10000) -or $process.ExitCode -ne 0){throw 'Native tray exit did not finish normally'}
 $unchanged=(Get-FileHash (Join-Path $data 'timetable.db')).Hash -eq $before
 if(!$unchanged){throw 'Native tray interaction changed the dataset'}
 @{version=$release.version;capturedAtUtc=[DateTimeOffset]::UtcNow.ToString('O');normalExit=$true;databaseUnchanged=$unchanged;settingsOpenedFromNativeMenu=$true;settingsXPreservedSession=$true;isolatedStartupDisabled=$true;formsLoaded=$formsLoaded;physicalOverflowIconRightClick=$true;scope='Real Windows hidden-icons button invoked; target identified by owned HWND/uID Shell_NotifyIconGetRect and Shell UIA name/PID, then physical right click. Owned popup item rectangles verify Settings/X/exit. No tray callback injected; no Explorer restart or school request. UIA FromPoint returns the overflow parent on this Shell, so icon identity uses matching descendant bounds plus Shell_NotifyIconGetRect. Native menu UIA accessibility remains unclaimed.'}|ConvertTo-Json|Set-Content (Join-Path $root 'docs/validation/tray-overflow-ui.json') -Encoding utf8NoBOM
 @{pid=$process.Id;status='Exited';exitCode=$process.ExitCode}|ConvertTo-Json|Set-Content -LiteralPath $runRecord -Encoding utf8NoBOM
 'System overflow icon: physical right-click, settings, X and explicit exit verified.'
}catch{
 @{pid=$process.Id;status=if($process.HasExited){'Exited'}else{'RetainedForInspection'};errorType=$_.Exception.GetType().Name}|ConvertTo-Json|Set-Content -LiteralPath $runRecord -Encoding utf8NoBOM
 throw
}finally{[DesktopAudit]::Move($cursor.X,$cursor.Y);$process.Dispose()}
