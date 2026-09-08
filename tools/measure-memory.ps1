<#
.SYNOPSIS
    Measures Driftwall's memory in the two states that matter: window open, and closed to the tray.

.DESCRIPTION
    The second number is the one that counts. A wallpaper switcher spends almost all of its life as a
    tray icon, so what it holds while nobody is looking at it is what a user actually pays for.
    Rotation is left off so the measurement never touches the desktop wallpaper.
#>
[CmdletBinding()]
param(
    [string]$Exe = "$PSScriptRoot\..\dist\Driftwall.exe",
    [int]$OpenSeconds = 14,
    [int]$TrimSeconds = 10,

    # Start hidden in the tray so the browse grid is never built: the true idle footprint.
    [switch]$StartHidden
)

$ErrorActionPreference = 'Stop'

# The seeded settings below are for this run only; the real ones come back at the end.
. "$PSScriptRoot\SettingsGuard.ps1"
$guard = Backup-DriftwallSettings
trap { Restore-DriftwallSettings $guard; break }

$dataDir = Join-Path $env:LOCALAPPDATA 'Driftwall'
New-Item -ItemType Directory -Force -Path $dataDir | Out-Null

@"
{
  "schemaVersion": 1,
  "sources": [
    { "providerId": "wallhaven", "enabled": true, "mode": "Top", "timeRange": "month", "weight": 3 }
  ],
  "rotationEnabled": false,
  "changeOnStartup": false,
  "startMinimized": $($StartHidden.IsPresent.ToString().ToLower()),
  "closeToTray": true,
  "showTrayIcon": true,
  "aggressiveMemoryTrim": true
}
"@ | Set-Content -Path (Join-Path $dataDir 'settings.json') -Encoding UTF8

Get-Process Driftwall -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

function Report([string]$label, $p) {
    $p.Refresh()
    [PSCustomObject]@{
        State       = $label
        WorkingSetMB = [math]::Round($p.WorkingSet64 / 1MB, 1)
        PrivateMB    = [math]::Round($p.PrivateMemorySize64 / 1MB, 1)
        Threads      = $p.Threads.Count
        Handles      = $p.HandleCount
        CpuSeconds   = [math]::Round($p.TotalProcessorTime.TotalSeconds, 2)
    }
}

$process = Start-Process $Exe -PassThru
Start-Sleep -Seconds $OpenSeconds
if ($process.HasExited) { throw "Driftwall exited with code $($process.ExitCode)" }

$results = @()
$results += Report 'window open, grid loaded' $process

# Escape closes the window to the notification area, which is what triggers the memory trim.
$shell = New-Object -ComObject wscript.shell
[void]$shell.AppActivate($process.Id)
Start-Sleep -Milliseconds 700
$shell.SendKeys('{ESC}')

Start-Sleep -Seconds $TrimSeconds
$results += Report 'closed to tray' $process

$cpuBefore = $process.TotalProcessorTime
Start-Sleep -Seconds 10
$process.Refresh()
$idleCpuMs = ($process.TotalProcessorTime - $cpuBefore).TotalMilliseconds

$results | Format-Table -AutoSize
Write-Host ("CPU used while idle in the tray for 10s: {0} ms" -f [math]::Round($idleCpuMs, 1)) -ForegroundColor Cyan

Restore-DriftwallSettings $guard
