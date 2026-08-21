# Album Fixer

Album Fixer is a native Windows app that locally splits FLAC+CUE images and extracts SACD ISOs into tagged tracks, shows per-album progress, and produces a readable conversion report.

## What it does

- Classifies FLAC+CUE image splits, SACD/DSD extraction, existing-track metadata repair, and ambiguous sources. Verified end-to-end write-back is enabled for FLAC+CUE, one SACD ISO, and same-format standalone FLAC, DSF, or DFF tracks; DSF or DFF repair may coexist with one retained SACD ISO. Ambiguous modes stop before changing files.
- Gives repair-only mode precedence when separated tracks coexist with an image.
- Bundles the pinned Windows `sacd_extract` executable in every build and publish. On startup, verifies that bundled tool is present and attempts to install missing FFmpeg components through WinGet. Failures are shown to the user and affected workflows remain blocked.
- Checks a local fixed Windows Temp volume and conservative staging capacity before processing. No external coding agent is required.
- Accepts either one album folder or a parent folder containing independent albums. Batch mode discovers disjoint album roots and uses a hardware-aware bounded pipeline with separate NAS read, local processing, and NAS write-back limits.
- Shows determinate inventory progress while discovering files, reading reports and CUE references, verifying completion evidence, and classifying media. Report evidence and resolved album folders are inventoried with up to four concurrent workers; progress reports files and completed albums while keeping NAS scan pressure bounded.
- Lists selected sources in the Albums tab before inventory finishes, replaces parent placeholders with discovered album rows, and keeps one lifecycle badge per row from discovery through inventory, readiness, processing, and the terminal result. Detailed job phases such as splitting and tag fixing supply the active badge text without adding a second status label.
- Gives every batch album a unique Windows Temp job, destination-side staging folder, progress state, report, commit owner, and failure boundary. Transient local and destination staging is removed after success, failure, or cancellation; a failed album does not interrupt unrelated albums. Strong CPU/NVMe systems can process up to four FLAC albums locally while NAS transfers remain limited to one or two lanes; SACD processing remains capped at two jobs.
- Admits albums independently: an album-specific preflight blocker skips only that album while healthy siblings continue. Each album's preflight badge becomes its live phase-and-percent indicator after the run starts.
- Retains legacy inner-folder tracks while producing new tracks at the album root. On a rerun, only root-level files explicitly listed with matching recorded sizes in a prior Album Fixer report may be replaced; they remain in a private rollback area until final verification passes. Incomplete legacy `Tracks\...` cleanup remains limited to exact report-listed files.
- Copies network albums and required audio tools into a unique Windows Temp job and checks source file sizes before processing. The job is always deleted after its durable terminal report is written. Cryptographic hashes are intentionally not calculated to avoid extra full-file NAS reads.
- Parses each CUE and splits its tracks in one local FFmpeg process, writing locally available tags. Artwork is normalized to a bounded JPEG in memory and embedded directly into each track; Album Fixer does not create downloaded, normalized, temporary, or destination-side image files, and preserves every existing artwork file unchanged.
- Reads every area reported by a SACD ISO and extracts each area sequentially to `Stereo` or `Multichannel`. It repeats every extraction independently, compares untagged track sizes, embeds tags and the same in-memory cover bytes, and checks that tagging did not change the DSD payload length. Local front-cover artwork has priority; when it is absent, an exact MusicBrainz release match may supply Cover Art Archive artwork that is bounded and normalized entirely in memory.
- When SACD disc text omits album identity or track names, resolves fallback evidence conservatively: valid disc text remains authoritative; an unambiguous catalog-number, SACD-format, and track-count match is next; checksum filenames provide the strongest local artist/title hint; the album folder supplies title, year, and edition hints; and an external track listing is accepted only when its count and the stronger catalog/local identity evidence agree. Conflicting evidence stops safely.
- Before SACD extraction, searches MusicBrainz, MusicBrainz-linked public Discogs records, Apple Music, and Discogs. Without `DISCOGS_TOKEN`, direct public Discogs title search is used only when a complete ordered local track-title list is available to verify every returned track; a token also enables the broader catalog workflows. Exact-edition fields require conservative artist/title/format/year/track-count matching; every used source and lookup warning is recorded.
- Places tracks directly beside the source for a single FLAC+CUE image. When several FLAC+CUE images share the selected album folder, places their tracks in deterministic `CD1`, `CD2`, … subfolders.
- After every track exists, reads the metadata-gap handoff. Only named missing fields are passed to deterministic C# enrichment using MusicBrainz, linked public Discogs data, Apple Music, authenticated Discogs when configured, and Cover Art Archive. Resolved fields are written to the existing FLAC files with TagLibSharp; no coding-agent process or skill is started. `LABEL`, `BARCODE`, and `RELEASECOUNTRY` are optional: unresolved values remain report warnings but do not prevent Complete status or source deletion. Missing required metadata uses a **Required metadata missing** warning badge and retains the source.
- Uses quick ffprobe container, required-tag, and embedded-artwork checks; writes through destination-side staging and compares file sizes. Decoded PCM/MD5 and cryptographic hash comparisons are skipped; when **Delete originals** is selected, a confirmed single source is deleted, while multiple sources are retained. Clearing the option retains every original.
- Recovers an already completed FLAC+CUE split when a later Album Fixer fallback report replaced its success report and the original image is already gone, but only when the retry stopped before processing and the exact CUE-derived root track set passes filename, native FLAC, required-tag, embedded-artwork, and modification-time checks. The recovery is labeled as quick verification without decoded PCM equivalence.
- Skips already-split standalone FLAC albums when the current files themselves prove that no repair is needed: one consistent album identity, complete sequential track/disc numbering, required tags (including composer when classical/opera policy requires it), readable audio, and embedded artwork on every track. This supports `CD1`, `CD2`, … layouts and treats CUE references to removed historical images as preserved provenance rather than blockers; any incomplete tag, numbering gap, album disagreement, or missing embedded cover fails closed into normal inspection/repair behavior.
- Repairs same-format standalone FLAC, DSF, or DFF tracks without requiring a CUE sheet. Existing nonempty tags and embedded artwork have highest priority. When their album identity is insufficient, external lookup also tries album-title candidates parsed from the album folder and verifies a title-only result against every ordered existing-tag/filename track title before accepting it. Exact MusicBrainz releases and linked public Discogs tracklists may then fill missing album fields, canonical titles, and verified per-track composer credits; any track disagreement rejects the fallback. Filenames remain the final local title/number fallback. DFF metadata is read from native DSDIFF information and ID3 chunks and written through a native staged-copy ID3 path. Before transactional replacement, the app proves each FLAC frame payload, DSF data chunk, or DFF DSD chunk is byte-identical by SHA-256. TIFF scans are accepted as cover inputs. When uniform DSF or DFF tracks coexist with exactly one retained SACD ISO, selecting **Delete originals** deletes only that exact ISO after final native-audio-payload, tag, embedded-artwork, and file-size verification; the repaired tracks are never deletion targets. Clearing the option retains the ISO.
- Shows phase progress with elapsed time, prioritizes currently active albums in the live album list, and includes live activity, inventory, final verification status, output counts, source disposition, and formatted report JSON.
- Source policy: with **Delete originals** selected, a successful one-image FLAC+CUE run deletes the exact confirmed image after final quick and file-size checks. A successful single-SACD run deletes the exact ISO only after both extraction sizes and final DSF/DSD structure, required-tag, artwork, report-path, and file-size checks pass. A uniform existing-track DSF/DFF repair may likewise delete its single coexisting retained ISO only after exact final native-payload, tag, embedded-artwork, and path verification. Missing optional `LABEL`, `BARCODE`, or `RELEASECOUNTRY` values do not block deletion. Clearing the option, or processing a multi-image album, retains every original.
- Retains sources whenever a job is failed, incomplete, canceled, uncertain, or missing required proof.

