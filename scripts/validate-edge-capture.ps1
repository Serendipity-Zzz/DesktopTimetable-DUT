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
$process=$null;$pressed=$false
$data=Join-Path $root ('artifacts/edge-capture-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $data|Out-Null
@{schemaVersion=1;state=2;edgeDirection=2;edgeLevel=1}|ConvertTo-Json|Set-Content (Join-Path $data 'settings.json') -Encoding utf8
try {
 [DesktopAudit]::Move(300,300)
 $process=Start-Process -FilePath (Join-Path $root '.tools/dotnet/dotnet.exe') -ArgumentList @('"'+$app+'"','--desktop-demo','--data-directory','"'+$data+'"') -WindowStyle Hidden -PassThru
 $null=$process.Handle
 Wait-For {$w=Owned '侧边条';$w -and $w.Width -eq [math]::Round(6*$w.Dpi/96) -and $w.Height -eq [math]::Round(64*$w.Dpi/96)} 'strip positioned'
 $strip=Owned '侧边条';[DesktopAudit]::Move($strip.X+[int]($strip.Width/2),$strip.Y+[int]($strip.Height/2))
 Wait-For {Owned '按钮0'} 'expanded'
 $button=Owned '按钮0'
 Wait-For {[DesktopAudit]::Hit($button.X+[int]($button.Width/2),$button.Y+[int]($button.Height/2)) -eq $button.Hwnd} 'button rendered'
 [DesktopAudit]::PressVerified($button);$pressed=$true
 try {
  [DesktopAudit]::Move(10,10);Start-Sleep -Milliseconds 1350
  Check 'captured-outside-longer-than-one-second-keeps-panel' ([bool](Owned '周课表'))
  Check 'captured-outside-does-not-open-settings' (!(Owned '设置'))
 } finally {[DesktopAudit]::ReleaseMouse();$pressed=$false}
 Start-Sleep -Milliseconds 650
 Check 'release-starts-new-wait' ([bool](Owned '周课表'))
 Wait-For {!(Owned '周课表')} 'closed after release'
 Check 'outside-release-cancels-command' (!(Owned '设置'))
 [DesktopAudit]::Move($strip.X+[int]($strip.Width/2),$strip.Y+[int]($strip.Height/2))
 Wait-For {Owned '按钮0'} 'reopen'
 Invoke-Control '按钮0' '设置';Wait-For {Named (Element '设置') '通用'} 'general'
 (Named (Element '设置') '通用').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
 Invoke-Control '设置' '退出程序'
 Check 'normal-exit' ($process.WaitForExit(10000) -and $process.ExitCode -eq 0)
 @{capturedAtUtc=[DateTimeOffset]::UtcNow.ToString('O');checks=$checks;log=@(Get-Content (Join-Path $data 'desktop-validation.log'))}|ConvertTo-Json -Depth 5|Set-Content (Join-Path $root 'docs/validation/edge-capture.json') -Encoding utf8
 Write-Output "Edge capture: $($checks.Count) checks passed."
} finally {
 if($pressed){[DesktopAudit]::ReleaseMouse()}
 if($process){if(!$process.HasExited){$process.Kill($true);$process.WaitForExit()};$process.Dispose()}
 [DesktopAudit]::Move($cursor[0],$cursor[1])
}
