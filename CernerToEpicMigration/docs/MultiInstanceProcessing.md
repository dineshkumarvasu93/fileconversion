# Multi-Instance Processing

How to run several instances of the application against **one** input root so they divide the
folders between themselves at runtime, and the step-by-step procedure for demonstrating it.

> **Scope.** Sections 1 to 3 explain the mechanism. Sections 4 to 9 are the procedure, in order,
> from configuration to sign-off. Section 10 is the client-facing demo script; sections 11 to 13
> are troubleshooting, known issues and reference.

The principle throughout: **no folder name appears in any script or configuration file.** Each
instance is given the input *root* and discovers what is under it at runtime, so the same
configuration handles 10 folders or 100 without being edited.

---

## 1. What multi-instance mode does

One instance already converts documents in parallel across threads
(`MaxDegreeOfParallelism`). Multi-instance mode is the layer above that: several **processes**,
each with its own thread pool, working the same input root at the same time.

| Claim                                                   | Where it is proven                                        |
| ------------------------------------------------------- | --------------------------------------------------------- |
| Instances run simultaneously and independently          | [§8](#8-run-the-instances), separate console windows       |
| Input folders are discovered dynamically, never named   | [§6](#6-rehearse-with-a-dry-run), the `--dry-run` listing  |
| No folder is processed by two instances                 | [§9](#9-verify-the-run), the `.done` files and file trace  |
| Work is distributed by availability, not by assignment  | [§9](#9-verify-the-run), the distribution summary          |
| The same configuration scales to any number of folders  | [§4](#4-configure-the-instances), nothing to change        |

Without `--instance-id` nothing here is active: a single run discovers every folder, claims
nothing, writes no coordination files, and behaves exactly as it always has.

---

## 2. How the folders are divided

Every instance walks the **same** dynamically discovered folder list and takes the folders nobody
else holds. Coordination is two files per folder in the folder named by `--coordination`:

| File            | Written when                   | Removed when                           | Purpose                                                       |
| --------------- | ------------------------------ | -------------------------------------- | ------------------------------------------------------------- |
| `{folder}.claim` | An instance takes the folder   | The instance exits (even if killed)     | The lease. Held open, so a second instance cannot open it     |
| `{folder}.done`  | The folder finishes completely | Never — cleared by the reset in [§7](#7-reset-before-every-run) | Stops a finished folder being re-claimed once its lease drops |

The mutual exclusion is the operating system's, not the application's: the second instance to open
the same `.claim` path for writing is refused by the file system. That holds across processes and
across machines sharing the folder over SMB, which a lock inside one process would not.

```text
                        Input root (10 folders discovered at runtime)
                                        │
              ┌─────────────────────────┼─────────────────────────┐
              ▼                         ▼                         ▼
        Instance-1                Instance-2                Instance-3
              │                         │                         │
              │  try claim part1 ─► ok  │  try claim part1 ─► busy │  try claim part1 ─► busy
              │                         │  try claim part2 ─► ok   │  try claim part2 ─► busy
              │                         │                         │  try claim part3 ─► ok
              ▼                         ▼                         ▼
          process                   process                   process
              │                         │                         │
        write part1.done          write part2.done          write part3.done
              │                         │                         │
              └───────────────► take next unclaimed ◄─────────────┘
```

Because an instance returns for another folder as soon as it finishes one, an instance that draws a
small folder keeps working while a slower instance is still on its first. Nothing is pre-assigned.

**Crash behaviour.** The `.claim` lease is opened with `DeleteOnClose`, so Windows releases it when
the process exits for any reason. A killed instance returns its folder to the pool instead of
stranding it; because no `.done` was written, another instance picks it up.

---

## 3. What each instance needs its own of

| Setting                | Shared or per-instance | Why                                                                                    |
| ---------------------- | ---------------------- | -------------------------------------------------------------------------------------- |
| `InputBasePath`        | **Shared**             | The whole point — one root, divided at runtime                                         |
| `CoordinationPath`     | **Shared**             | The instances coordinate through it; a per-instance one divides nothing                 |
| `OutputRtfBasePath`    | **Shared**             | Output mirrors the input structure, so instances never collide inside it                |
| `ReportBasePath`       | **Per instance**       | The run lock is per report folder — a shared one makes instances 2 and 3 refuse to start |
| `LogBasePath`          | **Per instance**       | Three processes cannot roll the same log file                                           |
| `InstanceId`           | **Per instance**       | Identifies the instance in the logs and the claim files                                 |
| `MaxDegreeOfParallelism` | Per instance         | Budget it across all instances — see the note below                                     |

> **Thread budget.** `MaxDegreeOfParallelism` is per **instance**, not per machine. Three instances
> at 4 threads is 12 concurrent conversions on one host. Set instances × threads to roughly the
> core count; oversubscribing makes every instance slower.

---

## 4. Configure the instances

Everything below refers to the prepared demo layout:

```text
D:\Davita\fileconversion_poc\demo\
├── bin\                 the application and appsettings.json
├── share\               the input root  (part1 … part10)
├── coordination\        .claim / .done files   (created on first run)
├── output\rtf\          converted RTF          (created on first run)
├── reports\             instance1\, instance2\, instance3\
├── run_demo.bat         starts the instances
├── dry_run.bat          safe preview, converts nothing
└── verify_demo.ps1      post-run validation
```

### Step 4.1 — Set the shared paths

In `demo\bin\appsettings.json`, only the **shared** values are set. No folder name appears:

```json
"InputBasePath":     "D:\\Davita\\fileconversion_poc\\demo\\share",
"OutputRtfBasePath": "D:\\Davita\\fileconversion_poc\\demo\\output\\rtf",
"CoordinationPath":  "D:\\Davita\\fileconversion_poc\\demo\\coordination",
"InstanceId":        "",
```

`InstanceId` stays empty here — the launcher supplies it per instance.

> The coordination folder is deliberately **outside** the input root. A folder inside the root
> would otherwise be enumerated as input; discovery excludes it either way, but keeping it outside
> also leaves the share holding nothing but documents.

### Step 4.2 — Set the per-instance paths

These are **not** in `appsettings.json` — the launcher sets them as environment variables, which
override the file:

```bat
set "MigrationConfig__ReportBasePath=%REPORTS%\instance%N%"
set "MigrationConfig__LogBasePath=%REPORTS%\instance%N%\logs"
```

### Step 4.3 — Confirm the processing settings

| Setting                     | Demo value | Why                                                                |
| --------------------------- | ---------- | ------------------------------------------------------------------ |
| `MaxDegreeOfParallelism`    | `4`        | 3 instances × 4 = 12 workers on a 12-core host                    |
| `BatchSize`                 | `100`      | Checkpoint interval — smaller keeps the progress visibly moving    |
| `EnableFileTrace`           | `true`     | One row per document; the evidence for "no file processed twice"   |
| `EnableConsoleDashboard`    | `true`     | Each window shows live progress                                    |
| `ArchiveOnSuccess`          | `true`     | Converted inputs move to `{folder}\archive` — see [§7](#7-reset-before-every-run) |

---

## 5. The launcher script

`run_demo.bat` starts the instances and nothing else. It contains no folder name — only the root:

```bat
for /L %%I in (1,1,%INSTANCES%) do (
    start "Migration Instance-%%I" cmd /k ""%~f0" --instance %%I"
)
```

Each window then runs one instance:

```bat
"%BIN%\CernerToEpicMigration.exe" ^
    --input "%SHARE%" ^
    --output "%OUT%" ^
    --coordination "%COORD%" ^
    --instance-id "Instance-%N%" ^
    --threads %THREADS%
```

To run a different number of instances, change one line — `set "INSTANCES=3"`. Nothing else in the
script or the configuration changes.

---

## 6. Rehearse with a dry run

`--dry-run` scans and reports **without converting, moving or writing anything**. It is the safe
way to prove the configuration before spending the input on it.

```powershell
D:\Davita\fileconversion_poc\demo\dry_run.bat
```

**Expected output:**

```text
  Input      : D:\Davita\fileconversion_poc\demo\share
  RTF output : D:\Davita\fileconversion_poc\demo\output\rtf
  Threads    : 4   Batch size: 100   Max attempts: 3

DRY RUN - nothing is converted, moved or written.

Date Folder                    Files     Size (GB)
--------------------------------------------------
part1                             96          0.00
part10                           178          0.00
part2                            151          0.00
part3                            289          0.00
part4                             92          0.00
part5                            141          0.00
part6                            141          0.00
part7                            122          0.00
part8                            113          0.00
part9                             79          0.00
--------------------------------------------------
TOTAL                          1,402          0.01
```

| Check                   | Expected                                      |
| ----------------------- | --------------------------------------------- |
| Folders listed          | All 10, discovered at runtime                 |
| `TOTAL` file count      | 1,402                                         |
| Nothing changed on disk | No `archive`, `error` or claim folder created |

This listing is also the best evidence for the client that no folder is hardcoded: the names come
from the share, not from a script.

---

## 7. Reset before every run

**A run consumes its input.** With `ArchiveOnSuccess` on, every converted document is *moved* into
`{folder}\archive`. A second run over the same share therefore finds almost nothing to do, and the
`.done` files make the instances skip every folder outright.

> **Before the first rehearsal, take a master copy of the share.** Without one there is no way back
> to the starting state, and the demo can only be run once.

```powershell
# Once, before any run:
Copy-Item 'D:\Davita\fileconversion_poc\demo\share' `
          'D:\Davita\fileconversion_poc\demo\master' -Recurse

# Before EVERY run after that:
Remove-Item 'D:\Davita\fileconversion_poc\demo\share'        -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item 'D:\Davita\fileconversion_poc\demo\output'       -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item 'D:\Davita\fileconversion_poc\demo\coordination' -Recurse -Force -ErrorAction SilentlyContinue
Copy-Item   'D:\Davita\fileconversion_poc\demo\master' `
            'D:\Davita\fileconversion_poc\demo\share' -Recurse
```

Reports are **not** cleared — each run writes its own timestamped set.

| Skipped step                | Symptom                                                              |
| --------------------------- | --------------------------------------------------------------------- |
| Share not restored          | Instances report far fewer files, or "no files found"                 |
| `coordination` not cleared  | Every folder skipped as "already completed by another instance"       |
| `output` not cleared        | Old RTF counted in the verification totals                            |

---

## 8. Run the instances

```powershell
D:\Davita\fileconversion_poc\demo\run_demo.bat
```

Three windows open, one per instance, each titled `Migration Instance-N`.

**Expected in each window** — the banner names the instance and the claim folder:

```text
  Version    : 1.0.0   Host: <machine>   .NET 9.0.x
  Instance   : Instance-1   PID: 27088
  Claims     : D:\Davita\fileconversion_poc\demo\coordination
```

**Expected in the log** — every line is stamped with the instance, and the claims are visible:

```text
[INF] [Instance-1] Claimed folder part1.
[INF] [Instance-2] Skipping folder part1: claimed by another instance.
[INF] [Instance-2] Claimed folder part2.
[INF] [Instance-3] Skipping folder part2: claimed by another instance.
[INF] [Instance-3] Claimed folder part3.
```

**Ways to show the instances are genuinely running in parallel:**

| Evidence          | Where                                                                     |
| ----------------- | ------------------------------------------------------------------------- |
| Console windows   | Three titled windows, each with its own live dashboard                    |
| Task Manager      | Three `CernerToEpicMigration.exe` processes                               |
| Logs              | `reports\instanceN\logs\migration_*.log`, each stamped `[Instance-N]`     |
| Claim folder      | `.claim` files appearing and disappearing as folders are taken and finished |

**Exit codes**, printed in each window when it finishes:

| Code | Meaning                                                    |
| ---- | ---------------------------------------------------------- |
| 0    | Every document this instance handled converted successfully |
| 1    | Completed, but some documents failed to the error folder    |
| 2    | Invalid arguments or configuration                          |
| 3    | Fatal — processing stopped early                            |

---

## 9. Verify the run

```powershell
powershell -ExecutionPolicy Bypass -File D:\Davita\fileconversion_poc\demo\verify_demo.ps1
```

The script is read-only. It reports five things, in order:

| # | Check                | Pass criterion                                                        |
| - | -------------------- | ---------------------------------------------------------------------- |
| 1 | Folder ownership     | One `.done` per input folder, no `.claim` left behind                 |
| 2 | Work distribution    | Every instance took at least one folder                               |
| 3 | File counts          | Unprocessed inputs `0`; archived + error = the original file count    |
| 4 | **Duplicate check**  | No document appears in more than one instance's file trace            |
| 5 | Failures             | Every failure has a recorded reason                                   |

**Expected shape of a clean run:**

```text
Folder ownership (one .done per folder = processed exactly once)
part1                -> Instance-1
part2                -> Instance-3
part3                -> Instance-2
...
  All folders completed.

Work distribution
  Instance-1       4 folder(s): part1, part5, ...
  Instance-2       3 folder(s): part3, ...
  Instance-3       3 folder(s): part2, part4, ...

Duplicate check (file trace across all instances)
  Documents traced : 1402
  No document was processed more than once.
```

Check 4 is the real proof. The `.done` files show which instance *claimed* each folder; the file
trace is written per document by whichever worker actually converted it, so agreement between the
two means no document was touched twice.

> The folder split will differ on every run — that is the expected result of dynamic claiming, not
> a fault. A fixed split every time would mean the work was assigned, not claimed.

---

## 10. Client demo script

| Step | Show                   | Do                                                                          | Say                                                                       |
| ---- | ---------------------- | --------------------------------------------------------------------------- | ------------------------------------------------------------------------- |
| 1    | The input root         | Open `demo\share`                                                           | "Production-like structure. Ten folders today; the number is not fixed."   |
| 2    | The application        | Open `demo\bin`, show `appsettings.json`                                    | "One input root is configured. No folder is named anywhere."              |
| 3    | Dynamic discovery      | Run `dry_run.bat` ([§6](#6-rehearse-with-a-dry-run))                        | "The list comes from the share at runtime, not from configuration."       |
| 4    | Start the instances    | Run `run_demo.bat` ([§8](#8-run-the-instances))                             | "Three instances, same configuration, same input root."                   |
| 5    | Parallel processing    | Three windows side by side; Task Manager                                     | "Each has claimed a different folder — nothing was assigned in advance."  |
| 6    | Monitoring             | Dashboards, then the `[Instance-N]` log lines                               | "Every log line says which instance did the work."                        |
| 7    | Validate completion    | Run `verify_demo.ps1` ([§9](#9-verify-the-run))                             | "All folders processed, none twice, nothing missed."                      |

**Closing message:** the application runs multiple instances in parallel and discovers and processes
the available input folders dynamically. No folder name is hardcoded or assigned to an instance, so
the same configuration handles 10 folders or 100.

---

## 11. Troubleshooting

| Symptom                                                              | Cause                                                         | Fix                                                              |
| -------------------------------------------------------------------- | ------------------------------------------------------------- | ---------------------------------------------------------------- |
| Instance 2 and 3 exit immediately, "Another migration run is already using…" | They share a `ReportBasePath`                                | Give each its own — the launcher already does ([§4.2](#step-42--set-the-per-instance-paths)) |
| Every folder "already completed by another instance"                 | `.done` files left from the previous run                      | Clear `coordination` ([§7](#7-reset-before-every-run))           |
| One instance does all the work                                       | It claimed everything before the others started               | Normal on a small corpus; stagger the starts, or use more folders |
| An instance finds nothing to do and exits 0                          | The other instances hold every remaining folder               | Expected — not an error                                          |
| A folder is never processed                                          | Its instance was killed *and* the lease is still held         | Check for a stale `.claim`; the lease clears when the process ends |
| Instances write to the same log file                                 | `LogBasePath` not overridden per instance                     | [§4.2](#step-42--set-the-per-instance-paths)                     |

---

## 12. Known issues to resolve before a client demo

> **Telerik licence.** Without a licence file on the build machine, every converted RTF contains a
> trial notice — *"We hope you enjoyed your Trial period…"* — visible to anyone who opens the
> output. This is independent of multi-instance mode and affects any build made without the
> licence. Resolve it before showing output files to a client. See the Telerik licence section of
> [README.md](../README.md).

> **Documents that are not Base64 envelopes.** The decoder requires each input to be a Base64
> envelope. In the demo corpus, 55 of 1,402 documents (3.9%) are plain XHTML and fail as
> `The file is not a decodable Base64 envelope`. They are spread across seven folders, so most
> instances will finish with exit code 1 and the verification will report those failures. Either
> accept and explain them, or extend the decoder to pass a plain-XHTML payload through unchanged.

---

## 13. Reference

### Command-line options

| Option                 | Purpose                                                                       |
| ---------------------- | ----------------------------------------------------------------------------- |
| `--input <path>`       | Input root. Shared by every instance                                          |
| `--output <path>`      | RTF output root. Shared                                                       |
| `--instance-id <name>` | Names this instance and **turns claiming on**. Required for multiple instances |
| `--coordination <path>` | Folder the instances claim through. Default `<input>\_migration_claims`       |
| `--threads <count>`    | Workers inside this instance                                                  |
| `--dry-run`            | Scan and report only — converts, moves and writes nothing                     |
| `--date-folder <name>` | Restrict to one folder. Diagnostic only; not how instances are divided         |

### Configuration keys

| Key                              | Notes                                                                |
| -------------------------------- | -------------------------------------------------------------------- |
| `MigrationConfig:InstanceId`     | Empty means single-instance: no claiming, no coordination files       |
| `MigrationConfig:CoordinationPath` | Empty defaults to `_migration_claims` under the input root, which discovery excludes |

Any key can be overridden per instance with an environment variable —
`MigrationConfig__ReportBasePath` and so on — which is how the launcher separates the instances.

### Behaviour notes

- **The pre-scan is skipped when claiming is on.** Totalling the whole input would give each
  instance a progress bar covering work it is not going to do; instead each folder is counted as it
  is claimed, so the totals in each window are that instance's own work.
- **`--resume` and the checkpoint are per instance**, because the report folder is. An instance
  resumes its own folders; folders finished by other instances are skipped via their `.done` files.

---

## Related documents

- [README.md](../README.md) — configuration, error reporting and operational detail
- [test-plan-multi-threading.md](test-plan-multi-threading.md) — validating parallelism *within* one instance
