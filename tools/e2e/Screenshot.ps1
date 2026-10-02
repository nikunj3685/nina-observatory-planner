<#
Screenshot of one process's main window, or with -Title of its window whose title contains that text (a dialog).
First tries PrintWindow (works while covered). NINA renders WPF with hardware acceleration, which PrintWindow often
returns as a blank image; then the window is brought on top for about a second and the screen area is captured.
#>
param([Parameter(Mandatory)] [int] $ProcessId, [Parameter(Mandatory)] [string] $Path, [string] $Title)
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing
if (-not ("OpWin4" -as [type])) {
    Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class OpWin4 {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int w, int h, uint flags);
    public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder s, int n);
    public static IntPtr Find(uint pid, string title) {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, l) => {
            uint p; GetWindowThreadProcessId(h, out p);
            if (p != pid || !IsWindowVisible(h)) { return true; }
            var sb = new System.Text.StringBuilder(512); GetWindowText(h, sb, 512);
            if (sb.ToString().IndexOf(title, StringComparison.OrdinalIgnoreCase) >= 0) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
"@
}
# measure in physical pixels, or windows on a scaled display are captured cropped
[void][OpWin4]::SetProcessDPIAware()
$p = Get-Process -Id $ProcessId
if ($Title) {
    $h = [OpWin4]::Find([uint32]$ProcessId, $Title)
    if ($h -eq [IntPtr]::Zero) { throw "No window titled '$Title' in process $ProcessId" }
} else {
    $h = $p.MainWindowHandle
    if ($h -eq [IntPtr]::Zero) { throw "Process $ProcessId has no main window yet" }
    [void][OpWin4]::ShowWindow($h, 9)
    [void][OpWin4]::SetWindowPos($h, [IntPtr]::Zero, 10, 10, 1900, 1040, 0x0004)
}
Start-Sleep -Milliseconds 800
$r = New-Object OpWin4+RECT
[void][OpWin4]::GetWindowRect($h, [ref]$r)
$w = $r.Right - $r.Left; $hgt = $r.Bottom - $r.Top

function Test-Blank($bmp) {
    $first = $bmp.GetPixel(10, 10)
    foreach ($pt in @(@(200, 150), @(800, 500), @(1300, 800), @(400, 700), @(1000, 300))) {
        if ($bmp.GetPixel([Math]::Min($pt[0], $bmp.Width - 1), [Math]::Min($pt[1], $bmp.Height - 1)) -ne $first) { return $false }
    }
    return $true
}

$bmp = New-Object System.Drawing.Bitmap $w, $hgt
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc(); [void][OpWin4]::PrintWindow($h, $hdc, 2); $g.ReleaseHdc($hdc); $g.Dispose()

if (Test-Blank $bmp) {
    $bmp.Dispose()
    # topmost for a moment, capture the screen pixels, then back to normal z-order (no focus change)
    [void][OpWin4]::SetWindowPos($h, [IntPtr](-1), 0, 0, 0, 0, 0x0001 -bor 0x0002 -bor 0x0040 -bor 0x0010)
    Start-Sleep -Milliseconds 900
    $bmp = New-Object System.Drawing.Bitmap $w, $hgt
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.Left, $r.Top, 0, 0, (New-Object System.Drawing.Size $w, $hgt))
    $g.Dispose()
    [void][OpWin4]::SetWindowPos($h, [IntPtr](-2), 0, 0, 0, 0, 0x0001 -bor 0x0002 -bor 0x0040 -bor 0x0010)
}
$bmp.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Output $Path
