<#
.SYNOPSIS
    Builds dist\DriftwallSetup-<version>.exe, the wizard people download and run.

.DESCRIPTION
    The installer helper. It needs Inno Setup 6 and takes care of that itself:

      1. An existing Inno Setup 6 install is used when there is one.
      2. Otherwise the official installer is downloaded from the jrsoftware GitHub release and unpacked
         in portable mode into tools\.inno - no admin rights, no registry entries, nothing outside the
         repo. Delete that folder to undo it.

    The wizard artwork (installer\art) is regenerated from tools\IconGen when it is missing, then
    installer\Driftwall.iss is compiled against dist\Driftwall.exe and the result is verified.

    CODE SIGNING. Windows shows "Unknown publisher" for any executable that carries no Authenticode
    signature from a certificate authority. Give this script a certificate and it signs Driftwall.exe,
    the setup wizard and the uninstaller, so all three show the certificate's name as their verified
    publisher:

      -CertificateThumbprint <sha1>   a code-signing certificate in the current user's certificate
                                      store (Certificates > Personal). Recommended: no secrets on the
                                      command line.
      -PfxPath <file> [-PfxPassword]  a .pfx file instead. The password is visible to other processes
                                      while signing runs; prefer the store.

    Both can also come from the environment (DRIFTWALL_SIGN_THUMBPRINT, DRIFTWALL_SIGN_PFX,
    DRIFTWALL_SIGN_PFX_PASSWORD), which is what a build machine would use. Without either, nothing is
    signed and the build is otherwise identical. signtool.exe comes from an installed Windows SDK, or
    is fetched from the Microsoft.Windows.SDK.BuildTools NuGet package into tools\.inno.

    .\build.ps1 -Installer runs this after publishing. Run it directly to package an exe you already
    have, or on a machine without the .NET SDK.

.EXAMPLE
    .\installer\build-installer.ps1
    .\installer\build-installer.ps1 -CertificateThumbprint 3f2a...  # signed release
    .\installer\build-installer.ps1 -Source C:\builds\Driftwall.exe -Version 1.2.0
    .\installer\build-installer.ps1 -ToolsOnly     # just make sure Inno Setup is available
#>
[CmdletBinding()]
param(
    # The published, self-contained executable to wrap.
    [string]$Source = "$PSScriptRoot\..\dist\Driftwall.exe",

    # Where DriftwallSetup-<version>.exe is written.
    [string]$Output = "$PSScriptRoot\..\dist",

    # Defaults to <Version> in the csproj so the installer never drifts from the exe inside it.
    [string]$Version,

    # Inno Setup release to fetch when none is installed. The script needs 6.7 or later (the wizard
    # background-colour directives arrived in 6.7.0); an older installed copy is skipped.
    [string]$InnoSetupVersion = '6.7.3',

    # --- code signing (optional) ---
    [string]$CertificateThumbprint = $env:DRIFTWALL_SIGN_THUMBPRINT,
    [string]$PfxPath = $env:DRIFTWALL_SIGN_PFX,
    [string]$PfxPassword = $env:DRIFTWALL_SIGN_PFX_PASSWORD,
    # RFC 3161 timestamp server, so signatures stay valid after the certificate expires. Empty skips it.
    [string]$TimestampUrl = 'http://timestamp.digicert.com',
    # Explicit signtool.exe, if the auto-detection should not be used.
    [string]$SignToolPath = $env:DRIFTWALL_SIGNTOOL,

    # Regenerate installer\art even if it already exists.
    [switch]$RegenerateArt,

    # The wizard follows the Windows light/dark setting. Force one to preview or screenshot it.
    [ValidateSet('dynamic', 'light', 'dark')]
    [string]$Appearance = 'dynamic',

    # Only make sure Inno Setup is available; do not compile anything.
    [switch]$ToolsOnly
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path "$PSScriptRoot\..").Path
$toolsDir = Join-Path $repo 'tools\.inno'
$portableDir = Join-Path $toolsDir 'InnoSetup6'
$artDir = Join-Path $PSScriptRoot 'art'

