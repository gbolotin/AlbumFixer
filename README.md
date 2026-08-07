# Album Fixer

Album Fixer is a native Windows app that locally splits FLAC+CUE album images into tagged tracks, shows the 12-stage transaction, and produces a readable conversion report.

## What it does

- Classifies FLAC+CUE image splits, SACD/DSD extraction, existing-track metadata repair, and ambiguous sources. Verified end-to-end write-back is currently enabled for FLAC+CUE; other modes stop before changing files.
- Gives repair-only mode precedence when separated tracks coexist with an image.
- Checks required audio tools, a local fixed Windows Temp volume, and conservative staging capacity before starting. Codex is not checked during a complete local run.
- Copies the selected album and audio tools into a unique Windows Temp job and verifies source size and SHA-256 before processing.
- Parses the CUE and splits all tracks in one local FFmpeg process, writing locally available tags and embedding local artwork without starting Codex.
- After every track exists, reads the metadata-gap handoff. Complete local metadata skips Codex entirely; only named missing required fields trigger Codex discovery, skill staging, and one metadata-only process.
- Uses quick ffprobe container, required-tag, and embedded-artwork checks; writes through destination-side staging and compares SHA-256 copy hashes. Decoded PCM/MD5 comparison is skipped, then the exact original FLAC image is deleted as requested.
- Shows phase progress, live activity, inventory, final verification status, output counts, source disposition, and formatted report JSON.
- Has one source policy: successful FLAC+CUE runs delete the exact inventoried image after final quick checks; failed, incomplete, canceled, or uncertain runs keep it.
- Retains sources whenever a job is failed, incomplete, canceled, uncertain, or missing required proof.

## Run

Open:

`publish\win-x64\Album Fixer.exe`

The packaged app is self-contained for 64-bit Windows and does not require a separate .NET installation.

1. Choose one album folder, not the whole music library.
2. Select **Scan album** and resolve any blocked preflight checks.
3. Review the deletion warning: PCM/MD5 comparison is skipped and the exact original FLAC image is deleted after successful final quick checks.
4. Select **Start split** and confirm permanent source deletion.
5. Review the progress timeline and the **Report** tab.

## Required tools

- Optional: Codex CLI and the installed album-fixer skill, used only when required metadata or cover art is missing after the local split.
- `ffmpeg` and `ffprobe` for FLAC workflows.
- `ffprobe` plus a safe DSF/DFF tagging path for DSD workflows.
- `sacd_extract` for SACD ISO extraction. A Sony DSD Disc image is probed separately and may not require it.

Album Fixer does not silently download tools. Missing FFmpeg tools block the local run; a missing optional Codex fallback matters only when required metadata is actually absent.

## Build and test

```powershell
dotnet build AlbumFixer.release.slnx -c Release
dotnet run --project tests\AlbumFixer.Core.SmokeTests\AlbumFixer.Core.SmokeTests.csproj -c Release
```

The smoke suite covers FLAC+CUE classification, multi-album blocking, repair-only precedence, verified local staging, a real one-process local split with tags and embedded artwork, the no-Codex path, terminal reports, progress parsing, and the metadata-only Codex boundary.

The runner uses the documented stable `codex exec --json` JSONL interface. See the [Codex developer command reference](https://developers.openai.com/codex/cli/reference).
