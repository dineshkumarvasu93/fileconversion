# Cerner to Epic Migration — Stage 1 (XHTML → RTF)

Implementation of **Stage 1 only** from `CernerToEpic-Migration-Design-Document.md`: read Cerner
XHTML documents from date-wise input folders, Base64-decode them, convert them to RTF with Telerik
Document Processing, and file the results, with batching, retries, reporting and checkpoint/resume
around it.

Input documents arrive **Base64-encoded**: the bytes on disk are an envelope, and the XHTML is
inside it. Each file is decoded before anything else touches it, and a file whose envelope cannot be
decoded is moved to the error folder without being converted.

Stage 2 (RTF → HL7) is **not** in this build. Per section 17 of the design document, the metadata
that PID/PV1/TXA need is still unsourced, so `--stage 2` and `--stage both` are rejected with an
explanatory error instead of producing HL7 messages with defaulted patient data.

**Target framework:** `net9.0` — the newest SDK installed on the current dev machine. The design
document calls for the latest LTS, which is now .NET 10; retargeting is a one-line change in
[CernerToEpicMigration.csproj](CernerToEpicMigration.csproj) once the .NET 10 SDK is available on the
build and migration servers.

## What it does

| Design document | Implemented by |
| --- | --- |
| 8.1 XHTML → RTF conversion | [Processing/TelerikXhtmlToRtfConverter.cs](Processing/TelerikXhtmlToRtfConverter.cs) |
| Base64 input decoding | [Processing/Base64InputDecoder.cs](Processing/Base64InputDecoder.cs), [Processing/XhtmlDocumentReader.cs](Processing/XhtmlDocumentReader.cs) |
| 7.1–7.4 folder layout and file lifecycle | [Processing/FileDiscoveryService.cs](Processing/FileDiscoveryService.cs), [Processing/FileManager.cs](Processing/FileManager.cs) — flat *and* patient-nested date folders |
| 9 threading, batching | [Processing/Stage1Pipeline.cs](Processing/Stage1Pipeline.cs) (`Parallel.ForEachAsync`) |
| 10 retry and error categories | [Processing/ErrorClassifier.cs](Processing/ErrorClassifier.cs), `Stage1Pipeline.ProcessFileAsync` |
| 11.1 console dashboard | [Monitoring/ConsoleDashboard.cs](Monitoring/ConsoleDashboard.cs), [Monitoring/MetricsCollector.cs](Monitoring/MetricsCollector.cs) |
| 11.2–11.4 reports and logging | [Reporting/ReportWriter.cs](Reporting/ReportWriter.cs), [Reporting/RollingCsvWriter.cs](Reporting/RollingCsvWriter.cs), Serilog file sinks |
| 13 configuration and CLI | [appsettings.json](appsettings.json), [Cli/CommandLineOptions.cs](Cli/CommandLineOptions.cs) |
| 14.3 checkpoint / resume | [State/CheckpointService.cs](State/CheckpointService.cs) |

## Folder layout

```
{InputBasePath}\2026-08-01\doc_001.xhtml               input (Base64-encoded XHTML)
                          \patient_001\doc_a.xhtml     ...or nested one level by patient
                          \archive\                    successfully converted originals
                          \error\                      failed originals + <name>.error.log
{OutputRtfBasePath}\2026-08-01\doc_001.rtf             converted output
                             \patient_001\doc_a.rtf    patient folder is mirrored, not flattened
{ReportBasePath}\migration_report_{timestamp}.csv      summary + per-folder breakdown
                \error_report_{timestamp}_001.csv      one row per failed document
                \file_trace_{timestamp}_001.csv        one row per document (opt-in)
                \checkpoint.json                       written after every batch
{LogBasePath}\migration_{yyyyMMdd}.log                 full run log
             \errors_{yyyyMMdd}.log                    warnings and above only
```

Both shapes work in the same run, and no setting selects between them — a date folder is walked to
whatever depth it has. Two rules make that safe:

- **`archive` and `error` are skipped**, but only directly under a date folder, which is the only
  place they are created. Without that, a second run would re-convert the entire archive — which
  with `ArchiveOnSuccess` on eventually holds every document the migration has ever processed.
- **The sub-folder is mirrored, never flattened**, into the output, the archive and the error
  folder. Flattening would collide the moment two patients each have a `doc_1.xhtml`; the collision
  handler renames rather than overwrites, so nothing would be lost — but `doc_1_437.rtf` no longer
  says whose it was. The error report carries a `Sub Folder` column for the same reason.

