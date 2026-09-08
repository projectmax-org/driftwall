<#
.SYNOPSIS
    Keeps the test scripts in this folder from costing you your real Driftwall configuration.

.DESCRIPTION
    Every script here seeds %LOCALAPPDATA%\Driftwall\settings.json with a throwaway configuration
    (rotation off, so the desktop is never touched). Before this file existed, that seeding silently
    overwrote the sources and API keys of whoever ran the script on their own machine.

    Dot-source it, call Backup-DriftwallSettings before seeding, and Restore-DriftwallSettings when
    the script ends, however it ends:

        . "$PSScriptRoot\SettingsGuard.ps1"
        $guard = Backup-DriftwallSettings
        trap { Restore-DriftwallSettings $guard; break }
        ...
        Restore-DriftwallSettings $guard

    Restore also stops the copy the script launched (a running app would write the seeded settings
    straight back) and, if Driftwall was running before the script started, starts it again hidden.
#>

function Backup-DriftwallSettings {
    $dataDir = Join-Path $env:LOCALAPPDATA 'Driftwall'
    $file = Join-Path $dataDir 'settings.json'

    $guard = @{
        File     = $file
        Backup   = $null
        Relaunch = (Get-Process Driftwall -ErrorAction SilentlyContinue | Select-Object -First 1).Path
    }

    if (Test-Path $file) {
        $guard.Backup = $file + '.before-test'
        Copy-Item $file $guard.Backup -Force
    }

    return $guard
}

function Restore-DriftwallSettings($guard) {
    if (-not $guard) { return }

    Get-Process Driftwall -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 800

    if ($guard.Backup -and (Test-Path $guard.Backup)) {
        Move-Item $guard.Backup $guard.File -Force
        Write-Host 'Your own settings.json has been put back.' -ForegroundColor DarkGray
    } elseif (Test-Path $guard.File) {
        # There was no configuration before; leaving the seeded one behind would start the real app
        # with rotation switched off.
        Remove-Item $guard.File -Force
    }

    if ($guard.Relaunch -and (Test-Path $guard.Relaunch)) {
        Start-Process $guard.Relaunch -ArgumentList '--minimized'
        Write-Host "Driftwall started again from $($guard.Relaunch)." -ForegroundColor DarkGray
    }
}
