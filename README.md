# Album Fixer

Album Fixer is a native Windows app that locally splits FLAC+CUE images and extracts SACD ISOs into tagged tracks, shows per-album progress, and produces a readable conversion report.

## What it does

- Classifies FLAC+CUE image splits, SACD/DSD extraction, existing-track metadata repair, and ambiguous sources. Verified end-to-end write-back is enabled for FLAC+CUE and one SACD ISO per album; other modes stop before changing files.
- Gives repair-only mode precedence when separated tracks coexist with an image.
- Checks required audio tools, a local fixed Windows Temp volume, and conservative staging capacity before starting. Codex is not checked during a complete local run.
- Accepts either one album folder or a parent folder containing independent albums. Batch mode discovers disjoint album roots and uses a hardware-aware bounded pipeline with separate NAS read, local processing, and NAS write-back limits.
- Gives every batch album a unique Windows Temp job, destination-side staging folder, progress state, report, commit owner, and failure boundary. A failed album does not interrupt unrelated albums. Strong CPU/NVMe systems can process up to four FLAC albums locally while NAS transfers remain limited to one or two lanes; SACD processing remains capped at two jobs.
- Admits albums independently: an album-specific preflight blocker skips only that album while healthy siblings continue. Each album's preflight badge becomes its live phase-and-percent indicator after the run starts.
- Retains legacy inner-folder tracks while producing new tracks at the album root. On a rerun, only root-level files explicitly listed and hash-verified by a prior Album Fixer report may be replaced; they remain in a private rollback area until final verification passes. Incomplete legacy `Tracks\...` cleanup remains limited to exact report-listed files.
- Copies the selected album and required audio tools into a unique Windows Temp job and verifies source size and SHA-256 before processing.
- Parses each CUE and splits its tracks in one local FFmpeg process, writing locally available tags and embedding local artwork without starting Codex.
- Reads every area reported by a SACD ISO and extracts each area sequentially to `Stereo` or `Multichannel`. It repeats every extraction independently, requires exact untagged track hashes, embeds tags and cover art, and proves that tagging did not alter the native DSD payload.
- Before SACD extraction, searches MusicBrainz, MusicBrainz-linked public Discogs records, Apple Music, and authenticated Discogs search when `DISCOGS_TOKEN` is configured. Exact-edition fields require conservative artist/title/format/year/track-count matching; every used source and lookup warning is recorded.
- Places tracks directly beside the source for a single FLAC+CUE image. When several FLAC+CUE images share the selected album folder, places their tracks in deterministic `CD1`, `CD2`, … subfolders.
- After every track exists, reads the metadata-gap handoff. Complete local/external metadata skips Codex entirely; only named missing fields trigger Codex discovery, skill staging, and one metadata-only process. Failed or unavailable external research never discards a successful SACD extraction: tracks are delivered as incomplete work and the original ISO is retained.
- Uses quick ffprobe container, required-tag, and embedded-artwork checks; writes through destination-side staging and compares SHA-256 copy hashes. Decoded PCM/MD5 comparison is skipped; a confirmed single source is deleted, while multiple sources are retained.
- Shows phase progress, live activity, inventory, final verification status, output counts, source disposition, and formatted report JSON.
- Source policy: a successful one-image FLAC+CUE run deletes the exact confirmed image after final quick checks. A successful single-SACD run deletes the exact ISO only after both extractions and final DSF/DSD, payload, tag, artwork, report-path, and copy-hash verification pass. Multi-image runs retain every original.
- Retains sources whenever a job is failed, incomplete, canceled, uncertain, or missing required proof.

## Run

Open:

`publish\win-x64\Album Fixer.exe`

The packaged app is self-contained for 64-bit Windows and does not require a separate .NET installation.

1. Add one or more album folders or parent folders. Each listed source is scanned recursively; duplicate or nested source selections are ignored.
2. Select **Scan albums**. Review which albums are ready and which will be skipped; a blocked album does not block ready siblings.
3. Review the source policy: each successful one-image FLAC album deletes only its exact original after quick checks; a SACD ISO requires two matching extractions and full DSD verification. Multi-image, failed, and canceled albums retain their originals.
4. Select the start button. Album Fixer displays the hardware- and capacity-aware pipeline limits before starting; additional albums wait in bounded queues.
5. Review global readiness in **Preflight**, follow each album's phase and percent in **Albums**, then review the timeline and **Report** tab.

## Required tools

- Optional: Codex CLI and the installed album-fixer skill, used only when required metadata or cover art is missing after the local split.
- Optional: a Discogs personal access token in the `DISCOGS_TOKEN` environment variable for direct Discogs database search. MusicBrainz, Apple Music, and public Discogs records linked by MusicBrainz do not require this token.
- `ffmpeg` and `ffprobe` for FLAC workflows.
- `ffprobe` and TagLibSharp-backed DSF tagging for the verified SACD workflow.
- `sacd_extract` for SACD ISO layout inspection and DSF extraction. Album Fixer stages an `id3tag=0` configuration so hashes compare the untagged extraction payloads.

Album Fixer does not silently download tools. Missing FFmpeg tools block FLAC runs, and missing `ffprobe` or `sacd_extract` blocks SACD ISO runs. A missing optional Codex fallback matters only when required metadata is actually absent.

## Build and test

```powershell
dotnet build AlbumFixer.release.slnx -c Release
dotnet run --project tests\AlbumFixer.Core.SmokeTests\AlbumFixer.Core.SmokeTests.csproj -c Release
```

The smoke suite covers FLAC+CUE classification, multi-album batch planning, partial preflight admission, bounded parallel execution and failure isolation, guarded legacy-output cleanup, repair-only precedence, verified local staging, real local splits with tags and embedded artwork, the no-Codex path, terminal reports, progress parsing, and the metadata-only Codex boundary.

The runner uses the documented stable `codex exec --json` JSONL interface. See the [Codex developer command reference](https://developers.openai.com/codex/cli/reference).