If no date sub-folders exist at all, the input base path itself is processed as a single folder and
a warning is logged. Once even one date folder exists, loose files sitting directly in
`InputBasePath` are **not** processed — they belong to no date folder, so there is nowhere correct
to put them. That used to be a silent skip; it is now a warning naming the pattern and the path.

## Production safeguards

Checks that run before a single document is touched, so a broken environment costs seconds
rather than a failed overnight run:

| Guard | Behaviour |
| --- | --- |
| **Run lock** | One run per report folder. A second launch exits with code 2 instead of racing the first over the same files. |
| **Pre-flight** | Input readable; output, report and log folders writable (probe file); a real one-document Telerik conversion as a licence/runtime smoke test. Any failure exits with code 2. |
| **Disk space** | The scan totals the input size and warns if the output volume has less than ~1.2x that free. Advisory — the operator decides. |
| **Checkpoint safety** | A run started *without* `--resume` renames any existing `checkpoint.json` to `checkpoint_{timestamp}.json` rather than overwriting the record of what was done. |
| **Signals** | `Ctrl+C` and `SIGTERM` (service stop, `kill`) both stop after in-flight files, with the checkpoint and reports written. |
| **Base64 envelope** | Every input file is Base64-decoded first. Line-wrapped envelopes and a leading BOM are accepted; anything that is not decodable Base64 (including unwrapped markup) is a permanent failure and goes straight to the error folder — never to the converter. |
| **Encoding** | The *decoded* payload is decoded by BOM, then by declared XML/meta charset, then UTF-8, with a windows-1252 fallback for legacy bytes. Accents, degree and micro signs survive as RTF `\uN` escapes. |
| **Error report** | Written with a shared handle and auto-flush, so it can be opened or tailed mid-run; a failure to write it is logged and never aborts processing. |

## Tests

```bash
dotnet test          # from the repository root (CernerToEpicMigration.sln)
```

124 tests cover Base64 decoding, encoding detection, the XHTML content gate, error
classification, CLI parsing, configuration validation, archive/error file handling, error report
columns and part splitting, search-pattern quarantine, patient-nested folders (mirroring, name
collisions across patients, and archive/error not being re-processed), CSV escaping, the run lock,
pre-flight, and
end-to-end pipeline runs (including retry-then-error, an undecodable envelope, checkpointing,
resume and cancellation) against the real Telerik converter — so a licence problem fails the test
run, not production.

## Running

```bash
dotnet run -- --input D:\Migration\Input --output D:\Migration\Output\rtf
dotnet run -- --dry-run                       # scan and report only, nothing is touched
dotnet run -- --date-folder 2026-08-01        # one date folder
dotnet run -- --resume                        # skip date folders already completed
dotnet run -- --help
```

Command-line values override `appsettings.json`. Any setting can also be overridden with an
environment variable, e.g. `MigrationConfig__ReportBasePath` or
`MigrationConfig__Processing__MaxDegreeOfParallelism`.

Exit codes: `0` all converted · `1` finished with failures, or interrupted · `2` bad arguments or
configuration · `3` fatal error (run stopped early).

`Ctrl+C` stops after the in-flight files; the checkpoint is saved and the reports are still written,
so `--resume` picks up from there.

### Publish for a migration server

```bash
dotnet restore -r win-x64                                          # add -s <feed> if the corporate feed is down
dotnet publish -c Release -r win-x64 --self-contained --no-restore -o ./publish
```

Produces a ~80 MB folder (212 files) that runs with no .NET installed on the target. The Telerik
licence is compiled into the binary at build time, so `telerik-license.txt` is **not** deployed —
but the build machine must have a valid one, and the binary must be rebuilt when the licence is
renewed. Verified: the published `CernerToEpicMigration.exe` converts and reports correctly.

### Deployment checklist

1. Publish from a build machine with a current Telerik licence; note the version in the banner.
2. Copy the `publish` folder to the migration server and set the paths in `appsettings.json`
   (or pass `--input` / `--output` and the `MigrationConfig__*` environment variables).
3. Confirm the service account has read/write on input, output, report and log folders.
4. Run `--dry-run` first: it reports the file count and total size per date folder and runs the
   folder checks without converting anything.
5. Run one date folder (`--date-folder`) and have a clinician or the Epic interface team confirm the
   RTF imports correctly before the bulk run.