function Get-Downloader {
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
}

$requiredInnoVersion = [version]'6.7'

function Get-InnoVersion([string]$iscc) {
    # ISCC.exe carries no version resource, but the release notes installed beside it start with
    # the newest version. Unknown is treated as acceptable; the compiler will say if it is not.
    $notes = Join-Path (Split-Path $iscc -Parent) 'whatsnew.htm'
    if (-not (Test-Path $notes)) { return $null }

    $text = (Get-Content $notes -Raw) -replace '<[^>]+>', ''
    $match = [regex]::Match($text, '(?m)^\s*(\d+\.\d+(?:\.\d+)?) \(\d{4}-\d{2}-\d{2}\)')
    if ($match.Success) { return [version]$match.Groups[1].Value }
    return $null
}

function Find-InnoSetup {
    # DRIFTWALL_ISCC lets a build machine point at its own copy without touching this script.
    $candidates = @(
        $env:DRIFTWALL_ISCC,
        (Join-Path $portableDir 'ISCC.exe'),
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    ) | Where-Object { $_ -and (Test-Path $_) }

    foreach ($candidate in $candidates) {
        $found = Get-InnoVersion $candidate
        if ($found -and $found -lt $requiredInnoVersion) {
            Write-Host "Skipping $candidate (Inno Setup $found; this script needs $requiredInnoVersion or later)" -ForegroundColor Yellow
            continue
        }
        return $candidate
    }

    return $null
}

