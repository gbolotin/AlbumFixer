# Album Fixer

Album Fixer is a native Windows control center for the installed `album-fixer` Codex skill. It inventories one album, blocks unsafe starts, streams the skill's 12-stage workflow, and turns `conversion-report.json` into a readable completion report.

## What it does

- Classifies FLAC+CUE image splits, SACD/DSD extraction, existing-track metadata repair, and ambiguous sources.
- Gives repair-only mode precedence when separated tracks coexist with an image.
- Checks Codex, the installed skill, required audio tools, a local fixed Windows Temp volume, and conservative staging capacity before starting.
- Runs Codex non-interactively with `workspace-write`, `ask-for-approval=never`, the album as the workspace, and only the exact job folder added as another writable directory.
- Shows phase progress, live activity, inventory, final verification status, output counts, source disposition, and formatted report JSON.
- Requires an explicit confirmation before the skill's default verified-source deletion policy can run.
- Retains sources whenever a job is failed, incomplete, canceled, uncertain, or missing required proof.

## Run

Open:

`publish\win-x64\Album Fixer.exe`

The packaged app is self-contained for 64-bit Windows and does not require a separate .NET installation.

1. Choose one album folder, not the whole music library.
2. Select **Scan album** and resolve any blocked preflight checks.
3. Choose whether verified originals may be deleted after the final pass.
4. Select **Start safe run** and confirm the policy.
5. Review the progress timeline and the **Report** tab.

## Required tools

- Codex CLI, authenticated and able to load `C:\Users\gbolotin\.codex\skills\album-fixer\SKILL.md`.
- `ffmpeg` and `ffprobe` for FLAC workflows.
- `ffprobe` plus a safe DSF/DFF tagging path for DSD workflows.
- `sacd_extract` for SACD ISO extraction. A Sony DSD Disc image is probed separately and may not require it.

Album Fixer does not silently download tools. Missing requirements block or safely stop the run, matching the skill contract.

## Build and test

```powershell
dotnet build AlbumFixer.release.slnx -c Release
dotnet run --project tests\AlbumFixer.Core.SmokeTests\AlbumFixer.Core.SmokeTests.csproj -c Release
```

The smoke suite covers FLAC+CUE classification, repair-only precedence, progress parsing, report summarization, and safe Codex command flags.

The runner uses the documented stable `codex exec --json` JSONL interface. See the [Codex developer command reference](https://developers.openai.com/codex/cli/reference).
