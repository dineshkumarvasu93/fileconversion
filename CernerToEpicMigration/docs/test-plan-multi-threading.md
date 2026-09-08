# Test Plan — Multi-Threaded Conversion

Validation procedure for the testing team: how to build the application, run it at 1 to 5 threads,
confirm the results are **correct** and **complete**, and record the outcome.

> **Scope.** Black-box validation of the shipped application. Every check below is made from the
> input tree, the output tree and the CSV reports — no debugger, no code reading, no access to the
> source required. Sections 1 to 7 are setup; the test cases are sections 8 to 11.

Companion to [thread-count-tuning.md](thread-count-tuning.md), which is the engineering procedure
for *choosing* the thread count. This document is how QA *signs off* on it.

---

## 1. What is being validated

Raising `MaxDegreeOfParallelism` makes several documents convert at the same time. Three claims
follow from that, and each one is a test case below:

| # | Claim                                                                                                                                                  | Test case                                                       |
| - | ------------------------------------------------------------------------------------------------------------------------------------------------------ | --------------------------------------------------------------- |
| 1 | **Correctness is independent of the thread count.** The same inputs produce the same outputs whether one thread runs or five.                    | [§8 TC-01](#8-tc-01--output-is-identical-at-every-thread-count) |
| 2 | **Nothing is lost.** Every input document ends up either converted-and-archived or in the error folder, and the reports account for all of them. | [§9 TC-02](#9-tc-02--every-document-is-accounted-for)           |
| 3 | **Failures still behave.** A bad document fails cleanly at any thread count instead of taking the run down.                                      | [§10 TC-03](#10-tc-03--failure-handling-under-concurrency)      |

Performance is measured separately ([§11 TC-04](#11-tc-04--performance-matrix)) and is **not** a
pass/fail criterion on its own — a faster run that loses a document fails this plan.

---

## 2. Prerequisites

| Item            | Requirement                                                                                   | How to check                                      |
| --------------- | --------------------------------------------------------------------------------------------- | ------------------------------------------------- |
| .NET SDK        | 9.0 or later (validated on 9.0.301)                                                           | `dotnet --version`                              |
| Source          | The repository, on the branch or tag under test                                               | `git log -1` — record the commit               |
| Telerik licence | A valid licence for Telerik Document Processing                                               | See the note below                                |
| Machine         | The migration server, or hardware matching it                                                 | Results from a laptop do not transfer             |
| Other load      | None. Close other applications; do not run during a scheduled antivirus scan or backup window |                                                   |
| Disk            | Free space of at least 1.5× the test corpus                                                  | The run prints a capacity line and warns if short |

> **Telerik licence.** The licence file is `telerik-license.txt` at the repository root. If it is
> missing or expired, the build prints `TKL004: Unable to locate licenses for all products` and
> `TKL105`. Treat those warnings as a **blocker to be resolved before testing** — report them to the
> development team rather than working around them, because licence state can affect conversion
> output and would invalidate TC-01.

Record every row above on the sign-off sheet ([§12](#12-recording-and-sign-off)). A result without
the environment recorded is not reproducible and does not count.

---

## 3. Build and publish the application under test

Do this **once** per build under test. Every test case then runs the same published `.exe` — never
rebuild in the middle of a cycle.

### Step 3.1 — Get the source

```powershell
cd 'D:\Davita\Document Process POC\docs\telerik - 3\fileconversion'
git log -1 --oneline      # record this commit on the sign-off sheet
```

### Step 3.2 — Restore and build

```powershell
dotnet restore
dotnet build --configuration Release
```

**Expected:** `Build succeeded.` The Telerik licensing lines are the only warnings that should
appear. Any `error CS…` is a blocker — stop and report it.

### Step 3.3 — Run the developer test suite

It must pass before functional testing begins:

```powershell
dotnet test --configuration Release
```

**Expected:** `Passed!  - Failed: 0`. Record the passed/total counts on the sign-off sheet.

### Step 3.4 — Publish

```powershell
dotnet publish CernerToEpicMigration\CernerToEpicMigration.csproj `
    --configuration Release `
    --output D:\Migration\bin
```

The Visual Studio publish profile
([FolderProfile.pubxml](../Properties/PublishProfiles/FolderProfile.pubxml)) targets
`D:\Publish\CernerToEpicMigration` instead. Either is fine — use one location for the whole cycle
and record which.

### Step 3.5 — Verify the published folder

```powershell
(Get-ChildItem D:\Migration\bin).Count                   # about 28 files
Test-Path D:\Migration\bin\CernerToEpicMigration.exe     # True
Test-Path D:\Migration\bin\appsettings.json              # True
Get-ChildItem D:\Migration\bin -Filter Telerik.*.dll     # 4 Telerik assemblies
```

**Expected:** `CernerToEpicMigration.exe`, `appsettings.json`, and `Telerik.Documents.Core.dll`,
`Telerik.Documents.Flow.dll`, `Telerik.Documents.DrawingML.dll`, `Telerik.Licensing.Runtime.dll` all
present.

> The build is **framework-dependent** — the machine that runs it needs the .NET 9 runtime
> installed. If the migration server has no .NET runtime, ask the development team for a
> self-contained publish instead.

### Step 3.6 — Confirm the executable runs

```powershell
cd D:\Migration\bin
.\CernerToEpicMigration.exe --help
```

**Expected:** the usage screen, listing the options and the exit codes:

```
Options:
  --input <path>          Override the input base path (folder of date-wise folders)
  --output <path>         Override the RTF output base path
  --threads <count>       Override max parallelism (default: processor count)
  --batch-size <size>     Override batch size (default: 1000)
  --date-folder <name>    Process a single date folder only, e.g. 2026-08-01
  --dry-run               Scan and report without converting or moving anything
  --resume                Skip date folders already completed per checkpoint.json

Exit codes:
  0  All discovered files converted successfully (or nothing to do)
  1  Completed, but one or more files failed and were moved to the error folder
  2  Invalid arguments or configuration
  3  Fatal error - processing stopped early
```

Those exit codes are pass criteria in TC-02 and TC-03. Read one with `$LASTEXITCODE` **immediately**
after a run — any other command overwrites it.

---

## 4. Test data

Build a **master copy** that is never processed and never written to:

```
D:\Migration\Master\        <- pristine test corpus, read-only in practice
D:\Migration\Input\         <- rebuilt from Master before every run
D:\Migration\Output\rtf\    <- deleted before every run
D:\Migration\Reports\       <- accumulates; one report set per run
D:\Migration\bin\           <- the published application from §3
```

| Requirement                               | Why it matters to this plan                                                                                                                                                                                                                            |
| ----------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| **At least 2,000 documents**        | Smaller runs measure start-up, not conversion.                                                                                                                                                                                                         |
| **Representative document sizes**   | Conversion cost is driven by document size.                                                                                                                                                                                                            |
| **Representative folder shape** | Date folders are processed one at a time, with a barrier between them. A folder holding fewer documents than the thread count cannot keep the workers busy, so a corpus of many small folders scales quite differently from one of a few large ones — copy the real shape, not just the file count. |
| **Include known-bad documents**     | Required by[§10 TC-03](#10-tc-03--failure-handling-under-concurrency). Add at least: one empty file, one that is not valid Base64, one whose Base64 decodes to binary, and one file whose extension the search pattern does not match (e.g. `.pdf`). |
| **Record the exact document count** | It is the expected value for every reconciliation check.                                                                                                                                                                                               |

---

## 5. Configuration under test

Edit `D:\Migration\bin\appsettings.json` — the copy **beside the .exe**, not the one in the source
tree. Set these once for the whole cycle and **do not change them between runs**:

```jsonc
"MigrationConfig": {
  "InputBasePath":     "D:\\Migration\\Input",
  "OutputRtfBasePath": "D:\\Migration\\Output\\rtf",
  "ReportBasePath":    "D:\\Migration\\Reports",
  "LogBasePath":       "D:\\Migration\\Reports\\Logs",

  "Processing": {
    "MaxDegreeOfParallelism": 0,     // varied per run via --threads
    "BatchSize": 1000,
    "ArchiveOnSuccess": true,
    "OverwriteExistingRtf": true,
    "ValidateXhtmlContent": true,
    "QuarantineUnmatchedFiles": true,
    "EnableFileTrace": true          // per-document evidence; set false again for production
  },

  "Dashboard": {
    "EnableConsoleDashboard": false  // no repaint during a timed run
  }
}
```

The thread count is varied **on the command line only** — `--threads 1`, `--threads 2` and so on.
Every report records what it ran with in its `Threads` header row, which is the evidence that a run
was the test case the sheet says it was.

> **Changing any other setting mid-cycle invalidates the whole cycle.** The reports will not warn
> you. If a setting must change, restart from TC-01.

---

## 6. Verify the setup before testing

`--dry-run` scans and reports without converting, moving or writing anything. Use it to prove the
configuration is right *before* spending a test cycle on it:

```powershell
cd D:\Migration\bin
.\CernerToEpicMigration.exe --dry-run
```

**Expected output** — check every line of the banner against what you configured:

```
  Version    : 1.0.0   Host: <machine>   .NET 9.0.x
  Input      : D:\Migration\Input
  RTF output : D:\Migration\Output\rtf
  Reports    : D:\Migration\Reports
  Logs       : D:\Migration\Reports\Logs
  Threads    : 12   Batch size: 1000   Max attempts: 3

DRY RUN - nothing is converted, moved or written.

Date Folder                    Files     Size (GB)
--------------------------------------------------
2026-09-03                     2,000          0.02
--------------------------------------------------
TOTAL                          2,000          0.02
```

| Check                   | Expected                                   |
| ----------------------- | ------------------------------------------ |
| The four paths          | Exactly the folders from[§4](#4-test-data) |
| `TOTAL` file count    | The known corpus size                      |
| Date folders listed     | All of them, none missing                  |
| Nothing changed on disk | Input, output and archive untouched        |

If the count is wrong here, it will be wrong in every test case. Fix it now.

---

## 7. Reset procedure — perform before EVERY run

**Skipping this invalidates the result, and the reports will still look normal.**

A run *moves* its input documents into `{date}\archive`. If the next run's documents are copied in
while that archive is still there, every document collides on the archive move and the run slows
down progressively — an effect that looks exactly like "more threads made it slower".

```powershell
Remove-Item 'D:\Migration\Input'      -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item 'D:\Migration\Output\rtf' -Recurse -Force -ErrorAction SilentlyContinue
Copy-Item   'D:\Migration\Master' 'D:\Migration\Input' -Recurse
```

Reports are **not** cleared — each run writes its own timestamped set, and the comparison needs them
side by side.

[tools/Run-ThreadSweep.ps1](../tools/Run-ThreadSweep.ps1) performs this reset automatically before
each run, and is the recommended way to execute [§11 TC-04](#11-tc-04--performance-matrix).

---

## 8. TC-01 — Output is identical at every thread count

**The single most important test in this plan.** Threads may change how long a run takes; they must
never change what it produces.

### Steps

1. Reset ([§7](#7-reset-procedure--perform-before-every-run)), then run at 1 thread and keep the
   output as the baseline:

   ```powershell
   cd D:\Migration\bin
   .\CernerToEpicMigration.exe --threads 5
   Copy-Item 'D:\Migration\Output\rtf' 'D:\Migration\Baseline' -Recurse
   ```
2. Reset. Run `.\CernerToEpicMigration.exe --threads 3`. Compare against the baseline.
3. Reset. Run `.\CernerToEpicMigration.exe --threads 5`. Compare against the baseline.

The comparison, run after steps 2 and 3:

```powershell
function Get-TreeHashes($root) {
    Get-ChildItem $root -Recurse -File | Sort-Object FullName | ForEach-Object {
        '{0}|{1}' -f $_.FullName.Substring($root.Length), (Get-FileHash $_.FullName -Algorithm SHA256).Hash
    }
}

Compare-Object (Get-TreeHashes 'D:\Migration\Baseline') (Get-TreeHashes 'D:\Migration\Output\rtf')
```

### Expected result

| Check                                    | Expected                                                 |
| ---------------------------------------- | -------------------------------------------------------- |
| File count in each output tree           | Identical across all three runs                          |
| `Compare-Object` result                | **No output** — every RTF byte-for-byte identical |
| `Total Files Succeeded` in each report | Identical across all three runs                          |
| `Total Files Failed` in each report    | Identical across all three runs                          |

**Pass** — no differences at any thread count.
**Fail** — any differing hash, any differing count. Attach the `Compare-Object` output and all three
reports; a difference here is a correctness defect, not a tuning question.

> Verified during preparation of this plan: 1,255 documents at 1 and 4 threads produced 1,255
> byte-for-byte identical RTF files. Conversion is deterministic and per-document, so this is the
> expected behaviour, not a hopeful one.

---

## 9. TC-02 — Every document is accounted for

No document may be silently dropped, at any thread count. Run this check against **every** run in the
cycle, not just one.

### Steps

Read the exit code **first** — any other command overwrites it — then open the run's
`migration_report_{timestamp}.csv` and count the trees.

The counts must cover **every** date folder, not one. A real extract has hundreds of them, so these
totals are aggregated across the whole tree:

```powershell
Write-Host "exit code    : $LASTEXITCODE"

$InputRoot  = 'D:\Migration\Input'
$OutputRoot = 'D:\Migration\Output\rtf'

$all = Get-ChildItem $InputRoot -Recurse -File

Write-Host "RTF written  : $(@(Get-ChildItem $OutputRoot -Recurse -File -Filter *.rtf).Count)"
Write-Host "archived     : $(@($all | Where-Object { $_.FullName -match '\\archive\\' }).Count)"
Write-Host "in error     : $(@($all | Where-Object { $_.FullName -match '\\error\\' -and $_.Extension -ne '.log' }).Count)"
Write-Host "left in input: $(@($all | Where-Object { $_.FullName -notmatch '\\(archive|error)\\' }).Count)"
```

### Expected result

| #  | Check                                              | Expected                                                                  |
| -- | -------------------------------------------------- | ------------------------------------------------------------------------- |
| 1  | `Status`                                         | `COMPLETED`                                                             |
| 2  | Exit code                                          | `0` on a clean corpus, `1` when the corpus contains planted bad files |
| 3  | `Total Files Found`                              | The known corpus size                                                     |
| 4  | `Total Files Found` = `Succeeded` + `Failed` | Balances exactly                                                          |
| 5  | `Total Files Processed` = `Total Files Found`  | Equal                                                                     |
| 6  | RTF files written                                  | =`Total Files Succeeded`                                                |
| 7  | Files in`archive`                                | =`Total Files Succeeded`                                                |
| 8  | Documents in`error`                              | =`Total Files Failed`                                                   |
| 9  | Files left in the date folder                      | **0**                                                               |
| 10 | `Error Report Rows`                              | =`Total Files Failed`                                                   |
| 11 | `Peak Concurrent Workers`                        | = the`Threads` value for that run                                       |

**Pass** — all eleven hold.
**Fail** — any imbalance. Checks 4 and 6–9 failing means documents were lost or double-counted.
Check 11 failing means the thread count did not take effect and the run is not the test case it
claims to be.

> Check 9 is the one to watch. A document left in the input folder was neither converted nor failed
> — it was skipped, and nothing else in the report will say so.

---

## 10. TC-03 — Failure handling under concurrency

Bad documents must fail cleanly and individually while other threads keep working.

### Steps

1. Confirm the master corpus contains the known-bad documents from [§4](#4-test-data).
2. Reset. Run at maximum concurrency — the worst case for shared error handling:

   ```powershell
   cd D:\Migration\bin
   .\CernerToEpicMigration.exe --threads 5
   Write-Host "exit code: $LASTEXITCODE"
   ```
3. Inspect `{date}\error`, `error_report_{timestamp}_001.csv` and the exit code.
4. Repeat the whole case at `--threads 1` and confirm identical counts.

### Expected result

| Check                            | Expected                                                                                                          |
| -------------------------------- | ----------------------------------------------------------------------------------------------------------------- |
| `Status`                       | `COMPLETED` — bad documents must not stop the run                                                              |
| Exit code                        | `1` (completed with failures). `0` means the bad documents were wrongly accepted; `3` means the run aborted |
| `Total Files Failed`           | Exactly the number of bad documents planted                                                                       |
| Each bad document                | Present in`{date}\error`, **not** in `archive`, **not** in the input folder                       |
| `.error.log` beside each       | One per failed document, naming the reason                                                                        |
| Error report rows                | One per failed document, with`Reason`, `Error Category` and `Batch Id` populated                            |
| The non-matching file (`.pdf`) | In`error` with reason `File name does not match *.xhtml`, and counted in `Total Files Found`                |
| Good documents in the same run   | All converted successfully                                                                                        |
| 1-thread repeat                  | Same counts as the 5-thread run                                                                                   |

**Pass** — failures are isolated: every bad document is in `error` with a row and a log, every good
document still converted, and one and five threads behave identically.
**Fail** — the run aborts, a bad document is archived as a success, a bad document silently
disappears, or good documents fail alongside it.

> Verified during preparation of this plan, at 3 threads on a 24-document corpus with four planted
> bad files: `Found 24 = Succeeded 20 + Failed 4`; 20 RTF written, 20 archived; 3 bad `.xhtml` plus
> 1 `.pdf` in `error` with 4 error logs; 0 left in the input folder; exit code 1.

---

## 11. TC-04 — Performance matrix

Run this only once TC-01 to TC-03 have passed. A fast run that fails those is not a result.

### Option A — scripted (recommended)

[Run-ThreadSweep.ps1](../tools/Run-ThreadSweep.ps1) resets, runs, reads that run's own report and
appends a row to the matrix CSV. It cannot forget the reset:

```powershell
cd 'D:\Davita\Document Process POC\docs\telerik - 3\fileconversion\CernerToEpicMigration\tools'

.\Run-ThreadSweep.ps1 -Threads 1,2,3,4,5 `
    -Master D:\Migration\Master `
    -Exe    D:\Migration\bin\CernerToEpicMigration.exe
```

To run the steps one at a time, waiting for each to finish, pass a single value. The matrix CSV is
appended to, so separately run steps still build one table:

```powershell
.\Run-ThreadSweep.ps1 -Threads 1 -Master D:\Migration\Master -Exe D:\Migration\bin\CernerToEpicMigration.exe
# wait for it to finish, then:
.\Run-ThreadSweep.ps1 -Threads 2 -Master D:\Migration\Master -Exe D:\Migration\bin\CernerToEpicMigration.exe
# ... then 3, 4, 5, and finally 1 again as the control
```

Results land in `D:\Migration\Reports\thread-sweep-matrix.csv`.

### Option B — manual

Repeat this block **six times**, changing only the `--threads` value: 1, 2, 3, 4, 5, then 1 again as
the control. Wait for each run to finish before starting the next.

```powershell
# --- reset (never skip) ---
Remove-Item 'D:\Migration\Input'      -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item 'D:\Migration\Output\rtf' -Recurse -Force -ErrorAction SilentlyContinue
Copy-Item   'D:\Migration\Master' 'D:\Migration\Input' -Recurse

# --- run (change the number each time) ---
cd D:\Migration\bin
.\CernerToEpicMigration.exe --threads 1
Write-Host "exit code: $LASTEXITCODE"
```

Each run prints its own summary and writes `migration_report_{timestamp}.csv`. Transcribe one row
per run into the table below; the source of every column is listed in
[thread-count-tuning.md §7](thread-count-tuning.md#7-the-matrix).

### Record

| Threads        | Elapsed | Files/s | Utilisation % | Service time (ms) | CPU ms/doc | Disk ms/doc | Archive ms/doc | Status |
| -------------- | ------- | ------- | ------------- | ----------------- | ---------- | ----------- | -------------- | ------ |
| 1              |         |         |               |                   |            |             |                |        |
| 2              |         |         |               |                   |            |             |                |        |
| 3              |         |         |               |                   |            |             |                |        |
| 4              |         |         |               |                   |            |             |                |        |
| 5              |         |         |               |                   |            |             |                |        |
| 1*(control)* |         |         |               |                   |            |             |                |        |

### Expected result

| Check                                   | Expected                                                                                                                                |
| --------------------------------------- | --------------------------------------------------------------------------------------------------------------------------------------- |
| `Status` on every row                 | `COMPLETED`                                                                                                                           |
| `Succeeded` / `Failed` on every row | Identical across all rows — the same work was done each time                                                                           |
| Control run vs the first 1-thread run   | Within a few percent.**If not, the cycle is not reproducible and no row can be trusted** — investigate before reporting anything |
| Throughput at the best thread count     | Higher than at 1 thread                                                                                                                 |

**This test has no pass/fail on speed.** It produces the matrix; engineering chooses the thread count
from it. What QA certifies is that the matrix is *valid* — same work in every row, reproducible
control run.

> Do not report the highest `Utilisation %` as the best result. Utilisation says the workers were
> busy, not that they were productive; workers can be 99% occupied and simply waiting for each
> other. Throughput (`Files/s`) is the comparison.

---

## 12. Recording and sign-off

For each cycle, record:

| Field                                        | Value                                                 |
| -------------------------------------------- | ----------------------------------------------------- |
| Git commit under test                        | *(from §3.1)*                                      |
| Build version                                | *(from the startup banner)*                         |
| Developer test suite                         | *(passed / total, from §3.3)*                      |
| Published to                                 | *(from §3.4)*                                      |
| Machine / logical cores                      |                                                       |
| Storage type (local SSD, SAN, network share) |                                                       |
| Antivirus real-time scanning                 | on / off / excluded for these paths                   |
| Corpus size and shape                        | *(document count, folder count, planted bad files)* |
| Date and tester                              |                                                       |

| Test                                         | Result          | Evidence                                          |
| -------------------------------------------- | --------------- | ------------------------------------------------- |
| TC-01 Output identical at every thread count | Pass / Fail     | `Compare-Object` output, three reports          |
| TC-02 Every document accounted for           | Pass / Fail     | Report + tree counts per run                      |
| TC-03 Failure handling under concurrency     | Pass / Fail     | Error report,`error` folder listing, exit codes |
| TC-04 Performance matrix                     | Valid / Invalid | `thread-sweep-matrix.csv`                       |

Keep for every run: `migration_report_*.csv`, `error_report_*.csv`, `file_trace_*.csv` and
`migration_*.log`. They are the evidence, and they are timestamped so they cannot be confused.

### Exit criteria

The build is validated for multi-threaded operation when **TC-01, TC-02 and TC-03 pass at 1, 3 and
5 threads**, and TC-04 produces a matrix whose control run reproduces.

Before the production run, set `EnableFileTrace` back to `false` and `EnableConsoleDashboard` back
to `true`.
