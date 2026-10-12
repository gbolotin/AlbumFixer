# Album Fixer instructions

Apply these rules together with the [common development rules](docs/agents/common-rules.md).

## Project architecture

Album Fixer is a .NET 10 WPF application that locally splits FLAC+CUE images, extracts SACD ISOs and repairs existing tracks into tagged, verified tracks, with a conversion report per album. The [README](README.md) describes the workflows and the source policy.

Projects:
- src/AlbumFixer.Core: scanning, preflight, staging, processing, metadata, verification, commit, reports and the source-retention policy. No WPF.
- src/AlbumFixer.App: the WPF application. `MainViewModel` coordinates the run and owns the source, album and status state; `ReportViewModel`, `InventoryViewModel` and `ActivityViewModel` hold their tabs' state. Each tab and panel is a UserControl in `Views`. `App.xaml.cs` is the composition root (`Microsoft.Extensions.DependencyInjection`, validated on build).
- tests/AlbumFixer.Core.SmokeTests: a console runner that exercises Core end to end on generated fixtures.

The adoption of WpfFoundation is scoped in [docs/wpffoundation-adoption.md](docs/wpffoundation-adoption.md), one pull request per slice.

## Application-specific rules

- Processing rules and the source-retention policy live in Core. The App presents them and never decides on its own whether a source is deleted or retained.
- The presentation never weakens a verification step: no UI change, refactor or convenience may skip, shorten or relax a check, a terminal report or the retention of an original when a proof is missing.
- Failures, cancellations and missing proof keep every original. A terminal report is written before transient staging is cleaned.
- The UI uses the Fluent theme only: theme brushes (success, caution and critical for states), type ramp styles, `ControlCornerRadius`/`OverlayCornerRadius` and the 4 px spacing steps; no app palette. The theme choice (System, Light, Dark) is applied through `IThemeService` and remembered in `%LOCALAPPDATA%Album Fixersettings.json`. Check visual changes in Light, Dark and high contrast.
- Dialogs go through WpfFoundation's `IDialogService` (wrapped by `IUserInteractionService` for Album Fixer's wording) and folder pickers through `IFileDialogService`; never the native `MessageBox`. Buttons name the action, and every confirmation that can delete a source or interrupt a job keeps the safe choice as the default.
- The bundled `sacd_extract.exe` is GPL-2.0. Keep `Tools/COPYING.sacd_extract.txt` and `Tools/README.md` beside it in every build, publish and release ZIP, and keep the SHA-256 check in `AlbumFixer.App.csproj`. Changing the binary means updating the hash, the README and the release notes' upstream source link together.
- `App` registers logging with `AppLogging.AddAppLogging` (Serilog, compact JSON under `%LOCALAPPDATA%\Album Fixer\Logs`, 14 days, 10 MB files), and classes take `ILogger<T>`. Log with message templates and named properties, not interpolated strings. The log complements the per-album JSON reports and doesn't replace them; logging must never change a run's outcome.

## WpfFoundation reference

- AlbumFixer.App references the `WpfFoundation` package pinned in `Directory.Packages.props`. Upgrade it by changing that version.
- `Directory.Build.props` turns on `UseLocalWpfFoundation` only when building `AlbumFixer.Local.slnx` and `..\WpfFoundation\src\WpfFoundation\WpfFoundation.csproj` exists. AlbumFixer.App then references that project instead of the package, so library code can be debugged and edited in the same session. `AlbumFixer.slnx` always uses the package; `-p:UseLocalWpfFoundation=true|false` overrides.

## Build, versioning and CI

- `global.json` pins the .NET 10 SDK. `Directory.Build.props` holds the shared compiler settings (C# 14, nullable, implicit usings, warnings as errors, deterministic) and the version; `Directory.Packages.props` holds every package version. Projects reference packages without a version.
- One SemVer `VersionPrefix` in `Directory.Build.props` covers every assembly. Local builds are `<VersionPrefix>-dev`; CI builds pass `-p:VersionSuffix=ci.<run>`. The window title, the startup log line and every conversion report (`album_fixer_version`) show the version.
- CI (`.github/workflows/ci.yml`) builds in Release, runs the smoke tests and publishes the self-contained `win-x64` app as a ZIP on every push and pull request. A `vX.Y.Z` tag builds `X.Y.Z` and creates a public GitHub Release with that ZIP, marked as a pre-release when the tag has a suffix, whose notes link the exact `sacd_extract` upstream source. Push tags only with Gabi's approval.
- CI runs the shared XAML design check from WpfFoundation. The XAML passes it; the check still reports without failing the build until the next slice makes it blocking. Justified exceptions go in `xaml-design-allowlist.txt` with a reason.

## Testing

- Run the smoke tests with `dotnet run --project tests/AlbumFixer.Core.SmokeTests -c Release`; they exit non-zero on failure. Parts that need FFmpeg are skipped when it is missing, so CI covers the rest.
- Never point a test or a manual run at a real library folder. Use generated fixtures or a scratch copy: a run with **Delete originals** selected deletes verified sources.