6. Start the bulk run. Watch the dashboard, or `migration_{date}.log` if the console is redirected.
7. On interruption, restart with `--resume`.

## Configuration

`appsettings.json` keeps only section headings — the reasoning lives here. Precedence, lowest to
highest: the file < `MigrationConfig__*` environment variables < the command line. A server-specific
value belongs in an environment variable, not an edited copy of the file, so the file stays
identical on every box and a support question stays answerable.

`//` comments are legal in `appsettings.json`: the .NET configuration provider skips them. The
editor's strict JSON schema flags them as errors anyway, which [.vscode/settings.json](../.vscode/settings.json)
turns off by treating `appsettings*.json` as `jsonc`.

### Paths

All four are required and are checked before a document is touched — input readable, the other
three writable (pre-flight writes a probe file into each).

| Setting | Default | Notes |
| --- | --- | --- |
| `InputBasePath` | — | Root holding the date-wise folders, e.g. `…\Input\2026-08-01\*.xhtml`. With no date sub-folders, this folder itself is processed as one and a warning is logged. |
| `OutputRtfBasePath` | — | Converted RTF; the date folder structure is mirrored underneath. |
| `ReportBasePath` | — | Summary CSV, error report, file trace, `checkpoint.json`, run lock. |
| `LogBasePath` | — | Serilog files. Sits **inside** `ReportBasePath` by default — move it out if the two have different retention, or clearing one takes the other with it. |

### Logging

Two sinks over the same events, split by level; both roll daily **and** at `LogFileSizeLimitMb`.

| Setting | Default | Notes |
| --- | --- | --- |
| `LogRetainedFileCountLimit` | `30` | Files kept per sink, oldest deleted first. |
| `LogFileSizeLimitMb` | `256` | Roll at this size as well as daily. Serilog's own default is a 1 GB cap with rolling **off**, which makes the log go silent mid-run rather than rolling — a run that starts failing writes a stack trace per document and reaches that in a day. |
| `EnableSeparateErrorLog` | `true` | Also write `errors_{date}.log` at Warning and above. That is the file you read to review failures; the full log is for reconstructing the run. |

### Processing — threading and batching

| Setting | Default | Notes |
| --- | --- | --- |
| `MaxDegreeOfParallelism` | `0` | 0 = `Environment.ProcessorCount`. Don't tune blind: the summary reports peak and average concurrency, and a low average means the workers were **starved** (I/O, antivirus, a slow share), not that this number is too small. |
| `BatchSize` | `1000` | Files per batch. A checkpoint is written after each one, so this is also the most work a kill or crash can cost. |

### Processing — retry

| Setting | Default | Notes |
| --- | --- | --- |
| `MaxRetryCount` | `3` | **Total** attempts per file, not extra ones — `1` means a single try. Only `Transient` failures retry; `Permanent` ones fail on the first attempt. |
| `RetryDelayMs` | `500` | Multiplied by the attempt number: 500 ms, 1000 ms, … |

### Processing — which files are picked up

| Setting | Default | Notes |
| --- | --- | --- |
| `FileSearchPattern` | `*.xhtml` | Input filter. Non-matching files are handled by `QuarantineUnmatchedFiles`. |
| `PreScanForEstimates` | `true` | Count everything up front so progress and the time estimate cover the whole run. Costs one extra walk of the input tree — minutes rather than seconds on a network share, which is where you'd turn it off. |
| `QuarantineUnmatchedFiles` | `true` | Move files the pattern does **not** match to the error folder instead of ignoring them. Off, those files are invisible: never counted, never reported, and the folder still signs off clean. Turn off where another process legitimately keeps its own files in the input folders. |

### Processing — conversion

| Setting | Default | Notes |
| --- | --- | --- |
| `ConversionTimeoutSeconds` | `60` | Telerik import/export timeout per document. Guards against one pathological file stalling a worker for the rest of the run. |
| `ValidateXhtmlContent` | `true` | Reject payloads holding no XHTML. Telerik's importer never rejects anything — an empty file and binary noise both export a valid but meaningless RTF that counts as a success. Deliberately narrow: it asks whether there is markup at all, not whether it's good. |
| `OverwriteExistingRtf` | `true` | False makes an already-present output a failure instead — how you prove a re-run isn't silently redoing finished work. |
| `EncodeRtfOutputAsBase64` | `false` | Wrap the RTF in a Base64 envelope. The name and `.rtf` extension are unchanged — only the bytes differ — so whatever consumes the output must switch over at the same time. |

