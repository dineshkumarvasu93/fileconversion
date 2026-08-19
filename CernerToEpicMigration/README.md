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
| 7.1–7.4 folder layout and file lifecycle | [Processing/FileDiscoveryService.cs](Processing/FileDiscoveryService.cs), [Processing/FileManager.cs](Processing/FileManager.cs) |
| 9 threading, batching | [Processing/Stage1Pipeline.cs](Processing/Stage1Pipeline.cs) (`Parallel.ForEachAsync`) |
| 10 retry and error categories | [Processing/ErrorClassifier.cs](Processing/ErrorClassifier.cs), `Stage1Pipeline.ProcessFileAsync` |
| 11.1 console dashboard | [Monitoring/ConsoleDashboard.cs](Monitoring/ConsoleDashboard.cs), [Monitoring/MetricsCollector.cs](Monitoring/MetricsCollector.cs) |
| 11.2–11.4 reports and logging | [Reporting/ReportWriter.cs](Reporting/ReportWriter.cs), Serilog file sink |
| 13 configuration and CLI | [appsettings.json](appsettings.json), [Cli/CommandLineOptions.cs](Cli/CommandLineOptions.cs) |
| 14.3 checkpoint / resume | [State/CheckpointService.cs](State/CheckpointService.cs) |

## Folder layout

```
{InputBasePath}\2026-08-01\patient_doc_001.xhtml     input (Base64-encoded XHTML)
                          \archive\                  successfully converted originals
                          \error\                    failed originals + <name>.error.log
{OutputRtfBasePath}\2026-08-01\patient_doc_001.rtf   converted output (plain RTF, or
                                                     Base64-encoded when the flag is on)
{ReportBasePath}\migration_report_{timestamp}.csv    summary + per-folder breakdown
                \error_report_{timestamp}.csv        one row per failed document
                \checkpoint.json                     written after every batch
{LogBasePath}\migration_{yyyyMMdd}.log               Serilog, 30 files retained
```

If no date sub-folders exist, the input base path itself is processed as a single folder and a
warning is logged.

## Production safeguards

Checks that run before a single document is touched, so a broken environment costs seconds
rather than a failed overnight run:

| Guard | Behaviour |
| --- | --- |
| **Run lock** | One run per report folder. A second launch exits with code 2 instead of racing the first over the same files. |
| **Pre-flight** | Input readable; output, report and log folders writable (probe file); a real one-document Telerik conversion as a licence/runtime smoke test. Any failure exits with code 2. |
| **Disk space** | The scan totals the input size and warns if the output volume has less than ~1.2x that free (~1.6x with `EncodeRtfOutputAsBase64` on). Advisory — the operator decides. |
| **Checkpoint safety** | A run started *without* `--resume` renames any existing `checkpoint.json` to `checkpoint_{timestamp}.json` rather than overwriting the record of what was done. |
| **Signals** | `Ctrl+C` and `SIGTERM` (service stop, `kill`) both stop after in-flight files, with the checkpoint and reports written. |
| **Base64 envelope** | Every input file is Base64-decoded first. Line-wrapped envelopes and a leading BOM are accepted; anything that is not decodable Base64 (including unwrapped markup) is a permanent failure and goes straight to the error folder — never to the converter. `Processing.EncodeRtfOutputAsBase64` puts the same envelope back on the output: a single-line ASCII Base64 string that `Convert.FromBase64String` reads directly. |
| **Encoding** | The *decoded* payload is decoded by BOM, then by declared XML/meta charset, then UTF-8, with a windows-1252 fallback for legacy bytes. Accents, degree and micro signs survive as RTF `\uN` escapes. |
| **Error report** | Written with a shared handle and flushed every 500 rows, every 2 seconds and at each batch boundary, so it can be opened or tailed mid-run without a disk round-trip per failure; a failure to write it is logged and never aborts processing. |
| **Log rolling** | Log files roll at 256 MB as well as daily, so a run that starts failing in bulk cannot silently stop logging part-way through a day. |

## Tests

```bash
dotnet test          # from the repository root (CernerToEpicMigration.sln)
```

83 tests cover Base64 decoding and Base64 output encoding, encoding detection, error classification, CLI parsing, configuration
validation, archive/error file handling (including repeated name collisions and the error-log cap),
CSV escaping, the run lock, pre-flight, and end-to-end pipeline runs (including retry-then-error, an
undecodable envelope, checkpointing, resume with and without archiving, a run without the pre-scan,
and cancellation) against the real Telerik converter — so a licence problem fails the test run, not
production.

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

### How `--resume` avoids redoing work

`checkpoint.json` records the date folders that finished end to end, and those are skipped outright.
Inside a folder that was interrupted, what stops the run redoing the documents it already converted
depends on `ArchiveOnSuccess`:

- **On (the default)** — converted documents have already moved to `{dateFolder}\archive`, so
  re-enumerating the input folder returns only what is left.
- **Off** — converted documents stay in the input folder, so the resumed run skips the ones that
  already have an RTF next to them. Each output is written to a temp file and renamed, so an RTF
  being there means the conversion finished. This costs one existence check per remaining file and
  only happens on a resume; a run *without* `--resume` converts everything regardless.

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

