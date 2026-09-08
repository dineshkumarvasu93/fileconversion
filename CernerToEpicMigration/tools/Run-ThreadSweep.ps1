<#
.SYNOPSIS
    Runs a controlled thread-count sweep and builds the comparison matrix.

.DESCRIPTION
    For each thread count: resets the input tree from a pristine master copy, runs the migration,
    then reads the run's summary report and appends one row to the matrix CSV.

    The reset is the point of this script. A run moves its input documents into {date}\archive, so
    running again over a dirty archive makes every document collide on the archive move - which
    quietly slows each successive run and makes the sweep report that threads are the problem when
    they are not. See docs/thread-count-tuning.md section 4.

.PARAMETER Threads
    Thread counts to test, in order. Default 1..5.

    Pass a single value to run one step by hand: -Threads 1, then -Threads 2 later. The matrix CSV
    is appended to, so steps run separately still build one table.

.PARAMETER Master
    Pristine copy of the input tree. Never processed, never written to. Required.

.PARAMETER Exe
    The published CernerToEpicMigration.exe.

.EXAMPLE
    .\Run-ThreadSweep.ps1 -Master D:\Migration\Master -Exe D:\Migration\bin\CernerToEpicMigration.exe

.EXAMPLE
    # one step at a time, waiting for each to finish before starting the next
    .\Run-ThreadSweep.ps1 -Threads 1 -Master D:\Migration\Master -Exe D:\Migration\bin\CernerToEpicMigration.exe
    .\Run-ThreadSweep.ps1 -Threads 2 -Master D:\Migration\Master -Exe D:\Migration\bin\CernerToEpicMigration.exe
#>
[CmdletBinding()]
param(
    [int[]]   $Threads = @(1, 2, 3, 4, 5),
    [Parameter(Mandatory)] [string] $Master,
    [Parameter(Mandatory)] [string] $Exe,
    [string] $InputPath  = 'D:\Migration\Input',
    [string] $OutputPath = 'D:\Migration\Output\rtf',
    [string] $Reports = 'D:\Migration\Reports',
    [string] $Matrix  = 'D:\Migration\Reports\thread-sweep-matrix.csv'
)

$ErrorActionPreference = 'Stop'

foreach ($path in @($Master, $Exe)) {
    if (-not (Test-Path $path)) { throw "Not found: $path" }
}

# Refuse to run against the master itself - one wrong argument would consume the only clean copy.
if ((Resolve-Path $Master).Path -eq (Resolve-Path $InputPath -ErrorAction SilentlyContinue).Path) {
    throw "-Master and -InputPath are the same folder. The master must be a separate, untouched copy."
}

<#
    Reads one value from the "Name,Value" header block of a summary report. The header stops at the
    first blank line; everything after it is the per-folder, per-worker and per-phase sections.
#>
function Get-ReportValue {
    param([string[]] $Lines, [string] $Name)

    foreach ($line in $Lines) {
        if ($line.Trim().Length -eq 0) { break }
        $split = $line.IndexOf(',')
        if ($split -gt 0 -and $line.Substring(0, $split) -eq $Name) {
            return $line.Substring($split + 1)
        }
    }
    return ''
}

<# Reads the "Avg ms/File" column of one row of the phase breakdown. #>
function Get-PhaseMs {
    param([string[]] $Lines, [string] $Phase)

    $row = $Lines | Where-Object { $_.StartsWith("$Phase,") } | Select-Object -First 1
    if (-not $row) { return '' }

    return ($row -split ',')[-1]
}