### Processing — what happens to the input file afterwards

| Setting | Default | Notes |
| --- | --- | --- |
| `ArchiveOnSuccess` | `true` | Move converted inputs to `{dateFolder}\archive`. False leaves them in place, which makes `--resume` fall back to checking whether each RTF already exists. |

### Processing — reports and diagnostics

| Setting | Default | Notes |
| --- | --- | --- |
| `MaxErrorLogFiles` | `0` | Cap on per-file `.error.log` files; 0 = no cap. Every failure is in the error report and run log regardless. **Worth capping before a bulk run**: on a systemic fault, writing one file per failure is what turns a slow run into a stalled one. |
| `MaxReportRowsPerFile` | `250000` | Rows per part of the error report and file trace; 0 = one unsplit file. Parts are numbered `_001`, `_002`, … each with its own header row. ~250k rows is roughly a 40 MB error part. |
| `EnableFileTrace` | `true` | One row per document: worker slot, thread, start, end, duration, attempts, outcome. A **tuning diagnostic** — how you prove the configured workers were busy. ~130 bytes a row, so ~130 MB per million documents; turn it off for the bulk run unless you want the trace. |

### Dashboard

| Setting | Default | Notes |
| --- | --- | --- |
| `Dashboard.RefreshIntervalSeconds` | `5` | Console repaint interval. |
| `Dashboard.EnableConsoleDashboard` | `true` | Off for unattended or service runs — progress still goes to the run log, one line per completed batch. |

## The error report

Failed documents are moved into one `error` folder per date folder. Once a run produces thousands
of them those folders stop being reviewable — which is what `error_report_{timestamp}_001.csv` is
for. It is the single list of everything that failed, written row by row as it happens, and it
opens while the run is still going.

| Column | What it answers |
| --- | --- |
| `Row` | Sequential across the whole run, so "row 41,233" identifies one failure. |
| `Error Time Utc` | When it failed. |
| `Batch Id` | e.g. `2026-08-01#0007` — joins the row to the batch line in the run log and to the file trace. `scan` means the file was rejected before batching. |
| `Date Folder` | Which input folder it came from. |
| `Sub Folder` | The patient folder within it, or empty on a flat drop. This is what makes a row actionable when two patients have a document of the same name. |
| `File Name` | What it is called **now**, inside the error folder. |
| `Actual File Name` | What it arrived as. Differs from `File Name` only when a re-run collided and the move had to suffix it `_1`, `_2` … — which is exactly when you need both to find it. |
| `File Size Bytes` | Size on disk; `0` when it could not be read. |
| `Source` | `Conversion` (it was converted and failed) or `Discovery` (rejected up front). |
| `Error Category` | `Transient` / `Permanent` / `Fatal`. |
| `Reason` | Plain language, with a handful of distinct values — this is the column you group by to see that 9,000 rows are one problem. |
| `Attempts` | How many tries it got. |
| `Error Log File` | The `.error.log` sitting beside it, or `(none)` if `MaxErrorLogFiles` suppressed it. |
| `Moved To Error` | `No` means it is **still in the input folder** — see `Move Error`. |
| `Error File Path` | Full path it now has. |
| `Input File Path` | Full path it had. |
| `Error Type`, `Error Message`, `Move Error` | The detail, out at the right where it does not push the sortable columns off screen. |

The report **splits into numbered parts** at `Processing.MaxReportRowsPerFile` rows
(`_001`, `_002`, …), each with its own header row. A single CSV of a million failures will not
open in a spreadsheet and is slow to search; parts stay openable and two people can review
different parts at once. The end-of-run console output names the part count, and the summary CSV
carries `Error Report Rows` and `Error Report Parts` so nobody reads part 1 and stops.

### Files that used to be invisible

Two classes of file were previously neither converted nor reported, and a date folder could be
signed off as complete with them still sitting in it:

- **Search-pattern mismatches.** A `.pdf`, a mistyped `.xhtm`, a half-renamed `.xhtml.bak` —
  discovery filtered them out, so nothing ever counted or mentioned them. They are now moved to
  the error folder with `Source=Discovery` and a reason naming the pattern they missed. They are
  added to the folder total before they are failed, so `found = succeeded + failed` still holds.
  Turn this off with `Processing.QuarantineUnmatchedFiles` where another process legitimately
  keeps its own files in the input folders.
