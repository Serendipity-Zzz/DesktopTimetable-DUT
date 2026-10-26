$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class WorkbenchCapture {
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr dc, uint flags);
}
'@
$exe = Join-Path $root '.tools/dotnet/dotnet.exe'
$app = Join-Path $root 'src/DlutTimetable.Desktop/bin/Release/net10.0-windows10.0.19041.0/DlutTimetable.Desktop.dll'
$process = Start-Process -FilePath $exe -ArgumentList @('"' + $app + '"', '--workbench') -WindowStyle Hidden -PassThru
try {
    $window = $null
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    $condition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $process.Id)
    while (!$window -and [DateTime]::UtcNow -lt $deadline) {
        $window = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $condition)
        if (!$window) { Start-Sleep -Milliseconds 100 }
    }
    if (!$window) { throw 'Workbench did not create a window' }
    function Find-Button([string]$name) {
        $match = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, $name)
        $element = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $match)
        if (!$element) { throw "Missing button: $name" }
        return $element
    }
    $demo = Find-Button '载入虚构示例'
    $demo.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $shanghai = [TimeZoneInfo]::ConvertTimeBySystemTimeZoneId([DateTimeOffset]::UtcNow, 'Asia/Shanghai')
    $week = [Math]::Clamp([int][Math]::Floor(($shanghai.Date - [datetime]'2026-08-31').TotalDays / 7) + 1, 1, 18)
    $marker = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, "2026-2027 · 第 $week 教学周   [虚构示例]")
    $loaded = $null
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    while (!$loaded -and [DateTime]::UtcNow -lt $deadline) {
        $loaded = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $marker)
        if (!$loaded) { Start-Sleep -Milliseconds 100 }
    }
    if (!$loaded) { throw 'Synthetic week projection was not displayed' }
    $handle = [IntPtr]$window.Current.NativeWindowHandle
    $bounds = $window.Current.BoundingRectangle
    $bitmap = [System.Drawing.Bitmap]::new([int]$bounds.Width, [int]$bounds.Height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $dc = $graphics.GetHdc()
    try { $captured = [WorkbenchCapture]::PrintWindow($handle, $dc, 2) }
    finally { $graphics.ReleaseHdc($dc); $graphics.Dispose() }
    try { if ($captured) { $bitmap.Save((Join-Path $root 'docs/validation/workbench.png')) } }
    finally { $bitmap.Dispose() }
    $dpi = [WorkbenchCapture]::GetDpiForWindow($handle)
    $quit = Find-Button '退出程序'
    $quit.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    if (!$process.WaitForExit(10000)) { throw 'Explicit exit did not end the workbench' }
    [pscustomobject]@{ pid=$process.Id; demoDisplayed=$true; windowDpi=$dpi; screenshot=$captured; exitCode=$process.ExitCode } | ConvertTo-Json
} finally {
    # Only the exact process launched by this script is eligible for cleanup.
    if (!$process.HasExited) { $process.Kill(); $process.WaitForExit() }
    $process.Dispose()
}
