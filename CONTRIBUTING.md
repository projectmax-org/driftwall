# Contributing to Driftwall

Thanks for looking. Driftwall is a small, opinionated app and the bar for changes is "would a
careful person who uses it every day want this". Bug reports, source fixes and well-argued features
are all welcome.

## Building

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download) and Windows 10 or 11.

```powershell
.\build.ps1              # self-contained dist\Driftwall.exe
.\build.ps1 -Installer   # plus dist\DriftwallSetup-<version>.exe (Inno Setup is fetched for you)
dotnet run --project src/Driftwall
```

`README.md` describes the layout. The short version: `Sources/` has one file per photo provider
behind `IPhotoSource`, `Wallpaper/` talks to Windows, `Core/` holds settings, cache and the rotation
queue, and `UI/` is the WPF app.

## Before you open a pull request

- Run `dotnet run --project tools\SelfTest`. It exercises monitor detection, composition and the real
  wallpaper API, and restores your desktop afterwards.
- Run the app from `dist` and use the thing you changed. Screenshots in the PR help a lot.
- Keep the resource behaviour. Idle memory and "does nothing between changes" are the point of the
  app; a change that adds a timer, a polling loop or a background thread needs a very good reason.
- One change per pull request, described in the first line of the description the way you would
  explain it to a user.

## Adding a photo source

Implement `IPhotoSource` (start from `PhotoSourceBase`), register it in `SourceRegistry`, add its
licensing note to `ProviderTerms.cs` and to `docs/SOURCES.md`. Anything whose terms restrict a
wallpaper app ships switched off with a warning, as Pexels and Reddit do today. Do not add a source
whose terms you have not read.

## Reporting bugs

Use the bug template. The log at `%LOCALAPPDATA%\Driftwall\driftwall.log` (Settings → About →
Open log file) is usually the fastest way to the cause. Strip API keys before pasting; the app
never writes them there, but your own paste might.

## Licence

By contributing you agree that your contribution is licensed under the MIT licence in `LICENSE`.
