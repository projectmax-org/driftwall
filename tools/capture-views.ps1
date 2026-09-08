<#
.SYNOPSIS
    Screenshots every tab of the Driftwall window by driving the navigation with UI Automation.

.DESCRIPTION
    Rotation is left off, so this never changes the desktop wallpaper. UI Automation is used rather
    than synthetic clicks at fixed coordinates so the script keeps working when the layout moves.
#>
[CmdletBinding()]
param(
    [string]$Exe = "$PSScriptRoot\..\dist\Driftwall.exe",
    [string]$OutputDirectory = "$PSScriptRoot\..\artifacts",
    [string[]]$Sections = @('Browse', 'Collections', 'Displays', 'Settings')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms, System.Drawing

# The seeded settings below are for this run only; the real ones come back at the end.
. "$PSScriptRoot\SettingsGuard.ps1"
$guard = Backup-DriftwallSettings
trap { Restore-DriftwallSettings $guard; break }

$dataDir = Join-Path $env:LOCALAPPDATA 'Driftwall'
New-Item -ItemType Directory -Force -Path $dataDir | Out-Null
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

@'
{
  "schemaVersion": 1,
  "sources": [
    { "providerId": "wallhaven", "enabled": true, "mode": "Top", "timeRange": "month", "weight": 3 },
    { "providerId": "bing", "enabled": true, "mode": "Top", "weight": 1 },
    { "providerId": "local", "enabled": false, "mode": "Top", "weight": 1, "options": {} }
  ],
  "rotationEnabled": false,
  "changeOnStartup": false,
  "startMinimized": false,
  "windowWidth": 1240,
  "windowHeight": 820
}
'@ | Set-Content -Path (Join-Path $dataDir 'settings.json') -Encoding UTF8

Get-Process Driftwall -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

$process = Start-Process $Exe -PassThru
Start-Sleep -Seconds 12
if ($process.HasExited) { throw "Driftwall exited with code $($process.ExitCode)" }

$root = [System.Windows.Automation.AutomationElement]::RootElement
$condition = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::NameProperty, 'Driftwall')

$window = $null
foreach ($attempt in 1..12) {
    $window = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $condition)
    if ($window) { break }
    Start-Sleep -Milliseconds 700
}
if (-not $window) { throw 'Could not find the Driftwall window through UI Automation.' }

function Save-WindowShot([string]$path, $element) {
    $r = $element.Current.BoundingRectangle
    # Include a little margin so the drop shadow and rounded corners are not clipped.
    $x = [int]([math]::Max(0, $r.X - 12)); $y = [int]([math]::Max(0, $r.Y - 12))
    $w = [int]($r.Width + 24); $h = [int]($r.Height + 24)

    $bitmap = New-Object System.Drawing.Bitmap $w, $h
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.CopyFromScreen($x, $y, 0, 0, (New-Object System.Drawing.Size($w, $h)))
    $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $graphics.Dispose(); $bitmap.Dispose()
}

foreach ($section in $Sections) {
    $navCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $section)
    $nav = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $navCondition)

    if (-not $nav) {
        Write-Host "  could not find the '$section' nav item" -ForegroundColor Yellow
        continue
    }

    try {
        $pattern = $nav.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
        $pattern.Select()
    } catch {
        try {
            $invoke = $nav.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
            $invoke.Invoke()
        } catch {
            Write-Host "  '$section' is not selectable: $($_.Exception.Message)" -ForegroundColor Yellow
            continue
        }
    }

    Start-Sleep -Seconds 3
    $shot = Join-Path $OutputDirectory ("view-" + $section.ToLower() + '.png')
    Save-WindowShot $shot $window
    Write-Host "  $section -> $shot" -ForegroundColor Green
}

Restore-DriftwallSettings $guard
Write-Host 'Done.' -ForegroundColor Cyan