- **Files with no XHTML in them.** Telerik's HTML importer never rejects anything: an empty
  payload and a run of binary noise both import and export a valid but meaningless RTF, which
  counted as a success everywhere. `Processing.ValidateXhtmlContent` adds the one content gate —
  deliberately narrow, since a Cerner export is a fragment as often as a full `<html>` document,
  so it asks whether there is any markup at all, not whether the markup is good. Empty, whitespace
  and binary payloads fail as `Permanent` and go to the error folder.

## Logging

Two sinks over the same events, both rolled daily *and* at `LogFileSizeLimitMb` (Serilog's own
default is a 1 GB cap with rolling **off**, which makes the log go silent mid-run instead of
rolling — a bulk run that starts failing writes a stack trace per document and reaches that in a
single day):

| File | Level | Use |
| --- | --- | --- |
| `migration_{yyyyMMdd}.log` | Information and above | Reconstructing what the run did. Large: progress, per-batch lines, per-file debug. |
| `errors_{yyyyMMdd}.log` | Warning and above | Reading the failures. Small enough to read end to end, instead of finding a few thousand warnings among millions of progress lines. |

`LogRetainedFileCountLimit` (default 30) caps how many of each are kept. Set
`EnableSeparateErrorLog` to `false` for one log only.

**Which file do I search?** Start with the error report — it is structured, so filtering and
grouping beat grepping. Take the `Batch Id` from the row and search `migration_*.log` for it to
see what the run was doing around that batch. Use `errors_*.log` when the question is about the
run rather than a document (a fatal error, a folder that could not be read, `MaxErrorLogFiles`
being hit). The optional `file_trace_*_NNN.csv` splits on the same `MaxReportRowsPerFile` setting
and carries the same `Batch Id`, so the three join up.

## Error handling

Failures are classified before anything is retried (design document 10.1):

- **Transient** (file locked, I/O timeout, memory pressure) — retried up to `MaxRetryCount` with an
  increasing delay.
- **Permanent** (undecodable Base64 envelope, malformed or unsupported document) — failed
  immediately, no retries.
- **Fatal** (disk full, access denied, licensing) — the run stops; input files are left in place so
  nothing is lost, and the exit code is `3`.

A file that exhausts its attempts is moved to `{dateFolder}\error` with a `.error.log` beside it. If
the file cannot be moved (still locked by another process), it stays in the input folder and the
error log records that — it is never silently dropped.

## Telerik licence

`telerik-license.txt` sits in this project folder; the Telerik build task also accepts
`TELERIK_LICENSE_PATH` / `TELERIK_LICENSE` environment variables, which is the better option on a
build server. A licensing problem is treated as a fatal error at runtime rather than a per-file
failure.

## Known limits before the bulk run

- **Telerik's HTML importer still never rejects anything.** Once the envelope is off, malformed
  markup imports and produces an RTF. `ValidateXhtmlContent` now catches the empty and binary
  payloads before the importer sees them, but it only asks whether there is markup at all — markup
  that is present and wrong still converts. Stage 1 reports *conversion* failures and the crudest
  *content* failures, not content correctness, so step 5 of the checklist is not optional.
- **Windows long paths.** Paths over 260 characters need `LongPathsEnabled=1` on the server;
  without it those documents fail as `PathTooLongException` (permanent, one file at a time).
- **Memory.** The file list of the date folder being processed is held in memory (roughly 200 bytes
  per path — about 200 MB for a folder of a million documents). Split very large folders if that
  matters on the target server.
- **Throughput figures** in the design document assume 2–3 MB documents on SSD. Measure with one
  real date folder before committing to a schedule.
- **Antivirus** on the input/output folders can dominate runtime; consider an exclusion for the
  migration paths.

## Notes and caveats

- Telerik's HTML importer is deliberately lenient: malformed markup imports without throwing and
  produces an RTF file. Empty and binary payloads are stopped by `ValidateXhtmlContent` before they
  reach it; everything past that gate is the importer's judgement, so validate a representative
  sample of the RTF output before a bulk run (design document risk 1).
- Base64 decoding runs before the converter, so a broken envelope is caught cleanly; it is not a
  content check, and a well-formed envelope around junk still reaches the importer.
- RTF is written without a byte order mark, so the file starts with `{\rtf1` as importers expect.
- Each output file is written to `<name>.rtf.tmp` and then renamed, so an interrupted run cannot
  leave a partially written RTF behind.
- Restore uses nuget.org for the Telerik and Microsoft packages
  (`dotnet restore -s https://api.nuget.org/v3/index.json` when the corporate feed is unreachable).