function Install-InnoSetup {
    New-Item -ItemType Directory -Force -Path $toolsDir | Out-Null
    $setup = Join-Path $toolsDir "innosetup-$InnoSetupVersion.exe"

    if (-not (Test-Path $setup)) {
        $tag = 'is-' + ($InnoSetupVersion -replace '\.', '_')
        $urls = @(
            "https://github.com/jrsoftware/issrc/releases/download/$tag/innosetup-$InnoSetupVersion.exe",
            # Whatever jrsoftware.org currently calls stable, if the pinned release has moved.
            'https://jrsoftware.org/download.php/is.exe'
        )

        Get-Downloader
        $downloaded = $false
        foreach ($url in $urls) {
            Write-Host "Downloading Inno Setup from $url" -ForegroundColor Cyan
            try {
                Invoke-WebRequest -Uri $url -OutFile $setup -UseBasicParsing
                $downloaded = $true
                break
            } catch {
                Write-Host "  failed: $($_.Exception.Message)" -ForegroundColor Yellow
            }
        }

        if (-not $downloaded) {
            throw "Could not download Inno Setup. Install it yourself with:  winget install JRSoftware.InnoSetup"
        }
    }

    # Portable mode is Inno Setup's own switch: files only, no Start menu entries, no uninstaller,
    # nothing in the registry. /CURRENTUSER keeps it clear of UAC.
    Write-Host "Unpacking Inno Setup into $portableDir" -ForegroundColor Cyan
    $process = Start-Process -FilePath $setup -Wait -PassThru -ArgumentList @(
        '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CURRENTUSER', '/PORTABLE=1', "/DIR=`"$portableDir`""
    )

    $iscc = Join-Path $portableDir 'ISCC.exe'
    if ($process.ExitCode -ne 0 -or -not (Test-Path $iscc)) {
        throw "Inno Setup did not unpack (exit code $($process.ExitCode))."
    }

    return $iscc
}

function Find-SignTool {
    if ($SignToolPath) {
        if (-not (Test-Path $SignToolPath)) { throw "signtool not found at $SignToolPath" }
        # Inno runs the sign tool from its own folder, so a relative path would not survive.
        return (Resolve-Path $SignToolPath).Path
    }

    # An installed Windows SDK, newest version first.
    $kits = "${env:ProgramFiles(x86)}\Windows Kits\10\bin"
    if (Test-Path $kits) {
        $found = Get-ChildItem $kits -Directory |
            Where-Object { $_.Name -match '^10\.' } |
            Sort-Object { [version]$_.Name } -Descending |
            ForEach-Object { Join-Path $_.FullName 'x64\signtool.exe' } |
            Where-Object { Test-Path $_ } |
            Select-Object -First 1
        if ($found) { return $found }
    }

    # Otherwise the same tool from Microsoft's NuGet package, unpacked next to Inno Setup.
    $local = Get-ChildItem (Join-Path $toolsDir 'signtool') -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\x64\\' } | Select-Object -First 1
    if ($local) { return $local.FullName }

    $package = Join-Path $toolsDir 'Microsoft.Windows.SDK.BuildTools.zip'
    Write-Host 'Downloading signtool.exe (Microsoft.Windows.SDK.BuildTools from nuget.org)...' -ForegroundColor Cyan
    Get-Downloader
    Invoke-WebRequest -Uri 'https://www.nuget.org/api/v2/package/Microsoft.Windows.SDK.BuildTools' -OutFile $package -UseBasicParsing
    $extracted = Join-Path $toolsDir 'signtool'
    Expand-Archive -Path $package -DestinationPath $extracted -Force
    Remove-Item $package -Force

    $local = Get-ChildItem $extracted -Recurse -Filter signtool.exe | Where-Object { $_.FullName -match '\\x64\\' } | Select-Object -First 1
    if (-not $local) { throw 'The BuildTools package did not contain an x64 signtool.exe.' }
    return $local.FullName
}

# The signtool arguments shared by every file we sign: SHA-256 digest, RFC 3161 timestamp, and the
# product name and URL Windows shows in its security prompts.
function Get-SignArguments {
    $arguments = @('sign', '/fd', 'SHA256')
    if ($TimestampUrl) { $arguments += @('/tr', $TimestampUrl, '/td', 'SHA256') }
    if ($CertificateThumbprint) {
        $arguments += @('/sha1', $CertificateThumbprint)
    } else {
        $arguments += @('/f', $PfxPath)
        if ($PfxPassword) { $arguments += @('/p', $PfxPassword) }
    }
    $arguments += @('/d', 'Driftwall', '/du', 'https://projectmax.app/driftwall')
    return $arguments
}

# One token of the sign command as Inno's compiler wants it: $ is its escape character and quotes
# are written as $q, so a path (or a password) containing either still arrives intact.
function ConvertTo-InnoArgument([string]$value) {
    $escaped = $value.Replace('$', '$$').Replace('"', '$q')
    if ($escaped -match '\s') { return "`$q$escaped`$q" }
    return $escaped
}

function Show-Signature([string]$file) {
    $signature = Get-AuthenticodeSignature $file
    $who = if ($signature.SignerCertificate) { $signature.SignerCertificate.Subject } else { '(none)' }
    Write-Host ("  {0,-28} {1,-14} {2}" -f (Split-Path $file -Leaf), $signature.Status, $who)
}

$iscc = Find-InnoSetup
if (-not $iscc) { $iscc = Install-InnoSetup }
Write-Host "Inno Setup: $iscc"

if ($ToolsOnly) { return }

# ---------------- inputs ----------------

if (-not (Test-Path $Source)) {
    throw "Nothing to package: $Source does not exist. Build it first with .\build.ps1"
}
$Source = (Resolve-Path $Source).Path

if (-not $Version) {
    $project = Join-Path $repo 'src\Driftwall\Driftwall.csproj'
    $Version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
    if (-not $Version) { $Version = '1.0.0' }
}

New-Item -ItemType Directory -Force -Path $Output | Out-Null
$Output = (Resolve-Path $Output).Path

$signing = [bool]($CertificateThumbprint -or $PfxPath)
if ($PfxPath) {
    if (-not (Test-Path $PfxPath)) { throw "Certificate file not found: $PfxPath" }
    $PfxPath = (Resolve-Path $PfxPath).Path
}

# Windows version resources hold four numbers, so a pre-release tag such as 1.1.0-beta is dropped
# there while the full string stays in AppVersion for the wizard and the Apps list.
$numericVersion = ($Version -split '[-+]')[0]

# ---------------- wizard artwork ----------------

# The welcome banner and header mark are drawn by the same generator as the app icon, so the
# installer looks like the app. They are checked in; this only regenerates them when asked or when
# a fresh clone is missing them.
if ($RegenerateArt -or -not (Test-Path (Join-Path $artDir 'wizard-mark-100.png'))) {
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    $dotnetPath = if ($dotnet) { $dotnet.Source } else { "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" }
    if (-not (Test-Path $dotnetPath)) {
        throw "installer\art is missing and the .NET SDK is not available to regenerate it. On a machine with the SDK run .\build.ps1 -Icon (or .\installer\build-installer.ps1 -RegenerateArt) and commit installer\art."
    }

    Write-Host 'Drawing the wizard artwork...' -ForegroundColor Cyan
    # Only "--" separates dotnet's own options from the generator's; anything dotnet run does not
    # recognise before it is handed to the program as an argument.
    & $dotnetPath run --project (Join-Path $repo 'tools\IconGen\IconGen.csproj') -c Release -- --wizard $artDir
    if ($LASTEXITCODE -ne 0) { throw 'Wizard artwork generation failed.' }
}

# ---------------- sign the application ----------------

$signTool = $null
if ($signing) {
    $signTool = Find-SignTool
    Write-Host "signtool:   $signTool"
    Write-Host "Signing $Source" -ForegroundColor Cyan
    # Timestamp servers have off moments; Inno retries its own signing, so this does too.
    $attempt = 0
    do {
        $attempt++
        & $signTool @(Get-SignArguments) $Source | Out-Null
        if ($LASTEXITCODE -eq 0) { break }
        if ($attempt -ge 3) { throw "signtool failed on $Source after $attempt attempts (exit code $LASTEXITCODE)." }
        Write-Host "  signtool failed (exit code $LASTEXITCODE); retrying in 5 s..." -ForegroundColor Yellow
        Start-Sleep -Seconds 5
    } while ($true)
} else {
    Write-Host 'No certificate given: the exe and installer will be unsigned (Windows shows "Unknown publisher").' -ForegroundColor Yellow
}

# ---------------- compile ----------------

Write-Host "Building installer $Version from $Source" -ForegroundColor Cyan
$arguments = @(
    '/Q', "/DAppVersion=$Version", "/DAppVersionNumeric=$numericVersion", "/DSourceExe=$Source",
    "/DWizardAppearance=$Appearance", "/O$Output"
)

if ($signing) {
    # Inno Setup signs Setup and the uninstaller through a named sign tool. The name is passed to
    # the script as a define so an unsigned build compiles without any SignTool directive at all.
    # $f stands for the file being signed; see the SignTool help topic.
    $command = ((@($signTool) + (Get-SignArguments) | ForEach-Object { ConvertTo-InnoArgument $_ }) -join ' ') + ' $f'
    $arguments += @("/Sdriftwall=$command", '/DSignToolName=driftwall')
}

& $iscc @arguments "$PSScriptRoot\Driftwall.iss"
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed with exit code $LASTEXITCODE." }

$setupExe = Join-Path $Output "DriftwallSetup-$Version.exe"
if (-not (Test-Path $setupExe)) { throw "Expected $setupExe but it was not produced." }

$setupMb = [math]::Round((Get-Item $setupExe).Length / 1MB, 1)
$hash = (Get-FileHash $setupExe -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host ''
Write-Host "Built $setupExe ($setupMb MB)" -ForegroundColor Green
Write-Host "SHA256 $hash"

Write-Host 'Signatures:'
Show-Signature $Source
Show-Signature $setupExe
