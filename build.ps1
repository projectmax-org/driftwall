<#
.SYNOPSIS
    Builds Driftwall and publishes a single self-contained Driftwall.exe.

.DESCRIPTION
    Self-contained by default so the .exe runs on a clean Windows machine with no .NET runtime
    installed, which is what a Steam or direct download needs. Use -FrameworkDependent for a small
    build when you know the target already has the .NET Desktop Runtime.

    -Installer then wraps the result in dist\DriftwallSetup-<version>.exe through
    installer\build-installer.ps1, which fetches Inno Setup by itself when it is not installed.

    If a copy of Driftwall is running from the output folder it is stopped so the file can be
    replaced, and started again (hidden, as at sign-in) once the build succeeds.

.EXAMPLE
    .\build.ps1
    .\build.ps1 -Run
    .\build.ps1 -Installer
    .\build.ps1 -FrameworkDependent
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64',

    [string]$Output = "$PSScriptRoot\dist",

    # Smaller output, but the target machine must already have the .NET Desktop Runtime.
    [switch]$FrameworkDependent,

    # Compress the bundle. Roughly halves the download, but costs RAM for the life of the process:
    # compressed assemblies are decompressed into memory at start-up instead of being mapped from
    # disk. Off by default because idle memory is the thing this app is judged on.
    [switch]$Compress,

    # Regenerate Assets\app.ico from tools\IconGen before building.
    [switch]$Icon,

    # Also build dist\DriftwallSetup-<version>.exe (see installer\build-installer.ps1).
    [switch]$Installer,

    # With -Installer: sign the exe, the wizard and the uninstaller with this code-signing
    # certificate (thumbprint of one in the current user's store, or a .pfx file). Without a
    # certificate Windows labels the downloads "Unknown publisher". DRIFTWALL_SIGN_THUMBPRINT and
    # DRIFTWALL_SIGN_PFX in the environment work too.
    [string]$CertificateThumbprint,
    [string]$PfxPath,
    [string]$PfxPassword,

    # Launch the published executable when the build succeeds.
    [switch]$Run
)

$ErrorActionPreference = 'Stop'
$project = "$PSScriptRoot\src\Driftwall\Driftwall.csproj"

function Resolve-Dotnet {
    $onPath = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }

    # A per-user install from dotnet-install.ps1 is not on PATH unless the user put it there.
    $found = @(
        "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe",
        "$env:ProgramFiles\dotnet\dotnet.exe",
        "$env:USERPROFILE\.dotnet\dotnet.exe"
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
    if ($found) { return $found }

    throw @'
The .NET 10 SDK was not found. Install it with either of:

  winget install Microsoft.DotNet.SDK.10

  Invoke-WebRequest https://dot.net/v1/dotnet-install.ps1 -OutFile dotnet-install.ps1
  .\dotnet-install.ps1 -Channel 10.0

The second is per-user, needs no admin rights, and puts the SDK in %LOCALAPPDATA%\Microsoft\dotnet,
where this script finds it without any PATH changes.
'@
}

$dotnet = Resolve-Dotnet
$outputFull = [IO.Path]::GetFullPath($Output)

Write-Host "Driftwall build" -ForegroundColor Cyan
Write-Host "  configuration : $Configuration"
Write-Host "  runtime       : $Runtime"
Write-Host "  self-contained: $(-not $FrameworkDependent)"
Write-Host "  output        : $outputFull"
Write-Host "  dotnet        : $dotnet"
Write-Host ''

if ($Icon) {
    Write-Host 'Regenerating the application icon and the installer artwork...' -ForegroundColor Cyan
    & $dotnet run --project "$PSScriptRoot\tools\IconGen\IconGen.csproj" -c Release -- "$PSScriptRoot\src\Driftwall\Assets"
    if ($LASTEXITCODE -ne 0) { throw 'Icon generation failed.' }
    & $dotnet run --project "$PSScriptRoot\tools\IconGen\IconGen.csproj" -c Release -- --wizard "$PSScriptRoot\installer\art"
    if ($LASTEXITCODE -ne 0) { throw 'Installer artwork generation failed.' }
}

# Windows will not let a running executable be deleted or overwritten. If the copy in the output
# folder is the one on the desktop right now (it is, when "start with Windows" points at dist),
# stop it for the duration of the build and bring it back afterwards.
$relaunchHidden = $false
$running = Get-Process Driftwall -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and ([IO.Path]::GetDirectoryName($_.Path).TrimEnd('\') -ieq $outputFull.TrimEnd('\')) }
if ($running) {
    Write-Host "Stopping the copy of Driftwall running from $outputFull so it can be replaced..." -ForegroundColor Yellow
    $running | Stop-Process -Force
    Start-Sleep -Milliseconds 800
    $relaunchHidden = -not $Run
}

$exe = Join-Path $Output 'Driftwall.exe'
$relaunched = $false

# Everything from here on runs under a finally block so a failed publish does not leave the
# desktop without the copy this script just stopped.
try {

if (Test-Path $Output) { Remove-Item $Output -Recurse -Force }

$arguments = @(
    'publish', $project,
    '-c', $Configuration,
    '-r', $Runtime,
    '-o', $Output,
    '--nologo',
    "-p:PublishSingleFile=$(-not $FrameworkDependent)",
    "-p:SelfContained=$(-not $FrameworkDependent)",
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    "-p:EnableCompressionInSingleFile=$([bool]$Compress)",
    '-p:PublishReadyToRun=true',
    '-p:DebugType=none',
    '-p:SatelliteResourceLanguages=en'
)

Write-Host 'Publishing...' -ForegroundColor Cyan
& $dotnet @arguments
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

if (-not (Test-Path $exe)) { throw "Expected $exe but it was not produced." }

$sizeMb = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host ''
Write-Host "Built $exe ($sizeMb MB)" -ForegroundColor Green

# A self-contained single-file build should be one executable and nothing else worth shipping.
$extras = Get-ChildItem $Output -File | Where-Object { $_.Name -ne 'Driftwall.exe' }
if ($extras) {
    Write-Host 'Additional files in the output folder:' -ForegroundColor Yellow
    $extras | ForEach-Object { Write-Host "  $($_.Name)" }
}

if ($Installer) {
    # The version comes from the csproj so the installer never drifts from the exe inside it.
    $version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
    if (-not $version) { $version = '1.0.0' }

    $signing = @{}
    if ($CertificateThumbprint) { $signing.CertificateThumbprint = $CertificateThumbprint }
    if ($PfxPath) { $signing.PfxPath = $PfxPath }
    if ($PfxPassword) { $signing.PfxPassword = $PfxPassword }

    & "$PSScriptRoot\installer\build-installer.ps1" -Source $exe -Output $Output -Version $version @signing
}

if ($Run) {
    Write-Host 'Launching...' -ForegroundColor Cyan
    Start-Process $exe
    $relaunched = $true
} elseif ($relaunchHidden) {
    Write-Host 'Starting Driftwall again in the notification area...' -ForegroundColor Cyan
    Start-Process $exe -ArgumentList '--minimized'
    $relaunched = $true
}

} finally {
    if ($relaunchHidden -and -not $relaunched) {
        if (Test-Path $exe) {
            Write-Host 'The build did not finish; starting the copy of Driftwall that was running before.' -ForegroundColor Yellow
            Start-Process $exe -ArgumentList '--minimized'
        } else {
            Write-Host "The build did not finish and $exe is gone. Start Driftwall again once it is rebuilt." -ForegroundColor Yellow
        }
    }
}