foreach ($count in $Threads) {

    Write-Host ''
    Write-Host "=== $count thread(s) ===" -ForegroundColor Cyan

    # --- reset -------------------------------------------------------------------------------
    # Everything the previous run touched goes, and the input tree is rebuilt from the master.
    # Reports are deliberately kept: each run writes its own timestamped file, and the matrix is
    # built from them.
    Write-Host '  resetting input and output...'
    Remove-Item $InputPath  -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item $OutputPath -Recurse -Force -ErrorAction SilentlyContinue
    Copy-Item   $Master $InputPath -Recurse

    # --- run ---------------------------------------------------------------------------------
    # Every path this script resets is also passed to the run, so the two can never disagree.
    # Input and output go on the command line; the report folder has no switch, so it goes through
    # the environment - both override appsettings.json (README, "Configuration").
    # The dashboard is turned off because a repaint loop that samples process CPU and working set
    # is measurement overhead inside the thing being measured.
    $env:MigrationConfig__ReportBasePath = $Reports
    $env:MigrationConfig__LogBasePath = Join-Path $Reports 'Logs'
    $env:MigrationConfig__Dashboard__EnableConsoleDashboard = 'false'

    $startedAt = Get-Date
    Write-Host "  running (started $($startedAt.ToString('HH:mm:ss')))..."

    & $Exe --threads $count --input $InputPath --output $OutputPath | Out-Null
    $exitCode = $LASTEXITCODE

    if ($exitCode -ne 0) {
        Write-Warning "  exit code $exitCode - the row below may describe an incomplete run."
    }

    # --- read the report this run wrote ------------------------------------------------------
    # Newest report written at or after this run started, so a stale file can never be picked up.
    $report = Get-ChildItem $Reports -Filter 'migration_report_*.csv' -ErrorAction SilentlyContinue |
              Where-Object { $_.LastWriteTime -ge $startedAt } |
              Sort-Object LastWriteTime -Descending |
              Select-Object -First 1

    if (-not $report) {
        Write-Warning "  no summary report found in $Reports - skipping this row."
        continue
    }

    $lines = Get-Content $report.FullName

    $row = [pscustomobject][ordered]@{
        Threads          = Get-ReportValue $lines 'Threads'
        Status           = Get-ReportValue $lines 'Status'
        Elapsed          = Get-ReportValue $lines 'Total Processing Time'
        FilesPerSecond   = Get-ReportValue $lines 'Average Files Per Second'
        FilesPerHour     = Get-ReportValue $lines 'Average Files Per Hour'
        Succeeded        = Get-ReportValue $lines 'Total Files Succeeded'
        Failed           = Get-ReportValue $lines 'Total Files Failed'
        Retries          = Get-ReportValue $lines 'Total Retries'
        PeakWorkers      = Get-ReportValue $lines 'Peak Concurrent Workers'
        AvgWorkers       = Get-ReportValue $lines 'Average Concurrent Workers'
        UtilisationPct   = Get-ReportValue $lines 'Worker Utilisation (%)'
        ServiceTimeMs    = Get-ReportValue $lines 'Average Worker Service Time (ms)'
        ImportMs         = Get-PhaseMs $lines 'Telerik import (XHTML)'
        ExportMs         = Get-PhaseMs $lines 'Telerik export (RTF)'
        ReadMs           = Get-PhaseMs $lines 'Read input'
        WriteMs          = Get-PhaseMs $lines 'Write output'
        ArchiveMs        = Get-PhaseMs $lines 'Archive input'
        CpuMs            = Get-PhaseMs $lines 'CPU total'
        DiskMs           = Get-PhaseMs $lines 'Disk total'
        Report           = $report.Name
    }

    $row | Export-Csv $Matrix -NoTypeInformation -Append -Encoding UTF8

    Write-Host ("  {0} - {1} files/s - service time {2} ms - CPU {3} ms/doc, disk {4} ms/doc" -f `
        $row.Elapsed, $row.FilesPerSecond, $row.ServiceTimeMs, $row.CpuMs, $row.DiskMs) -ForegroundColor Green
}

Write-Host ''
Write-Host "Matrix: $Matrix" -ForegroundColor Cyan

if (Test-Path $Matrix) {
    Import-Csv $Matrix |
        Format-Table Threads, Elapsed, FilesPerSecond, UtilisationPct, ServiceTimeMs, CpuMs, DiskMs, ArchiveMs -AutoSize
}