| Setting | Default | Notes |
| --- | --- | --- |
| `Processing.MaxDegreeOfParallelism` | `0` | 0 = `Environment.ProcessorCount` |
| `Processing.BatchSize` | `1000` | Progress granularity and checkpoint interval |
| `Processing.MaxRetryCount` | `3` | Total attempts per file, not extra attempts |
| `Processing.RetryDelayMs` | `500` | Multiplied by the attempt number: 500 ms, 1000 ms, … |
| `Processing.FileSearchPattern` | `*.xhtml` | Input filter |
| `Processing.ConversionTimeoutSeconds` | `60` | Telerik import/export timeout per document |
| `Processing.ArchiveOnSuccess` | `true` | Set false to leave inputs where they are |
| `Processing.OverwriteExistingRtf` | `true` | False makes an existing RTF a failure instead |
| `Processing.EncodeRtfOutputAsBase64` | `false` | Feature flag: write the RTF Base64-encoded instead of plain. The file name and the `.rtf` extension do not change — only the bytes inside — so the consumer has to be switched over at the same time |
| `Processing.PreScanForEstimates` | `true` | Count everything up front so progress and ETA cover the whole run. Off saves a full directory walk on a slow share; totals then fill in folder by folder and the disk-space check is skipped |
| `Processing.MaxErrorLogFiles` | `0` | Cap on per-file `.error.log` files, 0 = no cap. Every failure stays in the error report and the run log either way |
| `Dashboard.RefreshIntervalSeconds` | `5` | Dashboard repaint interval |
| `Dashboard.EnableConsoleDashboard` | `true` | Off for unattended/service runs |

### Tuning for a bulk run

| | Local SSD | Network share (SMB/NAS) |
| --- | --- | --- |
| `MaxDegreeOfParallelism` | `0` (= core count) — the conversion is CPU-bound | 2–3× core count — the workers are I/O-blocked, not CPU-bound |
| `BatchSize` | `1000` | `250`–`500`, for finer checkpointing; the barrier between batches costs very little |
| `PreScanForEstimates` | `true` | `false` if the up-front walk delays the first conversion by more than you want to wait |

Measure one real date folder before committing to a schedule: a million documents is roughly 9 hours
at 30 files/s and 28 hours at 10 files/s, and antivirus on the migration paths can dominate both
figures.

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
error log records that — it is never silently dropped. Set `MaxErrorLogFiles` to cap those per-file
logs on a run where a systemic problem is failing documents in bulk; the failures still reach the
error report CSV and the run log, and the documents are still moved out of the input folder.

## Telerik licence

`telerik-license.txt` sits in this project folder; the Telerik build task also accepts
`TELERIK_LICENSE_PATH` / `TELERIK_LICENSE` environment variables, which is the better option on a
build server. A licensing problem is treated as a fatal error at runtime rather than a per-file
failure.

## Known limits before the bulk run

- **Telerik's HTML importer never rejects anything.** Once the envelope is off, malformed markup,
  empty documents and binary content all import and produce an RTF. Base64 decoding is the only gate
  on the way in, and it only proves the envelope was intact — not that the payload is real XHTML. So
  Stage 1 reports *conversion* failures, not *content* correctness, and step 5 of the checklist is
  not optional.
- **Windows long paths.** Paths over 260 characters need `LongPathsEnabled=1` on the server;
  without it those documents fail as `PathTooLongException` (permanent, one file at a time).
- **Memory.** The file list of the date folder being processed is held in memory (roughly 200 bytes
  per path — about 200 MB for a folder of a million documents). Split very large folders if that
  matters on the target server.
- **Clear `archive\` before a full re-run of a date folder.** Re-running against an archive that
  still holds the previous run's documents collides on every filename. Nothing is lost — the copies
  are kept as `name_1`, `name_2` and so on — but every move takes the suffix path, and the archive
  ends up holding several copies of the same million documents.
- **One date folder holding a million documents is its own problem.** `archive\` and `error\` then
  hold up to a million entries too, and NTFS enumeration and moves degrade noticeably at that size.
  The date-wise layout normally keeps folders far smaller; if it does not, split them.
- **One process per report folder.** The run lock allows a single run per `ReportBasePath`, so
  sharding across machines means `--date-folder <name>` per machine, each with its own report
  folder. `ReportBasePath` has no command-line flag — set it per machine in `appsettings.json` or
  with `MigrationConfig__ReportBasePath`.
- **Throughput figures** in the design document assume 2–3 MB documents on SSD. Measure with one
  real date folder before committing to a schedule.
- **Antivirus** on the input/output folders can dominate runtime; consider an exclusion for the
  migration paths.

## Notes and caveats

- Telerik's HTML importer is deliberately lenient: malformed markup, empty files and even binary
  content import without throwing and produce an RTF file. Stage 1 therefore reports *conversion*
  failures, not *content* correctness — validate a representative sample of the RTF output before a
  bulk run (design document risk 1).
- Base64 decoding runs before the converter, so a broken envelope is caught cleanly; it is not a
  content check, and a well-formed envelope around junk still reaches the importer.
- RTF is written without a byte order mark, so the file starts with `{\rtf1` as importers expect.
- Each output file is written to `<name>.rtf.tmp` and then renamed, so an interrupted run cannot
  leave a partially written RTF behind.
- Restore uses nuget.org for the Telerik and Microsoft packages
  (`dotnet restore -s https://api.nuget.org/v3/index.json` when the corporate feed is unreachable).
