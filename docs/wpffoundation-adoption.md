# Album Fixer: WpfFoundation adoption scope

Album Fixer adopts WpfFoundation after UPSWarden, as section 11 of the WpfFoundation implementation plan describes (gbolotin/WpfFoundation, `docs/implementation-plan.md`). This document scopes that work before it starts. Album-processing behavior stays exactly the same; this is presentation work only.

## Current state

- **Projects:**
  - `AlbumFixer.App` (WPF, about 2,400 lines): `MainWindow`, a 1,625-line `MainViewModel`, view data and services.
  - `AlbumFixer.Core` (about 10,400 lines of processing).
  - `AlbumFixer.Core.SmokeTests`, a console runner with its own `Assert` and about 4,400 lines.
  - The release is a self-contained `win-x64` publish that bundles a hash-checked `sacd_extract.exe`.
- **Build:**
  - `Directory.Build.props` holds nullable, implicit usings, warnings as errors and deterministic builds, with `LangVersion` set to `latest`.
  - Package versions sit in each project file: CommunityToolkit.Mvvm 8.4.0, Microsoft.Extensions.DependencyInjection 10.0.0 and TagLibSharp 2.3.0.
  - There is no `global.json`, version, CI or `AGENTS.md`.
- **Composition:** already Microsoft.Extensions.DependencyInjection with `ValidateOnBuild`, like DoViFixer. The ViewModels already use CommunityToolkit.Mvvm.
- **UI:**
  - One window. Its five tabs (Albums, Preflight, Inventory, Report and Activity) are written inline in `MainWindow.xaml`, some as single very long lines.
  - The separate dark theme lives in `App.xaml`: about 10 named brushes (`Bg`, `Panel`, `Card`, `Mint`, `Amber`, `Red`, ...), custom button, tooltip and card templates, `CornerRadius="14"`, and literal font sizes and weights.
  - There are 26 literal colors in total.
- **Dialogs and files:**
  - `WpfUserInteractionService` uses native `MessageBox` for two Yes/No warnings (with No as the default) and two errors.
  - Folders are picked with `OpenFolderDialog`.
- **Long operations:** batch runs show progress inside the window only.

## Slices (one pull request each)

### 1. Setup
- Add `global.json` (.NET 10 SDK) and move package versions to `Directory.Packages.props`. Raise CommunityToolkit.Mvvm to 8.4.2 and Microsoft.Extensions.DependencyInjection to 10.0.12 to match the other applications.
- Pin `LangVersion` to 14.0. Add one SemVer `VersionPrefix` with `VersionSuffix` defaulting to `dev`. Show the version in the window title and in the report.
- Reference the `WpfFoundation` package with the local switch and an `AlbumFixer.Local.slnx`, as UPSWarden does.
- Copy WpfFoundation's `docs/agents/common-rules.md` unchanged and add an `AGENTS.md` with Album Fixer's rules: processing rules and source-retention policy stay in Core, and the presentation never weakens a verification step.
- Add CI. It builds Release, runs the smoke tests, and publishes the self-contained `win-x64` app as a ZIP. Versions are `ci.<run>`, and a `vX.Y.Z` tag becomes a GitHub Release (tags only with Gabi's approval).
- Add the XAML design check, report-only at first.

### 2. Dialogs and files
- Move `WpfUserInteractionService` onto WpfFoundation's `IDialogService`:
  - Confirmations use `ConfirmAsync` with action labels instead of Yes/No, for example "Delete originals" / "Keep originals".
  - Destructive confirmations keep the safe choice as the default, as they do now.
  - Errors use `ShowMessageAsync`.
- Pick folders through `IFileDialogService`.
- Accept dropped album folders with WpfFoundation's `FileDrop` behavior. The plan lists drag and drop as a pattern Album Fixer contributes.

### 3. Views
- **Split the window:** move each tab into its own UserControl, mapped by a one-line DataTemplate per the common rules, and keep the five-tab layout.
- **Split the ViewModel:** carve `MainViewModel` into a ViewModel per tab where the state separates naturally (Report, Activity, Inventory). The run orchestration stays in one coordinator. No behavior changes, and the smoke tests still pass.
- **Status:** show run status in a `WfStatusBarStyle` status bar where it helps.

### 4. Theme and design guidance
- **Dark theme:** Album Fixer keeps its separate dark theme, as the plan says. Set `ThemeMode="Dark"` so Fluent controls render dark. Move the theme's colors into one dictionary that uses the Fluent brush names where an equivalent exists, and list the remaining brand colors in `xaml-design-allowlist.txt` with their reason (the deliberate dark palette).
- **Guidance:** layout, typography, spacing and wording follow the guidance. That means type ramp styles instead of literal sizes and weights, `ControlCornerRadius`/`OverlayCornerRadius` instead of `14` and `7`, and the 4 px spacing steps. The custom button and tooltip templates give way to the Fluent ones, restyled through the theme colors.
- **Monospaced text:** the report's monospaced JSON stays, allowlisted like WpfFoundation's own code samples.

### 5. Long-operation feedback and the blocking check
- **Taskbar:** show batch progress and outcome on the taskbar through `IOperationFeedback`, with `TaskbarService`.
- **Blocking check:** make the XAML design check blocking.

## Out of scope

- **Processing:** all processing, verification, metadata lookup, staging and source-retention rules in `AlbumFixer.Core` stay as they are.
- **Smoke tests:** the runner stays as it is. Converting it to MSTest is a separate decision.
- **No new production dependencies:** WpfFoundation is the only addition. `WpfFoundation.Notifications` waits for question 2.

## Questions for Gabi

1. **Releases:** may CI publish public GitHub Releases that include the bundled `sacd_extract.exe` (GPL-2.0, `Tools/COPYING.sacd_extract.txt`)? Distributing the binary means also making its corresponding source available, for example by linking the exact upstream release (EuFlo/sacd-ripper 0.3.9.3b) next to the download. Otherwise CI would keep the ZIP as a private build artifact.
2. **Notifications:** should batch completion also raise a Windows notification? That needs `WpfFoundation.Notifications`, which adds about 25 MB of Windows SDK projection to the self-contained publish.
3. **Dark theme:** should Album Fixer stay dark-only, as this scope assumes, or should it follow the system theme with the dark palette only for Dark?