## Run

Open:

`publish\win-x64\Album Fixer.exe`

The packaged app is self-contained for 64-bit Windows and does not require a separate .NET installation.

1. Add one or more album folders or parent folders. Each listed source is scanned recursively; duplicate or nested source selections are ignored.
2. Select **Scan albums**. Review which albums are ready and which will be skipped; a blocked album does not block ready siblings.
3. Choose whether **Delete originals** should remain selected, then review the source policy: each eligible one-image FLAC album deletes only its exact original after quick and file-size checks; a SACD ISO requires two size-matching extractions plus DSD structure and metadata verification. Clearing the option, or running a multi-image, failed, or canceled album, retains its originals.
4. Select the start button. Album Fixer displays the hardware- and capacity-aware pipeline limits before starting; additional albums wait in bounded queues.
5. If one or more album transactions fail, select **Retry failed**. Album Fixer freshly rescans and preflights only those failed album roots, skips any that are now completed or blocked, and starts isolated transactions only for the remaining retryable failures. Completed and incomplete siblings are not queued again.
6. Review global readiness in **Preflight**, follow each album's phase and percent in **Albums**, then review the timeline and **Report** tab.

## Required tools

- Optional: a Discogs personal access token in the `DISCOGS_TOKEN` environment variable for broader direct Discogs database search. MusicBrainz, Apple Music, linked public Discogs records, and tightly verified title search backed by a complete ordered local track list do not require this token.
- `ffmpeg` and `ffprobe` for FLAC workflows.
- `ffmpeg`, `ffprobe`, and TagLibSharp-backed DSF tagging for in-memory artwork normalization and the verified SACD workflow.
- `sacd_extract` for SACD ISO layout inspection and DSF extraction. Album Fixer stages an `id3tag=0` configuration so independent untagged extraction sizes can be compared.

The published application includes the pinned `sacd_extract` 0.3.9.3b Windows executable under `Tools`. The build fails if that binary does not match its approved SHA-256. At startup, Album Fixer uses the bundled copy and installs only missing FFmpeg tools through WinGet. If a bundled component is absent or FFmpeg installation fails, Album Fixer shows the error and leaves workflows that require the missing component blocked. Metadata and artwork catalog failures are recorded; unresolved required FLAC tags stop the transaction safely, while unresolved artwork is delivered as incomplete work with the source retained.

## Application architecture

The WPF application uses MVVM. `MainViewModel` owns presentation state and exposes CommunityToolkit commands, while `MainWindow` contains only view-specific drag/drop and close-event adaptation. Dialogs, shell access, clipboard access, and the UI timer are behind injected interfaces.

`App.xaml.cs` is the composition root. It builds the `Microsoft.Extensions.DependencyInjection` service provider, registers the core processing services and WPF adapters, resolves the main window, and disposes the graph when the application exits.

## Build and test

```powershell
dotnet build AlbumFixer.slnx -c Release
dotnet run --project tests\AlbumFixer.Core.SmokeTests\AlbumFixer.Core.SmokeTests.csproj -c Release
```

The smoke suite covers FLAC+CUE classification, multi-album batch planning, partial preflight admission, bounded parallel execution and failure isolation, guarded legacy-output cleanup, repair-only precedence, verified local staging, real local splits with tags and embedded artwork, deterministic local metadata enrichment, terminal reports, and transactional commit behavior.
