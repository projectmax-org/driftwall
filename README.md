# Driftwall

A desktop wallpaper switcher for Windows that pulls photos from the web and your own folders, on a
schedule you choose, across as many monitors as you have.

Built to be invisible when you aren't using it: it sits in the notification area doing nothing
between changes, and gets out of the way entirely while you're gaming.

Driftwall is a [Project Max](https://projectmax-org.github.io) application: free, open source under the
[MIT licence](LICENSE), no account, no telemetry. Download the latest release from the
[releases page](https://github.com/projectmax-org/driftwall/releases/latest); the app's own page is
at [projectmax-org.github.io/driftwall](https://projectmax-org.github.io/driftwall/).

---

## What it does

**Photos from anywhere**
Wallhaven, Bing's daily wallpaper, Unsplash, Pexels, Pixabay, Flickr, Reddit, NASA's Astronomy
Picture of the Day, any RSS/Atom photo feed, and folders on your own computer. Mix as many as you
like — each source gets a weight that controls how often it comes up.

**Three ways to choose**
Top (the source's most popular), a category, or a keyword search. Per source, so you can run
"Wallhaven top of the month" and "Unsplash search: brutalist architecture" side by side.

**On your schedule**
Any interval from 5 minutes to a day, plus optional changes when Windows starts, when you unlock,
and when you shut down (so the next sign-in is already a new one).

**Real multi-monitor support**
Every display gets its own photo rendered at its exact native resolution — a 4K monitor is never fed
a 1080p file, and a portrait display gets portrait photos where they're available. Or use one photo
everywhere, or span one across the whole desktop. Mixed DPI is handled correctly.

**Five scaling modes**
Fill, Fit, Stretch, Center, Tile. Fit fills the empty area with a blurred copy of the photo, a flat
colour, or the photo's own average colour.

**Browse and keep**
Look through everything a source offers inside the app, save what you like into named collections,
then use a collection as a wallpaper source of its own. Saved photos are copied outside the cache,
so they survive cache eviction and work offline.

**Tray icon**
Double-click opens the app. Right-click gives you next, previous, refresh, pause, save the current
photo, view it online, and set a random photo from any collection. Can be hidden entirely.

---

## Staying out of the way

The resource behaviour was the design constraint, not an afterthought:

- **No polling.** One timer, armed once per change. Between changes the app does nothing at all.
- **Pauses for games.** Fullscreen apps, presentations and Direct3D exclusive mode are detected at
  the moment the timer fires, and the change is deferred rather than run. Optional pauses for battery
  power and metered connections too.
- **Bounded memory.** The browse grid virtualises: tiles load their thumbnail when they scroll into
  view and release it when they scroll out, so memory tracks the viewport, not the scroll history.
- **Decodes only what it needs.** A 45-megapixel photo destined for a 1080p monitor is decoded at
  about 2 megapixels. Full-size decoding never happens.
- **Gives memory back.** Closing the window compacts the large object heap and returns the working
  set to Windows.
- **No GPU use.** Composition runs on WPF's software rasteriser on a dedicated below-normal-priority
  thread, so it never competes with a game for the GPU or stutters the UI.

---

## Installing

Three ways in, depending on who the install is for.

**For you, right now** — no extra tools:

```powershell
.\tools\install-local.ps1
```

Installs to `%LOCALAPPDATA%\Programs\Driftwall`, puts shortcuts on the desktop and in the Start
menu, and registers under Settings → Apps. Add `-StartWithWindows` to have it start hidden at
sign-in. Undo with `.\tools\install-local.ps1 -Uninstall`.

**For people downloading it** — a real `DriftwallSetup-1.0.0.exe` wizard:

```powershell
.\build.ps1 -Installer
```

No installer tooling to set up first. [`installer/build-installer.ps1`](installer/build-installer.ps1)
uses [Inno Setup](https://jrsoftware.org/isinfo.php) 6.7 or later if it is already on the machine,
and otherwise downloads the official release and unpacks it in portable mode into `tools\.inno` (no
admin rights, no registry entries; delete the folder to undo). Run that script on its own to package
an exe you already have, or on a machine without the .NET SDK:

```powershell
.\installer\build-installer.ps1
```

The wizard is a per-user install with no UAC prompt, desktop and start-with-Windows options, an
uninstaller that asks before deleting your collections, and in-place upgrades. It speaks English and
Korean, follows the Windows light or dark appearance, and is drawn in the app's own colours with the
same mark as the icon; the artwork in `installer\art` comes from `tools\IconGen --wizard`. To preview
one appearance regardless of your Windows setting:

```powershell
.\installer\build-installer.ps1 -Appearance dark -Output C:\temp\preview
```

The script is [`installer/Driftwall.iss`](installer/Driftwall.iss); it also accepts the usual Inno
switches for unattended use:

```powershell
.\dist\DriftwallSetup-1.0.0.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
```

**For Steam** — nothing. Steam installs the portable `dist\Driftwall.exe` itself and manages
shortcuts; upload that file as the depot content.

### The "Unknown publisher" warning

Windows labels any executable without an Authenticode signature "Unknown publisher": in the
SmartScreen dialog, in Edge's download prompt, and in the UAC prompt if one is ever needed. Version
metadata does not change that; only a signature from a certificate authority does. The build is
ready for one, and signs `Driftwall.exe`, the setup wizard and the uninstaller with an RFC 3161
timestamp so the signatures outlive the certificate:

```powershell
.\build.ps1 -Installer -CertificateThumbprint <sha1 of a code-signing certificate in your store>
```

Four ways to supply the signature, all of them also readable from the environment so the release
workflow can use repository secrets:

| Option | What it takes | Fits GitHub Actions |
|---|---|---|
| `-ArtifactSigningEndpoint`, `-ArtifactSigningAccount`, `-ArtifactSigningProfile` | Microsoft's Artifact Signing service (formerly Trusted Signing) plus Azure credentials in `AZURE_TENANT_ID`, `AZURE_CLIENT_ID`, `AZURE_CLIENT_SECRET` | Yes, wired into `release.yml` |
| `-CertificateThumbprint` | A certificate in your Windows store, including one on a hardware token or a CA's cloud signer | Only on your own machine |
| `-SignCommand` | Any signing tool's command line with `$f` for the file, for example SSL.com's `CodeSignTool` | If the tool is installed on the runner |
| `-PfxPath`, `-PfxPassword` | A `.pfx` file | Yes, but CAs no longer issue exportable keys |

What a signature does and does not do (all checked against Microsoft's documentation in September
2026): it replaces "Unknown" with a **Verified publisher** line and lets Smart App Control run the
app, but the SmartScreen warning itself only goes away as downloads accumulate over weeks. No kind
of certificate skips that any more, EV included, since 2024. A self-signed certificate does nothing
for users: Windows trusts the chain, not the name.

**Whose name appears.** A certificate carries the *validated* legal name and nothing else; no
authority lets you type a brand in. "Project Max" shows as the publisher only once Project Max is a
registered trade name. In Korea that is a sole-proprietor registration (개인사업자 사업자등록, at
the tax office or on Hometax, with 상호 "Project Max"), after which an organisation-validated
certificate can carry it, subject to each authority's checks. Until then any certificate shows the
person's own name.

Where to get one, as of September 2026, with what it prints:

- **Microsoft Artifact Signing**, about USD 10 a month on a paid Azure subscription. Organisations
  in South Korea are eligible; individual developers are accepted only from the United States and
  Canada. Prints the validated organisation name. Signs from GitHub Actions with no key to store;
  the runner needs the .NET 8 runtime, which the workflow installs.
- **SSL.com**, about USD 129 a year for an individual or organisation certificate, plus its eSigner
  cloud signing (about USD 20 a month) if you want to sign from CI; otherwise a hardware key. Prints
  your personal name, or the organisation's with a business registration.
- **Certum "Open Source Code Signing"**, about EUR 49 a year with cloud signing or EUR 69 with a
  card. Cheapest, but it prints "Open Source Developer, Your Name", needs its phone app for each
  signing session, and may be revoked if used for commercial software.
- **Sectigo, DigiCert and resellers**, roughly USD 220 to 540 a year, hardware token included.
  Organisation certificates print the registered trade name.

## Building

Needs the [.NET 10 SDK](https://dotnet.microsoft.com/download). Either `winget install
Microsoft.DotNet.SDK.10`, or the per-user route that needs no admin rights:

```powershell
Invoke-WebRequest https://dot.net/v1/dotnet-install.ps1 -OutFile dotnet-install.ps1
.\dotnet-install.ps1 -Channel 10.0
```

That puts the SDK in `%LOCALAPPDATA%\Microsoft\dotnet`, where `build.ps1` finds it without any PATH
changes.

```powershell
.\build.ps1
```

Produces a single self-contained `dist\Driftwall.exe` that runs on a clean Windows machine with no
.NET runtime installed. If the copy in `dist` is the one running on your desktop, the script stops it
for the build and starts it again in the notification area afterwards.

```powershell
.\build.ps1 -Run                  # build then launch
.\build.ps1 -Configuration Debug  # debug build
.\build.ps1 -FrameworkDependent   # small exe, needs the .NET Desktop Runtime installed
.\build.ps1 -Icon                 # regenerate Assets\app.ico first
.\build.ps1 -Runtime win-arm64    # ARM64 build
```

Or straight from the SDK:

```bash
dotnet run --project src/Driftwall
```

---

## How it's put together

```
src/Driftwall/
  Core/          settings, collections, cache, rotation queue, logging, memory management
  Sources/       one file per photo provider, all behind IPhotoSource
  Wallpaper/     Win32/COM interop, monitor detection, image composition, applying the wallpaper
  Scheduling/    the rotation timer, Windows session events, run-at-login
  Tray/          notification-area icon and its menu
  UI/            theme, converters, view models, views, custom controls
tools/IconGen/   generates Assets/app.ico — edit the drawing code, re-run, commit the result
```

The pieces that are worth knowing about:

- **`IDesktopWallpaper`** (COM, Windows 8+) is what makes per-monitor wallpaper work. The app renders
  a file matching each monitor's exact pixel size and hands it over, so Windows never rescales it —
  the desktop's own "Fill" is a low-quality stretch, and avoiding it is most of why this looks sharp.
- **`RotationService`** keeps a look-ahead queue filled from every enabled source in weighted
  rotation, and prefetches the next few photos, so pressing Next is instant.
- **`RenderThread`** is a single dedicated STA thread that owns all WPF imaging. Doing image work on
  thread-pool threads would leak one `Dispatcher` per thread; doing it on the UI thread would stutter
  the window.
- **API keys** are encrypted with DPAPI against the current Windows account, so a copied
  `settings.json` loses its keys rather than leaking them.

Settings, collections, logs and caches live in `%LOCALAPPDATA%\Driftwall`.

### Verifying a change

```powershell
dotnet run --project tools\SelfTest
```

Checks monitor detection, composition at every scaling mode, and the real
`IDesktopWallpaper.SetWallpaper` path end to end. It captures your current wallpaper **and its
position** first and restores both in a `finally` block, so running it leaves the desktop exactly as
it found it.

| Script | What it does |
|---|---|
| `tools\smoke-test.ps1` | Launches the app, proves single-instance activation, screenshots it, reports memory |
| `tools\measure-memory.ps1` | Memory and idle CPU with the window open vs. closed to the tray (`-StartHidden` for the tray-only case) |
| `tools\capture-views.ps1` | Screenshots every tab by driving the nav through UI Automation |
| `tools\IconGen` | Regenerates `Assets\app.ico` — edit the drawing code, re-run, commit the result |

All of them write a settings file with rotation switched off first, so none of them touch your
desktop wallpaper. Your real `settings.json` is set aside before that and put back when the script
ends (see `tools\SettingsGuard.ps1`), so running them on your own machine costs you nothing.

Measured on a two-monitor setup (3840×2160 + 1920×1080), Release build:

| State | Working set | Idle CPU |
|---|---|---|
| Resident in the tray, window never opened | 37 MB | 0 ms / 10 s |
| Window open with a full photo grid loaded | 219 MB | — |
| Window closed back to the tray | 35 MB | — |

---

## Photo licensing

**Read [`docs/SOURCES.md`](docs/SOURCES.md) before selling this.** The sources do not share a licence,
and two of them (Pexels and Reddit) have terms that restrict exactly this kind of product. Both ship
disabled with an in-app warning. The default configuration — Wallhaven and Bing, no API key needed —
is the safe one.

---

## Keyboard shortcuts

| Shortcut | Action |
|---|---|
| `Ctrl` + `→` | Next wallpaper |
| `Ctrl` + `←` | Previous wallpaper |
| `F5` | Re-render the current wallpaper |
| `Esc` | Close to the notification area |
