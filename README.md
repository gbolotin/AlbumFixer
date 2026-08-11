# Album Fixer

Album Fixer is a native Windows app that locally splits FLAC+CUE images and extracts SACD ISOs into tagged tracks, shows per-album progress, and produces a readable conversion report.

## What it does

- Classifies FLAC+CUE image splits, SACD/DSD extraction, existing-track metadata repair, and ambiguous sources. Verified end-to-end write-back is enabled for FLAC+CUE and one SACD ISO per album; other modes stop before changing files.
- Gives repair-only mode precedence when separated tracks coexist with an image.
- Bundles the pinned Windows `sacd_extract` executable in every build and publish. On startup, verifies that bundled tool is present and attempts to install missing FFmpeg components through WinGet. Failures are shown to the user and affected workflows remain blocked.
- Checks a local fixed Windows Temp volume and conservative staging capacity before processing. No external coding agent is required.
- Accepts either one album folder or a parent folder containing independent albums. Batch mode discovers disjoint album roots and uses a hardware-aware bounded pipeline with separate NAS read, local processing, and NAS write-back limits.
- Gives every batch album a unique Windows Temp job, destination-side staging folder, progress state, report, commit owner, and failure boundary. Transient local and destination staging is removed after success, failure, or cancellation; a failed album does not interrupt unrelated albums. Strong CPU/NVMe systems can process up to four FLAC albums locally while NAS transfers remain limited to one or two lanes; SACD processing remains capped at two jobs.
- Admits albums independently: an album-specific preflight blocker skips only that album while healthy siblings continue. Each album's preflight badge becomes its live phase-and-percent indicator after the run starts.
- Retains legacy inner-folder tracks while producing new tracks at the album root. On a rerun, only root-level files explicitly listed with matching recorded sizes in a prior Album Fixer report may be replaced; they remain in a private rollback area until final verification passes. Incomplete legacy `Tracks\...` cleanup remains limited to exact report-listed files.
- Copies network albums and required audio tools into a unique Windows Temp job and checks source file sizes before processing. The job is always deleted after its durable terminal report is written. Cryptographic hashes are intentionally not calculated to avoid extra full-file NAS reads.
- Parses each CUE and splits its tracks in one local FFmpeg process, writing locally available tags. Artwork is normalized to a bounded JPEG in memory and embedded directly into each track; Album Fixer does not create downloaded, normalized, temporary, or destination-side image files, and preserves every existing artwork file unchanged.
- Reads every area reported by a SACD ISO and extracts each area sequentially to `Stereo` or `Multichannel`. It repeats every extraction independently, compares untagged track sizes, embeds tags and the same in-memory cover bytes, and checks that tagging did not change the DSD payload length.
- Before SACD extraction, searches MusicBrainz, MusicBrainz-linked public Discogs records, Apple Music, and authenticated Discogs search when `DISCOGS_TOKEN` is configured. Exact-edition fields require conservative artist/title/format/year/track-count matching; every used source and lookup warning is recorded.
- Places tracks directly beside the source for a single FLAC+CUE image. When several FLAC+CUE images share the selected album folder, places their tracks in deterministic `CD1`, `CD2`, … subfolders.
- After every track exists, reads the metadata-gap handoff. Only named missing fields are passed to deterministic C# enrichment using MusicBrainz, linked public Discogs data, Apple Music, authenticated Discogs when configured, and Cover Art Archive. Resolved fields are written to the existing FLAC files with TagLibSharp; no coding-agent process or skill is started. Failed or unavailable catalog research never discards a successful SACD extraction: tracks are delivered as incomplete work and the original ISO is retained.
- Uses quick ffprobe container, required-tag, and embedded-artwork checks; writes through destination-side staging and compares file sizes. Decoded PCM/MD5 and cryptographic hash comparisons are skipped; when **Delete originals** is selected, a confirmed single source is deleted, while multiple sources are retained. Clearing the option retains every original.
- Shows phase progress with elapsed time, prioritizes currently active albums in the live album list, and includes live activity, inventory, final verification status, output counts, source disposition, and formatted report JSON.
- Source policy: with **Delete originals** selected, a successful one-image FLAC+CUE run deletes the exact confirmed image after final quick and file-size checks. A successful single-SACD run deletes the exact ISO only after both extraction sizes and final DSF/DSD structure, tag, artwork, report-path, and file-size checks pass. Clearing the option, or processing a multi-image album, retains every original.
- Retains sources whenever a job is failed, incomplete, canceled, uncertain, or missing required proof.

## Run

Open:

`publish\win-x64\Album Fixer.exe`

The packaged app is self-contained for 64-bit Windows and does not require a separate .NET installation.

1. Add one or more album folders or parent folders. Each listed source is scanned recursively; duplicate or nested source selections are ignored.
2. Select **Scan albums**. Review which albums are ready and which will be skipped; a blocked album does not block ready siblings.
3. Choose whether **Delete originals** should remain selected, then review the source policy: each eligible one-image FLAC album deletes only its exact original after quick and file-size checks; a SACD ISO requires two size-matching extractions plus DSD structure and metadata verification. Clearing the option, or running a multi-image, failed, or canceled album, retains its originals.
4. Select the start button. Album Fixer displays the hardware- and capacity-aware pipeline limits before starting; additional albums wait in bounded queues.
5. Review global readiness in **Preflight**, follow each album's phase and percent in **Albums**, then review the timeline and **Report** tab.

## Required tools

- Optional: a Discogs personal access token in the `DISCOGS_TOKEN` environment variable for direct Discogs database search. MusicBrainz, Apple Music, and public Discogs records linked by MusicBrainz do not require this token.
- `ffmpeg` and `ffprobe` for FLAC workflows.
- `ffmpeg`, `ffprobe`, and TagLibSharp-backed DSF tagging for in-memory artwork normalization and the verified SACD workflow.
- `sacd_extract` for SACD ISO layout inspection and DSF extraction. Album Fixer stages an `id3tag=0` configuration so independent untagged extraction sizes can be compared.

The published application includes the pinned `sacd_extract` 0.3.9.3b Windows executable under `Tools`. The build fails if that binary does not match its approved SHA-256. At startup, Album Fixer uses the bundled copy and installs only missing FFmpeg tools through WinGet. If a bundled component is absent or FFmpeg installation fails, Album Fixer shows the error and leaves workflows that require the missing component blocked. Metadata and artwork catalog failures are recorded; unresolved required FLAC tags stop the transaction safely, while unresolved artwork is delivered as incomplete work with the source retained.

## Application architecture

The WPF application uses MVVM. `MainViewModel` owns presentation state and exposes CommunityToolkit commands, while `MainWindow` contains only view-specific drag/drop and close-event adaptation. Dialogs, shell access, clipboard access, and the UI timer are behind injected interfaces.

`App.xaml.cs` is the composition root. It builds the `Microsoft.Extensions.DependencyInjection` service provider, registers the core processing services and WPF adapters, resolves the main window, and disposes the graph when the application exits.

## Build and test

```powershell
dotnet build AlbumFixer.release.slnx -c Release
dotnet run --project tests\AlbumFixer.Core.SmokeTests\AlbumFixer.Core.SmokeTests.csproj -c Release
```

The smoke suite covers FLAC+CUE classification, multi-album batch planning, partial preflight admission, bounded parallel execution and failure isolation, guarded legacy-output cleanup, repair-only precedence, verified local staging, real local splits with tags and embedded artwork, deterministic local metadata enrichment, terminal reports, and transactional commit behavior.
