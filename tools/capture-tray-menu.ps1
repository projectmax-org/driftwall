<#
.SYNOPSIS
    Right-clicks the Driftwall notification-area icon and screenshots the menu it opens.

.DESCRIPTION
    Finds the tray icon through UI Automation (searching both the visible tray and the overflow
    flyout), then sends a real right-click at its location, because Shell_NotifyIcon menus are driven
    by mouse messages rather than by an automation pattern.
#>
[CmdletBinding()]
param(
    [string]$Exe = "$PSScriptRoot\..\dist\Driftwall.exe",
    [string]$OutputDirectory = "$PSScriptRoot\..\artifacts"
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms, System.Drawing

# The seeded settings below are for this run only; the real ones come back at the end.
. "$PSScriptRoot\SettingsGuard.ps1"
$guard = Backup-DriftwallSettings
trap { Restore-DriftwallSettings $guard; break }

Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class Mouse {
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e);
    public const uint RIGHTDOWN = 0x0008, RIGHTUP = 0x0010;
    public static void RightClick(int x, int y) {
        SetCursorPos(x, y);
        System.Threading.Thread.Sleep(250);
        mouse_event(RIGHTDOWN, 0, 0, 0, IntPtr.Zero);
        System.Threading.Thread.Sleep(60);
        mouse_event(RIGHTUP, 0, 0, 0, IntPtr.Zero);
    }
}
'@

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

$dataDir = Join-Path $env:LOCALAPPDATA 'Driftwall'
New-Item -ItemType Directory -Force -Path $dataDir | Out-Null
@'
{
  "schemaVersion": 1,
  "sources": [ { "providerId": "wallhaven", "enabled": true, "mode": "Top", "weight": 3 } ],
  "rotationEnabled": false,
  "changeOnStartup": false,
  "startMinimized": true,
  "showTrayIcon": true
}
'@ | Set-Content -Path (Join-Path $dataDir 'settings.json') -Encoding UTF8

Get-Process Driftwall -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

$process = Start-Process $Exe -PassThru
Start-Sleep -Seconds 8

$root = [System.Windows.Automation.AutomationElement]::RootElement
$nameCondition = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::NameProperty, 'Driftwall')

function Find-ByPrefix {
    # The tray button's automation Name is the icon's tooltip, which carries the current photo and
    # the countdown after it, so an exact-name match will not find it.
    $buttons = $root.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Button)))

    foreach ($b in $buttons) {
        if ($b.Current.Name -like 'Driftwall*') { return $b }
    }
    return $null
}

function Find-TrayIcon {
    $hit = Find-ByPrefix
    if ($hit) { return $hit }

    # Not pinned to the visible tray: open the overflow flyout and look again.
    $chevronCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'SystemTrayIcon')
    $chevrons = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $chevronCondition)
    foreach ($c in $chevrons) {
        try {
            $c.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
            Start-Sleep -Milliseconds 900
            $hit = Find-ByPrefix
            if ($hit) { return $hit }
        } catch { }
    }
    return $null
}

$icon = $null
foreach ($attempt in 1..6) {
    $icon = Find-TrayIcon
    if ($icon) { break }
    Start-Sleep -Milliseconds 800
}

if (-not $icon) {
    throw 'Could not locate the tray icon through UI Automation.'
}

$r = $icon.Current.BoundingRectangle
Write-Host ("Tray icon found at {0},{1} ({2}x{3})" -f [int]$r.X, [int]$r.Y, [int]$r.Width, [int]$r.Height) -ForegroundColor Green

[Mouse]::RightClick([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2))
Start-Sleep -Seconds 2

$bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$bitmap = New-Object System.Drawing.Bitmap $bounds.Width, $bounds.Height
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, $bounds.Size)
$shot = Join-Path $OutputDirectory 'tray-menu.png'
$bitmap.Save($shot, [System.Drawing.Imaging.ImageFormat]::Png)
$graphics.Dispose(); $bitmap.Dispose()

Write-Host "Screenshot: $shot" -ForegroundColor Green
Restore-DriftwallSettings $guard
