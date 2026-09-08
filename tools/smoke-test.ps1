<#
.SYNOPSIS
    Launches Driftwall, exercises the single-instance activation path, and captures a screenshot.

.DESCRIPTION
    Seeds %LOCALAPPDATA%\Driftwall\settings.json with rotation switched OFF and no
    change-on-startup, so running this never touches the real desktop wallpaper. Then it starts the
    app hidden, launches a second copy to prove the running instance is surfaced rather than
    duplicated, screenshots the window, records the process's memory, and shuts down.
#>
[CmdletBinding()]
param(
    [string]$Exe = "$PSScriptRoot\..\src\Driftwall\bin\Debug\net10.0-windows\Driftwall.exe",
    [string]$ShotDirectory = "$PSScriptRoot\..\artifacts",
    [int]$SettleSeconds = 6
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

# The seeded settings below are for this run only; the real ones come back at the end.
. "$PSScriptRoot\SettingsGuard.ps1"
$guard = Backup-DriftwallSettings
trap { Restore-DriftwallSettings $guard; break }

$dataDir = Join-Path $env:LOCALAPPDATA 'Driftwall'
New-Item -ItemType Directory -Force -Path $dataDir | Out-Null
New-Item -ItemType Directory -Force -Path $ShotDirectory | Out-Null

# Rotation off and no start-up change: this script must never alter the user's desktop.
@'
{
  "schemaVersion": 1,
  "sources": [
    { "providerId": "wallhaven", "enabled": true, "mode": "Top", "timeRange": "month", "weight": 3 },
    { "providerId": "bing", "enabled": true, "mode": "Top", "weight": 1 }
  ],
  "rotationEnabled": false,
  "intervalMinutes": 30,
  "changeOnStartup": false,
  "changeOnShutdown": false,
  "startMinimized": false,
  "showTrayIcon": true,
  "theme": "Dark"
}
'@ | Set-Content -Path (Join-Path $dataDir 'settings.json') -Encoding UTF8

Get-Process Driftwall -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 400

Write-Host "Launching $Exe" -ForegroundColor Cyan
$process = Start-Process $Exe -PassThru
Start-Sleep -Seconds $SettleSeconds

if ($process.HasExited) { throw "Driftwall exited immediately with code $($process.ExitCode)." }

# A second launch must surface the first instance, not start a rival.
Write-Host 'Launching a second copy to test single-instance activation...' -ForegroundColor Cyan
Start-Process $Exe | Out-Null
Start-Sleep -Seconds 3

$running = @(Get-Process Driftwall -ErrorAction SilentlyContinue)
Write-Host "Driftwall processes running: $($running.Count) (expected 1)" -ForegroundColor $(if ($running.Count -eq 1) { 'Green' } else { 'Red' })

function Save-Screenshot([string]$path) {
    $bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $bitmap = New-Object System.Drawing.Bitmap $bounds.Width, $bounds.Height
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, $bounds.Size)
    $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $graphics.Dispose()
    $bitmap.Dispose()
}

$shot = Join-Path $ShotDirectory 'driftwall.png'
Save-Screenshot $shot
Write-Host "Screenshot: $shot" -ForegroundColor Green

$live = Get-Process Driftwall -ErrorAction SilentlyContinue | Select-Object -First 1
if ($live) {
    $ws = [math]::Round($live.WorkingSet64 / 1MB, 1)
    $private = [math]::Round($live.PrivateMemorySize64 / 1MB, 1)
    Write-Host "Memory: working set $ws MB, private $private MB" -ForegroundColor Cyan
}

$log = Join-Path $dataDir 'driftwall.log'
if (Test-Path $log) {
    Write-Host ''
    Write-Host '--- log ---' -ForegroundColor Cyan
    Get-Content $log -Tail 30
}

Restore-DriftwallSettings $guard
