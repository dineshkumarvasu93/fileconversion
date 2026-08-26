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
{OutputRtfBasePath}\2026-08-01\patient_doc_001.rtf   converted output
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

75 tests cover Base64 decoding, encoding detection, error classification, CLI parsing, configuration
validation, archive/error file handling, CSV escaping, the run lock, pre-flight, and end-to-end
pipeline runs (including retry-then-error, an undecodable envelope, checkpointing, resume and
cancellation) against the real Telerik converter — so a licence problem fails the test run, not
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
| `Dashboard.RefreshIntervalSeconds` | `5` | Dashboard repaint interval |
| `Dashboard.EnableConsoleDashboard` | `true` | Off for unattended/service runs |

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
