<#
.SYNOPSIS
    Installs (or uninstalls) Driftwall for the current user without Inno Setup.

.DESCRIPTION
    Does what the real installer does, minus the wizard: copies Driftwall.exe to
    %LOCALAPPDATA%\Programs\Driftwall, creates Start-menu and desktop shortcuts, registers it in
    Add/Remove Programs, and optionally sets it to start with Windows. No admin rights needed.

    The distributable installer is installer\Driftwall.iss (build with .\build.ps1 -Installer);
    this script exists so a machine without Inno Setup can still get a proper install.

.EXAMPLE
    .\tools\install-local.ps1                     # install with desktop + Start-menu shortcuts
    .\tools\install-local.ps1 -StartWithWindows   # ...and start hidden at sign-in
    .\tools\install-local.ps1 -Uninstall          # remove it (keeps your collections)
    .\tools\install-local.ps1 -Uninstall -RemoveData
#>
[CmdletBinding()]
param(
    [string]$Source = "$PSScriptRoot\..\dist\Driftwall.exe",
    [switch]$Uninstall,
    [switch]$NoDesktopIcon,
    [switch]$StartWithWindows,
    [switch]$Launch,
    # With -Uninstall: also delete %LOCALAPPDATA%\Driftwall (settings, collections, cache).
    [switch]$RemoveData
)

$ErrorActionPreference = 'Stop'

$appName     = 'Driftwall'
$installDir  = Join-Path $env:LOCALAPPDATA "Programs\$appName"
$exePath     = Join-Path $installDir "$appName.exe"
$dataDir     = Join-Path $env:LOCALAPPDATA $appName
$startMenu   = Join-Path ([Environment]::GetFolderPath('Programs')) "$appName.lnk"
$desktop     = Join-Path ([Environment]::GetFolderPath('Desktop')) "$appName.lnk"
$uninstallKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$appName"
$runKey      = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'

function Stop-Driftwall {
    $running = Get-Process $appName -ErrorAction SilentlyContinue
    if ($running) {
        $running | Stop-Process -Force
        Start-Sleep -Milliseconds 600
    }
}

# IShellLinkW rather than WScript.Shell: the scripting object fails with "Value does not fall within
# the expected range" on profile paths containing non-ASCII characters, and a Korean or Japanese
# user name is exactly that. The COM interface marshals every path as UTF-16 and just works.
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

[ComImport, Guid("00021401-0000-0000-C000-000000000046")]
class ShellLinkObject { }

[ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IShellLinkW
{
    void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int cch, IntPtr fd, int flags);
    void GetIDList(out IntPtr pidl);
    void SetIDList(IntPtr pidl);
    void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int cch);
    void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
    void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int cch);
    void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
    void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int cch);
    void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
    void GetHotkey(out short hotkey);
    void SetHotkey(short hotkey);
    void GetShowCmd(out int showCmd);
    void SetShowCmd(int showCmd);
    void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int cch, out int icon);
    void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int icon);
    void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pathRel, int reserved);
    void Resolve(IntPtr hwnd, int flags);
    void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
}

public static class DriftwallShortcut
{
    public static void Create(string linkPath, string target, string description)
    {
        var link = (IShellLinkW)new ShellLinkObject();
        link.SetPath(target);
        link.SetWorkingDirectory(System.IO.Path.GetDirectoryName(target));
        link.SetIconLocation(target, 0);
        link.SetDescription(description);
        ((IPersistFile)link).Save(linkPath, false);
        Marshal.FinalReleaseComObject(link);
    }
}
'@

function New-Shortcut([string]$path, [string]$target) {
    [DriftwallShortcut]::Create($path, $target, 'Driftwall - wallpaper switcher')
}

if ($Uninstall) {
    Write-Host "Uninstalling $appName..." -ForegroundColor Cyan
    Stop-Driftwall

    foreach ($shortcut in @($startMenu, $desktop)) {
        if (Test-Path $shortcut) { Remove-Item $shortcut -Force; Write-Host "  removed $shortcut" }
    }

    if (Get-ItemProperty -Path $runKey -Name $appName -ErrorAction SilentlyContinue) {
        Remove-ItemProperty -Path $runKey -Name $appName
        Write-Host '  removed start-with-Windows entry'
    }

    if (Test-Path $uninstallKey) { Remove-Item $uninstallKey -Recurse -Force; Write-Host '  removed Add/Remove Programs entry' }

    if (Test-Path $installDir) { Remove-Item $installDir -Recurse -Force; Write-Host "  removed $installDir" }

    if ($RemoveData -and (Test-Path $dataDir)) {
        Remove-Item $dataDir -Recurse -Force
        Write-Host "  removed $dataDir (settings, collections, cache)"
    } elseif (Test-Path $dataDir) {
        Write-Host "  kept $dataDir - your collections and settings are still there (use -RemoveData to delete)"
    }

    Write-Host 'Done.' -ForegroundColor Green
    return
}

# ---------------- install ----------------

$Source = (Resolve-Path $Source).Path
if (-not (Test-Path $Source)) { throw "Build first: $Source does not exist. Run .\build.ps1" }

Write-Host "Installing $appName to $installDir" -ForegroundColor Cyan
Stop-Driftwall

New-Item -ItemType Directory -Force -Path $installDir | Out-Null
Copy-Item $Source $exePath -Force
# Keep a copy of this script beside the exe so Add/Remove Programs can uninstall without the repo.
Copy-Item $PSCommandPath (Join-Path $installDir 'uninstall.ps1') -Force

New-Shortcut $startMenu $exePath
Write-Host "  Start menu: $startMenu"

if (-not $NoDesktopIcon) {
    New-Shortcut $desktop $exePath
    Write-Host "  Desktop:    $desktop"
}

$version = (Get-Item $exePath).VersionInfo.ProductVersion
if (-not $version) { $version = '1.0.0' }
$sizeKb = [int]((Get-Item $exePath).Length / 1KB)

New-Item -Path $uninstallKey -Force | Out-Null
Set-ItemProperty -Path $uninstallKey -Name 'DisplayName'     -Value $appName
Set-ItemProperty -Path $uninstallKey -Name 'DisplayVersion'  -Value $version
Set-ItemProperty -Path $uninstallKey -Name 'DisplayIcon'     -Value $exePath
Set-ItemProperty -Path $uninstallKey -Name 'Publisher'       -Value $appName
Set-ItemProperty -Path $uninstallKey -Name 'InstallLocation' -Value $installDir
Set-ItemProperty -Path $uninstallKey -Name 'UninstallString' -Value "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$installDir\uninstall.ps1`" -Uninstall"
Set-ItemProperty -Path $uninstallKey -Name 'NoModify'        -Value 1 -Type DWord
Set-ItemProperty -Path $uninstallKey -Name 'NoRepair'        -Value 1 -Type DWord
Set-ItemProperty -Path $uninstallKey -Name 'EstimatedSize'   -Value $sizeKb -Type DWord
Write-Host '  Registered in Settings > Apps > Installed apps'

if ($StartWithWindows) {
    # Same value the app's own "Start with Windows" switch writes, so they stay in step.
    Set-ItemProperty -Path $runKey -Name $appName -Value "`"$exePath`" --minimized"
    Write-Host '  Will start hidden in the notification area at sign-in'
}

Write-Host ''
Write-Host "Installed $appName $version" -ForegroundColor Green

if ($Launch) { Start-Process $exePath }
